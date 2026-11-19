@echo off
rem Runs the whole suite in parallel TestRunner processes (default 3 shards).
rem Arguments go to tools\TestRunner\run-parallel.ps1: -Shards N, -Changed, -NoBuild,
rem -Filter KeyCopyPaste (repeatable), -Samples "path;path".
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\TestRunner\run-parallel.ps1" %*
exit /b %errorlevel%

