#!/usr/bin/env pwsh
# ============================================================
# STREAM Benchmark - Unified Launcher (Cross-platform)
# ============================================================
# Single recommended entrypoint for end users.
#
# Choose between:
#   1. Memory benchmark only
#   2. Memory + AI benchmark
#
# Advanced overrides:
#   $env:STREAMBENCH_LAUNCH_MODE = 'memory' | 'ai'
#   $env:STREAMBENCH_ARRAY_SIZE
#   $env:STREAMBENCH_AI_BACKEND = 'auto' | 'lmstudio' | 'foundry'
#   $env:STREAMBENCH_AI_MODEL
#   $env:STREAMBENCH_AI_DEVICES
#   $env:STREAMBENCH_AI_NO_DOWNLOAD = '1'
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Ensure UTF-8 output for Unicode spinner/box-drawing characters
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$script:StreamBenchCliLogWriter = $null
$script:StreamBenchCliLogPath = $null

function Open-StreamBenchCliLogWriter {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$Append
    )

    $mode = if ($Append) { [System.IO.FileMode]::Append } else { [System.IO.FileMode]::Create }
    $stream = [System.IO.FileStream]::new(
        $Path,
        $mode,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::ReadWrite)
    $writer = [System.IO.StreamWriter]::new(
        $stream,
        [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    return $writer
}

function Start-StreamBenchCliLog {
    param([Parameter(Mandatory)] [string]$BaseDirectory)

    if ($script:StreamBenchCliLogWriter) {
        return $script:StreamBenchCliLogPath
    }

    try {
        $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $path = Join-Path $BaseDirectory "StreamBench_cli_$timestamp.log"
        $writer = Open-StreamBenchCliLogWriter -Path $path
        $writer.WriteLine('# StreamBench CLI Transcript')
        $writer.WriteLine('# Start: {0}' -f [DateTime]::Now.ToString('O'))
        $writer.WriteLine('# WorkingDirectory: {0}' -f (Get-Location).Path)
        $writer.WriteLine()

        $script:StreamBenchCliLogWriter = $writer
        $script:StreamBenchCliLogPath = $path
        $env:STREAMBENCH_CLI_LOG = $path
        return $path
    } catch {
        $script:StreamBenchCliLogWriter = $null
        $script:StreamBenchCliLogPath = $null
        Remove-Item Env:\STREAMBENCH_CLI_LOG -ErrorAction SilentlyContinue
        return $null
    }
}

function Suspend-StreamBenchCliLog {
    if (-not $script:StreamBenchCliLogWriter) {
        return
    }

    $script:StreamBenchCliLogWriter.Dispose()
    $script:StreamBenchCliLogWriter = $null
}

function Resume-StreamBenchCliLog {
    if (-not $script:StreamBenchCliLogPath -or $script:StreamBenchCliLogWriter) {
        return
    }

    $script:StreamBenchCliLogWriter = Open-StreamBenchCliLogWriter -Path $script:StreamBenchCliLogPath -Append
}

function Stop-StreamBenchCliLog {
    try {
        Resume-StreamBenchCliLog
        if ($script:StreamBenchCliLogWriter) {
            $script:StreamBenchCliLogWriter.WriteLine()
            $script:StreamBenchCliLogWriter.WriteLine('# End: {0}' -f [DateTime]::Now.ToString('O'))
            $script:StreamBenchCliLogWriter.Dispose()
        }
    } catch {
    } finally {
        $script:StreamBenchCliLogWriter = $null
        $script:StreamBenchCliLogPath = $null
        Remove-Item Env:\STREAMBENCH_CLI_LOG -ErrorAction SilentlyContinue
    }
}

function Write-StreamBenchCliLog {
    param(
        [AllowNull()] [string]$Text,
        [switch]$NoNewline
    )

    if (-not $script:StreamBenchCliLogWriter) {
        return
    }

    $value = if ($null -eq $Text) { '' } else { $Text }
    if ($NoNewline) {
        $script:StreamBenchCliLogWriter.Write($value)
    } else {
        $script:StreamBenchCliLogWriter.WriteLine($value)
    }
}

function Write-Host {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
        [AllowEmptyCollection()]
        [object[]]$Object,
        [object]$Separator = ' ',
        [ConsoleColor]$ForegroundColor,
        [ConsoleColor]$BackgroundColor,
        [switch]$NoNewline
    )

    $text = [string]::Join(
        [string]$Separator,
        @($Object | ForEach-Object {
            if ($null -eq $_) { '' } else { $_.ToString() }
        }))
    Write-StreamBenchCliLog -Text $text -NoNewline:$NoNewline
    Microsoft.PowerShell.Utility\Write-Host @PSBoundParameters
}

