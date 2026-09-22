"""The local bridge: one stable contract between AI Deck and whatever engine is behind it.

Unity talks to this, never to ACE-Step. Two reasons, and neither is tidiness:

* ACE-Step's API is not ours. ``/release_task`` returning a task id, ``/query_result`` taking
  a list, the unified-repository download behaviour — all of that can change under us, and
  when it does the damage should stop at this file rather than reach a deck panel.
* The engine has no cancel. AI Deck still has to offer one, and what "cancel" *means* has to
  be decided in one place and written down (see :class:`GeneratorBridge.cancel`).

The bridge is deliberately small and has no dependencies outside the standard library.

**It binds to 127.0.0.1 only.** The generator is a local appliance; putting a model behind an
unauthenticated LAN port is not something a DJ application should do by accident.
"""

from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import sys
import urllib.request
import threading
import time
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from .client import AceStepClient, GenerationRequest, process_is_running
from .errors import (
    GeneratorError,
    GeneratorNotReadyError,
    GeneratorUnreachableError,
)

BRIDGE_HOST = "127.0.0.1"
BRIDGE_PORT = 8765
ENGINE_URL = "http://127.0.0.1:8001"

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "generator" / "scripts"
RUNTIME_ROOT = REPO_ROOT / ".aideck-generator"
PID_FILE = RUNTIME_ROOT / "run" / "server.pid"
LOG_DIR = RUNTIME_ROOT / "logs"
BRIDGE_LOG = LOG_DIR / "bridge.log"
DEFAULT_OUTPUT_DIR = Path.home() / "Music" / "AI Deck" / "Generated"


class BridgeLogger:
    """Writes a line, and never lets writing one break anything.

    The bridge is started as a child of AI Deck. When AI Deck goes away the bridge can be
    left running with a standard output whose reader has closed, and then ``print`` raises
    ``BrokenPipeError`` — which is exactly what happened: the first thing
    :meth:`GeneratorBridge.start_server` did was log, so pressing START AI SERVER returned
    ``internal: BrokenPipeError`` and the engine was never started at all.

    Logging is diagnostics. It must not be able to fail an operation, so every write is
    guarded, a broken stream is abandoned permanently rather than retried on every line, and
    the file copy under ``.aideck-generator/logs`` is what survives either way.

    Nothing personal reaches it: callers pass text that has already been through
    :func:`redact` and :func:`loggable`.
    """

    def __init__(self, path: Path = BRIDGE_LOG, stream: Any = None) -> None:
        self._path = path
        self._stream = sys.stdout if stream is None else stream
        self._stream_broken = False
        self._file_broken = False
        self._lock = threading.Lock()

    def __call__(self, message: str) -> None:
        line = f"{time.strftime('%Y-%m-%d %H:%M:%S')} [bridge] {redact(message)}"

        with self._lock:
            if not self._stream_broken and self._stream is not None:
                try:
                    print(line, file=self._stream, flush=True)
                except (BrokenPipeError, ValueError, OSError):
                    # The parent has gone, or the stream was closed under us. Say nothing
                    # about it on the stream that just failed, and stop trying.
                    self._stream_broken = True
                    _detach_broken_standard_streams()

            if self._file_broken:
                return

            try:
                self._path.parent.mkdir(parents=True, exist_ok=True)
                with self._path.open("a", encoding="utf-8") as handle:
                    handle.write(line + "\n")
            except OSError:
                # A read-only or missing runtime directory is not a reason to stop working.
                self._file_broken = True

    @property
    def stream_broken(self) -> bool:
        """Whether the standard stream has been abandoned. For tests and diagnostics."""
        return self._stream_broken


def _detach_broken_standard_streams() -> None:
    """Point the interpreter's own stdout and stderr at nowhere.

    Catching the write is not quite enough. CPython flushes ``sys.stdout`` once more as it
    exits, and if that flush fails the process ends with status 120 however cleanly it
    finished its work — so an orphaned bridge would report a failure it did not have. Swapping
    the broken streams for ``os.devnull`` makes the final flush a no-op.
    """
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            continue
        try:
            stream.flush()
        except Exception:
            try:
                setattr(sys, name, open(os.devnull, "w", encoding="utf-8"))
            except OSError:
                setattr(sys, name, None)


