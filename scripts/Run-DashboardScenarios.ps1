<#
.SYNOPSIS
    Runs the AgentSquad Dashboard Playwright scenario tests in a loop until all pass.
.DESCRIPTION
    Ensures the dashboard and runner are running, then runs all 11 scenario tests.
    Retries up to 3 times on failure, regenerates Scenarios.md screenshots on success.
.EXAMPLE
    .\scripts\Run-DashboardScenarios.ps1
#>
param(
    [int]$MaxRetries = 3,
    [string]$DashboardUrl = "http://localhost:5051",
    [string]$RunnerUrl = "http://localhost:5050"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "=== AgentSquad Dashboard Scenario Test Runner ===" -ForegroundColor Cyan

# Check dashboard is running
Write-Host "Checking dashboard at $DashboardUrl..." -NoNewline
try {
    $null = Invoke-WebRequest -Uri $DashboardUrl -TimeoutSec 5 -UseBasicParsing
    Write-Host " OK" -ForegroundColor Green
} catch {
    Write-Host " NOT RUNNING" -ForegroundColor Red
    Write-Host "Please start the dashboard first: dotnet run --project src/AgentSquad.Dashboard"
    exit 1
}

# Check runner is running
Write-Host "Checking runner API at $RunnerUrl..." -NoNewline
try {
    $null = Invoke-WebRequest -Uri $RunnerUrl -TimeoutSec 5 -UseBasicParsing
    Write-Host " OK" -ForegroundColor Green
} catch {
    Write-Host " NOT RUNNING (some scenarios may fail)" -ForegroundColor Yellow
}

$attempt = 0
$allPassed = $false

while ($attempt -lt $MaxRetries -and -not $allPassed) {
    $attempt++
    Write-Host "`n--- Attempt $attempt of $MaxRetries ---" -ForegroundColor Yellow

    $result = & dotnet test "$repoRoot\tests\AgentSquad.Dashboard.Tests" --no-build 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        $allPassed = $true
        Write-Host "`n✅ All scenarios PASSED!" -ForegroundColor Green
    } else {
        Write-Host "`n❌ Some scenarios failed (attempt $attempt)" -ForegroundColor Red
        $result | Select-String "Failed" | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }

        if ($attempt -lt $MaxRetries) {
            Write-Host "Retrying in 5 seconds..."
            Start-Sleep -Seconds 5
        }
    }
}

# Copy screenshots to docs
if ($allPassed) {
    $screenshotSrc = "$repoRoot\tests\AgentSquad.Dashboard.Tests\bin\Debug\net8.0\test-results\scenarios\screenshots"
    $screenshotDst = "$repoRoot\docs\scenario-screenshots"

    if (Test-Path $screenshotSrc) {
        New-Item -Path $screenshotDst -ItemType Directory -Force | Out-Null
        Copy-Item "$screenshotSrc\*.png" $screenshotDst -Force
        Write-Host "Screenshots copied to docs/scenario-screenshots/" -ForegroundColor Cyan
    }
}

if (-not $allPassed) {
    Write-Host "`n❌ Tests still failing after $MaxRetries attempts" -ForegroundColor Red
    exit 1
}
