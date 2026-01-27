@echo off
REM Platform script to run coppercli on Windows
REM Double-click this file to run coppercli

setlocal EnableDelayedExpansion

set "DOTNET="
set "PROJECT=coppercli\coppercli.csproj"
set "DOTNET_INSTALL_URL=https://dotnet.microsoft.com/download/dotnet/8.0"

REM Check if dotnet is in PATH
where dotnet >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    set "DOTNET=dotnet"
    goto :found
)

REM Check Program Files
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
    goto :found
)

REM Check Program Files (x86)
if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
    goto :found
)

REM Check user profile
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
    goto :found
)

REM Not found
echo ERROR: dotnet not found!
echo.
echo Please install .NET 8 SDK from:
echo   %DOTNET_INSTALL_URL%
echo.
echo Or install via winget:
echo   winget install Microsoft.DotNet.SDK.8
echo.
pause
exit /b 1

:found
cd /d "%~dp0"
if "%~1"=="" (
    echo Starting coppercli...
) else (
    echo Starting coppercli with args: %*
)
"%DOTNET%" run --project "%PROJECT%" -- %*
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Press any key to exit...
    pause >nul
)
