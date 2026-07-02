# LocalDevPlatform: Enterprise PR Workflow for VDT

## Problem Statement

VDT requires agents to merge their own PRs as part of the multi-agent development workflow. This is impossible in enterprise repos where branch policies require 1-2 human reviewers (Microsoft standard), blocking the entire pipeline for hours to days and making VDT unusable for real enterprise projects.

## Proposed Solution: LocalDevPlatform + Upstream Mirror (Hybrid)

**Unanimous consensus from 5 top-tier models (Opus 4.7, Opus 4.7 High, Opus 4.5, GPT-5.5, GPT-5.4), validated by 2 rubber-duck critiques (Opus 4.7 Extra-High, GPT-5.5).**

### Core Concept
Create a new `LocalDevPlatform` provider implementing all existing capability interfaces, backed by SQLite metadata + a local bare git repository. Agents work exactly as they do today — the full multi-PR, multi-review, multi-agent quality loop is preserved — but PRs/reviews/merges happen locally without touching the enterprise platform. At completion, one clean PR is submitted to the real repo for human review.

### Why NOT Option B (Single-PR-with-Commits)?
All 5 models and both rubber ducks rejected it:
- Breaks the PR-centric architecture (lifecycle calculator, FlowMonitor, labels, recovery)
- Destroys review atomicity (PM/Architect/TE can't review individual tasks)
- Strategy framework can't evaluate candidates without per-task isolation
- Merge conflicts between parallel agents on a shared branch
- Would require rewriting ~30% of engineering-agent code paths
- Re-incurs most bugs from LessonsLearned.md (#18, #19, #27, #29, #30, #35, #41, #43, #45)

## Architecture

### Key Design Decisions

1. **Git is the authority, SQLite is metadata** — Local PRs are NOT just database rows. A real local bare git repo backs all branches, commits, merges, and diffs. SQLite stores PR metadata, labels, comments, reviews, and lifecycle state. Strategy framework needs real SHAs.

2. **Local bare repo as workspace origin** — Agent worktrees point their `origin` at a local bare repo (e.g., `.agents/local-platform/repo.git`), not the enterprise remote. The safety guard in `WorktreeWorkspace.PushAsync` must be updated for this model.

3. **Single squash PR as default submission, multi-PR optional** — Default: one clean PR to the enterprise repo's main branch. For larger projects, operators can choose per-feature or per-wave submission (multiple smaller PRs). No compliance blocker either way.

4. **Upstream mirror is optional and best-effort** — `IUpstreamMirror` pushes agent branches to the real remote for visibility, but never blocks agent progress. Push-event scanning only (not PR-event CI — that requires draft PRs, a Phase 4+ consideration).

5. **Fork-based workflow as immediate bridge** — Zero code changes needed. Document in wizard: "Point VDT at your personal fork, then open a PR from fork → enterprise repo." Ships this week.

### New Components

```
Core/DevPlatform/Providers/Local/
├── LocalPullRequestService.cs       # IPullRequestService backed by SQLite + local git
├── LocalWorkItemService.cs          # IWorkItemService backed by SQLite
├── LocalReviewService.cs            # IReviewService backed by SQLite
├── LocalBranchService.cs            # IBranchService wrapping local bare repo
├── LocalRepositoryContentService.cs # IRepositoryContentService reading from worktrees
├── LocalRepositoryManagementService.cs # IRepositoryManagementService
├── LocalPlatformInfoService.cs      # IPlatformInfoService / IPlatformHostContext
├── LocalBareRepoManager.cs          # Creates/manages the local bare git repository
└── LocalPlatformSchema.cs           # SQLite schema + migrations

Core/DevPlatform/Submission/
├── ISubmissionService.cs            # Interface for final PR creation
├── GitHubSubmissionService.cs       # Opens PR on GitHub
├── AdoSubmissionService.cs          # Opens PR on ADO
└── SubmissionStateStore.cs          # Persists submission state machine

Core/DevPlatform/Mirror/
├── IUpstreamMirror.cs               # Best-effort push to real remote
├── NullUpstreamMirror.cs            # No-op (pure local mode)
├── GitHubUpstreamMirror.cs          # Pushes branches to GitHub
└── AdoUpstreamMirror.cs             # Pushes branches to ADO
```

### SQLite Schema (run-scoped)

```sql
-- All tables scoped by run_id for isolation
CREATE TABLE local_pull_requests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id TEXT NOT NULL,
    number INTEGER NOT NULL,  -- auto-incrementing per run
    title TEXT, body TEXT,
    state TEXT DEFAULT 'open',  -- open/closed/merged
    head_branch TEXT, head_sha TEXT,
    base_branch TEXT, base_sha TEXT,
    assigned_agent TEXT,
    created_at TEXT, updated_at TEXT, merged_at TEXT,
    UNIQUE(run_id, number)
);
CREATE TABLE local_pr_labels (pr_id INTEGER, label TEXT, PRIMARY KEY(pr_id, label));
CREATE TABLE local_pr_comments (id INTEGER PRIMARY KEY, pr_id INTEGER, author TEXT, body TEXT, created_at TEXT);
CREATE TABLE local_pr_reviews (id INTEGER PRIMARY KEY, pr_id INTEGER, reviewer TEXT, state TEXT, body TEXT, created_at TEXT);
CREATE TABLE local_pr_threads (id INTEGER PRIMARY KEY, pr_id INTEGER, path TEXT, line INTEGER, commit_sha TEXT, body TEXT, resolved INTEGER DEFAULT 0);
CREATE TABLE local_pr_files (pr_id INTEGER, path TEXT, status TEXT, additions INTEGER, deletions INTEGER);
CREATE TABLE local_work_items (id INTEGER PRIMARY KEY, run_id TEXT, number INTEGER, title TEXT, body TEXT, state TEXT, assigned_agent TEXT, labels_json TEXT, created_at TEXT, updated_at TEXT, closed_at TEXT);
CREATE TABLE local_work_item_comments (id INTEGER PRIMARY KEY, work_item_id INTEGER, author TEXT, body TEXT, created_at TEXT);
CREATE TABLE local_work_item_links (work_item_id INTEGER, linked_pr_id INTEGER, link_type TEXT);
CREATE TABLE local_branches (name TEXT PRIMARY KEY, run_id TEXT, head_sha TEXT, created_at TEXT);
```

### Configuration

```jsonc
// develop-settings.json
{
  "devPlatformKind": "Local",  // "GitHub" | "AzureDevOps" | "Local"
  "localPlatform": {
    "upstreamMirror": "None",  // "None" | "GitHub" | "AzureDevOps"
    "submissionTarget": {
      "platform": "GitHub",     // where to open the final PR
      "repo": "org/repo",
      "baseBranch": "main"
    }
  }
}
```

### Dashboard Changes (Minimal)
- "Local" badge on PR cards when `DevPlatformKind == Local`
- "Submit to Upstream" button on Completion phase (state machine: NotSubmitted → Preparing → PushedBranches → PrCreated → ReadyForHuman)
- Local PR URLs route to dashboard: `/repository/local/pr/{id}`
- All existing pages (Repository, PullRequests, PullRequestDetail, Issues, Timeline, Approvals) continue working unchanged via capability interfaces

## Critical Pre-Requisites (from Rubber Duck Reviews)

### P0: ~~Validate Compliance Acceptance~~ ✅ RESOLVED
Single-PR submission is confirmed acceptable. For larger projects, multiple PRs can be submitted (per-feature or per-wave). No compliance blocker.

### P1: Complete `IGitHubService` Migration
> "RunCoordinator, Dashboard, GateNotificationService still depend on IGitHubService directly" — Rubber Duck (Opus 4.7 xHigh)

`RunCoordinator.cs` hard-casts to `GitHubService` for `ReconfigureRepository`. `AgentOverview.razor` injects `IGitHubService`. These must be migrated to capability interfaces before LocalDevPlatform can work.

Extract: `IProjectIdentity` for `RepositoryFullName` + repo reconfiguration. Port all `IGitHubService` consumers.

### P2: Design Local Bare Repo + Workspace Push Model
> "Agent Workspace.PushAsync pushes to a real git remote with a safety guard; design doesn't say where the local remote lives." — Rubber Duck (Opus 4.7 xHigh)

Spell out:
- Bare repo location: `.agents/local-platform/{repo-name}.git`
- Who creates it: `LocalBareRepoManager` during project initialization
- How workspace `origin` is set: workspace config points at bare repo path
- Safety guard update: `WorktreeWorkspace.PushAsync` allows local bare repo URLs
- How `IUpstreamMirror` syncs refs from bare repo to enterprise remote

## Phased Rollout

### Phase 0: Prerequisites
- [ ] Validate compliance acceptance (P0)
- [ ] Complete `IGitHubService` migration (P1)
- [ ] Design local bare repo model (P2)

### Phase 1: Core LocalDevPlatform
- [ ] Create local bare repo manager
- [ ] Implement all Local*Service providers (7 services)
- [ ] SQLite schema + migrations (run-scoped)
- [ ] DI registration: `DevPlatformKind.Local` in `RunnerDevPlatformExtensions.cs`
- [ ] Wizard integration: "Where should development PRs live?" question
- [ ] Update workspace push safety guard for local bare repo
- [ ] Conformance test suite: parameterized tests that run against GitHub, ADO, AND Local
- [ ] Acceptance criteria: full project run (PM→Research→Arch→3 engineers→TE→completion) against Local provider

### Phase 2: Dashboard Integration
- [ ] "Local" badge on PR cards + mode indicator
- [ ] Local PR URL routing (`/repository/local/pr/{id}`)
- [ ] Submission state machine UI ("Submit to Upstream" button)
- [ ] Review dossier generation (summary of local reviews for final PR body)
- [ ] Reset script updates for local platform cleanup

### Phase 3: Upstream Mirror
- [ ] `IUpstreamMirror` interface + `NullUpstreamMirror`
- [ ] `GitHubUpstreamMirror`: push agent branches to enterprise remote
- [ ] `AdoUpstreamMirror`: push agent branches to enterprise remote
- [ ] Final PR creation via submission service
- [ ] Conflict preflight: rebase check before submission

### Phase 4: Hardening & Polish
- [ ] Recovery/restart tests (kill-mid-merge, orphan detection)
- [ ] ID translation layer (local PR #5 → upstream PR description references)
- [ ] Chaos testing (concurrent label writes, crash recovery)
- [ ] Label-race CAS semantics in SQLite (WAL + optimistic concurrency)
- [ ] Standalone Dashboard.Host registration for local platform services
- [ ] Optional: Draft PR mirroring for enterprise CI/scanning integration

## Key Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| ~~Compliance team rejects single-PR model~~ | ~~Critical~~ | ✅ Resolved — single or multi-PR both acceptable |
| `IGitHubService` still used in non-agent paths | High | Phase 0 migration prerequisite |
| Agent `PushAsync` assumes real remote | High | Local bare repo design (P2) |
| SQLite/git state divergence | Medium | Git is authority; SQLite is index/metadata |
| Label race conditions in SQLite | Medium | WAL mode + optimistic concurrency + conformance tests |
| Recovery complexity (3+ sources of truth) | Medium | Conformance tests + chaos testing in Phase 4 |
| `PullRequestWorkflow.cs` GitHub-specific assumptions | Medium | Audit 2593-line file during Phase 1 |
| Local PRs missing enterprise CI signal | Low | Upstream mirror (Phase 3) + accept push-event scanning tradeoff |

## Naming & Positioning

Per GPT-5.5's advice, avoid saying "bypass PR review." Position as:

> **VDT Local Review Mode:** Agents collaborate using local virtual PRs, then VDT produces one clean, human-reviewed enterprise PR at the end.

Or per Opus 4.7-High:

> "VDT runs your AI engineering team locally. When the team is done, it opens one PR against your main branch, the same way you would. You review it in CodeFlow like any other PR from a colleague."

## Research Sources

- **5 brainstorming agents**: Claude Opus 4.7, Claude Opus 4.7 (High Reasoning), Claude Opus 4.5, GPT-5.5, GPT-5.4
- **2 rubber-duck critiques**: Claude Opus 4.7 (Extra-High Reasoning), GPT-5.5
- **Codebase exploration**: DevPlatform interfaces, PullRequestWorkflow.cs, agent PR flows, PrLifecycleCalculator, Dashboard pages, GitHub/ADO provider implementations
