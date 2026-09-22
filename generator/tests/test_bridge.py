"""Bridge behaviour, with no engine and no model.

The bridge is the only thing AI Deck talks to, so the things worth testing are its promises:
one generation at a time, a refusal that says why, a cancel that really interrupts, a bind
that cannot reach the LAN, and a log that never contains a prompt, a lyric or a home path.
"""

import json
import subprocess
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from unittest.mock import patch

from generator.aideck_generator.bridge import (
    BridgeBusyError,
    BridgeError,
    BridgeState,
    GeneratorBridge,
    build_request,
    loggable,
    redact,
)
from generator.aideck_generator.bridge_server import serve
from generator.aideck_generator.errors import GenerationFailedError, GeneratorUnreachableError


def completed(returncode=0, stdout="", stderr=""):
    return subprocess.CompletedProcess(args=["script"], returncode=returncode,
                                       stdout=stdout, stderr=stderr)


class ScriptRunner:
    """Stands in for the shell scripts, recording which were run."""

    def __init__(self, status_code=3):
        self.calls = []
        self.status_code = status_code

    def __call__(self, script, env_overrides=None, timeout=180.0):
        name = Path(script).name
        self.calls.append(name)
        if name == "status_macos.sh":
            return completed(self.status_code)
        return completed(0)


class FakeHealth:
    def __init__(self, ready=True):
        self.is_ready = ready

    def describe(self):
        return "ready" if self.is_ready else "starting"


class FakeAudio:
    duration_seconds = 30.0
    sample_rate = 48000
    channels = 2


class FakeResult:
    def __init__(self, folder):
        self.audio_path = Path(folder) / "Track.wav"
        self.metadata_path = Path(folder) / "Track.json"
        self.elapsed_seconds = 85.1
        self.audio = FakeAudio()


class FakeClient:
    """An engine that never loads a model."""

    def __init__(self, folder, ready=True, error=None, block=None):
        self._folder = folder
        self._ready = ready
        self._error = error
        self._block = block

    def health(self):
        return FakeHealth(self._ready)

    def generate(self, request, output_directory, on_progress=None, **kwargs):
        if self._block is not None:
            self._block.wait(timeout=10)
        if self._error is not None:
            raise self._error
        if on_progress:
            on_progress("generating — 1 s elapsed")
        return FakeResult(self._folder)


REAL_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"


def pretend_engine_is_ready(bridge):
    """Make the bridge observe a ready engine.

    Observation no longer shells out to status_macos.sh — it asks the engine's own /health on
    loopback — so a test that wants "already running" has to say so where the bridge now looks.
    """
    bridge._engine_health = lambda: {"models_initialized": True}   # type: ignore[assignment]
    bridge._observed_at = None
    return bridge


def bridge_with(runner=None, client=None, tmp="/tmp"):
    return GeneratorBridge(
        scripts_dir=REAL_SCRIPTS,
        output_dir=Path(tmp),
        client_factory=lambda: client,
        runner=runner or ScriptRunner(),
        log=lambda _message: None,
    )


def wait_for(predicate, timeout=5.0):
    deadline = threading.Event()
    for _ in range(int(timeout * 100)):
        if predicate():
            return True
        deadline.wait(0.01)
    return False


# ------------------------------------------------------------------ validation


class RequestValidationTests(unittest.TestCase):
    def test_a_title_is_required(self):
        with self.assertRaisesRegex(ValueError, "title"):
            build_request({"title": "  ", "prompt": "night drive"})

    def test_a_style_description_is_required(self):
        with self.assertRaisesRegex(ValueError, "style"):
            build_request({"title": "Night", "prompt": ""})

    def test_duration_outside_the_range_is_refused(self):
        for duration in (9.0, 601.0):
            with self.assertRaises(ValueError):
                build_request({"title": "N", "prompt": "p", "duration_seconds": duration})

    def test_bpm_outside_the_range_is_refused(self):
        for bpm in (29, 301):
            with self.assertRaises(ValueError):
                build_request({"title": "N", "prompt": "p", "bpm": bpm})

    def test_nan_and_infinity_are_refused(self):
        for value in (float("nan"), float("inf"), float("-inf")):
            with self.assertRaisesRegex(ValueError, "finite"):
                build_request({"title": "N", "prompt": "p", "duration_seconds": value})

    def test_a_non_numeric_duration_is_refused(self):
        with self.assertRaisesRegex(ValueError, "number"):
            build_request({"title": "N", "prompt": "p", "duration_seconds": "quite long"})

    def test_an_unsupported_time_signature_is_refused(self):
        with self.assertRaisesRegex(ValueError, "Time signature"):
            build_request({"title": "N", "prompt": "p", "time_signature": "5"})

    def test_an_empty_seed_means_random(self):
        self.assertIsNone(build_request({"title": "N", "prompt": "p", "seed": ""}).seed)
        self.assertIsNone(build_request({"title": "N", "prompt": "p"}).seed)

    def test_a_non_integer_seed_is_refused(self):
        with self.assertRaisesRegex(ValueError, "whole number"):
            build_request({"title": "N", "prompt": "p", "seed": "abc"})

    def test_a_valid_request_keeps_its_values(self):
        request = build_request({
            "title": "Night", "prompt": "drive", "lyrics": "la",
            "duration_seconds": 45, "bpm": 124, "key_scale": "C minor",
            "time_signature": "3", "vocal_language": "en", "seed": "77",
        })
        self.assertEqual(45.0, request.duration_seconds)
        self.assertEqual(124, request.bpm)
        self.assertEqual(77, request.seed)


