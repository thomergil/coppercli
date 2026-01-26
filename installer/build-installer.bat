@echo off
REM Simple batch wrapper to run the PowerShell build script
REM Double-click this file to build the installer

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "build-installer.ps1" %*
pause
