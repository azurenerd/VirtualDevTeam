# Monitoring Loops — Operator Checklist for AI Agents

> Ground-truth checklist for an AI agent (Claude Opus, autopilot) supervising the VirtualDevTeam pipeline while the human is away. Read this top-to-bottom at the start of every monitoring session and again at meaningful pipeline milestones.

---

## 1. Purpose & How To Use This Document

This document is the deterministic loop you (the supervising agent) walk every time you wake up to check on the pipeline. **Re-read it at the start of each new session, after any reset, after a Runner restart, and whenever a pipeline phase transitions** (Research → Architecture → EngineeringPlanning → ParallelDevelopment → Testing → Review → Completion). Iterate the sections in order: snapshot → dashboard → PRs → issues → docs → strategy → media → logs → regressions → action. Tick the boxes mentally; if any check fails, jump to **§11 Acting On Findings** before continuing. Do not modify code based on a single failing check — gather evidence across at least two sections first. The Runner is at `http://localhost:5050`; the standalone Dashboard runs on a different port if launched separately. If `dotnet build VirtualDevTeam.sln` fails with **MSB3027 file-lock errors**, the Runner is up — that is the expected state during monitoring; build individual projects (e.g. `dotnet build src/VirtualDevTeam.Core`) instead.

### Monitoring Cadence

| Frequency | What to do |
|---|---|
| **Every loop (~5 min idle / immediately on activity)** | §2 Snapshot → §3.1 Agents → §3.5 Strategies → §11 if any red flag |
| **Every ~15 min during ParallelDev** | Full §3 dashboard sweep + §4 PR walk for each open PR |
| **At every phase transition** | §6 docs check + §5 issue audit + re-read this file's contents pertinent to the new phase |
| **At every strategy run completion** | §7 + §8 (full media pipeline check) for the just-completed task |
| **At every PR `ready-for-review` event** | §4 full PR check + §8.4 vision-description match |
| **At session start / after any reset** | Read this entire file top to bottom, then start at §2 |
| **At session end (handoff)** | Final §2 snapshot + write summary of any open todos to session state |

### Scheduled Prompts (Preferred Monitoring Pattern)

Use the `manage_schedule` tool to create a recurring monitoring prompt instead of sleeping in a loop. This keeps the agent responsive to user messages between monitoring ticks and avoids blocking the conversation.

```
manage_schedule:
  action: create
  interval: "2m"
  prompt: "Monitor VirtualDevTeam pipeline at http://localhost:5050. Compact format: only show working/blocked/error agents. Approve any pending PrePRClarification gates. If agents stuck >30min, investigate. If runner crashed, restart. Also commit and push pending changes to behumphr if any exist."
```

**Why this is better than `Task.Delay` loops:**
- The operator can talk to you between ticks — you're not blocked waiting
- Each tick is a fresh turn, so you can respond to new user requests immediately
- The schedule auto-fires even if the previous check completed instantly
- You can `manage_schedule action: list` to see active schedules and `action: stop id: N` to cancel

**Recommended intervals:**
- `2m` during active ParallelDevelopment (agents producing PRs)
- `5m` during quieter phases (Research, Architecture)
- `1m` if actively debugging a stuck agent

---

## 2. Quick-Reference Health Snapshot

Fill this in mentally (or by glancing at the dashboard) every loop. Anything outside the "healthy" column is a yellow flag; two or more yellow flags is a red flag.

