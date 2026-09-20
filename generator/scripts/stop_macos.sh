#!/usr/bin/env bash
# Stops the generator this project started, and nothing else.
#
# The rule is that a PID file is a claim, not a fact. PIDs are reused, so every signal below
# is sent only after the process has been confirmed to still be an ACE-Step server. There is
# no `pkill python` anywhere in here on purpose: the user's other Python work is not ours to
# end.

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

grace_seconds="${AIDECK_STOP_GRACE:-20}"

pid="$(read_pid || true)"

if [[ -z "$pid" ]]; then
    echo "No PID file at $pid_file — the generator was not started by these scripts."
    holder="$(port_holder)"
    if [[ -n "$holder" ]]; then
        echo "Something else is listening on port $generator_port:"
        ps -o pid=,command= -p "$holder" 2>/dev/null | sed 's/^/    /'
        echo "Left alone. Stop it yourself if it is yours."
    fi
    exit 0
fi

if ! is_running "$pid"; then
    echo "PID $pid is not a running ACE-Step server; removing the stale PID file."
    rm -f "$pid_file" "$port_file"
    exit 0
fi

echo "Stopping the generator (PID $pid)..."

# Collect the process and its descendants before signalling, because once the parent is gone
# the tree cannot be walked. `uv run` is the parent of the Python process that holds the
# models, so stopping only the recorded PID would leave several gigabytes resident.
descendants_of() {
    local parent="$1" child
    for child in $(pgrep -P "$parent" 2>/dev/null || true); do
        printf '%s\n' "$child"
        descendants_of "$child"
    done
}
targets="$pid
$(descendants_of "$pid")"

# Signal the process group first — it is one call and it reaches workers started after the
# tree was walked. Falls back to the collected PIDs if the group has already gone.
kill -TERM -- "-$pid" 2>/dev/null || kill -TERM $targets 2>/dev/null || true

waited=0
while (( waited < grace_seconds )); do
    if ! is_running "$pid"; then
        break
    fi
    sleep 1
    waited=$((waited + 1))
done

if is_running "$pid"; then
    echo "It did not stop within ${grace_seconds}s; sending SIGKILL."
    kill -KILL -- "-$pid" 2>/dev/null || kill -KILL $targets 2>/dev/null || true
    sleep 2
fi

# Anything left from the tree, but only what we collected and only if it is still ours.
for target in $targets; do
    if kill -0 "$target" 2>/dev/null; then
        command_line="$(ps -o command= -p "$target" 2>/dev/null || true)"
        if [[ "$command_line" == *acestep* || "$command_line" == *uvicorn* ]]; then
            kill -KILL "$target" 2>/dev/null || true
        fi
    fi
done

rm -f "$pid_file" "$port_file"

if is_running "$pid"; then
    echo "ERROR: PID $pid is still running. Inspect it with: ps -p $pid"
    exit 1
fi

remaining="$(port_holder)"
if [[ -n "$remaining" ]]; then
    echo "Note: port $generator_port is still held by PID $remaining, which we did not start:"
    ps -o pid=,command= -p "$remaining" 2>/dev/null | sed 's/^/    /'
fi

echo "Stopped."
echo
echo "Swap that macOS allocated during generation is not released by stopping the server;"
echo "it stays until the machine is restarted. ./generator/scripts/memory_report.sh shows it."
