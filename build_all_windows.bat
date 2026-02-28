@echo off
REM ============================================================
REM STREAM Benchmark - Build for Windows x64 and ARM64
REM ============================================================
REM This script compiles stream.c and stream_gpu.c for both
REM x64 and ARM64 architectures using MSVC (cl.exe).
REM
REM Output files:
REM   stream_cpu_win_x64.exe      - CPU benchmark for x64
REM   stream_gpu_win_x64.exe      - GPU benchmark for x64
REM   stream_cpu_win_arm64.exe    - CPU benchmark for ARM64
REM   stream_gpu_win_arm64.exe    - GPU benchmark for ARM64
REM
REM Run from any terminal (does NOT require Developer Command Prompt).
REM ============================================================

setlocal enabledelayedexpansion

REM --- Locate vcvarsall.bat ---
set "VCVARSALL="
for %%v in (2022 2025 18) do (
    for %%e in (Enterprise Professional Community BuildTools) do (
        set "CANDIDATE=C:\Program Files\Microsoft Visual Studio\%%v\%%e\VC\Auxiliary\Build\vcvarsall.bat"
        if exist "!CANDIDATE!" (
            set "VCVARSALL=!CANDIDATE!"
            goto :found_vcvars
        )
    )
)

REM Fallback: use vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -property installationPath`) do (
        set "VCVARSALL=%%i\VC\Auxiliary\Build\vcvarsall.bat"
    )
)

:found_vcvars
if not defined VCVARSALL (
    echo ERROR: Could not find vcvarsall.bat. Install Visual Studio with C++ workload.
    echo   winget install Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --passive"
    exit /b 1
)
if not exist "%VCVARSALL%" (
    echo ERROR: vcvarsall.bat not found at: %VCVARSALL%
    exit /b 1
)

echo Found vcvarsall.bat: %VCVARSALL%
echo.

set "SRCDIR=%~dp0"
set "ERRORS=0"

REM --- Build options ---
set "CPU_OPTS=/O2 /DTUNED /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=100 /openmp"
set "GPU_OPTS=/O2 /DSTREAM_ARRAY_SIZE=200000000 /DNTIMES=20"

REM ============================================================
REM  x64 Builds
REM ============================================================
echo ============================================================
echo  Building for x64 (AMD64)
echo ============================================================

cmd /c ""%VCVARSALL%" x64 >nul 2>&1 && cl.exe %CPU_OPTS% /Fe:"%SRCDIR%stream_cpu_win_x64.exe" "%SRCDIR%stream.c""
if %ERRORLEVEL% EQU 0 (
    echo [OK] stream_cpu_win_x64.exe
) else (
    echo [FAIL] stream_cpu_win_x64.exe
    set /a ERRORS+=1
)

cmd /c ""%VCVARSALL%" x64 >nul 2>&1 && cl.exe %GPU_OPTS% /Fe:"%SRCDIR%stream_gpu_win_x64.exe" "%SRCDIR%stream_gpu.c""
if %ERRORLEVEL% EQU 0 (
    echo [OK] stream_gpu_win_x64.exe
) else (
    echo [FAIL] stream_gpu_win_x64.exe
    set /a ERRORS+=1
)

echo.

REM ============================================================
REM  ARM64 Builds (cross-compiled from x64)
REM ============================================================
echo ============================================================
echo  Building for ARM64 (cross-compile from x64)
echo ============================================================

cmd /c ""%VCVARSALL%" x64_arm64 >nul 2>&1 && cl.exe %CPU_OPTS% /Fe:"%SRCDIR%stream_cpu_win_arm64.exe" "%SRCDIR%stream.c""
if %ERRORLEVEL% EQU 0 (
    echo [OK] stream_cpu_win_arm64.exe
) else (
    echo [FAIL] stream_cpu_win_arm64.exe
    set /a ERRORS+=1
)

cmd /c ""%VCVARSALL%" x64_arm64 >nul 2>&1 && cl.exe %GPU_OPTS% /Fe:"%SRCDIR%stream_gpu_win_arm64.exe" "%SRCDIR%stream_gpu.c""
if %ERRORLEVEL% EQU 0 (
    echo [OK] stream_gpu_win_arm64.exe
) else (
    echo [FAIL] stream_gpu_win_arm64.exe
    set /a ERRORS+=1
)

echo.

REM ============================================================
REM  Summary
REM ============================================================
echo ============================================================
echo  Build Summary
echo ============================================================
if exist "%SRCDIR%stream_cpu_win_x64.exe"      echo   [x] stream_cpu_win_x64.exe
if exist "%SRCDIR%stream_gpu_win_x64.exe"      echo   [x] stream_gpu_win_x64.exe
if exist "%SRCDIR%stream_cpu_win_arm64.exe"    echo   [x] stream_cpu_win_arm64.exe
if exist "%SRCDIR%stream_gpu_win_arm64.exe"    echo   [x] stream_gpu_win_arm64.exe
echo.
echo  Note: CPU builds require Visual C++ Redistributable on the target machine.
echo        Use run_stream.bat to auto-detect and install if needed.
echo.

REM --- Cleanup obj files ---
del /q "%SRCDIR%stream.obj" "%SRCDIR%stream_gpu.obj" 2>nul

if !ERRORS! GTR 0 (
    echo !ERRORS! build^(s^) failed.
    endlocal
    exit /b 1
)

echo All builds succeeded!
endlocal
