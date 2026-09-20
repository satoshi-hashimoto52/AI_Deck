#!/usr/bin/env bash
# Records memory and swap at one moment, with a label, so before/after can be compared.
#
# Usage:
#   ./generator/scripts/memory_report.sh before-start
#   ./generator/scripts/memory_report.sh after-load
#   ./generator/scripts/memory_report.sh after-generate
#   ./generator/scripts/memory_report.sh after-stop
#
# Each run appends one block to .aideck-generator/logs/memory.log and prints it.
#
# This script measures; it does not improve anything, and nothing in this repository claims a
# swap reduction that was not measured with it. macOS does not return swap to the pool when a
# process exits: the figure after stopping is expected to stay at its high-water mark until
# the machine is restarted.

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

label="${1:-snapshot}"
mkdir -p "$log_dir"
memory_log="$log_dir/memory.log"

pid="$(read_pid || true)"

{
    echo "=== $label — $(date '+%Y-%m-%d %H:%M:%S') ==="

    echo "-- swapusage"
    sysctl -n vm.swapusage 2>/dev/null || echo "   unavailable"

    echo "-- physical memory"
    total_bytes="$(sysctl -n hw.memsize 2>/dev/null || echo 0)"
    echo "   installed: $(( total_bytes / 1024 / 1024 / 1024 )) GB"

    # Pages are 16 KB on Apple Silicon; read the size rather than assuming it.
    page_size="$(sysctl -n hw.pagesize 2>/dev/null || echo 16384)"
    vm_stat | awk -v page="$page_size" '
        /Pages free/                 { printf "   free:        %.2f GB\n", $3 * page / 1073741824 }
        /Pages active/               { printf "   active:      %.2f GB\n", $3 * page / 1073741824 }
        /Pages wired down/           { printf "   wired:       %.2f GB\n", $4 * page / 1073741824 }
        /Pages occupied by compressor/ { printf "   compressed:  %.2f GB\n", $5 * page / 1073741824 }
    '

    echo "-- generator process"
    if [[ -n "$pid" ]] && is_running "$pid"; then
        echo "   server PID $pid"
        # The recorded PID is `uv run`; the Python child holds the models, so both are listed.
        ps -o pid=,rss=,command= -p "$pid" 2>/dev/null \
            | awk '{ rss = $2 / 1024; $1 = ""; $2 = ""; printf "   PID %s RSS %.0f MB %s\n", '"$pid"', rss, substr($0, 3, 60) }'
        for child in $(pgrep -P "$pid" 2>/dev/null || true); do
            ps -o pid=,rss=,command= -p "$child" 2>/dev/null \
                | awk '{ rss = $2 / 1024; printf "   PID %s RSS %.0f MB %s\n", $1, rss, substr($0, index($0, $3), 60) }'
        done
        total_rss="$( { echo "$pid"; pgrep -P "$pid" 2>/dev/null || true; } \
            | xargs -I{} ps -o rss= -p {} 2>/dev/null | awk '{ s += $1 } END { printf "%.0f", s / 1024 }' )"
        echo "   total RSS:   ${total_rss:-0} MB"
    else
        echo "   not running"
    fi
    echo
} | tee -a "$memory_log"

echo "Appended to $memory_log"
