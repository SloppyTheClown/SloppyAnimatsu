@echo off
rem Shows whether a test run is going, how far it has got, and whether another run may be started.
rem Arguments go to status.ps1 (for example: status.bat -Json).
rem Exit codes: 0 nothing is running, 1 a run is live, 2 a run died without a summary.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\TestRunner\status.ps1 %*
set RESULT=%errorlevel%
echo.
echo 0 = nothing running, 1 = a run is live, 2 = a run died without finishing
pause
exit /b %RESULT%