# --------------------------------------------------------------------- privacy


class LogPrivacyTests(unittest.TestCase):
    def test_the_prompt_and_lyrics_are_reduced_to_lengths(self):
        safe = loggable({
            "title": "Night", "prompt": "a very specific idea", "lyrics": "私の歌詞",
            "bpm": 118,
        })

        self.assertNotIn("prompt", safe)
        self.assertNotIn("lyrics", safe)
        self.assertEqual(20, safe["prompt_length"])
        self.assertEqual(4, safe["lyrics_length"])
        self.assertEqual("Night", safe["title"])
        self.assertEqual(118, safe["bpm"])

    def test_no_prompt_text_survives_serialisation(self):
        text = json.dumps(loggable({"prompt": "neon expressway", "lyrics": "ネオン"}))

        self.assertNotIn("neon expressway", text)
        self.assertNotIn("ネオン", text)

    def test_the_home_directory_is_folded_away(self):
        home = str(Path.home())

        self.assertEqual("~/Music/AI Deck", redact(f"{home}/Music/AI Deck"))
        self.assertNotIn(home, redact(f"failed to write {home}/Music/x.wav"))

    def test_the_bridge_logs_no_prompt_or_lyrics_while_generating(self):
        lines = []
        with __import__("tempfile").TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder),
                runner=ScriptRunner(status_code=0),
                log=lines.append,
            )
            bridge.generate({
                "title": "Night", "prompt": "secret neon expressway idea",
                "lyrics": "誰にも言えない歌詞",
            })
            wait_for(lambda: bridge.state()["state"] == BridgeState.COMPLETED.value)

        joined = "\n".join(lines)
        self.assertNotIn("secret neon expressway idea", joined)
        self.assertNotIn("誰にも言えない歌詞", joined)
        self.assertNotIn(str(Path.home()), joined)


# ---------------------------------------------------------------- state machine