function Read-Host {
    [CmdletBinding()]
    param([Parameter(Position = 0)] [object]$Prompt)

    if ($PSBoundParameters.ContainsKey('Prompt')) {
        $response = Microsoft.PowerShell.Utility\Read-Host $Prompt
        Write-StreamBenchCliLog -Text ("{0} {1}" -f $Prompt, $response)
        return $response
    }

    $response = Microsoft.PowerShell.Utility\Read-Host
    Write-StreamBenchCliLog -Text $response
    return $response
}

function Initialize-StreamBenchPlatformFlags {
    if ($null -eq (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue)) {
        $script:StreamBenchIsWindows = $true
        $script:StreamBenchIsMacOS = $false
        $script:StreamBenchIsLinux = $false
        return
    }

    $script:StreamBenchIsWindows = [bool]$IsWindows
    $script:StreamBenchIsMacOS = [bool]$IsMacOS
    $script:StreamBenchIsLinux = [bool]$IsLinux
}

function Get-StreamBenchPlatformContext {
    Initialize-StreamBenchPlatformFlags

    if ($script:StreamBenchIsWindows) {
        $osTag = 'win'
        try {
            $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        } catch {
            $nativeArch = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE -ErrorAction SilentlyContinue).PROCESSOR_ARCHITECTURE
            if (-not $nativeArch) { $nativeArch = $env:PROCESSOR_ARCHITECTURE }
            $osArch = switch ($nativeArch) {
                'ARM64' { 'Arm64' }
                'AMD64' { 'X64' }
                default { $nativeArch }
            }
        }
        $archTag = if ($osArch -eq 'Arm64') { 'arm64' } else { 'x64' }
        $ext = '.exe'
    } elseif ($script:StreamBenchIsMacOS) {
        $osTag = 'macos'
        $archTag = if ((uname -m) -eq 'arm64') { 'arm64' } else { 'x64' }
        $ext = ''
    } else {
        $osTag = 'linux'
        $archTag = if ((uname -m) -eq 'aarch64') { 'arm64' } else { 'x64' }
        $ext = ''
    }

    return [pscustomobject]@{
        IsWindows = [bool]$script:StreamBenchIsWindows
        IsMacOS = [bool]$script:StreamBenchIsMacOS
        IsLinux = [bool]$script:StreamBenchIsLinux
        OsTag = $osTag
        ArchTag = $archTag
        Ext = $ext
    }
}

function Refresh-StreamBenchPath {
    $machinePath = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
    $userPath = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    $segments = @()
    if (-not [string]::IsNullOrWhiteSpace($machinePath)) { $segments += $machinePath }
    if (-not [string]::IsNullOrWhiteSpace($userPath)) { $segments += $userPath }
    if ($segments.Count -gt 0) {
        $env:PATH = ($segments -join ';')
    }
}

function Start-StreamBenchSleepPrevention {
    param([Parameter(Mandatory)] [object]$Platform)

    if (-not $Platform.IsWindows) {
        return $false
    }

    try {
        if (-not ('Win32.StreamBenchSleepUtil' -as [type])) {
            $memberDefinition = '[System.Runtime.InteropServices.DllImport("kernel32.dll")]' + "`n" +
                'public static extern uint SetThreadExecutionState(uint esFlags);'
            Add-Type -Namespace Win32 -Name StreamBenchSleepUtil -MemberDefinition $memberDefinition -ErrorAction Stop
        }
        [Win32.StreamBenchSleepUtil]::SetThreadExecutionState(0x80000001) | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Stop-StreamBenchSleepPrevention {
    param([bool]$Enabled)

    if ($Enabled) {
        try {
            [Win32.StreamBenchSleepUtil]::SetThreadExecutionState(0x80000000) | Out-Null
        } catch {
        }
    }
}

function Test-StreamBenchInteractiveConsole {
    try {
        return (-not [Console]::IsInputRedirected) -and (-not [Console]::IsOutputRedirected)
    } catch {
        return $Host.Name -eq 'ConsoleHost'
    }
}

function Normalize-StreamBenchMode {
    param(
        [AllowNull()] [string]$Mode,
        [Parameter(Mandatory)] [string]$SourceName
    )

    if ([string]::IsNullOrWhiteSpace($Mode)) {
        return $null
    }

    switch ($Mode.Trim().ToLowerInvariant()) {
        'memory' { return 'memory' }
        'ai' { return 'ai' }
        default {
            Write-Host "  [!] Ignoring invalid $SourceName value '$Mode'. Valid values: memory, ai." -ForegroundColor Yellow
            return $null
        }
    }
}

function Normalize-StreamBenchAiBackend {
    param(
        [AllowNull()] [string]$Backend,
        [Parameter(Mandatory)] [string]$SourceName
    )

    if ([string]::IsNullOrWhiteSpace($Backend)) {
        return $null
    }

    switch ($Backend.Trim().ToLowerInvariant()) {
        'auto' { return 'auto' }
        'lmstudio' { return 'lmstudio' }
        'lm-studio' { return 'lmstudio' }
        'foundry' { return 'foundry' }
        'foundrylocal' { return 'foundry' }
        'foundry-local' { return 'foundry' }
        default {
            Write-Host "  [!] Ignoring invalid $SourceName value '$Backend'. Valid values: auto, lmstudio, foundry." -ForegroundColor Yellow
            return $null
        }
    }
}

function Get-StreamBenchAiBackendLabel {
    param([AllowNull()] [string]$Backend)

    switch ($Backend) {
        'lmstudio' { return 'LM Studio' }
        'foundry' { return 'Foundry Local' }
        default { return 'Auto-detect' }
    }
}

function Get-StreamBenchAiSettings {
    return [pscustomobject]@{
        Backend = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_BACKEND)) { '' } else { $env:STREAMBENCH_AI_BACKEND.Trim() }
        Model = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_MODEL)) { '' } else { $env:STREAMBENCH_AI_MODEL.Trim() }
        Devices = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_AI_DEVICES)) { '' } else { $env:STREAMBENCH_AI_DEVICES.Trim() }
        NoDownload = ($env:STREAMBENCH_AI_NO_DOWNLOAD -eq '1')
    }
}

