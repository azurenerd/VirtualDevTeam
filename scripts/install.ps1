#!/usr/bin/env pwsh
# install.ps1 — VirtualDevTeam installer
#
# Usage:
#   irm https://raw.githubusercontent.com/azurenerd/VirtualDevTeam/main/scripts/install.ps1 | iex
#
# Or download and inspect first:
#   Invoke-WebRequest https://raw.githubusercontent.com/azurenerd/VirtualDevTeam/main/scripts/install.ps1 -OutFile install.ps1
#   Get-Content install.ps1
#   .\install.ps1
#
# What it does:
#   1. Downloads the latest self-contained VDT exe from GitHub Releases
#   2. Installs to %LOCALAPPDATA%\VDT\ (or ~/.local/share/VDT/ on Linux/macOS)
#   3. Adds to PATH (user-scoped)
#   4. Runs vdt check-deps to verify prerequisites
#
# Flags:
#   -Version "1.0.0"    Install a specific version (default: latest)
#   -InstallDir "path"  Custom install directory
#   -NoDeps             Skip the prerequisite check
#   -Force              Overwrite existing installation

param(
    [string]$Version,
    [string]$InstallDir,
    [switch]$NoDeps,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Speed up Invoke-WebRequest

$RepoOwner = "azurenerd"
$RepoName = "VirtualDevTeam"
$ExeName = if ($IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { "vdt.exe" } else { "vdt" }

# Detect platform
$Platform = if ($IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    "win-x64"
} elseif ($IsMacOS) {
    "osx-arm64"
} else {
    "linux-x64"
}

# Determine install directory
if (-not $InstallDir) {
    if ($IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $InstallDir = Join-Path $env:LOCALAPPDATA "VDT"
    } else {
        $InstallDir = Join-Path $HOME ".local" "share" "VDT" "bin"
    }
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║       VirtualDevTeam Installer                   ║" -ForegroundColor Cyan
Write-Host "║       AI-powered multi-agent development team    ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Platform:    $Platform"
Write-Host "  Install to:  $InstallDir"
Write-Host ""

# Check for existing installation
$ExePath = Join-Path $InstallDir $ExeName
if ((Test-Path $ExePath) -and -not $Force) {
    $currentVersion = & $ExePath version 2>&1 | Select-Object -First 1
    Write-Host "  Existing:    $currentVersion" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  VDT is already installed. Use -Force to overwrite." -ForegroundColor Yellow
    Write-Host "  Or run 'vdt check-deps' to verify prerequisites." -ForegroundColor Yellow
    exit 0
}

# Resolve version
if (-not $Version) {
    Write-Host "  Resolving latest release..." -NoNewline
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest" -Headers @{ 'User-Agent' = 'VDT-Installer' }
        $Version = $release.tag_name -replace '^v', ''
        Write-Host " $Version" -ForegroundColor Green
    } catch {
        Write-Host " FAILED" -ForegroundColor Red
        Write-Host ""
        Write-Host "  Could not resolve latest version. Specify with -Version '1.0.0'" -ForegroundColor Red
        Write-Host "  Or download manually from: https://github.com/$RepoOwner/$RepoName/releases" -ForegroundColor Yellow
        exit 1
    }
}

# Download
$AssetName = "vdt-$Platform.zip"
$DownloadUrl = "https://github.com/$RepoOwner/$RepoName/releases/download/v$Version/$AssetName"
$TempZip = Join-Path ([System.IO.Path]::GetTempPath()) "vdt-$Version-$Platform.zip"

Write-Host "  Downloading  v$Version ($Platform)..." -NoNewline
try {
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $TempZip -UseBasicParsing
    $sizeMB = [math]::Round((Get-Item $TempZip).Length / 1MB, 1)
    Write-Host " ${sizeMB}MB" -ForegroundColor Green
} catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Download failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  URL: $DownloadUrl" -ForegroundColor Yellow
    exit 1
}

# Extract
Write-Host "  Installing..." -NoNewline
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
try {
    Expand-Archive -Path $TempZip -DestinationPath $InstallDir -Force
    Write-Host " OK" -ForegroundColor Green
} catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "  Extract failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
}

# Add to PATH (user-scoped)
if ($IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    if ($userPath -notlike "*$InstallDir*") {
        [Environment]::SetEnvironmentVariable("PATH", "$userPath;$InstallDir", "User")
        Write-Host "  PATH updated (user-scoped)" -ForegroundColor Green
    }
    # Also update current session
    $env:PATH = "$env:PATH;$InstallDir"
} else {
    # Linux/macOS: suggest adding to shell profile
    $profileLine = "export PATH=`"`$PATH:$InstallDir`""
    $shellProfile = if (Test-Path "$HOME/.zshrc") { "$HOME/.zshrc" } else { "$HOME/.bashrc" }
    $profileContent = if (Test-Path $shellProfile) { Get-Content $shellProfile -Raw } else { "" }
    if ($profileContent -notlike "*$InstallDir*") {
        Add-Content $shellProfile "`n# VirtualDevTeam`n$profileLine"
        Write-Host "  Added to $shellProfile" -ForegroundColor Green
    }
}

# Verify
Write-Host ""
$installedVersion = & $ExePath version 2>&1 | Select-Object -First 1
Write-Host "  ✅ Installed: $installedVersion" -ForegroundColor Green
Write-Host ""

# Check deps
if (-not $NoDeps) {
    Write-Host "  Running prerequisite check..."
    Write-Host ""
    & $ExePath check-deps
}

Write-Host ""
Write-Host "  🚀 Ready! Run 'vdt start' to launch VirtualDevTeam." -ForegroundColor Cyan
Write-Host "     Dashboard will open at http://localhost:5050" -ForegroundColor Cyan
Write-Host ""
