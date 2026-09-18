import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from generator.aideck_generator.client import (
    AceStepClient,
    GenerationRequest,
    GeneratorError,
)


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
    def __init__(self, responses):
        self._responses = list(responses)
        self.requests = []

    def __call__(self, request, timeout):
        self.requests.append(request)
        return FakeResponse(self._responses.pop(0))


def response(data):
    return json.dumps({"data": data, "code": 200, "error": None}).encode("utf-8")


class GenerationRequestTests(unittest.TestCase):
    def test_payload_uses_the_small_mac_model_and_wav(self):
        payload = GenerationRequest("Night", "night drive", "lyrics").to_payload()

        self.assertEqual("acestep-v15-turbo", payload["model"])
        self.assertEqual("acestep-5Hz-lm-0.6B", payload["lm_model_path"])
        self.assertEqual("mlx", payload["lm_backend"])
        self.assertEqual("wav", payload["audio_format"])
        self.assertEqual(1, payload["batch_size"])

    def test_invalid_duration_is_refused_before_a_request(self):
        with self.assertRaisesRegex(ValueError, "Duration"):
            GenerationRequest("Night", "night drive", "", duration_seconds=9).validate()


class AceStepClientTests(unittest.TestCase):
    def test_generation_polls_downloads_and_writes_metadata_atomically(self):
        wave = b"RIFF" + (b"\x00" * 4) + b"WAVE" + b"audio"
        opener = QueueOpener(
            [
                response({"task_id": "task-1", "status": "queued"}),
                response([{"task_id": "task-1", "status": 0, "result": None}]),
                response(
                    [
                        {
                            "task_id": "task-1",
                            "status": 1,
                            "result": json.dumps(
                                [{"file": "/v1/audio?path=result.wav", "metas": {"bpm": 118}}]
                            ),
                        }
                    ]
                ),
                wave,
            ]
        )
        client = AceStepClient(opener=opener)

        with tempfile.TemporaryDirectory() as directory:
            with patch("generator.aideck_generator.client.time.sleep"):
                result = client.generate(
                    GenerationRequest("Neon / Highway", "night drive", "lyrics"),
                    Path(directory),
                    poll_seconds=0.05,
                )

            self.assertEqual("Neon Highway.wav", result.audio_path.name)
            self.assertEqual(wave, result.audio_path.read_bytes())
            metadata = json.loads(result.metadata_path.read_text(encoding="utf-8"))
            self.assertEqual("task-1", metadata["task_id"])
            self.assertEqual(118, metadata["generator_result"]["metas"]["bpm"])
            self.assertFalse(list(Path(directory).glob("*.part")))

    def test_failed_task_surfaces_the_generator_error(self):
        opener = QueueOpener(
            [
                response({"task_id": "task-2", "status": "queued"}),
                response([{"task_id": "task-2", "status": 2, "error": "out of memory"}]),
            ]
        )

        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(GeneratorError, "out of memory"):
                AceStepClient(opener=opener).generate(
                    GenerationRequest("Night", "night drive", "lyrics"),
                    Path(directory),
                    poll_seconds=0.05,
                )

    def test_non_wave_download_is_deleted_and_refused(self):
        opener = QueueOpener(
            [
                response({"task_id": "task-3", "status": "queued"}),
                response(
                    [
                        {
                            "task_id": "task-3",
                            "status": 1,
                            "result": json.dumps([{"file": "/v1/audio?path=result.wav"}]),
                        }
                    ]
                ),
                b"not a wave",
            ]
        )

        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(GeneratorError, "valid WAV"):
                AceStepClient(opener=opener).generate(
                    GenerationRequest("Night", "night drive", "lyrics"),
                    Path(directory),
                    poll_seconds=0.05,
                )
            self.assertFalse(list(Path(directory).iterdir()))


if __name__ == "__main__":
    unittest.main()