function Select-StreamBenchMode {
    param(
        [AllowNull()] [string]$SelectedMode,
        [Parameter(Mandatory)] [object]$AiSettings
    )

    $mode = Normalize-StreamBenchMode -Mode $SelectedMode -SourceName 'selected mode'
    if ($mode) {
        return $mode
    }

    $mode = Normalize-StreamBenchMode -Mode $env:STREAMBENCH_LAUNCH_MODE -SourceName 'STREAMBENCH_LAUNCH_MODE'
    if ($mode) {
        return $mode
    }

    if ($AiSettings.Backend -or $AiSettings.Model -or $AiSettings.Devices -or $AiSettings.NoDownload) {
        return 'ai'
    }

    if (-not (Test-StreamBenchInteractiveConsole)) {
        return 'memory'
    }

    Write-Host ''
    Write-Host '  Choose benchmark mode:' -ForegroundColor Cyan
    Write-Host '    1. Memory benchmark only (Recommended)' -ForegroundColor Green
    Write-Host '    2. Memory benchmark + AI benchmark' -ForegroundColor Green
    Write-Host ''

    $choice = Read-Host '  Select 1 or 2 (press Enter for 1)'
    $choiceText = if ($null -eq $choice) { '' } else { $choice.ToString().Trim() }
    if ($choiceText -eq '2') {
        return 'ai'
    }

    return 'memory'
}

function Select-StreamBenchAiBackend {
    param([Parameter(Mandatory)] [object]$AiSettings)

    $backend = Normalize-StreamBenchAiBackend -Backend $AiSettings.Backend -SourceName 'STREAMBENCH_AI_BACKEND'
    if ($backend) {
        return $backend
    }

    if (-not (Test-StreamBenchInteractiveConsole)) {
        return 'auto'
    }

    Write-Host ''
    Write-Host '  Choose AI backend:' -ForegroundColor Cyan
    Write-Host '    1. Auto-detect (Recommended)' -ForegroundColor Green
    Write-Host '    2. LM Studio' -ForegroundColor Green
    Write-Host '    3. Foundry Local' -ForegroundColor Green
    Write-Host ''

    $choice = Read-Host '  Select 1, 2, or 3 (press Enter for 1)'
    $choiceText = if ($null -eq $choice) { '' } else { $choice.ToString().Trim() }
    switch ($choiceText) {
        '2' { return 'lmstudio' }
        '3' { return 'foundry' }
        default { return 'auto' }
    }
}

function Get-StreamBenchArraySize {
    $arraySize = if ([string]::IsNullOrWhiteSpace($env:STREAMBENCH_ARRAY_SIZE)) { '200000000' } else { $env:STREAMBENCH_ARRAY_SIZE.Trim() }
    if ($arraySize -notmatch '^\d+$') {
        Write-Host ''
        Write-Host "  [ERROR] STREAMBENCH_ARRAY_SIZE must be a positive integer, got: $arraySize" -ForegroundColor Red
        return $null
    }
    return $arraySize
}

