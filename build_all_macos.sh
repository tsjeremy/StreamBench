#!/bin/bash
# ============================================================
# STREAM Benchmark - Build for macOS (x64 and ARM64)
# ============================================================
# This script compiles stream.c and stream_gpu.c for macOS.
# It detects the current architecture and can build universal
# binaries (both x64 and ARM64 in one file).
#
# Output files:
#   stream_cpu_macos_x64        - CPU benchmark for Intel Mac
#   stream_cpu_macos_arm64      - CPU benchmark for Apple Silicon
#   stream_gpu_macos_x64        - GPU benchmark for Intel Mac
#   stream_gpu_macos_arm64      - GPU benchmark for Apple Silicon
#
# Prerequisites:
#   xcode-select --install
#   brew install libomp
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

ERRORS=0

# --- Detect libomp ---
OMP_PREFIX=""
if [ -d "/opt/homebrew/opt/libomp" ]; then
    OMP_PREFIX="/opt/homebrew/opt/libomp"  # Apple Silicon Homebrew
elif [ -d "/usr/local/opt/libomp" ]; then
    OMP_PREFIX="/usr/local/opt/libomp"     # Intel Homebrew
fi

if [ -z "$OMP_PREFIX" ]; then
    echo "WARNING: libomp not found. CPU builds will be single-threaded."
    echo "  Install with: brew install libomp"
    CPU_FLAGS="-O2"
else
    echo "Found libomp at: $OMP_PREFIX"
    CPU_FLAGS="-O2 -Xpreprocessor -fopenmp -I${OMP_PREFIX}/include -L${OMP_PREFIX}/lib -lomp"
fi

CPU_DEFS="-DTUNED -DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100"
GPU_DEFS="-DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100"

# ============================================================
#  x64 (Intel) Builds
# ============================================================
echo "============================================================"
echo " Building for x64 (Intel)"
echo "============================================================"

if clang -arch x86_64 $CPU_FLAGS $CPU_DEFS -o stream_cpu_macos_x64 stream.c 2>/dev/null; then
    echo "[OK] stream_cpu_macos_x64"
else
    echo "[FAIL] stream_cpu_macos_x64"
    ERRORS=$((ERRORS + 1))
fi

if clang -arch x86_64 -O2 $GPU_DEFS -o stream_gpu_macos_x64 stream_gpu.c -lm 2>/dev/null; then
    echo "[OK] stream_gpu_macos_x64"
else
    echo "[FAIL] stream_gpu_macos_x64"
    ERRORS=$((ERRORS + 1))
fi

echo ""

# ============================================================
#  ARM64 (Apple Silicon) Builds
# ============================================================
echo "============================================================"
echo " Building for ARM64 (Apple Silicon)"
echo "============================================================"

if clang -arch arm64 $CPU_FLAGS $CPU_DEFS -o stream_cpu_macos_arm64 stream.c 2>/dev/null; then
    echo "[OK] stream_cpu_macos_arm64"
else
    echo "[FAIL] stream_cpu_macos_arm64"
    ERRORS=$((ERRORS + 1))
fi

if clang -arch arm64 -O2 $GPU_DEFS -o stream_gpu_macos_arm64 stream_gpu.c -lm 2>/dev/null; then
    echo "[OK] stream_gpu_macos_arm64"
else
    echo "[FAIL] stream_gpu_macos_arm64"
    ERRORS=$((ERRORS + 1))
fi

echo ""

# ============================================================
#  Summary
# ============================================================
echo "============================================================"
echo " Build Summary"
echo "============================================================"
for f in stream_cpu_macos_x64 stream_gpu_macos_x64 stream_cpu_macos_arm64 stream_gpu_macos_arm64; do
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
        echo "  Run: dotnet run --project StreamBench -- --cpu --array-size 200M"
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
