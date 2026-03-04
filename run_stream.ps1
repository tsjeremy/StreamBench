#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Setup & Run (Cross-platform)
# ============================================================
# Runs CPU and GPU memory bandwidth benchmarks (default mode) via the
# StreamBench frontend for formatted output with system info,
# colored tables, and CSV/JSON file saving.
#
# AI benchmark is intentionally NOT included in this default launcher.
# Use --ai on StreamBench directly, or run run_stream_ai.ps1.
#
# Works on Windows, macOS, and Linux.
#
# Prerequisites:
#   - StreamBench self-contained binary (same folder as this script)
#
# Usage:
#   pwsh ./run_stream.ps1          (or .\run_stream.ps1 on Windows)
#   powershell -ExecutionPolicy Bypass -File .\run_stream.ps1
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
#  PowerShell 5.1 compatibility: define $IsWindows if not present
# ------------------------------------------------------------------
if ($null -eq (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue)) {
    # PowerShell 5.1 only runs on Windows
    $IsWindows = $true
    $IsMacOS   = $false
    $IsLinux   = $false
}

# ------------------------------------------------------------------
#  Detect OS and architecture
# ------------------------------------------------------------------
if ($IsWindows) {
    $osTag   = 'win'
    # Use .NET RuntimeInformation for reliable ARM64 detection
    # ($env:PROCESSOR_ARCHITECTURE reports "AMD64" on ARM64 Windows
    #  when running under x64 emulation in PowerShell 5.1)
    try {
        $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    } catch {
        # Fallback for PowerShell 5.1 where RuntimeInformation is unavailable
        $nativeArch = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE -ErrorAction SilentlyContinue).PROCESSOR_ARCHITECTURE
        if (-not $nativeArch) { $nativeArch = $env:PROCESSOR_ARCHITECTURE }
        $osArch = switch ($nativeArch) {
            'ARM64' { 'Arm64' }
            'AMD64' { 'X64' }
            default { $nativeArch }
        }
    }
    $archTag = if ($osArch -eq 'Arm64') { 'arm64' } else { 'x64' }
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
#   Windows:  StreamBench_win_x64.exe    / StreamBench_win_arm64.exe
#   macOS:    StreamBench_osx-arm64      / StreamBench_osx-x64
#   Linux:    StreamBench_linux-arm64    / StreamBench_linux-x64
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

# Also check for standalone C backend executables (build-from-source scenario)
$cpuExe = Join-Path $ScriptDir "stream_cpu_${osTag}_${archTag}${ext}"
$gpuExe = Join-Path $ScriptDir "stream_gpu_${osTag}_${archTag}${ext}"

$hasBench = $null -ne $benchExe
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
        Write-Host "  [ERROR] StreamBench frontend not found: $($benchNames[0])" -ForegroundColor Red
        Write-Host '          Or: .NET 10 SDK + StreamBench/ project folder' -ForegroundColor Red
        Write-Host '          Install .NET from: https://dot.net'
        exit 1
    }
} else {
    Write-Host ''
    Write-Host "  [ERROR] StreamBench binary not found: $($benchNames[0])" -ForegroundColor Red
    Write-Host "          Expected in: $ScriptDir" -ForegroundColor Red
    Write-Host ''
    Write-Host '  Download it from:' -ForegroundColor Yellow
    Write-Host '    https://github.com/tsjeremy/StreamBench/releases/latest'
    Write-Host ''
    Write-Host "  Place $($benchNames[0]) in the same folder as this script and re-run."
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
