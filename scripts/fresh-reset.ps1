<#
.SYNOPSIS
    Complete fresh reset for AgentSquad — clears ALL state so the runner starts from scratch.

.DESCRIPTION
    This script performs every cleanup step needed for a truly fresh run.
    It mirrors what the Dashboard cleanup does, but from the CLI.

    Items cleaned:
      1. Kill any running dotnet/runner processes
      2. Delete SQLite checkpoint DBs (workflow state persistence)
      3. Delete agent workspace directories (C:\Agents\*)
      4. Delete Playwright temp files (C:\temp\playwright-test)
      5. Clean GitHub repo files via local git clone (keep only preserved files)
      6. Delete all remote agent branches
      7. Close all open GitHub issues (via gh CLI or API)
      8. Close all open GitHub PRs (via gh CLI or API)

.PARAMETER GitHubRepo
    The GitHub repo in owner/repo format. If not provided, reads from appsettings.json.

.PARAMETER GitHubToken
    GitHub PAT for API access. If not provided, reads from dotnet user-secrets
    then falls back to appsettings.json.

.PARAMETER PreserveFiles
    Comma-separated list of files to keep in the repo. Default: OriginalDesignConcept.html

.PARAMETER SkipGitHub
    Skip GitHub API operations (issues, PRs). Useful when only local cleanup is needed.

.EXAMPLE
    .\scripts\fresh-reset.ps1 -GitHubToken "ghp_xxx"
    .\scripts\fresh-reset.ps1 -GitHubToken "ghp_xxx" -PreserveFiles "OriginalDesignConcept.html,.gitignore,README.md"
#>

param(
    [string]$GitHubRepo = "",
    [string]$GitHubToken = "",
    [string]$PreserveFiles = "OriginalDesignConcept.html",
    [string]$WorkspaceRoot = "C:\Agents",
    [string]$RunnerDir = "",
    [switch]$SkipGitHub
)

# Resolve RunnerDir robustly (works even when $PSScriptRoot is empty)
if (-not $RunnerDir) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RunnerDir = Join-Path (Join-Path (Join-Path $scriptDir "..") "src") "AgentSquad.Runner"
}

# Read GitHubRepo from appsettings.json if not explicitly provided
if (-not $GitHubRepo) {
    $settingsPath = Join-Path $RunnerDir "appsettings.json"
    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $GitHubRepo = $settings.AgentSquad.Project.GitHubRepo
    }
    if (-not $GitHubRepo) {
        Write-Host "  ⚠ No GitHubRepo found in appsettings.json and none provided via parameter" -ForegroundColor Red
        exit 1
    }
}

$ErrorActionPreference = "Continue"
$preserveList = $PreserveFiles -split "," | ForEach-Object { $_.Trim() }

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  AgentSquad Fresh Reset" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Repo:      $GitHubRepo"
Write-Host "  Preserve:  $($preserveList -join ', ')"
Write-Host "  Workspace: $WorkspaceRoot"
Write-Host "========================================`n" -ForegroundColor Cyan

# ── Step 1: Kill running dotnet processes (recent ones only) ──
Write-Host "[1/8] Killing recent dotnet processes..." -ForegroundColor Yellow
$killed = 0
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -gt (Get-Date).AddHours(-2) } |
    ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $killed++
    }
Write-Host "      Killed $killed process(es)" -ForegroundColor Green

