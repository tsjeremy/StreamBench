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

# ------------------------------------------------------------------
#  Windows: auto-check prerequisites (silent — runs setup.ps1 if needed)
# ------------------------------------------------------------------
if ($IsWindows) {
    $setupNeeded = $false

    # 1. VC++ Redistributable (vcomp140.dll)
    if (-not (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -and
        -not (Test-Path "$env:SystemRoot\SysWOW64\vcomp140.dll")) {
        $setupNeeded = $true
    }

    # 2. .NET 10 Runtime (standalone) or .NET 10 SDK (source)
    $csprojCheck = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    if (Test-Path $csprojCheck) {
        # Source mode — need SDK
        $sdks = $null
        if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdks = & dotnet --list-sdks 2>$null }
        if (-not ($sdks -match '^10\.')) { $setupNeeded = $true }
    } else {
        # Standalone mode — need Runtime
        $runtimes = $null
        if (Get-Command dotnet -ErrorAction SilentlyContinue) { $runtimes = & dotnet --list-runtimes 2>$null }
        if (-not ($runtimes -match 'Microsoft\.NETCore\.App 10\.')) { $setupNeeded = $true }
    }

    if ($setupNeeded) {
        $setupScript = Join-Path $ScriptDir 'setup.ps1'
        if (Test-Path $setupScript) {
            Write-Host '  [!] Missing prerequisites detected — running setup.ps1...' -ForegroundColor Yellow
            Write-Host ''
            & $setupScript
            if ($LASTEXITCODE -ne 0) {
                Write-Host '  [FAIL] setup.ps1 finished with errors. Please resolve and re-run.' -ForegroundColor Red
                exit 1
            }
            # Refresh PATH after setup
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
            Write-Host ''
        } else {
            Write-Host '  [!] Prerequisites missing and setup.ps1 not found.' -ForegroundColor Yellow
            Write-Host '      Run setup.ps1 first, or install manually:' -ForegroundColor Yellow
            Write-Host '        winget install "Microsoft.VCRedist.2015+.$archTag"' -ForegroundColor Yellow
            Write-Host '        winget install Microsoft.DotNet.Runtime.10' -ForegroundColor Yellow
            exit 1
        }
    }
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
$arraySize = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_ARRAY_SIZE)) { '200000000' } else { $env:STREAMBENCH_ARRAY_SIZE.Trim() }
if ($arraySize -notmatch '^\d+$') {
    Write-Host ''
    Write-Host "  [ERROR] STREAMBENCH_ARRAY_SIZE must be a positive integer, got: $arraySize" -ForegroundColor Red
    exit 1
}

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

Write-Host ''

# ------------------------------------------------------------------
#  Run benchmarks
# ------------------------------------------------------------------
if ($useSelfContained) {
    # Self-contained binary handles both CPU + GPU automatically
    Write-Host ''
    & $benchCmd --array-size $arraySize

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [FAIL] Benchmark exited with error code $LASTEXITCODE." -ForegroundColor Red
    }
} else {
    # Dev mode: run separate backends via dotnet run
    function Invoke-Bench {
        param([string]$Mode, [string]$Exe)
        $csproj = Join-Path $ScriptDir 'StreamBench/StreamBench.csproj'
        dotnet run --project "$csproj" -- "--$Mode" --exe "$Exe" --array-size "$arraySize"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] $Mode benchmark exited with error." -ForegroundColor Red
        }
        Write-Host ''
    }

    if ($hasCpu) {
        Invoke-Bench -Mode 'cpu' -Exe $cpuExe
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
