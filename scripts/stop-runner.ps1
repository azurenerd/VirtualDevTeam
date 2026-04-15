<#
.SYNOPSIS
    Stops the AgentSquad runner process.
.DESCRIPTION
    Reads the PID from runner.pid and gracefully stops the process.
    Falls back to finding dotnet processes running AgentSquad.Runner.
.EXAMPLE
    .\scripts\stop-runner.ps1
    .\scripts\stop-runner.ps1 -Force
#>
param(
    [string]$PidFile = (Join-Path $PSScriptRoot ".." "runner.pid"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Stop-RunnerByPid {
    param([int]$Pid, [bool]$ForceKill)

    $proc = Get-Process -Id $Pid -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Host "Process $Pid is not running." -ForegroundColor Yellow
        return $false
    }

    if ($ForceKill) {
        Write-Host "Force-killing runner (PID: $Pid)..." -ForegroundColor Red
        Stop-Process -Id $Pid -Force
    } else {
        Write-Host "Stopping runner (PID: $Pid)..." -ForegroundColor Cyan
        Stop-Process -Id $Pid
        # Wait up to 30 seconds for graceful shutdown
        $timeout = 30
        $elapsed = 0
        while ((Get-Process -Id $Pid -ErrorAction SilentlyContinue) -and $elapsed -lt $timeout) {
            Start-Sleep -Seconds 1
            $elapsed++
            Write-Host "." -NoNewline
        }
        Write-Host ""

        if (Get-Process -Id $Pid -ErrorAction SilentlyContinue) {
            Write-Host "Process didn't stop gracefully, force-killing..." -ForegroundColor Yellow
            Stop-Process -Id $Pid -Force
        }
    }
    return $true
}

# Try PID file first
if (Test-Path $PidFile) {
    $runnerPid = [int](Get-Content $PidFile -Raw).Trim()
    $stopped = Stop-RunnerByPid -Pid $runnerPid -ForceKill $Force.IsPresent

    if ($stopped) {
        Remove-Item $PidFile -Force
        Write-Host "Runner stopped." -ForegroundColor Green
    } else {
        Write-Host "Stale PID file removed." -ForegroundColor Yellow
        Remove-Item $PidFile -Force
    }
} else {
    Write-Host "No runner.pid file found." -ForegroundColor Yellow
    Write-Host "Looking for running AgentSquad processes..." -ForegroundColor Cyan

    # Search for dotnet processes with AgentSquad.Runner in the command line
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*AgentSquad.Runner*" }

    if ($procs.Count -eq 0) {
        Write-Host "No AgentSquad runner processes found." -ForegroundColor Yellow
        exit 0
    }

    foreach ($p in $procs) {
        Write-Host "Found AgentSquad runner (PID: $($p.ProcessId))" -ForegroundColor Cyan
        Stop-RunnerByPid -Pid $p.ProcessId -ForceKill $Force.IsPresent
    }
    Write-Host "Done." -ForegroundColor Green
}