function Show-StreamBenchLauncherHeader {
    param(
        [Parameter(Mandatory)] [ValidateSet('memory', 'ai')] [string]$Mode,
        [Parameter(Mandatory)] [object]$AiSettings,
        [Parameter(Mandatory)] [string]$ArraySize
    )

    Write-Host ''
    Write-Host '  ========================================' -ForegroundColor DarkGray
    Write-Host '   STREAM Benchmark Launcher' -ForegroundColor Cyan
    Write-Host '  ========================================' -ForegroundColor DarkGray
    Write-Host ''

    if ($Mode -eq 'ai') {
        Write-Host '  [OK] Selected mode: Memory + AI benchmark' -ForegroundColor Green
        Write-Host "  [OK] AI backend: $(Get-StreamBenchAiBackendLabel -Backend $AiSettings.Backend)" -ForegroundColor Green
        Write-Host "  [OK] AI model: $(if ($AiSettings.Model) { $AiSettings.Model } else { '(auto-select)' })" -ForegroundColor Green
        Write-Host "  [OK] AI device(s): $(if ($AiSettings.Devices) { $AiSettings.Devices } else { '(all detected)' })" -ForegroundColor Green
        Write-Host '  [OK] Q3 local summary: auto (when memory JSON exists)' -ForegroundColor Green
    } else {
        Write-Host '  [OK] Selected mode: Memory benchmark only' -ForegroundColor Green
    }

    Write-Host "  [OK] Array size: $ArraySize" -ForegroundColor Green
    Write-Host ''
}

function Test-StreamBenchTcpEndpoint {
    param(
        [Parameter(Mandatory)] [string]$Host,
        [Parameter(Mandatory)] [int]$Port
    )

    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $connectTask = $client.ConnectAsync($Host, $Port)
            if (-not $connectTask.Wait(1000)) {
                return $false
            }
            return $client.Connected
        } finally {
            $client.Dispose()
        }
    } catch {
        return $false
    }
}

function Test-StreamBenchAiBackendAvailable {
    param([AllowNull()] [string]$Backend)

    switch ($Backend) {
        'foundry' {
            return [bool](Get-Command foundry -ErrorAction SilentlyContinue) -or
                   [bool](Get-Command foundrylocal -ErrorAction SilentlyContinue)
        }
        'lmstudio' {
            if ([bool](Get-Command lms -ErrorAction SilentlyContinue)) { return $true }
            if (Test-StreamBenchTcpEndpoint -Host '127.0.0.1' -Port 1234) { return $true }
            # Check well-known CLI paths (matches LmStudioAiBackend.cs FindLmsCli)
            if ($IsWindows -or (-not $PSVersionTable.PSEdition) -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
                $lmsPaths = @(
                    (Join-Path $env:USERPROFILE '.lmstudio\bin\lms.exe'),
                    (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\app\.webpack\lms.exe'),
                    (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\resources\bin\lms.exe'),
                    (Join-Path $env:LOCALAPPDATA 'Programs\LM Studio\lms.exe')
                )
            } else {
                $home = if ($env:HOME) { $env:HOME } else { $env:USERPROFILE }
                $lmsPaths = @(
                    (Join-Path $home '.lmstudio/bin/lms'),
                    '/usr/local/bin/lms'
                )
            }
            foreach ($p in $lmsPaths) {
                if (Test-Path $p) { return $true }
            }
            return $false
        }
        default {
            return (Test-StreamBenchAiBackendAvailable -Backend 'foundry') -or
                   (Test-StreamBenchAiBackendAvailable -Backend 'lmstudio')
        }
    }
}

function Ensure-StreamBenchPrerequisites {
    param(
        [Parameter(Mandatory)] [object]$Platform,
        [bool]$RequireAi,
        [AllowNull()] [string]$AiBackend = $null
    )

    if (-not $Platform.IsWindows) {
        return $true
    }

    $setupNeeded = $false

    if (-not (Test-Path "$env:SystemRoot\System32\vcomp140.dll") -and
        -not (Test-Path "$env:SystemRoot\SysWOW64\vcomp140.dll")) {
        $setupNeeded = $true
    }

    $csprojCheck = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'
    Refresh-StreamBenchPath

    if (Test-Path $csprojCheck) {
        $sdks = $null
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            $sdks = & dotnet --list-sdks 2>$null
        }
        if (-not ($sdks -match '^10\.')) {
            $setupNeeded = $true
        }
    } else {
        $runtimes = $null
        if (Get-Command dotnet -ErrorAction SilentlyContinue) {
            $runtimes = & dotnet --list-runtimes 2>$null
        }
        if (-not ($runtimes -match 'Microsoft\.NETCore\.App 10\.')) {
            $setupNeeded = $true
        }
    }

    if ($RequireAi -and -not (Test-StreamBenchAiBackendAvailable -Backend $AiBackend)) {
        $setupNeeded = $true
    }

    if (-not $setupNeeded) {
        return $true
    }

    $setupScript = Join-Path $ScriptDir 'setup.ps1'
    if (Test-Path $setupScript) {
        Write-Host '  [!] Missing prerequisites detected — running setup.ps1...' -ForegroundColor Yellow
        Write-Host ''
        & $setupScript
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  [FAIL] setup.ps1 finished with errors. Please resolve and re-run.' -ForegroundColor Red
            return $false
        }
        Refresh-StreamBenchPath
        Write-Host ''
        return $true
    }

    Write-Host '  [!] Prerequisites missing and setup.ps1 not found.' -ForegroundColor Yellow
    Write-Host '      Run setup.ps1 first, or install manually:' -ForegroundColor Yellow
    Write-Host "        winget install \"Microsoft.VCRedist.2015+.$($Platform.ArchTag)\"" -ForegroundColor Yellow
    Write-Host '        winget install Microsoft.DotNet.Runtime.10' -ForegroundColor Yellow
    if ($RequireAi) {
        switch ($AiBackend) {
            'foundry' { Write-Host '        winget install Microsoft.FoundryLocal' -ForegroundColor Yellow }
            'lmstudio' {
                Write-Host '        winget install ElementLabs.LMStudio' -ForegroundColor Yellow
                Write-Host '        # or download from https://lmstudio.ai' -ForegroundColor Yellow
            }
            default {
                Write-Host '        winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
                Write-Host '        winget install ElementLabs.LMStudio' -ForegroundColor Yellow
            }
        }
    }
    return $false
}

