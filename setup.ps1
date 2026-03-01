#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Windows Setup Script
# ============================================================
# Installs prerequisites and prepares the environment so that
# run_stream.ps1 and run_stream_ai.ps1 work on a fresh Windows
# machine (no Visual Studio required for running).
#
# What this script does:
#   1. Installs .NET 10 SDK (via winget) if missing
#   2. Installs Visual C++ Redistributable 2015+ (for vcomp140.dll)
#   3. Runs "dotnet restore" so the project assets file is created
#      (fixes: NETSDK1047 - assets file missing target net10.0/win-x64)
#
# Usage:
#   .\setup.ps1
#   pwsh -ExecutionPolicy Bypass -File .\setup.ps1
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$archTag = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM Benchmark - Windows Setup' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

$errors = 0

# ------------------------------------------------------------------
#  Helper: check winget availability
# ------------------------------------------------------------------
$hasWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
if (-not $hasWinget) {
    Write-Host '  [!] winget not found.' -ForegroundColor Yellow
    Write-Host '      Install App Installer from the Microsoft Store, then re-run this script.'
    Write-Host '      https://apps.microsoft.com/detail/9NBLGGH4NNS1'
    Write-Host ''
}

# ------------------------------------------------------------------
#  1. .NET 10 SDK
# ------------------------------------------------------------------
Write-Host '  [1/4] Checking .NET 10 SDK...' -ForegroundColor Cyan

$dotnetOk = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks -match '^10\.') {
        Write-Host '  [OK] .NET 10 SDK is already installed.' -ForegroundColor Green
        $dotnetOk = $true
    }
}

if (-not $dotnetOk) {
    Write-Host '  [!] .NET 10 SDK not found.' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host '  Installing .NET 10 SDK via winget...' -ForegroundColor Yellow
        winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] .NET 10 SDK installed.' -ForegroundColor Green
            # Refresh PATH for this session
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
            $dotnetOk = $true
        } else {
            Write-Host '  [FAIL] .NET 10 SDK installation failed.' -ForegroundColor Red
            Write-Host '         Download manually: https://dot.net/download'
            $errors++
        }
    } else {
        Write-Host '  Download .NET 10 SDK from: https://dot.net/download' -ForegroundColor Yellow
        $errors++
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  2. Visual C++ Redistributable (vcomp140.dll for OpenMP)
# ------------------------------------------------------------------
Write-Host '  [2/4] Checking Visual C++ Redistributable...' -ForegroundColor Cyan

$vcRedistOk = (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -or
              (Test-Path "$env:SystemRoot\SysWOW64\vcomp140.dll")

if ($vcRedistOk) {
    Write-Host '  [OK] Visual C++ Redistributable (vcomp140.dll) found.' -ForegroundColor Green
} else {
    Write-Host '  [!] vcomp140.dll not found (required by CPU benchmark).' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host "  Installing VC++ Redistributable ($archTag) via winget..." -ForegroundColor Yellow
        winget install "Microsoft.VCRedist.2015+.$archTag" --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] Visual C++ Redistributable installed.' -ForegroundColor Green
        } else {
            Write-Host '  [!] Installation may have failed. Download manually:' -ForegroundColor Yellow
            Write-Host "      https://aka.ms/vs/17/release/vc_redist.$archTag.exe"
        }
    } else {
        Write-Host "  Download manually: https://aka.ms/vs/17/release/vc_redist.$archTag.exe" -ForegroundColor Yellow
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  3. dotnet restore (creates project.assets.json for net10.0/win-x64)
# ------------------------------------------------------------------
Write-Host '  [3/4] Running dotnet restore...' -ForegroundColor Cyan

$csproj = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'
if (-not (Test-Path $csproj)) {
    Write-Host "  [FAIL] Project file not found: $csproj" -ForegroundColor Red
    $errors++
} elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '  [SKIP] dotnet not available — install .NET 10 SDK first.' -ForegroundColor Yellow
} else {
    # Restore base (no RID) so Debug builds work
    dotnet restore "$csproj" --nologo
    $restoreBase = $LASTEXITCODE

    # Restore with win-x64 RID so publish/release builds work
    dotnet restore "$csproj" -r win-x64 --nologo
    $restoreX64 = $LASTEXITCODE

    # Restore with win-arm64 RID for ARM64 machines
    dotnet restore "$csproj" -r win-arm64 --nologo
    $restoreArm64 = $LASTEXITCODE

    if ($restoreBase -eq 0 -and $restoreX64 -eq 0 -and $restoreArm64 -eq 0) {
        Write-Host '  [OK] dotnet restore succeeded (base, win-x64, win-arm64).' -ForegroundColor Green
    } else {
        Write-Host '  [FAIL] dotnet restore returned errors. Check the output above.' -ForegroundColor Red
        $errors++
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  4. dotnet restore with AI packages (for run_stream_ai.ps1)
# ------------------------------------------------------------------
Write-Host '  [4/4] Running dotnet restore (AI packages)...' -ForegroundColor Cyan

if (-not (Test-Path $csproj)) {
    Write-Host "  [SKIP] Project file not found." -ForegroundColor Yellow
} elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '  [SKIP] dotnet not available.' -ForegroundColor Yellow
} else {
    dotnet restore "$csproj" -p:EnableAI=true --nologo
    if ($LASTEXITCODE -eq 0) {
        Write-Host '  [OK] AI package restore succeeded.' -ForegroundColor Green
    } else {
        Write-Host '  [!] AI package restore failed (non-fatal — AI benchmark is optional).' -ForegroundColor Yellow
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  Summary
# ------------------------------------------------------------------
Write-Host '  ========================================' -ForegroundColor DarkGray
if ($errors -eq 0) {
    Write-Host '   Setup complete!' -ForegroundColor Green
    Write-Host ''
    Write-Host '  You can now run:' -ForegroundColor Cyan
    Write-Host '    .\run_stream.ps1        (memory benchmark only)'
    Write-Host '    .\run_stream_ai.ps1     (memory + AI benchmark)'
} else {
    Write-Host "   Setup finished with $errors issue(s). See messages above." -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Resolve the issues above, then re-run .\setup.ps1' -ForegroundColor Yellow
}
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

if ($errors -gt 0) { exit 1 }
