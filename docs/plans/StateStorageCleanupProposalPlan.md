# State Storage Cleanup Proposal Plan

> Generated 2026-05-21 from rubber-duck assessment by 5 AI models (claude-opus-4.7-high ×2, claude-opus-4.7-xhigh ×2, gpt-5.5).

## Executive Summary

VDT's state persistence is well-designed — **no DB stores full file content**, so a 200GB repo does NOT cause 200GB of state per PR. The real storage risks are:

1. ✅ **IMPLEMENTED**: `activity_log` retention — `PruneOldEntriesAsync` was dead code (zero callers). Now wired into HealthMonitor's periodic timer (30-day retention, daily prune).
2. ✅ **IMPLEMENTED**: Stale candidate worktree cleanup — `CleanupStaleCandidateWorktreesAsync` runs on first health tick, removes `.candidates/` dirs not tracked by `git worktree list`.
3. 🔴 **TODO**: Base64 screenshots in `strategy_candidates` — ~100MB/run stored inline as TEXT. Needs schema migration to store on disk.

## Current State Inventory

| Store | What | Size/Run | Retention? |
|-------|------|----------|-----------|
| `activity_log` | Agent events/decisions | 1-10MB | ✅ 30-day (newly wired) |
| `strategy_candidates` (base64) | PNG screenshots inline | **~100MB** | ❌ None — **#1 DB bloat** |
| `strategy_recovery` | Diff patches | ~10-30MB | ✅ Self-cleaning (INSERT OR REPLACE + delete on apply) |
| `flow_*` tables | FlowMonitor telemetry | Small | ✅ 14-day retention |
| `agent_memory` | Per-agent learnings | Small | ⚠️ PruneAsync exists, not scheduled |
| `.candidates/` worktrees | Full working trees | Repo size × candidates | ✅ Cleanup on startup (newly added) |
| Media artifacts | Screenshots/videos/GIFs | 1-50GB | ❌ Only fresh-reset.ps1 |

## Completed Fixes (This PR)

### Fix 1: Wire `PruneOldEntriesAsync` (activity_log + metrics retention)

**File**: `src/VirtualDevTeam.Orchestrator/HealthMonitor.cs`

- Added `AgentStateStore` as optional dependency to HealthMonitor
- Added `PruneStaleDbEntries()` method — runs once per 24h, deletes entries older than 30 days
- Mirrors FlowMonitorService's proven prune pattern
- Best-effort: catches exceptions, never crashes the health loop

### Fix 2: Stale Candidate Worktree Cleanup

**Files**: `src/VirtualDevTeam.Core/Strategies/GitWorktreeManager.cs`, `src/VirtualDevTeam.Orchestrator/HealthMonitor.cs`

- Added `CleanupStaleCandidateWorktreesAsync` to `GitWorktreeManager`
- Runs `git worktree prune` then scans `.candidates/` for dirs not in `git worktree list`
- Called from HealthMonitor on first health tick (one-time startup cleanup)
- Removes empty parent task/candidates dirs after cleanup

---

## Remaining Proposals (Not Yet Implemented)

### P0: Move Base64 Screenshots to Disk (schema migration)

**Problem**: `strategy_candidates` table stores `screenshot_base64` and `initial_screenshot_base64` as TEXT columns. Each PNG is ~200KB-1MB, base64 expands by ~33%. With 100 candidates/run × 2 screenshots = ~100MB/run, never pruned.

**Proposed fix**:
1. Store screenshots on disk at `{WorkspaceRoot}/strategy-artifacts/{runId}/{taskId}/{strategyId}/screenshot.png`
2. Add `screenshot_path` and `initial_screenshot_path` columns (TEXT, relative paths)
3. One-time migration: extract existing base64 to disk, update paths, null the base64 columns
4. Update `CandidateStateStore.PersistToSqlite()` to write files + paths instead of base64
5. Update `HydrateFromSqlite()` to load from disk paths
6. Update `Strategies.razor` to use `CandidateArtifactService` URL instead of inline base64 `data:` URIs
7. Run `PRAGMA incremental_vacuum` after migration to reclaim space

**Effort**: 4-6 hours (schema migration + 6 Razor read sites + 2 store write sites)

**Risk**: Medium — requires careful migration to not break the Frameworks page for in-progress runs.

### P1: Wire `agent_memory.PruneAsync`

**Problem**: `AgentMemoryStore.PruneAsync(keepCount)` exists but has no callers. Memory entries accumulate forever. Only the 30 most recent are read, so older entries are dead weight.

**Proposed fix**: Add `PruneAsync(keepPerAgent: 100)` call to the same HealthMonitor daily prune pass.

**Effort**: 15 minutes.

### P1: FlowMonitor DB Size Detector

**Problem**: No visibility into DB size growth. Operators don't know when to clean up until disk fills.

**Proposed fix**:
1. Add `DbSizeDetector : IFlowDetector` that checks `PRAGMA page_count * page_size` on each tick
2. Emit `Severity=Medium` finding when main DB exceeds 500MB, `Critical` at 1GB
3. Finding links to existing Configuration → Reset State panel
4. Register in `RunnerHealthMonitorExtensions.cs`

**Effort**: 1-2 hours.

### P2: Storage Visibility Panel

**Problem**: No UI shows current storage usage (DB sizes, worktree count, artifact disk usage).

**Proposed fix**:
1. Add "Storage" section to Configuration page
2. Show: main DB size, recovery DB size, local platform DB size, `.agents/` disk usage, `.candidates/` worktree count, media artifact count/size
3. "Refresh" button to rescan
4. Deep link from FlowMonitor findings

**Effort**: 2-3 hours.

### P2: Operational vs Historical DB Split

**Problem**: Operational state (current run recovery) and historical data (metrics, decisions, activity logs) share one DB. Can't prune operational without losing history.

**Proposed fix**:
- `virtualdevteam_operational_{repo}.db` — current run state, strict retention
- `virtualdevteam_history_{repo}.db` — summarized outcomes, costs, decisions
- On run completion: summarize operational → history, then compact operational

**Effort**: Significant refactor — 1-2 days. Deferred until enterprise multi-tenant scenario materializes.

---

## Key Design Rule

> **Never force-reset state without explicit user permission.** Any reset — whether triggered by FlowMonitor, CLI, or Dashboard — MUST go through an approval gate (dashboard Approval card, CLI confirmation prompt, or FlowMonitor approval). No exceptions.

## What Is NOT a Problem

- **Repo size ≠ state size**: No DB stores file content. LocalPlatform stores paths/stats, not patches.
- **Default workspace is InPlace**: Worktrees share `.git/objects` — not 5× the repo size.
- **FlowMonitor has proper retention**: 14-day default, daily prune.
- **Strategy recovery is self-cleaning**: `INSERT OR REPLACE` + delete on apply.
- **Small metadata tables** (`gate_approvals`, `ai_usage`, `processed_items`, `run_metadata`) are bounded and insignificant.
