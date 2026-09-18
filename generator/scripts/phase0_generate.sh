#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

cd "$repo_root"
python3 -m generator.aideck_generator health
python3 -m generator.aideck_generator generate \
    --title "Neon Highway Phase 0" \
    --duration 30 \
    --bpm 118 \
    --key "A minor"

echo
echo "Phase 0 generation completed."
echo "Press ADD FILES in AI Deck to scan ~/Music/AI Deck."
