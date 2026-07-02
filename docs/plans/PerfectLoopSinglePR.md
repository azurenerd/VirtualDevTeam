# Perfect Loop: Single-PR Framework Validation

## Purpose

Automated loop to validate that the Strategy Framework produces correct results for
single-PR tasks. The loop runs until **5 consecutive perfect iterations** are achieved.

## What "Perfect" Means

Each iteration must pass ALL of these criteria:

### T1 (Initial Round)
- [ ] **copilot-cli** produces non-empty patch (actual file changes)
- [ ] **squad** produces non-empty patch (actual file changes)
- [ ] Both strategies pass gate2-build (code compiles)
- [ ] Both strategies have screenshots captured (Playwright)
- [ ] Screenshots look correct (no missing CSS, no visual errors, no blank pages)
- [ ] Both strategies receive a judge score (LLM evaluation)
- [ ] If rework feedback is given, strategies address it correctly

### T-FINAL (Revision Round)
- [ ] Completes without errors (no `revision-patch-apply-failed`)
- [ ] Winner is selected
- [ ] Winner has screenshots captured
- [ ] Screenshots look correct
- [ ] Final patch is non-empty and buildable

## Loop Steps

```
┌─────────────────────────────────────────────────────────┐
│ 1. MINI RESET                                           │
│    - Run scripts/minimal-reset.ps1                      │
│    - Preserves Research.md, PMSpec.md, Architecture.md   │
│    - Clears: SQLite DBs, workspaces, agent branches,    │
│      open issues, open PRs                              │
│    - Fast-forwards to engineering tasks on restart       │
├─────────────────────────────────────────────────────────┤
│ 2. BUILD & START RUNNER                                 │
│    - dotnet build src/VirtualDevTeam.Runner             │
│    - cd src/VirtualDevTeam.Runner && dotnet run         │
│    - Verify responds at http://localhost:5050           │
├─────────────────────────────────────────────────────────┤
│ 3. MONITOR (poll every ≤60 seconds)                     │
│    - GET /api/strategies/active → shows running tasks   │
│    - GET /api/strategies/recent → shows completed tasks │
│    - Check candidate states, patch sizes, gate failures │
│    - Watch for: state progression through stages        │
├─────────────────────────────────────────────────────────┤
│ 4. VALIDATE RESULTS                                     │
│    - Both T1 candidates: state=Scored or Winner,        │
│      patchLinesAdded > 0, gateFailure = null,           │
│      screenshotPath non-null, judgeScore > 0            │
│    - T-FINAL: winner selected, screenshotPath non-null  │
├─────────────────────────────────────────────────────────┤
│ 5. ON FAILURE: RESEARCH & FIX                           │
│    - Check runner logs (stdout/stderr)                  │
│    - Check activity events via API                      │
│    - Identify root cause                                │
│    - Fix code                                           │
│    - Loop back to step 1                                │
├─────────────────────────────────────────────────────────┤
│ 6. ON SUCCESS: INCREMENT COUNTER                        │
│    - If 5 consecutive passes → DONE                     │
│    - Otherwise loop back to step 1                      │
└─────────────────────────────────────────────────────────┘
```

## API Endpoints for Monitoring

| Endpoint | Purpose |
|----------|---------|
| `GET /api/strategies/active` | Currently running framework tasks |
| `GET /api/strategies/recent?limit=5` | Recent completed tasks with full results |
| `GET /api/strategies/enabled` | Verify framework is enabled |
| `GET /api/runs/active` | Current run status and phase |

## Key Fields to Check in API Response

```json
{
  "taskId": "T1",
  "candidates": {
    "copilot-cli": {
      "state": 7,           // 7=Scored, 8=Winner
      "patchLinesAdded": 45,
      "patchLinesRemoved": 3,
      "filesChanged": 5,
      "judgeScore": 8.5,
      "gateFailure": null,  // null = passed all gates
      "screenshotPath": "/path/to/screenshot.png",
      "tokensUsed": 15000
    }
  },
  "winner": "copilot-cli",
  "outcomeDecision": "copilot-cli scored higher"
}
```

## CandidateState Values

| Value | Name | Meaning |
|-------|------|---------|
| 0 | Pending | Not started |
| 1 | Running | Strategy executing |
| 2 | Completed | Execution done, awaiting evaluation |
| 3 | Evaluated | Gates ran, screenshot taken |
| 4 | InitialScored | Judge scored, awaiting revision |
| 5 | Revising | Revision round in progress |
| 6 | Retrying | Gate-failed, retrying |
| 7 | Scored | Final score assigned |
| 8 | Winner | Selected as winning strategy |
| 9 | Cancelled | Cancelled by user |

## Common Failure Modes & Fixes

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Empty patch (0 lines) | CLI session ran but made no git changes | Inline retry in RunOneAsync triggers |
| `revision-patch-apply-failed` | Empty patch entering revision round | Skip ApplyPatch for empty patches |
| No screenshot | Playwright deps not restored | CLI-based dependency restore in PlaywrightRunner |
| Missing CSS in screenshot | libman/npm not run before capture | Same dep restore fix |
| `gate2-build` failure | Code doesn't compile | Strategy quality issue (not framework bug) |
| 0 tokens reported | Token tracking lost on retry | Accumulate tokens across attempts |
| Both strategies cancelled | Timeout too short | Check timeout config |

## Configuration Reference

Key settings in `appsettings.json` under `VirtualDevTeam:Strategies:`:

```json
{
  "Enabled": true,
  "EnabledStrategies": ["copilot-cli", "squad"],
  "TimeoutMinutes": 15,
  "GateRetry": {
    "MaxRetries": 1,
    "RetryTimeoutSeconds": 600,
    "RetryableGates": ["strategy-failed", "gate2-build", "gate1-output"]
  }
}
```

## Tracking Results

Use the SQL `framework_runs` table in session to track iterations:
```sql
SELECT run_number, t1_copilot_cli, t1_squad, t_final_result, winner, status, notes
FROM framework_runs ORDER BY run_number;
```
