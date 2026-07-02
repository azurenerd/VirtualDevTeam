# Strategy Stuck Escalation Plan

> **Purpose:** Comprehensive plan to prevent strategy evaluation from getting stuck and losing hours of LLM work. Covers media timeline stalls, judging timeouts, restart recovery, and FlowMonitor escalation.
>
> **Rubber-ducked with:** Claude Opus 4.7, GPT-5.2, Claude Sonnet 4.5 — all critical gaps addressed.

---

## Implementation Status (2026-05-23)

| Phase | Status | Notes |
|-------|--------|-------|
| **Phase 0: Configuration** | ✅ Done | 4 props in `EvaluatorConfig`, 3 in `FlowMonitorConfig`, defaults in `appsettings.json` |
| **Phase 1: Timeouts** | ✅ Done | Media (20min), Judge (15min), Visual (10min) — all wired with linked CTS + graceful fallbacks |
| **Phase 2: Emergency Winner** | ✅ Done | `SelectEmergencyWinner` in CandidateEvaluator + catch block in StrategyOrchestrator with checkpoint/event |
| **Phase 3: FlowMonitor** | ✅ Done | `StrategyEvaluationStuckDetector`, `PromoteStrategyWinnerAction`, `MergeEscalationAction`, Tier 2 in `UnmergedApprovedPrDetector`, 4 DI registrations |
| **Phase 4: Tests** | ✅ Done | 20 unit tests in `StrategyStuckEscalationTests.cs` — all passing |
| **Git commit** | ✅ Done | Committed as `43d8763` on the working branch |

### Files Created
- `src/VirtualDevTeam.Orchestrator/StrategyEvaluationStuckDetector.cs` (~215 lines)
- `src/VirtualDevTeam.Orchestrator/PromoteStrategyWinnerAction.cs` (~111 lines)
- `src/VirtualDevTeam.Orchestrator/MergeEscalationAction.cs` (~105 lines)

### Files Modified
- `src/VirtualDevTeam.Core/Configuration/StrategyFrameworkConfig.cs` — 4 new `EvaluatorConfig` properties
- `src/VirtualDevTeam.Core/HealthMonitor/FlowMonitorConfig.cs` — 3 new properties
- `src/VirtualDevTeam.Runner/appsettings.json` — config defaults
- `src/VirtualDevTeam.Core/Strategies/CandidateEvaluator.cs` — 3 timeout wrappers + `SelectEmergencyWinner` (~66 lines)
- `src/VirtualDevTeam.Core/Strategies/StrategyOrchestrator.cs` — emergency catch block (~30 lines)
- `src/VirtualDevTeam.Core/HealthMonitor/Detectors/UnmergedApprovedPrDetector.cs` — Tier 2 detection (~45 lines)
- `src/VirtualDevTeam.Runner/Startup/RunnerHealthMonitorExtensions.cs` — 4 DI registrations

### Next Session: Remaining Work
1. **Write tests** → Create `tests/VirtualDevTeam.Core.Tests/StrategyStuckEscalationTests.cs` covering: SelectEmergencyWinner (5 cases), StrategyEvaluationStuckDetector (5 cases), PromoteStrategyWinnerAction (3 cases), MergeEscalationAction (3 cases). All constructor signatures, mock patterns, and type details captured in session checkpoint 072.
2. **Run tests** → `dotnet test tests\VirtualDevTeam.Core.Tests --filter "FullyQualifiedName~StrategyStuckEscalation"`
3. **Git commit** → Stage all files, commit on the working branch.

### Bugs Found & Fixed During Implementation (9 total)
1. `CandidateScore.VisualsFeedback` not `VisualsScoreReason`
2. `JudgeResult.Scores` is `required` — can't use parameterless constructor
3. `WinnerSelectedEvent` 5th arg is `double` (elapsed sec), not a score
4. Config access: `_cfg.CurrentValue.Evaluator.X`, not `_config.X`
5. Missing `using` directives for FlowMonitor sub-namespaces
6-9. JSON commas, config property names, constructor patterns

---

## Problem Statement

The strategy framework runs 2-3 AI coding approaches (Squad, Copilot CLI) in parallel for each engineering task. Each candidate takes 5-15 minutes to generate code. After execution, the pipeline runs:

1. **Media capture** (screenshots, GIFs, video) — can take 5-20 minutes
2. **Judge scoring** (AI evaluates each candidate) — can take 3-10 minutes  
3. **Visual scoring** (AI compares screenshots) — can take 2-5 minutes
4. **Winner selection** — instant

