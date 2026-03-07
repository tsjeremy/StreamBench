#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Build for macOS (x64 and ARM64)
# ============================================================
# Compiles stream.c and stream_gpu.c for Intel and Apple Silicon,
# then builds the StreamBench .NET frontend.
#
# Output files:
#   stream_cpu_macos_x64        stream_gpu_macos_x64
#   stream_cpu_macos_arm64      stream_gpu_macos_arm64
#   StreamBench_osx-arm64       StreamBench_osx-x64
#
# Prerequisites:
#   xcode-select --install
#   brew install libomp
#
# Run:  pwsh ./build_all_macos.ps1
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
#  Detect libomp
# ------------------------------------------------------------------
$OmpPrefix = $null
if (Test-Path '/opt/homebrew/opt/libomp') { $OmpPrefix = '/opt/homebrew/opt/libomp' }
elseif (Test-Path '/usr/local/opt/libomp') { $OmpPrefix = '/usr/local/opt/libomp' }

if ($OmpPrefix) {
    Write-Host "Found libomp at: $OmpPrefix"
    $OmpFlags = "-Xpreprocessor -fopenmp -I$OmpPrefix/include -L$OmpPrefix/lib -lomp"
} else {
    Write-Host 'WARNING: libomp not found. CPU builds will be single-threaded.' -ForegroundColor Yellow
    Write-Host '  Install with: brew install libomp'
    $OmpFlags = ''
}

$CpuDefs = '-DTUNED -DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100'
$GpuDefs = '-DSTREAM_ARRAY_SIZE=200000000 -DNTIMES=100'

# ------------------------------------------------------------------
#  Build matrix
# ------------------------------------------------------------------
$builds = @(
    @{ Arch='x86_64'; Tag='x64';   Type='cpu'; Src='stream.c';     Extra="-lm"; UseOmp=$true  },
    @{ Arch='x86_64'; Tag='x64';   Type='gpu'; Src='stream_gpu.c'; Extra="-lm"; UseOmp=$false },
    @{ Arch='arm64';  Tag='arm64'; Type='cpu'; Src='stream.c';     Extra="-lm"; UseOmp=$true  },
    @{ Arch='arm64';  Tag='arm64'; Type='gpu'; Src='stream_gpu.c'; Extra="-lm"; UseOmp=$false }
)

$currentTag = ''
foreach ($b in $builds) {
    if ($b.Tag -ne $currentTag) {
        if ($currentTag) { Write-Host '' }
        $label = if ($b.Tag -eq 'x64') { 'x64 (Intel)' } else { 'ARM64 (Apple Silicon)' }
        Write-Host '============================================================'
        Write-Host " Building for $label"
        Write-Host '============================================================'
        $currentTag = $b.Tag
    }

    $defs  = if ($b.Type -eq 'cpu') { $CpuDefs } else { $GpuDefs }
    $out   = "stream_$($b.Type)_macos_$($b.Tag)"

    if ($b.UseOmp -and $OmpFlags) {
        $cmd = "clang -arch $($b.Arch) -O2 $OmpFlags $defs -o $out $($b.Src) $($b.Extra)"
        bash -c $cmd 2>$null
        if ($LASTEXITCODE -ne 0 -and $b.Arch -eq 'x86_64') {
            # x64 cross-link with arm64 libomp may fail; fall back to single-threaded
            $cmd = "clang -arch $($b.Arch) -O2 $defs -o $out $($b.Src) $($b.Extra)"
            bash -c $cmd 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[OK] $out (single-threaded; arm64 libomp cannot cross-link to x86_64)" -ForegroundColor Green
                continue
            }
        }
    } else {
        $cmd = "clang -arch $($b.Arch) -O2 $defs -o $out $($b.Src) $($b.Extra)"
        bash -c $cmd 2>$null
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] $out" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $out" -ForegroundColor Red
        $Errors++
    }
}
Write-Host ''

# ------------------------------------------------------------------
#  Summary
# ------------------------------------------------------------------
Write-Host '============================================================'
Write-Host ' Build Summary'
Write-Host '============================================================'
foreach ($b in $builds) {
    $f = "stream_$($b.Type)_macos_$($b.Tag)"
    if (Test-Path (Join-Path $ScriptDir $f)) { Write-Host "  [x] $f" }
}
Write-Host ''

if ($Errors -gt 0) {
    Write-Host "$Errors build(s) failed." -ForegroundColor Red
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

dotnet build "$ScriptDir/StreamBench/StreamBench.csproj" --configuration Release -p:Version=$Version --nologo -v quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host '[OK] StreamBench (.NET)' -ForegroundColor Green
} else {
    Write-Host '[FAIL] StreamBench (.NET)' -ForegroundColor Red
    $Errors++; Pop-Location; exit 1
}

# ------------------------------------------------------------------
#  Publish self-contained with embedded backends
# ------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host ' Publishing StreamBench (self-contained with embedded backends)'
Write-Host '============================================================'

$nativeTag = if ((uname -m) -eq 'arm64') { 'arm64' } else { 'x64' }

foreach ($tag in @('arm64','x64')) {
    $rid = "osx-$tag"
    $backendDir = Join-Path $ScriptDir 'StreamBench/backends'
    if (Test-Path $backendDir) { Remove-Item $backendDir -Recurse -Force }
    New-Item -ItemType Directory -Path $backendDir -Force | Out-Null

    foreach ($type in @('cpu','gpu')) {
        $src = Join-Path $ScriptDir "stream_${type}_macos_${tag}"
        if (Test-Path $src) { Copy-Item $src $backendDir }
    }

    dotnet publish "$ScriptDir/StreamBench/StreamBench.csproj" `
        -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:Version=$Version --nologo -v quiet `
        -o "$ScriptDir/publish/$rid"

    if ($LASTEXITCODE -eq 0) {
        $outBin = "StreamBench_osx-$tag"
        Copy-Item "$ScriptDir/publish/$rid/StreamBench" "$ScriptDir/$outBin"
        chmod +x "$ScriptDir/$outBin"
        Write-Host "[OK] $outBin (with embedded CPU + GPU backends)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] StreamBench_osx-$tag" -ForegroundColor Red
        $Errors++
    }

    Remove-Item $backendDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "  Run:  ./StreamBench_osx-$nativeTag --cpu"
Write-Host "        ./StreamBench_osx-$nativeTag --gpu"
Write-Host '  Or:   pwsh ./run_stream.ps1    (memory-only, auto-detect architecture)'
Write-Host '        pwsh ./run_stream_ai.ps1 (memory + AI, auto-detect architecture)'

Write-Host ''
if ($Errors -gt 0) {
    Write-Host "$Errors build(s) failed." -ForegroundColor Red
    Pop-Location; exit 1
}
Write-Host 'All builds succeeded!'
Pop-Location