class BridgeState(str, Enum):
    """Where the bridge is. The only vocabulary Unity needs to understand."""

    NOT_INSTALLED = "not-installed"
    STOPPED = "stopped"
    STARTING = "starting"
    READY = "ready"
    QUEUED = "queued"
    GENERATING = "generating"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLING = "cancelling"
    CANCELLED = "cancelled"

    @property
    def is_busy(self) -> bool:
        """States in which a second generation must be refused."""
        return self in {
            BridgeState.QUEUED,
            BridgeState.GENERATING,
            BridgeState.CANCELLING,
        }


# Fields that must never reach a log or a status payload's free text. The prompt and lyrics
# are the user's creative input and the paths name their home directory; neither belongs in a
# file that gets pasted into an issue.
_REDACTED_KEYS = {"prompt", "lyrics", "output_dir", "audio_path", "metadata_path"}

_HOME = str(Path.home())


def redact(text: str) -> str:
    """Replace the home directory with ``~`` so a log line names a file, not a person."""
    if not text:
        return text
    return text.replace(_HOME, "~")


def loggable(payload: Dict[str, Any]) -> Dict[str, Any]:
    """The parts of a request that are safe to write down.

    Lengths rather than contents: knowing the lyrics were 412 characters is enough to debug a
    request, and it cannot leak what they said.
    """
    safe: Dict[str, Any] = {}
    for key, value in payload.items():
        if key in _REDACTED_KEYS:
            safe[f"{key}_length"] = len(str(value)) if value is not None else 0
        else:
            safe[key] = value
    return safe


#: What each server-derived state should be saying. A state and a message that disagree is a
#: bug the user reads as the truth: `ready` carrying "Loading models. The first start takes
#: minutes." left the panel insisting it was still loading while everything else said ready.
SERVER_STATE_MESSAGES = {
    BridgeState.NOT_INSTALLED: "The local generator is not installed.",
    BridgeState.STOPPED: "The generator is stopped.",
    BridgeState.STARTING: "Loading models. The first start takes minutes.",
    BridgeState.READY: "Ready to generate.",
}


@dataclass
class GenerationProgress:
    """What the UI shows while something is happening."""

    state: BridgeState = BridgeState.STOPPED
    message: str = ""
    elapsed_seconds: float = 0.0
    error_kind: str = ""
    error_detail: str = ""
    title: str = ""
    audio_file: str = ""       # file name only, never a path
    duration_seconds: float = 0.0
    started_monotonic: float = field(default=0.0, repr=False)

    def to_payload(self) -> Dict[str, Any]:
        return {
            "state": self.state.value,
            "message": self.message,
            "elapsed_seconds": round(self.elapsed_seconds, 1),
            "error_kind": self.error_kind,
            "error_detail": self.error_detail,
            "title": self.title,
            "audio_file": self.audio_file,
            "duration_seconds": round(self.duration_seconds, 2),
        }


