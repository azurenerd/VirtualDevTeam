using System.Collections.Concurrent;
using System.Text;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Agents.Playtest;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.CompletionManifest;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Core.Services;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

/// <summary>
/// Software Engineer agent — handles high-complexity tasks and orchestrates the engineering team.
/// Extends <see cref="EngineerAgentBase"/> with planning, issue assignment, PR review,
/// and resource management capabilities.
/// </summary>
public partial class SoftwareEngineerAgent : EngineerAgentBase
{
    private const string IntegrationTaskId = "T-FINAL";
    private const string IntegrationTaskName = "Final Integration & Validation";

    /// <summary>
    /// Checks whether a task is the final integration task by ID or name.
    /// AssignTaskAsync renames issue titles from "[T-FINAL] Name" to "Agent: Name",
    /// so LoadTasksAsync falls back to "T-{N}" — the ID check alone is unreliable.
    /// </summary>
    private static bool IsIntegrationTask(EngineeringTask t) =>
        string.Equals(t.Id, IntegrationTaskId, StringComparison.OrdinalIgnoreCase)
        || t.Name.Contains(IntegrationTaskName, StringComparison.OrdinalIgnoreCase);

    private readonly AgentRegistry _registry;
    private readonly EngineeringTaskIssueManager _taskManager;
    private readonly SmeDefinitionGenerator? _smeGenerator;
    private readonly AgentSpawnManager? _spawnManager;
    private readonly DecisionGateService? _decisionGate;
    private readonly IDecisionLog? _decisionLog;

    // Platform abstraction for post-merge close-out (works for both GitHub and ADO)
    private readonly MergeCloseoutService? _mergeCloseout;

    // Change #1 — Scenario registry injected to stamp "Implements Scenarios:" on task issues.
    private readonly IScenarioRegistry? _scenarioRegistry;

    // Advisory scenario validation — records task-completion-based verdicts without blocking completion.
    private readonly IAppPlaytester? _appPlaytester;

    // Strategy Framework (Phase 1) — optional, opt-in via StrategyFrameworkConfig.Enabled.
    private readonly StrategyOrchestrator? _strategyOrchestrator;
    private readonly WinnerApplyService? _winnerApply;
    private readonly IOptionsMonitor<StrategyFrameworkConfig>? _strategyConfig;
    private readonly StrategyTaskStepBridge? _strategyStepBridge;

    /// <summary>
    /// Optional hot-reloading config monitor used by <see cref="RequestMoreEngineersIfNeededAsync"/>
    /// so an operator can raise <c>EngineerPool.SoftwareEngineerPool</c> on the Configuration page
    /// mid-run and have the SE leader's next scaling pass observe the new cap without a runner restart.
    /// Falls back to <see cref="EngineerAgentBase.Config"/> (snapshot at construction) when not provided
    /// — preserves existing test seams that build the agent without the monitor.
    /// </summary>
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _appConfigMonitor;
    private int _lastSeenPoolCap = -1;

    /// <summary>
    /// When a strategy winner is chosen but apply/build fails, store its patch here
    /// so the legacy codegen path can use it as reference context instead of starting from scratch.
    /// </summary>
    private string? _failedWinnerPatchContext;

    /// <summary>
    /// Stashed winner candidates from strategy framework runs, keyed by PR number.
    /// Consumed by FinalizeReadyForReviewAsync to attach proven media to the PR comment.
    /// Scoped by PR to prevent cross-task contamination if a task is aborted.
    /// </summary>
    private readonly Dictionary<int, VirtualDevTeam.Core.Strategies.CandidateResult> _winnersByPr = new();

    private bool _planningComplete;
    // Guards against repeatedly logging the same tracker steps and status messages
    // while the SE is idling waiting for PM to create Enhancement issues. Without this
    // each 15-second planning poll creates a fresh "Read architecture" / "Task decomposition"
    // pair in the dashboard, none of which ever completes.
    private bool _loggedWaitingForPmIssues;
    private bool _loggedArchitectureRead;
    private bool _taskInventoryLogged;
    private bool _planningSignalReceived;
    private bool _architectureReady;
    private bool _resourceRequestPending;
    private int _pendingWorkerRequests;
    private int _expectedEngineerCount;
    private bool _recoveredReviewPRs;
    private bool _recoveredInProgressPR;
    private bool _taskAssignmentGateCleared;
    private DateTime _lastResourceRequestTime = DateTime.MinValue;
    private static readonly TimeSpan SpawnCooldown = TimeSpan.FromSeconds(20);
    private bool _allTasksComplete;
    private bool _integrationPrCreated;
    private int _integrationPrRecreateCount;
    private bool _engineeringSignaled;
    private int? _integrationIssueNumber;
    private readonly Dictionary<string, int> _agentAssignments = new();
    private readonly ConcurrentDictionary<int, byte> _reviewedPrNumbers = new();
    private readonly ConcurrentDictionary<int, byte> _forceApprovalPrs = new();
    // 2026-05-12 fix (se-leader-merge-skip-sticky-dedup): was HashSet<int> which permanently
    // skipped any PR after first encounter. Now keyed by (prNumber, headSha) so new commits
    // trigger re-evaluation, and we only add AFTER successful merge — dedup means "already
    // merged, don't re-merge", not "attempted once, never retry".
    private readonly ConcurrentDictionary<int, string> _mergedTestedPrNumbersWithSha = new();
    /// <summary>
    /// Tracks PR numbers that have been confirmed merged/closed. Prevents re-enqueuing rework
    /// for PRs that were merged during or after a rework cycle.
    /// </summary>
    private readonly HashSet<int> _mergedPrNumbers = new();
    /// <summary>
    /// Tracks how many times each task has been picked up for implementation (branch + PR creation).
    /// Prevents infinite retry loops where the SE keeps re-entering the same task after rework
    /// failures, orphan recovery resets, or force-approval cycles. Keyed by task ID (e.g., "T1").
    /// </summary>
    private readonly Dictionary<string, int> _taskAcquisitionCounts = new();
    /// <summary>
    /// Task IDs that have hit the reacquisition cap and must not be re-selected this session.
    /// Provides immediate in-process exclusion before the expensive FindExistingPrForTaskAsync
    /// PR-list lookup, so a blocked task can't keep burning GitHub API calls on every loop.
    /// Rehydrated from the durable <see cref="EngineeringTaskIssueManager.StatusBlocked"/> label
    /// on restart via <see cref="EngineeringTaskIssueManager.IsTaskBlocked"/>.
    /// </summary>
    private readonly HashSet<string> _blockedTaskIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<int> _reviewQueue = new();

    // Final submission service — used in LDP mode to publish one clean PR to the real platform
    private readonly IFinalSubmissionService? _finalSubmission;
    private readonly VirtualDevTeam.Core.Merging.IMergeCoordinator? _mergeCoordinator;

    /// <summary>
    /// Shared across ALL PE instances in-process. Prevents the race condition where
    /// multiple PEs discover the same PR, both check GitHub (no review posted yet),
    /// and both post duplicate review comments. TryAdd gives atomic claim semantics.
    /// Value is (agentId, claimedAtUtc) for debugging stale claims.
    /// </summary>
    private static readonly ConcurrentDictionary<int, (string AgentId, DateTime ClaimedAt)> s_activeReviews = new();
    private readonly Dictionary<int, int> _conflictRetryByIssue = new();
    private int _continuationAttempts; // Tracks how many times ContinueOwnPrImplementationAsync is called without progress
    private const int MaxContinuationAttempts = 3;
    private string? _currentTaskName; // Human-readable name for dashboard display
    private DateTime _lastReviewDiscovery = DateTime.MinValue;
    private static readonly TimeSpan ReviewDiscoveryInterval = TimeSpan.FromMinutes(2);
    private DateTime _lastDepRecheckTime = DateTime.MinValue;
    private static readonly TimeSpan DepRecheckInterval = TimeSpan.FromMinutes(5);
    private int _idleLoopCount; // Tracks consecutive idle iterations for self-claim fallback
    private const int SelfClaimAfterIdleLoops = 3; // Self-claim after 3 idle loops (~45s with 15s poll)

    // ── Per-iteration PR cache ──────────────────────────────────────────
    // Avoids redundant GitHub API calls when multiple recovery/check methods
    // in the same loop iteration all need ListMergedAsync or ListOpenAsync.
    // 11 ListMergedAsync call sites × 5 engineers × 120 iterations/hr was
    // consuming ~3600 API calls/hr — the dominant rate limit consumer.
    // Reset at the top of each WorkOnOwnTasksAsync iteration.
    private IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>? _cachedMergedPRs;
    private IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>? _cachedOpenPRs;
    private DateTime _prCacheTimestamp;
    private DateTime _openPrCacheTimestamp;
    private static readonly TimeSpan PrCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Get merged PRs with per-iteration caching. Avoids redundant API calls.</summary>
    private async Task<IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>> GetCachedMergedPRsAsync(CancellationToken ct)
    {
        if (_cachedMergedPRs is not null && (DateTime.UtcNow - _prCacheTimestamp) < PrCacheTtl)
            return _cachedMergedPRs;
        _cachedMergedPRs = await PrService.ListMergedAsync(ct);
        _prCacheTimestamp = DateTime.UtcNow;
        return _cachedMergedPRs;
    }

    /// <summary>Get open PRs with per-iteration caching. Avoids redundant API calls.
    /// Mirrors <see cref="GetCachedMergedPRsAsync"/> — 9 ListOpenAsync call sites
    /// per SE loop iteration × 5 SEs was consuming ~1,800 API calls/hr.</summary>
    private async Task<IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>> GetCachedOpenPRsAsync(CancellationToken ct)
    {
        if (_cachedOpenPRs is not null && (DateTime.UtcNow - _openPrCacheTimestamp) < PrCacheTtl)
            return _cachedOpenPRs;
        _cachedOpenPRs = await PrService.ListOpenAsync(ct);
        _openPrCacheTimestamp = DateTime.UtcNow;
        return _cachedOpenPRs;
    }

    /// <summary>Invalidate the per-iteration PR cache (call at top of each loop).</summary>
    private void ResetPerIterationCache()
    {
        _cachedMergedPRs = null;
        _cachedOpenPRs = null;
    }

    /// <summary>Persist conflict retry counters to run_metadata (survives restart).</summary>
    private void PersistConflictRetryCounters()
    {
        if (StateStore is null || _conflictRetryByIssue.Count == 0) return;
        try
        {
            StateStore.SetRunMetadata($"{Identity.Id}:conflictRetry",
                System.Text.Json.JsonSerializer.Serialize(_conflictRetryByIssue));
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Failed to persist conflict retry counters");
        }
    }

    /// <summary>Restore conflict retry counters from run_metadata on startup.</summary>
    private void RestoreConflictRetryCounters()
    {
        if (StateStore is null) return;
        try
        {
            var entries = StateStore.GetRunMetadataByPrefix($"{Identity.Id}:conflictRetry");
            if (entries.TryGetValue($"{Identity.Id}:conflictRetry", out var json))
            {
                var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(json);
                if (restored is not null)
                {
                    foreach (var kvp in restored) _conflictRetryByIssue[kvp.Key] = kvp.Value;
                    Logger.LogInformation("SE restored {Count} conflict retry counters", restored.Count);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore conflict retry counters");
        }
    }

    /// <summary>
    /// Determines if this PE instance is the leader (responsible for orchestration-only tasks).
    /// The leader is the lowest-rank online PE. If no PEs are online, falls back to this instance.
    /// </summary>
    private bool IsLeader()
    {
        var onlinePEs = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
            .Where(a => a.Status is AgentStatus.Working or AgentStatus.Idle or AgentStatus.Online or AgentStatus.Initializing)
            .OrderBy(a => a.Identity.Rank)
            .ToList();
        return onlinePEs.Count == 0 || onlinePEs[0].Identity.Id == Identity.Id;
    }

    /// <summary>
    /// Checks if any PE agent has already reviewed a given PR by looking for
    /// [SoftwareEngineer*] review comments on GitHub.
    /// </summary>
    private async Task<bool> HasAnyPeReviewedAsync(int prNumber, CancellationToken ct)
    {
        var comments = await ReviewService.GetCommentsAsync(prNumber, ct);
        return comments.Any(c =>
            c.Body.Contains("[SoftwareEngineer]", StringComparison.OrdinalIgnoreCase) ||
            c.Body.Contains("[SoftwareEngineer ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Idempotency guard for the "Tests Reviewed — Merging" / "Merge Review" comment.
    /// Two layers of defense against the multi-SE merge race:
    ///   1) Process-local <see cref="s_testsReviewedClaimed"/> via <c>TryAdd</c> — the
    ///      first SE to call this for a given PR claims the slot atomically; concurrent
    ///      SEs in the same process are guaranteed to short-circuit even if they all
    ///      enter this method within the same millisecond.
    ///   2) GitHub-comment lookback (<see cref="TestsReviewedDedupWindow"/>) — covers the
    ///      cross-restart case where a prior runner posted but the in-process claim was
    ///      lost.
    /// Returns true at most once per PR per runner-process. We never release the slot —
    /// genuine re-reviews after rework cycles will see the GitHub-comment age check fail
    /// the lookback window and re-post a fresh approval.
    /// </summary>
    private async Task<bool> ShouldPostTestsReviewedAsync(int prNumber, CancellationToken ct)
    {
        // Layer 1: in-process atomic claim — wins the race against concurrent SEs.
        if (!s_testsReviewedClaimed.TryAdd(prNumber, 1))
        {
            Logger.LogDebug(
                "Skipping duplicate Tests Reviewed comment on PR #{Number} — claimed by another SE in this process",
                prNumber);
            return false;
        }

        // Layer 2: cross-restart comment lookback. If this runner just started and a
        // prior runner already posted, we never want to post again.
        try
        {
            var comments = await ReviewService.GetCommentsAsync(prNumber, ct);
            var cutoff = DateTime.UtcNow - TestsReviewedDedupWindow;
            var alreadyPosted = comments.Any(c =>
                c.CreatedAt >= cutoff &&
                (c.Body.Contains("[SoftwareEngineer] Tests Reviewed", StringComparison.OrdinalIgnoreCase) ||
                 c.Body.Contains("[SoftwareEngineer] Merge Review", StringComparison.OrdinalIgnoreCase)));
            if (alreadyPosted)
            {
                Logger.LogDebug(
                    "Skipping duplicate Tests Reviewed comment on PR #{Number} — another runner posted within {WindowSec}s",
                    prNumber, (int)TestsReviewedDedupWindow.TotalSeconds);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Comment lookup failed — we already won the in-process claim, so post anyway.
            Logger.LogWarning(ex, "Tests-Reviewed dedup comment-check failed on PR #{Number}; posting (in-process claim already won)", prNumber);
            return true;
        }
    }

    /// <summary>
    /// Process-local claim set: PR numbers for which the "Tests Reviewed — Merging"
    /// comment has already been posted (or is being posted) by THIS runner. Static so it
    /// covers all SE instances in the same process. We never remove entries — the slot
    /// is permanent for the runner's lifetime to ensure idempotency under all retry paths.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> s_testsReviewedClaimed = new();

    private static readonly TimeSpan TestsReviewedDedupWindow = TimeSpan.FromSeconds(60);

    public SoftwareEngineerAgent(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        AgentWorkspaceServices workspace,
        AgentRegistry registry,
        ILogger<SoftwareEngineerAgent> logger,
        SmeDefinitionGenerator? smeGenerator = null,
        AgentSpawnManager? spawnManager = null,
        DecisionGateService? decisionGate = null,
        IDecisionLog? decisionLog = null,
        StrategyOrchestrator? strategyOrchestrator = null,
        WinnerApplyService? winnerApply = null,
        IOptionsMonitor<StrategyFrameworkConfig>? strategyConfig = null,
        StrategyTaskStepBridge? strategyStepBridge = null,
        MergeCloseoutService? mergeCloseout = null,
        PrePRClarificationStore? clarificationStore = null,
        IOptionsMonitor<VirtualDevTeamConfig>? appConfigMonitor = null,
        IScenarioRegistry? scenarioRegistry = null,
        IAppPlaytester? appPlaytester = null,
        ClaimedTaskRegistry? claimRegistry = null,
        IFinalSubmissionService? finalSubmission = null,
        VirtualDevTeam.Core.Merging.IMergeCoordinator? mergeCoordinator = null)
        : base(identity, core, platform, workspace, logger, decisionGate, decisionLog, clarificationStore, claimRegistry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _taskManager = new EngineeringTaskIssueManager(platform.WorkItemService, logger);
        _smeGenerator = smeGenerator;
        _spawnManager = spawnManager;
        _decisionGate = decisionGate;
        _decisionLog = decisionLog;
        _strategyOrchestrator = strategyOrchestrator;
        _winnerApply = winnerApply;
        _strategyConfig = strategyConfig;
        _strategyStepBridge = strategyStepBridge;
        _mergeCloseout = mergeCloseout;
        _appConfigMonitor = appConfigMonitor;
        _scenarioRegistry = scenarioRegistry;
        _appPlaytester = appPlaytester;
        _finalSubmission = finalSubmission;
        _mergeCoordinator = mergeCoordinator;
    }


    protected override string GetRoleDisplayName() => "Software Engineer";

    /// <summary>
    /// Force workspace clone when ForceRedoFinalIntegration is set — the SE leader needs
    /// a local workspace for T-FINAL strategy framework execution (strategies require a
    /// local worktree to create candidate branches and run build/test).
    /// </summary>
    protected override bool ShouldForceWorkspaceClone()
    {
        return _strategyConfig?.CurrentValue.ForceRedoFinalIntegration == true;
    }

    protected override string GetImplementationSystemPrompt(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("software-engineer/implementation-system",
                new Dictionary<string, string> { ["tech_stack"] = techStack }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }
        return $"You are a Software Engineer implementing a high-complexity engineering task. " +
            $"The project uses {techStack} as its technology stack. " +
            "The PM Specification defines the business requirements, and the Architecture " +
            "document defines the technical design. The GitHub Issue contains the User Story " +
            "and acceptance criteria for this specific task. " +
            "Produce detailed, production-quality code. " +
            "Ensure the implementation fulfills the business goals from the PM spec. " +
            "Be thorough — this is the most critical part of the system.\n\n" +
            "RUNNABLE RULE: The application MUST compile and be runnable after your changes. " +
            "Do not leave stub methods that throw NotImplementedException, do not reference types " +
            "or services that don't exist yet, and do not break the build. If a feature depends on " +
            "code from another task that hasn't been implemented yet, use graceful fallbacks " +
            "(e.g., return empty collections, show placeholder text) instead of throwing exceptions. " +
            "After your implementation, `dotnet build` must succeed and `dotnet run` must start without errors.\n\n" +
            "DEPENDENCY RULE: Before using ANY external library, package, or framework, check the project's " +
            "dependency manifest (e.g., .csproj, package.json, requirements.txt, etc.). " +
            "If a dependency is not already listed, add it to the manifest and include that file in your output. " +
            "Never import/using/require a package without ensuring it is declared in the project.";
    }

    protected override string GetReworkSystemPrompt(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("software-engineer/rework-system",
                new Dictionary<string, string> { ["tech_stack"] = techStack }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }
        return $"You are a Software Engineer making SURGICAL fixes to an existing pull request based on reviewer feedback. " +
            $"The project uses {techStack}. " +
            "SURGICAL REWORK RULES: " +
            "1. Read each feedback item carefully. Make ONLY the changes needed to address that specific item. " +
            "2. Do NOT rewrite, reorganize, or regenerate files that weren't mentioned in the feedback. " +
            "3. Do NOT touch CSS, config, project files, or infrastructure unless the reviewer SPECIFICALLY asked. " +
            "4. Your diff should be minimal — a reviewer should see a small, focused set of changes. " +
            "5. Only include files you actually changed in your output.";
    }

    protected override Task<string> GetAdditionalReworkContextAsync(CancellationToken ct)
    {
        var taskSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
            $"- [{t.Id}] {t.Name} ({t.Complexity}, {t.Status})"));
        return Task.FromResult($"## Engineering Tasks\n{taskSummary}\n\n");
    }

    /// <summary>
    /// Append visual design context to an implementation prompt if the task involves UI work.
    /// Gates on task/issue content to avoid injecting HTML into non-UI tasks (data models, tests, etc.).
    /// </summary>
    private async Task AppendDesignContextIfRelevantAsync(
        StringBuilder ctx, string? taskName, string? taskDescription, string? issueBody, CancellationToken ct)
    {
        // Heuristic: only inject design context for UI-related tasks
        var combined = $"{taskName} {taskDescription} {issueBody}".ToLowerInvariant();
        var uiKeywords = new[] { "ui", "layout", "css", "component", "razor", "blazor", "page", "header",
            "timeline", "heatmap", "dashboard", "display", "render", "visual", "style", "svg", "html",
            "frontend", "shell", "scaffold", "foundation" };
        if (!uiKeywords.Any(k => combined.Contains(k)))
            return;

        var designCtx = await GetDesignContextAsync(ct);
        if (!string.IsNullOrWhiteSpace(designCtx))
            ctx.AppendLine(designCtx + "\n");
    }

    #region Lifecycle Overrides

    /// <summary>PE has a custom set of subscriptions for orchestration.</summary>
    protected override void RegisterAdditionalSubscriptions()
    {
        Subscribe<StatusUpdateMessage>(HandleStatusUpdateAsync);
        Subscribe<ReviewRequestMessage>(HandleReviewRequestAsync);
        Subscribe<PlanningCompleteMessage>(HandlePlanningCompleteAsync);
        // Restore SE-specific conflict retry counters from SQLite
        RestoreConflictRetryCounters();
        // Wake immediately when PM approves a PR (SE can merge it)
        Subscribe<PrApprovedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("SE received PrApprovedMessage for PR #{Number} from {Approver}",
                msg.PrNumber, msg.ApproverAgent);
            WakeLoop();
        });
        // Wake when tests are completed (SE tracks overall progress)
        Subscribe<TestsCompletedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("SE received TestsCompletedMessage for PR #{Number}",
                msg.PrNumber);
            WakeLoop();
        });
        // Wake when any PR is merged (dependency checks, recovery, progress tracking)
        Subscribe<PrMergedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("SE received PrMergedMessage for PR #{Number}: {Title}",
                msg.PrNumber, msg.PrTitle);
            // Invalidate per-iteration PR cache so next check sees fresh data
            _cachedMergedPRs = null;
            _cachedOpenPRs = null;
            WakeLoop();
        });
    }

    /// <summary>
    /// PE has a two-phase loop: Phase 1 waits for architecture + issues,
    /// Phase 2 is continuous orchestration + own task work.
    /// </summary>
    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for Architecture document");

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            try
            {
                var isLeader = IsLeader();

                if (!_planningComplete)
                {
                    if (isLeader)
                    {
                        if (await CheckForArchitectureAsync(ct))
                        {
                            if (!_loggedArchitectureRead)
                            {
                                var readArchStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Read architecture & PMSpec",
                                    "Architecture and PM Specification documents detected, starting engineering plan", Identity.ModelTier);
                                TaskTracker.CompleteStep(readArchStepId);
                                _loggedArchitectureRead = true;
                            }
                            await CreateEngineeringPlanAsync(ct);
                            // _planningComplete is set inside CreateEngineeringPlanAsync
                            // on success or valid restore paths
                        }
                    }
                    else
                    {
                        // Non-leader PEs: check if engineering plan issues already exist (created by leader)
                        if (await CheckForArchitectureAsync(ct))
                        {
                            await SyncEngineeringPlanFromGitHubAsync(ct);
                            if (_taskManager.TotalCount > 0)
                                _planningComplete = true;
                        }
                    }
                }
                else
                {
                    // Ensure each loop iteration sees fresh PR data — stale cache caused
                    // the safety check at line 612 to miss a just-merged integration PR
                    // (MergeCloseoutService race), triggering unnecessary re-creation.
                    ResetPerIterationCache();

                    // Show meaningful status at the start of each orchestration loop
                    var pending = _taskManager.PendingCount;
                    var done = _taskManager.DoneCount;
                    var total = _taskManager.TotalCount;
                    // 2026-05-13 fix (se-leader-status-stale): a waiting/orchestrating agent
                    // is IDLE, not WORKING. Previously `pending > 0` made the leader Working
                    // forever (e.g., when T-FINAL is pending but blocked on dependencies).
                    // Operators couldn't distinguish a healthy idle leader from a stuck one.
                    //
                    // Leader is "Working" ONLY when something needs immediate action:
                    //  - PRs queued for review (immediate work) → Working
                    //  - The brief assignment-cycle status (line 2569) → Working temporarily,
                    //    next loop iteration resets to Idle.
                    // Pending tasks alone (blocked-on-deps or just awaiting workers) = Idle.
                    var hasActionableWork = isLeader
                        ? (_reviewQueue.Count > 0)
                        : (CurrentPrNumber is not null || _reviewQueue.Count > 0);
                    var leaderTag = isLeader ? "Leader" : $"Worker#{Identity.Rank}";
                    var statusVerb = isLeader ? "Orchestrating" : "Working on";

                    // Preserve "Engineering complete" status once signaled so HealthMonitor can detect it
                    if (!_engineeringSignaled)
                    {
                        // Workers with an active PR: show the PR and task name they're working on
                        if (!isLeader && CurrentPrNumber is not null)
                        {
                            var taskDesc = _currentTaskName is not null
                                ? $"PR #{CurrentPrNumber}: {_currentTaskName}"
                                : $"PR #{CurrentPrNumber}";
                            UpdateStatus(AgentStatus.Working,
                                $"[{leaderTag}] {taskDesc}");
                        }
                        else
                        {
                            var queuedDesc = _reviewQueue.Count > 0 ? $", {_reviewQueue.Count} PRs to review" : "";
                            var pendingDesc = pending > 0 ? $"{pending} tasks remaining{queuedDesc}" : "All tasks assigned";
                            UpdateStatus(hasActionableWork ? AgentStatus.Working : AgentStatus.Idle,
                                $"[{leaderTag}] {statusVerb} — {pendingDesc}");
                        }
                    }

                    // ── Task inventory step: log task states so the dashboard shows the breakdown ──
                    if (!_taskInventoryLogged && total > 0)
                    {
                        var taskGroupId = "task-inventory";
                        var inventoryStepId = TaskTracker.BeginStep(Identity.Id, taskGroupId, "Task Inventory",
                            $"{done}/{total} done, {pending} pending, {total - done - pending} in-progress", Identity.ModelTier);

                        foreach (var t in _taskManager.Tasks.Where(t => t.Id != IntegrationTaskId))
                        {
                            var statusIcon = t.Status switch
                            {
                                "Pending" => "⏳",
                                "Assigned" => "👤",
                                "InProgress" => "🔧",
                                "Done" or "Closed" => "✅",
                                _ => "❓"
                            };
                            var prRef = t.PullRequestNumber.HasValue ? $" PR #{t.PullRequestNumber}" : "";
                            var taskStepId = TaskTracker.BeginStep(Identity.Id, taskGroupId,
                                $"{statusIcon} {t.Name}",
                                $"Status: {t.Status}, Complexity: {t.Complexity}{prRef}",
                                Identity.ModelTier);
                            TaskTracker.CompleteStep(taskStepId);
                        }

                        TaskTracker.CompleteStep(inventoryStepId);
                        _taskInventoryLogged = true;
                    }

                    // Startup recovery (runs once) + per-tick PR status monitoring
                    await RunRecoveryAndPrMonitoringAsync(ct);
                    // LEADER ONLY: Evaluate resource needs FIRST so spawns happen before leader grabs tasks
                    // Skip in SinglePR & Serial modes — no parallelization benefit
                    if (isLeader && !_allTasksComplete && Config.Limits.AllowsParallelEngineering)
                        await EvaluateResourceNeedsAsync(ct);

                    // Priority 0: Continue work on our own in-progress PR before anything else.
                    // Once the PR reaches "past implementation" (reviewer labels present), hand
                    // off to review/merge flows so the SE can start the next task in parallel.
                    if (CurrentPrNumber is not null)
                    {
                        if (await IsOwnPrPastImplementationAsync(ct))
                        {
                            var releasedPr = CurrentPrNumber.Value;
                            TrackPastImplementationPr(releasedPr);

                            // Mark the corresponding task done so CheckAllTasksCompleteAsync
                            // can detect engineering completion. Without this, the task issue
                            // stays open and the SE never signals engineering.all.complete.
                            var taskForPr = _taskManager.Tasks.FirstOrDefault(t =>
                                t.PullRequestNumber == releasedPr && t.IssueNumber.HasValue);
                            if (taskForPr is not null)
                            {
                                await _taskManager.MarkImplementationCompleteAsync(taskForPr.IssueNumber!.Value, releasedPr, ct);
                                Logger.LogInformation(
                                    "Task {TaskId} (issue #{IssueNumber}) marked ImplementationComplete — PR #{PrNumber} is past implementation",
                                    taskForPr.Id, taskForPr.IssueNumber.Value, releasedPr);
                            }

                            Logger.LogInformation(
                                "SE PR #{PrNumber} past implementation — releasing CurrentPrNumber to pick up next task",
                                releasedPr);
                            CurrentPrNumber = null;
                            _currentTaskName = null;
                            Identity.AssignedPullRequest = null;
                        }
                        else
                        {
                            await ContinueOwnPrImplementationAsync(ct);
                            continue; // Skip reviews until our own PR is done
                        }
                    }
                    // Priority 1: Process rework feedback on our own PRs
                    await ProcessOwnReworkAsync(ct);

                    // Check if all tasks are complete → integration phase (LEADER ONLY)
                    // Guard: only check completion if planning is done and tasks were created
                    if (!_allTasksComplete && isLeader && _planningComplete)
                    {
                        await CheckAllTasksCompleteAsync(ct);
                    }

                    if (_allTasksComplete && isLeader)
                    {
                        // SinglePR mode: no integration PR needed — signal completion directly
                        if (Config.Limits.IsSinglePr)
                        {
                            if (!_engineeringSignaled)
                            {
                                Logger.LogInformation("SinglePR mode: all tasks complete and merged — signaling engineering complete (no integration PR)");
                                await SignalEngineeringCompleteAsync(ct);
                            }
                        }
                        // Multi-PR mode (parallel or serial): create integration PR to combine all task PRs
                        else if (!_integrationPrCreated)
                        {
                            await CreateIntegrationPRAsync(ct);
                        }
                        else
                        {
                            // Safety: verify the integration PR actually exists (open or merged).
                            // After crash/restart, _integrationPrCreated may be stale if the
                            // old integration PR was closed (not merged) during shutdown.
                            var openPRs = await GetCachedOpenPRsAsync(ct);
                            var mergedPRs = await GetCachedMergedPRsAsync(ct);
                            var hasIntegration = openPRs.Concat(mergedPRs).Any(pr =>
                                IsCurrentRunScopePr(pr) &&
                                (PullRequestWorkflow.Labels.IsFinalIntegrationPr(pr.Labels, pr.Title, pr.HeadBranch) ||
                                 (CurrentPrNumber.HasValue && pr.Number == CurrentPrNumber.Value)));
                            if (!hasIntegration)
                            {
                                // Guard: only re-create if we haven't already created one this run.
                                // The _integrationPrRecreateCount prevents infinite re-invocations
                                // when the title-match heuristic fails to find the PR.
                                _integrationPrRecreateCount++;
                                if (_integrationPrRecreateCount > 1 && _integrationPrRecreateCount <= 5)
                                {
                                    Logger.LogWarning(
                                        "Integration PR re-creation suppressed (attempt {Count}) — the PR may have been renamed or the title search is too narrow",
                                        _integrationPrRecreateCount);
                                }
                                else if (_integrationPrRecreateCount > 5 && _integrationPrRecreateCount % 50 == 0)
                                {
                                    // After 5 suppressions, periodically retry in case the initial
                                    // CreateIntegrationPRAsync failed due to transient issues (empty
                                    // strategy patch, CLI pipe error, etc.)
                                    Logger.LogWarning(
                                        "Integration PR re-creation: retrying after {Count} suppressed attempts",
                                        _integrationPrRecreateCount);
                                    _integrationPrCreated = false;
                                    _engineeringSignaled = false;
                                    await CreateIntegrationPRAsync(ct);
                                }
                                else if (_integrationPrRecreateCount <= 1)
                                {
                                    Logger.LogWarning("Integration PR was marked as created but no open/merged integration PR found — re-creating (attempt 1)");
                                    _integrationPrCreated = false;
                                    _engineeringSignaled = false;
                                    await CreateIntegrationPRAsync(ct);
                                }
                            }
                            else if (!_engineeringSignaled)
                            {
                                await SignalEngineeringCompleteAsync(ct);
                            }
                        }
                    }
                    else if (!_allTasksComplete)
                    {
                        // LEADER ONLY: Merge approved PRs FIRST — before implementation work.
                        // This prevents the merge queue from stalling while the leader works on
                        // a long implementation (strategy framework can take 45+ min per task).
                        if (isLeader)
                            await MergeTestedPRsAsync(ct);

                        // LEADER ONLY: Recover orphaned assigned tasks with no open PRs
                        if (isLeader)
                            await RecoverOrphanedAssignmentsAsync(ct);
                        // LEADER ONLY: Assign issues to available workers (non-leader PEs, SE, JE)
                        if (isLeader)
                            await AssignTasksToAvailableEngineersAsync(ct);
                        // LEADER ONLY: Periodically warn on in-progress tasks with unmet deps
                        // (surfaces dep-gate bypass caused by regex mismatch or restart)
                        if (isLeader)
                            await WarnIfInProgressTasksDepsUnmetAsync(ct);
                        // ALL PEs: Work on own tasks (leader defers to spawned workers when available)
                        await WorkOnOwnTasksAsync(ct);
                        // ALL PEs: Discover open PRs needing review
                        await DiscoverUnreviewedEngineerPRsAsync(ct);
                    }

                    // ALL PEs: Always review PRs — even after all tasks complete
                    await DiscoverUnreviewedEngineerPRsAsync(ct);
                    await ReviewEngineerPRsAsync(ct);

                    // Merge PRs that have been fully approved (and tested if inline workflow)
                    await MergeTestedPRsAsync(ct);

                    // If we have no active work but are tracking past-implementation PRs
                    // (waiting for review/test/merge), show Idle instead of Working.
                    if (CurrentPrNumber is null && PastImplementationPrCount > 0
                        && Status == AgentStatus.Working)
                    {
                        UpdateStatus(AgentStatus.Idle,
                            $"Waiting for PR(s) to complete review/test/merge ({PastImplementationPrCount} pending)");
                    }
                    else if (CurrentPrNumber is null && _allTasksComplete && _integrationPrCreated
                        && Status == AgentStatus.Working)
                    {
                        UpdateStatus(AgentStatus.Idle, "Waiting for integration PR to merge");
                    }

                }

                await WaitForWakeOrTimeoutAsync(
                    TimeSpan.FromSeconds(Config.Limits.GitHubPollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Software Engineer loop error, continuing after brief delay");
                RecordError($"SE error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Working, "Recovering from error");
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "Software Engineer loop exited");
    }

    #endregion

    #region Phase 1 — Wait for Architecture and Create Plan

    private async Task<bool> CheckForArchitectureAsync(CancellationToken ct)
    {
        try
        {
            // Path 1: PM sent PlanningComplete AND Architecture.md has real content
            if (_planningSignalReceived)
            {
                var archDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
                if (!archDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                    && archDoc.Length > 200)
                {
                    Logger.LogInformation("Planning complete signal received and Architecture.md ready");
                    return true;
                }
                Logger.LogInformation("Planning signal received but Architecture.md not ready yet, waiting...");
            }

            // Path 2: Architect sent ArchitectureComplete via message bus AND enhancement issues exist
            // BUG FIX: Replaced issue-polling fallback. Previously the Architect created a
            // spurious GitHub Issue to notify PE ("Software Engineer: Question from Architect").
            // Now uses the _architectureReady flag set by the ArchitectureComplete bus message.
            if (_architectureReady)
            {
                var enhancements = await WorkItemService.ListByLabelAsync(
                    IssueWorkflow.Labels.Enhancement, ct: ct);
                if (_planningSignalReceived || enhancements.Count > 0)
                {
                    Logger.LogInformation(
                        "Architecture ready signal received via bus, {Count} enhancement issues found",
                        enhancements.Count);
                    return true;
                }
                Logger.LogInformation("Architecture ready but no enhancement issues yet, waiting for PM...");
            }

            // Path 3: Recovery — Architecture.md exists on disk with real content AND enhancement issues exist
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            if (!architectureDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                && architectureDoc.Length > 200
                && architectureDoc.Contains("## System Components", StringComparison.OrdinalIgnoreCase))
            {
                var enhancementIssues = await WorkItemService.ListByLabelAsync(
                    IssueWorkflow.Labels.Enhancement, ct: ct);
                if (enhancementIssues.Count > 0)
                {
                    Logger.LogInformation(
                        "Architecture.md found with content and {Count} enhancement issues exist, proceeding",
                        enhancementIssues.Count);
                    return true;
                }

                // 2026-05-12 fix (workflow-recovery-pm-restarts-from-research, part 4):
                // Late-stage-restart path for workers. When the prior run completed and closed
                // its enhancement issues, the enhancement count is 0. Without this path,
                // worker SEs (non-leader) wait forever for enhancement issues that will never
                // come because PM correctly skipped recreating them (commits 1ef981e+165f2ae).
                // If engineering-task issues exist (any state), the project is past the
                // enhancement phase — workers proceed and SyncEngineeringPlanFromGitHubAsync
                // will populate _taskManager from the existing engineering-task issues.
                var engineeringTasksAny = await WorkItemService.ListByLabelAsync(
                    "engineering-task", "all", ct);
                if (engineeringTasksAny.Count > 0)
                {
                    Logger.LogInformation(
                        "Architecture.md found with content + {Count} engineering-task issues exist (any state) — " +
                        "project is past enhancement phase, worker proceeding via engineering-task recovery",
                        engineeringTasksAny.Count);
                    return true;
                }

                // Path 4: Mini-reset bootstrap — Architecture.md has content but issues were cleared.
                // Without this, the leader SE waits forever for enhancement issues that only it can create.
                // Only the leader proceeds (CreateEngineeringPlanAsync is a one-writer operation).
                if (IsLeader())
                {
                    Logger.LogInformation(
                        "Mini-reset recovery: Architecture.md present, no enhancement issues yet. " +
                        "Leader SE proceeding to create engineering plan.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check for architecture document");
        }

        return false;
    }

    private async Task CreateEngineeringPlanAsync(CancellationToken ct)
    {
        // ── Early-exit guard: engineering is already complete ────────────────
        // After a Runner restart, the workflow can recover to phase=Completion (all
        // engineering signals already fired) but the SE leader still falls into this
        // method because its in-process flags reset on restart. Without this guard,
        // it generates a NEW engineering plan and creates duplicate engineering-task
        // issues (regression seen in 2026-05-09 session: 8 dupes #1293-#1300 created
        // even though T1-T6 + T-FINAL were all already merged).
        //
        // Detection: we have merged engineering PRs for THIS run AND zero open
        // engineering-task issues remain.
        //
        // CRITICAL (2026-05-11 fix, post-run-restart-t1-duplicate): the previous filter
        // (HeadBranch.Contains("/softwareengineer")) was too NARROW — it missed SME-
        // engineer-merged PRs (e.g. Game Developer 1's merged PR #1430 in the
        // 2026-05-11 GridGuardians run), causing the SE leader to regenerate T1 as
        // duplicate PR #1440 after a restart. The original f95607a fix (2026-05-10)
        // was correctly excluding non-engineer roles but too restrictive about which
        // engineer roles to accept. We now use the central
        // EngineeringTaskIssueManager.IsEngineeringPrBranch helper which accepts any
        // agent/ branch whose role-segment is NOT an explicit non-engineer role.
        try
        {
            var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                .Where(IsCurrentRunScopePr)
                .ToList();

            // CRITICAL: filter to CURRENT run scope only. Without this, merged PRs from
            // a previous run (which survive minimal-reset because they're already merged)
            // cause the SE to skip plan creation entirely, fast-forwarding to "complete"
            // without doing any work. Branch format: agent/{runScope}/{agentSlug}/{taskSlug}
            var currentRunScope = BranchProvider?.RunScope;
            var ownedMergedPRs = mergedPRs
                .Where(p => EngineeringTaskIssueManager.IsEngineeringPrBranch(p.HeadBranch))
                .Where(p => IsCurrentRunScopePr(p))
                .ToList();
            var openEngineeringTasks = await WorkItemService.ListByLabelAsync(
                EngineeringTaskIssueManager.TaskLabel, "open", ct);

            if (ownedMergedPRs.Count > 0 && openEngineeringTasks.Count == 0)
            {
                var forceRedo = _strategyConfig?.CurrentValue.ForceRedoFinalIntegration == true;
                if (forceRedo)
                {
                    Logger.LogInformation(
                        "ForceRedoFinalIntegration=true: {MergedCount} merged PRs but NOT short-circuiting — T-FINAL will re-run",
                        ownedMergedPRs.Count);
                    _allTasksComplete = true;
                    _planningComplete = true;
                    // Don't set _integrationPrCreated or _engineeringSignaled
                }
                else
                {
                    Logger.LogInformation(
                        "Engineering already complete on restart: {MergedCount} merged engineering PR(s), 0 open engineering-task issues — short-circuiting plan creation",
                        ownedMergedPRs.Count);
                    LogActivity("system", $"♻️ Recovery: engineering already complete ({ownedMergedPRs.Count} merged PRs, no open tasks) — skipping plan generation");

                    _allTasksComplete = true;
                    _integrationPrCreated = true;
                    // DO NOT set _engineeringSignaled = true here. The main loop's safety
                    // check (line ~651) will detect !_engineeringSignaled, find the merged
                    // integration PR, and call SignalEngineeringCompleteAsync — which is
                    // where SubmitFinalPRAsync (the GitHub push) lives. Setting the flag
                    // here blocked final submission on every recovery restart. (Bug fix)
                    _planningComplete = true;

                    // Load task data so SignalEngineeringCompleteAsync has correct counts
                    // for the final PR title/body (e.g., "Complete Implementation (10 tasks)")
                    try { await _taskManager.LoadTasksAsync(ct); }
                    catch (Exception loadEx)
                    {
                        Logger.LogWarning(loadEx, "Recovery: failed to load task counts — final PR will show 0 tasks");
                    }

                    UpdateStatus(AgentStatus.Working, "Engineering complete — submitting final PR");

                    // Emit signals so the workflow phase doesn't get stuck waiting for
                    // EngineeringPlanReady that will never come on this restart.
                    await PublishStatusAsync("EngineeringPlanReady", AgentStatus.Idle,
                        details: $"Restored: engineering already complete ({ownedMergedPRs.Count} merged PRs).",
                        currentTask: "Recovery", ct: ct);
                    // Don't emit EngineeringComplete here — let SignalEngineeringCompleteAsync do it
                    // after the final PR is submitted, so we get the correct sequence.
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Don't let the recovery probe break legitimate first-run plan creation.
            Logger.LogWarning(ex, "Engineering-already-complete recovery probe failed (non-fatal) — proceeding with normal plan creation");
        }

        // Set enhancement scope FIRST so all subsequent LoadTasksAsync calls filter stale tasks
        var scopeEnhancements = await WorkItemService.ListByLabelAsync(
            IssueWorkflow.Labels.Enhancement, ct: ct);
        if (scopeEnhancements.Count > 0)
        {
            _taskManager.SetEnhancementScope(scopeEnhancements.Select(i => i.Number));
        }

        // Recovery: check for existing engineering-task issues from a prior run
        await _taskManager.LoadTasksAsync(ct);
        if (_taskManager.TotalCount > 0)
        {
            // With enhancement scope set, loaded tasks are already filtered to current run.
            // Double-check: validate at least one task has a matching parent.
            var enhancementNumbers = scopeEnhancements.Select(i => i.Number).ToHashSet();
            var hasMatchingParent = _taskManager.Tasks.Any(t =>
                t.ParentIssueNumber.HasValue && enhancementNumbers.Contains(t.ParentIssueNumber.Value));

            if (scopeEnhancements.Count == 0 || !hasMatchingParent)
            {
                // 2026-05-12 fix (workflow-recovery-pm-restarts-from-research, part 3):
                // Before discarding restored tasks as "stale", check if any are PAST Pending
                // state. Enhancement issues from a completed prior run are typically CLOSED
                // (so scopeEnhancements.Count == 0 here) — but the engineering tasks they
                // spawned ARE the source of truth for what was/is being worked on. Discarding
                // them and waiting for PM to recreate enhancement issues is wrong; the project
                // is past the enhancement phase by definition. Only clear if every task is
                // truly fresh-Pending (no prior progress at all).
                //
                // 2026-05-16 fix: also check for associated PRs on tasks. If any task has
                // a PullRequestNumber, it was worked on — even if its status label is still
                // "Pending" (status labels can lag behind actual work). Also check if any
                // task is from the current run scope (has open issues, not just closed history).
                var anyTaskPastPending = _taskManager.Tasks.Any(t =>
                    EngineeringTaskIssueManager.IsTaskPastImplementation(t)
                    || !string.Equals(t.Status, "Pending", StringComparison.OrdinalIgnoreCase)
                    || t.PullRequestNumber.HasValue);

                // Additional check: if there are OPEN engineering tasks, they're from the
                // current/recent run and should be kept regardless of enhancement scope.
                var hasOpenTasks = _taskManager.Tasks.Any(t =>
                    t.IssueNumber.HasValue && string.Equals(t.Status, "Pending", StringComparison.OrdinalIgnoreCase));

                if (anyTaskPastPending || hasOpenTasks)
                {
                    Logger.LogInformation(
                        "Found {Count} engineering-task issues from a prior run (some past Pending) — " +
                        "keeping them and skipping enhancement-mismatch discard. Project is in resume mode.",
                        _taskManager.TotalCount);
                    // Fall through to restore path below (don't clear)
                }
                else
                {
                    Logger.LogWarning(
                        "Found {Count} engineering-task issues but they don't match current enhancement issues — ignoring stale tasks",
                        _taskManager.TotalCount);
                    _taskManager.ClearCache();
                    // Fall through to fresh-plan path with empty cache
                }
            }

            if (_taskManager.TotalCount > 0)
            {
                Logger.LogInformation("Restored {Count} tasks from existing engineering-task issues ({Done} done, {Pending} pending)",
                    _taskManager.TotalCount, _taskManager.DoneCount, _taskManager.PendingCount);

                // Add visible planning steps so the dashboard shows what happened during recovery
                var restoreStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Restore engineering plan",
                    $"Recovered {_taskManager.TotalCount} tasks from existing issues ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)", Identity.ModelTier);

                // Register display names for restored tasks so dashboard shows meaningful titles
                RegisterTaskDisplayNames(_taskManager.Tasks);

                // Recover integration issue number if present
                var recoveredIntegration = _taskManager.Tasks.FirstOrDefault(IsIntegrationTask);
                if (recoveredIntegration?.IssueNumber is not null)
                    _integrationIssueNumber = recoveredIntegration.IssueNumber;

                // Re-establish native GitHub blocked-by dependency links. These come from a separate
                // API call and are NOT restored by LoadTasksAsync (which only reads issue metadata).
                // AddIssueDependencyAsync is idempotent (422 on duplicate is swallowed), so safe
                // to call every time — this ensures the UI shows "Blocked by" indicators after
                // any resume / mini-reset where issues were preserved but links weren't.
                try
                {
                    LogActivity("planning", "🔗 Re-establishing task dependency links from restored issues");
                    await _taskManager.LinkTaskDependenciesAsync(_taskManager.Tasks.ToList(), ct);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Dependency link restoration failed (non-fatal)");
                }

                TaskTracker.CompleteStep(restoreStepId);

                // Belt-and-suspenders: cross-reference our past-implementation PRs against tasks.
                // If any task has a ready-for-review (or later) PR but is not marked Done,
                // mark it Done now. This handles the race where the runner crashed after
                // FinalizeReadyForReviewAsync but before MarkDoneAsync completed.
                // Also tracks MERGED PRs so the closed-without-PR orphan check below
                // doesn't reopen tasks whose work is already on main.
                var mergedPrTitlesByTaskName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var ourOpenPRs = (await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
                    var pastImplPRs = ourOpenPRs
                        .Where(pr => pr.Labels.Any(l =>
                            l == "ready-for-review" || l == "architect-approved" ||
                            l == "pm-approved" || l == "approved" || l == "tests-added"))
                        .ToList();

                    foreach (var prInfo in pastImplPRs)
                    {
                        // Find the matching task by title (PR title format: "AgentName: TaskTitle")
                        var colonIdx = prInfo.Title.IndexOf(':');
                        var prTaskTitle = colonIdx >= 0 ? prInfo.Title[(colonIdx + 1)..].Trim() : prInfo.Title;

                        var matchingTask = _taskManager.Tasks.FirstOrDefault(t =>
                            !EngineeringTaskIssueManager.IsTaskPastImplementation(t) &&
                            t.IssueNumber.HasValue &&
                            t.Name.Equals(prTaskTitle, StringComparison.OrdinalIgnoreCase));

                        if (matchingTask is not null)
                        {
                            await _taskManager.MarkImplementationCompleteAsync(matchingTask.IssueNumber!.Value, prInfo.Number, ct);
                            Logger.LogInformation(
                                "Recovery: marked task {TaskId} (issue #{Issue}) ImplementationComplete — matched open PR #{Pr} with past-implementation labels",
                                matchingTask.Id, matchingTask.IssueNumber, prInfo.Number);
                        }
                    }

                    // Also collect MERGED PRs so we can prove completion for already-merged tasks.
                    // Without this, a task that was Done + closed + had its PR merged appears as
                    // "closed without PR" because the cross-reference above only inspected open PRs.
                    var mergedPRs = await GetCachedMergedPRsAsync(ct);
                    foreach (var mergedPr in mergedPRs.Where(IsCurrentRunScopePr))
                    {
                        if (!mergedPr.Title.StartsWith(Identity.DisplayName + ":", StringComparison.OrdinalIgnoreCase) &&
                            !mergedPr.Title.StartsWith(Identity.DisplayName + " ", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var colonIdx = mergedPr.Title.IndexOf(':');
                        var prTaskTitle = colonIdx >= 0 ? mergedPr.Title[(colonIdx + 1)..].Trim() : mergedPr.Title;
                        if (!string.IsNullOrWhiteSpace(prTaskTitle))
                            mergedPrTitlesByTaskName[prTaskTitle] = mergedPr.Number;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "PR cross-reference recovery failed (non-fatal)");
                }

                // Recover completion state from restored tasks to prevent duplicate work on restart
                var restoredNonIntegration = _taskManager.Tasks
                    .Where(t => t.Id != IntegrationTaskId)
                    .ToList();

                // Safety: re-open tasks that are "Done" but never had a PR (externally closed).
                // EXCEPT: a task whose name matches a MERGED PR title is legitimately complete —
                // the PullRequestNumber field just wasn't persisted across restarts (see Lessons
                // Learned #7). Treat these as Done, do NOT reset to Pending.
                var closedWithoutPrOnRestore = restoredNonIntegration
                    .Where(t => EngineeringTaskIssueManager.IsTaskDone(t)
                                && t.PullRequestNumber is null or 0
                                && !mergedPrTitlesByTaskName.ContainsKey(t.Name))
                    .ToList();

                // Tasks that ARE backed by a merged PR — heal the in-memory state so they
                // don't trip the orphan check on subsequent loops.
                var healedFromMergedPr = restoredNonIntegration
                    .Where(t => EngineeringTaskIssueManager.IsTaskDone(t)
                                && t.PullRequestNumber is null or 0
                                && mergedPrTitlesByTaskName.ContainsKey(t.Name))
                    .ToList();
                if (healedFromMergedPr.Count > 0)
                {
                    Logger.LogInformation(
                        "Recovery: {Count} Done task(s) had no PR # in tracker but match merged PRs — keeping as Done: {Tasks}",
                        healedFromMergedPr.Count,
                        string.Join(", ", healedFromMergedPr.Select(t =>
                            $"{t.Id} (#{t.IssueNumber} → merged PR #{mergedPrTitlesByTaskName[t.Name]})")));
                }
                if (closedWithoutPrOnRestore.Count > 0)
                {
                    Logger.LogWarning(
                        "Recovery: {Count} task(s) closed without PR — re-opening: {Tasks}",
                        closedWithoutPrOnRestore.Count,
                        string.Join(", ", closedWithoutPrOnRestore.Select(t => $"{t.Id} (#{t.IssueNumber})")));
                    foreach (var orphan in closedWithoutPrOnRestore)
                    {
                        if (orphan.IssueNumber.HasValue)
                        {
                            await _taskManager.ResetToPendingAsync(orphan.IssueNumber.Value, ct);
                            ClaimRegistry?.Release(orphan.IssueNumber.Value);
                        }
                    }
                }

                if (restoredNonIntegration.Count > 0
                    && closedWithoutPrOnRestore.Count == 0
                    && restoredNonIntegration.All(EngineeringTaskIssueManager.IsTaskPastImplementation))
                {
                    _allTasksComplete = true;
                    Logger.LogInformation("State recovery: all {Count} non-integration tasks are Done — setting _allTasksComplete", restoredNonIntegration.Count);

                    // Check if integration PR was already created and/or merged
                    var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
                    var openPRs = (await GetCachedOpenPRsAsync(ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
                    var allPRs = mergedPRs.Concat(openPRs).ToList();
                    var integrationPR = allPRs.FirstOrDefault(pr =>
                        PullRequestWorkflow.Labels.IsFinalIntegrationPr(pr.Labels, pr.Title, pr.HeadBranch));

                    // ForceRedoFinalIntegration: skip integration PR recovery so T-FINAL re-runs
                    var forceRedo = _strategyConfig?.CurrentValue.ForceRedoFinalIntegration == true;
                    if (forceRedo && integrationPR is not null)
                    {
                        Logger.LogInformation(
                            "ForceRedoFinalIntegration=true: ignoring existing integration PR #{Number} — T-FINAL will re-run",
                            integrationPR.Number);
                    }
                    else if (integrationPR is not null)
                    {
                        _integrationPrCreated = true;
                        Logger.LogInformation("State recovery: found integration PR #{Number} (merged={IsMerged}) — setting _integrationPrCreated",
                            integrationPR.Number, integrationPR.IsMerged);
                    }

                    // If all tasks done AND no open PRs AND at least one merged PR, engineering is complete
                    if (openPRs.Count == 0 && mergedPRs.Count > 0)
                    {
                        if (!forceRedo)
                        {
                            _integrationPrCreated = true;
                            // DO NOT set _engineeringSignaled = true here — same fix as
                            // the short-circuit path above. Let the main loop call
                            // SignalEngineeringCompleteAsync so SubmitFinalPRAsync runs.
                            Logger.LogInformation("State recovery: no open PRs + {Count} merged — final submission pending", mergedPRs.Count);
                            UpdateStatus(AgentStatus.Working, "Engineering complete — submitting final PR");
                        }
                        else
                        {
                            _engineeringSignaled = false;
                            Logger.LogInformation(
                                "ForceRedoFinalIntegration=true: skipping engineering-complete recovery — T-FINAL will re-run");
                        }
                    }
                }

                _planningComplete = true;
                UpdateStatus(AgentStatus.Working,
                    $"Loaded {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)");

                // Emit the plan-ready signal so workflow can advance
                await PublishStatusAsync("EngineeringPlanReady", AgentStatus.Working,
                    details: $"Restored engineering plan with {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending).",
                    currentTask: "Engineering Planning", ct: ct);

                return;
            }
        }

        UpdateStatus(AgentStatus.Working, "Creating engineering plan from Issues");
        Logger.LogInformation("Starting engineering plan creation from Enhancement issues");

        LogActivity("planning", "📋 Reading architecture doc and PM spec for engineering planning");
        UpdateStatus(AgentStatus.Working, "📋 Gathering context for engineering plan");
        var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
        var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
        var teamComposition = await ProjectFiles.GetTeamCompositionAsync(ct);

        var enhancementIssues = scopeEnhancements;

        if (enhancementIssues.Count == 0)
        {
            if (!_loggedWaitingForPmIssues)
            {
                Logger.LogWarning("No open enhancement issues found — PM may not have created them yet, will retry");
                LogActivity("task", "⏳ Waiting for PM to create User Story Issues before engineering planning");
                _loggedWaitingForPmIssues = true;
            }
            UpdateStatus(AgentStatus.Idle, "Waiting for PM to create User Story Issues");
            _planningComplete = false;
            return;
        }
        // Reset the guard so the next stall (if any) logs once again.
        _loggedWaitingForPmIssues = false;

        LogActivity("task", "📋 Starting engineering plan creation from Enhancement issues");

        var decompStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Task decomposition",
            "Decomposing enhancement issues into engineering tasks", Identity.ModelTier);

        var issuesSummary = string.Join("\n\n", enhancementIssues.Select(i =>
            $"### Issue #{i.Number}: {i.Title}\n{i.Body}"));

        // Single-issue mode enrichment: resolve doc links in the issue body
        // This allows the SE to get full PMSpec/Architecture content even when
        // the issue body only contains summary + links to the actual docs
        if (Config.Limits.SingleIssueMode && Platform.DocResolver is not null && enhancementIssues.Count == 1)
        {
            try
            {
                var issue = enhancementIssues[0];
                var resolveContext = new DocumentResolutionContext(EffectiveBranch);
                var resolvedDocs = await Platform.DocResolver?.ResolveReferencesAsync(issue.Body ?? "", resolveContext, ct);

                if (resolvedDocs.Count > 0)
                {
                    Logger.LogInformation("Resolved {Count} document references from single Enhancement issue #{Number}",
                        resolvedDocs.Count, issue.Number);

                    foreach (var doc in resolvedDocs)
                    {
                        // Enrich architecture and PMSpec if not already loaded with meaningful content
                        if (doc.Path.EndsWith("Architecture.md", StringComparison.OrdinalIgnoreCase)
                            && (architectureDoc.Contains("No architecture document") || architectureDoc.Length < 100))
                        {
                            architectureDoc = doc.Content;
                        }
                        else if (doc.Path.EndsWith("PMSpec.md", StringComparison.OrdinalIgnoreCase)
                            && (pmSpec.Contains("No PM specification") || pmSpec.Length < 100))
                        {
                            pmSpec = doc.Content;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Document reference resolution failed (non-fatal), continuing with direct file reads");
            }
        }

        // Fetch repo structure so PE can include file path guidance in tasks
        var repoStructure = await GetRepoStructureForContextAsync(ct);

        // Read visual design reference files for UI task context
        var designContext = await ReadDesignReferencesAsync(ct);

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var parsedTasks = new List<EngineeringTask>();
        var singlePrMode = Config.Limits.SinglePRMode;
        VirtualDevTeam.Core.Agents.Reasoning.AssessmentResult? peAssessmentResult = null;

        if (singlePrMode)
        {
            Logger.LogInformation("SinglePRMode=true — bypassing fragmented planning. Producing one monolithic engineering task.");
            LogActivity("planning", "🧩 SinglePRMode enabled — producing ONE engineering task for the whole project");

            var monolithicDesc = new StringBuilder();
            monolithicDesc.AppendLine("**Deliver the ENTIRE project as a single cohesive implementation.**");
            monolithicDesc.AppendLine();
            monolithicDesc.AppendLine("This task runs in SinglePRMode. Do NOT split the work into multiple PRs and do NOT emit partial scaffolding. Produce every file the project needs in ONE implementation pass: project manifests, entry point, DI registration, all data models, all services, all components/pages, CSS, sample data, and any required config.");
            monolithicDesc.AppendLine();
            monolithicDesc.AppendLine("After this task merges, the product must BUILD and RUN end-to-end with no follow-up wiring PRs required. The T-FINAL integration task exists only as a safety net — aim to have no integration fixes needed.");
            monolithicDesc.AppendLine();
            monolithicDesc.AppendLine("## Scope (all user stories in the plan)");
            foreach (var issue in enhancementIssues)
                monolithicDesc.AppendLine($"- Issue #{issue.Number}: {issue.Title}");
            monolithicDesc.AppendLine();
            monolithicDesc.AppendLine("Reference the PM Spec, Architecture document, and any design images supplied. Where design images are provided, match them pixel-for-pixel — do not simplify, rename sections, or rearrange the layout.");

            parsedTasks.Add(new EngineeringTask
            {
                Id = "T1",
                Name = "Implement entire project (SinglePRMode)",
                Description = monolithicDesc.ToString(),
                Complexity = "High",
                Dependencies = new List<string>(),
                ParentIssueNumber = enhancementIssues.FirstOrDefault()?.Number,
                RelatedEnhancementNumbers = enhancementIssues.Select(e => e.Number).ToList(),
                Wave = "W0",
                OwnedFiles = new List<string>(),
                SkillTags = new List<string> { "fullstack", "foundation" }
            });

            TaskTracker.CompleteStep(decompStepId);
        }
        else
        {

        // ── Project Complexity Assessment ──────────────────────────────────────
        // Assess project size to guide task count. Uses enhancement issue count as
        // primary signal, with architecture doc breadth as secondary.
        var (projectComplexity, targetTaskCount) = AssessProjectComplexity(
            enhancementIssues.Count, architectureDoc);
        Logger.LogInformation("Project complexity assessed as {Complexity} — targeting {Count} tasks max",
            projectComplexity, targetTaskCount);
        LogActivity("planning", $"📊 Project complexity: {projectComplexity} — targeting ≤{targetTaskCount} tasks");

        var promptVars = new Dictionary<string, string>
        {
            ["projectComplexity"] = projectComplexity,
            ["targetTaskCount"] = targetTaskCount.ToString(),
            ["unanswered_decisions"] = DecisionContextBuilder.BuildUnansweredDecisionsContext(
                Core!.Config.UnansweredDecisionQuestions, _decisionLog)
        };

        var history = CreateChatHistory();
        var planSys = PromptService is not null
            ? await PromptService.RenderAsync("software-engineer/plan-generation-system",
                promptVars, ct)
            : null;
        history.AddSystemMessage(planSys ??
            "You are a Software Engineer creating an engineering plan from GitHub Issues (User Stories), " +
            "an architecture document, and a PM specification. " +
            "Each GitHub Issue represents a User Story or Feature from the PM Spec.\n\n" +
            "Your job is to:\n" +
            "1. Review each Issue and the architecture/PM spec\n" +
            "2. Map each Issue to one or more engineering tasks\n" +
            "3. Classify each task by complexity (High/Medium/Low)\n" +
            "4. Identify dependencies between tasks\n" +
            "5. Reference the source GitHub Issue number for each task\n" +
            "6. For each task, specify which files to create/modify and the namespace to use\n\n" +

            "## CRITICAL — Foundation Task (MUST be Task T1)\n" +
            "The FIRST task (T1) MUST ALWAYS be a 'Project Foundation & Scaffolding' task that:\n" +
            "- Creates a **comprehensive, technology-specific .gitignore** — this MUST be `CREATE:.gitignore` as the FIRST entry in T1's FilePlan. " +
            "Derive all patterns from the project's technology stack and architecture. " +
            "Cover: build/compiler output, dependency directories, package artifacts, IDE/editor files, OS junk, " +
            "secrets/env, test/coverage output, logs/temp/cache, and framework-generated files. " +
            "For multi-tech projects, aggregate patterns for ALL components in one root file. " +
            "Do NOT ignore lockfiles, migrations, seed data, or runtime data files the app needs.\n" +
            "- Sets up the solution/project structure, build configuration, and shared infrastructure\n" +
            "- Creates the core data models, interfaces, and abstractions from the architecture document\n" +
            "- Establishes the directory layout, namespaces, and integration points that all other tasks build upon\n" +
            "- Includes dependency injection registration, configuration models, and shared utilities\n" +
            "- Complexity: High (this is the most important task — it sets the foundation)\n" +
            "- Has NO dependencies (all other tasks should depend on T1)\n\n" +

            "### T1 MUST create comprehensive placeholders for parallel engineers:\n" +
            "T1 is the SINGLE SOURCE OF TRUTH for the project skeleton. It must be thorough enough that " +
            "NO other task needs to create foundational files. Specifically, T1 must:\n" +
            "- Create ALL shared data model files with complete record/class definitions\n" +
            "- Create ALL service interfaces (e.g., IDataService, IAuthService) as real interfaces with method signatures\n" +
            "- Create the application entry point (Program.cs) with ALL DI registrations as stubs " +
            "(e.g., `builder.Services.AddSingleton<IMyService, MyService>();` even if MyService is a placeholder)\n" +
            "- Create stub/skeleton component files for EVERY major UI component or page. " +
            "**CRITICAL FOR WEB/UI PROJECTS**: Each placeholder component MUST be VISUALLY DISTINCT — " +
            "use colored backgrounds (#f0f0f0, #e8f4fd, #fef3cd, etc.), visible borders (1px solid #ccc), " +
            "padding, and large bold label text (e.g., '📊 Heatmap Component — Placeholder'). " +
            "A completely blank white page means the scaffold FAILED. " +
            "The goal is that when you take a screenshot, every section is clearly visible with its name.\n" +
            "- Create the global CSS file with the full layout structure and clearly marked section boundaries " +
            "(e.g., `/* === HEADER STYLES === */`, `/* === FOOTER STYLES === */`). " +
            "Include a `.placeholder` CSS class with: background-color: #f0f4f8; border: 2px dashed #94a3b8; " +
            "border-radius: 8px; padding: 2rem; text-align: center; font-size: 1.2rem; color: #475569;\n" +
            "- Create sample data files (e.g., data.json) that EXACTLY match the data model records/classes you define. " +
            "Every property in the C# record must have a corresponding JSON field with the correct casing and structure. " +
            "If DashboardData has a 'milestoneStreams' property of type List<MilestoneStream>, the JSON must have 'milestoneStreams' as an array of matching objects. " +
            "A schema mismatch between data.json and the data model will cause runtime validation errors.\n" +
            "- CRITICAL: data.json MUST be committed to the repository, NOT gitignored. " +
            "The app needs this file to start and render content. Do NOT create data.example.json or data.template.json as the primary file — " +
            "create data.json directly with sample data. The .gitignore must NOT exclude data.json.\n" +
            "- Create configuration files (launchSettings.json, appsettings.json) with correct ports and settings\n\n" +
            "The goal: after T1 merges, `dotnet build` (or equivalent) succeeds, `dotnet run` starts the app, " +
            "and the app renders a working shell with VISUALLY DISTINCT placeholder sections — " +
            "colored backgrounds, dashed borders, and clear labels for every component area. " +
            "A Playwright screenshot of the running app should show a clear grid/layout of labeled sections, NOT a blank white page. " +
            "Every subsequent task only FILLS IN existing placeholders — it never creates the skeleton.\n\n" +

            "### T1 owns ALL cross-cutting files exclusively:\n" +
            "- .gitignore, .sln, .csproj, Program.cs, App.razor, Routes.razor, _Imports.razor\n" +
            "- Global CSS (app.css), layout components, shared models, configuration\n" +
            "- These files appear ONLY in T1's FilePlan. Other tasks reference them as USE: or MODIFY: " +
            "ONLY if T1 declares them as SHARED.\n\n" +

            "## CRITICAL — EXACTLY ONE Task Creates Each File\n" +
            "This is the #1 rule for preventing merge conflicts and duplicate work:\n" +
            "- Every file in the repository MUST be owned by EXACTLY ONE task\n" +
            "- The task that CREATEs a file is its owner — no other task may CREATE the same file\n" +
            "- If another task needs to modify an owned file, the owner must declare it SHARED in the FilePlan\n" +
            "- Before assigning CREATE to any file, verify no other task in your plan already creates it\n" +
            "- If the 'Already-Merged PRs' section shows a file already exists on main, " +
            "NO task should CREATE it — use MODIFY: instead (or skip it entirely)\n\n" +

            "## CRITICAL — Repository Structure Rules\n" +
            "The repository root IS the solution root. All file paths are relative to the repo root.\n" +
            "- Place the `.sln` file at the REPO ROOT (e.g., `MyApp.sln`)\n" +
            "- Place source projects in a SINGLE project subfolder (e.g., `MyApp/MyApp.csproj`, `MyApp/Program.cs`)\n" +
            "- NEVER create multiple levels of folders with the same name — `MyApp/MyApp/MyApp/` is WRONG\n" +
            "- Only ONE `.gitignore` at the repo root — do NOT create nested `.gitignore` files in subfolders\n\n" +

            "## CRITICAL — Parallel-Friendly Task Decomposition\n" +
            "Multiple engineers will work on tasks IN PARALLEL. Design tasks to MINIMIZE overlap and merge conflicts:\n" +
            "- **Separate by component/module boundary**: each task should own a distinct set of files. " +
            "Two tasks should NEVER create or modify the same file (unless declared SHARED).\n" +
            "- **Vertical slicing over horizontal**: prefer tasks that implement a complete feature end-to-end " +
            "(model + service + component + tests) rather than tasks that cut across all features at one layer.\n" +
            "- **Explicit file ownership**: every task's FilePlan must list EXACTLY which files it creates or modifies. " +
            "If two tasks need to touch the same file (e.g., DI registration in Program.cs), " +
            "assign that responsibility to only ONE of them.\n" +
            "- **Shared infrastructure in T1**: anything that multiple tasks would need (base classes, interfaces, " +
            "config models, shared DTOs) should go in T1 so parallel tasks only CONSUME these, never create them.\n" +
            "- **Shared file registry**: If a file MUST be modified by multiple tasks (e.g., Program.cs for DI registration), " +
            "declare it as SHARED in T1's FilePlan (e.g., `SHARED:MyApp/Program.cs`). Only SHARED files may be touched by multiple tasks. " +
            "Keep shared files to an absolute minimum.\n" +
            "- **Minimize cross-task dependencies**: maximize tasks that depend ONLY on T1 " +
            "so they can all run in parallel. Chain dependencies (T3→T2→T1) should be rare.\n\n" +

            "## CRITICAL — Wave Scheduling for Parallel Execution\n" +
            "Assign each task to a WAVE that determines execution order:\n" +
            "- **W0**: Foundation task (T1) ONLY. Runs first, alone. Must complete before any other task starts.\n" +
            "- **W1**: Tasks that depend only on T1. These all run in parallel after T1 merges.\n" +
            "- **W2+**: Tasks depending on W1 tasks, and so on.\n\n" +
            "GOAL: At least 60% of non-foundation tasks should be in W1 (parallelizable immediately after T1). " +
            "A star topology (all tasks depend only on T1) is ideal — it maximizes W1 parallelism.\n" +
            "IMPORTANT: T1 is the ONLY task in W0. Do NOT put any other task in W0. " +
            "No two tasks in W0 should ever exist — that causes duplicate scaffolding.\n\n" +

            "## CRITICAL — Preventing Duplicate Work\n" +
            "If the 'Already-Merged PRs' section is present in the user prompt, it lists files that " +
            "ALREADY EXIST on the main branch from previously merged pull requests.\n" +
            "- Do NOT create tasks that recreate files listed in merged PRs\n" +
            "- If T1 scaffolding has already been merged, skip T1 entirely and start from W1 tasks\n" +
            "- Only include tasks for work that has NOT been done yet\n" +
            "- If a merged PR partially covers a feature, create a task only for the REMAINING work\n" +
            "- **NEVER create placeholder tasks named 'REMOVED', 'SKIP', 'N/A', or 'Merged into'.** " +
            "Simply OMIT tasks that are already done — do not include them in the output at all.\n" +
            "- Renumber remaining task IDs sequentially (T1, T2, T3...) after omitting done tasks.\n\n" +

            "CRITICAL: Review the existing repository structure carefully. " +
            "Tasks MUST reference existing files when appropriate (modify, not recreate). " +
            "New files should follow the existing directory structure and naming conventions.\n\n" +
            "Task complexity mapping:\n" +
            "- **High**: Complex tasks requiring deep expertise → Software Engineer\n" +
            "- **Medium**: Moderate tasks → Software Engineer\n" +
            "- **Low**: Straightforward tasks → Software Engineer");

        var userPromptBuilder = new System.Text.StringBuilder();
        userPromptBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");
        userPromptBuilder.AppendLine($"## Architecture Document\n{architectureDoc}\n");

        if (!string.IsNullOrWhiteSpace(teamComposition))
        {
            userPromptBuilder.AppendLine("## Team Composition");
            userPromptBuilder.AppendLine("The PM has analyzed the project and composed a team with specialist engineers. ");
            userPromptBuilder.AppendLine("When assigning SkillTags to tasks, align them with the specialist capabilities listed below ");
            userPromptBuilder.AppendLine("so the skill-based assignment algorithm can match tasks to the right engineers.");
            userPromptBuilder.AppendLine(teamComposition);
            userPromptBuilder.AppendLine();
        }

        if (!string.IsNullOrEmpty(repoStructure))
        {
            userPromptBuilder.AppendLine("## Existing Repository Structure (main branch)");
            userPromptBuilder.AppendLine(repoStructure);
            userPromptBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(designContext))
        {
            userPromptBuilder.AppendLine("## Visual Design Reference");
            userPromptBuilder.AppendLine("The repository contains design reference files that define the EXACT UI to be built. " +
                "For any UI-related task, include the relevant design details in the task description: " +
                "specific CSS patterns, color hex codes, layout structure, and component hierarchy.");
            userPromptBuilder.AppendLine(designContext);
            userPromptBuilder.AppendLine();
        }

        // Include files from already-merged PRs so the plan doesn't recreate them
        try
        {
            var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                .Where(IsCurrentRunScopePr)
                .ToList();
            if (mergedPRs.Count > 0)
            {
                var mergedFileSummary = new System.Text.StringBuilder();
                mergedFileSummary.AppendLine("## Already-Merged PRs (DO NOT recreate these files)");
                mergedFileSummary.AppendLine("The following PRs have already been merged. Their files ALREADY EXIST on main.");
                mergedFileSummary.AppendLine("Your plan MUST NOT include tasks that CREATE these files — they are done.\n");

                foreach (var mpr in mergedPRs.Take(10))
                {
                    var prFiles = await PrService.GetChangedFilesAsync(mpr.Number, ct);
                    if (prFiles.Count > 0)
                    {
                        mergedFileSummary.AppendLine($"### PR #{mpr.Number}: {mpr.Title}");
                        mergedFileSummary.AppendLine($"Files: {string.Join(", ", prFiles)}");
                        mergedFileSummary.AppendLine();
                    }
                }
                userPromptBuilder.AppendLine(mergedFileSummary.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not fetch merged PRs for plan deduplication — proceeding without");
        }

        userPromptBuilder.AppendLine($"## GitHub Issues (User Stories)\n{issuesSummary}\n");
        var planUserSuffix = PromptService is not null
            ? await PromptService.RenderAsync("software-engineer/plan-generation-user-suffix",
                promptVars, ct)
            : null;
        userPromptBuilder.AppendLine(planUserSuffix ??
            "Create an engineering plan mapping these Issues to tasks. " +
            "REMEMBER:\n" +
            "- T1 MUST be the Project Foundation & Scaffolding task (High complexity, no dependencies). " +
            "It sets up the solution structure, shared interfaces, base classes, config, and DI registration " +
            "so all other tasks have a clear skeleton to build upon. T1 is in Wave W0 — it runs ALONE.\n" +
            "- T1 must create COMPREHENSIVE placeholders: every model, every interface, every component stub, " +
            "every CSS section marker, sample data files, config files. After T1 merges the app must BUILD and RUN.\n" +
            "- ALL other tasks should depend on T1 at minimum and be in W1 or later.\n" +
            "- Design tasks for PARALLEL execution: each task should own distinct files with NO overlap.\n" +
            "- NEVER assign the same file as CREATE in two different tasks. " +
            "If two tasks need the same file, T1 creates it and declares it SHARED.\n" +
            "- If 'Already-Merged PRs' lists files that already exist, do NOT create tasks for those files. " +
            "Only plan tasks for work that hasn't been done yet.\n" +
            "- Prefer vertical slices (one feature end-to-end) over horizontal layers.\n" +
            "- Maximize tasks that depend ONLY on T1 (star topology, not chains).\n" +
            "- Assign each task a WAVE: W0 for T1 only, W1 for tasks after T1, W2+ for later waves.\n\n" +
            "Output ONLY structured lines in this format:\n" +
            "TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>|<Wave>|<SkillTags>\n\n" +
            "The FilePlan field should contain semicolon-separated file operations:\n" +
            "  CREATE:path/to/file.ext(namespace);MODIFY:path/to/existing.ext;USE:ExistingType(namespace)\n" +
            "  SHARED:path/to/file.ext — declare a file that multiple tasks may modify (use sparingly, T1 only)\n\n" +
            "The Wave field: W0 for T1 only, W1 for tasks parallelizable after T1, W2+ for later waves.\n\n" +
            "The SkillTags field: comma-separated domain tags for skill-based engineer assignment. Examples:\n" +
            "  frontend,react,css — for UI/UX tasks\n" +
            "  backend,api,database — for server-side tasks\n" +
            "  infrastructure,azure,devops — for cloud/infra tasks\n" +
            "  fullstack — for tasks spanning multiple domains\n" +
            "  foundation — for T1 scaffolding\n" +
            "Use specific tags that describe the domain expertise needed.\n\n" +
            "Example:\n" +
            "TASK|T1|42|Project Foundation & Scaffolding|Create solution structure, shared models, interfaces, " +
            "DI registration, and configuration|High|NONE|" +
            "CREATE:.gitignore;CREATE:MyApp.sln;CREATE:MyApp/MyApp.csproj;CREATE:MyApp/Program.cs(MyApp);CREATE:MyApp/Models/AppConfig.cs(MyApp.Models);SHARED:MyApp/Program.cs|W0|foundation\n" +
            "TASK|T2|43|Implement auth module|Build JWT authentication with refresh tokens|Medium|T1|" +
            "CREATE:MyApp/Services/AuthService.cs(MyApp.Services);MODIFY:MyApp/Program.cs;USE:IAuthService(MyApp.Interfaces)|W1|backend,api,security\n" +
            "TASK|T3|44|Implement user profile UI|Build user profile page with React components|Medium|T1|" +
            "CREATE:MyApp/Components/UserProfile.razor(MyApp.Components)|W1|frontend,blazor,css\n\n" +
            "Note: T1 is the ONLY task in W0 — it must complete alone before W1 starts. " +
            "T2 and T3 are both in W1 (parallel-safe) and own completely separate files. " +
            "Program.cs is declared SHARED in T1, so T2 can MODIFY it.\n\n" +
            "Only output TASK lines, nothing else.");

        // Attach design images (PNG/JPG) as ImageContent if available, else plain text.
        AddUserMessageWithDesignImages(history, userPromptBuilder.ToString());

        LogActivity("planning", "🤖 Calling AI to generate engineering plan from user stories");
        UpdateStatus(AgentStatus.Working, "🤖 Generating engineering plan with AI");
        AgentCallContext.CurrentCallContext = "Generating engineering plan from user stories";
        var response = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var structuredText = response.Content ?? "";
        TaskTracker.RecordLlmCall(decompStepId);
        TaskTracker.CompleteStep(decompStepId);

        LogActivity("planning", "📝 AI generated plan, parsing tasks...");

        // Extract and log any DECISION blocks from the plan generation response
        if (_decisionLog is not null)
        {
            var decisions = DecisionBlockParser.ExtractDecisions(structuredText);
            foreach (var d in decisions)
            {
                if (_decisionGate is not null)
                {
                    await _decisionGate.ClassifyAndGateDecisionAsync(
                        Identity.Id, Identity.DisplayName,
                        "EngineeringPlanning", d.Title,
                        $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                        category: d.SourceQuestion is not null ? "WizardQuestion" : "EngineeringDecision",
                        modelTier: Identity.ModelTier, ct: ct);
                }
                else
                {
                    _decisionLog.Log(new AgentDecision
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        AgentId = Identity.Id,
                        AgentDisplayName = Identity.DisplayName,
                        Phase = "EngineeringPlanning",
                        ImpactLevel = d.Impact,
                        Title = d.Title,
                        Rationale = $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                        SourceQuestion = d.SourceQuestion,
                        Status = DecisionStatus.AutoApproved
                    });
                }
            }
            if (decisions.Count > 0)
            {
                Logger.LogInformation("SE logged {Count} decisions from plan generation", decisions.Count);
                structuredText = DecisionBlockParser.StripDecisionBlocks(structuredText);
            }
        }

        // Self-assessment: assess and refine the engineering plan
        var assessStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Self-assessment & impact classification",
            "Assessing engineering plan quality and classifying impact", Identity.ModelTier);
        Core!.ReasoningLog!.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Generating,
            Phase = "Engineering Planning",
            Summary = "Engineering plan generated from enhancement issues",
            Iteration = 0,
        });

        LogActivity("planning", "🔍 Self-assessing engineering plan quality");
        var criteria = AssessmentCriteria.GetForRole(Identity.Role);
        if (criteria is not null)
        {
            var (refinedOutput, assessment) = await Core!.SelfAssessment!.AssessAndRefineWithResultAsync(
                Identity.Id,
                Identity.DisplayName,
                Identity.Role,
                "Engineering Planning",
                structuredText,
                criteria,
                $"Project: {Config.Project.ResolvedDescription ?? Config.Project.Description}\nArchitecture doc available for reference",
                chat,
                classifyImpact: _decisionGate is not null,
                ct);
            structuredText = refinedOutput;
            peAssessmentResult = assessment;
            TaskTracker.RecordLlmCall(assessStepId);
        }
        TaskTracker.CompleteStep(assessStepId);

        var issueMap = enhancementIssues.ToDictionary(i => i.Number);

        foreach (var line in structuredText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TASK|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7)
                continue;

            var deps = parts[6].Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : parts[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            int.TryParse(parts[2].Trim().TrimStart('#'), out var issueNum);

            // Parse optional FilePlan field (8th field) for file/namespace guidance
            var filePlan = parts.Length >= 8 ? parts[7].Trim() : "";

            // Parse optional Wave field (9th field) — default W1 for backward compat
            var wave = parts.Length >= 9 ? parts[8].Trim() : "W1";
            if (string.IsNullOrWhiteSpace(wave)) wave = "W1";

            // Parse optional SkillTags field (10th field) for skill-based assignment
            var skillTags = parts.Length >= 10
                ? parts[9].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.ToLowerInvariant()).ToList()
                : new List<string>();

            // Extract owned files from FilePlan (both CREATE and MODIFY)
            var ownedFiles = ExtractAllFilesFromFilePlan(filePlan);

            // Parse typed dependencies: "T1(files),T3(api)" → {T1:files, T3:api}
            var (plainDeps, depTypes) = ParseTypedDependencies(deps);

            // Validate task name — if AI put a wave identifier (W1, W2, etc.) as the name,
            // fall back to the description (truncated) or the parent issue title
            var taskName = parts[3].Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(taskName, @"^W\d+$"))
            {
                Logger.LogWarning("Task {TaskId} has wave identifier '{Name}' as name — falling back to description",
                    parts[1].Trim(), taskName);
                var desc = parts[4].Trim();
                taskName = desc.Length > 80 ? desc[..80] : desc;
                if (string.IsNullOrWhiteSpace(taskName) && issueNum > 0 && issueMap.TryGetValue(issueNum, out var parentIssue))
                    taskName = parentIssue.Title;
                if (string.IsNullOrWhiteSpace(taskName))
                    taskName = $"Task {parts[1].Trim()}";
            }

            parsedTasks.Add(new EngineeringTask
            {
                Id = parts[1].Trim(),
                Name = taskName,
                Description = parts[4].Trim() + (string.IsNullOrEmpty(filePlan) ? "" :
                    $"\n\n### File Plan\n{FormatFilePlan(filePlan)}"),
                Complexity = NormalizeComplexity(parts[5].Trim()),
                Dependencies = plainDeps,
                DependencyTypes = depTypes,
                ParentIssueNumber = issueNum > 0 ? issueNum : null,
                Wave = wave,
                OwnedFiles = ownedFiles,
                SkillTags = skillTags
            });
        }

        if (parsedTasks.Count == 0)
        {
            Logger.LogWarning("No tasks parsed from AI response, creating a fallback task per issue");
            foreach (var issue in enhancementIssues)
            {
                parsedTasks.Add(new EngineeringTask
                {
                    Id = $"T-{issue.Number}",
                    Name = issue.Title,
                    Description = issue.Body,
                    Complexity = "Medium",
                    ParentIssueNumber = issue.Number
                });
            }
        }

        // Filter out invalid/placeholder tasks the AI may have generated
        var invalidNames = new[] { "REMOVED", "SKIP", "N/A", "MERGED INTO", "DUPLICATE", "ALREADY DONE" };
        var invalidTasks = parsedTasks.Where(t =>
            invalidNames.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(t.Name)).ToList();
        if (invalidTasks.Count > 0)
        {
            var removedIds = invalidTasks.Select(t => t.Id).ToHashSet();
            parsedTasks.RemoveAll(t => removedIds.Contains(t.Id));
            foreach (var task in parsedTasks)
            {
                task.Dependencies.RemoveAll(d => removedIds.Contains(d));
                task.DependencyTypes = task.DependencyTypes
                    .Where(kv => !removedIds.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            }
            Logger.LogInformation("Filtered {Count} invalid/placeholder tasks from AI output: {Names}",
                invalidTasks.Count, string.Join(", ", invalidTasks.Select(t => $"{t.Id}:{t.Name}")));
            LogActivity("warning", $"⚠️ Filtered {invalidTasks.Count} invalid placeholder tasks from plan");
        }

        // Remove any T-FINAL the AI may have generated — we always add our own canonical one below.
        // Must happen early (before overlap detection, wave validation, normalization) so AI-generated
        // T-FINAL doesn't influence dependency/wave calculations.
        var aiGeneratedFinal = parsedTasks.Where(t => string.Equals(t.Id, IntegrationTaskId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (aiGeneratedFinal.Count > 0)
        {
            Logger.LogInformation("Removing {Count} AI-generated T-FINAL task(s) — canonical T-FINAL will be added after normalization", aiGeneratedFinal.Count);
            parsedTasks.RemoveAll(t => string.Equals(t.Id, IntegrationTaskId, StringComparison.OrdinalIgnoreCase));
        }

        // Warn about duplicate task IDs (AI sometimes generates duplicates)
        var duplicateIds = parsedTasks.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
        {
            Logger.LogWarning("AI generated duplicate task IDs: {Duplicates}. Last definition wins in lookups.",
                string.Join(", ", duplicateIds));
        }

        // Enforce foundation-first pattern: ensure T1 is a foundation task
        // and all other tasks depend on it
        EnsureFoundationFirstPattern(parsedTasks, enhancementIssues);

        // Tier 1 — soft-detect empty FilePlan: any non-integration task with no declared
        // CREATE/MODIFY entries is a parallel-safety hole because file-overlap detection
        // can't see a task that claims no files. Log loudly + record an AgentDecision, but
        // do NOT throw — that would crash the agent loop into a retry storm if the AI keeps
        // producing empty FilePlans. The validation-table prompt rule is the primary
        // defense; this is the failsafe alarm so the operator sees it on the Reasoning page.
        var tasksWithoutFiles = parsedTasks
            .Where(t => !string.Equals(t.Id, IntegrationTaskId, StringComparison.OrdinalIgnoreCase)
                        && (t.OwnedFiles is null || t.OwnedFiles.Count == 0))
            .ToList();
        if (tasksWithoutFiles.Count > 0)
        {
            var listing = string.Join(", ", tasksWithoutFiles.Select(t => $"{t.Id} ({t.Name})"));
            Logger.LogWarning(
                "Engineering plan: {Count} task(s) have an empty FilePlan — file-overlap detection cannot guard them against parallel merge conflicts: {Tasks}",
                tasksWithoutFiles.Count, listing);
            LogActivity("planning",
                $"⚠️ {tasksWithoutFiles.Count} task(s) have empty FilePlan — proceeding with reduced parallel-safety: {listing}");
            if (_decisionLog is not null)
            {
                try
                {
                    _decisionLog.Log(new VirtualDevTeam.Core.Agents.Decisions.AgentDecision
                    {
                        Id = $"empty-fileplan-{DateTime.UtcNow:yyyyMMddHHmmss}",
                        AgentId = Identity.Id,
                        AgentDisplayName = Identity.DisplayName,
                        Phase = "EngineeringPlanning",
                        ImpactLevel = VirtualDevTeam.Core.Agents.Decisions.DecisionImpactLevel.M,
                        Title = $"Empty FilePlan on {tasksWithoutFiles.Count} task(s) — parallel-safety reduced",
                        Rationale = $"AI plan emitted task(s) without CREATE/MODIFY file declarations: {listing}. " +
                                    "These tasks bypass file-overlap detection. Engineers may hit merge conflicts " +
                                    "if their actual implementations touch shared files.",
                        Status = VirtualDevTeam.Core.Agents.Decisions.DecisionStatus.AutoApproved,
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to log empty-FilePlan decision (non-fatal)");
                }
            }
        }

        // PE Parallelism: Validate and repair file overlaps using AI-assisted fixing
        // MUST run BEFORE SerializeOverlappingTasks so serialization sees the final
        // repaired file ownership, not the pre-repair state (2026-05-16 fix).
        await ValidateAndRepairTaskPlanAsync(parsedTasks, chat, ct);

        // Serialize tasks that touch the same files to prevent merge conflicts
        // Runs AFTER repair so it catches any files added during AI-assisted repair.
        SerializeOverlappingTasks(parsedTasks);

        // PE Parallelism: Validate wave assignments and log metrics
        ValidateWaves(parsedTasks);
        var finalSharedFiles = ExtractSharedFilesFromFilePlan(
            ExtractRawFilePlanFromDescription(parsedTasks.FirstOrDefault()?.Description ?? ""));
        var finalOverlaps = DetectFileOverlaps(parsedTasks, finalSharedFiles);
        LogParallelismMetrics(parsedTasks, finalOverlaps);

        // Tier 1 — DecisionGate on residual overlap: if AI repair failed and overlaps still
        // exist, log a structured decision and continue with a loud warning. We don't throw
        // because in some cases (truly additive shared files like .gitignore lists) the
        // overlap is benign; the decision log preserves the provenance for the operator
        // to review on the Reasoning page.
        if (finalOverlaps.Count > 0 && _decisionLog is not null)
        {
            try
            {
                var overlapDetail = string.Join("; ",
                    finalOverlaps.Select(kv => $"{kv.Key} → [{string.Join(",", kv.Value)}]"));
                _decisionLog.Log(new VirtualDevTeam.Core.Agents.Decisions.AgentDecision
                {
                    Id = $"file-overlap-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    AgentId = Identity.Id,
                    AgentDisplayName = Identity.DisplayName,
                    Phase = "EngineeringPlanning",
                    ImpactLevel = VirtualDevTeam.Core.Agents.Decisions.DecisionImpactLevel.M,
                    Title = $"File overlaps remain after AI repair ({finalOverlaps.Count} file(s))",
                    Rationale = $"AI repair didn't fully resolve overlapping FilePlan claims. " +
                                $"Proceeding with warning — engineers may hit merge conflicts. " +
                                $"Detail: {overlapDetail}",
                    Status = VirtualDevTeam.Core.Agents.Decisions.DecisionStatus.AutoApproved,
                });
                LogActivity("planning",
                    $"⚠️ {finalOverlaps.Count} file overlap(s) remain after AI repair — logged decision, proceeding");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log file-overlap decision (non-fatal)");
            }
        }

        // Post-parse normalization: merge undersized tasks and enforce task count cap.
        // This runs BEFORE issue creation so merged tasks never produce orphan issues.
        NormalizeTaskPlan(parsedTasks, targetTaskCount);

        } // end else (non-SinglePRMode AI decomposition block)

        // Add a final integration & validation task that depends on ALL other tasks.
        // The PE leader will self-assign this after all other tasks are done.
        var allTaskIds = parsedTasks.Select(t => t.Id).ToList();
        // T-FINAL needs a ParentIssueNumber so the enhancement scope filter in LoadTasksAsync
        // doesn't exclude it — without this, dependency links to T1 won't be created on GitHub.
        var integrationParentIssue = enhancementIssues.FirstOrDefault()?.Number;
        parsedTasks.Add(new EngineeringTask
        {
            Id = IntegrationTaskId,
            Name = IntegrationTaskName,
            Description = "Final integration and validation step performed by the Software Engineer.\n\n" +
                "After all engineering tasks have been completed and merged:\n" +
                "1. Review the full codebase against the Architecture and PM Spec for integration gaps\n" +
                "2. Check for broken cross-module references, missing DI wiring, missing route registration\n" +
                "3. Run `dotnet build` (or equivalent) and fix ALL build errors\n" +
                "4. Run `dotnet test` and fix any test failures caused by integration issues\n" +
                "5. Verify the app starts successfully (`dotnet run` must not crash on startup)\n" +
                "6. Create a PR with integration fixes — you MUST commit at minimum a build-verification result\n\n" +
                "IMPORTANT: Do NOT close this issue without creating a PR. Even if no code changes are needed, " +
                "commit a verification comment confirming the build passes and the app starts. " +
                "This task is automatically assigned to the PE leader when all other tasks are complete.",
            Complexity = "High",
            Dependencies = allTaskIds,
            ParentIssueNumber = integrationParentIssue
        });

        // Classify task decomposition decision impact
        var decisionStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Decision gate",
            "Classifying task decomposition impact for approval", Identity.ModelTier);
        TaskTracker.SetStepWaiting(decisionStepId);
        if (_decisionGate is not null)
        {
            var decisionTaskSummary = string.Join(", ", parsedTasks.Where(t => t.Id != IntegrationTaskId)
                .Select(t => $"{t.Id}:{t.Name}({t.Complexity},{t.Wave})"));
            var waveDistribution = parsedTasks.Where(t => t.Id != IntegrationTaskId)
                .GroupBy(t => t.Wave).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}: {g.Count()} tasks");

            AgentDecision planDecision;
            if (peAssessmentResult?.HasImpactClassification == true)
            {
                planDecision = await _decisionGate.ClassifyFromAssessmentAsync(
                    agentId: Identity.Id,
                    agentDisplayName: Identity.DisplayName,
                    phase: "Engineering Planning",
                    title: "Engineering task decomposition and wave scheduling",
                    context: $"Decomposed {enhancementIssues.Count} enhancement issues into {parsedTasks.Count - 1} engineering tasks + integration task. " +
                             $"Wave distribution: {string.Join(", ", waveDistribution)}. " +
                             $"Tasks: {decisionTaskSummary}",
                    assessment: peAssessmentResult,
                    category: "TaskPlanning",
                    modelTier: Identity.ModelTier,
                    ct: ct);
            }
            else
            {
                planDecision = await _decisionGate.ClassifyAndGateDecisionAsync(
                    agentId: Identity.Id,
                    agentDisplayName: Identity.DisplayName,
                    phase: "Engineering Planning",
                    title: "Engineering task decomposition and wave scheduling",
                    context: $"Decomposed {enhancementIssues.Count} enhancement issues into {parsedTasks.Count - 1} engineering tasks + integration task. " +
                             $"Wave distribution: {string.Join(", ", waveDistribution)}. " +
                             $"Tasks: {decisionTaskSummary}",
                    category: "TaskPlanning",
                    modelTier: Identity.ModelTier,
                    ct: ct);
            }

            if (planDecision.Status == DecisionStatus.Pending)
            {
                Logger.LogInformation("Engineering plan decision gated — waiting for human approval");
                planDecision = await _decisionGate.WaitForDecisionAsync(planDecision.Id, ct);
            }

            if (planDecision.Status == DecisionStatus.Rejected)
            {
                Logger.LogWarning("Engineering plan REJECTED: {Feedback}", planDecision.HumanFeedback);
                await RememberAsync(MemoryType.Decision,
                    "Engineering plan rejected",
                    planDecision.HumanFeedback ?? "No feedback provided", ct);
                UpdateStatus(AgentStatus.Idle, "Engineering plan rejected — awaiting new direction");
                TaskTracker.FailStep(decisionStepId, "Plan rejected by human");
                return;
            }
        }
        TaskTracker.CompleteStep(decisionStepId);

        // Informational check: log overlap with already-merged PRs from this run.
        // We do NOT auto-drop tasks — overlap is common (shared files, scaffolding) and
        // does not mean the task is complete. The AI planner was already told about merged files.
        try
        {
            var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                .Where(IsCurrentRunScopePr)
                .ToList();
            if (mergedPRs.Count > 0)
            {
                var allMergedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mpr in mergedPRs.Take(10))
                {
                    var prFiles = await PrService.GetChangedFilesAsync(mpr.Number, ct);
                    foreach (var f in prFiles)
                        allMergedFiles.Add(f.ToLowerInvariant().Replace('\\', '/'));
                }

                foreach (var task in parsedTasks)
                {
                    if (task.Id == IntegrationTaskId || task.OwnedFiles.Count == 0)
                        continue;

                    var normalizedFiles = task.OwnedFiles
                        .Select(f => f.ToLowerInvariant().Replace('\\', '/'))
                        .ToList();
                    var overlap = normalizedFiles.Count(f => allMergedFiles.Contains(f));

                    if (overlap > 0)
                    {
                        Logger.LogInformation(
                            "Task {TaskId} has {Overlap}/{Total} files overlapping with merged PRs — task retained, overlap is expected for shared files",
                            task.Id, overlap, normalizedFiles.Count);
                    }
                }
            }
        }

        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Merged PR overlap check failed — proceeding with full plan");
        }

        // Recompute waves from dependency graph to eliminate gaps after task removal.
        // In serial mode, enforce a strict linear chain (one PR at a time).
        if (Config.Limits.IsSerialMultiPr)
        {
            ApplySerialDependencyChain(parsedTasks);
        }
        else
        {
            // Wave = max(dependency waves) + 1, with foundation (T1) always at W0.
            RecomputeWavesFromDependencies(parsedTasks);
        }

        // Create GitHub issues for each task (the single source of truth)
        UpdateStatus(AgentStatus.Working, $"📋 Creating {parsedTasks.Count} task issues from plan");
        LogActivity("planning", $"📌 Creating {parsedTasks.Count} task issues on GitHub");
        var createIssuesStepId = TaskTracker.BeginStep(Identity.Id, "pe-planning", "Create GitHub issues",
            $"Creating {parsedTasks.Count} engineering task issues on GitHub", Identity.ModelTier);
        var createdTasks = await _taskManager.CreateTaskIssuesAsync(parsedTasks, ct);

        // Change #1 — Stamp "## Implements Scenarios" into each engineering-task issue.
        // Uses a cheap per-task LLM micro-call to map task description → scenario IDs.
        // Gracefully skipped if no scenario registry is wired or no scenarios are loaded.
        await StampScenarioTagsAsync(createdTasks, ct);

        // Register display names so dashboard shows "#{issue}: {name}" instead of "T1", "T2", etc.
        RegisterTaskDisplayNames(createdTasks);

        // Track the integration issue number for later self-assignment
        var integrationTask = createdTasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
        if (integrationTask?.IssueNumber is not null)
            _integrationIssueNumber = integrationTask.IssueNumber;

        // Now resolve dependency task IDs (T1, T2) to actual issue numbers
        // Build a map from task ID → issue number (use last-wins for safety against AI duplicates)
        var taskIdToIssue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in createdTasks)
            taskIdToIssue[t.Id] = t.IssueNumber ?? 0;
        foreach (var task in createdTasks)
        {
            if (task.Dependencies.Count == 0 || !task.IssueNumber.HasValue)
                continue;

            var depIssueNumbers = task.Dependencies
                .Where(d => taskIdToIssue.ContainsKey(d) && taskIdToIssue[d] > 0)
                .Select(d => taskIdToIssue[d])
                .ToList();

            if (depIssueNumbers.Count > 0)
            {
                // Update the issue body with dependency issue numbers
                var updatedBody = EngineeringTaskIssueManager.BuildIssueBodyWithDeps(task, depIssueNumbers);
                await WorkItemService.UpdateAsync(task.IssueNumber.Value, body: updatedBody, ct: ct);
            }
        }

        // Reload to pick up dependency info from updated issue bodies, then create GitHub links
        await _taskManager.LoadTasksAsync(ct);

        LogActivity("planning", "🔗 Establishing task dependencies");
        // Create native GitHub blocked-by dependency links between tasks
        await _taskManager.LinkTaskDependenciesAsync(_taskManager.Tasks.ToList(), ct);
        TaskTracker.CompleteStep(createIssuesStepId);

        // REQ-PE-009: Validate all PM enhancements have engineering tasks
        // Skip in SinglePR — T1 monolithic task covers ALL enhancements by design
        if (Config.Limits.IsSinglePr)
        {
            Logger.LogInformation(
                "SinglePRMode — skipping enhancement coverage validation (T1 covers all {Count} enhancements)",
                enhancementIssues.Count);
        }
        else
        {
            await ValidateEnhancementCoverageAsync(enhancementIssues.ToAgentIssues(), ct);
        }

        // Validate engineering plan structure: wave dependencies, issue links, design references
        await ValidateEngineeringPlanStructureAsync(ct);

        // === Gate: EngineeringPlan — human reviews plan before finalization ===
        // Skip gate on resume if tasks are already loaded (plan was already approved)
        if (_taskManager.TotalCount > 0 && _taskManager.Tasks.Any(t => t.AssignedTo is not null))
        {
            Logger.LogInformation("Tasks already assigned, skipping EngineeringPlan gate (resume scenario)");
        }
        else
        {
            await WaitForHumanGateAsync(
                GateIds.EngineeringPlan,
                "Engineering plan ready for human review before finalization",
                ct: ct);
        }

        Logger.LogInformation("Engineering plan created with {Count} tasks from {IssueCount} issues",
            _taskManager.TotalCount, enhancementIssues.Count);
        LogActivity("task", $"📋 Engineering plan created: {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues");
        Core!.FlowTimeline?.RecordEvent("se.plan.created", $"Engineering Plan Created ({_taskManager.TotalCount} tasks)",
            agentId: Identity.Id, phase: "Engineering", category: VirtualDevTeam.Core.HealthMonitor.MilestoneCategory.Work);

        Core!.ReasoningLog!.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Planning,
            Phase = "Engineering Planning",
            Summary = $"Created engineering plan: {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues",
            Detail = $"Tasks: {string.Join(", ", _taskManager.Tasks.Select(t => $"{t.Name} ({t.Complexity})"))}"
        });

        var taskSummary = string.Join(", ", _taskManager.Tasks.Select(t => $"{t.Id}:{t.Name}({t.Complexity})"));
        await RememberAsync(MemoryType.Decision,
            $"Created engineering plan with {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues",
            $"Tasks: {TruncateForMemory(taskSummary)}", ct);

        await PublishStatusAsync("EngineeringPlanReady", AgentStatus.Working,
            details: $"Engineering plan created with {_taskManager.TotalCount} tasks. " +
                      "Ready to assign work to engineers.",
            currentTask: "Engineering Planning", ct: ct);

        UpdateStatus(AgentStatus.Working, "✅ Engineering plan created and tasks assigned");
        _planningComplete = true;
    }

    /// <summary>
    /// REQ-PE-009: After creating the engineering plan, validate that every PM enhancement
    /// issue has at least one linked engineering task. For missed enhancements, either create
    /// additional tasks or post a justification comment explaining how it's covered.
    /// </summary>
    private async Task ValidateEnhancementCoverageAsync(
        IReadOnlyList<AgentIssue> enhancementIssues, CancellationToken ct)
    {
        try
        {
            // Defense-in-depth: never create extra tasks in SinglePR mode
            if (Config.Limits.IsSinglePr)
            {
                Logger.LogInformation(
                    "ValidateEnhancementCoverageAsync skipped — SinglePRMode (T1 covers all enhancements)");
                return;
            }

            // Build set of parent issue numbers that have engineering tasks
            // Include both ParentIssueNumber (single parent) and RelatedEnhancementNumbers (multi-parent)
            var coveredParents = new HashSet<int>();
            foreach (var t in _taskManager.Tasks)
            {
                if (t.ParentIssueNumber.HasValue)
                    coveredParents.Add(t.ParentIssueNumber.Value);
                foreach (var related in t.RelatedEnhancementNumbers)
                    coveredParents.Add(related);
            }

            var uncoveredEnhancements = enhancementIssues
                .Where(e => !coveredParents.Contains(e.Number))
                .ToList();

            if (uncoveredEnhancements.Count == 0)
            {
                Logger.LogInformation("Enhancement coverage validation passed: all {Count} enhancements have engineering tasks",
                    enhancementIssues.Count);
                return;
            }

            Logger.LogWarning(
                "Enhancement coverage gap: {UncoveredCount}/{TotalCount} enhancements have no engineering tasks: {Issues}",
                uncoveredEnhancements.Count, enhancementIssues.Count,
                string.Join(", ", uncoveredEnhancements.Select(e => $"#{e.Number}")));

            // Ask AI to determine if each uncovered enhancement is covered by existing tasks or was missed
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Include file ownership so the AI can detect file-level overlap
            var existingTasksSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
            {
                var files = t.OwnedFiles.Count > 0
                    ? $" | Files: [{string.Join(", ", t.OwnedFiles)}]"
                    : "";
                return $"- {t.Id}: {t.Name} (Parent: #{t.ParentIssueNumber}){files} — {t.Description?.Split('\n').FirstOrDefault()}";
            }));

            // Also include files from merged PRs so coverage validation knows what's already built
            var mergedFileContext = "";
            try
            {
                var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                    .Where(IsCurrentRunScopePr)
                    .ToList();
                if (mergedPRs.Count > 0)
                {
                    var mergedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mpr in mergedPRs.Take(10))
                    {
                        var prFiles = await PrService.GetChangedFilesAsync(mpr.Number, ct);
                        foreach (var f in prFiles) mergedFiles.Add(f);
                    }
                    if (mergedFiles.Count > 0)
                        mergedFileContext = $"\n\n## Already Merged Files (already on main)\n{string.Join("\n", mergedFiles.OrderBy(f => f).Select(f => $"- {f}"))}\n\nIf the enhancement's requirements are satisfied by these already-merged files, respond with COVERED.";
                }
            }
            catch { /* non-critical */ }

            foreach (var enhancement in uncoveredEnhancements)
            {
                var history = CreateChatHistory();
                var enhSys = PromptService is not null
                    ? await PromptService.RenderAsync("software-engineer/enhancement-coverage-system",
                        new Dictionary<string, string>(), ct)
                    : null;
                history.AddSystemMessage(enhSys ??
                    "You are a Software Engineer validating engineering plan coverage. " +
                    "An enhancement (user story) has no dedicated engineering task. " +
                    "Determine if this enhancement is COVERED by existing tasks or was MISSED.\n\n" +
                    "IMPORTANT: Pay close attention to the Files listed for each task. " +
                    "If an existing task creates the same files that this enhancement needs " +
                    "(e.g., solution scaffolding, data models, services), it IS covered even if the task " +
                    "has a different parent issue number.\n\n" +
                    "If COVERED: respond with COVERED followed by which specific tasks address it and how.\n" +
                    "If MISSED: respond with MISSED followed by what engineering task should be created. " +
                    "The new task MUST NOT create files that are already owned by existing tasks.");

                var enhUser = PromptService is not null
                    ? await PromptService.RenderAsync("software-engineer/enhancement-coverage-user",
                        new Dictionary<string, string>
                        {
                            ["enhancement_number"] = enhancement.Number.ToString(),
                            ["enhancement_title"] = enhancement.Title,
                            ["enhancement_body"] = enhancement.Body ?? "",
                            ["existing_tasks_summary"] = existingTasksSummary
                        }, ct)
                    : null;
                history.AddUserMessage(enhUser ??
                    $"## Uncovered Enhancement #{enhancement.Number}: {enhancement.Title}\n{enhancement.Body}\n\n" +
                    $"## Existing Engineering Tasks\n{existingTasksSummary}{mergedFileContext}\n\n" +
                    "Is this enhancement covered by the existing tasks or already-merged files, or was it missed?");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var responseText = response.Content ?? "";

                if (responseText.Contains("COVERED", StringComparison.OrdinalIgnoreCase))
                {
                    // Post justification comment on the enhancement issue
                    var justification = responseText
                        .Replace("COVERED", "").Replace("covered", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    await WorkItemService.AddCommentAsync(enhancement.Number,
                        $"📋 **Software Engineer — Coverage Analysis**\n\n" +
                        $"This user story does not have a dedicated engineering task, but its requirements are " +
                        $"addressed by existing tasks in the engineering plan:\n\n{justification}",
                        ct);

                    Logger.LogInformation(
                        "Enhancement #{Number} covered by existing tasks — justification posted",
                        enhancement.Number);
                }
                else
                {
                    // Enhancement was missed — create an additional task
                    var taskDescription = responseText
                        .Replace("MISSED", "").Replace("missed", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    // Assign to the highest existing wave so auto-created tasks don't
                    // bypass wave ordering (they'd default to null = always eligible)
                    var maxWave = _taskManager.Tasks
                        .Where(t => !string.IsNullOrEmpty(t.Wave) &&
                                    !string.Equals(t.Id, "T-FINAL", StringComparison.OrdinalIgnoreCase))
                        .Select(t => t.Wave!)
                        .OrderByDescending(w => w, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault() ?? "W1";

                    var newTaskId = _taskManager.NextAvailableTaskId();
                    var newTask = new EngineeringTask
                    {
                        Id = newTaskId,
                        Name = $"Implement {enhancement.Title}",
                        Description = $"Auto-created from uncovered enhancement #{enhancement.Number}.\n\n{taskDescription}",
                        Complexity = "Medium",
                        Wave = maxWave,
                        ParentIssueNumber = enhancement.Number,
                        Dependencies = _taskManager.Tasks.Any(t => t.Id == "T1")
                            ? new List<string> { "T1" }
                            : new List<string>()
                    };

                    var created = await _taskManager.CreateTaskIssuesAsync(new[] { newTask }, ct);
                    if (created.Count > 0)
                    {
                        Logger.LogInformation(
                            "Created additional task {TaskId} (Issue #{IssueNumber}) for missed enhancement #{EnhancementNumber}",
                            newTaskId, created[0].IssueNumber, enhancement.Number);
                        LogActivity("task", $"📋 Created task {newTaskId} for missed enhancement #{enhancement.Number}: {enhancement.Title}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Enhancement coverage validation failed — continuing without validation");
        }
    }

    /// <summary>
    /// Validate the engineering plan structure: wave dependencies are correct,
    /// all tasks have GitHub issues, blocking links match expected wave order,
    /// and UI tasks reference the design file.
    /// </summary>
    private async Task ValidateEngineeringPlanStructureAsync(CancellationToken ct)
    {
        try
        {
            var tasks = _taskManager.Tasks.ToList();
            if (tasks.Count == 0) return;

            var issues = new List<string>();
            var warnings = new List<string>();

            // 1. Verify all tasks have GitHub issues created
            var missingIssues = tasks.Where(t => !t.IssueNumber.HasValue).ToList();
            if (missingIssues.Count > 0)
            {
                issues.Add($"❌ {missingIssues.Count} tasks have no GitHub issue: " +
                    string.Join(", ", missingIssues.Select(t => t.Id)));
            }

            // 2. Verify wave structure: W2+ tasks should be blocked by at least one earlier wave task
            var tasksByWave = new Dictionary<string, List<EngineeringTask>>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in tasks)
            {
                var wave = task.Wave ?? "W1";
                if (!tasksByWave.ContainsKey(wave))
                    tasksByWave[wave] = new List<EngineeringTask>();
                tasksByWave[wave].Add(task);
            }

            var sortedWaves = tasksByWave.Keys.OrderBy(w => w).ToList();
            if (sortedWaves.Count > 1)
            {
                for (var i = 1; i < sortedWaves.Count; i++)
                {
                    var wave = sortedWaves[i];
                    var prevWaves = sortedWaves.Take(i).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var prevTaskIds = tasks
                        .Where(t => prevWaves.Contains(t.Wave ?? "W1"))
                        .Select(t => t.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var task in tasksByWave[wave])
                    {
                        var hasDepOnPrevWave = task.Dependencies.Any(d => prevTaskIds.Contains(d));
                        if (!hasDepOnPrevWave && task.DependencyIssueNumbers.Count == 0)
                        {
                            issues.Add($"❌ {task.Id} ({task.Name}) is in {wave} but has NO dependency on any {string.Join("/", prevWaves)} task — should be blocked");
                        }
                    }
                }
            }

            // 3. Verify GitHub dependency links are resolved for tasks with declared dependencies
            foreach (var task in tasks.Where(t => t.Dependencies.Count > 0 && t.IssueNumber.HasValue))
            {
                if (task.DependencyIssueNumbers.Count == 0)
                {
                    warnings.Add($"⚠️ {task.Id} declares dependencies [{string.Join(", ", task.Dependencies)}] but has no resolved issue numbers");
                }
            }

            // 4. Check UI tasks reference design file
            // Only match on task NAME (not description — descriptions contain project-level
            // context like "dashboard" that would false-positive pure backend tasks).
            // Keywords are narrowed to UI-specific terms, not general project terms.
            var uiKeywords = new[] { "ui", "layout", "css", "component", "razor",
                "header", "heatmap", "display", "svg", "frontend", "react", "blazor" };
            foreach (var task in tasks)
            {
                var taskNameLower = task.Name.ToLowerInvariant();
                if (uiKeywords.Any(k => taskNameLower.Contains(k)))
                {
                    var hasDesignRef = $"{task.Name} {task.Description}".ToLowerInvariant().Contains("design");
                    if (!hasDesignRef)
                    {
                        warnings.Add($"⚠️ {task.Id} ({task.Name}) appears to be a UI task but doesn't reference design context");
                    }
                }
            }

            // Log and report results
            if (issues.Count == 0 && warnings.Count == 0)
            {
                Logger.LogInformation(
                    "✅ Engineering plan validation passed: {TaskCount} tasks, {WaveCount} waves, all dependencies correct",
                    tasks.Count, tasksByWave.Count);
                LogActivity("planning", $"✅ Plan validation passed: {tasks.Count} tasks, {tasksByWave.Count} waves");
            }
            else
            {
                if (issues.Count > 0)
                {
                    Logger.LogWarning(
                        "Engineering plan structure has {IssueCount} issues:\n{Issues}",
                        issues.Count, string.Join("\n", issues));
                    LogActivity("planning", $"⚠️ Plan validation: {issues.Count} issues, {warnings.Count} warnings");

                    // Attempt repair: re-link dependencies for tasks missing links
                    Logger.LogInformation("Attempting to repair engineering plan dependency links...");
                    await _taskManager.LinkTaskDependenciesAsync(tasks, ct);
                    Logger.LogInformation("Dependency link repair complete");
                }

                if (warnings.Count > 0)
                {
                    Logger.LogInformation(
                        "Engineering plan warnings ({Count}):\n{Warnings}",
                        warnings.Count, string.Join("\n", warnings));
                }

                // Post validation report on the first task issue for visibility
                var report = new StringBuilder();
                report.AppendLine("## 📋 Engineering Plan Validation Report\n");
                if (issues.Count > 0)
                {
                    report.AppendLine("### Structural Issues (auto-repaired where possible)");
                    foreach (var issue in issues)
                        report.AppendLine($"- {issue}");
                    report.AppendLine();
                }
                if (warnings.Count > 0)
                {
                    report.AppendLine("### Warnings");
                    foreach (var warning in warnings)
                        report.AppendLine($"- {warning}");
                }

                var firstTaskIssue = tasks.FirstOrDefault(t => t.IssueNumber.HasValue);
                if (firstTaskIssue?.IssueNumber is not null)
                {
                    await WorkItemService.AddCommentAsync(firstTaskIssue.IssueNumber.Value,
                        report.ToString(), ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Engineering plan structure validation failed — continuing without validation");
        }
    }

    /// <summary>
    /// Non-leader PEs sync task state from GitHub issues instead of creating the plan.
    /// They load existing engineering-task issues created by the leader.
    /// </summary>
    private async Task SyncEngineeringPlanFromGitHubAsync(CancellationToken ct)
    {
        try
        {
            // Set enhancement scope to filter out stale tasks from prior runs
            var enhancements = await WorkItemService.ListByLabelAsync(
                IssueWorkflow.Labels.Enhancement, ct: ct);
            if (enhancements.Count > 0)
                _taskManager.SetEnhancementScope(enhancements.Select(i => i.Number));

            await _taskManager.LoadTasksAsync(ct);
            if (_taskManager.TotalCount > 0)
            {
                Logger.LogInformation(
                    "Non-leader PE synced {Count} tasks from GitHub ({Done} done, {Pending} pending)",
                    _taskManager.TotalCount, _taskManager.DoneCount, _taskManager.PendingCount);
                UpdateStatus(AgentStatus.Idle,
                    $"Synced {_taskManager.TotalCount} tasks, entering development loop");
            }
            else
            {
                Logger.LogDebug("Non-leader PE: no engineering-task issues found yet, waiting for leader");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Non-leader PE failed to sync engineering plan from GitHub");
        }
    }

    #endregion

    #region Phase 2 — Continuous Development Loop

    /// <summary>
    /// Detect tasks with status:assigned/in-progress that have no open PR → reset to pending.
    /// This handles cases where a PR was closed without merging (e.g., rebase wiped changes,
    /// merge conflict close-and-recreate failed halfway, or app restarted mid-operation).
    /// </summary>
    private async Task RecoverOrphanedAssignmentsAsync(CancellationToken ct)
    {
        try
        {
            var assignedTasks = _taskManager.Tasks
                .Where(t => t.Status is "Assigned" or "InProgress"
                         && t.IssueNumber.HasValue
                         && !EngineeringTaskIssueManager.IsTaskDone(t))
                .ToList();

            if (assignedTasks.Count == 0)
                return;

            // Skip tasks that are currently tracked in _agentAssignments — these were
            // recently assigned by us and the engineer may not have created a PR yet.
            var trackedIssueNums = new HashSet<int>(_agentAssignments.Values);

            // Get all open PRs once to check against
            var openPRs = (await GetCachedOpenPRsAsync(ct))
                .Where(IsCurrentRunScopePr)
                .ToList();
            var openPrIssueRefs = new HashSet<int>();
            foreach (var pr in openPRs)
            {
                // Extract issue number from PR body "Closes #NNN"
                var closesMatch = System.Text.RegularExpressions.Regex.Match(
                    pr.Body ?? "", @"Closes\s+#(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (closesMatch.Success && int.TryParse(closesMatch.Groups[1].Value, out var issueNum))
                    openPrIssueRefs.Add(issueNum);
            }

            foreach (var task in assignedTasks)
            {
                // Skip tasks we've already assigned to an engineer this session
                if (trackedIssueNums.Contains(task.IssueNumber!.Value))
                    continue;

                // Skip tasks assigned to us (leader) IF we're actively tracking a PR for them.
                // This prevents the pathological cycle where orphan recovery resets a task the
                // leader is actively reworking/force-approving.
                // BUT: if CurrentPrNumber is null (e.g., after restart with no PR created),
                // let the orphan check proceed — the task truly is orphaned.
                if (string.Equals(task.AssignedTo, Identity.DisplayName, StringComparison.OrdinalIgnoreCase)
                    && CurrentPrNumber is not null)
                    continue;

                // Check if there's an open PR that references this task's issue.
                // Use both the regex match (fast, for bulk) and the canonical parser (thorough).
                if (openPrIssueRefs.Contains(task.IssueNumber!.Value))
                {
                    // Restore to _agentAssignments so PE tracks this assignment
                    if (task.AssignedTo is not null)
                    {
                        var matchingAgent = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                            .Where(a => a.Identity.Id != Identity.Id) // exclude leader
                            .FirstOrDefault(a => string.Equals(a.Identity.DisplayName, task.AssignedTo, StringComparison.OrdinalIgnoreCase));
                        if (matchingAgent is not null && !_agentAssignments.ContainsKey(matchingAgent.Identity.Id))
                        {
                            _agentAssignments[matchingAgent.Identity.Id] = task.IssueNumber!.Value;
                            Logger.LogInformation("Restored assignment tracking: {Engineer} → issue #{IssueNumber}",
                                task.AssignedTo, task.IssueNumber);
                        }
                    }
                    continue;
                }

                // Also check if there's an open PR with the assigned engineer's name in the title
                var hasMatchingPr = openPRs.Any(pr =>
                    task.AssignedTo is not null
                    && pr.Title.Contains(task.AssignedTo, StringComparison.OrdinalIgnoreCase));

                if (hasMatchingPr)
                    continue;

                // Check if any open PR works on the same task but under a different engineer
                // (handles reassignment: issue says "SoftwareEngineer 1: Foo" but PR is "SoftwareEngineer 3: Foo").
                // Extract task name without agent prefix and do exact match against PR titles.
                var taskTitle = ExtractTaskTitle(task.Name);
                if (taskTitle.Length > 5)
                {
                    var reassignedPr = openPRs.FirstOrDefault(pr =>
                        string.Equals(ExtractTaskTitle(pr.Title), taskTitle, StringComparison.OrdinalIgnoreCase));
                    if (reassignedPr is not null)
                    {
                        // Restore assignment tracking to the actual PR owner
                        var prOwner = ExtractAgentPrefix(reassignedPr.Title);
                        if (prOwner is not null)
                        {
                            var ownerAgent = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                                .Where(a => a.Identity.Id != Identity.Id)
                                .FirstOrDefault(a => string.Equals(a.Identity.DisplayName, prOwner, StringComparison.OrdinalIgnoreCase));
                            if (ownerAgent is not null && !_agentAssignments.ContainsKey(ownerAgent.Identity.Id))
                            {
                                _agentAssignments[ownerAgent.Identity.Id] = task.IssueNumber!.Value;
                                Logger.LogInformation(
                                    "Task #{IssueNumber} reassigned: issue says {OriginalOwner} but open PR belongs to {ActualOwner} — restoring tracking",
                                    task.IssueNumber, task.AssignedTo, prOwner);
                            }
                        }
                        continue;
                    }
                }

                // Thorough fallback: use canonical PR-body parser which handles Closes/Fixes/Resolves
                var hasLinkedPr = openPRs.Any(pr =>
                    PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body) == task.IssueNumber!.Value);

                if (hasLinkedPr)
                    continue;

                // No open PR found — this assignment is orphaned, reset to pending
                Logger.LogWarning(
                    "Task #{IssueNumber} ({TaskName}) is {Status} but has no open PR — resetting to Pending",
                    task.IssueNumber, task.Name, task.Status);

                await _taskManager.ResetToPendingAsync(task.IssueNumber!.Value, ct);

                // Clear from our assignment tracking if present
                var orphanedAgent = _agentAssignments
                    .FirstOrDefault(kvp => kvp.Value == task.IssueNumber!.Value);
                if (orphanedAgent.Key is not null)
                    _agentAssignments.Remove(orphanedAgent.Key);

                // Release claim so other agents can pick it up
                ClaimRegistry?.Release(task.IssueNumber!.Value);

                LogActivity("recovery", $"🔄 Reset orphaned task #{task.IssueNumber} ({task.Name}) to Pending — no open PR found");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check for orphaned task assignments");
        }
    }

    private async Task AssignTasksToAvailableEngineersAsync(CancellationToken ct)
    {
        try
        {
            var registeredEngineers = new List<EngineerInfo>();

            // Include non-leader SEs (including specialist engineers) as assignable workers
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SoftwareEngineer))
            {
                if (agent.Identity.Id == Identity.Id) continue; // Skip self (leader)
                registeredEngineers.Add(new EngineerInfo
                {
                    AgentId = agent.Identity.Id,
                    Name = agent.Identity.DisplayName,
                    Role = AgentRole.SoftwareEngineer,
                    Capabilities = agent.Identity.Capabilities
                });
            }

            // === Gate: TaskAssignment — human reviews task assignments ===
            // Only fire this gate once per lifetime; skip on resume if agents already have assignments
            if (_taskAssignmentGateCleared || _agentAssignments.Count > 0)
            {
                if (!_taskAssignmentGateCleared)
                    Logger.LogInformation("Agents already have assignments, skipping TaskAssignment gate (resume scenario)");
                _taskAssignmentGateCleared = true;
            }
            else
            {
                await WaitForHumanGateAsync(
                    GateIds.TaskAssignment,
                    $"Ready to assign {_taskManager.PendingCount} engineering tasks to available engineers",
                    ct: ct);
                _taskAssignmentGateCleared = true;
            }

            // Build list of free engineers (not currently assigned)
            var freeEngineers = new List<EngineerInfo>();
            foreach (var engineer in registeredEngineers)
            {
                if (_agentAssignments.ContainsKey(engineer.AgentId))
                {
                    var assignedIssueNum = _agentAssignments[engineer.AgentId];
                    var assignedTask = _taskManager.FindByIssueNumber(assignedIssueNum);
                    if (assignedTask is not null && !EngineeringTaskIssueManager.IsTaskPastImplementation(assignedTask))
                        continue;
                    _agentAssignments.Remove(engineer.AgentId);
                }
                freeEngineers.Add(engineer);
            }

            // Get all assignable tasks (pending with dependencies met, wave-eligible, excluding integration and foundation)
            // Foundation tasks (T1/W0) are excluded — the SE Lead handles them directly.
            var assignableTasks = _taskManager.Tasks
                .Where(t => t.Status == "Pending" && _taskManager.IsWaveEligible(t) && _taskManager.AreDependenciesMet(t) && !IsIntegrationTask(t) && t.IssueNumber.HasValue && !IsFoundationTask(t))
                .ToList();

            // Dependency-merged guard: verify that dependency PRs are actually merged to main,
            // not just marked "Done" in-memory. Prevents assigning tasks whose dependencies
            // haven't landed yet (e.g., PR approved but merge pending).
            if (assignableTasks.Count > 0)
            {
                var mergedPrNumbers = (await GetCachedMergedPRsAsync(ct))
                    .Where(IsCurrentRunScopePr)
                    .Select(p => p.Number).ToHashSet();
                assignableTasks = assignableTasks
                    .Where(t => AreDependencyPrsMerged(t, mergedPrNumbers))
                    .ToList();
            }

            UpdateStatus(AgentStatus.Working, $"📊 Scanning {assignableTasks.Count} tasks for assignment eligibility");

            // LLM-based semantic skill matching: single call matches all tasks to all engineers
            UpdateStatus(AgentStatus.Working, $"🤖 Matching {assignableTasks.Count} tasks to {freeEngineers.Count} engineers");
            var llmAssignments = await MatchTasksToEngineersWithLlmAsync(assignableTasks, freeEngineers, ct);
            if (llmAssignments is not null)
            {
                // Process LLM assignments
                foreach (var (engineerAgentId, task) in llmAssignments)
                {
                    if (!task.IssueNumber.HasValue) continue;
                    var engineer = freeEngineers.FirstOrDefault(e =>
                        string.Equals(e.AgentId, engineerAgentId, StringComparison.OrdinalIgnoreCase));
                    if (engineer is null) continue;

                    assignableTasks.Remove(task);
                    await _taskManager.AssignTaskAsync(task.IssueNumber.Value, engineer.Name, ct);
                    _agentAssignments[engineer.AgentId] = task.IssueNumber.Value;

                    var skillMatch = engineer.Capabilities.Count > 0
                        ? $" (skills: {string.Join(",", engineer.Capabilities)})"
                        : " (generalist)";
                    var taskSkills = task.SkillTags.Count > 0
                        ? $" [tags: {string.Join(",", task.SkillTags)}]"
                        : "";

                    var assignStepId = TaskTracker.BeginStep(Identity.Id, "pe-orchestration", "Assign engineers",
                        $"Assigning issue #{task.IssueNumber} ({task.Name}){taskSkills} to {engineer.Name}{skillMatch} (LLM-matched)", Identity.ModelTier);

                    Logger.LogInformation(
                        "Assigned issue #{IssueNumber} ({TaskName}){TaskSkills} to {Engineer}{SkillMatch} (LLM-matched)",
                        task.IssueNumber, task.Name, taskSkills, engineer.Name, skillMatch);

                    await MessageBus.PublishAsync(new IssueAssignmentMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = engineer.AgentId,
                        MessageType = "IssueAssignment",
                        IssueNumber = task.IssueNumber.Value,
                        IssueTitle = task.Name,
                        Complexity = task.Complexity,
                        IssueUrl = task.IssueUrl
                    }, ct);
                    TaskTracker.CompleteStep(assignStepId);

                    freeEngineers.Remove(engineer);
                }
            }

            // Fallback: assign remaining free engineers to remaining tasks using exact-match logic
            foreach (var engineer in freeEngineers)
            {
                if (assignableTasks.Count == 0) break;

                EngineeringTask? bestTask;
                if (engineer.Capabilities.Count > 0)
                {
                    // Specialist: find the best-matching task by skill overlap
                    bestTask = FindBestMatchingTask(assignableTasks, engineer.Capabilities);
                    if (bestTask is null)
                    {
                        // No matching tasks — specialist takes general work (lifecycle: repurposed)
                        bestTask = assignableTasks.FirstOrDefault();
                        if (bestTask is not null)
                        {
                            Logger.LogInformation(
                                "Specialist {Name} has no matching tasks — repurposing to general task {TaskId}",
                                engineer.Name, bestTask.Id);
                        }
                    }
                }
                else
                {
                    // Generalist: prefer tasks that no specialist would match, or highest complexity
                    bestTask = FindBestTaskForGeneralist(assignableTasks, registeredEngineers);
                }

                if (bestTask is null || !bestTask.IssueNumber.HasValue)
                    continue;

                // Atomic claim check — prevents two workers getting the same task
                if (!TryClaimTask(bestTask.IssueNumber.Value, bestTask.Name))
                {
                    Logger.LogDebug("Leader assign: task #{IssueNumber} already claimed", bestTask.IssueNumber);
                    continue;
                }

                assignableTasks.Remove(bestTask);
                await _taskManager.AssignTaskAsync(bestTask.IssueNumber.Value, engineer.Name, ct);
                _agentAssignments[engineer.AgentId] = bestTask.IssueNumber.Value;

                var skillMatch = engineer.Capabilities.Count > 0
                    ? $" (skills: {string.Join(",", engineer.Capabilities)})"
                    : " (generalist)";
                var taskSkills = bestTask.SkillTags.Count > 0
                    ? $" [tags: {string.Join(",", bestTask.SkillTags)}]"
                    : "";

                var assignStepId = TaskTracker.BeginStep(Identity.Id, "pe-orchestration", "Assign engineers",
                    $"Assigning issue #{bestTask.IssueNumber} ({bestTask.Name}){taskSkills} to {engineer.Name}{skillMatch}", Identity.ModelTier);

                Logger.LogInformation(
                    "Assigned issue #{IssueNumber} ({TaskName}){TaskSkills} to {Engineer}{SkillMatch}",
                    bestTask.IssueNumber, bestTask.Name, taskSkills, engineer.Name, skillMatch);

                await MessageBus.PublishAsync(new IssueAssignmentMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = engineer.AgentId,
                    MessageType = "IssueAssignment",
                    IssueNumber = bestTask.IssueNumber.Value,
                    IssueTitle = bestTask.Name,
                    Complexity = bestTask.Complexity,
                    IssueUrl = bestTask.IssueUrl
                }, ct);
                TaskTracker.CompleteStep(assignStepId);
            }

            var assignedCount = _agentAssignments.Count;
            UpdateStatus(AgentStatus.Working, $"✅ Assigned {assignedCount} tasks to engineers");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to assign tasks to available engineers");
        }
    }

    private async Task WorkOnOwnTasksAsync(CancellationToken ct)
    {
        try
        {
            // Don't start a new task if PE already has a PR in progress
            if (CurrentPrNumber is not null)
                return;

            EngineeringTask? task;

            if (!IsLeader())
            {
                // ── WORKER PE: only work on tasks explicitly assigned by the leader ──
                UpdateStatus(AgentStatus.Idle, "📋 Scanning for available tasks");

                // Check for pending assignments delivered via IssueAssignmentMessage
                if (AssignmentQueue.TryDequeue(out var assignment))
                {
                    // Refresh cache from GitHub to get the latest status after leader's assignment
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindByIssueNumber(assignment.IssueNumber);
                    if (task is null || EngineeringTaskIssueManager.IsTaskPastImplementation(task))
                    {
                        Logger.LogWarning(
                            "Worker PE: assigned task #{IssueNumber} not found or already done, skipping",
                            assignment.IssueNumber);
                        return;
                    }
                    Logger.LogInformation(
                        "Worker PE picking up assigned task #{IssueNumber}: {Name}",
                        assignment.IssueNumber, task.Name);
                    UpdateStatus(AgentStatus.Working, $"📋 Selected task: {Truncate(task.Name, 40)}");
                }
                else
                {
                    // No pending message — refresh from GitHub and look for tasks assigned to us
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindAssignedTo(Identity.DisplayName);
                    if (task is null)
                    {
                        // Self-claim fallback: if we've been idle too long and there are unassigned
                        // pending tasks, claim one directly instead of waiting forever for the leader.
                        // This handles the case where workers spawn after the leader is already deep
                        // in its own implementation and can't loop back to assign tasks.
                        _idleLoopCount++;
                        if (_idleLoopCount >= SelfClaimAfterIdleLoops)
                        {
                            var selfClaimCandidates = _taskManager.Tasks
                                .Where(t => t.Status == "Pending"
                                    && _taskManager.IsWaveEligible(t)
                                    && _taskManager.AreDependenciesMet(t)
                                    && !IsIntegrationTask(t)
                                    && t.IssueNumber.HasValue
                                    && !IsFoundationTask(t)
                                    && string.IsNullOrEmpty(t.AssignedTo)
                                    && !_blockedTaskIds.Contains(t.Id))
                                .ToList();

                            // Dependency-merged guard: verify dependency PRs are actually merged to main,
                            // not just the issue transiently closed. Prevents wave violations from race
                            // conditions where issues are briefly closed then reopened.
                            if (selfClaimCandidates.Count > 0)
                            {
                                var mergedPrNumbers = (await GetCachedMergedPRsAsync(ct))
                                    .Where(IsCurrentRunScopePr)
                                    .Select(p => p.Number).ToHashSet();
                                selfClaimCandidates = selfClaimCandidates
                                    .Where(t => AreDependencyPrsMerged(t, mergedPrNumbers))
                                    .ToList();
                            }

                            task = selfClaimCandidates
                                .OrderByDescending(t => MatchesCapabilities(t))
                                .ThenByDescending(t => t.Complexity == "High" ? 3 : t.Complexity == "Medium" ? 2 : 1)
                                .FirstOrDefault();

                            if (task is not null)
                            {
                                if (!TryClaimTask(task.IssueNumber!.Value, task.Name))
                                {
                                    Logger.LogDebug("Worker PE self-claim: task #{IssueNumber} already claimed", task.IssueNumber);
                                }
                                else
                                {
                                    await _taskManager.AssignTaskAsync(task.IssueNumber!.Value, Identity.DisplayName, ct);
                                    Logger.LogInformation(
                                        "Worker PE self-claimed unassigned task #{IssueNumber}: {Name} (idle for {Loops} loops)",
                                        task.IssueNumber, task.Name, _idleLoopCount);
                                    UpdateStatus(AgentStatus.Working, $"📋 Selected task: {Truncate(task.Name, 40)}");
                                    _idleLoopCount = 0;
                                }
                            }
                            else
                            {
                                Logger.LogDebug("Worker PE: no unassigned tasks available for self-claim");
                                return;
                            }
                        }
                        else
                        {
                            UpdateStatus(AgentStatus.Idle, "⏳ Waiting for task assignment");
                            Logger.LogDebug("Worker PE: no task assigned to {Name}, waiting for leader (idle {Count}/{Max})",
                                Identity.DisplayName, _idleLoopCount, SelfClaimAfterIdleLoops);
                            return;
                        }
                    }
                    else
                    {
                        _idleLoopCount = 0;
                        Logger.LogInformation(
                            "Worker PE recovered assigned task #{IssueNumber}: {Name}",
                            task.IssueNumber, task.Name);
                    }
                }
            }
            else
            {
                // ── LEADER PE: refresh cache from GitHub to see latest assignments, then pick ──
                await _taskManager.LoadTasksAsync(ct);

                // ═══ ASSIGN-FIRST RULE: Always ensure idle workers get tasks before the leader blocks itself ═══
                // The leader's implementation loop can block for many minutes. Before self-claiming
                // ANY task, verify all available workers are busy. If any are idle, assign to them first.
                var allWorkers = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                    .Where(a => a.Identity.Id != Identity.Id)
                    .ToList();

                if (allWorkers.Count > 0)
                {
                    var idleWorkers = allWorkers
                        .Where(a => a.Status is AgentStatus.Idle or AgentStatus.Online or AgentStatus.Initializing)
                        .Where(a => a.Identity.AssignedPullRequest is null)
                        .ToList();

                    if (idleWorkers.Count > 0)
                    {
                        // There are idle workers — try to assign them work before we pick up our own
                        Logger.LogInformation(
                            "SE leader: {IdleCount} idle worker(s) detected, assigning tasks before self-claiming",
                            idleWorkers.Count);
                        await AssignTasksToAvailableEngineersAsync(ct);

                        // Re-check: are there still unassigned tasks that idle workers could take?
                        // Only proceed to self-claim if all workers are now busy OR no more assignable tasks remain.
                        await _taskManager.LoadTasksAsync(ct); // refresh after assignment
                        var unassignedTasks = _taskManager.Tasks
                            .Where(t => t.Status == "Pending"
                                && _taskManager.IsWaveEligible(t)
                                && _taskManager.AreDependenciesMet(t)
                                && !IsIntegrationTask(t)
                                && t.IssueNumber.HasValue
                                && !IsFoundationTask(t)
                                && string.IsNullOrEmpty(t.AssignedTo))
                            .ToList();

                        // Re-read idle workers after assignment attempt
                        var stillIdleWorkers = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                            .Where(a => a.Identity.Id != Identity.Id)
                            .Where(a => a.Status is AgentStatus.Idle or AgentStatus.Online or AgentStatus.Initializing)
                            .Where(a => a.Identity.AssignedPullRequest is null)
                            .ToList();

                        if (stillIdleWorkers.Count > 0 && unassignedTasks.Count > 0)
                        {
                            // Workers are still idle and tasks exist — defer self-claim to give workers
                            // time to pick up assignments (they may be mid-loop)
                            Logger.LogInformation(
                                "SE leader deferring self-claim: {IdleWorkers} worker(s) still idle, {Tasks} unassigned task(s) available",
                                stillIdleWorkers.Count, unassignedTasks.Count);
                            return;
                        }
                    }
                }

                // Prioritize foundation task (T1/W0) for self-implementation — it sets the
                // project structure that all other tasks depend on, so the Lead handles it directly.
                var foundationTask = _taskManager.Tasks
                    .Where(t => t.Status == "Pending"
                        && _taskManager.AreDependenciesMet(t)
                        && !IsIntegrationTask(t)
                        && t.IssueNumber.HasValue
                        && IsFoundationTask(t))
                    .OrderBy(t => t.IssueNumber) // Prefer earliest-created when duplicates exist
                    .FirstOrDefault();

                if (foundationTask is not null)
                {
                    // Check if foundation task is already claimed before committing to it.
                    // If claimed by another agent (e.g., a specialist), fall through to normal
                    // task selection instead of entering a retry loop.
                    if (ClaimRegistry?.IsClaimed(foundationTask.IssueNumber!.Value) == true)
                    {
                        Logger.LogInformation(
                            "Foundation task {TaskId} (#{IssueNumber}) already claimed by {Holder} — falling through to normal task selection",
                            foundationTask.Id, foundationTask.IssueNumber,
                            ClaimRegistry.GetClaimHolder(foundationTask.IssueNumber!.Value) ?? "unknown");
                        task = _taskManager.FindNextAssignableTask("High", "Medium", "Low");
                    }
                    else
                    {
                        task = foundationTask;
                        LogActivity("task", $"🏗️ SE Lead taking foundation task #{task.IssueNumber} for self-implementation");
                        Logger.LogInformation(
                            "SE Lead claiming foundation task {TaskId} (#{IssueNumber}: {Name}) for self-implementation",
                            task.Id, task.IssueNumber, task.Name);
                    }
                }
                else
                {
                    task = _taskManager.FindNextAssignableTask("High", "Medium", "Low");
                }

                // Never pick up the integration task through normal assignment — it's handled
                // by CheckAllTasksCompleteAsync → CreateIntegrationPRAsync
                if (task is not null && IsIntegrationTask(task))
                    task = null;

                if (task is null)
                {
                    if (_taskManager.PendingCount > 0)
                        Logger.LogDebug("No assignable tasks: {Pending} pending, some blocked by dependencies",
                            _taskManager.PendingCount);
                    return;
                }

                // Final guard: if non-foundation task and idle workers exist, defer to them
                if (!IsFoundationTask(task))
                {
                    var busyCheck = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                        .Where(a => a.Identity.Id != Identity.Id)
                        .Where(a => a.Status is AgentStatus.Idle or AgentStatus.Online or AgentStatus.Initializing)
                        .Where(a => a.Identity.AssignedPullRequest is null)
                        .ToList();

                    if (busyCheck.Count > 0)
                    {
                        Logger.LogInformation(
                            "SE leader deferring task {TaskId} ({Complexity}) — {IdleCount} idle worker(s) should take it",
                            task.Id, task.Complexity, busyCheck.Count);
                        return;
                    }
                }
            }

            if (!task.IssueNumber.HasValue)
            {
                Logger.LogWarning("Task {TaskId} has no issue number — skipping", task.Id);
                return;
            }

            // ── Layer 1 guard: in-memory HashSet — bail before any GitHub API calls ──
            if (_blockedTaskIds.Contains(task.Id))
            {
                Logger.LogDebug(
                    "Task {TaskId} (issue #{IssueNumber}) is in-process blocked — skipping without API calls",
                    task.Id, task.IssueNumber);
                return;
            }

            // Claim validation: re-fetch from GitHub to prevent race conditions
            await _taskManager.LoadTasksAsync(ct);
            var freshTask = _taskManager.FindByIssueNumber(task.IssueNumber.Value);
            if (freshTask is null || EngineeringTaskIssueManager.IsTaskPastImplementation(freshTask))
            {
                Logger.LogInformation(
                    "Task #{IssueNumber} already done or closed — skipping",
                    task.IssueNumber);
                return;
            }
            if (EngineeringTaskIssueManager.IsTaskBlocked(freshTask))
            {
                // Platform label set (may be from a previous session) — sync to in-memory HashSet
                _blockedTaskIds.Add(task.Id);
                Logger.LogInformation(
                    "Task #{IssueNumber} is blocked on the platform — skipping",
                    task.IssueNumber);
                return;
            }
            if (freshTask.Status is "InProgress"
                && !string.Equals(freshTask.AssignedTo, Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation(
                    "Task #{IssueNumber} already in-progress by {Other}, skipping",
                    task.IssueNumber, freshTask.AssignedTo);
                return;
            }

            // ── Retry guard: check if we already have an open PR for this task ──
            // Prevents the SE from creating duplicate PRs when the task is re-entered after
            // rework failures, force-approval cycles, or orphan recovery resets.
            try
            {
                var existingPr = await FindExistingPrForTaskAsync(task, ct);
                if (existingPr is not null)
                {
                    Logger.LogInformation(
                        "Task {TaskId} already has open PR #{PrNumber} — restoring tracking instead of creating new PR",
                        task.Id, existingPr.Number);

                    if (PullRequestWorkflow.Labels.IsPastImplementation(existingPr.Labels))
                    {
                        // PR is past implementation (ready-for-review/approved) — track it
                        // so rework/merge flows continue, but don't block new task pickup
                        TrackPastImplementationPr(existingPr.Number);

                        // Mark the task as implementation-complete so it won't be re-developed.
                        // Issue stays open so wave gating remains enforced until PR merge.
                        if (task.IssueNumber.HasValue)
                        {
                            await _taskManager.MarkImplementationCompleteAsync(task.IssueNumber.Value, existingPr.Number, ct);
                            Logger.LogInformation(
                                "Task {TaskId} (issue #{IssueNumber}) marked ImplementationComplete — PR #{PrNumber} is past implementation",
                                task.Id, task.IssueNumber.Value, existingPr.Number);
                        }
                    }
                    else
                    {
                        // PR is still in implementation — restore CurrentPrNumber so the
                        // ContinueOwnPrImplementationAsync path handles it
                        CurrentPrNumber = existingPr.Number;
                        Identity.AssignedPullRequest = existingPr.Number.ToString();
                        _currentTaskName = task.Name;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to check for existing PR for task {TaskId}", task.Id);
            }

            // Atomic claim check before touching GitHub — must come BEFORE the reacquisition
            // counter so that tasks already claimed by another agent (e.g., a specialist running
            // a strategy) don't burn through the counter while the SE leader polls.
            if (!TryClaimTask(task.IssueNumber.Value, task.Name))
            {
                Logger.LogInformation("Leader self-claim: task #{IssueNumber} already claimed by another agent", task.IssueNumber);
                return; // skip this task
            }

            // ── Reacquisition cap: prevent infinite task retry loops ──
            // Only increment AFTER TryClaimTask succeeds — we are actually acquiring the task,
            // not just observing it while another agent works on it.
            var acquisitions = _taskAcquisitionCounts.GetValueOrDefault(task.Id, 0) + 1;
            _taskAcquisitionCounts[task.Id] = acquisitions;
            if (acquisitions > Config.Limits.MaxTaskReacquisitions)
            {
                var blockReason =
                    $"⛔ Blocked by Software Engineer: task {task.Id} has been picked up {acquisitions} times " +
                    $"(max {Config.Limits.MaxTaskReacquisitions}) without reaching a stable implementation state. " +
                    "Keeping this issue open with status:blocked so it is excluded from automatic pickup and visible for operator review.";
                Logger.LogWarning(
                    "Task {TaskId} has been picked up {Attempts} times (max {Max}) — marking blocked to prevent infinite retry",
                    task.Id, acquisitions, Config.Limits.MaxTaskReacquisitions);
                LogActivity("task", $"⛔ Task {task.Id} blocked after {acquisitions} acquisition attempts (max {Config.Limits.MaxTaskReacquisitions})");
                _blockedTaskIds.Add(task.Id); // Layer 1: immediate in-process exclusion
                await _taskManager.MarkBlockedAsync(task.IssueNumber.Value, blockReason, ct); // Layer 2: durable platform label
                ClaimRegistry?.Release(task.IssueNumber.Value); // Release the claim so it can be inspected
                return;
            }

            // File overlap check: if this task's files already exist in recently merged PRs,
            // the work is already done — mark the task complete and skip.
            if (task.OwnedFiles.Count > 0)
            {
                // Informational: log file overlap with merged PRs from this run.
                // We do NOT auto-skip — overlap is expected for shared files (models, config, etc.).
                // The AI code generator is told about existing files and will modify rather than recreate.
                try
                {
                    var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
                    var mergedFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mergedPr in mergedPRs.Take(10))
                    {
                        var prFiles = await PrService.GetChangedFilesAsync(mergedPr.Number, ct);
                        foreach (var f in prFiles)
                            mergedFileSet.Add(f.ToLowerInvariant().Replace('\\', '/'));
                    }

                    var taskFilesNormalized = task.OwnedFiles
                        .Select(f => f.ToLowerInvariant().Replace('\\', '/'))
                        .ToList();
                    var overlapping = taskFilesNormalized
                        .Count(f => mergedFileSet.Contains(f));

                    if (overlapping > 0)
                    {
                        Logger.LogInformation(
                            "Task {TaskId} (#{IssueNumber}): {Overlap}/{Total} files overlap with merged PRs — proceeding (shared files are expected)",
                            task.Id, task.IssueNumber, overlapping, taskFilesNormalized.Count);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not check merged PR file overlap for task {TaskId}", task.Id);
                }
            }

            // Mark as assigned to self via the task manager
            await _taskManager.AssignTaskAsync(task.IssueNumber.Value, Identity.DisplayName, ct);
            _currentTaskName = task.Name;

            UpdateStatus(AgentStatus.Working, $"Working on: {task.Name}");
            LogActivity("task", $"📋 Claimed task #{task.IssueNumber}: {task.Name} ({task.Complexity})");
            Logger.LogInformation("Software Engineer working on task {TaskId}: {TaskName}",
                task.Id, task.Name);

            // ── Pre-PR Clarification Questions (gate before any implementation) ──
            string clarificationContext = "";
            if (task.IssueNumber.HasValue && ClarificationStore is not null)
            {
                var clarStepId = TaskTracker.BeginStep(Identity.Id, task.Id,
                    "Generate clarification questions", "Calling LLM for pre-PR questions", Identity.ModelTier);
                try
                {
                    var taskIssue = (await WorkItemService.GetAsync(task.IssueNumber.Value, ct))?.ToAgentIssue();
                    if (taskIssue is not null)
                    {
                        var pmSpec = await ProjectFiles.GetPMSpecAsync(ct) ?? "";
                        var archDoc = await ProjectFiles.GetArchitectureDocAsync(ct) ?? "";
                        clarificationContext = await GeneratePrePRQuestionsAsync(taskIssue, pmSpec, archDoc, ct);
                        TaskTracker.RecordLlmCall(clarStepId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Pre-PR clarification failed for task {TaskId} — continuing without", task.Id);
                }
                finally
                {
                    TaskTracker.CompleteStep(clarStepId);
                }
            }

            // Fallback gate: if question generation failed/returned empty but the gate IS enabled,
            // still pause for human approval (dual-path parity with WorkOnIssueAsync).
            if (string.IsNullOrEmpty(clarificationContext)
                && task.IssueNumber.HasValue
                && (GateCheck?.RequiresHuman(GateIds.PrePRClarification) ?? false))
            {
                var gateStepId = TaskTracker.BeginStep(Identity.Id, task.Id,
                    "Waiting for clarification approval", "Gate enabled but no questions generated", Identity.ModelTier);
                TaskTracker.SetStepWaiting(gateStepId);
                try
                {
                    Logger.LogInformation(
                        "{Agent} pre-PR question generation returned empty but gate is enabled — requesting approval to proceed",
                        Identity.DisplayName);

                    var fallbackGateResult = await WaitForHumanGateAsync(
                        GateIds.PrePRClarification,
                        $"{Identity.DisplayName}: Clarification question generation failed for task {task.Id}: {task.Name}. " +
                        "Approve to proceed without clarification questions.",
                        task.IssueNumber.Value, ct: ct);

                    if (fallbackGateResult.WasRejected)
                    {
                        Logger.LogInformation(
                            "{Agent} pre-PR fallback gate rejected for task {TaskId}: {Feedback} — skipping task",
                            Identity.DisplayName, task.Id, fallbackGateResult.Feedback);
                        return;
                    }
                }
                finally
                {
                    TaskTracker.CompleteStep(gateStepId);
                }
            }

            // Track step: Generate PR description
            var descStepId = TaskTracker.BeginStep(Identity.Id, task.Id, "Generate PR description",
                $"Creating description for {task.Name}", Identity.ModelTier);
            UpdateStatus(AgentStatus.Working, $"Generating PR description: {task.Name}");
            var prDescription = await GenerateTaskDescriptionAsync(task, ct);
            TaskTracker.RecordLlmCall(descStepId);
            TaskTracker.CompleteStep(descStepId);

            if (task.IssueNumber.HasValue)
                prDescription = $"Closes #{task.IssueNumber}\n\n{prDescription}";

            // Inject clarification context into PR description for downstream consumers
            if (!string.IsNullOrEmpty(clarificationContext))
                prDescription += $"\n\n## Implementation Decisions\n{clarificationContext}";

            // Sanitize AI-generated content to prevent accidental auto-close of sibling issues
            prDescription = SanitizeAutoCloseReferences(prDescription, task.IssueNumber);

            // Track step: Create branch & PR
            var createPrStepId = TaskTracker.BeginStep(Identity.Id, task.Id, "Create branch & PR",
                $"Creating branch and PR for {task.Name}", Identity.ModelTier);
            UpdateStatus(AgentStatus.Working, $"Creating branch & PR: {task.Name}");
            var branchName = await PrWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName,
                $"{task.Id}-{task.Name}",
                ct);

            var pr = await PrWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                task.Name,
                prDescription,
                task.Complexity,
                "Architecture.md",
                "",
                branchName,
                additionalLabels: null,
                ct);
            TaskTracker.CompleteStep(createPrStepId);

            // Mark task in-progress via the task manager
            if (task.IssueNumber.HasValue)
            {
                await _taskManager.MarkInProgressAsync(task.IssueNumber.Value, pr.Number, ct);
                // Create native platform link (ADO: Development section artifact link, GitHub: "Closes #X" in body)
                await PrService.LinkWorkItemAsync(pr.Number, task.IssueNumber.Value, ct);
            }

            // Track this PR so PE doesn't start another task concurrently
            CurrentPrNumber = pr.Number;
            Identity.AssignedPullRequest = pr.Number.ToString();

            // Bind CLI session to this PR for conversational continuity
            ActivatePrSession(pr.Number);

            Logger.LogInformation(
                "Software Engineer created PR #{PrNumber} for task {TaskId}, starting implementation",
                pr.Number, task.Id);

            // ── Strategy Framework integration (opt-in via StrategyFrameworkConfig.Enabled) ──
            // Try the multi-strategy orchestrator first. If it produces and applies a winning
            // patch (with build verification), skip the legacy code-gen path and proceed to
            // ready-for-review. On any failure, fall back to the legacy path so we never
            // leave the task half-done.
            if (await TryRunStrategyFrameworkAsync(task, pr, ct))
            {
                Logger.LogInformation(
                    "Strategy framework produced winning candidate for PR #{PrNumber} (task {TaskId}); skipping legacy code-gen",
                    pr.Number, task.Id);
                Core!.ReasoningLog!.Log(new AgentReasoningEvent
                {
                    AgentId = Identity.Id,
                    AgentDisplayName = Identity.DisplayName,
                    EventType = AgentReasoningEventType.Decision,
                    Phase = "Code Generation",
                    Summary = $"Strategy framework succeeded for PR #{pr.Number} — using winning candidate",
                    Detail = $"Task: {task.Name}. Multi-strategy orchestrator produced and applied winning patch with build verification."
                });
                await FinalizeReadyForReviewAsync(pr, task, ct);
                return;
            }

            // ── Fallback: delegate to base class agentic step loop ──
            // The base class ImplementAndCommitAsync prefers agentic CLI mode (full tool access)
            // over blind FILE: block generation. Build a synthetic issue with any failed winner
            // context, then delegate.
            Logger.LogInformation(
                "Strategy framework did not produce a winner for task {TaskId}; delegating to agentic step-by-step implementation",
                task.Id);

            // Build a synthetic issue for the step generation
            AgentIssue? sourceIssue = null;
            if (task.IssueNumber.HasValue)
                sourceIssue = (await WorkItemService.GetAsync(task.IssueNumber.Value, ct))?.ToAgentIssue();

            var syntheticIssue = sourceIssue ?? new AgentIssue
            {
                Number = task.IssueNumber ?? 0,
                Title = task.Name,
                Body = task.Description,
                State = "open",
                Labels = new List<string>()
            };

            // If the strategy framework chose a winner but apply/build failed, pass it as reference
            string? winnerReference = _failedWinnerPatchContext;
            _failedWinnerPatchContext = null; // consume once
            if (!string.IsNullOrEmpty(winnerReference))
            {
                var referenceNote =
                    "\n\n## Reference Implementation (from strategy framework — failed to apply cleanly)\n" +
                    "Use this as a strong starting point. Fix any issues that prevented it from building:\n\n" +
                    $"```diff\n{(winnerReference.Length > 8000 ? winnerReference[..8000] + "\n...(truncated)" : winnerReference)}\n```";
                syntheticIssue = syntheticIssue with { Body = (syntheticIssue.Body ?? "") + referenceNote };
                Logger.LogInformation(
                    "Injecting failed strategy winner patch ({Length} chars) as reference for agentic codegen on task {TaskId}",
                    winnerReference.Length, task.Id);
            }

            // Delegate to base class which uses agentic CLI with full tool access
            await base.ImplementAndCommitAsync(pr, syntheticIssue, ct);

            // Track step: Mark ready for review
            await FinalizeReadyForReviewAsync(pr, task, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to work on own tasks: {Message}", ex.Message);
            RecordError($"WorkOnOwnTasks: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            LogActivity("task", $"❌ Task failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sync the PR branch with main, mark it ready-for-review, and broadcast the review request.
    /// Extracted so both the legacy code-gen path and the strategy framework path share the
    /// same finalization sequence.
    /// </summary>
    private async Task FinalizeReadyForReviewAsync(AgentPullRequest pr, EngineeringTask task, CancellationToken ct)
    {
        var readyStepId = TaskTracker.BeginStep(Identity.Id, task.Id, "Mark ready for review",
            $"Syncing branch and marking PR #{pr.Number} ready", Identity.ModelTier);

        // NoMessyCodePlan post-Tier-2: pre-self-assessment screenshot expectation check.
        // Surfaces blank canvases / wrong scenes / error pages as implementation notes so the
        // self-assessment LLM folds them into gap analysis. (Lesson #14, #16 — SoftwareEngineer
        // bypasses base-class paths; cross-cutting features must be wired into BOTH this method
        // AND EngineerAgentBase.MarkPrCompleteAsync.)
        await RunPrePublishScreenshotCheckAsync(pr, ct);

        // Pre-publish self-assessment: re-read requirements with fresh context and verify completeness
        if (task.IssueNumber is not null)
        {
            var issue = (await WorkItemService.GetAsync(task.IssueNumber.Value, ct))?.ToAgentIssue();
            if (issue is not null)
            {
                await RunPrePublishAssessmentAsync(pr, issue, ct);

                // Change #2 — Completion manifest enforcement (Lesson #14: SoftwareEngineerAgent
                // bypasses base-class MarkPrCompleteAsync; enforcement MUST be wired here too).
                if (await IsBlockedByCompletionManifestAsync(pr, issue, ct))
                {
                    TaskTracker.FailStep(readyStepId, "Blocked by completion manifest enforcement");
                    // Enqueue self-rework to fix stubs (same fix as base-class path)
                    var stubFeedback = $"[Self-Assessment] Stub detection found incomplete implementations in PR #{pr.Number}. " +
                        "Please implement all stub/placeholder methods fully. " +
                        "Check the completion manifest comments on the PR for specific offenders.";
                    ReworkQueue.Enqueue(new ReworkItem(pr.Number, pr.Title, stubFeedback, Identity.DisplayName));
                    Logger.LogInformation(
                        "{Role} {Name} enqueued self-rework for stub-blocked PR #{PrNumber}",
                        Identity.Role, Identity.DisplayName, pr.Number);
                    return;
                }
            }
        }

        // Sync branch with main before marking ready — ensures PR is merge-clean
        TaskTracker.RecordSubStep(readyStepId, "Syncing branch with main");
        await SyncBranchWithMainAsync(pr.Number, ct);

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

        // D1: placeholder-string guard. For tasks that claim to wire/compose/integrate/finalize
        // a UI, refuse to mark ready if the PR ships literal placeholder strings in UI files.
        // Skipped in SinglePR: the strategy framework judge already evaluated code quality,
        // and the T-FINAL task name always contains integration verbs causing false positives.
        if (!Config.Limits.IsSinglePr)
        {
            var placeholderWarning = await CheckPrForPlaceholderStringsAsync(pr, task, ct);
            if (!string.IsNullOrEmpty(placeholderWarning))
            {
                Logger.LogWarning("D1 guard: PR #{Pr} contains forbidden placeholder strings for an integration task. Will proceed anyway with warning logged.", pr.Number);
                await ReviewService.AddCommentAsync(pr.Number,
                    $"[SoftwareEngineer] ⚠️ Placeholder strings detected (non-blocking warning):\n\n{placeholderWarning}\n\n" +
                    "This task contains integration verbs but the PR has literal placeholder strings in UI files. " +
                    "Marking ready-for-review anyway — reviewers should verify these are acceptable.",
                    ct);
                // Continue to mark ready — don't silently go idle (old behavior caused stuck state)
            }
        }

        TaskTracker.RecordSubStep(readyStepId, "Marking PR ready for review + capturing screenshot");
        // Consume stashed winner candidate (if strategy framework ran) to attach proven media
        _winnersByPr.Remove(pr.Number, out var winnerForMedia);
        await MarkReadyForReviewWithScreenshotAsync(pr, winnerForMedia, ct);
        TaskTracker.CompleteStep(readyStepId);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "CodeReview"
        }, ct);

        UpdateStatus(AgentStatus.Working, $"Ready for review: {task.Name}");
        Logger.LogInformation(
            "Software Engineer completed implementation for PR #{PrNumber} (task {TaskId})",
            pr.Number, task.Id);

        // Mark as ImplementationComplete (not Done) — keeps issue open so wave gate remains
        // enforced. Later waves only start when this task's PR is merged and marked Done.
        // This still prevents re-development on restart (ImplementationComplete ≠ Pending).
        if (task.IssueNumber.HasValue)
        {
            await _taskManager.MarkImplementationCompleteAsync(task.IssueNumber.Value, pr.Number, ct);
            Logger.LogInformation(
                "Task {TaskId} (issue #{IssueNumber}) marked ImplementationComplete after ready-for-review (restart safety + wave gate preserved)",
                task.Id, task.IssueNumber.Value);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Change #1 — Scenario tagging (WP-J Wave 2)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// After engineering-task issues are created, calls a cheap per-task LLM micro-call
    /// to determine which project scenarios each task implements, then appends a
    /// "## Implements Scenarios" section to the issue body.
    ///
    /// <para>Strategy: LLM micro-call (option b from plan spec). Each call is a single
    /// user message requesting a JSON array of scenario IDs — output ≤ 50 tokens.</para>
    ///
    /// <para>Gracefully skipped when no scenario registry is wired, no scenarios are loaded,
    /// or when any individual task call fails — never blocks the engineering plan flow.</para>
    /// </summary>
    private async Task StampScenarioTagsAsync(IReadOnlyList<EngineeringTask> tasks, CancellationToken ct)
    {
        if (_scenarioRegistry is null) return;

        IReadOnlyList<Scenario> scenarios = _scenarioRegistry.Current;
        if (scenarios.Count == 0)
        {
            try
            {
                scenarios = await _scenarioRegistry.LoadAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load scenarios for task stamping — skipping");
                return;
            }
        }

        if (scenarios.Count == 0)
        {
            Logger.LogDebug("No scenarios loaded — skipping scenario stamp for {Count} tasks", tasks.Count);
            return;
        }

        // Serialize scenario catalogue once (shared across all tasks)
        var scenarioSummary = System.Text.Json.JsonSerializer.Serialize(
            scenarios.Select(s => new { id = s.Id, title = s.Title }),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Accumulate scenario→task reverse mapping for registry update
        var scenarioToTasks = new Dictionary<string, List<string>>();

        foreach (var task in tasks)
        {
            if (task.IssueNumber is null) continue;

            try
            {
                var taskDesc = task.Description is { Length: > 200 } d ? d[..200] + "…" : task.Description;
                var micro = $"Task: {task.Name}\nDescription: {taskDesc}\n\n" +
                            $"Scenarios: {scenarioSummary}\n\n" +
                            "Return a JSON array of scenario IDs this task implements. " +
                            "Return [] if it is pure infrastructure. Example: [\"S01\",\"S03\"]";

                var microHistory = CreateChatHistory();
                microHistory.AddUserMessage(micro);
                var result = await chat.GetChatMessageContentAsync(microHistory, cancellationToken: ct);
                var raw = result?.Content?.Trim() ?? "[]";

                // Strip markdown fences if present
                if (raw.StartsWith("```"))
                {
                    var nl = raw.IndexOf('\n');
                    if (nl >= 0) raw = raw[(nl + 1)..];
                    if (raw.TrimEnd().EndsWith("```")) raw = raw.TrimEnd()[..^3];
                    raw = raw.Trim();
                }

                List<string> scenarioIds;
                try
                {
                    scenarioIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [];
                }
                catch
                {
                    Logger.LogDebug("Scenario tag LLM response could not be parsed for task {TaskId} — skipping stamp", task.Id);
                    continue;
                }

                if (scenarioIds.Count == 0) continue;

                // Validate IDs against registry to avoid phantom references
                var valid = scenarioIds
                    .Where(id => _scenarioRegistry.FindById(id) is not null)
                    .ToList();

                if (valid.Count == 0) continue;

                // Append section to the issue body
                var stamp = $"\n\n## Implements Scenarios\n{string.Join("\n", valid.Select(id => $"- {id}"))}";

                var workItem = await WorkItemService.GetAsync(task.IssueNumber.Value, ct);
                if (workItem is not null)
                {
                    var newBody = (workItem.Body ?? "") + stamp;
                    await WorkItemService.UpdateAsync(task.IssueNumber.Value, body: newBody, ct: ct);
                    Logger.LogInformation(
                        "Stamped scenario tags on task #{IssueNumber} ({TaskId}): {ScenarioIds}",
                        task.IssueNumber.Value, task.Id, string.Join(", ", valid));
                }

                // Build reverse mapping: scenario → task references
                var taskRef = $"{task.Id}: {task.Name}";
                foreach (var scenarioId in valid)
                {
                    if (!scenarioToTasks.TryGetValue(scenarioId, out var list))
                    {
                        list = new List<string>();
                        scenarioToTasks[scenarioId] = list;
                    }
                    list.Add(taskRef);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "Failed to stamp scenario tags on task #{IssueNumber} ({TaskId}) — skipping",
                    task.IssueNumber, task.Id);
            }
        }

        // Write reverse mapping back to registry so ImplementingTasks is populated
        if (scenarioToTasks.Count > 0)
        {
            try
            {
                var readOnly = scenarioToTasks.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value);
                await _scenarioRegistry.UpdateImplementingTasksAsync(readOnly, ct);
                Logger.LogInformation(
                    "Updated ImplementingTasks for {Count} scenarios in registry",
                    scenarioToTasks.Count);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to update ImplementingTasks in scenario registry");
            }
        }
    }

    /// <summary>
    /// D1: Check whether the PR's touched UI files contain forbidden literal placeholder
    /// strings when the task claims to wire/compose/integrate/finalize a UI component.
    /// Returns a human-readable warning message if violations found, otherwise empty.
    /// </summary>
    private async Task<string> CheckPrForPlaceholderStringsAsync(
        AgentPullRequest pr, EngineeringTask task, CancellationToken ct){
        var taskText = ($"{task.Name} {task.Description}").ToLowerInvariant();
        string[] integrationVerbs = { "wire", "compose", "integrate", "finalize", "final ", "hook up", "connect", "render" };
        if (!integrationVerbs.Any(v => taskText.Contains(v))) return string.Empty;

        // Generic forbidden literals — no component names (project-agnostic).
        // Catches the word "placeholder" as a standalone user-visible label in any form:
        //   "(placeholder)", "placeholder", 'placeholder', "Widget placeholder", "Panel placeholder", etc.
        string[] forbiddenLiterals = {
            "(placeholder)",
            "\"placeholder\"",
            "'placeholder'",
            "lorem ipsum",
            "coming soon",
            "todo — fill in",
            "todo: fill in",
        };
        // Matches any "<Word> placeholder" or standalone "placeholder" used as rendered text in
        // markup nodes (e.g., `<p>Timeline placeholder</p>`, `<div>Heatmap placeholder</div>`).
        // Anchored between a markup boundary (>) and the closing </, or between quotes in attribute values.
        var placeholderRegex = new System.Text.RegularExpressions.Regex(
            @"(?:>|""|')\s*(?:[A-Za-z][A-Za-z0-9_-]{0,40}\s+)?placeholder\s*(?:<|""|')",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        try
        {
            var files = await PrService.GetChangedFilesAsync(pr.Number, ct);
            var uiFiles = files.Where(f =>
            {
                // D1: exclude test files to avoid false blocks on guard/meta tests that
                // themselves reference the word "placeholder" (e.g., test asserting it's absent).
                var normalized = f.Replace('\\', '/');
                if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) return false;
                if (normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)) return false;
                var name = System.IO.Path.GetFileNameWithoutExtension(normalized);
                if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)) return false;

                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                return ext == ".razor" || ext == ".html" || ext == ".cshtml" ||
                       ext == ".tsx" || ext == ".jsx" || ext == ".vue";
            }).ToList();

            if (uiFiles.Count == 0) return string.Empty;

            var violations = new List<string>();
            foreach (var file in uiFiles)
            {
                var content = await RepoContent.GetFileContentAsync(file, pr.HeadBranch, ct);
                if (string.IsNullOrEmpty(content)) continue;
                var lower = content.ToLowerInvariant();

                string? hit = null;
                foreach (var lit in forbiddenLiterals)
                {
                    if (lower.Contains(lit)) { hit = lit; break; }
                }
                if (hit is null)
                {
                    var m = placeholderRegex.Match(content);
                    if (m.Success)
                    {
                        var snippet = m.Value.Length > 60 ? m.Value.Substring(0, 60) + "…" : m.Value;
                        hit = snippet.Trim();
                    }
                }
                if (hit is not null)
                {
                    violations.Add($"- `{file}` contains literal `{hit}`");
                }
            }

            if (violations.Count == 0) return string.Empty;
            return "Forbidden placeholder strings detected:\n" + string.Join("\n", violations);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "D1: placeholder check failed for PR #{Pr} (skipping)", pr.Number);
            return string.Empty;
        }
    }

    /// <summary>
    /// Strategy Framework integration (Phase 1). When opted in via
    /// <c>StrategyFrameworkConfig.Enabled</c>, runs all configured code-generation
    /// strategies in parallel against per-candidate worktrees, picks a winner, applies
    /// the patch to the PR branch, build-verifies, then commits and pushes with strategy
    /// trailers.
    ///
    /// Returns <c>true</c> when the framework produced and shipped a winner — caller skips
    /// legacy code-gen. Returns <c>false</c> on any guard failure, no winner, head change,
    /// build failure, or exception — caller should fall back to the legacy path.
    /// </summary>
    private async Task<bool> TryRunStrategyFrameworkAsync(
        EngineeringTask task, AgentPullRequest pr, CancellationToken ct)
    {
        // Guards: services must be wired and feature must be opted in.
        if (_strategyOrchestrator is null || _winnerApply is null || _strategyConfig is null)
            return false;

        var cfg = _strategyConfig.CurrentValue;
        if (!cfg.Enabled || cfg.EnabledStrategies.Count == 0)
            return false;

        if (BuildRunnerSvc is null)
        {
            Logger.LogDebug(
                "Strategy framework requires BuildRunner; skipping for task {TaskId}", task.Id);
            return false;
        }

        // Strategy claim coordination: prevent duplicate strategy evaluation by multiple agents
        // on the same task (e.g., FE1 and SE2 both starting candidates for KPI Banner).
        if (task.IssueNumber.HasValue && ClaimRegistry is not null)
        {
            if (!ClaimRegistry.TryClaimStrategy(task.IssueNumber.Value, Identity.Id))
            {
                Logger.LogInformation(
                    "Strategy framework: another agent is already evaluating task #{IssueNumber} ({TaskName}); skipping to avoid duplicate compute",
                    task.IssueNumber.Value, task.Name);
                return false;
            }
        }

        if (Workspace is null)
        {
            Logger.LogDebug(
                "Strategy framework: Workspace is null; attempting on-demand workspace initialization for task {TaskId}", task.Id);

            if (!await EnsureWorkspaceInitializedAsync(ct))
            {
                Logger.LogDebug(
                    "Strategy framework requires LocalWorkspace; skipping for task {TaskId}", task.Id);
                return false;
            }
        }

        var runScope = BranchProvider?.RunScope;
        var fallbackSlug = Identity.DisplayName.Replace(" ", "").ToLowerInvariant();
        var fallbackTaskSlug = $"{task.Id}-{task.Name}";
        var fallbackBranch = runScope is not null
            ? $"agent/{runScope}/{fallbackSlug}/{fallbackTaskSlug}"
            : $"agent/{fallbackSlug}/{fallbackTaskSlug}";
        var branchName = pr.HeadBranch ?? fallbackBranch;

        try
        {
            // Resume PR branch state from the remote — CreateTaskBranchAsync already pushed it.
            await Workspace.CheckoutBranchAsync(branchName, ct);

            var localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();
            if (string.IsNullOrEmpty(localHead))
            {
                Logger.LogWarning("Strategy framework: could not resolve local HEAD for {Branch}; falling back", branchName);
                return false;
            }

            // Pre-flight: confirm remote head hasn't advanced since checkout.
            // CheckoutBranchAsync already fetched, so a new push would be visible here.
            var remoteHead = (await Workspace.GetRemoteShaAsync(branchName, ct)).Trim();
            if (!string.IsNullOrEmpty(remoteHead) &&
                !string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "Strategy framework: remote {Branch} ({Remote}) ahead of local ({Local}); falling back",
                    branchName, remoteHead, localHead);
                return false;
            }

            var runId = StateStore.LastBootUtc != DateTime.MinValue
                ? StateStore.LastBootUtc.ToString("yyyyMMddTHHmmssZ")
                : "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

            var techStack = Config.Project.TechStack ?? "";

            // Load PMSpec, architecture, and source-issue context — same data the legacy
            // single-pass uses. Failures here just leave the corresponding context empty;
            // the baseline generator handles missing context gracefully.
            string pmSpecDoc = "", architectureDoc = "", issueContext = "", designContext = "";
            try { pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct) ?? ""; } catch { /* best-effort */ }
            try { architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct) ?? ""; } catch { /* best-effort */ }

            AgentIssue? sourceIssue = null;
            if (task.IssueNumber.HasValue)
            {
                try { sourceIssue = (await WorkItemService.GetAsync(task.IssueNumber.Value, ct))?.ToAgentIssue(); } catch { /* best-effort */ }
            }
            if (sourceIssue is not null)
                issueContext = $"\n\n## GitHub Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}";

            try
            {
                var designSb = new StringBuilder();
                await AppendDesignContextIfRelevantAsync(designSb, task.Name, task.Description, sourceIssue?.Body, ct);
                designContext = designSb.ToString();
            }
            catch { /* best-effort */ }

            var taskCtx = new TaskContext
            {
                TaskId = task.Id,
                TaskTitle = task.Name,
                TaskDescription = task.Description ?? "",
                PrBranch = branchName,
                BaseSha = localHead,
                RunId = runId,
                AgentRepoPath = Workspace.RepoPath,
                Complexity = MapComplexityToInt(task.Complexity),
                IsWebTask = LooksLikeWebTask(techStack, task.Name, task.Description),
                Wave = task.Wave,
                PmSpec = pmSpecDoc,
                Architecture = architectureDoc,
                TechStack = techStack,
                IssueContext = issueContext,
                DesignContext = designContext,
                ExistingProjectContext = Config.Project.ExistingProjectContext,
            };

            UpdateStatus(AgentStatus.Working, $"Strategy candidates: {task.Name}");

            // Register with the task-step bridge so each strategy candidate gets live dashboard visibility
            var enabledCount = cfg.EnabledStrategies.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var containerStepId = _strategyStepBridge?.RegisterTask(taskCtx.RunId, task.Id, Identity.Id, enabledCount);

            // Surface a clickable PR link on the Frameworks dashboard for the entire run.
            try
            {
                await _strategyOrchestrator.EmitTaskPrLinkedAsync(
                    taskCtx.RunId, task.Id, pr.Number, pr.Url, pr.Title, ct);
            }
            catch (Exception linkEx)
            {
                Logger.LogDebug(linkEx, "Failed to emit TaskPrLinked event for task {TaskId} → PR #{PrNumber}",
                    task.Id, pr.Number);
            }

            var outcome = await _strategyOrchestrator.RunCandidatesAsync(taskCtx, ct);

            if (!outcome.HasWinner)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id,
                    succeeded: false, winnerStrategy: null);
                Logger.LogInformation(
                    "Strategy framework: no winner for task {TaskId} ({Reason}); falling back",
                    task.Id, outcome.Evaluation.TieBreakReason ?? "");
                return false;
            }

            var winner = outcome.Evaluation.Winner!;
            if (string.IsNullOrEmpty(winner.Patch))
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id, succeeded: false);
                Logger.LogInformation(
                    "Strategy framework: winner {Strategy} produced empty patch for task {TaskId}; falling back",
                    winner.StrategyId, task.Id);
                return false;
            }

            // Reject the marker-file-only stub baseline — until p1-baseline-contract lands,
            // baseline produces only `.strategy-baseline.md` which would ship a no-op PR.
            if (IsStubMarkerOnlyPatch(winner.Patch))
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id, succeeded: false);
                Logger.LogInformation(
                    "Strategy framework: winner {Strategy} produced stub marker-only patch; falling back to legacy path",
                    winner.StrategyId);
                return false;
            }

            // Apply the winning patch into the workspace's working tree (head-change safe).
            // Re-capture localHead right before apply — strategy evaluation may have taken 15+ min,
            // during which SyncWithMainAsync could rebase the branch (rewriting commit history).
            localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();

            // Primary: file-level copy from candidate worktree (avoids git apply brittleness).
            // Fallback: patch-based apply for checkpoint recovery or when worktree is unavailable.
            var winnerWorktreePath = outcome.Evaluation.WinnerWorktreePath;
            ApplyOutcome apply;
            if (!string.IsNullOrEmpty(winnerWorktreePath) && Directory.Exists(winnerWorktreePath))
            {
                apply = await _winnerApply.ApplyFromWorktreeAsync(
                    Workspace.RepoPath, branchName, localHead, winnerWorktreePath, ct);
                // Fall back to patch-based apply when file-copy fails for any recoverable reason:
                // - "overlap-N-files": live branch advanced, need 3-way merge
                // - "worktree-no-changes": judge may have mutated worktree HEAD or index.lock blocked staging
                if (!apply.Applied && (apply.FailureReason?.StartsWith("overlap") == true
                                    || apply.FailureReason == "worktree-no-changes"))
                {
                    Logger.LogInformation(
                        "File-copy failed ({Reason}); falling back to 3-way patch apply",
                        apply.FailureReason);
                    if (!string.IsNullOrWhiteSpace(winner.Patch))
                    {
                        apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
                    }
                    else
                    {
                        Logger.LogError(
                            "Strategy framework: winner {Strategy} worktree apply returned {Reason} AND " +
                            "winner.Patch is empty — no recovery path available. Both index.lock and " +
                            "judge HEAD mutation may have caused data loss.",
                            winner.StrategyId, apply.FailureReason);
                    }
                }
            }
            else
            {
                apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
            }

            // Dispose the winner worktree handle now — we've either copied files or given up
            if (outcome.Evaluation.WinnerWorktreeHandle is not null)
            {
                try { await outcome.Evaluation.WinnerWorktreeHandle.DisposeAsync(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose winner worktree handle"); }
            }

            if (!apply.Applied)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id, succeeded: false);
                Logger.LogWarning(
                    "Strategy framework: winner {Strategy} apply failed for task {TaskId}: {Reason}; falling back with winner context",
                    winner.StrategyId, task.Id, apply.FailureReason);
                _failedWinnerPatchContext = winner.Patch;
                return false;
            }

            // Force-add any patch files that .gitignore would exclude. LLMs frequently
            // generate .gitignore rules that accidentally exclude implementation files
            // (e.g., data.json, *.test.ts). Without this, `git add -A` silently skips
            // them and the PR ships with only the tracking marker file.
            await ForceAddIgnoredPatchFilesAsync(winner.Patch, ct);

            // Build-verify before committing — never push broken code.
            var wsConfig = Config.Workspace;
            var build = await BuildRunnerSvc.BuildAsync(
                Workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!build.Success)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id, succeeded: false);
                Logger.LogWarning(
                    "Strategy framework: winner {Strategy} build failed for task {TaskId}; reverting and falling back with winner context",
                    winner.StrategyId, task.Id);
                _failedWinnerPatchContext = winner.Patch;
                await Workspace.RevertUncommittedChangesAsync(ct);
                return false;
            }

            // Build commit message with sanitized strategy trailers.
            var trailers = new Dictionary<string, string>
            {
                [StrategyTrailers.StrategyKey] = SanitizeTrailerValue(winner.StrategyId),
                [StrategyTrailers.RunIdKey] = SanitizeTrailerValue(runId),
            };
            var tieBreak = outcome.Evaluation.TieBreakReason;
            if (!string.IsNullOrWhiteSpace(tieBreak))
                trailers[StrategyTrailers.TieBreakKey] = SanitizeTrailerValue(tieBreak);

            var subject = $"Implement {task.Name}";
            var commitBody = $"Generated by strategy '{winner.StrategyId}' (run {runId}).";
            var fullMessage = StrategyTrailers.Append($"{subject}\n\n{commitBody}\n", trailers);

            await Workspace.CommitAsync(fullMessage, ct);

            // Post-commit validation: ensure required runtime files (e.g. data.json) are tracked
            // and not gitignored. LLMs frequently generate .gitignore rules that exclude data.json.
            await ValidateRequiredRuntimeFilesAsync(branchName, ct);

            // Commit per-candidate preview screenshots BEFORE pushing so the dashboard
            // sees them on the first HeadSha it reads from the PR. Write files locally
            // and commit with git (not GitHub API) so everything ships in one push.
            var screenshotsWritten = false;
            foreach (var cand in outcome.Evaluation.Candidates)
            {
                try
                {
                    if (cand.ScreenshotBytes is null || cand.ScreenshotBytes.Length == 0)
                    {
                        Logger.LogWarning(
                            "Strategy {Strategy} has no screenshot bytes for PR #{PrNumber} — skipping. " +
                            "Check CandidateEvaluator logs for capture outcome.",
                            cand.StrategyId, pr.Number);
                        continue;
                    }

                    var screenshotRelPath = $".screenshots/pr-{pr.Number}-{cand.StrategyId}.png";
                    var screenshotFullPath = Path.Combine(Workspace.RepoPath, screenshotRelPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(screenshotFullPath)!);
                    await File.WriteAllBytesAsync(screenshotFullPath, cand.ScreenshotBytes, ct);
                    screenshotsWritten = true;
                    Logger.LogInformation(
                        "Wrote {Strategy} preview screenshot ({Size} bytes) to {Path}",
                        cand.StrategyId, cand.ScreenshotBytes.Length, screenshotRelPath);
                }
                catch (Exception screenshotEx)
                {
                    Logger.LogWarning(screenshotEx,
                        "Failed to write {Strategy} screenshot for PR #{PrNumber} — continuing with next candidate",
                        cand.StrategyId, pr.Number);
                }
            }

            if (screenshotsWritten)
            {
                try
                {
                    await RunGitCommandAsync(Workspace.RepoPath, "add -A .screenshots", ct);
                    await Workspace.CommitAsync(
                        $"📸 Strategy preview screenshots for PR #{pr.Number}", ct);
                }
                catch (Exception commitEx)
                {
                    Logger.LogWarning(commitEx,
                        "Failed to commit screenshot files for PR #{PrNumber} — screenshots won't appear in dashboard",
                        pr.Number);
                }
            }

            // Publish: push code + screenshots + data.json fix all in one push.
            // Treat push failures as PUBLISH errors (NOT generation errors).
            // After a successful commit we must NEVER revert or fall back to legacy —
            // doing so throws away perfectly-good generated code. On push failure, log
            // the error, leave the commit in place (next SE loop will push again), and
            // return true so caller doesn't run legacy code-gen on top of our committed work.
            try
            {
                await Workspace.PushAsync(branchName, ct);
            }
            catch (Exception pushEx)
            {
                Logger.LogError(pushEx,
                    "Strategy framework: committed winner {Strategy} for task {TaskId} but push to {Branch} failed — " +
                    "commit preserved locally; SE outer loop will retry push. Will NOT revert or fall back to legacy.",
                    winner.StrategyId, task.Id, branchName);
                // Return true: generation succeeded and is committed. Do NOT retry generation.
                return true;
            }

            // Write winner-strategy marker into PR body so dashboard can identify which tile is the winner.
            try
            {
                var currentBody = pr.Body ?? "";
                if (!currentBody.Contains("winner-strategy:", StringComparison.OrdinalIgnoreCase))
                {
                    var markerComment = $"\n\n<!-- winner-strategy: {winner.StrategyId} -->";
                    await PrService.UpdateAsync(pr.Number, body: currentBody + markerComment, ct: ct);
                }
            }
            catch (Exception markerEx)
            {
                Logger.LogDebug(markerEx, "Failed to write winner-strategy marker to PR #{PrNumber}", pr.Number);
            }

            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id,
                succeeded: true, winnerStrategy: winner.StrategyId);

            // Stash the winner candidate so FinalizeReadyForReviewAsync can attach its
            // proven media (screenshots, GIF, video) to the PR ready-for-review comment
            // instead of capturing a fresh screenshot that may differ from what was evaluated.
            _winnersByPr[pr.Number] = winner;

            Logger.LogInformation(
                "Strategy framework shipped winner {Strategy} for task {TaskId} on PR #{PrNumber}",
                winner.StrategyId, task.Id, pr.Number);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Strategy framework path threw for task {TaskId}; falling back to legacy code-gen", task.Id);
            // Best-effort unregister — use a fallback runId since the original may not be in scope
            try
            {
                var fallbackRunId = StateStore.LastBootUtc != DateTime.MinValue
                    ? StateStore.LastBootUtc.ToString("yyyyMMddTHHmmssZ")
                    : "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                _strategyStepBridge?.UnregisterTask(fallbackRunId, task.Id, succeeded: false);
            }
            catch { /* bridge cleanup must not prevent fallback */ }
            // Only revert UNCOMMITTED changes — never destroy a committed winner.
            // The revert only runs here (pre-commit failure path).
            try { await Workspace.RevertUncommittedChangesAsync(ct); } catch { }
            return false;
        }
        finally
        {
            // Always release the strategy claim so other agents can evaluate if needed.
            if (task.IssueNumber.HasValue)
                ClaimRegistry?.ReleaseStrategy(task.IssueNumber.Value);
        }
    }

    private static int MapComplexityToInt(string? complexity)
        => complexity?.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            _ => 1,
        };

    private static bool LooksLikeWebTask(string techStack, string? name, string? description)
    {
        var blob = $"{techStack} {name} {description}".ToLowerInvariant();
        return blob.Contains("blazor") || blob.Contains("aspnet") || blob.Contains("asp.net")
            || blob.Contains("react") || blob.Contains("angular") || blob.Contains("vue")
            || blob.Contains("html") || blob.Contains("ui") || blob.Contains("dashboard")
            || blob.Contains("page") || blob.Contains("frontend");
    }

    /// <summary>
    /// After committing generated code, validate that runtime files are present.
    /// This is project-agnostic: it scans for *.sample.*, *.template.*, *.example.*
    /// files and ensures the corresponding actual file exists. Also fixes .gitignore
    /// rules that exclude files matching sample/template counterparts.
    /// </summary>
    /// <summary>
    /// After applying the winning strategy patch, check if any patch files are
    /// gitignored. If so, force-add them so <c>git add -A</c> in CommitAsync
    /// doesn't silently skip them — which would produce a PR with only the
    /// tracking marker file and no implementation code.
    /// </summary>
    private async Task ForceAddIgnoredPatchFilesAsync(string patch, CancellationToken ct)
    {
        if (Workspace is null) return;

        try
        {
            var patchFiles = PatchAnalyzer.Parse(patch)
                .Where(f => f.Type != FileChangeType.Deleted)
                .Select(f => f.Path)
                .ToList();

            if (patchFiles.Count == 0) return;

            var repoPath = Workspace.RepoPath;
            var ignoredFiles = new List<string>();

            foreach (var file in patchFiles)
            {
                var fullPath = Path.Combine(repoPath, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) continue;

                // Check if git would ignore this file
                var psi = new System.Diagnostics.ProcessStartInfo("git", $"check-ignore -q \"{file}\"")
                {
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) continue;
                await proc.WaitForExitAsync(ct);

                // Exit 0 = file IS ignored; exit 1 = file is NOT ignored
                if (proc.ExitCode == 0)
                    ignoredFiles.Add(file);
            }

            if (ignoredFiles.Count > 0)
            {
                Logger.LogWarning(
                    "Strategy patch: {Count} file(s) are gitignored — force-adding: {Files}",
                    ignoredFiles.Count, string.Join(", ", ignoredFiles));

                foreach (var file in ignoredFiles)
                {
                    var addPsi = new System.Diagnostics.ProcessStartInfo("git", $"add -f \"{file}\"")
                    {
                        WorkingDirectory = repoPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var addProc = System.Diagnostics.Process.Start(addPsi);
                    if (addProc is not null)
                        await addProc.WaitForExitAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check/force-add ignored patch files — continuing");
        }
    }

    private async Task ValidateRequiredRuntimeFilesAsync(string branchName, CancellationToken ct)
    {
        if (Workspace is null) return;

        try
        {
            var repoPath = Workspace.RepoPath;

            // Phase 1: Scan for sample/template/example files whose actual counterpart is missing.
            // E.g. data.sample.json exists → data.json should too; config.template.yaml → config.yaml.
            await MaterializeMissingSampleFilesAsync(repoPath, ct);

            // Phase 2: Check for .gitignore rules that exclude files which have sample counterparts.
            await FixGitignoreForMaterializedFilesAsync(repoPath, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to validate required runtime files — continuing");
        }
    }

    /// <summary>
    /// Scans the workspace for *.sample.*, *.template.*, *.example.* files.
    /// For each one, if the corresponding actual file (without the .sample/.template/.example
    /// suffix) is missing, copies the sample to create it. Commits all created files.
    /// This is fully project-agnostic — works for data.json, config.yaml, .env, etc.
    /// </summary>
    private async Task MaterializeMissingSampleFilesAsync(string repoPath, CancellationToken ct)
    {
        var suffixes = new[] { ".sample", ".template", ".example" };
        var createdFiles = new List<string>();

        // Find all sample/template/example files in non-test directories
        var allFiles = Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(repoPath, f);
                // Skip test dirs, bin/obj, .git, node_modules, .candidates
                return !rel.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                    && !rel.StartsWith(".git" + Path.DirectorySeparatorChar)
                    && !rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    && !rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !rel.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                    && !rel.Contains($"{Path.DirectorySeparatorChar}.candidates{Path.DirectorySeparatorChar}")
                    && !rel.Contains($"{Path.DirectorySeparatorChar}.candidates-eval{Path.DirectorySeparatorChar}");
            })
            .ToList();

        foreach (var sampleFile in allFiles)
        {
            var fileName = Path.GetFileName(sampleFile);

            // Check if this file matches the pattern: name.sample.ext or name.template.ext
            foreach (var suffix in suffixes)
            {
                // Pattern: "data.sample.json" → actual file "data.json"
                var idx = fileName.IndexOf(suffix + ".", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    // Also handle: "data.json.sample" → actual file "data.json"
                    if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var actualName = fileName[..^suffix.Length];
                        if (string.IsNullOrEmpty(actualName)) continue;
                        var actualPath = Path.Combine(Path.GetDirectoryName(sampleFile)!, actualName);
                        if (!File.Exists(actualPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
                            File.Copy(sampleFile, actualPath);
                            createdFiles.Add(Path.GetRelativePath(repoPath, actualPath));
                            Logger.LogInformation(
                                "Materialized {Actual} from {Sample} — LLM created sample but not the runtime file",
                                Path.GetRelativePath(repoPath, actualPath),
                                Path.GetRelativePath(repoPath, sampleFile));
                        }
                    }
                    continue;
                }

                // "data.sample.json" → "data.json"
                var actualFileName = fileName[..idx] + fileName[(idx + suffix.Length)..];
                if (string.IsNullOrEmpty(actualFileName) || actualFileName == ".") continue;

                var actualFilePath = Path.Combine(Path.GetDirectoryName(sampleFile)!, actualFileName);
                if (!File.Exists(actualFilePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(actualFilePath)!);
                    File.Copy(sampleFile, actualFilePath);
                    createdFiles.Add(Path.GetRelativePath(repoPath, actualFilePath));
                    Logger.LogInformation(
                        "Materialized {Actual} from {Sample} — LLM created sample but not the runtime file",
                        Path.GetRelativePath(repoPath, actualFilePath),
                        Path.GetRelativePath(repoPath, sampleFile));
                }
                break; // Only match first suffix pattern per file
            }
        }

        if (createdFiles.Count > 0)
        {
            await RunGitCommandAsync(repoPath, "add -A", ct);
            var fileList = string.Join(", ", createdFiles.Take(5));
            if (createdFiles.Count > 5) fileList += $" (+{createdFiles.Count - 5} more)";
            await Workspace.CommitAsync(
                $"fix: create {createdFiles.Count} missing runtime file(s) from samples\n\n" +
                $"LLM generated sample/template files but did not create the actual runtime files.\n" +
                $"Materialized: {fileList}", ct);
        }
    }

    /// <summary>
    /// Checks .gitignore rules for any files that were just materialized from samples.
    /// If a materialized file would be ignored, fixes the .gitignore.
    /// </summary>
    private async Task FixGitignoreForMaterializedFilesAsync(string repoPath, CancellationToken ct)
    {
        // Get list of untracked files that should be tracked
        var statusResult = await RunGitCommandAsync(repoPath, "status --porcelain", ct);
        if (statusResult.ExitCode != 0) return;

        var untrackedFiles = statusResult.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("??") || line.StartsWith("!!"))
            .Select(line => line[3..].Trim().Trim('"'))
            .ToList();

        bool anyFixed = false;
        foreach (var file in untrackedFiles)
        {
            var checkResult = await RunGitCommandAsync(repoPath, $"check-ignore -v \"{file}\"", ct);
            if (checkResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(checkResult.StdOut))
            {
                Logger.LogWarning(
                    "File {File} is excluded by .gitignore ({Details}). Auto-fixing.",
                    file, checkResult.StdOut.Trim());
                await FixGitignoreForRequiredFileAsync(repoPath, Path.GetFileName(file), ct);
                anyFixed = true;
            }
        }

        if (anyFixed)
        {
            await RunGitCommandAsync(repoPath, "add -A", ct);
            await Workspace.CommitAsync(
                "fix: un-ignore runtime files excluded by LLM-generated .gitignore", ct);
        }
    }

    /// <summary>
    /// Remove .gitignore rules that exclude a required file. Handles common patterns
    /// like **/data.json, data.json, and /data.json.
    /// </summary>
    private async Task FixGitignoreForRequiredFileAsync(string repoPath, string fileName, CancellationToken ct)
    {
        // Find .gitignore files in the repo
        var gitignorePaths = Directory.GetFiles(repoPath, ".gitignore", SearchOption.AllDirectories);

        foreach (var fullPath in gitignorePaths)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(fullPath, ct);
                var filtered = lines.Where(line =>
                {
                    var trimmed = line.Trim();
                    // Remove lines that ignore this specific file
                    if (trimmed == fileName || trimmed == $"/{fileName}" ||
                        trimmed == $"**/{fileName}" || trimmed == $"*/{fileName}" ||
                        trimmed == $"*{fileName}")
                        return false;
                    return true;
                }).ToArray();

                if (filtered.Length < lines.Length)
                {
                    // Add a negation rule to explicitly allow the file
                    var withNegation = filtered.Append($"!{fileName}").Append($"!**/{fileName}").ToArray();
                    await File.WriteAllLinesAsync(fullPath, withNegation, ct);
                    Logger.LogInformation(
                        "Fixed {GitignorePath}: removed {Count} exclusion rule(s) for {File} and added negation",
                        fullPath, lines.Length - filtered.Length, fileName);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to fix {GitignorePath} for required file {File}",
                    fullPath, fileName);
            }
        }
    }

    /// <summary>
    /// Run a git command in the specified directory. Returns exit code and stdout.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut)> RunGitCommandAsync(
        string workDir, string arguments, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            return (-1, "");

        // Read stdout and stderr CONCURRENTLY to avoid pipe deadlock.
        // Redirecting stderr without reading it causes the 4KB buffer to fill,
        // blocking the child process and hanging stdout ReadToEndAsync forever.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await stdoutTask;
        _ = await stderrTask; // drain stderr to prevent deadlock
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout);
    }

    /// <summary>
    /// Detect the Phase-1 stub baseline patch (only modifies <c>.strategy-baseline.md</c>).
    /// Returns true when the patch contains no other tracked file changes.
    /// </summary>
    private static bool IsStubMarkerOnlyPatch(string patch)
    {
        if (string.IsNullOrEmpty(patch)) return true;
        var sawAnyDiff = false;
        foreach (var line in patch.Split('\n'))
        {
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal)) continue;
            sawAnyDiff = true;
            // Lines look like: diff --git a/path b/path
            var parts = line.Split(' ');
            if (parts.Length < 4) return false;
            var aPath = parts[2].StartsWith("a/", StringComparison.Ordinal) ? parts[2][2..] : parts[2];
            if (!aPath.EndsWith(".strategy-baseline.md", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return sawAnyDiff;
    }

    /// <summary>
    /// Collapse newlines and tabs into spaces, trim, and cap length so the value is a
    /// safe single-line scalar for <see cref="StrategyTrailers.BuildBlock"/> (which throws on CR/LF).
    /// </summary>
    private static string SanitizeTrailerValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
            else if (c < 0x20) continue;
            else sb.Append(c);
        }
        var s = sb.ToString().Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>
    /// Periodically re-validate dependency gate for all in-progress tasks.
    /// Emits a Warning if any assigned/in-progress task has unmet dependencies,
    /// surfacing dep-gate bypasses caused by regex mismatch or across-restart state.
    /// Leader-only; runs at most once per DepRecheckInterval.
    /// </summary>
    private async Task WarnIfInProgressTasksDepsUnmetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastDepRecheckTime < DepRecheckInterval)
            return;
        _lastDepRecheckTime = DateTime.UtcNow;

        try
        {
            var inProgressTasks = _taskManager.Tasks
                .Where(t => t.Status is "InProgress" or "Assigned")
                .ToList();

            if (inProgressTasks.Count == 0)
                return;

            foreach (var task in inProgressTasks)
            {
                if (task.IssueNumber is null)
                    continue;

                // Re-fetch issue body for current dep state — live API check, not in-memory
                try
                {
                    var issue = await WorkItemService!.GetAsync(task.IssueNumber.Value, ct);
                    if (issue is null)
                        continue;

                    var satisfied = await AreDependenciesSatisfiedAsync(issue.Body, ct);
                    if (!satisfied)
                    {
                        var depNums = EngineeringTaskIssueManager.ParseDependencies(issue.Body, Logger);
                        Logger.LogWarning(
                            "[DepRecheck] In-progress task #{IssueNumber} ({Name}) has UNMET dependencies ({Deps}) — " +
                            "this task should not be in-progress yet. Possible dep-gate bypass on assignment. " +
                            "PR #{PrNumber} may be working on dependent code before prerequisites are merged.",
                            task.IssueNumber, task.Name,
                            depNums.Count > 0
                                ? string.Join(", ", depNums.Select(d => $"#{d}"))
                                : "(parsing failed — 'depends on' text detected but no #N found)",
                            task.PullRequestNumber);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogDebug(ex, "[DepRecheck] Error checking task #{IssueNumber}", task.IssueNumber);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "[DepRecheck] Error during dep recheck scan");
        }
    }

    /// <summary>
    /// Periodically scan GitHub for open PRs with 'ready-for-review' label that aren't
    /// in the review queue. This catches PRs whose ReviewRequestMessage was lost on restart.
    /// </summary>
    private async Task DiscoverUnreviewedEngineerPRsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastReviewDiscovery < ReviewDiscoveryInterval)
            return;
        _lastReviewDiscovery = DateTime.UtcNow;

        try
        {
            var openPRs = await GetCachedOpenPRsAsync(ct);
            var discovered = 0;

            foreach (var pr in openPRs)
            {
                // Only ready-for-review PRs
                if (!pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!IsCurrentRunScopePr(pr))
                    continue;

                // Skip PRs owned by this PE (use colon delimiter to prevent "SoftwareEngineer" matching "SoftwareEngineer 1:")
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if already reviewed or already queued or claimed by another PE
                if (_reviewedPrNumbers.ContainsKey(pr.Number))
                    continue;
                if (s_activeReviews.ContainsKey(pr.Number))
                    continue;

                // Add to review queue
                _reviewQueue.Enqueue(pr.Number);
                discovered++;
                Logger.LogInformation(
                    "SE discovered unreviewed PR #{Number}: {Title} (ready-for-review)",
                    pr.Number, pr.Title);
            }

            if (discovered > 0)
                Logger.LogInformation("SE discovered {Count} unreviewed engineer PRs", discovered);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to discover unreviewed engineer PRs");
        }
    }

    private async Task ReviewEngineerPRsAsync(CancellationToken ct)
    {
        try
        {
            var prNumbersToReview = new HashSet<int>();
            while (_reviewQueue.TryDequeue(out var prNumber))
                prNumbersToReview.Add(prNumber);

            if (prNumbersToReview.Count == 0)
                return;

            UpdateStatus(AgentStatus.Working, $"🔍 Processing review queue ({prNumbersToReview.Count} PRs pending)");

            await Parallel.ForEachAsync(prNumbersToReview,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (prNumber, token) =>
            {
                if (_reviewedPrNumbers.ContainsKey(prNumber))
                    return;

                // Atomic cross-PE claim: prevent multiple PE instances from reviewing
                // the same PR simultaneously. Only one PE can claim a PR at a time.
                if (!s_activeReviews.TryAdd(prNumber, (Identity.Id, DateTime.UtcNow)))
                {
                    Logger.LogDebug("PR #{Number} already claimed by another PE, skipping", prNumber);
                    return;
                }

                try
                {

                // Cross-PE dedup: if ANY PE has already reviewed this PR AND no rework
                // happened since, skip it. This prevents SE1, SE2, SE3 from each posting a
                // separate CHANGES REQUESTED on the same PR within minutes of each other
                // (observed on PR #1259 in run cb24a668). Uses RoleNeedsReviewAsync which
                // treats "SoftwareEngineer", "SoftwareEngineer 1", "SoftwareEngineer 2" as
                // the same role — the previous NeedsReviewFromAsync("SoftwareEngineer")
                // call did exact-match comparison and never recognized numbered SE reviews.
                if (!_forceApprovalPrs.ContainsKey(prNumber)
                    && await HasAnyPeReviewedAsync(prNumber, ct)
                    && !await PrWorkflow.RoleNeedsReviewAsync(prNumber, "SoftwareEngineer", ct))
                {
                    _reviewedPrNumbers.TryAdd(prNumber, 0);
                    return;
                }

                // Skip NeedsReviewFromAsync for force-approval — there's no new rework,
                // but we need to approve to unblock the engineer.
                if (!_forceApprovalPrs.ContainsKey(prNumber)
                    && !await PrWorkflow.RoleNeedsReviewAsync(prNumber, "SoftwareEngineer", ct))
                {
                    _reviewedPrNumbers.TryAdd(prNumber, 0);
                    return;
                }

                var pr = (await PrService.GetAsync(prNumber, ct))?.ToAgentPR();
                if (pr is null)
                    return;

                // BUG FIX: Skip PRs that were merged/closed between discovery and review.
                // After merge, the head branch is deleted — reading files from it returns empty,
                // causing the reviewer to falsely claim "zero files in diff".
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.TryAdd(prNumber, 0);
                    Logger.LogDebug("PR #{Number} is no longer open (state: {State}), skipping review",
                        prNumber, pr.State);
                    return;
                }

                // Skip our own PRs (use colon delimiter for multi-PE correctness)
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.TryAdd(prNumber, 0);
                    return;
                }

                Logger.LogInformation("SE reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"🔍 Reviewing PR #{pr.Number}: {pr.Title}");

                // BUG FIX: Force-approve after max rework cycles to prevent infinite loops.
                // Only actually force-approve if we're a required reviewer for this PR —
                // otherwise we'd create redundant approval comments.
                bool approved;
                string? reviewBody;
                if (_forceApprovalPrs.ContainsKey(prNumber))
                {
                    _forceApprovalPrs.TryRemove(prNumber, out _);
                    var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                    var requiredReviewers = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                    if (!requiredReviewers.Any(r => r.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.LogInformation("SE is not a required reviewer for PR #{Number} — skipping force-approval", prNumber);
                        _reviewedPrNumbers.TryAdd(prNumber, 0);
                        return;
                    }
                    // Idempotency: don't post a duplicate force-approval comment if this PE
                    // (or any PE) has already force-approved this PR. PR #1216 in the
                    // 2026-05-08 run got "Force-approving after maximum rework cycles" 3x
                    // because each FinalApproval message re-added to _forceApprovalPrs and
                    // there was no check for an already-posted approval.
                    var existingComments = await ReviewService.GetCommentsAsync(prNumber, ct);
                    var alreadyForceApproved = existingComments.Any(c =>
                        (c.Body.Contains("[SoftwareEngineer]", StringComparison.OrdinalIgnoreCase) ||
                         c.Body.Contains("[SoftwareEngineer ", StringComparison.OrdinalIgnoreCase)) &&
                        c.Body.Contains("Force-approving", StringComparison.OrdinalIgnoreCase));
                    if (alreadyForceApproved)
                    {
                        Logger.LogInformation("PR #{Number} already has a SoftwareEngineer force-approval comment — skipping duplicate", prNumber);
                        _reviewedPrNumbers.TryAdd(prNumber, 0);
                        return;
                    }
                    approved = true;
                    reviewBody = $"Force-approving after maximum rework cycles. " +
                        $"The engineer has made best-effort improvements across multiple iterations.";
                }
                else
                {
                    // Check if the author actually committed new code since our last review.
                    // Prevents pointless re-reviews of unchanged code (wastes AI calls and creates duplicate feedback).
                    var hasNewCommits = await PrWorkflow.HasNewCommitsSinceReviewAsync(prNumber, "SoftwareEngineer", ct);
                    if (!hasNewCommits)
                    {
                        Logger.LogDebug("No new commits on PR #{Number} since last review — skipping until author pushes fixes", prNumber);
                        return; // Don't re-review or auto-approve unchanged code
                    }
                    else
                    {
                        IReadOnlyList<PlatformInlineComment> inlineComments;
                        (approved, reviewBody, inlineComments) = await EvaluatePrQualityAsync(pr, ct);

                        // Submit inline file comments if we have any and the feature is enabled.
                        // When inline comments are posted, the review body is included as the
                        // review summary — don't post a duplicate standalone comment.
                        if (inlineComments.Count > 0 && Config.Review.EnableInlineComments)
                        {
                            await SubmitPlatformInlineCommentsAsync(prNumber, reviewBody ?? "", approved, inlineComments, ct);
                            reviewBody = null; // suppress duplicate standalone comment
                        }
                    }
                }

                if (reviewBody is null)
                    return;

                // === Gate: PRReviewApproval — human reviews before PE approval ===
                if (approved)
                {
                    var prGateResult = await WaitForHumanGateAsync(
                        GateIds.PRReviewApproval,
                        $"SE ready to approve PR #{prNumber}",
                        prNumber, ct: ct);

                    // Human rejected — treat as rework request
                    if (prGateResult.Decision == GateDecision.Rejected)
                    {
                        Logger.LogInformation("Human rejected PRReviewApproval for PR #{Number}: {Feedback}", prNumber, prGateResult.Feedback);
                        approved = false;
                        reviewBody = $"**Human reviewer requested changes:**\n\n{prGateResult.Feedback}";
                    }
                }

                if (approved)
                {
                    // Submit formal GitHub APPROVE only if agents have separate accounts
                    if (Config.Review.EnableFormalReviews)
                    {
                        try
                        {
                            await ReviewService.AddReviewAsync(prNumber,
                                $"✅ **[{Identity.DisplayName}] APPROVED**\n\n{reviewBody}", "APPROVE", ct);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex,
                                "Formal APPROVE review failed on PR #{Number} (expected in single-PAT setup)",
                                prNumber);
                        }
                    }

                    // Resolve any prior SE inline review threads
                    await ResolveSEReviewThreadsAsync(prNumber, ct);

                    var requireTests = Config.Workspace.IsInlineTestWorkflow;
                    // Defer merge when FinalPRApproval gate requires human — we'll gate before merging
                    var deferMerge = GateCheck.RequiresHuman(GateIds.FinalPRApproval);
                    var result = await PrWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "SoftwareEngineer", reviewBody, requireTests, deferMerge, ct);

                    // Signal TE that code review is complete: add architect-approved label
                    // when the Architect is not a required reviewer (specialist PRs).
                    // Must happen AFTER ApproveAndMaybeMergeAsync (which posts the approval
                    // comment) to avoid a race where TE starts before the comment exists.
                    if (result != MergeAttemptResult.Merged)
                    {
                        var prAuthorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                        var prRequiredReviewers = PullRequestWorkflow.GetRequiredReviewers(prAuthorRole);
                        if (!prRequiredReviewers.Any(r => r.Contains("Architect", StringComparison.OrdinalIgnoreCase))
                            && !pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                        {
                            await PrService.AddLabelAsync(pr.Number, PullRequestWorkflow.Labels.ArchitectApproved, ct);
                            Logger.LogInformation(
                                "SE added architect-approved to PR #{Number} (SE is code reviewer, Architect not required)",
                                pr.Number);

                            // Notify TE so it can start testing immediately instead of waiting for next poll
                            await Core.MessageBus.PublishAsync(new PrApprovedMessage
                            {
                                FromAgentId = Identity.Id,
                                ToAgentId = "*",
                                MessageType = nameof(PrApprovedMessage),
                                PrNumber = pr.Number,
                                PrTitle = pr.Title,
                                ApproverAgent = Identity.DisplayName
                            }, ct);
                        }
                    }

                    // All reviewers approved and merge was deferred for human gate
                    if (result == MergeAttemptResult.ReadyToMerge)
                    {
                        // === Gate: FinalPRApproval — human reviews before merge ===
                        var finalGateResult = await WaitForHumanGateAsync(
                            GateIds.FinalPRApproval,
                            $"PR #{pr.Number} approved by all reviewers, ready for final merge",
                            pr.Number, ct: ct);

                        if (finalGateResult.Decision == GateDecision.Rejected)
                        {
                            // Human rejected — request changes with their feedback (unlimited rework)
                            Logger.LogInformation("Human rejected FinalPRApproval for PR #{Number}: {Feedback}", prNumber, finalGateResult.Feedback);
                            LogActivity("task", $"🔄 Human rejected merge of PR #{pr.Number} — requesting changes");
                            var feedback = $"**Human reviewer rejected merge:**\n\n{finalGateResult.Feedback}";
                            await PrWorkflow.RequestChangesAsync(pr.Number, "HumanReviewer", feedback, ct);
                            await MessageBus.PublishAsync(new ChangesRequestedMessage
                            {
                                FromAgentId = Identity.Id,
                                ToAgentId = "*",
                                MessageType = "ChangesRequested",
                                PrNumber = pr.Number,
                                PrTitle = pr.Title,
                                ReviewerAgent = "HumanReviewer",
                                Feedback = feedback
                            }, ct);
                            // Don't add to _reviewedPrNumbers so PE re-reviews after rework
                            return;
                        }

                        // Human approved — proceed with merge
                        var mergeResult = await PrWorkflow.MergeApprovedTestedPRAsync(
                            pr.Number, "SoftwareEngineer", ct);
                        if (mergeResult == MergeAttemptResult.Merged)
                        {
                            Logger.LogInformation("SE merged PR #{Number} after human approval", pr.Number);
                            LogActivity("task", $"✅ Approved and merged PR #{pr.Number}: {pr.Title} (human approved)");
                            if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                                await MarkEngineerTaskDoneAsync(pr, ct);
                            // Close linked work items via platform abstraction (ADO parity)
                            if (_mergeCloseout is not null)
                                await _mergeCloseout.CloseLinkedWorkItemsAsync(pr.Number, ct);
                            await RememberAsync(MemoryType.Action,
                                $"Reviewed and approved+merged PR #{pr.Number}: {pr.Title}", ct: ct);
                        }
                        else if (mergeResult == MergeAttemptResult.ConflictBlocked)
                        {
                            Logger.LogWarning("SE approved PR #{Number} but merge blocked by conflicts after human gate", pr.Number);
                            LogActivity("task", $"⚠️ PR #{pr.Number} blocked by merge conflicts — closing and recreating");
                            await TryCloseAndRecreatePRAsync(pr, ct);
                        }
                        else if (mergeResult == MergeAttemptResult.SecurityBlocked)
                        {
                            Logger.LogWarning(
                                "SE: human approved PR #{Number} but merge is security-blocked. Leaving PR open for security review.",
                                pr.Number);
                            LogActivity("task",
                                $"🛑 PR #{pr.Number} is security-blocked — SecurityAuditor findings must be resolved");
                        }
                    }
                    else if (result == MergeAttemptResult.Merged)
                    {
                        Logger.LogInformation("SE approved and merged PR #{Number}", pr.Number);
                        LogActivity("task", $"✅ Approved and merged PR #{pr.Number}: {pr.Title}");

                        // Mark the engineering task Done via issue manager (skip test PRs)
                        if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                            await MarkEngineerTaskDoneAsync(pr, ct);
                        // Close linked work items via platform abstraction (ADO parity)
                        if (_mergeCloseout is not null)
                            await _mergeCloseout.CloseLinkedWorkItemsAsync(pr.Number, ct);

                        await RememberAsync(MemoryType.Action,
                            $"Reviewed and approved+merged PR #{pr.Number}: {pr.Title}", ct: ct);
                    }
                    else if (result == MergeAttemptResult.ConflictBlocked)
                    {
                        Logger.LogWarning("SE approved PR #{Number} but merge blocked by conflicts, attempting close-and-recreate", pr.Number);
                        LogActivity("task", $"⚠️ PR #{pr.Number} blocked by merge conflicts — closing and recreating");
                        await TryCloseAndRecreatePRAsync(pr, ct);
                    }
                    else if (result == MergeAttemptResult.SecurityBlocked)
                    {
                        // Do NOT close-and-recreate — the PR must stay open for human inspection of security findings.
                        Logger.LogWarning(
                            "SE approved PR #{Number} but merge is security-blocked. SecurityAuditor findings must be resolved. Leaving PR open.",
                            pr.Number);
                        LogActivity("task",
                            $"🛑 PR #{pr.Number} is security-blocked — SecurityAuditor findings must be resolved before merge");
                    }
                    else if (result == MergeAttemptResult.AwaitingTests)
                    {
                        Logger.LogInformation("SE approved PR #{Number}, waiting for Test Engineer to add tests", pr.Number);
                        LogActivity("task", $"✅ Approved PR #{pr.Number}: {pr.Title} — awaiting tests");
                    }
                    else
                    {
                        Logger.LogInformation("SE approved PR #{Number}, waiting for PM approval", pr.Number);
                        LogActivity("task", $"✅ Approved PR #{pr.Number}, waiting for PM approval");
                    }
                }
                else
                {
                    await PrWorkflow.RequestChangesAsync(pr.Number, Identity.DisplayName, reviewBody, ct);
                    Logger.LogInformation("SE requested changes on PR #{Number}", pr.Number);
                    LogActivity("task", $"❌ Requested changes on PR #{pr.Number}: {pr.Title}");

                    await RememberAsync(MemoryType.Decision,
                        $"Requested changes on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

                    await MessageBus.PublishAsync(new ChangesRequestedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ChangesRequested",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewerAgent = Identity.DisplayName,
                        Feedback = reviewBody
                    }, ct);
                }

                _reviewedPrNumbers.TryAdd(prNumber, 0);

                } // end try
                finally
                {
                    s_activeReviews.TryRemove(prNumber, out _);
                }
            });

            // Reset status after review loop completes
            if (prNumbersToReview.Count > 0)
                UpdateStatus(AgentStatus.Working, $"✅ Review round complete: {prNumbersToReview.Count} PRs processed");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review engineer PRs");
        }
    }

    /// <summary>
    /// Inline test workflow: find PRs with both 'approved' and 'tests-added' labels,
    /// do a lightweight review of the test code, and merge.
    /// Engineers wrote the code (already approved by PE). TE added tests.
    /// PE does a quick test-quality check then merges. PM/Architect don't re-review.
    /// </summary>
    private async Task MergeTestedPRsAsync(CancellationToken ct)
    {
        try
        {
            var openPRs = (await GetCachedOpenPRsAsync(ct))
                .ToAgentPRs()
                .Where(IsCurrentRunScopePr)
                .ToList();

            // Inline workflow + TE enabled: require both pm-approved AND tests-added labels
            // Non-inline workflow: require pm-approved AND architect-approved. tests-added is NOT
            //   required because in non-inline mode, the TE creates a separate test PR that gets
            //   merged independently — the feature PR's merge gate is only review labels.
            // TE-disabled (inline): TE never applies tests-added, so requiring it would deadlock.
            //   Fall back to pm-approved + architect-approved.
            var requireTestsAdded = Config.Workspace.IsInlineTestWorkflow && Config.Review.TestEngineerReviews;
            var candidatePRs = requireTestsAdded
                ? openPRs.Where(pr =>
                    pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase) &&
                    pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase)).ToList()
                : openPRs.Where(pr =>
                    pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase) &&
                    pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase)).ToList();

            if (candidatePRs.Count == 0)
                return;

            UpdateStatus(AgentStatus.Working, $"📊 Collecting {candidatePRs.Count} approved PRs for integration");

            foreach (var pr in candidatePRs)
            {
                if (ct.IsCancellationRequested) break;

                // Skip if already successfully merged at this same head SHA
                // (the existing PR.HeadSha will differ on any new commit, naturally
                // invalidating the dedup). 2026-05-12 fix (se-leader-merge-skip-sticky-dedup):
                // previous version was HashSet<int> that permanently skipped any PR after first
                // encounter regardless of whether the merge actually succeeded. Now we only
                // skip if we've VERIFIED a merge at this SHA — re-attempts on the same PR
                // for any reason (rate-limit pause, transient error) are correctly retried.
                if (_mergedTestedPrNumbersWithSha.TryGetValue(pr.Number, out var prevSha) &&
                    string.Equals(prevSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogDebug("Skipping PR #{Number} — already successfully merged at head SHA {Sha}", pr.Number, pr.HeadSha);
                    continue;
                }

                Logger.LogInformation(
                    "Found pm-approved+tested PR #{Number}: {Title} — reviewing tests and merging",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing tests on PR #{pr.Number}");

                // Lightweight test review: check that TE actually added test files
                var changedFiles = await PrService.GetChangedFilesAsync(pr.Number, ct);
                var testFiles = changedFiles
                    .Where(f => f.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Test", StringComparison.Ordinal))
                    .ToList();

                if (testFiles.Count == 0)
                {
                    // TE processed this PR but tests couldn't be built — still merge if PM approved
                    Logger.LogInformation(
                        "PR #{Number} has tests-added+pm-approved but no test files (TE build failed) — merging anyway",
                        pr.Number);
                    if (await ShouldPostTestsReviewedAsync(pr.Number, ct))
                    {
                        await ReviewService.AddCommentAsync(pr.Number,
                            "✅ **[SoftwareEngineer] Merge Review** — TE attempted testing but build failed. " +
                            "PM has approved the code. Proceeding with merge.", ct);
                    }
                }
                else
                {
                    // Post test review approval (idempotency-guarded — see ShouldPostTestsReviewedAsync).
                    // Multi-PE merge race used to fire this comment 2-3 times in the same second on
                    // the same PR (#1257 fired 3x at 21:31:36-37, #1261 fired 2x, #1260 fired 2x in
                    // run cb24a668). The guard re-fetches comments and skips when an SE already
                    // posted "Tests Reviewed" within the recent window.
                    if (await ShouldPostTestsReviewedAsync(pr.Number, ct))
                    {
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"✅ **[SoftwareEngineer] Tests Reviewed** — {testFiles.Count} test file(s) verified. Merging.", ct);
                    }
                }

                // === Gate: FinalPRApproval — human reviews before final merge ===
                var finalGateResult = await WaitForHumanGateAsync(
                    GateIds.FinalPRApproval,
                    $"PR #{pr.Number} has passed all reviews and tests, ready for final merge",
                    pr.Number, ct: ct);

                if (finalGateResult.Decision == GateDecision.Rejected)
                {
                    // Human rejected — request changes with their feedback (unlimited rework)
                    Logger.LogInformation("Human rejected FinalPRApproval for tested PR #{Number}: {Feedback}",
                        pr.Number, finalGateResult.Feedback);
                    LogActivity("task", $"🔄 Human rejected merge of PR #{pr.Number} — requesting changes");
                    var feedback = $"**Human reviewer rejected merge:**\n\n{finalGateResult.Feedback}";
                    await PrWorkflow.RequestChangesAsync(pr.Number, "HumanReviewer", feedback, ct);
                    await MessageBus.PublishAsync(new ChangesRequestedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ChangesRequested",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewerAgent = "HumanReviewer",
                        Feedback = feedback
                    }, ct);
                    // Remove from tracked set so PE re-reviews after rework
                    _mergedTestedPrNumbersWithSha.TryRemove(pr.Number, out _);
                    continue;
                }

                UpdateStatus(AgentStatus.Working, $"🔄 Merging PR #{pr.Number} into main branch");

                MergeAttemptResult result;
                if (_mergeCoordinator is not null)
                {
                    // Serialize through the merge coordinator to prevent N² thrash
                    var coordResult = await _mergeCoordinator.RunExclusiveAsync(
                        pr.Number, Identity.Id,
                        async merCt =>
                        {
                            var r = await PrWorkflow.MergeApprovedTestedPRAsync(
                                pr.Number, "SoftwareEngineer", merCt);
                            return r switch
                            {
                                MergeAttemptResult.Merged => VirtualDevTeam.Core.Merging.MergeOutcome.Merged,
                                MergeAttemptResult.NotOpen => VirtualDevTeam.Core.Merging.MergeOutcome.NotOpen,
                                _ => VirtualDevTeam.Core.Merging.MergeOutcome.ConflictDetected,
                            };
                        }, ct);

                    if (coordResult.Outcome == VirtualDevTeam.Core.Merging.MergeOutcome.Skipped)
                    {
                        Logger.LogDebug("PR #{Number} merge skipped by coordinator: {Detail}", pr.Number, coordResult.Detail);
                        continue;
                    }

                    result = coordResult.Outcome switch
                    {
                        VirtualDevTeam.Core.Merging.MergeOutcome.Merged => MergeAttemptResult.Merged,
                        VirtualDevTeam.Core.Merging.MergeOutcome.NotOpen => MergeAttemptResult.NotOpen,
                        _ => MergeAttemptResult.ConflictBlocked,
                    };
                }
                else
                {
                    // Fallback: direct merge without coordination
                    result = await PrWorkflow.MergeApprovedTestedPRAsync(
                        pr.Number, "SoftwareEngineer", ct);
                }

                if (result == MergeAttemptResult.Merged)
                {
                    // Record successful merge (with head SHA) so we don't try again unless a new commit appears.
                    _mergedTestedPrNumbersWithSha[pr.Number] = pr.HeadSha ?? "";
                    UpdateStatus(AgentStatus.Working, $"✅ Merged PR #{pr.Number} successfully");
                    Logger.LogInformation("SE merged tested PR #{Number}", pr.Number);
                    LogActivity("task", $"✅ Merged PR #{pr.Number}: {pr.Title} (code approved + tests added)");

                    if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                        await MarkEngineerTaskDoneAsync(pr, ct);
                    // Close linked work items via platform abstraction (ADO parity)
                    if (_mergeCloseout is not null)
                        await _mergeCloseout.CloseLinkedWorkItemsAsync(pr.Number, ct);

                    await RememberAsync(MemoryType.Action,
                        $"Merged tested PR #{pr.Number}: {pr.Title}", ct: ct);

                    // SinglePR mode: the single task PR IS the final deliverable — signal complete immediately
                    if (Config.Limits.IsSinglePr && !_engineeringSignaled)
                    {
                        Logger.LogInformation("SinglePR mode: task PR #{Number} merged — signaling engineering complete", pr.Number);
                        _allTasksComplete = true;
                        await SignalEngineeringCompleteAsync(ct);
                    }
                    // If this was the integration PR, signal engineering complete
                    if (_integrationPrCreated && _allTasksComplete && !_engineeringSignaled)
                    {
                        await CloseIntegrationIssueAsync(
                            $"✅ Integration PR #{pr.Number} merged successfully.", ct);
                        await SignalEngineeringCompleteAsync(ct);
                    }
                }
                else if (result == MergeAttemptResult.ConflictBlocked)
                {
                    // Re-fetch PR before closing — another agent may have merged it in the
                    // race window (Lesson #18: multi-worker merge race). If already merged,
                    // the "conflict" was just a stale state from the losing racer.
                    var freshPr = (await PrService.GetAsync(pr.Number, ct))?.ToAgentPR();
                    if (freshPr is not null && (!string.Equals(freshPr.State, "open", StringComparison.OrdinalIgnoreCase) || freshPr.IsMerged))
                    {
                        Logger.LogInformation(
                            "PR #{Number} appeared conflict-blocked but is already {State} (merged={Merged}) — " +
                            "another agent won the merge race. Skipping close-and-recreate.",
                            pr.Number, freshPr.State, freshPr.IsMerged);
                        if (freshPr.IsMerged)
                        {
                            _mergedTestedPrNumbersWithSha[pr.Number] = freshPr.HeadSha ?? "";
                            if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                                await MarkEngineerTaskDoneAsync(pr, ct);
                            if (_integrationPrCreated && _allTasksComplete && !_engineeringSignaled)
                            {
                                await CloseIntegrationIssueAsync(
                                    $"✅ Integration PR #{pr.Number} merged successfully.", ct);
                                await SignalEngineeringCompleteAsync(ct);
                            }
                        }
                        continue;
                    }

                    UpdateStatus(AgentStatus.Working, $"⚠️ Resolving merge conflict for PR #{pr.Number}");
                    Logger.LogWarning("Tested PR #{Number} has merge conflicts", pr.Number);
                    LogActivity("task", $"⚠️ Tested PR #{pr.Number} blocked by merge conflicts");
                    await TryCloseAndRecreatePRAsync(pr, ct);
                }
                else if (result == MergeAttemptResult.SecurityBlocked)
                {
                    // Do NOT close-and-recreate. Keep the PR open for human inspection.
                    Logger.LogWarning(
                        "SE tested-PR merge for PR #{Number} is security-blocked. " +
                        "SecurityAuditor findings must be resolved before this PR can merge.",
                        pr.Number);
                    LogActivity("task",
                        $"🛑 PR #{pr.Number} security-blocked — leaving open for SecurityAuditor re-review");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to merge tested PRs");
        }
    }

    private async Task ProcessOwnReworkAsync(CancellationToken ct)
    {
        // Drain and batch rework items per PR (same logic as base class loop)
        var batches = new Dictionary<int, List<ReworkItem>>();
        while (ReworkQueue.TryDequeue(out var rework))
        {
            if (!batches.TryGetValue(rework.PrNumber, out var list))
            {
                list = new List<ReworkItem>();
                batches[rework.PrNumber] = list;
            }
            list.Add(rework);
        }
        foreach (var batch in batches.Values)
        {
            await HandleReworkAsync(batch, ct);
        }
    }

    /// <summary>
    /// <summary>
    /// Consolidated recovery + PR monitoring entry point (SimplificationRecommendations §2.6).
    /// 
    /// **Startup recovery** (each method runs once via internal guards):
    ///   1. RecoverReadyForReviewPRsAsync — reclaim our ready-for-review PRs, resume rework/merge
    ///   2. RecoverOwnInProgressPRAsync — reclaim our in-progress PR after restart
    ///   3. RecoverStuckInProgressPRAsync — mark stuck in-progress PRs ready-for-review
    ///   4. RecoverConflictingApprovedPRsAsync — resolve merge conflicts on approved PRs
    /// 
    /// **Per-tick monitoring** (runs every loop iteration):
    ///   - CheckOwnPrStatusAsync — detect when PRs get merged/closed externally
    /// </summary>
    private async Task RunRecoveryAndPrMonitoringAsync(CancellationToken ct)
    {
        // One-time startup recovery (each method has internal _recovered* guard)
        await RecoverReadyForReviewPRsAsync(ct);
        await RecoverOwnInProgressPRAsync(ct);
        await RecoverStuckInProgressPRAsync(ct);
        await RecoverConflictingApprovedPRsAsync(ct);

        // Per-tick: detect externally merged/closed PRs and update task state
        await CheckOwnPrStatusAsync(ct);
    }

    /// <summary>
    /// On restart, check for our own PRs that are ready-for-review.
    /// Instead of blindly re-broadcasting, check PR comments for unaddressed feedback:
    /// - If CHANGES_REQUESTED exists → populate ReworkQueue directly
    /// - If all required reviewers approved → attempt merge
    /// - If no reviews yet → re-broadcast ReviewRequestMessage
    /// </summary>
    private async Task RecoverReadyForReviewPRsAsync(CancellationToken ct)
    {
        if (_recoveredReviewPRs)
            return;

        try
        {
            var myPRs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);

            // CRITICAL: filter to CURRENT run scope. An open PR from a prior run (which
            // survives a runner restart with a new run scope) must not be reclaimed by
            // the new run — its branch is agent/{oldScope}/..., no current-run task links
            // to it, and no reviewer will pick it up. Reclaiming it (setting CurrentPrNumber,
            // marking tasks Done by title match, re-broadcasting review requests) stalls
            // the pipeline indefinitely. Branch format: agent/{runScope}/{agentSlug}/{taskSlug}
            var currentRunScope = BranchProvider?.RunScope;
            if (currentRunScope is not null)
            {
                var beforeCount = myPRs.Count;
                myPRs = myPRs
                    .Where(pr => IsCurrentRunScopePr(pr))
                    .ToList()
                    .AsReadOnly();
                if (beforeCount != myPRs.Count)
                {
                    Logger.LogInformation(
                        "SE recovery: filtered {Before} → {After} PR(s) to current run scope {Scope} (ignored {Skipped} cross-run PRs)",
                        beforeCount, myPRs.Count, currentRunScope, beforeCount - myPRs.Count);
                }
            }

            // First pass: populate _pastImplementationPrs with ALL open PRs we own.
            // This restores in-memory ownership after a restart so HandleChangesRequestedAsync
            // (which filters by CurrentPrNumber OR IsPastImplementationPrTracked(n)) will
            // recognize late review feedback from PM/Architect on shipped PRs.
            foreach (var pr in myPRs)
            {
                if (string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                {
                    TrackPastImplementationPr(pr.Number);

                    // If PR is past implementation, mark the corresponding task Done in cache.
                    // Without this, the task manager reports the task as "Pending" after restart
                    // (because WI in ADO wasn't closed), causing SE to re-run implementation.
                    // This works for both GitHub and ADO since IsPastImplementation checks labels
                    // which are platform-agnostic (set via AddLabelAsync on both platforms).
                    if (PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels))
                    {
                        // Strategy 1: Match by PullRequestNumber (set during runtime, not persisted)
                        var taskForPr = _taskManager.Tasks.FirstOrDefault(t =>
                            t.PullRequestNumber == pr.Number && t.IssueNumber.HasValue);

                        // Strategy 2: Match by linked work items (platform-agnostic, most reliable)
                        if (taskForPr is null)
                        {
                            try
                            {
                                var linkedIds = await PrService.GetLinkedWorkItemIdsAsync(pr.Number, ct);
                                if (linkedIds.Count > 0)
                                {
                                    taskForPr = _taskManager.Tasks.FirstOrDefault(t =>
                                        t.IssueNumber.HasValue
                                        && linkedIds.Contains(t.IssueNumber.Value)
                                        && !EngineeringTaskIssueManager.IsTaskPastImplementation(t));
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogDebug(ex, "Could not fetch linked work items for PR #{PrNumber} during recovery", pr.Number);
                            }
                        }

                        // Strategy 3: Fallback to exact PR title match (needed when links don't exist)
                        if (taskForPr is null)
                        {
                            var expectedPrefix = $"{Identity.DisplayName}:";
                            if (pr.Title.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                // Extract task name from PR title: "{DisplayName}: {TaskName}"
                                var prTaskName = pr.Title[(expectedPrefix.Length)..].Trim();
                                taskForPr = _taskManager.Tasks.FirstOrDefault(t =>
                                    t.IssueNumber.HasValue
                                    && !EngineeringTaskIssueManager.IsTaskPastImplementation(t)
                                    && string.Equals(t.Name, prTaskName, StringComparison.OrdinalIgnoreCase));
                            }
                        }

                        if (taskForPr is not null && !EngineeringTaskIssueManager.IsTaskPastImplementation(taskForPr))
                        {
                            await _taskManager.MarkImplementationCompleteAsync(taskForPr.IssueNumber!.Value, pr.Number, ct);
                            Logger.LogInformation(
                                "State recovery: marked task {TaskId} (issue #{IssueNumber}) ImplementationComplete — PR #{PrNumber} is past implementation (labels: {Labels})",
                                taskForPr.Id, taskForPr.IssueNumber.Value, pr.Number,
                                string.Join(", ", pr.Labels));
                        }
                    }
                }
            }

            // Also seed rework queue for PRs with unaddressed CHANGES REQUESTED feedback,
            // regardless of whether the "ready-for-review" label is still present (PM/Architect
            // typically clears it when requesting changes). The older loop below only handles
            // PRs still labelled "ready-for-review" for merge-recovery.
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Skip already approved (pm-approved) PRs — they go through merge path
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                    continue;
                // Skip if still ready-for-review (handled by the merge-recovery loop below)
                if (pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;

                var pendingFb = await PrWorkflow.GetPendingChangesRequestedAsync(pr.Number, ct);
                if (pendingFb is { } pending && !ReworkQueue.Any(r => r.PrNumber == pr.Number))
                {
                    ReworkQueue.Enqueue(new ReworkItem(pr.Number, pr.Title, pending.Feedback, pending.Reviewer));
                    Logger.LogInformation(
                        "SE recovered unaddressed changes-requested feedback on PR #{PrNumber} from {Reviewer} (no ready-for-review label)",
                        pr.Number, pending.Reviewer);
                }
            }

            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Match PRs with ready-for-review OR architect-approved label
                // (Architect removes ready-for-review when approving, so also check architect-approved)
                if (!pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase) &&
                    !pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Track this PR
                CurrentPrNumber = pr.Number;
                Identity.AssignedPullRequest = pr.Number.ToString();

                // Check for unaddressed CHANGES_REQUESTED feedback on GitHub
                var pendingFeedback = await PrWorkflow.GetPendingChangesRequestedAsync(pr.Number, ct);
                if (pendingFeedback is { } pending)
                {
                    // Populate rework queue directly — no need to re-broadcast
                    ReworkQueue.Enqueue(new ReworkItem(pr.Number, pr.Title, pending.Feedback, pending.Reviewer));
                    Logger.LogInformation(
                        "SE recovered unaddressed feedback on PR #{PrNumber} from {Reviewer}",
                        pr.Number, pending.Reviewer);
                    UpdateStatus(AgentStatus.Working, $"Processing recovered feedback on PR #{pr.Number}");
                    continue;
                }

                // No unaddressed changes — check if all reviewers approved (maybe we can merge)
                var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                var required = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                var approved = await PrWorkflow.GetApprovedReviewersAsync(pr.Number, ct);

                if (required.All(r => approved.Contains(r, StringComparer.OrdinalIgnoreCase)))
                {
                    // If inline test workflow, also require tests-added label before merge
                    if (Config.Workspace.IsInlineTestWorkflow &&
                        !pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation(
                            "SE PR #{PrNumber} has all approvals but waiting for TE tests before merge", pr.Number);
                        continue;
                    }

                    // Security hard-block: never merge a security-blocked PR from the recovery path either.
                    if (pr.Labels.Contains(PullRequestWorkflow.Labels.SecurityBlocked, StringComparer.OrdinalIgnoreCase))
                    {
                        Logger.LogWarning(
                            "SE recovery path: PR #{PrNumber} has all approvals but is security-blocked — leaving open",
                            pr.Number);
                        continue;
                    }

                    // All approved (and tested if required) — try merge
                    Logger.LogInformation("SE PR #{PrNumber} has all approvals, merging", pr.Number);
                    try
                    {
                        await PrService.MergeAsync(pr.Number,
                            $"Merged after dual approval from {string.Join(" and ", approved)}", ct);
                    }
                    catch (Octokit.PullRequestNotMergeableException)
                    {
                        Logger.LogWarning("SE PR #{PrNumber} not mergeable, syncing branch with main", pr.Number);
                        var synced = await PrService.UpdateBranchAsync(pr.Number, ct);
                        if (!synced)
                        {
                            // Standard sync failed — try force-rebase onto main
                            Logger.LogWarning("SE PR #{PrNumber} branch sync failed — attempting force-rebase", pr.Number);
                            synced = await PrService.RebaseBranchAsync(pr.Number, ct);
                        }

                        if (synced)
                        {
                            await Task.Delay(5000, ct);
                            try
                            {
                                await PrService.MergeAsync(pr.Number,
                                    $"Merged after branch sync and dual approval from {string.Join(" and ", approved)}", ct);
                            }
                            catch (Exception retryEx)
                            {
                                Logger.LogWarning(retryEx, "SE PR #{PrNumber} still not mergeable after sync", pr.Number);
                                await TryCloseAndRecreatePRAsync(pr, ct);
                                continue;
                            }
                        }
                        else
                        {
                            Logger.LogWarning("SE PR #{PrNumber} has real merge conflicts, attempting close-and-recreate", pr.Number);
                            await TryCloseAndRecreatePRAsync(pr, ct);
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(pr.HeadBranch))
                        await BranchService.DeleteAsync(pr.HeadBranch, ct);

                    var taskTitle2 = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                    var task2 = taskTitle2 is not null ? _taskManager.FindByName(taskTitle2) : null;
                    if (task2?.IssueNumber.HasValue == true)
                        await _taskManager.MarkDoneAsync(task2.IssueNumber.Value, pr.Number, ct);

                    // Broadcast merge event so downstream agents (PM, TE, other SEs) wake immediately
                    await MessageBus.PublishAsync(new PrMergedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "PrMerged",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        HeadBranch = pr.HeadBranch ?? "",
                        LinkedIssueNumber = task2?.IssueNumber,
                    }, ct);

                    CurrentPrNumber = null;
                    _currentTaskName = null;
                    Identity.AssignedPullRequest = null;
                    UpdateStatus(AgentStatus.Idle, "Ready for next task");
                    continue;
                }

                // Partial or no reviews — re-broadcast for missing reviewers
                await MessageBus.PublishAsync(new ReviewRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ReviewRequest",
                    PrNumber = pr.Number,
                    PrTitle = pr.Title,
                    ReviewType = "Recovery"
                }, ct);

                Logger.LogInformation("SE re-broadcast review request for own PR #{PrNumber}: {Title}",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Idle, $"PR #{pr.Number} awaiting review");
            }

            _recoveredReviewPRs = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover ready-for-review PRs (will retry next loop)");
        }
    }

    /// <summary>
    /// On restart, detect PE's own open in-progress PRs and restore CurrentPrNumber
    /// so the PE can continue implementation instead of leaving the PR orphaned.
    /// </summary>
    private async Task RecoverOwnInProgressPRAsync(CancellationToken ct)
    {
        if (_recoveredInProgressPR)
            return;
        _recoveredInProgressPR = true;

        // If we already have a tracked PR, nothing to recover
        if (CurrentPrNumber is not null)
            return;

        try
        {
            var myPRs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            // CRITICAL: filter to CURRENT run scope only. Without this, an open PR from a
            // PRIOR run (which survives a runner restart with a new run scope) is adopted
            // as CurrentPrNumber and the SE waits forever for reviewers who never look at
            // it — stalling the new run's pipeline. Branch format: agent/{runScope}/...
            var currentRunScope = BranchProvider?.RunScope;
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (currentRunScope is not null
                    && !IsCurrentRunScopePr(pr))
                {
                    Logger.LogInformation(
                        "SE skipping cross-run PR #{PrNumber} (branch {Branch}) — not in current run scope {Scope}",
                        pr.Number, pr.HeadBranch, currentRunScope);
                    continue;
                }

                // Look for in-progress PRs (not past implementation — those are handled by
                // review / merge flows, not by resuming implementation).
                if (PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels))
                    continue;

                // Found an in-progress PR that belongs to the PE
                CurrentPrNumber = pr.Number;
                Identity.AssignedPullRequest = pr.Number.ToString();
                ActivatePrSession(pr.Number);

                Logger.LogInformation(
                    "SE recovered own in-progress PR #{PrNumber}: {Title} — will continue implementation",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Resuming: {pr.Title}");
                break; // Only recover one PR at a time
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover own in-progress PR");
        }
    }

    /// <summary>
    /// Check whether our currently tracked PR has progressed past the SE's implementation phase
    /// (has ready-for-review, architect-approved, pm-approved, approved, or tests-added label).
    /// Reviewers strip the `ready-for-review` label when they act and replace it with downstream
    /// approval labels — so the SE must treat any of those as "past implementation" and avoid
    /// re-entering ContinueOwnPrImplementationAsync (which would re-checkout the branch and
    /// clobber reviewer-produced commits).
    /// </summary>
    private async Task<bool> IsOwnPrPastImplementationAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is null)
            return false;

        try
        {
            var pr = await PrService.GetAsync(CurrentPrNumber.Value, ct);
            return pr is not null && PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds an existing open PR for the given task by checking if any open PR links to the
    /// task's issue number (via "Closes #NNN" in the body) or matches the SE's title prefix.
    /// Used to prevent creating duplicate PRs when a task is re-acquired after rework failures.
    /// </summary>
    private async Task<AgentPullRequest?> FindExistingPrForTaskAsync(EngineeringTask task, CancellationToken ct)
    {
        if (!task.IssueNumber.HasValue)
            return null;

        var openPRs = (await GetCachedOpenPRsAsync(ct))
            .ToAgentPRs()
            .Where(IsCurrentRunScopePr)
            .ToList();

        // Primary match: PR body contains "Closes #<issue>"
        foreach (var pr in openPRs)
        {
            var linkedIssue = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (linkedIssue == task.IssueNumber.Value)
            {
                // Verify PR is owned by this SE (title prefix match)
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                    return pr;
            }
        }

        // Fallback: PR title matches our naming convention for this task
        var expectedPrefix = $"{Identity.DisplayName}:";
        foreach (var pr in openPRs)
        {
            if (pr.Title.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && pr.Title.Contains(task.Name, StringComparison.OrdinalIgnoreCase))
            {
                return pr;
            }
        }

        return null;
    }

    /// <summary>
    /// Continue implementing our own in-progress PR. Reads existing commits to determine
    /// what's been done, generates remaining steps, and implements them.
    /// </summary>
    private async Task ContinueOwnPrImplementationAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is null)
            return;

        // Guard: prevent infinite continuation loops (e.g., push keeps failing)
        _continuationAttempts++;
        if (_continuationAttempts > MaxContinuationAttempts)
        {
            Logger.LogWarning(
                "SE PR #{PrNumber} exceeded {Max} continuation attempts — releasing to prevent runaway loop",
                CurrentPrNumber.Value, MaxContinuationAttempts);
            LogActivity("task", $"⛔ PR #{CurrentPrNumber.Value} blocked after {_continuationAttempts} failed continuation attempts");
            CurrentPrNumber = null;
            Identity.AssignedPullRequest = null;
            _continuationAttempts = 0;
            return;
        }

        try
        {
            var pr = (await PrService.GetAsync(CurrentPrNumber.Value, ct))?.ToAgentPR();
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;
                _continuationAttempts = 0;
                return;
            }

            // Find the linked issue for context
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            AgentIssue? sourceIssue = null;
            if (issueNumber.HasValue)
                sourceIssue = (await WorkItemService.GetAsync(issueNumber.Value, ct))?.ToAgentIssue();

            // Get existing files to understand what's already been done
            var existingFiles = await GetPrFileListAsync(pr.Number, ct);

            Logger.LogInformation(
                "SE continuing implementation on PR #{PrNumber} (existing files: {Files})",
                pr.Number, existingFiles?.Split('\n').Length ?? 0);

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var techStack = Config.Project.TechStack;

            // Generate implementation steps based on what remains to be done
            var syntheticIssue = sourceIssue ?? new AgentIssue
            {
                Number = issueNumber ?? 0,
                Title = pr.Title,
                Body = pr.Body ?? "",
                State = "open",
                Labels = new List<string>()
            };

            // SinglePassMode: produce complete implementation in one prompt (same as initial implementation)
            if (Config.CopilotCli.SinglePassMode)
            {
                Logger.LogInformation("SE using single-pass for continued implementation on PR #{PrNumber}", pr.Number);
                UpdateStatus(AgentStatus.Working, $"Implementing: {Truncate(pr.Title, 60)}");

                var history = CreateChatHistory();
                history.AddSystemMessage(GetImplementationSystemPrompt(techStack));

                var ctx = new System.Text.StringBuilder();
                ctx.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                ctx.AppendLine($"## Architecture\n{architectureDoc}\n");
                if (sourceIssue is not null)
                    ctx.AppendLine($"## GitHub Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}\n");
                ctx.AppendLine($"## Task: {pr.Title}\n{pr.Body}\n");
                if (!string.IsNullOrEmpty(existingFiles))
                    ctx.AppendLine($"## Existing Files in PR (may have build errors — fix or replace as needed)\n{existingFiles}\n");

                // Include visual design reference for UI-related tasks
                await AppendDesignContextIfRelevantAsync(ctx, pr.Title, pr.Body, sourceIssue?.Body, ct);

                ctx.AppendLine("Implement ONLY the files needed for this specific task. " +
                    "Output each file using this exact format:\n\n" +
                    "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                    $"Use the {techStack} technology stack. " +
                    "SCOPE RULE: Only output files that are NEW or MINIMALLY MODIFIED for this task. " +
                    "Do NOT regenerate .sln, .csproj, Program.cs, or other infrastructure files unless " +
                    "this task explicitly requires changes to them. " +
                    "Every file MUST use the FILE: marker format so it can be parsed and committed.");

                history.AddUserMessage(ctx.ToString());
                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var responseText = response.Content?.Trim() ?? "";
                var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(responseText);

                // Retry once if AI didn't produce FILE: markers
                if (codeFiles.Count == 0 && !string.IsNullOrEmpty(responseText))
                {
                    Logger.LogWarning(
                        "SE single-pass continuation produced no FILE: blocks (response length={Length}). Retrying.",
                        responseText.Length);

                    history.AddAssistantMessage(responseText);
                    history.AddUserMessage(
                        "Your response did not contain any parseable code files. " +
                        "You MUST output every file using EXACTLY this format:\n\n" +
                        "FILE: path/to/file.ext\n```language\n<complete file content>\n```\n\n" +
                        "Output the ACTUAL source code files. Do not describe — produce code.");

                    var retryResp = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                    codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(retryResp.Content?.Trim() ?? "");

                    if (codeFiles.Count == 0)
                    {
                        Logger.LogError("SE single-pass continuation retry also produced no files. Aborting.");
                        LogActivity("task", "❌ Continuation failed — AI unable to produce code in FILE: format");
                        return;
                    }
                }
                else if (codeFiles.Count == 0)
                {
                    Logger.LogWarning("SE single-pass continuation got empty response. Aborting.");
                    return;
                }

                if (Workspace is not null && BuildRunnerSvc is not null)
                {
                    var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                        $"Implement {pr.Title}", 1, 1, pr.Title, chat, ct, isRework: true);
                    if (!committed)
                    {
                        Logger.LogWarning("SE single-pass continuation blocked by build errors on PR #{PrNumber}", pr.Number);
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"❌ **Build Blocked:** Single-pass continuation could not produce a buildable commit.", ct);
                        return;
                    }
                }
                else
                {
                    await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, $"Implement {pr.Title}", ct);
                }

                await SyncBranchWithMainAsync(pr.Number, ct);
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
                return;
            }

            var steps = await GenerateImplementationStepsAsync(
                chat, pr, syntheticIssue, pmSpecDoc, architectureDoc, techStack, ct);

            if (steps.Count == 0)
            {
                Logger.LogWarning("SE could not generate remaining steps for PR #{PrNumber}, marking ready", pr.Number);
                await SyncBranchWithMainAsync(pr.Number, ct);
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
                return;
            }

            Logger.LogInformation(
                "SE generated {Count} implementation steps for continued work on PR #{PrNumber}",
                steps.Count, pr.Number);

            var completedSteps = new List<string>();
            for (var i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                var stepNumber = i + 1;

                UpdateStatus(AgentStatus.Working,
                    $"PR #{pr.Number} step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");
                Logger.LogInformation(
                    "SE implementing step {Step}/{Total} for PR #{PrNumber}: {Desc}",
                    stepNumber, steps.Count, pr.Number, Truncate(step, 100));

                var stepHistory = CreateChatHistory();
                stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

                var ctx = new System.Text.StringBuilder();
                ctx.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                ctx.AppendLine($"## Architecture\n{architectureDoc}\n");
                if (sourceIssue is not null)
                    ctx.AppendLine($"## Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}\n");
                ctx.AppendLine($"## PR Description\n{pr.Body}\n");

                // Include visual design reference for UI-related tasks
                await AppendDesignContextIfRelevantAsync(ctx, pr.Title, pr.Body, sourceIssue?.Body, ct);

                if (!string.IsNullOrEmpty(existingFiles) || completedSteps.Count > 0)
                {
                    ctx.AppendLine("## Previously Completed Steps / Existing Files");
                    if (!string.IsNullOrEmpty(existingFiles))
                        ctx.AppendLine($"Files already in PR:\n{existingFiles}\n");
                    for (var j = 0; j < completedSteps.Count; j++)
                        ctx.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                    ctx.AppendLine();
                }

                ctx.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                ctx.AppendLine(step);
                ctx.AppendLine();
                ctx.AppendLine("Implement ONLY this step. Output each file using this format:\n");
                ctx.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
                ctx.AppendLine($"Use the {techStack} technology stack. Every file MUST use the FILE: marker format.");
                if (!string.IsNullOrEmpty(existingFiles) || completedSteps.Count > 0)
                    ctx.AppendLine("If updating a file from a previous step, include the COMPLETE updated file content.");

                stepHistory.AddUserMessage(ctx.ToString());

                var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                var stepImpl = stepResponse.Content?.Trim() ?? "";

                var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(stepImpl);
                if (codeFiles.Count > 0)
                {
                    if (Workspace is not null && BuildRunnerSvc is not null)
                    {
                        var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                            $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}",
                            stepNumber, steps.Count, step, chat, ct, isRework: true);
                        if (!committed)
                        {
                            Logger.LogWarning("SE rework step {Step}/{Total} blocked by build errors on PR #{PrNumber}",
                                stepNumber, steps.Count, pr.Number);
                            await ReviewService.AddCommentAsync(pr.Number,
                                $"❌ **Build Blocked:** Rework step {stepNumber}/{steps.Count} could not produce a buildable commit.", ct);
                            return;
                        }
                    }
                    else
                    {
                        await PrWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}", ct);
                    }
                    Logger.LogInformation(
                        "SE committed {FileCount} files for step {Step}/{Total} on PR #{PrNumber}",
                        codeFiles.Count, stepNumber, steps.Count, pr.Number);
                }

                completedSteps.Add(step);
            }

            // All steps done — mark ready for review
            await SyncBranchWithMainAsync(pr.Number, ct);
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

            Logger.LogInformation(
                "SE completed continued implementation for PR #{PrNumber}",
                pr.Number);
            _continuationAttempts = 0; // Reset on success
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to continue own PR #{PrNumber} implementation", CurrentPrNumber);
        }
    }

    /// <summary>
    /// Detect and recover from the case where WorkOnOwnTasksAsync created a PR and
    /// committed code but failed before calling MarkReadyForReviewAsync. The task is
    /// InProgress, the PR is open with "in-progress" label, but no one is reviewing it.
    /// Fix: mark it ready-for-review and broadcast the review request.
    /// Runs once per restart (guard: _recoveredStuckPR).
    /// </summary>
    private bool _recoveredStuckPR;
    private async Task RecoverStuckInProgressPRAsync(CancellationToken ct)
    {
        if (_recoveredStuckPR)
            return;
        _recoveredStuckPR = true;

        try
        {
            // Only act if we have a tracked PR
            if (CurrentPrNumber is null)
                return;

            var pr = (await PrService.GetAsync(CurrentPrNumber.Value, ct))?.ToAgentPR();
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                return;

            // Only recover in-progress PRs (not already ready-for-review or further along)
            if (pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase) ||
                pr.Labels.Contains("architect-approved", StringComparer.OrdinalIgnoreCase) ||
                pr.Labels.Contains("pm-approved", StringComparer.OrdinalIgnoreCase) ||
                pr.Labels.Contains("approved", StringComparer.OrdinalIgnoreCase) ||
                pr.Labels.Contains("tests-added", StringComparer.OrdinalIgnoreCase))
                return;

            // Must have at least some code committed (updated after creation)
            if (pr.UpdatedAt is null || pr.UpdatedAt <= pr.CreatedAt.AddMinutes(1))
                return;

            // Don't recover if the last comment is a build-blocked message — no code was committed
            var comments = await ReviewService.GetCommentsAsync(CurrentPrNumber.Value, ct);
            var lastComment = comments.LastOrDefault();
            if (lastComment?.Body?.Contains("Build Blocked", StringComparison.OrdinalIgnoreCase) == true)
            {
                Logger.LogDebug("PR #{PrNumber} last comment is Build Blocked — not recovering", CurrentPrNumber.Value);
                return;
            }

            Logger.LogInformation(
                "SE recovering stuck in-progress PR #{PrNumber} — marking ready for review",
                pr.Number);
            LogActivity("system", $"🔄 Recovering stuck PR #{pr.Number} — marking ready for review");

            // Sync branch with main before marking ready — ensures PR is merge-clean
            await SyncBranchWithMainAsync(pr.Number, ct);
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

            UpdateStatus(AgentStatus.Idle, $"PR #{pr.Number} ready for review (recovered)");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover stuck in-progress PR");
        }
    }

    /// <summary>
    /// Scans for open PRs authored by this agent that have approval labels but are
    /// CONFLICTING (merge state dirty). These PRs were approved by reviewers but
    /// developed merge conflicts while waiting. Reclaims them and attempts in-place
    /// conflict resolution via Copilot CLI. Runs once per restart.
    /// </summary>
    private bool _recoveredConflictingPRs;
    private async Task RecoverConflictingApprovedPRsAsync(CancellationToken ct)
    {
        if (_recoveredConflictingPRs || CurrentPrNumber is not null)
            return;
        _recoveredConflictingPRs = true;

        try
        {
            var myPRs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsCurrentRunScopePr(pr))
                    continue;

                // Only target PRs that are past implementation (approved/ready-for-review)
                if (!PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels))
                    continue;

                // Check if this PR has a merge conflict
                var fullPr = await PrService.GetAsync(pr.Number, ct);
                if (fullPr is null) continue;

                var isConflicting = string.Equals(fullPr.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullPr.MergeableState, "conflicting", StringComparison.OrdinalIgnoreCase);

                if (!isConflicting) continue;

                Logger.LogInformation(
                    "Found approved but conflicting PR #{PrNumber}: {Title} — reclaiming for conflict resolution",
                    pr.Number, pr.Title);

                // Reclaim the PR
                CurrentPrNumber = pr.Number;
                Identity.AssignedPullRequest = pr.Number.ToString();
                UpdateStatus(AgentStatus.Working, $"Resolving merge conflict on PR #{pr.Number}");

                // Attempt in-place resolution
                var resolved = await TryResolveConflictInPlaceAsync(pr, ct);
                if (resolved)
                {
                    Logger.LogInformation("PR #{PrNumber} conflict resolved — releasing back to merge flow", pr.Number);
                    LogActivity("task", $"✅ Resolved merge conflict on PR #{pr.Number} after restart recovery");
                    CurrentPrNumber = null;
                    Identity.AssignedPullRequest = null;
                }
                else
                {
                    Logger.LogWarning("PR #{PrNumber} conflict could not be auto-resolved — calling TryCloseAndRecreatePRAsync", pr.Number);
                    await TryCloseAndRecreatePRAsync(pr, ct);
                }

                break; // Handle one conflicting PR per cycle
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover conflicting approved PRs");
        }
    }

    /// <summary>
    private async Task CheckOwnPrStatusAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is not null)
        {
            await CheckSinglePrStatusAsync(CurrentPrNumber.Value, isPast: false, ct);
        }

        // Snapshot to allow removal during iteration.
        var pastSnapshot = GetPastImplementationPrSnapshot();
        foreach (var prNumber in pastSnapshot)
        {
            await CheckSinglePrStatusAsync(prNumber, isPast: true, ct);
        }
    }

    private async Task CheckSinglePrStatusAsync(int prNumber, bool isPast, CancellationToken ct)
    {
        try
        {
            var pr = await PrService.GetAsync(prNumber, ct);
            if (pr is not null && string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                return;

            var wasMerged = pr?.IsMerged == true;
            Logger.LogInformation("SE own PR #{PrNumber} is no longer open ({State}, merged={Merged}), clearing tracking",
                prNumber, pr?.State ?? "unknown", wasMerged);

            // Find the task by matching the PR title or the task manager cache
            var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr?.Title ?? "");
            var task = taskTitle is not null ? _taskManager.FindByName(taskTitle) : null;

            if (task?.IssueNumber.HasValue == true)
            {
                if (wasMerged)
                {
                    await _taskManager.MarkDoneAsync(task.IssueNumber.Value, prNumber, ct);
                    Logger.LogInformation("SE task {TaskId} marked Done (PR #{PrNumber} merged)",
                        task.Id, prNumber);
                    LogActivity("task", $"✅ Task {task.Id}: {task.Name} completed (PR #{prNumber} merged)");
                }
                else
                {
                    await _taskManager.ResetToPendingAsync(task.IssueNumber.Value, ct);
                    ClaimRegistry?.Release(task.IssueNumber.Value);
                    Logger.LogInformation("SE task {TaskId} reset to Pending (PR #{PrNumber} closed without merge)",
                        task.Id, prNumber);
                }
            }

            if (isPast)
            {
                UntrackPastImplementationPr(prNumber);
                _mergedPrNumbers.Add(prNumber);
            }
            else
            {
                CurrentPrNumber = null;
                _currentTaskName = null;
                Identity.AssignedPullRequest = null;
                _mergedPrNumbers.Add(prNumber);
            }

            if (wasMerged && _allTasksComplete && _integrationPrCreated)
            {
                await SignalEngineeringCompleteAsync(ct);
            }
            else if (!isPast)
            {
                UpdateStatus(AgentStatus.Idle, "Ready for next task");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check own PR #{PrNumber} status", prNumber);
        }
    }

    private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
    {
        try
        {
            if (_resourceRequestPending)
            {
                // Check if the batch spawn request has been fulfilled
                var currentWorkers = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer).Count();
                if (currentWorkers >= _expectedEngineerCount)
                {
                    _resourceRequestPending = false;
                    _pendingWorkerRequests = 0;
                }
                else if (DateTime.UtcNow - _lastResourceRequestTime > SpawnCooldown)
                {
                    // Cooldown expired — clear and re-evaluate (handles partial spawns or gate delays)
                    _resourceRequestPending = false;
                    _pendingWorkerRequests = 0;
                }
                else
                {
                    return;
                }
            }

            var parallelizable = _taskManager.Tasks.Count(t =>
                t.Status == "Pending" && _taskManager.AreDependenciesMet(t));

            if (parallelizable < 2)
                return;

            // Wave-aware scaling: cap parallelism to current wave's ready tasks
            var currentWaveTasks = _taskManager.Tasks
                .Where(t => t.Status is "Pending" or "Assigned" or "InProgress")
                .ToList();
            var activeWaves = currentWaveTasks
                .GroupBy(t => t.Wave, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .ToList();

            if (activeWaves.Count > 0)
            {
                var currentWave = activeWaves[0].Key;
                var currentWaveParallelizable = activeWaves[0].Count(t =>
                    t.Status == "Pending" && _taskManager.AreDependenciesMet(t));

                // Cap requested engineers to current wave parallelism — don't spawn
                // more than the current wave can use (future waves are gated)
                if (currentWaveParallelizable < parallelizable)
                {
                    Logger.LogInformation(
                        "Wave-driven spawn cap: {Global} globally parallelizable but only {Wave} in current wave {WaveName} — capping to {Wave}",
                        parallelizable, currentWaveParallelizable, currentWave, currentWaveParallelizable);
                    parallelizable = currentWaveParallelizable;
                }

                Logger.LogInformation(
                    "Wave-aware scaling: current wave {Wave} has {Parallelizable} parallelizable / {Total} total tasks",
                    currentWave, currentWaveParallelizable, activeWaves[0].Count());
            }

            if (parallelizable < 2)
                return;

            // Count free workers (non-leader Software Engineers not currently assigned)
            var freeWorkers = 0;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SoftwareEngineer))
                if (agent.Identity.Id != Identity.Id && !_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeWorkers++;

            // Leader counts as 1 available capacity only if not currently implementing
            var leaderCapacity = CurrentPrNumber is null ? 1 : 0;
            var availableCapacity = freeWorkers + leaderCapacity;

            if (parallelizable <= availableCapacity)
                return;

            // Calculate how many MORE engineers we need — request all at once.
            // Read pool config from IOptionsMonitor at call time so an operator's
            // mid-run pool increase on the Configuration page takes effect on the
            // next scaling pass without restarting the runner. Falls back to the
            // construction-time snapshot when the monitor wasn't injected (test seam).
            var poolConfig = _appConfigMonitor?.CurrentValue.Limits.EngineerPool
                ?? Config.Limits.EngineerPool;
            var observedPoolCap = poolConfig.EffectiveMaxAdditional;
            if (_lastSeenPoolCap >= 0 && _lastSeenPoolCap != observedPoolCap)
            {
                Logger.LogInformation(
                    "EngineerPool.SoftwareEngineerPool changed from {OldValue} to {NewValue} (observed by SE leader scaling)",
                    _lastSeenPoolCap, observedPoolCap);
            }
            _lastSeenPoolCap = observedPoolCap;

            var currentEngineerCount = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer).Count();
            var seCapacity = observedPoolCap
                - (currentEngineerCount - 1); // -1 for leader

            if (seCapacity <= 0)
            {
                Logger.LogDebug(
                    "SE needs more workers ({Parallelizable} parallelizable) but pool exhausted ({Current}/{Max})",
                    parallelizable, currentEngineerCount - 1, observedPoolCap);
                return;
            }

            var neededWorkers = Math.Min(parallelizable - availableCapacity, seCapacity);
            if (neededWorkers <= 0)
                return;

            Logger.LogInformation(
                "SE requesting {Count} additional engineers: {Parallelizable} tasks parallelizable, " +
                "{Available} capacity available, {SeCapacity} pool slots remaining",
                neededWorkers, parallelizable, availableCapacity, seCapacity);

            // Identify the most common unassigned skill tags for specialist scaling
            var unassignedTasks = _taskManager.Tasks
                .Where(t => t.Status == "Pending" && _taskManager.AreDependenciesMet(t) && t.SkillTags.Count > 0)
                .ToList();
            var dominantSkills = unassignedTasks
                .SelectMany(t => t.SkillTags)
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            await MessageBus.PublishAsync(new ResourceRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ResourceRequest",
                RequestedRole = AgentRole.SoftwareEngineer,
                RequestedCount = neededWorkers,
                Justification = $"{parallelizable} tasks can be worked in parallel but only {availableCapacity} capacity available (requesting {neededWorkers} more)",
                CurrentTeamSize = currentEngineerCount,
                DesiredCapabilities = dominantSkills
            }, ct);

            _resourceRequestPending = true;
            _pendingWorkerRequests = neededWorkers;
            _expectedEngineerCount = currentEngineerCount + neededWorkers;
            _lastResourceRequestTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate resource needs");
        }
    }

    /// <summary>
    /// Close a PR that has unresolvable merge conflicts and reset the associated task
    /// so it can be re-implemented from a clean branch off latest main.
    /// Max retries per task (keyed by issue number) to prevent infinite close-and-recreate loops.
    /// </summary>
    private async Task TryCloseAndRecreatePRAsync(AgentPullRequest pr, CancellationToken ct)
    {
        const int MaxConflictRetries = 2;

        // Defensive: if the PR is already merged or closed, do NOT reset the task.
        // This protects against the multi-worker race where one SE merges
        // while others see a transient NotMergeable exception and would
        // otherwise treat their own attempt as a real conflict, triggering
        // a destructive task-reset on already-completed work.
        try
        {
            var freshPr = (await PrService.GetAsync(pr.Number, ct))?.ToAgentPR();
            if (freshPr is null ||
                !string.Equals(freshPr.State, "open", StringComparison.OrdinalIgnoreCase) ||
                freshPr.IsMerged)
            {
                Logger.LogInformation(
                    "PR #{Number} already closed/merged (state={State}, merged={Merged}) — skipping conflict-recreate path",
                    pr.Number, freshPr?.State, freshPr?.IsMerged);
                LogActivity("task", $"ℹ️ PR #{pr.Number} already closed/merged — no conflict to resolve");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not re-fetch PR #{Number} state before close-and-recreate", pr.Number);
            // Fall through — better to attempt the recovery than skip on a transient API error
        }

        // Guard: NEVER close-and-recreate a final integration PR. Closing it triggers the
        // _integrationPrCreated safety check which re-creates the entire T-FINAL from scratch,
        // wasting 30+ min of strategy evaluation. Integration PRs should only use in-place
        // conflict resolution. If that fails, the SE leader's safety loop will re-attempt.
        if (PullRequestWorkflow.Labels.IsFinalIntegrationPr(pr.Labels, pr.Title, pr.HeadBranch))
        {
            Logger.LogWarning(
                "PR #{Number} is a final-integration PR — refusing to close-and-recreate. " +
                "Attempting in-place resolution only; if that fails, the SE leader loop will retry.",
                pr.Number);
            if (Workspace is not null)
            {
                var resolved = await TryResolveConflictInPlaceAsync(pr, ct);
                if (resolved)
                {
                    Logger.LogInformation("Integration PR #{Number} conflict resolved in-place", pr.Number);
                    LogActivity("task", $"✅ Resolved integration PR #{pr.Number} conflict in-place (no recreate)");
                }
                else
                {
                    Logger.LogWarning(
                        "Integration PR #{Number} in-place resolution failed — will retry on next loop iteration",
                        pr.Number);
                    LogActivity("task", $"⚠️ Integration PR #{pr.Number} conflict unresolved — will retry next loop");
                }
            }
            return;
        }

        // Resolve the associated task FIRST so we can key retry count by issue number
        EngineeringTask? task = null;

        // Attempt 1: Parse linked issue number from PR body (e.g., "Closes #N", "Fixes #N")
        if (task is null && !string.IsNullOrEmpty(pr.Body))
        {
            var linkedMatch = System.Text.RegularExpressions.Regex.Match(
                pr.Body, @"(?:closes|fixes|resolves)\s+#(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (linkedMatch.Success && int.TryParse(linkedMatch.Groups[1].Value, out var linkedIssueNum))
            {
                task = _taskManager.FindByIssueNumber(linkedIssueNum);
                if (task is not null)
                    Logger.LogDebug("TryCloseAndRecreatePR: resolved task via linked issue #{IssueNumber} from PR body", linkedIssueNum);
            }
        }

        // Attempt 2: Search by task name from PR title (exact match)
        if (task is null)
        {
            var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
            if (taskTitle is not null)
            {
                // Handle doubled agent prefix: "Agent: Agent: TaskName"
                var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskTitle);
                if (innerName is not null) taskTitle = innerName;
                task = _taskManager.FindByName(taskTitle);
            }
        }

        // Attempt 3: Search by issue number from agent assignments
        if (task is null)
        {
            foreach (var issueNum in _agentAssignments.Values)
            {
                var candidate = _taskManager.FindByIssueNumber(issueNum);
                if (candidate is not null && pr.Title.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    task = candidate;
                    break;
                }
            }
        }

        // Attempt 4: Fuzzy containment match across all tracked tasks
        if (task is null)
        {
            var allTasks = _taskManager.Tasks;
            foreach (var candidate in allTasks)
            {
                if (!string.IsNullOrEmpty(candidate.Name) &&
                    pr.Title.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    task = candidate;
                    Logger.LogDebug("TryCloseAndRecreatePR: resolved task via fuzzy title match: '{TaskName}' in PR title '{PrTitle}'",
                        candidate.Name, pr.Title);
                    break;
                }
            }
        }

        if (task is null)
        {
            Logger.LogWarning(
                "TryCloseAndRecreatePR: could not resolve task for PR #{PrNumber} (title='{PrTitle}') — will proceed with recovery using PR-number-based key",
                pr.Number, pr.Title);
        }

        // Key retry count by issue number (stable across PR close/recreate cycles)
        var retryKey = task?.IssueNumber ?? -pr.Number; // negative PR# as fallback key
        _conflictRetryByIssue.TryGetValue(retryKey, out var retries);
        if (retries >= MaxConflictRetries)
        {
            Logger.LogWarning(
                "Task (issue {IssueKey}) already retried {Retries} time(s) for conflicts — giving up",
                retryKey, retries);
            await ReviewService.AddCommentAsync(pr.Number,
                $"⛔ **Permanently blocked** — This task has been closed and recreated {retries} time(s) " +
                $"but continues to hit merge conflicts. Requires manual intervention.", ct);
            return;
        }

        try
        {
            // ── Step 1: Try to resolve the conflict in-place via merge + CLI ──
            // Most conflicts are simple (additive changes from different PRs). Resolving
            // saves 10+ min vs recreating the entire PR from scratch.
            if (Workspace is not null)
            {
                var resolved = await TryResolveConflictInPlaceAsync(pr, ct);
                if (resolved)
                {
                    Logger.LogInformation(
                        "PR #{PrNumber} conflict resolved in-place — pushed merged result",
                        pr.Number);
                    LogActivity("task", $"✅ Resolved merge conflict on PR #{pr.Number} in-place (no recreate needed)");
                    _conflictRetryByIssue[retryKey] = retries + 1;
                    PersistConflictRetryCounters();
                    return;
                }
                Logger.LogInformation(
                    "PR #{PrNumber} conflict could not be auto-resolved — falling back to close-and-recreate",
                    pr.Number);
            }

            // ── Step 2: Close and recreate from clean main ──
            var closeComment =
                $"🔄 **Closing due to merge conflicts that could not be auto-resolved.**\n\n" +
                $"Attempted in-place conflict resolution but it failed. " +
                $"The task will be re-implemented on a fresh branch from latest `{EffectiveBranch}`." +
                $" (retry {retries + 1}/{MaxConflictRetries})";

            await ReviewService.AddCommentAsync(pr.Number, closeComment, ct);
            await PrService.CloseAsync(pr.Number, ct);

            // Close any OTHER open PRs for the same task (orphans from previous
            // close-and-recreate cycles where a different agent created a new PR
            // but the old agent's PR was never closed). Prevents pr-approval-stuck
            // FlowMonitor findings on abandoned PRs.
            if (task is not null)
            {
                try
                {
                    var openPrs = await GetCachedOpenPRsAsync(ct);
                    foreach (var orphan in openPrs)
                    {
                        if (orphan.Number == pr.Number) continue;
                        if (!IsCurrentRunScopePr(orphan)) continue;
                        if (orphan.Title.Contains(task.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.LogInformation(
                                "Closing orphan PR #{OrphanPr} for task {TaskId} (superseded by close-and-recreate of PR #{OriginalPr})",
                                orphan.Number, task.Id, pr.Number);
                            await ReviewService.AddCommentAsync(orphan.Number,
                                $"🔄 **Closing orphan PR** — superseded when task `{task.Id}` was reset " +
                                $"after merge conflicts on PR #{pr.Number}.", ct);
                            await PrService.CloseAsync(orphan.Number, ct);
                        }
                    }
                }
                catch (Exception orphanEx)
                {
                    Logger.LogDebug(orphanEx, "Failed to clean up orphan PRs for task {TaskId}", task.Id);
                }
            }

            Logger.LogInformation(
                "Closed conflicted PR #{PrNumber} ({Title}), will recreate from clean main (retry {Retry}/{Max})",
                pr.Number, pr.Title, retries + 1, MaxConflictRetries);
            LogActivity("task", $"🔄 Closed conflicted PR #{pr.Number} — will recreate from clean branch (retry {retries + 1}/{MaxConflictRetries})");

            if (!string.IsNullOrEmpty(pr.HeadBranch))
            {
                try { await BranchService.DeleteAsync(pr.HeadBranch, ct); }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not delete old branch {Branch}", pr.HeadBranch);
                }
            }

            _conflictRetryByIssue[retryKey] = retries + 1;
            PersistConflictRetryCounters();

            if (CurrentPrNumber == pr.Number)
            {
                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;
            }

            if (task is null || !task.IssueNumber.HasValue)
            {
                Logger.LogWarning("No task found for conflicted PR #{PrNumber} — cannot recreate", pr.Number);
                return;
            }

            var isPeOwned = pr.Title.StartsWith(Identity.DisplayName + ":", StringComparison.OrdinalIgnoreCase);

            // Reset task to Pending via the task manager
            await _taskManager.ResetToPendingAsync(task.IssueNumber.Value, ct);

            // Important: ResetToPendingAsync clears _agentAssignments but does not affect the in-process
            // ClaimedTaskRegistry. If we don't release, this task stays unclaimable and can deadlock the run.
            ClaimRegistry?.Release(task.IssueNumber.Value);

            // Merge-conflict retries should NOT count against the reacquisition cap.
            // The implementation was successful — only the merge failed due to concurrent
            // changes to shared files. Without this reset, tasks hit the cap after 3-4
            // merge conflicts and get permanently blocked.
            if (task.Id is not null)
            {
                _taskAcquisitionCounts.Remove(task.Id);
                _blockedTaskIds.Remove(task.Id);
            }

            if (isPeOwned)
            {
                Logger.LogInformation(
                    "SE task {TaskId} reset to Pending — will re-implement on next cycle", task.Id);
                UpdateStatus(AgentStatus.Idle, "Ready for next task");
            }
            else if (task.IssueNumber.HasValue)
            {
                // Engineer-owned: find the engineer and re-send the assignment
                var engineerAgentId = _agentAssignments
                    .FirstOrDefault(kv => kv.Value == task.IssueNumber.Value).Key;

                if (engineerAgentId is not null)
                {
                    _agentAssignments.Remove(engineerAgentId);

                    var engineer = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                        .Where(a => a.Identity.Id != Identity.Id)
                        .FirstOrDefault(a => a.Identity.Id == engineerAgentId);

                    if (engineer is not null)
                    {
                        await _taskManager.AssignTaskAsync(task.IssueNumber.Value, engineer.Identity.DisplayName, ct);
                        _agentAssignments[engineerAgentId] = task.IssueNumber.Value;

                        await MessageBus.PublishAsync(new IssueAssignmentMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = engineerAgentId,
                            MessageType = "IssueAssignment",
                            IssueNumber = task.IssueNumber.Value,
                            IssueTitle = task.Name,
                            Complexity = task.Complexity,
                            IssueUrl = task.IssueUrl
                        }, ct);

                        Logger.LogInformation(
                            "Re-assigned task {TaskId} (issue #{IssueNumber}) to {Engineer} after conflict recovery",
                            task.Id, task.IssueNumber, engineer.Identity.DisplayName);
                    }
                    else
                    {
                        Logger.LogWarning(
                            "Original engineer {AgentId} not found for task {TaskId} — will be reassigned",
                            engineerAgentId, task.Id);
                    }
                }
                else
                {
                    Logger.LogInformation("Task {TaskId} reset to Pending for reassignment", task.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close-and-recreate PR #{PrNumber}", pr.Number);
        }
    }

    /// <summary>
    /// Attempts to resolve a merge conflict in-place by merging the target branch into the
    /// PR branch and using Copilot CLI to resolve any conflicts. Returns true if the conflict
    /// was resolved and pushed successfully. Returns false if resolution failed (caller should
    /// fall back to close-and-recreate).
    /// </summary>
    private async Task<bool> TryResolveConflictInPlaceAsync(AgentPullRequest pr, CancellationToken ct)
    {
        if (Workspace is null) return false;

        try
        {
            // Checkout the PR branch
            await Workspace.CheckoutBranchAsync(pr.HeadBranch, ct);

            // Fetch latest target branch
            var fetchResult = await RunGitCommandAsync(Workspace.RepoPath, $"fetch origin {EffectiveBranch}", ct);
            if (fetchResult.ExitCode != 0)
            {
                Logger.LogWarning("Failed to fetch {Branch} for conflict resolution", EffectiveBranch);
                return false;
            }

            // Try git merge — leave conflicts in place (no --abort)
            var mergeResult = await RunGitCommandAsync(
                Workspace.RepoPath, $"merge origin/{EffectiveBranch} --no-edit", ct);

            if (mergeResult.ExitCode == 0)
            {
                // No conflicts — merge succeeded, just push
                await Workspace.PushAsync(pr.HeadBranch, ct);
                return true;
            }

            // There are conflicts — check which files
            var conflictResult = await RunGitCommandAsync(
                Workspace.RepoPath, "diff --name-only --diff-filter=U", ct);

            if (string.IsNullOrWhiteSpace(conflictResult.StdOut))
            {
                await RunGitCommandAsync(Workspace.RepoPath, "merge --abort", ct);
                return false;
            }

            var conflictFiles = conflictResult.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToList();

            Logger.LogInformation(
                "PR #{PrNumber} has {Count} conflicting file(s): {Files} — attempting CLI resolution",
                pr.Number, conflictFiles.Count, string.Join(", ", conflictFiles));

            // Use Copilot CLI to resolve conflicts — run directly in the workspace
            var copilotPath = VirtualDevTeam.Core.AI.FreshPathResolver.ResolveExecutable(
                Config.CopilotCli.ExecutablePath ?? "copilot")
                ?? Config.CopilotCli.ExecutablePath ?? "copilot";

            // Enrich prompt with acceptance criteria from the linked task issue
            var taskContext = "";
            try
            {
                var linkedWiIds = await PrService.GetLinkedWorkItemIdsAsync(pr.Number, ct);
                if (linkedWiIds.Count > 0)
                {
                    var wi = await WorkItemService.GetAsync(linkedWiIds[0], ct);
                    if (wi is not null)
                        taskContext = $"\n\n## Original Task Requirements\n{wi.Body}\n\n";
                }
                if (string.IsNullOrEmpty(taskContext) && !string.IsNullOrEmpty(pr.Body))
                    taskContext = $"\n\n## PR Description\n{pr.Body}\n\n";
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not load task context for conflict resolution prompt");
            }

            // Get git log of what changed on main since this branch diverged
            var mainChanges = "";
            try
            {
                var logResult = await RunGitCommandAsync(Workspace.RepoPath,
                    $"log origin/{EffectiveBranch} --oneline -10 -- {string.Join(" ", conflictFiles.Select(f => $"\"{f}\""))}", ct);
                if (logResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(logResult.StdOut))
                    mainChanges = $"\n\n## Recent changes on main affecting these files:\n{logResult.StdOut}\n";
            }
            catch { /* best effort */ }

            var resolvePrompt =
                $"Resolve ALL merge conflict markers (<<<<<<< / ======= / >>>>>>>) in the files in this repository. " +
                $"This PR ({pr.Title}) is being merged with the latest main branch. " +
                $"The conflicting files are: {string.Join(", ", conflictFiles)}. " +
                taskContext +
                mainChanges +
                "Choose the resolution that best preserves the intent of both changes — " +
                "typically both sides are adding new code (DI registrations, imports, routes) that should ALL be kept. " +
                "Ensure the code compiles and is semantically correct. " +
                "After resolving, run `dotnet build` to verify the resolution compiles. " +
                "Do NOT delete any functionality from either side unless one side clearly supersedes the other.";

            using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            resolveCts.CancelAfter(TimeSpan.FromMinutes(10));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = copilotPath,
                Arguments = "--allow-all --no-ask-user --silent --no-color --no-auto-update",
                WorkingDirectory = Workspace.RepoPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            bool cliResolved = false;
            System.Diagnostics.Process? cliProcess = null;
            try
            {
                cliProcess = System.Diagnostics.Process.Start(psi);
                if (cliProcess is not null)
                {
                    await cliProcess.StandardInput.WriteLineAsync(resolvePrompt);
                    cliProcess.StandardInput.Close();
                    await cliProcess.WaitForExitAsync(resolveCts.Token);
                    cliResolved = cliProcess.ExitCode == 0;
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout — kill the CLI process so it stops mutating the workspace
                Logger.LogWarning("CLI conflict resolution timed out (10 min) — killing process");
                try { cliProcess?.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            catch (Exception cliEx)
            {
                Logger.LogDebug(cliEx, "CLI conflict resolution process failed");
            }
            finally
            {
                cliProcess?.Dispose();
            }

            if (!cliResolved)
            {
                Logger.LogWarning("CLI conflict resolution failed — aborting merge");
                await RunGitCommandAsync(Workspace.RepoPath, "merge --abort", ct);
                return false;
            }

            // Stage ONLY the conflicting files — not everything (CLI may have created artifacts)
            var filesToStage = string.Join("\" \"", conflictFiles);
            await RunGitCommandAsync(Workspace.RepoPath, $"add -- \"{filesToStage}\"", ct);

            // Verify no conflict markers remain in staged files
            var checkResult = await RunGitCommandAsync(Workspace.RepoPath, "diff --cached --check", ct);
            if (checkResult.ExitCode != 0)
            {
                Logger.LogWarning("Conflict markers still present after CLI resolution — aborting");
                await RunGitCommandAsync(Workspace.RepoPath, "merge --abort", ct);
                return false;
            }

            // Build verification: ensure the resolved code actually compiles before committing.
            // Without this, semantic conflicts (e.g., duplicate DI registrations) pass marker
            // checks but break the build, wasting a retry on a known-broken push.
            if (BuildRunnerSvc is not null)
            {
                var wsConfig = Config.Workspace;
                var buildResult = await BuildRunnerSvc.BuildAsync(
                    Workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
                if (!buildResult.Success)
                {
                    Logger.LogWarning("Build failed after conflict resolution — aborting merge. Errors: {Errors}",
                        buildResult.Errors.Length > 200 ? buildResult.Errors[..200] : buildResult.Errors);
                    await RunGitCommandAsync(Workspace.RepoPath, "merge --abort", ct);
                    return false;
                }
            }

            // Commit the merge resolution
            await Workspace.CommitAsync(
                $"Merge {EffectiveBranch}: resolved {conflictFiles.Count} conflict(s) via Copilot CLI", ct);
            await Workspace.PushAsync(pr.HeadBranch, ct);

            await ReviewService.AddCommentAsync(pr.Number,
                $"✅ **Merge conflict auto-resolved** via Copilot CLI.\n\n" +
                $"Resolved {conflictFiles.Count} conflicting file(s): " +
                string.Join(", ", conflictFiles.Select(f => $"`{f}`")),
                ct);

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "In-place conflict resolution failed for PR #{PrNumber}", pr.Number);
            try { await RunGitCommandAsync(Workspace.RepoPath, "merge --abort", ct); }
            catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Builds the scenario verification section for T-FINAL prompts.
    /// Shared between strategy and legacy paths. Returns empty string if no scenarios.
    /// </summary>
    private async Task<string> BuildScenarioVerificationSectionAsync(CancellationToken ct)
    {
        if (_scenarioRegistry is null) return "";

        var scenarios = _scenarioRegistry.Current;
        if (scenarios.Count == 0)
        {
            try { scenarios = await _scenarioRegistry.LoadAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* best effort */ }
        }

        if (scenarios.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Scenario Verification (CRITICAL)");
        sb.AppendLine("The following approved scenarios are the acceptance contract for this project.");
        sb.AppendLine("Verify that the codebase implements each scenario's required behavior.\n");
        foreach (var s in scenarios)
        {
            sb.AppendLine($"### {s.Id}: {s.Title}");
            if (!string.IsNullOrWhiteSpace(s.Actor))
                sb.AppendLine($"**Actor:** {s.Actor}");
            if (!string.IsNullOrWhiteSpace(s.Trigger))
                sb.AppendLine($"**Trigger:** {s.Trigger}");
            if (s.ExpectedTerminalState.Count > 0)
                sb.AppendLine($"**Expected Outcome:** {string.Join("; ", s.ExpectedTerminalState)}");
            if (s.Steps.Count > 0)
                sb.AppendLine($"**Steps:** {string.Join(" → ", s.Steps)}");
            sb.AppendLine();
        }
        sb.AppendLine("If any scenario is NOT implemented or is missing critical wiring, add the necessary code.\n");
        return sb.ToString();
    }

    private async Task CheckAllTasksCompleteAsync(CancellationToken ct)
    {
        if (_taskManager.TotalCount == 0)
            return;

        UpdateStatus(AgentStatus.Working, $"📊 Checking completion: {_taskManager.DoneCount}/{_taskManager.TotalCount} tasks done");

        // Refresh from GitHub to get latest state
        await _taskManager.LoadTasksAsync(ct);

        // Check if all tasks EXCEPT the final integration task are done.
        // Match by ID ("T-FINAL") OR by name ("Final Integration & Validation") because
        // AssignTaskAsync replaces the title from "[T-FINAL] Name" to "Agent: Name",
        // and LoadTasksAsync then parses the ID from the title — falling back to "T-{N}"
        // when the [T-FINAL] bracket prefix is gone.
        var nonIntegrationTasks = _taskManager.Tasks
            .Where(t => t.Id != IntegrationTaskId
                        && !t.Name.Contains(IntegrationTaskName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonIntegrationTasks.Count == 0 || !nonIntegrationTasks.All(EngineeringTaskIssueManager.IsTaskPastImplementation))
            return;

        // Safety check: tasks that are "Done" (issue closed) but never went through the proper
        // completion flow (which sets status:implementation-complete label) were likely closed
        // externally (e.g., GitHub "Closes #N" auto-linking from another PR).
        // Re-open these tasks so they actually get implemented.
        // NOTE: We check labels, not PullRequestNumber, because PullRequestNumber is ephemeral
        // (lost after LoadTasksAsync reloads from platform). Labels persist on the platform.
        // EXCEPTION: tasks whose name matches a MERGED PR title from any engineering agent
        // (this SE leader, an SE worker, or an SME engineer role) are legitimately complete —
        // MarkDoneAsync doesn't add status:implementation-complete (the merge itself IS the
        // proof of completion). Without this exemption, every restart of this loop tries to
        // re-implement merged tasks.
        //
        // 2026-05-11 broadening (post-run-restart-t1-duplicate): the prior filter only matched
        // PRs whose title started with THIS agent's display name, so SME-engineer-merged work
        // (e.g. "Game Developer 1: T1 Project Foundation") was invisible. We now harvest the
        // task title from ANY merged PR on an engineering branch (per
        // EngineeringTaskIssueManager.IsEngineeringPrBranch) regardless of which role merged it.
        HashSet<string> mergedPrTaskTitles;
        try
        {
                    var mergedPRs = (await GetCachedMergedPRsAsync(ct))
                        .Where(IsCurrentRunScopePr)
                        .ToList();
            // Filter to CURRENT run scope so prior-run merged PRs don't spuriously exempt
            // current-run orphan tasks that happen to share a title.
            var currentRunScope = BranchProvider?.RunScope;
            mergedPrTaskTitles = new HashSet<string>(
                mergedPRs
                    .Where(pr => EngineeringTaskIssueManager.IsEngineeringPrBranch(pr.HeadBranch))
                    .Where(pr => IsCurrentRunScopePr(pr))
                    .Select(pr =>
                    {
                        var idx = pr.Title.IndexOf(':');
                        return idx >= 0 ? pr.Title[(idx + 1)..].Trim() : pr.Title;
                    })
                    .Where(t => !string.IsNullOrWhiteSpace(t)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to list merged PRs for orphan-task check; proceeding without exemption");
            mergedPrTaskTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var closedWithoutPr = nonIntegrationTasks
            .Where(t => EngineeringTaskIssueManager.IsTaskDone(t)
                        && !t.Labels.Any(l => string.Equals(l,
                            EngineeringTaskIssueManager.StatusImplementationComplete,
                            StringComparison.OrdinalIgnoreCase))
                        && !mergedPrTaskTitles.Contains(t.Name)
                        // 2026-05-13 fix: real orphans had an agent assignment that
                        // didn't produce a PR. Manually-closed issues (operator cleanup)
                        // have AssignedTo=null and should NOT match. Without this,
                        // every restart of a cleaned-up project loops on the same
                        // false-positive orphans, never reaching _allTasksComplete=true.
                        && !string.IsNullOrEmpty(t.AssignedTo))
            .ToList();

        if (closedWithoutPr.Count > 0)
        {
            // 2026-05-13 fix (orphan-detection-false-positive-on-manual-close):
            // The earlier change to pass allowReopen:true here caused continuous loops:
            // operator-manually-closed issues match all 3 orphan conditions (Done in
            // cache + no implementation-complete label + no merged PR for the title)
            // because gh-cli close doesn't transition labels and never creates a PR.
            // Auto-reopen → SE picks it up → PR created for already-done work → wastes
            // $0.5-2/incident in LLM. Worse, it repeats on every restart.
            //
            // We can't reliably distinguish manual-close from real-orphan without
            // expensive per-issue API calls to check the closing user. So drop the
            // auto-reopen entirely: log a Warning + emit a finding for the FlowMonitor
            // surface, and let the operator manually reopen if a real orphan slipped
            // through. The system used to claim auto-reopen was the legitimate path
            // for a rare bug case; in practice manual-close is FAR more common.
            //
            // 2026-05-13 part-2: also REMOVED the early `return;` that previously
            // blocked T-FINAL from ever triggering. Dropping orphans from cache is
            // a non-fatal cleanup — the completion check should still proceed.
            // Without this, the runner deadlocks: orphans get dropped → return →
            // next tick re-loads them → drop → return → forever.
            Logger.LogWarning(
                "Found {Count} task(s) marked Done with no associated PR (closed without implementation). " +
                "NOT auto-reopening (the legitimate auto-reopen scenario is rare; manual-close is common). " +
                "Operator should manually reopen if these are genuinely orphaned work. Tasks: {Tasks}",
                closedWithoutPr.Count,
                string.Join(", ", closedWithoutPr.Select(t => $"{t.Id} (#{t.IssueNumber})")));

            foreach (var orphanTask in closedWithoutPr)
            {
                if (orphanTask.IssueNumber.HasValue)
                {
                    // allowReopen:false → ResetToPendingAsync will detect closed state,
                    // refuse to reopen, and drop from local cache. This prevents the
                    // recovery loop from re-attempting the same false-positive each tick.
                    await _taskManager.ResetToPendingAsync(orphanTask.IssueNumber.Value, ct, allowReopen: false);
                    ClaimRegistry?.Release(orphanTask.IssueNumber.Value);
                    LogActivity("warning", $"⚠️ Task {orphanTask.Id} (#{orphanTask.IssueNumber}) marked Done with no PR — dropped from cache; operator can manually reopen if real orphan");
                }
            }
            // Fall through to completion check — these dropped tasks are no longer in
            // the cache, so the next code block's evaluation works on a clean view.
            // Re-fetch nonIntegrationTasks since we just modified _taskManager.Tasks.
            nonIntegrationTasks = _taskManager.Tasks
                .Where(t => !IsIntegrationTask(t))
                .ToList();
        }

        // Gate: all task PRs must be MERGED before starting integration.
        // Tasks are marked Done when PRs get approval labels (for restart recovery),
        // but integration requires the code to actually be on the target branch.
        // CRITICAL: scope filter — open PRs from a prior run share this SE's display
        // name and title format but their branches are agent/{oldScope}/... and do not
        // belong to current-run tasks. Without the scope filter they permanently block
        // T-FINAL on every new run after a restart.
        var openPRs = await GetCachedOpenPRsAsync(ct);
        var agentPrefix = $"{Identity.DisplayName}:";
        var integrationRunScope = BranchProvider?.RunScope;
        var unmergerdTaskPRs = openPRs
            .Where(pr => pr.Title.StartsWith(agentPrefix, StringComparison.OrdinalIgnoreCase)
                         && !PullRequestWorkflow.Labels.IsFinalIntegrationPr(pr.Labels, pr.Title, pr.HeadBranch)
                         && PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels)
                         && IsCurrentRunScopePr(pr))
            .ToList();

        if (unmergerdTaskPRs.Count > 0)
        {
            Logger.LogInformation(
                "All tasks marked Done but {Count} task PR(s) still awaiting merge: {PRs}",
                unmergerdTaskPRs.Count,
                string.Join(", ", unmergerdTaskPRs.Select(p => $"#{p.Number}")));
            UpdateStatus(AgentStatus.Working,
                $"Waiting for {unmergerdTaskPRs.Count} PR(s) to merge before integration");
            return;
        }

        _allTasksComplete = true;
        Logger.LogInformation("🎉 All {Count} engineering tasks are complete! Starting final integration.",
            nonIntegrationTasks.Count);
        LogActivity("system", $"🎉 All {nonIntegrationTasks.Count} engineering tasks complete — entering integration phase");
        UpdateStatus(AgentStatus.Working, "All tasks complete — starting final integration & validation");

        // Self-assign the integration issue
        if (_integrationIssueNumber is not null)
        {
            await _taskManager.AssignTaskAsync(_integrationIssueNumber.Value, Identity.DisplayName, ct);
            Logger.LogInformation("Self-assigned integration issue #{IssueNumber}", _integrationIssueNumber);
        }

        await PublishStatusAsync("AllTasksComplete", AgentStatus.Working,
            details: $"All {nonIntegrationTasks.Count} engineering tasks are done — starting final integration",
            ct: ct);
    }

    private async Task CreateIntegrationPRAsync(CancellationToken ct)
    {
        // Guard: verify all dependency PRs are merged before starting T-FINAL.
        // T-FINAL runs against the integrated codebase — if any task PR is still
        // in review/open, the integration result will be incomplete.
        try
        {
            var openPrs = await GetCachedOpenPRsAsync(ct);
            var openEngineeringPrs = openPrs
                .Where(p => EngineeringTaskIssueManager.IsEngineeringPrBranch(p.HeadBranch)
                    && IsCurrentRunScopePr(p)
                    && !PullRequestWorkflow.Labels.IsFinalIntegrationPr(p.Labels, p.Title, p.HeadBranch))
                .ToList();
            if (openEngineeringPrs.Count > 0)
            {
                var prNumbers = string.Join(", ", openEngineeringPrs.Select(p => $"#{p.Number}"));
                Logger.LogInformation(
                    "T-FINAL deferred: {Count} engineering PR(s) still open ({PRs}) — waiting for all to merge",
                    openEngineeringPrs.Count, prNumbers);
                UpdateStatus(AgentStatus.Idle,
                    $"Waiting for {openEngineeringPrs.Count} PR(s) to merge before T-FINAL: {prNumbers}");
                _allTasksComplete = false; // reset so the next loop iteration re-checks
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to check open PRs before T-FINAL — proceeding anyway");
        }

        var integrationStepId = TaskTracker.BeginStep(Identity.Id, "pe-integration", "Final integration review",
            "Reviewing merged work for integration gaps (missing wiring, imports, config)", Identity.ModelTier);
        try
        {
            UpdateStatus(AgentStatus.Working, "Creating integration PR");

            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var techStack = Config.Project.TechStack;

            // Build a task summary from issues for context
            var taskSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
                $"- [{t.Id}] {t.Name} ({t.Complexity}, {t.Status})"));

            // Try strategy framework first (gives dashboard visibility + multi-candidate eval)
            if (await TryCreateIntegrationPRViaStrategyAsync(
                    pmSpecDoc, architectureDoc, techStack, taskSummary, integrationStepId, ct))
            {
                return;
            }

            // Fallback: legacy single-shot LLM integration review
            await CreateIntegrationPRLegacyAsync(
                pmSpecDoc, architectureDoc, techStack, taskSummary, integrationStepId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create integration PR");
            RecordError($"Integration PR failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            TaskTracker.FailStep(integrationStepId, ex.Message);
            // Signal completion anyway so the pipeline doesn't get stuck
            _integrationPrCreated = true;
            await SignalEngineeringCompleteAsync(ct);
        }
    }

    /// <summary>
    /// Attempts to run T-FINAL integration through the strategy framework for dashboard visibility
    /// and multi-candidate evaluation. Returns true if it handled the integration (whether fixes
    /// were needed or not). Returns false to signal caller should fall back to legacy path.
    /// </summary>
    private async Task<bool> TryCreateIntegrationPRViaStrategyAsync(
        string pmSpecDoc, string architectureDoc, string techStack, string taskSummary,
        string integrationStepId, CancellationToken ct)
    {
        if (_strategyOrchestrator is null || _winnerApply is null || _strategyConfig is null || Workspace is null)
            return false;

        var cfg = _strategyConfig.CurrentValue;
        if (!cfg.Enabled || cfg.EnabledStrategies.Count == 0)
            return false;

        // T-FINAL integration tasks bypass strategies by default. Strategies consistently
        // fail on T-FINAL (copilot-cli exhausts tool-call-cap reading the full codebase,
        // squad fails gate2-build on test infrastructure) and add ~18 min overhead with
        // zero value. The SE always falls back to legacy single-pass anyway.
        // Only consulted when Enabled is true — when Enabled=false, strategies are
        // already bypassed globally before reaching this point.
        if (cfg.SkipStrategiesForFinalIntegration)
        {
            Logger.LogInformation(
                "Strategy framework skipped for T-FINAL (SkipStrategiesForFinalIntegration=true)");
            return false;
        }
        var integrationDescSb = new System.Text.StringBuilder();
        integrationDescSb.AppendLine("All individual task PRs have been merged to the target branch.");
        integrationDescSb.AppendLine("Your job is to perform FINAL INTEGRATION VALIDATION — verify the complete system works end-to-end.");
        integrationDescSb.AppendLine();

        // IMPORTANT: This is a validation task. If we ask the agentic strategy to "review everything" it
        // tends to crawl the entire codebase and exhaust the tool-call cap without making forward progress.
        // Force a deterministic workflow: run build/tests FIRST, then only inspect files implicated by failures.
        integrationDescSb.AppendLine("## IMPORTANT (Validation Task)");
        integrationDescSb.AppendLine("- Start by running build/tests FIRST. Do NOT do a full-codebase audit.");
        integrationDescSb.AppendLine("- Only open/read files that are directly implicated by a build/test failure.");
        integrationDescSb.AppendLine("- Stop as soon as build + tests are green.");
        integrationDescSb.AppendLine("- **Parallelize where possible**: run backend build and frontend install simultaneously, run test suites in parallel, don't wait for one to finish before starting the next.");
        integrationDescSb.AppendLine();

        integrationDescSb.AppendLine("## Step 1: Build & Test Verification (run these first)");
        
        // Detect full-stack project: if TechStack mentions frontend frameworks or a client/ dir exists
        var hasFrontend = !string.IsNullOrWhiteSpace(techStack) && (
            techStack.Contains("React", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("Vue", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("Angular", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("Vite", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("Next", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("npm", StringComparison.OrdinalIgnoreCase) ||
            techStack.Contains("TypeScript", StringComparison.OrdinalIgnoreCase));

        integrationDescSb.AppendLine("### Backend");
        integrationDescSb.AppendLine("- Find the .sln file and run: `dotnet build <solution>.sln`");
        integrationDescSb.AppendLine("- Run: `dotnet test <solution>.sln`");
        integrationDescSb.AppendLine();

        if (hasFrontend)
        {
            integrationDescSb.AppendLine("### Frontend (CRITICAL — do not skip!)");
            integrationDescSb.AppendLine("- Find the `client/` or frontend directory with `package.json`");
            integrationDescSb.AppendLine("- Run: `npm install` (or `npm ci`)");
            integrationDescSb.AppendLine("- Run: `npm run build` (TypeScript compilation + bundler)");
            integrationDescSb.AppendLine("- Run: `npm test` (if test script exists)");
            integrationDescSb.AppendLine("- Frontend build failures are integration bugs just like backend build failures.");
            integrationDescSb.AppendLine();
            integrationDescSb.AppendLine("### Speed tip: run backend and frontend in parallel");
            integrationDescSb.AppendLine("- Start `dotnet build` and `npm install` at the same time — they are independent.");
            integrationDescSb.AppendLine("- While backend tests run, start the frontend build.");
            integrationDescSb.AppendLine("- Run test suites (dotnet test, npm test, UI tests) in parallel when possible.");
            integrationDescSb.AppendLine();
        }

        integrationDescSb.AppendLine("### E2E / Playwright / UI Tests (do NOT skip!)");
        integrationDescSb.AppendLine("If the project has E2E or Playwright tests that require a running server:");
        integrationDescSb.AppendLine("1. Start the server (e.g., `dotnet run` in the API project) in the background");
        integrationDescSb.AppendLine("2. Wait for it to be ready (check the health endpoint or port)");
        integrationDescSb.AppendLine("3. Run the E2E/Playwright tests against the live server");
        integrationDescSb.AppendLine("4. Stop the server when tests complete");
        integrationDescSb.AppendLine("These tests verify the real integrated system — skipping them defeats the purpose of T-FINAL validation.");
        integrationDescSb.AppendLine();

        integrationDescSb.AppendLine("- If applicable, briefly start the app to confirm it boots without crashing");
        integrationDescSb.AppendLine();

        integrationDescSb.AppendLine("## Step 2: Fix ONLY what fails");
        integrationDescSb.AppendLine("If build or tests fail, make the MINIMUM code/config changes required to get green.");
        integrationDescSb.AppendLine("After each fix, rerun the commands above until everything passes.");
        integrationDescSb.AppendLine("If build + tests pass on the first try, you are DONE — do not explore or audit the codebase.");
        integrationDescSb.AppendLine();
        integrationDescSb.AppendLine("### Timeout failures");
        integrationDescSb.AppendLine("If a test fails due to a timeout (e.g., Playwright 30s default), DOUBLE the timeout and rerun.");
        integrationDescSb.AppendLine("Timeouts on pages with heavy JS bundles (Swagger, dashboards) are expected — increase the timeout, don't skip or delete the test.");
        integrationDescSb.AppendLine("Use `WaitUntil = WaitUntilState.DOMContentLoaded` instead of `Load` for pages known to have heavy client-side JS.");
        integrationDescSb.AppendLine();

        // Inject approved scenarios as verification contracts
        if (_scenarioRegistry is not null)
        {
            var scenarios = _scenarioRegistry.Current;
            if (scenarios.Count == 0)
            {
                try { scenarios = await _scenarioRegistry.LoadAsync(ct); }
                catch { /* best effort */ }
            }

            if (scenarios.Count > 0)
            {
                integrationDescSb.AppendLine("## Step 3: Scenario Verification (MANDATORY)");
                integrationDescSb.AppendLine("You MUST verify each scenario below. For each one:");
                integrationDescSb.AppendLine("1. Check that the code implementing it exists and is wired correctly");
                integrationDescSb.AppendLine("2. Verify via existing automated tests OR by tracing the code path");
                integrationDescSb.AppendLine("3. Record a verdict: ✅ verified, ❌ broken (with reason), or ⚠️ inconclusive");
                integrationDescSb.AppendLine();
                integrationDescSb.AppendLine("After verification, output a **Scenario Verification Report** table:");
                integrationDescSb.AppendLine("```");
                integrationDescSb.AppendLine("| ID | Title | Verdict | Evidence |");
                integrationDescSb.AppendLine("|----|-------|---------|----------|");
                integrationDescSb.AppendLine("| S01 | <title> | ✅ verified | <one-line evidence> |");
                integrationDescSb.AppendLine("```");
                integrationDescSb.AppendLine();
                foreach (var s in scenarios)
                {
                    integrationDescSb.AppendLine($"### {s.Id}: {s.Title}");
                    if (!string.IsNullOrWhiteSpace(s.Actor))
                        integrationDescSb.AppendLine($"**Actor:** {s.Actor}");
                    if (!string.IsNullOrWhiteSpace(s.Trigger))
                        integrationDescSb.AppendLine($"**Trigger:** {s.Trigger}");
                    if (s.ExpectedTerminalState.Count > 0)
                        integrationDescSb.AppendLine($"**Expected Outcome:** {string.Join("; ", s.ExpectedTerminalState)}");
                    if (s.Steps.Count > 0)
                        integrationDescSb.AppendLine($"**Steps:** {string.Join(" → ", s.Steps)}");
                    integrationDescSb.AppendLine();
                }
                integrationDescSb.AppendLine("If any scenario is NOT implemented or is missing critical wiring, fix it before declaring integration complete.");
                integrationDescSb.AppendLine("If a broken scenario requires fixes, apply them and re-run the relevant tests.");
                integrationDescSb.AppendLine();
            }
        }

        integrationDescSb.AppendLine("## Completed Tasks");
        integrationDescSb.AppendLine(taskSummary);
        integrationDescSb.AppendLine();

        integrationDescSb.AppendLine("## Stop Condition");
        integrationDescSb.AppendLine("- If ALL builds (backend + frontend if applicable) and ALL tests pass on the first try, stop immediately — the integration is clean.");
        integrationDescSb.AppendLine("- If fixes were required, commit the minimal fixes and stop once ALL builds + tests are green.");
        integrationDescSb.AppendLine("- Do NOT continue exploring, auditing, or cleaning up after tests pass. Commit and stop.");
        integrationDescSb.AppendLine();

        integrationDescSb.AppendLine("## REQUIRED: Final Integration Report");
        integrationDescSb.AppendLine("Before stopping, you MUST create a file `AgentDocs/FinalIntegrationReport.md` with a summary of everything you verified:");
        integrationDescSb.AppendLine("- **Build Results**: Which builds passed/failed and any errors encountered");
        integrationDescSb.AppendLine("- **Test Results**: Which tests passed/failed with counts (e.g., 21/21 passed)");
        integrationDescSb.AppendLine("- **Scenario Verification**: Which user scenarios were verified and their status");
        integrationDescSb.AppendLine("- **Fixes Applied**: Any integration fixes made (or 'None needed — clean integration')");
        integrationDescSb.AppendLine("- **Recommended Next Steps**: List 5-10 concrete recommendations to take this project/feature to the next level — improvements for polish, robustness, UX, performance, accessibility, error handling, test coverage, or production-readiness that go beyond the current scope. Prioritize by impact.");
        integrationDescSb.AppendLine("This report is the primary deliverable of T-FINAL — commit it even if no code fixes were needed.");

        var integrationDescription = integrationDescSb.ToString();

        // ── Compute focus files from merged PRs ──
        // Prioritize cross-cutting files (appear in multiple PRs) and known integration
        // hotspots (DI registrations, project files, package manifests). Provides the
        // agentic CLI a bounded file list instead of letting it crawl the entire codebase.
        List<string>? focusFiles = null;
        try
        {
            var mergedPRs = await GetCachedMergedPRsAsync(ct);
            var currentRunMerged = mergedPRs
                .Where(IsCurrentRunScopePr)
                .Where(p => !PullRequestWorkflow.Labels.IsFinalIntegrationPr(p.Labels, p.Title, p.HeadBranch))
                .ToList();

            if (currentRunMerged.Count > 0)
            {
                var fileOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var mpr in currentRunMerged.Take(15))
                {
                    try
                    {
                        var prFiles = await PrService.GetChangedFilesAsync(mpr.Number, ct);
                        foreach (var f in prFiles)
                        {
                            var normalized = f.Replace('\\', '/');
                            fileOccurrences[normalized] = fileOccurrences.GetValueOrDefault(normalized) + 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "T-FINAL: failed to get changed files for PR #{Number}", mpr.Number);
                    }
                }

                // Prioritize: files in multiple PRs (cross-cutting), then known integration hotspots
                var crossCuttingPatterns = new[] { "Program.cs", ".csproj", "package.json", "appsettings", "Startup", "Extensions.cs", ".sln" };
                var ranked = fileOccurrences
                    .OrderByDescending(kv => kv.Value) // multi-PR files first
                    .ThenByDescending(kv => crossCuttingPatterns.Any(p => kv.Key.Contains(p, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
                    .ThenBy(kv => kv.Key)
                    .Select(kv => kv.Key)
                    .Take(50)
                    .ToList();

                if (ranked.Count > 0)
                {
                    focusFiles = ranked;
                    Logger.LogInformation("T-FINAL: computed {Count} focus files from {PRCount} merged PRs ({MultiPR} appear in multiple PRs)",
                        ranked.Count, currentRunMerged.Count,
                        fileOccurrences.Count(kv => kv.Value > 1));

                    // Inject focus file list into the prompt for CLI guidance
                    integrationDescSb.AppendLine();
                    integrationDescSb.AppendLine("## Focus Files (from merged PRs — check these if build/test fails)");
                    integrationDescSb.AppendLine("These files were changed across the merged PRs. If build/tests fail, start debugging here:");
                    foreach (var f in ranked.Take(30))
                    {
                        var count = fileOccurrences[f];
                        integrationDescSb.AppendLine(count > 1
                            ? $"- `{f}` (changed in {count} PRs — likely integration point)"
                            : $"- `{f}`");
                    }
                    if (ranked.Count > 30)
                        integrationDescSb.AppendLine($"- ... and {ranked.Count - 30} more files");
                    integrationDescSb.AppendLine();
                    integrationDescription = integrationDescSb.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "T-FINAL: failed to compute focus files — proceeding without");
        }

        // ── Scan for remaining AI_STUB / AI_TODO markers ──
        // These markers were placed by T1 and should have been replaced by later tasks.
        // Any remaining stubs indicate potentially incomplete implementations.
        try
        {
            var stubReport = await ScanForRemainingStubMarkersAsync(ct);
            if (!string.IsNullOrEmpty(stubReport))
            {
                Logger.LogWarning("T-FINAL: found remaining AI_STUB/AI_TODO markers in workspace");
                integrationDescSb.AppendLine();
                integrationDescSb.AppendLine("## Remaining Stub Markers (AI_STUB / AI_TODO)");
                integrationDescSb.AppendLine("The following stub markers from the scaffolding phase were NOT replaced by later tasks.");
                integrationDescSb.AppendLine("Assess each one: is it an incomplete feature (fix it) or intentionally deferred (document why in the report)?");
                integrationDescSb.AppendLine();
                integrationDescSb.AppendLine(stubReport);
                integrationDescSb.AppendLine();
                integrationDescription = integrationDescSb.ToString();
            }
            else
            {
                Logger.LogInformation("T-FINAL: no remaining AI_STUB/AI_TODO markers found — all stubs replaced");
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "T-FINAL: failed to scan for stub markers — proceeding without");
        }

        // Pull latest target branch BEFORE creating the T-FINAL branch — ensures we start
        // from the exact state after all W1/W2 PRs merged. Without this, the local workspace
        // may be behind and the T-FINAL branch will have merge conflicts with changes that
        // were merged while earlier tasks were being reviewed/tested.
        try
        {
            await Workspace.SyncWithMainAsync(ct);
            Logger.LogInformation("T-FINAL: synced local workspace with {Branch} before branching", EffectiveBranch);
        }
        catch (Exception syncEx)
        {
            Logger.LogWarning(syncEx, "T-FINAL: failed to sync with {Branch} — proceeding with current HEAD", EffectiveBranch);
        }

        // Create branch BEFORE orchestration — WinnerApplyService needs it to exist
        // When ForceRedoFinalIntegration is set, the old integration branch may still exist
        // from a prior T-FINAL run. Try to delete it so CreateTaskBranchAsync can create fresh.
        if (cfg.ForceRedoFinalIntegration)
        {
            try
            {
                // List branches matching the integration pattern and delete them
                var branches = await BranchService.ListAsync("agent/", ct);
                foreach (var b in branches.Where(n =>
                    n.Contains("final-integration", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await BranchService.DeleteAsync(b, ct);
                        Logger.LogInformation("ForceRedoFinalIntegration: deleted old integration branch {Branch}", b);
                    }
                    catch { /* best effort */ }
                }
            }
            catch (Exception delEx)
            {
                Logger.LogDebug(delEx, "ForceRedoFinalIntegration: could not clean old integration branches");
            }
        }
        var branchName = await PrWorkflow.CreateTaskBranchAsync(
            Identity.DisplayName, "final-integration", ct);

        // Fetch and checkout the branch locally — WinnerApplyService needs it for git rev-parse
        await Workspace.CheckoutBranchAsync(branchName, ct);

        string localHead;
        try
        {
            localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();
        }
        catch
        {
            localHead = "HEAD";
        }

        var runId = StateStore.LastBootUtc != DateTime.MinValue
            ? StateStore.LastBootUtc.ToString("yyyyMMddTHHmmssZ")
            : "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

        var taskCtx = new TaskContext
        {
            TaskId = IntegrationTaskId,
            TaskTitle = "Final Integration",
            TaskDescription = integrationDescription,
            PrBranch = branchName,
            BaseSha = localHead,
            RunId = runId,
            AgentRepoPath = Workspace.RepoPath,
            Complexity = 3, // Medium-high: cross-module wiring
            IsWebTask = LooksLikeWebTask(techStack, "Final Integration", integrationDescription),
            // T-FINAL is conceptually the integration wave — surface that on the dashboard.
            Wave = "W-FINAL",
            // T-FINAL prompts can explode in size (PM spec + Architecture + scenario list),
            // which pushes the agentic CLI into "crawl everything" mode. Provide enough context
            // to orient, but keep it bounded so the agent prioritizes build/test execution.
            PmSpec = pmSpecDoc.Length > 15_000 ? pmSpecDoc[..15_000] + "\n...(truncated)" : pmSpecDoc,
            Architecture = architectureDoc.Length > 15_000 ? architectureDoc[..15_000] + "\n...(truncated)" : architectureDoc,
            TechStack = techStack,
            IssueContext = "",
            DesignContext = "",
            FocusFiles = focusFiles,
            ExistingProjectContext = Config.Project.ExistingProjectContext,
            // T-FINAL validation needs more headroom than normal code-gen tasks.
            // Normal tasks create files from scratch (fast, focused); T-FINAL validates
            // a whole integrated codebase (build, test, potentially fix wiring).
            ToolCallCapOverride = 1000,
        };

        UpdateStatus(AgentStatus.Working, "Strategy candidates: Final Integration");

        // Register with the task-step bridge for Frameworks dashboard visibility
        var enabledCount = cfg.EnabledStrategies.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var containerStepId = _strategyStepBridge?.RegisterTask(taskCtx.RunId, IntegrationTaskId, Identity.Id, enabledCount);

        var outcome = await _strategyOrchestrator.RunCandidatesAsync(taskCtx, ct);

        // No winner → check if all strategies legitimately found "no fixes needed"
        if (!outcome.HasWinner)
        {
            // Only count candidates that actually EXECUTED successfully and produced an empty patch
            // as evidence of clean integration. Candidates that failed to start (framework-not-ready)
            // or produced garbage (parser failures) are NOT evidence — they simply didn't run.
            var successfullyRanButEmpty = outcome.Evaluation.Candidates
                .Where(c => c.Execution.Succeeded && string.IsNullOrWhiteSpace(c.Patch))
                .ToList();
            var totalSuccessfullyRan = outcome.Evaluation.Candidates
                .Count(c => c.Execution.Succeeded);

            // Require at least ONE candidate that ran successfully and found nothing to fix.
            // Require that ALL candidates that ran successfully produced empty patches (no disagreement).
            if (successfullyRanButEmpty.Count > 0 && successfullyRanButEmpty.Count == totalSuccessfullyRan)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId,
                    succeeded: true, winnerStrategy: null);
                Logger.LogInformation(
                    "Strategy framework: {Count}/{Total} candidates that executed successfully produced empty patches — no integration fixes needed",
                    successfullyRanButEmpty.Count, outcome.Evaluation.Candidates.Count);
                LogActivity("task", $"✅ No integration fixes needed ({successfullyRanButEmpty.Count} of {outcome.Evaluation.Candidates.Count} strategies ran successfully and found nothing to fix)");
                _integrationPrCreated = true;
                TaskTracker.CompleteStep(integrationStepId);
                await CloseIntegrationIssueAsync(
                    $"✅ No integration fixes needed — {successfullyRanButEmpty.Count} strategy candidate(s) examined the code and found no issues " +
                    $"({outcome.Evaluation.Candidates.Count - totalSuccessfullyRan} candidate(s) failed to execute).", ct);
                await SignalEngineeringCompleteAsync(ct);
                return true;
            }

            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId,
                succeeded: false, winnerStrategy: null);
            Logger.LogInformation(
                "Strategy framework: no winner for T-FINAL ({Reason}); falling back to legacy path",
                outcome.Evaluation.TieBreakReason ?? "no candidates succeeded");
            return false;
        }

        var winner = outcome.Evaluation.Winner!;

        // Empty patch or stub-only → genuine "no integration fixes needed"
        if (string.IsNullOrEmpty(winner.Patch) || IsStubMarkerOnlyPatch(winner.Patch))
        {
            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId,
                succeeded: true, winnerStrategy: winner.StrategyId);
            Logger.LogInformation(
                "Strategy framework: winner {Strategy} produced no meaningful changes for T-FINAL — no integration fixes needed",
                winner.StrategyId);
            LogActivity("task", "✅ No integration fixes needed (strategy framework confirmed) — signaling completion");
            _integrationPrCreated = true;
            TaskTracker.CompleteStep(integrationStepId);
            await CloseIntegrationIssueAsync("✅ No integration fixes needed — strategy framework confirmed all tasks cleanly integrated.", ct);
            await SignalEngineeringCompleteAsync(ct);
            return true;
        }

        // Apply the winning patch — re-capture localHead right before apply since strategy
        // evaluation may have taken 15+ min and SyncWithMainAsync could rebase the branch.
        localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();

        // Primary: file-level copy from candidate worktree (avoids git apply brittleness).
        var winnerWorktreePath = outcome.Evaluation.WinnerWorktreePath;
        ApplyOutcome apply;
        if (!string.IsNullOrEmpty(winnerWorktreePath) && Directory.Exists(winnerWorktreePath))
        {
            apply = await _winnerApply.ApplyFromWorktreeAsync(
                Workspace.RepoPath, branchName, localHead, winnerWorktreePath, ct);
            // Fall back to patch-based apply when file-copy fails for any recoverable reason
            if (!apply.Applied && (apply.FailureReason?.StartsWith("overlap") == true
                                || apply.FailureReason == "worktree-no-changes"))
            {
                Logger.LogInformation(
                    "T-FINAL file-copy failed ({Reason}); falling back to 3-way patch apply",
                    apply.FailureReason);
                if (!string.IsNullOrWhiteSpace(winner.Patch))
                {
                    apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
                }
                else
                {
                    Logger.LogError(
                        "T-FINAL: winner {Strategy} worktree apply returned {Reason} AND winner.Patch is empty — no recovery path",
                        winner.StrategyId, apply.FailureReason);
                }
            }
        }
        else
        {
            apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
        }

        // Dispose the winner worktree handle now
        if (outcome.Evaluation.WinnerWorktreeHandle is not null)
        {
            try { await outcome.Evaluation.WinnerWorktreeHandle.DisposeAsync(); }
            catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose winner worktree handle"); }
        }

        if (!apply.Applied)
        {
            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId, succeeded: false);
            Logger.LogWarning(
                "Strategy framework: winner {Strategy} apply failed for T-FINAL: {Reason}; falling back",
                winner.StrategyId, apply.FailureReason);
            return false;
        }

        // Build-verify before committing
        var wsConfig = Config.Workspace;
        var build = await BuildRunnerSvc!.BuildAsync(
            Workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
        if (!build.Success)
        {
            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId, succeeded: false);
            Logger.LogWarning("Strategy framework: T-FINAL winner {Strategy} build failed; reverting and falling back",
                winner.StrategyId);
            await Workspace.RevertUncommittedChangesAsync(ct);
            return false;
        }

        // Commit with strategy trailers
        var trailers = new Dictionary<string, string>
        {
            [StrategyTrailers.StrategyKey] = SanitizeTrailerValue(winner.StrategyId),
            [StrategyTrailers.RunIdKey] = SanitizeTrailerValue(runId),
        };
        var tieBreak = outcome.Evaluation.TieBreakReason;
        if (!string.IsNullOrWhiteSpace(tieBreak))
            trailers[StrategyTrailers.TieBreakKey] = SanitizeTrailerValue(tieBreak);

        var subject = "Integration fixes: wiring, config, and cross-module references";
        var commitBody = $"Generated by strategy '{winner.StrategyId}' (run {runId}).";
        var fullMessage = StrategyTrailers.Append($"{subject}\n\n{commitBody}\n", trailers);
        await Workspace.CommitAsync(fullMessage, ct);

        // Create PR
        var prBody = $"## Final Integration PR\n\n" +
            $"All {_taskManager.TotalCount} engineering tasks have been completed and merged.\n" +
            $"This PR addresses integration gaps identified during final review.\n\n" +
            $"Strategy: `{winner.StrategyId}` | Run: `{runId}`\n\n" +
            $"<!-- winner-strategy: {winner.StrategyId} -->";

        var pr = await PrWorkflow.CreateTaskPullRequestAsync(
            Identity.DisplayName,
            "Final Integration",
            prBody,
            "High",
            "Architecture.md",
            "",
            branchName,
            [PullRequestWorkflow.Labels.FinalIntegration],
            ct);

        // Surface a clickable PR link on the Frameworks dashboard for the integration task.
        // (Strategies have already completed by now — RecordTaskPrLinked back-fills the recent snapshot.)
        try
        {
            await _strategyOrchestrator!.EmitTaskPrLinkedAsync(
                taskCtx.RunId, IntegrationTaskId, pr.Number, pr.Url, pr.Title, ct);
        }
        catch (Exception linkEx)
        {
            Logger.LogDebug(linkEx, "Failed to emit TaskPrLinked event for integration PR #{PrNumber}", pr.Number);
        }

        // Link T-FINAL issue to PR — ensures "Closes #N" in body for auto-close on merge
        // and timeline association. Without this, the T-FINAL PR is orphaned from its issue.
        var integrationTask = _taskManager.Tasks.FirstOrDefault(IsIntegrationTask);
        if (integrationTask?.IssueNumber is int integrationIssueNum)
        {
            try
            {
                await PrService.LinkWorkItemAsync(pr.Number, integrationIssueNum, ct);
                Logger.LogInformation("Linked integration PR #{PrNumber} to issue #{IssueNumber}",
                    pr.Number, integrationIssueNum);
            }
            catch (Exception linkIssueEx)
            {
                Logger.LogWarning(linkIssueEx,
                    "Failed to link integration PR #{PrNumber} to issue #{IssueNumber}",
                    pr.Number, integrationIssueNum);
            }
        }

        // Write candidate screenshots before push
        var screenshotsWritten = false;
        foreach (var cand in outcome.Evaluation.Candidates)
        {
            try
            {
                if (cand.ScreenshotBytes is null || cand.ScreenshotBytes.Length == 0) continue;
                var screenshotRelPath = $".screenshots/pr-{pr.Number}-{cand.StrategyId}.png";
                var screenshotFullPath = Path.Combine(Workspace.RepoPath, screenshotRelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotFullPath)!);
                await File.WriteAllBytesAsync(screenshotFullPath, cand.ScreenshotBytes, ct);
                screenshotsWritten = true;
            }
            catch (Exception screenshotEx)
            {
                Logger.LogWarning(screenshotEx, "Failed to write {Strategy} screenshot for integration PR", cand.StrategyId);
            }
        }

        if (screenshotsWritten)
        {
            try
            {
                await RunGitCommandAsync(Workspace.RepoPath, "add -A .screenshots", ct);
                await Workspace.CommitAsync($"📸 Strategy preview screenshots for PR #{pr.Number}", ct);
            }
            catch (Exception commitEx)
            {
                Logger.LogWarning(commitEx, "Failed to commit screenshot files for integration PR #{PrNumber}", pr.Number);
            }
        }

        // Push
        try
        {
            await Workspace.PushAsync(branchName, ct);
        }
        catch (Exception pushEx)
        {
            Logger.LogError(pushEx, "Strategy framework: T-FINAL committed but push failed — commit preserved locally");
        }

        CurrentPrNumber = pr.Number;
        Identity.AssignedPullRequest = pr.Number.ToString();
        _integrationPrCreated = true;

        // Mark ready for review
        await SyncBranchWithMainAsync(pr.Number, ct);
        await MarkReadyForReviewWithScreenshotAsync(pr, ct);

        // Do NOT add tests-added label here — that's the TE's responsibility.
        // The TE will see architect-approved + no tests-added, assess testability,
        // and add tests-added + post a completion comment. The PM's defense-in-depth
        // check requires a real TE comment before reviewing. Previously the SE added
        // tests-added directly (bypassing TE), which deadlocked the PM for 6+ hours
        // because no TE comment ever appeared.
        //
        // ⚠️ LESSON (commit 69211d3): If you ever add label writes after
        // MarkReadyForReviewAsync, you MUST re-fetch the PR first to get current labels.
        // MarkReadyForReview swaps in-progress→ready-for-review; using the original
        // pr.Labels would overwrite that swap (Lesson #4: GitHub label API replaces
        // the entire label set atomically).

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "Integration"
        }, ct);

        Logger.LogInformation("Created integration PR #{PrNumber} via strategy {Strategy}",
            pr.Number, winner.StrategyId);
        LogActivity("task", $"📦 Created integration PR #{pr.Number} via strategy '{winner.StrategyId}'");

        await CommentOnIntegrationIssueAsync(
            $"⏳ Integration PR #{pr.Number} created via strategy '{winner.StrategyId}'. Issue will close when PR is merged.", ct);
        await RememberAsync(MemoryType.Action,
            $"Created integration PR #{pr.Number} via strategy '{winner.StrategyId}'", ct: ct);

        _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId,
            succeeded: true, winnerStrategy: winner.StrategyId);
        TaskTracker.CompleteStep(integrationStepId);
        return true;
    }

    /// <summary>
    /// Legacy single-shot LLM integration review (fallback when strategy framework unavailable or fails).
    /// </summary>
    private async Task CreateIntegrationPRLegacyAsync(
        string pmSpecDoc, string architectureDoc, string techStack, string taskSummary,
        string integrationStepId, CancellationToken ct)
    {
        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = CreateChatHistory();
        var intSys = PromptService is not null
            ? await PromptService.RenderAsync("software-engineer/integration-review-system",
                new Dictionary<string, string> { ["tech_stack"] = techStack }, ct)
            : null;
        history.AddSystemMessage(intSys ??
            "You are a Software Engineer performing final integration review. " +
            $"The project uses {techStack}. " +
            "All individual task PRs have been merged to main. Your job is to:\n" +
            "1. Review the architecture and PM spec for any missing wiring, imports, or configuration\n" +
            "2. Identify integration gaps (broken cross-module references, missing route registration, missing DI wiring)\n" +
            "3. Generate any integration fix files needed\n\n" +
            "Output each file using: FILE: path/to/file.ext\n```language\n<content>\n```\n\n" +
            "If no integration fixes are needed, output ONLY the text: NO_INTEGRATION_FIXES_NEEDED");

        var intUser = PromptService is not null
            ? await PromptService.RenderAsync("software-engineer/integration-review-user",
                new Dictionary<string, string>
                {
                    ["pm_spec"] = pmSpecDoc,
                    ["architecture"] = architectureDoc,
                    ["task_summary"] = taskSummary
                }, ct)
            : null;
        // Build scenario verification section for the user prompt
        var scenarioSection = await BuildScenarioVerificationSectionAsync(ct);

        history.AddUserMessage(intUser ??
            $"## PM Specification\n{pmSpecDoc}\n\n" +
            $"## Architecture\n{architectureDoc}\n\n" +
            $"## Completed Tasks\n{taskSummary}\n\n" +
            scenarioSection +
            "Review the merged work against these documents. " +
            "Generate any missing integration files (config, wiring, startup registration, etc.).");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        TaskTracker.RecordLlmCall(integrationStepId);
        var integrationContent = response.Content?.Trim() ?? "";

        var codeFiles = VirtualDevTeam.Core.AI.CodeFileParser.ParseFiles(integrationContent);

        if (codeFiles.Count == 0 ||
            integrationContent.Contains("NO_INTEGRATION_FIXES_NEEDED", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("No integration fixes needed — all tasks cleanly integrated");
            LogActivity("task", "✅ No integration fixes needed — signaling completion");
            _integrationPrCreated = true;
            TaskTracker.CompleteStep(integrationStepId);
            await CloseIntegrationIssueAsync("✅ No integration fixes needed — all tasks cleanly integrated.", ct);
            await SignalEngineeringCompleteAsync(ct);
            return;
        }

        // Sync with latest target branch before creating T-FINAL branch (same as strategy path)
        if (Workspace is not null)
        {
            try
            {
                await Workspace.SyncWithMainAsync(ct);
            }
            catch (Exception syncEx)
            {
                Logger.LogWarning(syncEx, "T-FINAL legacy: failed to sync with {Branch}", EffectiveBranch);
            }
        }

        // Create integration branch and PR
        var branchName = await PrWorkflow.CreateTaskBranchAsync(
            Identity.DisplayName, "final-integration", ct);

        var prBody = $"## Final Integration PR\n\n" +
            $"All {_taskManager.TotalCount} engineering tasks have been completed and merged.\n" +
            $"This PR addresses integration gaps identified during final review.\n\n" +
            $"### Files Changed\n" +
            string.Join("\n", codeFiles.Select(f => $"- `{f.Path}`"));

        var pr = await PrWorkflow.CreateTaskPullRequestAsync(
            Identity.DisplayName,
            "Final Integration",
            prBody,
            "High",
            "Architecture.md",
            "",
            branchName,
            [PullRequestWorkflow.Labels.FinalIntegration],
            ct);

        if (Workspace is not null && BuildRunnerSvc is not null)
        {
            var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                "Integration fixes: wiring, config, and cross-module references",
                1, 1, "Final Integration", chat, ct);
            if (!committed)
            {
                Logger.LogWarning("SE integration PR #{PrNumber} blocked by build errors", pr.Number);
                await ReviewService.AddCommentAsync(pr.Number,
                    "❌ **Build Blocked:** Integration fixes could not produce a buildable commit.", ct);
            }
        }
        else
        {
            await PrWorkflow.CommitCodeFilesToPRAsync(
                pr.Number, codeFiles, "Integration fixes: wiring, config, and cross-module references", ct);
        }

        CurrentPrNumber = pr.Number;
        Identity.AssignedPullRequest = pr.Number.ToString();
        _integrationPrCreated = true;

        // Link T-FINAL issue to PR — ensures "Closes #N" for auto-close and timeline
        var legacyIntegrationTask = _taskManager.Tasks.FirstOrDefault(IsIntegrationTask);
        if (legacyIntegrationTask?.IssueNumber is int legacyIssueNum)
        {
            try
            {
                await PrService.LinkWorkItemAsync(pr.Number, legacyIssueNum, ct);
            }
            catch (Exception linkEx)
            {
                Logger.LogWarning(linkEx, "Failed to link legacy integration PR #{PrNumber} to issue #{IssueNumber}",
                    pr.Number, legacyIssueNum);
            }
        }

        // Sync and mark ready for review
        await SyncBranchWithMainAsync(pr.Number, ct);
        await MarkReadyForReviewWithScreenshotAsync(pr, ct);

        // Do NOT add tests-added label — TE owns this responsibility.
        // Same fix as the strategy T-FINAL path: let Architect → TE → PM flow proceed naturally.

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "Integration"
        }, ct);

        Logger.LogInformation("Created integration PR #{PrNumber} with {FileCount} fixes",
            pr.Number, codeFiles.Count);
        LogActivity("task", $"📦 Created integration PR #{pr.Number} with {codeFiles.Count} fixes");

        await CommentOnIntegrationIssueAsync(
            $"⏳ Integration PR #{pr.Number} created with {codeFiles.Count} fixes. Issue will close when PR is merged.", ct);
        await RememberAsync(MemoryType.Action,
            $"Created integration PR #{pr.Number} with {codeFiles.Count} integration fixes", ct: ct);
        TaskTracker.CompleteStep(integrationStepId);
    }

    private async Task CloseIntegrationIssueAsync(string comment, CancellationToken ct)
    {
        if (_integrationIssueNumber is null)
        {
            // Try to find it from the task manager cache
            var task = _taskManager.Tasks.FirstOrDefault(IsIntegrationTask);
            if (task?.IssueNumber is not null)
                _integrationIssueNumber = task.IssueNumber;
        }

        if (_integrationIssueNumber is not null)
        {
            try
            {
                await WorkItemService.AddCommentAsync(_integrationIssueNumber.Value, comment, ct);
                await WorkItemService.CloseAsync(_integrationIssueNumber.Value, ct);
                Logger.LogInformation("Closed integration issue #{IssueNumber}", _integrationIssueNumber);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to close integration issue #{IssueNumber}", _integrationIssueNumber);
            }
        }
    }

    /// <summary>
    /// Adds a progress comment to the integration issue without closing it.
    /// Used when an integration PR is created but not yet merged.
    /// </summary>
    private async Task CommentOnIntegrationIssueAsync(string comment, CancellationToken ct)
    {
        if (_integrationIssueNumber is null)
        {
            var task = _taskManager.Tasks.FirstOrDefault(IsIntegrationTask);
            if (task?.IssueNumber is not null)
                _integrationIssueNumber = task.IssueNumber;
        }

        if (_integrationIssueNumber is not null)
        {
            try
            {
                await WorkItemService.AddCommentAsync(_integrationIssueNumber.Value, comment, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to comment on integration issue #{IssueNumber}", _integrationIssueNumber);
            }
        }
    }

    /// <summary>
    /// Advisory scenario validation — infers pass/fail from task completion status and posts
    /// a summary comment on the T-FINAL PR. Never blocks completion; all failures are logged
    /// as warnings and swallowed.
    /// </summary>
    private async Task RunScenarioValidationAsync(CancellationToken ct)
    {
        try
        {
            if (_scenarioRegistry is null)
                return;

            var scenarios = _scenarioRegistry.Current;
            if (scenarios.Count == 0)
            {
                // Try a reload in case scenarios were never loaded in this session
                scenarios = await _scenarioRegistry.LoadAsync(ct);
            }

            if (scenarios.Count == 0)
            {
                Logger.LogDebug("No scenarios registered — skipping scenario validation");
                return;
            }

            Logger.LogInformation("Running advisory scenario validation for {Count} scenarios", scenarios.Count);

            // Compute verdicts by checking whether each scenario's implementing tasks are done
            var updated = new List<Scenario>(scenarios.Count);
            int passCount = 0, failCount = 0, skippedCount = 0;

            foreach (var scenario in scenarios)
            {
                if (scenario.Infrastructure)
                {
                    // Infrastructure scenarios are not user-facing — skip
                    updated.Add(scenario);
                    skippedCount++;
                    continue;
                }

                if (scenario.ImplementingTasks.Count == 0)
                {
                    // No tasks tagged — inconclusive
                    updated.Add(scenario with
                    {
                        VerificationStatus = VerificationStatus.Inconclusive,
                        VerificationReason = "No implementing tasks tagged — scenario-to-task mapping missing",
                    });
                    skippedCount++;
                    continue;
                }

                // Check if all implementing tasks are done by matching task IDs against
                // the engineering task manager's task list.
                // ImplementingTasks entries look like "T03: Tower placement UI".
                // Task ID matching is tried first (T03 → task.Id), then falls back to
                // title matching because PMSpec task IDs (T1, T3) differ from engineering
                // work item IDs (T-14, T-15) when issues don't embed [T3] in the title.
                var allDone = true;
                foreach (var taskRef in scenario.ImplementingTasks)
                {
                    // Extract ID prefix and title from "T03: Tower placement UI"
                    var taskId = taskRef.Contains(':')
                        ? taskRef[..taskRef.IndexOf(':')].Trim()
                        : taskRef.Trim();
                    var taskTitle = taskRef.Contains(':')
                        ? taskRef[(taskRef.IndexOf(':') + 1)..].Trim()
                        : "";

                    // Try exact ID match first
                    var matchingTask = _taskManager.Tasks.FirstOrDefault(t =>
                        string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));

                    // Fallback: match by title (strip agent prefix from task name)
                    // Engineering task names like "Service Inventory Management" should match
                    // scenario refs like "T3: Service Inventory Management"
                    // Check both directions since titles may diverge:
                    //   scenario: "43-Step Agent Workflow and External Tools"
                    //   task:     "Agent Workflow Engine and Tools"
                    if (matchingTask is null && !string.IsNullOrWhiteSpace(taskTitle))
                    {
                        matchingTask = _taskManager.Tasks.FirstOrDefault(t =>
                            !string.IsNullOrWhiteSpace(t.Name) &&
                            (t.Name.Contains(taskTitle, StringComparison.OrdinalIgnoreCase) ||
                             taskTitle.Contains(t.Name, StringComparison.OrdinalIgnoreCase) ||
                             FuzzyTitleMatch(t.Name, taskTitle)));
                    }

                    if (matchingTask is null || !EngineeringTaskIssueManager.IsTaskDone(matchingTask))
                    {
                        allDone = false;
                        break;
                    }
                }

                if (allDone)
                {
                    updated.Add(scenario with { VerificationStatus = VerificationStatus.InferredPass });
                    passCount++;
                }
                else
                {
                    updated.Add(scenario with { VerificationStatus = VerificationStatus.InferredFail });
                    failCount++;
                }
            }

            // Persist verdicts to the sidecar so the Scenarios page shows results
            await _scenarioRegistry.WriteSidecarAsync(updated, ct);
            Logger.LogInformation(
                "Scenario validation complete: {Pass} inferred pass, {Fail} inferred fail, {Skipped} skipped",
                passCount, failCount, skippedCount);

            // Run real AppPlaytester verification if available and we have a workspace.
            // Track results to determine if promotion fallback is needed.
            bool appPlaytesterTechnicalFailure = false;
            int appPlaytesterVerifiedCount = 0;
            int appPlaytesterBrokenCount = 0;

            // Specific, operator-facing reason for why live verification could not promote scenarios.
            // Set at each technical-failure trigger so the dashboard shows the REAL cause instead of a
            // single generic "encountered technical failures" string. Null until a failure occurs.
            string? appPlaytesterFailureReason = null;

            // Scenario IDs that received a real per-scenario playtest report (and thus already carry a
            // report-derived reason). These must NOT be overwritten by the run-level failure summary.
            var reportedScenarioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Save pre-playtest verdicts so we can restore InferredPass on technical failure
            var prePlaytestVerdicts = updated.Select(s => s.VerificationStatus).ToList();

            // Live scenario verification needs a running app, which needs a local workspace. The cold-start
            // clone-skip optimization (EngineerAgentBase: "engineering already complete") can intentionally
            // leave Workspace == null when a FINISHED project is restarted purely to re-verify — which
            // previously degraded every scenario to Inconclusive ("no workspace available — technical
            // failure") and let the final PR ship unverified. If we have scenarios to live-verify but no
            // workspace, materialize one on demand so the app can actually be launched and scenarios get a
            // real Verified/Broken verdict instead of a silent technical failure.
            if (_appPlaytester is not null && Workspace is null && passCount > 0)
            {
                Logger.LogInformation(
                    "Scenario verification needs a live app but the workspace was skipped on this restart " +
                    "(engineering already complete) — creating workspace on demand for {Count} scenario(s)",
                    passCount);
                UpdateStatus(AgentStatus.Working, "📁 Setting up workspace for scenario verification");
                var wsReady = await EnsureWorkspaceInitializedAsync(ct);
                if (wsReady)
                    Logger.LogInformation("On-demand workspace ready for scenario verification at {Path}", Workspace!.RepoPath);
                else
                    Logger.LogWarning("On-demand workspace creation failed — scenario verification will fall back to technical-failure handling");
            }

            if (_appPlaytester is not null && Workspace is not null && passCount > 0)
            {
                AppLaunchResult? appLaunchResult = null;
                try
                {
                    Logger.LogInformation("Running AppPlaytester for real scenario verification on {Count} scenarios", passCount);

                    // Launch the app so AppPlaytester has a BaseUrl to connect to.
                    // Without this, CliAppPlaytester returns Inconclusive for every scenario
                    // because BaseUrl is empty (root cause of false "Verified" badges).
                    var wsConfig = Config.Workspace;

                    // AppLauncher.StartAppUnderTestAsync already auto-detects the start command
                    // via AI-driven CLI detection (with heuristic fallback) when AppStartCommand is empty.
                    // No need to pre-detect here — just pass through to LaunchVerifiedAppAsync.
                    if (ScreenshotRunner is not null)
                    {
                        var envVars = new Dictionary<string, string>();
                        appLaunchResult = await ScreenshotRunner.LaunchVerifiedAppAsync(
                            Workspace.RepoPath, wsConfig, envVars, ct);

                        if (appLaunchResult is null)
                        {
                            Logger.LogWarning("App failed to start for scenario verification — treating as technical failure");
                            appPlaytesterTechnicalFailure = true;
                            appPlaytesterFailureReason =
                                "Not live-verified: the app under test failed to start, so AppPlaytester could not run the scenario.";
                        }
                    }
                    else
                    {
                        Logger.LogWarning("Cannot start app for scenario verification: ScreenshotRunner={HasRunner}, AppStartCommand='{Cmd}' — skipping live verification",
                            ScreenshotRunner is not null, wsConfig.AppStartCommand ?? "(null)");
                        appPlaytesterTechnicalFailure = true;
                        appPlaytesterFailureReason =
                            "Not live-verified: no app start command/runner was available to launch the app for verification.";
                    }

                    if (!appPlaytesterTechnicalFailure)
                    {
                        var baseUrl = appLaunchResult?.VerifiedUrl ?? string.Empty;
                        var handle = new VirtualDevTeam.Core.Agents.Playtest.AppHandle
                        {
                            WorkspacePath = Workspace.RepoPath,
                            TargetType = VirtualDevTeam.Core.Agents.Playtest.AppTargetType.Web,
                            BaseUrl = baseUrl,
                        };
                        var scenariosToVerify = updated
                            .Where(s => s.VerificationStatus == VerificationStatus.InferredPass)
                            .ToList();
                        var reports = await _appPlaytester.RunAsync(handle, scenariosToVerify, ct);

                        if (reports.Length > 0)
                        {
                            // Map playtest verdicts back to scenarios
                            foreach (var report in reports)
                            {
                                var idx = updated.FindIndex(s =>
                                    string.Equals(s.Id, report.ScenarioId, StringComparison.OrdinalIgnoreCase));
                                if (idx < 0) continue;

                                reportedScenarioIds.Add(updated[idx].Id);
                                updated[idx] = updated[idx] with
                                {
                                    VerificationStatus = report.Verdict,
                                    VerificationReason = report.Verdict == VerificationStatus.Verified
                                        ? $"Verified by AppPlaytester (confidence: {report.Confidence:P0})"
                                        : report.AmbiguityNote ?? report.ExecutionError ?? "Playtest did not verify",
                                };
                            }

                            // Update sidecar with real verdicts
                            await _scenarioRegistry.WriteSidecarAsync(updated, ct);
                            appPlaytesterVerifiedCount = reports.Count(r => r.Verdict == VerificationStatus.Verified);
                            appPlaytesterBrokenCount = reports.Count(r => r.Verdict == VerificationStatus.Broken);
                            Logger.LogInformation(
                                "AppPlaytester completed: {Total} scenarios tested, {Verified} verified, {Broken} broken",
                                reports.Length, appPlaytesterVerifiedCount, appPlaytesterBrokenCount);

                            // If the app started and AppPlaytester ran, results are REAL
                            // (not technical failure). Broken = code has bugs, not tool failure.
                            // Only treat as technical failure if app didn't start (handled above)
                            // or AppPlaytester threw an exception (handled in catch block).
                            if (appPlaytesterVerifiedCount == 0 && appPlaytesterBrokenCount == 0)
                            {
                                // ALL scenarios Inconclusive with no Broken verdicts — likely tool issue.
                                // The per-scenario reports above already carry the specific reason
                                // (AmbiguityNote/ExecutionError); we keep those and only flag the run.
                                appPlaytesterTechnicalFailure = true;
                                appPlaytesterFailureReason =
                                    "Live verification was inconclusive for every scenario (no pass/fail signal) — " +
                                    "see each scenario's reason for the specific AppPlaytester output.";
                                Logger.LogWarning(
                                    "AppPlaytester returned 0 verified AND 0 broken ({Inconclusive} inconclusive) — treating as technical failure",
                                    reports.Length);
                            }
                            else if (appPlaytesterVerifiedCount == 0)
                            {
                                // App started, some scenarios are Broken — this is real test data, not tool failure.
                                // Keep the AppPlaytester verdicts. InferredPass scenarios that AppPlaytester
                                // found Inconclusive can still be promoted since advisory validation passed them.
                                Logger.LogInformation(
                                    "AppPlaytester verified 0 but found {Broken} broken scenarios — results are real (app was running). " +
                                    "InferredPass scenarios with Inconclusive playtest will be promoted to Verified.",
                                    appPlaytesterBrokenCount);
                            }
                        }
                        else
                        {
                            // AppPlaytester returned zero reports despite having scenarios to verify.
                            // This is a tool-level issue — don't promote to Verified.
                            Logger.LogWarning(
                                "AppPlaytester returned 0 reports despite {Count} InferredPass scenarios — treating as technical failure",
                                passCount);
                            appPlaytesterTechnicalFailure = true;
                            appPlaytesterFailureReason =
                                "Not live-verified: AppPlaytester produced no report for the scenario (the verification run yielded no output).";
                        }
                    } // end if (!appPlaytesterTechnicalFailure)
                }
                catch (Exception playEx)
                {
                    Logger.LogWarning(playEx, "AppPlaytester verification threw — treating as technical failure");
                    appPlaytesterTechnicalFailure = true;
                    appPlaytesterFailureReason =
                        $"Not live-verified: AppPlaytester errored during verification ({playEx.GetType().Name}: {playEx.Message}).";
                }
                finally
                {
                    // Kill the app process we launched for playtesting
                    if (appLaunchResult?.Process is { } appProc && !appProc.HasExited)
                    {
                        try { appProc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    }
                    if (appLaunchResult?.CompanionProcess is { } companionProc && !companionProc.HasExited)
                    {
                        try { companionProc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    }
                }
            }
            else if (_appPlaytester is not null && Workspace is null)
            {
                // AppPlaytester is configured but no workspace is available even after the on-demand
                // creation attempt above — so we genuinely could not launch a local app. This happens when
                // on-demand workspace creation failed (e.g. SharedCloneManager/host repo unavailable) or
                // there were no InferredPass scenarios to verify. Be honest: nothing was live-verified here.
                Logger.LogWarning("AppPlaytester configured but no workspace available (on-demand creation did not succeed) — treating as technical failure");
                appPlaytesterTechnicalFailure = true;
                appPlaytesterFailureReason =
                    "Not live-verified: a local workspace could not be created for this run (on-demand workspace " +
                    "setup did not succeed), so the app could not be launched. Scenarios were validated from " +
                    "task/PR state only — no live app testing was performed. Re-run a fresh build to live-verify.";
            }

            // Promote InferredPass → Verified ONLY when no AppPlaytester is configured.
            // When AppPlaytester had a technical failure, leave as InferredPass — the scenario
            // was NOT actually verified and the dashboard must not show a false "Verified" badge.
            if (_appPlaytester is null)
            {
                var promotedCount = 0;
                for (int i = 0; i < updated.Count; i++)
                {
                    var originalVerdict = prePlaytestVerdicts[i];
                    if (originalVerdict == VerificationStatus.InferredPass &&
                        updated[i].VerificationStatus != VerificationStatus.Verified)
                    {
                        updated[i] = updated[i] with
                        {
                            VerificationStatus = VerificationStatus.Verified,
                            VerificationReason = "All implementing tasks completed; integration validated by T-FINAL",
                        };
                        promotedCount++;
                    }
                }

                if (promotedCount > 0)
                {
                    await _scenarioRegistry.WriteSidecarAsync(updated, ct);
                    Logger.LogInformation(
                        "Promoted {Promoted} scenarios from InferredPass → Verified (no AppPlaytester configured)",
                        promotedCount);
                }
            }
            else if (appPlaytesterTechnicalFailure)
            {
                // AppPlaytester failed technically — mark as Inconclusive so the dashboard
                // does NOT show a false "Verified" badge. Use the SPECIFIC reason for why
                // (no workspace, app didn't start, exception, etc.) instead of a generic string,
                // and preserve any per-scenario reason that a real playtest report already produced.
                var runLevelReason = appPlaytesterFailureReason
                    ?? "All implementing tasks completed but live verification could not be performed — verification inconclusive.";
                var inconclusiveCount = 0;
                for (int i = 0; i < updated.Count; i++)
                {
                    var originalVerdict = prePlaytestVerdicts[i];
                    if (originalVerdict == VerificationStatus.InferredPass &&
                        updated[i].VerificationStatus != VerificationStatus.Verified)
                    {
                        // Scenarios that received a real per-scenario report keep that report's
                        // specific reason; only mark them Inconclusive if not already non-pass.
                        var keepReportReason = reportedScenarioIds.Contains(updated[i].Id);
                        updated[i] = updated[i] with
                        {
                            VerificationStatus = VerificationStatus.Inconclusive,
                            VerificationReason = keepReportReason
                                ? updated[i].VerificationReason
                                : runLevelReason,
                        };
                        inconclusiveCount++;
                    }
                }

                if (inconclusiveCount > 0)
                {
                    await _scenarioRegistry.WriteSidecarAsync(updated, ct);
                    Logger.LogWarning(
                        "Marked {Count} scenarios as Inconclusive — reason: {Reason}",
                        inconclusiveCount, runLevelReason);
                }
            }
            else
            {
                // AppPlaytester ran with REAL results (app started, scenarios were actually tested).
                // Promote InferredPass scenarios that AppPlaytester found Inconclusive → Verified
                // (advisory validation passed them AND the app was running — good enough evidence).
                // Keep Broken verdicts as-is — those are real failures found by the playtester.
                var promotedCount = 0;
                var keptBrokenCount = 0;
                for (int i = 0; i < updated.Count; i++)
                {
                    var originalVerdict = prePlaytestVerdicts[i];
                    if (originalVerdict == VerificationStatus.InferredPass &&
                        updated[i].VerificationStatus == VerificationStatus.Broken)
                    {
                        // AppPlaytester found a real bug — keep Broken
                        keptBrokenCount++;
                    }
                    else if (originalVerdict == VerificationStatus.InferredPass &&
                             updated[i].VerificationStatus != VerificationStatus.Verified)
                    {
                        // AppPlaytester returned Inconclusive but advisory validation passed —
                        // promote to Verified since app was running and tasks are complete
                        updated[i] = updated[i] with
                        {
                            VerificationStatus = VerificationStatus.Verified,
                            VerificationReason = "All implementing tasks completed; integration validated by T-FINAL (advisory pass + live app started)",
                        };
                        promotedCount++;
                    }
                }

                if (promotedCount > 0 || keptBrokenCount > 0)
                {
                    await _scenarioRegistry.WriteSidecarAsync(updated, ct);
                    Logger.LogInformation(
                        "AppPlaytester results applied: {Promoted} scenarios promoted to Verified, {Broken} scenarios kept as Broken",
                        promotedCount, keptBrokenCount);
                }
            }

            // Post a summary comment on the integration PR (if one exists)
            if (CurrentPrNumber is not null)
            {
                var verifiedCount = updated.Count(s => s.VerificationStatus == VerificationStatus.Verified);
                await PostScenarioSummaryCommentAsync(CurrentPrNumber.Value, updated,
                    verifiedCount > 0 ? verifiedCount : passCount, failCount, skippedCount, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Advisory scenario validation failed — continuing with completion");
        }
    }

    /// <summary>
    /// Fuzzy title match: checks if two titles share enough significant words to be considered
    /// the same task. Handles divergence like "43-Step Agent Workflow and External Tools" vs
    /// "Agent Workflow Engine and Tools" where substring match fails but they describe the same work.
    /// Returns true if ≥50% of the significant words in the shorter title appear in the longer one.
    /// </summary>
    private static bool FuzzyTitleMatch(string taskName, string scenarioTitle)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "and", "the", "of", "for", "in", "on", "a", "an", "to", "with" };

        static string[] ExtractWords(string text, HashSet<string> stop) =>
            text.Split(new[] { ' ', '-', ',', ':', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !stop.Contains(w))
                .Select(w => w.ToLowerInvariant())
                .ToArray();

        var taskWords = ExtractWords(taskName, stopWords);
        var scenarioWords = ExtractWords(scenarioTitle, stopWords);
        if (taskWords.Length == 0 || scenarioWords.Length == 0) return false;

        // Use the shorter list as the reference
        var (shorter, longer) = taskWords.Length <= scenarioWords.Length
            ? (taskWords, scenarioWords)
            : (scenarioWords, taskWords);
        var longerSet = new HashSet<string>(longer);
        var matchCount = shorter.Count(w => longerSet.Contains(w));

        // Require ≥50% word overlap from the shorter title
        return matchCount >= Math.Ceiling(shorter.Length * 0.5);
    }

    /// <summary>
    /// Posts a Markdown summary of scenario validation verdicts as a PR comment.
    /// </summary>
    private async Task PostScenarioSummaryCommentAsync(
        int prNumber, List<Scenario> scenarios, int passCount, int failCount, int skippedCount,
        CancellationToken ct)
    {
        try
        {
            var verifiedCount = scenarios.Count(s => s.VerificationStatus == VerificationStatus.Verified);
            var inferredPassCount = scenarios.Count(s => s.VerificationStatus == VerificationStatus.InferredPass);

            var sb = new StringBuilder();
            sb.AppendLine("## 📋 Scenario Validation Summary");
            sb.AppendLine();
            sb.AppendLine($"| Result | Count |");
            sb.AppendLine($"|--------|-------|");
            if (verifiedCount > 0)
                sb.AppendLine($"| ✅ Verified | {verifiedCount} |");
            if (inferredPassCount > 0)
                sb.AppendLine($"| ✅ Inferred Pass | {inferredPassCount} |");
            sb.AppendLine($"| ❌ Inferred Fail | {failCount} |");
            sb.AppendLine($"| ⏭️ Skipped | {skippedCount} |");
            sb.AppendLine();

            if (scenarios.Any(s => !s.Infrastructure))
            {
                sb.AppendLine("| Scenario | Title | Verdict |");
                sb.AppendLine("|----------|-------|---------|");
                foreach (var s in scenarios.Where(s => !s.Infrastructure))
                {
                    var icon = s.VerificationStatus switch
                    {
                        VerificationStatus.Verified => "✅",
                        VerificationStatus.InferredPass => "✅",
                        VerificationStatus.InferredFail => "❌",
                        VerificationStatus.Broken => "💔",
                        _ => "⏭️",
                    };
                    sb.AppendLine($"| {s.Id} | {s.Title} | {icon} {s.VerificationStatus} |");
                }
                sb.AppendLine();
            }

            if (verifiedCount > 0)
                sb.AppendLine($"> **{verifiedCount}/{scenarios.Count(s => !s.Infrastructure)}** scenarios verified via T-FINAL integration validation.");
            else
                sb.AppendLine("> **Note:** This is an advisory assessment based on task completion status.");

            await ReviewService.AddCommentAsync(prNumber, sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to post scenario summary comment on PR #{PrNumber}", prNumber);
        }
    }

    /// <summary>
    /// Generates an AI-enriched PR body summarizing all engineering work for the final submission PR.
    /// Falls back to a simple summary if AI generation fails.
    /// </summary>
    private async Task<string> GenerateEnrichedPrBodyAsync(string projectName, CancellationToken ct)
    {
        try
        {
            // Gather merged PRs scoped to this run
            var mergedPrs = await GetCachedMergedPRsAsync(ct);
            var scopedPrs = mergedPrs.Where(pr => IsCurrentRunScopePr(pr)).ToList();

            // Build task summary data
            var tasks = _taskManager.Tasks;
            var completedCount = tasks.Count(t => t.Status == "Done");
            var totalFiles = scopedPrs.Sum(pr => pr.ChangedFiles.Count);

            // Build the data context for AI summarization
            var dataSummary = new System.Text.StringBuilder();
            dataSummary.AppendLine($"# Project: {projectName}");
            dataSummary.AppendLine($"Total Tasks: {tasks.Count} ({completedCount} completed)");
            dataSummary.AppendLine($"Total PRs Merged: {scopedPrs.Count}");
            dataSummary.AppendLine($"Total Files Changed: {totalFiles}");
            dataSummary.AppendLine();

            dataSummary.AppendLine("## Engineering Tasks");
            foreach (var task in tasks)
            {
                dataSummary.AppendLine($"- **{task.Name}** (Wave: {task.Wave}, Complexity: {task.Complexity}, Status: {task.Status})");
                if (!string.IsNullOrWhiteSpace(task.Description))
                {
                    var firstLine = task.Description.Split('\n').FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(firstLine))
                        dataSummary.AppendLine($"  {firstLine}");
                }
                if (!string.IsNullOrWhiteSpace(task.AssignedTo))
                    dataSummary.AppendLine($"  Assigned to: {task.AssignedTo}");
            }
            dataSummary.AppendLine();

            dataSummary.AppendLine("## Merged Pull Requests");
            foreach (var pr in scopedPrs.OrderBy(p => p.MergedAt))
            {
                dataSummary.AppendLine($"- PR #{pr.Number}: {pr.Title} ({pr.ChangedFiles} files)");
            }

            // AI call to generate structured summary
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();

            history.AddSystemMessage(
                "You are a technical writer creating a PR description for a software project. " +
                "Write a clear, structured markdown summary of the engineering work completed. " +
                "Keep it concise but informative — a reviewer should understand what was built without reading every commit. " +
                "Use the following structure:\n" +
                "1. **Executive Summary** — 2-3 sentences on what was built\n" +
                "2. **Key Features** — Bullet list of major features/capabilities added\n" +
                "3. **Technical Changes** — Brief overview of the technical approach and notable decisions\n" +
                "4. **Task Breakdown** — Table with Task | Complexity | Status columns\n\n" +
                "Do NOT include a title heading (the PR already has one). Start directly with the Executive Summary section. " +
                "Keep the total output under 2000 characters.");

            history.AddUserMessage(dataSummary.ToString());

            var result = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var aiSummary = result.Content?.Trim();

            if (string.IsNullOrWhiteSpace(aiSummary))
            {
                Logger.LogWarning("AI returned empty summary for final PR — using fallback");
                return BuildFallbackPrBody(projectName, scopedPrs, tasks);
            }

            // Compose final body: AI summary + metadata footer
            var body = new System.Text.StringBuilder();
            body.AppendLine(aiSummary);
            body.AppendLine();
            body.AppendLine("---");
            body.AppendLine($"📊 **{tasks.Count}** tasks | **{scopedPrs.Count}** PRs merged | **{totalFiles}** files changed");
            body.AppendLine();
            body.AppendLine("*Generated by VirtualDevTeam agent pipeline — Local Dev Mode*");

            return body.ToString();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to generate enriched PR body — using fallback");
            return BuildFallbackPrBody(projectName, null, _taskManager.Tasks);
        }
    }

    private static string BuildFallbackPrBody(
        string projectName,
        IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>? scopedPrs,
        IReadOnlyList<EngineeringTask> tasks)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine($"## {projectName}");
        body.AppendLine();
        body.AppendLine($"All **{tasks.Count}** engineering tasks have been completed and integrated.");
        body.AppendLine();

        if (scopedPrs is { Count: > 0 })
        {
            body.AppendLine("### Merged PRs");
            foreach (var pr in scopedPrs.OrderBy(p => p.MergedAt))
                body.AppendLine($"- #{pr.Number}: {pr.Title}");
            body.AppendLine();
        }

        body.AppendLine("This PR contains the complete implementation produced by the VirtualDevTeam agent pipeline.");
        body.AppendLine("Please review and merge when ready.");
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine("*Submitted via Local Dev Mode*");

        return body.ToString();
    }

    private async Task SignalEngineeringCompleteAsync(CancellationToken ct)
    {
        // Advisory: run scenario validation before signaling completion (never blocks)
        await RunScenarioValidationAsync(ct);

        // LDP mode: publish one clean PR to the real platform before signaling completion
        if (_finalSubmission is not null && Core!.Config.DevPlatform.Platform == DevPlatformType.Local)
        {
            try
            {
                // Push agent work to the configured working branch, PR targets main.
                // The working branch exists for VDT to use — no separate vdt/final/ branch needed.
                var workingBranch = Core.Config.Project.WorkingBranch;
                var defaultBranch = Core.Config.Project.DefaultBranch ?? "main";
                // branchName = the working branch we push to (head of PR)
                // baseBranch = the protected branch the PR targets (e.g., main)
                // CRITICAL: if workingBranch == defaultBranch (e.g., both "main"), GitHub/ADO
                // rejects PR creation (head==base). Fall back to auto-generated branch.
                var branchName = !string.IsNullOrWhiteSpace(workingBranch)
                    && !string.Equals(workingBranch, defaultBranch, StringComparison.OrdinalIgnoreCase)
                    ? workingBranch
                    : $"vdt/final/{defaultBranch}";
                var baseBranch = defaultBranch;
                var projectName = Core.Config.Project.Name ?? "Project";
                var title = $"{projectName} — Complete Implementation ({_taskManager.TotalCount} tasks)";
                var body = await GenerateEnrichedPrBodyAsync(projectName, ct);

                var finalPr = await _finalSubmission.SubmitFinalPRAsync(branchName, title, body, baseBranch, ct);
                LogActivity("system", $"📤 Final PR #{finalPr.Number} submitted to platform: {finalPr.Url}");
                Logger.LogInformation("LDP: Final PR #{Number} submitted to platform", finalPr.Number);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "LDP: Failed to submit final PR to platform — local state preserved");
                LogActivity("warning", $"⚠️ Failed to submit final PR: {ex.Message}");
            }
        }

        // Close the integration issue now that engineering is truly complete
        await CloseIntegrationIssueAsync("✅ Engineering complete — all tasks done and integrated.", ct);

        // Close any remaining open engineering task issues
        await _taskManager.CloseAllRemainingTaskIssuesAsync(ct);

        // Notify PM to review enhancement issues — PM owns the lifecycle of user stories
        try
        {
            await PublishStatusAsync("StatusUpdate", AgentStatus.Idle,
                details: "All engineering tasks are complete and merged. PM should review enhancement issues for final acceptance.",
                currentTask: "AllTasksComplete", ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to notify PM about engineering completion");
        }

        UpdateStatus(AgentStatus.Idle, "Engineering complete");
        _engineeringSignaled = true;
        LogActivity("system", "🏁 Engineering phase complete — all tasks done and integrated");

        await PublishStatusAsync("EngineeringComplete", AgentStatus.Idle,
            details: $"All {_taskManager.TotalCount} tasks complete. Engineering phase finished.",
            ct: ct);

        await RememberAsync(MemoryType.Action,
            $"Engineering phase complete: {_taskManager.TotalCount} tasks done", ct: ct);

        // Signal all agents to clean up local workspaces
        if (Config.Workspace.IsEnabled && Config.Workspace.CleanupOnProjectComplete)
        {
            try
            {
                await MessageBus.PublishAsync(new WorkspaceCleanupMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "WorkspaceCleanup",
                    Reason = "Project complete — all engineering tasks finished and integrated"
                }, ct);
                Logger.LogInformation("Broadcast workspace cleanup signal to all agents");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to broadcast workspace cleanup signal");
            }
        }
    }

    #endregion

    #region PE-Specific Message Handlers

    private Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Status update from {Agent}: {Status} — {Details}",
            message.FromAgentId, message.NewStatus, message.Details);

        // BUG FIX: Handle ArchitectureComplete message from the Architect agent.
        // Previously the Architect created a spurious GitHub Issue to notify the PE, but
        // the correct path is this bus message. Sets the _architectureReady flag so
        // CheckForArchitectureAsync can proceed without polling for fake issues.
        if (string.Equals(message.MessageType, "ArchitectureComplete", StringComparison.OrdinalIgnoreCase))
        {
            _architectureReady = true;
            Logger.LogInformation("Architecture complete signal received via message bus from {Agent}",
                message.FromAgentId);
            return Task.CompletedTask;
        }

        // BUG FIX: Key _agentAssignments by agent Id (message.FromAgentId) not DisplayName.
        // Also match task by Name (case-insensitive) with Id fallback, because engineers
        // send the issue Title as CurrentTask but the backlog stores it as task Name/Id.
        if (message.MessageType == "TaskComplete"
            && _agentAssignments.ContainsKey(message.FromAgentId))
        {
            _agentAssignments.Remove(message.FromAgentId);

            // Task completion is tracked via issue state (closed = Done)
            // No need to update an in-memory backlog
        }

        return Task.CompletedTask;
    }

    protected override Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        if (message.Title.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
            message.Title.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Ignoring non-engineering task assignment: {Title}", message.Title);
            return Task.CompletedTask;
        }

        Logger.LogInformation(
            "Received task assignment from {From}: {Title}",
            message.FromAgentId, message.Title);

        if (!_taskManager.Tasks.Any(t =>
                string.Equals(t.Name, message.Title, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Received externally-assigned task: {Title} — will be handled via issues", message.Title);
        }

        return Task.CompletedTask;
    }

    private Task HandleReviewRequestAsync(ReviewRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Review request from {Agent} for PR #{PrNumber}: {Title} ({ReviewType})",
            message.FromAgentId, message.PrNumber, message.PrTitle, message.ReviewType);

        _reviewedPrNumbers.TryRemove(message.PrNumber, out _);

        // BUG FIX: Track FinalApproval requests so PE auto-approves after max rework cycles.
        if (string.Equals(message.ReviewType, "FinalApproval", StringComparison.OrdinalIgnoreCase))
            _forceApprovalPrs.TryAdd(message.PrNumber, 0);

        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    protected override Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        // This is our own PR if we're currently implementing it, or if we shipped it past
        // implementation and it's still being tracked for review/merge.
        var isOurPr = CurrentPrNumber == message.PrNumber
            || IsPastImplementationPrTracked(message.PrNumber);

        if (!isOurPr)
            return Task.CompletedTask;

        // Skip if this PR has already been merged/closed (prevents rework on merged PRs)
        if (_mergedPrNumbers.Contains(message.PrNumber))
        {
            Logger.LogInformation(
                "SE ignoring change request on already-merged PR #{PrNumber} from {Reviewer}",
                message.PrNumber, message.ReviewerAgent);
            return Task.CompletedTask;
        }

        // Fallback: check platform API for PRs merged by workers (leader's _mergedPrNumbers
        // only tracks PRs merged by this leader, not by worker agents)
        try
        {
            var pr = PrService.GetAsync(message.PrNumber, ct).GetAwaiter().GetResult();
            if (pr is not null && string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase))
            {
                _mergedPrNumbers.Add(message.PrNumber); // cache for future checks
                Logger.LogInformation(
                    "SE ignoring change request on closed/merged PR #{PrNumber} from {Reviewer} (detected via API)",
                    message.PrNumber, message.ReviewerAgent);
                return Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "PR state check failed for #{PrNumber} — proceeding with rework", message.PrNumber);
        }

        Logger.LogInformation(
            "SE received change request from {Reviewer} on own PR #{PrNumber}",
            message.ReviewerAgent, message.PrNumber);

        ReworkQueue.Enqueue(new ReworkItem(message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
        return Task.CompletedTask;
    }

    private Task HandlePlanningCompleteAsync(PlanningCompleteMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Planning complete signal received from {Agent}: {Count} issues created",
            message.FromAgentId, message.IssueCount);
        _planningSignalReceived = true;
        return Task.CompletedTask;
    }

    #endregion
}
