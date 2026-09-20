#!/usr/bin/env bash
# Starts the local ACE-Step server with the M1 Safe profile.
#
# Refuses to start a second copy, says exactly what it is loading and where, and separates
# "the process is up" from "the API will answer" — on a 16 GB M1 those are several minutes
# apart on a cold start, and Phase 0 gave the user no way to tell them apart.

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

wait_ready="${AIDECK_WAIT_READY:-true}"
ready_timeout="${AIDECK_READY_TIMEOUT:-1800}"

if [[ ! -d "$ace_root/.venv" ]]; then
    echo "ERROR: The generator is not installed."
    echo "Run: ./generator/scripts/setup_macos.sh"
    exit 1
fi

# ---------------------------------------------------------------- already running?

if existing_pid="$(read_pid)" && is_running "$existing_pid"; then
    echo "ERROR: The generator is already running as PID $existing_pid."
    echo "  Status: ./generator/scripts/status_macos.sh"
    echo "  Stop:   ./generator/scripts/stop_macos.sh"
    exit 1
fi

if [[ -f "$pid_file" ]]; then
    stale="$(read_pid || true)"
    echo "Note: removing a stale PID file (PID ${stale:-unknown} is not our server)."
    rm -f "$pid_file"
fi

holder="$(port_holder)"
if [[ -n "$holder" ]]; then
    echo "ERROR: Port $generator_port is already in use by PID $holder:"
    ps -o pid=,command= -p "$holder" 2>/dev/null | sed 's/^/    /'
    echo
    echo "That is not a server this script started. Either stop it yourself, or start the"
    echo "generator on another port with:"
    echo "    AIDECK_GENERATOR_PORT=8011 ./generator/scripts/start_macos.sh"
    exit 1
fi

# ---------------------------------------------------------------- start

mkdir -p "$run_dir" "$log_dir" "$runtime_root/cache"
apply_m1_safe_profile

cat <<EOF
AI Deck local generator — M1 Safe profile
  URL            : $generator_url
  Generation     : $ACESTEP_DIT_MODEL
  Language model : $ACESTEP_LM_MODEL (backend: $ACESTEP_LM_BACKEND_NAME)
  Checkpoints    : $checkpoints_dir
  HF cache       : $HF_HOME
  Log            : $log_file
EOF

# Job control is switched on so the server becomes its own process group leader. `uv run`
# spawns Python as a child, so signalling the recorded PID alone would orphan the worker that
# is actually holding several gigabytes; stop_macos.sh signals the group.
cd "$ace_root"
set -m
uv run acestep-api \
    --host "$generator_host" \
    --port "$generator_port" \
    --download-source huggingface \
    --init-llm \
    --lm-model-path "$ACESTEP_LM_MODEL" \
    >> "$log_file" 2>&1 &
server_pid=$!
set +m

echo "$server_pid" > "$pid_file"
echo "$generator_port" > "$port_file"
echo "  PID            : $server_pid"
echo

cleanup() {
    trap - INT TERM
    echo
    echo "Stopping the generator (PID $server_pid)..."
    "$script_dir/stop_macos.sh" || true
    exit 0
}
trap cleanup INT TERM

if [[ "$wait_ready" != "true" ]]; then
    echo "Started in the background. Follow it with:"
    echo "    ./generator/scripts/status_macos.sh"
    exit 0
fi

echo "Loading models. A cold start downloads and loads about 10 GB and can take"
echo "several minutes; this is not a hang. Press Ctrl+C to stop the server."
echo

started_at="$(date +%s)"
last_state=""
while true; do
    if ! is_running "$server_pid"; then
        echo
        echo "ERROR: The generator exited during startup. Last lines of its log:"
        tail -n 25 "$log_file" | sed 's/^/    /'
        rm -f "$pid_file"
        exit 1
    fi

    status="$(http_status 5)"
    if [[ "$status" == "200" ]]; then
        payload="$(health_json 5)"
        if [[ "$(health_field "$payload" models_initialized)" == "True" ]]; then
            elapsed=$(( $(date +%s) - started_at ))
            echo "READY after ${elapsed}s."
            echo "  Generation model : $(health_field "$payload" loaded_model)"
            echo "  Language model   : $(health_field "$payload" loaded_lm_model)"
            echo "  LM initialised   : $(health_field "$payload" llm_initialized)"
            echo
            echo "Next:"
            echo "    ./generator/scripts/phase1_generate.sh"
            echo
            echo "Leave this window open. Press Ctrl+C to stop the server."
            break
        fi
        state="API is up, models still loading"
    elif [[ "$status" == "000" ]]; then
        state="process is up, the API is not accepting connections yet"
    else
        state="API answered HTTP $status"
    fi

    if [[ "$state" != "$last_state" ]]; then
        echo "  … $state"
        last_state="$state"
    fi

    if (( $(date +%s) - started_at > ready_timeout )); then
        echo "ERROR: Still not ready after ${ready_timeout}s. The server is still running as"
        echo "PID $server_pid; inspect $log_file, or stop it with stop_macos.sh."
        exit 1
    fi
    sleep 5
done

# Stay in the foreground so Ctrl+C means what the user expects.
wait "$server_pid"