class GeneratorBridge:
    """Owns the engine process and at most one generation at a time.

    Process ownership is explicit and one-directional. The bridge stops an ACE-Step server
    **only if the bridge started it**; a server the user already had running is adopted for
    the session and left alone afterwards. Someone else's server is not ours to kill just
    because we found it.
    """

    def __init__(
        self,
        scripts_dir: Path = SCRIPTS_DIR,
        output_dir: Path = DEFAULT_OUTPUT_DIR,
        client_factory: Optional[Callable[[], AceStepClient]] = None,
        runner: Optional[Callable[..., subprocess.CompletedProcess]] = None,
        log: Optional[Callable[[str], None]] = None,
        clock: Optional[Callable[[], float]] = None,
        engine_url: str = ENGINE_URL,
        observe_ttl_seconds: float = 2.0,
        memory_ttl_seconds: float = 10.0,
    ) -> None:
        self._scripts_dir = Path(scripts_dir)
        self._output_dir = Path(output_dir)
        self._client_factory = client_factory or (lambda: AceStepClient(server_pid=read_server_pid()))
        self._runner = runner or _run_script
        self._logger = log or BridgeLogger()
        self._clock = clock or time.monotonic

        # Reentrant on purpose: several public methods take the lock and then ask for a
        # state snapshot, which takes it again. With a plain Lock that is a deadlock, and it
        # was one — cancelling when nothing was running hung the bridge for ever.
        self._lock = threading.RLock()
        self._progress = GenerationProgress()
        self._worker: Optional[threading.Thread] = None
        self._cancel_requested = False
        self._owns_server = False
        self._last_result: Optional[Dict[str, Any]] = None

        self._engine_url = engine_url
        self._observe_ttl = observe_ttl_seconds
        self._observed_state = BridgeState.STOPPED
        self._observed_at: Optional[float] = None
        self._memory: Dict[str, Any] = {}
        self._memory_at: Optional[float] = None
        self._memory_ttl = memory_ttl_seconds

    def _log(self, message: str) -> None:
        """Log, and swallow anything the logger does.

        Belt and braces over :class:`BridgeLogger`, which already guards its own writes: a
        logger passed in from outside must not be able to fail an operation either. Diagnostics
        are never worth a failed request.
        """
        try:
            self._logger(message)
        except Exception:
            # Deliberately silent: reporting a logging failure needs the logger.
            pass

    # ------------------------------------------------------------------ state

    def state(self) -> Dict[str, Any]:
        """A snapshot for the UI. Cheap enough to poll a few times a second."""
        with self._lock:
            progress = self._progress
            if progress.state in {BridgeState.QUEUED, BridgeState.GENERATING} and progress.started_monotonic:
                progress.elapsed_seconds = self._clock() - progress.started_monotonic

            payload = progress.to_payload()
            payload["owns_server"] = self._owns_server
            payload["result"] = self._last_result

        # Asked outside the lock: it shells out, and no UI poll should be able to block a
        # running generation's state updates.
        if payload["state"] in {BridgeState.STOPPED.value, BridgeState.STARTING.value,
                                BridgeState.READY.value, BridgeState.NOT_INSTALLED.value,
                                BridgeState.COMPLETED.value, BridgeState.FAILED.value,
                                BridgeState.CANCELLED.value}:
            observed = self._observe_server()
            payload["server_state"] = observed.value
            # Only overwrite states that are *about* the server, never a finished result.
            if payload["state"] in {BridgeState.STOPPED.value, BridgeState.STARTING.value,
                                    BridgeState.READY.value, BridgeState.NOT_INSTALLED.value}:
                # State *and* message move together. Updating only the state is what produced
                # a payload reading `state: ready` with "Loading models. The first start takes
                # minutes." still attached, which the panel showed and the user believed.
                message = SERVER_STATE_MESSAGES.get(observed, "")
                payload["state"] = observed.value
                payload["message"] = message
                with self._lock:
                    self._progress.state = observed
                    self._progress.message = message
        else:
            payload["server_state"] = BridgeState.READY.value

        payload["memory"] = self.memory()
        return payload

    def _observe_server(self) -> BridgeState:
        """What the engine is doing, without starting a process to find out.

        This used to shell out to ``status_macos.sh`` on **every** ``/v1/state``. That script
        spawns bash, curl, lsof, ps and python3 — five processes — and the deck polls once a
        second while the models load. On a 16 GB machine already at 25 GB of swap, spawning
        five processes a second is not diagnosis, it is part of the problem, and it filled the
        engine's own log with `GET /health` besides.

        The same three questions are answered here with no fork at all: is it installed (a
        file test), is a process alive (``kill -0``, a syscall), and is it answering (one HTTP
        request on loopback). The answer is cached briefly, so several UI polls in a row cost
        one probe.

        Safe stopping is unchanged and still goes through ``stop_macos.sh``, which validates a
        PID against the process's own command line. Observing does not need that: reading a
        stale PID can only mislead a status line, never signal the wrong process.
        """
        now = self._clock()
        if self._observed_at is not None and now - self._observed_at < self._observe_ttl:
            return self._observed_state

        state = self._probe_server()
        self._observed_state = state
        self._observed_at = now
        return state

    def _probe_server(self) -> BridgeState:
        if not (self._scripts_dir / "status_macos.sh").exists():
            return BridgeState.NOT_INSTALLED
        if not (RUNTIME_ROOT / "ACE-Step-1.5" / ".venv").exists():
            return BridgeState.NOT_INSTALLED

        health = self._engine_health()
        if health is not None:
            # It answers. Ready only once it says its models are loaded — a server that is
            # accepting connections while still loading is exactly the case the deck must be
            # able to tell apart.
            return BridgeState.READY if health.get("models_initialized") else BridgeState.STARTING

        # Not answering. A live PID means it is still coming up rather than absent.
        pid = read_server_pid()
        if pid is not None and process_is_running(pid):
            return BridgeState.STARTING
        return BridgeState.STOPPED

    def memory(self) -> Dict[str, Any]:
        """Swap and compressed memory, sampled at most every few seconds.

        Reported so the deck can say what the machine is actually doing before the user starts
        a ten-gigabyte load. The two figures that matter on a 16 GB Mac are the swap in use and
        the compressor: resident set size of the server process does **not** describe the
        load — most of a model's cost here is unified memory the kernel is compressing and
        paging, which never appears in one process's RSS.
        """
        now = self._clock()
        if self._memory_at is not None and now - self._memory_at < self._memory_ttl:
            return self._memory

        sample = _sample_memory()
        self._memory = sample
        self._memory_at = now
        return sample

    def _engine_health(self) -> Optional[Dict[str, Any]]:
        """One loopback request, or None if nothing answered."""
        try:
            with urllib.request.urlopen(self._engine_url + "/health", timeout=3) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except Exception:
            # Refused, timed out, or nonsense: all mean "not answering" to a status line.
            return None

        data = payload.get("data", payload)
        return data if isinstance(data, dict) else None

    # ----------------------------------------------------------------- server

    def start_server(self) -> Dict[str, Any]:
        """Start the engine, unless it is already up — in which case adopt it.

        Never called automatically. A ten-gigabyte model load is a deliberate act, and AI Deck
        starting one because it happened to launch would be indefensible on a 16 GB machine.
        """
        with self._lock:
            if self._progress.state.is_busy:
                raise BridgeBusyError("A generation is already running.")

        observed = self._observe_server()
        if observed == BridgeState.NOT_INSTALLED:
            raise BridgeError(
                "The local generator is not installed.",
                kind="not-installed",
                detail="Run ./generator/scripts/setup_macos.sh once.",
            )
        if observed in {BridgeState.READY, BridgeState.STARTING}:
            # Already running and not ours: use it, but do not adopt responsibility for it.
            self._log(f"engine already running ({observed.value}); adopted, not owned")
            self._set_state(observed, "The generator was already running.")
            return self.state()

        self._log("starting the engine")
        self._set_state(BridgeState.STARTING, "Starting the generator…")
        result = self._runner(
            self._scripts_dir / "start_macos.sh",
            env_overrides={"AIDECK_WAIT_READY": "false"},
        )
        if result.returncode != 0:
            detail = redact((result.stderr or result.stdout or "").strip()[:300])
            self._set_state(BridgeState.FAILED, "The generator could not be started.",
                            kind="start-failed", detail=detail)
            raise BridgeError("The generator could not be started.", kind="start-failed",
                              detail=detail)

        self._owns_server = True
        self._set_state(BridgeState.STARTING, "Loading models. The first start takes minutes.")
        return self.state()

    def stop_server(self, force: bool = False) -> Dict[str, Any]:
        """Stop the engine, but only one we started.

        ``force`` is what the *user* pressing "stop the AI server" means: they are asking for
        this machine's memory back, and it is theirs to ask for. Application shutdown does not
        force, so quitting AI Deck never stops a server someone else was using.
        """
        if not self._owns_server and not force:
            self._log("engine was not started by AI Deck; leaving it running")
            return self.state()

        self._log("stopping the engine")
        result = self._runner(self._scripts_dir / "stop_macos.sh")
        self._owns_server = False
        if result.returncode != 0:
            detail = redact((result.stderr or result.stdout or "").strip()[:300])
            self._log(f"stop reported a problem: {detail}")
        self._set_state(BridgeState.STOPPED, "The generator is stopped.")
        return self.state()

    # ------------------------------------------------------------- generation

    def generate(self, fields: Dict[str, Any]) -> Dict[str, Any]:
        """Queue one generation. Refuses a second while one is running."""
        request = build_request(fields)

        with self._lock:
            if self._progress.state.is_busy:
                raise BridgeBusyError("A generation is already running.")
            self._cancel_requested = False
            self._last_result = None
            self._progress = GenerationProgress(
                state=BridgeState.QUEUED,
                message="Queued.",
                title=request.title,
                started_monotonic=self._clock(),
            )
            self._worker = threading.Thread(
                target=self._run_generation, args=(request,), daemon=True,
                name="aideck-generation",
            )
            self._worker.start()

        self._log(f"queued: {json.dumps(loggable(fields), ensure_ascii=False)}")
        return self.state()

    def _run_generation(self, request: GenerationRequest) -> None:
        try:
            client = self._client_factory()
            health = client.health()
            if not health.is_ready:
                raise GeneratorNotReadyError(
                    "The generator has not finished loading yet.",
                    detail="Wait for the state to read ready, then generate.",
                )

            self._set_state(BridgeState.GENERATING, "Generating…")
            result = client.generate(
                request,
                self._output_dir,
                on_progress=lambda message: self._set_state(
                    BridgeState.GENERATING, message
                ) if not self._cancel_requested else None,
            )
        except GeneratorError as exc:
            if self._cancel_requested:
                # The engine went away because *we* stopped it. That is a cancellation, not a
                # failure, and calling it a failure would be a lie about who did what.
                self._set_state(BridgeState.CANCELLED, "Generation cancelled.")
                self._log("generation cancelled")
                return
            self._set_state(BridgeState.FAILED, str(exc), kind=exc.kind,
                            detail=redact(exc.detail or ""))
            self._log(f"generation failed: {exc.kind}")
            return
        except Exception as exc:  # a bug here must not leave the UI stuck in "generating"
            self._set_state(BridgeState.FAILED, "The generator stopped unexpectedly.",
                            kind="internal", detail=redact(str(exc))[:300])
            self._log(f"generation error: {type(exc).__name__}")
            return

        if self._cancel_requested:
            # It finished during cancellation. The file is complete and valid, so it is kept
            # on disk, but it is not announced: the user asked for this not to happen.
            self._set_state(BridgeState.CANCELLED, "Generation cancelled.")
            self._log("generation completed during cancellation; not reported")
            return

        with self._lock:
            self._last_result = {
                "audio_file": result.audio_path.name,
                "audio_path": str(result.audio_path),
                "metadata_file": result.metadata_path.name,
                "elapsed_seconds": round(result.elapsed_seconds, 1),
                "duration_seconds": round(result.audio.duration_seconds, 2) if result.audio else 0.0,
                "sample_rate": result.audio.sample_rate if result.audio else 0,
                "channels": result.audio.channels if result.audio else 0,
            }
        self._set_state(
            BridgeState.COMPLETED,
            "Done.",
            audio_file=result.audio_path.name,
            duration=result.audio.duration_seconds if result.audio else 0.0,
        )
        self._log(f"generation completed in {result.elapsed_seconds:.1f}s")

    def cancel(self) -> Dict[str, Any]:
        """Stop the running generation.

        **What this actually does, because it matters:** ACE-Step v0.1.8 has no cancellation
        endpoint. Its HTTP surface is ``release_task``, ``query_result``, ``health``, the
        model-inventory and LoRA routes, and its job store can mark a job running, succeeded
        or failed — there is nothing that stops work already handed to a worker.

        So cancelling **stops the engine process**, by PID, through the same validated script
        `stop_macos.sh` uses. The generation really does end; the model is unloaded and the
        server must be started again before the next one. What it is *not* is a polite
        request that lets the server keep its models warm.

        Marking the UI cancelled while the work continued in the background was the one
        option ruled out: it would leave a 16 GB machine grinding on a job nobody is waiting
        for, and a file would appear minutes later from a generation the user believed they
        had stopped.
        """
        with self._lock:
            if not self._progress.state.is_busy:
                already_idle = True
            else:
                already_idle = False
                self._cancel_requested = True

        if already_idle:
            return self.state()

        self._set_state(BridgeState.CANCELLING, "Cancelling — stopping the generator…")
        self._log("cancelling by stopping the engine (no cancel API upstream)")

        # force=True: the user asked for this explicitly, so a server we merely adopted is
        # still stopped — there is no other way to interrupt the work.
        self._runner(self._scripts_dir / "stop_macos.sh")
        self._owns_server = False

        worker = self._worker
        if worker is not None:
            worker.join(timeout=30)

        self._set_state(BridgeState.CANCELLED, "Generation cancelled. The generator is stopped.")
        return self.state()

    def shutdown(self) -> None:
        """Called when AI Deck quits. Stops only what we started."""
        if self._owns_server:
            self.stop_server()

    # ----------------------------------------------------------------- detail

    def _set_state(
        self,
        state: BridgeState,
        message: str,
        kind: str = "",
        detail: str = "",
        audio_file: str = "",
        duration: float = 0.0,
    ) -> None:
        with self._lock:
            self._progress.state = state
            self._progress.message = message
            self._progress.error_kind = kind
            self._progress.error_detail = detail
            if audio_file:
                self._progress.audio_file = audio_file
            if duration:
                self._progress.duration_seconds = duration
            if self._progress.started_monotonic:
                self._progress.elapsed_seconds = self._clock() - self._progress.started_monotonic


