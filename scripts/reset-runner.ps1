<#
.SYNOPSIS
    Full reset: stop runner, clean GitHub repo, delete local workspaces and DB.
.DESCRIPTION
    Performs a complete cleanup so the next runner start begins from a fresh state:
    1. Stops any running AgentSquad runner process
    2. Closes all open issues and PRs on the target GitHub repo
    3. Deletes all agent branches (keeps main/master only)
    4. Deletes agent-generated files from the repo (keeps only preserved files)
    5. Deletes local agent workspace directories (C:\Agents by default)
    6. Deletes the SQLite checkpoint/state DB
    7. Verifies the repo is fully clean before reporting success
.EXAMPLE
    .\scripts\reset-runner.ps1
    .\scripts\reset-runner.ps1 -WorkspaceRoot "D:\AgentWorkspaces" -SkipGitHub
#>
param(
    [string]$WorkspaceRoot = "C:\Agents",
    [string]$SettingsPath = (Join-Path $PSScriptRoot ".." "src" "AgentSquad.Runner" "appsettings.json"),
    [string]$RunnerProjectDir = (Join-Path $PSScriptRoot ".." "src" "AgentSquad.Runner"),
    [string]$PreserveFiles = "OriginalDesignConcept.html,.gitignore",
    [switch]$SkipGitHub,
    [switch]$SkipLocal
)

$ErrorActionPreference = "Continue"
$preserveList = $PreserveFiles -split "," | ForEach-Object { $_.Trim() }

Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  AgentSquad Full Reset" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

# ── Phase 1: Stop runner ────────────────────────────
Write-Host "`n▶ Phase 1/5: Stopping runner..." -ForegroundColor Yellow
& (Join-Path $PSScriptRoot "stop-runner.ps1") -ErrorAction SilentlyContinue

# ── Phase 2: Clean GitHub ───────────────────────────
if (-not $SkipGitHub) {
    Write-Host "`n▶ Phase 2/5: Cleaning GitHub repository..." -ForegroundColor Yellow

    # Read repo name from appsettings.json
    if (-not (Test-Path $SettingsPath)) {
        Write-Host "  ⚠ appsettings.json not found at $SettingsPath — skipping GitHub cleanup" -ForegroundColor Red
    } else {
        $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
        $repo = $settings.AgentSquad.Project.GitHubRepo

        # Get PAT: try user-secrets first, fall back to appsettings.json
        $pat = $null
        try {
            $secretsOutput = dotnet user-secrets list --project $RunnerProjectDir 2>&1
            $patLine = $secretsOutput | Where-Object { $_ -match 'GitHubToken' }
            if ($patLine) {
                $pat = ($patLine -split '= ', 2)[1].Trim()
            }
        } catch { }

        if (-not $pat) {
            $pat = $settings.AgentSquad.Project.GitHubToken
        }

        if (-not $pat) {
            Write-Host "  ⚠ No PAT found in user-secrets or appsettings.json — skipping GitHub cleanup" -ForegroundColor Red
            Write-Host "    Set PAT via: dotnet user-secrets set 'AgentSquad:Project:GitHubToken' '<your-pat>' --project src\AgentSquad.Runner" -ForegroundColor Gray
        } else {
            $headers = @{ Authorization = "token $pat"; Accept = "application/vnd.github+json" }
            Write-Host "  Repo: $repo" -ForegroundColor Gray

            # Close all open issues (paginated, re-fetch page 1 each pass)
            $closedIssues = 0
            do {
                $issues = Invoke-RestMethod "https://api.github.com/repos/$repo/issues?state=open&per_page=100&page=1" -Headers $headers -ErrorAction SilentlyContinue
                $issueOnly = @($issues | Where-Object { -not $_.pull_request })
                foreach ($i in $issueOnly) {
                    Invoke-RestMethod "https://api.github.com/repos/$repo/issues/$($i.number)" -Method Patch -Headers $headers -Body '{"state":"closed"}' -ContentType 'application/json' -ErrorAction SilentlyContinue | Out-Null
                    $closedIssues++
                }
            } while ($issueOnly.Count -gt 0)
            Write-Host "  Closed $closedIssues issues" -ForegroundColor Green

            # Close all open PRs (paginated, re-fetch page 1 each pass)
            $closedPrs = 0
            do {
                $prs = Invoke-RestMethod "https://api.github.com/repos/$repo/pulls?state=open&per_page=100&page=1" -Headers $headers -ErrorAction SilentlyContinue
                foreach ($pr in $prs) {
                    Invoke-RestMethod "https://api.github.com/repos/$repo/pulls/$($pr.number)" -Method Patch -Headers $headers -Body '{"state":"closed"}' -ContentType 'application/json' -ErrorAction SilentlyContinue | Out-Null
                    $closedPrs++
                }
            } while ($prs.Count -gt 0)
            Write-Host "  Closed $closedPrs PRs" -ForegroundColor Green

            # Delete all non-main branches
            $deletedBranches = 0
            $branches = Invoke-RestMethod "https://api.github.com/repos/$repo/branches?per_page=100" -Headers $headers -ErrorAction SilentlyContinue
            foreach ($b in $branches) {
                if ($b.name -eq "main" -or $b.name -eq "master") { continue }
                Invoke-RestMethod "https://api.github.com/repos/$repo/git/refs/heads/$($b.name)" -Method Delete -Headers $headers -ErrorAction SilentlyContinue | Out-Null
                $deletedBranches++
            }
            Write-Host "  Deleted $deletedBranches branches" -ForegroundColor Green

            # Delete repo files not in preserve list
            Write-Host "  Cleaning repo files (preserving: $($preserveList -join ', '))..." -ForegroundColor Cyan
            $branchInfo = Invoke-RestMethod "https://api.github.com/repos/$repo/branches/main" -Headers $headers
            $treeSha = $branchInfo.commit.commit.tree.sha
            $tree = Invoke-RestMethod "https://api.github.com/repos/$repo/git/trees/$($treeSha)?recursive=1" -Headers $headers
            $filesToDelete = $tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -notin $preserveList }
            $deletedFiles = 0
            foreach ($f in $filesToDelete) {
                try {
                    $fileInfo = Invoke-RestMethod "https://api.github.com/repos/$repo/contents/$($f.path)" -Headers $headers
                    $body = @{ message = "Reset: delete $($f.path)"; sha = $fileInfo.sha } | ConvertTo-Json
                    Invoke-RestMethod "https://api.github.com/repos/$repo/contents/$($f.path)" -Headers $headers -Method Delete -Body $body -ContentType "application/json" | Out-Null
                    $deletedFiles++
                } catch {
                    Write-Host "    WARN: Failed to delete $($f.path): $($_.Exception.Message)" -ForegroundColor DarkYellow
                }
            }
            Write-Host "  Deleted $deletedFiles files from repo" -ForegroundColor Green

            # ── Verify GitHub is clean ──
            Write-Host "`n  Verifying GitHub cleanup..." -ForegroundColor Cyan
            $allClean = $true
            Start-Sleep -Seconds 2

            # Verify issues/PRs
            $verifyPage = 1; $verifyTotal = 0
            do {
                $batch = Invoke-RestMethod "https://api.github.com/repos/$repo/issues?state=open&per_page=100&page=$verifyPage" -Headers $headers
                $verifyTotal += $batch.Count; $verifyPage++
            } while ($batch.Count -eq 100)
            if ($verifyTotal -gt 0) {
                Write-Host "  ✗ FAIL: $verifyTotal open issues/PRs remain!" -ForegroundColor Red
                $allClean = $false
            } else {
                Write-Host "  ✓ No open issues/PRs" -ForegroundColor Green
            }

            # Verify branches
            $verifyBranches = Invoke-RestMethod "https://api.github.com/repos/$repo/branches?per_page=100" -Headers $headers
            $nonMain = @($verifyBranches | Where-Object { $_.name -ne 'main' -and $_.name -ne 'master' })
            if ($nonMain.Count -gt 0) {
                Write-Host "  ✗ FAIL: $($nonMain.Count) non-main branches remain: $($nonMain.name -join ', ')" -ForegroundColor Red
                $allClean = $false
            } else {
                Write-Host "  ✓ Only main branch" -ForegroundColor Green
            }

            # Verify repo files
            $verifyContents = Invoke-RestMethod "https://api.github.com/repos/$repo/contents?ref=main" -Headers $headers
            $extraFiles = @($verifyContents | Where-Object { $_.name -notin $preserveList })
            if ($extraFiles.Count -gt 0) {
                Write-Host "  ✗ FAIL: Extra files remain: $($extraFiles.name -join ', ')" -ForegroundColor Red
                $allClean = $false
            } else {
                Write-Host "  ✓ Only preserved files remain: $($verifyContents.name -join ', ')" -ForegroundColor Green
            }

            if (-not $allClean) {
                Write-Host "  ⚠ GitHub cleanup incomplete — review failures above" -ForegroundColor Yellow
            }
        }
    }
} else {
    Write-Host "`n▶ Phase 2/5: Skipping GitHub cleanup (--SkipGitHub)" -ForegroundColor Gray
}

