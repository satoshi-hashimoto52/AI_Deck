"""Client behaviour, with no model and no server.

Every test here runs in milliseconds against a fake opener. Loading a real 4.5 GB model to
find out whether a timeout is classified correctly would make the suite unrunnable, and the
things most likely to be wrong — which error a failure becomes, whether a header-only WAV is
accepted, whether a second file of the same name overwrites the first — need no model at all.
"""

import io
import json
import socket
import struct
import tempfile
import unittest
import urllib.error
import wave
from pathlib import Path

from generator.aideck_generator.client import (
    MAC_DIT_MODEL,
    MAC_LM_BACKEND,
    MAC_LM_MODEL,
    AceStepClient,
    GenerationRequest,
    safe_filename,
)
from generator.aideck_generator.errors import (
    GenerationFailedError,
    GeneratorHttpError,
    GeneratorNotReadyError,
    GeneratorStoppedError,
    GeneratorTimeoutError,
    GeneratorUnreachableError,
    InvalidGeneratedAudioError,
)
from generator.aideck_generator.wave_check import WaveValidationError, inspect_wave


# --------------------------------------------------------------------------- helpers


class FakeResponse:
    def __init__(self, body):
        self._body = io.BytesIO(body)

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        return False

    def read(self, size=-1):
        return self._body.read(size)


class QueueOpener:
    """Returns queued bodies, or raises a queued exception, one call at a time."""

    def __init__(self, responses):
        self._responses = list(responses)
        self.requests = []

    def __call__(self, request, timeout):
        self.requests.append((request, timeout))
        item = self._responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return FakeResponse(item)


def response(data):
    return json.dumps({"data": data, "code": 200, "error": None}).encode("utf-8")


def health_body(ready=True, lm=MAC_LM_MODEL):
    return response(
        {
            "status": "ok",
            "models_initialized": ready,
            "llm_initialized": ready,
            "loaded_model": MAC_DIT_MODEL,
            "loaded_lm_model": lm,
        }
    )


