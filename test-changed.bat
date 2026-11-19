@echo off
rem Runs the tests that the files written since the last finished run can affect.
rem Extra arguments go to TestRunner (for example --filter=KeyCopyPaste or --verbose).
setlocal
cd /d "%~dp0"
set "TEMP=%CD%\.tmp-run\temp"
set "TMP=%TEMP%"
if not exist "%TEMP%" mkdir "%TEMP%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\TestRunner\status.ps1" >nul 2>&1
if "%errorlevel%"=="1" (
  echo A test run is already live - not starting another one. See status.bat / watch.bat.
  exit /b 1
)

dotnet build "%~dp0SloppyAnimatsu.sln" -m:1 /nodeReuse:false -v q
if errorlevel 1 (
  echo Build failed - no tests were started.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\launch-on-monitor3.ps1" -FilePath "%~dp0watch.bat" -WorkingDirectory "%~dp0." -Title "SloppyAnimatsu tests"
dotnet "%~dp0tools\TestRunner\bin\Debug\net9.0\TestRunner.dll" --changed %*
exit /b %errorlevel%


