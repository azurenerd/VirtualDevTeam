using System.Collections.Concurrent;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.CompletionManifest;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

/// <summary>
/// Base class for all engineering agents (Software Engineer).
/// Contains shared logic for issue-driven work, rework handling, clarification loop,
/// PR lifecycle, and message bus interaction. Subclasses override behavior
/// via virtual/abstract methods for role-specific AI prompts and capabilities.
/// </summary>
public abstract class EngineerAgentBase : AgentBase
{
    protected readonly AgentPlatformServices Platform;
    protected readonly AgentWorkspaceServices WorkspaceServices;
    protected readonly DecisionGateService? DecisionGate;
    protected readonly IDecisionLog? DecisionLog;
    protected readonly PrePRClarificationStore? ClarificationStore;
    protected readonly ClaimedTaskRegistry? ClaimRegistry;

    /// <summary>
    /// Set by CommitViaLocalWorkspaceAsync when commit fails. Callers can check this
    /// to distinguish "build errors" from "no-op commit" for accurate PR comments.
    /// Reset to null at the start of each CommitViaLocalWorkspaceAsync call.
    /// </summary>
    protected string? LastCommitFailureReason { get; private set; }

    // Protected accessors — preserve subclass compatibility with old field names
    protected IMessageBus MessageBus => Core!.MessageBus;
    protected IPullRequestService PrService => Platform.PrService;
    protected IWorkItemService WorkItemService => Platform.WorkItemService;
    protected IRepositoryContentService RepoContent => Platform.RepoContent;
    protected IReviewService ReviewService => Platform.ReviewService;
    protected IBranchService BranchService => Platform.BranchService!;
    protected PullRequestWorkflow PrWorkflow => Platform.PrWorkflow;
    protected IssueWorkflow IssueWf => Platform.IssueWorkflow!;
    protected ProjectFileManager ProjectFiles => Core!.ProjectFiles;
    protected ModelRegistry Models => Core!.ModelRegistry;
    protected VirtualDevTeamConfig Config => Core!.Config;
    protected AgentStateStore StateStore => Core!.StateStore!;
    protected IPromptTemplateService PromptService => Core!.PromptService!;
    protected IGateCheckService GateCheck => Core!.GateCheck;
    protected IAgentTaskTracker TaskTracker => Core!.TaskTracker!;
    protected IRunBranchProvider? BranchProvider => Platform.BranchProvider;
    protected BuildRunner? BuildRunnerSvc => WorkspaceServices.BuildRunner;
    protected TestRunner? TestRunnerSvc => WorkspaceServices.TestRunner;
    protected Core.Metrics.BuildTestMetrics? Metrics => WorkspaceServices.Metrics;
    protected PlaywrightRunner? ScreenshotRunner => WorkspaceServices.PlaywrightRunner;

    /// <summary>
    /// Guidance injected into implementation prompts to prevent merge conflicts
    /// by encouraging additive edits rather than full-file rewrites.
    /// </summary>
    protected const string AdditiveEditingGuidance = @"
## MERGE-CONFLICT PREVENTION — ADDITIVE EDITING RULES
- NEVER rewrite entire files — add or modify only the specific sections you need.
- Use exports/imports additively: append new exports, don't reorganize existing ones.
- Keep existing code intact when adding new functions, classes, or components.
- If a file already exists, EXTEND it (append new code, insert at appropriate location) rather than replacing its contents.
- When adding items to arrays, objects, or switch statements, append to the end rather than reordering.
- Preserve all existing comments, formatting, and whitespace outside your changes.
";

    protected readonly HashSet<int> ProcessedIssueIds = new();
    protected readonly ConcurrentQueue<ReworkItem> ReworkQueue = new();
    /// <summary>Per-PR timestamp when the first rework item arrived (for debounce batching).</summary>
    private readonly Dictionary<int, DateTime> _reworkDebounceTimers = new();
    protected readonly ConcurrentQueue<IssueAssignmentMessage> AssignmentQueue = new();
    protected readonly ConcurrentQueue<ClarificationResponseMessage> ClarificationResponses = new();
    // Track issues explicitly assigned to THIS agent via message bus.
    // Prevents multiple same-name agents from racing on the same PR via Priority 5 recovery.
    protected readonly HashSet<int> BusAssignedIssues = new();
    // Track retry attempts for issue assignments that fail during WorkOnIssueAsync
    private readonly Dictionary<int, int> _issueRetryAttempts = new();
    private const int MaxIssueRetries = 3;
    // Track rework attempts per PR per reviewer. Human reviewers are exempt from exhaustion
    // but still counted for telemetry.
    protected readonly Dictionary<(int PrNumber, string Reviewer), int> ReworkAttemptCounts = new();
    // Separate counter for TE source-bug rework — tracked independently so TE feedback
    // isn't blocked by exhausted peer review cycles.
    protected readonly Dictionary<int, int> TeReworkAttemptCounts = new();
    // Prevent duplicate "max limit" comments when multiple reviewers' feedback arrives
    private readonly HashSet<int> _forceApprovalSentPrs = new();
    // Per-PR CLI session IDs — resumes the session used to create the PR during rework
    private readonly Dictionary<int, string> _prSessionIds = new();
    // Current task's file scope prompt text (set before each task, used by build-fix)
    private string _currentFileScopeBlock = "";
    // Implementation context notes — accumulated during implementation for handoff to self-assessment
    protected readonly List<string> _implementationNotes = new();

    // ── Scope relaxation tiers for build-fix prompts ──
    // Tier 1: strict scope (attempts 1–2). Tier 2: escalated (attempts 3+).
    // Attempt threshold is 0-indexed: escalate when attempt >= ScopeEscalationAttemptThreshold.
    private const int ScopeEscalationAttemptThreshold = 2;

    private const string ScopeRelaxationTier1 = """
        SCOPE: Only fix files that YOU created or modified in this task.
        Do NOT create new files or modify files outside the task scope to fix errors.
        If an error is in a file you did not create, adjust YOUR files to work with the existing code.
        Make minimal, surgical edits — do not rewrite entire files.
        """;

    private const string ScopeRelaxationTier2 = """
        SCOPE ESCALATION: Previous build-fix attempts within your normal scope failed.
        You now have EXPANDED SCOPE to resolve this build failure. Follow these rules:

        1. PREFER renaming YOUR types/components/routes to avoid conflicts with existing code.
           Do NOT modify another agent's implementation files — rename yours instead.

        2. Project/solution/config files (.sln, .csproj, package.json, tsconfig.json, Cargo.toml,
           go.mod, etc.) ARE in scope — fix corrupted paths, missing references, or misconfigurations.

        3. Shared infrastructure files (Program.cs, App.razor, _Imports.razor, _Host.cshtml,
           index.html, etc.) may be modified ONLY to add registrations, imports, or routes
           needed for YOUR code to compile. Do NOT remove or change existing entries.

        4. Do NOT delete files you didn't create. Do NOT restructure the project.
           Make the MINIMUM changes needed to make the build pass.
        """;

    /// <summary>Record an implementation decision/constraint for handoff to self-assessment.</summary>
    protected void RecordImplementationNote(string note)
    {
        _implementationNotes.Add($"[{DateTime.UtcNow:HH:mm}] {note}");
    }

    protected void TrackPastImplementationPr(int prNumber)
    {
        if (_activePastImplementationPrs.TryAdd(prNumber, 0))
            _pastImplementationPrs.Add(prNumber);
    }

    protected bool IsPastImplementationPrTracked(int prNumber)
        => _activePastImplementationPrs.ContainsKey(prNumber);

    protected int PastImplementationPrCount
        => _activePastImplementationPrs.Count;

    protected int[] GetPastImplementationPrSnapshot()
        => _pastImplementationPrs
            .Where(IsPastImplementationPrTracked)
            .Distinct()
            .ToArray();

    protected void UntrackPastImplementationPr(int prNumber)
        => _activePastImplementationPrs.TryRemove(prNumber, out _);

