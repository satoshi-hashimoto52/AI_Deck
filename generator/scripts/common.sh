#!/usr/bin/env bash
# Shared paths and the M1 Safe profile. Sourced by every generator script so that one
# definition of "which model, which directory" cannot drift between start, stop and status.
#
# Not executable on its own.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
runtime_root="$repo_root/.aideck-generator"
ace_root="$runtime_root/ACE-Step-1.5"
run_dir="$runtime_root/run"
log_dir="$runtime_root/logs"
pid_file="$run_dir/server.pid"
port_file="$run_dir/server.port"
log_file="$log_dir/server.log"

ace_version="v0.1.8"
generator_host="127.0.0.1"
generator_port="${AIDECK_GENERATOR_PORT:-8001}"
generator_url="http://$generator_host:$generator_port"

# ---------------------------------------------------------------- M1 Safe profile
#
# Measured on this machine (see docs/GENERATOR_PHASE1.md):
#   acestep.gpu_config.get_gpu_config() -> tier4, 11.8 GB unified memory,
#   available_lm_models == ['acestep-5Hz-lm-0.6B'], resolve_lm_backend('mlx') == 'mlx'
#
# So 0.6B is not merely preferred here, it is the only LM this tier will accept; the
# server's own fallback resolves to it as well. The value is pinned anyway rather than left
# to auto-detection, because auto-detection returns the *largest* available model and that
# rule would pick a bigger one the moment the tier changed.
ACESTEP_DIT_MODEL="acestep-v15-turbo"
ACESTEP_LM_MODEL="acestep-5Hz-lm-0.6B"
ACESTEP_LM_BACKEND_NAME="mlx"

# The API server hardcodes <package parent>/checkpoints and never reads
# ACESTEP_CHECKPOINTS_DIR (acestep/api/startup_model_init.py). Phase 0 set that variable, so
# models were fetched into two places and the 1.7B LM landed in both. Point at the directory
# the server actually uses, and do not set the variable it ignores.
checkpoints_dir="$ace_root/checkpoints"

apply_m1_safe_profile() {
    export ACESTEP_CONFIG_PATH="$ACESTEP_DIT_MODEL"
    export ACESTEP_LM_MODEL_PATH="$ACESTEP_LM_MODEL"
    export ACESTEP_LM_BACKEND="$ACESTEP_LM_BACKEND_NAME"
    export ACESTEP_INIT_LLM="true"
    # The server lazy-loads by default (ACESTEP_NO_INIT defaults to True in
    # acestep/api/startup_model_init.py), which makes /health report models_initialized=false
    # for ever and hides the ten gigabytes of loading inside the first generation request.
    # Loading eagerly is what lets "starting" and "ready" mean anything.
    export ACESTEP_NO_INIT="false"
    export ACESTEP_DEVICE="auto"
    export ACESTEP_API_HOST="$generator_host"
    export ACESTEP_API_PORT="$generator_port"
    export TOKENIZERS_PARALLELISM="false"
    export HF_HOME="$runtime_root/cache/huggingface"
    # Deliberately NOT exported: ACESTEP_CHECKPOINTS_DIR. See the comment above.
}

is_running() {
    # A PID alone is not proof: PIDs are reused. Check that the process exists *and* that its
    # command line still looks like our server, so a stale file cannot make this script
    # signal something unrelated.
    local pid="$1"
    [[ -n "$pid" ]] || return 1
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" 2>/dev/null || return 1

    local command_line
    command_line="$(ps -o command= -p "$pid" 2>/dev/null || true)"
    [[ "$command_line" == *acestep* || "$command_line" == *uvicorn* ]]
}

read_pid() {
    [[ -f "$pid_file" ]] || return 1
    tr -d '[:space:]' < "$pid_file"
}

port_holder() {
    # PID listening on the generator port, if any. Empty when the port is free.
    lsof -nP -iTCP:"$generator_port" -sTCP:LISTEN -t 2>/dev/null | head -n 1 || true
}

http_status() {
    # curl prints "000" itself when it never got a response, and also exits non-zero, so the
    # result is normalised here rather than appending a second fallback to its output.
    local code
    code="$(curl --silent --max-time "${1:-5}" --output /dev/null \
        --write-out '%{http_code}' "$generator_url/health" 2>/dev/null)" || true
    if [[ "$code" =~ ^[0-9]{3}$ ]]; then
        printf '%s' "$code"
    else
        printf '000'
    fi
}

health_json() {
    curl --silent --max-time "${1:-5}" "$generator_url/health" 2>/dev/null || true
}

# Pulls one field out of the /health payload without adding a JSON dependency to a shell
# script. Only used for display; the Python client parses it properly.
health_field() {
    local payload="$1" key="$2"
    printf '%s' "$payload" | python3 -c '
import json, sys
key = sys.argv[1]
try:
    payload = json.load(sys.stdin)
except Exception:
    sys.exit(0)
data = payload.get("data", payload)
if isinstance(data, dict):
    value = data.get(key)
    if value is not None:
        print(value)
' "$key" 2>/dev/null || true
}
