@echo off
setlocal
chcp 65001 >nul 2>&1

REM Try PowerShell launcher first (full-featured)
if exist "%~dp0run_stream.ps1" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run_stream.ps1"
    set _EC=%ERRORLEVEL%
    goto :done
)

REM Fallback: run StreamBench exe directly when .ps1 is missing
REM (Windows Smart App Control on new PCs can quarantine .ps1 files)
echo.
echo   [!] run_stream.ps1 not found -- launching StreamBench directly.
echo.

set _ARCH=x64
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set _ARCH=arm64

if exist "%~dp0StreamBench_win_%_ARCH%.exe" (
    "%~dp0StreamBench_win_%_ARCH%.exe"
    set _EC=%ERRORLEVEL%
    goto :done
)

echo   [ERROR] Could not find StreamBench_win_%_ARCH%.exe
echo.
echo   If Windows blocked the .ps1 scripts during extraction, try:
echo     1. Right-click the downloaded .zip file
echo     2. Select Properties
echo     3. Check "Unblock" at the bottom, then click OK
echo     4. Re-extract the ZIP
set _EC=1

:done
echo.
echo   Press any key to exit...
pause >nul
exit /b %_EC%
