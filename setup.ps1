#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Windows Setup Script
# ============================================================
# Installs prerequisites and prepares the environment so that
# run_stream.ps1 and run_stream_ai.ps1 work on a fresh Windows
# machine (no Visual Studio required for running).
#
# What this script does:
#   1. Installs Visual C++ Redistributable 2015+ (for vcomp140.dll)
#   2. (Source mode only) Installs .NET 10 SDK if missing
#   3. (Source mode only) Runs "dotnet restore" for base + AI packages
#   4. (Optional) Installs Microsoft Foundry Local for AI benchmark
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
#  Detect mode: standalone (exe only) vs source (has StreamBench.csproj)
# ------------------------------------------------------------------
$csproj = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'
$hasSource = Test-Path $csproj
if ($hasSource) {
    Write-Host '  Mode: Source (StreamBench project found)' -ForegroundColor DarkGray
} else {
    Write-Host '  Mode: Standalone (pre-built executables only)' -ForegroundColor DarkGray
}
Write-Host ''

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
#  1. Visual C++ Redistributable (vcomp140.dll for OpenMP)
# ------------------------------------------------------------------
Write-Host '  [1/4] Checking Visual C++ Redistributable...' -ForegroundColor Cyan

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
#  2. .NET 10 SDK (source mode only)
# ------------------------------------------------------------------
if ($hasSource) {
    Write-Host '  [2/4] Checking .NET 10 SDK...' -ForegroundColor Cyan

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
} else {
    Write-Host '  [2/4] .NET 10 SDK — [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  3. dotnet restore (source mode only)
# ------------------------------------------------------------------
if ($hasSource) {
    Write-Host '  [3/4] Running dotnet restore...' -ForegroundColor Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host '  [SKIP] dotnet not available — install .NET 10 SDK first.' -ForegroundColor Yellow
    } else {
        dotnet restore "$csproj" --nologo
        $restoreBase = $LASTEXITCODE

        dotnet restore "$csproj" -r win-x64 --nologo
        $restoreX64 = $LASTEXITCODE

        dotnet restore "$csproj" -r win-arm64 --nologo
        $restoreArm64 = $LASTEXITCODE

        if ($restoreBase -eq 0 -and $restoreX64 -eq 0 -and $restoreArm64 -eq 0) {
            Write-Host '  [OK] dotnet restore succeeded (base, win-x64, win-arm64).' -ForegroundColor Green
        } else {
            Write-Host '  [FAIL] dotnet restore returned errors. Check the output above.' -ForegroundColor Red
            $errors++
        }

        # AI package restore
        Write-Host ''
        Write-Host '  Running dotnet restore (AI packages)...' -ForegroundColor Cyan
        dotnet restore "$csproj" -p:EnableAI=true --nologo
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] AI package restore succeeded.' -ForegroundColor Green
        } else {
            Write-Host '  [!] AI package restore failed (non-fatal — AI benchmark is optional).' -ForegroundColor Yellow
        }
    }
} else {
    Write-Host '  [3/4] dotnet restore — [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  4. Microsoft Foundry Local (for AI benchmark)
# ------------------------------------------------------------------
Write-Host '  [4/4] Checking Microsoft Foundry Local (AI benchmark)...' -ForegroundColor Cyan

$foundryOk = [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
if ($foundryOk) {
    Write-Host '  [OK] Microsoft Foundry Local is installed.' -ForegroundColor Green
} else {
    Write-Host '  [!] Foundry Local not found (required for AI benchmark).' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host '  Installing Microsoft Foundry Local via winget...' -ForegroundColor Yellow
        winget install Microsoft.FoundryLocal --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] Foundry Local installed.' -ForegroundColor Green
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
        } else {
            Write-Host '  [!] Installation may have failed (non-fatal — AI benchmark is optional).' -ForegroundColor Yellow
            Write-Host '      Install manually: winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
        }
    } else {
        Write-Host '  Install manually: winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
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