# ── Step 2: Delete SQLite checkpoint DBs ──
# NOTE: develop-settings.json is user-specific and must survive resets.
# The filter "agentsquad_*.db*" is intentionally narrow to avoid touching it.
Write-Host "[2/8] Deleting SQLite checkpoint DBs..." -ForegroundColor Yellow
$dbCount = 0
$resolvedRunnerDir = Resolve-Path $RunnerDir -ErrorAction SilentlyContinue
if ($resolvedRunnerDir) {
    Get-ChildItem $resolvedRunnerDir -Filter "agentsquad_*.db*" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force
        $dbCount++
    }
    # Also purge agent-created SME definitions from sme-definitions.json
    # These are spawned at runtime and should not survive a reset
    $smeFile = Join-Path $resolvedRunnerDir "sme-definitions.json"
    if (Test-Path $smeFile) {
        try {
            $smeDefs = Get-Content $smeFile -Raw | ConvertFrom-Json -AsHashtable
            $agentCreated = @($smeDefs.Keys | Where-Object { $smeDefs[$_].createdByAgentId })
            if ($agentCreated.Count -gt 0) {
                foreach ($key in $agentCreated) { $smeDefs.Remove($key) }
                $smeDefs | ConvertTo-Json -Depth 10 | Set-Content $smeFile -Encoding UTF8
                Write-Host "      Purged $($agentCreated.Count) agent-created SME definition(s)" -ForegroundColor Green
            }
        } catch {
            Write-Host "      Warning: Could not clean sme-definitions.json: $_" -ForegroundColor Yellow
        }
    }
    # Remove active defs file entirely
    $activeFile = Join-Path $resolvedRunnerDir "sme-definitions-active.json"
    if (Test-Path $activeFile) { Remove-Item $activeFile -Force }
}
Write-Host "      Deleted $dbCount DB file(s)" -ForegroundColor Green

# ── Step 3: Delete agent workspace directories ──
Write-Host "[3/8] Cleaning agent workspaces ($WorkspaceRoot)..." -ForegroundColor Yellow
$wsCount = 0
if (Test-Path $WorkspaceRoot) {
    Get-ChildItem $WorkspaceRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $wsCount++
    }
}
Write-Host "      Removed $wsCount workspace(s)" -ForegroundColor Green

# ── Step 4: Delete Playwright temp files ──
Write-Host "[4/8] Cleaning Playwright temp files..." -ForegroundColor Yellow
$pwClean = $false
if (Test-Path "C:\temp\playwright-test") {
    Remove-Item "C:\temp\playwright-test" -Recurse -Force -ErrorAction SilentlyContinue
    $pwClean = $true
}
Write-Host "      $(if ($pwClean) { 'Cleaned' } else { 'Nothing to clean' })" -ForegroundColor Green