| Metric | Where to Read | Healthy | Yellow | Red |
|---|---|---|---|---|
| Current Phase | `/timeline` header | One of Research/Arch/EngPlanning/ParallelDev/Testing/Review | Same phase >2h with no movement | Same phase >6h, or unexpected backward jump (impossible — log bug) |
| Active Agents | `/agents` cards | 5–10 working/idle, no Error status | 1+ in `Error` for <5 min | Any in `Error` >5 min, or all Idle while phase is ParallelDev |
| Open PRs (agent-authored) | `/repository` → Pull Requests | 1–6 open, each with `in-progress` or `ready-for-review` label | A PR with no label movement >1h | PR open >6h with no commits, or `agent-stuck` label present |
| Failing Strategies | `/strategies` active task cards | 0 candidates in Failed; ≥2 of 3 enabled candidates Completed/Scored per task | 1 candidate Failed (acceptable — others should win) | All 3 candidates Failed, or any candidate stuck `Initializing` >60s |
| Last Strategy Winner | `/strategies` Recent Completed table | Winner has score ≥7, non-empty `TieBreakReason`, `🔗 PR #N` badge present | Winner score 5–6, or `JudgeSkippedReason` set | Winner score <5, no PR badge, or empty `TieBreakReason` |
| Time Since Last Activity | `/agents` "last action" timestamp (most recent across agents) | <5 min | 5–15 min | >15 min during ParallelDev (likely deadlock or stuck LLM call) |
| LLM Calls In-Flight | `/agents` AI overlay (any "Working (AI)") | 1–4 concurrent | 0 for >3 min during active phase | All agents stuck in "Working (AI)" >5 min (CLI hang) |
| Pending Approvals | `/approvals` count badge | 0–2 | 3–5, or any waiting >15 min | Any blocking approval waiting >1h |
| Token Burn Rate | `/metrics` last hour | Steady, matches active agent count | Spike with no new agents (loop?) | Burning tokens with 0 PRs progressing |
| Workspace Disk | `.agents/` dir size | <2 GB | 2–10 GB | >10 GB — cleanup overdue |

---

## 3. Dashboard UI Monitoring

Browse `http://localhost:5050`. If the page won't load, the Runner is dead — see §9.

### 3.1 `/agents` — Live Agent Cards
- [ ] Each expected agent (PM, Researcher, Architect, Software Engineer 1–N, Test Engineer, plus any spawned specialists) has a card.
- [ ] **Status** badge is `Working` or `Idle`. `Error` is suspicious; `Stopped` during ParallelDev is a regression.
- [ ] **AI-call overlay** ("Working (AI): <descriptive context>") appears for agents currently calling the LLM. A generic "AI call in progress" with no context = `AgentCallContext.CurrentCallContext` was not set (regression — see §10).
- [ ] **Working-on tooltip** (hover) shows current task title / PR #. Empty tooltip on a `Working` card = state lost.
- [ ] No two agents claim the same PR # (would indicate restart-recovery duplication — close orphan PR per **Lessons Learned #1** in agents.md).
- [ ] Drill-down: click an agent → recent decisions / memory entries should be <5 min old if status is `Working`.

### 3.2 `/timeline` — Phase Progression
- [ ] Header shows current phase + elapsed time.
- [ ] Wave columns (`W0 / W1 / W2`) populated for ParallelDev. Tasks in `W0` should complete before `W1` starts; `W1` before `W2`.
- [ ] Each task badge shows status: `pending` / `in-progress` / `ready-for-review` / `done`.
- [ ] **"Ready for review previews" gating** — if enabled, W1/W2 tasks should not show `in-progress` until prior wave's previews are approved.
- [ ] Suspicious: a task in W1 marked `in-progress` while every W0 task is still `pending` (wave gating regression — see §10 "Wave gating skips T1").
- [ ] Drill-down: click a task → opens the linked GitHub Issue / PR.

### 3.3 `/repository` — Code / PRs / Issues
Three tabs in this order: **Code**, **Pull Requests**, **Issues**.
- [ ] **Code** tab loads the file browser at `/repository/files` — directory listing should match the active project repo.
- [ ] **Pull Requests** tab: every open PR shows agent display name in title, expected label set, and last-update timestamp <1h for active PRs.
- [ ] **Issues** tab: open issues for in-progress tasks have `status:in-progress` label; closed = `status:done`. Stale `status:in-progress` on a closed PR's issue = label sync failure.
- [ ] Suspicious: an open PR with no associated issue (orphan PR — likely a recovery glitch).

### 3.4 `/testing` — Preview Build & Test Artifacts
Currently hidden from nav but reachable via direct URL.
- [ ] **Preview Build** tab: if a preview is running, port 5100–5199 (or OS-assigned) should respond. Settings persisted at `{WorkspaceRoot}/preview-settings.json`.
- [ ] Output stream shows recent lines; token redaction active (no raw `ghp_…` strings).
- [ ] **Test Artifacts** tab: per-agent folders show screenshots/videos/Playwright traces from `{WorkspaceRoot}/{agent}/{repo}/test-results/`. Empty for an agent that just claimed to "ready-for-review" with screenshots = artifact indexer cache stale (30 s TTL — refresh and re-check).

