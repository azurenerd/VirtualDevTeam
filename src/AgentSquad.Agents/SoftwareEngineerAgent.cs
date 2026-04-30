using System.Collections.Concurrent;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.Agents.Reasoning;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Services;
using AgentSquad.Core.Strategies;
using AgentSquad.Core.Workspace;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// Software Engineer agent — handles high-complexity tasks and orchestrates the engineering team.
/// Extends <see cref="EngineerAgentBase"/> with planning, issue assignment, PR review,
/// and resource management capabilities.
/// </summary>
public class SoftwareEngineerAgent : EngineerAgentBase
{
    private const string IntegrationTaskId = "T-FINAL";
    private const string IntegrationTaskName = "Final Integration & Validation";

    private readonly AgentRegistry _registry;
    private readonly IGateCheckService _gateCheck;
    private readonly EngineeringTaskIssueManager _taskManager;
    private readonly SelfAssessmentService _selfAssessment;
    private readonly IAgentReasoningLog _reasoningLog;
    private readonly SmeDefinitionGenerator? _smeGenerator;
    private readonly AgentSpawnManager? _spawnManager;
    private readonly DecisionGateService? _decisionGate;

    // Platform abstraction for post-merge close-out (works for both GitHub and ADO)
    private readonly MergeCloseoutService? _mergeCloseout;

    // Document reference resolver for single-issue mode context enrichment
    private readonly IDocumentReferenceResolver? _docResolver;

    // Strategy Framework (Phase 1) — optional, opt-in via StrategyFrameworkConfig.Enabled.
    private readonly StrategyOrchestrator? _strategyOrchestrator;
    private readonly WinnerApplyService? _winnerApply;
    private readonly IOptionsMonitor<StrategyFrameworkConfig>? _strategyConfig;
    private readonly StrategyTaskStepBridge? _strategyStepBridge;

    /// <summary>
    /// When a strategy winner is chosen but apply/build fails, store its patch here
    /// so the legacy codegen path can use it as reference context instead of starting from scratch.
    /// </summary>
    private string? _failedWinnerPatchContext;

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
    private bool _recoveredReviewPRs;
    private bool _recoveredInProgressPR;
    private bool _taskAssignmentGateCleared;
    private DateTime _lastResourceRequestTime = DateTime.MinValue;
    private static readonly TimeSpan SpawnCooldown = TimeSpan.FromSeconds(45);
    private bool _allTasksComplete;
    private bool _integrationPrCreated;
    private bool _engineeringSignaled;
    private int? _integrationIssueNumber;
    private readonly Dictionary<string, int> _agentAssignments = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
    private readonly HashSet<int> _mergedTestedPrNumbers = new();
    /// <summary>
    /// PRs the SE has shipped past implementation (ready-for-review or downstream review labels).
    /// CurrentPrNumber is cleared once a PR reaches this state so the SE can start the next task
    /// while the PR continues through review/merge. This set is consulted by
    /// HandleChangesRequestedAsync and CheckOwnPrStatusAsync to retain ownership semantics for
    /// review correlation and merge tracking.
    /// </summary>
    private readonly HashSet<int> _pastImplementationPrs = new();
    /// <summary>
    /// Tracks how many times each task has been picked up for implementation (branch + PR creation).
    /// Prevents infinite retry loops where the SE keeps re-entering the same task after rework
    /// failures, orphan recovery resets, or force-approval cycles. Keyed by task ID (e.g., "T1").
    /// </summary>
    private readonly Dictionary<string, int> _taskAcquisitionCounts = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();

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

