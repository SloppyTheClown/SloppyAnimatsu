@echo off
rem Builds every SloppyAnimatsu project. Extra arguments go to dotnet build (for example: build.bat -c Release).
cd /d "%~dp0"
dotnet build SloppyAnimatsu.sln %*
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)
