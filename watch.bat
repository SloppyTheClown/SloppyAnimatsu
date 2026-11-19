@echo off
rem Live test progress in this window, refreshed until the run ends (or Ctrl+C).
rem The first argument is the refresh interval in seconds (default 5).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\TestRunner\watch.ps1 %*
set RESULT=%errorlevel%
echo.
pause
exit /b %RESULT%