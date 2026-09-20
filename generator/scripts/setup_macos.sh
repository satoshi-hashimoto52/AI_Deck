#!/usr/bin/env bash
# Installs the pinned ACE-Step runtime. Safe to run again at any time.
#
# Re-running must be boring: it re-uses the clone, re-uses the virtual environment, and never
# re-downloads a model. Nothing here deletes or moves anything the user already has — a
# mismatched checkout is reported, not replaced.

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    echo "ERROR: The local generator requires an Apple Silicon Mac."
    exit 1
fi

for tool in git uv curl python3; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        case "$tool" in
            git)  echo "ERROR: git was not found. Run: xcode-select --install" ;;
            uv)   echo "ERROR: uv was not found. Install it with: brew install uv" ;;
            *)    echo "ERROR: $tool was not found." ;;
        esac
        exit 1
    fi
done

mkdir -p "$runtime_root" "$run_dir" "$log_dir" "$runtime_root/cache"

# ---------------------------------------------------------------- source

if [[ ! -d "$ace_root/.git" ]]; then
    echo "Downloading the pinned ACE-Step source ($ace_version)..."
    git clone --depth 1 --branch "$ace_version" \
        https://github.com/ACE-Step/ACE-Step-1.5.git "$ace_root"
else
    installed_version="$(git -C "$ace_root" describe --tags --exact-match 2>/dev/null || true)"
    if [[ "$installed_version" == "$ace_version" ]]; then
        echo "ACE-Step $ace_version is already present; leaving it alone."
    else
        echo "ERROR: The existing runtime is '${installed_version:-an untagged checkout}',"
        echo "       but this project pins '$ace_version'."
        echo
        echo "Nothing has been changed. Move it aside yourself and run this again:"
        echo "    mv '$ace_root' '$ace_root.previous'"
        exit 1
    fi
fi

# ---------------------------------------------------------------- environment

# `uv sync` is itself idempotent and resolves from the lock file, so a second run is a no-op
# that costs a second. It is not skipped on the strength of .venv existing, because a
# half-finished first run leaves that directory behind too.
echo "Checking the isolated Python environment..."
(cd "$ace_root" && uv sync --frozen)

# ---------------------------------------------------------------- report

echo
echo "Setup complete."
echo "  Runtime      : $ace_root"
echo "  Checkpoints  : $checkpoints_dir"
echo "  Logs         : $log_dir"

if [[ -d "$checkpoints_dir" ]]; then
    echo
    echo "Models already on disk:"
    for model in "$checkpoints_dir"/*/; do
        [[ -d "$model" ]] || continue
        name="$(basename "$model")"
        [[ "$name" == .* ]] && continue
        echo "    $(du -sh "$model" 2>/dev/null | cut -f1)  $name"
    done
    echo "  (nothing above is re-downloaded)"
else
    echo
    echo "No models yet. The first start downloads about 10 GB."
fi

cat <<EOF

Next command:
  ./generator/scripts/start_macos.sh
EOF
