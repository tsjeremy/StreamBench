#!/bin/bash
# ============================================================
# STREAM Benchmark — macOS / Linux AI launcher
# ============================================================
# Equivalent of run_stream_ai.cmd for macOS and Linux.
# Preselects AI mode, then delegates to the PowerShell launcher
# or falls back to running the AI-enabled binary directly.
# ============================================================

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# ── Try PowerShell launcher (full-featured, AI mode) ─────────
if [ -f "$SCRIPT_DIR/run_stream_ai.ps1" ]; then
    PWSH=""
    if command -v pwsh >/dev/null 2>&1; then
        PWSH="pwsh"
    elif [ -x "/opt/homebrew/bin/pwsh" ]; then
        PWSH="/opt/homebrew/bin/pwsh"
    elif [ -x "/usr/local/bin/pwsh" ]; then
        PWSH="/usr/local/bin/pwsh"
    fi

    if [ -n "$PWSH" ]; then
        exec "$PWSH" -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/run_stream_ai.ps1"
    fi

    echo ""
    echo "  [!] PowerShell (pwsh) not found — falling back to direct binary launch."
    echo "      Install PowerShell for the full launcher experience:"
    echo "        brew install powershell"
    echo ""
fi

# ── Fallback: run StreamBench binary directly with --ai ──────
ARCH="$(uname -m)"
OS="$(uname -s)"

if [ "$OS" = "Darwin" ]; then
    OS_TAG="osx"
    if [ "$ARCH" = "arm64" ]; then
        ARCH_TAG="arm64"
    else
        ARCH_TAG="x64"
    fi
else
    OS_TAG="linux"
    if [ "$ARCH" = "aarch64" ]; then
        ARCH_TAG="arm64"
    else
        ARCH_TAG="x64"
    fi
fi

BIN="StreamBench_${OS_TAG}-${ARCH_TAG}"

if [ -x "$SCRIPT_DIR/$BIN" ]; then
    exec "$SCRIPT_DIR/$BIN" --ai
fi

# Try removing quarantine (macOS blocks unsigned downloads)
if [ "$OS" = "Darwin" ] && [ -f "$SCRIPT_DIR/$BIN" ]; then
    echo "  [!] Removing macOS quarantine flag from $BIN..."
    xattr -d com.apple.quarantine "$SCRIPT_DIR/$BIN" 2>/dev/null || true
    chmod +x "$SCRIPT_DIR/$BIN"
    exec "$SCRIPT_DIR/$BIN" --ai
fi

echo ""
echo "  [ERROR] Could not find $BIN in $SCRIPT_DIR"
echo ""
echo "  Options:"
echo "    1. Download the pre-built binary from:"
echo "       https://github.com/tsjeremy/StreamBench/releases/latest"
echo "    2. Install PowerShell and use the full launcher:"
echo "       brew install powershell && pwsh ./run_stream_ai.ps1"
echo "    3. Build from source (see BUILDING.md)"
echo ""
exit 1