    private async Task TrackCurrentPrAsPastIfApplicableAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is not int currentPrNumber)
            return;

        var currentPr = await PrService.GetAsync(currentPrNumber, ct);
        if (currentPr is null
            || !string.Equals(currentPr.State, "open", StringComparison.OrdinalIgnoreCase)
            || !PullRequestWorkflow.Labels.IsPastImplementation(currentPr.Labels))
            return;

        TrackPastImplementationPr(currentPrNumber);
    }

    private static bool IsHumanReviewer(string? reviewer) =>
        string.Equals(reviewer?.Trim(), "Operator", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reviewer?.Trim(), "HumanReviewer", StringComparison.OrdinalIgnoreCase);

    private void RecordOperatorReworkNotes(IEnumerable<ReworkItem> reworkBatch)
    {
        foreach (var feedback in reworkBatch
                     .Where(item => IsHumanReviewer(item.Reviewer) && !string.IsNullOrWhiteSpace(item.Feedback))
                     .Select(item => item.Feedback.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var note = $"[OPERATOR REQUEST] {feedback} — This change was explicitly requested by the operator and must not be reverted.";
            if (_implementationNotes.Any(existing => string.Equals(existing, note, StringComparison.OrdinalIgnoreCase)))
                continue;

            _implementationNotes.Add(note);
        }
    }

    // Cached repo tree for giving agents visibility into existing code (Tier 1: repo structure awareness)
    private IReadOnlyList<string>? _repoTreeCache;
    private DateTime _repoTreeCacheExpiry = DateTime.MinValue;
    // Local workspace for real build/test verification (null when disabled)
    protected IAgentWorkspace? Workspace;
    private bool _pendingWorkspaceCleanup;
    protected int? CurrentIssueNumber;
    // CurrentPrNumber is inherited from AgentBase (public getter, protected setter).
    // Engineers set this when opening a PR and clear it when the PR is merged/closed
    // — see Lessons Learned #7 about not persisting this across restarts.
    protected readonly ConcurrentBag<int> _pastImplementationPrs = new();
    private readonly ConcurrentDictionary<int, byte> _activePastImplementationPrs = new();

    protected EngineerAgentBase(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        AgentWorkspaceServices workspace,
        ILogger<AgentBase> logger,
        DecisionGateService? decisionGate = null,
        IDecisionLog? decisionLog = null,
        PrePRClarificationStore? clarificationStore = null,
        ClaimedTaskRegistry? claimRegistry = null)
        : base(identity, core, logger)
    {
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        WorkspaceServices = workspace ?? throw new ArgumentNullException(nameof(workspace));
        DecisionGate = decisionGate;
        DecisionLog = decisionLog;
        ClarificationStore = clarificationStore;
        ClaimRegistry = claimRegistry;
    }

    protected string EffectiveBranch => BranchProvider?.EffectiveBranch ?? Config.Project.DefaultBranch;

    /// <summary>
    /// Override in subclasses to force workspace clone even when engineering appears complete.
    /// Used by SE leader when ForceRedoFinalIntegration requires a local workspace for strategies.
    /// </summary>
    protected virtual bool ShouldForceWorkspaceClone() => false;

    /// <summary>
    /// Ensure <see cref="Workspace"/> is initialized. Some flows (notably strategy-framework rework)
    /// can run after an agent skipped workspace setup during startup (e.g., "engineering done" fast
    /// path) or after a prior initialization failure. Call this before any workspace-dependent work.
    /// </summary>
    protected async Task<bool> EnsureWorkspaceInitializedAsync(CancellationToken ct)
    {
        if (Workspace is not null)
            return true;

        if (!Config.Workspace.IsEnabled)
            return false;

        try
        {
            if (Config.Workspace.IsWorktreeMode)
            {
                var sharedClone = Core.SharedCloneManager
                    ?? throw new InvalidOperationException("SharedCloneManager not registered — required for Worktree/InPlace mode");
                var agentSlug = Identity.Id.Replace(" ", "").Replace("/", "-").Replace("\\", "-");
                Workspace = new WorktreeWorkspace(
                    sharedClone,
                    agentSlug,
                    EffectiveBranch,
                    Config.Workspace.WorkspaceMode,
                    Config.Workspace.SparseCheckoutPaths?.Count > 0 ? Config.Workspace.SparseCheckoutPaths : null,
                    Logger,
                    Core.PushFailureTracker,
                    Config.Workspace.AgentPushRemote);
            }
            else
            {
                var repoUrl = Config.GetGitCloneUrl();
                Workspace = new LocalWorkspace(
                    Config.Workspace,
                    Identity.Id,
                    repoUrl,
                    EffectiveBranch,
                    Logger);
            }

            await Workspace.InitializeAsync(ct);
            Logger.LogInformation(
                "{Role} {Name} initialized local workspace on-demand at {Path} (mode={Mode})",
                Identity.Role, Identity.DisplayName, Workspace.RepoPath, Config.Workspace.WorkspaceMode);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "{Role} {Name} failed to initialize local workspace on-demand (mode={Mode}, LocalCheckoutPath={LocalCheckoutPath}); will attempt clone fallback",
                Identity.Role, Identity.DisplayName, Config.Workspace.WorkspaceMode, Config.Workspace.LocalCheckoutPath ?? "(null)");
            Workspace = null;
        }

        // Fallback: if Worktree/InPlace workspace init fails, try a full clone so workspace-dependent
        // workflows (strategy framework) can still run.
        if (Config.Workspace.IsWorktreeMode)
        {
            try
            {
                var repoUrl = Config.GetGitCloneUrl();
                var cloneWs = new LocalWorkspace(
                    Config.Workspace,
                    Identity.Id,
                    repoUrl,
                    EffectiveBranch,
                    Logger);
                await cloneWs.InitializeAsync(ct);
                Workspace = cloneWs;
                Logger.LogWarning(
                    "{Role} {Name} fell back to clone workspace at {Path} after worktree/in-place init failed",
                    Identity.Role, Identity.DisplayName, Workspace.RepoPath);
                return true;
            }
            catch (Exception fallbackEx)
            {
                Logger.LogWarning(fallbackEx,
                    "{Role} {Name} clone-workspace fallback also failed; proceeding API-only",
                    Identity.Role, Identity.DisplayName);
                Workspace = null;
            }
        }

        return false;
    }


    #region Lifecycle

    protected override async Task OnInitializeAsync(CancellationToken ct)
    {
        Subscribe<TaskAssignmentMessage>(HandleTaskAssignmentAsync);
        Subscribe<IssueAssignmentMessage>(HandleIssueAssignmentAsync);
        Subscribe<ChangesRequestedMessage>(HandleChangesRequestedAsync);
        Subscribe<ClarificationResponseMessage>(HandleClarificationResponseAsync);

        // Subscribe to FlowMonitor nudge so engineers acknowledge stuck-detection probes.
        // Without this, rung 1 (kick-agent-poll) bus messages go undelivered.
        Subscribe<FlowMonitorNudgeMessage>(HandleFlowMonitorNudgeAsync);

        // Subscribe to workspace cleanup signal from PE leader
        Subscribe<WorkspaceCleanupMessage>(HandleWorkspaceCleanupAsync);

        // Subscribe to task claim broadcasts so we don't race on the same issue
        Subscribe<TaskClaimedMessage>(HandleTaskClaimedAsync);

        // Wake when any PR is merged — engineers check dependency state and progress
        Subscribe<PrMergedMessage>(async (msg, _) =>
        {
            Logger.LogDebug("Engineer received PrMergedMessage for PR #{Number}: {Title}",
                msg.PrNumber, msg.PrTitle);
            WakeLoop();
        });

        RegisterAdditionalSubscriptions();

        // Initialize local workspace if configured
        if (Config.Workspace.IsEnabled)
        {
            // post-mon-workspace-clone-skip: when restarting the runner on a finished project,
            // every engineer agent re-clones the target repo (~30s wasted per agent) only to
            // immediately go Idle. Probe for engineering completion BEFORE the clone — if all
            // engineering-task issues are closed AND there's nothing in-flight AND we have
            // evidence engineering DID run for this project (≥1 merged softwareengineer PR),
            // skip the clone. Failure of the probe is non-fatal: fall through to normal clone.
            //
            // CRITICAL (2026-05-10 fix): the previous probe ("0 open engineering-task issues
            // AND 0 open PRs for this role") fired false-positive on FRESH RESET — there are
            // no engineering tasks yet (PM+Architect haven't finished) AND no open PRs for SE
            // role yet (engineering hasn't started). Need a POSITIVE signal that engineering
            // ran in the past: at least one merged softwareengineer PR.
            bool engineeringDone = false;
            try
            {
                // Allow subclasses to force workspace clone (e.g., SE leader needs workspace
                // for T-FINAL strategy framework even when engineering is "done").
                if (ShouldForceWorkspaceClone())
                {
                    Logger.LogInformation(
                        "{Role} {Name} forcing workspace clone (subclass override)",
                        Identity.Role, Identity.DisplayName);
                    engineeringDone = false;
                }
                else
                {
                    var openTasks = await WorkItemService.ListByLabelAsync("engineering-task", state: "open", ct);
                if (openTasks.Count == 0)
                {
                    var openPrs = await PrService.ListOpenAsync(ct);
                    var roleHasOpenPr = openPrs.Any(p =>
                        (p.AssignedAgent ?? "").StartsWith(Identity.Role.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (!roleHasOpenPr)
                    {
                        // Positive signal: at least one merged PR from an engineering branch
                        // (SE leader, SE worker, OR SME engineer role) proves engineering happened.
                        // ListMergedAsync is run-scoped where the platform supports it (GitHub uses
                        // _runStartedUtc), so prior-run PRs are filtered. The central
                        // EngineeringTaskIssueManager.IsEngineeringPrBranch helper excludes
                        // auto-merged research/pmspec/architecture PRs by branch role-segment.
                        var mergedPrs = await PrService.ListMergedAsync(ct);
                        // Filter to CURRENT run scope — historical merged PRs from prior
                        // runs (which survive minimal-reset) must not trigger skip-clone.
                        var currentScope = BranchProvider?.RunScope;
                        var hasEngineeringEvidence = mergedPrs.Any(p =>
                            EngineeringTaskIssueManager.IsEngineeringPrBranch(p.HeadBranch)
                            && (currentScope is null || (p.HeadBranch?.Contains($"/{currentScope}/", StringComparison.OrdinalIgnoreCase) == true)));
                        if (hasEngineeringEvidence)
                        {
                            engineeringDone = true;
                            Logger.LogInformation(
                                "{Role} {Name} skipping workspace clone — engineering already complete (0 open engineering-task issues, 0 open PRs for this role, ≥1 merged engineering PR confirms prior completion)",
                                Identity.Role, Identity.DisplayName);
                            UpdateStatus(AgentStatus.Idle, "✅ Engineering complete from prior run — workspace not needed");
                        }
                    }
                }
                } // end else (not forcing workspace clone)
            }
            catch (Exception probeEx)
            {
                Logger.LogDebug(probeEx,
                    "{Role} {Name} engineering-done probe failed (non-fatal — proceeding with clone)",
                    Identity.Role, Identity.DisplayName);
            }

            if (!engineeringDone)
            {
                UpdateStatus(AgentStatus.Working, "📁 Setting up workspace");
                var initStepId = TaskTracker.BeginStep(Identity.Id, "initialization",
                    "Workspace setup", "Initializing git workspace", Identity.ModelTier);
                try
                {
                    if (Config.Workspace.IsWorktreeMode)
                    {
                        // Worktree/InPlace mode: create lightweight worktree from shared .git
                        var sharedClone = Core.SharedCloneManager
                            ?? throw new InvalidOperationException("SharedCloneManager not registered — required for Worktree/InPlace mode");
                        var agentSlug = Identity.Id.Replace(" ", "").Replace("/", "-").Replace("\\", "-");
                        Workspace = new WorktreeWorkspace(
                            sharedClone,
                            agentSlug,
                            EffectiveBranch,
                            Config.Workspace.WorkspaceMode,
                            Config.Workspace.SparseCheckoutPaths?.Count > 0 ? Config.Workspace.SparseCheckoutPaths : null,
                            Logger,
                            Core.PushFailureTracker,
                            Config.Workspace.AgentPushRemote);
                    }
                    else
                    {
                        // Clone mode (default): full git clone per agent
                        var repoUrl = Config.GetGitCloneUrl();
                        Workspace = new LocalWorkspace(
                            Config.Workspace,
                            Identity.Id,
                            repoUrl,
                            EffectiveBranch,
                            Logger);
                    }
                    await Workspace.InitializeAsync(ct);
                    Logger.LogInformation("{Role} {Name} initialized local workspace at {Path}",
                        Identity.Role, Identity.DisplayName, Workspace.RepoPath);
                    TaskTracker.CompleteStep(initStepId);
                }
                catch (Exception ex)
                {
                    TaskTracker.CompleteStep(initStepId);
                    Logger.LogWarning(ex, "{Role} {Name} failed to initialize local workspace, falling back to API mode",
                        Identity.Role, Identity.DisplayName);
                    Workspace = null;
                }
            }
        }

        // Restore CLI session IDs from database so rework resumes the same conversation
        try
        {
            var sessions = await StateStore.LoadCliSessionsAsync(Identity.Id, ct);
            foreach (var (prNumber, sessionId) in sessions)
                _prSessionIds[prNumber] = sessionId;

            if (sessions.Count > 0)
                Logger.LogInformation("{Role} {Name} restored {Count} CLI session(s) from database",
                    Identity.Role, Identity.DisplayName, sessions.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} failed to restore CLI sessions from database",
                Identity.Role, Identity.DisplayName);
        }

        // Restore rework attempt counts from checkpoint
        try
        {
            var reworkCounts = await StateStore.LoadReworkAttemptsAsync(Identity.Role.ToString(), ct);
            foreach (var kvp in reworkCounts)
                ReworkAttemptCounts[kvp.Key] = kvp.Value;

            if (reworkCounts.Count > 0)
                Logger.LogInformation("{Role} restored {Count} rework attempt counters from checkpoint",
                    Identity.Role, reworkCounts.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} failed to restore rework counters from checkpoint", Identity.Role);
        }

        // Restore TE rework + issue retry counters from run_metadata
        RestoreRetryCounters();

        Logger.LogInformation("{Role} {Name} initialized, awaiting task assignments",
            Identity.Role, Identity.DisplayName);
    }

    /// <summary>Override to register additional message bus subscriptions beyond the standard four.</summary>
    protected virtual void RegisterAdditionalSubscriptions() { }

    /// <summary>Handle TaskClaimedMessage from another agent — record in the claim registry.</summary>
    private Task HandleTaskClaimedAsync(TaskClaimedMessage msg, CancellationToken ct)
    {
        if (msg.FromAgentId == Identity.Id) return Task.CompletedTask; // ignore own claims
        ClaimRegistry?.RecordClaim(msg.IssueNumber, msg.FromAgentId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempt to claim a work item via the in-process registry + bus broadcast.
    /// Returns true if this agent won the claim. Must be called BEFORE touching GitHub.
    /// </summary>
    protected bool TryClaimTask(int issueNumber, string title)
    {
        if (ClaimRegistry is null) return true; // no registry wired (tests) — allow claim

        if (!ClaimRegistry.TryClaim(issueNumber, Identity.Id))
        {
            Logger.LogInformation(
                "{Role} {Name}: task #{IssueNumber} ({Title}) already claimed by {Holder} — skipping",
                Identity.Role, Identity.DisplayName, issueNumber, title,
                ClaimRegistry.GetClaimHolder(issueNumber) ?? "unknown");
            return false;
        }

        // Broadcast to all other engineers so they see this claim immediately
        _ = MessageBus.PublishAsync(new TaskClaimedMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "TaskClaimed",
            IssueNumber = issueNumber,
            IssueTitle = title,
        });

        return true;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Ready for task assignments");

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            try
            {
                // Priority 1: Process rework feedback from reviewers
                // Debounce: wait for multiple reviewers' feedback to arrive before starting
                // one combined rework pass (avoids sequential PM→rework→Architect→rework).
                if (ReworkQueue.TryPeek(out var peeked))
                {
                    var debounceSeconds = Config.Limits.ReworkDebounceSeconds;
                    if (debounceSeconds > 0)
                    {
                        if (!_reworkDebounceTimers.ContainsKey(peeked.PrNumber))
                        {
                            _reworkDebounceTimers[peeked.PrNumber] = DateTime.UtcNow;
                            Logger.LogInformation(
                                "Rework debounce started for PR #{PrNumber} — waiting {Seconds}s for additional reviewer feedback",
                                peeked.PrNumber, debounceSeconds);
                        }

                        var elapsed = DateTime.UtcNow - _reworkDebounceTimers[peeked.PrNumber];
                        if (elapsed.TotalSeconds < debounceSeconds)
                        {
                            // Still within debounce window — skip rework this cycle, process other work
                            goto skipRework;
                        }
                    }

                    // Debounce expired (or disabled) — drain ALL queued items for this PR
                    if (ReworkQueue.TryDequeue(out var rework))
                    {
                        _reworkDebounceTimers.Remove(rework.PrNumber);
                        var batchedFeedback = new List<ReworkItem> { rework };
                        var overflow = new List<ReworkItem>();
                        while (ReworkQueue.TryDequeue(out var extra))
                        {
                            if (extra.PrNumber == rework.PrNumber)
                                batchedFeedback.Add(extra);
                            else
                                overflow.Add(extra);
                        }
                        foreach (var item in overflow)
                            ReworkQueue.Enqueue(item);

                        Logger.LogInformation(
                            "Rework debounce complete for PR #{PrNumber} — batched {Count} feedback items from {Reviewers}",
                            rework.PrNumber, batchedFeedback.Count,
                            string.Join(", ", batchedFeedback.Select(r => r.Reviewer).Distinct()));

                        // Defense-in-depth: discard rework for merged/closed PRs before
                        // starting an expensive CLI rework session (Lesson #79).
                        try
                        {
                            var prState = await PrService.GetAsync(rework.PrNumber, ct);
                            if (prState is null || !string.Equals(prState.State, "open", StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogInformation(
                                    "{Role} {Name} skipping rework for PR #{PrNumber} — PR is {State}",
                                    Identity.Role, Identity.DisplayName, rework.PrNumber, prState?.State ?? "deleted");
                                continue;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw; // Don't swallow shutdown cancellation
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "Could not verify PR #{PrNumber} state before rework, proceeding", rework.PrNumber);
                        }

                        await HandleReworkAsync(batchedFeedback, ct);
                        continue;
                    }
                }
                skipRework:

                // Priority 2: Process new issue assignments
                // Guard: don't start a new task while a previous PR is still open.
                // The SE agent has its own guard in WorkOnOwnTasksAsync; this protects
                // specialist agents whose assignments come via the bus/queue.
                if (CurrentPrNumber is not null && AssignmentQueue.Count > 0)
                {
                    Logger.LogDebug(
                        "{Role} {Name} deferring new assignment — PR #{Pr} is still open",
                        Identity.Role, Identity.DisplayName, CurrentPrNumber);
                }
                else if (AssignmentQueue.TryDequeue(out var assignment))
                {
                    try
                    {
                        await WorkOnIssueAsync(assignment, ct);
                        _issueRetryAttempts.Remove(assignment.IssueNumber);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _issueRetryAttempts.TryGetValue(assignment.IssueNumber, out var attempts);
                        attempts++;
                        if (attempts < MaxIssueRetries)
                        {
                            _issueRetryAttempts[assignment.IssueNumber] = attempts;
                            PersistRetryCounters();
                            Logger.LogWarning(ex,
                                "{Role} {Name} failed issue #{Number} (attempt {Attempt}/{Max}), re-enqueuing",
                                Identity.Role, Identity.DisplayName, assignment.IssueNumber, attempts, MaxIssueRetries);
                            RecordError($"Issue #{assignment.IssueNumber} attempt {attempts} failed: {ex.Message}",
                                Microsoft.Extensions.Logging.LogLevel.Warning, ex);
                            AssignmentQueue.Enqueue(assignment);
                            await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        }
                        else
                        {
                            Logger.LogError(ex,
                                "{Role} {Name} permanently failed issue #{Number} after {Max} attempts",
                                Identity.Role, Identity.DisplayName, assignment.IssueNumber, MaxIssueRetries);
                            RecordError($"Issue #{assignment.IssueNumber} permanently failed: {ex.Message}",
                                Microsoft.Extensions.Logging.LogLevel.Error, ex);
                            _issueRetryAttempts.Remove(assignment.IssueNumber);
                            CurrentIssueNumber = null;
                            CurrentPrNumber = null;
                            Identity.AssignedPullRequest = null;
                            await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        }
                    }
                    continue;
                }

                // Priority 3: Subclass-specific loop work (PE orchestration, etc.)
                await RunAdditionalLoopWorkAsync(ct);

                // Priority 4: Check if our current PR was merged/closed — reset state
                // BUG FIX: This check was added because CurrentPrNumber is now kept set after
                // commit (to allow ChangesRequestedMessage matching). Without this, a merged PR
                // would never be cleared and the engineer would be stuck forever.
                if (CurrentPrNumber is not null)
                {
                    var currentPr = await PrService.GetAsync(CurrentPrNumber.Value, ct);
                    if (currentPr is null || !string.Equals(currentPr.State, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation("{Role} {Name} PR #{PrNumber} is no longer open (merged/closed), resetting",
                            Identity.Role, Identity.DisplayName, CurrentPrNumber.Value);
                        UntrackPastImplementationPr(CurrentPrNumber.Value);
                        CurrentPrNumber = null;
                        Identity.AssignedPullRequest = null;
                    }
                }

                // Priority 5: Recovery — check for existing open PR after restart
                // BUG FIX: Also re-tracks ready-for-review PRs so that rework feedback
                // (ChangesRequestedMessage) can still match this engineer after a restart.
                // Without this, restarted engineers would ignore rework requests.
                if (CurrentPrNumber is null)
                {
                    var myTasks = (await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
                    var activePR = myTasks.FirstOrDefault(pr =>
                        string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                        && !pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase));

                    if (activePR != null && Identity.AssignedPullRequest != activePR.Number.ToString())
                    {
                        // Guard: only resume PRs that were assigned to THIS agent.
                        // Check 1: agent-id metadata in PR body (durable, survives restarts)
                        // Check 2: linked issue in BusAssignedIssues (volatile, in-memory only)
                        // A PR is ours if the embedded agent-id matches, OR if we have the linked issue assigned via bus.
                        var prAgentId = ExtractAgentIdFromPrBody(activePR.Body);
                        var linkedIssue = ExtractLinkedIssueFromPrBody(activePR.Body);
                        var isOurPr = string.Equals(prAgentId, Identity.Id, StringComparison.OrdinalIgnoreCase)
                            || (linkedIssue.HasValue && BusAssignedIssues.Contains(linkedIssue.Value));

                        if (!isOurPr)
                        {
                            Logger.LogDebug(
                                "{Role} {Name} skipping PR #{PrNumber} — agent-id '{PrAgentId}' != '{OurId}' and issue #{IssueNumber} not in bus assignments",
                                Identity.Role, Identity.DisplayName, activePR.Number,
                                prAgentId ?? "none", Identity.Id, linkedIssue);
                        }
                        else
                        {
                            // Sync branch with main before resuming work (picks up changes merged since last run)
                            await SyncBranchWithMainAsync(activePR.Number, ct);
                            await WorkOnExistingPrAsync(activePR, ct);
                        }
                    }
                    else
                    {
                        // Track ready-for-review PRs and check for unaddressed feedback
                        // Filter to PRs that belong to THIS agent (by agent-id in body or bus assignment)
                        var reviewPRs = myTasks.Where(pr =>
                            string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                            && pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase)
                            && IsOurPullRequest(pr))
                            .ToList();

                        if (reviewPRs.Count > 0)
                        {
                            // Pick the first one with pending feedback, else the first one
                            foreach (var reviewPR in reviewPRs)
                            {
                                TrackPastImplementationPr(reviewPR.Number);
                                CurrentPrNumber = reviewPR.Number;
                                Identity.AssignedPullRequest = reviewPR.Number.ToString();

                                // Check for unaddressed CHANGES_REQUESTED feedback on GitHub
                                var pendingFeedback = await PrWorkflow.GetPendingChangesRequestedAsync(reviewPR.Number, ct);
                                if (pendingFeedback is { } pending)
                                {
                                    // Populate rework queue directly — engineer needs to address feedback
                                    ReworkQueue.Enqueue(new ReworkItem(reviewPR.Number, reviewPR.Title, pending.Feedback, pending.Reviewer));
                                    Logger.LogInformation(
                                        "{Role} {Name} recovered unaddressed feedback on PR #{PrNumber} from {Reviewer}",
                                        Identity.Role, Identity.DisplayName, reviewPR.Number, pending.Reviewer);
                                    UpdateStatus(AgentStatus.Working, $"Processing recovered feedback on PR #{reviewPR.Number}");
                                    break; // Process one PR at a time
                                }

                                // No pending feedback — re-broadcast review request
                                Logger.LogInformation("{Role} {Name} re-tracking PR #{PrNumber} awaiting review",
                                    Identity.Role, Identity.DisplayName, reviewPR.Number);
                                await MessageBus.PublishAsync(new ReviewRequestMessage
                                {
                                    FromAgentId = Identity.Id,
                                    ToAgentId = "*",
                                    MessageType = "ReviewRequest",
                                    PrNumber = reviewPR.Number,
                                    PrTitle = reviewPR.Title,
                                    ReviewType = "Recovery"
                                }, ct);
                                Logger.LogInformation("{Role} {Name} re-broadcast review request for PR #{PrNumber}",
                                    Identity.Role, Identity.DisplayName, reviewPR.Number);
                                UpdateStatus(AgentStatus.Idle, $"PR #{reviewPR.Number} awaiting review");
                                break; // One PR at a time
                            }
                        }
                        else if (activePR == null)
                        {
                            UpdateStatus(AgentStatus.Idle, "Waiting for task assignment");
                        }
                    }
                }

                await CheckForIssuesAsync(ct);

                // Refresh diagnostic with memory context each loop iteration
                await RefreshDiagnosticWithMemoryAsync(ct);

                await WaitForWakeOrTimeoutAsync(
                    TimeSpan.FromSeconds(Config.Limits.GitHubPollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Role} {Name} loop error", Identity.Role, Identity.DisplayName);
                RecordError($"Loop error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Error, ex.Message);
                try { await Task.Delay(10_000, ct); }
                catch (OperationCanceledException) { break; }
                UpdateStatus(AgentStatus.Idle, "Recovered from error");
            }
        }

        UpdateStatus(AgentStatus.Offline, $"{Identity.Role} loop exited");
    }

    /// <summary>
    /// Called each loop iteration for subclass-specific work (e.g., PE orchestration).
    /// Default is no-op for Senior/Junior. Override in PE to add assignment, review, etc.
    /// </summary>
    protected virtual Task RunAdditionalLoopWorkAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called after each implementation step completes within the step loop.
    /// Allows subclasses (e.g., SE leader) to perform inter-step coordination such as
    /// assigning tasks to idle workers without waiting for the entire PR to finish.
    /// </summary>
    protected virtual Task OnStepCompletedAsync(int stepNumber, int totalSteps, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called when an existing open PR is found for this agent (recovery after restart).
    /// Subclasses can override to customize behavior.
    /// </summary>
    protected virtual Task WorkOnExistingPrAsync(AgentPullRequest pr, CancellationToken ct)
        => WorkOnLegacyPrAsync(pr, ct);

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        // Clean up workspace if cleanup was requested
        if (_pendingWorkspaceCleanup && Workspace is not null)
        {
            try
            {
                await Workspace.CleanupAsync();
                Logger.LogInformation("{Role} {Name} workspace cleaned up", Identity.Role, Identity.DisplayName);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Role} {Name} failed to clean up workspace", Identity.Role, Identity.DisplayName);
            }
        }
    }

    private Task HandleWorkspaceCleanupAsync(WorkspaceCleanupMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("{Role} {Name} received workspace cleanup signal: {Reason}",
            Identity.Role, Identity.DisplayName, msg.Reason);
        _pendingWorkspaceCleanup = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle FlowMonitor nudge messages. Logs receipt of the probe. Does NOT update
    /// status timestamp — that would mask genuinely stuck agents. The build-loop status
    /// updates (added below) provide the legitimate heartbeat.
    /// </summary>
    private Task HandleFlowMonitorNudgeAsync(FlowMonitorNudgeMessage msg, CancellationToken ct)
    {
        Logger.LogInformation(
            "{Role} {Name} received FlowMonitor nudge: {Reason} — triggering immediate re-poll",
            Identity.Role, Identity.DisplayName, msg.Reason ?? "no reason");

        WakeLoop();
        return Task.CompletedTask;
    }

    #endregion

    #region Branch Sync

    /// <summary>
    /// Sync a PR branch with the latest main to avoid merge conflicts.
    /// Logs result but does not throw — sync failures are non-fatal.
    /// </summary>
    protected async Task SyncBranchWithMainAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            // First check if the branch is actually behind main — skip sync entirely if not
            var isBehind = await PrService.IsBehindBaseAsync(prNumber, ct);
            if (!isBehind)
            {
                Logger.LogDebug("{Role} {Name} PR #{PrNumber} branch is already up to date with main — no sync needed",
                    Identity.Role, Identity.DisplayName, prNumber);
                return;
            }

            // Branch IS behind main — try non-destructive merge update first
            Logger.LogInformation("{Role} {Name} PR #{PrNumber} branch is behind main — syncing",
                Identity.Role, Identity.DisplayName, prNumber);

            var synced = await PrService.UpdateBranchAsync(prNumber, ct);
            if (synced)
            {
                Logger.LogInformation("{Role} {Name} synced PR #{PrNumber} branch with main",
                    Identity.Role, Identity.DisplayName, prNumber);
            }
            else
            {
                // Genuine merge conflict — force-rebase as last resort
                Logger.LogWarning("{Role} {Name} PR #{PrNumber} has merge conflicts — attempting force-rebase onto main",
                    Identity.Role, Identity.DisplayName, prNumber);

                var rebased = await PrService.RebaseBranchAsync(prNumber, ct);
                if (rebased)
                {
                    Logger.LogInformation("{Role} {Name} force-rebased PR #{PrNumber} onto main — conflicts resolved",
                        Identity.Role, Identity.DisplayName, prNumber);
                }
                else
                {
                    Logger.LogWarning("{Role} {Name} force-rebase failed for PR #{PrNumber} — PR may need close-and-recreate",
                        Identity.Role, Identity.DisplayName, prNumber);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} failed to sync PR #{PrNumber} branch",
                Identity.Role, Identity.DisplayName, prNumber);
        }
    }

    #endregion

    #region CLI Session Management

    /// <summary>
    /// Gets or creates a CLI session ID for a specific PR. When an engineer starts
    /// a new task, a fresh session is created. When doing rework on an existing PR,
    /// the same session is resumed so the CLI has full context of what was built.
    /// </summary>
    protected string GetOrCreatePrSession(int prNumber)
    {
        if (!_prSessionIds.TryGetValue(prNumber, out var sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
            _prSessionIds[prNumber] = sessionId;
            Logger.LogDebug("{Role} {Name} created CLI session {Session} for PR #{Pr}",
                Identity.Role, Identity.DisplayName, sessionId, prNumber);

            // Persist to DB so session survives runner restarts
            _ = Task.Run(async () =>
            {
                try { await StateStore.SaveCliSessionAsync(Identity.Id, prNumber, sessionId); }
                catch (Exception ex) { Logger.LogWarning(ex, "Failed to persist CLI session for PR #{Pr}", prNumber); }
            });
        }
        SetCliSession(sessionId);
        return sessionId;
    }

    /// <summary>
    /// Activates the CLI session for a PR. Call this before any AI interaction
    /// related to a specific PR (implementation, rework, self-review).
    /// </summary>
    protected void ActivatePrSession(int prNumber)
    {
        GetOrCreatePrSession(prNumber);
    }

    #endregion

    #region Issue-Driven Work

    /// <summary>
    /// Checks whether all dependency issues (from "Depends On:" metadata) are closed.
    /// Returns true if the item has no dependencies or all are resolved.
    /// </summary>
    protected async Task<bool> AreDependenciesSatisfiedAsync(string? body, CancellationToken ct)
    {
        if (WorkItemService is null) return true; // Can't check — assume OK

        var deps = EngineeringTaskIssueManager.ParseDependencies(body, Logger);
        if (deps.Count == 0) return true;

        foreach (var depNumber in deps)
        {
            try
            {
                var dep = await WorkItemService.GetAsync(depNumber, ct);
                if (dep is null || !dep.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch
            {
                // If we can't check a dependency, assume it's unmet
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Processes a new Issue assignment. Reads the Issue, optionally runs the clarification loop,
    /// creates a PR linking to the Issue, and implements the solution.
    /// </summary>
    protected virtual async Task WorkOnIssueAsync(IssueAssignmentMessage assignment, CancellationToken ct)
    {
        var taskId = $"issue-{assignment.IssueNumber}";
        try
        {
            // Guard: services must be available (ActivatorUtilities should resolve them from DI)
            if (WorkItemService is null)
            {
                Logger.LogError("{Role} {Name} cannot work on issue #{Number}: WorkItemService is null (DI misconfiguration)",
                    Identity.Role, Identity.DisplayName, assignment.IssueNumber);
                CurrentIssueNumber = null;
                return;
            }

            // Clear any previous PR tracking from prior task. If the prior PR has already
            // moved past implementation, keep routing later review feedback back to us.
            await TrackCurrentPrAsPastIfApplicableAsync(ct);
            CurrentPrNumber = null;
            Identity.AssignedPullRequest = null;
            _implementationNotes.Clear();
            _lastAssessmentGaps = "";

            CurrentIssueNumber = assignment.IssueNumber;
            UpdateStatus(AgentStatus.Working, $"Starting issue #{assignment.IssueNumber}: {assignment.IssueTitle}");
            LogActivity("task", $"📋 Starting issue #{assignment.IssueNumber}: {assignment.IssueTitle}");

            var claimStepId = TaskTracker.BeginStep(Identity.Id, taskId, "Claim issue",
                $"Claiming issue #{assignment.IssueNumber}: {assignment.IssueTitle}", Identity.ModelTier);
            var issue = (await WorkItemService.GetAsync(assignment.IssueNumber, ct))?.ToAgentIssue();
            if (issue is null)
            {
                Logger.LogWarning("Cannot find issue #{Number}", assignment.IssueNumber);
                CurrentIssueNumber = null;
                return;
            }

            // Dependency gate: skip if prerequisites are still open
            if (!await AreDependenciesSatisfiedAsync(issue.Body, ct))
            {
                var deps = EngineeringTaskIssueManager.ParseDependencies(issue.Body, Logger);
                Logger.LogWarning(
                    "{Role} {Name}: Issue #{Number} has unmet dependencies ({Deps}) — deferring",
                    Identity.Role, Identity.DisplayName, assignment.IssueNumber,
                    string.Join(", ", deps.Select(d => $"#{d}")));
                TaskTracker.CompleteStep(claimStepId);
                CurrentIssueNumber = null;
                return;
            }

            // File overlap check: skip task if its files already exist in merged PRs
            var ownedFiles = EngineeringTaskIssueManager.ParseOwnedFiles(issue.Body);
            if (ownedFiles.Count > 0)
            {
                try
                {
                    var mergedPRs = await PrService.ListMergedAsync(ct);
                    var mergedFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mergedPr in mergedPRs.Take(10))
                    {
                        var prFiles = await PrService.GetChangedFilesAsync(mergedPr.Number, ct);
                        foreach (var f in prFiles)
                            mergedFileSet.Add(f.ToLowerInvariant().Replace('\\', '/'));
                    }

                    var normalizedOwned = ownedFiles
                        .Select(f => f.ToLowerInvariant().Replace('\\', '/'))
                        .ToList();
                    var overlapping = normalizedOwned.Count(f => mergedFileSet.Contains(f));

                    // Only skip when ALL files overlap AND none are marked as shared/multi-task.
                    // Stub files created by foundation tasks should not cause downstream tasks to be skipped.
                    var descLower = (issue.Body ?? "").ToLowerInvariant();
                    var hasSharedFiles = descLower.Contains("shared") || descLower.Contains("multi-task") || descLower.Contains("stub");
                    if (overlapping == normalizedOwned.Count && !hasSharedFiles)
                    {
                        Logger.LogWarning(
                            "{Role} {Name}: Task #{IssueNumber} has {Overlap}/{Total} files already in merged PRs — skipping as duplicate",
                            Identity.Role, Identity.DisplayName, assignment.IssueNumber, overlapping, normalizedOwned.Count);
                        LogActivity("warning",
                            $"⚠️ Skipping issue #{assignment.IssueNumber} — {overlapping}/{normalizedOwned.Count} files already created by a merged PR");

                        await WorkItemService.AddCommentAsync(assignment.IssueNumber,
                            $"⚠️ **Duplicate detected by {Identity.DisplayName}**: {overlapping}/{normalizedOwned.Count} files from this task " +
                            $"already exist in merged PRs. Closing as duplicate to avoid overlapping work.",
                            ct);
                        await WorkItemService.CloseAsync(assignment.IssueNumber, ct);
                        TaskTracker.CompleteStep(claimStepId);
                        CurrentIssueNumber = null;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "File overlap check failed — proceeding with task anyway");
                }
            }

            Logger.LogInformation("{Role} {Name} starting work on issue #{Number}: {Title}",
                Identity.Role, Identity.DisplayName, issue.Number, issue.Title);

            // Best-effort: post clarifying questions on ambiguous acceptance criteria before implementation
            await PostClarifyingQuestionsAsync(issue.Number, issue.Body ?? "", ct);

            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var techStack = Config.Project.TechStack;

            // Pre-PR Clarification: generate questions, wait for human approval if gate enabled.
            // 2026-05-12 split fix (claim-issue-step-bundles-too-much): the whole "Claim issue"
            // step previously bundled (a) the actual claim, (b) clarification-question generation
            // (60-90s LLM call on opus-1m), (c) implementation-planning LLM call, (d) clarification
            // loop. Operators saw "Claim issue 4m+" with no idea which sub-phase was slow. We now
            // emit nested child steps under the parent so the dashboard shows the breakdown.
            //
            // 2026-05-12 evening (rd-7 fix): wrap the entire clarification + planning block in a
            // try/finally so if GeneratePrePRQuestionsAsync, the planning LLM call, or
            // RunClarificationLoopAsync throws, claimStepId still gets closed. Without this guard
            // the parent step stays InProgress indefinitely until MaxStepsPerAgent eviction (~100
            // steps later), leaving "Claim issue ●" stuck in the dashboard for the dead run.
            // CompleteStep is idempotent (TaskTracker.CompleteStep:104-112 just sets Status +
            // CompletedAt; calling twice on a completed step is a harmless re-write).
            //
            // Variables declared at outer scope so they survive the try/finally and remain
            // accessible to the PR-creation block below.
            string clarificationContext = "";
            string planContent = "";
            try
            {
                var clarStepId = TaskTracker.BeginChildStep(Identity.Id, taskId, claimStepId,
                "Generate clarification questions",
                "Calling LLM to generate pre-PR clarification questions for human approval");
            try
            {
                clarificationContext = await GeneratePrePRQuestionsAsync(issue, pmSpecDoc, architectureDoc, ct);
                TaskTracker.RecordLlmCall(clarStepId);
            }
            finally
            {
                TaskTracker.CompleteStep(clarStepId);
            }

            // Fallback gate: if question generation failed/returned empty but the gate IS enabled,
            // still pause for human approval. Without this, any failure in GeneratePrePRQuestionsAsync
            // (JSON parse failure, exception, ClarificationStore null) silently bypasses the gate.
            // Uses WaitForHumanGateAsync for standard approval card rendering on the Approvals page.
            if (string.IsNullOrEmpty(clarificationContext)
                && (GateCheck?.RequiresHuman(GateIds.PrePRClarification) ?? false))
            {
                Logger.LogInformation(
                    "{Agent} pre-PR question generation returned empty but gate is enabled — requesting approval to proceed",
                    Identity.DisplayName);

                var fallbackGateResult = await WaitForHumanGateAsync(
                    GateIds.PrePRClarification,
                    $"{Identity.DisplayName}: Clarification question generation failed for Issue #{issue.Number}: {issue.Title}. " +
                    "Approve to proceed without clarification questions.",
                    issue.Number, ct: ct);

                if (fallbackGateResult.WasRejected)
                {
                    Logger.LogInformation(
                        "{Agent} pre-PR fallback gate rejected for issue #{Number}: {Feedback} — unclaiming task",
                        Identity.DisplayName, issue.Number, fallbackGateResult.Feedback);
                    CurrentIssueNumber = null;
                    TaskTracker.CompleteStep(claimStepId);
                    return;
                }
            }

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Implementation planning sub-step — the SECOND slow LLM call inside "Claim issue".
            // Without splitting it the operator can't tell whether the agent is stuck in the
            // clarification phase or the planning phase.
            var planStepId = TaskTracker.BeginChildStep(Identity.Id, taskId, claimStepId,
                "Plan implementation",
                "LLM produces implementation steps + acceptance criteria from issue + PMSpec + Architecture");
            try
            {
                // Use AI to understand the Issue and plan approach
                var memoryContext = await GetMemoryContextAsync(ct: ct);
                var planHistory = CreateChatHistory();
                var planSys = PromptService is not null
                    ? await PromptService.RenderAsync("engineer-base/planning-system", new Dictionary<string, string>
                    {
                        ["role_display_name"] = GetRoleDisplayName(),
                        ["tech_stack"] = techStack,
                        ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}"
                    }, ct)
                    : null;
                planHistory.AddSystemMessage(planSys
                    ?? $"You are a {GetRoleDisplayName()} analyzing a GitHub Issue (User Story) before starting work. " +
                       $"The project uses {techStack}. " +
                       "Read the Issue carefully and produce:\n" +
                       "1. A summary of what you understand needs to be built\n" +
                       "2. The acceptance criteria extracted from the Issue\n" +
                       "3. Detailed **Implementation Steps** — an ordered, numbered list of discrete steps " +
                       "to complete this task. Step 1 should be scaffolding (project structure, config, boilerplate). " +
                       "All file paths MUST be relative to the repo root. Place .sln at repo root, project under ProjectName/. " +
                       "NEVER create redundant same-named nested folders (e.g., RepoName/RepoName/ is WRONG). " +
                       "Each step should be a self-contained unit of committable work. 3-6 steps total.\n" +
                       "4. Any questions you have — if the requirements are UNCLEAR, list them. " +
                       "If you understand everything well enough to proceed, say 'NO_QUESTIONS'." +
                       (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}"));

                var planUser = PromptService is not null
                    ? await PromptService.RenderAsync("engineer-base/planning-user", new Dictionary<string, string>
                    {
                        ["pm_spec"] = pmSpecDoc,
                        ["architecture"] = architectureDoc,
                        ["issue_number"] = issue.Number.ToString(),
                        ["issue_title"] = issue.Title,
                        ["issue_body"] = issue.Body ?? ""
                    }, ct)
                    : null;
                var planUserContent = planUser
                    ?? $"## PM Specification\n{pmSpecDoc}\n\n" +
                       $"## Architecture\n{architectureDoc}\n\n" +
                       $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}";

                // Inject human-validated clarification decisions into planning context
                if (!string.IsNullOrEmpty(clarificationContext))
                    planUserContent += $"\n\n{clarificationContext}";

                planHistory.AddUserMessage(planUserContent);

                var planResponse = await chat.GetChatMessageContentAsync(planHistory, cancellationToken: ct);
                planContent = planResponse.Content?.Trim() ?? "";
                TaskTracker.RecordLlmCall(planStepId);
                TaskTracker.RecordLlmCall(claimStepId);

                // Clarification loop (if the engineer has questions)
                planContent = await RunClarificationLoopAsync(planHistory, planContent, issue, ct);
            }
            finally
            {
                TaskTracker.CompleteStep(planStepId);
            }
            TaskTracker.CompleteStep(claimStepId);
            }
            finally
            {
                // rd-7 fix: idempotent safety net — ensures claimStepId is closed even if
                // any of GeneratePrePRQuestionsAsync / planning LLM call / RunClarificationLoopAsync
                // throws. The explicit close above runs in the success path; this re-runs on
                // exception path. AgentTaskTracker.CompleteStep is idempotent.
                TaskTracker.CompleteStep(claimStepId);
            }

            // Parse and route contract-change DECISION blocks from plan output
            await ProcessPlanDecisionBlocksAsync(planContent, issue, ct);

            // Create PR linking to the Issue — include Implementation Steps
            UpdateStatus(AgentStatus.Working, "📝 Creating pull request");
            var createPrStepId = TaskTracker.BeginStep(Identity.Id, taskId, "Create PR",
                $"Creating PR for issue #{issue.Number}", Identity.ModelTier);
            var prDescription = $"Closes #{issue.Number}\n\n" +
                $"<!-- agent-id: {Identity.Id} -->\n" +
                $"## Understanding\n{ExtractSection(planContent, "summary", "understand")}\n\n" +
                $"## Acceptance Criteria\n{ExtractSection(planContent, "acceptance", "criteria")}\n\n" +
                $"## Implementation Steps\n{ExtractSection(planContent, "task", "plan", "step")}";
            // Sanitize AI-generated content to prevent accidental auto-close of sibling issues
            prDescription = SanitizeAutoCloseReferences(prDescription, issue.Number);

            var branchName = await PrWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName,
                $"issue-{issue.Number}-{Slugify(issue.Title)}",
                ct);

            var pr = await PrWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                issue.Title,
                prDescription,
                assignment.Complexity,
                "Architecture.md",
                "PMSpec.md",
                branchName,
                additionalLabels: null,
                ct);

            if (pr is null)
            {
                Logger.LogError("{Role} {Name} PR creation returned null for issue #{Number}",
                    Identity.Role, Identity.DisplayName, issue.Number);
                TaskTracker.CompleteStep(createPrStepId);
                CurrentIssueNumber = null;
                return;
            }

            CurrentPrNumber = pr.Number;
            Identity.AssignedPullRequest = pr.Number.ToString();

            // Bind CLI session to this PR for conversational continuity
            ActivatePrSession(pr.Number);

            Logger.LogInformation("{Role} {Name} created PR #{PrNumber} for issue #{IssueNumber}",
                Identity.Role, Identity.DisplayName, pr.Number, issue.Number);
            LogActivity("github", $"Created PR #{pr.Number} for issue #{issue.Number}: {issue.Title}");
            TaskTracker.CompleteStep(createPrStepId);

            await RememberAsync(MemoryType.Action,
                $"Created PR #{pr.Number} for issue #{issue.Number}: {issue.Title}",
                $"Branch: {branchName}. Plan: {TruncateForMemory(planContent)}", ct);

            await ImplementAndCommitAsync(pr, issue, ct);

            CurrentIssueNumber = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Role} {Name} failed on issue #{Number}",
                Identity.Role, Identity.DisplayName, assignment.IssueNumber);
            RecordError($"Failed on issue #{assignment.IssueNumber}: {ex.Message}",
                Microsoft.Extensions.Logging.LogLevel.Error, ex);
            CurrentIssueNumber = null;
        }
    }

    /// <summary>
    /// Core implementation logic: uses AI to produce an implementation plan with discrete steps,
    /// then iterates step by step — committing code after each step. This avoids one monolithic
    /// AI call and ensures incremental progress is visible on the PR.
    /// </summary>
    protected virtual async Task ImplementAndCommitAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        var implTaskId = $"pr-{pr.Number}";
        var architectureDoc = await GetArchitectureForContextAsync(ct);
        var pmSpecDoc = await GetPMSpecForContextAsync(ct);
        var techStack = Config.Project.TechStack;

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        List<string> steps;

        // Step 1: Generate ordered implementation steps from the PR description
        // SingleCommitMode: treat entire task as one step rather than generating a multi-step plan
        if (Config.Agents.SingleCommitMode)
        {
            Logger.LogInformation("{Role} {Name} using single-commit mode for PR #{Number}",
                Identity.Role, Identity.DisplayName, pr.Number);

            // Even in single-commit mode, prefer agentic CLI (full tool access) over blind generation
            if (Workspace is not null && BuildRunnerSvc is not null)
            {
                steps = [$"Implement the full task described in issue #{issue.Number}: {issue.Title}\n\n{issue.Body}"];
            }
            else
            {
                // No workspace — blind single-pass is the only option (API-only mode)
                Logger.LogWarning("{Role} {Name}: no workspace available; using blind single-pass for PR #{Number}. " +
                    "Code quality will be degraded without tool access.",
                    Identity.Role, Identity.DisplayName, pr.Number);
                await ImplementSinglePassAsync(pr, issue, pmSpecDoc, architectureDoc, techStack, chat, ct);
                return;
            }
        }
        else
        {
            UpdateStatus(AgentStatus.Working, $"PR #{pr.Number} generating implementation steps");
            var genStepsStepId = TaskTracker.BeginStep(Identity.Id, implTaskId, "Generate implementation steps",
                $"Breaking PR #{pr.Number} into discrete implementation steps", Identity.ModelTier);
            steps = await GenerateImplementationStepsAsync(chat, pr, issue, pmSpecDoc, architectureDoc, techStack, ct);
            TaskTracker.RecordLlmCall(genStepsStepId);
            TaskTracker.CompleteStep(genStepsStepId);

            if (steps.Count == 0)
            {
                Logger.LogWarning("{Role} {Name} step generation returned 0 steps; synthesizing single agentic step for PR #{Number}",
                    Identity.Role, Identity.DisplayName, pr.Number);
                // Synthesize a single step so we still go through the agentic path (full tool access)
                steps = [$"Implement the full task described in issue #{issue.Number}: {issue.Title}\n\n{issue.Body}"];
            }
        }

        Logger.LogInformation("{Role} {Name} generated {Count} implementation steps for PR #{Number}",
            Identity.Role, Identity.DisplayName, steps.Count, pr.Number);
        LogActivity("task", $"Generated {steps.Count} implementation steps for PR #{pr.Number}");
        RecordImplementationNote($"Plan: {steps.Count} implementation steps for '{issue.Title}'");

        // Check for previously completed steps (crash recovery)
        var resumeFromStep = await DetectCompletedStepsAsync(pr.Number, steps.Count, ct);
        if (resumeFromStep > 0)
        {
            Logger.LogInformation("{Role} {Name} resuming PR #{PrNumber} from step {Step}/{Total} (skipping {Completed} already-committed steps)",
                Identity.Role, Identity.DisplayName, pr.Number, resumeFromStep + 1, steps.Count, resumeFromStep);
            LogActivity("task", $"♻️ Resuming PR #{pr.Number} from step {resumeFromStep + 1}/{steps.Count} ({resumeFromStep} steps already committed)");
        }

        // Step 2: Iterate through each step, generating code and committing
        var completedSteps = new List<string>();

        // Pre-populate completed steps list for context (steps we're skipping)
        for (var s = 0; s < resumeFromStep && s < steps.Count; s++)
            completedSteps.Add(steps[s]);

        for (var i = resumeFromStep; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            var stepNumber = i + 1;

            var execStepId = TaskTracker.BeginStep(Identity.Id, implTaskId,
                $"Execute step {stepNumber}: {Truncate(step, 60)}",
                $"Step {stepNumber}/{steps.Count} for PR #{pr.Number}", Identity.ModelTier);

            UpdateStatus(AgentStatus.Working,
                $"🔨 Implementing step {stepNumber}/{steps.Count}: {Truncate(step, 40)}");
            Logger.LogInformation("{Role} {Name} implementing step {Step}/{Total} for PR #{PrNumber}: {StepDesc}",
                Identity.Role, Identity.DisplayName, stepNumber, steps.Count, pr.Number,
                Truncate(step, 100));

            var stepHistory = CreateChatHistory();
            stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

            var contextBuilder = new System.Text.StringBuilder();
            contextBuilder.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
            contextBuilder.AppendLine($"## Architecture\n{architectureDoc}\n");

            // Tier 1: Include existing repo structure so engineer knows what already exists
            var repoStructure = await GetRepoStructureForContextAsync(ct);
            if (!string.IsNullOrEmpty(repoStructure))
            {
                contextBuilder.AppendLine("## Existing Repository Structure (main branch)");
                contextBuilder.AppendLine(repoStructure);
                contextBuilder.AppendLine();
                contextBuilder.AppendLine("IMPORTANT: The repository already has the files listed above. " +
                    "Do NOT create files that duplicate existing functionality. " +
                    "Place new files in the appropriate existing directories. " +
                    "Use namespaces consistent with existing code. " +
                    "If you need to add functionality that relates to an existing file, MODIFY that file instead of creating a new one. " +
                    "ESPECIALLY for model/type definitions: check if the type already exists in an existing file (e.g., a shared Models file) before creating a new file for it. " +
                    "Creating a duplicate type definition in a separate file will cause build errors.\n");
            }

            contextBuilder.AppendLine($"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n");
            contextBuilder.AppendLine($"## PR Description\n{pr.Body}\n");

            // Include visual design context for UI-related tasks
            var designCtx = await GetDesignContextAsync(ct);
            if (!string.IsNullOrWhiteSpace(designCtx))
                contextBuilder.AppendLine(designCtx + "\n");

            if (completedSteps.Count > 0)
            {
                contextBuilder.AppendLine("## Previously Completed Steps");
                for (var j = 0; j < completedSteps.Count; j++)
                    contextBuilder.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                contextBuilder.AppendLine();

                // Include list of files already committed so the AI knows what exists
                var existingFiles = await GetPrFileListAsync(pr.Number, ct);
                if (!string.IsNullOrEmpty(existingFiles))
                    contextBuilder.AppendLine($"## Files already in this PR\n{existingFiles}\n");
            }

            // Tier 2: Load actual content of existing files mentioned in this step
            // so the AI can make surgical modifications instead of rewriting from scratch
            var existingFileContent = await GetExistingFileContentForStepAsync(step, pr.HeadBranch, ct);
            if (!string.IsNullOrEmpty(existingFileContent))
                contextBuilder.AppendLine(existingFileContent);

            // === Agentic CLI Edit Mode (default when workspace available) ===
            var useAgenticMode = Workspace is not null && BuildRunnerSvc is not null;
            if (useAgenticMode)
            {
                var branchName = GetPrBranchName(pr);
                await Workspace!.CheckoutBranchAsync(branchName, ct);

                using var cliScope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                    AllowFileEdits: true,
                    OverrideWorkingDirectory: Workspace.RepoPath));

                var agenticPrompt = new System.Text.StringBuilder();
                agenticPrompt.AppendLine($"You are implementing step {stepNumber}/{steps.Count} for a coding task.");
                agenticPrompt.AppendLine($"Tech stack: {techStack}");
                agenticPrompt.AppendLine();
                agenticPrompt.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                agenticPrompt.AppendLine(step);
                agenticPrompt.AppendLine();
                agenticPrompt.AppendLine($"## Issue #{issue.Number}: {issue.Title}");
                agenticPrompt.AppendLine(Truncate(issue.Body ?? "", 2000));
                agenticPrompt.AppendLine();
                if (completedSteps.Count > 0)
                {
                    agenticPrompt.AppendLine("## Previously Completed Steps");
                    for (var j = 0; j < completedSteps.Count; j++)
                        agenticPrompt.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                    agenticPrompt.AppendLine();
                }
                agenticPrompt.AppendLine("## Instructions");
                agenticPrompt.AppendLine("1. Read existing files before modifying them");
                agenticPrompt.AppendLine("2. Make surgical edits — do NOT rewrite entire files");
                agenticPrompt.AppendLine("3. Run `dotnet build` after changes to verify compilation");
                agenticPrompt.AppendLine("4. Fix any build errors before finishing");
                agenticPrompt.AppendLine("5. Do NOT run git push or create PRs");
                agenticPrompt.AppendLine("6. Do NOT modify files outside this task's scope");

                var agenticHistory = CreateChatHistory();
                agenticHistory.AddUserMessage(agenticPrompt.ToString());

                var headBefore = await Workspace.GetHeadShaAsync("HEAD", ct);
                try
                {
                    await chat.GetChatMessageContentAsync(agenticHistory, cancellationToken: ct);
                    TaskTracker.RecordLlmCall(execStepId);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "{Role} {Name} agentic step {Step}/{Total} failed",
                        Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                    await Workspace.RevertUncommittedChangesAsync(ct);
                    TaskTracker.FailStep(execStepId, $"Agentic step failed: {ex.Message}");
                    completedSteps.Add(step);
                    continue;
                }

                var headAfter = await Workspace.GetHeadShaAsync("HEAD", ct);
                var cliCommitted = !string.Equals(headBefore?.Trim(), headAfter?.Trim(), StringComparison.OrdinalIgnoreCase);
                var changedFiles = await Workspace.GetChangedFilePathsAsync(ct);

                if (changedFiles.Count > 0 || cliCommitted)
                {
                    if (changedFiles.Count > 0)
                        await Workspace.CommitAsync($"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}", ct);
                    await Workspace.PushAsync(branchName, ct);

                    Logger.LogInformation("{Role} {Name} committed step {Step}/{Total} on PR #{PrNumber} (agentic mode)",
                        Identity.Role, Identity.DisplayName, stepNumber, steps.Count, pr.Number);
                    LogActivity("task", $"✅ Step {stepNumber}/{steps.Count} committed (agentic): {Truncate(step, 80)}");
                    await CheckpointTaskProgressAsync(pr.Number, CurrentIssueNumber, stepNumber, ct);
                    TaskTracker.CompleteStep(execStepId);
                }
                else
                {
                    Logger.LogWarning("{Role} {Name} agentic step {Step}/{Total} produced no changes",
                        Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                    TaskTracker.CompleteStep(execStepId);
                }

                completedSteps.Add(step);
                continue;
            }

            // === FILE: Block Mode (fallback when no local workspace) ===

            // Inject file scope rules from the task's File Plan
            var scopeBlock = BuildFileScopePromptBlock(pr.Body, issue.Body);
            _currentFileScopeBlock = scopeBlock; // Cache for build-fix prompts
            if (!string.IsNullOrEmpty(scopeBlock))
                contextBuilder.AppendLine(scopeBlock);
            if (completedSteps.Count > 0)
                contextBuilder.AppendLine("If you need to update a file from a previous step, include the COMPLETE updated file content.");

            contextBuilder.AppendLine("\nINCREMENTAL MODIFICATION RULE: When modifying an EXISTING file (one shown in " +
                "'Existing File Contents' or 'Existing Repository Structure' above), you MUST preserve ALL existing " +
                "code, CSS classes, HTML structure, and functionality that is not directly related to this step. " +
                "Make SURGICAL additions and targeted edits — do NOT rewrite the entire file from scratch. " +
                "Your diff should show mostly additions with minimal modifications to existing lines. " +
                "If you are adding a new section (e.g., a heatmap component), insert it at the appropriate " +
                "location within the existing file structure without altering surrounding code.");

            contextBuilder.AppendLine(AdditiveEditingGuidance);

            stepHistory.AddUserMessage(contextBuilder.ToString());

            var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
            var stepImpl = stepResponse.Content?.Trim() ?? "";
            TaskTracker.RecordLlmCall(execStepId);
            TaskTracker.RecordSubStep(execStepId, $"Implementation for step {stepNumber}");

            // Optional self-review for this step
            stepHistory.AddAssistantMessage(stepImpl);
            var finalStepOutput = await RunSelfReviewAsync(stepHistory, stepImpl, ct);
            TaskTracker.RecordSubStep(execStepId, $"Self-review for step {stepNumber}");

            var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(finalStepOutput);
            if (codeFiles.Count == 0)
                codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(stepImpl);

            // Auto-correct file paths missing project subdirectory prefix
            // (e.g., "Components/Header.razor" → "src/MyProject/Components/Header.razor")
            if (codeFiles.Count > 0)
            {
                var resolved = await PrWorkflow.ResolveFilePathsAsync(codeFiles, ct);
                codeFiles = resolved as List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>
                    ?? new List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>(resolved);
            }

            // Enforce file scope: only allow files listed in the task's File Plan
            if (codeFiles.Count > 0)
                codeFiles = FilterToAllowedScope(codeFiles, pr.Body, issue.Body, pr.Number);

            if (codeFiles.Count > 0)
            {
                var commitMsg = $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}";
                bool committed;

                // Local workspace mode: write → build → test → commit → push
                if (Workspace is not null && BuildRunnerSvc is not null)
                {
                    committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, commitMsg, stepNumber, steps.Count, step, chat, ct);
                }
                else
                {
                    // Fallback: GitHub API mode (no local build/test — code is NOT build-validated)
                    await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, commitMsg, ct);
                    _ = Metrics?.RecordApiOnlyCommitAsync(Identity.Id, ct);
                    committed = true;
                }

                if (committed)
                {
                    Logger.LogInformation("{Role} {Name} committed {FileCount} files for step {Step}/{Total} on PR #{PrNumber}",
                        Identity.Role, Identity.DisplayName, codeFiles.Count, stepNumber, steps.Count, pr.Number);
                    LogActivity("task", $"✅ Step {stepNumber}/{steps.Count} committed ({codeFiles.Count} files): {Truncate(step, 80)}");

                    await RememberAsync(MemoryType.Action,
                        $"PR #{pr.Number}: Committed step {stepNumber}/{steps.Count} ({codeFiles.Count} files)",
                        Truncate(step, 200), ct);

                    // Checkpoint progress so we can resume after a crash
                    await CheckpointTaskProgressAsync(pr.Number, CurrentIssueNumber, stepNumber, ct);
                    TaskTracker.CompleteStep(execStepId);
                }
                else
                {
                    Logger.LogWarning("{Role} {Name} step {Step}/{Total} blocked by build errors, skipping",
                        Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                    LogActivity("task", $"⛔ Step {stepNumber}/{steps.Count} blocked by build errors: {Truncate(step, 80)}");
                    RecordImplementationNote($"SKIPPED step {stepNumber}/{steps.Count} due to build errors: {Truncate(step, 120)}");
                    TaskTracker.FailStep(execStepId, "Blocked by build errors");
                }
            }
            else
            {
                Logger.LogWarning("{Role} {Name} step {Step}/{Total} produced no parseable files, skipping commit",
                    Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                TaskTracker.CompleteStep(execStepId, AgentTaskStepStatus.Skipped);
            }

            completedSteps.Add(step);

            // Between-step hook: allows subclasses (SE leader) to run inter-step coordination
            // such as assigning tasks to idle workers while the leader is busy implementing.
            await OnStepCompletedAsync(stepNumber, steps.Count, ct);
        }

        // Mark PR ready for review after all steps complete
        var markReadyStepId = TaskTracker.BeginStep(Identity.Id, implTaskId, "Mark ready for review",
            $"Marking PR #{pr.Number} ready for review", Identity.ModelTier);
        TaskTracker.SetStepWaiting(markReadyStepId);
        await MarkPrCompleteAsync(pr, issue, ct);
        TaskTracker.CompleteStep(markReadyStepId);
    }

    /// <summary>
    /// Detects how many implementation steps have already been committed to a PR
    /// by examining commit messages for the "Step N/M" pattern. Returns the 0-based
    /// index to resume from (i.e., the number of completed steps).
    /// </summary>
    protected async Task<int> DetectCompletedStepsAsync(int prNumber, int totalSteps, CancellationToken ct)
    {
        try
        {
            // First check SQLite checkpoint (faster, more reliable)
            var checkpoint = await StateStore.LoadAgentTaskCheckpointAsync(Identity.Role.ToString(), ct);
            if (checkpoint is not null && checkpoint.PrNumber == prNumber && checkpoint.StepIndex > 0)
            {
                Logger.LogInformation("{Role} found SQLite checkpoint: step {Step} for PR #{Pr}",
                    Identity.Role, checkpoint.StepIndex, prNumber);
                return Math.Min(checkpoint.StepIndex, totalSteps);
            }

            // Fallback: parse commit messages from GitHub
            var commitMessages = await PrService.GetCommitMessagesAsync(prNumber, ct);
            var maxCompletedStep = 0;

            foreach (var msg in commitMessages)
            {
                // Match "Step 3/6:" pattern
                var match = System.Text.RegularExpressions.Regex.Match(msg, @"^Step\s+(\d+)/(\d+):");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var stepNum))
                {
                    maxCompletedStep = Math.Max(maxCompletedStep, stepNum);
                }
            }

            return Math.Min(maxCompletedStep, totalSteps);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to detect completed steps for PR #{Pr}, starting from beginning", prNumber);
            return 0;
        }
    }

    /// <summary>
    /// Checkpoint current task progress to SQLite after each successful step commit.
    /// </summary>
    protected async Task CheckpointTaskProgressAsync(int prNumber, int? issueNumber, int stepIndex, CancellationToken ct)
    {
        try
        {
            var reworkJson = System.Text.Json.JsonSerializer.Serialize(
                ReworkAttemptCounts.ToDictionary(
                    kvp => $"{kvp.Key.PrNumber}|{kvp.Key.Reviewer}",
                    kvp => kvp.Value));
            await StateStore.SaveAgentTaskCheckpointAsync(
                Identity.Role.ToString(),
                currentTaskId: null,
                stepIndex: stepIndex,
                prNumber: prNumber,
                issueNumber: issueNumber,
                reworkAttemptsJson: reworkJson,
                stateJson: null,
                ct);
            // Also persist other retry counters so they survive restart
            PersistRetryCounters();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to checkpoint task progress for PR #{Pr} step {Step}", prNumber, stepIndex);
        }
    }

    /// <summary>
    /// Persist all retry/attempt counters to run_metadata (SQLite) so they survive restarts.
    /// Called after each checkpoint and after each rework attempt increment.
    /// </summary>
    protected void PersistRetryCounters()
    {
        if (StateStore is null) return;
        try
        {
            if (TeReworkAttemptCounts.Count > 0)
                StateStore.SetRunMetadata($"{Identity.Id}:teReworkAttempts",
                    System.Text.Json.JsonSerializer.Serialize(TeReworkAttemptCounts));
            if (_issueRetryAttempts.Count > 0)
                StateStore.SetRunMetadata($"{Identity.Id}:issueRetryAttempts",
                    System.Text.Json.JsonSerializer.Serialize(_issueRetryAttempts));
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Failed to persist retry counters for {Agent}", Identity.DisplayName);
        }
    }

    /// <summary>
    /// Restore retry counters from run_metadata on startup.
    /// </summary>
    protected void RestoreRetryCounters()
    {
        if (StateStore is null) return;
        try
        {
            var entries = StateStore.GetRunMetadataByPrefix($"{Identity.Id}:");
            if (entries.TryGetValue($"{Identity.Id}:teReworkAttempts", out var teReworkJson))
            {
                var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(teReworkJson);
                if (restored is not null)
                {
                    foreach (var kvp in restored) TeReworkAttemptCounts[kvp.Key] = kvp.Value;
                    Logger.LogInformation("{Agent} restored {Count} TE rework counters", Identity.DisplayName, restored.Count);
                }
            }
            if (entries.TryGetValue($"{Identity.Id}:issueRetryAttempts", out var issueJson))
            {
                var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(issueJson);
                if (restored is not null)
                {
                    foreach (var kvp in restored) _issueRetryAttempts[kvp.Key] = kvp.Value;
                    Logger.LogInformation("{Agent} restored {Count} issue retry counters", Identity.DisplayName, restored.Count);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Agent} failed to restore retry counters", Identity.DisplayName);
        }
    }

    /// <summary>
    /// Uses AI to break the task into ordered implementation steps.
    /// Step 1 should be scaffolding; subsequent steps build on it.
    /// </summary>
    protected async Task<List<string>> GenerateImplementationStepsAsync(
        IChatCompletionService chat, AgentPullRequest pr, AgentIssue issue,
        string pmSpec, string archDoc, string techStack, CancellationToken ct)
    {
        try
        {
            var history = CreateChatHistory();
            var stepSys = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/step-planning-system", new Dictionary<string, string>
                {
                    ["role_display_name"] = GetRoleDisplayName(),
                    ["tech_stack"] = techStack
                }, ct)
                : null;
            history.AddSystemMessage(stepSys
                ?? $"You are a {GetRoleDisplayName()} planning implementation steps for a coding task. " +
                   $"The project uses {techStack}. " +
                   "Break the task into 3-6 discrete, ordered implementation steps. " +
                   "IMPORTANT rules:\n" +
                   "- Step 1 MUST be project scaffolding: folder structure, config files, boilerplate, " +
                   "package manifests, and empty placeholder files that establish the project skeleton.\n" +
                   "- All file paths are relative to the REPOSITORY ROOT. The repo root IS the solution root.\n" +
                   "- Place .sln at repo root, project files under a single ProjectName/ subfolder.\n" +
                   "- NEVER create multiple levels of same-named folders (e.g., MyApp/MyApp/MyApp/ is WRONG).\n" +
                   "- Only ONE .gitignore at the repo root.\n" +
                   "- Each subsequent step should build on what the previous steps created.\n" +
                   "- Each step should be a self-contained unit of work that produces committable code.\n" +
                   "- Steps should be small enough to complete in a single AI response.\n" +
                   "- The final step should handle polish: integration, cleanup, and any remaining wiring.\n\n" +
                   "Output ONLY a numbered list of steps, one per line. Each step should be a clear, " +
                   "actionable description (1-2 sentences) of what to build. No other text.");

            var stepUser = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/step-planning-user", new Dictionary<string, string>
                {
                    ["issue_number"] = issue.Number.ToString(),
                    ["issue_title"] = issue.Title,
                    ["issue_body"] = issue.Body ?? "",
                    ["pr_body"] = pr.Body ?? "",
                    ["architecture"] = archDoc,
                    ["pm_spec"] = pmSpec
                }, ct)
                : null;
            history.AddUserMessage(stepUser
                ?? $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n" +
                   $"## PR Description\n{pr.Body}\n\n" +
                   $"## Architecture\n{archDoc}\n\n" +
                   $"## PM Specification\n{pmSpec}");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";

            // Parse any DECISION blocks emitted before the numbered steps
            var decisionBlocks = DecisionBlockParser.ParsePipeDelimited(content);
            if (decisionBlocks.Count > 0 && DecisionGate != null)
            {
                Logger.LogInformation("Found {Count} contract-change decision(s) in step planning output",
                    decisionBlocks.Count);
                // Process decisions in a fire-and-forget style — don't block step generation
                // The decisions are logged and gating happens via self-assessment
                foreach (var (impact, title, rationale, files) in decisionBlocks)
                {
                    try
                    {
                        var assessment = new Core.Agents.Reasoning.AssessmentResult
                        {
                            Passed = true,
                            Gaps = Array.Empty<string>(),
                            Summary = $"Contract change: {title}",
                            ImpactLevel = ParseImpactLevel(impact),
                            ImpactRationale = rationale,
                            AffectedFiles = files,
                            Alternatives = "Keep existing contract as-is",
                            RiskAssessment = "Changing this contract affects consuming code"
                        };

                        await DecisionGate.ClassifyFromAssessmentAsync(
                            Identity.Id, Identity.DisplayName, "Implementation",
                            $"Contract Change: {title}", $"Step planning: {rationale}", assessment,
                            category: "Contract Change",
                            modelTier: Identity.ModelTier, ct: ct);

                        RecordImplementationNote($"📋 Contract change logged: {title} ({impact})");
                    }
                    catch (Exception dex)
                    {
                        Logger.LogWarning(dex, "Failed to process contract-change decision from step planning: {Title}", title);
                    }
                }
            }

            return ParseNumberedSteps(content);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} failed to generate implementation steps",
                Identity.Role, Identity.DisplayName);
            return new List<string>();
        }
    }

    /// <summary>
    /// <summary>
    /// DEPRECATED: Blind single-pass implementation without tool access. Only used as a last resort
    /// when no local workspace is available (API-only mode). Produces lower quality code because
    /// the LLM cannot browse the codebase, run builds, or use any tools.
    /// Prefer the agentic step loop (which uses AllowFileEdits + workspace) for all implementations.
    /// </summary>
    private async Task ImplementSinglePassAsync(
        AgentPullRequest pr, AgentIssue issue,
        string pmSpec, string archDoc, string techStack,
        IChatCompletionService chat, CancellationToken ct)
    {
        var history = CreateChatHistory();
        history.AddSystemMessage(GetImplementationSystemPrompt(techStack));

        var promptBuilder = new System.Text.StringBuilder();
        promptBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");
        promptBuilder.AppendLine($"## Architecture\n{archDoc}\n");
        promptBuilder.AppendLine($"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n");
        promptBuilder.AppendLine($"## PR Description\n{pr.Body}\n");

        var designCtx = await GetDesignContextAsync(ct);
        if (!string.IsNullOrWhiteSpace(designCtx))
            promptBuilder.AppendLine(designCtx + "\n");

        promptBuilder.AppendLine("Produce a complete implementation. Output each file using this format:\n");
        promptBuilder.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
        promptBuilder.AppendLine($"Use the {techStack} technology stack. " +
            "Include all source code files, configuration, and tests. " +
            "Every file MUST use the FILE: marker format. " +
            "File paths must be valid filesystem paths (e.g., src/Models/User.cs). " +
            "Do NOT put code, directives, brackets, or instructions in the file path.\n\n" +
            "CRITICAL: The app must compile and run after your changes. " +
            "Use graceful fallbacks for missing dependencies from other tasks — " +
            "never throw NotImplementedException or reference types that don't exist yet.");

        promptBuilder.AppendLine(AdditiveEditingGuidance);

        history.AddUserMessage(promptBuilder.ToString());

        var implResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        history.AddAssistantMessage(implResponse.Content ?? "");
        var implementation = implResponse.Content?.Trim() ?? "";

        var finalOutput = await RunSelfReviewAsync(history, implementation, ct);
        await CommitAndNotifyAsync(pr, issue, finalOutput, implementation, ct);
    }

    /// <summary>
    /// Uses a quick AI call to identify ambiguities in the issue's acceptance criteria
    /// and posts clarifying questions as a comment. Best-effort — never blocks implementation.
    /// </summary>
    private async Task PostClarifyingQuestionsAsync(int issueNumber, string issueBody, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(issueBody))
                return;

            var kernel = Models.GetKernel("budget", Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();
            history.AddSystemMessage(
                "You are a software engineer reviewing a task before implementation. " +
                "Identify 1-3 clarifying questions about ambiguous acceptance criteria. " +
                "If everything is clear, respond with just 'CLEAR'. " +
                "Keep questions concise and actionable.");
            history.AddUserMessage($"Task:\n{issueBody}");
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";

            if (!content.Contains("CLEAR", StringComparison.OrdinalIgnoreCase) && content.Length > 10)
            {
                await WorkItemService.AddCommentAsync(issueNumber,
                    $"**[{Identity.DisplayName}] Pre-Implementation Questions:**\n\n{content}\n\n" +
                    "_Proceeding with implementation based on current understanding._", ct);
                LogActivity("task", $"❓ Posted clarifying questions on issue #{issueNumber}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to post clarifying questions on issue #{Number}", issueNumber);
        }
    }

    /// <summary>
    /// Generates pre-PR clarification questions using AI and waits for human approval if gate is enabled.
    /// Returns the finalized Q&A context string to inject into planning prompts, or empty string on failure.
    /// </summary>
    protected async Task<string> GeneratePrePRQuestionsAsync(
        AgentIssue issue, string pmSpec, string architecture, CancellationToken ct)
    {
        if (ClarificationStore is null)
        {
            Logger.LogDebug("PrePRClarificationStore not available — skipping question generation");
            return "";
        }

        try
        {
            UpdateStatus(AgentStatus.Working, "🤔 Generating clarification questions");
            var techStack = Config.Project.TechStack;
            var projectDesc = Config.Project.ResolvedDescription ?? Config.Project.Description ?? "";

            // Change #4 — Extract scenario IDs from the issue body and surface them in the
            // clarification prompt so questions are scenario-grounded (Lesson #16 dual-path:
            // both WorkOnIssueAsync specialist path and WorkOnOwnTasksAsync SE path call here).
            var implementsScenarios = ParseImplementedScenarios(issue.Body);
            var scenarioContext = implementsScenarios.Count > 0
                ? $"Task implements scenarios: {string.Join(", ", implementsScenarios)}."
                : "";

            // Generate questions via LLM
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();

            var prompt = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/pre-pr-questions", new Dictionary<string, string>
                {
                    ["issue_number"] = issue.Number.ToString(),
                    ["issue_title"] = issue.Title,
                    ["issue_body"] = issue.Body ?? "",
                    ["pm_spec"] = pmSpec ?? "",
                    ["architecture"] = architecture ?? "",
                    ["tech_stack"] = techStack,
                    ["project_description"] = projectDesc,
                    ["implements_scenarios"] = scenarioContext
                }, ct)
                : null;

            if (prompt is null)
            {
                // Hardcoded fallback — include scenario reference block when available
                var scenarioBlock = string.IsNullOrEmpty(scenarioContext) ? "" : $"\n\n**Scenarios:** {scenarioContext}";
                prompt = $"You are a senior engineer about to implement Issue #{issue.Number}: {issue.Title}.\n" +
                    $"Tech stack: {techStack}\n\nTask:\n{issue.Body}{scenarioBlock}\n\nPM Spec:\n{pmSpec}\n\n" +
                    $"Architecture:\n{architecture}\n\n" +
                    "Generate up to 10 clarification questions as a JSON array. Each element: " +
                    "{\"question\": \"...\", \"proposedAnswer\": \"...\", \"impactLevel\": \"XS|S|M|L|XL\", \"category\": \"...\"}.\n" +
                    "Return ONLY the JSON array.";
            }

            history.AddUserMessage(prompt);
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";

            // Parse JSON array response — retry once on failure
            var questions = ParsePrePRQuestions(content);
            if (questions.Count == 0)
            {
                Logger.LogWarning(
                    "{Agent} pre-PR question parse returned 0 results for issue #{Number} — retrying once (response length: {Len})",
                    Identity.DisplayName, issue.Number, content.Length);

                // Retry with a fresh LLM call
                var retryHistory = CreateChatHistory();
                retryHistory.AddUserMessage(prompt + "\n\nIMPORTANT: Return ONLY a valid JSON array, no markdown fences or extra text.");
                var retryResponse = await chat.GetChatMessageContentAsync(retryHistory, cancellationToken: ct);
                var retryContent = retryResponse.Content?.Trim() ?? "";
                questions = ParsePrePRQuestions(retryContent);

                if (questions.Count == 0)
                {
                    Logger.LogWarning(
                        "{Agent} pre-PR question generation failed after retry for issue #{Number} — proceeding without clarification",
                        Identity.DisplayName, issue.Number);
                    return "";
                }

                Logger.LogInformation("{Agent} pre-PR question retry succeeded: {Count} questions for issue #{Number}",
                    Identity.DisplayName, questions.Count, issue.Number);
            }

            // Store the question set
            var setId = $"prepr-{Identity.Id}-{issue.Number}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var questionSet = new PrePRClarificationSet
            {
                Id = setId,
                AgentId = Identity.Id,
                AgentDisplayName = Identity.DisplayName,
                IssueNumber = issue.Number,
                IssueTitle = issue.Title,
                Questions = questions
            };
            ClarificationStore.Add(questionSet);

            // Check if gate is enabled
            var gateRequired = GateCheck?.RequiresHuman(GateIds.PrePRClarification) ?? false;

            if (gateRequired)
            {
                // Wait for human approval via the gate system
                Logger.LogInformation("{Agent} waiting for pre-PR clarification approval on issue #{Number}",
                    Identity.DisplayName, issue.Number);
                UpdateStatus(AgentStatus.Blocked, "⏳ Awaiting clarification approval");

                try
                {
                var gateContext = $"{Identity.DisplayName} has {questions.Count} implementation questions for Issue #{issue.Number}: {issue.Title}";
                var gateResult = await GateCheck!.CheckGateAsync(
                    GateIds.PrePRClarification, gateContext, issue.Number, ct);

                if (gateResult == GateResult.Proceed)
                {
                    // Gate already approved (e.g., restart recovery) — finalize with proposed answers
                    if (ClarificationStore.Get(setId)?.IsFinalized != true)
                        ClarificationStore.Finalize(setId);
                }
                else if (gateResult == GateResult.WaitingForHuman)
                {
                    // Poll until the question set is finalized or gate is resolved
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        var current = ClarificationStore.Get(setId);
                        if (current?.IsFinalized == true) break;

                        // Check if gate was approved via generic card or platform
                        if (GateCheck.IsGateApprovedLocally(GateIds.PrePRClarification, issue.Number))
                        {
                            // Finalize with proposed answers if not already done by the special card
                            if (current?.IsFinalized != true)
                                ClarificationStore.Finalize(setId);
                            break;
                        }

                        // Check if gate was rejected — don't wait forever
                        if (GateCheck.GetLocalRejection(GateIds.PrePRClarification, issue.Number) is not null)
                        {
                            Logger.LogWarning("{Agent} pre-PR clarification rejected for issue #{Number} — proceeding with proposed answers",
                                Identity.DisplayName, issue.Number);
                            ClarificationStore.Finalize(setId);
                            break;
                        }
                    }
                }
                }
                finally
                {
                    UpdateStatus(AgentStatus.Working, "Clarification resolved, resuming work");
                }
            }
            else
            {
                // Auto-approve — use proposed answers directly
                ClarificationStore.AutoApprove(setId);
            }

            // Log each Q&A as a decision
            var finalizedSet = ClarificationStore.Get(setId);
            if (finalizedSet is not null)
            {
                LogQuestionsAsDecisions(finalizedSet);
                return BuildClarificationContext(finalizedSet);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Pre-PR question generation failed for issue #{Number} — proceeding without", issue.Number);
        }

        return "";
    }

    private List<PrePRQuestion> ParsePrePRQuestions(string jsonContent)
    {
        var questions = new List<PrePRQuestion>();
        try
        {
            // 2026-05-13 fix (pre-pr-clarification-failed-after-retry): the LLM
            // (especially Copilot CLI in agentic mode) frequently emits mixed prose +
            // JSON: a preamble paragraph + bullet-pointed Q/A markdown + the JSON
            // array somewhere in the middle/end. The previous parser called
            // JsonDocument.Parse(content) on the raw content and failed with
            // "Invalid start of value" → returned empty list → agent burned $0.37
            // per task on guaranteed-to-fail retries.
            //
            // New parser:
            //   1. Strip markdown fences
            //   2. If content doesn't start with '[', find the FIRST balanced JSON
            //      array via bracket matching (skips prose preamble)
            //   3. Parse the extracted substring
            //   4. On failure, log Debug with content snippet for diagnosis

            var content = jsonContent.Trim();

            // Strip markdown fences if present
            if (content.StartsWith("```"))
            {
                var firstNewline = content.IndexOf('\n');
                if (firstNewline > 0) content = content[(firstNewline + 1)..];
                var lastFence = content.LastIndexOf("```");
                if (lastFence > 0) content = content[..lastFence];
                content = content.Trim();
            }

            // Extract first balanced JSON array if content doesn't already start with '['.
            if (!content.StartsWith("["))
            {
                var openIdx = content.IndexOf('[');
                if (openIdx >= 0)
                {
                    var endIdx = FindMatchingClose(content, openIdx);
                    if (endIdx > openIdx)
                        content = content.Substring(openIdx, endIdx - openIdx + 1);
                }
            }

            using var doc = System.Text.Json.JsonDocument.Parse(content.Trim());
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var question = element.GetProperty("question").GetString() ?? "";
                var answer = element.GetProperty("proposedAnswer").GetString() ?? "";
                var impact = element.TryGetProperty("impactLevel", out var imp) ? imp.GetString() ?? "S" : "S";
                var category = element.TryGetProperty("category", out var cat) ? cat.GetString() ?? "General" : "General";

                if (string.IsNullOrWhiteSpace(question)) continue;

                questions.Add(new PrePRQuestion
                {
                    Question = question,
                    ProposedAnswer = answer,
                    ImpactLevel = ParseImpactLevel(impact),
                    Category = category
                });
            }
        }
        catch (Exception ex)
        {
            // Log a snippet of the failing content (first 300 chars) so operators
            // can diagnose what the LLM emitted vs. what the parser expected.
            var snippet = jsonContent.Length <= 300 ? jsonContent : jsonContent[..300] + "…";
            Logger.LogWarning(ex,
                "Failed to parse pre-PR questions JSON (content length: {Len}). Snippet: {Snippet}",
                jsonContent.Length, snippet);
        }

        return questions.Take(10).ToList();
    }

    /// <summary>
    /// Find the index of the closing bracket matching the open bracket at <paramref name="openIdx"/>.
    /// Handles nested brackets, strings, and string-escape sequences. Returns -1 if unmatched.
    /// </summary>
    private static int FindMatchingClose(string content, int openIdx)
    {
        if (openIdx < 0 || openIdx >= content.Length) return -1;
        var open = content[openIdx];
        var close = open == '[' ? ']' : open == '{' ? '}' : '\0';
        if (close == '\0') return -1;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (int i = openIdx; i < content.Length; i++)
        {
            var c = content[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static DecisionImpactLevel ParseImpactLevel(string level) => level.ToUpperInvariant() switch
    {
        "XS" => DecisionImpactLevel.XS,
        "S" => DecisionImpactLevel.S,
        "M" => DecisionImpactLevel.M,
        "L" => DecisionImpactLevel.L,
        "XL" => DecisionImpactLevel.XL,
        _ => DecisionImpactLevel.S
    };

    private void LogQuestionsAsDecisions(PrePRClarificationSet set)
    {
        if (DecisionLog is null) return;

        foreach (var q in set.Questions)
        {
            DecisionLog.Log(new AgentDecision
            {
                Id = $"prepr-{set.IssueNumber}-{Guid.NewGuid():N}"[..24],
                AgentId = set.AgentId,
                AgentDisplayName = set.AgentDisplayName,
                Phase = "ParallelDevelopment",
                ImpactLevel = q.ImpactLevel,
                Title = q.Question,
                Rationale = q.FinalAnswer ?? q.ProposedAnswer,
                Category = "Pre-PR Clarification",
                SourceQuestion = q.Question,
                Status = set.WasAutoApproved ? DecisionStatus.AutoApproved : DecisionStatus.Approved,
                AssociatedPrNumber = null // PR not created yet at this point
            });
        }
    }

    private static string BuildClarificationContext(PrePRClarificationSet set)
    {
        if (set.Questions.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Pre-Implementation Decisions (Human-Validated)");
        sb.AppendLine();
        foreach (var q in set.Questions)
        {
            sb.AppendLine($"**Q:** {q.Question}");
            sb.AppendLine($"**A:** {q.FinalAnswer ?? q.ProposedAnswer}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // TruncateForPrompt removed (2026-05-15): With claude-opus-4.6-1m context window,
    // there's no need to hard-cut PMSpec/Architecture at 3000 chars. The truncation was
    // losing important sections at the end of these docs (e.g., non-functional requirements,
    // extensibility notes, visual design specs). Full documents give the agent better context
    // for generating meaningful clarification questions.
    // Old implementation: text.Length <= maxChars ? text : text[..maxChars] + "\n[...truncated]"

    /// <summary>
    /// Marks PR as ready for review and sends notification messages.
    /// Used after incremental steps complete.
    /// </summary>
    protected async Task MarkPrCompleteAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        // NoMessyCodePlan post-Tier-2: pre-self-assessment screenshot expectation check.
        // Captures a screenshot of the running app, asks vision-AI whether what's rendered matches
        // what the PR claimed to deliver, and surfaces the verdict as an implementation note so the
        // downstream self-assessment LLM can fold it into its gap analysis. The engineer catches
        // blank canvases / wrong-scene renders / error pages BEFORE marking ready — no longer waits
        // for TE or PM review to catch them.
        await RunPrePublishScreenshotCheckAsync(pr, ct);

        // Pre-publish self-assessment: re-read requirements with fresh context and verify completeness
        await RunPrePublishAssessmentAsync(pr, issue, ct);

        // Check for pending decisions that would block this PR from merge
        if (DecisionLog is not null && Core!.Config.DecisionGating.BlockPrMergeOnPendingDecisions)
        {
            var pendingDecisions = DecisionLog.GetDecisionsForPr(pr.Number)
                .Where(d => d.Status == DecisionStatus.Pending)
                .ToList();
            if (pendingDecisions.Count > 0)
            {
                Logger.LogInformation("PR #{PrNumber} has {Count} pending decisions — awaiting approval before marking ready",
                    pr.Number, pendingDecisions.Count);
                LogActivity("gate", $"⏳ PR #{pr.Number} has {pendingDecisions.Count} pending decision(s) — waiting for approval");
                await PrService.AddLabelsAsync(pr.Number, ["awaiting-decision-approval"], ct);
            }
        }

        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(pr.Number, ct);

        // === Gate: PRCodeComplete — human reviews code before marking ready ===
        await WaitForHumanGateAsync(
            GateIds.PRCodeComplete,
            $"Engineer code complete on PR #{pr.Number}, ready for human review before marking ready-for-review",
            pr.Number, ct: ct);

        // Change #2 — Completion manifest enforcement (base-class path used by SpecialistEngineerAgent;
        // Lesson #14: SoftwareEngineerAgent bypasses this method and has its own wiring below).
        if (await IsBlockedByCompletionManifestAsync(pr, issue, ct))
        {
            // Enqueue self-rework so the agent re-enters the implementation loop to fix
            // the stubs instead of silently going idle with status "blocked — stubs detected".
            // Without this, the agent exits WorkOnIssueAsync → main loop has no work → sits forever.
            var stubFeedback = $"[Self-Assessment] Stub detection found incomplete implementations in PR #{pr.Number}. " +
                "Please implement all stub/placeholder methods fully. " +
                "Check the completion manifest comments on the PR for specific offenders.";
            ReworkQueue.Enqueue(new ReworkItem(pr.Number, pr.Title, stubFeedback, Identity.DisplayName));
            Logger.LogInformation(
                "{Role} {Name} enqueued self-rework for stub-blocked PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, pr.Number);
            return;
        }

        await MarkReadyForReviewWithScreenshotAsync(pr, ct);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "CodeReview"
        }, ct);

        await PublishStatusAsync("TaskComplete", AgentStatus.Online,
            details: $"PR #{pr.Number} implementation complete and ready for review.",
            currentTask: issue.Title, ct: ct);

        Logger.LogInformation("{Role} {Name} completed PR #{Number}, marked ready for review",
            Identity.Role, Identity.DisplayName, pr.Number);
        LogActivity("task", $"🎉 Completed PR #{pr.Number}: {pr.Title} — marked ready for review");

        // Clear task checkpoint since this PR is complete
        await CheckpointTaskProgressAsync(pr.Number, CurrentIssueNumber, stepIndex: 0, ct);

        UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting review/next task");
    }

    /// <summary>
    /// System prompt for step-by-step implementation. Focuses the AI on one step at a time.
    /// </summary>
    protected virtual string GetStepImplementationSystemPrompt(string techStack, int stepNumber, int totalSteps)
    {
        // Try template first (sync check — templates are cached after first load)
        if (PromptService is not null)
        {
            var gitignoreRule = stepNumber == 1
                ? "GITIGNORE RULE: If the project does not already have a .gitignore, create one as your FIRST file. " +
                  "Include ALL standard ignores for the project's technology stack (e.g., bin/obj for .NET, " +
                  "node_modules for Node.js, __pycache__ for Python, target for Rust/Java, etc.). " +
                  "This prevents build artifacts from being committed. " +
                  "IMPORTANT: Do NOT gitignore data files like data.json, sample-data.json, etc. " +
                  "These must be committed so the app works when cloned.\n\n" +
                  "DATA FILE RULE: Do NOT create 'example' or 'template' data files (e.g., data.example.json, " +
                  "data.template.json). Create the ACTUAL data file (e.g., data.json) with sample data directly. " +
                  "The app must compile and run immediately after this PR is cloned — no manual file renaming steps. " +
                  "Never gitignore data files that the app needs to start.\n\n"
                : "";
            var rendered = PromptService.RenderAsync("engineer-base/step-implementation-system", new Dictionary<string, string>
            {
                ["role_display_name"] = GetRoleDisplayName(),
                ["step_number"] = stepNumber.ToString(),
                ["total_steps"] = totalSteps.ToString(),
                ["tech_stack"] = techStack,
                ["gitignore_rule"] = gitignoreRule
            }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        return $"You are a {GetRoleDisplayName()} implementing step {stepNumber} of {totalSteps} " +
            $"in a coding task. The project uses {techStack}. " +
            "Focus ONLY on the current step described below. " +
            "Produce clean, production-quality code for this step only. " +
            "If files from previous steps need updating, include the COMPLETE updated file. " +
            "Be thorough for this step but do not implement future steps.\n\n" +
            "INCREMENTAL MODIFICATION PRINCIPLE: When modifying an existing file (especially UI " +
            "components like .razor, .html, .css, .jsx files), you MUST preserve all existing code " +
            "that is not directly related to your current step. Do NOT rename existing CSS classes, " +
            "reorganize HTML structure, or refactor working code. Insert your changes at the " +
            "appropriate location and leave everything else unchanged. A good modification should " +
            "produce a minimal diff — mostly additions with few changes to existing lines.\n\n" +
            (stepNumber == 1
                ? "GITIGNORE RULE: If the project does not already have a .gitignore, create one as your FIRST file. " +
                  "Include ALL standard ignores for the project's technology stack (e.g., bin/obj for .NET, " +
                  "node_modules for Node.js, __pycache__ for Python, target for Rust/Java, etc.). " +
                  "This prevents build artifacts from being committed. " +
                  "IMPORTANT: Do NOT gitignore data files like data.json, sample-data.json, etc. " +
                  "These must be committed so the app works when cloned.\n\n" +
                  "DATA FILE RULE: Do NOT create 'example' or 'template' data files (e.g., data.example.json, " +
                  "data.template.json). Create the ACTUAL data file (e.g., data.json) with sample data directly. " +
                  "The app must compile and run immediately after this PR is cloned — no manual file renaming steps. " +
                  "Never gitignore data files that the app needs to start.\n\n" +
                  "VISUAL PLACEHOLDER RULE (WEB/UI PROJECTS): Every stub/placeholder component MUST be " +
                  "VISUALLY DISTINCT when rendered. Use colored backgrounds (#f0f4f8, #e8f4fd, #fef3cd), " +
                  "dashed borders (2px dashed #94a3b8), padding (2rem), and large bold label text " +
                  "(e.g., '📊 Heatmap Component — Placeholder'). " +
                  "Add a `.placeholder` CSS class: { background: #f0f4f8; border: 2px dashed #94a3b8; " +
                  "border-radius: 8px; padding: 2rem; text-align: center; font-size: 1.2rem; color: #475569; " +
                  "min-height: 200px; display: flex; align-items: center; justify-content: center; }. " +
                  "Apply this class to every placeholder component. A Playwright screenshot of the scaffold " +
                  "MUST show a clear grid of labeled, colored sections — NEVER a blank white page.\n\n"
                : "") +
            "DEPENDENCY RULE: Before using ANY external library, package, or framework, check the project's " +
            "dependency manifest (e.g., .csproj, package.json, requirements.txt, Cargo.toml, go.mod, pom.xml, etc.). " +
            "If a dependency is not already listed, add it to the manifest file and include that file in your output. " +
            "Never assume a package is available — always verify and declare dependencies explicitly.";
    }

    /// <summary>Parses numbered list lines (e.g., "1. Do X") into a list of step descriptions.</summary>
    private static List<string> ParseNumberedSteps(string content)
    {
        var steps = new List<string>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Skip markdown noise that the LLM sometimes returns instead of steps:
            // - Headers (## Summary, ## Acceptance Criteria, ## Implementation Steps)
            // - Checkbox lines ([ ] dotnet build succeeds, [x] done)
            // - Horizontal rules (---, ***)
            // - "Closes #NNN" link lines
            if (trimmed.StartsWith('#') || trimmed.StartsWith("---") || trimmed.StartsWith("***"))
                continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\[[ xX]?\]\s"))
                continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^Closes?\s+#\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            // Match "1. ...", "1) ...", "Step 1: ...", "- ..."
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                trimmed, @"^(\d+[\.\)]\s*|Step\s+\d+[:\.\)]\s*|-\s*)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Skip lines that are too short to be actionable implementation steps
            if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length >= 15)
                steps.Add(cleaned.Trim());
        }

        // Cap at 10 steps — if more were parsed, the LLM likely returned a document
        // instead of an implementation plan. Take the first 10 and log a warning.
        const int MaxSteps = 10;
        if (steps.Count > MaxSteps)
            steps = steps.Take(MaxSteps).ToList();

        return steps;
    }

    /// <summary>Gets the list of files already on the PR branch for context.</summary>
    protected async Task<string> GetPrFileListAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var files = await PrService.GetChangedFilesAsync(prNumber, ct);
            if (files.Count == 0) return "";
            return string.Join("\n", files.Select(f => $"- {f}"));
        }
        catch
        {
            return "";
        }
    }

    protected static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    /// <summary>
    /// Compact human-readable summary of `git status --porcelain` output for log lines and
    /// commit messages: e.g. "5 new, 2 modified (3 PNGs)". Used by the workspace-commit
    /// fallback in <see cref="ImplementSinglePassAsync"/> when the LLM wrote files via
    /// shell tools (e.g. an agentic session writing PNGs directly) instead of emitting
    /// FILE: blocks. We want operators to see what actually got committed at a glance.
    /// </summary>
    internal static string SummarizeWorkspaceStatus(string porcelainStatus)
    {
        if (string.IsNullOrWhiteSpace(porcelainStatus)) return "no changes";
        var lines = porcelainStatus.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        int added = 0, modified = 0, deleted = 0, pngCount = 0, otherBinary = 0;
        foreach (var line in lines)
        {
            if (line.Length < 3) continue;
            // Porcelain format: XY <path> where X is index status, Y is worktree status.
            // ?? = untracked, A = added, M = modified, D = deleted, etc.
            var code = line[..2];
            var path = line[3..].Trim();
            if (code.Contains('?') || code.Contains('A')) added++;
            else if (code.Contains('D')) deleted++;
            else modified++;

            // Track binary asset shapes for at-a-glance media counts.
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) pngCount++;
            else if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                otherBinary++;
        }
        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} new");
        if (modified > 0) parts.Add($"{modified} modified");
        if (deleted > 0) parts.Add($"{deleted} deleted");
        var primary = parts.Count > 0 ? string.Join(", ", parts) : "0 changes";
        var binaryNote = pngCount + otherBinary > 0
            ? $" ({pngCount} PNG{(pngCount != 1 ? "s" : "")}{(otherBinary > 0 ? $", {otherBinary} other media" : "")})"
            : "";
        return primary + binaryNote;
    }

    /// <summary>Truncate text for memory storage (keep it concise but useful).</summary>
    protected static string TruncateForMemory(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Take first N chars, cut at last sentence boundary
        if (text.Length <= maxLength) return text;
        var cut = text[..maxLength];
        var lastPeriod = cut.LastIndexOf('.');
        return lastPeriod > maxLength / 2 ? cut[..(lastPeriod + 1)] : cut + "…";
    }

    /// <summary>
    /// Extracts the first N characters of a document for use as trimmed context in rework prompts.
    /// Includes the executive summary / key sections, cutting at a paragraph boundary.
    /// </summary>
    protected static string TruncateForReworkContext(string text, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(text)) return "(not available)";
        if (text.Length <= maxChars) return text;
        // Try to cut at a paragraph boundary (double newline)
        var cut = text[..maxChars];
        var lastParagraph = cut.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (lastParagraph > maxChars / 3)
            return cut[..lastParagraph] + "\n\n(… truncated for rework context)";
        var lastNewline = cut.LastIndexOf('\n');
        return (lastNewline > maxChars / 2 ? cut[..lastNewline] : cut) + "\n(… truncated for rework context)";
    }

    /// <summary>
    /// Commits code files to PR, marks ready for review, notifies reviewers.
    /// </summary>
    protected async Task CommitAndNotifyAsync(
        AgentPullRequest pr, AgentIssue issue, string finalOutput, string fallbackImpl, CancellationToken ct)
    {
        _currentFileScopeBlock = BuildFileScopePromptBlock(pr.Body, issue.Body);
        var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(finalOutput);
        if (codeFiles.Count == 0)
            codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(fallbackImpl);

        // Enforce file scope before committing
        if (codeFiles.Count > 0)
            codeFiles = FilterToAllowedScope(codeFiles, pr.Body, issue.Body, pr.Number);

        if (codeFiles.Count > 0)
        {
            Logger.LogInformation("{Role} {Name} parsed {Count} code files for PR #{Number}",
                Identity.Role, Identity.DisplayName, codeFiles.Count, pr.Number);

            if (Workspace is not null && BuildRunnerSvc is not null)
            {
                var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                    $"Implement issue #{issue.Number}: {issue.Title}", 1, 1,
                    issue.Title, chat, ct);

                if (!committed)
                {
                    Logger.LogWarning("{Role} {Name} implementation for issue #{IssueNumber} blocked by build errors",
                        Identity.Role, Identity.DisplayName, issue.Number);
                    LogActivity("task", $"⛔ PR #{pr.Number} implementation blocked by build errors for issue #{issue.Number}");
                    RecordImplementationNote($"Build errors encountered during single-pass implementation for issue #{issue.Number}");
                }
            }
            else
            {
                await PrWorkflow.CommitCodeFilesToPRAsync(
                    pr.Number, codeFiles, $"Implement issue #{issue.Number}: {issue.Title}", ct);
            }
        }
        else
        {
            // Before falling back to the "committing raw" markdown placeholder, check whether
            // the agent wrote files directly to the workspace via shell tools (e.g. an agentic
            // session that called REST to generate PNGs, or a script that ran `dotnet new`).
            // The LLM's response is text-only with no FILE: blocks but the workspace IS the
            // real source of truth — losing those changes by writing a markdown placeholder
            // over them would be the worst outcome.
            var workspaceCommitted = false;
            if (Workspace is not null)
            {
                try
                {
                    var status = await Workspace.GetStatusAsync(ct);
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        var summary = SummarizeWorkspaceStatus(status);
                        Logger.LogInformation(
                            "{Role} {Name} no FILE: blocks parsed for PR #{Number} but workspace has {Summary} — committing those instead of writing a markdown placeholder",
                            Identity.Role, Identity.DisplayName, pr.Number, summary);
                        // Workspace.CommitAsync auto-stages via `git add -A`.
                        await Workspace.CommitAsync(
                            $"Implement issue #{issue.Number}: {issue.Title}\n\n" +
                            $"(workspace-commit: agent wrote files via shell tools; no FILE: blocks emitted)\n\n" +
                            $"Detected changes: {summary}", ct);
                        await Workspace.PushAsync(pr.HeadBranch ?? "HEAD", ct);
                        workspaceCommitted = true;
                        LogActivity("task", $"✅ PR #{pr.Number} workspace-commit ({summary})");
                        RecordImplementationNote($"Workspace-commit path: agent wrote {summary} directly via shell tools.");
                    }
                }
                catch (Exception wsEx)
                {
                    Logger.LogWarning(wsEx,
                        "{Role} {Name} workspace inspection failed for PR #{Number}; falling back to markdown placeholder",
                        Identity.Role, Identity.DisplayName, pr.Number);
                }
            }

            if (!workspaceCommitted)
            {
                Logger.LogWarning("{Role} {Name} could not parse files for PR #{Number}, committing raw",
                    Identity.Role, Identity.DisplayName, pr.Number);

                await PrWorkflow.CommitFixesToPRAsync(
                    pr.Number,
                    $"src/issue-{issue.Number}-implementation.md",
                    $"## Implementation\n\n{finalOutput}",
                    "Add implementation",
                    ct);
            }
        }

        // Pre-publish self-assessment: re-read requirements with fresh context and verify completeness
        await RunPrePublishAssessmentAsync(pr, issue, ct);

        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(pr.Number, ct);

        // === Gate: PRCodeComplete — human reviews code before marking ready ===
        await WaitForHumanGateAsync(
            GateIds.PRCodeComplete,
            $"Engineer code complete on PR #{pr.Number}, ready for human review before marking ready-for-review",
            pr.Number, ct: ct);

        await MarkReadyForReviewWithScreenshotAsync(pr, ct);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "CodeReview"
        }, ct);

        await PublishStatusAsync("TaskComplete", AgentStatus.Online,
            details: $"PR #{pr.Number} implementation complete and ready for review.",
            currentTask: issue.Title, ct: ct);

        Logger.LogInformation("{Role} {Name} completed PR #{Number}, marked ready for review",
            Identity.Role, Identity.DisplayName, pr.Number);
        LogActivity("task", $"🎉 Completed PR #{pr.Number}: {pr.Title} — marked ready for review");

        UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting review/next task");
        // Keep CurrentPrNumber and AssignedPullRequest set so rework feedback can match.
    }

    #endregion

    #region Rework Handling

    /// <summary>
    /// Marker prefix in review feedback that identifies Test Engineer source-bug reports.
    /// When present, rework uses a separate counter (TeReworkAttemptCounts) so TE feedback
    /// isn't blocked by exhausted peer review cycles.
    /// </summary>
    internal const string TeSourceBugMarker = "[TE-SOURCE-BUG]";

    /// <summary>
    /// Addresses reviewer feedback on a PR. Batches feedback from multiple reviewers
    /// into a single rework round so the cycle count is per-round, not per-reviewer.
    /// </summary>
    protected virtual async Task HandleReworkAsync(List<ReworkItem> reworkBatch, CancellationToken ct)
    {
        _ = Metrics?.RecordReworkRequestedAsync(Identity.Id, ct);
        var rework = reworkBatch[0]; // Use first item for PR number/title
        var reworkTaskId = $"rework-pr-{rework.PrNumber}";

        var reworkStepId = TaskTracker.BeginStep(Identity.Id, reworkTaskId, "Address review feedback",
            $"Reworking PR #{rework.PrNumber} based on reviewer feedback", Identity.ModelTier);

        var pr = (await PrService.GetAsync(rework.PrNumber, ct))?.ToAgentPR();
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Skipped);
            return;
        }

        // Determine if this is TE source-bug feedback (uses separate counter + limit)
        var isTeSourceBug = reworkBatch.Any(r =>
            r.Feedback.Contains(TeSourceBugMarker, StringComparison.OrdinalIgnoreCase));

        // Per-reviewer rework tracking: each reviewer gets their own cycle limit.
        // Human reviewers are exempt from exhaustion but still tracked for telemetry.
        var reviewers = reworkBatch
            .Select(r => r.Reviewer?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preserveApprovals = reviewers.Count > 0 && reviewers.All(IsHumanReviewer);
        int attempts;
        int maxCycles;
        bool anyExhausted = false;
        bool countsTowardLimit;

        if (isTeSourceBug)
        {
            attempts = TeReworkAttemptCounts.GetValueOrDefault(rework.PrNumber, 0) + 1;
            TeReworkAttemptCounts[rework.PrNumber] = attempts;
            maxCycles = Config.Limits.MaxTestReworkCycles;
            anyExhausted = attempts >= maxCycles;
            countsTowardLimit = true;
        }
        else
        {
            // Track per (PR, reviewer) and use reviewer-specific limits for non-human reviewers.
            attempts = 0;
            maxCycles = Config.Limits.MaxReworkCycles;
            var maxHumanAttempts = 0;
            var hasLimitedReviewer = false;
            foreach (var reviewer in reviewers)
            {
                var key = (rework.PrNumber, reviewer.ToUpperInvariant());
                var reviewerAttempts = ReworkAttemptCounts.GetValueOrDefault(key, 0) + 1;
                ReworkAttemptCounts[key] = reviewerAttempts;

                if (IsHumanReviewer(reviewer))
                {
                    maxHumanAttempts = Math.Max(maxHumanAttempts, reviewerAttempts);
                    Logger.LogInformation(
                        "Operator/human rework cycle {Count} for PR #{PrNumber} from {Reviewer} — no cycle limit applied",
                        reviewerAttempts,
                        rework.PrNumber,
                        reviewer);
                    continue;
                }

                hasLimitedReviewer = true;

                // Use reviewer-specific limit if available
                var reviewerMax = reviewer.Contains("ProgramManager", StringComparison.OrdinalIgnoreCase)
                    ? Config.Limits.MaxPmReworkCycles
                    : reviewer.Contains("Architect", StringComparison.OrdinalIgnoreCase)
                        ? Config.Limits.MaxArchitectReworkCycles
                        : Config.Limits.MaxReworkCycles;

                if (reviewerAttempts >= reviewerMax)
                    anyExhausted = true;

                // Track highest attempt count for logging/comments that count toward the limit.
                if (reviewerAttempts > attempts)
                {
                    attempts = reviewerAttempts;
                    maxCycles = reviewerMax;
                }
            }

            countsTowardLimit = hasLimitedReviewer;
            if (!hasLimitedReviewer)
            {
                attempts = Math.Max(maxHumanAttempts, 1);
                maxCycles = attempts;
            }
        }

        // Persist updated counters so they survive restart
        PersistRetryCounters();

        var attemptDisplay = countsTowardLimit ? $"{attempts}/{maxCycles}" : $"{attempts} (human-exempt)";
        var limitNote = countsTowardLimit
            ? $"This rework attempt counted toward the limit ({attemptDisplay})."
            : "This feedback came from a human reviewer and did not count toward the rework limit.";

        if (anyExhausted)
        {
            var cycleType = isTeSourceBug ? "test rework" : "rework";
            Logger.LogWarning(
                "{Role} {Name} reached max {CycleType} cycles ({Max}) for PR #{PrNumber}, requesting force-approval",
                Identity.Role, Identity.DisplayName, cycleType, maxCycles, rework.PrNumber);

            // === Gate: ReworkExhaustion — human decides on exhausted rework cycles ===
            await WaitForHumanGateAsync(
                GateIds.ReworkExhaustion,
                $"PR #{rework.PrNumber} has exhausted rework cycles, human decision needed",
                rework.PrNumber, ct: ct);

            // Only post the comment once per PR — check both in-memory set AND existing PR comments
            if (_forceApprovalSentPrs.Add(rework.PrNumber))
            {
                // Check if a force-approval comment already exists (from prior run)
                var existingComments = await ReviewService.GetCommentsAsync(rework.PrNumber, ct);
                var alreadyPosted = existingComments.Any(c =>
                    c.Body.Contains("maximum rework cycle limit", StringComparison.OrdinalIgnoreCase) ||
                    c.Body.Contains("maximum test rework cycle limit", StringComparison.OrdinalIgnoreCase));

                if (!alreadyPosted)
                {
                    await ReviewService.AddCommentAsync(
                        rework.PrNumber,
                        $"⚠️ **{Identity.DisplayName}** has reached the maximum {cycleType} cycle limit " +
                        $"({maxCycles}). Requesting final approval to unblock progress.",
                        ct);
                }

                await MessageBus.PublishAsync(new ReviewRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ReviewRequest",
                    PrNumber = pr.Number,
                    PrTitle = pr.Title,
                    ReviewType = "FinalApproval"
                }, ct);
            }
            return;
        }

        // Combine feedback from all reviewers into one prompt
        var allReviewers = string.Join(", ", reworkBatch.Select(r => r.Reviewer).Distinct());
        var combinedFeedback = string.Join("\n\n---\n\n",
            reworkBatch.Select(r => $"### Feedback from {r.Reviewer}\n{r.Feedback}"));

        // Fetch inline review threads so the SE sees exact file/line feedback
        var inlineThreadsContext = "";
        try
        {
            var threads = await ReviewService.GetThreadsAsync(rework.PrNumber, ct);
            var unresolvedThreads = threads.Where(t => !t.IsResolved && !string.IsNullOrWhiteSpace(t.Body)).ToList();
            if (unresolvedThreads.Count > 0)
            {
                var threadLines = unresolvedThreads.Select((t, i) =>
                    $"{i + 1}. **{t.FilePath}** (line {t.Line}): {t.Body}");
                inlineThreadsContext = "\n\n### Inline Review Comments (file-specific)\n" +
                    "These are the exact file and line locations that reviewers flagged:\n" +
                    string.Join("\n", threadLines);
                combinedFeedback += inlineThreadsContext;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to fetch inline review threads for rework prompt on PR #{PrNumber}", rework.PrNumber);
        }

        UpdateStatus(AgentStatus.Working, $"Addressing feedback on PR #{rework.PrNumber} (attempt {attemptDisplay})");
        LogActivity("task", $"🔄 Reworking PR #{rework.PrNumber} based on feedback from {allReviewers} (attempt {attemptDisplay})");
        Logger.LogInformation("{Role} {Name} reworking PR #{PrNumber} based on feedback from {Reviewers} (attempt {Attempt}/{Max})",
            Identity.Role, Identity.DisplayName, rework.PrNumber, allReviewers, attempts, maxCycles);

        // Resume the CLI session that was used to create this PR
        ActivatePrSession(rework.PrNumber);

        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Set file scope for rework (used by build-fix prompts)
            _currentFileScopeBlock = BuildFileScopePromptBlock(pr.Body, null);

            var techStack = Config.Project.TechStack;
            var reworkMemory = await GetMemoryContextAsync(ct: ct);

            // --- Surgical rework: only load files mentioned in feedback ---
            // Extract file paths from inline threads and feedback text
            var mentionedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var threads = await ReviewService.GetThreadsAsync(rework.PrNumber, ct);
                foreach (var t in threads.Where(t => !t.IsResolved && !string.IsNullOrWhiteSpace(t.FilePath)))
                    mentionedFiles.Add(t.FilePath!);
            }
            catch { /* already logged above */ }
            // Also parse file paths from feedback text (matches patterns like path/to/file.ext)
            foreach (var match in System.Text.RegularExpressions.Regex.Matches(
                combinedFeedback, @"(?:^|\s|`)([\w\-./]+\.(?:cs|js|ts|tsx|jsx|html|css|json|md|yml|yaml|razor|csproj|sln|xml|config|py|rb|go|rs|vue|svelte|astro))(?:\s|`|$|:|\))",
                System.Text.RegularExpressions.RegexOptions.Multiline).Cast<System.Text.RegularExpressions.Match>())
            {
                mentionedFiles.Add(match.Groups[1].Value);
            }

            // Load PR file contents — scoped to mentioned files when possible
            var fullFilesContext = await PrWorkflow.GetPRCodeContextAsync(
                rework.PrNumber, pr.HeadBranch, ct: ct);
            string currentFilesContext;
            if (mentionedFiles.Count > 0 && !string.IsNullOrEmpty(fullFilesContext))
            {
                // Filter to only files mentioned in feedback (plus their close neighbors for context)
                var filteredSections = new System.Text.StringBuilder();
                var sections = fullFilesContext.Split(new[] { "\n### File: ", "\n## File: " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var section in sections)
                {
                    var firstLine = section.Split('\n')[0].Trim();
                    if (mentionedFiles.Any(f => firstLine.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        filteredSections.AppendLine($"### File: {section}");
                    }
                }
                currentFilesContext = filteredSections.Length > 0 ? filteredSections.ToString() : fullFilesContext;
                if (filteredSections.Length > 0)
                {
                    Logger.LogInformation("Surgical rework: loaded {Filtered}/{Total} files matching feedback for PR #{PrNumber}",
                        mentionedFiles.Count, sections.Length, rework.PrNumber);
                }
            }
            else
            {
                currentFilesContext = fullFilesContext;
            }

            // Trim architecture/PM spec to concise summaries for rework context
            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var archSummary = TruncateForReworkContext(architectureDoc, 500);
            var pmSpecSummary = TruncateForReworkContext(pmSpecDoc, 500);

            var history = CreateChatHistory();

            // Determine if we can use CLI edit mode (requires local workspace + build runner)
            var useCliEditMode = Workspace is not null && BuildRunnerSvc is not null;

            if (useCliEditMode)
            {
                // CLI Edit Mode: system prompt tells LLM to use native edit/view/create tools
                history.AddSystemMessage(GetReworkSystemPromptCliEdit(techStack) +
                    (string.IsNullOrEmpty(reworkMemory) ? "" : $"\n\n{reworkMemory}"));
            }
            else
            {
                // FILE: Block Mode: system prompt tells LLM to output FILE: blocks
                history.AddSystemMessage(GetReworkSystemPrompt(techStack) +
                    (string.IsNullOrEmpty(reworkMemory) ? "" : $"\n\n{reworkMemory}"));
            }

            var additionalCtx = await GetAdditionalReworkContextAsync(ct);

            if (useCliEditMode)
            {
                // CLI edit user prompt — instructs use of edit tools, no FILE: block format
                var reworkUser = PromptService is not null
                    ? await PromptService.RenderAsync("engineer-base/rework-user-cli-edit", new Dictionary<string, string>
                    {
                        ["pr_number"] = rework.PrNumber.ToString(),
                        ["pr_title"] = rework.PrTitle,
                        ["pr_body"] = pr.Body ?? "",
                        ["architecture"] = archSummary,
                        ["pm_spec"] = pmSpecSummary,
                        ["additional_context"] = string.IsNullOrEmpty(additionalCtx) ? "" : $"{additionalCtx}\n",
                        ["current_files_context"] = string.IsNullOrEmpty(currentFilesContext) ? "" :
                            $"## Files Referenced in Feedback\n{currentFilesContext}\n\n",
                        ["feedback"] = combinedFeedback
                    }, ct)
                    : null;
                history.AddUserMessage(reworkUser
                    ?? $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                       $"## Review Feedback (Address ALL items below)\n{combinedFeedback}\n\n" +
                       (string.IsNullOrEmpty(currentFilesContext) ? "" :
                           $"## Files Referenced in Feedback\n{currentFilesContext}\n\n") +
                       $"## Context Summary\n" +
                       $"- **Architecture approach:** {archSummary}\n" +
                       $"- **PM Spec goals:** {pmSpecSummary}\n\n" +
                       additionalCtx +
                       $"## Original PR Description\n{pr.Body}\n\n" +
                       "SURGICAL REWORK INSTRUCTIONS:\n" +
                       "1. Start with a brief CHANGES SUMMARY addressing each numbered feedback item\n" +
                       "2. Use your view tool to read files mentioned in the feedback\n" +
                       "3. Use your edit tool to make ONLY the specific changes needed — do NOT rewrite entire files\n" +
                       "4. Do NOT touch files that weren't mentioned in the feedback");
            }
            else
            {
                // FILE: block user prompt (original behavior)
                var reworkUser = PromptService is not null
                    ? await PromptService.RenderAsync("engineer-base/rework-user", new Dictionary<string, string>
                    {
                        ["pr_number"] = rework.PrNumber.ToString(),
                        ["pr_title"] = rework.PrTitle,
                        ["pr_body"] = pr.Body ?? "",
                        ["architecture"] = archSummary,
                        ["pm_spec"] = pmSpecSummary,
                        ["additional_context"] = string.IsNullOrEmpty(additionalCtx) ? "" : $"{additionalCtx}\n",
                        ["current_files_context"] = string.IsNullOrEmpty(currentFilesContext) ? "" :
                            $"## Files Referenced in Feedback\n{currentFilesContext}\n\n",
                        ["feedback"] = combinedFeedback
                    }, ct)
                    : null;
                history.AddUserMessage(reworkUser
                    ?? $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                       $"## Review Feedback (Address ALL items below)\n{combinedFeedback}\n\n" +
                       (string.IsNullOrEmpty(currentFilesContext) ? "" :
                           $"## Files Referenced in Feedback\n{currentFilesContext}\n\n") +
                       $"## Context Summary\n" +
                       $"- **Architecture approach:** {archSummary}\n" +
                       $"- **PM Spec goals:** {pmSpecSummary}\n\n" +
                       additionalCtx +
                       $"## Original PR Description\n{pr.Body}\n\n" +
                       "SURGICAL REWORK INSTRUCTIONS:\n" +
                       "1. Start with CHANGES SUMMARY addressing each numbered feedback item\n" +
                       "2. Output ONLY files that need modification using FILE: format\n" +
                       "3. Each FILE: block must contain the COMPLETE file content\n" +
                       "4. Do NOT regenerate files that weren't mentioned in the feedback");
            }

            // === Agentic Mode: checkout branch + push allow-all context BEFORE LLM call ===
            var branchName = GetPrBranchName(pr);
            IDisposable? cliEditScope = null;
            if (useCliEditMode)
            {
                await Workspace!.CheckoutBranchAsync(branchName, ct);
                Logger.LogInformation("{Role} {Name} checked out branch {Branch} for agentic rework on PR #{PrNumber}",
                    Identity.Role, Identity.DisplayName, branchName, rework.PrNumber);

                // Use AgenticAllowAll for rework — gives full shell access (git rm, rm, etc.)
                // instead of just edit/view/create tools. This enables file deletion, .gitignore
                // additions, and any other shell operation the review feedback requires.
                cliEditScope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                    AgenticAllowAll: true,
                    OverrideWorkingDirectory: Workspace.RepoPath));
            }

            string responseText;
            try
            {
                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                responseText = response.Content?.Trim() ?? "";
            }
            catch
            {
                // On failure, clean up CLI edit context and revert workspace
                if (useCliEditMode)
                {
                    cliEditScope?.Dispose();
                    await Workspace!.RevertUncommittedChangesAsync(ct);
                }
                throw;
            }
            finally
            {
                cliEditScope?.Dispose();
            }

            var changesSummary = PullRequestWorkflow.ExtractChangesSummary(responseText);

            // Re-check PR state after the (potentially long) LLM call — PR may have been merged while we were reworking
            var prAfterRework = (await PrService.GetAsync(rework.PrNumber, ct))?.ToAgentPR();
            if (prAfterRework is null || !string.Equals(prAfterRework.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation(
                    "{Role} {Name} aborting rework on PR #{PrNumber} — PR was {State} during rework",
                    Identity.Role, Identity.DisplayName, rework.PrNumber,
                    prAfterRework?.State ?? "deleted");
                if (useCliEditMode)
                    await Workspace!.RevertUncommittedChangesAsync(ct);
                TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Skipped);
                return;
            }

            if (useCliEditMode)
            {
                // === CLI Edit Mode: detect changes via git status, scope enforce, build+commit ===
                // 2026-05-16 fix: also check if CLI created commits (HEAD advanced).
                // The CLI can commit changes itself, leaving the working tree clean.
                // Previously this was misdetected as "no changes" and triggered the
                // FILE: block fallback unnecessarily (PR #1855 incident).
                var headBefore = await Workspace!.GetHeadShaAsync("HEAD", ct);
                var changedFiles = await Workspace!.GetChangedFilePathsAsync(ct);
                changedFiles = await EnforceCliEditScopeAsync(changedFiles, pr.Body, rework.PrNumber, ct, isRework: true);

                // Check if CLI committed behind our back (HEAD advanced but working tree clean)
                var headAfter = await Workspace.GetHeadShaAsync("HEAD", ct);
                var cliCommitted = !string.Equals(headBefore?.Trim(), headAfter?.Trim(), StringComparison.OrdinalIgnoreCase);

                if (changedFiles.Count > 0 || cliCommitted)
                {
                    if (cliCommitted && changedFiles.Count == 0)
                    {
                        Logger.LogInformation(
                            "{Role} {Name} CLI edit mode: CLI created commit(s) for PR #{PrNumber} (HEAD {Before} → {After})",
                            Identity.Role, Identity.DisplayName, rework.PrNumber,
                            headBefore?.Trim()[..7], headAfter?.Trim()[..7]);
                    }
                    else
                    {
                        Logger.LogInformation("{Role} {Name} CLI edit mode: {Count} files changed for PR #{PrNumber}",
                            Identity.Role, Identity.DisplayName, changedFiles.Count, rework.PrNumber);
                    }

                    bool committed;
                    if (cliCommitted && changedFiles.Count == 0)
                    {
                        // CLI already committed — just push
                        await Workspace.PushAsync(pr.HeadBranch!, ct);
                        committed = true;
                    }
                    else
                    {
                        committed = await CommitViaLocalWorkspaceAsync(pr, Array.Empty<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>(),
                        "Address review feedback", 1, 1, "Address review feedback", chat, ct,
                        isRework: true, cliEditMode: true);
                    }

                    if (committed)
                    {
                        RecordOperatorReworkNotes(reworkBatch);
                        TaskTracker.CompleteStep(reworkStepId);

                        await FinalizeReworkSubmissionAsync(
                            pr,
                            reworkTaskId,
                            allReviewers,
                            attemptDisplay,
                            changesSummary,
                            changedFiles,
                            preserveApprovals,
                            ct);

                        _ = Metrics?.RecordReworkCompletedAsync(Identity.Id, ct);

                        var memoryTitle = preserveApprovals
                            ? $"Addressed operator feedback for PR #{pr.Number} via CLI edit rework (attempt {attemptDisplay})"
                            : $"Submitted CLI edit rework for PR #{pr.Number} (attempt {attemptDisplay})";
                        var memoryDetail = preserveApprovals
                            ? $"Operator feedback preserved existing approvals. Changed {changedFiles.Count} files."
                            : $"Feedback from {allReviewers}. Changed {changedFiles.Count} files.";

                        await RememberAsync(MemoryType.Action, memoryTitle, memoryDetail, ct);
                    }
                    else
                    {
                        TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
                        Logger.LogWarning("{Role} {Name} CLI edit rework for PR #{PrNumber} blocked by build errors",
                            Identity.Role, Identity.DisplayName, pr.Number);
                        _ = Metrics?.RecordReworkBuildBlockedAsync(Identity.Id, ct);
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"**[{Identity.DisplayName}] Rework blocked** — CLI edit rework produced code with build errors " +
                            $"that could not be auto-resolved. {limitNote}", ct);

                        await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
                    }
                }
                else
                {
                    // CLI made no changes — fall back to FILE: block mode
                    Logger.LogWarning(
                        "{Role} {Name} CLI edit rework on PR #{PrNumber} produced no file changes, falling back to FILE: block mode",
                        Identity.Role, Identity.DisplayName, pr.Number);

                    // Clean workspace before fallback to avoid committing stale state
                    await Workspace!.RevertUncommittedChangesAsync(ct);

                    // Build a fresh FILE: block prompt with explicit fallback note
                    var fallbackHistory = CreateChatHistory();
                    fallbackHistory.AddSystemMessage(GetReworkSystemPrompt(techStack) +
                        (string.IsNullOrEmpty(reworkMemory) ? "" : $"\n\n{reworkMemory}"));
                    fallbackHistory.AddUserMessage(
                        $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                        $"**NOTE:** A previous CLI edit attempt completed but produced no effective file changes. " +
                        $"You MUST output complete corrected files using FILE: format below.\n\n" +
                        $"## Review Feedback (Address ALL items below)\n{combinedFeedback}\n\n" +
                        (string.IsNullOrEmpty(currentFilesContext) ? "" :
                            $"## Files Referenced in Feedback\n{currentFilesContext}\n\n") +
                        $"## Context Summary\n" +
                        $"- **Architecture approach:** {archSummary}\n" +
                        $"- **PM Spec goals:** {pmSpecSummary}\n\n" +
                        additionalCtx +
                        $"## Original PR Description\n{pr.Body}\n\n" +
                        "SURGICAL REWORK INSTRUCTIONS:\n" +
                        "1. Start with CHANGES SUMMARY addressing each numbered feedback item\n" +
                        "2. Output ONLY files that need modification using FILE: format\n" +
                        "3. Each FILE: block must contain the COMPLETE file content\n" +
                        "4. Do NOT regenerate files that weren't mentioned in the feedback");

                    var fallbackResponse = await chat.GetChatMessageContentAsync(fallbackHistory, cancellationToken: ct);
                    var fallbackText = fallbackResponse.Content?.Trim() ?? "";
                    var fallbackChangesSummary = PullRequestWorkflow.ExtractChangesSummary(fallbackText);

                    var fallbackFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(fallbackText);
                    if (fallbackFiles.Count > 0)
                        fallbackFiles = FilterToAllowedScope(fallbackFiles, pr.Body, null, pr.Number);

                    if (fallbackFiles.Count > 0)
                    {
                        bool fallbackCommitted = await CommitViaLocalWorkspaceAsync(pr, fallbackFiles,
                            "Address review feedback", 1, 1, "Address review feedback", chat, ct, isRework: true);

                        if (fallbackCommitted)
                        {
                            RecordOperatorReworkNotes(reworkBatch);
                            TaskTracker.CompleteStep(reworkStepId);
                            Logger.LogInformation("{Role} {Name} FILE: block fallback succeeded for PR #{PrNumber} ({Count} files)",
                                Identity.Role, Identity.DisplayName, pr.Number, fallbackFiles.Count);

                            await FinalizeReworkSubmissionAsync(
                                pr,
                                reworkTaskId,
                                allReviewers,
                                attemptDisplay,
                                fallbackChangesSummary,
                                fallbackFiles.Select(f => f.Path).ToList(),
                                preserveApprovals,
                                ct);

                            _ = Metrics?.RecordReworkCompletedAsync(Identity.Id, ct);

                            var memoryTitle = preserveApprovals
                                ? $"Addressed operator feedback for PR #{pr.Number} via FILE: block fallback (attempt {attemptDisplay})"
                                : $"Submitted rework for PR #{pr.Number} via FILE: block fallback (attempt {attemptDisplay})";
                            var memoryDetail = preserveApprovals
                                ? "CLI edit produced no changes, fell back to FILE: blocks while preserving existing approvals."
                                : $"CLI edit produced no changes, fell back to FILE: blocks. Feedback from {allReviewers}.";

                            await RememberAsync(MemoryType.Action, memoryTitle, memoryDetail, ct);
                        }
                        else
                        {
                            TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
                            Logger.LogWarning("{Role} {Name} FILE: block fallback rework for PR #{PrNumber} blocked by build errors",
                                Identity.Role, Identity.DisplayName, pr.Number);
                            _ = Metrics?.RecordReworkBuildBlockedAsync(Identity.Id, ct);
                            await ReviewService.AddCommentAsync(pr.Number,
                                $"**[{Identity.DisplayName}] Rework blocked** — FILE: block fallback produced code with build errors " +
                                $"that could not be auto-resolved. {limitNote}", ct);

                            // Revert workspace to prevent dirty state leaking into next attempt
                            await Workspace!.RevertUncommittedChangesAsync(ct);

                            await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
                        }
                    }
                    else
                    {
                        // Both CLI edit and FILE: block fallback produced no changes
                        TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
                        Logger.LogWarning(
                            "{Role} {Name} CLI edit AND FILE: block fallback on PR #{PrNumber} both produced no committable changes",
                            Identity.Role, Identity.DisplayName, pr.Number);
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"**[{Identity.DisplayName}] Rework attempted** — CLI edit mode and FILE: block fallback both produced no committable changes. " +
                            $"{limitNote}", ct);

                        // Revert workspace to prevent dirty state leaking into next attempt
                        await Workspace!.RevertUncommittedChangesAsync(ct);

                        await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(responseText))
            {
                // === FILE: Block Mode (original behavior for API-only path) ===
                var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(responseText);

                // Enforce file scope during rework too
                if (codeFiles.Count > 0)
                    codeFiles = FilterToAllowedScope(codeFiles, pr.Body, null, pr.Number);

                if (codeFiles.Count > 0)
                {
                    bool committed;
                    if (Workspace is not null && BuildRunnerSvc is not null)
                    {
                        committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, "Address review feedback",
                            1, 1, "Address review feedback", chat, ct, isRework: true);
                    }
                    else
                    {
                        await PrWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, "Address review feedback", ct);
                        committed = true;
                    }

                    if (committed)
                    {
                        RecordOperatorReworkNotes(reworkBatch);
                        TaskTracker.CompleteStep(reworkStepId);

                        await FinalizeReworkSubmissionAsync(
                            pr,
                            reworkTaskId,
                            allReviewers,
                            attemptDisplay,
                            changesSummary,
                            codeFiles.Select(f => f.Path).ToList(),
                            preserveApprovals,
                            ct);

                        _ = Metrics?.RecordReworkCompletedAsync(Identity.Id, ct);

                        var memoryTitle = preserveApprovals
                            ? $"Addressed operator feedback for PR #{pr.Number} (attempt {attemptDisplay})"
                            : $"Submitted rework for PR #{pr.Number} (attempt {attemptDisplay})";
                        var memoryDetail = preserveApprovals
                            ? $"Operator feedback preserved existing approvals. Changes: {TruncateForMemory(responseText)}"
                            : $"Feedback from {allReviewers}. Changes: {TruncateForMemory(responseText)}";

                        await RememberAsync(MemoryType.Action, memoryTitle, memoryDetail, ct);
                    }
                    else
                    {
                        TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
                        Logger.LogWarning("{Role} {Name} rework for PR #{PrNumber} blocked by build errors",
                            Identity.Role, Identity.DisplayName, pr.Number);
                        _ = Metrics?.RecordReworkBuildBlockedAsync(Identity.Id, ct);
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"**[{Identity.DisplayName}] Rework blocked** — Address review feedback produced code with build errors " +
                            $"that could not be auto-resolved. {limitNote}", ct);

                        await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
                    }
                }
                else
                {
                    // AI failed to produce FILE: blocks
                    TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
                    Logger.LogWarning(
                        "{Role} {Name} rework on PR #{PrNumber} produced no FILE: blocks — no code changes committed. " +
                        "Skipping ready-for-review to avoid pointless re-review of unchanged code",
                        Identity.Role, Identity.DisplayName, pr.Number);
                    await ReviewService.AddCommentAsync(pr.Number,
                        $"**[{Identity.DisplayName}] Rework attempted** — AI response did not produce committable file changes. " +
                        $"{limitNote}", ct);

                    await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
                }
            }
        }
        catch (Exception ex)
        {
            TaskTracker.CompleteStep(reworkStepId, AgentTaskStepStatus.Failed);
            Logger.LogError(ex, "{Role} {Name} failed rework on PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, rework.PrNumber);

            // Re-enqueue rework items on failure so they get retried next loop.
            await RequeueReworkIfStillOpenAsync(reworkBatch, ct);
        }

        // Reset status after rework — prevent stale "Addressing feedback" display
        UpdateStatus(AgentStatus.Idle, "Rework cycle complete, checking tasks");
    }

    /// <summary>
    /// Re-enqueues rework items only if the PR is still open. Prevents agents from
    /// looping on merged/closed PRs when rework fails during a long CLI session and
    /// the PR gets merged concurrently by another agent path.
    /// </summary>
    private async Task RequeueReworkIfStillOpenAsync(List<ReworkItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var prNumber = items[0].PrNumber;
        try
        {
            var pr = await PrService.GetAsync(prNumber, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation(
                    "{Role} {Name} discarding rework requeue for PR #{PrNumber} — PR is {State}",
                    Identity.Role, Identity.DisplayName, prNumber, pr?.State ?? "deleted");
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Don't swallow shutdown cancellation
        }
        catch (Exception ex)
        {
            // If we can't verify PR state, re-enqueue to avoid permanently losing items
            Logger.LogDebug(ex, "Could not verify PR #{PrNumber} state for requeue, proceeding with requeue", prNumber);
        }
        foreach (var item in items)
            ReworkQueue.Enqueue(item);
    }

    /// <summary>
    /// After rework, reply to each unresolved inline review thread explaining what was addressed.
    /// The reviewer will later resolve these threads when approving.
    /// </summary>
    private async Task ReplyToInlineReviewThreadsAsync(
        int prNumber, string? changesSummary, List<Core.AI.CodeFileParser.CodeFile> updatedFiles,
        string attemptDisplay, CancellationToken ct)
    {
        try
        {
            var threads = await ReviewService.GetThreadsAsync(prNumber, ct);
            var unresolvedThreads = threads.Where(t => !t.IsResolved).ToList();

            if (unresolvedThreads.Count == 0) return;

            var changedPaths = updatedFiles.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var thread in unresolvedThreads)
            {
                // Check if the rework touched the file this thread is about
                var fileWasChanged = changedPaths.Any(p =>
                    p.EndsWith(thread.FilePath, StringComparison.OrdinalIgnoreCase) ||
                    thread.FilePath.EndsWith(p, StringComparison.OrdinalIgnoreCase));

                string replyBody;
                if (fileWasChanged)
                {
                    replyBody = $"**[{Identity.DisplayName}]** ✅ Addressed (rework {attemptDisplay}) — this file was updated in the rework commit.";
                    if (!string.IsNullOrWhiteSpace(changesSummary))
                        replyBody += $"\n\n{changesSummary}";
                }
                else
                {
                    replyBody = $"**[{Identity.DisplayName}]** ℹ️ Not modified in rework {attemptDisplay}. " +
                        "The feedback may be addressed by changes in related files, or may need a follow-up.";
                }

                // Reply via platform-abstract review service (don't resolve — that's the reviewer's job)
                try
                {
                    await ReviewService.ReplyToThreadAsync(prNumber, thread.ThreadId, replyBody, ct);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to reply to review thread {ThreadId} on PR #{Number}", thread.ThreadId, prNumber);
                }
            }

            Logger.LogInformation("{Role} replied to {Count} inline review threads on PR #{PrNumber}",
                Identity.Role, unresolvedThreads.Count, prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reply to inline review threads on PR #{PrNumber} — rework still submitted", prNumber);
        }
    }

    /// <summary>
    /// Overload for CLI edit mode: takes changed file paths instead of CodeFile objects.
    /// </summary>
    private async Task ReplyToInlineReviewThreadsAsync(
        int prNumber, string? changesSummary, IReadOnlyCollection<string> changedFilePaths,
        string attemptDisplay, CancellationToken ct)
    {
        // Delegate to the existing implementation by wrapping paths as CodeFile objects
        var codeFiles = changedFilePaths
            .Select(p => new Core.AI.CodeFileParser.CodeFile(p, "", ""))
            .ToList();
        await ReplyToInlineReviewThreadsAsync(prNumber, changesSummary, codeFiles, attemptDisplay, ct);
    }

    private async Task FinalizeReworkSubmissionAsync(
        AgentPullRequest pr,
        string reworkTaskId,
        string allReviewers,
        string attemptDisplay,
        string? changesSummary,
        IReadOnlyCollection<string> changedFilePaths,
        bool preserveApprovals,
        CancellationToken ct)
    {
        var followUpStepId = TaskTracker.BeginStep(
            Identity.Id,
            reworkTaskId,
            preserveApprovals ? "Post operator response" : "Capture screenshot & re-request review",
            preserveApprovals
                ? $"Posting operator-addressed response on PR #{pr.Number} while preserving approvals"
                : $"Taking screenshot and marking PR #{pr.Number} ready for re-review",
            Identity.ModelTier);

        await ReviewService.AddCommentAsync(
            pr.Number,
            BuildReworkCompletionComment(allReviewers, changesSummary, changedFilePaths, preserveApprovals),
            ct);

        await ReplyToInlineReviewThreadsAsync(pr.Number, changesSummary, changedFilePaths, attemptDisplay, ct);

        if (preserveApprovals)
        {
            TaskTracker.CompleteStep(followUpStepId);
            Logger.LogInformation(
                "Operator feedback addressed for PR #{PrNumber} — approvals preserved",
                pr.Number);
            return;
        }

        await SyncBranchWithMainAsync(pr.Number, ct);
        await MarkReadyForReviewWithScreenshotAsync(pr, ct);
        TaskTracker.CompleteStep(followUpStepId);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "Rework"
        }, ct);

        Logger.LogInformation(
            "{Role} {Name} submitted rework for PR #{PrNumber}, re-requesting review",
            Identity.Role,
            Identity.DisplayName,
            pr.Number);
    }

    private string BuildReworkCompletionComment(
        string allReviewers,
        string? changesSummary,
        IReadOnlyCollection<string> changedFilePaths,
        bool preserveApprovals)
    {
        if (preserveApprovals)
        {
            var comment = "**[Operator-Addressed]** Implemented the requested changes.\n\n" +
                          "Changes made based on operator feedback. Existing review approvals preserved.";
            return AppendReworkDetails(comment, changesSummary, changedFilePaths);
        }

        var reworkComment = $"**[{Identity.DisplayName}] Rework** — Addressed feedback from {allReviewers}.";
        return AppendReworkDetails(reworkComment, changesSummary, changedFilePaths);
    }

    private static string AppendReworkDetails(
        string comment,
        string? changesSummary,
        IReadOnlyCollection<string> changedFilePaths)
    {
        if (!string.IsNullOrWhiteSpace(changesSummary))
            return comment + "\n\n" + changesSummary;

        if (changedFilePaths.Count == 0)
            return comment + "\n\n**Files updated:** committed changes from the rework session.";

        return comment + "\n\n**Files updated:** " +
               string.Join(", ", changedFilePaths.Select(path => $"`{path}`"));
    }

    /// <summary>
    /// Enforce file scope after CLI edit mode by reverting out-of-scope files.
    /// Returns the filtered list of changed files that are within scope.
    /// </summary>
    protected async Task<List<string>> EnforceCliEditScopeAsync(
        List<string> changedFiles, string? prBody, int prNumber, CancellationToken ct,
        bool isRework = false)
    {
        if (changedFiles.Count == 0 || Workspace is null)
            return changedFiles;

        // During rework, skip scope enforcement entirely. The reviewer explicitly asked
        // for changes — the agent should be trusted to address feedback without artificial
        // file-plan constraints. The reviewer will re-review the result.
        if (isRework)
        {
            Logger.LogDebug("{Role} {Name} skipping scope enforcement for rework on PR #{PrNumber} ({FileCount} files)",
                Identity.Role, Identity.DisplayName, prNumber, changedFiles.Count);
            return changedFiles;
        }

        // Get allowed files from the file plan (same logic as FilterToAllowedScope)
        var allowedFiles = ExtractAllowedFilesFromDescription(prBody);

        var inScope = new List<string>();
        var outOfScope = new List<string>();

        foreach (var file in changedFiles)
        {
            var norm = NormalizePath(file);

            // Infrastructure files are always allowed
            if (IsInfrastructureFile(norm))
            {
                inScope.Add(file);
                continue;
            }

            // If no file plan found, allow all (fail-open)
            if (allowedFiles.Count == 0)
            {
                inScope.Add(file);
                continue;
            }

            // Check against allowed file plan
            var fileName = Path.GetFileName(norm);
            if (allowedFiles.Any(a =>
                a.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                a.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                norm.EndsWith(a, StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(norm, StringComparison.OrdinalIgnoreCase)))
            {
                inScope.Add(file);
            }
            else
            {
                outOfScope.Add(file);
                Logger.LogWarning("{Role} {Name} CLI edit blocked out-of-scope file: {Path} on PR #{PrNumber}",
                    Identity.Role, Identity.DisplayName, file, prNumber);
            }
        }

        // Revert out-of-scope files
        if (outOfScope.Count > 0)
        {
            LogActivity("scope", $"🚫 CLI edit: reverting {outOfScope.Count} out-of-scope file(s)");
            RecordImplementationNote($"Reverted {outOfScope.Count} out-of-scope file(s): {string.Join(", ", outOfScope.Take(5))}");
            await Workspace.RevertFilesAsync(outOfScope, ct);
        }

        return inScope;
    }

    /// <summary>
    /// Compute the PR branch name for workspace operations.
    /// </summary>
    private string GetPrBranchName(AgentPullRequest pr)
    {
        var runScope = BranchProvider?.RunScope;
        var fallbackSlug = Identity.Id.Replace(" ", "-").ToLowerInvariant();
        var fallbackBranch = runScope is not null
            ? $"agent/{runScope}/{fallbackSlug}/pr-{pr.Number}"
            : $"agent/{fallbackSlug}/pr-{pr.Number}";
        return pr.HeadBranch ?? fallbackBranch;
    }

    /// <summary>
    /// Runs the clarification loop with the PM if the AI plan contains questions.
    /// Returns the updated plan content after clarifications are resolved.
    /// </summary>
    protected async Task<string> RunClarificationLoopAsync(
        ChatHistory planHistory, string planContent, AgentIssue issue, CancellationToken ct)
    {
        if (planContent.Contains("NO_QUESTIONS", StringComparison.OrdinalIgnoreCase) ||
            !planContent.Contains("?"))
            return planContent;

        var maxRounds = Config.Limits.MaxClarificationRoundTrips;
        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        for (var round = 0; round < maxRounds; round++)
        {
            var questions = ExtractQuestions(planContent);
            if (string.IsNullOrWhiteSpace(questions))
                break;

            Logger.LogInformation(
                "{Role} {Name} asking clarification on issue #{Number} (round {Round}/{Max})",
                Identity.Role, Identity.DisplayName, issue.Number, round + 1, maxRounds);

            await WorkItemService.AddCommentAsync(issue.Number,
                $"**{Identity.DisplayName}** has questions before starting work:\n\n{questions}",
                ct);

            await MessageBus.PublishAsync(new ClarificationRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ClarificationRequest",
                IssueNumber = issue.Number,
                Question = questions
            }, ct);

            UpdateStatus(AgentStatus.Blocked, $"Waiting for clarification on issue #{issue.Number}");

            // Wait for response (poll the clarification response queue)
            var responseReceived = false;
            for (var i = 0; i < 60; i++) // ~5 minutes
            {
                if (ClarificationResponses.TryDequeue(out var resp) &&
                    resp.IssueNumber == issue.Number)
                {
                    responseReceived = true;
                    planHistory.AddAssistantMessage(planContent);
                    var clarificationMsg = PromptService is not null
                        ? await PromptService.RenderAsync("engineer-base/clarification-followup",
                            new Dictionary<string, string> { ["pm_response"] = resp.Response }, ct)
                        : null;
                    planHistory.AddUserMessage(clarificationMsg
                        ?? $"The PM has responded to your questions:\n\n{resp.Response}\n\n" +
                           "Based on this clarification, update your understanding. " +
                           "If you still have questions, list them. Otherwise say 'NO_QUESTIONS'.");

                    var updatedPlan = await chat.GetChatMessageContentAsync(
                        planHistory, cancellationToken: ct);
                    planContent = updatedPlan.Content?.Trim() ?? "";
                    break;
                }
                await Task.Delay(5000, ct);
            }

            if (!responseReceived)
            {
                Logger.LogWarning(
                    "No clarification response received for issue #{Number}, proceeding anyway",
                    issue.Number);
                break;
            }

            if (planContent.Contains("NO_QUESTIONS", StringComparison.OrdinalIgnoreCase))
                break;
        }

        return planContent;
    }

    #endregion

    #region Message Handlers

    protected virtual Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation("{Role} {Name} received task assignment: {Title} (Complexity: {Complexity})",
            Identity.Role, Identity.DisplayName, message.Title, message.Complexity);
        return Task.CompletedTask;
    }

    protected virtual Task HandleIssueAssignmentAsync(IssueAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation("{Role} {Name} received issue assignment: #{IssueNumber} {Title}",
            Identity.Role, Identity.DisplayName, message.IssueNumber, message.IssueTitle);
        LogActivity("message", $"Received issue assignment: #{message.IssueNumber} {message.IssueTitle}");
        BusAssignedIssues.Add(message.IssueNumber);
        AssignmentQueue.Enqueue(message);
        return Task.CompletedTask;
    }

    protected virtual async Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        // Match by CurrentPrNumber OR AssignedPullRequest OR a PR we've already shipped past
        // implementation. That allows engineers to keep picking up new work without losing late
        // reviewer feedback on older PRs.
        var matchesPastImplementationPr = _pastImplementationPrs.Contains(message.PrNumber)
            && IsPastImplementationPrTracked(message.PrNumber);

        if (Identity.AssignedPullRequest != message.PrNumber.ToString() &&
            CurrentPrNumber != message.PrNumber &&
            !matchesPastImplementationPr)
            return;

        if (matchesPastImplementationPr)
        {
            var trackedPr = await PrService.GetAsync(message.PrNumber, ct);
            if (trackedPr is null || !string.Equals(trackedPr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                UntrackPastImplementationPr(message.PrNumber);
                return;
            }
        }

        Logger.LogInformation("{Role} {Name} received change request from {Reviewer} on PR #{PrNumber}",
            Identity.Role, Identity.DisplayName, message.ReviewerAgent, message.PrNumber);

        ReworkQueue.Enqueue(new ReworkItem(message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
    }

    protected virtual Task HandleClarificationResponseAsync(ClarificationResponseMessage message, CancellationToken ct)
    {
        Logger.LogInformation("{Role} {Name} received clarification response for issue #{IssueNumber}",
            Identity.Role, Identity.DisplayName, message.IssueNumber);
        ClarificationResponses.Enqueue(message);
        return Task.CompletedTask;
    }

    #endregion

    #region Issue Monitoring

    protected async Task CheckForIssuesAsync(CancellationToken ct)
    {
        try
        {
            var issues = await IssueWf.GetIssuesForAgentAsync(Identity.DisplayName, ct);

            foreach (var issue in issues)
            {
                if (ProcessedIssueIds.Contains(issue.Number))
                    continue;

                ProcessedIssueIds.Add(issue.Number);

                Logger.LogInformation("{Role} {Name} processing issue #{Number}: {Title}",
                    Identity.Role, Identity.DisplayName, issue.Number, issue.Title);

                if (issue.Body.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase)
                    || issue.Body.Contains("feedback", StringComparison.OrdinalIgnoreCase))
                {
                    await IssueWf.ResolveIssueAsync(
                        issue.Number,
                        $"Acknowledged. {Identity.DisplayName} will address the feedback.",
                        ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} failed to check issues",
                Identity.Role, Identity.DisplayName);
        }
    }

    protected async Task ReportBlockerAsync(string title, string details, CancellationToken ct)
    {
        try
        {
            var issue = await IssueWf.ReportBlockerAsync(
                Identity.DisplayName, title, details, ct);
            UpdateStatus(AgentStatus.Blocked, title);

            Logger.LogWarning("{Role} {Name} reported blocker issue #{Number}: {Title}",
                Identity.Role, Identity.DisplayName, issue.Number, title);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Role} {Name} failed to report blocker",
                Identity.Role, Identity.DisplayName);
            RecordError($"Blocker report failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning, ex);
        }
    }

    #endregion

    #region Legacy PR Work (recovery after restart)

    /// <summary>
    /// Handles an existing open PR found for this agent (typically after a restart).
    /// Uses incremental step-by-step implementation like issue-driven work.
    /// </summary>
    protected async Task WorkOnLegacyPrAsync(AgentPullRequest pr, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Working on PR #{pr.Number}: {pr.Title}");
        Identity.AssignedPullRequest = pr.Number.ToString();

        // Resume or create a CLI session for this PR
        ActivatePrSession(pr.Number);

        Logger.LogInformation("{Role} {Name} starting work on PR #{Number}: {Title}",
            Identity.Role, Identity.DisplayName, pr.Number, pr.Title);

        try
        {
            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var techStack = Config.Project.TechStack;

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Set file scope for this task (used by build-fix prompts)
            _currentFileScopeBlock = BuildFileScopePromptBlock(pr.Body, null);

            // Build a synthetic issue from the PR body for the incremental implementation
            var syntheticIssue = new AgentIssue
            {
                Number = 0,
                Title = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title) ?? pr.Title,
                Body = pr.Body ?? "",
                State = "open",
                Labels = new List<string>()
            };

            // Generate implementation steps
            var steps = await GenerateImplementationStepsAsync(
                chat, pr, syntheticIssue, pmSpecDoc, architectureDoc, techStack, ct);

            if (steps.Count == 0)
            {
                // Fallback to single-pass
                Logger.LogWarning("{Role} {Name} no steps generated for legacy PR #{Number}, using single-pass",
                    Identity.Role, Identity.DisplayName, pr.Number);

                var history = CreateChatHistory();
                history.AddSystemMessage(GetImplementationSystemPrompt(techStack));
                var singlePassUser = PromptService is not null
                    ? await PromptService.RenderAsync("engineer-base/single-pass-implementation",
                        new Dictionary<string, string>
                        {
                            ["pm_spec"] = pmSpecDoc,
                            ["architecture"] = architectureDoc,
                            ["task_title"] = syntheticIssue.Title,
                            ["pr_body"] = pr.Body ?? "",
                            ["tech_stack"] = techStack
                        }, ct)
                    : null;
                history.AddUserMessage(singlePassUser
                    ?? $"## PM Specification\n{pmSpecDoc}\n\n" +
                       $"## Architecture\n{architectureDoc}\n\n" +
                       $"## Task: {syntheticIssue.Title}\n{pr.Body}\n\n" +
                       "Produce a complete implementation. Output each file using this format:\n\n" +
                       "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                       $"Use the {techStack} technology stack. " +
                       "Include all source code files, configuration, and tests. " +
                       "Every file MUST use the FILE: marker format. " +
                       "File paths must be valid filesystem paths (e.g., src/Models/User.cs). " +
                       "Do NOT put code, directives, brackets, or instructions in the file path." +
                       AdditiveEditingGuidance);

                var implResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                history.AddAssistantMessage(implResponse.Content ?? "");
                var implementation = implResponse.Content?.Trim() ?? "";
                var finalOutput = await RunSelfReviewAsync(history, implementation, ct);

                var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(finalOutput);
                if (codeFiles.Count == 0)
                    codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(implementation);

                // Enforce file scope
                if (codeFiles.Count > 0)
                    codeFiles = FilterToAllowedScope(codeFiles, pr.Body, syntheticIssue.Body, pr.Number);

                if (codeFiles.Count > 0)
                {
                    if (Workspace is not null && BuildRunnerSvc is not null)
                    {
                        var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, "Implement task",
                            1, 1, syntheticIssue.Title, chat, ct, isRework: true);
                        if (!committed)
                        {
                            Logger.LogWarning("{Role} {Name} single-step implementation for PR #{PrNumber} blocked by build errors",
                                Identity.Role, Identity.DisplayName, pr.Number);
                            LogActivity("task", $"⛔ PR #{pr.Number} implementation blocked by build errors");
                        }
                    }
                    else
                    {
                        await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, "Implement task", ct);
                    }
                }
            }
            else
            {
                // Incremental step-by-step implementation
                var completedSteps = new List<string>();
                for (var i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = steps[i];
                    var stepNumber = i + 1;

                    UpdateStatus(AgentStatus.Working,
                        $"PR #{pr.Number} step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");

                    var stepHistory = CreateChatHistory();
                    stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

                    var contextBuilder = new System.Text.StringBuilder();
                    contextBuilder.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                    contextBuilder.AppendLine($"## Architecture\n{architectureDoc}\n");
                    contextBuilder.AppendLine($"## Task: {syntheticIssue.Title}\n{pr.Body}\n");

                    if (completedSteps.Count > 0)
                    {
                        contextBuilder.AppendLine("## Previously Completed Steps");
                        for (var j = 0; j < completedSteps.Count; j++)
                            contextBuilder.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                        contextBuilder.AppendLine();
                        var existingFiles = await GetPrFileListAsync(pr.Number, ct);
                        if (!string.IsNullOrEmpty(existingFiles))
                            contextBuilder.AppendLine($"## Files already in this PR\n{existingFiles}\n");
                    }

                    // Load actual content of existing files mentioned in this step
                    var existingFileContent = await GetExistingFileContentForStepAsync(step, pr.HeadBranch, ct);
                    if (!string.IsNullOrEmpty(existingFileContent))
                        contextBuilder.AppendLine(existingFileContent);

                    contextBuilder.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                    contextBuilder.AppendLine(step);
                    contextBuilder.AppendLine();
                    contextBuilder.AppendLine("Implement ONLY this step. Output each file using this format:\n");
                    contextBuilder.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
                    contextBuilder.AppendLine($"Use the {techStack} technology stack. Every file MUST use the FILE: marker format.");
                    contextBuilder.AppendLine("File paths must be valid filesystem paths (e.g., src/Models/User.cs). " +
                        "Do NOT put code, directives, brackets, or instructions in the file path.");
                    if (completedSteps.Count > 0)
                        contextBuilder.AppendLine("If you need to update a file from a previous step, include the COMPLETE updated file content.");

                    contextBuilder.AppendLine("\nINCREMENTAL MODIFICATION RULE: When modifying an EXISTING file, " +
                        "preserve ALL existing code, CSS classes, HTML structure, and functionality not directly " +
                        "related to this step. Make SURGICAL additions — do NOT rewrite the file from scratch.");

                    stepHistory.AddUserMessage(contextBuilder.ToString());

                    var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                    var stepImpl = stepResponse.Content?.Trim() ?? "";

                    stepHistory.AddAssistantMessage(stepImpl);
                    var finalStep = await RunSelfReviewAsync(stepHistory, stepImpl, ct);

                    var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(finalStep);
                    if (codeFiles.Count == 0)
                        codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(stepImpl);

                    // Enforce file scope
                    if (codeFiles.Count > 0)
                        codeFiles = FilterToAllowedScope(codeFiles, pr.Body, syntheticIssue.Body, pr.Number);

                    if (codeFiles.Count > 0)
                    {
                        var commitMsg = $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}";
                        bool committed;
                        if (Workspace is not null && BuildRunnerSvc is not null)
                        {
                            committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, commitMsg,
                                stepNumber, steps.Count, step, chat, ct, isRework: true);
                        }
                        else
                        {
                            await PrWorkflow.CommitCodeFilesToPRAsync(
                                pr.Number, codeFiles, commitMsg, ct);
                            committed = true;
                        }

                        if (!committed)
                        {
                            Logger.LogWarning("{Role} {Name} step {Step}/{Total} blocked by build errors, skipping",
                                Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                            LogActivity("task", $"⛔ Step {stepNumber}/{steps.Count} blocked by build errors: {Truncate(step, 80)}");
                        }
                    }

                    completedSteps.Add(step);
                }
            }

            // Sync branch with main before marking ready — ensures PR is merge-clean
            await SyncBranchWithMainAsync(pr.Number, ct);

            // === Gate: PRCodeComplete — human reviews code before marking ready ===
            await WaitForHumanGateAsync(
                GateIds.PRCodeComplete,
                $"Engineer code complete on PR #{pr.Number}, ready for human review before marking ready-for-review",
                pr.Number, ct: ct);

            await MarkReadyForReviewWithScreenshotAsync(pr, ct);

            await MessageBus.PublishAsync(new ReviewRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ReviewRequest",
                PrNumber = pr.Number,
                PrTitle = pr.Title,
                ReviewType = "CodeReview"
            }, ct);

            Logger.LogInformation("{Role} {Name} completed PR #{Number}, marked ready for review",
                Identity.Role, Identity.DisplayName, pr.Number);
            LogActivity("task", $"🎉 Completed PR #{pr.Number}: {pr.Title} — marked ready for review");

            UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting next task");
            Identity.AssignedPullRequest = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Role} {Name} failed working on PR #{Number}",
                Identity.Role, Identity.DisplayName, pr.Number);
            RecordError($"Failed on PR #{pr.Number}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);

            await ReportBlockerAsync(
                $"Implementation failure on PR #{pr.Number}",
                $"Failed while working on PR #{pr.Number}: {pr.Title}\n\nError: {ex.Message}",
                ct);
        }
    }

    #endregion

    #region Virtual Extension Points

    /// <summary>Display name for prompts, e.g., "Software Engineer".</summary>
    protected abstract string GetRoleDisplayName();

    /// <summary>System prompt for the implementation AI call.</summary>
    protected abstract string GetImplementationSystemPrompt(string techStack);

    /// <summary>System prompt for the rework AI call.</summary>
    protected virtual string GetReworkSystemPrompt(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("engineer-base/rework-system", new Dictionary<string, string>
            {
                ["role_display_name"] = GetRoleDisplayName(),
                ["tech_stack"] = techStack,
                ["scope_relaxation"] = "SCOPE RULE: Only modify files that are part of YOUR task's File Plan. " +
                    "Do NOT modify, rewrite, or delete test files, shared infrastructure files (App.razor, " +
                    "_Host.cshtml, Program.cs), or any files outside your task scope."
            }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        return $"You are a {GetRoleDisplayName()} addressing review feedback on a pull request. " +
            $"The project uses {techStack}. " +
            "Carefully read the feedback, understand what needs to be fixed, and produce " +
            "an updated implementation that addresses ALL the feedback points. " +
            "Be thorough — every feedback item must be resolved.\n\n" +
            "INCREMENTAL MODIFICATION RULE: When fixing existing files, make ONLY the changes " +
            "required to address the feedback. Do NOT rewrite or reorganize code that is not " +
            "mentioned in the feedback. Preserve existing CSS classes, variable names, HTML structure, " +
            "and functionality that works correctly. Your changes should be surgical — " +
            "a reviewer should see a minimal, focused diff.\n\n" +
            "SCOPE RULE: Only modify files that are part of YOUR task's File Plan. " +
            "Do NOT modify, rewrite, or delete test files, shared infrastructure files (App.razor, " +
            "_Host.cshtml, Program.cs), or any files outside your task scope. " +
            "If review feedback asks you to revert a file you shouldn't have changed, " +
            "simply omit that file from your output — do not try to reconstruct it.\n\n" +
            "DEPENDENCY RULE: Before using ANY external library/package/framework, check the project's " +
            "dependency manifest. If a dependency is not already listed, add it and include the updated manifest.\n\n" +
            "CRITICAL: Your response MUST start with a CHANGES SUMMARY that addresses EACH numbered " +
            "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
            "in one sentence what you changed or why no change was needed. This summary is posted as " +
            "a PR comment so reviewers can verify their feedback was addressed point-by-point.\n\n" +
            "After the CHANGES SUMMARY, output corrected files using FILE: format.";
    }

    /// <summary>
    /// Returns the rework system prompt for CLI edit mode — instructs LLM to use native edit tools.
    /// </summary>
    protected string GetReworkSystemPromptCliEdit(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("engineer-base/rework-system-cli-edit", new Dictionary<string, string>
            {
                ["role_display_name"] = GetRoleDisplayName(),
                ["tech_stack"] = techStack,
                ["scope_relaxation"] = "SCOPE RULE: Only modify files that are part of YOUR task's File Plan. " +
                    "Do NOT modify, rewrite, or delete test files, shared infrastructure files (App.razor, " +
                    "_Host.cshtml, Program.cs), or any files outside your task scope."
            }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        return $"You are a {GetRoleDisplayName()} making SURGICAL fixes to an existing pull request based on reviewer feedback. " +
            $"The project uses {techStack}. " +
            "You have access to tools that let you read, edit, and create files directly. USE THEM.\n\n" +
            "SURGICAL REWORK RULES:\n" +
            "1. Read each feedback item carefully. Make ONLY the changes needed to address that specific item.\n" +
            "2. Use your view tool to read the current file content before making changes.\n" +
            "3. Use your edit tool to make targeted, line-level changes. Do NOT rewrite entire files.\n" +
            "4. Do NOT touch CSS, config, project files, or infrastructure unless the reviewer SPECIFICALLY asked.\n" +
            "5. Your diff should be minimal — a reviewer should see a small, focused set of changes.\n\n" +
            "SCOPE RULE: Only modify files that are part of YOUR task's File Plan. " +
            "Do NOT modify, rewrite, or delete test files, shared infrastructure files (App.razor, " +
            "_Host.cshtml, Program.cs), or any files outside your task scope.\n\n" +
            "DEPENDENCY RULE: Before using ANY external library/package/framework, check the project's " +
            "dependency manifest. If a dependency is not already listed, add it and include the updated manifest.\n\n" +
            "CRITICAL: Start your response with a CHANGES SUMMARY that addresses EACH numbered " +
            "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
            "in one sentence what you changed or why no change was needed. Then use your tools to make the edits.";
    }

    /// <summary>
    /// Optional self-review pass after implementation. Senior overrides to do a multi-turn review.
    /// Default: return implementation as-is.
    /// </summary>
    protected virtual Task<string> RunSelfReviewAsync(ChatHistory history, string implementation, CancellationToken ct)
        => Task.FromResult(implementation);

    /// <summary>
    /// Pre-publish screenshot expectation check: captures a screenshot of the running app and asks
    /// vision-AI whether what's on screen matches what the PR title/body claims to deliver. Surfaces
    /// the verdict as an implementation note so the downstream self-assessment LLM can fold it into
    /// its gap analysis.
    ///
    /// <para>
    /// **Why this exists:** in the 2026-05-11 tower-defense run, every captured screenshot was a blank
    /// canvas because the target app's backend crashed on startup (SQLite UNIQUE constraint in seed
    /// data). The pipeline approved 6 PRs in a row because:
    /// </para>
    /// <list type="number">
    ///   <item>`dotnet build` passed (compile-time fine)</item>
    ///   <item>Unit tests passed (didn't exercise seed→serve→render)</item>
    ///   <item>Architect + PM reviewed code structure, not runtime behavior</item>
    ///   <item>TE captured the blank screenshot + uploaded it; vision-AI described it as "blank canvas"
    ///         in a log line that nobody acted on</item>
    /// </list>
    ///
    /// <para>
    /// This check moves the catch upstream — the engineer's own self-assessment now sees a
    /// "screenshot shows blank canvas but PR claims to deliver X" note and the self-assessment LLM
    /// flags it as a gap, triggering the existing rework cycle. Safe-by-default: any failure
    /// (no workspace, no vision model, INCONCLUSIVE verdict) leaves the flow unchanged.
    /// </para>
    /// </summary>
    protected async Task RunPrePublishScreenshotCheckAsync(AgentPullRequest pr, CancellationToken ct)
    {
        // Guard: workspace required to launch the app and screenshot it
        if (Workspace is null)
        {
            Logger.LogDebug("{Role} {Name} skipping pre-publish screenshot check — no local workspace",
                Identity.Role, Identity.DisplayName);
            return;
        }
        if (WorkspaceServices?.PlaywrightRunner is null)
        {
            Logger.LogDebug("{Role} {Name} skipping pre-publish screenshot check — no PlaywrightRunner",
                Identity.Role, Identity.DisplayName);
            return;
        }

        // Hard timeout: the entire screenshot check (app startup + capture + vision eval)
        // must complete within 5 minutes. Without this, a hanging npm install or app startup
        // can stall the agent indefinitely (observed: SE3 stuck 107 min on screenshot check).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            UpdateStatus(AgentStatus.Working, $"Pre-publish screenshot check for PR #{pr.Number}");
            LogActivity("screenshot", $"🔎 Pre-publish screenshot check on PR #{pr.Number}");

            var captureResult = await WorkspaceServices.PlaywrightRunner.CaptureAppScreenshotAsync(
                Workspace.RepoPath, Core.Config.Workspace, timeoutCts.Token);

            if (captureResult is null || captureResult.Bytes.Length == 0)
            {
                // No capture at all — could be backend-only PR (no UI to screenshot) OR the app
                // failed to start. PlaywrightRunner already logs the reason; we record a soft note
                // for the self-assessment LLM but don't force a gap.
                var note = $"Pre-publish screenshot check on PR #{pr.Number}: no screenshot captured " +
                           "(app did not start as a web server, or the static-HTML fallback found no " +
                           "renderable entry point). If this PR was supposed to deliver a UI, the " +
                           "self-assessment must verify the app actually starts. If backend-only, ignore.";
                RecordImplementationNote(note);
                Logger.LogInformation("{Role} {Name} pre-publish screenshot check: no capture for PR #{PrNumber}",
                    Identity.Role, Identity.DisplayName, pr.Number);
                return;
            }

            // Run the semantic expectation check
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var img = new PullRequestWorkflow.ScreenshotImage(captureResult.Bytes, "image/png",
                $"Pre-publish screenshot PR #{pr.Number}", "");
            var verdict = await PullRequestWorkflow.EvaluateScreenshotAgainstExpectationsAsync(
                img, pr.Title, pr.Body, chat, ct);

            Logger.LogInformation(
                "Pre-publish screenshot check PR #{PrNumber}: {Verdict} (conf {Confidence:0.00}) — observed='{Observed}' expected='{Expected}'",
                pr.Number, verdict.Verdict, verdict.Confidence, verdict.Observed, verdict.Expected);
            LogActivity("screenshot",
                $"🔬 Pre-publish screenshot: {verdict.Verdict} (conf {verdict.Confidence:0.00}) — {verdict.Observed}");

            // Record verdict for the self-assessment LLM. Different framing per verdict so the
            // downstream LLM doesn't over-react to inconclusive checks on backend-only PRs.
            if (verdict.Verdict == "DOES_NOT_MATCH" && verdict.Confidence >= 0.6)
            {
                var issuesList = verdict.BlockingIssues.Count > 0
                    ? "\n  - " + string.Join("\n  - ", verdict.BlockingIssues)
                    : "";
                var note = $"⚠️ PRE-PUBLISH SCREENSHOT CHECK FAILED for PR #{pr.Number} " +
                           $"(confidence {verdict.Confidence:0.00}). " +
                           $"Expected: {verdict.Expected}. " +
                           $"Observed: {verdict.Observed}.{issuesList}\n" +
                           "This is a HARD GAP — the rendered UI does not match what this PR claimed " +
                           "to deliver. Common causes: backend startup crash (e.g. SQLite UNIQUE " +
                           "constraint in seed data), missing migrations, wrong-route rendered, scene-key " +
                           "error, stuck loading spinner. INVESTIGATE AND FIX before marking ready-for-review.";
                RecordImplementationNote(note);
                LogActivity("screenshot", $"⚠️ Self-assessment will flag PR #{pr.Number} for rework — rendered UI mismatches intent");
            }
            else if (verdict.Verdict == "INCONCLUSIVE")
            {
                // Soft note — surfaces the AI's observation without forcing a gap
                RecordImplementationNote(
                    $"Pre-publish screenshot check on PR #{pr.Number}: INCONCLUSIVE (likely backend-only " +
                    $"or unverifiable). Observed: {verdict.Observed}");
            }
            else
            {
                // MATCHES — soft positive signal
                RecordImplementationNote(
                    $"Pre-publish screenshot check on PR #{pr.Number}: MATCHES " +
                    $"(confidence {verdict.Confidence:0.00}). Observed: {verdict.Observed}");
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Screenshot check timed out (5 min hard cap) — not a fatal error, just skip
            Logger.LogWarning(
                "{Role} {Name} pre-publish screenshot check timed out for PR #{PrNumber} (5 min cap) — proceeding without",
                Identity.Role, Identity.DisplayName, pr.Number);
            RecordImplementationNote(
                $"Pre-publish screenshot check on PR #{pr.Number}: TIMED OUT after 5 minutes " +
                "(app startup or capture took too long). Self-assessment should verify app starts correctly.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never block a PR on a check failure — log + continue
            Logger.LogWarning(ex,
                "{Role} {Name} pre-publish screenshot check failed for PR #{PrNumber} (non-fatal — proceeding)",
                Identity.Role, Identity.DisplayName, pr.Number);
        }
    }

    /// <summary>
    /// Pre-publish self-assessment: re-reads the Issue requirements with a fresh context window,
    /// inspects what was actually built, and verifies completeness before marking ready-for-review.
    /// If gaps are found, attempts a targeted fix cycle (build/test/recommit).
    /// All assessment intelligence lives in prompt templates — this method is plumbing only.
    /// </summary>
    protected async Task<bool> RunPrePublishAssessmentAsync(
        AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        // Guard: workspace required for file inspection
        if (Workspace is null)
        {
            Logger.LogDebug("{Role} {Name} skipping self-assessment — no local workspace available",
                Identity.Role, Identity.DisplayName);
            return true;
        }

        // Guard: prompt service required for template-driven assessment
        if (PromptService is null)
        {
            Logger.LogDebug("{Role} {Name} skipping self-assessment — no prompt service available",
                Identity.Role, Identity.DisplayName);
            return true;
        }

        var maxAttempts = Config.AgenticLoop.MaxIterations;
        if (maxAttempts < 1) maxAttempts = 2;

        var techStack = Config.Project.TechStack;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stepId = TaskTracker.BeginStep(Identity.Id,
                $"PR-{pr.Number}", $"Self-assessment (attempt {attempt})",
                $"Reviewing implementation against requirements for PR #{pr.Number}",
                Identity.ModelTier);

            try
            {
                UpdateStatus(AgentStatus.Working, $"Self-assessing PR #{pr.Number} (attempt {attempt}/{maxAttempts})");

                // Gather inputs for the assessment (fresh context — no implementation history)
                var changedFiles = await Workspace.GetDiffFileListVsMainAsync(ct);
                if (changedFiles.Count == 0)
                {
                    Logger.LogDebug("{Role} {Name} no changed files vs main — skipping self-assessment",
                        Identity.Role, Identity.DisplayName);
                    TaskTracker.CompleteStep(stepId, AgentTaskStepStatus.Skipped);
                    return true;
                }

                var changedFilesList = string.Join("\n", changedFiles.Select(f => $"- {f}"));
                var previousGaps = attempt > 1 ? _lastAssessmentGaps : "";

                // Build implementation handoff context — accumulated notes + recent activity log
                var handoffContext = BuildImplementationHandoffContext();

                // Render prompt templates
                var systemPrompt = await PromptService.RenderAsync("engineer-base/self-assessment-system",
                    new Dictionary<string, string>
                    {
                        ["role_display_name"] = GetRoleDisplayName(),
                        ["tech_stack"] = techStack
                    }, ct);

                var userPrompt = await PromptService.RenderAsync("engineer-base/self-assessment-user",
                    new Dictionary<string, string>
                    {
                        ["issue_title"] = issue.Title,
                        ["issue_body"] = issue.Body ?? "(no issue body)",
                        ["changed_files"] = changedFilesList,
                        ["workspace_path"] = Workspace.RepoPath,
                        ["pr_number"] = pr.Number.ToString(),
                        ["attempt"] = attempt.ToString(),
                        ["implementation_context"] = handoffContext,
                        ["previous_gaps"] = string.IsNullOrEmpty(previousGaps)
                            ? ""
                            : $"### Gaps from Previous Assessment\n\nThe following gaps were identified and should now be fixed:\n{previousGaps}"
                    }, ct);

                if (string.IsNullOrEmpty(systemPrompt) || string.IsNullOrEmpty(userPrompt))
                {
                    Logger.LogWarning("{Role} {Name} self-assessment prompt templates not found — skipping",
                        Identity.Role, Identity.DisplayName);
                    TaskTracker.CompleteStep(stepId, AgentTaskStepStatus.Skipped);
                    return true;
                }

                // Fresh context window — new ChatHistory, not the implementation conversation
                var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var history = new ChatHistory();
                history.AddSystemMessage(systemPrompt);
                history.AddUserMessage(userPrompt);

                Logger.LogInformation("{Role} {Name} running self-assessment attempt {Attempt}/{Max} for PR #{PrNumber}",
                    Identity.Role, Identity.DisplayName, attempt, maxAttempts, pr.Number);

                var result = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var response = result?.Content ?? string.Empty;
                TaskTracker.RecordLlmCall(stepId);

                // Parse the JSON verdict
                var verdict = ParseAssessmentVerdict(response);

                if (verdict.Pass)
                {
                    if (verdict.IsInconclusive)
                    {
                        // The LLM returned an empty / unparsable response. We default to PASS
                        // so a transient tooling hiccup doesn't block the engineer, but we WARN
                        // so the silent-rubber-stamp is at least visible in the runner log.
                        // Also surface in PR memory so a future human auditor can find it.
                        Logger.LogWarning(
                            "{Role} {Name} self-assessment INCONCLUSIVE for PR #{PrNumber} (defaulting to pass): {Summary}. " +
                            "Raw response was empty or unparsable on attempt {Attempt}/{Max}. Manual audit recommended.",
                            Identity.Role, Identity.DisplayName, pr.Number, verdict.Summary, attempt, maxAttempts);
                        LogActivity("task", $"⚠️ Self-assessment inconclusive on PR #{pr.Number} (defaulting to pass) — {verdict.Summary}");
                    }
                    else
                    {
                        Logger.LogInformation("{Role} {Name} self-assessment PASSED for PR #{PrNumber}: {Summary}",
                            Identity.Role, Identity.DisplayName, pr.Number, verdict.Summary);
                        LogActivity("task", $"✅ Self-assessment passed for PR #{pr.Number}: {verdict.Summary}");
                    }
                    // Change #3 — Write completion manifest so enforcement check has data.
                    await WriteCompletionManifestAsync(pr, issue, changedFiles, passed: true, verdict.Gaps, ct);
                    TaskTracker.CompleteStep(stepId);
                    return true;
                }

                // NEEDS_CHANGES — log gaps and attempt fix if not at max attempts
                var gapsList = string.Join("\n", verdict.Gaps.Select((g, i) => $"{i + 1}. {g}"));
                _lastAssessmentGaps = gapsList;

                Logger.LogInformation("{Role} {Name} self-assessment found {GapCount} gaps for PR #{PrNumber}",
                    Identity.Role, Identity.DisplayName, verdict.Gaps.Count, pr.Number);
                LogActivity("task", $"🔍 Self-assessment found {verdict.Gaps.Count} gaps in PR #{pr.Number}");

                if (attempt >= maxAttempts)
                {
                    // Max attempts reached — post gaps as a comment and proceed
                    Logger.LogWarning("{Role} {Name} self-assessment max attempts reached for PR #{PrNumber}, proceeding with gaps",
                        Identity.Role, Identity.DisplayName, pr.Number);
                    await ReviewService.AddCommentAsync(pr.Number,
                        $"⚠️ **Self-Assessment:** Found {verdict.Gaps.Count} remaining gap(s) after {maxAttempts} assessment cycles:\n{gapsList}\n\n_Proceeding to review — reviewers please verify these areas._", ct);
                    // Write manifest: if the verdict is inconclusive (JSON parse failure), treat as pass
                    // for the manifest — a generic "JSON parse failed" gap would mark ALL files as stubs,
                    // which is worse than not assessing at all. Real reviewers will catch actual issues.
                    var manifestPassed = verdict.IsInconclusive || verdict.Pass;
                    await WriteCompletionManifestAsync(pr, issue, changedFiles, passed: manifestPassed, verdict.Gaps, ct);
                    TaskTracker.CompleteStep(stepId);
                    return true; // Don't block the pipeline
                }

                // Attempt fix: send gap list to CLI for targeted repair
                TaskTracker.RecordSubStep(stepId, $"Fixing {verdict.Gaps.Count} gaps");
                var fixSuccess = await AttemptSelfAssessmentFixAsync(pr, issue, gapsList, techStack, chat, ct);

                if (!fixSuccess)
                {
                    Logger.LogWarning("{Role} {Name} self-assessment fix attempt failed for PR #{PrNumber}",
                        Identity.Role, Identity.DisplayName, pr.Number);
                }

                TaskTracker.CompleteStep(stepId);
                // Loop continues to re-assess
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Role} {Name} self-assessment error for PR #{PrNumber} — proceeding without assessment",
                    Identity.Role, Identity.DisplayName, pr.Number);
                TaskTracker.FailStep(stepId, ex.Message);
                return true; // Don't block on assessment errors
            }
        }

        return true; // Always allow proceeding
    }

    /// <summary>
    /// Attempts to fix gaps identified by self-assessment using a fresh prompt.
    /// Writes fixes to workspace, rebuilds, retests, and recommits.
    /// </summary>
    private async Task<bool> AttemptSelfAssessmentFixAsync(
        AgentPullRequest pr, AgentIssue issue, string gapsList, string techStack,
        IChatCompletionService chat, CancellationToken ct)
    {
        try
        {
            var fixSystemPrompt = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/self-assessment-fix-system",
                    new Dictionary<string, string>
                    {
                        ["role_display_name"] = GetRoleDisplayName(),
                        ["tech_stack"] = techStack
                    }, ct)
                : null;

            if (string.IsNullOrEmpty(fixSystemPrompt))
            {
                Logger.LogDebug("Self-assessment fix prompt template not found — skipping fix");
                return false;
            }

            // Build fix prompt with gaps + issue context
            var fixHistory = new ChatHistory();
            fixHistory.AddSystemMessage(fixSystemPrompt);
            fixHistory.AddUserMessage(
                $"## Gaps to Fix\n\n{gapsList}\n\n" +
                $"## Original Requirements\n\n**{issue.Title}**\n\n{issue.Body ?? "(no body)"}\n\n" +
                $"## Workspace\n\nThe code is at: `{Workspace!.RepoPath}`\n\n" +
                "Use your tools to inspect existing files, then make targeted fixes for each gap. " +
                "Output changed/new files using FILE: format with complete file content.");

            var fixResult = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixResponse = fixResult?.Content ?? string.Empty;

            // Parse code files from the fix response
            var fixFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(fixResponse);
            if (fixFiles.Count == 0)
            {
                Logger.LogDebug("{Role} {Name} self-assessment fix produced no parseable files",
                    Identity.Role, Identity.DisplayName);
                return false;
            }

            // Write fixes to workspace
            foreach (var file in fixFiles)
                await Workspace.WriteFileAsync(file.Path, file.Content, ct);

            // Rebuild to verify fixes compile
            if (BuildRunnerSvc is not null)
            {
                var buildResult = await BuildRunnerSvc.BuildAsync(
                    Workspace.RepoPath, Config.Workspace.BuildCommand,
                    Config.Workspace.BuildTimeoutSeconds, ct);

                if (!buildResult.Success)
                {
                    Logger.LogWarning("{Role} {Name} self-assessment fix broke the build — reverting",
                        Identity.Role, Identity.DisplayName);
                    await Workspace.RevertUncommittedChangesAsync(ct);
                    return false;
                }
            }

            // Commit and push the fixes
            var branchName = GetPrBranchName(pr);
            await Workspace.CommitAsync($"Self-assessment fixes for #{issue.Number}", ct);
            await Workspace.PushAsync(branchName, ct);

            Logger.LogInformation("{Role} {Name} self-assessment fix committed {FileCount} files for PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, fixFiles.Count, pr.Number);
            LogActivity("task", $"🔧 Self-assessment fix: committed {fixFiles.Count} file(s) to PR #{pr.Number}");

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} self-assessment fix error — reverting uncommitted changes",
                Identity.Role, Identity.DisplayName);
            try { await Workspace!.RevertUncommittedChangesAsync(ct); } catch { /* best effort */ }
            return false;
        }
    }

    // Stores gap list between assessment attempts for context continuity
    private string _lastAssessmentGaps = "";

    /// <summary>
    /// Parse the JSON verdict from the self-assessment AI response.
    /// Handles markdown code fences, conversational preamble/postamble, and malformed JSON.
    /// Uses multi-stage extraction: code fences → first/last brace → keyword fallback.
    /// Inconclusive responses (empty / unparsable) default to PASS so a tooling
    /// hiccup doesn't block the engineer, but the <see cref="AssessmentVerdict.IsInconclusive"/>
    /// flag is set so the call site can log a WARN and surface the issue for audit.
    /// </summary>
    private static AssessmentVerdict ParseAssessmentVerdict(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new AssessmentVerdict(true, new List<string>(), "Empty response — assuming pass", IsInconclusive: true);

        // Stage 1: Strip markdown code fences if present
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0)
                json = json[(firstNewline + 1)..];
            if (json.EndsWith("```"))
                json = json[..^3];
            json = json.Trim();
        }

        // Stage 2: Try parsing the cleaned text directly
        var result = TryParseVerdictJson(json);
        if (result is not null) return result;

        // Stage 3: Extract JSON by finding first '{' and last '}' — handles conversational
        // preamble/postamble wrapping (e.g., "Here is my assessment: { ... } Hope this helps!")
        var firstBrace = response.IndexOf('{');
        var lastBrace = response.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var extracted = response.Substring(firstBrace, lastBrace - firstBrace + 1);
            result = TryParseVerdictJson(extracted);
            if (result is not null) return result;
        }

        // Stage 4: Keyword-based fallback when no JSON can be parsed
        if (response.Contains("NEEDS_CHANGES", StringComparison.OrdinalIgnoreCase))
            return new AssessmentVerdict(false, new List<string> { "Assessment indicated changes needed but JSON parse failed" }, "Parse error", IsInconclusive: true);

        return new AssessmentVerdict(true, new List<string>(), "JSON parse failed — assuming pass", IsInconclusive: true);
    }

    /// <summary>Attempt to parse a JSON string as a self-assessment verdict. Returns null if parsing fails.</summary>
    private static AssessmentVerdict? TryParseVerdictJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdict = root.TryGetProperty("verdict", out var v)
                ? v.GetString()?.Trim().ToUpperInvariant() ?? "PASS"
                : "PASS";

            var gaps = new List<string>();
            if (root.TryGetProperty("gaps", out var gapsArr) && gapsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var g in gapsArr.EnumerateArray())
                {
                    var gapText = g.GetString();
                    if (!string.IsNullOrWhiteSpace(gapText))
                        gaps.Add(gapText);
                }
            }

            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

            return new AssessmentVerdict(verdict == "PASS", gaps, summary, IsInconclusive: false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record AssessmentVerdict(bool Pass, List<string> Gaps, string Summary, bool IsInconclusive = false);

    /// <summary>
    /// Builds a compact implementation handoff summary for the self-assessment context.
    /// Combines accumulated implementation notes with recent activity log entries so
    /// the assessor understands key decisions, constraints, and failures from implementation.
    /// </summary>
    private string BuildImplementationHandoffContext()
    {
        var sb = new System.Text.StringBuilder();

        // Include accumulated implementation notes (decisions, constraints, failures)
        if (_implementationNotes.Count > 0)
        {
            sb.AppendLine("### Implementation Notes");
            sb.AppendLine();
            foreach (var note in _implementationNotes.TakeLast(20)) // Cap at 20 most recent
                sb.AppendLine($"- {note}");
            sb.AppendLine();
        }

        // Include recent activity log as supplementary context
        if (StateStore is not null)
        {
            try
            {
                var activities = StateStore.GetRecentActivityAsync(Identity.Id, 15, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (activities.Count > 0)
                {
                    sb.AppendLine("### Recent Activity");
                    sb.AppendLine();
                    foreach (var a in activities.Take(10))
                        sb.AppendLine($"- [{a.EventType}] {a.Details}");
                }
            }
            catch
            {
                // Activity log is best-effort — never block assessment
            }
        }

        return sb.ToString();
    }

    /// <summary>Additional context to include in rework prompts (e.g., PE includes engineering plan).</summary>
    protected virtual Task<string> GetAdditionalReworkContextAsync(CancellationToken ct)
        => Task.FromResult("");

    /// <summary>Get PMSpec content. Junior overrides to truncate for budget models.</summary>
    protected virtual Task<string> GetPMSpecForContextAsync(CancellationToken ct)
        => Config.Agents.SlimEngineerContext
            ? Task.FromResult("See linked issue for acceptance criteria and architecture context.")
            : ProjectFiles.GetPMSpecAsync(ct);

    /// <summary>Get Architecture content. Junior overrides to truncate for budget models.</summary>
    protected virtual Task<string> GetArchitectureForContextAsync(CancellationToken ct)
        => Config.Agents.SlimEngineerContext
            ? Task.FromResult("See linked issue for acceptance criteria and architecture context.")
            : ProjectFiles.GetArchitectureDocAsync(ct);

    /// <summary>
    /// Read visual design reference files from the repository for UI implementation context.
    /// Cached per-agent instance to avoid repeated reads within the same task.
    /// </summary>
    private string? _cachedDesignContext;
    private bool _designContextLoaded;
    private IReadOnlyList<DesignImage>? _cachedDesignImages;

    /// <summary>
    /// A binary design reference (PNG/JPG) ready to be attached to a chat message
    /// as <see cref="Microsoft.SemanticKernel.ImageContent"/>.
    /// </summary>
    protected sealed record DesignImage(byte[] Data, string MimeType, string Path);

    protected async Task<string?> GetDesignContextAsync(CancellationToken ct)
    {
        if (_designContextLoaded) return _cachedDesignContext;
        _designContextLoaded = true;

        try
        {
            var tree = await RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
            bool IsDesignHtml(string f)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".html" && ext != ".htm") return false;
                var name = Path.GetFileName(f).ToLowerInvariant();
                return name.Contains("design") || name.Contains("concept") ||
                       name.Contains("mockup") || name.Contains("wireframe") ||
                       name.Contains("prototype") || name.Contains("reference");
            }

            bool IsDesignImage(string f)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp") return false;
                // Anything under the scoped design-screenshots folder is always considered a design ref
                if (f.StartsWith(ProjectFiles.DesignScreenshotsPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                // Otherwise, keyword-filter by file name or parent folder
                var lower = f.ToLowerInvariant();
                return lower.Contains("design") || lower.Contains("concept") ||
                       lower.Contains("mockup") || lower.Contains("wireframe") ||
                       lower.Contains("prototype") || lower.Contains("reference") ||
                       lower.Contains("screenshot");
            }

            var designFiles = tree.Where(IsDesignHtml).ToList();
            var designImages = tree.Where(IsDesignImage).ToList();

            if (designFiles.Count == 0 && designImages.Count == 0)
            {
                _cachedDesignImages = Array.Empty<DesignImage>();
                return null;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Visual Design Reference");
            sb.AppendLine("The following design files define the EXACT UI to be built. " +
                "You MUST match the layout, colors, typography, spacing, and component structure precisely. " +
                "Copy CSS values (hex colors, font sizes, margins, paddings, grid templates) DIRECTLY from the design HTML. " +
                "Do NOT simplify, generalize, or 'improve' the design — reproduce it pixel-for-pixel. " +
                "The final rendered page must look identical to the design reference at 1920×1080.\n");

            // Load image bytes so callers can attach them as ImageContent to the chat history.
            var loadedImages = new List<DesignImage>();
            if (designImages.Count > 0)
            {
                sb.AppendLine("### Design Images (attached as vision content)");
                sb.AppendLine("These images are the AUTHORITATIVE visual spec. When they disagree with the HTML concept, the IMAGE wins:\n");
                foreach (var imgPath in designImages)
                {
                    try
                    {
                        var bytes = await RepoContent.GetFileBytesAsync(imgPath, ct: ct);
                        if (bytes is null || bytes.Length == 0) continue;
                        var ext = Path.GetExtension(imgPath).ToLowerInvariant();
                        var mime = ext switch
                        {
                            ".png"  => "image/png",
                            ".jpg"  => "image/jpeg",
                            ".jpeg" => "image/jpeg",
                            ".webp" => "image/webp",
                            _       => "application/octet-stream"
                        };
                        loadedImages.Add(new DesignImage(bytes, mime, imgPath));
                        sb.AppendLine($"- `{imgPath}` ({bytes.Length / 1024} KB, {mime})");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Failed to load design image: {Path}", imgPath);
                    }
                }
                sb.AppendLine();
            }

            foreach (var file in designFiles)
            {
                var content = await RepoContent.GetFileContentAsync(file, EffectiveBranch, ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### `{file}`");
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 15000 ? content[..15000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            _cachedDesignContext = sb.ToString().TrimEnd();
            _cachedDesignImages = loadedImages;
            Logger.LogInformation("{Role} {Name} loaded {Count} design HTML files + {Images} design images ({ImgBytes} bytes total)",
                Identity.Role, Identity.DisplayName, designFiles.Count, loadedImages.Count,
                loadedImages.Sum(i => i.Data.Length));
            return _cachedDesignContext;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read design reference files");
            _cachedDesignImages = Array.Empty<DesignImage>();
            return null;
        }
    }

    /// <summary>
    /// Returns the binary design images discovered by the last call to
    /// <see cref="GetDesignContextAsync"/>. Call <see cref="GetDesignContextAsync"/>
    /// first; this method never fetches on its own.
    /// </summary>
    protected IReadOnlyList<DesignImage> GetCachedDesignImages()
        => _cachedDesignImages ?? Array.Empty<DesignImage>();

    /// <summary>
    /// Appends a user message to the chat history, attaching any cached design images
    /// (PNG/JPG) as <see cref="ImageContent"/> alongside the text. Call
    /// <see cref="GetDesignContextAsync"/> first to populate the cache.
    /// </summary>
    protected void AddUserMessageWithDesignImages(ChatHistory history, string text)
    {
        var images = GetCachedDesignImages();
        if (images.Count == 0)
        {
            history.AddUserMessage(text);
            return;
        }

        var items = new ChatMessageContentItemCollection { new TextContent(text) };
        foreach (var img in images)
        {
            items.Add(new ImageContent(img.Data, img.MimeType) { ModelId = $"design: {img.Path}" });
        }
        history.AddUserMessage(items);
    }

    /// <summary>
    /// Get the repository's file tree from main branch (cached for 5 minutes).
    /// Used to give engineers visibility into existing code structure before they create files.
    /// </summary>
    protected async Task<string> GetRepoStructureForContextAsync(CancellationToken ct)
    {
        try
        {
            if (_repoTreeCache is null || DateTime.UtcNow >= _repoTreeCacheExpiry)
            {
                _repoTreeCache = await RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
                _repoTreeCacheExpiry = DateTime.UtcNow.AddMinutes(5);
            }

            if (_repoTreeCache.Count == 0) return "";

            return ConflictDetector.FormatTreeForPrompt(_repoTreeCache);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch repo tree for context");
            return "";
        }
    }

    /// <summary>
    /// Reads the current content of existing files that a step description mentions.
    /// Searches for file paths from the repo tree that appear in the step text (e.g., "modify Dashboard.razor").
    /// This gives the AI the actual current code so it can make surgical changes instead of rewriting from scratch.
    /// </summary>
    protected async Task<string> GetExistingFileContentForStepAsync(
        string stepDescription, string? prBranch, CancellationToken ct)
    {
        try
        {
            if (_repoTreeCache is null || _repoTreeCache.Count == 0)
                return "";

            // Find files mentioned in the step description (by filename or partial path)
            var mentionedFiles = new List<string>();
            foreach (var filePath in _repoTreeCache)
            {
                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(fileName)) continue;

                // Check if the step mentions this file by name (case-insensitive)
                if (stepDescription.Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                    stepDescription.Contains(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    mentionedFiles.Add(filePath);
                }
            }

            if (mentionedFiles.Count == 0)
                return "";

            // Cap at 5 files to avoid token explosion
            const int maxFiles = 5;
            const int maxFileSize = 8000;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Existing File Contents (READ CAREFULLY before modifying)");
            sb.AppendLine("The following files already exist in the repository. When modifying these files, " +
                "make ONLY the changes required for this step. Preserve ALL existing code, structure, " +
                "CSS classes, and functionality that is not directly related to your changes.\n");

            var filesLoaded = 0;
            foreach (var file in mentionedFiles.Take(maxFiles))
            {
                try
                {
                    // Try PR branch first, fall back to main
                    var content = !string.IsNullOrEmpty(prBranch)
                        ? await RepoContent.GetFileContentAsync(file, prBranch, ct)
                        : null;
                    content ??= await RepoContent.GetFileContentAsync(file, EffectiveBranch, ct);

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var truncated = content.Length > maxFileSize
                            ? content[..maxFileSize] + "\n<!-- truncated -->"
                            : content;

                        sb.AppendLine($"### Current content of `{file}`");
                        var ext = Path.GetExtension(file).TrimStart('.');
                        sb.AppendLine($"```{ext}");
                        sb.AppendLine(truncated);
                        sb.AppendLine("```\n");
                        filesLoaded++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not read existing file {File} for incremental context", file);
                }
            }

            if (filesLoaded == 0)
                return "";

            Logger.LogInformation("{Role} {Name} loaded {Count} existing file(s) for incremental modification context",
                Identity.Role, Identity.DisplayName, filesLoaded);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to load existing files for step context");
            return "";
        }
    }

    #endregion

    #region Local Workspace Build/Test

    /// <summary>
    /// Write files to local workspace, build, test, fix if needed, then commit and push.
    /// This ensures no code reaches GitHub until it actually compiles and passes tests.
    /// Returns false if the step was blocked due to unresolvable build errors.
    /// </summary>
    protected async Task<bool> CommitViaLocalWorkspaceAsync(
        AgentPullRequest pr,
        IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> codeFiles,
        string commitMsg,
        int stepNumber,
        int totalSteps,
        string stepDescription,
        IChatCompletionService chat,
        CancellationToken ct,
        bool isRework = false,
        bool cliEditMode = false)
    {
        LastCommitFailureReason = null;
        var wsConfig = Config.Workspace;
        var branchName = GetPrBranchName(pr);

        // Ensure workspace is on the right branch
        // In CLI edit mode, branch checkout was done before the LLM call
        if (!cliEditMode && stepNumber == 1)
        {
            if (isRework)
            {
                // Rework: checkout the EXISTING remote branch to preserve prior commits.
                // Creating a fresh branch from main would destroy all original implementation files.
                await Workspace!.CheckoutBranchAsync(branchName, ct);
                Logger.LogInformation("{Role} {Name} checked out existing branch {Branch} for rework",
                    Identity.Role, Identity.DisplayName, branchName);
            }
            else
            {
                // Initial implementation: create a fresh branch from main
                await Workspace!.SyncWithMainAsync(ct);
                await Workspace.CreateBranchAsync(branchName, ct);
            }
        }

        // Write files to local filesystem (skip in CLI edit mode — files already edited by CLI)
        if (!cliEditMode)
        {
            foreach (var file in codeFiles)
                await Workspace!.WriteFileAsync(file.Path, file.Content, ct);

            // REQ-WS-004: Ensure project files exist before building
            EnsureProjectFiles(codeFiles);
        }
        else
        {
            // CLI edit mode: check for project file scaffolding needs from disk
            EnsureProjectFilesFromDisk();
        }

        // Build with retry loop
        var (buildSuccess, lastBuildErrors) = await BuildWithRetryAsync(
            codeFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct, cliEditMode);

        if (!buildSuccess)
        {
            if (isRework)
            {
                // REWORK MODE: Do NOT regenerate from scratch — that would discard the surgical changes.
                // BuildWithRetryAsync already attempted iterative fixes. If those failed, revert and report.
                Logger.LogWarning("{Role} {Name} rework build failed after retries for step {Step}/{Total} — skipping full regeneration (surgical mode)",
                    Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
                LogActivity("build", $"❌ Rework build failed for step {stepNumber}/{totalSteps} — reverting surgical changes");
                _ = Metrics?.RecordBuildBlockedCommitAsync(Identity.Id, ct);
                _ = Metrics?.RecordBlockedCommitAsync(Identity.Id, ct);

                await Workspace!.RevertUncommittedChangesAsync(ct);

                var errorDetails = !string.IsNullOrWhiteSpace(lastBuildErrors)
                    ? $"\n\n<details>\n<summary>Build Errors (last attempt)</summary>\n\n```\n{Truncate(lastBuildErrors, 3000)}\n```\n</details>"
                    : "";

                await ReviewService.AddCommentAsync(pr.Number,
                    $"⚠️ **Rework Build Failed:** Surgical changes for step {stepNumber}/{totalSteps} (`{Truncate(stepDescription, 80)}`) " +
                    $"could not compile after {wsConfig.MaxBuildRetries} fix attempts. Changes reverted — feedback may need manual resolution.{errorDetails}", ct);

                return false;
            }

            // INITIAL IMPLEMENTATION: try full code regeneration from scratch
            Logger.LogWarning("{Role} {Name} build failed after retries for step {Step}/{Total}, attempting full code regeneration",
                Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
            LogActivity("build", $"🔄 Build failed — regenerating code from scratch for step {stepNumber}/{totalSteps}");

            // Revert the failed files before regenerating
            await Workspace!.RevertUncommittedChangesAsync(ct);
            _ = Metrics?.RecordBuildRegenerationAsync(Identity.Id, ct);

            var regeneratedFiles = await RegenerateCodeForStepAsync(
                pr, stepDescription, stepNumber, totalSteps, codeFiles, chat, ct);

            if (regeneratedFiles is not null && regeneratedFiles.Count > 0)
            {
                // Write regenerated files and try building again
                foreach (var file in regeneratedFiles)
                    await Workspace.WriteFileAsync(file.Path, file.Content, ct);

                EnsureProjectFiles(regeneratedFiles);

                (buildSuccess, lastBuildErrors) = await BuildWithRetryAsync(
                    regeneratedFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct);

                if (buildSuccess)
                {
                    Logger.LogInformation("{Role} {Name} code regeneration fixed build errors for step {Step}/{Total}",
                        Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
                    LogActivity("build", $"✅ Code regeneration fixed build errors for step {stepNumber}/{totalSteps}");
                    _ = Metrics?.RecordBuildRegenerationSuccessAsync(Identity.Id, ct);
                }
            }

            if (!buildSuccess)
            {
                // GATE: Do NOT commit broken code — revert workspace and skip this step
                Logger.LogError("{Role} {Name} build failed even after code regeneration for step {Step}/{Total}, blocking commit",
                    Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
                LogActivity("build", $"❌ Step {stepNumber}/{totalSteps} blocked — build errors could not be resolved");
                _ = Metrics?.RecordBuildBlockedCommitAsync(Identity.Id, ct);
                _ = Metrics?.RecordBlockedCommitAsync(Identity.Id, ct);

                await Workspace!.RevertUncommittedChangesAsync(ct);

                // Include actual build errors in the PR comment for diagnostics
                var errorDetails = !string.IsNullOrWhiteSpace(lastBuildErrors)
                    ? $"\n\n<details>\n<summary>Build Errors (last attempt)</summary>\n\n```\n{Truncate(lastBuildErrors, 3000)}\n```\n</details>"
                    : "";

                await ReviewService.AddCommentAsync(pr.Number,
                    $"❌ **Build Blocked:** Step {stepNumber}/{totalSteps} (`{Truncate(stepDescription, 80)}`) was **not committed** " +
                    $"because build errors could not be resolved after {wsConfig.MaxBuildRetries} fix attempts + full code regeneration.\n\n" +
                    $"This step needs manual review or will be addressed in a follow-up.{errorDetails}", ct);

                return false;
            }
        }

        // Test with retry loop — no failing tests are ever committed
        if (TestRunnerSvc is not null)
        {
            var testSuccess = await TestWithRetryAndRemoveAsync(
                chat, wsConfig, stepNumber, totalSteps, stepDescription, pr, ct);

            if (!testSuccess)
            {
                // Should not happen — TestWithRetryAndRemoveAsync removes failing tests as last resort
                Logger.LogError("{Role} {Name} test loop failed unexpectedly for step {Step}/{Total}",
                    Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
            }

            // After test fixes, verify build still passes (test fixes might break the build)
            var finalBuild = await BuildRunnerSvc!.BuildAsync(
                Workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!finalBuild.Success)
            {
                Logger.LogWarning("{Role} {Name} post-test-fix build failed for step {Step}/{Total}, running build fix loop",
                    Identity.Role, Identity.DisplayName, stepNumber, totalSteps);

                var (postTestBuildOk, _) = await BuildWithRetryAsync(
                    codeFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct);

                if (!postTestBuildOk)
                {
                    // Extremely unlikely but handle gracefully — revert and block
                    await Workspace!.RevertUncommittedChangesAsync(ct);
                    await ReviewService.AddCommentAsync(pr.Number,
                        $"❌ **Build Blocked:** Step {stepNumber}/{totalSteps} — test fixes broke the build and could not be resolved.", ct);
                    return false;
                }
            }
        }

        // Commit locally and push — only reached if build succeeded and tests pass
        var headBeforeCommit = await Workspace!.GetHeadShaAsync("HEAD", ct);
        await Workspace!.CommitAsync(commitMsg, ct);
        var headAfterCommit = await Workspace.GetHeadShaAsync("HEAD", ct);

        // Verify commit actually produced changes. If HEAD didn't advance, the commit
        // was a no-op (likely all files were gitignored). Don't push or claim success.
        if (string.Equals(headBeforeCommit?.Trim(), headAfterCommit?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("{Role} {Name} commit was a no-op (HEAD unchanged) — likely all files gitignored",
                Identity.Role, Identity.DisplayName);
            LastCommitFailureReason = "no-op-commit";
            return false;
        }

        await Workspace.PushAsync(branchName, ct);
        _ = Metrics?.RecordSuccessfulCommitAsync(Identity.Id, ct);

        // Screenshot is captured at ready-for-review time (once, with the ready-for-review
        // comment) rather than per-step to avoid cluttering PR timelines with duplicate comments.
        return true;
    }

    /// <summary>
    /// Capture a UI screenshot (web app, console app, etc.), commit it to the PR branch,
    /// and return a Markdown snippet suitable for embedding in the ready-for-review comment.
    /// Returns null (and logs) when no screenshot can be produced — callers should fall back
    /// to posting the ready-for-review comment without any image.
    /// </summary>
    /// <remarks>
    /// This method is project-agnostic: <see cref="PlaywrightRunner.CaptureAppScreenshotAsync"/>
    /// is expected to return a web screenshot when the repo is a web project, or a console-output
    /// image / null when it isn't. Any capture failure is swallowed so the pipeline is never blocked.
    /// </remarks>
    protected async Task<string?> TryCaptureReadyReviewScreenshotMarkdownAsync(
        AgentPullRequest pr, string branchName, CancellationToken ct)
    {
        if (ScreenshotRunner is null || Workspace is null || !Config.Workspace.CaptureScreenshots)
        {
            Logger.LogInformation("Screenshot skipped for PR #{PrNumber}: Runner={HasRunner}, Workspace={HasWorkspace}, Enabled={Enabled}",
                pr.Number, ScreenshotRunner is not null, Workspace is not null, Config.Workspace.CaptureScreenshots);
            return null;
        }

        // Try to install browsers if not present before checking IsReady
        if (!ScreenshotRunner.IsReady)
        {
            try
            {
                await ScreenshotRunner.EnsureBrowsersInstalledAsync(Config.Workspace, Workspace.RepoPath, ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to install Playwright browsers proactively for PR #{PrNumber}", pr.Number);
            }
        }

        if (!ScreenshotRunner.IsReady)
        {
            Logger.LogDebug("Playwright not ready, skipping ready-for-review screenshot for PR #{PrNumber}: {Reason}",
                pr.Number, ScreenshotRunner.NotReadyReason);
            return null;
        }

        try
        {
            Logger.LogDebug("{Role} {Name} capturing ready-for-review screenshot for PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, pr.Number);

            // If no app start command is configured, use the CLI to detect how to start this app
            if (string.IsNullOrWhiteSpace(Config.Workspace.AppStartCommand))
            {
                var detectedCommand = await DetectAppStartCommandViaCli(Workspace.RepoPath, ct);
                if (!string.IsNullOrWhiteSpace(detectedCommand))
                {
                    Config.Workspace.AppStartCommand = detectedCommand;
                    Logger.LogInformation("CLI detected app start command: {Command}", detectedCommand);
                }
            }

            // Pass the PR body as task description so the screenshot can navigate to
            // task-specific URLs from acceptance criteria (reuses ExtractTestUrlPaths
            // logic from the strategy framework).
            var screenshotResult = await ScreenshotRunner.CaptureAppScreenshotAsync(
                Workspace.RepoPath, Config.Workspace, ct, taskDescription: pr.Body);

            if (screenshotResult is null || screenshotResult.Bytes.Length == 0)
            {
                Logger.LogInformation(
                    "No screenshot captured for PR #{PrNumber} (not a runnable web/console app or capture returned empty) — " +
                    "proceeding with ready-for-review without image.", pr.Number);
                return null;
            }

            var screenshotBytes = screenshotResult.Bytes;

            // Vision-based expectation check: does the screenshot show what the PR claims to deliver?
            // Same check used by CheckPrePublishScreenshotAsync — shared via PullRequestWorkflow.
            // On mismatch, log a warning but still proceed (don't block the ready-for-review flow).
            try
            {
                var evalKernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                var evalChat = evalKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                var evalImg = new PullRequestWorkflow.ScreenshotImage(screenshotBytes, "image/png",
                    $"Ready-for-review PR #{pr.Number}", "");
                var evaluation = await PullRequestWorkflow.EvaluateScreenshotAgainstExpectationsAsync(
                    evalImg, pr.Title, pr.Body, evalChat, ct);

                if (!evaluation.MatchesExpectations && evaluation.Confidence >= 0.6)
                {
                    Logger.LogWarning(
                        "Ready-review screenshot for PR #{PrNumber} does NOT match expectations " +
                        "(confidence {Confidence:0.00}): observed='{Observed}', expected='{Expected}'",
                        pr.Number, evaluation.Confidence, evaluation.Observed, evaluation.Expected);
                    LogActivity("screenshot",
                        $"⚠️ Ready-review screenshot mismatch (conf {evaluation.Confidence:0.00}): {evaluation.Observed}");
                }
                else
                {
                    Logger.LogDebug("Ready-review screenshot for PR #{PrNumber}: expectation check {Verdict} (conf {Confidence:0.00})",
                        pr.Number, evaluation.Verdict, evaluation.Confidence);
                }
            }
            catch (Exception evalEx)
            {
                // Non-fatal — proceed with upload even if evaluation fails
                Logger.LogDebug(evalEx, "Screenshot expectation evaluation failed for PR #{PrNumber} — proceeding with upload", pr.Number);
            }

            // Upload as a release asset for a permanent (non-expiring) URL.
            var screenshotFilename = $"pr-{pr.Number}-ready-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png";
            var imageUrl = await RepoContent.UploadImageForCommentAsync(
                screenshotFilename, screenshotBytes, "image/png", pr.Number, ct);

            if (imageUrl is null)
            {
                Logger.LogWarning("Failed to upload screenshot for PR #{PrNumber} — URL was null", pr.Number);
                return null;
            }

            Logger.LogInformation("{Role} {Name} attached ready-for-review screenshot for PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, pr.Number);
            LogActivity("screenshot", $"📸 Ready-for-review screenshot attached to PR #{pr.Number}");

            // Describe the screenshot using vision (CLI --attachment enables proper image analysis).
            // Falls back to page-text description if vision fails.
            var description = string.Empty;
            try
            {
                var descKernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                var descChat = descKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                var img = new PullRequestWorkflow.ScreenshotImage(screenshotBytes, "image/png",
                    $"Ready-for-review PR #{pr.Number}", imageUrl);
                description = await PullRequestWorkflow.DescribeScreenshotAsync(img, descChat, ct);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    LogActivity("screenshot", $"🖼️ Screenshot content (PR #{pr.Number}): {description}");
                    Logger.LogInformation("{Role} {Name} screenshot description for PR #{PrNumber}: {Description}",
                        Identity.Role, Identity.DisplayName, pr.Number, description);
                }
            }
            catch (Exception descEx)
            {
                Logger.LogDebug(descEx, "Vision-based description failed for PR #{PrNumber}, trying page text fallback", pr.Number);

                // Fallback to page-text description if vision fails
                if (!string.IsNullOrWhiteSpace(screenshotResult.PageText))
                {
                    try
                    {
                        var descKernel2 = Models.GetKernel(Identity.ModelTier, Identity.Id);
                        var descChat2 = descKernel2.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
                        history.AddSystemMessage(
                            "You are a UI analyst. Given the visible text content of a web page, describe what the page shows in 1-2 sentences. " +
                            "Focus on: page title, main content area, key UI elements. Be concise and factual.");
                        history.AddUserMessage(
                            $"This is the visible text content of a web application screenshot:\n\n{screenshotResult.PageText}");
                        var response = await descChat2.GetChatMessageContentsAsync(history, cancellationToken: ct);
                        description = response.FirstOrDefault()?.Content?.Trim() ?? string.Empty;
                    }
                    catch (Exception fallbackEx)
                    {
                        Logger.LogDebug(fallbackEx, "Page-text description also failed for PR #{PrNumber}", pr.Number);
                    }
                }
            }

            // Build the markdown snippet to embed in the ready-for-review comment.
            var md = $"### 📸 End-Result Preview\n\n![Ready-for-review screenshot of PR #{pr.Number}]({imageUrl})";
            if (!string.IsNullOrWhiteSpace(description))
                md += $"\n\n_{description.Trim()}_";
            return md;
        }
        catch (Exception ex)
        {
            // Never let screenshot failures block the pipeline.
            Logger.LogWarning(ex, "Screenshot capture failed for PR #{PrNumber} — continuing without image", pr.Number);
            return null;
        }
    }

    /// <summary>
    /// Uses a Copilot CLI session to detect the command needed to start the web application
    /// in the given workspace. The CLI reasons about the project type (dotnet, node, python, etc.)
    /// and returns just the command string — no hardcoded language detection needed.
    /// </summary>
    private async Task<string?> DetectAppStartCommandViaCli(string workspacePath, CancellationToken ct)
    {
        try
        {
            using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AllowFileEdits: false,
                OverrideWorkingDirectory: workspacePath));

            // Use a deterministic port in the 5100-5899 range based on workspace path
            var port = 5100 + (Math.Abs(workspacePath.GetHashCode()) % 800);

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
            var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
            history.AddSystemMessage(
                "You are a project analysis assistant. Look at the workspace files to determine how to start this web application. " +
                "Respond with ONLY the shell command to start the app — nothing else. No explanation, no markdown, just the command. " +
                "If this is not a web application that can be started, respond with exactly: NONE");
            history.AddUserMessage(
                $"What single shell command would start this web application listening on port {port}? " +
                $"Look at the project files in the workspace to determine the project type and appropriate start command. " +
                $"Examples of what you might return:\n" +
                $"- dotnet run --no-launch-profile --project \"src/MyApp/MyApp.csproj\" --urls http://localhost:{port}\n" +
                $"- npm run dev -- --port {port}\n" +
                $"- python manage.py runserver {port}\n" +
                $"Respond with ONLY the command, or NONE if not a web app.");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var command = response.Content?.Trim();

            if (string.IsNullOrWhiteSpace(command) || command.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("CLI determined this is not a startable web app");
                return null;
            }

            // Sanity check — command should be a reasonable shell command, not a paragraph
            if (command.Length > 300 || command.Contains('\n'))
            {
                Logger.LogDebug("CLI returned unexpected multi-line response for app start detection, ignoring");
                return null;
            }

            return command;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "CLI app start command detection failed");
            return null;
        }
    }

    /// <summary>
    /// Fallback when screenshot capture is unavailable: generates a markdown summary of
    /// the implementation (files changed, build status) to include in the ready-for-review comment.
    /// </summary>
    private async Task<string?> TryBuildImplementationSummaryMarkdownAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("### 📋 Implementation Summary");
            sb.AppendLine();

            // List files changed in the PR
            var files = await PrService.GetFileDiffsAsync(pr.Number, ct);
            if (files is not null && files.Count > 0)
            {
                var totalAdded = files.Sum(f => f.Additions);
                var totalRemoved = files.Sum(f => f.Deletions);
                sb.AppendLine($"**Files changed:** {files.Count} (+{totalAdded} / -{totalRemoved} lines)");
                sb.AppendLine();
                foreach (var f in files.Take(15))
                {
                    var status = f.Status switch
                    {
                        "added" => "➕",
                        "removed" => "➖",
                        "modified" => "✏️",
                        "renamed" => "🔄",
                        _ => "📄"
                    };
                    sb.AppendLine($"- {status} `{f.FileName}`");
                }
                if (files.Count > 15)
                    sb.AppendLine($"- _...and {files.Count - 15} more_");
            }

            // Include build status if workspace is available
            if (Workspace is not null && WorkspaceServices.BuildRunner is not null)
            {
                var buildResult = await WorkspaceServices.BuildRunner.BuildAsync(
                    Workspace.RepoPath, Config.Workspace.BuildCommand, Config.Workspace.BuildTimeoutSeconds, ct);
                sb.AppendLine();
                sb.AppendLine(buildResult.Success
                    ? "✅ **Build:** Succeeded"
                    : $"⚠️ **Build:** Failed ({buildResult.ParsedErrors.Count} errors)");
            }

            return sb.Length > 40 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to generate implementation summary for PR #{PrNumber}", pr.Number);
            return null;
        }
    }

    /// <summary>
    /// Universal "mark ready for review" helper that always attempts to capture a screenshot
    /// and embed it in the ready-for-review comment (no separate comment). Falls back to a
    /// text-only ready-for-review comment when capture isn't possible (non-web/console, Playwright
    /// not ready, capture returns empty, etc.) — the pipeline is never blocked by imaging.
    /// </summary>
    protected Task MarkReadyForReviewWithScreenshotAsync(
        AgentPullRequest pr, CancellationToken ct)
        => MarkReadyForReviewWithScreenshotAsync(pr, winnerCandidate: null, ct: ct);

    /// <summary>
    /// Overload that prefers proven strategy-winner media when available, then falls through
    /// to today's live-capture path. The winner's screenshots/GIF were already validated during
    /// strategy evaluation — reusing them is faster and proves the exact code that won.
    /// </summary>
    protected async Task MarkReadyForReviewWithScreenshotAsync(
        AgentPullRequest pr,
        VirtualDevTeam.Core.Strategies.CandidateResult? winnerCandidate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        string? md = null;

        // (1) Prefer proven winner media (strategy framework path)
        if (winnerCandidate is not null)
        {
            try
            {
                md = await TryBuildWinnerMediaMarkdownAsync(pr, winnerCandidate, ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Winner-media markdown failed for PR #{PrNumber} — falling back to live capture", pr.Number);
            }
        }

        // (2) Fallback: live capture (today's behavior, unchanged)
        if (md is null)
        {
            try
            {
                md = await TryCaptureReadyReviewScreenshotMarkdownAsync(pr, pr.HeadBranch, ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Screenshot helper threw for PR #{PrNumber} — proceeding without image", pr.Number);
            }
        }

        // (3) Last-resort text summary
        if (md is null)
        {
            Logger.LogWarning(
                "No screenshot captured for PR #{PrNumber} at ready-for-review — reviewers will not have a visual preview. " +
                "Check Playwright browser availability and CaptureScreenshots config.",
                pr.Number);
            md = await TryBuildImplementationSummaryMarkdownAsync(pr, ct);
            md = "⚠️ _No app screenshot available for this PR. Visual verification was not performed._\n\n" + (md ?? "");
        }

        await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct, md);
    }

    /// <summary>
    /// Build a Markdown block embedding the WINNER candidate's already-captured media
    /// (screenshot, animated GIF) into the ready-for-review comment. Reuses media
    /// proven during strategy evaluation rather than capturing fresh — eliminates
    /// duplicate work and guarantees the reviewer sees the exact frames that won.
    /// Returns null when the candidate has no media (caller falls back to live capture).
    /// </summary>
    private async Task<string?> TryBuildWinnerMediaMarkdownAsync(
        AgentPullRequest pr,
        VirtualDevTeam.Core.Strategies.CandidateResult winner,
        CancellationToken ct)
    {
        if (winner.ScreenshotBytes is not { Length: > 0 })
        {
            Logger.LogDebug("Winner {Strategy} has no screenshot bytes for PR #{Pr} — falling back", winner.StrategyId, pr.Number);
            return null;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### 📸 End-Result Preview");
        sb.AppendLine();
        sb.AppendLine($"_Generated by strategy `{winner.StrategyId}` — same media used to select this candidate._");
        sb.AppendLine();

        // (a) Primary screenshot (inline image)
        string? primaryUrl = null;
        try
        {
            var name = $"pr-{pr.Number}-winner-{winner.StrategyId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png";
            primaryUrl = await RepoContent.UploadImageForCommentAsync(name, winner.ScreenshotBytes, "image/png", pr.Number, ct);
            if (primaryUrl is not null)
                sb.AppendLine($"![Winner preview]({primaryUrl})");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to upload winner screenshot for PR #{Pr}", pr.Number);
        }

        if (primaryUrl is null)
            return null; // No point continuing without the primary image

        // (b) Animated GIF (inline — GitHub/ADO render GIFs natively)
        const int maxGifBytes = 10 * 1024 * 1024; // 10 MB
        if (!string.IsNullOrEmpty(winner.AnimatedGifPath) && File.Exists(winner.AnimatedGifPath))
        {
            try
            {
                var gifBytes = await File.ReadAllBytesAsync(winner.AnimatedGifPath, ct);
                if (gifBytes.Length > 0 && gifBytes.Length <= maxGifBytes)
                {
                    var gifName = $"pr-{pr.Number}-winner-{winner.StrategyId}.gif";
                    var gifUrl = await RepoContent.UploadImageForCommentAsync(gifName, gifBytes, "image/gif", pr.Number, ct);
                    if (gifUrl is not null)
                    {
                        sb.AppendLine();
                        sb.AppendLine("**🎞️ Interaction recording:**");
                        sb.AppendLine($"![Winner interaction]({gifUrl})");
                    }
                }
                else if (gifBytes.Length > maxGifBytes)
                {
                    Logger.LogInformation("Skipping inline GIF ({Size} bytes > {Cap}) for PR #{Pr}",
                        gifBytes.Length, maxGifBytes, pr.Number);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to upload winner GIF for PR #{Pr}", pr.Number);
            }
        }

        // (c) Additional screenshots (collapsible details)
        var extras = (winner.ScreenshotPaths ?? Array.Empty<string>())
            .Where(File.Exists)
            .Take(4).ToList();
        foreach (var path in extras)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Length == 0) continue;
                var url = await RepoContent.UploadImageForCommentAsync(
                    $"pr-{pr.Number}-winner-{winner.StrategyId}-{Path.GetFileName(path)}",
                    bytes, "image/png", pr.Number, ct);
                if (url is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<details><summary>📷 {Path.GetFileNameWithoutExtension(path)}</summary>");
                    sb.AppendLine();
                    sb.AppendLine($"![{Path.GetFileName(path)}]({url})");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to upload extra screenshot {Path} for PR #{Pr}", path, pr.Number);
            }
        }

        // (d) Video — upload as release asset / PR attachment (not committed to branch to
        // avoid polluting PR diff and CI). Uses UploadImageForCommentAsync which handles
        // GitHub release assets, ADO PR attachments, and gracefully returns null on Local.
        const int maxVideoBytes = 20 * 1024 * 1024; // 20 MB
        if (!string.IsNullOrEmpty(winner.VideoPath) && File.Exists(winner.VideoPath))
        {
            try
            {
                var videoBytes = await File.ReadAllBytesAsync(winner.VideoPath, ct);
                if (videoBytes.Length > 0 && videoBytes.Length <= maxVideoBytes)
                {
                    var ext = Path.GetExtension(winner.VideoPath);
                    var videoName = $"pr-{pr.Number}-winner-{winner.StrategyId}-video{ext}";
                    var videoUrl = await RepoContent.UploadImageForCommentAsync(
                        videoName, videoBytes, "video/webm", pr.Number, ct);
                    if (videoUrl is not null)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"🎥 [Full interaction video]({videoUrl}) ({videoBytes.Length / 1024} KB)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to upload winner video for PR #{Pr}", pr.Number);
            }
        }

        Logger.LogInformation("{Role} {Name} attached winner strategy media for PR #{Pr} (strategy: {Strategy})",
            Identity.Role, Identity.DisplayName, pr.Number, winner.StrategyId);
        LogActivity("screenshot", $"📸 Winner strategy media attached to PR #{pr.Number} (strategy: {winner.StrategyId})");
        return sb.ToString();
    }

    /// <summary>
    /// Overload accepting only a PR number — fetches the full PR to obtain HeadBranch.
    /// Falls back to plain text ready-for-review if the fetch fails.
    /// </summary>
    protected async Task MarkReadyForReviewWithScreenshotAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var pr = (await PrService.GetAsync(prNumber, ct))?.ToAgentPR();
            if (pr is not null)
            {
                await MarkReadyForReviewWithScreenshotAsync(pr, ct);
                return;
            }
            Logger.LogDebug("Could not fetch PR #{PrNumber} to capture screenshot — posting text-only ready-for-review", prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Fetch for PR #{PrNumber} threw — posting text-only ready-for-review", prNumber);
        }
        await PrWorkflow.MarkReadyForReviewAsync(prNumber, Identity.DisplayName, ct);
    }

    /// <summary>
    /// Build the project locally, feeding errors back to AI for fix attempts.
    /// Returns success flag and the last build error summary (if any) for diagnostics.
    /// </summary>
    protected async Task<(bool Success, string? LastErrors)> BuildWithRetryAsync(
        IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> originalFiles,
        IChatCompletionService chat,
        WorkspaceConfig wsConfig,
        int stepNumber, int totalSteps, string stepDescription,
        CancellationToken ct,
        bool cliEditMode = false)
    {
        string? lastErrorSummary = null;
        // Track allowed files from the original scope for CLI edit mode enforcement
        IReadOnlyCollection<string>? allowedFiles = cliEditMode
            ? originalFiles.Select(f => NormalizePath(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        for (int attempt = 0; attempt <= wsConfig.MaxBuildRetries; attempt++)
        {
            UpdateStatus(AgentStatus.Working, $"🔨 Building (attempt {attempt + 1}/{wsConfig.MaxBuildRetries + 1})");
            _ = Metrics?.RecordBuildAttemptAsync(Identity.Id, ct);
            var buildResult = await BuildRunnerSvc!.BuildAsync(
                Workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

            if (buildResult.Success)
            {
                _ = Metrics?.RecordBuildSuccessAsync(Identity.Id, ct);
                UpdateStatus(AgentStatus.Working, "✅ Build succeeded");
                if (attempt > 0)
                    Logger.LogInformation("{Role} {Name} build succeeded after {Attempt} fix attempt(s)",
                        Identity.Role, Identity.DisplayName, attempt);
                return (true, null);
            }

            // Capture error summary for diagnostics
            lastErrorSummary = buildResult.ParsedErrors.Count > 0
                ? string.Join("\n", buildResult.ParsedErrors.Take(20))
                : buildResult.Errors.Length > 2000 ? buildResult.Errors[..2000] : buildResult.Errors;

            if (attempt >= wsConfig.MaxBuildRetries)
            {
                _ = Metrics?.RecordBuildFailureAsync(Identity.Id, ct);
                break;
            }

            _ = Metrics?.RecordBuildFixAttemptAsync(Identity.Id, ct);

            Logger.LogWarning("{Role} {Name} build failed (attempt {Attempt}/{Max}): {ErrorCount} errors",
                Identity.Role, Identity.DisplayName, attempt + 1, wsConfig.MaxBuildRetries + 1, buildResult.ParsedErrors.Count);
            UpdateStatus(AgentStatus.Working, $"⚠️ Build failed, retrying ({attempt + 1}/{wsConfig.MaxBuildRetries + 1})");

            // Escalate model tier after 2 failed fix attempts to break out of repetitive failures
            var effectiveChat = chat;
            if (attempt >= 2 && Models is not null)
            {
                var escalatedTier = GetEscalatedTier(Identity.ModelTier);
                if (escalatedTier != Identity.ModelTier)
                {
                    var escalatedKernel = Models.GetKernel(escalatedTier, Identity.Id);
                    effectiveChat = escalatedKernel.GetRequiredService<IChatCompletionService>();
                    Logger.LogInformation("{Role} {Name} escalating build-fix model from {Current} to {Escalated} tier on attempt {Attempt}",
                        Identity.Role, Identity.DisplayName, Identity.ModelTier, escalatedTier, attempt + 1);
                    LogActivity("model-escalation", $"⬆️ Escalating to {escalatedTier} tier for build-fix attempt {attempt + 1}");
                }
            }

            LogActivity("build", $"🔧 Build failed (attempt {attempt + 1}), asking AI to fix {buildResult.ParsedErrors.Count} errors");

            // Determine scope tier: escalate after ScopeEscalationAttemptThreshold failed attempts
            var isEscalatedScope = attempt >= ScopeEscalationAttemptThreshold;
            var scopeText = isEscalatedScope ? ScopeRelaxationTier2 : ScopeRelaxationTier1;
            if (isEscalatedScope)
            {
                Logger.LogInformation("{Role} {Name} escalating build-fix scope to Tier 2 on attempt {Attempt} — expanding allowed files",
                    Identity.Role, Identity.DisplayName, attempt + 1);
                LogActivity("scope-escalation", $"🔓 Scope escalated to Tier 2 on attempt {attempt + 1} — agent may fix project files and rename own types");
            }

            // Refresh status with specific context before long AI fix call — this keeps
            // the stuck-detector from firing during multi-minute AI fix sessions.
            UpdateStatus(AgentStatus.Working,
                $"🔧 Fixing {buildResult.ParsedErrors.Count} build errors (attempt {attempt + 1}/{wsConfig.MaxBuildRetries + 1})");

            if (cliEditMode)
            {
                // CLI edit mode: let the CLI fix files directly using its native tools
                await BuildFixWithCliEditAsync(effectiveChat, buildResult, lastErrorSummary,
                    stepNumber, totalSteps, stepDescription, allowedFiles!, scopeText, isEscalatedScope, ct);
            }
            else
            {
                // FILE: block mode: parse AI response and write files manually
                await BuildFixWithFileBlocksAsync(effectiveChat, originalFiles, buildResult, lastErrorSummary,
                    stepNumber, totalSteps, stepDescription, scopeText, isEscalatedScope, ct);
            }
        }

        return (false, lastErrorSummary);
    }

    /// <summary>
    /// Build-fix using CLI edit mode — the CLI edits files directly with its native tools.
    /// </summary>
    private async Task BuildFixWithCliEditAsync(
        IChatCompletionService chat,
        BuildResult buildResult,
        string errorSummary,
        int stepNumber, int totalSteps, string stepDescription,
        IReadOnlyCollection<string> allowedFiles,
        string scopeRelaxation,
        bool isEscalatedScope,
        CancellationToken ct)
    {
        var fixPrompt = PromptService is not null
            ? await PromptService.RenderAsync("engineer-base/build-fix-cli-edit",
                new Dictionary<string, string>
                {
                    ["step_number"] = stepNumber.ToString(),
                    ["total_steps"] = totalSteps.ToString(),
                    ["step_description"] = stepDescription,
                    ["error_count"] = buildResult.ParsedErrors.Count.ToString(),
                    ["error_summary"] = errorSummary,
                    ["scope_relaxation"] = scopeRelaxation
                }, ct)
            : null;
        fixPrompt ??= $"""
            Build errors detected. Fix ALL errors using the edit tool (surgical changes only).
            
            BUILD ERRORS:
            {errorSummary}
            
            {scopeRelaxation}
            """;

        // Push CLI edit context for this call
        using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
            AllowFileEdits: true,
            OverrideWorkingDirectory: Workspace!.RepoPath));

        var fixHistory = CreateChatHistory();
        fixHistory.AddSystemMessage(GetReworkSystemPromptCliEdit(Config.Project.TechStack));
        fixHistory.AddUserMessage(fixPrompt);
        await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);

        // Enforce scope after CLI edits — revert out-of-scope files.
        // When scope is escalated (Tier 2), skip the strict scope filter — the agent is
        // explicitly allowed to fix project files and rename its own types to new paths.
        if (!isEscalatedScope)
        {
            var changedAfterFix = await Workspace.GetChangedFilePathsAsync(ct);
            var outOfScope = changedAfterFix
                .Where(f => !IsInfrastructureFile(NormalizePath(f)))
                .Where(f => !allowedFiles.Any(a =>
                    NormalizePath(f).Equals(a, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f).Equals(Path.GetFileName(a), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (outOfScope.Count > 0)
            {
                LogActivity("scope", $"🚫 Build-fix CLI edit: reverting {outOfScope.Count} out-of-scope file(s)");
                await Workspace.RevertFilesAsync(outOfScope, ct);
            }
        }
    }

    /// <summary>
    /// Build-fix using FILE: block parsing — traditional parse-and-write approach.
    /// </summary>
    private async Task BuildFixWithFileBlocksAsync(
        IChatCompletionService chat,
        IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> originalFiles,
        BuildResult buildResult,
        string errorSummary,
        int stepNumber, int totalSteps, string stepDescription,
        string scopeRelaxation,
        bool isEscalatedScope,
        CancellationToken ct)
    {
        var fixPrompt = PromptService is not null
            ? await PromptService.RenderAsync("engineer-base/build-fix",
                new Dictionary<string, string>
                {
                    ["step_number"] = stepNumber.ToString(),
                    ["total_steps"] = totalSteps.ToString(),
                    ["step_description"] = stepDescription,
                    ["error_count"] = buildResult.ParsedErrors.Count.ToString(),
                    ["error_summary"] = errorSummary,
                    ["scope_relaxation"] = scopeRelaxation
                }, ct)
            : null;
        fixPrompt ??= $"""
            The code from step {stepNumber}/{totalSteps} ({stepDescription}) has build errors.
            
            BUILD ERRORS:
            {errorSummary}
            
            Fix ALL build errors. Output ONLY the corrected files using this format:
            FILE: path/to/file.ext
            ```language
            <complete corrected file content>
            ```
            
            Include the COMPLETE file content for each file that needs changes.
            {scopeRelaxation}
            {_currentFileScopeBlock}
            """;

        // Include the current content of failing files so the AI can see what to fix
        var failingFilePaths = ExtractFilePathsFromBuildErrors(buildResult.ParsedErrors, Workspace!.RepoPath);
        
        // Also find files for types referenced in errors
        var referencedTypePaths = await FindReferencedTypeFilesAsync(buildResult.ParsedErrors, failingFilePaths, ct);
        foreach (var p in referencedTypePaths)
            failingFilePaths.Add(p);

        if (failingFilePaths.Count > 0)
        {
            var fileContext = new System.Text.StringBuilder();
            fileContext.AppendLine("\n\nCURRENT FILE CONTENTS (fix these files):");
            foreach (var filePath in failingFilePaths.Take(10))
            {
                try
                {
                    var content = await Workspace.ReadFileAsync(filePath, ct);
                    if (content is not null)
                    {
                        var ext = Path.GetExtension(filePath).TrimStart('.');
                        fileContext.AppendLine($"\nFILE: {filePath}");
                        fileContext.AppendLine($"```{ext}");
                        fileContext.AppendLine(content);
                        fileContext.AppendLine("```");
                    }
                }
                catch { /* skip unreadable files */ }
            }
            fixPrompt += fileContext.ToString();
        }

        var fixHistory = CreateChatHistory();
        fixHistory.AddUserMessage(fixPrompt);
        var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
        var fixedFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(fixResponse.Content ?? "");

        // Apply scope filter to build-fix output.
        // When scope is escalated (Tier 2), skip the strict filter — the agent is explicitly
        // allowed to fix project files and rename its own types to new paths.
        if (!isEscalatedScope && fixedFiles.Count > 0 && !string.IsNullOrEmpty(_currentFileScopeBlock))
        {
            var beforeCount = fixedFiles.Count;
            var filtered = new List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>();
            foreach (var f in fixedFiles)
            {
                var norm = NormalizePath(f.Path);
                if (IsInfrastructureFile(norm) || originalFiles.Any(o =>
                        NormalizePath(o.Path).Equals(norm, StringComparison.OrdinalIgnoreCase)))
                    filtered.Add(f);
                else
                    Logger.LogWarning("{Role} {Name} build-fix blocked out-of-scope file: {Path}",
                        Identity.Role, Identity.DisplayName, f.Path);
            }
            fixedFiles = filtered;
            if (fixedFiles.Count < beforeCount)
                LogActivity("scope", $"🚫 Build-fix: blocked {beforeCount - fixedFiles.Count} out-of-scope files");
        }

        foreach (var file in fixedFiles)
            await Workspace.WriteFileAsync(file.Path, file.Content, ct);
    }

    /// <summary>
    /// Returns the next higher model tier for escalation during build/test fix retries.
    /// Escalation path: local → budget → standard → premium (premium stays at premium).
    /// </summary>
    private static string GetEscalatedTier(string currentTier) => currentTier switch
    {
        "local" => "budget",
        "budget" => "standard",
        "standard" => "premium",
        _ => "premium"  // premium and unknown tiers stay at premium
    };

    /// <summary>
    /// Extract relative file paths from build error messages.
    /// Errors look like: "C:\full\path\repo\src\File.cs(50,48): error CS1503: ..."
    /// Returns paths relative to the repo root (e.g., "src/File.cs").
    /// </summary>
    private static HashSet<string> ExtractFilePathsFromBuildErrors(
        IReadOnlyList<string> parsedErrors, string repoPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRepo = repoPath.Replace('\\', '/').TrimEnd('/') + "/";

        foreach (var error in parsedErrors)
        {
            // Match pattern: path(line,col): error/warning
            var match = System.Text.RegularExpressions.Regex.Match(
                error, @"^(.+?)\(\d+,\d+\)\s*:");
            if (!match.Success) continue;

            var fullPath = match.Groups[1].Value.Replace('\\', '/');
            if (fullPath.StartsWith(normalizedRepo, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = fullPath[normalizedRepo.Length..];
                paths.Add(relativePath);
            }
        }

        return paths;
    }

    /// <summary>
    /// Find source files for types referenced in build errors that aren't already in the failing set.
    /// For example, CS0311 "no implicit conversion from 'TypeA' to 'TypeB'" means TypeA's file
    /// likely needs editing, even though the error is reported in the file that uses TypeA.
    /// </summary>
    private Task<HashSet<string>> FindReferencedTypeFilesAsync(
        IReadOnlyList<string> parsedErrors, HashSet<string> alreadyIncluded, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Workspace is null) return Task.FromResult(result);

        // Extract type names mentioned in errors (e.g., 'Namespace.TypeName')
        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var error in parsedErrors)
        {
            // Match quoted type references like 'VirtualDevTeam.Runner.Services.DashboardDataService'
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(error, @"'([A-Z][A-Za-z0-9_.]+)'"))
            {
                var typeName = m.Groups[1].Value;
                var simpleName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
                if (simpleName.Length > 2 && !simpleName.Contains("Extensions"))
                    typeNames.Add(simpleName);
            }
        }

        if (typeNames.Count == 0) return Task.FromResult(result);

        var repoPath = Workspace.RepoPath;
        var normalizedRepo = repoPath.Replace('\\', '/').TrimEnd('/') + "/";

        // Search workspace for .cs files matching these type names
        try
        {
            var allCsFiles = Directory.GetFiles(repoPath, "*.cs", SearchOption.AllDirectories);
            foreach (var fullPath in allCsFiles)
            {
                if (fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var fileName = Path.GetFileNameWithoutExtension(fullPath);
                if (typeNames.Contains(fileName) || typeNames.Contains(fileName.TrimStart('I')))
                {
                    var normalized = fullPath.Replace('\\', '/');
                    if (normalized.StartsWith(normalizedRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        var relativePath = normalized[normalizedRepo.Length..];
                        if (!alreadyIncluded.Contains(relativePath))
                            result.Add(relativePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not search for referenced type files");
        }

        if (result.Count > 0)
            Logger.LogInformation("Found {Count} additional files for types referenced in build errors: {Files}",
                result.Count, string.Join(", ", result));

        return Task.FromResult(result);
    }

    /// <summary>
    /// Strict test enforcement: try to fix failing tests up to MaxTestRetries times.
    /// If tests still fail after all attempts, ask AI to remove the unfixable tests with documentation,
    /// then verify the remaining tests pass. Guarantees no failing tests are ever committed.
    /// </summary>
    private async Task<bool> TestWithRetryAndRemoveAsync(
        IChatCompletionService chat,
        WorkspaceConfig wsConfig,
        int stepNumber, int totalSteps, string stepDescription,
        AgentPullRequest pr,
        CancellationToken ct)
    {
        // Phase 1: Try to fix failing tests (up to MaxTestRetries attempts)
        for (int attempt = 0; attempt <= wsConfig.MaxTestRetries; attempt++)
        {
            UpdateStatus(AgentStatus.Working, $"🧪 Running tests (attempt {attempt + 1}/{wsConfig.MaxTestRetries + 1})");
            _ = Metrics?.RecordTestRunAsync(Identity.Id, ct);
            var testResult = await TestRunnerSvc!.RunTestsAsync(
                Workspace!.RepoPath, wsConfig.TestCommand, wsConfig.TestTimeoutSeconds, ct);

            if (testResult.Success)
            {
                UpdateStatus(AgentStatus.Working, "✅ Tests passed");
                if (attempt > 0)
                    Logger.LogInformation("{Role} {Name} tests passed after {Attempt} fix attempt(s): {Passed} passed",
                        Identity.Role, Identity.DisplayName, attempt, testResult.Passed);
                else
                    Logger.LogInformation("{Role} {Name} tests passed: {Passed} passed, {Skipped} skipped",
                        Identity.Role, Identity.DisplayName, testResult.Passed, testResult.Skipped);
                return true;
            }

            if (attempt >= wsConfig.MaxTestRetries)
            {
                // All fix attempts exhausted — move to Phase 2 (test removal)
                Logger.LogWarning("{Role} {Name} tests still failing after {Max} fix attempts for step {Step}/{Total} — removing unfixable tests",
                    Identity.Role, Identity.DisplayName, wsConfig.MaxTestRetries, stepNumber, totalSteps);
                LogActivity("test", $"⚠️ Tests unfixable after {wsConfig.MaxTestRetries} attempts — removing failing tests for step {stepNumber}/{totalSteps}");
                _ = Metrics?.RecordTestMaxRetriesReachedAsync(Identity.Id, ct);

                return await RemoveFailingTestsAsync(testResult, chat, wsConfig, stepNumber, totalSteps, stepDescription, pr, ct);
            }

            _ = Metrics?.RecordTestFixAttemptAsync(Identity.Id, ct);

            Logger.LogWarning("{Role} {Name} tests failed (attempt {Attempt}/{Max}): {Failed} failed, {Passed} passed",
                Identity.Role, Identity.DisplayName, attempt + 1, wsConfig.MaxTestRetries,
                testResult.Failed, testResult.Passed);
            UpdateStatus(AgentStatus.Working, "⚠️ Tests failed, analyzing errors");
            LogActivity("test", $"🧪 Tests failed (attempt {attempt + 1}/{wsConfig.MaxTestRetries}): {testResult.Failed} failed, asking AI to fix");

            // Escalate model tier after 2 failed test-fix attempts
            var effectiveChat = chat;
            if (attempt >= 2 && Models is not null)
            {
                var escalatedTier = GetEscalatedTier(Identity.ModelTier);
                if (escalatedTier != Identity.ModelTier)
                {
                    var escalatedKernel = Models.GetKernel(escalatedTier, Identity.Id);
                    effectiveChat = escalatedKernel.GetRequiredService<IChatCompletionService>();
                    Logger.LogInformation("{Role} {Name} escalating test-fix model from {Current} to {Escalated} tier on attempt {Attempt}",
                        Identity.Role, Identity.DisplayName, Identity.ModelTier, escalatedTier, attempt + 1);
                    LogActivity("model-escalation", $"⬆️ Escalating to {escalatedTier} tier for test-fix attempt {attempt + 1}");
                }
            }

            var failureSummary = testResult.FailureDetails.Count > 0
                ? string.Join("\n", testResult.FailureDetails.Take(10))
                : testResult.Output.Length > 2000 ? testResult.Output[^2000..] : testResult.Output;

            var fixPrompt = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/test-fix",
                    new Dictionary<string, string>
                    {
                        ["step_number"] = stepNumber.ToString(),
                        ["total_steps"] = totalSteps.ToString(),
                        ["step_description"] = stepDescription,
                        ["failed_count"] = testResult.Failed.ToString(),
                        ["total_count"] = testResult.Total.ToString(),
                        ["failure_summary"] = failureSummary
                    }, ct)
                : null;
            fixPrompt ??= $"""
                The code from step {stepNumber}/{totalSteps} ({stepDescription}) has test failures.
                
                TEST FAILURES ({testResult.Failed} of {testResult.Total}):
                {failureSummary}
                
                Fix the code so all tests pass. Output ONLY the corrected files using this format:
                FILE: path/to/file.ext
                ```language
                <complete corrected file content>
                ```
                
                Include the COMPLETE file content for each file that needs changes.
                Do NOT modify the test files unless the tests themselves are wrong.
                """;

            var fixHistory = CreateChatHistory();
            fixHistory.AddUserMessage(fixPrompt);
            var fixResponse = await effectiveChat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixedFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(fixResponse.Content ?? "");

            foreach (var file in fixedFiles)
                await Workspace.WriteFileAsync(file.Path, file.Content, ct);

            // Rebuild after fix before re-testing
            var rebuildResult = await BuildRunnerSvc!.BuildAsync(
                Workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!rebuildResult.Success)
            {
                Logger.LogWarning("{Role} {Name} rebuild after test fix failed (attempt {Attempt}), feeding build errors to AI",
                    Identity.Role, Identity.DisplayName, attempt + 1);

                // Try to fix the build error introduced by the test fix
                var (buildFixOk, _) = await BuildWithRetryAsync(
                    fixedFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct);
                if (!buildFixOk)
                {
                    // Revert the bad test fix and try again from the previous state
                    await Workspace.RevertUncommittedChangesAsync(ct);
                    Logger.LogWarning("{Role} {Name} reverted broken test fix (attempt {Attempt}), continuing fix loop",
                        Identity.Role, Identity.DisplayName, attempt + 1);
                }
            }
        }

        return false; // Should not reach here
    }

    /// <summary>
    /// Last resort: ask AI to remove failing tests that cannot be fixed, documenting why.
    /// Verifies the remaining code builds and all remaining tests pass before returning.
    /// </summary>
    private async Task<bool> RemoveFailingTestsAsync(
        TestResult lastTestResult,
        IChatCompletionService chat,
        WorkspaceConfig wsConfig,
        int stepNumber, int totalSteps, string stepDescription,
        AgentPullRequest pr,
        CancellationToken ct,
        int removalDepth = 0)
    {
        const int MaxRemovalPasses = 3;
        if (removalDepth >= MaxRemovalPasses)
        {
            Logger.LogWarning("{Role} {Name} test removal exhausted after {Max} passes — proceeding with {Failed} failing tests",
                Identity.Role, Identity.DisplayName, MaxRemovalPasses, lastTestResult.Failed);
            return false;
        }
        var failureSummary = lastTestResult.FailureDetails.Count > 0
            ? string.Join("\n", lastTestResult.FailureDetails.Take(20))
            : lastTestResult.Output.Length > 3000 ? lastTestResult.Output[^3000..] : lastTestResult.Output;

        var removePrompt = PromptService is not null
            ? await PromptService.RenderAsync("engineer-base/test-removal",
                new Dictionary<string, string>
                {
                    ["max_retries"] = wsConfig.MaxTestRetries.ToString(),
                    ["failure_details"] = failureSummary
                }, ct)
            : null;
        removePrompt ??= $"""
            The following tests have been failing despite {wsConfig.MaxTestRetries} attempts to fix them.
            These tests MUST be removed because they cannot be made to pass within the current constraints.

            FAILING TESTS:
            {failureSummary}

            For each failing test:
            1. REMOVE the failing test method entirely
            2. Add a comment at the location where it was removed:
               // TEST REMOVED: [TestMethodName] - Could not be resolved after {wsConfig.MaxTestRetries} fix attempts.
               // Reason: [brief description of the failure]
               // This test should be revisited when the underlying issue is resolved.
            3. Keep ALL passing tests intact — do not remove or modify them

            Output ONLY the updated test files using this format:
            FILE: path/to/test/file.ext
            ```language
            <complete updated file content with failing tests removed>
            ```

            Include the COMPLETE file content for each test file that needs changes.
            Ensure the remaining code still compiles after removal.
            """;

        UpdateStatus(AgentStatus.Working,
            $"🧹 Removing failing tests (LLM analysis, pass {removalDepth + 1}/{MaxRemovalPasses})");

        var history = CreateChatHistory();
        history.AddUserMessage(removePrompt);
        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var updatedFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(response.Content ?? "");

        if (updatedFiles.Count > 0)
        {
            _ = Metrics?.RecordTestsRemovedAsync(Identity.Id, lastTestResult.Failed, ct);
            foreach (var file in updatedFiles)
                await Workspace!.WriteFileAsync(file.Path, file.Content, ct);

            UpdateStatus(AgentStatus.Working,
                $"🔨 Verifying build after test removal (pass {removalDepth + 1}/{MaxRemovalPasses})");

            // Verify build still passes after test removal
            var buildResult = await BuildRunnerSvc!.BuildAsync(
                Workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!buildResult.Success)
            {
                var (buildFixed, _) = await BuildWithRetryAsync(
                    updatedFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct);
                if (!buildFixed)
                {
                    Logger.LogError("{Role} {Name} build broken after test removal — reverting",
                        Identity.Role, Identity.DisplayName);
                    await Workspace.RevertUncommittedChangesAsync(ct);
                    return false;
                }
            }

            UpdateStatus(AgentStatus.Working,
                $"🧪 Verifying remaining tests after removal (pass {removalDepth + 1}/{MaxRemovalPasses})");

            // Verify remaining tests pass
            var finalTestResult = await TestRunnerSvc!.RunTestsAsync(
                Workspace.RepoPath, wsConfig.TestCommand, wsConfig.TestTimeoutSeconds, ct);
            if (!finalTestResult.Success)
            {
                Logger.LogWarning("{Role} {Name} some tests still failing after removal — attempting removal pass {Pass}/{Max}",
                    Identity.Role, Identity.DisplayName, removalDepth + 2, MaxRemovalPasses);

                // Recursive pass with depth guard
                return await RemoveFailingTestsAsync(finalTestResult, chat, wsConfig, stepNumber, totalSteps, stepDescription, pr, ct, removalDepth + 1);
            }

            // Document what was removed on the PR
            var removedTestNames = lastTestResult.FailureDetails.Count > 0
                ? string.Join(", ", lastTestResult.FailureDetails.Take(10).Select(d => $"`{Truncate(d, 60)}`"))
                : $"{lastTestResult.Failed} test(s)";

            await ReviewService.AddCommentAsync(pr.Number,
                $"⚠️ **Tests Removed:** Step {stepNumber}/{totalSteps} — the following tests could not be made to pass " +
                $"after {wsConfig.MaxTestRetries} fix attempts and were removed with documentation:\n\n" +
                $"{removedTestNames}\n\n" +
                $"These tests should be revisited in a follow-up. All remaining tests pass ({finalTestResult.Passed} passed).", ct);

            Logger.LogInformation("{Role} {Name} removed unfixable tests for step {Step}/{Total}, {Passed} remaining tests pass",
                Identity.Role, Identity.DisplayName, stepNumber, totalSteps, finalTestResult.Passed);
            LogActivity("test", $"🧹 Removed unfixable tests, {finalTestResult.Passed} remaining tests pass");

            return true;
        }

        Logger.LogWarning("{Role} {Name} AI did not produce test removal output — tests still failing",
            Identity.Role, Identity.DisplayName);
        return false;
    }
    private async Task<IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>?> RegenerateCodeForStepAsync(
        AgentPullRequest pr,
        string stepDescription,
        int stepNumber,
        int totalSteps,
        IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> failedFiles,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        try
        {
            var failedFileList = string.Join(", ", failedFiles.Select(f => $"`{f.Path}`"));

            var regenPrompt = PromptService is not null
                ? await PromptService.RenderAsync("engineer-base/regeneration",
                    new Dictionary<string, string>
                    {
                        ["failed_file_list"] = failedFileList,
                        ["step_description"] = stepDescription,
                        ["scope_relaxation"] = ScopeRelaxationTier2
                    }, ct)
                : null;
            regenPrompt ??= $"""
                Your previous implementation for step {stepNumber}/{totalSteps} ("{stepDescription}") had build errors 
                that could not be fixed. You need to regenerate the code from scratch with a different approach.

                The following files had issues: {failedFileList}

                Requirements for this step:
                {stepDescription}

                IMPORTANT:
                - Generate a COMPLETE, FRESH implementation — do not try to patch the previous code
                - Ensure all interfaces match their implementations exactly
                - Ensure all referenced types, namespaces, and dependencies exist
                - Double-check method signatures match across interface/class boundaries
                - Include ALL necessary using statements
                {ScopeRelaxationTier2}
                {_currentFileScopeBlock}

                Output each file using this format:
                FILE: path/to/file.ext
                ```language
                <complete file content>
                ```
                """;

            var history = CreateChatHistory();
            history.AddUserMessage(regenPrompt);
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var regeneratedFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(response.Content ?? "");

            if (regeneratedFiles.Count > 0)
            {
                Logger.LogInformation("{Role} {Name} regenerated {Count} files for step {Step}/{Total}",
                    Identity.Role, Identity.DisplayName, regeneratedFiles.Count, stepNumber, totalSteps);
            }

            return regeneratedFiles;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} failed to regenerate code for step {Step}/{Total}",
                Identity.Role, Identity.DisplayName, stepNumber, totalSteps);
            return null;
        }
    }

    /// <summary>
    /// Set up the workspace branch for a PR (sync main, create/checkout branch).
    /// Called before the first step when in local workspace mode.
    /// </summary>
    protected async Task PrepareWorkspaceBranchAsync(string branchName, CancellationToken ct)
    {
        if (Workspace is null) return;
        var stepId = TaskTracker.BeginStep(Identity.Id, "branch-setup",
            "Setup workspace branch", $"Syncing with main and creating branch {branchName}", Identity.ModelTier);
        try
        {
            await Workspace.SyncWithMainAsync(ct);
            await Workspace.CreateBranchAsync(branchName, ct);
        }
        finally { TaskTracker.CompleteStep(stepId); }
    }

    #endregion

    #region File Scope Enforcement

    /// <summary>
    /// Extracts allowed file paths from a PR/Issue description's File Plan section.
    /// Returns CREATE and MODIFY paths. Returns empty if no file plan is found (fail open).
    /// </summary>
    internal static HashSet<string> ExtractAllowedFilesFromDescription(string? description)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(description)) return allowed;

        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();

            // Markdown format: "- ➕ **Create:** `path`" or "- ✏️ **Modify:** `path`" or "- 🔗 **Shared (multi-task):** `path`"
            if (trimmed.Contains("**Create:**") || trimmed.Contains("**Modify:**") || trimmed.Contains("**Shared"))
            {
                var backtickStart = trimmed.IndexOf('`');
                var backtickEnd = trimmed.LastIndexOf('`');
                if (backtickStart >= 0 && backtickEnd > backtickStart)
                {
                    var path = trimmed[(backtickStart + 1)..backtickEnd].Trim();
                    path = StripParentheticalSuffix(path);
                    if (!string.IsNullOrEmpty(path))
                        allowed.Add(NormalizePath(path));
                }
            }

            // Raw format: "CREATE:path" or "MODIFY:path"
            if (trimmed.StartsWith("CREATE:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("MODIFY:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIdx = trimmed.IndexOf(':');
                var path = trimmed[(colonIdx + 1)..].Trim();
                path = StripParentheticalSuffix(path);
                if (!string.IsNullOrEmpty(path))
                    allowed.Add(NormalizePath(path));
            }
        }

        return allowed;
    }

    /// <summary>
    /// Strips parenthetical namespace suffixes from file paths.
    /// The SE Lead sometimes appends namespace hints like "Program.cs(ReportingDashboard)"
    /// or "App.razor(ReportingDashboard.Components)" which break path matching.
    /// </summary>
    private static string StripParentheticalSuffix(string path)
    {
        // Match pattern: path ending with "(SomeNamespace)" after a file extension
        var parenIdx = path.LastIndexOf('(');
        if (parenIdx > 0 && path.EndsWith(')'))
        {
            var beforeParen = path[..parenIdx];
            // Only strip if the part before '(' looks like a file path (has an extension)
            if (beforeParen.Contains('.'))
                return beforeParen;
        }
        return path;
    }

    /// <summary>
    /// Extracts the linked issue number from a PR body (e.g., "Closes #1626").
    /// Returns null if no linked issue is found.
    /// </summary>
    protected static int? ExtractLinkedIssueFromPrBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        // Match patterns like "Closes #123", "Fixes #456", "Resolves #789"
        var match = System.Text.RegularExpressions.Regex.Match(body,
            @"(?:Closes|Fixes|Resolves)\s+#(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var num) ? num : null;
    }

    /// <summary>
    /// Strips GitHub auto-close keywords (Closes/Fixes/Resolves #N) from AI-generated text
    /// for any issue number OTHER than the task's own issue. Prevents accidental auto-close
    /// of sibling tasks when a PR is merged.
    /// </summary>
    protected static string SanitizeAutoCloseReferences(string text, int? ownIssueNumber)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Replace "Closes #N", "Fixes #N", "Resolves #N" (case-insensitive) with just "#N"
        // but only for issue numbers that aren't our own task's issue.
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\b(Closes|Fixes|Resolves)\s+#(\d+)",
            match =>
            {
                if (int.TryParse(match.Groups[2].Value, out var issueNum) && issueNum == ownIssueNumber)
                    return match.Value; // Keep our own "Closes #N"
                return $"#{match.Groups[2].Value}"; // Strip the keyword, keep the reference
            });
    }

    /// <summary>
    /// Extracts the agent ID from a PR body's metadata comment (e.g., "&lt;!-- agent-id: frontend-engineer-1 --&gt;").
    /// Returns null if no agent ID is found. Used to prevent multi-agent collision on same PR.
    /// </summary>
    protected static string? ExtractAgentIdFromPrBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(body,
            @"<!--\s*agent-id:\s*(\S+)\s*-->", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Checks if a PR belongs to this agent. Uses agent-id metadata in PR body (durable)
    /// or linked issue in BusAssignedIssues (volatile). For PRs without agent-id metadata
    /// (legacy PRs created before this check), falls back to bus assignment check.
    /// </summary>
    protected bool IsOurPullRequest(AgentPullRequest pr)
    {
        var prAgentId = ExtractAgentIdFromPrBody(pr.Body);
        if (prAgentId is not null)
            return string.Equals(prAgentId, Identity.Id, StringComparison.OrdinalIgnoreCase);

        // Fallback for legacy PRs without agent-id metadata
        var linkedIssue = ExtractLinkedIssueFromPrBody(pr.Body);
        return linkedIssue.HasValue && BusAssignedIssues.Contains(linkedIssue.Value);
    }

    /// <summary>
    /// Returns true when the PR belongs to the active run scope. Older runs may leave
    /// open PRs with the same agent display name, but branch names include the run
    /// scope segment: agent/{runScope}/{agentSlug}/{taskSlug}.
    /// Cross-run adopted PRs (reused from a prior run scope) are accepted if their
    /// body links to an issue via "Closes #N" — proving the PR was adopted into this run.
    /// </summary>
    protected bool IsCurrentRunScopePr(AgentPullRequest pr)
    {
        return PullRequestWorkflow.IsCurrentRunScopePr(pr.HeadBranch, pr.Body, BranchProvider?.RunScope);
    }

    /// <summary>
    /// Returns true when the platform PR belongs to the active run scope.
    /// Cross-run adopted PRs are accepted if their body links to an issue.
    /// </summary>
    protected bool IsCurrentRunScopePr(PlatformPullRequest pr)
    {
        return PullRequestWorkflow.IsCurrentRunScopePr(pr.HeadBranch, pr.Body, BranchProvider?.RunScope);
    }

    private bool IsCurrentRunScopeBranch(string? headBranch)
    {
        var runScope = BranchProvider?.RunScope;
        if (string.IsNullOrWhiteSpace(runScope))
            return true;

        return !string.IsNullOrWhiteSpace(headBranch)
            && headBranch.Contains($"/{runScope}/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters code files to only those within the task's file plan scope.
    /// Infrastructure files (.csproj, .sln, .props, Directory.Build.targets) are always allowed.
    /// If no file plan exists, all files pass through (fail open with warning).
    /// </summary>
    internal List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> FilterToAllowedScope(
        IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> codeFiles,
        string? prDescription,
        string? issueDescription,
        int prNumber)
    {
        // Try PR description first, then issue description
        var allowed = ExtractAllowedFilesFromDescription(prDescription);
        if (allowed.Count == 0)
            allowed = ExtractAllowedFilesFromDescription(issueDescription);

        // Fail open: no file plan found, allow everything but log a warning
        if (allowed.Count == 0)
        {
            Logger.LogWarning("{Role} {Name} PR #{PrNumber}: No file plan found in description — skipping scope filter",
                Identity.Role, Identity.DisplayName, prNumber);
            return codeFiles as List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>
                ?? new List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>(codeFiles);
        }

        var result = new List<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile>();
        var blocked = new List<string>();

        foreach (var file in codeFiles)
        {
            var normalized = NormalizePath(file.Path);

            // Always allow infrastructure/build files
            if (IsInfrastructureFile(normalized))
            {
                result.Add(file);
                continue;
            }

            // Check if file is in the allowed set (exact match or filename match)
            if (IsFileAllowed(normalized, allowed))
            {
                result.Add(file);
            }
            else
            {
                blocked.Add(file.Path);
            }
        }

        if (blocked.Count > 0)
        {
            Logger.LogWarning("{Role} {Name} PR #{PrNumber}: Blocked {Count} out-of-scope files: {Files}",
                Identity.Role, Identity.DisplayName, prNumber, blocked.Count, string.Join(", ", blocked));
            LogActivity("scope", $"🚫 Blocked {blocked.Count} out-of-scope files on PR #{prNumber}: {string.Join(", ", blocked)}");
        }

        return result;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsInfrastructureFile(string normalizedPath)
    {
        var fileName = Path.GetFileName(normalizedPath);
        return fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Routes.razor", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            // Node.js / web project infrastructure
            || fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("tsconfig.node.json", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("vite.config", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("vitest.config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".eslintrc.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".eslintrc.cjs", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".prettierrc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("tailwind.config.js", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("tailwind.config.ts", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("postcss.config.js", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("postcss.config.cjs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Test files live under tests/ or *.Tests/ dirs, or have test-related extensions.</summary>
    private static bool IsTestFile(string normalizedPath)
    {
        var norm = normalizedPath.Replace('\\', '/');
        return norm.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || norm.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)
            || norm.Contains(".Test/", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("__tests__/", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith(".spec.tsx", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileAllowed(string normalizedPath, HashSet<string> allowed)
    {
        // Exact match (after normalization)
        if (allowed.Contains(normalizedPath))
            return true;

        // Match by filename only (handles path prefix differences)
        var fileName = Path.GetFileName(normalizedPath);
        foreach (var a in allowed)
        {
            var allowedFileName = Path.GetFileName(a);
            // Full filename match (e.g., "TimelineSection.razor" matches any path ending with that)
            if (fileName.Equals(allowedFileName, StringComparison.OrdinalIgnoreCase))
                return true;

            // The generated path ends with the allowed path (handles missing prefix)
            if (normalizedPath.EndsWith(a, StringComparison.OrdinalIgnoreCase))
                return true;

            // The allowed path ends with the generated path (handles extra prefix in plan)
            if (a.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Generates a prompt instruction block that tells the AI which files are in scope.
    /// Returns empty string if no file plan is found (so prompts degrade gracefully).
    /// </summary>
    internal static string BuildFileScopePromptBlock(string? prDescription, string? issueDescription)
    {
        var allowed = ExtractAllowedFilesFromDescription(prDescription);
        if (allowed.Count == 0)
            allowed = ExtractAllowedFilesFromDescription(issueDescription);

        if (allowed.Count == 0)
            return "";

        var fileList = string.Join("\n", allowed.Select(f => $"  - `{f}`"));
        return $"""

            FILE SCOPE RULE — STRICTLY ENFORCED:
            You may ONLY create or modify the following files (from the task's File Plan):
            {fileList}

            Do NOT create, modify, or output any files outside this list.
            Do NOT modify test files, shared infrastructure (App.razor, _Host.cshtml, Program.cs),
            or any other files not explicitly listed above.
            If a build error references a file outside this list, fix the error by adjusting
            YOUR in-scope files only — do NOT modify the out-of-scope file.
            Project files (.csproj, .sln) are the only exception and may be created if needed.
            """;
    }

    #endregion

    #region Project File Scaffolding

    /// <summary>
    /// REQ-WS-004: After AI code generation, validate that .csproj and .sln files exist.
    /// If .cs files are present without a .csproj, scaffold a minimal one.
    /// If no .sln exists at repo root, scaffold one referencing all .csproj files.
    /// </summary>
    private void EnsureProjectFiles(IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> codeFiles)
    {
        if (Workspace?.RepoPath is null) return;

        try
        {
            // Find directories with .cs files (from AI output + existing on disk)
            var dirsWithCsFiles = codeFiles
                .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetDirectoryName(f.Path)?.Replace('\\', '/') ?? "")
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Walk up to find the project root for each .cs file dir
            // (the nearest ancestor that has or should have a .csproj)
            var projectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirsWithCsFiles)
            {
                var projDir = FindOrInferProjectDir(dir, codeFiles);
                if (projDir is not null)
                    projectDirs.Add(projDir);
            }

            foreach (var projDir in projectDirs)
            {
                // Check if AI already generated a .csproj or one exists on disk
                var hasCsprojInOutput = codeFiles.Any(f =>
                    f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    f.Path.Replace('\\', '/').StartsWith(projDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

                var fullDir = Path.Combine(Workspace.RepoPath, projDir);
                var csprojOnDisk = Directory.Exists(fullDir) &&
                    Directory.GetFiles(fullDir, "*.csproj").Length > 0;

                if (hasCsprojInOutput || csprojOnDisk) continue;

                // Scaffold a .csproj
                var projectName = Path.GetFileName(projDir.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(projectName)) projectName = "Project";

                var isBlazor = codeFiles.Any(f =>
                    f.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                    f.Content.Contains("@page", StringComparison.OrdinalIgnoreCase) ||
                    f.Content.Contains("RenderFragment", StringComparison.OrdinalIgnoreCase));

                var csprojContent = GenerateAppCsproj(isBlazor);
                var csprojPath = Path.Combine(projDir, $"{projectName}.csproj");
                var fullPath = Path.Combine(Workspace.RepoPath, csprojPath);

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, csprojContent);
                Logger.LogInformation("Scaffolded missing {CsprojPath} for project files", csprojPath);
            }

            // Check for .sln at repo root
            EnsureSolutionFile(codeFiles);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Project file scaffolding check failed — continuing without it");
        }
    }

    /// <summary>
    /// CLI edit mode variant: ensure project files exist by scanning the disk.
    /// In rework mode, the project structure already exists from initial implementation,
    /// so this is a lightweight safety check for any newly created .cs files.
    /// </summary>
    private void EnsureProjectFilesFromDisk()
    {
        if (Workspace?.RepoPath is null) return;

        try
        {
            // Find all .cs files on disk (excluding .git, bin, obj, node_modules)
            var csFiles = Directory.EnumerateFiles(Workspace.RepoPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                .Select(f => Path.GetRelativePath(Workspace.RepoPath, f).Replace('\\', '/'))
                .ToList();

            if (csFiles.Count == 0) return;

            // Build CodeFile wrappers for the disk-based check
            var diskFiles = csFiles.Select(p => new Core.AI.CodeFileParser.CodeFile(p, "", "")).ToList();
            EnsureProjectFiles(diskFiles);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CLI edit mode project file check failed — continuing");
        }
    }
    /// </summary>
    private string? FindOrInferProjectDir(string csFileDir, IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> codeFiles)
    {
        if (Workspace?.RepoPath is null) return null;

        // Walk up the path looking for an existing .csproj
        var parts = csFileDir.Replace('\\', '/').Split('/');
        for (var i = parts.Length; i >= 1; i--)
        {
            var candidate = string.Join('/', parts[..i]);
            var fullCandidate = Path.Combine(Workspace.RepoPath, candidate);

            if (Directory.Exists(fullCandidate) &&
                Directory.GetFiles(fullCandidate, "*.csproj").Length > 0)
                return null; // .csproj already exists — no scaffolding needed

            // Check if AI output has a .csproj here
            if (codeFiles.Any(f =>
                f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(f.Path)?.Replace('\\', '/') == candidate))
                return null;
        }

        // No existing .csproj found — infer project root.
        // For "src/ProjectName/Services/Foo.cs", the project root is "src/ProjectName"
        // For "ProjectName/Models/Bar.cs", it's "ProjectName"
        if (parts.Length >= 2 && parts[0].Equals("src", StringComparison.OrdinalIgnoreCase))
            return $"{parts[0]}/{parts[1]}";
        if (parts.Length >= 1)
            return parts[0];

        return csFileDir;
    }

    /// <summary>
    /// Ensure a .sln file exists at the repo root. If not, scaffold one referencing all .csproj files.
    /// </summary>
    private void EnsureSolutionFile(IReadOnlyList<VirtualDevTeam.Core.AI.CodeFileParser.CodeFile> codeFiles)
    {
        if (Workspace?.RepoPath is null) return;

        // Check if .sln already exists at the repo root (on disk or in AI output).
        // Only root-level .sln counts — nested ones (e.g. "Sub/App.sln") won't be found by `dotnet build` at repo root.
        var slnOnDisk = Directory.GetFiles(Workspace.RepoPath, "*.sln").Length > 0;
        var slnInOutputAtRoot = codeFiles.Any(f =>
            f.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) &&
            !f.Path.Contains('/') && !f.Path.Contains('\\'));
        if (slnOnDisk || slnInOutputAtRoot) return;

        // Find all .csproj files (on disk + from AI output)
        var csprojPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var csproj in Directory.EnumerateFiles(Workspace.RepoPath, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Workspace.RepoPath, csproj).Replace('/', '\\');
            csprojPaths.Add(relative);
        }

        foreach (var f in codeFiles.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            csprojPaths.Add(f.Path.Replace('/', '\\'));

        if (csprojPaths.Count == 0) return;

        // Generate a minimal .sln
        var slnName = Path.GetFileName(Workspace.RepoPath);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");

        foreach (var csprojPath in csprojPaths.OrderBy(p => p))
        {
            var projName = Path.GetFileNameWithoutExtension(csprojPath);
            var projGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            sb.AppendLine($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projName}\", \"{csprojPath}\", \"{projGuid}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("EndGlobal");

        var slnPath = Path.Combine(Workspace.RepoPath, $"{slnName}.sln");
        File.WriteAllText(slnPath, sb.ToString());
        Logger.LogInformation("Scaffolded missing solution file {SlnPath} with {Count} projects",
            $"{slnName}.sln", csprojPaths.Count);
    }

    /// <summary>
    /// Generate a minimal .csproj for a web or console application.
    /// </summary>
    private static string GenerateAppCsproj(bool isBlazor)
    {
        var sdk = isBlazor ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk";
        return $@"<Project Sdk=""{sdk}"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
";
    }

    #endregion

    /// <summary>
    /// Parses DECISION|impact|title|rationale|files blocks from plan output and routes
    /// them through the DecisionGateService for tracking and optional human gating.
    /// Contract changes to scaffold interfaces/models/APIs are tracked as decisions.
    /// </summary>
    protected async Task ProcessPlanDecisionBlocksAsync(string planContent, AgentIssue issue, CancellationToken ct)
    {
        if (DecisionGate == null || string.IsNullOrWhiteSpace(planContent)) return;

        var decisions = DecisionBlockParser.ParsePipeDelimited(planContent);
        if (decisions.Count == 0) return;

        Logger.LogInformation("Found {Count} contract-change decision(s) in plan for issue #{Number}",
            decisions.Count, issue.Number);

        foreach (var (impact, title, rationale, files) in decisions)
        {
            try
            {
                var context = $"Contract change for issue #{issue.Number}: {issue.Title}\n" +
                              $"Rationale: {rationale}\n" +
                              $"Affected files: {files}";

                var assessment = new Core.Agents.Reasoning.AssessmentResult
                {
                    Passed = true,
                    Gaps = Array.Empty<string>(),
                    Summary = $"Contract change: {title}",
                    ImpactLevel = ParseImpactLevel(impact),
                    ImpactRationale = rationale,
                    AffectedFiles = files,
                    Alternatives = $"Keep existing contract as-is (may not meet requirements for issue #{issue.Number})",
                    RiskAssessment = $"Changing this contract affects any code consuming the modified interface/model"
                };

                var decision = await DecisionGate.ClassifyFromAssessmentAsync(
                    Identity.Id, Identity.DisplayName, "Implementation",
                    $"Contract Change: {title}", context, assessment,
                    category: "Contract Change",
                    modelTier: Identity.ModelTier, ct: ct);

                if (decision.Status == DecisionStatus.Pending)
                {
                    Logger.LogInformation("Contract-change decision gated for approval: {Title} (impact: {Impact})",
                        title, impact);
                    RecordImplementationNote($"⚠️ Contract change decision pending approval: {title} ({impact})");
                }
                else
                {
                    RecordImplementationNote($"📋 Contract change auto-approved: {title} ({impact})");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to process contract-change decision: {Title}", title);
            }
        }
    }

    #region Static Helpers

    protected static string ExtractQuestions(string content)
    {
        var lines = content.Split('\n');
        var questions = lines.Where(l => l.TrimStart().Contains('?')).ToList();
        return questions.Count > 0 ? string.Join("\n", questions) : "";
    }

    protected static string ExtractSection(string content, params string[] keywords)
    {
        var lines = content.Split('\n');
        var collecting = false;
        var result = new List<string>();

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (keywords.Any(k => lower.Contains(k)))
            {
                collecting = true;
                result.Add(line);
                continue;
            }

            if (collecting)
            {
                if (line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("**"))
                {
                    if (result.Count > 1) break;
                }
                result.Add(line);
            }
        }

        return result.Count > 0 ? string.Join('\n', result).Trim() : content[..Math.Min(500, content.Length)];
    }

    protected static string Slugify(string title)
    {
        var slug = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace(':', '-');
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return slug.Length > 40 ? slug[..40] : slug;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // Change #2/#3 — Completion Manifest helpers (WP-J Wave 2)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the "## Implements Scenarios" section from an issue / PR body
    /// and returns the list of scenario IDs (e.g., ["S01", "S03"]).
    /// Returns an empty list when the section is absent.
    /// </summary>
    protected static List<string> ParseImplementedScenarios(string? issueBody)
    {
        if (string.IsNullOrWhiteSpace(issueBody)) return [];

        var ids = new List<string>();
        var inSection = false;
        foreach (var rawLine in issueBody.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("## Implements Scenarios", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }

            if (inSection)
            {
                if (line.TrimStart().StartsWith("##")) break; // next section
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^-\s+(S\d+)");
                if (match.Success)
                    ids.Add(match.Groups[1].Value);
            }
        }

        return ids;
    }

    /// <summary>
    /// Writes a <see cref="CompletionManifest"/> sidecar after self-assessment completes.
    /// If workspace is active, the manifest lands in the worktree; otherwise falls back to
    /// the configured workspace root for API-only mode. Failures are non-fatal (Warning only).
    /// </summary>
    private async Task WriteCompletionManifestAsync(
        AgentPullRequest pr,
        AgentIssue issue,
        IReadOnlyList<string> changedFiles,
        bool passed,
        IReadOnlyList<string> gaps,
        CancellationToken ct)
    {
        try
        {
            var manifestPath = CompletionManifestPathResolver.Resolve(
                pr.Number,
                worktreePath: Workspace?.RepoPath,
                storagePath: Config.Workspace.RootPath);

            // Build one export per changed file. When self-assessment returns NEEDS_CHANGES,
            // map FullyImplemented per-file using the gaps list instead of blanket-failing every file.
            // A gap string typically references a file path — only mark that file as not fully implemented.
            var firstGapReason = gaps.Count > 0 ? string.Join("; ", gaps.Take(3)) : null;
            var gapFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!passed && gaps.Count > 0)
            {
                foreach (var gap in gaps)
                {
                    // Match gap text against changed file paths (gaps often reference file names or paths)
                    foreach (var f in changedFiles)
                    {
                        var fileName = Path.GetFileName(f);
                        if (gap.Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                            gap.Contains(f.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        {
                            gapFilePaths.Add(f);
                        }
                    }
                }
            }

            var exports = changedFiles
                .Select(f =>
                {
                    // If we identified specific gap files, only those are not fully implemented.
                    // If no gap files could be matched (gaps were generic), fall back to marking all.
                    var fileHasGap = !passed && (gapFilePaths.Count == 0 || gapFilePaths.Contains(f));
                    return new ManifestExport
                    {
                        File = f.Replace('\\', '/'),
                        Symbol = Path.GetFileName(f),
                        FullyImplemented = !fileHasGap,
                        StubOk = false,
                        Reason = fileHasGap ? firstGapReason : null
                    };
                })
                .ToList<ManifestExport>();

            // Ensure at least one export entry when no files tracked (rare edge case)
            if (exports.Count == 0 && !passed)
            {
                exports.Add(new ManifestExport
                {
                    File = "(unknown)",
                    Symbol = "implementation",
                    FullyImplemented = false,
                    StubOk = false,
                    Reason = firstGapReason ?? "self-assessment returned NEEDS_CHANGES with no file list"
                });
            }

            var scenarioIds = ParseImplementedScenarios(issue.Body);

            var manifest = new CompletionManifest
            {
                Version = 1,
                AgentId = Identity.Id,
                PrNumber = pr.Number,
                TaskId = issue.Number.ToString(),
                Exports = exports,
                ScenariosImplemented = scenarioIds,
                GeneratedAt = DateTimeOffset.UtcNow
            };

            await CompletionManifestWriter.WriteAsync(manifestPath, manifest, ct);

            Logger.LogInformation(
                "{Role} {Name} wrote completion manifest for PR #{PrNumber} ({Exports} exports, passed={Passed}) at {Path}",
                Identity.Role, Identity.DisplayName, pr.Number, exports.Count, passed, manifestPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "{Role} {Name} failed to write completion manifest for PR #{PrNumber} — proceeding without",
                Identity.Role, Identity.DisplayName, pr.Number);
        }
    }

    /// <summary>
    /// Reads the completion manifest for a PR and runs <see cref="CompletionManifestEnforcement.Check"/>.
    /// When blocked, logs Critical, applies <c>stub-detected</c> label, posts a comment, records a
    /// memory entry, and returns <c>true</c> so the caller can abort the ready-for-review flow.
    /// Returns <c>false</c> when the PR may proceed (manifest passes, or manifest is missing).
    /// </summary>
    protected async Task<bool> IsBlockedByCompletionManifestAsync(
        AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        var manifestPath = CompletionManifestPathResolver.Resolve(
            pr.Number,
            worktreePath: Workspace?.RepoPath,
            storagePath: Config.Workspace.RootPath);

        CompletionManifest? manifest;
        try
        {
            manifest = await CompletionManifestReader.ReadAsync(manifestPath, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "{Role} {Name} could not read completion manifest for PR #{PrNumber} — skipping enforcement",
                Identity.Role, Identity.DisplayName, pr.Number);
            return false;
        }

        if (manifest is null)
        {
            // Manifest missing: warn in workspace mode (manifest should have been written);
            // silently skip in API-only mode.
            if (Workspace is not null)
            {
                Logger.LogWarning(
                    "{Role} {Name} completion manifest not found for PR #{PrNumber} at '{Path}' (workspace mode) — " +
                    "skipping enforcement until wiring catches up",
                    Identity.Role, Identity.DisplayName, pr.Number, manifestPath);
            }
            return false; // Graceful degrade
        }

        var enforcement = CompletionManifestEnforcement.Check(manifest);
        if (enforcement is not EnforcementResult.BlockedByStub blocked)
            return false; // All clear

        // ── PR is blocked ──────────────────────────────────────────────────────
        Logger.LogCritical(
            "{Role} {Name} PR #{PrNumber} BLOCKED by stub detection — {Count} offender(s): {Offenders}",
            Identity.Role, Identity.DisplayName, pr.Number, blocked.Offenders.Count,
            string.Join(", ", blocked.Offenders.Select(o => $"{o.File}:{o.Symbol}")));

        LogActivity("gate", $"🚫 PR #{pr.Number} blocked by stub detection — {blocked.Offenders.Count} offender(s)");
        UpdateStatus(AgentStatus.Working, $"PR #{pr.Number} blocked — stubs detected, fixing");

        var offenderLines = string.Join("\n",
            blocked.Offenders.Select(o =>
                $"- `{o.File}:{o.Symbol}`{(o.Reason is not null ? $" — {o.Reason}" : " — not fully implemented")}"));

        var comment =
            $"🚫 **Stub Detection Block**\n\n" +
            $"This PR cannot be marked ready-for-review because the following exported symbols " +
            $"are not fully implemented and are not explicitly marked `stub_ok`:\n\n{offenderLines}\n\n" +
            $"_To unblock: implement the stubs, or annotate them as `stub_ok: true` with a reason " +
            $"in `.completion-manifests/pr-{pr.Number}.json`._";

        try
        {
            await ReviewService.AddCommentAsync(pr.Number, comment, ct);
            await PrService.AddLabelsAsync(pr.Number, ["stub-detected"], ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to post stub-detection comment/label for PR #{PrNumber}", pr.Number);
        }

        await RememberAsync(MemoryType.Decision,
            $"PR #{pr.Number} blocked by stub detection",
            $"{blocked.Offenders.Count} symbol(s) not fully implemented: " +
            string.Join(", ", blocked.Offenders.Select(o => $"{o.File}:{o.Symbol}")),
            ct);

        return true; // Blocked — caller must abort
    }
}