# ── Phase 3: Clean local workspaces ─────────────────
if (-not $SkipLocal) {
    Write-Host "`n▶ Phase 3/5: Cleaning local agent workspaces..." -ForegroundColor Yellow

    if (Test-Path $WorkspaceRoot) {
        $dirs = Get-ChildItem -Path $WorkspaceRoot -Directory -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            Remove-Item -Path $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  Deleted $($dirs.Count) workspace directories from $WorkspaceRoot" -ForegroundColor Green
    } else {
        Write-Host "  No workspace directory at $WorkspaceRoot" -ForegroundColor Gray
    }
} else {
    Write-Host "`n▶ Phase 3/5: Skipping local cleanup (--SkipLocal)" -ForegroundColor Gray
}

# ── Phase 4: Delete checkpoint DB ────────────────────
Write-Host "`n▶ Phase 4/5: Deleting checkpoint database..." -ForegroundColor Yellow
$runnerDir = Join-Path $PSScriptRoot ".." "src" "AgentSquad.Runner"
$dbFiles = Get-ChildItem -Path $runnerDir -Filter "agentsquad_*.db*" -ErrorAction SilentlyContinue
if ($dbFiles) {
    foreach ($db in $dbFiles) {
        Remove-Item -Path $db.FullName -Force -ErrorAction SilentlyContinue
        Write-Host "  Deleted $($db.Name)" -ForegroundColor Green
    }
} else {
    Write-Host "  No DB files found" -ForegroundColor Gray
}

# ── Phase 5: Final summary ──────────────────────────
Write-Host "`n═══════════════════════════════════════════" -ForegroundColor Green
Write-Host "  ✅ Reset complete. Run start-runner.ps1 to begin fresh." -ForegroundColor Green
Write-Host "═══════════════════════════════════════════" -ForegroundColor Green