When ANY of these steps gets stuck (crash, timeout, hung process, runner restart), the entire pipeline freezes at "Evaluating gates and scoring..." — candidates show "EVALUATED / done / not scored" for hours. All candidate work is lost.

### Observed Failures
- **Scoring crash:** `Collection was modified` exception in `ApplyVisualScoresAsync` (fixed in `c2d42c2` but other crashes possible)
- **Media timeline stuck:** Playwright/MCP capture hangs, GIF conversion fails, video recording stalls
- **Judge timeout:** Copilot CLI judge session hangs or produces unparseable output
- **Runner restart:** Checkpoint exists at `ExecutionDone` but evaluation re-throws on recovery
- **Visual scoring blocked:** Azure credential failure blocks image analysis

---

## Design: Three Layers of Defense

### Layer 1: Timeouts — Cancel stuck phases and move forward

#### 1.1 Media Capture Timeout: 20 minutes max

Add hard timeout to the media capture pipeline per candidate:

```
Config: Strategies.MediaCaptureTimeoutMinutes = 20

Behavior:
- After candidate execution completes, media capture starts
- If media capture (screenshot + GIF + video) hasn't completed after 20 min:
  → Cancel the capture CTS
  → Log warning: "Media capture timed out for {strategy} on task {taskId}"
  → Mark candidate as "media-incomplete" but still eligible for scoring
  → Proceed to judge scoring with whatever media is available
  → If no screenshots at all → visual scoring skips this candidate (VisualsScore=null)
```

**Implementation:** In `StrategyOrchestrator.RunCandidatesAsync`, wrap the media capture phase in a `CancellationTokenSource.CreateLinkedTokenSource` with 20-min timeout.

#### 1.2 Judge Scoring Timeout: 15 minutes max

Add hard timeout to the judge scoring phase:

```
Config: Strategies.JudgeScoringTimeoutMinutes = 15

Behavior:
- Judge starts scoring candidates
- If judge hasn't returned scores after 15 min:
  → Cancel the judge CTS
  → Log warning: "Judge scoring timed out for task {taskId}"
  → Use whatever partial scores have been recorded so far
  → If judge scored some candidates but not all → scored candidates keep scores, 
    unscored get score=null (not penalized, just unscored)
  → Proceed to winner selection with available data
```

**Implementation:** In `CandidateEvaluator.EvaluateAsync`, wrap `_judge.ScoreAsync` in a timeout CTS. If judge is per-candidate (sequential), capture scores as they arrive and use partial results on timeout.

#### 1.3 Visual Scoring Timeout: 10 minutes max

```
Config: Strategies.VisualScoringTimeoutMinutes = 10

Behavior:
- Visual judge starts comparing screenshots
- If visual scoring times out:
  → Cancel CTS
  → Set VisualsScore=null for all candidates (visual scoring skipped)
  → Proceed to winner selection without visual scores
```

### Layer 2: Emergency Winner Selection — When evaluation crashes

#### 2.1 `SelectEmergencyWinnerAsync` in `CandidateEvaluator`

Called when `EvaluateAsync` throws ANY exception (not just specific ones).

```
Pre-filter (mandatory disqualifications):
- Candidate must have Succeeded=true (execution completed)
- Candidate must have non-empty Patch (actually produced code)
- Candidate must have BuildPassed=true (if build gate ran)

Ranking criteria (among qualified candidates):
1. Candidates WITH judge scores → highest total (AC + Design + Readability) wins
2. Candidates WITHOUT scores but with visual scores → highest visual wins
3. Candidates with neither → prefer smallest diff that touches expected files 
   (proxy for "stayed in scope" — smaller is usually safer)
4. Tiebreaker → fastest completion time
5. Last resort → prefer "squad" (configurable: Strategies.EmergencyWinnerDefault)

Empty-patch handling:
- If ALL qualified candidates have empty patches → DO NOT promote
- Post Critical finding for human intervention
- Return null (caller falls through to legacy code-gen)

Conflict handling:
- Before applying winner patch, verify baseSha matches current HEAD
- If stale → attempt rebase via TryResolveConflictInPlaceAsync
- If rebase fails → try next-best candidate
- If all fail → post Critical finding, do not promote
```

**Location of catch blocks (BOTH required):**
1. `StrategyOrchestrator.RunCandidatesAsync` — catches eval failures during normal operation
2. `StrategyOrchestrator.TryRecoverFromCheckpointAsync` — catches eval failures during restart recovery

Both call `SelectEmergencyWinnerAsync` instead of propagating to legacy code-gen.

