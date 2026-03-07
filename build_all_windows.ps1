#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Build for Windows x64 and ARM64
# ============================================================
# Compiles stream.c and stream_gpu.c for both x64 and ARM64
# using MSVC (cl.exe), then builds the StreamBench .NET frontend.
#
# Output files:
#   stream_cpu_win_x64.exe        stream_gpu_win_x64.exe
#   stream_cpu_win_arm64.exe      stream_gpu_win_arm64.exe
#   StreamBench_win_x64.exe       StreamBench_win_arm64.exe
#
# Run from any terminal:  .\build_all_windows.ps1
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $ScriptDir

# Read version from the single source of truth
$VersionFile = Join-Path $ScriptDir 'VERSION'
if (Test-Path $VersionFile) {
    $Version = (Get-Content $VersionFile -Raw).Trim()
    Write-Host "Version: $Version (from VERSION file)"
} else {
    $Version = '0.0.0-dev'
    Write-Host "WARNING: VERSION file not found, using $Version" -ForegroundColor Yellow
}

$Errors = 0

# ------------------------------------------------------------------
#  Find vcvarsall.bat
# ------------------------------------------------------------------
function Find-VcVarsAll {
    $editions = @('Enterprise','Professional','Community','BuildTools')
    $versions = @('2022','2025','18')
    $roots    = @($env:ProgramFiles, ${env:ProgramFiles(x86)})

    foreach ($ver in $versions) {
        foreach ($ed in $editions) {
            foreach ($root in $roots) {
                if (-not $root) { continue }
                $candidate = Join-Path $root "Microsoft Visual Studio\$ver\$ed\VC\Auxiliary\Build\vcvarsall.bat"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }

    # Fallback: vswhere
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -property installationPath 2>$null
        if ($installPath) {
            $candidate = Join-Path $installPath 'VC\Auxiliary\Build\vcvarsall.bat'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    return $null
}

$vcvarsall = Find-VcVarsAll
if (-not $vcvarsall) {
    Write-Host 'ERROR: Could not find vcvarsall.bat. Install Visual Studio with C++ workload.' -ForegroundColor Red
    Write-Host '  winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"'
    Pop-Location; exit 1
}
Write-Host "Found vcvarsall.bat: $vcvarsall"
Write-Host ''

# ------------------------------------------------------------------
#  Build options
# ------------------------------------------------------------------
$CpuOpts = '/O2 /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /openmp'
$CpuLibs = 'advapi32.lib'
$GpuOpts = '/O2 /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100'
$GpuLibs = 'advapi32.lib'

# Helper: invoke cl.exe inside a vcvarsall environment (with version resource)
function Invoke-Cl {
    param([string]$Arch, [string]$Opts, [string]$OutExe, [string]$Source, [string]$Libs)
    # Compile the version resource, then compile+link the C source with it
    $resFile = [IO.Path]::ChangeExtension($OutExe, '.res')
    $cmd = "`"$vcvarsall`" $Arch >nul 2>&1 && rc.exe /nologo /fo `"$ScriptDir\$resFile`" `"$ScriptDir\stream_version.rc`" && cl.exe $Opts /Fe:`"$ScriptDir\$OutExe`" `"$ScriptDir\$Source`" `"$ScriptDir\$resFile`" /link $Libs"
    cmd /c $cmd
    Remove-Item "$ScriptDir\$resFile" -ErrorAction SilentlyContinue
    return $LASTEXITCODE -eq 0
}

# ------------------------------------------------------------------
#  C Builds (x64 + ARM64)
# ------------------------------------------------------------------

$archGroups = @(
    @{ Arch='x64';       Label='x64 (AMD64)';                      Items=@(
        @{ Opts=$CpuOpts; Out='stream_cpu_win_x64.exe';    Src='stream.c';     Libs=$CpuLibs },
        @{ Opts=$GpuOpts; Out='stream_gpu_win_x64.exe';    Src='stream_gpu.c'; Libs=$GpuLibs }
    )},
    @{ Arch='x64_arm64'; Label='ARM64 (cross-compile from x64)';   Items=@(
        @{ Opts=$CpuOpts; Out='stream_cpu_win_arm64.exe';  Src='stream.c';     Libs=$CpuLibs },
        @{ Opts=$GpuOpts; Out='stream_gpu_win_arm64.exe';  Src='stream_gpu.c'; Libs=$GpuLibs }
    )}
)

$builds = @()
foreach ($group in $archGroups) {
    Write-Host '============================================================'
    Write-Host " Building for $($group.Label)"
    Write-Host '============================================================'
    foreach ($b in $group.Items) {
        $builds += @{ Out = $b.Out }
        if (Invoke-Cl -Arch $group.Arch -Opts $b.Opts -OutExe $b.Out -Source $b.Src -Libs $b.Libs) {
            Write-Host "[OK] $($b.Out)" -ForegroundColor Green
        } else {
            Write-Host "[FAIL] $($b.Out)" -ForegroundColor Red
            $Errors++
        }
    }
    Write-Host ''
}
Write-Host ''

# ------------------------------------------------------------------
#  Summary
# ------------------------------------------------------------------
Write-Host '============================================================'
Write-Host ' Build Summary'
Write-Host '============================================================'
foreach ($b in $builds) {
    if (Test-Path (Join-Path $ScriptDir $b.Out)) {
        Write-Host "  [x] $($b.Out)"
    }
}
Write-Host ''
Write-Host '  Note: CPU builds require Visual C++ Redistributable on the target machine.'
Write-Host '        Use run_stream.ps1 to auto-detect and install if needed.'
Write-Host ''

# Cleanup obj files
Remove-Item "$ScriptDir\stream.obj","$ScriptDir\stream_gpu.obj" -ErrorAction SilentlyContinue

if ($Errors -gt 0) {
    Write-Host "$Errors C build(s) failed." -ForegroundColor Red
    Pop-Location; exit 1
}
Write-Host 'All C builds succeeded!'

# ------------------------------------------------------------------
#  .NET 10 Frontend Build
# ------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host ' Building StreamBench (.NET 10 frontend)'
Write-Host '============================================================'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "WARNING: 'dotnet' not found. Skipping StreamBench build." -ForegroundColor Yellow
    Write-Host '  Install .NET 10 SDK from: https://dot.net'
    Pop-Location; exit 0
}

dotnet build "$ScriptDir\StreamBench\StreamBench.csproj" --configuration Release -p:Version=$Version --nologo -v quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host '[OK] StreamBench (.NET)' -ForegroundColor Green
} else {
    Write-Host '[FAIL] StreamBench (.NET)' -ForegroundColor Red
    $Errors++
    Pop-Location; exit 1
}

# ------------------------------------------------------------------
#  Publish self-contained single-file executables with embedded backends
# ------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host ' Publishing StreamBench (self-contained with embedded backends)'
Write-Host '============================================================'

$publishTargets = @(
    @{ Rid='win-x64';   Tag='x64';   CpuBin='stream_cpu_win_x64.exe';   GpuBin='stream_gpu_win_x64.exe'   },
    @{ Rid='win-arm64'; Tag='arm64'; CpuBin='stream_cpu_win_arm64.exe'; GpuBin='stream_gpu_win_arm64.exe' }
)

foreach ($t in $publishTargets) {
    $backendDir = Join-Path $ScriptDir 'StreamBench\backends'
    if (Test-Path $backendDir) { Remove-Item $backendDir -Recurse -Force }
    New-Item -ItemType Directory -Path $backendDir -Force | Out-Null

    $cpuSrc = Join-Path $ScriptDir $t.CpuBin
    $gpuSrc = Join-Path $ScriptDir $t.GpuBin
    if (Test-Path $cpuSrc) { Copy-Item $cpuSrc $backendDir }
    if (Test-Path $gpuSrc) { Copy-Item $gpuSrc $backendDir }

    # Standard (memory-only) publish
    dotnet publish "$ScriptDir\StreamBench\StreamBench.csproj" `
        -c Release -r $t.Rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version `
        -o "$ScriptDir\publish\$($t.Rid)" --nologo -v quiet

    if ($LASTEXITCODE -eq 0) {
        $outExe = "StreamBench_win_$($t.Tag).exe"
        Copy-Item "$ScriptDir\publish\$($t.Rid)\StreamBench.exe" "$ScriptDir\$outExe"
        Write-Host "[OK] $outExe (with embedded CPU + GPU backends)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] StreamBench_win_$($t.Tag).exe" -ForegroundColor Red
        $Errors++
    }

    # AI-enabled publish (includes Foundry Local + ONNX runtime)
    dotnet publish "$ScriptDir\StreamBench\StreamBench.csproj" `
        -c Release -r $t.Rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:EnableAI=true `
        -p:Version=$Version `
        -o "$ScriptDir\publish\$($t.Rid)-ai" --nologo -v quiet

    if ($LASTEXITCODE -eq 0) {
        $outExeAi = "StreamBench_win_$($t.Tag)_ai.exe"
        Copy-Item "$ScriptDir\publish\$($t.Rid)-ai\StreamBench.exe" "$ScriptDir\$outExeAi"
        Write-Host "[OK] $outExeAi (with embedded CPU + GPU backends + AI)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] StreamBench_win_$($t.Tag)_ai.exe" -ForegroundColor Red
        $Errors++
    }

    Remove-Item $backendDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '  Run:  StreamBench_win_x64.exe --cpu'
Write-Host '        StreamBench_win_x64.exe --gpu'
Write-Host '  Or:   .\run_stream.ps1     (memory-only, auto-detect architecture)'
Write-Host '        .\run_stream_ai.ps1  (memory + AI, auto-detect architecture)'

Write-Host ''
if ($Errors -gt 0) {
    Write-Host "$Errors build(s) failed." -ForegroundColor Red
    Pop-Location; exit 1
}
Write-Host 'All builds succeeded!'
Pop-Location
