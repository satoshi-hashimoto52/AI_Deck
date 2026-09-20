"""Does this file actually contain audio?

A generation that reports success and writes a header-only WAV is worse than one that fails,
because the failure is only discovered when the deck plays silence in front of an audience.
The check is deliberately structural rather than perceptual: it parses the RIFF chunks, and
it reads the samples to see whether any of them are non-zero.
"""

from __future__ import annotations

import struct
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


@dataclass(frozen=True)
class WaveSummary:
    """What a valid WAV turned out to contain."""

    channels: int
    sample_rate: int
    frames: int
    duration_seconds: float
    peak_amplitude: float

    def describe(self) -> str:
        return (
            f"{self.duration_seconds:.1f} s, {self.channels} ch, {self.sample_rate} Hz, "
            f"peak {self.peak_amplitude:.3f}"
        )


class WaveValidationError(ValueError):
    """The file is not a WAV that AI Deck could load and play."""


def inspect_wave(path: Path, *, minimum_seconds: float = 1.0) -> WaveSummary:
    """Parse a WAV and confirm it holds audible audio, or raise.

    ``minimum_seconds`` guards the case the RIFF parser cannot: a well-formed file holding a
    few frames. One second is short enough never to reject a real request — the client floor
    is ten seconds — and long enough to catch a truncated download.
    """

    if not path.exists():
        raise WaveValidationError(f"{path.name} was not written.")

    size = path.stat().st_size
    if size < 44:
        raise WaveValidationError(
            f"{path.name} is {size} bytes, which is smaller than a WAV header."
        )

    try:
        with wave.open(str(path), "rb") as handle:
            channels = handle.getnchannels()
            sample_rate = handle.getframerate()
            sample_width = handle.getsampwidth()
            frames = handle.getnframes()
            payload = handle.readframes(frames)
    except (wave.Error, EOFError, struct.error) as exc:
        raise WaveValidationError(f"{path.name} is not a readable WAV file: {exc}") from exc

    if channels <= 0 or sample_rate <= 0:
        raise WaveValidationError(f"{path.name} declares no channels or no sample rate.")
    if frames <= 0:
        raise WaveValidationError(f"{path.name} contains a header but no audio frames.")

    duration = frames / float(sample_rate)
    if duration < minimum_seconds:
        raise WaveValidationError(
            f"{path.name} is only {duration:.2f} s long, which is too short to be the "
            f"requested track."
        )

    peak = _peak_amplitude(payload, sample_width)
    if peak <= 0.0:
        raise WaveValidationError(
            f"{path.name} is {duration:.1f} s of digital silence, so the generation did not "
            f"produce audio."
        )

    return WaveSummary(channels, sample_rate, frames, duration, peak)


def _peak_amplitude(payload: bytes, sample_width: int) -> float:
    """Largest absolute sample, normalised to 0..1.

    Written here rather than pulled from ``audioop``: that module was removed in Python 3.13,
    and a loop over at most a few million samples costs milliseconds against a generation that
    costs a minute and a half.
    """

    if not payload:
        return 0.0

    if sample_width == 1:
        # 8-bit WAV is unsigned, centred on 128.
        return max(abs(byte - 128) for byte in payload) / 127.0

    if sample_width == 2:
        count = len(payload) // 2
        if count == 0:
            return 0.0
        values = struct.unpack(f"<{count}h", payload[: count * 2])
        return max(abs(value) for value in values) / 32768.0

    if sample_width == 4:
        count = len(payload) // 4
        if count == 0:
            return 0.0
        values = struct.unpack(f"<{count}i", payload[: count * 4])
        return max(abs(value) for value in values) / 2147483648.0

    if sample_width == 3:
        peak = 0
        for offset in range(0, len(payload) - 2, 3):
            value = int.from_bytes(payload[offset : offset + 3], "little", signed=True)
            peak = max(peak, abs(value))
        return peak / 8388608.0

    # An unknown width is not something to guess at; treat any non-zero byte as signal so a
    # real file is never rejected for a format this function has not been taught.
    return 1.0 if any(payload) else 0.0


def summarise(path: Path, *, minimum_seconds: float = 1.0) -> Optional[WaveSummary]:
    """Same check, returning None instead of raising. For status output only."""
    try:
        return inspect_wave(path, minimum_seconds=minimum_seconds)
    except WaveValidationError:
        return None
