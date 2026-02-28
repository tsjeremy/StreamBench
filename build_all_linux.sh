#!/bin/bash
# ============================================================
# STREAM Benchmark - Build for Linux (native + cross-compile)
# ============================================================
# This script compiles stream.c and stream_gpu.c for Linux.
# Native builds use gcc. Cross-compilation requires cross toolchains.
#
# Output files:
#   stream_cpu_linux_x64        - CPU benchmark for x86_64
#   stream_gpu_linux_x64        - GPU benchmark for x86_64
#   stream_cpu_linux_arm64      - CPU benchmark for aarch64
#   stream_gpu_linux_arm64      - GPU benchmark for aarch64
#
# Prerequisites:
#   sudo apt install build-essential libomp-dev
#   # For ARM64 cross-compile:
#   sudo apt install gcc-aarch64-linux-gnu
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

ERRORS=0

CPU_DEFS="-DTUNED -DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100"
GPU_DEFS="-DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100"

# ============================================================
#  x64 Builds (native)
# ============================================================
echo "============================================================"
echo " Building for x64 (native)"
echo "============================================================"

if gcc -O2 -fopenmp $CPU_DEFS -o stream_cpu_linux_x64 stream.c; then
    echo "[OK] stream_cpu_linux_x64"
else
    echo "[FAIL] stream_cpu_linux_x64"
    ERRORS=$((ERRORS + 1))
fi

if gcc -O2 $GPU_DEFS -o stream_gpu_linux_x64 stream_gpu.c -ldl -lm; then
    echo "[OK] stream_gpu_linux_x64"
else
    echo "[FAIL] stream_gpu_linux_x64"
    ERRORS=$((ERRORS + 1))
fi

echo ""

# ============================================================
#  ARM64 Builds (cross-compile)
# ============================================================
echo "============================================================"
echo " Building for ARM64 (cross-compile)"
echo "============================================================"

CROSS_CC="aarch64-linux-gnu-gcc"
if command -v "$CROSS_CC" &>/dev/null; then
    if $CROSS_CC -O2 -fopenmp $CPU_DEFS -o stream_cpu_linux_arm64 stream.c -static; then
        echo "[OK] stream_cpu_linux_arm64"
    else
        echo "[FAIL] stream_cpu_linux_arm64"
        ERRORS=$((ERRORS + 1))
    fi

    if $CROSS_CC -O2 $GPU_DEFS -o stream_gpu_linux_arm64 stream_gpu.c -ldl -lm -static; then
        echo "[OK] stream_gpu_linux_arm64"
    else
        echo "[FAIL] stream_gpu_linux_arm64"
        ERRORS=$((ERRORS + 1))
    fi
else
    echo "[SKIP] ARM64 cross-compiler not found."
    echo "  Install with: sudo apt install gcc-aarch64-linux-gnu"
fi

echo ""

# ============================================================
#  Summary
# ============================================================
echo "============================================================"
echo " Build Summary"
echo "============================================================"
for f in stream_cpu_linux_x64 stream_gpu_linux_x64 stream_cpu_linux_arm64 stream_gpu_linux_arm64; do
    if [ -f "$f" ]; then
        echo "  [x] $f"
    fi
done
echo ""

if [ $ERRORS -gt 0 ]; then
    echo "$ERRORS build(s) failed."
    exit 1
else
    echo "All C builds succeeded!"
fi

# ============================================================
#  .NET 10 Frontend Build
# ============================================================
echo ""
echo "============================================================"
echo " Building StreamBench (.NET 10 frontend)"
echo "============================================================"

if ! command -v dotnet &> /dev/null; then
    echo "WARNING: 'dotnet' not found. Skipping StreamBench build."
    echo "  Install .NET 10 SDK from: https://dot.net"
else
    if dotnet build "$SCRIPT_DIR/StreamBench/StreamBench.csproj" --configuration Release --nologo -v quiet; then
        echo "[OK] StreamBench (.NET)"
        echo ""
        echo "  Run:  ./run_stream.sh"
        echo "  Or:   dotnet run --project StreamBench -- --cpu --array-size 200M"
        echo "        dotnet run --project StreamBench -- --gpu --array-size 200M"
    else
        echo "[FAIL] StreamBench (.NET)"
        ERRORS=$((ERRORS + 1))
    fi
fi

echo ""
if [ $ERRORS -gt 0 ]; then
    echo "$ERRORS build(s) failed."
    exit 1
else
    echo "All builds succeeded!"
fi
