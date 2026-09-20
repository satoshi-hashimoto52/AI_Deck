"""Small, dependency-free client for the ACE-Step 1.5 REST API.

The Unity application will eventually call the AI Deck generator bridge rather than
ACE-Step directly.  Keeping the upstream protocol behind this type gives us one place to
validate responses and to survive an upstream API change without touching the deck UI.

Three things here exist because of what Phase 0 got wrong on a 16 GB M1:

* every transport failure was reported as "not reachable", including a server that was up
  and loading;
* one 30-second timeout covered submission, polling and download alike, so a normal 87-second
  generation could be failed on the clock; and
* success was declared on a file whose first twelve bytes said ``RIFF…WAVE``, which a
  header-only file also says.
"""

from __future__ import annotations

import errno
import json
import os
import re
import socket
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, Optional, Tuple

from .errors import (
    GenerationFailedError,
    GeneratorError,
    GeneratorHttpError,
    GeneratorNotReadyError,
    GeneratorStoppedError,
    GeneratorTimeoutError,
    GeneratorUnreachableError,
    InvalidGeneratedAudioError,
)
from .wave_check import WaveSummary, WaveValidationError, inspect_wave

# The Mac profile. These are the only combination Phase 1 supports on Apple Silicon with
# 16 GB, and they are asserted by a test so that a careless edit shows up as a red test
# rather than as a 3.5 GB download.
MAC_DIT_MODEL = "acestep-v15-turbo"
MAC_LM_MODEL = "acestep-5Hz-lm-0.6B"
MAC_LM_BACKEND = "mlx"


@dataclass(frozen=True)
class GenerationRequest:
    """Validated subset of generation controls exposed by AI Deck."""

    title: str
    prompt: str
    lyrics: str
    duration_seconds: float = 30.0
    bpm: int = 118
    key_scale: str = "A minor"
    time_signature: str = "4"
    vocal_language: str = "ja"
    seed: Optional[int] = None

    def validate(self) -> None:
        if not self.title.strip():
            raise ValueError("Title is required.")
        if not self.prompt.strip():
            raise ValueError("Music description is required.")
        if not 10.0 <= self.duration_seconds <= 600.0:
            raise ValueError("Duration must be between 10 and 600 seconds.")
        if not 30 <= self.bpm <= 300:
            raise ValueError("BPM must be between 30 and 300.")
        if self.time_signature not in {"2", "3", "4", "6"}:
            raise ValueError("Time signature must be one of 2, 3, 4 or 6.")

    def to_payload(self) -> Dict[str, Any]:
        self.validate()
        payload: Dict[str, Any] = {
            "prompt": self.prompt,
            "lyrics": self.lyrics,
            "thinking": True,
            "vocal_language": self.vocal_language,
            "audio_format": "wav",
            "model": MAC_DIT_MODEL,
            "bpm": self.bpm,
            "key_scale": self.key_scale,
            "time_signature": self.time_signature,
            "audio_duration": self.duration_seconds,
            "inference_steps": 8,
            "batch_size": 1,
            "lm_model_path": MAC_LM_MODEL,
            "lm_backend": MAC_LM_BACKEND,
            "use_cot_caption": True,
            "use_cot_language": True,
            "use_random_seed": self.seed is None,
        }
        if self.seed is not None:
            payload["seed"] = self.seed
        return payload


@dataclass(frozen=True)
class GenerationResult:
    """Files and timing produced by one completed generation request."""

    task_id: str
    audio_path: Path
    metadata_path: Path
    elapsed_seconds: float
    audio: Optional[WaveSummary] = None


@dataclass(frozen=True)
class ServerHealth:
    """What ``/health`` said, reduced to the two questions worth asking."""

    models_initialized: bool
    llm_initialized: bool
    loaded_model: str
    loaded_lm_model: str
    raw: Dict[str, Any] = field(default_factory=dict)

    @property
    def is_ready(self) -> bool:
        """Ready means the generation model is loaded; the LM is reported separately."""
        return self.models_initialized

    def describe(self) -> str:
        if not self.models_initialized:
            return "starting — models are still loading"
        lm = self.loaded_lm_model or "none"
        return f"ready — DiT {self.loaded_model or 'unknown'}, LM {lm}"


