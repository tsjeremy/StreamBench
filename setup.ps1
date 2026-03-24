#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Setup Script (Windows / macOS)
# ============================================================
# Installs prerequisites and prepares the environment so that
# run_stream.ps1 works on a fresh machine.
#
# Windows:
#   1. Installs Visual C++ Redistributable 2015+ (for vcomp140.dll)
#   2. Installs .NET 10 SDK (source) or .NET 10 Runtime (standalone)
#   3. (Source mode only) Checks for MSVC Build Tools (cl.exe)
#   4. (Source mode only) Runs "dotnet restore" for base + AI packages
#   5. Checks GPU driver / OpenCL availability for GPU benchmark
#   6. (Optional) Checks / installs PowerShell 7
#   7. (Optional) Installs AI backends (Microsoft Foundry Local and/or LM Studio)
#
# macOS (Apple Silicon):
#   1. Checks / installs Homebrew
#   2. Installs .NET 10 SDK (source) or .NET 10 Runtime (standalone)
#   3. (Source mode only) Checks Xcode CLI tools + libomp for C backends
#   4. (Source mode only) Runs "dotnet restore" for base + AI packages
#   5. Checks GPU (Metal/OpenCL) availability for GPU benchmark
#   6. PowerShell 7 -- already running if you see this
#   7. (Optional) Installs AI backends (Foundry Local via brew, LM Studio via brew cask)
#
# Usage:
#   pwsh ./setup.ps1                                          # macOS / Linux
#   .\setup.ps1                                               # Windows (PowerShell 7)
#   pwsh -ExecutionPolicy Bypass -File .\setup.ps1            # Windows
#   powershell -ExecutionPolicy Bypass -File .\setup.ps1      # Windows (5.1)
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

$platformLabel = if ($IsMacOS) { 'macOS' } elseif ($IsLinux) { 'Linux' } else { 'Windows' }

