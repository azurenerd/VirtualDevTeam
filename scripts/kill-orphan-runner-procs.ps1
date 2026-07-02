#Requires -Version 7.0
<#
.SYNOPSIS
    Kill orphan node/dotnet processes left behind by a crashed or restarted Runner,
    WITHOUT killing the user's interactive Copilot CLI sessions, VS Code language
    servers, or other unrelated node tooling.

.DESCRIPTION
    The runner spawns many child processes that don't always die cleanly:
      - Copilot CLI MCP servers (node.exe spawned by `npx @playwright/mcp`,
        `npx @modelcontextprotocol/server-*`, etc.)
      - Squad framework spawns (cmd.exe -> copilot.exe -> node MCPs)
      - Blazor dev servers running the agent's app under test
      - dotnet build/test workers in candidate worktrees

    Naive cleanup (kill all node.exe) destroys the user's interactive Copilot CLI
    sessions and other unrelated tooling. This script uses a multi-criteria
    surgical filter:

      1. Process is `node`, `dotnet`, or `copilot`
      2. PROCESS started >= MinAgeSeconds ago (avoid races on a freshly started runner)
      3. AND any of:
         a. CommandLine matches a runner-spawned MCP / framework / CLI pattern
            (incl. `--allow-all` / `--no-ask-user` for Runner-spawned copilot.exe)
         b. Working directory is inside `<workspace>\.agents\` or `\.candidates\`

    A process must satisfy (1) AND (2) AND (3a OR 3b) to be killed.

    Run this BEFORE restarting the Runner, AFTER a runner crash, or any time the
    workspace is showing stale state. Safe to run while interactive Copilot CLI
    sessions are open.

.PARAMETER WhatIf
    Print what would be killed without actually killing anything.

.PARAMETER MinAgeSeconds
    Only consider processes older than this many seconds. Default 120s — protects
    a freshly-started runner whose processes are still ramping up.

.PARAMETER WorkspaceRoot
    Workspace root path used for the working-directory match. Defaults to the
    repo's `src/VirtualDevTeam.Runner/.agents` if it exists.

.EXAMPLE
    .\scripts\kill-orphan-runner-procs.ps1 -WhatIf
    .\scripts\kill-orphan-runner-procs.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]$MinAgeSeconds = 120,
    [string]$WorkspaceRoot = ""
)

$ErrorActionPreference = 'Stop'

# Resolve workspace root
if (-not $WorkspaceRoot) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidate = Join-Path $repoRoot "src\VirtualDevTeam.Runner\.agents"
    if (Test-Path $candidate) { $WorkspaceRoot = $candidate }
}
$WorkspaceRoot = $WorkspaceRoot ? (Resolve-Path $WorkspaceRoot -ErrorAction SilentlyContinue).Path : ""

Write-Host "kill-orphan-runner-procs: scanning..." -ForegroundColor Cyan
Write-Host "  MinAgeSeconds: $MinAgeSeconds"
Write-Host "  WorkspaceRoot: $($WorkspaceRoot ? $WorkspaceRoot : '(unset — working-dir filter disabled)')"
Write-Host ""

# Patterns that indicate a runner-spawned process. Different process types need
# different patterns — applying node/dotnet patterns to copilot.exe causes false
# positives (e.g., `mcp-config` substring-matches the interactive session's
# `--additional-mcp-config` flag).

# Patterns for node.exe / dotnet.exe (MCP servers, Squad framework, Blazor, etc.)
# MCP-server patterns (playwright, modelcontextprotocol) can false-positive on
# the interactive Copilot CLI session's own MCP servers. These are separated into
# $mpcServerPatterns and REQUIRE a working-directory match (inside .agents/ or .candidates/)
# to avoid killing the user's Copilot CLI session's Playwright/MCP tools.
$mpcServerPatterns = @(
    '@playwright/mcp',
    '@modelcontextprotocol/server-',
    '@modelcontextprotocol\\server-'
)

# Non-MCP patterns that are unambiguously runner-spawned (safe to kill on cmdline match alone)
$cmdLinePatterns_NodeDotnet = @(
    'blazor-devserver',
    '--agent squad',
    'agent\s+squad',
    'workspace-reader',
    'github-mcp-server'
)

# Patterns ONLY for copilot.exe — Runner-spawned autonomous sessions always have
# at least one of these flags. Interactive sessions never do.
$cmdLinePatterns_Copilot = @(
    '--allow-all',
    '--no-ask-user',
    '--agent\s+squad'
)

# Deny-list patterns for copilot.exe — if any are present, ALWAYS skip even if
# a positive pattern matches. These flags only appear in interactive Copilot CLI
# sessions (the one hosting this script). Belt-and-suspenders protection.
$copilotInteractiveMarkers = @(
    # `--resume` can be followed by `=guid` OR whitespace. The 2026-05-12 cleanup
    # observed `--resume=e9583b14-...` failing the previous `--resume\s` pattern
    # and putting an interactive session on the kill list. Allow either separator.
    '--resume[=\s]',
    '--add-dir[=\s]',
    '--additional-mcp-config'
)

$cutoff = (Get-Date).AddSeconds(-$MinAgeSeconds)

# Pull all running node.exe, dotnet.exe, and copilot.exe with their CommandLine + WorkingDirectory
# via WMI / CIM (Get-Process doesn't expose these by itself).
# copilot.exe is included because Runner-spawned CLI processes hold worktree handles
# that block workspace cleanup; the per-process pattern set + interactive deny-list keeps
# the user's interactive Copilot CLI session safe.
$candidates = Get-CimInstance Win32_Process -Filter "Name='node.exe' OR Name='dotnet.exe' OR Name='copilot.exe'" |
    Select-Object ProcessId, Name, CommandLine, ExecutablePath, CreationDate

$orphans = New-Object System.Collections.Generic.List[object]

foreach ($c in $candidates) {
    $procId = $c.ProcessId
    if (-not $procId) { continue }

    # Skip our own pwsh / current shell tree just in case
    if ($procId -eq $PID) { continue }

    # Age filter (criterion #2)
    $started = $c.CreationDate
    if ($started -is [datetime] -and $started -gt $cutoff) { continue }

    $cmd = $c.CommandLine ?? ''

    # Belt-and-suspenders: never kill copilot.exe carrying interactive markers,
    # no matter what other patterns match. Protects the user's CLI session.
    if ($c.Name -eq 'copilot.exe') {
        $isInteractive = $false
        foreach ($m in $copilotInteractiveMarkers) {
            if ($cmd -match $m) { $isInteractive = $true; break }
        }
        if ($isInteractive) { continue }
    }

    # Pick the right pattern set per process type
    $patterns = ($c.Name -eq 'copilot.exe') ? $cmdLinePatterns_Copilot : $cmdLinePatterns_NodeDotnet

    # CommandLine match (criterion #3a)
    $cmdMatched = $false
    $matchReason = ""
    foreach ($p in $patterns) {
        if ($cmd -match $p) { $cmdMatched = $true; $matchReason = "cmdline-match"; break }
    }

    # MCP server patterns: require BOTH cmdline match AND working-dir match.
    # This prevents killing the interactive Copilot CLI session's own MCP servers
    # (e.g., @playwright/mcp used by the operator's Playwright tools).
    if (-not $cmdMatched -and $c.Name -ne 'copilot.exe') {
        foreach ($p in $mpcServerPatterns) {
            if ($cmd -match $p) {
                # Only count as a match if working dir is inside .agents/ or .candidates/
                if ($WorkspaceRoot -and ($cmd -like "*$WorkspaceRoot*" -or $cmd -match '\\\.agents\\' -or $cmd -match '\\\.candidates\\')) {
                    $cmdMatched = $true
                    $matchReason = "mcp-server+workdir"
                    break
                }
                # Also match if the parent process is a known runner-spawned copilot.exe
                # (check if parent's cmdline has --allow-all or --no-ask-user)
                try {
                    $parentPid = (Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue).ParentProcessId
                    if ($parentPid) {
                        $parentCmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$parentPid" -ErrorAction SilentlyContinue).CommandLine
                        if ($parentCmd -and ($parentCmd -match '--allow-all' -or $parentCmd -match '--no-ask-user' -or $parentCmd -match '--agent\s+squad')) {
                            $cmdMatched = $true
                            $matchReason = "mcp-server+runner-parent"
                            break
                        }
                    }
                } catch { } # best-effort parent check
            }
        }
    }

    # Working-directory match (criterion #3b) — copilot.exe NEVER qualifies via
    # workdir alone. The interactive session's --add-dir often points inside the
    # repo, which would false-positive a pure workdir match.
    $cwdMatched = $false
    if ($c.Name -ne 'copilot.exe' -and $WorkspaceRoot -and $cmd) {
        if ($cmd -like "*$WorkspaceRoot*" -or $cmd -match '\\\.agents\\' -or $cmd -match '\\\.candidates\\') {
            $cwdMatched = $true
        }
    }

    if ($cmdMatched -or $cwdMatched) {
        # Pull live process record for memory + start time display
        $live = Get-Process -Id $procId -ErrorAction SilentlyContinue
        $orphans.Add([pscustomobject]@{
            Pid          = $procId
            Name         = $c.Name
            MemMB        = $live ? [math]::Round($live.WorkingSet64 / 1MB, 0) : 0
            StartTime    = $started
            Reason       = $cmdMatched ? $matchReason : 'workdir-match'
            CommandLine  = ($cmd.Length -gt 140) ? $cmd.Substring(0, 137) + '...' : $cmd
        })
    }
}

if ($orphans.Count -eq 0) {
    Write-Host "No orphan runner processes found." -ForegroundColor Green
    exit 0
}

Write-Host "Orphan candidates ($($orphans.Count)):" -ForegroundColor Yellow
$orphans | Format-Table Pid, Name, MemMB, StartTime, Reason, CommandLine -AutoSize -Wrap

$totalMB = [math]::Round(($orphans | Measure-Object MemMB -Sum).Sum, 0)
Write-Host ""
Write-Host "Total memory held: $totalMB MB" -ForegroundColor Yellow
Write-Host ""

if ($WhatIfPreference) {
    Write-Host "(WhatIf) — no processes killed." -ForegroundColor Magenta
    exit 0
}

$killed = 0
$failed = 0
foreach ($m in $orphans) {
    if ($PSCmdlet.ShouldProcess("PID $($m.Pid) ($($m.Name))", "Stop-Process")) {
        try {
            Stop-Process -Id $m.Pid -Force -ErrorAction Stop
            $killed++
        } catch {
            $failed++
            Write-Host "  FAILED $($m.Pid): $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "Killed: $killed | Failed: $failed | Memory reclaimed: ~$totalMB MB" -ForegroundColor Green
