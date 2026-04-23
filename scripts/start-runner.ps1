<#
.SYNOPSIS
    Starts the AgentSquad runner as a detached background process.
.DESCRIPTION
    Launches dotnet run for AgentSquad.Runner as an independent process that
    survives shell/terminal crashes. Writes PID to runner.pid and redirects
    stdout/stderr to timestamped log files.
.EXAMPLE
    .\scripts\start-runner.ps1
    .\scripts\start-runner.ps1 -LogDir "C:\logs"
#>
param(
    [string]$LogDir = (Join-Path $PSScriptRoot ".." "Logs"),
    [string]$ProjectDir = (Join-Path (Join-Path (Join-Path $PSScriptRoot "..") "src") "AgentSquad.Runner"),
    [string]$PidFile = (Join-Path $PSScriptRoot ".." "Logs" "runner.pid")
)

$ErrorActionPreference = "Stop"

# Check if already running
if (Test-Path $PidFile) {
    $existingPid = Get-Content $PidFile -Raw | ForEach-Object { $_.Trim() }
    if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
        Write-Host "Runner is already running (PID: $existingPid)" -ForegroundColor Yellow
        Write-Host "Use .\scripts\stop-runner.ps1 to stop it first."
        exit 1
    }
    Remove-Item $PidFile -Force
}

# Ensure log directory exists
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

# Build first to catch errors early
Write-Host "Building AgentSquad.Runner..." -ForegroundColor Cyan
$buildResult = & dotnet build (Join-Path $ProjectDir "AgentSquad.Runner.csproj") --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed:" -ForegroundColor Red
    $buildResult | Write-Host
    exit 1
}
Write-Host "Build succeeded." -ForegroundColor Green

# Timestamp for log files
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogDir "runner-$timestamp-stdout.log"
$stderrLog = Join-Path $LogDir "runner-$timestamp-stderr.log"

# Also maintain a "latest" symlink-style log for convenience
$latestStdout = Join-Path $LogDir "runner-latest-stdout.log"
$latestStderr = Join-Path $LogDir "runner-latest-stderr.log"

# Launch as detached process
$proc = Start-Process -FilePath "dotnet" `
    -ArgumentList "run", "--project", $ProjectDir, "--no-build" `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -WindowStyle Hidden `
    -PassThru

# Save PID
$proc.Id | Out-File -FilePath $PidFile -NoNewline

# Update latest log pointers
$stdoutLog | Out-File -FilePath $latestStdout -NoNewline
$stderrLog | Out-File -FilePath $latestStderr -NoNewline

Write-Host ""
Write-Host "AgentSquad Runner started!" -ForegroundColor Green
Write-Host "  PID:    $($proc.Id)"
Write-Host "  Stdout: $stdoutLog"
Write-Host "  Stderr: $stderrLog"
Write-Host "  PID file: $PidFile"
Write-Host ""
Write-Host "Dashboard: http://localhost:5050" -ForegroundColor Cyan
Write-Host ""
Write-Host "Use '.\scripts\stop-runner.ps1' to stop."
Write-Host "Use '.\scripts\runner-status.ps1' to check status."
Write-Host "Use 'Get-Content $stdoutLog -Wait' to tail logs."
