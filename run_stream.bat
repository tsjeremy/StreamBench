@echo off
REM ============================================================
REM STREAM Benchmark - Setup & Run (for target machines)
REM ============================================================
REM This script is designed for machines that RECEIVE the .exe files.
REM It checks prerequisites, auto-detects the architecture,
REM installs Visual C++ Redistributable if needed, and runs
REM the correct benchmark.
REM
REM Just double-click this file or run it from any terminal.
REM ============================================================

setlocal enabledelayedexpansion

REM --- Enable ANSI color codes (Windows 10+) ---
for /f %%a in ('echo prompt $E ^| cmd') do set "ESC=%%a"
set "C_RESET=%ESC%[0m"
set "C_BOLD=%ESC%[1m"
set "C_DIM=%ESC%[2m"
set "C_RED=%ESC%[91m"
set "C_GREEN=%ESC%[32m"
set "C_YELLOW=%ESC%[33m"
set "C_CYAN=%ESC%[36m"
set "C_BCYAN=%ESC%[1;96m"
set "C_BWHITE=%ESC%[1;97m"
set "C_BGREEN=%ESC%[1;92m"

echo.
echo  %C_DIM%========================================%C_RESET%
echo  %C_BCYAN% STREAM Memory Bandwidth Benchmark%C_RESET%
echo  %C_DIM%========================================%C_RESET%
echo.

REM --- Detect architecture ---
set "ARCH=%PROCESSOR_ARCHITECTURE%"
if /i "%ARCH%"=="AMD64" (
    set "ARCH_LABEL=x64"
    set "REDIST_URL=https://aka.ms/vs/17/release/vc_redist.x64.exe"
) else if /i "%ARCH%"=="ARM64" (
    set "ARCH_LABEL=arm64"
    set "REDIST_URL=https://aka.ms/vs/17/release/vc_redist.arm64.exe"
) else if /i "%ARCH%"=="x86" (
    set "ARCH_LABEL=x64"
    set "REDIST_URL=https://aka.ms/vs/17/release/vc_redist.x64.exe"
) else (
    echo %C_RED% [ERROR] Unknown architecture: %ARCH%%C_RESET%
    echo     Please run the correct executable manually.
    goto :pause_exit
)

echo  %C_CYAN%Detected architecture:%C_RESET% %C_BWHITE%%ARCH% [%ARCH_LABEL%]%C_RESET%

REM --- Set paths ---
set "SCRIPTDIR=%~dp0"
set "CPU_EXE=%SCRIPTDIR%stream_cpu_win_%ARCH_LABEL%.exe"
set "GPU_EXE=%SCRIPTDIR%stream_gpu_win_%ARCH_LABEL%.exe"

REM --- Check if executables exist ---
set "HAS_CPU=0"
set "HAS_GPU=0"
if exist "%CPU_EXE%" set "HAS_CPU=1"
if exist "%GPU_EXE%" set "HAS_GPU=1"

if "!HAS_CPU!"=="0" if "!HAS_GPU!"=="0" (
    echo.
    echo  %C_RED%[ERROR] No benchmark executables found for %ARCH_LABEL%.%C_RESET%
    echo          Expected files in: %SCRIPTDIR%
    echo            - stream_cpu_win_%ARCH_LABEL%.exe
    echo            - stream_gpu_win_%ARCH_LABEL%.exe
    echo.
    echo          Run build_all_windows.bat on a build machine to compile them.
    goto :pause_exit
)

REM --- Check for VCOMP140.DLL (OpenMP runtime) ---
set "DLL_OK=0"
if exist "%SystemRoot%\System32\vcomp140.dll" set "DLL_OK=1"

if "!DLL_OK!"=="0" (
    where vcomp140.dll >nul 2>&1 && set "DLL_OK=1"
)

