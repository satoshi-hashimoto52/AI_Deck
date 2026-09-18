"""Small, dependency-free client for the ACE-Step 1.5 REST API.

The Unity application will eventually call the AI Deck generator bridge rather than
ACE-Step directly.  Keeping the upstream protocol behind this type gives us one place to
validate responses and to survive an upstream API change without touching the deck UI.
"""

from __future__ import annotations

import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional


class GeneratorError(RuntimeError):
    """Raised when the local generator cannot complete a request safely."""


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
            "model": "acestep-v15-turbo",
            "bpm": self.bpm,
            "key_scale": self.key_scale,
            "time_signature": self.time_signature,
            "audio_duration": self.duration_seconds,
            "inference_steps": 8,
            "batch_size": 1,
            "lm_model_path": "acestep-5Hz-lm-0.6B",
            "lm_backend": "mlx",
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


class AceStepClient:
    """Submit, poll and download one generation from a local ACE-Step server."""

    def __init__(
        self,
        base_url: str = "http://127.0.0.1:8001",
        timeout_seconds: float = 30.0,
        opener: Optional[Callable[..., Any]] = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._timeout_seconds = timeout_seconds
        self._opener = opener or urllib.request.urlopen

    def health(self) -> Dict[str, Any]:
        response = self._request_json("GET", "/health")
        data = response.get("data", response)
        if not isinstance(data, dict):
            raise GeneratorError("Generator health response was malformed.")
        return data

    def generate(
        self,
        request: GenerationRequest,
        output_directory: Path,
        poll_seconds: float = 2.0,
        generation_timeout_seconds: float = 1800.0,
    ) -> GenerationResult:
        request.validate()
        started = time.monotonic()
        submitted = self._request_json("POST", "/release_task", request.to_payload())
        task_id = self._task_id(submitted)

        result_item: Optional[Dict[str, Any]] = None
        while time.monotonic() - started < generation_timeout_seconds:
            query = self._request_json(
                "POST", "/query_result", {"task_id_list": [task_id]}
            )
            status, item, error = self._task_state(query, task_id)
            if status == 1:
                result_item = item
                break
            if status == 2:
                raise GeneratorError(error or "Music generation failed.")
            time.sleep(max(0.05, poll_seconds))

        if result_item is None:
            raise GeneratorError("Music generation timed out.")

        audio_url = result_item.get("file")
        if not isinstance(audio_url, str) or not audio_url.startswith("/"):
            raise GeneratorError("Generator did not return a local audio URL.")

        output_directory.mkdir(parents=True, exist_ok=True)
        filename = self._safe_filename(request.title) + ".wav"
        audio_path = self._unique_path(output_directory / filename)
        self._download_wave(audio_url, audio_path)

        elapsed = time.monotonic() - started
        metadata = {
            "schema_version": 1,
            "engine": "ACE-Step 1.5",
            "task_id": task_id,
            "elapsed_seconds": round(elapsed, 3),
            "request": asdict(request),
            "generator_result": {
                key: value for key, value in result_item.items() if key not in {"file", "wave"}
            },
        }
        metadata_path = audio_path.with_suffix(".json")
        temporary_metadata = metadata_path.with_suffix(".json.part")
        temporary_metadata.write_text(
            json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        os.replace(temporary_metadata, metadata_path)

        return GenerationResult(task_id, audio_path, metadata_path, elapsed)

    def _request_json(
        self, method: str, path: str, payload: Optional[Dict[str, Any]] = None
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
            with self._opener(request, timeout=self._timeout_seconds) as response:
                raw = response.read()
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise GeneratorError(f"Generator returned HTTP {exc.code}: {detail[:300]}") from exc
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise GeneratorError(
                "Local generator is not reachable. Start ACE-Step on port 8001 first."
            ) from exc

        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise GeneratorError("Generator returned invalid JSON.") from exc
        if not isinstance(parsed, dict):
            raise GeneratorError("Generator response was not a JSON object.")
        if parsed.get("code", 200) != 200 or parsed.get("error"):
            raise GeneratorError(str(parsed.get("error") or "Generator request failed."))
        return parsed

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
    ) -> tuple[int, Optional[Dict[str, Any]], Optional[str]]:
        data = response.get("data")
        if not isinstance(data, list):
            raise GeneratorError("Generator task response was malformed.")
        record = next(
            (item for item in data if isinstance(item, dict) and item.get("task_id") == task_id),
            None,
        )
        if record is None:
            raise GeneratorError("Generator task disappeared from the queue.")

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

    def _download_wave(self, audio_url: str, destination: Path) -> None:
        parsed = urllib.parse.urlparse(audio_url)
        if parsed.scheme or parsed.netloc:
            raise GeneratorError("Generator returned a non-local audio URL.")
        temporary = destination.with_suffix(".wav.part")
        request = urllib.request.Request(self._base_url + audio_url, method="GET")
        try:
            with self._opener(request, timeout=self._timeout_seconds) as response:
                with temporary.open("wb") as output:
                    while True:
                        block = response.read(1024 * 1024)
                        if not block:
                            break
                        output.write(block)
        except Exception:
            temporary.unlink(missing_ok=True)
            raise

        with temporary.open("rb") as generated:
            header = generated.read(12)
        if len(header) != 12 or header[:4] != b"RIFF" or header[8:12] != b"WAVE":
            temporary.unlink(missing_ok=True)
            raise GeneratorError("Generated file was not a valid WAV container.")
        os.replace(temporary, destination)

    @staticmethod
    def _safe_filename(title: str) -> str:
        cleaned = re.sub(r"[^\w\- ]+", "", title, flags=re.UNICODE).strip()
        cleaned = re.sub(r"\s+", " ", cleaned)
        return cleaned[:80] or "AI Deck Generated"

    @staticmethod
    def _unique_path(path: Path) -> Path:
        if not path.exists():
            return path
        for number in range(2, 10000):
            candidate = path.with_name(f"{path.stem} {number}{path.suffix}")
            if not candidate.exists():
                return candidate
        raise GeneratorError("Could not choose a unique output filename.")