### 3.5 `/strategies` (Frameworks Page)
- [ ] **Active task cards** — one per in-flight engineering task. Each shows enabled candidates (default `squad`, `mcp-enhanced`, `copilot-cli`).
- [ ] **PR-link badge** (added today): green `🔗 PR #N` badge linking back to the engineering PR. Missing badge on a task whose strategy already finished + winner selected = link-resolution regression.
- [ ] **Recent Completed table** — winner per task, scores, judge feedback, runtime.
- [ ] **Candidate-detail card** (expand a candidate): score, judge feedback text, screenshot (inline thumbnail), webm video (clickable), animated gif (animated thumbnail).
- [ ] **Media badges** in collapsed view — three icons (📷 / 🎞️ / 🎥) per candidate. Missing icon = corresponding artifact wasn't produced (see §8).
- [ ] Suspicious: a candidate marked `Completed` with score 0 and no judge feedback = judge crashed; check `JudgeSkippedReason`.

### 3.6 `/metrics` — Usage & Performance
- [ ] **Token usage** sparkline rising during active phase. Flat-line during ParallelDev = no LLM calls happening.
- [ ] **Model distribution** — agents should currently all be on `claude-opus-4.6-1m` (1M-context Opus 4.6, per `appsettings.json`). Mixed providers means a fallback was triggered (ModelRegistry fell back from CLI to API key) — investigate.
- [ ] Spend per phase — ParallelDev should dominate; if Research is dominating an hour into ParallelDev, the Researcher is looping.

### 3.7 `/configuration` — Gates & Tiers
- [ ] **Gate toggles** match wizard intent (see `develop-settings.json`, the runtime source of truth — NOT `appsettings.json`).
- [ ] **Model tier wiring** — premium / standard / budget / local. All agents on premium today.
- [ ] **Strategies.EnabledStrategies** matches `["squad", "mcp-enhanced", "copilot-cli"]` (baseline removed from UI).
- [ ] Drift between `appsettings.json` and `develop-settings.json`: trust `develop-settings.json` at runtime.

### 3.8 `/approvals` — Pending Approvals
- [ ] Pending cards shown in top section. Three gate types to expect: `PrePRClarification`, `AgentToAgentResponse`, `FinalPRApproval`.
- [ ] **PrePRClarification** — list of editable Q&A; if gate disabled, `AutoApprove` should fire instantly (no card should appear).
- [ ] **AgentToAgentResponse** — agent's drafted reply; human can edit/approve/reject.
- [ ] **FinalPRApproval** — keyed per-PR (NOT global) — a previously approved PR should not auto-approve a new one (regression: stale `_localApprovals` global key, see §10).
- [ ] **Rework state** — animated spinner + feedback quote + commit/changes link after rework requested. Spinner stuck >10 min with no new commit = rework loop didn't pick up the comment.

### 3.9 `/reasoning` — Agent Decisions / Memory Log
- [ ] Stream of recent `AgentDecision` entries with `Action / Decision / Learning / Instruction` types.
- [ ] Per agent: most recent entry should be <10 min old if `Working`.
- [ ] PrePR clarification questions logged here as decisions with impact level.
- [ ] Suspicious: same decision text repeating from one agent (agent stuck in a loop).

---

## 4. PR-Level Monitoring (GitHub)

For every open PR authored by an SE/Specialist agent, verify the following. Use `gh pr view <N> --json title,labels,body,commits` for fast inspection.

### 4.1 Title, Branch, Labels
- [ ] **Title prefix** matches agent display name: `{AgentDisplayName}: {TaskTitle}` (e.g. `Software Engineer 1: Implement auth`). Wrong prefix = title-parsing recovery will mis-route work.
- [ ] **Branch name**: `agent/{name}/{task-slug}` (e.g. `agent/software-engineer-1/implement-auth`).
- [ ] **Labels** transition correctly: `in-progress` → `ready-for-review` → `human-approved`. `agent-stuck` or `blocker` requires intervention. Re-fetch labels before each transition (concurrent label writes overwrite — see §10).

