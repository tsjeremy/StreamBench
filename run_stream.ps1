#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Setup & Run (Cross-platform)
# ============================================================
# Runs CPU and GPU memory bandwidth benchmarks via the
# StreamBench frontend for formatted output with system info,
# colored tables, and CSV/JSON file saving.
#
# Works on Windows, macOS, and Linux.
#
# Prerequisites:
#   - StreamBench self-contained binary (same folder as this script)
#
# Usage:
#   pwsh ./run_stream.ps1          (or .\run_stream.ps1 on Windows)
#
# Windows note: If you downloaded this file from the internet,
# Windows may block it. Fix with ONE of these:
#
#   Option 1 - Unblock the file first (recommended):
#     Unblock-File .\run_stream.ps1
#     .\run_stream.ps1
#
#   Option 2 - Run with bypass flag:
#     pwsh -ExecutionPolicy Bypass -File .\run_stream.ps1
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# ------------------------------------------------------------------
#  Detect OS and architecture
# ------------------------------------------------------------------
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

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM Memory Bandwidth Benchmark' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

# ------------------------------------------------------------------
#  Resolve executable paths
# ------------------------------------------------------------------
# StreamBench self-contained binary (has CPU+GPU backends embedded):
#   Windows:  StreamBench_win-x64.exe    / StreamBench_win-arm64.exe
#   macOS:    StreamBench_osx-arm64      / StreamBench_osx-x64
#   Linux:    StreamBench_linux-arm64    / StreamBench_linux-x64
if ($osTag -eq 'win') {
    $benchName = "StreamBench_win-${archTag}${ext}"
} elseif ($osTag -eq 'macos') {
    $benchName = "StreamBench_osx-${archTag}${ext}"
} else {
    $benchName = "StreamBench_linux-${archTag}${ext}"
}

$benchExe = Join-Path $ScriptDir $benchName

# Also check for standalone C backend executables (build-from-source scenario)
$cpuExe = Join-Path $ScriptDir "stream_cpu_${osTag}_${archTag}${ext}"
$gpuExe = Join-Path $ScriptDir "stream_gpu_${osTag}_${archTag}${ext}"

$hasBench = Test-Path $benchExe
$hasCpu   = Test-Path $cpuExe
$hasGpu   = Test-Path $gpuExe

# ------------------------------------------------------------------
#  Find StreamBench runner
# ------------------------------------------------------------------
$benchCmd = $null
$useSelfContained = $false

if ($hasBench) {
    # Preferred: self-contained binary with embedded backends
    $benchCmd = $benchExe
    $useSelfContained = $true
    Write-Host "  [OK] Found $benchName" -ForegroundColor Green
} elseif ($hasCpu -or $hasGpu) {
    # Fallback: StreamBench frontend + separate C backend executables
    # (build-from-source / dev scenario)
    $csproj = Join-Path $ScriptDir 'StreamBench/StreamBench.csproj'
    if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
        $benchCmd = '__dotnet__'
        Write-Host '  [OK] Using dotnet run (dev mode)' -ForegroundColor Green
    } else {
        Write-Host ''
        Write-Host "  [ERROR] StreamBench frontend not found: $benchName" -ForegroundColor Red
        Write-Host '          Or: .NET 10 SDK + StreamBench/ project folder' -ForegroundColor Red
        Write-Host '          Install .NET from: https://dot.net'
        exit 1
    }
} else {
    Write-Host ''
    Write-Host "  [ERROR] StreamBench binary not found: $benchName" -ForegroundColor Red
    Write-Host "          Expected in: $ScriptDir" -ForegroundColor Red
    Write-Host ''
    Write-Host '  Download it from:' -ForegroundColor Yellow
    Write-Host '    https://github.com/tsjeremy/StreamBench/releases/tag/v5.10.07'
    Write-Host ''
    Write-Host "  Place $benchName in the same folder as this script and re-run."
    exit 1
}

# ------------------------------------------------------------------
#  Windows: check for vcomp140.dll (OpenMP runtime)
# ------------------------------------------------------------------
if ($IsWindows) {
    $dllOk = (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -or
             [bool](Get-Command vcomp140.dll -ErrorAction SilentlyContinue)

    if (-not $dllOk) {
        Write-Host ''
        Write-Host '  [!] MISSING: vcomp140.dll' -ForegroundColor Yellow
        Write-Host '  The CPU benchmark requires the Visual C++ Redistributable.'
        Write-Host ''

        if (Get-Command winget -ErrorAction SilentlyContinue) {
            $choice = Read-Host '  Install VC++ Redistributable now? [Y/n]'
            if ($choice -ne 'n') {
                winget install "Microsoft.VCRedist.2015+.$archTag" --accept-package-agreements --accept-source-agreements
                if ($LASTEXITCODE -eq 0) {
                    Write-Host '  [OK] Installation succeeded!' -ForegroundColor Green
                    $dllOk = $true
                } else {
                    Write-Host '  [!] Installation may have failed. Download manually:' -ForegroundColor Red
                    Write-Host "       https://aka.ms/vs/17/release/vc_redist.$archTag.exe"
                }
            } else {
                Write-Host '  Skipped. CPU benchmark will not run without vcomp140.dll.' -ForegroundColor Yellow
            }
        } else {
            Write-Host "  Download manually: https://aka.ms/vs/17/release/vc_redist.$archTag.exe"
        }
        Write-Host ''
    }
}

Write-Host ''

# ------------------------------------------------------------------
#  Run benchmarks
# ------------------------------------------------------------------
if ($useSelfContained) {
    # Self-contained binary handles both CPU + GPU automatically
    Write-Host ''
    & $benchCmd --array-size 200000000

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [FAIL] Benchmark exited with error code $LASTEXITCODE." -ForegroundColor Red
    }
} else {
    # Dev mode: run separate backends via dotnet run
    function Invoke-Bench {
        param([string]$Mode, [string]$Exe)
        $csproj = Join-Path $ScriptDir 'StreamBench/StreamBench.csproj'
        dotnet run --project "$csproj" -- "--$Mode" --exe "$Exe" --array-size 200000000
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] $Mode benchmark exited with error." -ForegroundColor Red
        }
        Write-Host ''
    }

    if ($hasCpu) {
        $skipCpu = $IsWindows -and -not $dllOk
        if ($skipCpu) {
            Write-Host '  [SKIP] CPU benchmark requires vcomp140.dll.' -ForegroundColor Yellow
        } else {
            Invoke-Bench -Mode 'cpu' -Exe $cpuExe
        }
    }

    if ($hasGpu) {
        Invoke-Bench -Mode 'gpu' -Exe $gpuExe
    }
}

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   Benchmark Complete' -ForegroundColor Green
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''
