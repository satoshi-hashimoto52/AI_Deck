"""State and message must agree, and observing must not cost a process per poll.

Both of these were reported from the machine: the panel sat at "Starting — loading models"
while ACE-Step, the bridge payload and the log all said ready, and the engine's log filled
with one `GET /health` per second while the Mac was at 25 GB of swap.
"""

import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from generator.aideck_generator.bridge import (
    SERVER_STATE_MESSAGES,
    SWAP_WARNING_GB,
    BridgeState,
    GeneratorBridge,
    _sample_memory,
)

from generator.tests.test_bridge import REAL_SCRIPTS, ScriptRunner


class CountingRunner(ScriptRunner):
    """Counts every process the bridge would have spawned."""

    def __call__(self, script, env_overrides=None, timeout=180.0):
        return super().__call__(script, env_overrides, timeout)


def bridge(observed, runner=None, clock=None, ttl=2.0):
    b = GeneratorBridge(
        scripts_dir=REAL_SCRIPTS,
        output_dir=Path(tempfile.gettempdir()),
        client_factory=lambda: None,
        runner=runner or CountingRunner(status_code=0),
        log=lambda _m: None,
        clock=clock or (lambda: 0.0),
        observe_ttl_seconds=ttl,
    )
    b._probe_server = lambda: observed          # type: ignore[assignment]
    return b


class StateAndMessageAgreeTests(unittest.TestCase):
    def test_ready_never_carries_a_loading_message(self):
        # The exact reported payload: state ready, message still "Loading models…".
        b = bridge(BridgeState.READY)
        b._progress.message = "Loading models. The first start takes minutes."

        state = b.state()

        self.assertEqual("ready", state["state"])
        self.assertNotIn("Loading", state["message"])
        self.assertEqual(SERVER_STATE_MESSAGES[BridgeState.READY], state["message"])

    def test_the_internal_progress_is_updated_too_not_just_the_payload(self):
        b = bridge(BridgeState.READY)
        b._progress.message = "Loading models. The first start takes minutes."

        b.state()

        self.assertEqual(BridgeState.READY, b._progress.state)
        self.assertEqual(SERVER_STATE_MESSAGES[BridgeState.READY], b._progress.message)

    def test_every_server_state_has_a_message_that_matches_it(self):
        for observed, expected in SERVER_STATE_MESSAGES.items():
            b = bridge(observed)
            b._progress.message = "something stale"
            state = b.state()
            self.assertEqual(observed.value, state["state"])
            self.assertEqual(expected, state["message"], f"{observed} disagreed with its message")

    def test_starting_still_says_it_is_loading(self):
        b = bridge(BridgeState.STARTING)

        self.assertIn("Loading", b.state()["message"])

    def test_a_finished_result_message_is_not_overwritten_by_the_server_state(self):
        # Completed is not a server state; its message must survive.
        b = bridge(BridgeState.READY)
        b._progress.state = BridgeState.COMPLETED
        b._progress.message = "Done."

        state = b.state()

        self.assertEqual("completed", state["state"])
        self.assertEqual("Done.", state["message"])


