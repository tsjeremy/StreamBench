#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Memory + AI Run (Cross-platform)
# ============================================================
# Runs CPU + GPU memory bandwidth benchmarks, then AI benchmark
# and optional 3-question local JSON relation summary
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
    $benchNames = @(
        "StreamBench_win-${archTag}${ext}",
        "StreamBench_win_${archTag}${ext}"
    )
} elseif ($osTag -eq 'macos') {
    $benchNames = @(
        "StreamBench_osx-${archTag}${ext}",
        "StreamBench_osx_${archTag}${ext}"
    )
} else {
    $benchNames = @(
        "StreamBench_linux-${archTag}${ext}",
        "StreamBench_linux_${archTag}${ext}"
    )
}

$benchName = $null
$benchExe  = $null
foreach ($name in $benchNames) {
    $candidate = Join-Path $ScriptDir $name
    if (Test-Path $candidate) {
        $benchName = $name
        $benchExe  = $candidate
        break
    }
}
$csproj = Join-Path $ScriptDir 'StreamBench/StreamBench.csproj'
$aiModel = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_MODEL)) { 'phi-3.5-mini' } else { $env:STREAMBENCH_AI_MODEL }
$aiDevices = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_DEVICES)) { 'cpu' } else { $env:STREAMBENCH_AI_DEVICES }
$aiLocalSummary = if ($env:STREAMBENCH_AI_LOCAL_SUMMARY -eq '0') { $false } else { $true }

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM + AI Benchmark Launcher' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

Write-Host "  [OK] AI model: $aiModel" -ForegroundColor Green
Write-Host "  [OK] AI device(s): $aiDevices" -ForegroundColor Green
Write-Host "  [OK] AI local summary: $aiLocalSummary" -ForegroundColor Green

if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
    Write-Host '  [OK] Using dotnet build + app run (source mode)' -ForegroundColor Green
    & dotnet build "$csproj" -p:EnableAI=true --nologo -v:q
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $debugDir = Join-Path $ScriptDir 'StreamBench\bin\Debug'
    $appArgs = @(
        '--cpu',
        '--gpu',
        '--ai',
        '--ai-device', "$aiDevices",
        '--ai-model', "$aiModel",
        '--array-size', '200000000'
    )
    if ($aiLocalSummary) {
        $appArgs += '--ai-local-summary'
    }

    if ($IsWindows) {
        $appExe = Get-ChildItem -Path $debugDir -Filter 'StreamBench.exe' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($appExe) {
            & $appExe.FullName @appArgs
            exit $LASTEXITCODE
        }
    }

    $dll = Get-ChildItem -Path $debugDir -Filter 'StreamBench.dll' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $dll) {
        Write-Host "  [ERROR] Built StreamBench app not found under $debugDir" -ForegroundColor Red
        exit 1
    }

    & dotnet $dll.FullName @appArgs
    exit $LASTEXITCODE
}

if ($benchExe) {
    Write-Host "  [OK] Found $benchName" -ForegroundColor Green
    $exeArgs = @('--cpu','--gpu','--ai','--ai-device',"$aiDevices",'--ai-model',"$aiModel",'--array-size','200000000')
    if ($aiLocalSummary) {
        $exeArgs += '--ai-local-summary'
    }
    & $benchExe @exeArgs
    exit $LASTEXITCODE
}

Write-Host "  [ERROR] StreamBench binary or dotnet project not found." -ForegroundColor Red
Write-Host "          Expected one of: $($benchNames -join ', ') in $ScriptDir" -ForegroundColor Red
Write-Host "          Or project file: StreamBench/StreamBench.csproj" -ForegroundColor Red
exit 1
