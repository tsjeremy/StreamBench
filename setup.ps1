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
#   2. Installs .NET 10 SDK (source) or .NET 10 Runtime (standalone)
#   3. (Source mode only) Checks for MSVC Build Tools (cl.exe)
#   4. (Source mode only) Runs "dotnet restore" for base + AI packages
#   5. Checks GPU driver / OpenCL availability for GPU benchmark
#   6. (Optional) Checks / installs PowerShell 7
#   7. (Optional) Installs Microsoft Foundry Local for AI benchmark
#
# Usage:
#   .\setup.ps1
#   pwsh -ExecutionPolicy Bypass -File .\setup.ps1
#   powershell -ExecutionPolicy Bypass -File .\setup.ps1
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
#  Reliable architecture detection (works on PS 5.1 + ARM64 Windows)
# ------------------------------------------------------------------
# $env:PROCESSOR_ARCHITECTURE reports the *process* architecture, not
# the OS architecture.  On ARM64 Windows, PowerShell 5.1 is an x64
# binary running under emulation, so $env:PROCESSOR_ARCHITECTURE
# returns "AMD64" instead of "ARM64".
#
# [RuntimeInformation]::OSArchitecture returns the true OS architecture
# regardless of the process emulation layer.
# ------------------------------------------------------------------
try {
    $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
} catch {
    # Fallback for PowerShell 5.1 where RuntimeInformation is unavailable.
    # Check native OS arch via registry to detect ARM64 even under x64 emulation.
    $nativeArch = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE -ErrorAction SilentlyContinue).PROCESSOR_ARCHITECTURE
    if (-not $nativeArch) { $nativeArch = $env:PROCESSOR_ARCHITECTURE }
    $osArch = switch ($nativeArch) {
        'ARM64' { 'Arm64' }
        'AMD64' { 'X64' }
        default { $nativeArch }
    }
}
$archTag = if ($osArch -eq 'Arm64') { 'arm64' } else { 'x64' }

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host '   STREAM Benchmark - Windows Setup' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''
Write-Host "  PowerShell version : $($PSVersionTable.PSVersion)" -ForegroundColor DarkGray
Write-Host "  OS Architecture    : $osArch" -ForegroundColor DarkGray
Write-Host "  Selected arch tag  : $archTag" -ForegroundColor DarkGray
Write-Host ''

$errors = 0

# ------------------------------------------------------------------
#  Prevent system sleep during setup (Windows only).
#  ES_CONTINUOUS (0x80000000) | ES_SYSTEM_REQUIRED (0x00000001)
#  blocks unattended sleep; screen-off timeout is unaffected.
# ------------------------------------------------------------------
$sleepPrevented = $false
if ($IsWindows) {
    try {
        Add-Type -Namespace Win32 -Name PowerMgmt -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            public static extern uint SetThreadExecutionState(uint esFlags);
'@ -ErrorAction Stop
        [Win32.PowerMgmt]::SetThreadExecutionState(0x80000001) | Out-Null
        $sleepPrevented = $true
        Write-Host '  [OK] System sleep prevention active (screen-off timeout unchanged).' -ForegroundColor DarkGray
    } catch {
        Write-Host '  [!] Could not prevent system sleep (non-fatal).' -ForegroundColor DarkGray
    }
    Write-Host ''
}

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
Write-Host '  [1/7] Checking Visual C++ Redistributable...' -ForegroundColor Cyan

