@echo off
rem Builds if needed and starts the SloppyAnimatsu viewer. Arguments go to the viewer (for example: run.bat scene.akt).
rem Dropping an .akt file onto this script opens it.
cd /d "%~dp0"
dotnet run --project src\SloppyAnimatsu.Viewer -- %*
if errorlevel 1 pause
