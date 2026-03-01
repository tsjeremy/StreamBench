#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Memory + AI Run (Cross-platform)
# ============================================================
# Runs CPU + GPU memory bandwidth benchmarks, then AI benchmark
# in one command via the StreamBench frontend.
#
# Default launcher (run_stream.ps1) is memory-only.
# This script is the explicit AI-enabled launcher.
#
# Usage:
#   pwsh ./run_stream_ai.ps1
#   .\run_stream_ai.ps1   (Windows)
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

if ($IsWindows) {
    $osTag   = 'win'
    $archTag = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $ext     = '.exe'
} elseif ($IsMacOS) {
    $osTag   = 'macos'
    $archTag = if ((uname -m) -eq 'arm64') { 'arm64' } else { 'x64' }
    $ext     = ''
} else {
    $osTag   = 'linux'
    $archTag = if ((uname -m) -eq 'aarch64') { 'arm64' } else { 'x64' }
    $ext     = ''
}

if ($osTag -eq 'win') {
    $benchName = "StreamBench_win-${archTag}${ext}"
} elseif ($osTag -eq 'macos') {
    $benchName = "StreamBench_osx-${archTag}${ext}"
} else {
    $benchName = "StreamBench_linux-${archTag}${ext}"
}

$benchExe = Join-Path $ScriptDir $benchName
$csproj = Join-Path $ScriptDir 'StreamBench/StreamBench.csproj'

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM + AI Benchmark Launcher' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

if (Test-Path $benchExe) {
    Write-Host "  [OK] Found $benchName" -ForegroundColor Green
    & $benchExe --cpu --gpu --ai --array-size 200000000
    exit $LASTEXITCODE
}

if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
    Write-Host '  [OK] Using dotnet run (dev mode)' -ForegroundColor Green
    dotnet run --project "$csproj" -- --cpu --gpu --ai --array-size 200000000
    exit $LASTEXITCODE
}

Write-Host "  [ERROR] StreamBench binary or dotnet project not found." -ForegroundColor Red
Write-Host "          Expected binary: $benchName in $ScriptDir" -ForegroundColor Red
Write-Host "          Or project file: StreamBench/StreamBench.csproj" -ForegroundColor Red
exit 1