class BridgeStateTests(unittest.TestCase):
    def test_a_missing_installation_is_reported_as_not_installed(self):
        bridge = GeneratorBridge(
            scripts_dir=Path("/tmp/aideck-no-such-scripts"),
            output_dir=Path("/tmp"),
            client_factory=lambda: None,
            runner=ScriptRunner(status_code=3),
            log=lambda _m: None,
        )

        self.assertEqual(BridgeState.NOT_INSTALLED.value, bridge.state()["state"])

    def test_starting_the_server_is_refused_when_not_installed(self):
        bridge = GeneratorBridge(
            scripts_dir=Path("/tmp/aideck-no-such-scripts"),
            output_dir=Path("/tmp"),
            client_factory=lambda: None,
            runner=ScriptRunner(status_code=3),
            log=lambda _m: None,
        )

        with self.assertRaises(BridgeError) as caught:
            bridge.start_server()

        self.assertEqual("not-installed", caught.exception.kind)

    def test_a_generation_moves_through_queued_generating_and_completed(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder:
            seen = []
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder),
                runner=ScriptRunner(status_code=0),
                log=lambda message: seen.append(message),
            )

            first = bridge.generate({"title": "Night", "prompt": "drive"})
            self.assertEqual(BridgeState.QUEUED.value, first["state"])

            self.assertTrue(wait_for(
                lambda: bridge.state()["state"] == BridgeState.COMPLETED.value
            ), f"never completed: {bridge.state()}")

            final = bridge.state()
            self.assertEqual("Track.wav", final["result"]["audio_file"])
            self.assertEqual(30.0, final["result"]["duration_seconds"])

    def test_a_second_generation_is_refused_while_one_runs(self):
        import tempfile

        gate = threading.Event()
        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder, block=gate),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            bridge.generate({"title": "One", "prompt": "drive"})

            try:
                with self.assertRaises(BridgeBusyError):
                    bridge.generate({"title": "Two", "prompt": "drive"})
            finally:
                gate.set()
                wait_for(lambda: not BridgeState(bridge.state()["state"]).is_busy)

    def test_a_failed_generation_reports_the_kind_not_a_crash(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(
                    folder, error=GenerationFailedError("it failed", detail="out of memory")
                ),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            bridge.generate({"title": "Night", "prompt": "drive"})
            wait_for(lambda: bridge.state()["state"] == BridgeState.FAILED.value)

            state = bridge.state()
            self.assertEqual(BridgeState.FAILED.value, state["state"])
            self.assertEqual("task-failed", state["error_kind"])
            self.assertIsNone(state["result"], "a failed run must not offer a file")

    def test_generating_before_the_models_are_loaded_fails_as_not_ready(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder, ready=False),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            bridge.generate({"title": "Night", "prompt": "drive"})
            wait_for(lambda: bridge.state()["state"] == BridgeState.FAILED.value)

            self.assertEqual("not-ready", bridge.state()["error_kind"])

    def test_a_failed_start_is_reported_rather_than_left_starting(self):
        class FailingRunner(ScriptRunner):
            def __call__(self, script, env_overrides=None, timeout=180.0):
                name = Path(script).name
                self.calls.append(name)
                if name == "status_macos.sh":
                    return completed(3)
                return completed(1, stderr="port 8001 is already in use")

        bridge = bridge_with(runner=FailingRunner(status_code=3))
        with patch.object(GeneratorBridge, "_observe_server", return_value=BridgeState.STOPPED):
            with self.assertRaises(BridgeError) as caught:
                bridge.start_server()

        self.assertEqual("start-failed", caught.exception.kind)
        self.assertIn("8001", caught.exception.detail)


# -------------------------------------------------------------- process owning


class ProcessOwnershipTests(unittest.TestCase):
    def test_an_already_running_server_is_adopted_and_not_owned(self):
        runner = ScriptRunner(status_code=0)
        bridge = pretend_engine_is_ready(bridge_with(runner=runner))

        state = bridge.start_server()

        self.assertEqual(BridgeState.READY.value, state["state"])
        self.assertFalse(state["owns_server"])
        self.assertNotIn("start_macos.sh", runner.calls)

    def test_shutdown_does_not_stop_a_server_we_did_not_start(self):
        runner = ScriptRunner(status_code=0)
        bridge = pretend_engine_is_ready(bridge_with(runner=runner))
        bridge.start_server()

        bridge.shutdown()

        self.assertNotIn("stop_macos.sh", runner.calls)

    def test_shutdown_stops_a_server_we_started(self):
        runner = ScriptRunner(status_code=3)
        bridge = bridge_with(runner=runner)
        with patch.object(GeneratorBridge, "_observe_server", return_value=BridgeState.STOPPED):
            bridge.start_server()

        bridge.shutdown()

        self.assertIn("stop_macos.sh", runner.calls)

    def test_the_user_can_force_stop_an_adopted_server(self):
        runner = ScriptRunner(status_code=0)
        bridge = pretend_engine_is_ready(bridge_with(runner=runner))
        bridge.start_server()

        bridge.stop_server(force=True)

        self.assertIn("stop_macos.sh", runner.calls)


# -------------------------------------------------------------------- cancel


class CancelTests(unittest.TestCase):
    """Cancel has to *interrupt*, not just relabel the UI."""

    class StoppingRunner(ScriptRunner):
        """Releases the blocked generation when stop_macos.sh runs.

        That is what happens for real: there is no cancel endpoint, so the engine process is
        stopped, and the client's next request fails because the server has gone.
        """

        def __init__(self, gate, status_code=0):
            super().__init__(status_code)
            self._gate = gate

        def __call__(self, script, env_overrides=None, timeout=180.0):
            result = super().__call__(script, env_overrides, timeout)
            if Path(script).name == "stop_macos.sh":
                self._gate.set()
            return result

    def _running_bridge(self, folder, gate, error=None):
        runner = self.StoppingRunner(gate)
        bridge = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(folder),
            client_factory=lambda: FakeClient(folder, block=gate, error=error),
            runner=runner,
            log=lambda _m: None,
        )
        bridge.generate({"title": "Night", "prompt": "drive"})
        self.assertTrue(wait_for(
            lambda: bridge.state()["state"] == BridgeState.GENERATING.value
        ), "the generation never started")
        return bridge, runner

    def test_cancelling_stops_the_engine_because_there_is_no_cancel_api(self):
        # ACE-Step v0.1.8 exposes release_task and query_result and nothing that stops work
        # already handed to a worker, so the only honest cancel is to stop the process.
        import tempfile

        gate = threading.Event()
        with tempfile.TemporaryDirectory() as folder:
            bridge, runner = self._running_bridge(
                folder, gate, error=GeneratorUnreachableError("the server has gone")
            )
            state = bridge.cancel()

        self.assertEqual(BridgeState.CANCELLED.value, state["state"])
        self.assertIn("stop_macos.sh", runner.calls)
        self.assertIsNone(state["result"], "a cancelled run must not offer a file")

    def test_the_engine_going_away_during_a_cancel_is_not_reported_as_a_failure(self):
        import tempfile

        gate = threading.Event()
        with tempfile.TemporaryDirectory() as folder:
            bridge, _runner = self._running_bridge(
                folder, gate, error=GeneratorUnreachableError("the server has gone")
            )
            bridge.cancel()
            state = bridge.state()

        self.assertEqual(BridgeState.CANCELLED.value, state["state"])
        self.assertEqual("", state["error_kind"], "cancelling is not an error")

    def test_a_run_that_finishes_during_cancellation_is_not_announced(self):
        # The file is complete and stays on disk, but the user asked for it not to happen, so
        # it is not offered to the library.
        import tempfile

        gate = threading.Event()
        with tempfile.TemporaryDirectory() as folder:
            bridge, _runner = self._running_bridge(folder, gate, error=None)
            state = bridge.cancel()

        self.assertEqual(BridgeState.CANCELLED.value, state["state"])
        self.assertIsNone(state["result"])

    def test_cancelling_when_nothing_runs_changes_nothing(self):
        runner = ScriptRunner(status_code=0)
        bridge = bridge_with(runner=runner)

        bridge.cancel()

        self.assertNotIn("stop_macos.sh", runner.calls)


