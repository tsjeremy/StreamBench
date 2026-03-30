#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - AI Compatibility Launcher
# ============================================================
# Legacy shortcut that forwards to run_stream.ps1 with AI mode
# preselected. New users should prefer run_stream.ps1 or
# run_stream.cmd, which present a simple mode-selection prompt.
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$launcher = Join-Path $ScriptDir 'run_stream.ps1'

if (-not (Test-Path $launcher)) {
    Write-Host "  [ERROR] Unified launcher not found: $launcher" -ForegroundColor Red
    exit 1
}

. $launcher
Write-Host ''
Write-Host '  [!] AI compatibility shortcut selected. If cached-only mode is enabled, uncached models will be skipped rather than downloaded.' -ForegroundColor Yellow
exit (Invoke-StreamBenchLauncher -SelectedMode 'ai')
