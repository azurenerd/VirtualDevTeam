<#
.SYNOPSIS
    Warm-restarts the VirtualDevTeam runner — graceful stop + fresh start.
.DESCRIPTION
    Designed to be invoked by the runner itself (via the /api/dashboard/runtime/restart
    endpoint) as a detached background process. The runner spawns this script, then
    calls IHostApplicationLifetime.StopApplication(). This script:

      1. Reads the existing runner PID from runner.pid
      2. Waits up to 30s for the runner process to exit
      3. Re-launches via start-runner.ps1

    Because the runner state (workflow phase, agent status, signals) is checkpointed
    to SQLite, the new runner resumes from where the old one stopped — in-flight LLM
    calls are cancelled but durable platform work (PRs, issues, branches) is safe.

    Detached: this script keeps running after the runner that spawned it has died.
.EXAMPLE
    .\scripts\restart-runner.ps1
    Manual restart from a new shell.

    .\scripts\restart-runner.ps1 -StoppedByRunner
    Invoked by the runner via the runtime/restart API endpoint.
#>
param(
    [string]$LogDir = (Join-Path $PSScriptRoot ".." "Logs"),
    [string]$PidFile = (Join-Path $PSScriptRoot ".." "Logs" "runner.pid"),
    [int]$WaitSeconds = 30,
    [switch]$StoppedByRunner
)

$ErrorActionPreference = "Continue"

# Resolve PidFile to absolute (we may be run from anywhere)
$PidFile = (Resolve-Path $PidFile -ErrorAction SilentlyContinue).Path
if (-not $PidFile) {
    $PidFile = Join-Path (Join-Path $PSScriptRoot "..") "Logs\runner.pid"
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$restartLog = Join-Path $LogDir "restart-runner.log"
$ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
"[$ts] restart-runner.ps1 invoked (StoppedByRunner=$StoppedByRunner, PidFile=$PidFile)" |
    Add-Content -Path $restartLog

# Phase 1: Wait for the existing runner process to exit
if (Test-Path $PidFile) {
    $oldPid = (Get-Content $PidFile -Raw).Trim()
    if ($oldPid -and $oldPid -match '^\d+$') {
        $oldPid = [int]$oldPid
        "[$ts] Waiting up to $WaitSeconds s for runner PID $oldPid to exit..." |
            Add-Content -Path $restartLog

        $waited = 0
        while ($waited -lt $WaitSeconds) {
            if (-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
                "[$ts] Runner PID $oldPid exited after $waited s." |
                    Add-Content -Path $restartLog
                break
            }
            Start-Sleep -Seconds 1
            $waited++
        }

        # Force-kill if still alive after the grace period (rare — graceful shutdown takes 2-3s)
        if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
            "[$ts] Runner PID $oldPid still alive after $WaitSeconds s — force-killing." |
                Add-Content -Path $restartLog
            try { Stop-Process -Id $oldPid -Force -ErrorAction Stop } catch { }
            Start-Sleep -Seconds 2
        }
    }
}
else {
    "[$ts] No PID file at $PidFile — assuming runner already stopped." |
        Add-Content -Path $restartLog
}

# Brief settling pause so the OS releases file locks / ports
Start-Sleep -Seconds 2

# Phase 2: Re-launch via start-runner.ps1
$startScript = Join-Path $PSScriptRoot "start-runner.ps1"
if (-not (Test-Path $startScript)) {
    "[$ts] FATAL: start-runner.ps1 not found at $startScript — cannot restart." |
        Add-Content -Path $restartLog
    exit 1
}

"[$ts] Re-launching runner via $startScript..." | Add-Content -Path $restartLog
try {
    & $startScript -LogDir $LogDir
    "[$ts] start-runner.ps1 returned exit code $LASTEXITCODE" |
        Add-Content -Path $restartLog
}
catch {
    "[$ts] FATAL: start-runner.ps1 threw: $_" | Add-Content -Path $restartLog
    exit 1
}
