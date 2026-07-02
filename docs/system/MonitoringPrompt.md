# VDT Pipeline Monitoring Prompt

Read `Session.md` first (mandatory per copilot-instructions.md). Then perform these monitoring checks:

## Pre-Flight
1. Verify the Runner is running: `Get-NetTCPConnection -LocalPort 5050 -State Listen`
2. If not running, report to user and stop — do NOT start the runner yourself.
3. Verify Playwright MCP tools are available (try `playwright-browser_navigate`). If not, fall back to API-only monitoring.

## Monitoring Cycle (every 5 minutes, configurable)

### 1. Dashboard Visual Sweep (Playwright)
Screenshot each dashboard page at viewport 1440×900. For each, validate the expected state based on the current workflow phase.

| Page | URL | What to validate |
|------|-----|-----------------|
| **Agents** | `/agents` | All expected agents have cards. No `Error` status. Working agents show descriptive AI-call context (not generic "AI call in progress"). No two agents claim same PR. |
| **Timeline** | `/timeline` | Current phase + elapsed time shown. Wave columns populated during ParallelDev. Task badges show correct status progression. No backward phase jumps. |
| **Repository** | `/repository` | Three tabs (Code, PRs, Issues). Open PRs have agent names in titles, expected labels, recent updates. No orphan PRs without issues. |
| **Strategies** | `/strategies` | Active task cards show enabled candidates. No candidate stuck in RUNNING >30 min with no activity. PR-link badges present on completed tasks. No all-Failed candidate sets. |
| **Approvals** | `/approvals` | No gates pending >30 min (unless architecture/PM which need human review). PrePRClarification auto-approved when gate disabled. No stuck rework spinners >10 min. |
| **Flow Monitor** | `/flow-monitor` | No Critical/High unresolved findings. No findings ActedOn but not Resolved for >1h. Stuck-strategy findings trigger reset/cancel actions. |
| **Metrics** | `/metrics` | Token usage rising during active phases (not flat-lined). Model distribution matches expected config (all on same tier). |
| **Reasoning** | `/reasoning` | Recent decisions for Working agents (<10 min old). No repeating decision text (agent loop). |
| **Configuration** | `/configuration` | Gate toggles match develop-settings.json. Strategy config matches expected enabled strategies. |

### 2. API Health Checks (always, even without Playwright)
```
GET /api/health-snapshot          → phase, agent counts, LLM in-flight, cost
GET /api/dashboard/agents         → per-agent status + reason
GET /api/dashboard/platform/pull-requests  → PR count, labels, states
GET /api/dashboard/platform/work-items     → WI count, open/closed
GET /api/dashboard/health/flow-monitor     → findings count + severity
GET /api/dashboard/decisions/pending       → pending gate decisions
```

### 3. Flow Monitor Findings Assessment
- Check each finding against expected pipeline behavior for current phase
- **Critical findings** → investigate immediately, root cause, fix if possible
- **agent-stuck** → check if FlowMonitor's escalation ladder fired (kick → reset → cancel)
- **stuck-strategy** → verify StuckStrategyCandidateDetector detected it and CancelStrategyCandidateAction or ResetStrategyCandidateAction responded
- **phase-advancement-watchdog** → check if it's a false positive (phase just started) or real (pipeline stuck)

### 4. Strategy Framework Health
- Any candidate RUNNING >15 min with 0 activity entries → flag as potentially stuck (StuckSeconds=600 should kill at 10 min)
- Verify the retry escalation ladder worked: check for retry activity entries ("⚠️ Strategy failed... Retry 1/2" or "Retry 2/2 rung 2 — no wrapper")
- If all candidates failed for a task, check if the orchestrator archived the task or retried

### 5. Process Health
- Count orphan node processes with `@playwright/mcp` or `@modelcontextprotocol/server-` in command line
- If count > 20, run `scripts/kill-orphan-runner-procs.ps1 -WhatIf` and report
- Check copilot process count and CPU — any at 0s CPU for >5 min is suspicious

### 6. Scenario Validation (per Requirements.md §20)
Based on the current workflow phase, validate these end-to-end scenarios:

**During ParallelDevelopment:**
- SE Leader should have created engineering tasks (WIs exist)
- SE Workers should be claiming tasks (status: "Scanning for available tasks" → "Working")
- Strategy framework should show active candidates for in-progress tasks
- PRs should transition: created → in-progress → ready-for-review

**During Review (after ready-for-review):**
- Architect should pick up PRs with ready-for-review label
- TE should run after architect-approved
- PM should run after tests-added
- Labels should progress: in-progress → ready-for-review → architect-approved → tests-added → pm-approved

**During Merge:**
- SE should merge PRs with both pm-approved + tests-added
- Work items should transition to closed
- Next wave tasks should become eligible

## Actions (when issues found)

| Issue | Action |
|-------|--------|
| Agent stuck >30 min | Check FlowMonitor findings. If no auto-action fired, investigate root cause. |
| Strategy candidate stuck | Verify StuckSeconds=600 killed it. If not, check logs for why. |
| All candidates failed | Check if retry escalation ran (rung 1 + rung 2). Review logs for failure reasons. |
| Gate pending >30 min | Auto-approve PrePRClarification gates. Report architecture/PM gates for human review. |
| Runner crashed | Report to user. Do NOT restart — let the user decide. |
| Phase stuck >2h | Check signals. Verify agents are making progress. Look for deadlocks. |
| No LLM calls for >15 min during ParallelDev | Check for hung CLI processes. Verify copilot availability. |

## Reporting
- Summarize findings concisely (1-2 sentences per check)
- Include Playwright screenshots as evidence for visual issues
- Only escalate to user if: agent stuck >30 min, Critical flow finding, runner crash, strategy all-failed, or test failure
- For routine "all clear" cycles: `✅ All agents progressing normally — [phase] phase, [N] agents active, [M] PRs open`