class AceStepClient:
    """Submit, poll and download one generation from a local ACE-Step server."""

    def __init__(
        self,
        base_url: str = "http://127.0.0.1:8001",
        request_timeout_seconds: float = 30.0,
        download_timeout_seconds: float = 600.0,
        opener: Optional[Callable[..., Any]] = None,
        server_pid: Optional[int] = None,
        sleep: Optional[Callable[[float], None]] = None,
        monotonic: Optional[Callable[[], float]] = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        # Two separate limits. A control request that has not answered in 30 seconds is
        # wrong; a download of a ten-minute WAV legitimately is not.
        self._request_timeout_seconds = request_timeout_seconds
        self._download_timeout_seconds = download_timeout_seconds
        self._opener = opener or urllib.request.urlopen
        self._server_pid = server_pid
        self._sleep = sleep or time.sleep
        self._monotonic = monotonic or time.monotonic

    # ---------------------------------------------------------------- health

    def health(self) -> ServerHealth:
        """Ask the server what it has loaded. Raises if it cannot be reached at all."""
        response = self._request_json("GET", "/health")
        data = response.get("data", response)
        if not isinstance(data, dict):
            raise GeneratorError("Generator health response was malformed.")
        return ServerHealth(
            models_initialized=bool(data.get("models_initialized")),
            llm_initialized=bool(data.get("llm_initialized")),
            loaded_model=str(data.get("loaded_model") or ""),
            loaded_lm_model=str(data.get("loaded_lm_model") or ""),
            raw=data,
        )

    def wait_until_ready(
        self,
        timeout_seconds: float = 1800.0,
        poll_seconds: float = 5.0,
        on_progress: Optional[Callable[[str], None]] = None,
    ) -> ServerHealth:
        """Block until the server reports its models loaded.

        A first start downloads and loads roughly 10 GB, so the default ceiling is half an
        hour.  Only *unreachable* is retried: an HTTP error or a stopped process is reported
        immediately rather than waited out.
        """
        started = self._monotonic()
        last_note = ""
        while True:
            try:
                health = self.health()
                if health.is_ready:
                    return health
                note = health.describe()
            except GeneratorUnreachableError as exc:
                self._raise_if_server_gone(exc)
                note = "waiting for the server to accept connections"

            if note != last_note and on_progress:
                on_progress(note)
                last_note = note

            waited = self._monotonic() - started
            if waited >= timeout_seconds:
                raise GeneratorTimeoutError(
                    f"The generator was still not ready after {waited:.0f} s.",
                    waited_seconds=waited,
                    limit_seconds=timeout_seconds,
                )
            self._sleep(max(0.05, poll_seconds))

    # ------------------------------------------------------------ generation

    def generate(
        self,
        request: GenerationRequest,
        output_directory: Path,
        poll_seconds: float = 3.0,
        generation_timeout_seconds: float = 1800.0,
        on_progress: Optional[Callable[[str], None]] = None,
    ) -> GenerationResult:
        """Run one generation to completion and write a verified WAV plus its metadata.

        ``generation_timeout_seconds`` is a ceiling, not an expectation: the measured Phase 0
        run was 86.7 s for 30 seconds of audio, and the ceiling exists only so a wedged server
        cannot block for ever.
        """
        request.validate()
        started = self._monotonic()
        submitted = self._request_json("POST", "/release_task", request.to_payload())
        task_id = self._task_id(submitted)
        if on_progress:
            on_progress(f"queued as {task_id}")

        result_item = self._await_task(
            task_id, started, poll_seconds, generation_timeout_seconds, on_progress
        )

        audio_url = result_item.get("file")
        if not isinstance(audio_url, str) or not audio_url.startswith("/"):
            raise GeneratorError("Generator did not return a local audio URL.")

        output_directory.mkdir(parents=True, exist_ok=True)
        filename = safe_filename(request.title) + ".wav"
        audio_path = self._unique_path(output_directory / filename)
        if on_progress:
            on_progress(f"downloading to {audio_path.name}")
        summary = self._download_wave(audio_url, audio_path, request)

        elapsed = self._monotonic() - started
        metadata_path = self._write_metadata(audio_path, task_id, request, result_item, elapsed, summary)
        return GenerationResult(task_id, audio_path, metadata_path, elapsed, summary)

    def _await_task(
        self,
        task_id: str,
        started: float,
        poll_seconds: float,
        generation_timeout_seconds: float,
        on_progress: Optional[Callable[[str], None]],
    ) -> Dict[str, Any]:
        last_note = ""
        while True:
            try:
                query = self._request_json("POST", "/query_result", {"task_id_list": [task_id]})
            except GeneratorUnreachableError as exc:
                # Mid-generation the connection dropping means the server went away, which on
                # 16 GB is most often the kernel killing it. Say that, do not say "start it".
                self._raise_if_server_gone(exc)
                raise

            status, item, error = self._task_state(query, task_id)
            if status == 1:
                if item is None:
                    raise GeneratorError("Generator reported success without a result.")
                return item
            if status == 2:
                raise GenerationFailedError(
                    "Music generation failed on the server.", detail=error or None
                )

            waited = self._monotonic() - started
            note = f"generating — {waited:.0f} s elapsed"
            if on_progress and note != last_note:
                on_progress(note)
                last_note = note

            if waited >= generation_timeout_seconds:
                raise GeneratorTimeoutError(
                    f"Generation did not finish within {generation_timeout_seconds:.0f} s. "
                    f"The task may still be running on the server.",
                    waited_seconds=waited,
                    limit_seconds=generation_timeout_seconds,
                )
            self._sleep(max(0.05, poll_seconds))

    # --------------------------------------------------------------- transport

    def _request_json(
        self,
        method: str,
        path: str,
        payload: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        body = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(
            self._base_url + path, data=body, headers=headers, method=method
        )
        try:
            with self._opener(request, timeout=self._request_timeout_seconds) as response:
                raw = response.read()
        except urllib.error.HTTPError as exc:
            raise self._http_error(exc) from exc
        except Exception as exc:  # narrowed and re-raised by _transport_error
            raise self._transport_error(exc, path) from exc

        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise GeneratorError("Generator returned invalid JSON.") from exc
        if not isinstance(parsed, dict):
            raise GeneratorError("Generator response was not a JSON object.")

        code = parsed.get("code", 200)
        if code != 200 or parsed.get("error"):
            message = str(parsed.get("error") or "Generator request failed.")
            if _looks_like_initialising(message):
                raise GeneratorNotReadyError(
                    "The generator is still loading its models.", detail=message
                )
            raise GeneratorHttpError(int(code) if isinstance(code, int) else 500, message)
        return parsed

    @staticmethod
    def _http_error(exc: urllib.error.HTTPError) -> GeneratorError:
        try:
            detail = exc.read().decode("utf-8", errors="replace")[:300]
        except Exception:
            detail = ""

        if exc.code == 503 or _looks_like_initialising(detail):
            return GeneratorNotReadyError(
                "The generator is still loading its models.", detail=detail or None
            )
        return GeneratorHttpError(
            exc.code, f"The generator answered HTTP {exc.code}.", detail=detail or None
        )

    def _transport_error(self, exc: Exception, path: str) -> GeneratorError:
        """Tell a refused connection, a timeout and a dead socket apart."""
        reason = getattr(exc, "reason", exc)

        if isinstance(exc, (socket.timeout, TimeoutError)) or isinstance(
            reason, (socket.timeout, TimeoutError)
        ):
            return GeneratorTimeoutError(
                f"The generator did not answer {path} within "
                f"{self._request_timeout_seconds:.0f} s.",
                waited_seconds=self._request_timeout_seconds,
                limit_seconds=self._request_timeout_seconds,
            )

        if isinstance(reason, ConnectionRefusedError) or getattr(reason, "errno", None) in (
            errno.ECONNREFUSED,
            errno.ECONNRESET,
        ):
            return GeneratorUnreachableError(
                f"Nothing is listening on {self._base_url}.",
                detail="Start it with ./generator/scripts/start_macos.sh",
            )

        if isinstance(exc, (urllib.error.URLError, OSError)):
            return GeneratorUnreachableError(
                f"Could not reach {self._base_url}.", detail=str(reason)
            )
        raise exc

    def _raise_if_server_gone(self, cause: GeneratorError) -> None:
        """Upgrade 'unreachable' to 'stopped' when we know the PID and it has exited."""
        if self._server_pid is None or process_is_running(self._server_pid):
            return
        raise GeneratorStoppedError(
            f"The generator process (PID {self._server_pid}) is no longer running. "
            f"On 16 GB this is usually the system stopping it under memory pressure; "
            f"its log will say.",
            detail=str(cause),
        ) from cause

    # ---------------------------------------------------------------- results

    @staticmethod
    def _task_id(response: Dict[str, Any]) -> str:
        data = response.get("data")
        task_id = data.get("task_id") if isinstance(data, dict) else None
        if not isinstance(task_id, str) or not task_id:
            raise GeneratorError("Generator did not return a task ID.")
        return task_id

    @staticmethod
    def _task_state(
        response: Dict[str, Any], task_id: str
    ) -> Tuple[int, Optional[Dict[str, Any]], Optional[str]]:
        data = response.get("data")
        if not isinstance(data, list):
            raise GeneratorError("Generator task response was malformed.")
        record = next(
            (item for item in data if isinstance(item, dict) and item.get("task_id") == task_id),
            None,
        )
        if record is None:
            raise GenerationFailedError("The generator lost track of this task.")

        status = record.get("status")
        if status not in (0, 1, 2):
            raise GeneratorError("Generator returned an unknown task status.")
        if status != 1:
            return status, None, str(record.get("error") or "") or None

        raw_result = record.get("result")
        try:
            results = json.loads(raw_result) if isinstance(raw_result, str) else raw_result
        except json.JSONDecodeError as exc:
            raise GeneratorError("Generator result contained invalid JSON.") from exc
        if not isinstance(results, list) or not results or not isinstance(results[0], dict):
            raise GeneratorError("Generator succeeded without an audio result.")
        return status, results[0], None

    def _download_wave(
        self, audio_url: str, destination: Path, request: GenerationRequest
    ) -> WaveSummary:
        parsed = urllib.parse.urlparse(audio_url)
        if parsed.scheme or parsed.netloc:
            raise GeneratorError("Generator returned a non-local audio URL.")

        temporary = destination.with_suffix(".wav.part")
        http_request = urllib.request.Request(self._base_url + audio_url, method="GET")
        try:
            with self._opener(http_request, timeout=self._download_timeout_seconds) as response:
                with temporary.open("wb") as output:
                    while True:
                        block = response.read(1024 * 1024)
                        if not block:
                            break
                        output.write(block)
        except urllib.error.HTTPError as exc:
            temporary.unlink(missing_ok=True)
            raise self._http_error(exc) from exc
        except Exception as exc:
            temporary.unlink(missing_ok=True)
            raise self._transport_error(exc, audio_url) from exc

        # Only now is it audio as far as this client is concerned. The file is renamed into
        # place after it passes, so a rejected generation never leaves a playable-looking
        # file in the user's music folder.
        try:
            summary = inspect_wave(temporary, minimum_seconds=_minimum_seconds(request))
        except WaveValidationError as exc:
            temporary.unlink(missing_ok=True)
            raise InvalidGeneratedAudioError(str(exc)) from exc

        os.replace(temporary, destination)
        return summary

    def _write_metadata(
        self,
        audio_path: Path,
        task_id: str,
        request: GenerationRequest,
        result_item: Dict[str, Any],
        elapsed: float,
        summary: WaveSummary,
    ) -> Path:
        metadata = {
            "schema_version": 2,
            "engine": "ACE-Step 1.5",
            "profile": "m1-safe",
            "models": {
                "dit": MAC_DIT_MODEL,
                "lm": MAC_LM_MODEL,
                "lm_backend": MAC_LM_BACKEND,
            },
            "task_id": task_id,
            "generated_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "elapsed_seconds": round(elapsed, 3),
            "audio": {
                "file": audio_path.name,
                "channels": summary.channels,
                "sample_rate": summary.sample_rate,
                "duration_seconds": round(summary.duration_seconds, 3),
                "peak_amplitude": round(summary.peak_amplitude, 6),
            },
            "request": asdict(request),
            "generator_result": {
                key: value for key, value in result_item.items() if key not in {"file", "wave"}
            },
        }
        metadata_path = audio_path.with_suffix(".json")
        temporary = metadata_path.with_suffix(".json.part")
        temporary.write_text(
            json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        os.replace(temporary, metadata_path)
        return metadata_path

    @staticmethod
    def _unique_path(path: Path) -> Path:
        """Never overwrite. A second 'Neon Highway' becomes 'Neon Highway 2'."""
        if not path.exists():
            return path
        for number in range(2, 10000):
            candidate = path.with_name(f"{path.stem} {number}{path.suffix}")
            if not candidate.exists():
                return candidate
        raise GeneratorError("Could not choose a unique output filename.")


def _minimum_seconds(request: GenerationRequest) -> float:
    """Half the requested length, floored at one second.

    A generator that returns three seconds for a thirty-second request has failed even though
    the file parses, but models do trim, so half is the line rather than the exact length.
    """
    return max(1.0, request.duration_seconds * 0.5)


def _looks_like_initialising(text: str) -> bool:
    lowered = (text or "").lower()
    return any(
        marker in lowered
        for marker in ("not initialized", "not initialised", "initializing", "initialising",
                       "still loading", "model not loaded", "models are loading")
    )


def process_is_running(pid: int) -> bool:
    """Whether this PID exists. Signal 0 checks without touching the process."""
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        # It exists and belongs to someone else, which is still "running".
        return True
    return True


_UNSAFE = re.compile(r"[^\w\-. ]+", re.UNICODE)
_RESERVED = {".", "..", ""}


def safe_filename(title: str) -> str:
    """A file name that cannot escape its folder or collide with a shell idiom.

    Path separators, leading dots and control characters are removed rather than replaced, so
    a title of ``../../etc/passwd`` becomes ``etcpasswd`` instead of something that traverses.
    """
    cleaned = _UNSAFE.sub("", title.replace("/", " ").replace("\\", " "))
    cleaned = re.sub(r"\s+", " ", cleaned).strip(" .")
    cleaned = cleaned[:80].strip(" .")
    if cleaned in _RESERVED:
        return "AI Deck Generated"
    return cleaned or "AI Deck Generated"
