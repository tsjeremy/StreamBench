#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Build for Linux (x64 and ARM64)
# ============================================================
# Compiles stream.c and stream_gpu.c for Linux.
# Native builds use gcc. ARM64 cross-compilation requires the
# aarch64 cross toolchain.
#
# Output files:
#   stream_cpu_linux_x64        stream_gpu_linux_x64
#   stream_cpu_linux_arm64      stream_gpu_linux_arm64
#   StreamBench_linux-x64       StreamBench_linux-arm64
#
# Prerequisites:
#   sudo apt install build-essential libomp-dev
#   # For ARM64 cross-compile:
#   sudo apt install gcc-aarch64-linux-gnu
#
# Run:  pwsh ./build_all_linux.ps1
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $ScriptDir

# Read version from the single source of truth
$VersionFile = Join-Path $ScriptDir 'VERSION'
if (Test-Path $VersionFile) {
    $Version = (Get-Content $VersionFile -Raw).Trim()
    Write-Host "Version: $Version (from VERSION file)"
} else {
    $Version = '0.0.0-dev'
    Write-Host "WARNING: VERSION file not found, using $Version" -ForegroundColor Yellow
}

$Errors = 0

$CpuDefs = '-DTUNED -DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100'
$GpuDefs = '-DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100'

# ------------------------------------------------------------------
#  Build matrix
# ------------------------------------------------------------------
$CrossCC = 'aarch64-linux-gnu-gcc'
$hasCross = [bool](Get-Command $CrossCC -ErrorAction SilentlyContinue)

$builds = @(
    @{ CC='gcc';     Tag='x64';   Type='cpu'; Src='stream.c';     Flags="-O2 -fopenmp $CpuDefs";       Libs=''         },
    @{ CC='gcc';     Tag='x64';   Type='gpu'; Src='stream_gpu.c'; Flags="-O2 $GpuDefs";                Libs='-ldl -lm' },
    @{ CC=$CrossCC;  Tag='arm64'; Type='cpu'; Src='stream.c';     Flags="-O2 -fopenmp $CpuDefs -static"; Libs=''       },
    @{ CC=$CrossCC;  Tag='arm64'; Type='gpu'; Src='stream_gpu.c'; Flags="-O2 $GpuDefs -static";        Libs='-ldl -lm' }
)

$currentTag = ''
foreach ($b in $builds) {
    if ($b.Tag -ne $currentTag) {
        if ($currentTag) { Write-Host '' }
        $label = if ($b.Tag -eq 'x64') { 'x64 (native)' } else { 'ARM64 (cross-compile)' }
        Write-Host '============================================================'
        Write-Host " Building for $label"
        Write-Host '============================================================'
        $currentTag = $b.Tag
    }

    # Skip ARM64 if cross-compiler not available
    if ($b.Tag -eq 'arm64' -and -not $hasCross) {
        Write-Host "[SKIP] ARM64 cross-compiler not found." -ForegroundColor Yellow
        Write-Host "  Install with: sudo apt install gcc-aarch64-linux-gnu"
        break
    }

    $out = "stream_$($b.Type)_linux_$($b.Tag)"
    $cmd = "$($b.CC) $($b.Flags) -o $out $($b.Src) $($b.Libs)"
    bash -c $cmd 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] $out" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $out" -ForegroundColor Red
        $Errors++
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  Summary
# ------------------------------------------------------------------
Write-Host '============================================================'
Write-Host ' Build Summary'
Write-Host '============================================================'
foreach ($b in $builds) {
    $f = "stream_$($b.Type)_linux_$($b.Tag)"
    if (Test-Path (Join-Path $ScriptDir $f)) { Write-Host "  [x] $f" }
}
Write-Host ''

if ($Errors -gt 0) {
    Write-Host "$Errors build(s) failed." -ForegroundColor Red
    Pop-Location; exit 1
}
Write-Host 'All C builds succeeded!'

# ------------------------------------------------------------------
#  .NET 10 Frontend Build
# ------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host ' Building StreamBench (.NET 10 frontend)'
Write-Host '============================================================'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "WARNING: 'dotnet' not found. Skipping StreamBench build." -ForegroundColor Yellow
    Write-Host '  Install .NET 10 SDK from: https://dot.net'
    Pop-Location; exit 0
}

dotnet build "$ScriptDir/StreamBench/StreamBench.csproj" --configuration Release -p:Version=$Version --nologo -v quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host '[OK] StreamBench (.NET)' -ForegroundColor Green
} else {
    Write-Host '[FAIL] StreamBench (.NET)' -ForegroundColor Red
    $Errors++; Pop-Location; exit 1
}

# ------------------------------------------------------------------
#  Publish self-contained with embedded backends
# ------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host ' Publishing StreamBench (self-contained with embedded backends)'
Write-Host '============================================================'

$nativeArch = uname -m
$nativeTag = if ($nativeArch -eq 'aarch64') { 'arm64' } else { 'x64' }

foreach ($tag in @('x64','arm64')) {
    $rid = "linux-$tag"
    $backendDir = Join-Path $ScriptDir 'StreamBench/backends'
    if (Test-Path $backendDir) { Remove-Item $backendDir -Recurse -Force }
    New-Item -ItemType Directory -Path $backendDir -Force | Out-Null

    foreach ($type in @('cpu','gpu')) {
        $src = Join-Path $ScriptDir "stream_${type}_linux_${tag}"
        if (Test-Path $src) { Copy-Item $src $backendDir }
    }

    dotnet publish "$ScriptDir/StreamBench/StreamBench.csproj" `
        -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:Version=$Version --nologo -v quiet `
        -o "$ScriptDir/publish/$rid"

    if ($LASTEXITCODE -eq 0) {
        $outBin = "StreamBench_linux-$tag"
        Copy-Item "$ScriptDir/publish/$rid/StreamBench" "$ScriptDir/$outBin"
        chmod +x "$ScriptDir/$outBin"
        Write-Host "[OK] $outBin (with embedded CPU + GPU backends)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] StreamBench_linux-$tag" -ForegroundColor Red
        $Errors++
    }

    Remove-Item $backendDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "  Run:  ./StreamBench_linux-$nativeTag --cpu"
Write-Host "        ./StreamBench_linux-$nativeTag --gpu"
Write-Host '  Or:   pwsh ./run_stream.ps1    (memory-only, auto-detect architecture)'
Write-Host '        pwsh ./run_stream_ai.ps1 (memory + AI, auto-detect architecture)'

Write-Host ''
if ($Errors -gt 0) {
    Write-Host "$Errors build(s) failed." -ForegroundColor Red
    Pop-Location; exit 1
}
Write-Host 'All builds succeeded!'
Pop-Location
