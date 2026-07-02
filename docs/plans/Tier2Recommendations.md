# Tier-2 FlowMonitor — Deferred Detectors

> **Purpose:** This document tracks the 12 Tier-2 detectors that were intentionally deferred during the
> 2026-05-11 implementation push. The 11 implemented detectors (5 HIGH + 6 MEDIUM flow-state) cover the
> stuck-flow classes that have actually fired in real runs. The 12 deferred detectors below are useful but
> speculative — they protect against bug classes that haven't yet been observed in production.
>
> **Revisit criteria.** Promote a deferred detector to the "implement" queue when:
> 1. A real run hits the bug class it would catch (look in `findings` + `run_issues` tables).
> 2. The cost/benefit shifts (e.g., GitHub rate-limit headers start tripping `429`s).
> 3. The base infrastructure it depends on stabilises (e.g., FilePlan enforcement matures).
>
> **Master plan reference.** Full descriptions live in `plan.md` (session workspace) T2.5, T2.6, T2.12,
> T2.14–T2.20, T2.22, T2.23, T2.24.

## Why these are deferred

The 11 detectors that just shipped are all motivated by an actual finding row in the tracking DB or a
documented run incident. The 12 below would prevent classes of bugs that **haven't yet fired** in any run
we have records for. Building speculative coverage is wasted code — every detector adds tick cost,
audit-log noise, and false-positive risk. Wait for real signal before paying that price.

## Deferred — Code/scope safety (6)

These detectors enforce policies on what agents are allowed to commit. The base FilePlan enforcement
matures over time; until it does, runtime detection here is mostly speculative.

### T2.5 `FilePlanViolationDetector` (MEDIUM)
- **Signal:** PR diff contains files outside the agent's declared FilePlan AND not in T1 SHARED registry.
- **Action:** PR comment listing rogue files (Critical) + emit finding; flag for re-review.
- **Defer rationale:** plan-time `DetectFileOverlaps` is currently the canonical enforcement point.
  Runtime detection would require persisting FilePlan-per-task and re-checking against the PR diff. Not
  worth it until we observe a real "engineer wrote rogue files" incident.
- **Revisit when:** finding or run_issues row documents an engineer modifying files outside scope at runtime.

### T2.6 `ProductionFileGuardDetector` (MEDIUM)
- **Signal:** non-Architect agent modifies blocklisted file (`Program.cs`, `appsettings.json`, `*.csproj`,
  certs, secrets).
- **Action:** post comment requiring human approval; block AutoApprove gate.
- **Defer rationale:** the blocklist needs maintenance (different project = different "production" files);
  the current `human-review-required` label + gate covers most of this. Build this when the blocklist gets
  centralised.
- **Revisit when:** agents start touching infra files outside their scope and we need a guard.

### T2.15 `CommitMessageDriftDetector` (MEDIUM)
- **Signal:** ≥50% of commits on an agent's branch don't reference the assigned task ID.
- **Defer rationale:** the assigned task ID isn't always visible in commit messages today; the conformance
  metric is more useful as a post-hoc audit than a real-time detector.
- **Revisit when:** the team starts measuring commit-message hygiene seriously.

### T2.16 `ZeroTestFileDetector` (MEDIUM)
- **Signal:** TE agent's PR diff has 0 `*Tests*.cs`/`*Spec.ts` files AND no `[TE-BYPASS]` label.
- **Defer rationale:** the merge gate could enforce this without needing a flow detector — adding a
  pre-merge check in `PullRequestWorkflow` is the better architecture.
- **Revisit when:** we observe a TE PR sneaking through with zero tests and no bypass label.

### T2.17 `RoleBoundaryDetector` (MEDIUM)  — *deferred for now, but high real-world signal*
- **Signal:** reviewer ID == author ID (self-approval), OR PM approval text Levenshtein < 10 from prior
  approval (rubber-stamp).
- **Defer rationale (rubber-duck):** finding #8 (HIGH) — self-assessment auto-pass on empty LLM response —
  is the real, observed instance of this. Fix the empty-response bug first (`EngineerAgentBase.cs:3524`
  should fail-safe to `NEEDS_CHANGES`). Once that one-line fix ships, evaluate whether a broader
  detector is still needed. The detector is a defense-in-depth layer; the actual fix is one-line.
- **Revisit when:** the one-line `EngineerAgentBase.cs:3524` fix is in and we've observed a *new*
  self-approval instance afterwards.

### T2.19 `PrSemanticDriftDetector` (MEDIUM, AI-assisted)
- **Signal:** when a PR is `ready-for-review`, send PR description + first 200 lines of diff to LLM:
  "Does diff implement what description claims? Return 0.0–1.0." Below `ConfidenceThreshold` (0.75) → drift.
- **Defer rationale:** the AI Anomaly Detector (T2.21, just shipped) gives us a cheaper general-purpose
  AI safety net. Add this targeted detector only if T2.21 misses semantic-drift cases in real runs.
- **Revisit when:** T2.21 fires a generic anomaly and the root cause turns out to be semantic drift, three
  or more times across runs.

## Deferred — Infrastructure / housekeeping (4)

Plumbing — useful but not load-bearing. These prevent edge-case incidents that haven't fired yet.