# --------------------------------------------------------------- the HTTP face


class BridgeServerTests(unittest.TestCase):
    def test_binding_anywhere_but_loopback_is_refused(self):
        for host in ("0.0.0.0", "192.168.1.10", "::"):
            with self.assertRaisesRegex(ValueError, "loopback"):
                serve(host=host, port=0)

    def _request(self, url, payload=None):
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        headers = {"Content-Type": "application/json"} if data else {}
        request = urllib.request.Request(url, data=data, headers=headers,
                                         method="POST" if data is not None else "GET")
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return response.status, json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            return exc.code, json.loads(exc.read().decode("utf-8"))

    def test_the_contract_answers_state_generate_and_refuses_the_unknown(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            server, _thread = serve(host="127.0.0.1", port=0, bridge=bridge)
            port = server.server_address[1]
            base = f"http://127.0.0.1:{port}"
            try:
                status, body = self._request(f"{base}/v1/state")
                self.assertEqual(200, status)
                self.assertTrue(body["ok"])
                self.assertIn("state", body["data"])

                status, body = self._request(f"{base}/v1/generate",
                                             {"title": "Night", "prompt": "drive"})
                self.assertEqual(200, status)
                self.assertEqual(BridgeState.QUEUED.value, body["data"]["state"])
                wait_for(lambda: bridge.state()["state"] == BridgeState.COMPLETED.value)

                status, body = self._request(f"{base}/v1/nope", {})
                self.assertEqual(404, status)
                self.assertFalse(body["ok"])
            finally:
                server.shutdown()
                server.server_close()

    def test_invalid_input_comes_back_as_a_400_with_a_kind(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            server, _thread = serve(host="127.0.0.1", port=0, bridge=bridge)
            port = server.server_address[1]
            try:
                status, body = self._request(
                    f"http://127.0.0.1:{port}/v1/generate", {"title": "", "prompt": "x"}
                )
                self.assertEqual(400, status)
                self.assertEqual("invalid-input", body["error"]["kind"])
            finally:
                server.shutdown()
                server.server_close()

    def test_a_second_generation_over_http_is_a_409(self):
        import tempfile

        gate = threading.Event()
        with tempfile.TemporaryDirectory() as folder:
            bridge = GeneratorBridge(
                scripts_dir=REAL_SCRIPTS,
                output_dir=Path(folder),
                client_factory=lambda: FakeClient(folder, block=gate),
                runner=ScriptRunner(status_code=0),
                log=lambda _m: None,
            )
            server, _thread = serve(host="127.0.0.1", port=0, bridge=bridge)
            port = server.server_address[1]
            base = f"http://127.0.0.1:{port}"
            try:
                self._request(f"{base}/v1/generate", {"title": "One", "prompt": "drive"})
                status, body = self._request(f"{base}/v1/generate",
                                             {"title": "Two", "prompt": "drive"})
                self.assertEqual(409, status)
                self.assertEqual("busy", body["error"]["kind"])
            finally:
                gate.set()
                wait_for(lambda: not BridgeState(bridge.state()["state"]).is_busy)
                server.shutdown()
                server.server_close()


if __name__ == "__main__":
    unittest.main()
