# cleanup-orphan-worktrees.ps1 — Prune stale VDT worktrees
# Mirrors the discipline of kill-orphan-runner-procs.ps1:
# only touches worktrees with a .vdt-worktree-id marker file.
# Safe to run at any time (runner up or down).
#
# Usage:
#   pwsh -File scripts/cleanup-orphan-worktrees.ps1 [-WhatIf]
#   pwsh -File scripts/cleanup-orphan-worktrees.ps1 -HostRepoPath C:\src\BigProject
#
# By default reads the repo path from develop-settings.json or appsettings.json.

param(
    [string]$HostRepoPath,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Resolve host repo path
if (-not $HostRepoPath) {
    $runnerDir = Join-Path $PSScriptRoot '..' 'src' 'VirtualDevTeam.Runner'
    $devSettings = Join-Path $runnerDir 'develop-settings.json'
    if (Test-Path $devSettings) {
        $settings = Get-Content $devSettings -Raw | ConvertFrom-Json
        if ($settings.existingRepoPath) {
            $HostRepoPath = $settings.existingRepoPath
        }
    }
    if (-not $HostRepoPath) {
        $appSettings = Join-Path $runnerDir 'appsettings.json'
        if (Test-Path $appSettings) {
            $cfg = Get-Content $appSettings -Raw | ConvertFrom-Json
            $HostRepoPath = $cfg.VirtualDevTeam.Workspace.RootPath
        }
    }
}

if (-not $HostRepoPath -or -not (Test-Path $HostRepoPath)) {
    Write-Host "No host repo path found. Use -HostRepoPath <path> or configure develop-settings.json." -ForegroundColor Yellow
    exit 0
}

Write-Host "Host repo: $HostRepoPath"

# List all worktrees
$worktreeOutput = git -C $HostRepoPath worktree list --porcelain 2>&1
$worktrees = @()
$current = @{}

foreach ($line in $worktreeOutput) {
    if ($line -match '^worktree (.+)') {
        if ($current.Count -gt 0) { $worktrees += [PSCustomObject]$current }
        $current = @{ Path = $Matches[1]; Branch = ''; Bare = $false }
    }
    elseif ($line -match '^branch (.+)') {
        $current.Branch = $Matches[1]
    }
    elseif ($line -match '^bare') {
        $current.Bare = $true
    }
}
if ($current.Count -gt 0) { $worktrees += [PSCustomObject]$current }

# Filter to VDT-created worktrees (have marker file)
$vdtWorktrees = $worktrees | Where-Object {
    -not $_.Bare -and (Test-Path (Join-Path $_.Path '.vdt-worktree-id'))
}

if ($vdtWorktrees.Count -eq 0) {
    Write-Host "No VDT worktrees found. Nothing to clean." -ForegroundColor Green
    exit 0
}

Write-Host "Found $($vdtWorktrees.Count) VDT worktree(s):"
$vdtWorktrees | ForEach-Object {
    $marker = Get-Content (Join-Path $_.Path '.vdt-worktree-id') -ErrorAction SilentlyContinue
    Write-Host "  $($_.Path) [$marker] branch=$($_.Branch)"
}

if ($WhatIf) {
    Write-Host "`n[WhatIf] Would remove $($vdtWorktrees.Count) worktree(s)" -ForegroundColor Cyan
    exit 0
}

$removed = 0
foreach ($wt in $vdtWorktrees) {
    try {
        Write-Host "Removing: $($wt.Path)..." -NoNewline
        git -C $HostRepoPath worktree remove --force $wt.Path 2>&1 | Out-Null
        if (Test-Path $wt.Path) {
            # Force delete if git worktree remove didn't fully clean up
            Remove-Item $wt.Path -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host " OK" -ForegroundColor Green
        $removed++
    }
    catch {
        Write-Host " FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Prune stale worktree entries
git -C $HostRepoPath worktree prune 2>&1 | Out-Null

Write-Host "`nRemoved $removed of $($vdtWorktrees.Count) worktree(s)" -ForegroundColor Green
