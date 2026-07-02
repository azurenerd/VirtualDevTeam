# Capability Boundaries — VirtualDevTeam Dev-Platform Layer

> **Audience:** Any developer adding code that touches PRs, issues, branches, reviews, or repo files.
> **Source:** NoMessyCodePlan Theme 3 (2026-05-11).
> **TL;DR:** Use the capability interfaces, not `IGitHubService`. The capability interfaces work for both GitHub and Azure DevOps; `IGitHubService` is GitHub-only legacy that exists for back-compat.

## The boundary

```
+-----------------------------+
|  Agent / Orchestrator code  |       ← USES capability interfaces only
+--------------+--------------+
               |
               v
+-----------------------------+
|  Capability interfaces      |       ← public surface
|  IPullRequestService        |
|  IWorkItemService           |
|  IReviewService             |
|  IRepositoryContentService  |
|  IBranchService             |
+--------------+--------------+
               |
       +-------+--------+
       |                |
       v                v
+-------------+   +-----------+
| GitHub      |   | AzureDev  |        ← provider adapters wrap the concrete API
| Adapters    |   | Ops       |
|             |   | Adapters  |
+------+------+   +-----------+
       |
       v
+-----------------+
| IGitHubService  |                    ← LEGACY internal adapter — DO NOT USE FROM AGENTS
| (Octokit-backed)|
+-----------------+
```

## Rule 1 — Agents and orchestration use capability interfaces

When you need to create a PR, post a comment, list issues, or read a file from the repo, inject the capability interface from `VirtualDevTeam.Core.DevPlatform.Capabilities` via `AgentPlatformServices` (or DI directly):

```csharp
// ✅ DO
public sealed class MyAgent : EngineerAgentBase
{
    public MyAgent(AgentIdentity id, AgentCoreServices core,
                   AgentPlatformServices platform, ILogger<MyAgent> logger)
        : base(id, core, logger)
    {
        _prs   = platform.PullRequestService;
        _items = platform.WorkItemService;
    }
}

// ❌ DON'T — IGitHubService is GitHub-only and bypasses the capability abstraction
public sealed class MyAgent : EngineerAgentBase
{
    public MyAgent(AgentIdentity id, AgentCoreServices core, IGitHubService github, ...)
}
```

## Rule 2 — Adapters are the only legitimate `IGitHubService` consumers

The classes in `src/VirtualDevTeam.Core/DevPlatform/Providers/GitHub/` (the `GitHub*Adapter` files) wrap `IGitHubService` to implement the capability interfaces. That's by design — they bridge the legacy adapter and the new boundary. The `[Obsolete]` warning is suppressed at the top of those files with `#pragma warning disable CS0618`.

If you're not writing a new GitHub adapter, you don't need that `#pragma`. The warning is a signal to migrate.

## Rule 3 — Internal services that already use IGitHubService are TODO migration sites

These four callers still touch `IGitHubService` directly:

- `ConflictResolver` (uses 4 methods: comments, GetPullRequest, CreateIssue)
- `RunCoordinator` (uses Reconfigure)
- `GateNotificationService` (uses pull request listing)
- `AgentOverview.razor` (uses pull request status display)

Each emits a `CS0618` warning today. When touching any of these, prefer porting the call to a capability interface in the same PR — the warning surfaces as a TODO.

## Rule 4 — Code review checklist

When reviewing a PR that adds new code:

- [ ] Does it inject `IGitHubService` outside the `DevPlatform/Providers/` directory? If yes — flag for refactor.
- [ ] Does it add a NEW method to `IGitHubService`? If yes — strongly consider adding the equivalent to the appropriate capability interface instead. New methods on `IGitHubService` deepen the legacy debt.
- [ ] Does it suppress `CS0618` outside the GitHub adapter layer? If yes — the suppression should come with a comment explaining why the migration isn't being done in this PR (and ideally a tracking issue).

## Future direction

`IGitHubService` is large (~120 methods). The plan is incremental retirement:

1. **Now** — `[Obsolete]` attribute lights up new direct uses + this doc gives the social contract.
2. **Next** — when touching any of the four TODO migration sites, port the call (~1 PR each).
3. **Later** — when the four sites are migrated, mark the `[Obsolete(..., error: true)]` so any future direct use breaks the build.
4. **Eventually** — once `IGitHubService` is only used by the adapter layer, delete the methods that aren't strictly needed by adapters, and consider whether the interface should be made internal.

No deadline is enforced — incremental migration only. If a deadline is added, it'll go here.