if "!DLL_OK!"=="1" (
    echo  %C_CYAN%OpenMP runtime:%C_RESET%      %C_GREEN%[OK] vcomp140.dll found%C_RESET%
    echo.
) else (
    echo.
    echo  %C_DIM%--------------------------------------------------------%C_RESET%
    echo  %C_YELLOW%[!] MISSING: vcomp140.dll%C_RESET%
    echo  %C_DIM%--------------------------------------------------------%C_RESET%
    echo.
    echo  The CPU benchmark requires the Visual C++ Redistributable
    echo  for accurate multi-threaded memory bandwidth measurement.
    echo.

    REM --- Attempt auto-install via winget ---
    where winget >nul 2>&1
    if !ERRORLEVEL! EQU 0 (
        echo  %C_CYAN%[Auto-fix available]%C_RESET% winget detected on this system.
        echo.
        set /p "INSTALL_CHOICE=  Install VC++ Redistributable now? [Y/n]: "

        if /i "!INSTALL_CHOICE!"=="n" (
            echo.
            echo  %C_YELLOW%Skipped. CPU benchmark will not run without vcomp140.dll.%C_RESET%
            echo.
        ) else (
            echo.
            echo  %C_CYAN%Installing Visual C++ Redistributable...%C_RESET%
            echo.
            winget install Microsoft.VCRedist.2015+.%ARCH_LABEL% --accept-package-agreements --accept-source-agreements
            if !ERRORLEVEL! EQU 0 (
                echo.
                echo  %C_GREEN%[OK] Installation succeeded!%C_RESET%
                set "DLL_OK=1"
            ) else (
                echo.
                echo  %C_RED%[!] Installation may have failed.%C_RESET%
                echo      Try downloading manually: %REDIST_URL%
            )
        )
    ) else (
        echo  winget is not available. Please install manually:
        echo.
        echo    %C_CYAN%Download: %REDIST_URL%%C_RESET%
        echo.
        echo  After installing, re-run this script.
        echo.
        set /p "WAIT_CHOICE=  Press Enter after installing, or type Q to quit: "
        if /i "!WAIT_CHOICE!"=="Q" goto :pause_exit

        REM Re-check after user says they installed
        if exist "%SystemRoot%\System32\vcomp140.dll" (
            set "DLL_OK=1"
            echo  %C_GREEN%[OK] vcomp140.dll found!%C_RESET%
        ) else (
            echo  %C_YELLOW%[!] vcomp140.dll still not found.%C_RESET%
        )
    )
    echo.
)

REM ============================================================
REM  Run CPU Benchmark
REM ============================================================
if "!HAS_CPU!"=="1" (
    if "!DLL_OK!"=="1" (
        echo  %C_DIM%========================================%C_RESET%
        echo  %C_BCYAN% Running CPU Benchmark%C_RESET%
        echo   %C_DIM%!CPU_EXE!%C_RESET%
        echo  %C_DIM%========================================%C_RESET%
        echo.
        "!CPU_EXE!"
        echo.
    ) else (
        echo  %C_YELLOW%[SKIP] CPU benchmark requires vcomp140.dll.%C_RESET%
        echo         Install Visual C++ Redistributable:
        echo         %REDIST_URL%
        echo.
    )
)

REM ============================================================
REM  Run GPU Benchmark
REM ============================================================
if "!HAS_GPU!"=="1" (
    echo  %C_DIM%========================================%C_RESET%
    echo  %C_BCYAN% Running GPU Benchmark%C_RESET%
    echo   %C_DIM%!GPU_EXE!%C_RESET%
    echo  %C_DIM%========================================%C_RESET%
    echo.
    "!GPU_EXE!"
    echo.
) else (
    echo  %C_YELLOW%[SKIP] GPU benchmark not found: stream_gpu_win_%ARCH_LABEL%.exe%C_RESET%
    echo.
)

echo  %C_DIM%========================================%C_RESET%
echo  %C_BGREEN% Benchmark Complete%C_RESET%
echo  %C_DIM%========================================%C_RESET%
echo.
echo  %C_CYAN%Results saved to CSV files in:%C_RESET% %C_BWHITE%%SCRIPTDIR%%C_RESET%
echo.

:pause_exit
echo  Press any key to exit...
pause >nul
endlocal