### T2.12 `LabelSyncReconciliationAction` (MEDIUM)
- **Type:** action, not detector — applies to existing label-mismatch findings.
- **Signal/action:** fetch current labels, compute desired, write only the delta. Idempotent.
- **Defer rationale:** Lessons Learned #4 says concurrent label writes silently lose labels. Today's
  workaround is "re-fetch before writing." A canonical idempotent action is cleaner but the workaround
  works. Build this when the workaround pattern proliferates and centralising helps maintainability.
- **Revisit when:** ≥3 different places have the re-fetch-before-write idiom and we want to centralise.

### T2.14 `ApiRateLimitDetector` (MEDIUM)
- **Signal:** GitHub `/rate_limit` endpoint reports `remaining < 100` (warn) or `< 20` (critical).
- **Action:** alert operator + throttle non-essential FlowMonitor API calls (skip merged-PR scans, skip
  branch list calls, etc.).
- **Defer rationale:** never observed in any run we have records of. EMU accounts have generous limits;
  rate-exhaustion would silently stall all agents but we'd notice via other detectors (everything looks
  idle). Build when we see a real 429.
- **Revisit when:** any run shows a 429 from `octokit.net` in logs, or we add a project with a
  rate-restricted PAT.

### T2.22 `WorkspaceBloatDetector + ArtifactGC` (MEDIUM)
- **Signal:** `.agents/` directory size > 10GB.
- **Action:** delete `strategy-artifacts/<old-runId>` folders older than 7 days.
- **Defer rationale:** disk space hasn't been a problem because runs clean their own scratch areas; the
  `scripts/kill-orphan-runner-procs.ps1` cleanup script handles the worst case.
- **Revisit when:** a developer reports `.agents/` consuming >5GB or runner-disk alarms fire.

### T2.23 `EmptyPatchCircuitBreaker` (MEDIUM)
- **Signal:** strategy framework returns ≥2 retries with empty patches for the same task.
- **Defer rationale:** strategy framework already has fallback logic in `StrategyOrchestrator`. Adding a
  FlowMonitor-level circuit breaker would double-cover; trust the strategy framework's own retry budget
  first.
- **Revisit when:** strategy framework fallback logic fails to break the loop and we see ≥3 empty-patch
  retries in one task.

## Deferred — LOW priority (2)

Speculative coverage for rare scenarios. Almost certainly never worth building.

### T2.20 `AgentIdentityDuplicationDetector` (LOW)
- **Signal:** same `AgentId` prefix authoring ≥2 simultaneous PRs.
- **Defer rationale:** `AgentRegistry` already enforces single-instance-per-id at registration. A
  duplicate could only happen via a misconfiguration or a bug in the spawn path; if that's happening
  we'd see it via other symptoms (race-y label writes, doubled merge attempts).
- **Revisit when:** anomaly observed; otherwise: never.

### T2.24 `DuplicateImplementationDetector` (LOW)
- **Signal:** new PR's file set overlaps >60% with a merged PR's file set AND task-title cosine similarity
  > 0.85.
- **Defer rationale:** highest effort in the list (file embeddings + history fetch + cosine math + LLM).
  The task-planning phase already deduplicates work via the PM's spec breakdown; runtime detection here
  would be reactive only. Cost > benefit.
- **Revisit when:** PM repeatedly produces overlapping tasks across runs and we want a post-spec dedup
  guard.

## Implementation cost summary

If all 12 were implemented:
- **6 SMALL** (T2.12, T2.14, T2.15, T2.16, T2.20, T2.22): ~1 day each, ~6 days total
- **5 MEDIUM** (T2.5, T2.6, T2.17, T2.19, T2.23): ~2 days each, ~10 days total
- **1 HIGH** (T2.24): ~5 days

**Total:** ~21 days of focused work for protection against bugs that haven't fired yet. Cherry-pick if
and when real findings justify them.

## Where the shipped 11 came from

For comparison — every shipped Tier-2 detector in this batch maps to a real symptom in the tracking DB:

| Shipped | Maps to |
|---|---|
| T2.1 IdleAgentPhaseStuckDetector | `run_issues` #5 — agent-stuck label on PR #1367 after agent went Idle |
| T2.2 TEFalseCompletionDetector | `findings` #12 (CRITICAL) — PR #1261 merged with failing UI tests |
| T2.3 LabelTransitionTimeoutDetector | `run_issues` #6 — PR #1394 all approvals 30+ min unmerged |
| T2.4 ReworkSaturationDetector | `findings` #1/#2/#3 — duplicate reviews + force-approve thrash |
| T2.7 HandoffGapDetector | `findings` #1 (HIGH) — PM posted CHANGES_REQUESTED 5x in 23min |
| T2.8 PhaseAdvancementWatchdog | `run_issues` #7 — engineering.all.complete firing prematurely (forward-stall counterpart) |
| T2.9 StatusReasonStagnationDetector | `run_issues` #1 — FlowMonitor false positive on legit Strategy work, need finer-grained stuck signal |
| T2.10 OrphanPRDetector | Lessons Learned #7 — restart leaves orphan PRs |
| T2.11 IdleIdleCycleDetector | `findings` #2 — force-approve idempotency loss, observed Idle/Working thrash |
| T2.18 EmptyQueueDetector | `findings` #4 — SE planning before all issues created |
| T2.21 AI Anomaly Detector | catch-all for unknown future classes |