$vcRedistOk = (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -or
              (Test-Path "$env:SystemRoot\SysWOW64\vcomp140.dll")

if ($vcRedistOk) {
    Write-Host '  [OK] Visual C++ Redistributable (vcomp140.dll) found.' -ForegroundColor Green
} else {
    Write-Host '  [!] vcomp140.dll not found (required by CPU benchmark).' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host "  Installing VC++ Redistributable ($archTag) via winget..." -ForegroundColor Yellow
        winget install "Microsoft.VCRedist.2015+.$archTag" --accept-package-agreements --accept-source-agreements --silent
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
    Write-Host '  [2/7] Checking .NET 10 SDK...' -ForegroundColor Cyan

    # Refresh PATH so we pick up dotnet even if it was installed in another session
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')

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
            winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements --silent
            $wingetExit = $LASTEXITCODE
            # Refresh PATH after install
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
            # Verify dotnet 10 is now on PATH (winget returns non-zero for "already installed")
            $sdks = $null
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                $sdks = & dotnet --list-sdks 2>$null
            }
            if ($sdks -match '^10\.') {
                Write-Host '  [OK] .NET 10 SDK installed.' -ForegroundColor Green
                $dotnetOk = $true
            } elseif ($wingetExit -eq 0) {
                Write-Host '  [OK] .NET 10 SDK installed.' -ForegroundColor Green
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
    Write-Host '  [2/7] Checking .NET 10 Runtime...' -ForegroundColor Cyan

    # Standalone mode requires .NET 10 Runtime (not the full SDK)
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')

    $dotnetRuntimeOk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($runtimes -match 'Microsoft\.NETCore\.App 10\.') {
            Write-Host '  [OK] .NET 10 Runtime is already installed.' -ForegroundColor Green
            $dotnetRuntimeOk = $true
        }
    }

    if (-not $dotnetRuntimeOk) {
        Write-Host '  [!] .NET 10 Runtime not found.' -ForegroundColor Yellow
        if ($hasWinget) {
            Write-Host '  Installing .NET 10 Runtime via winget...' -ForegroundColor Yellow
            winget install Microsoft.DotNet.Runtime.10 --accept-package-agreements --accept-source-agreements --silent
            $wingetExit = $LASTEXITCODE
            # Refresh PATH after install
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
            $runtimes = $null
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                $runtimes = & dotnet --list-runtimes 2>$null
            }
            if ($runtimes -match 'Microsoft\.NETCore\.App 10\.') {
                Write-Host '  [OK] .NET 10 Runtime installed.' -ForegroundColor Green
                $dotnetRuntimeOk = $true
            } elseif ($wingetExit -eq 0) {
                Write-Host '  [OK] .NET 10 Runtime installed.' -ForegroundColor Green
                $dotnetRuntimeOk = $true
            } else {
                Write-Host '  [FAIL] .NET 10 Runtime installation failed.' -ForegroundColor Red
                Write-Host '         Download manually: https://dot.net/download' -ForegroundColor Yellow
                $errors++
            }
        } else {
            Write-Host '  Download .NET 10 Runtime from: https://dot.net/download' -ForegroundColor Yellow
            $errors++
        }
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  3. MSVC Build Tools (source mode only — for building C backends)
# ------------------------------------------------------------------
if ($hasSource) {
    Write-Host '  [3/7] Checking MSVC Build Tools (cl.exe)...' -ForegroundColor Cyan

    # Look for vcvarsall.bat (same logic as build_all_windows.ps1)
    $vcFound = $false
    $editions = @('Enterprise','Professional','Community','BuildTools')
    $versions = @('2022','2025','18')
    $roots    = @($env:ProgramFiles, ${env:ProgramFiles(x86)})
    foreach ($ver in $versions) {
        foreach ($ed in $editions) {
            foreach ($root in $roots) {
                if (-not $root) { continue }
                $candidate = Join-Path $root "Microsoft Visual Studio\$ver\$ed\VC\Auxiliary\Build\vcvarsall.bat"
                if (Test-Path $candidate) { $vcFound = $true; break }
            }
            if ($vcFound) { break }
        }
        if ($vcFound) { break }
    }
    if (-not $vcFound) {
        # Fallback: vswhere
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $installPath = & $vswhere -latest -property installationPath 2>$null
            if ($installPath) {
                $candidate = Join-Path $installPath 'VC\Auxiliary\Build\vcvarsall.bat'
                if (Test-Path $candidate) { $vcFound = $true }
            }
        }
    }

    if ($vcFound) {
        Write-Host '  [OK] MSVC Build Tools found (cl.exe available via vcvarsall.bat).' -ForegroundColor Green
    } else {
        Write-Host '  [!] MSVC Build Tools not found (needed to build C backends from source).' -ForegroundColor Yellow
        if ($hasWinget) {
            Write-Host '      Install with:' -ForegroundColor Yellow
            Write-Host '        winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"' -ForegroundColor Yellow
        } else {
            Write-Host '      Install Visual Studio 2022 with "Desktop development with C++" workload.' -ForegroundColor Yellow
            Write-Host '      https://visualstudio.microsoft.com/downloads/' -ForegroundColor Yellow
        }
    }
} else {
    Write-Host '  [3/7] MSVC Build Tools — [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  4. dotnet restore (source mode only)
# ------------------------------------------------------------------
if ($hasSource) {
    Write-Host '  [4/7] Running dotnet restore...' -ForegroundColor Cyan

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
    Write-Host '  [4/7] dotnet restore — [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  5. GPU driver / OpenCL check (for GPU benchmark)
# ------------------------------------------------------------------
Write-Host '  [5/7] Checking GPU driver / OpenCL availability...' -ForegroundColor Cyan

$openclDll = Join-Path $env:SystemRoot 'System32\OpenCL.dll'
if (Test-Path $openclDll) {
    Write-Host '  [OK] OpenCL.dll found — GPU benchmark should work.' -ForegroundColor Green
} else {
    Write-Host '  [!] OpenCL.dll not found in System32.' -ForegroundColor Yellow
    Write-Host '      GPU benchmark requires an OpenCL-capable GPU with up-to-date drivers.' -ForegroundColor Yellow
    Write-Host '      Install or update your GPU driver:' -ForegroundColor Yellow
    Write-Host '        NVIDIA : https://www.nvidia.com/drivers' -ForegroundColor Yellow
    Write-Host '        AMD    : https://www.amd.com/en/support' -ForegroundColor Yellow
    Write-Host '        Intel  : https://www.intel.com/content/www/us/en/download-center' -ForegroundColor Yellow
    Write-Host '      (GPU benchmark is optional — CPU benchmark will still work.)' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  6. PowerShell 7 (pwsh) — recommended for best experience
# ------------------------------------------------------------------
Write-Host '  [6/7] Checking PowerShell 7 (pwsh)...' -ForegroundColor Cyan

if (Get-Command pwsh -ErrorAction SilentlyContinue) {
    $pwshVer = (& pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()') 2>$null
    Write-Host "  [OK] PowerShell 7 found (pwsh $pwshVer)." -ForegroundColor Green
} else {
    Write-Host '  [!] PowerShell 7 (pwsh) not found.' -ForegroundColor Yellow
    Write-Host '      Scripts work with PowerShell 5.1, but pwsh is recommended.' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host '  Installing PowerShell 7 via winget...' -ForegroundColor Yellow
        winget install Microsoft.PowerShell --accept-package-agreements --accept-source-agreements --silent
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] PowerShell 7 installed.' -ForegroundColor Green
        } else {
            Write-Host '  [!] Installation may have failed.' -ForegroundColor Yellow
            Write-Host '      Install manually: winget install Microsoft.PowerShell' -ForegroundColor Yellow
        }
    } else {
        Write-Host '      Install: winget install Microsoft.PowerShell' -ForegroundColor Yellow
        Write-Host '      Or: https://github.com/PowerShell/PowerShell/releases/latest' -ForegroundColor Yellow
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  7. Microsoft Foundry Local (for AI benchmark)
# ------------------------------------------------------------------
Write-Host '  [7/7] Checking Microsoft Foundry Local (AI benchmark)...' -ForegroundColor Cyan

# Refresh PATH before checking — catches installs done in other sessions
$env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
            [System.Environment]::GetEnvironmentVariable('PATH', 'User')

$foundryOk = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
             [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)

# MSIX alias fallback: probe well-known WindowsApps path if not on PATH yet
if (-not $foundryOk) {
    $msixDir = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'
    foreach ($name in @('foundry.exe', 'foundrylocal.exe')) {
        $fullPath = Join-Path $msixDir $name
        if (Test-Path $fullPath) {
            $foundryOk = $true
            break
        }
    }
    if ($foundryOk) {
        # Ensure WindowsApps is on PATH for this session
        if ($env:PATH -notlike "*$msixDir*") {
            $env:PATH = "$msixDir;$env:PATH"
        }
    }
}

if ($foundryOk) {
    Write-Host '  [OK] Microsoft Foundry Local CLI is installed.' -ForegroundColor Green
    # Validate service actually works
    $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
    try {
        $statusOutput = & $foundryCmd service status 2>&1
        $statusStr = ($statusOutput | Out-String)
        if ($statusStr -match 'running') {
            Write-Host '  [OK] Foundry Local service is running.' -ForegroundColor Green
        } else {
            Write-Host '  [!] Foundry Local CLI found but service not running.' -ForegroundColor Yellow
            Write-Host "      Start it with: $foundryCmd service start" -ForegroundColor Yellow
        }
    } catch {
        Write-Host '  [!] Foundry Local CLI found but service check failed.' -ForegroundColor Yellow
        Write-Host "      Try: $foundryCmd service start" -ForegroundColor Yellow
    }

    # Check if any models are already cached (fast local-only check)
    try {
        $cacheOutput = & $foundryCmd cache list 2>&1 | Out-String
        $hasCachedModels = ($cacheOutput -match 'phi-|qwen|deepseek|gpt-')
        if ($hasCachedModels) {
            Write-Host '  [OK] AI models are cached and ready.' -ForegroundColor Green
        } else {
            # No models cached — on first run, 'foundry model list' downloads
            # execution providers (EPs) for your hardware, which can take several
            # minutes.  Show progress so the user knows it is not hung.
            Write-Host '  [!] No AI models cached yet.' -ForegroundColor Yellow
            Write-Host '      First-time setup: downloading execution providers for your hardware...' -ForegroundColor Cyan
            Write-Host '      (This is a one-time download and may take several minutes.)' -ForegroundColor Yellow
            $epStart = Get-Date
            Write-Host "      Started: $($epStart.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
            # Run with timeout (5 min) to prevent indefinite hangs
            $epJob = Start-Job -ScriptBlock { param($cmd) & $cmd model list 2>&1 } -ArgumentList $foundryCmd
            $epTimedOut = $false
            while ($epJob.State -eq 'Running') {
                $waited = [int]((Get-Date) - $epStart).TotalSeconds
                if ($waited -ge 300) { $epTimedOut = $true; break }
                Write-Host '.' -NoNewline -ForegroundColor Cyan
                Start-Sleep -Seconds 5
            }
            Write-Host ''
            if ($epTimedOut) {
                Write-Host '      [!] EP download timed out after 5 min (non-fatal). Stopping...' -ForegroundColor Yellow
                $epJob | Stop-Job -PassThru | Remove-Job -Force
            } else {
                # Suppress catalog dump — only the EP download matters here
                Receive-Job $epJob | Out-Null
                Remove-Job $epJob -Force
            }
            $epSec = [int]((Get-Date) - $epStart).TotalSeconds
            Write-Host "      Execution providers ready in ${epSec}s." -ForegroundColor DarkGray
            Write-Host ''
            Write-Host '  Downloading default AI model (phi-3.5-mini)...' -ForegroundColor Cyan
            Write-Host '      (This may take several minutes on first run.)' -ForegroundColor Yellow
            $dlStart = Get-Date
            Write-Host "      Started: $($dlStart.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
            # Run with timeout (10 min) to prevent indefinite hangs
            $dlJob = Start-Job -ScriptBlock { param($cmd) & $cmd model download phi-3.5-mini 2>&1 } -ArgumentList $foundryCmd
            $dlTimedOut = $false
            while ($dlJob.State -eq 'Running') {
                $waited = [int]((Get-Date) - $dlStart).TotalSeconds
                if ($waited -ge 600) { $dlTimedOut = $true; break }
                Write-Host '.' -NoNewline -ForegroundColor Cyan
                Start-Sleep -Seconds 5
            }
            Write-Host ''
            if ($dlTimedOut) {
                Write-Host "      [!] Model download timed out after 10 min (non-fatal). Stopping..." -ForegroundColor Yellow
                $dlJob | Stop-Job -PassThru | Remove-Job -Force
                $dlSec = 600
            } else {
                $dlOutput = Receive-Job $dlJob 2>&1 | Out-String
                Remove-Job $dlJob -Force
                $dlSec = [int]((Get-Date) - $dlStart).TotalSeconds
            }
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  [OK] Default model (phi-3.5-mini) downloaded in ${dlSec}s." -ForegroundColor Green
            } else {
                Write-Host "  [!] Model download failed after ${dlSec}s (non-fatal). Try manually: foundry model run phi-3.5-mini" -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host '  [!] Could not check model cache (non-fatal).' -ForegroundColor Yellow
    }
} else {
    Write-Host '  [!] Foundry Local not found (required for AI benchmark).' -ForegroundColor Yellow
    if ($hasWinget) {
        Write-Host '  Installing Microsoft Foundry Local via winget...' -ForegroundColor Yellow
        winget install Microsoft.FoundryLocal --accept-package-agreements --accept-source-agreements --silent
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [OK] Foundry Local installed.' -ForegroundColor Green
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                        [System.Environment]::GetEnvironmentVariable('PATH', 'User')
            # Probe MSIX alias in well-known WindowsApps path
            $foundryOk = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                         [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
            if (-not $foundryOk) {
                $msixDir = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'
                foreach ($name in @('foundry.exe', 'foundrylocal.exe')) {
                    $fullPath = Join-Path $msixDir $name
                    if (Test-Path $fullPath) {
                        $foundryOk = $true
                        if ($env:PATH -notlike "*$msixDir*") {
                            $env:PATH = "$msixDir;$env:PATH"
                        }
                        break
                    }
                }
            }
            if (-not $foundryOk) {
                Write-Host '  [!] Foundry Local installed but CLI not yet on PATH.' -ForegroundColor Yellow
                Write-Host '      Please restart your terminal/PowerShell session, then re-run setup.ps1.' -ForegroundColor Yellow
            } else {
                # CLI is reachable — first-run EP download + default model
                $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
                Write-Host '      First-time setup: downloading execution providers for your hardware...' -ForegroundColor Cyan
                Write-Host '      (This is a one-time download and may take several minutes.)' -ForegroundColor Yellow
                $epStart2 = Get-Date
                Write-Host "      Started: $($epStart2.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
                # Run with timeout (5 min) to prevent indefinite hangs
                $epJob2 = Start-Job -ScriptBlock { param($cmd) & $cmd model list 2>&1 } -ArgumentList $foundryCmd
                $epTimedOut2 = $false
                while ($epJob2.State -eq 'Running') {
                    $waited2 = [int]((Get-Date) - $epStart2).TotalSeconds
                    if ($waited2 -ge 300) { $epTimedOut2 = $true; break }
                    Write-Host '.' -NoNewline -ForegroundColor Cyan
                    Start-Sleep -Seconds 5
                }
                Write-Host ''
                if ($epTimedOut2) {
                    Write-Host '      [!] EP download timed out after 5 min (non-fatal). Stopping...' -ForegroundColor Yellow
                    $epJob2 | Stop-Job -PassThru | Remove-Job -Force
                } else {
                    # Suppress catalog dump — only the EP download matters here
                    Receive-Job $epJob2 | Out-Null
                    Remove-Job $epJob2 -Force
                }
                $epSec2 = [int]((Get-Date) - $epStart2).TotalSeconds
                Write-Host "      Execution providers ready in ${epSec2}s." -ForegroundColor DarkGray
                Write-Host ''
                Write-Host '  Downloading default AI model (phi-3.5-mini)...' -ForegroundColor Cyan
                Write-Host '      (This may take several minutes on first run.)' -ForegroundColor Yellow
                $dlStart2 = Get-Date
                Write-Host "      Started: $($dlStart2.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
                # Run with timeout (10 min) to prevent indefinite hangs
                $dlJob2 = Start-Job -ScriptBlock { param($cmd) & $cmd model download phi-3.5-mini 2>&1 } -ArgumentList $foundryCmd
                $dlTimedOut2 = $false
                while ($dlJob2.State -eq 'Running') {
                    $waited2 = [int]((Get-Date) - $dlStart2).TotalSeconds
                    if ($waited2 -ge 600) { $dlTimedOut2 = $true; break }
                    Write-Host '.' -NoNewline -ForegroundColor Cyan
                    Start-Sleep -Seconds 5
                }
                Write-Host ''
                if ($dlTimedOut2) {
                    Write-Host "      [!] Model download timed out after 10 min (non-fatal). Stopping..." -ForegroundColor Yellow
                    $dlJob2 | Stop-Job -PassThru | Remove-Job -Force
                    $dlSec2 = 600
                } else {
                    $dlOutput2 = Receive-Job $dlJob2 2>&1 | Out-String
                    Remove-Job $dlJob2 -Force
                    $dlSec2 = [int]((Get-Date) - $dlStart2).TotalSeconds
                }
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  [OK] Default model (phi-3.5-mini) downloaded in ${dlSec2}s." -ForegroundColor Green
                } else {
                    Write-Host "  [!] Model download failed after ${dlSec2}s (non-fatal). Try: foundry model run phi-3.5-mini" -ForegroundColor Yellow
                }
            }
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

# ------------------------------------------------------------------
#  Restore sleep settings (always, even if errors occurred above)
# ------------------------------------------------------------------
if ($sleepPrevented) {
    try { [Win32.PowerMgmt]::SetThreadExecutionState(0x80000000) | Out-Null } catch {}
}

if ($errors -gt 0) { exit 1 }
