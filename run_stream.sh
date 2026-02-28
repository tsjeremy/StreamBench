#!/bin/bash
# ============================================================
# STREAM Benchmark - Setup & Run (Linux / macOS)
# ============================================================
# Runs CPU and GPU memory bandwidth benchmarks via the
# StreamBench frontend for formatted output with system
# info, colored tables, and CSV/JSON file saving.
#
# Prerequisites:
#   - Pre-built C backend executables (build_all_linux.sh or
#     build_all_macos.sh)
#   - StreamBench_${os}_${arch} self-contained binary
#     OR .NET 10 SDK/Runtime (https://dot.net) as fallback
#
# Usage:
#   chmod +x run_stream.sh
#   ./run_stream.sh
# ============================================================

set -euo pipefail

# --- Colors ---
C_RESET="\033[0m"
C_DIM="\033[2m"
C_RED="\033[91m"
C_GREEN="\033[32m"
C_YELLOW="\033[33m"
C_CYAN="\033[36m"
C_BCYAN="\033[1;96m"
C_BWHITE="\033[1;97m"
C_BGREEN="\033[1;92m"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo ""
echo -e " ${C_DIM}========================================${C_RESET}"
echo -e " ${C_BCYAN} STREAM Memory Bandwidth Benchmark${C_RESET}"
echo -e " ${C_DIM}========================================${C_RESET}"
echo ""

# --- Detect OS and architecture ---
OS_NAME="$(uname -s)"
ARCH_NAME="$(uname -m)"

case "$OS_NAME" in
    Linux)  OS_TAG="linux" ;;
    Darwin) OS_TAG="macos" ;;
    *)
        echo -e " ${C_RED}[ERROR] Unsupported OS: $OS_NAME${C_RESET}"
        exit 1
        ;;
esac

case "$ARCH_NAME" in
    x86_64)  ARCH_TAG="x64"   ;;
    aarch64) ARCH_TAG="arm64" ;;
    arm64)   ARCH_TAG="arm64" ;;
    *)
        echo -e " ${C_RED}[ERROR] Unsupported architecture: $ARCH_NAME${C_RESET}"
        exit 1
        ;;
esac

echo -e " ${C_CYAN}OS:${C_RESET}           ${C_BWHITE}${OS_NAME}${C_RESET}"
echo -e " ${C_CYAN}Architecture:${C_RESET}  ${C_BWHITE}${ARCH_NAME} [${ARCH_TAG}]${C_RESET}"

# --- Set executable paths ---
CPU_EXE="${SCRIPT_DIR}/stream_cpu_${OS_TAG}_${ARCH_TAG}"
GPU_EXE="${SCRIPT_DIR}/stream_gpu_${OS_TAG}_${ARCH_TAG}"
BENCH_EXE="${SCRIPT_DIR}/StreamBench_${OS_TAG}_${ARCH_TAG}"
STREAMBENCH="${SCRIPT_DIR}/StreamBench/StreamBench.csproj"

HAS_CPU=0
HAS_GPU=0
[ -f "$CPU_EXE" ] && HAS_CPU=1
[ -f "$GPU_EXE" ] && HAS_GPU=1

if [ $HAS_CPU -eq 0 ] && [ $HAS_GPU -eq 0 ]; then
    echo ""
    echo -e " ${C_RED}[ERROR] No benchmark executables found for ${OS_TAG}_${ARCH_TAG}.${C_RESET}"
    echo "         Expected files in: $SCRIPT_DIR"
    echo "           - stream_cpu_${OS_TAG}_${ARCH_TAG}"
    echo "           - stream_gpu_${OS_TAG}_${ARCH_TAG}"
    echo ""
    echo "         Run build_all_${OS_TAG}.sh to compile them."
    exit 1
fi

# --- Determine StreamBench runner ---
# Prefer self-contained binary; fall back to dotnet run
if [ -f "$BENCH_EXE" ] && [ -x "$BENCH_EXE" ]; then
    BENCH_CMD="$BENCH_EXE"
    echo -e " ${C_CYAN}StreamBench:${C_RESET}   ${C_GREEN}[OK] StreamBench_${OS_TAG}_${ARCH_TAG} (standalone)${C_RESET}"
elif command -v dotnet &>/dev/null && [ -f "$STREAMBENCH" ]; then
    BENCH_CMD="dotnet run --project $STREAMBENCH --"
    echo -e " ${C_CYAN}StreamBench:${C_RESET}   ${C_GREEN}[OK] dotnet run (fallback)${C_RESET}"
else
    echo ""
    echo -e " ${C_RED}[ERROR] StreamBench frontend not found.${C_RESET}"
    echo "         Expected: StreamBench_${OS_TAG}_${ARCH_TAG} (standalone)"
    echo "         Or: .NET 10 SDK + StreamBench/ project folder"
    echo ""
    echo -e "         Install .NET from: ${C_CYAN}https://dot.net${C_RESET}"
    exit 1
fi
echo ""

# ============================================================
#  Run CPU Benchmark via StreamBench (.NET)
# ============================================================
if [ $HAS_CPU -eq 1 ]; then
    $BENCH_CMD --cpu --exe "$CPU_EXE" --array-size 200000000
    echo ""
else
    echo -e " ${C_YELLOW}[SKIP] CPU executable not found: stream_cpu_${OS_TAG}_${ARCH_TAG}${C_RESET}"
    echo ""
fi

# ============================================================
#  Run GPU Benchmark via StreamBench (.NET)
# ============================================================
if [ $HAS_GPU -eq 1 ]; then
    $BENCH_CMD --gpu --exe "$GPU_EXE" --array-size 200000000
    echo ""
else
    echo -e " ${C_YELLOW}[SKIP] GPU executable not found: stream_gpu_${OS_TAG}_${ARCH_TAG}${C_RESET}"
    echo ""
fi

echo -e " ${C_DIM}========================================${C_RESET}"
echo -e " ${C_BGREEN} Benchmark Complete${C_RESET}"
echo -e " ${C_DIM}========================================${C_RESET}"
echo ""