Write-Host ''
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host "   STREAM Benchmark - $platformLabel Setup" -ForegroundColor Cyan
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
$caffeinatePid = $null
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
} elseif ($IsMacOS) {
    try {
        $caffeinatePid = (Start-Process caffeinate -ArgumentList '-dims' -PassThru -ErrorAction Stop).Id
        $sleepPrevented = $true
        Write-Host '  [OK] System sleep prevention active (caffeinate -dims).' -ForegroundColor DarkGray
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
#  Helper: check winget (Windows) / Homebrew (macOS) availability
# ------------------------------------------------------------------
$hasWinget = $false
$hasBrew = $false

if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
    $hasWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
    if (-not $hasWinget) {
        Write-Host '  [!] winget not found.' -ForegroundColor Yellow
        Write-Host '      Install App Installer from the Microsoft Store, then re-run this script.'
        Write-Host '      https://apps.microsoft.com/detail/9NBLGGH4NNS1'
        Write-Host ''
    }
} elseif ($IsMacOS) {
    $hasBrew = [bool](Get-Command brew -ErrorAction SilentlyContinue)
    if (-not $hasBrew) {
        Write-Host '  [!] Homebrew not found — installing...' -ForegroundColor Yellow
        Write-Host '      Homebrew is the macOS package manager (like winget on Windows).' -ForegroundColor DarkGray
        try {
            # The official Homebrew installer
            bash -c 'NONINTERACTIVE=1 /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"'
            # Add Homebrew to PATH for this session (Apple Silicon default)
            if (Test-Path '/opt/homebrew/bin/brew') {
                $env:PATH = "/opt/homebrew/bin:/opt/homebrew/sbin:$env:PATH"
            }
            $hasBrew = [bool](Get-Command brew -ErrorAction SilentlyContinue)
            if ($hasBrew) {
                Write-Host '  [OK] Homebrew installed.' -ForegroundColor Green
            } else {
                Write-Host '  [!] Homebrew install completed but brew not found on PATH.' -ForegroundColor Yellow
                Write-Host '      Open a new terminal and re-run this script.' -ForegroundColor Yellow
            }
        } catch {
            Write-Host '  [!] Homebrew installation failed.' -ForegroundColor Yellow
            Write-Host '      Install manually: /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"' -ForegroundColor Yellow
        }
        Write-Host ''
    }
}

# ------------------------------------------------------------------
#  1. C runtime prerequisites
# ------------------------------------------------------------------
Write-Host '  [1/7] Checking C runtime prerequisites...' -ForegroundColor Cyan

if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
    # Windows: Visual C++ Redistributable (vcomp140.dll for OpenMP)
    $vcRedistOk = (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -or
                  (Test-Path "$env:SystemRoot\SysWOW64\vcomp140.dll")

    if ($vcRedistOk) {
        Write-Host '  [OK] Visual C++ Redistributable (vcomp140.dll) found.' -ForegroundColor Green
    } else {
        Write-Host '  [!] vcomp140.dll not found (required by CPU benchmark).' -ForegroundColor Yellow
        if ($hasWinget) {
            Write-Host "  Installing VC++ Redistributable ($archTag) via winget..." -ForegroundColor Yellow
            winget install "Microsoft.VCRedist.2015+.$archTag" --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
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
} elseif ($IsMacOS) {
    # macOS: libomp (for CPU OpenMP benchmark — needed at runtime for multi-threaded CPU backend)
    $hasLibomp = (Test-Path '/opt/homebrew/opt/libomp') -or (Test-Path '/usr/local/opt/libomp')
    if ($hasLibomp) {
        Write-Host '  [OK] libomp found (OpenMP support for CPU benchmark).' -ForegroundColor Green
    } else {
        Write-Host '  [!] libomp not found (needed for multi-threaded CPU benchmark).' -ForegroundColor Yellow
        if ($hasBrew) {
            Write-Host '  Installing libomp via Homebrew...' -ForegroundColor Yellow
            & brew install libomp
            if ($LASTEXITCODE -eq 0) {
                Write-Host '  [OK] libomp installed.' -ForegroundColor Green
            } else {
                Write-Host '  [!] libomp install failed. Install manually: brew install libomp' -ForegroundColor Yellow
            }
        } else {
            Write-Host '      Install with: brew install libomp' -ForegroundColor Yellow
        }
    }
} else {
    Write-Host '  [--] Linux: ensure libomp-dev / libomp is installed for CPU multi-threading.' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  2. .NET 10 SDK (source mode only)
# ------------------------------------------------------------------

# Helper: refresh PATH (cross-platform)
function Refresh-SetupPath {
    if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
        $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                    [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    } else {
        # On macOS/Linux, ensure common dotnet + Homebrew paths are in PATH
        $extraPaths = @('/opt/homebrew/bin', '/usr/local/bin', '/usr/local/share/dotnet',
                        "$HOME/.dotnet", "$HOME/.dotnet/tools")
        foreach ($p in $extraPaths) {
            if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) {
                $env:PATH = "${p}:$env:PATH"
            }
        }
    }
}

if ($hasSource) {
    Write-Host '  [2/7] Checking .NET 10 SDK...' -ForegroundColor Cyan

    Refresh-SetupPath

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
            winget install Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
            $wingetExit = $LASTEXITCODE
            Refresh-SetupPath
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
        } elseif ($hasBrew) {
            Write-Host '  Installing .NET 10 SDK via Homebrew...' -ForegroundColor Yellow
            brew install --cask dotnet-sdk
            Refresh-SetupPath
            $sdks = $null
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                $sdks = & dotnet --list-sdks 2>$null
            }
            if ($sdks -match '^10\.') {
                Write-Host '  [OK] .NET 10 SDK installed.' -ForegroundColor Green
                $dotnetOk = $true
            } else {
                Write-Host '  [!] .NET SDK installed but version 10 not detected.' -ForegroundColor Yellow
                Write-Host '      You may need to install the .NET 10 preview from https://dot.net/download' -ForegroundColor Yellow
                $errors++
            }
        } else {
            Write-Host '  Download .NET 10 SDK from: https://dot.net/download' -ForegroundColor Yellow
            $errors++
        }
    }
} else {
    Write-Host '  [2/7] Checking .NET 10 Runtime...' -ForegroundColor Cyan

    Refresh-SetupPath

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
            winget install Microsoft.DotNet.Runtime.10 --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
            $wingetExit = $LASTEXITCODE
            Refresh-SetupPath
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
        } elseif ($hasBrew) {
            Write-Host '  Installing .NET Runtime via Homebrew...' -ForegroundColor Yellow
            brew install --cask dotnet-sdk
            Refresh-SetupPath
            $runtimes = $null
            if (Get-Command dotnet -ErrorAction SilentlyContinue) {
                $runtimes = & dotnet --list-runtimes 2>$null
            }
            if ($runtimes -match 'Microsoft\.NETCore\.App 10\.') {
                Write-Host '  [OK] .NET 10 Runtime installed.' -ForegroundColor Green
                $dotnetRuntimeOk = $true
            } else {
                Write-Host '  [!] .NET Runtime installed but version 10 not detected.' -ForegroundColor Yellow
                Write-Host '      Download manually: https://dot.net/download' -ForegroundColor Yellow
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
#  3. Build tools (source mode only — for building C backends)
# ------------------------------------------------------------------
if ($hasSource) {
    if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
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
    } elseif ($IsMacOS) {
        Write-Host '  [3/7] Checking Xcode Command Line Tools (clang)...' -ForegroundColor Cyan

        $hasClang = [bool](Get-Command clang -ErrorAction SilentlyContinue)
        if ($hasClang) {
            Write-Host '  [OK] clang found (Xcode Command Line Tools installed).' -ForegroundColor Green
        } else {
            Write-Host '  [!] clang not found — installing Xcode Command Line Tools...' -ForegroundColor Yellow
            try {
                xcode-select --install 2>&1 | Out-Null
                Write-Host '  [OK] Xcode Command Line Tools installation triggered.' -ForegroundColor Green
                Write-Host '      Complete the installation dialog, then re-run this script.' -ForegroundColor Yellow
            } catch {
                Write-Host '  [!] Could not trigger Xcode CLI tools install.' -ForegroundColor Yellow
                Write-Host '      Install manually: xcode-select --install' -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host '  [3/7] Checking build tools (gcc/clang)...' -ForegroundColor Cyan
        $hasCC = [bool](Get-Command gcc -ErrorAction SilentlyContinue) -or [bool](Get-Command clang -ErrorAction SilentlyContinue)
        if ($hasCC) {
            Write-Host '  [OK] C compiler found.' -ForegroundColor Green
        } else {
            Write-Host '  [!] No C compiler found. Install gcc or clang.' -ForegroundColor Yellow
        }
    }
} else {
    Write-Host '  [3/7] Build tools -- [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  4. dotnet restore (source mode only)
# ------------------------------------------------------------------
if ($hasSource) {
    Write-Host '  [4/7] Running dotnet restore...' -ForegroundColor Cyan

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host '  [SKIP] dotnet not available -- install .NET 10 SDK first.' -ForegroundColor Yellow
    } else {
        dotnet restore "$csproj" --nologo
        $restoreBase = $LASTEXITCODE

        # Platform-specific RID restores
        if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
            dotnet restore "$csproj" -r win-x64 --nologo
            $restoreR1 = $LASTEXITCODE
            dotnet restore "$csproj" -r win-arm64 --nologo
            $restoreR2 = $LASTEXITCODE
            $ridLabel = 'win-x64, win-arm64'
        } elseif ($IsMacOS) {
            dotnet restore "$csproj" -r osx-arm64 --nologo
            $restoreR1 = $LASTEXITCODE
            $restoreR2 = 0  # only one RID needed for Apple Silicon
            $ridLabel = 'osx-arm64'
        } else {
            dotnet restore "$csproj" -r linux-x64 --nologo
            $restoreR1 = $LASTEXITCODE
            $restoreR2 = 0
            $ridLabel = 'linux-x64'
        }

        if ($restoreBase -eq 0 -and $restoreR1 -eq 0 -and $restoreR2 -eq 0) {
            Write-Host "  [OK] dotnet restore succeeded (base, $ridLabel)." -ForegroundColor Green
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
            Write-Host '  [!] AI package restore failed (non-fatal -- AI benchmark is optional).' -ForegroundColor Yellow
        }
    }
} else {
    Write-Host '  [4/7] dotnet restore -- [SKIP] not needed for standalone exe' -ForegroundColor DarkGray
}
Write-Host ''

# ------------------------------------------------------------------
#  5. GPU driver / OpenCL check (for GPU benchmark)
# ------------------------------------------------------------------
Write-Host '  [5/7] Checking GPU / OpenCL availability...' -ForegroundColor Cyan

if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
    $openclDll = Join-Path $env:SystemRoot 'System32\OpenCL.dll'
    if (Test-Path $openclDll) {
        Write-Host '  [OK] OpenCL.dll found -- GPU benchmark should work.' -ForegroundColor Green
    } else {
        Write-Host '  [!] OpenCL.dll not found in System32.' -ForegroundColor Yellow
        Write-Host '      GPU benchmark requires an OpenCL-capable GPU with up-to-date drivers.' -ForegroundColor Yellow
        Write-Host '      Install or update your GPU driver:' -ForegroundColor Yellow
        Write-Host '        NVIDIA : https://www.nvidia.com/drivers' -ForegroundColor Yellow
        Write-Host '        AMD    : https://www.amd.com/en/support' -ForegroundColor Yellow
        Write-Host '        Intel  : https://www.intel.com/content/www/us/en/download-center' -ForegroundColor Yellow
        Write-Host '      (GPU benchmark is optional -- CPU benchmark will still work.)' -ForegroundColor DarkGray
    }
} elseif ($IsMacOS) {
    # macOS has OpenCL built-in (via Metal/OpenCL compatibility layer)
    $oclIcd = '/System/Library/Frameworks/OpenCL.framework'
    if (Test-Path $oclIcd) {
        Write-Host '  [OK] macOS OpenCL framework found -- GPU benchmark should work.' -ForegroundColor Green
    } else {
        Write-Host '  [!] OpenCL framework not found (unexpected on macOS).' -ForegroundColor Yellow
        Write-Host '      GPU benchmark may not work. CPU benchmark will still work.' -ForegroundColor DarkGray
    }
} else {
    Write-Host '  [--] Linux: ensure OpenCL ICD (mesa-opencl-icd, nvidia-opencl, etc.) is installed for GPU benchmark.' -ForegroundColor DarkGray
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
    if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
        Write-Host '      Scripts work with PowerShell 5.1, but pwsh is recommended.' -ForegroundColor Yellow
        if ($hasWinget) {
            Write-Host '  Installing PowerShell 7 via winget...' -ForegroundColor Yellow
            winget install Microsoft.PowerShell --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
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
    } elseif ($IsMacOS) {
        if ($hasBrew) {
            Write-Host '  Installing PowerShell 7 via Homebrew...' -ForegroundColor Yellow
            & brew install powershell
            if ($LASTEXITCODE -eq 0) {
                Write-Host '  [OK] PowerShell 7 installed.' -ForegroundColor Green
            } else {
                Write-Host '  [!] Installation may have failed.' -ForegroundColor Yellow
                Write-Host '      Install manually: brew install powershell' -ForegroundColor Yellow
            }
        } else {
            Write-Host '      Install: brew install powershell' -ForegroundColor Yellow
            Write-Host '      Or: https://github.com/PowerShell/PowerShell/releases/latest' -ForegroundColor Yellow
        }
    } else {
        Write-Host '      Install: https://github.com/PowerShell/PowerShell/releases/latest' -ForegroundColor Yellow
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  7. AI Backend Setup (Foundry Local and/or LM Studio)
# ------------------------------------------------------------------
Write-Host '  [7/7] Setting up AI backend (AI benchmark)...' -ForegroundColor Cyan

# ── Detect available backends ──

# Refresh PATH before checking — catches installs done in other sessions
if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')
} else {
    Refresh-SetupPath
}

# Foundry detection (Windows / macOS)
$foundryOk = $false
$foundryCmd = $null
if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
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
        if ($foundryOk -and ($env:PATH -notlike "*$msixDir*")) {
            $env:PATH = "$msixDir;$env:PATH"
        }
    }
    if ($foundryOk) {
        $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
    }
} elseif ($IsMacOS) {
    $foundryOk = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                 [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)

    # Homebrew fallback: probe well-known Homebrew paths (ARM64 + Intel)
    if (-not $foundryOk) {
        foreach ($dir in @('/opt/homebrew/bin', '/usr/local/bin')) {
            foreach ($name in @('foundry', 'foundrylocal')) {
                $fullPath = Join-Path $dir $name
                if (Test-Path $fullPath) {
                    $foundryOk = $true
                    $foundryCmd = $fullPath
                    break
                }
            }
            if ($foundryOk) { break }
        }
    }
    if ($foundryOk -and -not $foundryCmd) {
        $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
    }
}

