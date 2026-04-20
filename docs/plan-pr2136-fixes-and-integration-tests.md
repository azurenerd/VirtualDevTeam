## Plan: PR #2136 failure-class fixes + inline-review regression + offline workflow integration tests

**Status:** planning-only; nothing implemented. Safe to load & execute later.
**Coordination:** another CLI is running independent fixes in parallel — DO NOT `dotnet build` without user confirmation. All log-grep / file-read steps are safe.
**Sibling docs:**
- `docs/plan-dashboard-quality-fixes.md` (prior plan — Phases A–F for the original dashboard-blank failure class).
- `docs/AgentSquadGapAnalysis.md` (the other CLI's root-cause write-up — owns remediations R1–R7, of which R1/R2/R3 have shipped in commit `e99ff88`).

This plan **extends** those docs and is explicitly reconciled against them (see §"Reconciliation with shipped work and sibling plan" below).

## PR #2136 confirms every root cause is still live

PR `azurenerd/ReportingDashboard#2136` (T4 "Implement Global Page Layout, CSS, and Dashboard Shell") was merged with:
- SE's own "ready-for-review" screenshot = blank white 1920×1080 page; SE's caption literally says *"nearly blank white page... broken or empty render"*.
- TE UI test `Heatmap_Grid_Uses_Expected_Column_Template` **FAILED** (`grid-template-columns: none` instead of `160px…`).
- TE uploaded `pr-2135-app-preview.png` (wrong PR number — stale from #2135).
- Architect flipped from CHANGES REQUESTED → APPROVED on a no-op "rework" where SE re-emitted identical files and claimed all 5 feedback items were already addressed.
- PM CHANGES REQUESTED correctly caught the blank page — but `MaxPmReworkCycles=1` means it got force-overridden.

Active RCs: RC-1, RC-3, RC-4, RC-5, RC-7.

**Minimum set to block #2136-class failures = Phase A + Phase C + Phase D** (Phase B is higher-value but heavier; E is cheap and should go with them).

## Reconciliation with shipped work and sibling plan

**Shipped in commit `e99ff88` (other CLI, 2026-04-20):**
- ✅ Gap-analysis **R1** — `IGitHubService.GetFileBytesAsync` + PNG/JPG/WEBP design-image discovery → fed to SE at plan/single-pass/step prompts as `ImageContent`. This partially supersedes my earlier "Phase B (add design screenshot to review)" — the **SE** now sees design images, but the **PM review path** was not updated. My plan's Phase B is narrowed to "attach design image to PM review prompt" only.
- ✅ Gap-analysis **R2** — new `LimitsConfig.SinglePRMode` (default `false`). When true, SE emits a single T1+T-FINAL plan bypassing decomposition. My plan's WS3 fixtures must exercise both modes.
- ✅ Gap-analysis **R3** — rewrote `single-pass-implementation.md` SCOPE RULE to allow integration edits with `INTEGRATION EDIT:` marker; replaced `integration-review-system.md` with 9-point checklist (DI, middleware, routing, imports, static assets, data copy, composition, error paths, build/run). This complements but does NOT cover my WS1/D2 (forbid literal `"placeholder"` strings) — D2 still needed.

**Still unshipped and owned by `docs/AgentSquadGapAnalysis.md`:**
- ⏳ R4 — Combine PM+Architect into one agent run (reduces cross-phase serialization loss).
- ⏳ R5 — In-session build/run loop (SE iterates on build failures in the same chat turn, not a fresh PR).
- ⏳ R6 — Rework continuation + no-op detector + keep cap=1. **I am adopting R6 in my plan as item A1′ (see WS1 below), replacing my original "raise caps to 3" proposal.**
- ⏳ R7 — Golden-case end-to-end test of AgentSquad itself. **Convergent with my WS3.** Suggested merge: my WS3 (offline harness + fixture format) becomes the infrastructure; R7's golden-case test becomes the first fixture.

**Conflict resolved:** my prior plan wanted `MaxReworkCycles=3`; gap-analysis R6 wants cap=1 + continuation + no-op detection. **R6 is the better fix** — it would have caught PR #2136's round-2 behavior (SE claimed "no change needed" and re-emitted identical files → Architect flipped to APPROVED on unchanged code). Raising caps to 3 would just have produced more expensive cycles of the same degradation. My A1 is replaced by A1′.

**Clean division of ownership going forward:**
- `AgentSquadGapAnalysis.md` owns R4, R5, R6, R7 (strategic reshapes).
- `plan-pr2136-fixes-and-integration-tests.md` (this doc) owns the #2136-class failure gates (WS1), the inline-review regression (WS2), the fast offline harness (WS3 — which R7's golden test plugs into).
- `plan-dashboard-quality-fixes.md` is historical; its Phase A/B/C/D/E/F ideas are now absorbed into this doc (A1′ supersedes A1; B narrowed; C/D/E kept as WS1).

---

## Workstream 1 — Implement the remaining #2136 gates (Phase C + D + adjusted A + narrowed B + E)

**Reconciled:** the other CLI's gap-analysis R1/R2/R3 already landed (design images to SE, SinglePRMode flag, 9-point integration checklist). This changes WS1 as follows:
- **My old A1 (raise rework caps to 3)** is REPLACED by **A1′ (adopt gap-analysis R6: rework continuation + no-op detector with cap=1)** — better fix, would have caught PR #2136's round-2 identical re-emit.
- **My old Phase B (PM design-anchor review)** NARROWED to PM side only — SE side is done.
- **D2 (forbid placeholder literals)** still valid — the new 9-point integration checklist doesn't explicitly ban `"placeholder"` strings in `.razor`/`.cs`/`.html`.

Ordered sub-todos (for SQL tracking):

### A1′ — Rework continuation + no-op detector (supersedes "raise caps")
- File: `src/AgentSquad.Agents/EngineerAgentBase.cs` rework path (and equivalent Architect/PM/TE paths).
- Change 1: on rework, pass PREVIOUS LLM output + the critique as continuation of the existing `ChatHistory`, not as a fresh turn. (The gap analysis R6 calls this out as the root cause of cycle degradation.)
- Change 2: after a new rework output is produced, compare to the previous output via normalized line-diff similarity; if ≥ 90% identical → abort rework, post `⚠️ Rework produced no substantive change — stopping cycle` comment, and route to the exhaustion gate (not force-approval).
- Leave `MaxReworkCycles=1` as-is. This is cheaper than raising caps AND catches PR #2136's exact behavior (SE round-2 claimed "no change needed" and re-emitted identical files → Architect flipped to APPROVED on unchanged code).
- Acceptance: unit test on the similarity detector with a synthetic 100%-identical re-emit → returns `noop=true`. Integration test feeds a stubbed agent that re-emits same diff → rework halts with the warning comment instead of proceeding to approval.

### A2 — PM hard-block merge on UI test failures (unchanged from earlier plan)
- File: `src/AgentSquad.Agents/ProgramManagerAgent.cs` (~line 960 where `hasTeCompletionComment` is built).
- Parse latest TE comment for `UI Tests: ❌ N passed, M failed` and if `M > 0` → force verdict to CHANGES_REQUESTED regardless of other signals AND regardless of rework-cycle count.
- Comment body: *"PM hard-block: UI test failures detected. All UI tests must pass before approval — rework cap does not apply."*
- Acceptance: test feeds a TE comment with `❌ 1 failed` into the PM review evaluator → expect CHANGES_REQUESTED verdict.

### A3 — Rework-exhaustion gate honors UI failures (unchanged)
- File: `src/AgentSquad.Agents/EngineerAgentBase.cs` line ~1500 (`HandleReworkAsync` force-approval path).
- New config flag: `AgentSquad.Gates.ReworkExhaustion.BlockOnUiFailures = true` (default true).
- If gate triggers AND latest TE comment shows UI failures → do NOT request force-approval; open a `🚨 needs-human-review` label + conversation comment, then idle the PR.
- Acceptance: integration test shows the PR is labeled and idled instead of merged.

### B-PM — Attach design image to PM review prompt (narrowed scope — SE side already shipped in R1)
- File: `src/AgentSquad.Agents/ProgramManagerAgent.cs` (PM screenshot-review prompt builder, lines 2556–2600).
- Use the already-added `IGitHubService.GetFileBytesAsync` to fetch design reference PNGs (same discovery keywords the SE uses per R1).
- Attach as a second `ImageContent` input to the vision call alongside the SE's ready-for-review screenshot.
- Update prompt to explicit comparison: *"Screenshot 1 is the ACTUAL shipped output. Screenshot 2 is the TARGET DESIGN. Approve only if header/timeline/heatmap are recognizably present and positioned similarly. Blank pages, literal 'placeholder' strings, or missing components → CHANGES REQUESTED."*
- Acceptance: unit test that PM review prompt contains both image blobs when a design PNG exists in the repo.

### C1 — Skip wrong-PR-number screenshots in upload
- File: `src/AgentSquad.Agents/TestEngineerAgent.cs` line ~1720 (`UploadTestArtifactsToPrAsync`).
- Regex `^pr-(\d+)-` against filename; skip if captured number ≠ current `pr.Number`.
- Acceptance: test drops `pr-2135-app-preview.png` into a fake workspace for PR 2136 → assert upload list excludes it.

### C2 — Explicit "no live screenshot" comment on capture failure
- File: `src/AgentSquad.Agents/TestEngineerAgent.cs` line ~1599 (standalone capture fallback).
- If capture fails AND no new PNG with correct `pr-{N}-…` exists → post `⚠️ No live screenshot was captured for this PR. PM should flag this as a visual-validation hazard.`
- Acceptance: induce failure in a fake run → comment is present.

### C3 — Pre-capture cleanup
- File: `src/AgentSquad.Agents/TestEngineerAgent.cs` (before the Playwright runner step).
- Delete `test-results/screenshots/pr-*.png` from the TE's local workspace before each PR.
- Acceptance: test places a stale PNG in the dir, runs TE prep, asserts the PNG is gone.

### D1 — SE self-check for placeholder strings on ready-for-review
- File: `src/AgentSquad.Agents/SoftwareEngineerAgent.cs` (ready-for-review branch, near existing self-review step).
- Grep the SE's PR diff for quoted `"placeholder"` (case-insensitive) in `.razor`/`.cs`/`.html`.
- If matches AND task title matches `(wire|compose|integrate|final|implement.*shell|implement.*layout)` → do not mark ready; comment `⚠️ Placeholder strings detected. Task claims integration but diff retains placeholder literals. Refusing to mark ready-for-review.`
- Acceptance: unit test on detection regex + full SE-loop integration test where placeholder-laden diff refuses to progress.

### D2 — Strategy prompt update (still valid after R3's 9-point checklist shipped)
- Update baseline SE strategy prompt + `prompts/software-engineer/single-pass-implementation.md` to explicitly forbid literal placeholder strings (`"placeholder"`, `"Timeline placeholder"`, `"Heatmap placeholder"`) in `.razor`/`.cs`/`.html`.
- Require real components OR the `.Empty` view-model equivalent when layout engines aren't yet wired up.
- Note: R3's integration-review prompt covers cross-file DI/middleware concerns; this complements rather than duplicates.

### E1 — Scaffolding PR must include bootable data (unchanged from earlier plan)
- File: `src/AgentSquad.Agents/SoftwareEngineerAgent.cs` — scaffolding/foundation task handling (T1-class tasks).
- Require either `wwwroot/data.json` OR `wwwroot/data.template.json` + a template→runtime copy step in the app boot path.
- PM review prompt flags blank-app scaffolding PRs missing bootable sample data.
- Acceptance: scaffolding PR without `data.json`/`data.template.json` gets CHANGES REQUESTED.

### Validation after WS1 lands
- Mini-reset + fresh run on a small project.
- Monitor PR #1 through PR #3 — expect: no blank screenshots merged, stale screenshots filtered, UI-test failures block merge, placeholder strings rejected, rework cycles terminate cleanly on no-op re-emits.

---

## Workstream 2 — Inline-review-comment regression

### Symptom
Architect review on PR #2136 (and the T-5 rework of same) posted its feedback **only in the Conversation tab**, not as inline comments on the Files-changed tab. Expected path: `SubmitInlineReviewCommentsAsync` → GitHub Reviews API with line-anchored comments.

### Known code paths (already implemented)
- `ArchitectAgent.cs:993,1015,1068` — calls `SubmitInlineReviewCommentsAsync` for COMMENT / APPROVE / REQUEST_CHANGES verdicts.
- `SoftwareEngineerAgent.cs:2860,2866,4972,4980` — same for SE-reviewing-SE paths.
- `GitHubService.cs:452-556` — `SubmitReviewAsync` with line-based inline comments, uses `DiffPositionMapper` to filter out-of-diff lines.
- **`ProgramManagerAgent.cs` — ZERO matches for inline comment submission. PM has no inline path at all.** (Grep confirmed.)

### Hypotheses (prioritized by likelihood)
1. **PM never posts inline** — by design-gap, not regression. PM-reported CHANGES REQUESTED items are file-specific but always land in conversation tab.
2. **Architect LLM output stopped populating `Comments` list** — the guard `if (reviewResult.Comments.Count > 0 && _config.Review.EnableInlineComments)` silently skips inline when the model returns zero comments. PR #2136 Architect feedback is clearly file/line specific ("`Dashboard.razor` imports `ReportingDashboard.Web.Layout`…", "`wwwroot/app.css` is missing…") — those absolutely should be inline. Prompt drift likely dropped the structured-output format.
3. **`_config.Review.EnableInlineComments = false` in current `appsettings.json`** — verify.
4. **`DiffPositionMapper` is dropping all comments as "file not in PR diff"** — GitHubService line 478 logs `LogDebug`. Raise to `LogInformation` and check runner logs.
5. **"Own pull request" downgrade path (GitHubService:523-545)** fires because the PAT can't review its own PR → review is downgraded to a plain conversation comment with inline comments appended as text. This is the single-PAT-setup fallback and may explain ALL conversation-tab-only behavior in this run.

### Diagnostic todos (run before coding)
- R2-a: Grep last runner log for `"Downgraded {Event} to COMMENT on own PR"` — if present, hypothesis 5 is confirmed.
- R2-b: Grep log for `"Inline comment on {File}:{Line} skipped — file not in PR diff"` — count vs. attempted.
- R2-c: Dump latest `reviewResult.Comments.Count` from an Architect review — LogInformation upgrade needed.
- R2-d: Confirm `_config.Review.EnableInlineComments` live value.

### Fix todos
- R2-1: If hypothesis 5 (own-PR downgrade) is root cause — the comments ARE in the downgraded body but as a text block, not on lines. The only fix is a separate `github-actions[bot]`-equivalent reviewer account OR accepting the conversation-tab format is a fundamental limitation of the single-PAT setup. Options:
  - (a) Document as known limitation; leave as-is.
  - (b) Add a GitHub App / bot account for reviews and store its token alongside the main PAT (`AgentSquad.Review.BotToken`). Use bot token exclusively in `SubmitReviewAsync`.
  - (c) Post inline comments as **standalone PR review-comment API calls** (POST `/repos/{o}/{r}/pulls/{n}/comments`) — these are not part of a review so the "own PR" constraint doesn't apply, and they DO show up in the Files-changed tab inline.
  - **Recommended: (c)** — smallest code change, no account rework. Add `CreatePullRequestReviewCommentAsync(prNumber, body, commitId, path, line, side)` to `IGitHubService`. Use it from all agents that currently call `SubmitInlineReviewCommentsAsync`. The review verdict itself (APPROVE / REQUEST_CHANGES) stays a plain conversation comment with the same body.
- R2-2: If hypothesis 2 (LLM drops inline structure) — reinforce the structured-output parser. Look at `ArchitectAgent.cs:1574-1600` parser. Add a fallback regex that extracts "`File.ext:LineNo`" patterns from the free-form review text and synthesizes inline comments. (Currently the parser relies on the model producing a specific JSON/delimited block; prompt drift breaks that.)
- R2-3: If hypothesis 3 (config toggled off) — flip on in `appsettings.json` and set default in `AgentSquadConfig.cs` to `EnableInlineComments = true`.
- R2-4: Add a PM inline path — PM review prompt already produces file-specific feedback; plumb it through the same `SubmitInlineReviewCommentsAsync` pattern the Architect uses. Symmetric with R2-1 fix once committed.
- R2-5: Upgrade `DiffPositionMapper` skipped-comment log from Debug to Information with a PR-scoped correlation field so the filter rate is visible at runtime without re-enabling verbose logging.

### Acceptance
- Fresh mini-reset PR where Architect requests changes → comments appear under the Files-changed tab at specific lines.
- PM requests changes → same.
- Runner log shows `SubmitReviewAsync` mapping ≥ 80% of LLM-produced inline comments to in-diff positions.

---

## Workstream 3 — Fast offline engineering-workflow integration test

### Goal
Replay any PR's lifecycle (assign task → SE implements → TE tests → Architect reviews → PM reviews → merge) end-to-end **without** hitting GitHub or waiting for a real copilot CLI. Input: a synthetic task description, acceptance criteria, and a pre-built diff/test-result fixture. Output: assertions about which labels, comments, verdicts, and merge state the workflow arrives at — in seconds, not hours.

### Existing building blocks
- `tests/AgentSquad.Integration.Tests/` — already hosts workflow-level tests (`WorkflowStateMachineTests`, `SystemBootstrapTests`).
- `tests/AgentSquad.FakeCopilotCli/` — already a fake CLI process; currently minimal.
- No fake `IGitHubService` exists yet — all agents talk directly to real `GitHubService`. This is the central gap.

### Design

#### Component 1 — `InMemoryGitHubService : IGitHubService` (new, in `tests/AgentSquad.Integration.Tests/Fakes/`)
- Holds in-memory dictionaries: `Dictionary<int, AgentPullRequest>`, `Dictionary<int, AgentIssue>`, `List<IssueComment>`, `List<InlineReviewComment>`, `Dictionary<int, HashSet<string>>` for labels, etc.
- Implements every `IGitHubService` method with plain dictionary operations — no network.
- Supports seeding from a fixture: `SeedPullRequest(prNumber, title, body, branch, author, labels, files[])`, `SeedIssue(...)`, `SeedFileDiff(prNumber, path, patch)`.
- Records every call (`Calls` list) for assertion: `Calls.Where(c => c.Method == "AddPullRequestCommentAsync" && c.Args.PrNumber == 2136)`.
- Emits events on mutation so agents that subscribe to "new comment" polling can be notified deterministically.

#### Component 2 — `ScriptedCopilotCli` (enhance existing `FakeCopilotCli`)
- Today's `FakeCopilotCli` is a real stdio process. Keep it, but add a scripting mode where the test provides a dictionary `prompt → response` or a `Func<string, string>` predicate.
- Example: `ScriptedCopilotCli.WhenPromptContains("You are the Architect").Returns("APPROVED\n...Architecture Review: looks good...")`.
- Avoids the complexity of a full LLM. Each agent's prompt path becomes a deterministic branch.
- Strategy: use the **existing** `CopilotCliProcessManager` with the `copilot` executable pointing at `FakeCopilotCli.exe`. Set the script via env var (`FAKE_CLI_SCRIPT_JSON=…`) or a file pointer.

#### Component 3 — `WorkflowTestHarness`
- Wires the full DI container (Orchestrator + Agents + Core) with two substitutions:
  - `IGitHubService` → `InMemoryGitHubService` (seeded per test)
  - `IChatCompletionService` / CLI runner → `ScriptedCopilotCli`
- Exposes `await harness.RunUntilAsync(predicate, timeout)` — advances time (pollers use `ITimeProvider`) until the predicate is satisfied or a test-scoped timeout trips.
- Exposes `harness.Github.Calls` and `harness.Github.CurrentStateOf(prNumber)` for assertions.

#### Component 4 — Fixture format
- A test fixture is a JSON file describing a single PR lifecycle replay:
  ```json
  {
    "pr": { "number": 9001, "title": "…", "body": "…", "branch": "agent/se/t4-…", "author": "SoftwareEngineer" },
    "task": { "number": 9000, "title": "[T4] …", "acceptanceCriteria": [ "…", "…" ] },
    "files": [ { "path": "src/Dashboard.razor", "patch": "@@ -0,0 +1,50 @@\n+…" } ],
    "testResults": { "unit": "20 passed", "ui": "5 passed, 1 failed" },
    "agentScripts": {
      "architect.reviewPrompt": "CHANGES_REQUESTED\nReasoning: …",
      "pm.reviewPrompt": "APPROVED\nReasoning: …"
    },
    "expect": {
      "finalState": "needs-human-review",
      "labelsContain": [ "ui-test-failing" ],
      "commentBodyContains": [ "UI test failures detected" ]
    }
  }
  ```
- Store under `tests/AgentSquad.Integration.Tests/Fixtures/`.

#### Component 5 — Test cases (initial batch)
- `Fixture_Pr2136_Blank_Screenshot_With_Ui_Failure_Blocks_Merge` — replays #2136; expects `needs-human-review` + no merge.
- `Fixture_Scaffolding_Without_Data_Json_Is_Rejected` — expects PM CHANGES REQUESTED.
- `Fixture_Clean_Pr_Merges_Happy_Path` — sanity check; expects merge.
- `Fixture_Placeholder_Strings_Blocked_At_Se_Self_Review` — validates Workstream 1 D1.
- `Fixture_Stale_Screenshot_Filtered_By_Te` — validates Workstream 1 C1.
- `Fixture_Rework_Noop_Halts_Cycle` — validates Workstream 1 A1′ (round-2 identical re-emit → cycle halts, no force-approval).
- `Fixture_SinglePRMode_Emits_One_Task_Plan` — validates gap-analysis R2 stayed stable under refactors. Exercises `LimitsConfig.SinglePRMode=true`; expects exactly one T1 task + T-FINAL in the engineering plan.
- `Fixture_MultiPRMode_Default_Decomposes` — sanity counterpart; `SinglePRMode=false` → expects multi-task plan with T1 scaffolding + children.
- **Fixture_Golden_ReportingDashboard** (merges gap-analysis R7 as a fixture) — full end-to-end: given the ReportingDashboard project description, asserts the resulting PR chain produces a buildable Blazor Server project with header/timeline/heatmap components referenced in `Dashboard.razor` (not literal placeholder strings).

#### Sequencing
- Step 1: build `InMemoryGitHubService` + a minimal harness that runs ONE agent pass (e.g. just Architect review) against a seeded PR. Validate the agent produces expected comments/labels.
- Step 2: add scripted CLI responses; full review flow (Architect → TE → PM).
- Step 3: add SE side (diff ingestion, ready-for-review posting).
- Step 4: add orchestration — state machine, message bus, polling loop — under a virtual time provider so 5-second polls become microseconds.
- Step 5: fixture-driven test discovery (`[Theory]` feeding fixture file names).

### Cost/payoff
- ~2-3 days of scaffolding for Components 1-4, then each fixture is ~30 min to author.
- Every future bug is replayable: copy the real PR's diff + comments into a fixture, run locally, iterate fixes in minutes instead of hours per run.
- Unlocks CI regression prevention — every merged plan phase gets one fixture that proves the gate works.

### Open sub-questions
- Do we use `ITimeProvider` / `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) for deterministic polling, or do we keep wall-clock polls and just cap the test with a generous timeout? Recommend FakeTimeProvider.
- Where does `AgentFactory` pick up the `IGitHubService` — do we need DI rewiring or can we substitute via `IServiceCollection.Replace(…)`? Recommend Replace() in the harness.
- Single `ScriptedCopilotCli` process shared across all agents, or one per agent identity? Recommend one shared with prompt-based routing.

---

## Implementation sequencing recommendation

**Pre-flight:** `git pull` the repo so the other CLI's latest (R1/R2/R3) commits are in your working tree before starting any WS1 item — several WS1 items reference `IGitHubService.GetFileBytesAsync` which only exists after `e99ff88`.

1. **First**: Workstream 2 diagnosis (R2-a through R2-d) — cheap log-greps, no code changes, tells us which inline-comment hypothesis is real before we pick a fix.
2. **Second**: WS1 A1′ + A2 + C1 + D1 (four highest-leverage changes; each touches ~1 file). A1′ especially — it's the direct cure for PR #2136's identical-re-emit cycle.
3. **Third**: Workstream 2 fix (whichever hypothesis wins) — unlocks better review-quality signal.
4. **Fourth**: WS3 Components 1 + 4 + first fixture (`Fixture_Pr2136_…`) — proves we can replay #2136 offline and all subsequent fixes stay green.
5. **Fifth**: Remaining WS1 items (A3, B-PM, C2, C3, D2, E1) + remaining WS3 fixtures + golden-case fixture (absorbs gap-analysis R7).
6. **Later / out of scope for this plan**: gap-analysis R4 (combine PM+Architect) and R5 (in-session build loop) — handled by the other CLI's doc, not duplicated here.

Each item is a separate SQL todo; no implementation starts until user says "go".

## Compile-coordination note

Another CLI is running independent fixes. Any `dotnet build` must be gated on user confirmation. Planning-only work needs no compile. When we start implementing, ask before first build, and always `git pull` first to avoid colliding with the other CLI's in-flight work.


---

## 2026-04-20 execution update — diagnosis complete, scope narrowed

### WS2 diagnosis — CONFIRMED ROOT CAUSE
All hypotheses except #2 refuted by log grep:
- H3 (config off): REFUTED — `appsettings.json` shows `EnableInlineComments: true`.
- H4 (DiffPositionMapper filtering): REFUTED — zero "skipped — file not in PR diff" log lines (which would only log if comments were being attempted).
- H5 (own-PR downgrade): REFUTED — zero "Downgraded ... to COMMENT on own PR" log lines.
- **H2 (LLM drops inline Comments)**: CONFIRMED — but it's **prompt, not drift**.

**Real root cause**: `prompts/architect/pr-review-system.md` instructs the LLM to emit a literal `APPROVED/REWORK` header plus numbered-list body — that is NOT JSON. `ArchitectAgent.TryParseStructuredReview` requires JSON → returns null → text fallback path at line 1473-1478 hardcodes `Comments = []`. `SubmitInlineReviewCommentsAsync` returns early at the `if (comments.Count == 0)` guard. No inline comment is ever submitted.

Same pattern in `SoftwareEngineerAgent.cs` line 4708-4727.

Log evidence: `Logs\*.log` shows `Architect AI didn't return valid JSON for PR #1441 / #1592 / #1593 / ... falling back to text parsing` on EVERY PR reviewed.

### WS1 supersession — 99c13ea already shipped nearly all of it
Commit `99c13ea` (Apr 20 11:54 "Fix dashboard-quality failures: Phases A-E + rubber-duck guards") shipped: A1 (raise caps 3/3/2/2), A2 (UI hard-block on PM force-approval and no-new-commits auto-approval), B1+B2+B3, C1+C2+C3, D1+D2, E1. Follow-up commits `94bc78d` (TE auto-generates UI smoke tests) and `0bbd242` (screenshot consolidated into review comment) closed two more user-reported gaps.

SQL todos marked done: ws1-a2-pm-ui-hardblock, ws1-b-pm-design-anchor, ws1-c1-skip-wrong-pr-screenshots, ws1-c2-no-screenshot-warning, ws1-c3-precapture-cleanup, ws1-d1-se-placeholder-check, ws1-d2-strategy-prompt-no-placeholders, ws1-e1-scaffolding-bootable-data.

### WS2 supersession
- ws2-fix-standalone-review-comments: SUPERSEDED — hypothesis 5 refuted, standalone API rewrite not needed.

### Active remaining scope
1. **ws2-parser-hardening** (NEXT) — prompt + parser fix applied to ArchitectAgent.cs + SoftwareEngineerAgent.cs text-fallback paths. Regex extracts `^\s*\d+\.\s*[\"']?<file>[\"']?:<line>:\s*<body>` items and builds InlineReviewComment list. Prompt updated to encourage the format.
2. **ws2-pm-inline-path** — PM has no inline path; use same helper once built.
3. **ws2-log-upgrade** — promote DiffPositionMapper Debug → Information.
4. **ws1-a1prime-rework-continuation** — less urgent now that caps=3. Hold unless a no-op cycle is observed in a fresh run.
5. **ws1-a3-rework-gate-ui** — redundant with A2 (PM-side block already prevents the merge). Likely drop.
6. **WS3 offline harness** — unchanged; still needed for CI regression coverage.

### Next actions
- Implement WS2 parser hardening (shared helper where possible).
- Update architect review prompt with `file:line:` format guidance.
- Mini-reset validation run after WS2 fix ships (human-gated).
