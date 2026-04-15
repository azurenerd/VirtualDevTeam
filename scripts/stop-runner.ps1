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
    param([int]$ProcessId, [bool]$ForceKill)

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Host "Process $ProcessId is not running." -ForegroundColor Yellow
        return $false
    }

    if ($ForceKill) {
        Write-Host "Force-killing runner (PID: $ProcessId)..." -ForegroundColor Red
        Stop-Process -Id $ProcessId -Force
    } else {
        Write-Host "Stopping runner (PID: $ProcessId)..." -ForegroundColor Cyan
        Stop-Process -Id $ProcessId
        # Wait up to 30 seconds for graceful shutdown
        $timeout = 30
        $elapsed = 0
        while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and $elapsed -lt $timeout) {
            Start-Sleep -Seconds 1
            $elapsed++
            Write-Host "." -NoNewline
        }
        Write-Host ""

        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            Write-Host "Process didn't stop gracefully, force-killing..." -ForegroundColor Yellow
            Stop-Process -Id $ProcessId -Force
        }
    }
    return $true
}

# Try PID file first
if (Test-Path $PidFile) {
    $runnerPid = [int](Get-Content $PidFile -Raw).Trim()
    $stopped = Stop-RunnerByPid -ProcessId $runnerPid -ForceKill $Force.IsPresent

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
        Stop-RunnerByPid -ProcessId $p.ProcessId -ForceKill $Force.IsPresent
    }
    Write-Host "Done." -ForegroundColor Green
}
