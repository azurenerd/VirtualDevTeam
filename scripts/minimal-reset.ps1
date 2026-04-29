<#
.SYNOPSIS
    Minimal reset for AgentSquad — clears state but preserves key startup documents.

.DESCRIPTION
    Like fresh-reset.ps1 but keeps Research.md, PMSpec.md, Architecture.md, and
    OriginalDesignConcept.html in the repo so the agent pipeline fast-forwards
    past the research/architecture phases straight to engineering tasks.

    Items cleaned:
      1. Kill running dotnet processes
      2. Delete SQLite checkpoint DBs + SME definitions
      3. Delete agent workspaces (C:\Agents\*)
      4. Delete Playwright temp files
      5. Clean GitHub repo files (keep preserved docs)
      6. Delete remote agent branches
      7. Close all open issues
      8. Close all open PRs

.PARAMETER GitHubRepo
    The GitHub repo in owner/repo format. If not provided, reads from appsettings.json.

.PARAMETER GitHubToken
    GitHub PAT. If not provided, reads from dotnet user-secrets then appsettings.json.

.PARAMETER PreserveFiles
    Comma-separated files to keep. Defaults include startup docs.

.EXAMPLE
    .\scripts\minimal-reset.ps1
    .\scripts\minimal-reset.ps1 -GitHubToken "ghp_xxx"
#>

param(
    [string]$GitHubRepo = "",
    [string]$GitHubToken = "",
    [string]$PreserveFiles = "OriginalDesignConcept.html,Research.md,PMSpec.md,Architecture.md",
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
Write-Host "  AgentSquad Minimal Reset" -ForegroundColor Cyan
Write-Host "  (preserves startup docs)" -ForegroundColor DarkCyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Repo:      $GitHubRepo"
Write-Host "  Preserve:  $($preserveList -join ', ')"
Write-Host "  Workspace: $WorkspaceRoot"
Write-Host "========================================`n" -ForegroundColor Cyan

# ── Step 1: Kill running dotnet processes ──
Write-Host "[1/9] Killing recent dotnet processes..." -ForegroundColor Yellow
$killed = 0
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -gt (Get-Date).AddHours(-2) } |
    ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $killed++
    }
Write-Host "      Killed $killed process(es)" -ForegroundColor Green

# ── Step 2: Delete SQLite checkpoint DBs + SME defs ──
# NOTE: develop-settings.json is user-specific and must survive resets.
# The filter "agentsquad_*.db*" is intentionally narrow to avoid touching it.
Write-Host "[2/9] Deleting SQLite checkpoint DBs..." -ForegroundColor Yellow
$dbCount = 0
$resolvedRunnerDir = Resolve-Path $RunnerDir -ErrorAction SilentlyContinue
if ($resolvedRunnerDir) {
    Get-ChildItem $resolvedRunnerDir -Filter "agentsquad_*.db*" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force
        $dbCount++
    }
    # Purge agent-created SME definitions
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
Write-Host "[3/9] Cleaning agent workspaces ($WorkspaceRoot)..." -ForegroundColor Yellow
$wsCount = 0
if (Test-Path $WorkspaceRoot) {
    Get-ChildItem $WorkspaceRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $wsCount++
    }
}
Write-Host "      Removed $wsCount workspace(s)" -ForegroundColor Green

