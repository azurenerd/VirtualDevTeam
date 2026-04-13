<#
.SYNOPSIS
    Starts the standalone AgentSquad Dashboard as a detached background process.
.DESCRIPTION
    Launches the Dashboard.Host project on port 5051, connecting to the Runner
    API at port 5050. The dashboard can be restarted independently without
    disrupting running agents.
.EXAMPLE
    .\scripts\start-dashboard.ps1
    .\scripts\start-dashboard.ps1 -LogDir "C:\logs"
#>
param(
    [string]$LogDir = (Join-Path $PSScriptRoot ".." "logs"),
    [string]$ProjectDir = (Join-Path $PSScriptRoot ".." "src" "AgentSquad.Dashboard.Host"),
    [string]$PidFile = (Join-Path $PSScriptRoot ".." "dashboard.pid")
)

$ErrorActionPreference = "Stop"

# Check if already running
if (Test-Path $PidFile) {
    $existingPid = Get-Content $PidFile -Raw | ForEach-Object { $_.Trim() }
    if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
        Write-Host "Dashboard is already running (PID: $existingPid)" -ForegroundColor Yellow
        Write-Host "Stop it with: Stop-Process -Id $existingPid"
        exit 1
    }
    Remove-Item $PidFile -Force
}

# Check Runner is running
try {
    $null = Invoke-WebRequest -Uri "http://localhost:5050/api/dashboard/agents" -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
    Write-Host "Runner detected at http://localhost:5050" -ForegroundColor Green
} catch {
    Write-Host "WARNING: Runner not detected at http://localhost:5050" -ForegroundColor Yellow
    Write-Host "  Start the runner first: .\scripts\start-runner.ps1" -ForegroundColor Yellow
    Write-Host "  Dashboard will retry connecting automatically." -ForegroundColor Gray
}

# Ensure log directory exists
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

# Build first
Write-Host "Building AgentSquad.Dashboard.Host..." -ForegroundColor Cyan
$buildResult = & dotnet build (Join-Path $ProjectDir "AgentSquad.Dashboard.Host.csproj") --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed:" -ForegroundColor Red
    $buildResult | Write-Host
    exit 1
}
Write-Host "Build succeeded." -ForegroundColor Green

# Timestamp for log files
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdoutLog = Join-Path $LogDir "dashboard-$timestamp-stdout.log"
$stderrLog = Join-Path $LogDir "dashboard-$timestamp-stderr.log"

# Launch as detached process
$proc = Start-Process -FilePath "dotnet" `
    -ArgumentList "run", "--project", $ProjectDir, "--no-build" `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -WindowStyle Hidden `
    -PassThru

# Save PID
$proc.Id | Out-File -FilePath $PidFile -NoNewline

Write-Host ""
Write-Host "AgentSquad Dashboard started!" -ForegroundColor Green
Write-Host "  PID:    $($proc.Id)"
Write-Host "  Stdout: $stdoutLog"
Write-Host "  Stderr: $stderrLog"
Write-Host ""
Write-Host "Dashboard: http://localhost:5051 (standalone)" -ForegroundColor Cyan
Write-Host "Runner:    http://localhost:5050 (embedded dashboard also available)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Stop with: Stop-Process -Id $($proc.Id)"
