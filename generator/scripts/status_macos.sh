#!/usr/bin/env bash
# Reports what the generator is doing, distinguishing "not started", "starting" and "ready".
#
# Exit codes mirror the Python client so a script can branch on them:
#   0 ready   3 not running   4 starting   5 answered with an HTTP error

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

echo "AI Deck local generator"
echo "  URL         : $generator_url"
echo "  Checkpoints : $checkpoints_dir"
echo "  Log         : $log_file"

pid="$(read_pid || true)"
if [[ -n "$pid" ]] && is_running "$pid"; then
    rss_kb="$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ')"
    echo "  Process     : running as PID $pid (RSS $(( ${rss_kb:-0} / 1024 )) MB)"
elif [[ -n "$pid" ]]; then
    echo "  Process     : NOT running (stale PID file records $pid)"
else
    echo "  Process     : not started by these scripts"
fi

holder="$(port_holder)"
if [[ -n "$holder" ]]; then
    echo "  Port $generator_port   : held by PID $holder"
else
    echo "  Port $generator_port   : free"
fi

status="$(http_status 5)"
case "$status" in
    200)
        payload="$(health_json 5)"
        initialised="$(health_field "$payload" models_initialized)"
        if [[ "$initialised" == "True" ]]; then
            echo "  State       : READY"
            echo "  Generation model : $(health_field "$payload" loaded_model)"
            echo "  Language model   : $(health_field "$payload" loaded_lm_model)"
            echo "  LM initialised   : $(health_field "$payload" llm_initialized)"
            expected="$ACESTEP_LM_MODEL"
            actual="$(health_field "$payload" loaded_lm_model)"
            if [[ -n "$actual" && "$actual" != *"$expected"* ]]; then
                echo
                echo "  WARNING: the loaded LM is '$actual', not the M1 Safe '$expected'."
            fi
            exit 0
        fi
        echo "  State       : STARTING — the API answers but the models are still loading"
        exit 4
        ;;
    000)
        if [[ -n "$pid" ]] && is_running "$pid"; then
            echo "  State       : STARTING — the process is up, the API is not listening yet"
            exit 4
        fi
        echo "  State       : NOT RUNNING"
        echo
        echo "Start it with: ./generator/scripts/start_macos.sh"
        exit 3
        ;;
    *)
        echo "  State       : the API answered HTTP $status"
        exit 5
        ;;
esac