# LM Studio detection (cross-platform)
$lmsOk = $false
$lmsCmd = $null
if (Get-Command lms -ErrorAction SilentlyContinue) {
    $lmsOk = $true
    $lmsCmd = 'lms'
} else {
    # Probe well-known LM Studio CLI paths
    $lmsPaths = @()
    if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
        $lmsPaths += Join-Path $env:USERPROFILE '.lmstudio\bin\lms.exe'
        $lmsPaths += Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\app\.webpack\lms.exe'
        $lmsPaths += Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\bin\lms.exe'
        $lmsPaths += Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\lms.exe'
    } elseif ($IsMacOS) {
        $lmsPaths += Join-Path $HOME '.lmstudio/bin/lms'
        $lmsPaths += '/opt/homebrew/bin/lms'
        $lmsPaths += '/usr/local/bin/lms'
    } else {
        $lmsPaths += Join-Path $HOME '.lmstudio/bin/lms'
    }
    foreach ($p in $lmsPaths) {
        if (Test-Path $p) {
            $lmsOk = $true
            $lmsCmd = $p
            break
        }
    }
    # Fallback: recursive search in the LM Studio install directory
    if (-not $lmsOk) {
        $lmInstallDir = if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
            Join-Path $env:LOCALAPPDATA 'Programs\LM Studio'
        } elseif ($IsMacOS) {
            '/Applications/LM Studio.app'
        } else { $null }
        if ($lmInstallDir -and (Test-Path $lmInstallDir)) {
            $lmsExe = Get-ChildItem -Path $lmInstallDir -Filter 'lms.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $lmsExe -and -not $IsWindows) {
                $lmsExe = Get-ChildItem -Path $lmInstallDir -Filter 'lms' -Recurse -ErrorAction SilentlyContinue |
                    Where-Object { -not $_.PSIsContainer } | Select-Object -First 1
            }
            if ($lmsExe) {
                $lmsOk = $true
                $lmsCmd = $lmsExe.FullName
            }
        }
    }
}