# ── Step 4: Delete Playwright temp files ──
Write-Host "[4/9] Cleaning Playwright temp files..." -ForegroundColor Yellow
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
    }
    else {
        $owner, $repo = $GitHubRepo -split "/"
        $headers = @{
            "Authorization" = "Bearer $GitHubToken"
            "Accept"        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $baseUrl = "https://api.github.com/repos/$owner/$repo"

        # ── Step 5: Clean repo files via local git clone (preserving docs) ──
        Write-Host "[5/9] Cleaning repo files (preserving startup docs)..." -ForegroundColor Yellow
        $tempDir = Join-Path $env:TEMP "agentsquad-reset-$(Get-Random)"
        try {
            $cloneUrl = "https://x-access-token:${GitHubToken}@github.com/$owner/$repo.git"
            git clone $cloneUrl $tempDir 2>&1 | Out-Null

            Push-Location $tempDir
            git config user.email "agentsquad-cleanup@noreply"
            git config user.name "AgentSquad Reset"

            $allFiles = Get-ChildItem -Recurse -File | Where-Object { $_.FullName -notmatch "\\\.git\\" }
            $deleted = 0
            $kept = 0
            foreach ($f in $allFiles) {
                $rel = $f.FullName.Replace("$tempDir\", "").Replace("\", "/")
                $keep = $false
                foreach ($p in $preserveList) {
                    if ($rel -eq $p -or $f.Name -eq $p) { $keep = $true; break }
                }
                if ($keep) {
                    $kept++
                } else {
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
                git commit -m "Minimal reset — preserved $kept startup doc(s), removed $deleted other file(s)" 2>&1 | Out-Null
                git push origin main 2>&1 | Out-Null
            }
            Pop-Location
            Write-Host "      Deleted $deleted file(s), preserved $kept file(s): $($preserveList -join ', ')" -ForegroundColor Green
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
        Write-Host "[6/9] Deleting remote agent branches..." -ForegroundColor Yellow
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

        # ── Step 7: Close all open issues ──
        Write-Host "[7/9] Closing all open issues..." -ForegroundColor Yellow
        $maxRetries = 3
        for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
            $issueClosed = 0
            $page = 1
            do {
                $issueUri = "${baseUrl}/issues?state=open" + '&per_page=100' + '&page=' + $page
                $issues = Invoke-RestMethod -Uri $issueUri -Headers $headers
                foreach ($issue in $issues) {
                    if ($issue.pull_request) { continue }
                    try {
                        Invoke-RestMethod -Uri "$baseUrl/issues/$($issue.number)" -Method Patch -Headers $headers `
                            -Body '{"state":"closed"}' -ContentType "application/json" | Out-Null
                        $issueClosed++
                    } catch {
                        Write-Host "      WARN: Failed to close issue #$($issue.number)" -ForegroundColor DarkYellow
                    }
                    Start-Sleep -Milliseconds 300
                }
                $page++
            } while ($issues.Count -eq 100)

            Start-Sleep -Seconds 2
            $remainUri = "${baseUrl}/issues?state=open" + '&per_page=100'
            $remaining = Invoke-RestMethod -Uri $remainUri -Headers $headers
            $remainingIssues = @($remaining | Where-Object { -not $_.pull_request })
            if ($remainingIssues.Count -eq 0) {
                Write-Host "      Closed $issueClosed issue(s) — verified 0 open remain" -ForegroundColor Green
                break
            }
            Write-Host "      Attempt ${attempt}/${maxRetries}: $($remainingIssues.Count) still open, retrying..." -ForegroundColor DarkYellow
        }

        # ── Step 8: Close all open PRs ──
        Write-Host "[8/9] Closing all open PRs..." -ForegroundColor Yellow
        for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
            $prClosed = 0
            $page = 1
            do {
                $prUri = "${baseUrl}/pulls?state=open" + '&per_page=100' + '&page=' + $page
                $prs = Invoke-RestMethod -Uri $prUri -Headers $headers
                foreach ($pr in $prs) {
                    try {
                        Invoke-RestMethod -Uri "$baseUrl/pulls/$($pr.number)" -Method Patch -Headers $headers `
                            -Body '{"state":"closed"}' -ContentType "application/json" | Out-Null
                        $prClosed++
                    } catch {
                        Write-Host "      WARN: Failed to close PR #$($pr.number)" -ForegroundColor DarkYellow
                    }
                    Start-Sleep -Milliseconds 300
                }
                $page++
            } while ($prs.Count -eq 100)

            Start-Sleep -Seconds 2
            $remainPrUri = "${baseUrl}/pulls?state=open" + '&per_page=100'
            $remainingPrs = Invoke-RestMethod -Uri $remainPrUri -Headers $headers
            if ($remainingPrs.Count -eq 0) {
                Write-Host "      Closed $prClosed PR(s) — verified 0 open remain" -ForegroundColor Green
                break
            }
            Write-Host "      Attempt ${attempt}/${maxRetries}: $($remainingPrs.Count) still open, retrying..." -ForegroundColor DarkYellow
        }
    }
}

# ── Step 9: Final verification ──
Write-Host "`n[9/9] Final verification..." -ForegroundColor Yellow
$allGood = $true

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

if (Test-Path $WorkspaceRoot) {
    $leftoverWs = Get-ChildItem $WorkspaceRoot -Directory -ErrorAction SilentlyContinue
    if ($leftoverWs) {
        Write-Host "      FAIL: $($leftoverWs.Count) workspace dir(s) still exist!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: Workspaces clean" -ForegroundColor Green
    }
} else {
    Write-Host "      OK: Workspace root does not exist" -ForegroundColor Green
}

$leftoverProcs = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddHours(-2) }
if ($leftoverProcs) {
    Write-Host "      FAIL: $($leftoverProcs.Count) dotnet process(es) still running!" -ForegroundColor Red
    $allGood = $false
} else {
    Write-Host "      OK: No runner processes" -ForegroundColor Green
}

# Verify preserved files exist in repo
if (-not $SkipGitHub -and $GitHubToken) {
    $headers2 = @{ "Authorization" = "Bearer $GitHubToken"; "Accept" = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
    $baseUrl2 = "https://api.github.com/repos/$GitHubRepo"
    foreach ($pf in $preserveList) {
        try {
            $null = Invoke-RestMethod -Uri "$baseUrl2/contents/$pf" -Headers $headers2 -ErrorAction Stop
            Write-Host "      OK: $pf exists in repo" -ForegroundColor Green
        } catch {
            Write-Host "      WARN: $pf NOT found in repo" -ForegroundColor Yellow
        }
    }

    $openIssUri = "${baseUrl2}/issues?state=open" + '&per_page=1'
    $openIssues = Invoke-RestMethod -Uri $openIssUri -Headers $headers2
    $openIssueCount = @($openIssues | Where-Object { -not $_.pull_request }).Count
    if ($openIssueCount -gt 0) {
        Write-Host "      FAIL: $openIssueCount open issue(s) remain!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No open issues" -ForegroundColor Green
    }

    $openPrUri2 = "${baseUrl2}/pulls?state=open" + '&per_page=1'
    $openPrs2 = Invoke-RestMethod -Uri $openPrUri2 -Headers $headers2
    if ($openPrs2.Count -gt 0) {
        Write-Host "      FAIL: $($openPrs2.Count) open PR(s) remain!" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "      OK: No open PRs" -ForegroundColor Green
    }
}

# Verify no stale SME definitions
if ($resolvedRunnerDir2) {
    $smeFile2 = Join-Path $resolvedRunnerDir2 "sme-definitions.json"
    if (Test-Path $smeFile2) {
        try {
            $smeDefs2 = Get-Content $smeFile2 -Raw | ConvertFrom-Json -AsHashtable
            $stale = @($smeDefs2.Keys | Where-Object { $smeDefs2[$_].createdByAgentId })
            if ($stale.Count -gt 0) {
                Write-Host "      FAIL: $($stale.Count) agent-created SME def(s) remain!" -ForegroundColor Red
                $allGood = $false
            } else {
                Write-Host "      OK: No stale SME definitions" -ForegroundColor Green
            }
        } catch {
            Write-Host "      OK: No stale SME definitions" -ForegroundColor Green
        }
    }
}

Write-Host ""
if ($allGood) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  MINIMAL RESET COMPLETE — ALL CHECKS PASSED" -ForegroundColor Green
    $preservedStr = $preserveList -join ", "
    Write-Host "  Preserved: $preservedStr" -ForegroundColor Green
    Write-Host "  Pipeline will fast-forward to engineering tasks" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  RESET COMPLETE WITH WARNINGS" -ForegroundColor Red
    Write-Host "  Check failures above" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
}