class BridgeError(RuntimeError):
    """A bridge-level refusal, carrying a kind the UI can branch on."""

    def __init__(self, message: str, *, kind: str = "bridge-error", detail: str = "") -> None:
        super().__init__(message)
        self.kind = kind
        self.detail = detail


class BridgeBusyError(BridgeError):
    """Only one generation at a time; a 16 GB machine cannot honestly do two."""

    def __init__(self, message: str) -> None:
        super().__init__(message, kind="busy")


# --------------------------------------------------------------------- request


_TIME_SIGNATURES = {"2", "3", "4", "6"}


def build_request(fields: Dict[str, Any]) -> GenerationRequest:
    """Validate the UI's fields into a request, or raise ``ValueError``.

    Unity validates too, and deliberately so: the UI's job is to stop a bad request being
    sent, and this one's is to stop a bad request being run whatever sent it.
    """
    title = str(fields.get("title", "")).strip()
    prompt = str(fields.get("prompt", "")).strip()
    lyrics = str(fields.get("lyrics", "") or "")

    if not title:
        raise ValueError("A title is required.")
    if not prompt:
        raise ValueError("A style description is required.")

    duration = _finite_number(fields.get("duration_seconds", 30.0), "Duration")
    bpm = int(_finite_number(fields.get("bpm", 118), "BPM"))
    time_signature = str(fields.get("time_signature", "4"))
    if time_signature not in _TIME_SIGNATURES:
        raise ValueError("Time signature must be one of 2, 3, 4 or 6.")

    seed_value = fields.get("seed", None)
    seed: Optional[int] = None
    if seed_value not in (None, "", "null"):
        try:
            seed = int(seed_value)
        except (TypeError, ValueError) as exc:
            raise ValueError("Seed must be a whole number, or left empty.") from exc

    request = GenerationRequest(
        title=title,
        prompt=prompt,
        lyrics=lyrics,
        duration_seconds=duration,
        bpm=bpm,
        key_scale=str(fields.get("key_scale", "A minor")).strip() or "A minor",
        time_signature=time_signature,
        vocal_language=str(fields.get("vocal_language", "ja")).strip() or "ja",
        seed=seed,
    )
    request.validate()
    return request