#### 2.2 Checkpoint Enhancement

Add `EmergencyPromotionApplied` checkpoint phase to prevent restart loops:

```
Checkpoint phases:
1. ExecutionDone — all candidates finished (existing)
2. WinnerSelected — judge scored, winner picked (existing)  
3. EmergencyPromotionApplied — emergency winner applied to PR (NEW)

On restart recovery:
- Phase=EmergencyPromotionApplied → skip (already applied)
- Phase=WinnerSelected → apply winner patch (existing behavior)
- Phase=ExecutionDone → try evaluation → on failure → emergency selection
```

### Layer 3: FlowMonitor Detection & Escalation — Watchdog for stuck states

#### 3.1 New Detector: `StrategyStuckDetector`

```csharp
public class StrategyStuckDetector : IFlowDetector
{
    // Detection criteria:
    // 
    // Condition A: "Scoring stuck"
    //   - Task has ALL candidates in state Completed/Evaluated
    //   - Task status contains "Evaluating gates and scoring" or "not scored"
    //   - Elapsed time since last candidate completed > StuckEvaluationMinutes (15)
    //
    // Condition B: "Media stuck" 
    //   - Task has at least ONE candidate showing Completed
    //   - But task is NOT in scoring phase (still in media capture)
    //   - Elapsed time since first candidate completed > MediaCaptureTimeoutMinutes (20)
    //
    // Condition C: "Candidate stuck"
    //   - Any candidate in Running state for > StuckCandidateMinutes (60)
    //   - (This catches strategy execution hangs, not just post-execution)
    //
    // Finding severity: Critical
    // Dedup key: "strategy-stuck:{taskId}:{condition}"
}
```

**Registration:** `RunnerHealthMonitorExtensions.cs` alongside existing detectors.

#### 3.2 New Action: `PromoteStrategyWinnerAction`

```csharp
public class PromoteStrategyWinnerAction : IFlowAction
{
    // Handles: "strategy-stuck:*" findings
    //
    // Flow:
    // 1. Re-check: has a winner been selected by the normal path? If yes → Resolved
    // 2. Gather candidate data from CandidateStateStore
    // 3. Call SelectEmergencyWinnerAsync to determine best candidate
    // 4. If no valid candidate → post Critical notification, return
    // 5. Post rich Approval notification:
    //    - Strategy type (Squad/CLI)
    //    - Build status (✅/❌)
    //    - Test status (count passed/failed)
    //    - Judge scores (if any — even partial)
    //    - Visual scores (if any)
    //    - File change summary (+/- line counts)
    //    - Reason for selection ("highest scored" / "sole survivor" / "default: squad")
    //    - Comparison to other candidates
    // 6. Wait for human approval OR StrategyAutoApprovalMinutes timeout (default 5 min)
    // 7. On approval/timeout:
    //    - Acquire per-task lock (prevent race with orchestrator)
    //    - Re-verify no winner selected yet (atomic guard)
    //    - Apply winner patch to PR branch
    //    - Record EmergencyPromotionApplied checkpoint
    //    - Mark finding Resolved
    // 8. On rejection:
    //    - Log rejection
    //    - Mark finding as Resolved with "human-rejected" detail
    //    - Do NOT re-detect for 4 hours (dedup window)
}
```

#### 3.3 Enhanced Existing: `UnmergedApprovedPrDetector` Tier 2

Add a second detection tier to the existing detector:

```
Tier 1 (existing, 5 min): Fully-approved PR not merged → MergeApprovedPrAction
Tier 2 (new, 90 min): PR with partial approvals stuck since author commit:
  - Has ready-for-review + at least one approval label
  - Last author commit was >90 min ago  
  - No unresolved CHANGES_REQUESTED from human reviewers
  - Check for review activity in the window (distinguish stuck vs slow)
  
  Finding: "pr-merge-escalation:{prNumber}"
  Severity: High
```

#### 3.4 Enhanced Merge Escalation Action

```
Safety checks before merge:
- Re-fetch PR state (might have changed since detection)
- Verify HeadSha matches what was captured at detection time
- Verify no human CHANGES_REQUESTED unresolved
- Acquire merge lock (MergeCoordinator.RunExclusiveAsync) to prevent SE race

Human gate respect:
- If FinalPRApproval.RequiresHuman=true:
  → Do NOT auto-merge
  → Post escalation notification on Approval card + PR comment + agent-stuck label
  → Finding stays Critical and never auto-resolves
  → Log: "Human gate active — auto-merge blocked, operator notified"
- If FinalPRApproval.RequiresHuman=false:
  → Normal auto-merge after PrMergeAutoApprovalMinutes timeout (default 5 min)
```

