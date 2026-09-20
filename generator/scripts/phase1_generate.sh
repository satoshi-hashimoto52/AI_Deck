#!/usr/bin/env bash
# Generates one 30-second track with the M1 Safe profile and takes a memory reading either
# side of it, so every run produces the measurement the documentation quotes.
#
#   ./generator/scripts/phase1_generate.sh ["Track title"] [duration-seconds]

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

title="${1:-Neon Highway Phase 1}"
duration="${2:-30}"

cd "$repo_root"

echo "== Checking the server =="
if ! "$script_dir/status_macos.sh"; then
    status=$?
    if [[ $status -eq 3 ]]; then
        echo
        echo "Start it first: ./generator/scripts/start_macos.sh"
        exit 3
    fi
    if [[ $status -eq 4 ]]; then
        echo
        echo "Waiting for the models to finish loading..."
        python3 -m generator.aideck_generator wait --max-wait 1800 || exit $?
    else
        exit $status
    fi
fi

echo
"$script_dir/memory_report.sh" before-generate >/dev/null
echo "== Generating =="

set +e
AIDECK_GENERATOR_PID="$(read_pid || true)" \
python3 -m generator.aideck_generator generate \
    --title "$title" \
    --duration "$duration" \
    --bpm 118 \
    --key "A minor" \
    --max-wait 1800 \
    --poll 3
generate_status=$?
set -e

echo
"$script_dir/memory_report.sh" after-generate >/dev/null

if [[ $generate_status -ne 0 ]]; then
    echo "Generation did not succeed (exit $generate_status). Nothing was added to your music"
    echo "folder. The message above says which kind of failure it was."
    exit $generate_status
fi

cat <<EOF

Done. The track is in ~/Music/AI Deck/Generated alongside a .json of the exact settings.
Press ADD FILES in AI Deck with ~/Music/AI Deck in the path field to import it.

Memory readings were appended to $log_dir/memory.log
EOF