### 4.2 PR Body — End-Result Preview
- [ ] PR body contains an **End-Result Preview** section.
- [ ] Section shows a **real screenshot** (not a loading splash). If image is a spinner or blank canvas → WAIT detection failed; flag as media regression.
- [ ] Section contains an **AI-generated description** that *matches* the screenshot. Read the description, then look at the image. If the description references "user clicks the Save button" but the image shows a 404 page — **vision pipeline regressed** (CLI image not passed via `--attachment`). Open a todo immediately.
- [ ] **Inline strategy preview screenshots** committed at `.screenshots/pr-N-<strategy>.png` and rendered inline in the PR body (one per strategy that scored).

### 4.3 Commit Trailer & Strategy Provenance
- [ ] Latest commit message contains the strategy framework trailer:
  ```
  Strategy: <strategy-name>
  Run-Id: <runId>
  ```
- [ ] Co-author trailer present: `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- [ ] Missing trailers = direct commit bypassing the strategy framework wrapper.

### 4.4 Conversation Threads
- [ ] **Clarification questions thread** (if `PrePRClarification` gate enabled) — 1 to 10 questions with proposed answers, all marked answered before implementation commits started.
- [ ] **Rework comments** — every `Rework` review comment should have a follow-up commit addressing it; a rework comment >2 commits old with no response = rework loop dropped it.
- [ ] **Inline review comments** from `PeerReview` or `ArchitectReview` should be either resolved or have a response.

### 4.5 Files Changed
- [ ] At least one non-trivial file changed (not just a `.md` no-op).
- [ ] No accidental commits to `appsettings.json`, `develop-settings.json`, secrets, `.agents/`, or `bin/Debug/`.
- [ ] Changes scoped to the task — large unrelated edits = scope creep.

---

## 5. Issue-Level Monitoring

Engineering tasks are derived from `EngineeringPlan.md` and persisted as GitHub issues. Spec issues are derived from `PMSpec.md` and exist before engineering starts.

### 5.1 Engineering Task Issues
- [ ] Each task `T-<N>` in `EngineeringPlan.md` has a corresponding GitHub issue.
- [ ] Issue title prefixed with target agent display name (or generic if unassigned).
- [ ] **Complexity label** present (e.g. `complexity:small|medium|large`).
- [ ] **Dependency links** — `Depends on #N` references in body resolve to existing issues; circular deps = bad plan.
- [ ] Label states track PR state: `status:in-progress` ↔ open PR with `in-progress`; `status:done` ↔ PR merged.
- [ ] Suspicious: issue with `status:in-progress` and no open PR = agent abandoned task (or restart loss); reset agent prefix in title to make it eligible for reassignment.

### 5.2 Spec-Level Issues
- [ ] Spec issues from PM (PMSpec.md derived) exist before any engineering issue is created.
- [ ] Each spec issue references the user story and acceptance criteria from `PMSpec.md`.

### 5.3 Sub-Issues for Clarifying Questions
- [ ] Sub-issues (or threaded comments) for clarifying questions are linked back to the parent task issue.
- [ ] Closed parent with open clarifying sub-issues = lifecycle bug.

---

## 6. Engineering Plan & PM Spec Documents

Verify these source-of-truth docs exist and are coherent.