# Report what's detected
Write-Host ''
if ($foundryOk) { Write-Host '  [OK] Microsoft Foundry Local is installed.' -ForegroundColor Green }
else            { Write-Host '  [--] Microsoft Foundry Local not found.' -ForegroundColor DarkGray }
if ($lmsOk)     { Write-Host '  [OK] LM Studio is installed.' -ForegroundColor Green }
else            { Write-Host '  [--] LM Studio not found.' -ForegroundColor DarkGray }
Write-Host ''

# ── Ask user which backend to set up ──

# If launched from run_stream.ps1, the backend choice is passed via env var
if ($env:STREAMBENCH_AI_BACKEND) {
    $aiChoice = switch ($env:STREAMBENCH_AI_BACKEND.ToLower()) {
        'foundry'  { '1' }
        'lmstudio' { '2' }
        default    { '3' }
    }
    $aiChoiceName = switch ($aiChoice) { '1' { 'Foundry Local' } '2' { 'LM Studio' } '3' { 'Both' } }
    Write-Host "  AI backend pre-selected by launcher: $aiChoiceName" -ForegroundColor DarkGray
} else {
    Write-Host '  Select AI backend for StreamBench:' -ForegroundColor Cyan
    Write-Host '    [1] Microsoft Foundry Local  (Windows/macOS, NPU/GPU/CPU support)' -ForegroundColor White
    Write-Host '    [2] LM Studio               (Windows/macOS/Linux, GPU/CPU)' -ForegroundColor White
    Write-Host '    [3] Both' -ForegroundColor White
    Write-Host '    [4] Skip AI setup' -ForegroundColor DarkGray
    Write-Host ''

    $aiChoice = Read-Host '  Enter choice (1-4)'
    if ([string]::IsNullOrWhiteSpace($aiChoice)) {
        if ($foundryOk -and $lmsOk) { $aiChoice = '3' }
        elseif ($foundryOk)         { $aiChoice = '1' }
        elseif ($lmsOk)             { $aiChoice = '2' }
        else                        { $aiChoice = '3' }
    }
}