function Find-StreamBenchExecutable {
    param([string[]]$CandidateNames)

    foreach ($name in $CandidateNames) {
        $candidate = Join-Path $ScriptDir $name
        if (Test-Path $candidate) {
            return [pscustomobject]@{
                Name = $name
                Path = $candidate
            }
        }
    }

    return $null
}

function Resolve-StreamBenchMemoryLaunch {
    param([Parameter(Mandatory)] [object]$Platform)

    if ($Platform.OsTag -eq 'win') {
        $benchNames = @(
            "StreamBench_win-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_win_$($Platform.ArchTag)$($Platform.Ext)"
        )
    } elseif ($Platform.OsTag -eq 'macos') {
        $benchNames = @(
            "StreamBench_osx-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_osx_$($Platform.ArchTag)$($Platform.Ext)"
        )
    } else {
        $benchNames = @(
            "StreamBench_linux-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_linux_$($Platform.ArchTag)$($Platform.Ext)"
        )
    }

    if ($Platform.OsTag -eq 'win' -and $Platform.ArchTag -eq 'arm64') {
        $benchNames += @(
            "StreamBench_win-x64$($Platform.Ext)",
            "StreamBench_win_x64$($Platform.Ext)"
        )
    }

    $bench = Find-StreamBenchExecutable -CandidateNames $benchNames
    $cpuCandidates = @("stream_cpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)")
    $gpuCandidates = @("stream_gpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)")
    if ($Platform.OsTag -eq 'win' -and $Platform.ArchTag -eq 'arm64') {
        $cpuCandidates += "stream_cpu_win_x64$($Platform.Ext)"
        $gpuCandidates += "stream_gpu_win_x64$($Platform.Ext)"
    }
    $cpu = Find-StreamBenchExecutable -CandidateNames $cpuCandidates
    $gpu = Find-StreamBenchExecutable -CandidateNames $gpuCandidates
    $cpuExe = if ($cpu) { $cpu.Path } else { Join-Path $ScriptDir $cpuCandidates[0] }
    $gpuExe = if ($gpu) { $gpu.Path } else { Join-Path $ScriptDir $gpuCandidates[0] }
    $hasCpu = $null -ne $cpu
    $hasGpu = $null -ne $gpu
    $csproj = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'

    if ($bench) {
        return [pscustomobject]@{
            LaunchType = 'self'
            BenchName = $bench.Name
            BenchExe = $bench.Path
            Csproj = $csproj
            HasCpu = $hasCpu
            HasGpu = $hasGpu
            CpuExe = $cpuExe
            GpuExe = $gpuExe
        }
    }

    if (($hasCpu -or $hasGpu) -and (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
        return [pscustomobject]@{
            LaunchType = 'dev'
            BenchName = $null
            BenchExe = $null
            Csproj = $csproj
            HasCpu = $hasCpu
            HasGpu = $hasGpu
            CpuExe = $cpuExe
            GpuExe = $gpuExe
        }
    }

    Write-Host ''
    Write-Host "  [ERROR] StreamBench binary not found: $($benchNames[0])" -ForegroundColor Red
    Write-Host "          Expected in: $ScriptDir" -ForegroundColor Red
    Write-Host ''
    Write-Host '  Download it from:' -ForegroundColor Yellow
    Write-Host '    https://github.com/tsjeremy/StreamBench/releases/latest'
    Write-Host ''
    Write-Host "  Place $($benchNames[0]) in the same folder as this script and re-run."
    return $null
}

function Resolve-StreamBenchAiLaunch {
    param([Parameter(Mandatory)] [object]$Platform)

    $csproj = Join-Path $ScriptDir 'StreamBench\StreamBench.csproj'
    $cpuCandidates = @("stream_cpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)")
    $gpuCandidates = @("stream_gpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)")
    if ($Platform.OsTag -eq 'win' -and $Platform.ArchTag -eq 'arm64') {
        $cpuCandidates += "stream_cpu_win_x64$($Platform.Ext)"
        $gpuCandidates += "stream_gpu_win_x64$($Platform.Ext)"
    }
    $cpu = Find-StreamBenchExecutable -CandidateNames $cpuCandidates
    $gpu = Find-StreamBenchExecutable -CandidateNames $gpuCandidates
    $cpuExe = if ($cpu) { $cpu.Path } else { Join-Path $ScriptDir $cpuCandidates[0] }
    $gpuExe = if ($gpu) { $gpu.Path } else { Join-Path $ScriptDir $gpuCandidates[0] }
    $hasCpu = $null -ne $cpu
    $hasGpu = $null -ne $gpu

    # Priority 1: prebuilt AI executable (end-user scenario)
    if ($Platform.OsTag -eq 'win') {
        $benchNames = @(
            "StreamBench_win_$($Platform.ArchTag)_ai$($Platform.Ext)",
            "StreamBench_win-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_win_$($Platform.ArchTag)$($Platform.Ext)"
        )
    } elseif ($Platform.OsTag -eq 'macos') {
        $benchNames = @(
            "StreamBench_osx_$($Platform.ArchTag)_ai$($Platform.Ext)",
            "StreamBench_osx-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_osx_$($Platform.ArchTag)$($Platform.Ext)"
        )
    } else {
        $benchNames = @(
            "StreamBench_linux_$($Platform.ArchTag)_ai$($Platform.Ext)",
            "StreamBench_linux-$($Platform.ArchTag)$($Platform.Ext)",
            "StreamBench_linux_$($Platform.ArchTag)$($Platform.Ext)"
        )
    }

    if ($Platform.OsTag -eq 'win' -and $Platform.ArchTag -eq 'arm64') {
        $benchNames += @(
            "StreamBench_win_x64_ai$($Platform.Ext)",
            "StreamBench_win-x64$($Platform.Ext)",
            "StreamBench_win_x64$($Platform.Ext)"
        )
    }

    $bench = Find-StreamBenchExecutable -CandidateNames $benchNames
    if ($bench) {
        return [pscustomobject]@{
            LaunchType = 'self'
            BenchName = $bench.Name
            BenchExe = $bench.Path
            Csproj = $csproj
            HasCpu = $hasCpu
            HasGpu = $hasGpu
            CpuExe = $cpuExe
            GpuExe = $gpuExe
        }
    }

    # Priority 2: build from source (developer scenario)
    if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
        return [pscustomobject]@{
            LaunchType = 'source'
            BenchName = $null
            BenchExe = $null
            Csproj = $csproj
            HasCpu = $hasCpu
            HasGpu = $hasGpu
            CpuExe = $cpuExe
            GpuExe = $gpuExe
        }
    }

    Write-Host '  [ERROR] StreamBench binary or dotnet project not found.' -ForegroundColor Red
    Write-Host "          Expected one of: $($benchNames -join ', ') in $ScriptDir" -ForegroundColor Red
    Write-Host '          Or project file: StreamBench\StreamBench.csproj' -ForegroundColor Red
    return $null
}

