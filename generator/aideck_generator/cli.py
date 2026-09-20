"""Command-line entry point for the local generator.

Exit codes are the error taxonomy, so a script wrapping this can tell "not started yet" from
"it failed" without parsing English.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from .client import AceStepClient, GenerationRequest, safe_filename
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

DEFAULT_OUTPUT_DIR = Path.home() / "Music" / "AI Deck" / "Generated"

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_UNREACHABLE = 3
EXIT_NOT_READY = 4
EXIT_HTTP = 5
EXIT_TASK_FAILED = 6
EXIT_TIMEOUT = 7
EXIT_SERVER_STOPPED = 8
EXIT_INVALID_AUDIO = 9
EXIT_OTHER = 1

_EXIT_FOR = {
    GeneratorUnreachableError: EXIT_UNREACHABLE,
    GeneratorNotReadyError: EXIT_NOT_READY,
    GeneratorHttpError: EXIT_HTTP,
    GenerationFailedError: EXIT_TASK_FAILED,
    GeneratorTimeoutError: EXIT_TIMEOUT,
    GeneratorStoppedError: EXIT_SERVER_STOPPED,
    InvalidGeneratedAudioError: EXIT_INVALID_AUDIO,
}


DEFAULT_PROMPT = (
    "Japanese female vocal, sophisticated night-drive melodic deep house and synthwave, "
    "airy intimate vocal, steady four-on-the-floor beat, deep warm bass, shimmering synth "
    "arpeggios, wide atmospheric pads, neon expressway after midnight, elegant and slightly "
    "melancholic, DJ-friendly instrumental intro and outro, no aggressive EDM drop"
)

DEFAULT_LYRICS = """[Verse 1]
窓を流れる　街の灯り
名前のない夜を追い越して
ラジオの向こう　揺れる声が
遠い記憶をそっと照らす

[Chorus]
ネオン・ハイウェイ　どこまでも
夜明けの前を走ってゆく
いまだけ時間をほどいて
この光の中で踊ろう

[Instrumental Break]

[Outro]
ネオンの先へ
"""


def _server_pid() -> int | None:
    """The PID the start script recorded, so a dead server is named as such."""
    raw = os.environ.get("AIDECK_GENERATOR_PID", "").strip()
    if raw.isdigit():
        return int(raw)

    pid_file = Path(__file__).resolve().parents[2] / ".aideck-generator" / "run" / "server.pid"
    try:
        text = pid_file.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return int(text) if text.isdigit() else None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="AI Deck local music generator client")
    parser.add_argument("--base-url", default="http://127.0.0.1:8001")
    parser.add_argument(
        "--request-timeout",
        type=float,
        default=30.0,
        help="Seconds to wait for one control request (default: 30)",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("health", help="Report whether ACE-Step is starting or ready")

    wait = subparsers.add_parser("wait", help="Block until ACE-Step has loaded its models")
    wait.add_argument("--max-wait", type=float, default=1800.0)
    wait.add_argument("--poll", type=float, default=5.0)

    generate = subparsers.add_parser("generate", help="Generate and save one verified WAV")
    generate.add_argument("--title", required=True)
    generate.add_argument("--prompt", default=DEFAULT_PROMPT)
    generate.add_argument("--lyrics-file", type=Path)
    generate.add_argument("--duration", type=float, default=30.0)
    generate.add_argument("--bpm", type=int, default=118)
    generate.add_argument("--key", default="A minor")
    generate.add_argument("--seed", type=int)
    generate.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    generate.add_argument(
        "--max-wait",
        type=float,
        default=1800.0,
        help="Seconds to allow the generation itself (default: 1800). The measured Phase 0 "
        "run was 87 s for 30 s of audio.",
    )
    generate.add_argument(
        "--poll",
        type=float,
        default=3.0,
        help="Seconds between progress checks (default: 3)",
    )
    return parser


def _progress(message: str) -> None:
    print(f"  … {message}", flush=True)


def main(argv: list[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    client = AceStepClient(
        arguments.base_url,
        request_timeout_seconds=arguments.request_timeout,
        server_pid=_server_pid(),
    )

    try:
        if arguments.command == "health":
            health = client.health()
            print(health.describe())
            return EXIT_OK if health.is_ready else EXIT_NOT_READY

        if arguments.command == "wait":
            health = client.wait_until_ready(
                timeout_seconds=arguments.max_wait,
                poll_seconds=arguments.poll,
                on_progress=_progress,
            )
            print(health.describe())
            return EXIT_OK

        lyrics = DEFAULT_LYRICS
        if arguments.lyrics_file:
            lyrics = arguments.lyrics_file.read_text(encoding="utf-8")

        request = GenerationRequest(
            title=arguments.title,
            prompt=arguments.prompt,
            lyrics=lyrics,
            duration_seconds=arguments.duration,
            bpm=arguments.bpm,
            key_scale=arguments.key,
            seed=arguments.seed,
        )

        health = client.health()
        if not health.is_ready:
            raise GeneratorNotReadyError(
                "The generator has not finished loading yet.",
                detail="Run: python3 -m generator.aideck_generator wait",
            )
        print(f"Server: {health.describe()}")
        print(
            f"Generating {arguments.duration:.0f} s locally; "
            f"allowing up to {arguments.max_wait:.0f} s."
        )

        result = client.generate(
            request,
            arguments.output_dir,
            poll_seconds=arguments.poll,
            generation_timeout_seconds=arguments.max_wait,
            on_progress=_progress,
        )
        print(f"Generated: {result.audio_path}")
        print(f"Metadata:  {result.metadata_path.name}")
        if result.audio:
            print(f"Audio:     {result.audio.describe()}")
        print(f"Elapsed:   {result.elapsed_seconds:.1f} seconds")
        if safe_filename(request.title) != result.audio_path.stem:
            print("Note: a file of that name already existed, so this one was numbered.")
        return EXIT_OK

    except GeneratorError as exc:
        print(f"ERROR: {exc.report()}", file=sys.stderr)
        for kind, code in _EXIT_FOR.items():
            if isinstance(exc, kind):
                return code
        return EXIT_OTHER
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_USAGE
    except OSError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_OTHER
    except KeyboardInterrupt:
        print("\nStopped at your request. Any running task continues on the server.")
        return EXIT_OTHER


if __name__ == "__main__":
    raise SystemExit(main())
