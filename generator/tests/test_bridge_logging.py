"""Logging must never be able to fail an operation.

These exist because it did. AI Deck starts the bridge as a child process; when AI Deck goes
away the bridge can be left running with a standard output whose reader has closed. The first
thing ``start_server`` used to do was log, so pressing START AI SERVER answered
``internal: BrokenPipeError`` and the engine was never started.
"""

import io
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

from generator.aideck_generator.bridge import (
    BridgeLogger,
    BridgeState,
    GeneratorBridge,
    loggable,
    redact,
)

from generator.tests.test_bridge import REAL_SCRIPTS, ScriptRunner, wait_for


class BrokenStream:
    """A stream that behaves like a pipe whose reader has gone."""

    def __init__(self, error=None):
        self.error = error or BrokenPipeError(32, "Broken pipe")
        self.attempts = 0

    def write(self, _text):
        self.attempts += 1
        raise self.error

    def flush(self):
        raise self.error


class BridgeLoggerTests(unittest.TestCase):
    def test_a_broken_stream_does_not_raise(self):
        with tempfile.TemporaryDirectory() as folder:
            logger = BridgeLogger(path=Path(folder) / "bridge.log", stream=BrokenStream())

            logger("starting the engine")   # must not raise

            self.assertTrue(logger.stream_broken)

    def test_a_broken_stream_is_abandoned_rather_than_retried(self):
        # Retrying a dead pipe on every line would turn one failure into thousands.
        with tempfile.TemporaryDirectory() as folder:
            stream = BrokenStream()
            logger = BridgeLogger(path=Path(folder) / "bridge.log", stream=stream)

            for _ in range(5):
                logger("a line")

            self.assertEqual(1, stream.attempts)

    def test_the_line_still_reaches_the_file_when_the_stream_is_broken(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bridge.log"
            logger = BridgeLogger(path=path, stream=BrokenStream())

            logger("the engine was started")

            self.assertIn("the engine was started", path.read_text(encoding="utf-8"))

    def test_a_closed_stream_is_survived_too(self):
        with tempfile.TemporaryDirectory() as folder:
            stream = io.StringIO()
            stream.close()
            logger = BridgeLogger(path=Path(folder) / "bridge.log", stream=stream)

            logger("a line")   # ValueError: I/O operation on closed file

            self.assertTrue(logger.stream_broken)

    def test_an_unwritable_log_directory_is_survived(self):
        stream = io.StringIO()
        logger = BridgeLogger(path=Path("/dev/null/cannot/exist/bridge.log"), stream=stream)

        logger("a line")   # must not raise

        self.assertIn("a line", stream.getvalue())

    def test_the_log_file_holds_no_home_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bridge.log"
            logger = BridgeLogger(path=path, stream=io.StringIO())

            logger(f"wrote {Path.home()}/Music/AI Deck/Generated/x.wav")

            written = path.read_text(encoding="utf-8")
            self.assertNotIn(str(Path.home()), written)
            self.assertIn("~/Music", written)


class StartServerSurvivesLoggingTests(unittest.TestCase):
    """The operation that actually broke."""

    def _bridge(self, log):
        return GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=ScriptRunner(status_code=0),   # engine already ready
            log=log,
        )

    def test_start_server_succeeds_although_the_logger_raises(self):
        def exploding(_message):
            raise BrokenPipeError(32, "Broken pipe")

        state = self._bridge(exploding).start_server()

        self.assertEqual(BridgeState.READY.value, state["state"])

    def test_a_logger_that_raises_anything_else_is_also_survived(self):
        def exploding(_message):
            raise RuntimeError("the logger is broken")

        state = self._bridge(exploding).start_server()

        self.assertEqual(BridgeState.READY.value, state["state"])

    def test_state_is_still_readable_with_a_broken_logger(self):
        def exploding(_message):
            raise BrokenPipeError(32, "Broken pipe")

        self.assertIn("state", self._bridge(exploding).state())

    def test_the_default_logger_survives_a_real_orphaned_pipe(self):
        # The full situation, reproduced: a child whose standard streams are pipes that
        # nobody will ever read, asked to do the thing that used to fail.
        # The child logs to a temporary file rather than the project's own bridge log: a test
        # has no business writing into the runtime directory it is testing.
        script = (
            "import sys; sys.path.insert(0, %r)\n"
            "from pathlib import Path\n"
            "from generator.aideck_generator.bridge import BridgeLogger, GeneratorBridge\n"
            "b = GeneratorBridge(log=BridgeLogger(path=Path(%r)))\n"
            "b._log('a line that would break a dead pipe')\n"
            "print('SURVIVED', file=open(%r, 'w'))\n"
        )
        with tempfile.TemporaryDirectory() as folder:
            marker = os.path.join(folder, "marker.txt")
            child_log = os.path.join(folder, "bridge.log")
            source = os.path.join(folder, "child.py")
            Path(source).write_text(
                script % (str(Path(__file__).resolve().parents[2]), child_log, marker),
                encoding="utf-8",
            )

            child = subprocess.Popen(
                [sys.executable, source],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                cwd=str(Path(__file__).resolve().parents[2]),
            )
            child.stdout.close()     # the reader goes away, exactly like a quitting AI Deck
            child.stderr.close()
            child.wait(timeout=60)

            self.assertEqual(0, child.returncode, "the child died writing a log line")
            self.assertIn("SURVIVED", Path(marker).read_text(encoding="utf-8"))


class OrphanBridgeTests(unittest.TestCase):
    def test_an_adopted_engine_is_not_stopped_on_shutdown(self):
        # An orphaned bridge that finds an engine running did not start it, so quitting must
        # leave it alone.
        runner = ScriptRunner(status_code=0)
        bridge = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=runner,
            log=lambda _m: None,
        )
        bridge.start_server()

        bridge.shutdown()

        self.assertNotIn("stop_macos.sh", runner.calls)

    def test_an_owned_engine_is_stopped_on_shutdown(self):
        from unittest.mock import patch

        runner = ScriptRunner(status_code=3)
        bridge = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=runner,
            log=lambda _m: None,
        )
        with patch.object(GeneratorBridge, "_observe_server", return_value=BridgeState.STOPPED):
            bridge.start_server()

        bridge.shutdown()

        self.assertIn("stop_macos.sh", runner.calls)


if __name__ == "__main__":
    unittest.main()
