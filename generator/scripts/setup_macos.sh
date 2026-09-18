#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
runtime_root="$repo_root/.aideck-generator"
ace_root="$runtime_root/ACE-Step-1.5"
ace_version="v0.1.8"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    echo "ERROR: Phase 0 requires an Apple Silicon Mac."
    exit 1
fi

if ! command -v git >/dev/null 2>&1; then
    echo "ERROR: git was not found. Run: xcode-select --install"
    exit 1
fi

if ! command -v uv >/dev/null 2>&1; then
    echo "ERROR: uv was not found. Install it with: brew install uv"
    exit 1
fi

mkdir -p "$runtime_root"
if [[ ! -d "$ace_root/.git" ]]; then
    echo "Downloading the pinned ACE-Step source ($ace_version)..."
    git clone --depth 1 --branch "$ace_version" \
        https://github.com/ACE-Step/ACE-Step-1.5.git "$ace_root"
else
    installed_version="$(git -C "$ace_root" describe --tags --exact-match 2>/dev/null || true)"
    if [[ "$installed_version" != "$ace_version" ]]; then
        echo "ERROR: Existing ACE-Step runtime is '$installed_version', expected '$ace_version'."
        echo "Move $ace_root aside and run this command again."
        exit 1
    fi
fi

echo "Installing the isolated Python environment..."
(cd "$ace_root" && uv sync --frozen)

cat <<EOF

Phase 0 setup completed.
Runtime: $ace_root
Model weights are not downloaded until the first server start.
Next command:
  ./generator/scripts/start_macos.sh
EOF
