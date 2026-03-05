#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Memory + AI Run (Cross-platform)
# ============================================================
# Runs CPU + GPU memory bandwidth benchmarks, then AI benchmark
# with automatic Q3 local JSON relation summary when memory JSON exists
# in one command via the StreamBench frontend.
#
# Default launcher (run_stream.ps1) is memory-only.
# This script is the explicit AI-enabled launcher.
#
# Usage:
#   pwsh ./run_stream_ai.ps1
#   .\run_stream_ai.ps1   (Windows)
#   powershell -ExecutionPolicy Bypass -File .\run_stream_ai.ps1
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

if ($osTag -eq 'win') {
    # Prefer AI-specific exe, then standard naming variants
    $benchNames = @(
        "StreamBench_win_${archTag}_ai${ext}",
        "StreamBench_win-${archTag}${ext}",
        "StreamBench_win_${archTag}${ext}"
    )
} elseif ($osTag -eq 'macos') {
    $benchNames = @(
        "StreamBench_osx_${archTag}_ai${ext}",
        "StreamBench_osx-${archTag}${ext}",
        "StreamBench_osx_${archTag}${ext}"
    )
} else {
    $benchNames = @(
        "StreamBench_linux_${archTag}_ai${ext}",
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
$aiModel = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_MODEL)) { '' } else { $env:STREAMBENCH_AI_MODEL }
$aiDevices = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_DEVICES)) { '' } else { $env:STREAMBENCH_AI_DEVICES }
$aiNoDownload = $env:STREAMBENCH_AI_NO_DOWNLOAD -eq '1'
$arraySize = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_ARRAY_SIZE)) { '200000000' } else { $env:STREAMBENCH_ARRAY_SIZE.Trim() }
if ($arraySize -notmatch '^\d+$') {
    Write-Host "  [ERROR] STREAMBENCH_ARRAY_SIZE must be a positive integer, got: $arraySize" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM + AI Benchmark Launcher' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

Write-Host "  [OK] AI model: $(if ($aiModel) { $aiModel } else { '(auto-select)' })" -ForegroundColor Green
Write-Host "  [OK] AI device(s): $(if ($aiDevices) { $aiDevices } else { '(all detected)' })" -ForegroundColor Green
Write-Host "  [OK] Q3 local summary: auto (when memory JSON exists)" -ForegroundColor Green
Write-Host "  [OK] Array size: $arraySize" -ForegroundColor Green

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
        '--array-size', "$arraySize"
    )
    if (-not [string]::IsNullOrWhiteSpace($aiDevices)) {
        $appArgs += @('--ai-device', "$aiDevices")
    }
    if (-not [string]::IsNullOrWhiteSpace($aiModel)) {
        $appArgs += @('--ai-model', "$aiModel")
    }
    if ($aiNoDownload) {
        $appArgs += '--ai-no-download'
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

    # Pre-flight: check Foundry Local is available (CLI alias is 'foundry', not 'foundrylocal')
    # Refresh PATH in case setup.ps1 just installed it in a prior session
    if ($IsWindows) {
        $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                    [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    }
    $foundryAvailable = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                        [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
    if (-not $foundryAvailable) {
        Write-Host ''
        Write-Host '  [!] Microsoft Foundry Local is not installed.' -ForegroundColor Yellow
        Write-Host '      The AI benchmark requires Foundry Local.' -ForegroundColor Yellow
        Write-Host '      Install it with: winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
        Write-Host '      After install, restart your terminal and re-run this script.' -ForegroundColor Yellow
        Write-Host ''
        Write-Host '      Continuing with memory benchmark only...' -ForegroundColor Yellow
        Write-Host ''
    }

    $exeArgs = @('--cpu','--gpu','--ai','--array-size',"$arraySize")
    if (-not [string]::IsNullOrWhiteSpace($aiDevices)) {
        $exeArgs += @('--ai-device', "$aiDevices")
    }
    if (-not [string]::IsNullOrWhiteSpace($aiModel)) {
        $exeArgs += @('--ai-model', "$aiModel")
    }
    if ($aiNoDownload) {
        $exeArgs += '--ai-no-download'
    }
    & $benchExe @exeArgs
    exit $LASTEXITCODE
}

Write-Host "  [ERROR] StreamBench binary or dotnet project not found." -ForegroundColor Red
Write-Host "          Expected one of: $($benchNames -join ', ') in $ScriptDir" -ForegroundColor Red
Write-Host "          Or project file: StreamBench/StreamBench.csproj" -ForegroundColor Red
exit 1
