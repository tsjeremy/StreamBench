@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run_stream_ai.ps1"
exit /b %ERRORLEVEL%