def _finite_number(value: Any, label: str) -> float:
    """Reject NaN, Infinity and anything that is not a number at all."""
    try:
        number = float(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{label} must be a number.") from exc
    if number != number or number in (float("inf"), float("-inf")):
        raise ValueError(f"{label} must be a finite number.")
    return number


# --------------------------------------------------------------------- process


#: Swap in use, in GB, above which the deck warns and recommends a restart. Chosen from the
#: measured runs on this machine: a normal generation peaks near 19 GB, and the session that
#: became unusable was at 25.5 GB with 0.08 GB of RAM free. Below 20 GB the machine has been
#: observed to recover; above it, it has not.
SWAP_WARNING_GB = 20.0

#: Compressed memory above which the machine is already thrashing rather than merely busy.
COMPRESSED_WARNING_GB = 6.0


def _sample_memory() -> Dict[str, Any]:
    """Swap, compressed and free memory. Two cheap reads, or empty on any failure."""
    sample: Dict[str, Any] = {}
    try:
        swap = subprocess.run(["sysctl", "-n", "vm.swapusage"],
                              capture_output=True, text=True, timeout=5).stdout
        used = re.search(r"used\s*=\s*([0-9.]+)M", swap)
        total = re.search(r"total\s*=\s*([0-9.]+)M", swap)
        if used:
            sample["swap_used_gb"] = round(float(used.group(1)) / 1024.0, 2)
        if total:
            sample["swap_total_gb"] = round(float(total.group(1)) / 1024.0, 2)

        page = subprocess.run(["sysctl", "-n", "hw.pagesize"],
                              capture_output=True, text=True, timeout=5).stdout.strip()
        page_size = int(page) if page.isdigit() else 16384
        stat = subprocess.run(["vm_stat"], capture_output=True, text=True, timeout=5).stdout
        for key, label in (("Pages free", "free_gb"),
                           ("Pages occupied by compressor", "compressed_gb")):
            found = re.search(rf"{key}:\s+([0-9]+)", stat)
            if found:
                sample[label] = round(int(found.group(1)) * page_size / 1073741824.0, 2)
    except Exception:
        # A status line is not worth failing a request for.
        return sample

    swap_used = sample.get("swap_used_gb", 0.0)
    compressed = sample.get("compressed_gb", 0.0)
    sample["under_pressure"] = swap_used >= SWAP_WARNING_GB or compressed >= COMPRESSED_WARNING_GB
    if sample["under_pressure"]:
        sample["advice"] = (
            f"This Mac is using {swap_used:.1f} GB of swap and "
            f"{compressed:.1f} GB of compressed memory. Restart it before generating; "
            f"macOS does not return this until reboot."
        )
    else:
        sample["advice"] = ""
    return sample


def read_server_pid() -> Optional[int]:
    try:
        text = PID_FILE.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return int(text) if text.isdigit() else None


def _run_script(
    script: Path,
    env_overrides: Optional[Dict[str, str]] = None,
    timeout: float = 180.0,
) -> subprocess.CompletedProcess:
    """Run one of the generator scripts.

    The scripts are the only thing that signals a process, because they already validate a
    PID against the process's own command line. Nothing here calls pkill or matches on a
    name.
    """
    environment = dict(os.environ)
    if env_overrides:
        environment.update(env_overrides)
    try:
        return subprocess.run(
            ["/bin/bash", str(script)],
            capture_output=True,
            text=True,
            timeout=timeout,
            env=environment,
            cwd=str(REPO_ROOT),
        )
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(
            args=[str(script)], returncode=124, stdout="", stderr="The script timed out."
        )
    except OSError as exc:
        return subprocess.CompletedProcess(
            args=[str(script)], returncode=127, stdout="", stderr=str(exc)
        )
