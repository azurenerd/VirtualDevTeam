# AgentSquad Workflow Monitoring Checklist

> **Rule: Never assume — always verify.** Every phase must be confirmed via GitHub labels, PR comments, runner logs, and agent status. If an agent goes Idle with open work, that's a bug.

---

## Pre-Run Verification

Before starting a workflow run, confirm:

- [ ] All previous Issues are closed (query GitHub API, don't assume)
- [ ] All previous PRs are closed (query GitHub API, don't assume)
- [ ] Repository files are clean (only preserved files like `.gitignore`, `README.md`)
- [ ] No stale branches remain (only `main`)
- [ ] SQLite DB deleted (if fresh reset)
- [ ] Agent workspaces deleted (if fresh reset)
- [ ] Re-check all of the above after cleanup script runs — **do not proceed until verified**

---

## Phase Progression Tracking

For **every PR** that enters the pipeline, track it through ALL phases using a SQL monitoring table:

```sql
INSERT INTO pr_monitor (pr_number, title, author, phase, status, last_checked)
VALUES (707, 'PrincipalEngineer: Project Foundation', 'PE', 'phase1_architect', 'in_progress', datetime('now'));
```

### Phase 1: Architect Review

- [ ] PR has `ready-for-review` label
- [ ] Architect agent picks up the PR (check runner logs for "reviewing PR #X")
- [ ] Architect posts review comment on PR
- [ ] Verdict is APPROVED or REWORK:
  - **APPROVED**: `architect-approved` label added to PR (verify via GitHub API)
  - **REWORK**: `ChangesRequestedMessage` sent to author (verify in runner logs), author wakes from Idle
- [ ] Architect does **NOT** merge (no merge action in logs)
- [ ] If REWORK: author reworks → re-submits → Architect re-reviews (track retry count)

**Failure modes to watch for:**
- Architect goes Idle without reviewing the PR
- Label not added after approval
- Author not notified after REWORK

### Phase 2: Test Engineer

- [ ] TE scans for PRs with `architect-approved` label (verify in logs)
- [ ] TE checks out the PR's branch (same PR, not separate)
- [ ] **TE verifies base code builds** — if build fails:
  - [ ] TE posts comment with build errors
  - [ ] TE sends `ChangesRequestedMessage` to author engineer
  - [ ] Author wakes up, fixes build, pushes
  - [ ] TE retries on next poll cycle
- [ ] TE generates and commits test files to the PR branch
- [ ] TE runs tests: unit → integration → UI
- [ ] TE posts screenshots/videos as PR comments
- [ ] TE adds `tests-added` label (verify via GitHub API)

**Failure modes to watch for:**
- TE goes Idle saying "All PRs tested" but PR has no `tests-added` label
- Base build fails but no `ChangesRequestedMessage` sent (the bug we just fixed)
- Tests committed but build fails — TE should retry with AI fixes
- Screenshots not posted
- **TE workspace init timeout → "API-only mode" fallback** — check logs for "falling back to API mode". If present, TE committed files without building or running them. NO screenshots will be posted. This is a critical failure that invalidates the whole TE phase.

### Phase 3: PM Review

- [ ] PM polls for PRs with `tests-added` label
- [ ] PM reads code, PMSpec, screenshots/videos
- [ ] PM posts review comment with business validation
- [ ] Verdict is APPROVED or CHANGES REQUESTED:
  - **APPROVED**: `pm-approved` label added (verify via GitHub API)
  - **CHANGES REQUESTED**: `ChangesRequestedMessage` sent to author
- [ ] If CHANGES REQUESTED: author reworks → TE re-tests → PM re-reviews

**Failure modes to watch for:**
- PM never picks up PR (not polling for `tests-added`)
- PM approves without checking screenshots
- Author not notified on CHANGES REQUESTED

### Phase 4: Merge

- [ ] PE checks for PRs with BOTH `pm-approved` AND `tests-added` labels
- [ ] PE performs squash merge
- [ ] Branch deleted after merge
- [ ] Linked Issue auto-closed (if all PRs for that issue are done)

**Failure modes to watch for:**
- PE merges without both labels
- PE merges before TE adds tests (old bug, should be fixed)
- Branch not cleaned up

---

## Rework Cycle Tracking

Track rework cycles per phase per PR:

| PR | Phase | Cycle | Max | Action |
|----|-------|-------|-----|--------|
| 707 | Architect | 1/3 | MaxArchitectReworkCycles | Rework |
| 707 | PM | 0/3 | MaxPmReworkCycles | N/A |

- [ ] After max cycles reached, force-approval triggers with explanatory note
- [ ] Force-approval adds the appropriate label and notifies downstream

---

## Agent Health Checks (Every 2-3 Minutes)

Check runner logs for:

```
# Stuck agents — Idle with open work
"status changed: Working -> Idle" — verify the work actually completed

# Active work
"status changed: Idle -> Working" — note what they're working on

# Error patterns
"Exception", "Error", "Failed", "rate limit", "timeout"
```

### Red Flags (Investigate Immediately)

1. **Agent Idle + PR in their phase without expected label** = stuck
2. **Agent Working for >10 minutes on same PR** = possibly hung
3. **"All PRs tested" but untested PRs exist** = TE scanning bug
4. **No bus messages after review** = notification gap
5. **Agent cycling Idle → Idle repeatedly** = no work being found (check label gates)

---

## End-of-Run Verification

After workflow completes or is stopped:

- [ ] All PRs either merged or have clear status (not abandoned mid-phase)
- [ ] All Issues either closed or have open PRs still in pipeline
- [ ] No orphaned branches
- [ ] Dashboard shows accurate agent status
- [ ] Runner logs show clean shutdown (no unhandled exceptions)

---

## Log Queries Reference

```powershell
# Check specific PR progress
Get-Content runner-output-runXX.log | Select-String "PR #707" | Select-Object -Last 20

# Check agent status changes
Get-Content runner-output-runXX.log | Select-String "status changed" | Select-Object -Last 30

# Check for errors
Get-Content runner-output-runXX.log | Select-String "Error|Exception|Failed" | Select-Object -Last 20

# Check label additions
Get-Content runner-output-runXX.log | Select-String "architect-approved|tests-added|pm-approved" | Select-Object -Last 20

# Check bus messages
Get-Content runner-output-runXX.log | Select-String "ChangesRequested|ReviewRequest|StatusUpdate" | Select-Object -Last 20
```

```powershell
# GitHub API verification
$headers = @{ Authorization = "token $token"; Accept = "application/vnd.github.v3+json" }

# Check PR labels
(Invoke-RestMethod -Uri "https://api.github.com/repos/azurenerd/ReportingDashboard/pulls/707" -Headers $headers).labels.name

# Check open PRs
(Invoke-RestMethod -Uri "https://api.github.com/repos/azurenerd/ReportingDashboard/pulls?state=open" -Headers $headers) | ForEach-Object { "$($_.number): $($_.title) [$($_.labels.name -join ',')]" }

# Check open issues
(Invoke-RestMethod -Uri "https://api.github.com/repos/azurenerd/ReportingDashboard/issues?state=open&per_page=100" -Headers $headers) | Where-Object { -not $_.pull_request } | ForEach-Object { "$($_.number): $($_.title)" }
```

---

## Change Log

| Date | Change |
|------|--------|
| 2026-04-11 | Initial version — sequential pipeline (Architect→TE→PM→merge), same-PR testing, visual evidence, multi-phase rework |
| 2026-04-11 | Added TE build-failure notification gap (fixed: TE now sends ChangesRequestedMessage on build failure) |
| 2026-04-11 | Added TE workspace init failure mode: git clone timeout → silent API-only fallback → no screenshots/no test execution |
| 2026-04-11 | Added PE self-merge bug: PE was skipping own PRs in MergeTestedPRsAsync (fixed) |
