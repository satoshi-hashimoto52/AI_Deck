#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
runtime_root="$repo_root/.aideck-generator"
ace_root="$runtime_root/ACE-Step-1.5"

if [[ ! -d "$ace_root/.venv" ]]; then
    echo "ERROR: Generator is not installed. Run ./generator/scripts/setup_macos.sh first."
    exit 1
fi

mkdir -p "$runtime_root/models" "$runtime_root/cache"
export ACESTEP_LM_BACKEND="mlx"
export ACESTEP_CONFIG_PATH="acestep-v15-turbo"
export ACESTEP_LM_MODEL_PATH="acestep-5Hz-lm-0.6B"
export ACESTEP_CHECKPOINTS_DIR="$runtime_root/models"
export ACESTEP_INIT_LLM="true"
export TOKENIZERS_PARALLELISM="false"
export HF_HOME="$runtime_root/cache/huggingface"

echo "Starting ACE-Step on http://127.0.0.1:8001"
echo "The first start downloads about 10 GB of model data."
echo "Keep this Terminal window open during generation."
cd "$ace_root"
exec uv run acestep-api \
    --host 127.0.0.1 \
    --port 8001 \
    --download-source huggingface \
    --init-llm \
    --lm-model-path acestep-5Hz-lm-0.6B