- [ ] **`PMSpec.md`** exists in agent docs folder, non-empty, contains: Executive Summary, Business Goals, User Stories & Acceptance Criteria, Scope, Non-Functional Requirements, Success Metrics, Constraints.
- [ ] **`Research.md`** exists and was committed before `PMSpec.md`.
- [ ] **`Architecture.md`** exists and references PMSpec sections.
- [ ] **`EngineeringPlan.md`** exists and references Architecture decisions.
- [ ] **Task IDs** (`T-1`, `T-2`, …) in `EngineeringPlan.md` match GitHub issue titles 1:1.
- [ ] **`T-FINAL`** integration task exists — this is the integration PR that wires everything together.
- [ ] **CSS-with-feature rule** respected: small ≤3 tasks, medium ≤6, large ≤10 (per `AssessProjectComplexity()` and `NormalizeTaskPlan()` — see Lessons Learned #13). Exceeding cap = normalization wasn't run.
- [ ] CSS / styling tasks are bundled with the feature task they style — never standalone CSS-only tasks (rule lives in `NormalizeTaskPlan()`).

---

## 7. Strategy Framework / Frameworks Loop

For each active strategy task verify per-candidate health.

### 7.1 Per-Task Checks
- [ ] All enabled candidates (`squad`, `mcp-enhanced`, `copilot-cli`) appear under the task card.
- [ ] No candidate is stuck in `Initializing` past **60 s** — that's a red flag, kill and retry.
- [ ] Each candidate progresses through: `Initializing` → `Running` → `Completed` → `Scored`.
- [ ] **Build gate** result shown (✅ / ❌). All-fail across candidates = environmental issue, not candidate quality.
- [ ] **Tests gate** result shown.
- [ ] **Screenshot gate** result shown — a candidate without a screenshot artifact should not be `Scored`.

### 7.2 Judge & Winner
- [ ] Judge scores present for every Scored candidate **unless** `JudgeSkippedReason` is non-empty.
- [ ] **Winner selected** with non-empty `TieBreakReason`. Empty = tie-break logic regressed.
- [ ] Winner's patch was actually applied — confirm via the engineering PR's commits.

### 7.3 Empty-Patch Retries
- [ ] An "empty patch" from a successful strategy triggers a retry. Look for `-retry`, `-retry2` suffixes.
- [ ] **More than 2 retries on the same candidate** = `baseSha` already contains the work and the agent reports "task is complete". Either (a) the task is genuinely done — close the issue; or (b) baseSha drifted — re-create from current main.

### 7.4 Per-Strategy Quirks
- [ ] **`squad`** (multi-agent squad) — should produce the most code; if it consistently scores below the others, check whether sub-agents are actually splitting work (look for ≥2 distinct authors in the candidate's per-commit history inside its worktree).
- [ ] **`mcp-enhanced`** — uses MCP tools. If MCP server isn't reachable, this strategy degrades silently to plain LLM output. Log line `MCP unavailable — fallback` confirms degradation.
- [ ] **`copilot-cli`** — direct CLI agentic session. Most likely to hit interactive-prompt watchdog edges. Failure mode: watchdog auto-answered a credential prompt → check `CliInteractiveWatchdog` log lines for `auth`/`credential` matches.
- [ ] No two strategies should have identical patches — if they do, diversity collapsed (typically a too-narrow task spec).

---

## 8. Media Pipeline End-to-End

Most fragile loop. For each candidate of each task, verify the full chain.

### 8.1 Artifact Files
- [ ] Primary screenshot: `framework-T-<N>-<strategy>-0-<page>.png` exists, **≥ 50 KB**, NOT a loading splash. <50 KB usually means capture happened mid-render.
- [ ] Webm video: corresponding `.webm` exists, **≥ 100 KB**, shows real interaction (not a static canvas for the full duration).
- [ ] Animated gif: corresponding `.gif` exists, **≥ 50 KB**, animated (multiple frames).
- [ ] All three under `src/VirtualDevTeam.Runner/.agents/strategy-artifacts/<runId>/T-<N>/<strategy>/`.

### 8.2 Snapshot Population
- [ ] Candidate snapshot fields populated: `ScreenshotBase64`, `VideoPath`, `AnimatedGifPath`. Any null = pipeline did not propagate the artifact even if file exists on disk.

### 8.3 UI Reflection
- [ ] `/strategies` candidate-detail card shows all three artifacts.
- [ ] Collapsed candidate row shows three media badges (📷 🎞️ 🎥).

### 8.4 PR End-Result Preview
- [ ] Winner's PR body End-Result Preview section has the right screenshot.
- [ ] AI vision description references **real visible content** in the screenshot — read the description, look at the screenshot, confirm a concrete element from the description is visible.

### 8.5 Failure Modes (specific signs)
- [ ] **Loading-screen-only captures** → `WaitForLoadingScreenToClearAsync` was skipped or `CaptureAppInteractionAsync` did not poll for content. Check candidate log for the wait phase.
- [ ] **Tiny file sizes** (screenshot <30 KB, video <50 KB, gif <20 KB) → capture cut short; usually a process-kill timing bug.
- [ ] **Description doesn't match screenshot** → vision pipeline regressed — the CLI did not pass the image via `--attachment`. Open a todo immediately and grep recent commits to `CopilotCli*` or vision plumbing.
- [ ] **All three artifacts missing across all candidates** → preview-launch failure, not vision/capture failure. Check launchSettings neutralization (§9).
- [ ] **Same screenshot reused across multiple candidates** → capture path collision; per-candidate output dir wasn't used. File names should include `<strategy>` token.
- [ ] **Video playable but gif static** → ffmpeg gif-conversion step failed silently. Look for `ffmpeg` errors in candidate log.
- [ ] **Description references a button/feature that's spec'd but not visible** → vision is working but the implementation didn't ship the feature. Score should reflect this; if winner score >7 with this gap, judge prompt regressed.

---

## 9. Logs & Filesystem Spot-Checks

### 9.1 Strategy Artifacts Directory
- [ ] `src/VirtualDevTeam.Runner/.agents/strategy-artifacts/<runId>/T-<N>/<strategy>/` populated per candidate (screenshot/video/gif trio).
- [ ] Per-task subfolders match `T-<N>` from `EngineeringPlan.md`.
- [ ] Empty subfolder for an active task = candidate never reached capture phase.

### 9.2 Per-Candidate Experiment Logs
- [ ] `src/VirtualDevTeam.Runner/bin/Debug/net8.0/experiment-data/<runId>-T-<N>-<strategy>.log` exists for agentic strategies.
- [ ] Search log for **"0 code changes"** — repeated occurrences across candidates = task already complete or baseSha mismatch.
- [ ] Search log for `-retry` suffix counts — >2 retries is a red flag.
- [ ] Search for `ERR` / `FAIL` / `Exception` patterns — recent crashes surface here before the dashboard.

### 9.3 Reset Scripts
- [ ] Confirm which reset was last used (check git stash / file mtimes on `.agents/`):
  - **`scripts/fresh-reset.ps1`** — wipes everything (workspace, state, memory). Use only when starting clean.
  - **`scripts/minimal-reset.ps1`** — preserves agent memory and recent state; clears active runs. Leaves startup .md files in repo to get to SE Agent PRs faster. Default for "try again".
  - **`scripts/reset-runner.ps1`** — restarts the Runner process only; keeps workspace and state intact.
- [ ] All three read `Workspace.RootPath` from `appsettings.json` — never hardcode a workspace path in scripts.

### 9.4 Workspace Path Resolution
- [ ] `.agents/` resolves correctly via `WorkspaceConfig.ResolveRootPath()`.
- [ ] **Memory**: `ResolveRootPath` skips already-absolute paths. If `appsettings.json` has a stale absolute path, it won't be re-resolved against current CWD. Always use relative path `.agents` (per Lessons Learned #8).

### 9.5 Browser-Pop Detection
- [ ] During a strategy run, no browser windows should open on the host.
- [ ] If they do → `launchSettings.json` was not neutralized in the candidate worktree (recently fixed in commit `8d8263b`). Check the candidate's worktree `Properties/launchSettings.json` — `launchBrowser` should be `false`.

### 9.6 Runner Process State
- [ ] Runner listening on `5050`: `Test-NetConnection localhost -Port 5050` returns `True`.
- [ ] If `dotnet build VirtualDevTeam.sln` errors with **MSB3027** → Runner is up (expected). Build individual projects to verify code compiles without stopping it: `dotnet build src/VirtualDevTeam.Core`, `dotnet build src/VirtualDevTeam.Agents`, etc.
- [ ] If 5050 doesn't respond → Runner crashed; check console for the last stack trace before restarting via `scripts/reset-runner.ps1`.

---

## 10. Common Regression Patterns ("things you've broken before")

Each entry: symptom → root cause → first place to look.

- [ ] **PR description doesn't match screenshot** → CLI vision regressed; image not passed via `--attachment`. Look in `CopilotCliChatCompletionService` / vision plumbing for recent edits.
- [ ] **All strategy gif/video are loading screens** → `WaitForLoadingScreenToClearAsync` skipped, or `CaptureAppInteractionAsync` doesn't poll for content. Look in capture orchestration code.
- [ ] **One strategy keeps producing empty patches** → `baseSha` already contains the work; agent says "task is complete". Either close the issue or re-create branch from current main.
- [ ] **Browser pops up on host during strategy run** → `launchSettings.json` not neutralized in candidate worktree (fixed in `8d8263b`). Re-check the worktree neutralization step.
- [ ] **Self-assessment didn't run on SE-direct path** → `SoftwareEngineerAgent.FinalizeReadyForReviewAsync` not wired (Lessons Learned #14). Specialist path uses base class — SE direct path overrides; both must call self-assessment.
- [ ] **Wave gating skips T-1** → enhancement scope filter dropped tasks without `ParentIssueNumber`. Check wave-eligibility predicate.
- [ ] **PrePR clarification skipped for some engineers** → must be wired in BOTH `EngineerAgentBase.WorkOnIssueAsync` and `SoftwareEngineerAgent.WorkOnOwnTasksAsync` (Lessons Learned #16).
- [ ] **`FinalPRApproval` auto-approves later PRs** → `_localApprovals` global key in `GateCheckService` not per-resource (Lessons Learned #10). Must key per PR number.
- [ ] **Develop wizard breaks after JSONL toggle** → `CopilotCli.JsonOutput=true` returns raw JSONL from `ExecutePromptAsync`; direct CLI callers must run `CliOutputParser.ParseJsonOutput()` first (Lessons Learned #12).
- [ ] **Dashboard "AI call in progress" with no context** → agent didn't set `AgentCallContext.CurrentCallContext` before the LLM call (Lessons Learned #11).
- [ ] **Concurrent label writes silently lose labels** → must re-fetch labels immediately before writing (Lessons Learned #4).
- [ ] **`.git/config.lock` race during parallel worktree creation** → strategy framework must serialize `git worktree add` or retry on lock failure (Lessons Learned #5).
- [ ] **Restart loses in-progress PR ownership** → SE recovery cross-references open PRs with linked work items / title matching; if a PR gets orphaned (e.g. duplicated agent), close it manually and reset the issue labels.
- [ ] **Standalone Dashboard crashes on startup** → service registered in Runner `Program.cs` but not in `StandaloneServiceRegistration` (Lessons Learned #3). Add to both DI paths.
- [ ] **Constructor parameter mismatch in tests** → new dep added to `ModelRegistry` / `AgentCoreServices` / `AgentPlatformServices` but test constructors not updated (Lessons Learned #2). Update all call sites including tests.
- [ ] **Frameworks PR-link badge missing** → today's `frameworks-pr-link` feature: each strategy task in the Frameworks UI should show `🔗 PR #N`. Missing badge = link resolution dropped the mapping. New regression — flag immediately.
- [ ] **Workspace path absolute and stale** → `WorkspaceConfig.ResolveRootPath()` skips already-absolute paths. If a previous session or repo move left an absolute path in `appsettings.json`, the workspace silently uses the wrong location (Lessons Learned #8). Always commit relative paths only.
- [ ] **`develop-settings.json` vs `appsettings.json` drift** → at runtime, the Configuration page and reset scripts must read from `develop-settings.json` for the active project; reading `appsettings.json` instead surfaces blank defaults (Lessons Learned #9).
- [ ] **CSS-only orphan task** → `NormalizeTaskPlan()` should bundle styling with feature task; a standalone CSS-only `T-N` slipped past complexity normalization (Lessons Learned #13).
- [ ] **New Blazor button class with no CSS** → button renders as unstyled browser default on dark theme; Blazor doesn't warn (Lessons Learned #15). Define every new button class in `dashboard.css`.
- [ ] **PR review fails on deleted source branch** → `GetPRCodeContextAsync` must guard with `filesRead` counter and warn instead of failing when source branch is gone (Lessons Learned #6).
- [ ] **In-memory-only flags lost across restart** → `_allTasksComplete`, `_integrationPrCreated`, `CurrentPrNumber` must be re-derived from durable GitHub/ADO state on startup (Lessons Learned #7). PullRequestNumber is NOT persisted in issue metadata — correlate via linked work items or title matching.

---

## 11. Acting On Findings

| Symptom | Action | Notes |
|---|---|---|
| Single agent in `Error` <5 min | **Investigate without restarting**; check `/reasoning` for that agent | Self-recovery loop usually catches it |
| Agent in `Error` >5 min OR all agents idle during ParallelDev | **Open todo + report to user** | Collect last 50 log lines first |
| Strategy candidate stuck `Initializing` >60s | **Investigate without restarting**; check experiment log | If still stuck after 5 min, kill run via dashboard |
| All 3 strategy candidates Failed | **Report to user** + capture per-candidate logs | Likely environmental, not code |
| PR description doesn't match screenshot (vision regression) | **Open todo**; do NOT reset (preserves repro) | Tag as `vision-pipeline` |
| Loading-screen-only captures across candidates | **Open todo**; do NOT reset | Tag as `media-pipeline` |
| Runner not responding on 5050 | **`scripts/reset-runner.ps1`** then re-check | State preserved |
| `develop-settings.json` corrupted / missing | **Report to user** — wizard must re-run | Do not auto-recreate |
| Stale state from prior run blocking new run | **`scripts/minimal-reset.ps1`** | Preserves memory |
| Need to start completely clean (e.g. major schema change) | **`scripts/fresh-reset.ps1`** | Wipes workspace + memory; user confirmation preferred |
| Workspace disk >10 GB | **`scripts/minimal-reset.ps1`** + manual `.agents/strategy-artifacts/` cleanup of old runIds | Keep most recent runId |
| Pending approval >1h | **Report to user** | Do not auto-approve |
| Orphan PR (no agent owns it) | **Close PR + strip agent prefix from issue title + remove `status:in-progress`** | Makes issue eligible for reassignment |
| `agent-stuck` label appears | **Read PR comments**; if reason is environmental, fix and `minimal-reset` | If reason is task ambiguity, escalate to user |
| Repeated retries (>2) on same candidate, "0 code changes" in log | **Investigate baseSha**; likely task already complete or branch drift | Do not blindly retry |
| New regression pattern not in §10 | **Add it to §10** before finishing the loop | Keep this doc current |
| All checks pass, nothing to do | **Sleep 5 min, re-run loop from §2** | Do not invent work |

---

## Appendix A — Key File Paths (Quick Reference)

| Purpose | Path |
|---|---|
| Runner appsettings (defaults — blank) | `src/VirtualDevTeam.Runner/appsettings.json` |
| Runtime project settings (source of truth) | `develop-settings.json` (gitignored, repo root) |
| Workspace root (default) | `.agents/` (relative to project root) |
| Strategy artifacts | `src/VirtualDevTeam.Runner/.agents/strategy-artifacts/<runId>/T-<N>/<strategy>/` |
| Per-candidate experiment logs | `src/VirtualDevTeam.Runner/bin/Debug/net8.0/experiment-data/<runId>-T-<N>-<strategy>.log` |
| Preview build settings | `{WorkspaceRoot}/preview-settings.json` |
| Test artifacts | `{WorkspaceRoot}/{agent}/{repo}/test-results/` |
| Reset scripts | `scripts/fresh-reset.ps1`, `scripts/minimal-reset.ps1`, `scripts/reset-runner.ps1` |
| Prompt templates | `prompts/{role}/*.md` |
| Agent docs (PMSpec, Architecture, EngineeringPlan, Research) | within active project workspace under `.agents/...` |

## Appendix B — Convention Cheatsheet (cross-ref `agents.md`)

- **PR title**: `{AgentDisplayName}: {TaskTitle}`
- **PR branch**: `agent/{name}/{task-slug}`
- **Issue title**: `{TargetAgent}: {Title}` or `Executive Request: {Title}`
- **Labels**: `in-progress`, `ready-for-review`, `blocker`, `agent-stuck`, `executive-request`, `resource-request`, `agent-question`, `awaiting-human-review`, `human-approved`, plus complexity labels
- **Commit trailer**: `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` + strategy framework `Strategy:` / `Run-Id:`
- **Default model**: `claude-opus-4.6-1m` (premium tier) for all agents currently
- **Enabled strategies**: `["squad", "mcp-enhanced", "copilot-cli"]` (baseline removed from UI)
- **Phase order** (no backward jumps): Initialization → Research → Architecture → EngineeringPlanning → ParallelDevelopment → Testing → Review → Completion
- **MSB3027 file-lock on full-solution build** = Runner is running (expected) — build individual projects instead