# Launch a child process that writes directly to the console (preserving \r spinner animation).
# Using [Process]::Start with no stdout redirection avoids PowerShell's pipeline capturing output.
function Start-StreamBenchProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [string[]]$Arguments = @()
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    if ($Arguments.Count -gt 0) {
        $escaped = foreach ($a in $Arguments) {
            if ($a -match '[ "]') { '"{0}"' -f ($a -replace '"', '\"') } else { $a }
        }
        $psi.Arguments = $escaped -join ' '
    }
    Suspend-StreamBenchCliLog
    try {
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.WaitForExit()
        return $p.ExitCode
    }
    finally {
        Resume-StreamBenchCliLog
    }
}

function Get-StreamBenchAiArgs {
    param(
        [Parameter(Mandatory)] [string]$ArraySize,
        [Parameter(Mandatory)] [object]$AiSettings,
        [switch]$UseEmbeddedMemoryBackends,
        [AllowNull()] [string]$CpuExe,
        [AllowNull()] [string]$GpuExe,
        [switch]$AiOnly
    )

    $exeArgs = @('--ai', '--array-size', "$ArraySize")
    if ($AiOnly) {
        $exeArgs += '--ai-only'
    }
    if ($UseEmbeddedMemoryBackends) {
        $exeArgs = @('--cpu', '--gpu') + $exeArgs
    } else {
        if (-not $AiOnly -and -not [string]::IsNullOrWhiteSpace($CpuExe) -and (Test-Path $CpuExe)) {
            $exeArgs += @('--cpu', '--cpu-exe', "$CpuExe")
        }
        if (-not $AiOnly -and -not [string]::IsNullOrWhiteSpace($GpuExe) -and (Test-Path $GpuExe)) {
            $exeArgs += @('--gpu', '--gpu-exe', "$GpuExe")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AiSettings.Backend)) {
        $exeArgs += @('--ai-backend', "$($AiSettings.Backend)")
    }
    if (-not [string]::IsNullOrWhiteSpace($AiSettings.Devices)) {
        $exeArgs += @('--ai-device', "$($AiSettings.Devices)")
    }
    if (-not [string]::IsNullOrWhiteSpace($AiSettings.Model)) {
        $exeArgs += @('--ai-model', "$($AiSettings.Model)")
    }
    if ($AiSettings.NoDownload) {
        $exeArgs += '--ai-no-download'
    }
    return $exeArgs
}

