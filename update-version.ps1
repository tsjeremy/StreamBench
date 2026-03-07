#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Updates the StreamBench version number everywhere from the single VERSION file.

.DESCRIPTION
    This script is the ONE command to run when bumping the version.
    It updates: VERSION, stream_version.h, stream.c header, stream_gpu.c header,
    README.md download links, and BUILDING.md release link.

    The .csproj reads VERSION at build time via MSBuild, so no manual edit is needed.
    The build scripts also read VERSION automatically.

.PARAMETER NewVersion
    The new version number (e.g., 5.10.21). Do NOT include a "v" prefix.

.EXAMPLE
    .\update-version.ps1 -NewVersion 5.10.21
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$NewVersion
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Read current version
$VersionFile = Join-Path $ScriptDir 'VERSION'
$OldVersion = (Get-Content $VersionFile -Raw).Trim()

if ($OldVersion -eq $NewVersion) {
    Write-Host "Version is already $NewVersion — nothing to do." -ForegroundColor Yellow
    exit 0
}

Write-Host "Updating version: $OldVersion -> $NewVersion" -ForegroundColor Cyan
Write-Host ''

$Updated = 0
$Today = Get-Date -Format 'yyyy/MM/dd'

# ── 1. VERSION file ─────────────────────────────────────────────────────
$path = $VersionFile
Set-Content -Path $path -Value "$NewVersion`n" -NoNewline:$false
Write-Host "  [OK] VERSION" -ForegroundColor Green
$Updated++

# ── 2. stream_version.h ─────────────────────────────────────────────────
$path = Join-Path $ScriptDir 'stream_version.h'
$content = Get-Content $path -Raw
$content = $content -replace '#define STREAM_VERSION ".*"', "#define STREAM_VERSION `"$NewVersion`""
Set-Content -Path $path -Value $content -NoNewline
Write-Host "  [OK] stream_version.h" -ForegroundColor Green
$Updated++

# ── 3. stream.c header comment (line 3) ─────────────────────────────────
$path = Join-Path $ScriptDir 'stream.c'
$lines = Get-Content $path
$lines[2] = $lines[2] -replace '\$Id: stream\.c,v [\d.]+ \d{4}/\d{2}/\d{2}', "`$Id: stream.c,v $NewVersion $Today"
Set-Content -Path $path -Value $lines
Write-Host "  [OK] stream.c (header comment)" -ForegroundColor Green
$Updated++

# ── 4. stream_gpu.c header comment (line 3) ──────────────────────────────
$path = Join-Path $ScriptDir 'stream_gpu.c'
$lines = Get-Content $path
$lines[2] = $lines[2] -replace '\$Id: stream_gpu\.c,v [\d.]+ \d{4}/\d{2}/\d{2}', "`$Id: stream_gpu.c,v $NewVersion $Today"
Set-Content -Path $path -Value $lines
Write-Host "  [OK] stream_gpu.c (header comment)" -ForegroundColor Green
$Updated++

# ── 5. README.md — replace all version references ────────────────────────
$path = Join-Path $ScriptDir 'README.md'
if (Test-Path $path) {
    $content = Get-Content $path -Raw
    $content = $content -replace "v$([regex]::Escape($OldVersion))", "v$NewVersion"
    $content = $content -replace "StreamBench_v$([regex]::Escape($OldVersion))", "StreamBench_v$NewVersion"
    Set-Content -Path $path -Value $content -NoNewline
    Write-Host "  [OK] README.md" -ForegroundColor Green
    $Updated++
}

# ── 6. BUILDING.md — replace release link version ────────────────────────
$path = Join-Path $ScriptDir 'BUILDING.md'
if (Test-Path $path) {
    $content = Get-Content $path -Raw
    $content = $content -replace 'releases/tag/v[\d.]+', "releases/tag/v$NewVersion"
    Set-Content -Path $path -Value $content -NoNewline
    Write-Host "  [OK] BUILDING.md" -ForegroundColor Green
    $Updated++
}

Write-Host ''
Write-Host "Updated $Updated files from $OldVersion to $NewVersion." -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Add a changelog entry to HISTORY.txt"
Write-Host "  2. Commit:  git add -A && git commit -m `"Bump version to $NewVersion`""
Write-Host "  3. Tag:     git tag v$NewVersion"
Write-Host "  4. Push:    git push origin main v$NewVersion"
