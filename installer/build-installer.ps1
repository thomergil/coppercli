<#
.SYNOPSIS
    Builds the coppercli Windows installer.

.DESCRIPTION
    This script:
    1. Publishes coppercli as a self-contained single-file Windows executable
    2. Runs Inno Setup to create the installer

.PARAMETER InnoSetupPath
    Path to ISCC.exe (Inno Setup compiler). Default searches common locations.

.EXAMPLE
    .\build-installer.ps1

.EXAMPLE
    .\build-installer.ps1 -InnoSetupPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#>

param(
    [string]$InnoSetupPath
)

$ErrorActionPreference = "Stop"

# Configuration
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ProjectPath = Join-Path $RepoRoot "coppercli\coppercli.csproj"
$PublishDir = Join-Path $ScriptDir "publish"
$IssFile = Join-Path $ScriptDir "coppercli.iss"
$OutputDir = Join-Path $ScriptDir "output"

# Find Inno Setup compiler
function Find-InnoSetup {
    $searchPaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $searchPaths) {
        if (Test-Path $path) {
            return $path
        }
    }

    # Try to find via PATH
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($iscc) {
        return $iscc.Source
    }

    return $null
}

Write-Host "=== coppercli Installer Build ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Clean previous build
Write-Host "Cleaning previous build..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}

# Step 2: Publish the application
Write-Host "Publishing coppercli for Windows x64..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $ProjectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o", $PublishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Published to: $PublishDir" -ForegroundColor Green

# Check what was published
$exePath = Join-Path $PublishDir "coppercli.exe"
if (Test-Path $exePath) {
    $exeSize = (Get-Item $exePath).Length / 1MB
    Write-Host "  coppercli.exe: $([math]::Round($exeSize, 2)) MB" -ForegroundColor Gray
}

# Step 3: Find Inno Setup
if (-not $InnoSetupPath) {
    $InnoSetupPath = Find-InnoSetup
}

if (-not $InnoSetupPath -or -not (Test-Path $InnoSetupPath)) {
    Write-Host ""
    Write-Host "WARNING: Inno Setup not found!" -ForegroundColor Yellow
    Write-Host "The application has been published to: $PublishDir" -ForegroundColor White
    Write-Host ""
    Write-Host "To create the installer:" -ForegroundColor White
    Write-Host "  1. Download Inno Setup from: https://jrsoftware.org/isinfo.php" -ForegroundColor Gray
    Write-Host "  2. Install it (default location is fine)" -ForegroundColor Gray
    Write-Host "  3. Run this script again, or open coppercli.iss in Inno Setup" -ForegroundColor Gray
    Write-Host ""
    exit 0
}

Write-Host "Using Inno Setup: $InnoSetupPath" -ForegroundColor Yellow

# Step 4: Build the installer
Write-Host "Building installer..." -ForegroundColor Yellow
& $InnoSetupPath $IssFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Inno Setup compilation failed!" -ForegroundColor Red
    exit 1
}

# Step 5: Report success
$installerPath = Get-ChildItem -Path $OutputDir -Filter "*.exe" | Select-Object -First 1
if ($installerPath) {
    $installerSize = $installerPath.Length / 1MB
    Write-Host ""
    Write-Host "=== Build Complete ===" -ForegroundColor Green
    Write-Host "Installer: $($installerPath.FullName)" -ForegroundColor White
    Write-Host "Size: $([math]::Round($installerSize, 2)) MB" -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "Build complete. Check $OutputDir for the installer." -ForegroundColor Green
}