function Invoke-StreamBenchMemoryLaunch {
    param(
        [Parameter(Mandatory)] [object]$Resolved,
        [Parameter(Mandatory)] [string]$ArraySize
    )

    if ($Resolved.LaunchType -eq 'self') {
        Write-Host "  [OK] Found $($Resolved.BenchName)" -ForegroundColor Green
        Write-Host ''
        $exitCode = Start-StreamBenchProcess -FilePath $Resolved.BenchExe -Arguments @('--array-size', $ArraySize)
        if ($exitCode -ne 0) {
            Write-Host "  [FAIL] Benchmark exited with error code $exitCode." -ForegroundColor Red
        }
        return $exitCode
    }

    Write-Host '  [OK] Using dotnet run (dev mode)' -ForegroundColor Green
    Write-Host ''

    $overallExitCode = 0
    function Invoke-StreamBenchDevBench {
        param(
            [Parameter(Mandatory)] [string]$Mode,
            [Parameter(Mandatory)] [string]$ExePath,
            [Parameter(Mandatory)] [string]$ProjectPath,
            [Parameter(Mandatory)] [string]$RequestedArraySize
        )

        return (Start-StreamBenchProcess -FilePath 'dotnet' -Arguments @('run', '--project', $ProjectPath, '--', "--$Mode", '--exe', $ExePath, '--array-size', $RequestedArraySize))
    }

    if ($Resolved.HasCpu) {
        $cpuExitCode = Invoke-StreamBenchDevBench -Mode 'cpu' -ExePath $Resolved.CpuExe -ProjectPath $Resolved.Csproj -RequestedArraySize $ArraySize
        if ($cpuExitCode -ne 0) {
            $overallExitCode = $cpuExitCode
        }
        Write-Host ''
    }

    if ($Resolved.HasGpu) {
        $gpuExitCode = Invoke-StreamBenchDevBench -Mode 'gpu' -ExePath $Resolved.GpuExe -ProjectPath $Resolved.Csproj -RequestedArraySize $ArraySize
        if ($gpuExitCode -ne 0) {
            $overallExitCode = $gpuExitCode
        }
        Write-Host ''
    }

    Write-Host '  ========================================' -ForegroundColor DarkGray
    if ($overallExitCode -eq 0) {
        Write-Host '   Benchmark Complete' -ForegroundColor Green
    } else {
        Write-Host '   Benchmark Finished With Errors' -ForegroundColor Yellow
    }
    Write-Host '  ========================================' -ForegroundColor DarkGray
    Write-Host ''

    return $overallExitCode
}

