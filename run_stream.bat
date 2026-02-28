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

echo.
echo  ========================================
echo   STREAM Memory Bandwidth Benchmark
echo  ========================================
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
    echo [!] Unknown architecture: %ARCH%
    echo     Please run the correct executable manually.
    goto :pause_exit
)

echo  Detected architecture: %ARCH% [%ARCH_LABEL%]

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
    echo  [ERROR] No benchmark executables found for %ARCH_LABEL%.
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
    echo  OpenMP runtime:      [OK] vcomp140.dll found
    echo.
) else (
    echo.
    echo  --------------------------------------------------------
    echo  [!] MISSING: vcomp140.dll
    echo  --------------------------------------------------------
    echo.
    echo  The CPU benchmark requires the Visual C++ Redistributable
    echo  for accurate multi-threaded memory bandwidth measurement.
    echo.

    REM --- Attempt auto-install via winget ---
    where winget >nul 2>&1
    if !ERRORLEVEL! EQU 0 (
        echo  [Auto-fix available] winget detected on this system.
        echo.
        set /p "INSTALL_CHOICE=  Install VC++ Redistributable now? [Y/n]: "

        if /i "!INSTALL_CHOICE!"=="n" (
            echo.
            echo  Skipped. CPU benchmark will not run without vcomp140.dll.
            echo.
        ) else (
            echo.
            echo  Installing Visual C++ Redistributable...
            echo.
            winget install Microsoft.VCRedist.2015+.%ARCH_LABEL% --accept-package-agreements --accept-source-agreements
            if !ERRORLEVEL! EQU 0 (
                echo.
                echo  [OK] Installation succeeded!
                set "DLL_OK=1"
            ) else (
                echo.
                echo  [!] Installation may have failed.
                echo      Try downloading manually: %REDIST_URL%
            )
        )
    ) else (
        echo  winget is not available. Please install manually:
        echo.
        echo    Download: %REDIST_URL%
        echo.
        echo  After installing, re-run this script.
        echo.
        set /p "WAIT_CHOICE=  Press Enter after installing, or type Q to quit: "
        if /i "!WAIT_CHOICE!"=="Q" goto :pause_exit

        REM Re-check after user says they installed
        if exist "%SystemRoot%\System32\vcomp140.dll" (
            set "DLL_OK=1"
            echo  [OK] vcomp140.dll found!
        ) else (
            echo  [!] vcomp140.dll still not found.
        )
    )
    echo.
)

REM ============================================================
REM  Run CPU Benchmark
REM ============================================================
if "!HAS_CPU!"=="1" (
    if "!DLL_OK!"=="1" (
        echo  ========================================
        echo   Running CPU Benchmark
        echo   !CPU_EXE!
        echo  ========================================
        echo.
        "!CPU_EXE!"
        echo.
    ) else (
        echo  [SKIP] CPU benchmark requires vcomp140.dll.
        echo         Install Visual C++ Redistributable:
        echo         %REDIST_URL%
        echo.
    )
)

REM ============================================================
REM  Run GPU Benchmark
REM ============================================================
if "!HAS_GPU!"=="1" (
    echo  ========================================
    echo   Running GPU Benchmark
    echo   !GPU_EXE!
    echo  ========================================
    echo.
    "!GPU_EXE!"
    echo.
) else (
    echo  [SKIP] GPU benchmark not found: stream_gpu_win_%ARCH_LABEL%.exe
    echo.
)

echo  ========================================
echo   Benchmark Complete
echo  ========================================
echo.
echo  Results saved to CSV files in: %SCRIPTDIR%
echo.

:pause_exit
echo  Press any key to exit...
pause >nul
endlocal