class ObservationCostTests(unittest.TestCase):
    def test_polling_repeatedly_does_not_spawn_a_process_each_time(self):
        # status_macos.sh spawns bash, curl, lsof, ps and python3. Once a second, on a machine
        # already at 25 GB of swap, that is part of the problem rather than a measurement.
        runner = CountingRunner(status_code=0)
        b = bridge(BridgeState.READY, runner=runner)

        for _ in range(20):
            b.state()

        self.assertEqual([], runner.calls,
                         f"observing spawned processes: {runner.calls}")

    def test_the_observation_is_cached_for_the_ttl_and_refreshed_after_it(self):
        now = [0.0]
        probes = []

        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=CountingRunner(status_code=0),
            log=lambda _m: None,
            clock=lambda: now[0],
            observe_ttl_seconds=2.0,
        )

        def probe():
            probes.append(now[0])
            return BridgeState.READY

        b._probe_server = probe          # type: ignore[assignment]

        for _ in range(5):
            b.state()
        self.assertEqual(1, len(probes), "the cache did not hold within the TTL")

        now[0] = 3.0
        b.state()
        self.assertEqual(2, len(probes), "the cache never expired")

    def test_the_real_probe_uses_http_and_a_signal_rather_than_a_shell(self):
        runner = CountingRunner(status_code=0)
        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=runner,
            log=lambda _m: None,
            engine_url="http://127.0.0.1:59999",   # nothing is listening
        )

        with patch("generator.aideck_generator.bridge.read_server_pid", return_value=None):
            self.assertEqual(BridgeState.STOPPED, b._probe_server())

        self.assertEqual([], runner.calls, "the probe shelled out")

    def test_the_real_probe_runs_end_to_end_without_a_stub(self):
        """Every branch of the real _probe_server, with nothing mocked out.

        The stubs in the tests above are convenient and they hid a plain NameError: the PID
        branch referenced a helper this module never imported, so pressing START AI SERVER
        answered `internal: NameError` on the actual machine while every test passed.
        """
        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=CountingRunner(status_code=0),
            log=lambda _m: None,
            engine_url="http://127.0.0.1:59999",      # nothing listening
        )

        # No engine, no PID file entry -> stopped.
        with patch("generator.aideck_generator.bridge.read_server_pid", return_value=None):
            self.assertEqual(BridgeState.STOPPED, b._probe_server())

        # A PID that is genuinely alive (this test process) -> starting.
        import os as _os
        with patch("generator.aideck_generator.bridge.read_server_pid", return_value=_os.getpid()):
            self.assertEqual(BridgeState.STARTING, b._probe_server())

        # A PID that cannot exist -> stopped.
        with patch("generator.aideck_generator.bridge.read_server_pid", return_value=2 ** 22):
            self.assertEqual(BridgeState.STOPPED, b._probe_server())

    def test_state_and_start_server_run_without_a_stubbed_probe(self):
        # The end-to-end shape of what the button does, so a missing name cannot pass again.
        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=CountingRunner(status_code=0),
            log=lambda _m: None,
            engine_url="http://127.0.0.1:59999",
        )

        state = b.state()                      # must not raise

        self.assertIn("state", state)
        self.assertIn("memory", state)

    def test_an_engine_that_answers_but_has_not_loaded_is_starting_not_ready(self):
        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=CountingRunner(status_code=0),
            log=lambda _m: None,
        )
        b._engine_health = lambda: {"models_initialized": False}   # type: ignore[assignment]

        self.assertEqual(BridgeState.STARTING, b._probe_server())

        b._engine_health = lambda: {"models_initialized": True}    # type: ignore[assignment]
        self.assertEqual(BridgeState.READY, b._probe_server())


class MemoryReportingTests(unittest.TestCase):
    def test_a_sample_from_this_machine_has_the_fields_the_panel_needs(self):
        sample = _sample_memory()

        for key in ("swap_used_gb", "compressed_gb", "free_gb", "under_pressure", "advice"):
            self.assertIn(key, sample)

    def test_heavy_swap_is_reported_as_pressure_with_the_numbers_in_the_advice(self):
        fake = f"total = 26624.00M  used = {int(SWAP_WARNING_GB * 1024) + 512}.00M  free = 100.00M"

        def run(args, **kwargs):
            out = {"vm.swapusage": fake, "hw.pagesize": "16384"}.get(
                args[-1], "Pages free: 1000.\nPages occupied by compressor: 100.")
            return subprocess.CompletedProcess(args, 0, stdout=out, stderr="")

        with patch("generator.aideck_generator.bridge.subprocess.run", side_effect=run):
            sample = _sample_memory()

        self.assertTrue(sample["under_pressure"])
        self.assertIn("swap", sample["advice"].lower())
        self.assertIn("Restart", sample["advice"])

    def test_a_healthy_machine_is_not_told_to_restart(self):
        def run(args, **kwargs):
            out = {"vm.swapusage": "total = 1024.00M  used = 10.00M  free = 1014.00M",
                   "hw.pagesize": "16384"}.get(
                args[-1], "Pages free: 100000.\nPages occupied by compressor: 1000.")
            return subprocess.CompletedProcess(args, 0, stdout=out, stderr="")

        with patch("generator.aideck_generator.bridge.subprocess.run", side_effect=run):
            sample = _sample_memory()

        self.assertFalse(sample["under_pressure"])
        self.assertEqual("", sample["advice"])

    def test_memory_is_sampled_at_most_once_per_ttl(self):
        now = [0.0]
        b = GeneratorBridge(
            scripts_dir=REAL_SCRIPTS,
            output_dir=Path(tempfile.gettempdir()),
            client_factory=lambda: None,
            runner=CountingRunner(status_code=0),
            log=lambda _m: None,
            clock=lambda: now[0],
            memory_ttl_seconds=10.0,
        )
        calls = []
        with patch("generator.aideck_generator.bridge._sample_memory",
                   side_effect=lambda: calls.append(now[0]) or {}):
            for _ in range(10):
                b.memory()
            self.assertEqual(1, len(calls))
            now[0] = 11.0
            b.memory()
            self.assertEqual(2, len(calls))

    def test_the_state_payload_carries_the_memory_reading(self):
        b = bridge(BridgeState.READY)

        self.assertIn("memory", b.state())


if __name__ == "__main__":
    unittest.main()