function Invoke-StreamBenchAiLaunch {
    param(
        [Parameter(Mandatory)] [object]$Resolved,
        [Parameter(Mandatory)] [string]$ArraySize,
        [Parameter(Mandatory)] [object]$AiSettings,
        [Parameter(Mandatory)] [object]$Platform
    )

    if ($Resolved.LaunchType -eq 'source') {
        Write-Host '  [OK] Using dotnet build + app run (source mode)' -ForegroundColor Green
        $buildOutput = & dotnet build "$($Resolved.Csproj)" -p:EnableAI=true --nologo -v:q 2>&1
        if ($LASTEXITCODE -ne 0) {
            $buildOutput | ForEach-Object { Write-Host $_ }
            return $LASTEXITCODE
        }

        $aiOnly = $false
        if (-not $Resolved.HasCpu -and -not $Resolved.HasGpu) {
            Write-Host '  [!] Source memory backends are not built locally; continuing with AI-only mode.' -ForegroundColor Yellow
            Write-Host '      Build them with .\build_all_windows.ps1 to enable memory benchmarks and Q3 locally.' -ForegroundColor Yellow
            $aiOnly = $true
        }
        if (-not $Resolved.HasCpu) {
            Write-Host '  [!] CPU backend not found; continuing without CPU memory benchmark.' -ForegroundColor Yellow
        }
        if (-not $Resolved.HasGpu) {
            Write-Host '  [!] GPU backend not found; continuing without GPU memory benchmark.' -ForegroundColor Yellow
        }

        $debugDir = Join-Path $ScriptDir 'StreamBench\bin\Debug'
        $appArgs = Get-StreamBenchAiArgs -ArraySize $ArraySize -AiSettings $AiSettings -CpuExe $Resolved.CpuExe -GpuExe $Resolved.GpuExe -AiOnly:$aiOnly

        if ($Platform.IsWindows) {
            $appExe = Get-ChildItem -Path $debugDir -Filter 'StreamBench.exe' -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

            if ($appExe) {
                return (Start-StreamBenchProcess -FilePath $appExe.FullName -Arguments $appArgs)
            }
        }

        $dll = Get-ChildItem -Path $debugDir -Filter 'StreamBench.dll' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $dll) {
            Write-Host "  [ERROR] Built StreamBench app not found under $debugDir" -ForegroundColor Red
            return 1
        }

        return (Start-StreamBenchProcess -FilePath 'dotnet' -Arguments (@($dll.FullName) + $appArgs))
    }

    Write-Host "  [OK] Found $($Resolved.BenchName)" -ForegroundColor Green

    $exeArgs = Get-StreamBenchAiArgs -ArraySize $ArraySize -AiSettings $AiSettings -UseEmbeddedMemoryBackends

    return (Start-StreamBenchProcess -FilePath $Resolved.BenchExe -Arguments $exeArgs)
}

function Invoke-StreamBenchLauncher {
    param([AllowNull()] [string]$SelectedMode)

    $platform = Get-StreamBenchPlatformContext
    $sleepPrevented = Start-StreamBenchSleepPrevention -Platform $platform
    $cliLogPath = Start-StreamBenchCliLog -BaseDirectory $ScriptDir

    try {
        if ($cliLogPath) {
            Write-Host ''
            Write-Host "  [OK] CLI transcript: $cliLogPath" -ForegroundColor DarkGray
        }

        $aiSettings = Get-StreamBenchAiSettings
        $mode = Select-StreamBenchMode -SelectedMode $SelectedMode -AiSettings $aiSettings
        if ($mode -eq 'ai') {
            $selectedBackend = Select-StreamBenchAiBackend -AiSettings $aiSettings
            $aiSettings = [pscustomobject]@{
                Backend = $selectedBackend
                Model = $aiSettings.Model
                Devices = $aiSettings.Devices
                NoDownload = $aiSettings.NoDownload
            }
        } else {
            $aiSettings = [pscustomobject]@{
                Backend = ''
                Model = $aiSettings.Model
                Devices = $aiSettings.Devices
                NoDownload = $aiSettings.NoDownload
            }
        }

        $arraySize = Get-StreamBenchArraySize
        if ($null -eq $arraySize) {
            return 1
        }

        Show-StreamBenchLauncherHeader -Mode $mode -AiSettings $aiSettings -ArraySize $arraySize

        if (-not (Ensure-StreamBenchPrerequisites -Platform $platform -RequireAi:($mode -eq 'ai') -AiBackend $aiSettings.Backend)) {
            return 1
        }

        if ($mode -eq 'ai') {
            $aiLaunch = Resolve-StreamBenchAiLaunch -Platform $platform
            if ($null -eq $aiLaunch) {
                return 1
            }
            return (Invoke-StreamBenchAiLaunch -Resolved $aiLaunch -ArraySize $arraySize -AiSettings $aiSettings -Platform $platform)
        }

        $memoryLaunch = Resolve-StreamBenchMemoryLaunch -Platform $platform
        if ($null -eq $memoryLaunch) {
            return 1
        }
        return (Invoke-StreamBenchMemoryLaunch -Resolved $memoryLaunch -ArraySize $arraySize)
    }
    finally {
        Stop-StreamBenchSleepPrevention -Enabled:$sleepPrevented
        Stop-StreamBenchCliLog
    }
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

exit (Invoke-StreamBenchLauncher)