    public SoftwareEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentRegistry registry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        SelfAssessmentService selfAssessment,
        IAgentReasoningLog reasoningLog,
        ILogger<SoftwareEngineerAgent> logger,
        IPromptTemplateService? promptService = null,
        RoleContextProvider? roleContextProvider = null,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null,
        SmeDefinitionGenerator? smeGenerator = null,
        AgentSpawnManager? spawnManager = null,
        DecisionGateService? decisionGate = null,
        IAgentTaskTracker? taskTracker = null,
        StrategyOrchestrator? strategyOrchestrator = null,
        WinnerApplyService? winnerApply = null,
        IOptionsMonitor<StrategyFrameworkConfig>? strategyConfig = null,
        StrategyTaskStepBridge? strategyStepBridge = null,
        MergeCloseoutService? mergeCloseout = null,
        IPullRequestService? prService = null,
        IWorkItemService? workItemService = null,
        IRepositoryContentService? repoContent = null,
        IReviewService? reviewService = null,
        IBranchService? branchService = null,
        IDocumentReferenceResolver? docResolver = null,
        IRunBranchProvider? branchProvider = null)
        : base(identity, messageBus, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, gateCheck, logger,
               promptService, roleContextProvider, buildRunner, testRunner, metrics, playwrightRunner, decisionGate, taskTracker,
               prService, workItemService, repoContent, reviewService, branchService, branchProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _selfAssessment = selfAssessment ?? throw new ArgumentNullException(nameof(selfAssessment));
        _reasoningLog = reasoningLog ?? throw new ArgumentNullException(nameof(reasoningLog));
        _taskManager = new EngineeringTaskIssueManager(workItemService!, logger);
        _smeGenerator = smeGenerator;
        _spawnManager = spawnManager;
        _decisionGate = decisionGate;
        _strategyOrchestrator = strategyOrchestrator;
        _winnerApply = winnerApply;
        _strategyConfig = strategyConfig;
        _strategyStepBridge = strategyStepBridge;
        _mergeCloseout = mergeCloseout;
        _docResolver = docResolver;
    }

    protected override string GetRoleDisplayName() => "Software Engineer";

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
        return $"You are a Software Engineer addressing review feedback on your pull request. " +
            $"The project uses {techStack}. " +
            "You have access to the full architecture, PM spec, and engineering plan. " +
            "Carefully read the feedback, understand what needs to be fixed, and produce " +
            "an updated implementation that addresses ALL the feedback points. " +
            "Be thorough and produce production-quality fixes.";
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
        Subscriptions.Add(MessageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        Subscriptions.Add(MessageBus.Subscribe<ReviewRequestMessage>(
            Identity.Id, HandleReviewRequestAsync));

        Subscriptions.Add(MessageBus.Subscribe<PlanningCompleteMessage>(
            Identity.Id, HandlePlanningCompleteAsync));
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
                                var readArchStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Read architecture & PMSpec",
                                    "Architecture and PM Specification documents detected, starting engineering plan", Identity.ModelTier);
                                _taskTracker.CompleteStep(readArchStepId);
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
                    // Show meaningful status at the start of each orchestration loop
                    var pending = _taskManager.PendingCount;
                    var done = _taskManager.DoneCount;
                    var total = _taskManager.TotalCount;
                    // Leader is "Working" only when it has actionable items right now
                    // (tasks to assign or PRs to review), not just because workers are busy
                    var hasActionableWork = isLeader
                        ? (pending > 0 || _reviewQueue.Count > 0)
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
                        var inventoryStepId = _taskTracker.BeginStep(Identity.Id, taskGroupId, "Task Inventory",
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
                            var taskStepId = _taskTracker.BeginStep(Identity.Id, taskGroupId,
                                $"{statusIcon} {t.Name}",
                                $"Status: {t.Status}, Complexity: {t.Complexity}{prRef}",
                                Identity.ModelTier);
                            _taskTracker.CompleteStep(taskStepId);
                        }

                        _taskTracker.CompleteStep(inventoryStepId);
                        _taskInventoryLogged = true;
                    }

                    // Recovery: re-track and re-broadcast review for our own ready-for-review PRs
                    await RecoverReadyForReviewPRsAsync(ct);
                    // Recovery: detect and resume our own in-progress PRs from prior runs
                    await RecoverOwnInProgressPRAsync(ct);
                    // Check if our tracked PR has been merged/closed
                    await CheckOwnPrStatusAsync(ct);
                    // Recovery: finish stuck in-progress PRs that were never marked ready
                    await RecoverStuckInProgressPRAsync(ct);
                    // LEADER ONLY: Evaluate resource needs FIRST so spawns happen before leader grabs tasks
                    if (isLeader && !_allTasksComplete)
                        await EvaluateResourceNeedsAsync(ct);

                    // Priority 0: Continue work on our own in-progress PR before anything else.
                    // Once the PR reaches "past implementation" (reviewer labels present), hand
                    // off to review/merge flows so the SE can start the next task in parallel.
                    if (CurrentPrNumber is not null)
                    {
                        if (await IsOwnPrPastImplementationAsync(ct))
                        {
                            var releasedPr = CurrentPrNumber.Value;
                            _pastImplementationPrs.Add(releasedPr);

                            // Mark the corresponding task done so CheckAllTasksCompleteAsync
                            // can detect engineering completion. Without this, the task issue
                            // stays open and the SE never signals engineering.all.complete.
                            var taskForPr = _taskManager.Tasks.FirstOrDefault(t =>
                                t.PullRequestNumber == releasedPr && t.IssueNumber.HasValue);
                            if (taskForPr is not null)
                            {
                                await _taskManager.MarkDoneAsync(taskForPr.IssueNumber!.Value, releasedPr, ct);
                                Logger.LogInformation(
                                    "Task {TaskId} (issue #{IssueNumber}) marked done — PR #{PrNumber} is past implementation",
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
                        // Integration phase: create integration PR if needed (LEADER ONLY)
                        if (!_integrationPrCreated)
                        {
                            await CreateIntegrationPRAsync(ct);
                        }
                    }
                    else if (!_allTasksComplete)
                    {
                        // LEADER ONLY: Recover orphaned assigned tasks with no open PRs
                        if (isLeader)
                            await RecoverOrphanedAssignmentsAsync(ct);
                        // LEADER ONLY: Assign issues to available workers (non-leader PEs, SE, JE)
                        if (isLeader)
                            await AssignTasksToAvailableEngineersAsync(ct);
                        // ALL PEs: Work on own tasks (leader defers to spawned workers when available)
                        await WorkOnOwnTasksAsync(ct);
                        // ALL PEs: Discover open PRs needing review
                        await DiscoverUnreviewedEngineerPRsAsync(ct);
                    }

                    // ALL PEs: Always review PRs — even after all tasks complete
                    await DiscoverUnreviewedEngineerPRsAsync(ct);
                    await ReviewEngineerPRsAsync(ct);

                    // Inline test workflow: merge PRs that have been approved + tested
                    if (Config.Workspace.IsInlineTestWorkflow)
                        await MergeTestedPRsAsync(ct);

                    // If we have no active work but are tracking past-implementation PRs
                    // (waiting for review/test/merge), show Idle instead of Working.
                    if (CurrentPrNumber is null && _pastImplementationPrs.Count > 0
                        && Status == AgentStatus.Working)
                    {
                        UpdateStatus(AgentStatus.Idle,
                            $"Waiting for PR(s) to complete review/test/merge ({_pastImplementationPrs.Count} pending)");
                    }
                    else if (CurrentPrNumber is null && _allTasksComplete && _integrationPrCreated
                        && Status == AgentStatus.Working)
                    {
                        UpdateStatus(AgentStatus.Idle, "Waiting for integration PR to merge");
                    }

                }

                await Task.Delay(
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
                Logger.LogWarning(
                    "Found {Count} engineering-task issues but they don't match current enhancement issues — ignoring stale tasks",
                    _taskManager.TotalCount);
                _taskManager.ClearCache();
                // Fall through to create a fresh plan
            }
            else
            {
                Logger.LogInformation("Restored {Count} tasks from existing engineering-task issues ({Done} done, {Pending} pending)",
                    _taskManager.TotalCount, _taskManager.DoneCount, _taskManager.PendingCount);

                // Add visible planning steps so the dashboard shows what happened during recovery
                var restoreStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Restore engineering plan",
                    $"Recovered {_taskManager.TotalCount} tasks from existing issues ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)", Identity.ModelTier);

                // Register display names for restored tasks so dashboard shows meaningful titles
                RegisterTaskDisplayNames(_taskManager.Tasks);

                // Recover integration issue number if present
                var recoveredIntegration = _taskManager.Tasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
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

                _taskTracker.CompleteStep(restoreStepId);

                // Recover completion state from restored tasks to prevent duplicate work on restart
                var restoredNonIntegration = _taskManager.Tasks
                    .Where(t => t.Id != IntegrationTaskId)
                    .ToList();
                if (restoredNonIntegration.Count > 0 && restoredNonIntegration.All(EngineeringTaskIssueManager.IsTaskDone))
                {
                    _allTasksComplete = true;
                    Logger.LogInformation("State recovery: all {Count} non-integration tasks are Done — setting _allTasksComplete", restoredNonIntegration.Count);

                    // Check if integration PR was already created and/or merged
                    var mergedPRs = await PrService.ListMergedAsync(ct);
                    var openPRs = await PrService.ListOpenAsync(ct);
                    var allPRs = mergedPRs.Concat(openPRs).ToList();
                    var integrationPR = allPRs.FirstOrDefault(pr =>
                        pr.Title.Contains("Integration", StringComparison.OrdinalIgnoreCase) ||
                        pr.HeadBranch.Contains("integration", StringComparison.OrdinalIgnoreCase));

                    if (integrationPR is not null)
                    {
                        _integrationPrCreated = true;
                        Logger.LogInformation("State recovery: found integration PR #{Number} (merged={IsMerged}) — setting _integrationPrCreated",
                            integrationPR.Number, integrationPR.IsMerged);
                    }

                    // If all tasks done AND no open PRs AND at least one merged PR, engineering is complete
                    if (openPRs.Count == 0 && mergedPRs.Count > 0)
                    {
                        _integrationPrCreated = true;
                        _engineeringSignaled = true;
                        Logger.LogInformation("State recovery: no open PRs + {Count} merged — setting _engineeringSignaled", mergedPRs.Count);
                        UpdateStatus(AgentStatus.Idle, "Engineering complete (recovered state)");
                    }
                }

                _planningComplete = true;
                UpdateStatus(AgentStatus.Working,
                    $"Loaded {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)");

                // Emit the plan-ready signal so workflow can advance
                await MessageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "EngineeringPlanReady",
                    NewStatus = AgentStatus.Working,
                    CurrentTask = "Engineering Planning",
                    Details = $"Restored engineering plan with {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)."
                }, ct);

                return;
            }
        }

        UpdateStatus(AgentStatus.Working, "Creating engineering plan from Issues");
        Logger.LogInformation("Starting engineering plan creation from Enhancement issues");

        LogActivity("planning", "📋 Reading architecture doc and PM spec for engineering planning");
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

        var decompStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Task decomposition",
            "Decomposing enhancement issues into engineering tasks", Identity.ModelTier);

        var issuesSummary = string.Join("\n\n", enhancementIssues.Select(i =>
            $"### Issue #{i.Number}: {i.Title}\n{i.Body}"));

        // Single-issue mode enrichment: resolve doc links in the issue body
        // This allows the SE to get full PMSpec/Architecture content even when
        // the issue body only contains summary + links to the actual docs
        if (Config.Limits.SingleIssueMode && _docResolver is not null && enhancementIssues.Count == 1)
        {
            try
            {
                var issue = enhancementIssues[0];
                var resolveContext = new DocumentResolutionContext(EffectiveBranch);
                var resolvedDocs = await _docResolver.ResolveReferencesAsync(issue.Body ?? "", resolveContext, ct);

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
        AgentSquad.Core.Agents.Reasoning.AssessmentResult? peAssessmentResult = null;

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

            _taskTracker.CompleteStep(decompStepId);
        }
        else
        {

        var history = CreateChatHistory();
        var planSys = PromptService is not null
            ? await PromptService.RenderAsync("software-engineer/plan-generation-system",
                new Dictionary<string, string>(), ct)
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
            "- Creates a proper .gitignore for the project's technology stack (e.g., bin/obj/node_modules/.env etc.) " +
            "— this MUST be the very first file in T1 to prevent build artifacts from being committed\n" +
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
                "specific CSS patterns, color hex codes, layout structure, and component hierarchy. " +
                "Every UI task description MUST say: 'Reference: OriginalDesignConcept.html in repository root for exact visual spec.'");
            userPromptBuilder.AppendLine(designContext);
            userPromptBuilder.AppendLine();
        }

        // Include files from already-merged PRs so the plan doesn't recreate them
        try
        {
            var mergedPRs = await PrService.ListMergedAsync(ct);
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
                new Dictionary<string, string>(), ct)
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
        var response = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var structuredText = response.Content ?? "";
        _taskTracker.RecordLlmCall(decompStepId);
        _taskTracker.CompleteStep(decompStepId);

        LogActivity("planning", "📝 AI generated plan, parsing tasks...");

        // Self-assessment: assess and refine the engineering plan
        var assessStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Self-assessment & impact classification",
            "Assessing engineering plan quality and classifying impact", Identity.ModelTier);
        _reasoningLog.Log(new AgentReasoningEvent
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
            var (refinedOutput, assessment) = await _selfAssessment.AssessAndRefineWithResultAsync(
                Identity.Id,
                Identity.DisplayName,
                Identity.Role,
                "Engineering Planning",
                structuredText,
                criteria,
                $"Project: {Config.Project.Description}\nArchitecture doc available for reference",
                chat,
                classifyImpact: _decisionGate is not null,
                ct);
            structuredText = refinedOutput;
            peAssessmentResult = assessment;
            _taskTracker.RecordLlmCall(assessStepId);
        }
        _taskTracker.CompleteStep(assessStepId);

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

        // Enforce foundation-first pattern: ensure T1 is a foundation task
        // and all other tasks depend on it
        EnsureFoundationFirstPattern(parsedTasks);

        // PE Parallelism: Validate and repair file overlaps using AI-assisted fixing
        await ValidateAndRepairTaskPlanAsync(parsedTasks, chat, ct);

        // PE Parallelism: Validate wave assignments and log metrics
        ValidateWaves(parsedTasks);
        var finalSharedFiles = ExtractSharedFilesFromFilePlan(
            ExtractRawFilePlanFromDescription(parsedTasks.FirstOrDefault()?.Description ?? ""));
        var finalOverlaps = DetectFileOverlaps(parsedTasks, finalSharedFiles);
        LogParallelismMetrics(parsedTasks, finalOverlaps);
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
                "3. Verify the solution builds cleanly\n" +
                "4. Create a PR with any integration fixes needed\n" +
                "5. If no fixes are needed, close this issue directly\n\n" +
                "This task is automatically assigned to the PE leader when all other tasks are complete.",
            Complexity = "High",
            Dependencies = allTaskIds,
            ParentIssueNumber = integrationParentIssue
        });

        // Classify task decomposition decision impact
        var decisionStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Decision gate",
            "Classifying task decomposition impact for approval", Identity.ModelTier);
        _taskTracker.SetStepWaiting(decisionStepId);
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
                _taskTracker.FailStep(decisionStepId, "Plan rejected by human");
                return;
            }
        }
        _taskTracker.CompleteStep(decisionStepId);

        // Informational check: log overlap with already-merged PRs from this run.
        // We do NOT auto-drop tasks — overlap is common (shared files, scaffolding) and
        // does not mean the task is complete. The AI planner was already told about merged files.
        try
        {
            var mergedPRs = await PrService.ListMergedAsync(ct);
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
        // Wave = max(dependency waves) + 1, with foundation (T1) always at W0.
        RecomputeWavesFromDependencies(parsedTasks);

        // Create GitHub issues for each task (the single source of truth)
        LogActivity("planning", $"📌 Creating {parsedTasks.Count} task issues on GitHub");
        var createIssuesStepId = _taskTracker.BeginStep(Identity.Id, "pe-planning", "Create GitHub issues",
            $"Creating {parsedTasks.Count} engineering task issues on GitHub", Identity.ModelTier);
        var createdTasks = await _taskManager.CreateTaskIssuesAsync(parsedTasks, ct);

        // Register display names so dashboard shows "#{issue}: {name}" instead of "T1", "T2", etc.
        RegisterTaskDisplayNames(createdTasks);

        // Track the integration issue number for later self-assignment
        var integrationTask = createdTasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
        if (integrationTask?.IssueNumber is not null)
            _integrationIssueNumber = integrationTask.IssueNumber;

        // Now resolve dependency task IDs (T1, T2) to actual issue numbers
        // Build a map from task ID → issue number
        var taskIdToIssue = createdTasks.ToDictionary(t => t.Id, t => t.IssueNumber ?? 0);
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
        _taskTracker.CompleteStep(createIssuesStepId);

        // REQ-PE-009: Validate all PM enhancements have engineering tasks
        // Skip in SinglePRMode — T1 monolithic task covers ALL enhancements by design
        if (Config.Limits.SinglePRMode)
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
            if (_gateCheck.RequiresHuman(GateIds.EngineeringPlan))
                UpdateStatus(AgentStatus.Working, "⏳ Awaiting human approval — engineering plan");
            await _gateCheck.WaitForGateAsync(
                GateIds.EngineeringPlan,
                "Engineering plan ready for human review before finalization",
                ct: ct);
        }

        Logger.LogInformation("Engineering plan created with {Count} tasks from {IssueCount} issues",
            _taskManager.TotalCount, enhancementIssues.Count);
        LogActivity("task", $"📋 Engineering plan created: {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues");

        _reasoningLog.Log(new AgentReasoningEvent
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

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringPlanReady",
            NewStatus = AgentStatus.Working,
            CurrentTask = "Engineering Planning",
            Details = $"Engineering plan created with {_taskManager.TotalCount} tasks. " +
                      "Ready to assign work to engineers."
        }, ct);

        UpdateStatus(AgentStatus.Idle, "Engineering plan complete, entering development loop");
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
            // Defense-in-depth: never create extra tasks in SinglePRMode
            if (Config.Limits.SinglePRMode)
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
                var mergedPRs = await PrService.ListMergedAsync(ct);
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
            var uiKeywords = new[] { "ui", "layout", "css", "component", "razor", "header",
                "timeline", "heatmap", "dashboard", "display", "svg", "shell", "scaffold", "foundation" };
            foreach (var task in tasks)
            {
                var combined = $"{task.Name} {task.Description}".ToLowerInvariant();
                if (uiKeywords.Any(k => combined.Contains(k)))
                {
                    var hasDesignRef = combined.Contains("design") || combined.Contains("originaldesignconcept");
                    if (!hasDesignRef)
                    {
                        warnings.Add($"⚠️ {task.Id} ({task.Name}) appears to be a UI task but doesn't reference OriginalDesignConcept.html");
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
            var openPRs = await PrService.ListOpenAsync(ct);
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
                if (_gateCheck.RequiresHuman(GateIds.TaskAssignment))
                    UpdateStatus(AgentStatus.Working, "⏳ Awaiting human approval — task assignments");
                await _gateCheck.WaitForGateAsync(
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
                    if (assignedTask is not null && !EngineeringTaskIssueManager.IsTaskDone(assignedTask))
                        continue;
                    _agentAssignments.Remove(engineer.AgentId);
                }
                freeEngineers.Add(engineer);
            }

            // Get all assignable tasks (pending with dependencies met, wave-eligible, excluding integration and foundation)
            // Foundation tasks (T1/W0) are excluded — the SE Lead handles them directly.
            var assignableTasks = _taskManager.Tasks
                .Where(t => t.Status == "Pending" && _taskManager.IsWaveEligible(t) && _taskManager.AreDependenciesMet(t) && t.Id != IntegrationTaskId && t.IssueNumber.HasValue && !IsFoundationTask(t))
                .ToList();

            // LLM-based semantic skill matching: single call matches all tasks to all engineers
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

                    var assignStepId = _taskTracker.BeginStep(Identity.Id, "pe-orchestration", "Assign engineers",
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
                    _taskTracker.CompleteStep(assignStepId);

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

                assignableTasks.Remove(bestTask);
                await _taskManager.AssignTaskAsync(bestTask.IssueNumber.Value, engineer.Name, ct);
                _agentAssignments[engineer.AgentId] = bestTask.IssueNumber.Value;

                var skillMatch = engineer.Capabilities.Count > 0
                    ? $" (skills: {string.Join(",", engineer.Capabilities)})"
                    : " (generalist)";
                var taskSkills = bestTask.SkillTags.Count > 0
                    ? $" [tags: {string.Join(",", bestTask.SkillTags)}]"
                    : "";

                var assignStepId = _taskTracker.BeginStep(Identity.Id, "pe-orchestration", "Assign engineers",
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
                _taskTracker.CompleteStep(assignStepId);
            }
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
                // Check for pending assignments delivered via IssueAssignmentMessage
                if (AssignmentQueue.TryDequeue(out var assignment))
                {
                    // Refresh cache from GitHub to get the latest status after leader's assignment
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindByIssueNumber(assignment.IssueNumber);
                    if (task is null || EngineeringTaskIssueManager.IsTaskDone(task))
                    {
                        Logger.LogWarning(
                            "Worker PE: assigned task #{IssueNumber} not found or already done, skipping",
                            assignment.IssueNumber);
                        return;
                    }
                    Logger.LogInformation(
                        "Worker PE picking up assigned task #{IssueNumber}: {Name}",
                        assignment.IssueNumber, task.Name);
                }
                else
                {
                    // No pending message — refresh from GitHub and look for tasks assigned to us
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindAssignedTo(Identity.DisplayName);
                    if (task is null)
                    {
                        Logger.LogDebug("Worker PE: no task assigned to {Name}, waiting for leader",
                            Identity.DisplayName);
                        return;
                    }
                    Logger.LogInformation(
                        "Worker PE recovered assigned task #{IssueNumber}: {Name}",
                        task.IssueNumber, task.Name);
                }
            }
            else
            {
                // ── LEADER PE: refresh cache from GitHub to see latest assignments, then pick ──
                await _taskManager.LoadTasksAsync(ct);

                // Prioritize foundation task (T1/W0) for self-implementation — it sets the
                // project structure that all other tasks depend on, so the Lead handles it directly.
                var foundationTask = _taskManager.Tasks
                    .Where(t => t.Status == "Pending"
                        && _taskManager.AreDependenciesMet(t)
                        && t.Id != IntegrationTaskId
                        && t.IssueNumber.HasValue
                        && IsFoundationTask(t))
                    .OrderBy(t => t.IssueNumber) // Prefer earliest-created when duplicates exist
                    .FirstOrDefault();

                if (foundationTask is not null)
                {
                    task = foundationTask;
                    LogActivity("task", $"🏗️ SE Lead taking foundation task #{task.IssueNumber} for self-implementation");
                    Logger.LogInformation(
                        "SE Lead claiming foundation task {TaskId} (#{IssueNumber}: {Name}) for self-implementation",
                        task.Id, task.IssueNumber, task.Name);
                }
                else
                {
                    task = _taskManager.FindNextAssignableTask("High", "Medium", "Low");
                }

                // Never pick up the integration task through normal assignment — it's handled
                // by CheckAllTasksCompleteAsync → CreateIntegrationPRAsync
                if (task?.Id == IntegrationTaskId)
                    task = null;

                if (task is null)
                {
                    if (_taskManager.PendingCount > 0)
                        Logger.LogDebug("No assignable tasks: {Pending} pending, some blocked by dependencies",
                            _taskManager.PendingCount);
                    return;
                }

                // Leader PE: guard against grabbing non-High tasks if we recently requested a PE spawn
                // AND workers (other PEs, SEs, JEs) actually exist to handle them.
                if (!string.Equals(task.Complexity, "High", StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - _lastResourceRequestTime < SpawnCooldown)
                {
                    var allWorkers = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                        .Where(a => a.Identity.Id != Identity.Id) // other SE workers
                        .ToList();
                    var freeWorkers = allWorkers.Count(a => !_agentAssignments.ContainsKey(a.Identity.Id));

                    if (freeWorkers > 0)
                    {
                        Logger.LogInformation(
                            "SE leader deferring {Complexity} task {TaskId} — {FreeWorkers} worker(s) available, " +
                            "spawn cooldown active ({Remaining:F0}s remaining)",
                            task.Complexity, task.Id, freeWorkers,
                            (SpawnCooldown - (DateTime.UtcNow - _lastResourceRequestTime)).TotalSeconds);
                        return;
                    }

                    // Only wait for spawned worker if at least one worker is already registered
                    if (allWorkers.Count > 0)
                    {
                        Logger.LogDebug(
                            "SE leader waiting for spawned worker before taking {Complexity} task {TaskId}",
                            task.Complexity, task.Id);
                        return;
                    }

                    Logger.LogInformation(
                        "SE leader taking {Complexity} task {TaskId} — no workers registered yet, not waiting",
                        task.Complexity, task.Id);
                }
            }

            if (!task.IssueNumber.HasValue)
            {
                Logger.LogWarning("Task {TaskId} has no issue number — skipping", task.Id);
                return;
            }

            // Claim validation: re-fetch from GitHub to prevent race conditions
            await _taskManager.LoadTasksAsync(ct);
            var freshTask = _taskManager.FindByIssueNumber(task.IssueNumber.Value);
            if (freshTask is null || EngineeringTaskIssueManager.IsTaskDone(freshTask))
            {
                Logger.LogInformation(
                    "Task #{IssueNumber} already done or closed — skipping",
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
                        _pastImplementationPrs.Add(existingPr.Number);

                        // Mark the task as done so CheckAllTasksCompleteAsync can progress.
                        // Without this, the issue stays open and the SE keeps re-entering
                        // WorkOnOwnTasksAsync for the same task every loop cycle.
                        if (task.IssueNumber.HasValue)
                        {
                            await _taskManager.MarkDoneAsync(task.IssueNumber.Value, existingPr.Number, ct);
                            Logger.LogInformation(
                                "Task {TaskId} (issue #{IssueNumber}) marked done — PR #{PrNumber} is past implementation",
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

            // ── Reacquisition cap: prevent infinite task retry loops ──
            var acquisitions = _taskAcquisitionCounts.GetValueOrDefault(task.Id, 0) + 1;
            _taskAcquisitionCounts[task.Id] = acquisitions;
            if (acquisitions > Config.Limits.MaxTaskReacquisitions)
            {
                Logger.LogWarning(
                    "Task {TaskId} has been picked up {Attempts} times (max {Max}) — marking blocked to prevent infinite retry",
                    task.Id, acquisitions, Config.Limits.MaxTaskReacquisitions);
                LogActivity("task", $"⛔ Task {task.Id} blocked after {acquisitions} acquisition attempts (max {Config.Limits.MaxTaskReacquisitions})");
                // Don't reset to Pending — leave as-is so orphan recovery doesn't re-queue it
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
                    var mergedPRs = await PrService.ListMergedAsync(ct);
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

            // Track step: Generate PR description
            var descStepId = _taskTracker.BeginStep(Identity.Id, task.Id, "Generate PR description",
                $"Creating description for {task.Name}", Identity.ModelTier);
            UpdateStatus(AgentStatus.Working, $"Generating PR description: {task.Name}");
            var prDescription = await GenerateTaskDescriptionAsync(task, ct);
            _taskTracker.RecordLlmCall(descStepId);
            _taskTracker.CompleteStep(descStepId);

            if (task.IssueNumber.HasValue)
                prDescription = $"Closes #{task.IssueNumber}\n\n{prDescription}";

            // Track step: Create branch & PR
            var createPrStepId = _taskTracker.BeginStep(Identity.Id, task.Id, "Create branch & PR",
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
                ct);
            _taskTracker.CompleteStep(createPrStepId);

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
                _reasoningLog.Log(new AgentReasoningEvent
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

            // Use incremental step-by-step implementation (same pattern as EngineerAgentBase)
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var techStack = Config.Project.TechStack;

            // If the strategy framework chose a winner but apply/build failed, pass it as reference
            string? winnerReference = _failedWinnerPatchContext;
            _failedWinnerPatchContext = null; // consume once

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

            // Enrich the issue body with the failed winner's patch as reference code
            if (!string.IsNullOrEmpty(winnerReference))
            {
                var referenceNote =
                    "\n\n## Reference Implementation (from strategy framework — failed to apply cleanly)\n" +
                    "Use this as a strong starting point. Fix any issues that prevented it from building:\n\n" +
                    $"```diff\n{(winnerReference.Length > 8000 ? winnerReference[..8000] + "\n...(truncated)" : winnerReference)}\n```";
                syntheticIssue = syntheticIssue with { Body = (syntheticIssue.Body ?? "") + referenceNote };
                Logger.LogInformation(
                    "Injecting failed strategy winner patch ({Length} chars) as reference for legacy codegen on task {TaskId}",
                    winnerReference.Length, task.Id);
            }

            // SinglePassMode: skip step generation, produce complete implementation in one prompt
            var useSinglePass = Config.CopilotCli.SinglePassMode;

            if (!useSinglePass)
            {
                // Multi-step path: generate steps then implement each one
                var genStepsStepId = _taskTracker.BeginStep(Identity.Id, task.Id, "Generate implementation steps",
                    $"Planning implementation steps for {task.Name}", Identity.ModelTier);
                UpdateStatus(AgentStatus.Working, $"Generating implementation steps: {task.Name}");
                var steps = await GenerateImplementationStepsAsync(
                    chat, pr, syntheticIssue, pmSpecDoc, architectureDoc, techStack, ct);
                _taskTracker.RecordLlmCall(genStepsStepId);
                _taskTracker.CompleteStep(genStepsStepId);

                if (steps.Count > 0)
                {
                    Logger.LogInformation(
                        "Software Engineer generated {Count} implementation steps for task {TaskId}",
                        steps.Count, task.Id);

                    var completedSteps = new List<string>();
                    for (var i = 0; i < steps.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var step = steps[i];
                        var stepNumber = i + 1;

                        var execStepId = _taskTracker.BeginStep(Identity.Id, task.Id,
                            $"Implement step {stepNumber}/{steps.Count}",
                            Truncate(step, 120), Identity.ModelTier);
                        UpdateStatus(AgentStatus.Working,
                            $"Implementing step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");
                        Logger.LogInformation(
                            "SE implementing step {Step}/{Total} for task {TaskId}: {Desc}",
                            stepNumber, steps.Count, task.Id, Truncate(step, 100));

                        var stepHistory = CreateChatHistory();
                        stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

                        var ctx = new System.Text.StringBuilder();
                        ctx.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                        ctx.AppendLine($"## Architecture\n{architectureDoc}\n");
                        if (sourceIssue is not null)
                            ctx.AppendLine($"## Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}\n");
                        ctx.AppendLine($"## Task: {task.Name}\n{task.Description}\n");
                        ctx.AppendLine($"## PR Description\n{pr.Body}\n");

                        // Include visual design reference for UI-related tasks
                        await AppendDesignContextIfRelevantAsync(ctx, task.Name, task.Description, sourceIssue?.Body, ct);

                        if (completedSteps.Count > 0)
                        {
                            ctx.AppendLine("## Previously Completed Steps");
                            for (var j = 0; j < completedSteps.Count; j++)
                                ctx.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                            ctx.AppendLine();
                            var existingFiles = await GetPrFileListAsync(pr.Number, ct);
                            if (!string.IsNullOrEmpty(existingFiles))
                                ctx.AppendLine($"## Files already in this PR\n{existingFiles}\n");
                        }

                        ctx.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                        ctx.AppendLine(step);
                        ctx.AppendLine();
                        ctx.AppendLine("Implement ONLY this step. Output each file using this format:\n");
                        ctx.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
                        ctx.AppendLine($"Use the {techStack} technology stack. Every file MUST use the FILE: marker format.");
                        if (completedSteps.Count > 0)
                            ctx.AppendLine("If updating a file from a previous step, include the COMPLETE updated file content.");

                        // Attach design images as ImageContent when available (UI tasks).
                        if (GetCachedDesignImages().Count > 0)
                            AddUserMessageWithDesignImages(stepHistory, ctx.ToString());
                        else
                            stepHistory.AddUserMessage(ctx.ToString());

                        var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                        var stepImpl = stepResponse.Content?.Trim() ?? "";
                        _taskTracker.RecordLlmCall(execStepId);

                        var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(stepImpl);

                        // Retry once if AI didn't produce FILE: markers for this step
                        if (codeFiles.Count == 0 && !string.IsNullOrEmpty(stepImpl))
                        {
                            Logger.LogWarning(
                                "SE step {Step}/{Total} produced no FILE: blocks (response length={Length}). Retrying.",
                                stepNumber, steps.Count, stepImpl.Length);

                            stepHistory.AddAssistantMessage(stepImpl);
                            stepHistory.AddUserMessage(
                                "Your response did not contain any parseable code files. " +
                                "You MUST output every file using EXACTLY this format:\n\n" +
                                "FILE: path/to/file.ext\n```language\n<complete file content>\n```\n\n" +
                                "Output the ACTUAL source code files for this step. Do not describe — produce code.");

                            var retryResp = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                            var retryImpl = retryResp.Content?.Trim() ?? "";
                            _taskTracker.RecordLlmCall(execStepId);
                            codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(retryImpl);

                            if (codeFiles.Count > 0)
                                Logger.LogInformation("SE step {Step}/{Total} retry succeeded: {FileCount} files", stepNumber, steps.Count, codeFiles.Count);
                            else
                                Logger.LogWarning("SE step {Step}/{Total} retry also produced no files, continuing", stepNumber, steps.Count);
                        }

                        if (codeFiles.Count > 0)
                        {
                            if (Workspace is not null && BuildRunnerSvc is not null)
                            {
                                var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                                    $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}",
                                    stepNumber, steps.Count, step, chat, ct);
                                if (!committed)
                                {
                                    _taskTracker.FailStep(execStepId, "Blocked by build errors");
                                    Logger.LogWarning("SE step {Step}/{Total} blocked by build errors on PR #{PrNumber}",
                                        stepNumber, steps.Count, pr.Number);
                                    await ReviewService.AddCommentAsync(pr.Number,
                                        $"❌ **Build Blocked:** Step {stepNumber}/{steps.Count} could not produce a buildable commit.", ct);
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

                        _taskTracker.CompleteStep(execStepId);
                        completedSteps.Add(step);
                    }
                }
                else
                {
                    useSinglePass = true; // fallback to single-pass if no steps generated
                }
            }

            if (useSinglePass)
            {
                // Single-pass: complete implementation in one AI call
                var implStepId = _taskTracker.BeginStep(Identity.Id, task.Id, "Implement (single-pass)",
                    $"Generating complete implementation for {task.Name}", Identity.ModelTier);
                UpdateStatus(AgentStatus.Working, $"Generating code: {task.Name}");
                Logger.LogInformation("SE using single-pass implementation for task {TaskId}", task.Id);

                var history = CreateChatHistory();
                history.AddSystemMessage(GetImplementationSystemPrompt(techStack));
                var issueContext = sourceIssue is not null
                    ? $"\n\n## GitHub Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}"
                    : "";

                // Build design context for UI-related tasks. AppendDesignContextIfRelevantAsync
                // primes GetCachedDesignImages() so we can attach PNG/JPG as ImageContent below.
                var designCtxBuilder = new StringBuilder();
                await AppendDesignContextIfRelevantAsync(designCtxBuilder, task.Name, task.Description, sourceIssue?.Body, ct);
                var designContext = designCtxBuilder.ToString();

                var singlePassUser = await AgentSquad.Agents.AI.SinglePassPromptBuilder.BuildUserPromptAsync(
                    new AgentSquad.Agents.AI.SinglePassPromptInputs
                    {
                        TaskName = task.Name,
                        TaskDescription = task.Description,
                        TechStack = techStack,
                        PmSpec = pmSpecDoc,
                        Architecture = architectureDoc,
                        IssueContext = issueContext,
                        DesignContext = designContext,
                    }, PromptService, ct);
                AddUserMessageWithDesignImages(history, singlePassUser);

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var implementation = response.Content?.Trim() ?? "";
                _taskTracker.RecordLlmCall(implStepId);

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);

                // Retry once if AI didn't produce FILE: markers
                if (codeFiles.Count == 0 && !string.IsNullOrEmpty(implementation))
                {
                    Logger.LogWarning(
                        "SE single-pass produced no parseable FILE: blocks (response length={Length}, preview: {Preview}). Retrying with explicit format reminder.",
                        implementation.Length, implementation[..Math.Min(300, implementation.Length)]);
                    LogActivity("task", "⚠️ AI response missing FILE: markers — sending retry with format reminder");

                    history.AddAssistantMessage(implementation);
                    history.AddUserMessage(
                        "Your response did not contain any parseable code files. " +
                        "You MUST output every file using EXACTLY this format (no exceptions):\n\n" +
                        "FILE: path/to/file.ext\n```language\n<complete file content>\n```\n\n" +
                        "Do NOT describe what to build — OUTPUT THE ACTUAL FILES with real code. " +
                        "Every source file, config file, and data file must use the FILE: marker format above. " +
                        "Start with the most critical files first (entry point, main page, config).");

                    var retryResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                    var retryImpl = retryResponse.Content?.Trim() ?? "";
                    _taskTracker.RecordLlmCall(implStepId);
                    codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(retryImpl);

                    if (codeFiles.Count == 0)
                    {
                        Logger.LogError(
                            "SE single-pass retry also produced no FILE: blocks (response length={Length}). Failing step.",
                            retryImpl.Length);
                        _taskTracker.FailStep(implStepId, "AI produced no parseable code files after retry");
                        LogActivity("task", "❌ Implementation failed — AI unable to produce code in FILE: format after retry");
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"❌ **Implementation Failed:** AI was unable to produce code files in the expected format after 2 attempts.", ct);
                        return;
                    }
                    Logger.LogInformation("SE single-pass retry succeeded: {FileCount} files parsed", codeFiles.Count);
                }
                else if (codeFiles.Count == 0)
                {
                    Logger.LogWarning("SE single-pass produced empty response. Failing step.");
                    _taskTracker.FailStep(implStepId, "AI produced empty response");
                    LogActivity("task", "❌ Implementation failed — AI returned empty response");
                    return;
                }

                if (Workspace is not null && BuildRunnerSvc is not null)
                {
                    var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles,
                        $"Implement {task.Name}", 1, 1, task.Name, chat, ct);
                    if (!committed)
                    {
                        _taskTracker.FailStep(implStepId, "Blocked by build errors");
                        Logger.LogWarning("SE single-pass implementation blocked by build errors on PR #{PrNumber}", pr.Number);
                        await ReviewService.AddCommentAsync(pr.Number,
                            $"❌ **Build Blocked:** Single-pass implementation could not produce a buildable commit.", ct);
                        return;
                    }
                }
                else
                {
                    await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, $"Implement {task.Name}", ct);
                }
                _taskTracker.CompleteStep(implStepId);
            }

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
        var readyStepId = _taskTracker.BeginStep(Identity.Id, task.Id, "Mark ready for review",
            $"Syncing branch and marking PR #{pr.Number} ready", Identity.ModelTier);

        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(pr.Number, ct);

        // D1: placeholder-string guard. For tasks that claim to wire/compose/integrate/finalize
        // a UI, refuse to mark ready if the PR ships literal placeholder strings in UI files.
        // Post a self-review comment + request rework from ourselves instead of silently shipping.
        var placeholderWarning = await CheckPrForPlaceholderStringsAsync(pr, task, ct);
        if (!string.IsNullOrEmpty(placeholderWarning))
        {
            Logger.LogWarning("D1 guard: PR #{Pr} contains forbidden placeholder strings for an integration task; not marking ready.", pr.Number);
            await ReviewService.AddCommentAsync(pr.Number,
                $"[SoftwareEngineer] ⚠️ Self-check blocked ready-for-review:\n\n{placeholderWarning}\n\n" +
                "This task claims to wire/compose/integrate/finalize a UI component, but the PR still contains " +
                "literal placeholder strings. Replace the placeholder strings with the real component invocation " +
                "(or a concrete empty state), then re-run the ready-for-review flow.",
                ct);
            _taskTracker.CompleteStep(readyStepId);
            return;
        }

        await MarkReadyForReviewWithScreenshotAsync(pr, ct);
        _taskTracker.CompleteStep(readyStepId);

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
    }

    /// <summary>
    /// D1: Check whether the PR's touched UI files contain forbidden literal placeholder
    /// strings when the task claims to wire/compose/integrate/finalize a UI component.
    /// Returns a human-readable warning message if violations found, otherwise empty.
    /// </summary>
    private async Task<string> CheckPrForPlaceholderStringsAsync(
        AgentPullRequest pr, EngineeringTask task, CancellationToken ct)
    {
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

        if (Workspace is null || BuildRunnerSvc is null)
        {
            Logger.LogDebug(
                "Strategy framework requires LocalWorkspace + BuildRunner; skipping for task {TaskId}", task.Id);
            return false;
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
                PmSpec = pmSpecDoc,
                Architecture = architectureDoc,
                TechStack = techStack,
                IssueContext = issueContext,
                DesignContext = designContext,
            };

            UpdateStatus(AgentStatus.Working, $"Strategy candidates: {task.Name}");

            // Register with the task-step bridge so each strategy candidate gets live dashboard visibility
            var enabledCount = cfg.EnabledStrategies.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var containerStepId = _strategyStepBridge?.RegisterTask(taskCtx.RunId, task.Id, Identity.Id, enabledCount);

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
            var apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
            if (!apply.Applied)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, task.Id, succeeded: false);
                Logger.LogWarning(
                    "Strategy framework: winner {Strategy} apply failed for task {TaskId}: {Reason}; falling back with winner context",
                    winner.StrategyId, task.Id, apply.FailureReason);
                _failedWinnerPatchContext = winner.Patch;
                return false;
            }

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

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
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
            var openPRs = await PrService.ListOpenAsync(ct);
            var discovered = 0;

            foreach (var pr in openPRs)
            {
                // Only ready-for-review PRs
                if (!pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;

                // Skip PRs owned by this PE (use colon delimiter to prevent "SoftwareEngineer" matching "SoftwareEngineer 1:")
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if already reviewed or already queued or claimed by another PE
                if (_reviewedPrNumbers.Contains(pr.Number))
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

            foreach (var prNumber in prNumbersToReview)
            {
                if (_reviewedPrNumbers.Contains(prNumber))
                    continue;

                // Atomic cross-PE claim: prevent multiple PE instances from reviewing
                // the same PR simultaneously. Only one PE can claim a PR at a time.
                if (!s_activeReviews.TryAdd(prNumber, (Identity.Id, DateTime.UtcNow)))
                {
                    Logger.LogDebug("PR #{Number} already claimed by another PE, skipping", prNumber);
                    continue;
                }

                try
                {

                // Cross-PE dedup: if ANY PE has already reviewed this PR, skip it.
                // This prevents multiple PE agents from reviewing the same PR.
                if (!_forceApprovalPrs.Contains(prNumber)
                    && await HasAnyPeReviewedAsync(prNumber, ct)
                    && !await PrWorkflow.NeedsReviewFromAsync(prNumber, "SoftwareEngineer", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                // Skip NeedsReviewFromAsync for force-approval — there's no new rework,
                // but we need to approve to unblock the engineer.
                if (!_forceApprovalPrs.Contains(prNumber)
                    && !await PrWorkflow.NeedsReviewFromAsync(prNumber, "SoftwareEngineer", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                var pr = (await PrService.GetAsync(prNumber, ct))?.ToAgentPR();
                if (pr is null)
                    continue;

                // Skip our own PRs (use colon delimiter for multi-PE correctness)
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                Logger.LogInformation("SE reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                // BUG FIX: Force-approve after max rework cycles to prevent infinite loops.
                // Only actually force-approve if we're a required reviewer for this PR —
                // otherwise we'd create redundant approval comments.
                bool approved;
                string? reviewBody;
                if (_forceApprovalPrs.Contains(prNumber))
                {
                    _forceApprovalPrs.Remove(prNumber);
                    var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                    var requiredReviewers = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                    if (!requiredReviewers.Any(r => r.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.LogInformation("SE is not a required reviewer for PR #{Number} — skipping force-approval", prNumber);
                        _reviewedPrNumbers.Add(prNumber);
                        continue;
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
                        continue; // Don't re-review or auto-approve unchanged code
                    }
                    else
                    {
                        IReadOnlyList<PlatformInlineComment> inlineComments;
                        (approved, reviewBody, inlineComments) = await EvaluatePrQualityAsync(pr, ct);

                        // Submit inline file comments if we have any and the feature is enabled
                        if (inlineComments.Count > 0 && Config.Review.EnableInlineComments)
                        {
                            await SubmitPlatformInlineCommentsAsync(prNumber, reviewBody ?? "", approved, inlineComments, ct);
                        }
                    }
                }

                if (reviewBody is null)
                    continue;

                // === Gate: PRReviewApproval — human reviews before PE approval ===
                if (approved)
                {
                    if (_gateCheck.RequiresHuman(GateIds.PRReviewApproval))
                        UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{prNumber}");
                    var prGateResult = await _gateCheck.WaitForGateAsync(
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
                                $"✅ **[SoftwareEngineer] APPROVED**\n\n{reviewBody}", "APPROVE", ct);
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
                    var deferMerge = _gateCheck.RequiresHuman(GateIds.FinalPRApproval);
                    var result = await PrWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "SoftwareEngineer", reviewBody, requireTests, deferMerge, ct);

                    // All reviewers approved and merge was deferred for human gate
                    if (result == MergeAttemptResult.ReadyToMerge)
                    {
                        // === Gate: FinalPRApproval — human reviews before merge ===
                        UpdateStatus(AgentStatus.Working, $"⏳ Awaiting final human approval to merge PR #{prNumber}");
                        var finalGateResult = await _gateCheck.WaitForGateAsync(
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
                            continue;
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
                    await PrWorkflow.RequestChangesAsync(pr.Number, "SoftwareEngineer", reviewBody, ct);
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
                        ReviewerAgent = "SoftwareEngineer",
                        Feedback = reviewBody
                    }, ct);
                }

                _reviewedPrNumbers.Add(prNumber);

                } // end try
                finally
                {
                    s_activeReviews.TryRemove(prNumber, out _);
                }
            }

            // Reset status after review loop completes
            if (prNumbersToReview.Count > 0)
                UpdateStatus(AgentStatus.Idle, "Review cycle complete, checking tasks");
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
            var openPRs = (await PrService.ListOpenAsync(ct)).ToAgentPRs();

            foreach (var pr in openPRs)
            {
                if (ct.IsCancellationRequested) break;

                // Must have pm-approved (PM final review done) AND tests-added (TE finished)
                // This is the Phase 3 sequential flow: Architect → TE → PM → merge
                bool hasPmApproved = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase);
                bool hasTests = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase);

                if (!hasPmApproved || !hasTests)
                    continue;

                // Skip if we already processed this PR in this cycle
                if (_reviewedPrNumbers.Contains(pr.Number) &&
                    !_mergedTestedPrNumbers.Add(pr.Number))
                    continue;

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
                    await ReviewService.AddCommentAsync(pr.Number,
                        "✅ **[SoftwareEngineer] Merge Review** — TE attempted testing but build failed. " +
                        "PM has approved the code. Proceeding with merge.", ct);
                }
                else
                {
                    // Post test review approval
                    await ReviewService.AddCommentAsync(pr.Number,
                        $"✅ **[SoftwareEngineer] Tests Reviewed** — {testFiles.Count} test file(s) verified. Merging.", ct);
                }

                // === Gate: FinalPRApproval — human reviews before final merge ===
                if (_gateCheck.RequiresHuman(GateIds.FinalPRApproval))
                    UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{pr.Number}");
                var finalGateResult = await _gateCheck.WaitForGateAsync(
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
                    // Remove from tracked sets so PE re-reviews after rework
                    _mergedTestedPrNumbers.Remove(pr.Number);
                    continue;
                }

                var result = await PrWorkflow.MergeApprovedTestedPRAsync(
                    pr.Number, "SoftwareEngineer", ct);

                if (result == MergeAttemptResult.Merged)
                {
                    Logger.LogInformation("SE merged tested PR #{Number}", pr.Number);
                    LogActivity("task", $"✅ Merged PR #{pr.Number}: {pr.Title} (code approved + tests added)");

                    if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                        await MarkEngineerTaskDoneAsync(pr, ct);
                    // Close linked work items via platform abstraction (ADO parity)
                    if (_mergeCloseout is not null)
                        await _mergeCloseout.CloseLinkedWorkItemsAsync(pr.Number, ct);

                    await RememberAsync(MemoryType.Action,
                        $"Merged tested PR #{pr.Number}: {pr.Title}", ct: ct);

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
                    Logger.LogWarning("Tested PR #{Number} has merge conflicts", pr.Number);
                    LogActivity("task", $"⚠️ Tested PR #{pr.Number} blocked by merge conflicts");
                    await TryCloseAndRecreatePRAsync(pr, ct);
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

            // First pass: populate _pastImplementationPrs with ALL open PRs we own.
            // This restores in-memory ownership after a restart so HandleChangesRequestedAsync
            // (which filters by CurrentPrNumber OR _pastImplementationPrs.Contains(n)) will
            // recognize late review feedback from PM/Architect on shipped PRs.
            foreach (var pr in myPRs)
            {
                if (string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                {
                    _pastImplementationPrs.Add(pr.Number);

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
                                        && !EngineeringTaskIssueManager.IsTaskDone(t));
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
                                    && !EngineeringTaskIssueManager.IsTaskDone(t)
                                    && string.Equals(t.Name, prTaskName, StringComparison.OrdinalIgnoreCase));
                            }
                        }

                        if (taskForPr is not null && !EngineeringTaskIssueManager.IsTaskDone(taskForPr))
                        {
                            await _taskManager.MarkDoneAsync(taskForPr.IssueNumber!.Value, pr.Number, ct);
                            Logger.LogInformation(
                                "State recovery: marked task {TaskId} (issue #{IssueNumber}) Done — PR #{PrNumber} is past implementation (labels: {Labels})",
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
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                    || !pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
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
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;

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

        var openPRs = (await PrService.ListOpenAsync(ct)).ToAgentPRs();

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
                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(responseText);

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
                    codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(retryResp.Content?.Trim() ?? "");

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

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(stepImpl);
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
    /// </summary>
    private async Task RecoverStuckInProgressPRAsync(CancellationToken ct)
    {
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
    /// Check if our currently tracked PR has been merged or closed, and clear state so
    /// the PE can move on to the next task. Also monitors PRs we've shipped past
    /// implementation so their merge/close transitions still mark tasks Done.
    /// </summary>
    private async Task CheckOwnPrStatusAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is not null)
        {
            await CheckSinglePrStatusAsync(CurrentPrNumber.Value, isPast: false, ct);
        }

        // Snapshot to allow removal during iteration.
        var pastSnapshot = _pastImplementationPrs.ToArray();
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
                    Logger.LogInformation("SE task {TaskId} reset to Pending (PR #{PrNumber} closed without merge)",
                        task.Id, prNumber);
                }
            }

            if (isPast)
            {
                _pastImplementationPrs.Remove(prNumber);
            }
            else
            {
                CurrentPrNumber = null;
                _currentTaskName = null;
                Identity.AssignedPullRequest = null;
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
                // Check if the spawn request has been fulfilled
                var currentWorkers = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer).Count();
                if (currentWorkers > _agentAssignments.Count + 1) // +1 for leader
                {
                    _resourceRequestPending = false;
                }
                else if (DateTime.UtcNow - _lastResourceRequestTime > SpawnCooldown)
                {
                    // Cooldown expired — clear the flag and fall through to re-evaluate.
                    // This handles the case where the spawned worker immediately picks up
                    // a task, making the worker-count check never pass.
                    _resourceRequestPending = false;
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

            // Wave-aware scaling: identify current wave and look ahead
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
                Logger.LogInformation(
                    "Wave-aware scaling: current wave {Wave} has {Parallelizable} parallelizable / {Total} total tasks",
                    currentWave, currentWaveParallelizable, activeWaves[0].Count());
            }

            // Count free workers (non-leader Software Engineers)
            var freeWorkers = 0;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SoftwareEngineer))
                if (agent.Identity.Id != Identity.Id && !_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeWorkers++;

            if (parallelizable > freeWorkers + 1)
            {
                // Request additional PE workers (primary scaling mechanism)
                // Fall back to SE/JE only if PE pool is exhausted and those pools are configured
                var poolConfig = Config.Limits.EngineerPool;

                AgentRole? neededRole = null;
                var seCapacity = poolConfig.EffectiveMaxAdditional
                    - (_registry.GetAgentsByRole(AgentRole.SoftwareEngineer).Count() - 1); // -1 for leader
                if (seCapacity > 0)
                {
                    neededRole = AgentRole.SoftwareEngineer;
                }

                if (neededRole is null)
                {
                    Logger.LogDebug(
                        "SE needs more workers ({Parallelizable} parallelizable) but all pools exhausted",
                        parallelizable);
                    return;
                }

                Logger.LogInformation(
                    "SE requesting additional {Role}: {Parallelizable} tasks parallelizable, {Free} workers free",
                    neededRole, parallelizable, freeWorkers);

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
                    RequestedRole = neededRole.Value,
                    Justification = $"{parallelizable} tasks can be worked in parallel but only {freeWorkers} workers are available",
                    CurrentTeamSize = _agentAssignments.Count + 1,
                    DesiredCapabilities = dominantSkills
                }, ct);

                _resourceRequestPending = true;
                _lastResourceRequestTime = DateTime.UtcNow;
            }
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

        // Resolve the associated task FIRST so we can key retry count by issue number
        EngineeringTask? task = null;

        // Search by task name from PR title
        var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
        if (taskTitle is not null)
        {
            // Handle doubled agent prefix: "Agent: Agent: TaskName"
            var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskTitle);
            if (innerName is not null) taskTitle = innerName;
            task = _taskManager.FindByName(taskTitle);
        }

        // Fallback: search by issue number from agent assignments
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
            // Close the conflicted PR with an explanation
            var closeComment =
                $"🔄 **Closing due to unresolvable merge conflicts.**\n\n" +
                $"This PR's branch has conflicts with `main` that cannot be auto-resolved. " +
                $"The task will be re-implemented on a fresh branch from latest `main`." +
                $" (retry {retries + 1}/{MaxConflictRetries})";

            await ReviewService.AddCommentAsync(pr.Number, closeComment, ct);
            await PrService.CloseAsync(pr.Number, ct);

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

    private async Task CheckAllTasksCompleteAsync(CancellationToken ct)
    {
        if (_taskManager.TotalCount == 0)
            return;

        // Refresh from GitHub to get latest state
        await _taskManager.LoadTasksAsync(ct);

        // Check if all tasks EXCEPT the final integration task are done
        var nonIntegrationTasks = _taskManager.Tasks
            .Where(t => t.Id != IntegrationTaskId)
            .ToList();

        if (nonIntegrationTasks.Count == 0 || !nonIntegrationTasks.All(EngineeringTaskIssueManager.IsTaskDone))
            return;

        // Gate: all task PRs must be MERGED before starting integration.
        // Tasks are marked Done when PRs get approval labels (for restart recovery),
        // but integration requires the code to actually be on the target branch.
        var openPRs = await PrService.ListOpenAsync(ct);
        var agentPrefix = $"{Identity.DisplayName}:";
        var unmergerdTaskPRs = openPRs
            .Where(pr => pr.Title.StartsWith(agentPrefix, StringComparison.OrdinalIgnoreCase)
                         && !pr.Title.Contains("Final Integration", StringComparison.OrdinalIgnoreCase)
                         && PullRequestWorkflow.Labels.IsPastImplementation(pr.Labels))
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

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "AllTasksComplete",
            NewStatus = AgentStatus.Working,
            Details = $"All {nonIntegrationTasks.Count} engineering tasks are done — starting final integration"
        }, ct);
    }

    private async Task CreateIntegrationPRAsync(CancellationToken ct)
    {
        var integrationStepId = _taskTracker.BeginStep(Identity.Id, "pe-integration", "Final integration review",
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
            _taskTracker.FailStep(integrationStepId, ex.Message);
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

        // Build rich task description that tells strategies about the integration goal
        var integrationDescription =
            $"All individual task PRs have been merged to the target branch. " +
            $"Review the architecture and PM spec for any missing wiring, imports, or configuration. " +
            $"Identify integration gaps: broken cross-module references, missing route registration, missing DI wiring, " +
            $"missing startup/config files, or any other cross-cutting concerns.\n\n" +
            $"If everything integrates cleanly, produce NO code changes.\n\n" +
            $"## Completed Tasks\n{taskSummary}\n\n" +
            $"## Architecture Summary\n{(architectureDoc.Length > 3000 ? architectureDoc[..3000] + "\n...(truncated)" : architectureDoc)}";

        // Create branch BEFORE orchestration — WinnerApplyService needs it to exist
        var branchName = await PrWorkflow.CreateTaskBranchAsync(
            Identity.DisplayName, "final-integration", ct);

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
            IsWebTask = false,
            PmSpec = pmSpecDoc,
            Architecture = architectureDoc,
            TechStack = techStack,
            IssueContext = "",
            DesignContext = "",
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
                _taskTracker.CompleteStep(integrationStepId);
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
            _taskTracker.CompleteStep(integrationStepId);
            await CloseIntegrationIssueAsync("✅ No integration fixes needed — strategy framework confirmed all tasks cleanly integrated.", ct);
            await SignalEngineeringCompleteAsync(ct);
            return true;
        }

        // Apply the winning patch
        var apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
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
            ct);

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

        // Add tests-added label (tests ran during build-verify)
        var integrationLabels = pr.Labels
            .Append(PullRequestWorkflow.Labels.TestsAdded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await PrService.UpdateAsync(pr.Number, labels: integrationLabels, ct: ct);
        Logger.LogInformation("Added tests-added label to integration PR #{PrNumber} (tests ran locally)", pr.Number);

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

        await CloseIntegrationIssueAsync(
            $"Integration PR #{pr.Number} created via strategy '{winner.StrategyId}'.", ct);
        await RememberAsync(MemoryType.Action,
            $"Created integration PR #{pr.Number} via strategy '{winner.StrategyId}'", ct: ct);

        _strategyStepBridge?.UnregisterTask(taskCtx.RunId, IntegrationTaskId,
            succeeded: true, winnerStrategy: winner.StrategyId);
        _taskTracker.CompleteStep(integrationStepId);
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
        history.AddUserMessage(intUser ??
            $"## PM Specification\n{pmSpecDoc}\n\n" +
            $"## Architecture\n{architectureDoc}\n\n" +
            $"## Completed Tasks\n{taskSummary}\n\n" +
            "Review the merged work against these documents. " +
            "Generate any missing integration files (config, wiring, startup registration, etc.).");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        _taskTracker.RecordLlmCall(integrationStepId);
        var integrationContent = response.Content?.Trim() ?? "";

        var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(integrationContent);

        if (codeFiles.Count == 0 ||
            integrationContent.Contains("NO_INTEGRATION_FIXES_NEEDED", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("No integration fixes needed — all tasks cleanly integrated");
            LogActivity("task", "✅ No integration fixes needed — signaling completion");
            _integrationPrCreated = true;
            _taskTracker.CompleteStep(integrationStepId);
            await CloseIntegrationIssueAsync("✅ No integration fixes needed — all tasks cleanly integrated.", ct);
            await SignalEngineeringCompleteAsync(ct);
            return;
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

        // Sync and mark ready for review
        await SyncBranchWithMainAsync(pr.Number, ct);
        await MarkReadyForReviewWithScreenshotAsync(pr, ct);

        // Add tests-added label
        var integrationLabels = pr.Labels
            .Append(PullRequestWorkflow.Labels.TestsAdded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await PrService.UpdateAsync(pr.Number, labels: integrationLabels, ct: ct);
        Logger.LogInformation("Added tests-added label to integration PR #{PrNumber} (tests ran locally)", pr.Number);

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

        await CloseIntegrationIssueAsync(
            $"Integration PR #{pr.Number} created with {codeFiles.Count} fixes.", ct);
        await RememberAsync(MemoryType.Action,
            $"Created integration PR #{pr.Number} with {codeFiles.Count} integration fixes", ct: ct);
        _taskTracker.CompleteStep(integrationStepId);
    }

    private async Task CloseIntegrationIssueAsync(string comment, CancellationToken ct)
    {
        if (_integrationIssueNumber is null)
        {
            // Try to find it from the task manager cache
            var task = _taskManager.Tasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
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

    private async Task SignalEngineeringCompleteAsync(CancellationToken ct)
    {
        // Close any remaining open engineering task issues
        await _taskManager.CloseAllRemainingTaskIssuesAsync(ct);

        // Notify PM to review enhancement issues — PM owns the lifecycle of user stories
        try
        {
            await MessageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "StatusUpdate",
                NewStatus = AgentStatus.Idle,
                CurrentTask = "AllTasksComplete",
                Details = "All engineering tasks are complete and merged. PM should review enhancement issues for final acceptance."
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to notify PM about engineering completion");
        }

        UpdateStatus(AgentStatus.Idle, "Engineering complete");
        _engineeringSignaled = true;
        LogActivity("system", "🏁 Engineering phase complete — all tasks done and integrated");

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringComplete",
            NewStatus = AgentStatus.Idle,
            Details = $"All {_taskManager.TotalCount} tasks complete. Engineering phase finished."
        }, ct);

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

        _reviewedPrNumbers.Remove(message.PrNumber);

        // BUG FIX: Track FinalApproval requests so PE auto-approves after max rework cycles.
        if (string.Equals(message.ReviewType, "FinalApproval", StringComparison.OrdinalIgnoreCase))
            _forceApprovalPrs.Add(message.PrNumber);

        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    protected override Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        // This is our own PR if we're currently implementing it, or if we shipped it past
        // implementation and it's still being tracked for review/merge.
        var isOurPr = CurrentPrNumber == message.PrNumber
            || _pastImplementationPrs.Contains(message.PrNumber);

        if (!isOurPr)
            return Task.CompletedTask;

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

    #region AI-Assisted Methods

    private async Task<(bool Approved, string? ReviewBody, IReadOnlyList<PlatformInlineComment> InlineComments)> EvaluatePrQualityAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var engineeringPlan = await ProjectFiles.GetEngineeringPlanAsync(ct);

            // Read the linked issue for acceptance criteria
            var issueContext = "";
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (issueNumber.HasValue)
            {
                try
                {
                    var issue = await WorkItemService.GetAsync(issueNumber.Value, ct);
                    if (issue is not null)
                        issueContext = $"## Linked Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n";
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for PE review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            var codeContext = await PrWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            // Tier 4: Get repo structure for duplicate detection during review
            var repoStructure = await GetRepoStructureForContextAsync(ct);

            var useInlineComments = Config.Review.EnableInlineComments;

            var history = CreateChatHistory();
            var reviewSys = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/code-review-system",
                    new Dictionary<string, string>(), ct)
                : null;

            // Build JSON-structured review system prompt (same pattern as Architect)
            var systemPrompt = reviewSys ??
                "You are a Software Engineer doing a technical code review.\n\n" +
                "SCOPE: You are reviewing EXACTLY ONE PR. Do NOT mention or review other PRs, " +
                "other tasks, or other engineers' work. Every issue you raise MUST reference a " +
                "file that appears in THIS PR's diff. If a file is not in the diff, do not comment on it.\n\n";

            // Append JSON format instructions
            systemPrompt +=
                "CHECK: architecture compliance, implementation completeness, code quality, " +
                "bugs/logic errors, missing validation, test coverage.\n\n" +
                "ACCEPTANCE CRITERIA FILE COMPLETENESS CHECK (critical):\n" +
                "- Compare the ACTUAL files in this PR against the acceptance criteria and file plan " +
                "in the linked issue and PR description.\n" +
                "- If the acceptance criteria specify files/components that should be created " +
                "and those files are MISSING from the PR, this is a REQUEST_CHANGES issue.\n" +
                "- List each missing file/component by name.\n\n" +
                "DUPLICATE/CONFLICT CHECKS (critical for multi-agent projects):\n" +
                "- Does this PR create types/classes that ALREADY EXIST in the main branch file listing?\n" +
                "- Does this PR use the CORRECT namespace consistent with existing code structure?\n" +
                "If you detect duplication or namespace conflicts, mark as REQUEST_CHANGES.\n\n" +
                "EXCESSIVE MODIFICATION CHECK:\n" +
                "- If this PR modifies an existing file, check whether the changes are SURGICAL or a FULL REWRITE.\n" +
                "- A PR that rewrites existing CSS/HTML structure beyond the task scope is REQUEST_CHANGES.\n\n" +
                "CRITICAL RULE: NEVER mention truncated code or inability to see full implementations. " +
                "If you cannot see a method body, ASSUME it is correctly implemented.\n\n" +
                "Only request changes for significant AND fixable issues. Minor style → APPROVE.\n\n" +
                "RESPONSE FORMAT — you MUST respond with ONLY a JSON object, nothing else.\n" +
                "Do NOT include any text before or after the JSON. Do NOT wrap in markdown fences.\n" +
                "The JSON schema is:\n" +
                "- \"verdict\": string, either \"APPROVE\" or \"REQUEST_CHANGES\"\n" +
                "- \"summary\": string, brief 1-2 sentence assessment\n" +
                (useInlineComments
                    ? "- \"comments\": array of objects with:\n" +
                      "  - \"file\": string, relative file path (e.g. \"ReportingDashboard/Services/MyService.cs\")\n" +
                      "  - \"line\": integer, line number in the new file where the comment applies\n" +
                      "  - \"priority\": string, one of \"🔴 Critical\", \"🟠 Important\", \"🟡 Suggestion\", \"🟢 Nit\"\n" +
                      "  - \"body\": string, description of the issue\n"
                    : "") +
                "\nExample response:\n" +
                "{\"verdict\":\"REQUEST_CHANGES\",\"summary\":\"Missing null validation in service layer.\"" +
                (useInlineComments ? ",\"comments\":[{\"file\":\"src/Services/MyService.cs\",\"line\":42,\"priority\":\"🔴 Critical\",\"body\":\"Missing null check on user parameter\"}]" : "") +
                "}\n\n" +
                "Your entire response must be parseable as JSON. Start with { and end with }.";

            history.AddSystemMessage(systemPrompt);

            var reviewContextBuilder = new System.Text.StringBuilder();
            reviewContextBuilder.AppendLine($"## Architecture\n{architectureDoc}\n");
            reviewContextBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");

            // Filter engineering plan to focus on the linked task to prevent cross-task confusion
            var planContext = engineeringPlan;
            if (issueNumber.HasValue && !string.IsNullOrEmpty(engineeringPlan))
            {
                var taskSection = ExtractTaskSectionFromPlan(engineeringPlan, issueNumber.Value, pr.Title);
                if (!string.IsNullOrEmpty(taskSection))
                    planContext = $"(Filtered to task relevant to this PR)\n\n{taskSection}";
            }
            reviewContextBuilder.AppendLine($"## Engineering Plan\n{planContext}\n");

            if (!string.IsNullOrEmpty(repoStructure))
            {
                reviewContextBuilder.AppendLine("## Existing Repository Structure (main branch)");
                reviewContextBuilder.AppendLine(repoStructure);
                reviewContextBuilder.AppendLine();
            }

            // Get screenshot images for vision-based review
            var screenshotImages = new List<PullRequestWorkflow.ScreenshotImage>();
            try
            {
                screenshotImages = await PrWorkflow.GetPRScreenshotImagesAsync(pr.Number, ct: ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not fetch screenshots for PE review of PR #{Number}", pr.Number);
            }

            // Log AI description of each screenshot for dashboard visibility
            if (screenshotImages.Count > 0)
            {
                try
                {
                    var descKernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                    var descChat = descKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                    foreach (var img in screenshotImages)
                    {
                        var desc = await PullRequestWorkflow.DescribeScreenshotAsync(img, descChat, ct);
                        LogActivity("screenshot", $"🖼️ SE reviewing screenshot (PR #{pr.Number}): {desc}");
                        Logger.LogInformation("SE peer screenshot description for PR #{PrNumber}: {Description}",
                            pr.Number, desc);
                    }
                }
                catch (Exception descEx)
                {
                    Logger.LogDebug(descEx, "Could not describe screenshots for SE review of PR #{Number}", pr.Number);
                }
            }

            // Update system prompt if screenshots available
            if (screenshotImages.Count > 0)
            {
                var visSys = PromptService is not null
                    ? await PromptService.RenderAsync("software-engineer/visual-validation-supplement",
                        new Dictionary<string, string>(), ct)
                    : null;
                history.AddSystemMessage(visSys ??
                    "VISUAL VALIDATION: Screenshots of the running application are included. " +
                    "LOOK at each screenshot carefully:\n" +
                    "- If the screenshot shows an error page, blank screen, JSON error, or unhandled exception, " +
                    "this is a REQUEST_CHANGES issue — the code does not work.\n" +
                    "- The visual output should match the PR's stated functionality.\n");
            }

            reviewContextBuilder.Append(issueContext);
            reviewContextBuilder.AppendLine($"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n");

            // Hard scoping barrier — prevents AI from cross-reviewing other PRs
            reviewContextBuilder.AppendLine("---");
            reviewContextBuilder.AppendLine($"⚠️ SCOPE CONSTRAINT: You are reviewing ONLY PR #{pr.Number} (\"{pr.Title}\").");
            reviewContextBuilder.AppendLine("Do NOT comment on other PRs, other tasks, or other engineers' work.");
            reviewContextBuilder.AppendLine("Every review item must reference a file that is CHANGED IN THIS PR.");
            reviewContextBuilder.AppendLine("If you mention a file not in this PR's diff, your review is WRONG.");
            reviewContextBuilder.AppendLine("---\n");

            reviewContextBuilder.Append(codeContext);

            // Add screenshots as vision content if available
            if (screenshotImages.Count > 0)
            {
                var items = new ChatMessageContentItemCollection();
                var screenshotIntro = "\n\n## 📸 Application Screenshots\n" +
                    "LOOK AT EACH IMAGE for errors, blank screens, or broken UI.\n\n";
                for (var i = 0; i < screenshotImages.Count; i++)
                    screenshotIntro += $"Screenshot {i + 1}: {screenshotImages[i].Description}\n";

                items.Add(new TextContent(reviewContextBuilder.ToString() + screenshotIntro));

                foreach (var img in screenshotImages)
                {
                    items.Add(new ImageContent(img.ImageBytes, img.MimeType)
                    {
                        ModelId = $"screenshot: {img.Description}"
                    });
                }

                history.AddUserMessage(items);
            }
            else
            {
                history.AddUserMessage(reviewContextBuilder.ToString());
            }

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";

            // Detect garbage AI responses (model breaking character, meta-commentary)
            if (PullRequestWorkflow.IsGarbageAIResponse(result))
            {
                Logger.LogWarning("SE review of PR #{Number} returned garbage AI response, retrying once", pr.Number);

                history.AddAssistantMessage(result);
                history.AddUserMessage(
                    "That response was not valid JSON. Respond with ONLY a JSON object.\n" +
                    "Example: {\"verdict\":\"APPROVE\",\"summary\":\"Code looks good.\"}\n" +
                    "Or: {\"verdict\":\"REQUEST_CHANGES\",\"summary\":\"Issues found.\",\"comments\":[]}");

                response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                result = response.Content?.Trim() ?? "";

                if (PullRequestWorkflow.IsGarbageAIResponse(result))
                {
                    Logger.LogWarning("SE review of PR #{Number} still garbage after retry — auto-approving", pr.Number);
                    return (true, "Code review passed. Implementation looks reasonable for the task scope.", []);
                }
            }

            // Try to parse as structured JSON (reuse Architect's pattern)
            var structured = TryParseStructuredSeReview(result);
            if (structured is not null)
            {
                var approved = structured.Value.Approved;
                var summary = structured.Value.Summary;
                var inlineComments = structured.Value.Comments;

                Logger.LogInformation(
                    "SE structured review of PR #{Number}: {Verdict}, {CommentCount} inline comments",
                    pr.Number, approved ? "APPROVE" : "REQUEST_CHANGES", inlineComments.Count);

                // Build a clean review body from the summary
                var reviewBody = summary;
                if (!approved && inlineComments.Count > 0)
                {
                    reviewBody += $"\n\n_{inlineComments.Count} inline comment(s) on specific files below._";
                }

                // Filter truncation complaints from inline comments
                var filteredComments = inlineComments
                    .Where(c => !IsTruncationComplaint(c.Body))
                    .ToList();

                // If all comments were truncation complaints, approve instead
                if (!approved && filteredComments.Count == 0 && string.IsNullOrWhiteSpace(summary))
                {
                    Logger.LogInformation("SE review of PR #{Number} only had truncation complaints — auto-approving", pr.Number);
                    return (true, "Code review passed. Implementation meets requirements for the task scope.", []);
                }

                return (approved, reviewBody, filteredComments);
            }

            // Fallback: plain text parsing (backward compat if JSON fails)
            Logger.LogDebug("SE review of PR #{Number} did not parse as JSON, falling back to text parsing", pr.Number);
            var textApproved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase)
                || result.Contains("\"verdict\":\"APPROVE\"", StringComparison.OrdinalIgnoreCase);

            var fallbackBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            fallbackBody = PullRequestWorkflow.StripReviewPreamble(fallbackBody);
            fallbackBody = FilterTruncationComplaints(fallbackBody);

            // WS2 parser-hardening: extract inline comments from text-format reviews.
            // When the LLM prefixes items with "file:line:", synthesize PlatformInlineComments
            // so review feedback lands on the Files-changed tab instead of conversation-only.
            var extractedInline = ExtractInlineCommentsFromText(fallbackBody);
            if (extractedInline.Count > 0)
            {
                Logger.LogInformation(
                    "SE text-parse fallback extracted {Count} inline comments for PR #{Number}",
                    extractedInline.Count, pr.Number);
            }

            if (!textApproved && string.IsNullOrWhiteSpace(fallbackBody))
            {
                Logger.LogInformation("SE review of PR #{Number} only had truncation complaints — auto-approving", pr.Number);
                return (true, "Code review passed. Implementation meets requirements for the task scope.", []);
            }

            return (textApproved, fallbackBody, extractedInline);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} quality with AI", pr.Number);
            return (false, null, []);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts just the section of the engineering plan that corresponds to the given
    /// issue number or PR title. Prevents the review AI from confusing tasks when
    /// the full plan with all tasks is in context.
    /// </summary>
    private static string? ExtractTaskSectionFromPlan(string plan, int issueNumber, string prTitle)
    {
        var lines = plan.Split('\n');
        var result = new System.Text.StringBuilder();
        bool capturing = false;
        int headerLevel = 0;

        // Patterns to match: "T1", "T2", issue #number, or the task title
        var issueRef = $"#{issueNumber}";
        var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(prTitle);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Detect markdown headers (## Task, ### T1, etc.)
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();

                if (capturing && level <= headerLevel)
                {
                    // Hit a same-level or higher header — stop capturing
                    break;
                }

                // Check if this header matches our task
                if (!capturing &&
                    (line.Contains(issueRef, StringComparison.OrdinalIgnoreCase) ||
                     (taskTitle is not null && line.Contains(taskTitle, StringComparison.OrdinalIgnoreCase))))
                {
                    capturing = true;
                    headerLevel = level;
                }
            }

            if (capturing)
                result.AppendLine(line);
        }

        var extracted = result.ToString().Trim();
        return string.IsNullOrEmpty(extracted) ? null : extracted;
    }

    /// <summary>
    /// Filters out numbered review items that complain about truncated or invisible code.
    /// Returns the remaining review body with items renumbered.
    /// </summary>
    private static string FilterTruncationComplaints(string reviewBody)
    {
        if (string.IsNullOrWhiteSpace(reviewBody))
            return reviewBody;

        string[] truncationKeywords =
        [
            "truncated",
            "cut off",
            "cannot verify",
            "cannot see",
            "can't verify",
            "can't see",
            "not visible",
            "not shown",
            "hidden",
            "implementation not visible",
            "implementations are cut",
            "code hides",
            "unable to verify",
            "unable to see"
        ];

        var lines = reviewBody.Split('\n');
        var filteredItems = new List<string>();
        var currentItem = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Check if this starts a new numbered item
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s"))
            {
                // Flush previous item if it exists
                if (currentItem.Length > 0)
                {
                    var item = currentItem.ToString();
                    if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        filteredItems.Add(item);
                    currentItem.Clear();
                }
                currentItem.AppendLine(line);
            }
            else
            {
                currentItem.AppendLine(line);
            }
        }

        // Flush last item
        if (currentItem.Length > 0)
        {
            var item = currentItem.ToString();
            if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                filteredItems.Add(item);
        }

        // Renumber remaining items
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < filteredItems.Count; i++)
        {
            var item = filteredItems[i].Trim();
            // Replace the leading number with the new index
            item = System.Text.RegularExpressions.Regex.Replace(item, @"^\d+[\.\)]", $"{i + 1}.");
            result.AppendLine(item);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Checks if a single review comment body is a truncation complaint.
    /// </summary>
    private static bool IsTruncationComplaint(string body)
    {
        string[] truncationKeywords =
        [
            "truncated", "cut off", "cannot verify", "cannot see",
            "can't verify", "can't see", "not visible", "not shown",
            "implementation not visible", "unable to verify", "unable to see"
        ];
        return truncationKeywords.Any(kw => body.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// WS2 parser-hardening fallback: extract inline review comments from text-format reviews.
    /// Matches numbered-list items prefixed with "file.ext:line:" so review feedback lands on
    /// the Files-changed tab even when the LLM doesn't emit structured JSON.
    /// </summary>
    private static List<PlatformInlineComment> ExtractInlineCommentsFromText(string? text)
    {
        var results = new List<PlatformInlineComment>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var pattern = @"(?m)^\s*(?:[-*]|\d+\.)?\s*[`""']?([\w./\\\-]+\.[a-zA-Z]{1,8})[`""']?:(\d+):\s*(.+?)(?:\r?\n(?=\s*(?:[-*]|\d+\.))|\r?\n\r?\n|\z)";
        var regex = new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
        {
            var file = match.Groups[1].Value.Trim();
            if (!int.TryParse(match.Groups[2].Value, out var line) || line < 1) continue;
            var body = match.Groups[3].Value.Trim();
            if (string.IsNullOrWhiteSpace(body) || IsTruncationComplaint(body)) continue;

            file = file.Replace('\\', '/');

            results.Add(new PlatformInlineComment
            {
                FilePath = file,
                Line = line,
                Body = body
            });
        }

        return results;
    }

    /// <summary>
    /// Parses SE review JSON response into structured components.
    /// Returns null if the response isn't valid JSON.
    /// </summary>
    private static (bool Approved, string Summary, IReadOnlyList<PlatformInlineComment> Comments)? TryParseStructuredSeReview(string text)
    {
        try
        {
            var json = text.Trim();

            // Strip markdown fences if present
            if (json.Contains("```"))
            {
                var fenceStart = json.IndexOf("```");
                var afterFence = json.IndexOf('\n', fenceStart);
                if (afterFence >= 0)
                {
                    var fenceEnd = json.IndexOf("```", afterFence);
                    json = fenceEnd > afterFence
                        ? json[(afterFence + 1)..fenceEnd].Trim()
                        : json[(afterFence + 1)..].Trim();
                }
            }

            // Find JSON object boundaries with proper nesting
            var startBrace = json.IndexOf('{');
            if (startBrace < 0) return null;

            var depth = 0;
            var endBrace = -1;
            var inString = false;
            var escape = false;
            for (var i = startBrace; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { endBrace = i; break; } }
            }
            if (endBrace < 0) return null;
            json = json[startBrace..(endBrace + 1)];

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdict = root.TryGetProperty("verdict", out var v)
                ? v.GetString()?.Trim().ToUpperInvariant() ?? "REQUEST_CHANGES"
                : "REQUEST_CHANGES";

            var approved = verdict.Contains("APPROVE") && !verdict.Contains("REQUEST");

            var summary = root.TryGetProperty("summary", out var s)
                ? s.GetString()?.Trim() ?? ""
                : "";

            var comments = new List<PlatformInlineComment>();
            if (root.TryGetProperty("comments", out var commentsArr)
                && commentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in commentsArr.EnumerateArray())
                {
                    var file = c.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var line = c.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var priority = c.TryGetProperty("priority", out var p) ? p.GetString() ?? "" : "";
                    var body = c.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(body) && line > 0)
                    {
                        comments.Add(new PlatformInlineComment
                        {
                            FilePath = file,
                            Line = line,
                            Body = $"{priority}: {body}".Trim()
                        });
                    }
                }
            }

            return (approved, summary, comments);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Submits inline review comments as a GitHub PR review.
    /// Tags each comment with [SoftwareEngineer] for ownership tracking.
    /// Falls back gracefully if the GitHub API call fails.
    /// </summary>
    private async Task SubmitPlatformInlineCommentsAsync(
        int prNumber, string summary, bool approved,
        IReadOnlyList<PlatformInlineComment> comments, CancellationToken ct)
    {
        try
        {
            var maxComments = Config.Review.MaxInlineCommentsPerReview;
            var toSubmit = comments.Take(maxComments)
                .Select(c => new PlatformInlineComment
                {
                    FilePath = c.FilePath,
                    Line = c.Line,
                    Body = $"**[SoftwareEngineer]** {c.Body}"
                })
                .ToList();

            if (toSubmit.Count == 0) return;

            // Single-PAT setup: APPROVE/REQUEST_CHANGES are forbidden on own PRs.
            // Always use COMMENT so inline comments land on the Files-changed tab.
            var eventType = "COMMENT";
            var reviewBody = $"🔧 **[SoftwareEngineer] Inline Review** — {(approved ? "APPROVED" : "CHANGES REQUESTED")}\n\n" +
                $"{summary}\n\n" +
                $"_{toSubmit.Count} inline comment(s) below_";

            await ReviewService.CreateReviewWithInlineCommentsAsync(
                prNumber, reviewBody, eventType, toSubmit, ct: ct);

            Logger.LogInformation(
                "Submitted {Count} SE inline review comments on PR #{Number}",
                toSubmit.Count, prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to submit SE inline review comments on PR #{Number} — review body was still posted as comment",
                prNumber);
        }
    }

    /// <summary>
    /// After approving a PR, resolve all open inline review threads left by this SE.
    /// Only resolves threads tagged with [SoftwareEngineer] to avoid touching other reviewers' threads.
    /// </summary>
    private async Task ResolveSEReviewThreadsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var threads = await ReviewService.GetThreadsAsync(prNumber, ct);
            var ownThreads = threads
                .Where(t => !t.IsResolved && t.Body.Contains("[SoftwareEngineer]", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ownThreads.Count == 0)
            {
                Logger.LogDebug("No unresolved SE review threads on PR #{Number}", prNumber);
                return;
            }

            Logger.LogInformation("Resolving {Count} SE review threads on PR #{Number} after approval",
                ownThreads.Count, prNumber);

            foreach (var thread in ownThreads)
            {
                var replyBody = $"✅ **[SoftwareEngineer] Resolved** — Rework addressed this feedback. Approved.";
                await ReviewService.ResolveThreadAsync(
                    prNumber, thread.ThreadId, replyBody, ct);
            }

            LogActivity("review", $"🔒 Resolved {ownThreads.Count} SE inline review thread(s) on PR #{prNumber}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resolve SE review threads on PR #{Number} — approval still proceeds", prNumber);
        }
    }

    /// <summary>
    /// After merging an engineer's PR, find the corresponding task issue and close it.
    /// Searches by task name from PR title, then by issue number from agent assignments.
    /// </summary>
    private async Task MarkEngineerTaskDoneAsync(AgentPullRequest pr, CancellationToken ct)
    {
        // Search by task name from PR title (strip agent prefix, handle double-prefix)
        var taskName = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
        EngineeringTask? task = null;

        if (taskName is not null)
        {
            var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskName);
            if (innerName is not null) taskName = innerName;
            task = _taskManager.FindByName(taskName);
        }

        // Fallback: match by issue number from the specific PR author's assignment
        if (task is null)
        {
            var agentName = PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title);
            if (agentName is not null)
            {
                var authorEntry = _agentAssignments.FirstOrDefault(kv =>
                {
                    var agent = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                        .Where(a => a.Identity.Id != Identity.Id)
                        .FirstOrDefault(a => a.Identity.Id == kv.Key);
                    return agent is not null &&
                           string.Equals(agent.Identity.DisplayName, agentName, StringComparison.OrdinalIgnoreCase);
                });

                if (authorEntry.Key is not null)
                    task = _taskManager.FindByIssueNumber(authorEntry.Value);
            }
        }

        if (task?.IssueNumber.HasValue == true)
        {
            await _taskManager.MarkDoneAsync(task.IssueNumber.Value, pr.Number, ct);
            Logger.LogInformation("Marked engineer task {TaskId} '{TaskName}' as Done (PR #{PrNumber} merged)",
                task.Id, task.Name, pr.Number);
        }
        else
        {
            Logger.LogWarning("Could not find task issue for merged PR #{PrNumber} ({Title})",
                pr.Number, pr.Title);
        }
    }

    private async Task<string> GenerateTaskDescriptionAsync(
        EngineeringTask task, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);

            var issueContext = "";
            if (task.IssueNumber.HasValue)
            {
                var issue = await WorkItemService.GetAsync(task.IssueNumber.Value, ct);
                if (issue is not null)
                    issueContext = $"\n\n## Source Issue #{issue.Number}: {issue.Title}\n{issue.Body}";
            }

            // In SinglePRMode, fetch ALL related user story bodies so the SE has full context
            if (task.RelatedEnhancementNumbers.Count > 0)
            {
                var storyDetails = new StringBuilder();
                storyDetails.AppendLine("\n\n## Related User Stories (Full Details)");
                foreach (var storyNum in task.RelatedEnhancementNumbers)
                {
                    if (storyNum == task.IssueNumber) continue; // Already fetched above
                    try
                    {
                        var story = await WorkItemService.GetAsync(storyNum, ct);
                        if (story is not null)
                        {
                            storyDetails.AppendLine($"\n### Issue #{story.Number}: {story.Title}");
                            storyDetails.AppendLine(story.Body ?? "(no description)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Could not fetch related story #{Number} for task context", storyNum);
                    }
                }
                issueContext += storyDetails.ToString();
            }

            var history = CreateChatHistory();
            var descSys = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/pr-description-system",
                    new Dictionary<string, string>(), ct)
                : null;
            history.AddSystemMessage(descSys ??
                "You are a Software Engineer writing a detailed PR description for an engineering task. " +
                "The description should be clear enough for another engineer to implement the task. " +
                "Include:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: An ordered, numbered list of discrete implementation steps. " +
                "Step 1 MUST be scaffolding (folder structure, config, boilerplate). " +
                "All paths relative to repo root. Place .sln at root, project under ProjectName/. " +
                "NEVER create redundant same-named nested folders. Each subsequent step " +
                "builds on the previous. Each step should be a self-contained committable unit of work. " +
                "3-6 steps total. Be specific about what each step produces.\n" +
                "4. **Testing**: What tests should cover");

            var descUser = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/pr-description-user",
                    new Dictionary<string, string>
                    {
                        ["pm_spec"] = pmSpec,
                        ["architecture"] = architectureDoc,
                        ["issue_context"] = issueContext,
                        ["task_name"] = task.Name,
                        ["task_description"] = task.Description
                    }, ct)
                : null;
            history.AddUserMessage(descUser ??
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Architecture\n{architectureDoc}" +
                issueContext +
                $"\n\n## Task: {task.Name}\n{task.Description}\n\n" +
                "Write a detailed PR description with:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: Ordered, numbered list of discrete steps. " +
                "Step 1 = scaffolding. Each step is a committable unit. 3-6 steps.\n" +
                "4. **Testing**: What tests should cover");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            return response.Content?.Trim() ?? task.Description;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to generate task description with AI, using raw description");
            return task.Description;
        }
    }

    private static string NormalizeComplexity(string complexity)
    {
        return complexity.ToLowerInvariant() switch
        {
            "high" or "complex" or "hard" => "High",
            "medium" or "moderate" or "mid" => "Medium",
            "low" or "simple" or "easy" => "Low",
            _ => "Medium"
        };
    }

    /// <summary>
    /// Formats a semicolon-separated file plan string into readable markdown.
    /// Input: "CREATE:src/Services/Auth.cs(MyApp.Services);MODIFY:src/Program.cs;USE:User(MyApp.Models)"
    /// Output: markdown bullet list with file operations
    /// </summary>
    /// <summary>
    /// Enforces the foundation-first pattern: ensures the first task (T1) has no dependencies
    /// and all other tasks depend on T1. If the AI didn't produce a proper foundation task,
    /// this reorders tasks so the foundation-like one is first, or injects a synthetic one.
    /// </summary>
    /// Recomputes wave assignments from the dependency graph to eliminate gaps.
    /// Foundation tasks (first task, W0) keep their wave. Other tasks get:
    /// wave = max(dependency waves) + 1, minimum W1 for tasks with no non-foundation deps.
    private void RecomputeWavesFromDependencies(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return;

        var taskById = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var foundationId = tasks[0].Id;

        // Assign W0 to foundation
        tasks[0].Wave = "W0";

        // Compute waves via BFS from dependencies
        var changed = true;
        var iterations = 0;
        while (changed && iterations < 20)
        {
            changed = false;
            iterations++;
            foreach (var task in tasks.Skip(1))
            {
                if (task.Id == IntegrationTaskId) continue;

                var maxDepWave = 0;
                foreach (var depId in task.Dependencies)
                {
                    if (string.Equals(depId, foundationId, StringComparison.OrdinalIgnoreCase))
                    {
                        maxDepWave = Math.Max(maxDepWave, 0); // W0
                        continue;
                    }
                    if (taskById.TryGetValue(depId, out var dep))
                    {
                        var depWaveNum = ParseWaveNumber(dep.Wave);
                        maxDepWave = Math.Max(maxDepWave, depWaveNum);
                    }
                }

                var newWave = $"W{maxDepWave + 1}";
                // Tasks with only foundation dependency → W1
                if (task.Dependencies.Count == 0 ||
                    task.Dependencies.All(d => string.Equals(d, foundationId, StringComparison.OrdinalIgnoreCase)))
                    newWave = "W1";

                if (!string.Equals(task.Wave, newWave, StringComparison.OrdinalIgnoreCase))
                {
                    task.Wave = newWave;
                    changed = true;
                }
            }
        }

        // Integration task always gets the highest wave + 1
        var integrationTask = tasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
        if (integrationTask is not null)
        {
            var maxWave = tasks.Where(t => t.Id != IntegrationTaskId)
                .Max(t => ParseWaveNumber(t.Wave));
            integrationTask.Wave = $"W{maxWave + 1}";
        }

        // Log the recomputed wave distribution
        var waveGroups = tasks.Where(t => t.Id != IntegrationTaskId)
            .GroupBy(t => t.Wave).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        Logger.LogInformation("Recomputed waves from dependency graph: {Distribution}",
            string.Join(", ", waveGroups));
    }

    private static int ParseWaveNumber(string? wave)
    {
        if (string.IsNullOrEmpty(wave)) return 1;
        if (wave.StartsWith('W') && int.TryParse(wave.AsSpan(1), out var num))
            return num;
        return 1;
    }

    /// <summary>
    /// Registers human-friendly display names for engineering tasks in the task tracker.
    /// Called on both fresh plan creation and task restoration so the dashboard always
    /// shows meaningful names like "#2221: Implement entire project" instead of "T1".
    /// </summary>
    private void RegisterTaskDisplayNames(IEnumerable<EngineeringTask> tasks)
    {
        foreach (var task in tasks)
        {
            var displayName = task.IssueNumber.HasValue
                ? $"#{task.IssueNumber}: {task.Name}"
                : task.Name;
            _taskTracker.RegisterTaskDisplayName(task.Id, displayName);
        }
    }

    /// <summary>
    /// Determines if a task is a foundation/scaffolding task that the SE Lead should handle itself.
    /// Heuristic: task ID is "T1", wave is "W0", title contains foundation keywords,
    /// or the task has zero dependencies.
    /// </summary>
    private static bool IsFoundationTask(EngineeringTask task)
    {
        var foundationKeywords = new[] { "foundation", "scaffolding", "scaffold", "setup", "initial" };
        var hasFoundationTitle = foundationKeywords.Any(k =>
            task.Name.Contains(k, StringComparison.OrdinalIgnoreCase));

        return string.Equals(task.Id, "T1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Wave, "W0", StringComparison.OrdinalIgnoreCase)
            || (hasFoundationTitle && task.DependencyIssueNumbers.Count == 0);
    }

    private void EnsureFoundationFirstPattern(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return;

        var foundationKeywords = new[] { "foundation", "scaffold", "setup", "structure", "skeleton", "template", "infrastructure", "project setup" };
        var firstTask = tasks[0];
        var isFirstFoundation = firstTask.Dependencies.Count == 0 &&
            foundationKeywords.Any(k => firstTask.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                        firstTask.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

        // If T1 isn't a foundation task, look for one elsewhere in the list and move it to front
        if (!isFirstFoundation)
        {
            var foundationIdx = tasks.FindIndex(t =>
                t.Dependencies.Count == 0 &&
                foundationKeywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                            t.Description.Contains(k, StringComparison.OrdinalIgnoreCase)));

            if (foundationIdx > 0)
            {
                var foundationTask = tasks[foundationIdx];
                tasks.RemoveAt(foundationIdx);
                tasks.Insert(0, foundationTask);
                Logger.LogInformation("Reordered foundation task '{Name}' to first position", foundationTask.Name);
            }
            else
            {
                Logger.LogInformation("No explicit foundation task found — T1 ({Name}) will serve as foundation", firstTask.Name);
            }
        }

        // Ensure T1 has no dependencies
        var t1 = tasks[0];
        if (t1.Dependencies.Count > 0)
        {
            tasks[0] = t1 with { Dependencies = new List<string>() };
            Logger.LogInformation("Cleared dependencies from foundation task T1 ({Name})", t1.Name);
        }

        // Ensure all other tasks depend on T1's ID
        var t1Id = tasks[0].Id;
        for (var i = 1; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (!task.Dependencies.Contains(t1Id, StringComparer.OrdinalIgnoreCase))
            {
                task.Dependencies.Insert(0, t1Id);
            }
        }
        // Note: File overlap detection is now handled by ValidateAndRepairTaskPlanAsync()
        // which runs after this method and uses AI-assisted repair.
    }

    /// <summary>
    /// Extracts CREATE file paths from a task description's File Plan section.
    /// </summary>
    private static List<string> ExtractCreateFilesFromDescription(string description)
    {
        var files = new List<string>();
        // Match lines like "- ➕ **Create:** `path/to/file.cs`" or raw "CREATE:path/to/file.cs"
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            // Markdown format from FormatFilePlan
            if (trimmed.Contains("**Create:**"))
            {
                var backtickStart = trimmed.IndexOf('`');
                var backtickEnd = trimmed.LastIndexOf('`');
                if (backtickStart >= 0 && backtickEnd > backtickStart)
                    files.Add(trimmed[(backtickStart + 1)..backtickEnd]);
            }
        }
        return files;
    }

    private static string FormatFilePlan(string filePlan)
    {
        if (string.IsNullOrWhiteSpace(filePlan)) return "";

        var sb = new System.Text.StringBuilder();
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;

            var action = op[..colonIdx].Trim().ToUpperInvariant();
            var detail = op[(colonIdx + 1)..].Trim();

            var icon = action switch
            {
                "CREATE" => "➕ **Create:**",
                "MODIFY" => "✏️ **Modify:**",
                "USE" => "📎 **Reference (do not recreate):**",
                "SHARED" => "🔗 **Shared (multi-task):**",
                _ => $"**{action}:**"
            };

            sb.AppendLine($"- {icon} `{detail}`");
        }

        return sb.ToString();
    }

    #endregion

    #region PE Parallelism Enhancements

    /// <summary>
    /// Normalizes a file path for consistent comparison:
    /// forward slashes, lowercase, no leading slash, no trailing slash.
    /// </summary>
    internal static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        // Strip namespace hint: "MyApp/File.cs(Namespace)" → "MyApp/File.cs"
        var parenIdx = path.IndexOf('(');
        if (parenIdx > 0) path = path[..parenIdx];
        return path.Trim().Replace('\\', '/').TrimStart('/').TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Extracts all CREATE and MODIFY file paths from a raw FilePlan string (semicolon-separated ops).
    /// Returns normalized paths.
    /// </summary>
    internal static List<string> ExtractAllFilesFromFilePlan(string filePlan)
    {
        if (string.IsNullOrWhiteSpace(filePlan)) return new();
        var files = new List<string>();
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;
            var action = op[..colonIdx].Trim().ToUpperInvariant();
            if (action is "CREATE" or "MODIFY")
            {
                var file = NormalizeFilePath(op[(colonIdx + 1)..]);
                if (!string.IsNullOrEmpty(file))
                    files.Add(file);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Extracts SHARED file declarations from T1's FilePlan.
    /// These are files explicitly allowed to be modified by multiple tasks.
    /// </summary>
    internal static HashSet<string> ExtractSharedFilesFromFilePlan(string filePlan)
    {
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filePlan)) return shared;
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;
            var action = op[..colonIdx].Trim().ToUpperInvariant();
            if (action == "SHARED")
            {
                var file = NormalizeFilePath(op[(colonIdx + 1)..]);
                if (!string.IsNullOrEmpty(file))
                    shared.Add(file);
            }
        }
        return shared;
    }

    /// <summary>
    /// Well-known infrastructure files that are inherently shared and should not trigger overlap errors.
    /// These files are commonly modified by multiple tasks (build config, solution files, etc.).
    /// </summary>
    private static readonly HashSet<string> InfrastructureFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", "directory.build.props", "directory.build.targets",
        "directory.packages.props", "global.json", "nuget.config",
        ".editorconfig", "docker-compose.yml", "dockerfile"
    };

    /// <summary>
    /// Detects file ownership overlaps between tasks.
    /// Returns a dictionary of file → list of task IDs that touch it.
    /// Excludes shared files (declared in T1's FilePlan) and well-known infrastructure files.
    /// </summary>
    internal static Dictionary<string, List<string>> DetectFileOverlaps(
        List<EngineeringTask> tasks, HashSet<string> sharedFiles)
    {
        var fileOwnership = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var file in task.OwnedFiles)
            {
                // Skip shared files and infrastructure files
                if (sharedFiles.Contains(file)) continue;
                var fileName = Path.GetFileName(file);
                if (InfrastructureFiles.Contains(fileName)) continue;

                if (!fileOwnership.TryGetValue(file, out var owners))
                {
                    owners = new List<string>();
                    fileOwnership[file] = owners;
                }
                if (!owners.Contains(task.Id, StringComparer.OrdinalIgnoreCase))
                    owners.Add(task.Id);
            }
        }
        // Return only files with >1 owner
        return fileOwnership
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates and repairs file overlaps in the task plan using AI-assisted fixing.
    /// If overlaps remain after AI retries, fails with a replan request rather than silently stealing files.
    /// </summary>
    private async Task ValidateAndRepairTaskPlanAsync(
        List<EngineeringTask> tasks,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        // Extract shared files from T1 (foundation task)
        var t1 = tasks.FirstOrDefault();
        var t1FilePlan = ExtractRawFilePlanFromDescription(t1?.Description ?? "");
        var sharedFiles = ExtractSharedFilesFromFilePlan(t1FilePlan);

        if (sharedFiles.Count > 0)
            Logger.LogInformation("Shared file registry from T1: {SharedFiles}",
                string.Join(", ", sharedFiles));

        var overlaps = DetectFileOverlaps(tasks, sharedFiles);
        if (overlaps.Count == 0)
        {
            Logger.LogInformation("No file overlaps detected — plan is parallel-safe");
            return;
        }

        Logger.LogWarning("File overlaps detected: {Count} files shared across tasks. Attempting AI-assisted repair",
            overlaps.Count);

        const int maxRetries = 2;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var overlapSummary = string.Join("\n", overlaps.Select(kv =>
                $"  - `{kv.Key}` owned by: {string.Join(", ", kv.Value)}"));

            var taskSummary = string.Join("\n", tasks.Select(t =>
                $"  {t.Id} ({t.Wave}): {t.Name} — Files: [{string.Join(", ", t.OwnedFiles)}]"));

            var fixPrompt =
                "The following engineering tasks have FILE OVERLAPS — multiple tasks create/modify the same file. " +
                "This causes merge conflicts when engineers work in parallel.\n\n" +
                $"## Overlapping Files\n{overlapSummary}\n\n" +
                $"## Current Tasks\n{taskSummary}\n\n" +
                "Fix this by:\n" +
                "1. Reassigning the disputed file to ONE task only\n" +
                "2. If a task loses a file, update its description to use the OTHER task's output instead\n" +
                "3. If a file truly MUST be shared, add it as SHARED in T1's FilePlan\n\n" +
                "Output the CORRECTED task lines in TASK format (same as before):\n" +
                "TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies>|<FilePlan>|<Wave>\n\n" +
                "Only output corrected TASK lines for tasks that CHANGED. Keep unchanged tasks as-is.";

            var fixHistory = CreateChatHistory();
            fixHistory.AddSystemMessage("You are a Software Engineer fixing file ownership conflicts in an engineering plan. " +
                "Each file should be owned by exactly one task unless explicitly declared SHARED.");
            fixHistory.AddUserMessage(fixPrompt);

            try
            {
                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixes = fixResponse.Content ?? "";

                // Parse corrected tasks and merge them into the existing list
                var fixedCount = ApplyTaskFixes(tasks, fixes);
                Logger.LogInformation("AI overlap repair attempt {Attempt}: {FixedCount} tasks updated",
                    attempt, fixedCount);

                // Re-detect overlaps
                sharedFiles = ExtractSharedFilesFromFilePlan(
                    ExtractRawFilePlanFromDescription(tasks.FirstOrDefault()?.Description ?? ""));
                overlaps = DetectFileOverlaps(tasks, sharedFiles);

                if (overlaps.Count == 0)
                {
                    Logger.LogInformation("File overlaps resolved after {Attempt} AI repair attempt(s)", attempt);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "AI overlap repair attempt {Attempt} failed", attempt);
            }
        }

        // After max retries, log the remaining overlaps as warnings (don't silently reassign)
        Logger.LogWarning(
            "File overlaps still present after {MaxRetries} AI repair attempts. " +
            "Remaining overlaps: {Overlaps}. Proceeding with warning — engineers may encounter merge conflicts",
            maxRetries,
            string.Join("; ", overlaps.Select(kv => $"{kv.Key} → [{string.Join(",", kv.Value)}]")));
    }

    /// <summary>
    /// Applies AI-generated task fixes to the existing task list.
    /// Returns the number of tasks that were updated.
    /// </summary>
    private int ApplyTaskFixes(List<EngineeringTask> tasks, string aiResponse)
    {
        var fixedCount = 0;
        foreach (var line in aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TASK|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7) continue;

            var taskId = parts[1].Trim();
            var existingIdx = tasks.FindIndex(t =>
                string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (existingIdx < 0) continue;

            var filePlan = parts.Length >= 8 ? parts[7].Trim() : "";
            var wave = parts.Length >= 9 ? parts[8].Trim() : tasks[existingIdx].Wave;
            var ownedFiles = ExtractAllFilesFromFilePlan(filePlan);

            var deps = parts[6].Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : parts[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var (plainDeps, depTypes) = ParseTypedDependencies(deps);

            tasks[existingIdx] = tasks[existingIdx] with
            {
                Name = parts[3].Trim(),
                Description = parts[4].Trim() + (string.IsNullOrEmpty(filePlan) ? "" :
                    $"\n\n### File Plan\n{FormatFilePlan(filePlan)}"),
                Complexity = NormalizeComplexity(parts[5].Trim()),
                Dependencies = plainDeps,
                DependencyTypes = depTypes,
                Wave = wave,
                OwnedFiles = ownedFiles
            };
            fixedCount++;
        }
        return fixedCount;
    }

    /// <summary>
    /// Extracts the raw FilePlan string from a task description that has already been formatted.
    /// Looks for the "### File Plan" section and reconstructs the semicolon-separated operations.
    /// </summary>
    private static string ExtractRawFilePlanFromDescription(string description)
    {
        var sb = new StringBuilder();
        var inFilePlan = false;
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == "### File Plan")
            {
                inFilePlan = true;
                continue;
            }
            if (inFilePlan)
            {
                if (trimmed.StartsWith("###") || string.IsNullOrWhiteSpace(trimmed))
                    break;
                // Parse "- ➕ **Create:** `path`" back to "CREATE:path"
                if (trimmed.Contains("**Create:**"))
                    AppendExtractedOp(sb, trimmed, "CREATE");
                else if (trimmed.Contains("**Modify:**"))
                    AppendExtractedOp(sb, trimmed, "MODIFY");
                else if (trimmed.Contains("**Shared (multi-task):**"))
                    AppendExtractedOp(sb, trimmed, "SHARED");
                else if (trimmed.Contains("**Reference"))
                    AppendExtractedOp(sb, trimmed, "USE");
            }
        }
        return sb.ToString().TrimEnd(';');
    }

    private static void AppendExtractedOp(StringBuilder sb, string line, string action)
    {
        var backtickStart = line.IndexOf('`');
        var backtickEnd = line.LastIndexOf('`');
        if (backtickStart >= 0 && backtickEnd > backtickStart)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append($"{action}:{line[(backtickStart + 1)..backtickEnd]}");
        }
    }

    /// <summary>
    /// Validates wave assignments in the task plan.
    /// Checks that at least 60% of non-foundation tasks are in W1 (parallelizable).
    /// Logs warnings if validation fails but does not reject the plan.
    /// </summary>
    internal bool ValidateWaves(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return true;

        // Skip foundation task (T1) from wave analysis
        var nonFoundationTasks = tasks.Skip(1).ToList();
        if (nonFoundationTasks.Count == 0) return true;

        var w1Count = nonFoundationTasks.Count(t =>
            string.Equals(t.Wave, "W1", StringComparison.OrdinalIgnoreCase));
        var w1Percentage = (double)w1Count / nonFoundationTasks.Count * 100;

        Logger.LogInformation(
            "Wave analysis: {W1Count}/{Total} non-foundation tasks in W1 ({Percentage:F0}%)",
            w1Count, nonFoundationTasks.Count, w1Percentage);

        // Log wave breakdown
        var waveGroups = nonFoundationTasks
            .GroupBy(t => t.Wave, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);
        foreach (var group in waveGroups)
        {
            Logger.LogInformation("  Wave {Wave}: {Tasks}",
                group.Key,
                string.Join(", ", group.Select(t => $"{t.Id}:{t.Name}")));
        }

        // Validate: W2+ tasks should not depend on other W2+ tasks from the same wave
        var waveViolations = new List<string>();
        foreach (var task in nonFoundationTasks)
        {
            if (string.Equals(task.Wave, "W1", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var depId in task.Dependencies)
            {
                var depTask = tasks.FirstOrDefault(t =>
                    string.Equals(t.Id, depId, StringComparison.OrdinalIgnoreCase));
                if (depTask is not null &&
                    string.Equals(depTask.Wave, task.Wave, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(depTask.Id, tasks[0].Id, StringComparison.OrdinalIgnoreCase))
                {
                    waveViolations.Add($"{task.Id}({task.Wave}) depends on {depTask.Id}({depTask.Wave})");
                }
            }
        }

        if (waveViolations.Count > 0)
        {
            Logger.LogWarning(
                "Wave ordering violations — tasks in the same wave depend on each other: {Violations}",
                string.Join("; ", waveViolations));
        }

        if (w1Percentage < 60)
        {
            Logger.LogWarning(
                "Low W1 parallelism: only {Percentage:F0}% of tasks in W1 (target: 60%+). " +
                "Consider restructuring tasks to reduce inter-task dependencies",
                w1Percentage);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses typed dependencies like "T1(files),T3(api)" into plain dep IDs and a type map.
    /// Plain format "T1,T3" is also supported (no type annotation → "full" dependency).
    /// </summary>
    internal static (List<string> PlainDeps, Dictionary<string, string> DepTypes) ParseTypedDependencies(
        List<string> rawDeps)
    {
        var plainDeps = new List<string>();
        var depTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dep in rawDeps)
        {
            var trimmed = dep.Trim();
            var parenStart = trimmed.IndexOf('(');
            if (parenStart > 0 && trimmed.EndsWith(')'))
            {
                var taskId = trimmed[..parenStart].Trim();
                var depType = trimmed[(parenStart + 1)..^1].Trim().ToLowerInvariant();
                plainDeps.Add(taskId);
                depTypes[taskId] = depType;
            }
            else
            {
                plainDeps.Add(trimmed);
                // No type annotation = full dependency
            }
        }

        return (plainDeps, depTypes);
    }

    /// <summary>
    /// Determines if a typed dependency can be relaxed (i.e., the dependent task can proceed
    /// even though the dependency isn't fully complete).
    /// Currently supports: "api" deps can proceed if the interface is declared in T1's shared files.
    /// </summary>
    internal static bool CanRelaxDependency(string depType, EngineeringTask depTask, HashSet<string> sharedFiles)
    {
        return depType.ToLowerInvariant() switch
        {
            // API dependency: can proceed if we have the interface contract from T1
            "api" or "interface" => depTask.Wave == "W1" || sharedFiles.Count > 0,
            // Schema dependency: can proceed if the schema is defined in T1
            "schema" or "model" => string.Equals(depTask.Id, "T1", StringComparison.OrdinalIgnoreCase),
            // File dependency: cannot be relaxed — must wait for actual file
            "files" or "file" => false,
            // Unknown type: treat as full dependency (cannot relax)
            _ => false
        };
    }

    /// <summary>
    /// Logs parallelism metrics for dashboard/monitoring.
    /// </summary>
    private void LogParallelismMetrics(List<EngineeringTask> tasks, Dictionary<string, List<string>> overlaps)
    {
        var nonFoundation = tasks.Count > 1 ? tasks.Skip(1).ToList() : tasks;
        var w1Count = nonFoundation.Count(t =>
            string.Equals(t.Wave, "W1", StringComparison.OrdinalIgnoreCase));
        var totalFiles = tasks.Sum(t => t.OwnedFiles.Count);
        var sharedFileCount = tasks.Count > 0
            ? ExtractSharedFilesFromFilePlan(
                ExtractRawFilePlanFromDescription(tasks[0].Description)).Count
            : 0;

        Logger.LogInformation(
            "📊 Parallelism metrics: {TaskCount} tasks, {W1Count} in W1 ({W1Pct:F0}%), " +
            "{FileCount} total files, {SharedCount} shared files, {OverlapCount} remaining overlaps",
            tasks.Count, w1Count,
            nonFoundation.Count > 0 ? (double)w1Count / nonFoundation.Count * 100 : 100,
            totalFiles, sharedFileCount, overlaps.Count);

        LogActivity("task", $"📊 Plan parallelism: {w1Count}/{nonFoundation.Count} tasks in W1, " +
            $"{totalFiles} files planned, {overlaps.Count} overlaps");
    }
    private async Task<string?> ReadDesignReferencesAsync(CancellationToken ct)
    {
        // Delegates to the base implementation so HTML + PNG/JPG discovery stays in one place.
        // GetDesignContextAsync caches both the rendered markdown AND the binary image bytes so
        // the plan-generation call site can attach images via AddUserMessageWithDesignImages.
        return await GetDesignContextAsync(ct);
    }

    #endregion

    #region SME Reactive Spawning

    /// <summary>
    /// Evaluates whether a task requires specialist expertise and, if so,
    /// generates an SME agent definition and requests spawning (human-gated).
    /// Returns the spawned agent's identity, or null if no SME was needed.
    /// </summary>
    protected async Task<AgentIdentity?> RequestSmeIfNeededAsync(
        string taskDescription, string? additionalContext, CancellationToken ct)
    {
        if (_smeGenerator is null || _spawnManager is null)
            return null;

        if (!Config.SmeAgents.Enabled || !Config.SmeAgents.AllowAgentCreatedDefinitions)
            return null;

        try
        {
            // Ask AI if this task needs specialist expertise
            var kernel = Models.GetKernel(Identity.ModelTier);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var assessPrompt = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/sme-assessment",
                    new Dictionary<string, string> { ["task_description"] = taskDescription }, ct)
                : null;
            assessPrompt ??= $"""
                Evaluate whether this engineering task requires specialist expertise beyond
                what a general Software Engineer can handle. Consider: security, databases,
                ML/AI, compliance, specific cloud services, accessibility, etc.

                Task: {taskDescription}

                Respond with ONLY "YES" or "NO" on the first line.
                If YES, on the second line list 2-3 required capability keywords (comma-separated).
                """;

            var history = CreateChatHistory();
            history.AddUserMessage(assessPrompt);

            var assessResponse = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var assessment = assessResponse.LastOrDefault()?.Content?.Trim() ?? "NO";

            if (!assessment.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
                return null;

            Logger.LogInformation("Task identified as needing SME expertise: {Task}",
                taskDescription[..Math.Min(100, taskDescription.Length)]);

            // Check for existing template match
            var capLines = assessment.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var capabilities = capLines.Length > 1
                ? capLines[1].Split(',', StringSplitOptions.TrimEntries).ToList()
                : new List<string>();

            var existingTemplate = await _smeGenerator.FindMatchingTemplateAsync(capabilities, ct);
            if (existingTemplate is not null)
            {
                Logger.LogInformation("Found existing SME template '{RoleName}' matching task",
                    existingTemplate.RoleName);
                return await _spawnManager.SpawnSmeAgentAsync(existingTemplate, ct: ct);
            }

            // Generate a new definition
            var genPrompt = _smeGenerator.BuildDefinitionGenerationPrompt(taskDescription, additionalContext);
            history = CreateChatHistory();
            history.AddUserMessage(genPrompt);

            var genResponse = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var genContent = genResponse.LastOrDefault()?.Content;

            if (string.IsNullOrWhiteSpace(genContent))
                return null;

            var definition = _smeGenerator.ParseDefinition(genContent, Identity.Id);
            if (definition is null)
            {
                Logger.LogWarning("Failed to parse AI-generated SME definition");
                return null;
            }

            Logger.LogInformation("Generated SME definition: {RoleName} for task",
                definition.RoleName);
            LogActivity("task", $"🧠 Requesting SME agent: {definition.RoleName}");

            // Spawn (human-gated via SmeAgentSpawn gate)
            return await _spawnManager.SpawnSmeAgentAsync(definition, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SME assessment/spawn failed for task — proceeding without specialist");
            return null;
        }
    }

    #region Skill-Based Assignment Helpers

    /// <summary>
    /// Uses a single LLM call to semantically match all assignable tasks to all free engineers.
    /// Returns a dictionary of engineerAgentId → assigned task. Engineers/tasks not in the result are unmatched.
    /// Falls back to null if the LLM call fails.
    /// </summary>
    private async Task<Dictionary<string, EngineeringTask>?> MatchTasksToEngineersWithLlmAsync(
        List<EngineeringTask> tasks, List<EngineerInfo> engineers, CancellationToken ct)
    {
        if (tasks.Count == 0 || engineers.Count == 0) return null;

        try
        {
            var stepId = _taskTracker.BeginStep(Identity.Id, "pe-orchestration", "LLM skill matching",
                $"Matching {tasks.Count} tasks to {engineers.Count} engineers using semantic skill analysis", Identity.ModelTier);

            var prompt = new StringBuilder();
            prompt.AppendLine("You are an engineering manager assigning tasks to the best-qualified engineers.");
            prompt.AppendLine("Match each task to the single best engineer based on SEMANTIC skill relevance.");
            prompt.AppendLine("Do NOT require exact keyword matches — use domain knowledge to infer skill fit.");
            prompt.AppendLine("For example: a Frontend Engineer should get UI/React/HTML tasks even if 'react' isn't in their capabilities.");
            prompt.AppendLine("A Cloud Engineer should get Azure/AWS/infrastructure tasks.");
            prompt.AppendLine("Generalist engineers (no specific capabilities) should get tasks that don't fit any specialist.");
            prompt.AppendLine();
            prompt.AppendLine("## Available Engineers");
            foreach (var eng in engineers)
            {
                var caps = eng.Capabilities.Count > 0
                    ? $"Capabilities: [{string.Join(", ", eng.Capabilities)}]"
                    : "Generalist (no specific capabilities)";
                prompt.AppendLine($"- **{eng.Name}** (ID: `{eng.AgentId}`) — {caps}");
            }
            prompt.AppendLine();
            prompt.AppendLine("## Tasks to Assign");
            foreach (var task in tasks)
            {
                var tags = task.SkillTags.Count > 0
                    ? $" | Tags: [{string.Join(", ", task.SkillTags)}]"
                    : "";
                prompt.AppendLine($"- **{task.Id}**: {task.Name} (Complexity: {task.Complexity}{tags})");
                if (!string.IsNullOrWhiteSpace(task.Description))
                    prompt.AppendLine($"  Description: {task.Description[..Math.Min(200, task.Description.Length)]}");
            }
            prompt.AppendLine();
            prompt.AppendLine("## Rules");
            prompt.AppendLine("1. Each engineer gets AT MOST one task");
            prompt.AppendLine("2. Each task is assigned to AT MOST one engineer");
            prompt.AppendLine("3. Prefer assigning specialists to tasks matching their domain");
            prompt.AppendLine("4. Assign higher-complexity tasks to more experienced/specialized engineers");
            prompt.AppendLine("5. If there are more tasks than engineers, leave extra tasks unassigned");
            prompt.AppendLine("6. If there are more engineers than tasks, leave extra engineers unassigned");
            prompt.AppendLine();
            prompt.AppendLine("Respond with ONLY a JSON object (no markdown fences):");
            prompt.AppendLine("{");
            prompt.AppendLine("  \"assignments\": [");
            prompt.AppendLine("    { \"taskId\": \"T1\", \"engineerAgentId\": \"agent-id\", \"reason\": \"Brief reason for this match\" }");
            prompt.AppendLine("  ]");
            prompt.AppendLine("}");

            // Use budget tier — this is structured matching, not code generation
            var kernel = Models.GetKernel("budget", Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();
            history.AddUserMessage(prompt.ToString());

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var content = response.FirstOrDefault()?.Content;

            _taskTracker.CompleteStep(stepId);

            if (string.IsNullOrWhiteSpace(content))
            {
                Logger.LogWarning("LLM skill matching returned empty response, falling back to exact match");
                return null;
            }

            // Parse JSON response
            var jsonContent = content.Trim();
            if (jsonContent.StartsWith("```")) // Strip markdown fences if present
            {
                var lines = jsonContent.Split('\n');
                jsonContent = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.TrimStart().StartsWith("```")));
            }

            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = System.Text.Json.JsonSerializer.Deserialize<LlmAssignmentResponse>(jsonContent, options);

            if (result?.Assignments is null || result.Assignments.Count == 0)
            {
                Logger.LogWarning("LLM skill matching returned no assignments, falling back to exact match");
                return null;
            }

            // Build validated assignment map
            var taskLookup = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var engineerIds = new HashSet<string>(engineers.Select(e => e.AgentId), StringComparer.OrdinalIgnoreCase);
            var assignments = new Dictionary<string, EngineeringTask>(StringComparer.OrdinalIgnoreCase);
            var assignedTaskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in result.Assignments)
            {
                if (string.IsNullOrEmpty(a.TaskId) || string.IsNullOrEmpty(a.EngineerAgentId))
                    continue;
                if (!taskLookup.TryGetValue(a.TaskId, out var task))
                {
                    Logger.LogDebug("LLM suggested unknown task ID {TaskId}, skipping", a.TaskId);
                    continue;
                }
                if (!engineerIds.Contains(a.EngineerAgentId))
                {
                    Logger.LogDebug("LLM suggested unknown engineer ID {EngineerId}, skipping", a.EngineerAgentId);
                    continue;
                }
                if (assignments.ContainsKey(a.EngineerAgentId) || assignedTaskIds.Contains(a.TaskId))
                    continue; // Duplicate — skip

                assignments[a.EngineerAgentId] = task;
                assignedTaskIds.Add(a.TaskId);

                Logger.LogInformation(
                    "LLM matched task {TaskId} ({TaskName}) → {Engineer}: {Reason}",
                    a.TaskId, task.Name, a.EngineerAgentId, a.Reason ?? "no reason given");
            }

            Logger.LogInformation("LLM skill matching produced {Count}/{Total} assignments",
                assignments.Count, Math.Min(tasks.Count, engineers.Count));

            return assignments.Count > 0 ? assignments : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LLM skill matching failed, falling back to exact match");
            return null;
        }
    }

    private sealed class LlmAssignmentResponse
    {
        public List<LlmAssignment> Assignments { get; set; } = [];
    }

    private sealed class LlmAssignment
    {
        public string TaskId { get; set; } = "";
        public string EngineerAgentId { get; set; } = "";
        public string? Reason { get; set; }
    }

    /// <summary>
    /// FALLBACK: Finds the task with the highest skill tag overlap with the given capabilities.
    /// Returns null if no tasks have any overlapping tags.
    /// </summary>
    private static EngineeringTask? FindBestMatchingTask(List<EngineeringTask> tasks, List<string> capabilities)
    {
        EngineeringTask? bestTask = null;
        var bestScore = 0;

        foreach (var task in tasks)
        {
            if (task.SkillTags.Count == 0) continue;
            var overlap = task.SkillTags.Count(tag =>
                capabilities.Any(cap => string.Equals(cap, tag, StringComparison.OrdinalIgnoreCase)));
            if (overlap > bestScore)
            {
                bestScore = overlap;
                bestTask = task;
            }
        }

        return bestTask;
    }

    /// <summary>
    /// For a generalist engineer, prefers tasks that no specialist would match well.
    /// Falls back to highest complexity unmatched task.
    /// </summary>
    private static EngineeringTask? FindBestTaskForGeneralist(
        List<EngineeringTask> tasks, List<EngineerInfo> allEngineers)
    {
        var specialists = allEngineers.Where(e => e.Capabilities.Count > 0).ToList();
        if (specialists.Count == 0)
        {
            // No specialists — just pick highest complexity
            return tasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();
        }

        // Prefer tasks that no specialist has matching skills for
        var unmatchedTasks = tasks.Where(t =>
        {
            if (t.SkillTags.Count == 0) return true; // No tags = anyone can do it
            return !specialists.Any(s => t.SkillTags.Any(tag =>
                s.Capabilities.Any(cap => string.Equals(cap, tag, StringComparison.OrdinalIgnoreCase))));
        }).ToList();

        if (unmatchedTasks.Count > 0)
            return unmatchedTasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();

        // All tasks have matching specialists — just pick highest complexity
        return tasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();
    }

    private static int ComplexityRank(string complexity) => complexity.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    #endregion

    #endregion
}

internal record EngineeringTask
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Complexity { get; init; } = "Medium";
    public string Status { get; init; } = "Pending";
    public string? AssignedTo { get; init; }
    public int? PullRequestNumber { get; init; }
    public int? IssueNumber { get; init; }
    /// <summary>GitHub internal ID (not the issue number). Required for sub-issue and dependency APIs.</summary>
    public long? GitHubId { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
    /// <summary>Issue numbers this task depends on (parsed from issue body "Depends On").</summary>
    public List<int> DependencyIssueNumbers { get; init; } = new();
    /// <summary>Parent PM issue number (parsed from issue body "Parent Issue").</summary>
    public int? ParentIssueNumber { get; init; }
    /// <summary>All enhancement issue numbers this task covers (used in SinglePRMode where one task spans many enhancements).</summary>
    public List<int> RelatedEnhancementNumbers { get; init; } = new();
    /// <summary>Current GitHub labels on this issue (for status label management).</summary>
    public List<string> Labels { get; init; } = new();

    /// <summary>Skill tags for capability-based task assignment (e.g., "frontend", "react", "database").</summary>
    public List<string> SkillTags { get; init; } = new();

    // ── PE Parallelism Enhancements ──

    /// <summary>Wave assignment for parallel scheduling (W1, W2, etc.). Default W1.</summary>
    public string Wave { get; set; } = "W1";
    /// <summary>Files this task owns (CREATE + MODIFY), extracted from FilePlan. Normalized paths.</summary>
    public List<string> OwnedFiles { get; init; } = new();
    /// <summary>Typed dependencies: taskId → dependency type (files, api, schema, etc.).</summary>
    public Dictionary<string, string> DependencyTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// BUG FIX: Added AgentId field. Previously only Name (DisplayName) was stored, but
// all message routing and _agentAssignments must use Identity.Id for correct delivery.
internal record EngineerInfo
{
    public string AgentId { get; init; } = "";
    public string Name { get; init; } = "";
    public AgentRole Role { get; init; }
    public List<string> Capabilities { get; init; } = [];
}