$setupFoundry   = $aiChoice -in @('1', '3')
$setupLmStudio  = $aiChoice -in @('2', '3')

# ── Foundry Local Setup ──

if ($setupFoundry) {
    Write-Host ''
    Write-Host '  -- Foundry Local Setup --' -ForegroundColor Cyan
    if ($foundryOk) {
        Write-Host '  [OK] Microsoft Foundry Local CLI is installed.' -ForegroundColor Green
        try {
            $statusOutput = & $foundryCmd service status 2>&1
            $statusStr = ($statusOutput | Out-String)
            if ($statusStr -match 'running') {
                Write-Host '  [OK] Foundry Local service is running.' -ForegroundColor Green
            } else {
                Write-Host '  [!] Foundry Local CLI found but service not running.' -ForegroundColor Yellow
                Write-Host '      It will start automatically when needed.' -ForegroundColor DarkGray
            }
        } catch {
            Write-Host '  [!] Could not check Foundry Local service status.' -ForegroundColor Yellow
        }

        # Check for cached models
        try {
            $cacheOutput = & $foundryCmd cache list 2>&1 | Out-String
            $hasCachedModels = ($cacheOutput -match 'phi-|qwen|deepseek|gpt-')
            if ($hasCachedModels) {
                Write-Host '  [OK] AI models are cached and ready.' -ForegroundColor Green
            } else {
                Write-Host '  No cached AI models found. Bootstrapping catalog & default model...' -ForegroundColor Yellow

                # Download execution providers (first-time, up to 5 min)
                Write-Host '  Downloading execution providers (first run only)...' -ForegroundColor Cyan
                $epStart = Get-Date
                $epJob = Start-Job -ScriptBlock { param($cmd) & $cmd model list 2>&1 } -ArgumentList $foundryCmd
                while ($epJob.State -eq 'Running') {
                    if ([int]((Get-Date) - $epStart).TotalSeconds -ge 300) { break }
                    Write-Host '.' -NoNewline -ForegroundColor Cyan
                    Start-Sleep -Seconds 5
                }
                Write-Host ''
                if ($epJob.State -eq 'Running') {
                    Write-Host '  [!] EP download timed out (non-fatal).' -ForegroundColor Yellow
                    $epJob | Stop-Job -PassThru | Remove-Job -Force
                } else {
                    Remove-Job $epJob -Force
                    $epSec = [int]((Get-Date) - $epStart).TotalSeconds
                    Write-Host "  Execution providers ready in ${epSec}s." -ForegroundColor DarkGray
                }

                # Download default model phi-3.5-mini (up to 10 min)
                Write-Host '  Downloading default AI model (phi-3.5-mini)...' -ForegroundColor Cyan
                Write-Host '  (This may take several minutes on first run.)' -ForegroundColor Yellow
                $dlStart = Get-Date
                $dlJob = Start-Job -ScriptBlock { param($cmd) & $cmd model download phi-3.5-mini 2>&1 } -ArgumentList $foundryCmd
                while ($dlJob.State -eq 'Running') {
                    if ([int]((Get-Date) - $dlStart).TotalSeconds -ge 600) { break }
                    Write-Host '.' -NoNewline -ForegroundColor Cyan
                    Start-Sleep -Seconds 5
                }
                Write-Host ''
                if ($dlJob.State -eq 'Running') {
                    Write-Host '  [!] Model download timed out after 10 min (non-fatal).' -ForegroundColor Yellow
                    $dlJob | Stop-Job -PassThru | Remove-Job -Force
                } else {
                    $dlOutput = Receive-Job $dlJob 2>&1 | Out-String
                    Remove-Job $dlJob -Force
                    $dlSec = [int]((Get-Date) - $dlStart).TotalSeconds
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "  [OK] Default model (phi-3.5-mini) downloaded in ${dlSec}s." -ForegroundColor Green
                    } else {
                        Write-Host "  [!] Model download may have issues (non-fatal). Try: foundry model run phi-3.5-mini" -ForegroundColor Yellow
                    }
                }
            }
        } catch {
            Write-Host '  [!] Could not check cached models (non-fatal).' -ForegroundColor Yellow
        }
    } else {
        # Foundry not installed — attempt install
        if ($hasWinget) {
            Write-Host '  Installing Microsoft Foundry Local via winget...' -ForegroundColor Yellow
            winget install Microsoft.FoundryLocal --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
            if ($LASTEXITCODE -eq 0) {
                Write-Host '  [OK] Foundry Local installed.' -ForegroundColor Green
                $msixDir = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'
                $foundryOk = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                             [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
                if (-not $foundryOk) {
                    foreach ($name in @('foundry.exe', 'foundrylocal.exe')) {
                        if (Test-Path (Join-Path $msixDir $name)) { $foundryOk = $true; break }
                    }
                    if ($foundryOk -and ($env:PATH -notlike "*$msixDir*")) {
                        $env:PATH = "$msixDir;$env:PATH"
                    }
                }
                if ($foundryOk) {
                    $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
                    Write-Host '  Bootstrapping execution providers...' -ForegroundColor Cyan
                    $epStart2 = Get-Date
                    $epJob2 = Start-Job -ScriptBlock { param($cmd) & $cmd model list 2>&1 } -ArgumentList $foundryCmd
                    while ($epJob2.State -eq 'Running') {
                        if ([int]((Get-Date) - $epStart2).TotalSeconds -ge 300) { break }
                        Write-Host '.' -NoNewline -ForegroundColor Cyan
                        Start-Sleep -Seconds 5
                    }
                    Write-Host ''
                    if ($epJob2.State -eq 'Running') {
                        $epJob2 | Stop-Job -PassThru | Remove-Job -Force
                    } else {
                        Remove-Job $epJob2 -Force
                    }
                }
            } else {
                Write-Host '  [!] Foundry install may have failed (non-fatal -- AI is optional).' -ForegroundColor Yellow
                Write-Host '      Install manually: winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
            }
        } elseif ($IsMacOS -and (Get-Command brew -ErrorAction SilentlyContinue)) {
            Write-Host '  Installing Microsoft Foundry Local via Homebrew...' -ForegroundColor Yellow
            & brew tap microsoft/foundrylocal 2>&1 | Out-Null
            & brew install foundrylocal
            if ($LASTEXITCODE -eq 0) {
                Write-Host '  [OK] Foundry Local installed.' -ForegroundColor Green
                Refresh-SetupPath
                $foundryOk = [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                             [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
                if (-not $foundryOk) {
                    foreach ($dir in @('/opt/homebrew/bin', '/usr/local/bin')) {
                        foreach ($name in @('foundry', 'foundrylocal')) {
                            $fullPath = Join-Path $dir $name
                            if (Test-Path $fullPath) {
                                $foundryOk = $true
                                $foundryCmd = $fullPath
                                break
                            }
                        }
                        if ($foundryOk) { break }
                    }
                }
                if ($foundryOk -and -not $foundryCmd) {
                    $foundryCmd = if (Get-Command foundry -ErrorAction SilentlyContinue) { 'foundry' } else { 'foundrylocal' }
                }
                if ($foundryOk) {
                    Write-Host '  Bootstrapping execution providers...' -ForegroundColor Cyan
                    $epStart2 = Get-Date
                    $epJob2 = Start-Job -ScriptBlock { param($cmd) & $cmd model list 2>&1 } -ArgumentList $foundryCmd
                    while ($epJob2.State -eq 'Running') {
                        if ([int]((Get-Date) - $epStart2).TotalSeconds -ge 300) { break }
                        Write-Host '.' -NoNewline -ForegroundColor Cyan
                        Start-Sleep -Seconds 5
                    }
                    Write-Host ''
                    if ($epJob2.State -eq 'Running') {
                        $epJob2 | Stop-Job -PassThru | Remove-Job -Force
                    } else {
                        Remove-Job $epJob2 -Force
                    }
                }
            } else {
                Write-Host '  [!] Foundry install may have failed (non-fatal -- AI is optional).' -ForegroundColor Yellow
                Write-Host '      Install manually: brew tap microsoft/foundrylocal && brew install foundrylocal' -ForegroundColor Yellow
            }
        } else {
            Write-Host '  [!] Package manager not available. Install Foundry manually:' -ForegroundColor Yellow
            if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
                Write-Host '      winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
            } elseif ($IsMacOS) {
                Write-Host '      brew tap microsoft/foundrylocal && brew install foundrylocal' -ForegroundColor Yellow
            }
        }
    }
}

# ── LM Studio Setup ──

if ($setupLmStudio) {
    Write-Host ''
    Write-Host '  -- LM Studio Setup --' -ForegroundColor Cyan
    if ($lmsOk) {
        Write-Host "  [OK] LM Studio CLI found at: $lmsCmd" -ForegroundColor Green

        # Check if server is running by probing the default port
        $lmsRunning = $false
        try {
            $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:1234/v1/models' -TimeoutSec 3 -ErrorAction Stop
            $lmsRunning = $true
            Write-Host '  [OK] LM Studio server is running on port 1234.' -ForegroundColor Green
        } catch {
            Write-Host '  [--] LM Studio server is not running (will start automatically when needed).' -ForegroundColor DarkGray
        }

        # Check loaded models
        if ($lmsRunning) {
            try {
                $models = ($resp.Content | ConvertFrom-Json).data
                if ($models.Count -gt 0) {
                    Write-Host "  [OK] $($models.Count) model(s) loaded in LM Studio:" -ForegroundColor Green
                    foreach ($m in $models) {
                        Write-Host "       - $($m.id)" -ForegroundColor DarkGray
                    }
                } else {
                    Write-Host '  [!] No models loaded. Load a model in LM Studio before running AI benchmark.' -ForegroundColor Yellow
                    Write-Host '      Popular models: phi-3.5-mini-instruct, qwen2.5-0.5b, gemma-2b (GGUF format)' -ForegroundColor DarkGray
                }
            } catch {
                Write-Host '  [!] Could not query loaded models.' -ForegroundColor Yellow
            }
        }
    } else {
        # LM Studio not installed — attempt auto-install
        if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
            if ($hasWinget) {
                Write-Host '  Installing LM Studio via winget...' -ForegroundColor Yellow
                winget install ElementLabs.LMStudio --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source winget
                if ($LASTEXITCODE -eq 0) {
                    Write-Host '  [OK] LM Studio installed.' -ForegroundColor Green

                    # Refresh PATH from registry so newly-installed commands are visible
                    $machinePath = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
                    $userPath    = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
                    $env:PATH    = "$machinePath;$userPath"

                    # Re-detect lms CLI after install
                    $lmsOk = [bool](Get-Command lms -ErrorAction SilentlyContinue)
                    if (-not $lmsOk) {
                        $lmsWellKnown = @(
                            (Join-Path $env:USERPROFILE '.lmstudio\bin\lms.exe'),
                            (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\app\.webpack\lms.exe'),
                            (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\bin\lms.exe'),
                            (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\lms.exe')
                        )
                        foreach ($p in $lmsWellKnown) {
                            if (Test-Path $p) {
                                $lmsOk = $true
                                $lmsCmd = $p
                                $lmsDir = Split-Path $p -Parent
                                if ($env:PATH -notlike "*$lmsDir*") {
                                    $env:PATH = "$lmsDir;$env:PATH"
                                }
                                break
                            }
                        }
                    } else {
                        $lmsCmd = 'lms'
                    }
                    # Fallback: recursive search in the LM Studio install directory
                    if (-not $lmsOk) {
                        $lmInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\LM Studio'
                        if (Test-Path $lmInstallDir) {
                            $lmsExe = Get-ChildItem -Path $lmInstallDir -Filter 'lms.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                            if ($lmsExe) {
                                $lmsOk = $true
                                $lmsCmd = $lmsExe.FullName
                                $lmsDir = $lmsExe.DirectoryName
                                if ($env:PATH -notlike "*$lmsDir*") {
                                    $env:PATH = "$lmsDir;$env:PATH"
                                }
                                Write-Host "  [OK] LM Studio CLI found via search: $lmsCmd" -ForegroundColor Green
                            }
                        }
                    }
                    if ($lmsOk) {
                        Write-Host "  [OK] LM Studio CLI available at: $lmsCmd" -ForegroundColor Green
                    } else {
                        Write-Host '  [!] LM Studio installed but CLI not found on PATH.' -ForegroundColor Yellow
                        Write-Host '      Open LM Studio once to register the CLI, then re-run setup.' -ForegroundColor DarkGray
                    }
                } else {
                    Write-Host '  [!] LM Studio install may have failed (non-fatal -- AI is optional).' -ForegroundColor Yellow
                    Write-Host '      Install manually: winget install ElementLabs.LMStudio' -ForegroundColor Yellow
                    Write-Host '      Or download from https://lmstudio.ai' -ForegroundColor DarkGray
                }
            } else {
                Write-Host '  [!] winget not available. Install LM Studio manually:' -ForegroundColor Yellow
                Write-Host '      winget install ElementLabs.LMStudio' -ForegroundColor Yellow
                Write-Host '      Or download from https://lmstudio.ai' -ForegroundColor DarkGray
            }
        } elseif ($IsMacOS) {
            if (Get-Command brew -ErrorAction SilentlyContinue) {
                Write-Host '  Installing LM Studio via Homebrew...' -ForegroundColor Yellow
                brew install --cask lm-studio
                if ($LASTEXITCODE -eq 0) {
                    Write-Host '  [OK] LM Studio installed.' -ForegroundColor Green
                    # Re-detect
                    $lmsCheck = Join-Path $HOME '.lmstudio/bin/lms'
                    if (Test-Path $lmsCheck) { $lmsOk = $true; $lmsCmd = $lmsCheck }
                    elseif (Get-Command lms -ErrorAction SilentlyContinue) { $lmsOk = $true; $lmsCmd = 'lms' }
                } else {
                    Write-Host '  [!] LM Studio install may have failed (non-fatal -- AI is optional).' -ForegroundColor Yellow
                    Write-Host '      Install manually: brew install --cask lm-studio' -ForegroundColor Yellow
                }
            } else {
                Write-Host '  [!] Homebrew not available. Install LM Studio manually:' -ForegroundColor Yellow
                Write-Host '      brew install --cask lm-studio' -ForegroundColor Yellow
                Write-Host '      Or download from https://lmstudio.ai' -ForegroundColor DarkGray
            }
        } else {
            Write-Host '  [!] Automatic LM Studio install is not supported on Linux.' -ForegroundColor Yellow
            Write-Host '      Download AppImage from https://lmstudio.ai' -ForegroundColor DarkGray
        }
        if ($lmsOk) {
            Write-Host ''
            # Download a default model if none are present (similar to Foundry bootstrap)
            $hasModels = $false
            try {
                $lsOutput = & $lmsCmd ls 2>&1 | Out-String
                if ($lsOutput -match 'phi-|qwen|llama|gemma|deepseek') { $hasModels = $true }
            } catch {}

            if (-not $hasModels) {
                Write-Host '  Downloading default AI model (phi-3.5-mini)...' -ForegroundColor Cyan
                Write-Host '  (This may take several minutes on first run.)' -ForegroundColor Yellow
                $dlStart = Get-Date
                $dlJob = Start-Job -ScriptBlock {
                    param($cmd)
                    & $cmd get "lmstudio-community/phi-3.5-mini-instruct-GGUF" --yes 2>&1
                    $LASTEXITCODE  # return exit code from job
                } -ArgumentList $lmsCmd
                while ($dlJob.State -eq 'Running') {
                    if ([int]((Get-Date) - $dlStart).TotalSeconds -ge 600) { break }
                    Write-Host '.' -NoNewline -ForegroundColor Cyan
                    Start-Sleep -Seconds 5
                }
                Write-Host ''
                if ($dlJob.State -eq 'Running') {
                    Write-Host '  [!] Model download timed out after 10 min (non-fatal).' -ForegroundColor Yellow
                    $dlJob | Stop-Job -PassThru | Remove-Job -Force
                } else {
                    $dlOutput = @(Receive-Job $dlJob 2>&1)
                    $jobExit = if ($dlOutput.Count -gt 0) { $dlOutput[-1] } else { 1 }
                    Remove-Job $dlJob -Force
                    $dlSec = [int]((Get-Date) - $dlStart).TotalSeconds
                    if ($jobExit -eq 0) {
                        Write-Host "  [OK] Default model (phi-3.5-mini) downloaded in ${dlSec}s." -ForegroundColor Green
                    } else {
                        Write-Host "  [!] Model download may have failed (non-fatal). Try: lms get phi-3.5-mini --yes" -ForegroundColor Yellow
                    }
                }
            } else {
                Write-Host '  [OK] AI model(s) already present in LM Studio.' -ForegroundColor Green
            }
        }
    }
}

# ── Save AI backend config ──

if ($aiChoice -ne '4') {
    $configBackend = switch ($aiChoice) {
        '1' { 'Foundry' }
        '2' { 'LmStudio' }
        '3' { 'Auto' }
        default { 'Auto' }
    }
    $configPath = Join-Path (Get-Location) 'streambench_ai_config.json'
    $configObj = @{ Backend = $configBackend } | ConvertTo-Json
    Set-Content -Path $configPath -Value $configObj -Encoding UTF8
    Write-Host ''
    Write-Host "  [OK] AI backend preference saved to streambench_ai_config.json ($configBackend)" -ForegroundColor Green
}

if ($aiChoice -eq '4') {
    Write-Host '  Skipping AI setup. Use --ai-backend to configure later.' -ForegroundColor DarkGray
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
    if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
        Write-Host '    .\run_stream.cmd        (recommended Windows launcher; choose memory or memory + AI)'
        Write-Host '    .\run_stream.ps1        (same unified launcher inside PowerShell)'
        Write-Host '    .\run_stream_ai.cmd     (Windows compatibility AI shortcut)'
        Write-Host '    .\run_stream_ai.ps1     (compatibility AI shortcut)'
    } else {
        Write-Host '    pwsh ./run_stream.ps1   (unified launcher; choose memory or memory + AI)'
        Write-Host '    pwsh ./run_stream_ai.ps1  (compatibility AI shortcut)'
        Write-Host ''
        Write-Host '  Or run directly:' -ForegroundColor Cyan
        Write-Host '    ./StreamBench_osx-arm64 --cpu                (CPU benchmark)'
        Write-Host '    ./StreamBench_osx-arm64 --gpu                (GPU benchmark)'
        Write-Host '    ./StreamBench_osx-arm64 --ai                 (AI benchmark)'
    }
} else {
    Write-Host "   Setup finished with $errors issue(s). See messages above." -ForegroundColor Yellow
    Write-Host ''
    if ($IsMacOS) {
        Write-Host '  Resolve the issues above, then re-run: pwsh ./setup.ps1' -ForegroundColor Yellow
    } else {
        Write-Host '  Resolve the issues above, then re-run .\setup.ps1' -ForegroundColor Yellow
    }
}
Write-Host '  ========================================' -ForegroundColor DarkGray
Write-Host ''

# ------------------------------------------------------------------
#  Restore sleep settings (always, even if errors occurred above)
# ------------------------------------------------------------------
if ($sleepPrevented) {
    if ($IsWindows) {
        try { [Win32.PowerMgmt]::SetThreadExecutionState(0x80000000) | Out-Null } catch {}
    } elseif ($caffeinatePid) {
        try { Stop-Process -Id $caffeinatePid -Force -ErrorAction SilentlyContinue } catch {}
    }
}

if ($errors -gt 0) { exit 1 } else { exit 0 }