if ($SkipGitHub) {
    Write-Host "`n[5-8] Skipping GitHub operations (-SkipGitHub)" -ForegroundColor DarkGray
}
else {
    if (-not $GitHubToken) {
        # Try user-secrets first, then appsettings.json
        $runnerProject = $RunnerDir
        try {
            $secretsOutput = dotnet user-secrets list --project $runnerProject 2>&1
            $patLine = $secretsOutput | Where-Object { $_ -match 'GitHubToken' }
            if ($patLine) {
                $GitHubToken = ($patLine -split '= ', 2)[1].Trim()
                Write-Host "  Using PAT from dotnet user-secrets" -ForegroundColor Gray
            }
        } catch { }

        if (-not $GitHubToken) {
            $settingsPath = Join-Path $runnerProject "appsettings.json"
            if (Test-Path $settingsPath) {
                $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
                $GitHubToken = $settings.AgentSquad.Project.GitHubToken
                if ($GitHubToken) {
                    Write-Host "  Using PAT from appsettings.json" -ForegroundColor Gray
                }
            }
        }
    }

    if (-not $GitHubToken) {
        Write-Host "`n[5-8] No PAT found — skipping GitHub operations" -ForegroundColor DarkGray
        Write-Host "      Set PAT via: dotnet user-secrets set 'AgentSquad:Project:GitHubToken' '<your-pat>' --project src\AgentSquad.Runner" -ForegroundColor DarkGray
    }
    else {
        $owner, $repo = $GitHubRepo -split "/"
        $headers = @{
            "Authorization" = "Bearer $GitHubToken"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $baseUrl = "https://api.github.com/repos/$owner/$repo"

        # ── Step 5: Clean repo files via local git clone ──
        Write-Host "[5/8] Cleaning repo files via local git clone..." -ForegroundColor Yellow
        $tempDir = Join-Path $env:TEMP "agentsquad-reset-$(Get-Random)"
        try {
            $cloneUrl = "https://x-access-token:${GitHubToken}@github.com/$owner/$repo.git"
            git clone $cloneUrl $tempDir 2>&1 | Out-Null

            Push-Location $tempDir
            git config user.email "agentsquad-cleanup@noreply"
            git config user.name "AgentSquad Reset"

            # Remove everything except preserved files and .git
            $allFiles = Get-ChildItem -Recurse -File | Where-Object { $_.FullName -notmatch "\\\.git\\" }
            $deleted = 0
            foreach ($f in $allFiles) {
                $rel = $f.FullName.Replace("$tempDir\", "").Replace("\", "/")
                $keep = $false
                foreach ($p in $preserveList) {
                    if ($rel -eq $p -or $f.Name -eq $p) { $keep = $true; break }
                }
                if (-not $keep) {
                    Remove-Item $f.FullName -Force
                    $deleted++
                }
            }

            # Remove empty dirs
            Get-ChildItem -Directory -Recurse | Where-Object { $_.FullName -notmatch "\\\.git" } |
                Sort-Object { $_.FullName.Length } -Descending |
                Where-Object { (Get-ChildItem $_.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0 } |
                ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

            if ($deleted -gt 0) {
                git add -A 2>&1 | Out-Null
                git commit -m "Fresh reset — kept $($preserveList.Count) preserved files" 2>&1 | Out-Null
                git push origin main 2>&1 | Out-Null
            }
            Pop-Location
            Write-Host "      Deleted $deleted file(s), preserved $($preserveList.Count)" -ForegroundColor Green
        }
        catch {
            Write-Host "      ERROR: $($_.Exception.Message)" -ForegroundColor Red
        }
        finally {
            if (Test-Path $tempDir) {
                Get-ChildItem $tempDir -Recurse -File -Force | ForEach-Object { $_.Attributes = 'Normal' }
                Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        # ── Step 6: Delete all remote agent branches ──
        Write-Host "[6/8] Deleting remote agent branches..." -ForegroundColor Yellow
        try {
            $branches = Invoke-RestMethod -Uri "$baseUrl/git/matching-refs/heads/agent/" -Headers $headers -ErrorAction Stop
            $brDeleted = 0
            foreach ($b in $branches) {
                $brName = $b.ref -replace "refs/heads/", ""
                try {
                    Invoke-RestMethod -Uri "$baseUrl/git/refs/heads/$brName" -Method Delete -Headers $headers | Out-Null
                    $brDeleted++
                } catch { }
            }
            Write-Host "      Deleted $brDeleted branch(es)" -ForegroundColor Green
        }
        catch {
            Write-Host "      No agent branches found or error: $($_.Exception.Message)" -ForegroundColor DarkGray
        }

        # ── Step 7: Close AI-Generated open issues (with verify-and-retry) ──
        Write-Host "[7/8] Closing AI-Generated open issues..." -ForegroundColor Yellow
        $maxRetries = 3
        for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
            $issueClosed = 0
            $page = 1
            do {
                $issues = Invoke-RestMethod -Uri "$baseUrl/issues?state=open&labels=AI-Generated&per_page=100&page=$page" -Headers $headers
                foreach ($issue in $issues) {
                    if ($issue.pull_request) { continue } # skip PRs
                    try {
                        Invoke-RestMethod -Uri "$baseUrl/issues/$($issue.number)" -Method Patch -Headers $headers `
                            -Body '{"state":"closed"}' -ContentType "application/json" | Out-Null
                        $issueClosed++
                    } catch {
                        Write-Host "      WARN: Failed to close issue #$($issue.number): $($_.Exception.Message)" -ForegroundColor DarkYellow
                    }
                    Start-Sleep -Milliseconds 300
                }
                $page++
            } while ($issues.Count -eq 100)

            # Verify: re-check for any remaining open AI-Generated issues
            Start-Sleep -Seconds 2
            $remaining = Invoke-RestMethod -Uri "$baseUrl/issues?state=open&labels=AI-Generated&per_page=100" -Headers $headers
            $remainingIssues = @($remaining | Where-Object { -not $_.pull_request })
            if ($remainingIssues.Count -eq 0) {
                Write-Host "      Closed $issueClosed AI-Generated issue(s) — verified 0 remain" -ForegroundColor Green
                break
            }
            Write-Host "      Attempt ${attempt}/${maxRetries}: closed $issueClosed but $($remainingIssues.Count) still open, retrying..." -ForegroundColor DarkYellow
        }
        if ($remainingIssues.Count -gt 0) {
            Write-Host "      WARNING: $($remainingIssues.Count) AI-Generated issue(s) still open after $maxRetries attempts!" -ForegroundColor Red
            $remainingIssues | ForEach-Object { Write-Host "        #$($_.number): $($_.title)" -ForegroundColor Red }
        }

        # ── Step 8: Close AI-Generated open PRs (with verify-and-retry) ──
        Write-Host "[8/8] Closing AI-Generated open PRs..." -ForegroundColor Yellow
        for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
            $prClosed = 0
            $page = 1
            do {
                $prs = Invoke-RestMethod -Uri "$baseUrl/pulls?state=open&per_page=100&page=$page" -Headers $headers
                foreach ($pr in $prs) {
                    # Only close PRs with AI-Generated label
                    $prLabels = @($pr.labels | ForEach-Object { $_.name })
                    if ($prLabels -notcontains "AI-Generated") { continue }
                    try {
                        Invoke-RestMethod -Uri "$baseUrl/pulls/$($pr.number)" -Method Patch -Headers $headers `
                            -Body '{"state":"closed"}' -ContentType "application/json" | Out-Null
                        $prClosed++
                    } catch {
                        Write-Host "      WARN: Failed to close PR #$($pr.number): $($_.Exception.Message)" -ForegroundColor DarkYellow
                    }
                    Start-Sleep -Milliseconds 300
                }
                $page++
            } while ($prs.Count -eq 100)

            # Verify: re-check for any remaining open AI-Generated PRs
            Start-Sleep -Seconds 2
            $remainingPrs = @()
            $vPage = 1
            do {
                $vPrs = Invoke-RestMethod -Uri "$baseUrl/pulls?state=open&per_page=100&page=$vPage" -Headers $headers
                $remainingPrs += @($vPrs | Where-Object { ($_.labels | ForEach-Object { $_.name }) -contains "AI-Generated" })
                $vPage++
            } while ($vPrs.Count -eq 100)
            if ($remainingPrs.Count -eq 0) {
                Write-Host "      Closed $prClosed AI-Generated PR(s) — verified 0 remain" -ForegroundColor Green
                break
            }
            Write-Host "      Attempt ${attempt}/${maxRetries}: closed $prClosed but $($remainingPrs.Count) still open, retrying..." -ForegroundColor DarkYellow
        }
        if ($remainingPrs.Count -gt 0) {
            Write-Host "      WARNING: $($remainingPrs.Count) AI-Generated PR(s) still open after $maxRetries attempts!" -ForegroundColor Red
            $remainingPrs | ForEach-Object { Write-Host "        #$($_.number): $($_.title)" -ForegroundColor Red }
        }
    }
}

# ── Step 9: Final verification ──
Write-Host "`n[9/9] Final verification..." -ForegroundColor Yellow
$allGood = $true

# Verify DBs deleted
$resolvedRunnerDir2 = Resolve-Path $RunnerDir -ErrorAction SilentlyContinue
if ($resolvedRunnerDir2) {
    $leftoverDbs = Get-ChildItem $resolvedRunnerDir2 -Filter "agentsquad_*.db*" -ErrorAction SilentlyContinue
    if ($leftoverDbs) {
        Write-Host "      FAIL: $($leftoverDbs.Count) SQLite DB(s) still exist!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No SQLite DBs" -ForegroundColor Green
    }
}

# Verify workspaces cleaned
if (Test-Path $WorkspaceRoot) {
    $leftoverWs = Get-ChildItem $WorkspaceRoot -Directory -ErrorAction SilentlyContinue
    if ($leftoverWs) {
        Write-Host "      FAIL: $($leftoverWs.Count) workspace dir(s) still exist!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: Workspaces clean" -ForegroundColor Green
    }
} else {
    Write-Host "      OK: Workspace root doesn't exist" -ForegroundColor Green
}

# Verify no dotnet processes
$leftoverProcs = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddHours(-2) }
if ($leftoverProcs) {
    Write-Host "      FAIL: $($leftoverProcs.Count) dotnet process(es) still running!" -ForegroundColor Red
    $allGood = $false
} else {
    Write-Host "      OK: No runner processes" -ForegroundColor Green
}

# Verify no agent-created SME definitions
if ($resolvedRunnerDir2) {
    $smeFile2 = Join-Path $resolvedRunnerDir2 "sme-definitions.json"
    if (Test-Path $smeFile2) {
        try {
            $smeDefs2 = Get-Content $smeFile2 -Raw | ConvertFrom-Json -AsHashtable
            $stale = @($smeDefs2.Keys | Where-Object { $smeDefs2[$_].createdByAgentId })
            if ($stale.Count -gt 0) {
                Write-Host "      FAIL: $($stale.Count) agent-created SME def(s) still in sme-definitions.json!" -ForegroundColor Red
                $allGood = $false
            } else {
                Write-Host "      OK: No stale SME definitions" -ForegroundColor Green
            }
        } catch {
            Write-Host "      OK: No stale SME definitions (file parse skipped)" -ForegroundColor Green
        }
    } else {
        Write-Host "      OK: No SME definitions file" -ForegroundColor Green
    }
}

# Verify GitHub (if not skipped)
if (-not $SkipGitHub -and $GitHubToken) {
    $headers2 = @{ "Authorization" = "Bearer $GitHubToken"; "Accept" = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
    $baseUrl2 = "https://api.github.com/repos/$GitHubRepo"

    $openIssues = Invoke-RestMethod -Uri "$baseUrl2/issues?state=open&labels=AI-Generated&per_page=1" -Headers $headers2
    $openIssueCount = @($openIssues | Where-Object { -not $_.pull_request }).Count
    if ($openIssueCount -gt 0) {
        Write-Host "      FAIL: $openIssueCount open AI-Generated issue(s) remain!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No open AI-Generated issues" -ForegroundColor Green
    }

    $openPrs2 = Invoke-RestMethod -Uri "$baseUrl2/pulls?state=open&per_page=100" -Headers $headers2
    $aiPrs2 = @($openPrs2 | Where-Object { ($_.labels | ForEach-Object { $_.name }) -contains "AI-Generated" })
    if ($aiPrs2.Count -gt 0) {
        Write-Host "      FAIL: $($aiPrs2.Count) open AI-Generated PR(s) remain!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No open AI-Generated PRs" -ForegroundColor Green
    }

    $agBranches = Invoke-RestMethod -Uri "$baseUrl2/git/matching-refs/heads/agent/" -Headers $headers2 -ErrorAction SilentlyContinue
    if ($agBranches -and $agBranches.Count -gt 0) {
        Write-Host "      FAIL: $($agBranches.Count) agent branch(es) remain!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No agent branches" -ForegroundColor Green
    }

    # Verify repo files — only preserved files should remain
    $repoContents = Invoke-RestMethod -Uri "$baseUrl2/contents?ref=main" -Headers $headers2 -ErrorAction SilentlyContinue
    $extraFiles = @($repoContents | Where-Object { $_.name -notin $preserveList })
    if ($extraFiles.Count -gt 0) {
        Write-Host "      FAIL: Extra files remain: $($extraFiles.name -join ', ')" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: Only preserved files remain ($($preserveList -join ', '))" -ForegroundColor Green
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
if ($allGood) {
    Write-Host "  ✅ Fresh reset VERIFIED — all clean!" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  Fresh reset completed WITH WARNINGS — check above" -ForegroundColor Yellow
}
Write-Host "  Run: cd src\AgentSquad.Runner && dotnet run" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan
