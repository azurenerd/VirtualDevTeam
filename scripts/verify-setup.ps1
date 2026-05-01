#!/usr/bin/env pwsh
# verify-setup.ps1 — Checks AgentSquad prerequisites and configuration
# Run from the repository root: ./scripts/verify-setup.ps1

$ErrorActionPreference = "Continue"
$passed = 0
$failed = 0

function Test-Prereq {
    param([string]$Name, [string]$Command, [string]$MinVersion, [string]$InstallHint)
    
    Write-Host -NoNewline "  $Name... "
    try {
        $output = & $Command.Split(' ')[0] $Command.Split(' ')[1..99] 2>&1 | Select-Object -First 1
        Write-Host -ForegroundColor Green "PASS ($output)"
        $script:passed++
        return $true
    }
    catch {
        Write-Host -ForegroundColor Red "FAIL"
        Write-Host "    Install: $InstallHint" -ForegroundColor Yellow
        $script:failed++
        return $false
    }
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " AgentSquad — Setup Verification" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ── Prerequisites ──
Write-Host "Prerequisites:" -ForegroundColor White

# .NET SDK
Write-Host -NoNewline "  .NET SDK... "
$dotnetVersion = dotnet --version 2>&1
if ($LASTEXITCODE -eq 0) {
    $major = [int]($dotnetVersion -split '\.')[0]
    if ($major -ge 8) {
        Write-Host -ForegroundColor Green "PASS (v$dotnetVersion)"
        $passed++
    } else {
        Write-Host -ForegroundColor Red "FAIL (v$dotnetVersion — need 8+)"
        Write-Host "    Install: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        $failed++
    }
} else {
    Write-Host -ForegroundColor Red "FAIL (not found)"
    Write-Host "    Install: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    $failed++
}

# Git
Write-Host -NoNewline "  Git... "
$gitVersion = git --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host -ForegroundColor Green "PASS ($gitVersion)"
    $passed++
} else {
    Write-Host -ForegroundColor Red "FAIL (not found)"
    Write-Host "    Install: https://git-scm.com/downloads" -ForegroundColor Yellow
    $failed++
}

# GitHub Copilot CLI (optional but recommended)
Write-Host -NoNewline "  GitHub Copilot CLI... "
$copilotVersion = copilot --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host -ForegroundColor Green "PASS (v$copilotVersion)"
    $passed++
} else {
    Write-Host -ForegroundColor Yellow "SKIP (optional — AI features won't work without it)"
    Write-Host "    Install: https://docs.github.com/en/copilot/using-github-copilot/using-github-copilot-in-the-command-line" -ForegroundColor Yellow
}

# Node.js (optional, for MCP servers)
Write-Host -NoNewline "  Node.js... "
$nodeVersion = node --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host -ForegroundColor Green "PASS ($nodeVersion)"
    $passed++
} else {
    Write-Host -ForegroundColor Yellow "SKIP (optional — needed only for MCP tool servers)"
    Write-Host "    Install: https://nodejs.org/" -ForegroundColor Yellow
}

# npx (required for MCP servers)
Write-Host -NoNewline "  npx... "
$npxVersion = npx --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host -ForegroundColor Green "PASS (v$npxVersion)"
    $passed++
} else {
    Write-Host -ForegroundColor Yellow "SKIP (optional — needed for MCP tool servers like WorkIQ)"
    Write-Host "    Install Node.js (npx is included): https://nodejs.org/" -ForegroundColor Yellow
}

Write-Host ""

# ── Configuration ──
Write-Host "Configuration:" -ForegroundColor White

# Check if user secrets exist
$runnerCsproj = Join-Path $PSScriptRoot ".." "src" "AgentSquad.Runner" "AgentSquad.Runner.csproj"
if (Test-Path $runnerCsproj) {
    $content = Get-Content $runnerCsproj -Raw
    if ($content -match '<UserSecretsId>([^<]+)</UserSecretsId>') {
        $secretsId = $Matches[1]
        $secretsPath = Join-Path $env:APPDATA "Microsoft" "UserSecrets" $secretsId "secrets.json"
        Write-Host -NoNewline "  User Secrets... "
        if (Test-Path $secretsPath) {
            $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
            $hasGhToken = $secrets.'AgentSquad:Project:GitHubToken' -and $secrets.'AgentSquad:Project:GitHubToken'.Length -gt 0
            $hasAdoPat = $secrets.'AgentSquad:DevPlatform:AzureDevOps:Pat' -and $secrets.'AgentSquad:DevPlatform:AzureDevOps:Pat'.Length -gt 0
            if ($hasGhToken -or $hasAdoPat) {
                $tokenType = if ($hasGhToken) { "GitHub PAT" } else { "ADO PAT" }
                Write-Host -ForegroundColor Green "PASS ($tokenType configured)"
                $passed++
            } else {
                Write-Host -ForegroundColor Yellow "PARTIAL (file exists but no PAT found)"
                Write-Host "    You can enter your PAT in the /develop wizard or run:" -ForegroundColor Yellow
                Write-Host '    dotnet user-secrets set "AgentSquad:Project:GitHubToken" "ghp_..."' -ForegroundColor Yellow
            }
        } else {
            Write-Host -ForegroundColor Yellow "NOT SET (no secrets configured yet)"
            Write-Host "    You can enter your PAT in the /develop wizard when you start" -ForegroundColor Yellow
        }
    }
}

# Check if appsettings.json has a repo configured
$appsettings = Join-Path $PSScriptRoot ".." "src" "AgentSquad.Runner" "appsettings.json"
if (Test-Path $appsettings) {
    $config = Get-Content $appsettings -Raw | ConvertFrom-Json
    $repo = $config.AgentSquad.Project.GitHubRepo
    Write-Host -NoNewline "  Repository... "
    if ($repo -and $repo.Contains('/')) {
        Write-Host -ForegroundColor Green "PASS ($repo)"
        $passed++
    } else {
        Write-Host -ForegroundColor Yellow "NOT SET (will be configured via /develop wizard)"
    }
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if ($failed -eq 0) {
    Write-Host "Ready to go! Run:" -ForegroundColor Green
    Write-Host "  cd src/AgentSquad.Runner && dotnet run" -ForegroundColor White
    Write-Host "  Then open http://localhost:5050/develop" -ForegroundColor White
} else {
    Write-Host "Fix the issues above, then run this script again." -ForegroundColor Yellow
}

Write-Host ""
exit $failed
