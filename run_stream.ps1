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
#   $env:STREAMBENCH_AI_MODEL
#   $env:STREAMBENCH_AI_DEVICES
#   $env:STREAMBENCH_AI_NO_DOWNLOAD = '1'
# ============================================================

$ErrorActionPreference = 'Continue'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

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

function Get-StreamBenchAiSettings {
    return [pscustomobject]@{
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

    if ($AiSettings.Model -or $AiSettings.Devices -or $AiSettings.NoDownload) {
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
        Write-Host "  [OK] AI model: $(if ($AiSettings.Model) { $AiSettings.Model } else { '(auto-select)' })" -ForegroundColor Green
        Write-Host "  [OK] AI device(s): $(if ($AiSettings.Devices) { $AiSettings.Devices } else { '(all detected)' })" -ForegroundColor Green
        Write-Host '  [OK] Q3 local summary: auto (when memory JSON exists)' -ForegroundColor Green
    } else {
        Write-Host '  [OK] Selected mode: Memory benchmark only' -ForegroundColor Green
    }

    Write-Host "  [OK] Array size: $ArraySize" -ForegroundColor Green
    Write-Host ''
}

function Ensure-StreamBenchPrerequisites {
    param(
        [Parameter(Mandatory)] [object]$Platform,
        [bool]$RequireAi
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

    if ($RequireAi -and
        -not (Get-Command foundry -ErrorAction SilentlyContinue) -and
        -not (Get-Command foundrylocal -ErrorAction SilentlyContinue)) {
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
        Write-Host '        winget install Microsoft.FoundryLocal' -ForegroundColor Yellow
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

    $bench = Find-StreamBenchExecutable -CandidateNames $benchNames
    $cpuExe = Join-Path $ScriptDir "stream_cpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)"
    $gpuExe = Join-Path $ScriptDir "stream_gpu_$($Platform.OsTag)_$($Platform.ArchTag)$($Platform.Ext)"
    $hasCpu = Test-Path $cpuExe
    $hasGpu = Test-Path $gpuExe
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
    if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $csproj)) {
        return [pscustomobject]@{
            LaunchType = 'source'
            BenchName = $null
            BenchExe = $null
            Csproj = $csproj
        }
    }

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

    $bench = Find-StreamBenchExecutable -CandidateNames $benchNames
    if ($bench) {
        return [pscustomobject]@{
            LaunchType = 'self'
            BenchName = $bench.Name
            BenchExe = $bench.Path
            Csproj = $csproj
        }
    }

    Write-Host '  [ERROR] StreamBench binary or dotnet project not found.' -ForegroundColor Red
    Write-Host "          Expected one of: $($benchNames -join ', ') in $ScriptDir" -ForegroundColor Red
    Write-Host '          Or project file: StreamBench\StreamBench.csproj' -ForegroundColor Red
    return $null
}

function Invoke-StreamBenchMemoryLaunch {
    param(
        [Parameter(Mandatory)] [object]$Resolved,
        [Parameter(Mandatory)] [string]$ArraySize
    )

    if ($Resolved.LaunchType -eq 'self') {
        Write-Host "  [OK] Found $($Resolved.BenchName)" -ForegroundColor Green
        Write-Host ''
        & $Resolved.BenchExe --array-size $ArraySize
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [FAIL] Benchmark exited with error code $LASTEXITCODE." -ForegroundColor Red
        }
        return $LASTEXITCODE
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

        & dotnet run --project "$ProjectPath" -- "--$Mode" --exe "$ExePath" --array-size "$RequestedArraySize"
        return $LASTEXITCODE
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
        & dotnet build "$($Resolved.Csproj)" -p:EnableAI=true --nologo -v:q
        if ($LASTEXITCODE -ne 0) {
            return $LASTEXITCODE
        }

        $debugDir = Join-Path $ScriptDir 'StreamBench\bin\Debug'
        $appArgs = @(
            '--cpu',
            '--gpu',
            '--ai',
            '--array-size', "$ArraySize"
        )
        if (-not [string]::IsNullOrWhiteSpace($AiSettings.Devices)) {
            $appArgs += @('--ai-device', "$($AiSettings.Devices)")
        }
        if (-not [string]::IsNullOrWhiteSpace($AiSettings.Model)) {
            $appArgs += @('--ai-model', "$($AiSettings.Model)")
        }
        if ($AiSettings.NoDownload) {
            $appArgs += '--ai-no-download'
        }

        if ($Platform.IsWindows) {
            $appExe = Get-ChildItem -Path $debugDir -Filter 'StreamBench.exe' -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

            if ($appExe) {
                & $appExe.FullName @appArgs
                return $LASTEXITCODE
            }
        }

        $dll = Get-ChildItem -Path $debugDir -Filter 'StreamBench.dll' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $dll) {
            Write-Host "  [ERROR] Built StreamBench app not found under $debugDir" -ForegroundColor Red
            return 1
        }

        & dotnet $dll.FullName @appArgs
        return $LASTEXITCODE
    }

    Write-Host "  [OK] Found $($Resolved.BenchName)" -ForegroundColor Green

    $exeArgs = @('--cpu', '--gpu', '--ai', '--array-size', "$ArraySize")
    if (-not [string]::IsNullOrWhiteSpace($AiSettings.Devices)) {
        $exeArgs += @('--ai-device', "$($AiSettings.Devices)")
    }
    if (-not [string]::IsNullOrWhiteSpace($AiSettings.Model)) {
        $exeArgs += @('--ai-model', "$($AiSettings.Model)")
    }
    if ($AiSettings.NoDownload) {
        $exeArgs += '--ai-no-download'
    }

    & $Resolved.BenchExe @exeArgs
    return $LASTEXITCODE
}

function Invoke-StreamBenchLauncher {
    param([AllowNull()] [string]$SelectedMode)

    $platform = Get-StreamBenchPlatformContext
    $sleepPrevented = Start-StreamBenchSleepPrevention -Platform $platform

    try {
        $aiSettings = Get-StreamBenchAiSettings
        $mode = Select-StreamBenchMode -SelectedMode $SelectedMode -AiSettings $aiSettings
        $arraySize = Get-StreamBenchArraySize
        if ($null -eq $arraySize) {
            return 1
        }

        Show-StreamBenchLauncherHeader -Mode $mode -AiSettings $aiSettings -ArraySize $arraySize

        if (-not (Ensure-StreamBenchPrerequisites -Platform $platform -RequireAi:($mode -eq 'ai'))) {
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
    }
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

exit (Invoke-StreamBenchLauncher)