def wav_bytes(seconds=30.0, sample_rate=8000, silent=False, frames_override=None):
    """A real WAV, so the validator is exercised rather than mocked."""
    frame_count = frames_override if frames_override is not None else int(seconds * sample_rate)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        if silent:
            payload = b"\x00\x00" * frame_count
        else:
            payload = struct.pack(f"<{frame_count}h", *([12000, -12000] * (frame_count // 2 + 1))[:frame_count])
        handle.writeframes(payload)
    return buffer.getvalue()


def client_for(responses, **kwargs):
    opener = QueueOpener(responses)
    kwargs.setdefault("sleep", lambda _seconds: None)
    return AceStepClient(opener=opener, **kwargs), opener


def a_request(**overrides):
    fields = dict(title="Night Drive", prompt="night drive", lyrics="la", duration_seconds=30.0)
    fields.update(overrides)
    return GenerationRequest(**fields)


# --------------------------------------------------------------------------- request


class GenerationRequestTests(unittest.TestCase):
    def test_payload_pins_the_mac_safe_models(self):
        payload = a_request().to_payload()

        self.assertEqual(MAC_DIT_MODEL, payload["model"])
        self.assertEqual(MAC_LM_MODEL, payload["lm_model_path"])
        self.assertEqual(MAC_LM_BACKEND, payload["lm_backend"])
        self.assertEqual("wav", payload["audio_format"])
        self.assertEqual(1, payload["batch_size"])

    def test_the_pinned_language_model_is_the_small_one(self):
        # The 1.7B is what the upstream default resolves to, and it is the reason this
        # profile exists at all. If this ever reads 1.7B again, it was not deliberate.
        self.assertEqual("acestep-5Hz-lm-0.6B", MAC_LM_MODEL)

    def test_invalid_duration_is_refused_before_a_request(self):
        with self.assertRaisesRegex(ValueError, "Duration"):
            a_request(duration_seconds=9).validate()

    def test_a_blank_title_is_refused(self):
        with self.assertRaisesRegex(ValueError, "Title"):
            a_request(title="   ").validate()


class SafeFilenameTests(unittest.TestCase):
    def test_path_separators_cannot_escape_the_output_folder(self):
        self.assertNotIn("/", safe_filename("../../etc/passwd"))
        self.assertNotIn("..", safe_filename("../../etc/passwd"))

    def test_a_title_of_only_punctuation_still_produces_a_name(self):
        self.assertEqual("AI Deck Generated", safe_filename("???"))
        self.assertEqual("AI Deck Generated", safe_filename(".."))

    def test_ordinary_titles_are_kept_readable(self):
        self.assertEqual("Neon Highway Phase 1", safe_filename("Neon Highway Phase 1"))

    def test_japanese_titles_survive(self):
        self.assertEqual("ネオン", safe_filename("ネオン"))


# --------------------------------------------------------------------------- health


class HealthTests(unittest.TestCase):
    def test_ready_is_reported_with_the_loaded_models(self):
        client, _ = client_for([health_body(ready=True)])

        health = client.health()

        self.assertTrue(health.is_ready)
        self.assertEqual(MAC_LM_MODEL, health.loaded_lm_model)
        self.assertIn("ready", health.describe())

    def test_a_loading_server_is_not_ready_and_says_so(self):
        client, _ = client_for([health_body(ready=False)])

        health = client.health()

        self.assertFalse(health.is_ready)
        self.assertIn("starting", health.describe())

    def test_waiting_polls_until_the_models_are_loaded(self):
        client, _ = client_for([health_body(ready=False), health_body(ready=False), health_body(True)])

        health = client.wait_until_ready(timeout_seconds=100, poll_seconds=0)

        self.assertTrue(health.is_ready)

    def test_waiting_gives_up_with_a_timeout_that_names_the_limit(self):
        clock = iter([0.0, 0.0, 5.0, 11.0, 11.0])
        client, _ = client_for(
            [health_body(ready=False)] * 5, monotonic=lambda: next(clock)
        )

        with self.assertRaises(GeneratorTimeoutError) as caught:
            client.wait_until_ready(timeout_seconds=10, poll_seconds=0)

        self.assertEqual(10, caught.exception.limit_seconds)


# --------------------------------------------------------------- error classification


class ErrorClassificationTests(unittest.TestCase):
    def test_a_refused_connection_is_unreachable_not_a_generic_failure(self):
        refused = urllib.error.URLError(ConnectionRefusedError(61, "Connection refused"))
        client, _ = client_for([refused])

        with self.assertRaises(GeneratorUnreachableError) as caught:
            client.health()

        self.assertEqual("unreachable", caught.exception.kind)
        self.assertIn("start_macos.sh", caught.exception.report())

    def test_a_socket_timeout_is_a_timeout_not_unreachable(self):
        client, _ = client_for([socket.timeout("timed out")], request_timeout_seconds=30)

        with self.assertRaises(GeneratorTimeoutError) as caught:
            client.health()

        self.assertEqual("timeout", caught.exception.kind)
        self.assertEqual(30, caught.exception.limit_seconds)

    def test_a_503_is_reported_as_still_loading(self):
        error = urllib.error.HTTPError(
            "http://127.0.0.1:8001/health", 503, "Service Unavailable", {}, io.BytesIO(b"loading")
        )
        client, _ = client_for([error])

        with self.assertRaises(GeneratorNotReadyError) as caught:
            client.health()

        self.assertEqual("not-ready", caught.exception.kind)

    def test_an_initialising_message_in_a_200_body_is_still_not_ready(self):
        body = json.dumps({"code": 500, "error": "Models are still initializing"}).encode()
        client, _ = client_for([body])

        with self.assertRaises(GeneratorNotReadyError):
            client.health()

    def test_another_http_status_keeps_its_code(self):
        error = urllib.error.HTTPError(
            "http://127.0.0.1:8001/health", 500, "Server Error", {}, io.BytesIO(b"boom")
        )
        client, _ = client_for([error])

        with self.assertRaises(GeneratorHttpError) as caught:
            client.health()

        self.assertEqual(500, caught.exception.status)
        self.assertIn("boom", caught.exception.report())

    def test_a_dead_server_process_is_reported_as_stopped(self):
        # PID 1 exists; a PID we know is gone is what proves the branch. 2**22 is above the
        # macOS maximum, so it can never be in use.
        refused = urllib.error.URLError(ConnectionRefusedError(61, "Connection refused"))
        client, _ = client_for(
            [response({"task_id": "t1"}), refused], server_pid=2 ** 22
        )

        with self.assertRaises(GeneratorStoppedError) as caught:
            client.generate(a_request(), Path(tempfile.gettempdir()), poll_seconds=0)

        self.assertEqual("server-stopped", caught.exception.kind)
        self.assertIn("memory pressure", str(caught.exception))

    def test_a_failed_task_is_a_task_failure_with_the_server_reason(self):
        client, _ = client_for(
            [
                response({"task_id": "t1"}),
                response([{"task_id": "t1", "status": 2, "error": "CUDA out of memory"}]),
            ]
        )

        with self.assertRaises(GenerationFailedError) as caught:
            client.generate(a_request(), Path(tempfile.gettempdir()), poll_seconds=0)

        self.assertEqual("task-failed", caught.exception.kind)
        self.assertIn("CUDA out of memory", caught.exception.report())


# --------------------------------------------------------------------- long generation


class GenerationTimingTests(unittest.TestCase):
    def test_a_generation_longer_than_the_request_timeout_still_succeeds(self):
        # The regression that matters: Phase 0's single 30-second timeout would have failed
        # the measured 87-second run. The control timeout stays small; the generation gets
        # its own, larger budget.
        clock = iter([0.0, 5.0, 40.0, 95.0, 120.0, 130.0, 140.0])
        with tempfile.TemporaryDirectory() as folder:
            client, _ = client_for(
                [
                    response({"task_id": "t1"}),
                    response([{"task_id": "t1", "status": 0}]),
                    response(
                        [
                            {
                                "task_id": "t1",
                                "status": 1,
                                "result": json.dumps([{"file": "/outputs/a.wav"}]),
                            }
                        ]
                    ),
                    wav_bytes(30.0),
                ],
                request_timeout_seconds=30.0,
                monotonic=lambda: next(clock),
            )

            result = client.generate(
                a_request(), Path(folder), poll_seconds=0, generation_timeout_seconds=1800
            )

            self.assertTrue(result.audio_path.exists())
            self.assertGreater(result.elapsed_seconds, 30.0)

    def test_the_download_gets_a_longer_timeout_than_a_control_call(self):
        with tempfile.TemporaryDirectory() as folder:
            client, opener = client_for(
                [
                    response({"task_id": "t1"}),
                    response(
                        [
                            {
                                "task_id": "t1",
                                "status": 1,
                                "result": json.dumps([{"file": "/outputs/a.wav"}]),
                            }
                        ]
                    ),
                    wav_bytes(30.0),
                ],
                request_timeout_seconds=30.0,
                download_timeout_seconds=600.0,
            )
            client.generate(a_request(), Path(folder), poll_seconds=0)

        control_timeout = opener.requests[0][1]
        download_timeout = opener.requests[-1][1]
        self.assertEqual(30.0, control_timeout)
        self.assertEqual(600.0, download_timeout)

    def test_exceeding_the_generation_budget_is_a_timeout_naming_the_limit(self):
        clock = iter([0.0, 10.0, 700.0])
        client, _ = client_for(
            [
                response({"task_id": "t1"}),
                response([{"task_id": "t1", "status": 0}]),
                response([{"task_id": "t1", "status": 0}]),
            ],
            monotonic=lambda: next(clock),
        )

        with self.assertRaises(GeneratorTimeoutError) as caught:
            client.generate(
                a_request(), Path(tempfile.gettempdir()), poll_seconds=0,
                generation_timeout_seconds=600,
            )

        self.assertEqual(600, caught.exception.limit_seconds)
        self.assertIn("still be running", str(caught.exception))


# --------------------------------------------------------------------------- output


class OutputFileTests(unittest.TestCase):
    def _generate_into(self, folder, title="Night Drive", audio=None):
        client, _ = client_for(
            [
                response({"task_id": "t1"}),
                response(
                    [
                        {
                            "task_id": "t1",
                            "status": 1,
                            "result": json.dumps([{"file": "/outputs/a.wav", "seed": 7}]),
                        }
                    ]
                ),
                audio if audio is not None else wav_bytes(30.0),
            ]
        )
        return client.generate(a_request(title=title), Path(folder), poll_seconds=0)

    def test_a_successful_generation_writes_audio_and_metadata(self):
        with tempfile.TemporaryDirectory() as folder:
            result = self._generate_into(folder)

            self.assertTrue(result.audio_path.exists())
            self.assertTrue(result.metadata_path.exists())
            metadata = json.loads(result.metadata_path.read_text(encoding="utf-8"))
            self.assertEqual("m1-safe", metadata["profile"])
            self.assertEqual(MAC_LM_MODEL, metadata["models"]["lm"])
            self.assertEqual("Night Drive", metadata["request"]["title"])
            self.assertEqual(7, metadata["generator_result"]["seed"])
            self.assertGreater(metadata["audio"]["duration_seconds"], 1.0)

    def test_a_second_track_of_the_same_name_does_not_overwrite_the_first(self):
        with tempfile.TemporaryDirectory() as folder:
            first = self._generate_into(folder)
            first_bytes = first.audio_path.read_bytes()

            second = self._generate_into(folder)

            self.assertNotEqual(first.audio_path, second.audio_path)
            self.assertEqual("Night Drive 2", second.audio_path.stem)
            self.assertTrue(first.audio_path.exists())
            self.assertEqual(first_bytes, first.audio_path.read_bytes())
            self.assertTrue(first.metadata_path.exists())
            self.assertTrue(second.metadata_path.exists())

    def test_no_partial_file_is_left_behind_when_the_audio_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(InvalidGeneratedAudioError):
                self._generate_into(folder, audio=wav_bytes(30.0, silent=True))

            self.assertEqual([], sorted(Path(folder).iterdir()))

    def test_a_non_wave_download_is_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(InvalidGeneratedAudioError):
                self._generate_into(folder, audio=b"not audio at all")

            self.assertEqual([], sorted(Path(folder).iterdir()))

    def test_a_remote_audio_url_is_refused(self):
        client, _ = client_for(
            [
                response({"task_id": "t1"}),
                response(
                    [
                        {
                            "task_id": "t1",
                            "status": 1,
                            "result": json.dumps([{"file": "http://example.com/a.wav"}]),
                        }
                    ]
                ),
            ]
        )

        with self.assertRaisesRegex(Exception, "local audio URL"):
            client.generate(a_request(), Path(tempfile.gettempdir()), poll_seconds=0)


# ---------------------------------------------------------------------- wav validation


class WaveValidationTests(unittest.TestCase):
    def _write(self, folder, body):
        path = Path(folder) / "probe.wav"
        path.write_bytes(body)
        return path

    def test_a_normal_track_is_accepted_and_summarised(self):
        with tempfile.TemporaryDirectory() as folder:
            summary = inspect_wave(self._write(folder, wav_bytes(30.0)))

            self.assertEqual(1, summary.channels)
            self.assertAlmostEqual(30.0, summary.duration_seconds, places=1)
            self.assertGreater(summary.peak_amplitude, 0.0)

    def test_a_header_with_no_frames_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self._write(folder, wav_bytes(0.0, frames_override=0))

            with self.assertRaisesRegex(WaveValidationError, "no audio frames"):
                inspect_wave(path)

    def test_digital_silence_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self._write(folder, wav_bytes(30.0, silent=True))

            with self.assertRaisesRegex(WaveValidationError, "silence"):
                inspect_wave(path)

    def test_a_truncated_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self._write(folder, wav_bytes(30.0)[:20])

            with self.assertRaises(WaveValidationError):
                inspect_wave(path)

    def test_a_file_far_shorter_than_requested_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self._write(folder, wav_bytes(2.0))

            with self.assertRaisesRegex(WaveValidationError, "too short"):
                inspect_wave(path, minimum_seconds=15.0)

    def test_a_missing_file_is_rejected_rather_than_crashing(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(WaveValidationError, "was not written"):
                inspect_wave(Path(folder) / "absent.wav")


if __name__ == "__main__":
    unittest.main()
