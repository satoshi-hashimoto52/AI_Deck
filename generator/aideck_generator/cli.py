"""Command-line entry point used for Phase 0 and future diagnostics."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .client import AceStepClient, GenerationRequest, GeneratorError


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


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="AI Deck local music generator client")
    parser.add_argument("--base-url", default="http://127.0.0.1:8001")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("health", help="Check whether ACE-Step is ready")

    generate = subparsers.add_parser("generate", help="Generate and save one WAV file")
    generate.add_argument("--title", required=True)
    generate.add_argument("--prompt", default=DEFAULT_PROMPT)
    generate.add_argument("--lyrics-file", type=Path)
    generate.add_argument("--duration", type=float, default=30.0)
    generate.add_argument("--bpm", type=int, default=118)
    generate.add_argument("--key", default="A minor")
    generate.add_argument("--seed", type=int)
    generate.add_argument(
        "--output-dir", type=Path, default=Path.home() / "Music" / "AI Deck" / "Generated"
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    client = AceStepClient(arguments.base_url)
    try:
        if arguments.command == "health":
            client.health()
            print("ACE-Step is ready.")
            return 0

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
        print("Generating locally. The first run also loads the model and takes longer...")
        result = client.generate(request, arguments.output_dir)
        print(f"Generated: {result.audio_path.name}")
        print(f"Elapsed: {result.elapsed_seconds:.1f} seconds")
        return 0
    except (GeneratorError, ValueError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