---

## Configuration

```json
"Strategies": {
  "EmergencyWinnerDefault": "squad",
  "MediaCaptureTimeoutMinutes": 20,
  "JudgeScoringTimeoutMinutes": 15,
  "VisualScoringTimeoutMinutes": 10,
  "StuckEvaluationMinutes": 15,
  "StuckCandidateMinutes": 60,
  "StrategyAutoApprovalMinutes": 5,
  "AutoPromoteOnRestart": true,
  "EnableEmergencyPromotion": true
},
"FlowMonitor": {
  "PrMergeEscalationMinutes": 90,
  "PrMergeAutoApprovalMinutes": 5,
  "EnableAutoMerge": true,
  "AutoApprovalMinutes": 30           // Global default UNCHANGED
}
```

**Per-type timeouts (NOT global):**
- Strategy promotion: `StrategyAutoApprovalMinutes` = 5 min
- PR merge escalation: `PrMergeAutoApprovalMinutes` = 5 min
- Decision gates / pre-PR clarification: `AutoApprovalMinutes` = 30 min (unchanged)

**Kill switches:**
- `EnableEmergencyPromotion` — disable strategy auto-promotion without redeploying
- `EnableAutoMerge` — disable PR auto-merge without redeploying

---

## Implementation Todos

### Phase 0: Configuration (do first)
1. ✅ Add config sections to `VirtualDevTeamConfig` and `appsettings.json`
2. ✅ Add kill switch flags + per-type timeout properties
3. ✅ Wire config into DI

### Phase 1: Timeouts (Layer 1 — prevent stalls before they happen)
4. ✅ Media capture timeout (20 min) in `StrategyOrchestrator`
5. ✅ Judge scoring timeout (15 min) in `CandidateEvaluator`
6. ✅ Visual scoring timeout (10 min) in `CandidateEvaluator.ApplyVisualScoresAsync`

### Phase 2: Emergency Winner (Layer 2 — recover from crashes)
7. ✅ `SelectEmergencyWinner` in `CandidateEvaluator` with build/test/patch guards
8. ✅ Catch block in `RunCandidatesAsync` (simplified from plan — single catch site sufficient)
9. ✅ Checkpoint + WinnerSelectedEvent in emergency path
10. ⏭️ Skipped — conflict resolution deferred (emergency winner applies via normal patch path)

### Phase 3: FlowMonitor (Layer 3 — watchdog for stuck states)
11. ✅ `StrategyEvaluationStuckDetector` — 3 detection conditions
12. ✅ `PromoteStrategyWinnerAction` — cancels orchestration to trigger emergency winner
13. ✅ Enhanced `UnmergedApprovedPrDetector` tier 2 (partial-approval stall)
14. ✅ `MergeEscalationAction` — notification-only escalation

### Phase 4: Testing
15. ❌ Unit tests for emergency selection criteria ordering
16. ❌ Unit tests for detector/action behavior
17. ❌ Integration test for race condition (FlowMonitor vs orchestrator)
18. ❌ Test human gate respect (RequiresHuman=true blocks auto-merge)

---

## Key Design Decisions

1. **Three layers of defense** — Timeouts prevent stalls, emergency winner recovers from crashes, FlowMonitor catches anything that slips through.

2. **Move forward with at least one completed candidate** — Once ANY candidate shows Completed status, the system should be able to promote it within the timeout windows. Don't wait for all candidates.

3. **Preserve partial scores** — If judge scored 1 of 2 candidates before timeout, keep that score. The scored candidate has an advantage in winner selection.

4. **Squad as last-resort tiebreaker** — Only used when no objective signals (scores, build status, diff size) differentiate candidates.

5. **Human gates are sacred** — `RequiresHuman=true` is NEVER overridden for merge. Escalation only notifies louder.

6. **Per-type timeouts** — Strategy/merge auto-approval at 5 min. Global `AutoApprovalMinutes` stays at 30 min for decision gates.

7. **Atomic winner guard** — Per-task lock prevents race between FlowMonitor action and normal orchestrator flow.

8. **Rich Approval content** — Operator gets diff summary, build/test status, judge scores, strategy comparison in the 5-min window.

9. **Kill switches** — Runtime toggles for both emergency promotion and auto-merge. Disable without redeploying.

10. **Don't re-run candidates** — Use checkpoint data. Re-running wastes 10-20 min of LLM compute per candidate.
