<#
.SYNOPSIS
    Checks the status of the AgentSquad runner process.
.DESCRIPTION
    Reports whether the runner is running, its PID, uptime, memory usage,
    and recent log output.
.EXAMPLE
    .\scripts\runner-status.ps1
    .\scripts\runner-status.ps1 -TailLines 20
#>
param(
    [string]$PidFile = (Join-Path $PSScriptRoot ".." "runner.pid"),
    [string]$LogDir = (Join-Path $PSScriptRoot ".." "logs"),
    [int]$TailLines = 10
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== AgentSquad Runner Status ===" -ForegroundColor Cyan
Write-Host ""

# Check PID file
if (-not (Test-Path $PidFile)) {
    Write-Host "Status: NOT RUNNING (no runner.pid file)" -ForegroundColor Red
    Write-Host ""

    # Check for orphaned processes
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*AgentSquad.Runner*" }

    if ($procs.Count -gt 0) {
        Write-Host "WARNING: Found orphaned AgentSquad processes:" -ForegroundColor Yellow
        foreach ($p in $procs) {
            Write-Host "  PID: $($p.ProcessId) — Started: $($p.CreationDate)" -ForegroundColor Yellow
        }
        Write-Host "  Use '.\scripts\stop-runner.ps1' to clean up."
    }
    exit 1
}

$runnerPid = [int](Get-Content $PidFile -Raw).Trim()
$proc = Get-Process -Id $runnerPid -ErrorAction SilentlyContinue

if (-not $proc) {
    Write-Host "Status: CRASHED (PID $runnerPid is not running)" -ForegroundColor Red
    Write-Host "  The runner was started but is no longer running."
    Write-Host "  Use '.\scripts\start-runner.ps1' to restart."
    Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    exit 1
}

# Process is running
$uptime = (Get-Date) - $proc.StartTime
$uptimeStr = "{0}d {1}h {2}m {3}s" -f $uptime.Days, $uptime.Hours, $uptime.Minutes, $uptime.Seconds
$memMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)

Write-Host "Status: RUNNING" -ForegroundColor Green
Write-Host "  PID:      $runnerPid"
Write-Host "  Uptime:   $uptimeStr"
Write-Host "  Memory:   ${memMB} MB"
Write-Host "  Started:  $($proc.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host ""

# Check dashboard
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5050" -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
    Write-Host "Dashboard: http://localhost:5050 (HTTP $($response.StatusCode))" -ForegroundColor Green
} catch {
    Write-Host "Dashboard: NOT RESPONDING" -ForegroundColor Yellow
}

# Show recent logs
$latestLogPointer = Join-Path $LogDir "runner-latest-stdout.log"
if (Test-Path $latestLogPointer) {
    $logFile = (Get-Content $latestLogPointer -Raw).Trim()
    if (Test-Path $logFile) {
        Write-Host ""
        Write-Host "=== Recent Log Output (last $TailLines lines) ===" -ForegroundColor Cyan
        Get-Content $logFile | Select-Object -Last $TailLines | ForEach-Object { Write-Host "  $_" }
    }
}

Write-Host ""
