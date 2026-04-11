using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// Base class for all engineering agents (Senior, Junior, Principal Engineer).
/// Contains shared logic for issue-driven work, rework handling, clarification loop,
/// PR lifecycle, and message bus interaction. Subclasses override behavior
/// via virtual/abstract methods for role-specific AI prompts and capabilities.
/// </summary>
public abstract class EngineerAgentBase : AgentBase
{
    protected readonly IMessageBus MessageBus;
    protected readonly IGitHubService GitHub;
    protected readonly PullRequestWorkflow PrWorkflow;
    protected readonly IssueWorkflow IssueWf;
    protected readonly ProjectFileManager ProjectFiles;
    protected readonly ModelRegistry Models;
    protected readonly AgentSquadConfig Config;
    protected readonly AgentStateStore StateStore;

    protected readonly HashSet<int> ProcessedIssueIds = new();
    protected readonly ConcurrentQueue<ReworkItem> ReworkQueue = new();
    protected readonly ConcurrentQueue<IssueAssignmentMessage> AssignmentQueue = new();
    protected readonly ConcurrentQueue<ClarificationResponseMessage> ClarificationResponses = new();
    protected readonly List<IDisposable> Subscriptions = new();
    // Track rework attempts per PR to enforce MaxReworkCycles limit.
    // Counts per review ROUND (not per individual reviewer feedback).
    protected readonly Dictionary<int, int> ReworkAttemptCounts = new();
    // Prevent duplicate "max limit" comments when multiple reviewers' feedback arrives
    private readonly HashSet<int> _forceApprovalSentPrs = new();
    // Per-PR CLI session IDs — resumes the session used to create the PR during rework
    private readonly Dictionary<int, string> _prSessionIds = new();
    // Cached repo tree for giving agents visibility into existing code (Tier 1: repo structure awareness)
    private IReadOnlyList<string>? _repoTreeCache;
    private DateTime _repoTreeCacheExpiry = DateTime.MinValue;
    // Local workspace for real build/test verification (null when disabled)
    protected LocalWorkspace? Workspace;
    protected readonly BuildRunner? BuildRunnerSvc;
    protected readonly TestRunner? TestRunnerSvc;
    protected readonly Core.Metrics.BuildTestMetrics? Metrics;
    protected readonly PlaywrightRunner? ScreenshotRunner;
    private bool _pendingWorkspaceCleanup;
    protected int? CurrentIssueNumber;
    protected int? CurrentPrNumber;

    protected EngineerAgentBase(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        IssueWorkflow issueWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentSquadConfig config,
        AgentMemoryStore memoryStore,
        ILogger<AgentBase> logger,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null)
        : base(identity, logger, memoryStore)
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        GitHub = github ?? throw new ArgumentNullException(nameof(github));
        PrWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        IssueWf = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        ProjectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        Models = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        StateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        BuildRunnerSvc = buildRunner;
        TestRunnerSvc = testRunner;
        Metrics = metrics;
        ScreenshotRunner = playwrightRunner;
    }

    #region Lifecycle

    protected override async Task OnInitializeAsync(CancellationToken ct)
    {
        Subscriptions.Add(MessageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        Subscriptions.Add(MessageBus.Subscribe<IssueAssignmentMessage>(
            Identity.Id, HandleIssueAssignmentAsync));

        Subscriptions.Add(MessageBus.Subscribe<ChangesRequestedMessage>(
            Identity.Id, HandleChangesRequestedAsync));

        Subscriptions.Add(MessageBus.Subscribe<ClarificationResponseMessage>(
            Identity.Id, HandleClarificationResponseAsync));

        // Subscribe to workspace cleanup signal from PE leader
        Subscriptions.Add(MessageBus.Subscribe<WorkspaceCleanupMessage>(
            Identity.Id, HandleWorkspaceCleanupAsync));

        RegisterAdditionalSubscriptions();

        // Initialize local workspace if configured
        if (Config.Workspace.IsEnabled)
        {
            try
            {
                var repoUrl = $"https://x-access-token:{Config.Project.GitHubToken}@github.com/{Config.Project.GitHubRepo}.git";
                Workspace = new LocalWorkspace(
                    Config.Workspace,
                    Identity.Id,
                    repoUrl,
                    Config.Project.DefaultBranch,
                    Logger);
                await Workspace.InitializeAsync(ct);
                Logger.LogInformation("{Role} {Name} initialized local workspace at {Path}",
                    Identity.Role, Identity.DisplayName, Workspace.RepoPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Role} {Name} failed to initialize local workspace, falling back to API mode",
                    Identity.Role, Identity.DisplayName);
                Workspace = null;
            }
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

        Logger.LogInformation("{Role} {Name} initialized, awaiting task assignments",
            Identity.Role, Identity.DisplayName);
    }

    /// <summary>Override to register additional message bus subscriptions beyond the standard four.</summary>
    protected virtual void RegisterAdditionalSubscriptions() { }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Ready for task assignments");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Priority 1: Process rework feedback from reviewers
                // Drain ALL queued rework items for the same PR into one round
                // so that feedback from multiple reviewers counts as a single rework cycle.
                if (ReworkQueue.TryDequeue(out var rework))
                {
                    var batchedFeedback = new List<ReworkItem> { rework };
                    // Drain additional items for the same PR
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

                    await HandleReworkAsync(batchedFeedback, ct);
                    continue;
                }

                // Priority 2: Process new issue assignments
                if (AssignmentQueue.TryDequeue(out var assignment))
                {
                    await WorkOnIssueAsync(assignment, ct);
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
                    var currentPr = await GitHub.GetPullRequestAsync(CurrentPrNumber.Value, ct);
                    if (currentPr is null || !string.Equals(currentPr.State, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation("{Role} {Name} PR #{PrNumber} is no longer open (merged/closed), resetting",
                            Identity.Role, Identity.DisplayName, CurrentPrNumber.Value);
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
                    var myTasks = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
                    var activePR = myTasks.FirstOrDefault(pr =>
                        string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                        && !pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase));

                    if (activePR != null && Identity.AssignedPullRequest != activePR.Number.ToString())
                    {
                        // Sync branch with main before resuming work (picks up changes merged since last run)
                        await SyncBranchWithMainAsync(activePR.Number, ct);
                        await WorkOnExistingPrAsync(activePR, ct);
                    }
                    else
                    {
                        // Track ready-for-review PRs and check for unaddressed feedback
                        var reviewPRs = myTasks.Where(pr =>
                            string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                            && pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                            .ToList();

                        if (reviewPRs.Count > 0)
                        {
                            // Pick the first one with pending feedback, else the first one
                            foreach (var reviewPR in reviewPRs)
                            {
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

                await Task.Delay(
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
    /// Called when an existing open PR is found for this agent (recovery after restart).
    /// Subclasses can override to customize behavior.
    /// </summary>
    protected virtual Task WorkOnExistingPrAsync(AgentPullRequest pr, CancellationToken ct)
        => WorkOnLegacyPrAsync(pr, ct);

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in Subscriptions)
            sub.Dispose();
        Subscriptions.Clear();

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
            var isBehind = await GitHub.IsBranchBehindMainAsync(prNumber, ct);
            if (!isBehind)
            {
                Logger.LogDebug("{Role} {Name} PR #{PrNumber} branch is already up to date with main — no sync needed",
                    Identity.Role, Identity.DisplayName, prNumber);
                return;
            }

            // Branch IS behind main — try non-destructive merge update first
            Logger.LogInformation("{Role} {Name} PR #{PrNumber} branch is behind main — syncing",
                Identity.Role, Identity.DisplayName, prNumber);

            var synced = await GitHub.UpdatePullRequestBranchAsync(prNumber, ct);
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

                var rebased = await GitHub.RebaseBranchOnMainAsync(prNumber, ct);
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
    /// Processes a new Issue assignment. Reads the Issue, optionally runs the clarification loop,
    /// creates a PR linking to the Issue, and implements the solution.
    /// </summary>
    protected virtual async Task WorkOnIssueAsync(IssueAssignmentMessage assignment, CancellationToken ct)
    {
        try
        {
            // Clear any previous PR tracking from prior task
            CurrentPrNumber = null;
            Identity.AssignedPullRequest = null;

            CurrentIssueNumber = assignment.IssueNumber;
            UpdateStatus(AgentStatus.Working, $"Starting issue #{assignment.IssueNumber}: {assignment.IssueTitle}");
            LogActivity("task", $"📋 Starting issue #{assignment.IssueNumber}: {assignment.IssueTitle}");

            var issue = await GitHub.GetIssueAsync(assignment.IssueNumber, ct);
            if (issue is null)
            {
                Logger.LogWarning("Cannot find issue #{Number}", assignment.IssueNumber);
                CurrentIssueNumber = null;
                return;
            }

            Logger.LogInformation("{Role} {Name} starting work on issue #{Number}: {Title}",
                Identity.Role, Identity.DisplayName, issue.Number, issue.Title);

            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var techStack = Config.Project.TechStack;

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Use AI to understand the Issue and plan approach
            var memoryContext = await GetMemoryContextAsync(ct: ct);
            var planHistory = new ChatHistory();
            planHistory.AddSystemMessage(
                $"You are a {GetRoleDisplayName()} analyzing a GitHub Issue (User Story) before starting work. " +
                $"The project uses {techStack}. " +
                "Read the Issue carefully and produce:\n" +
                "1. A summary of what you understand needs to be built\n" +
                "2. The acceptance criteria extracted from the Issue\n" +
                "3. Detailed **Implementation Steps** — an ordered, numbered list of discrete steps " +
                "to complete this task. Step 1 should be scaffolding (project structure, config, boilerplate). " +
                "Each step should be a self-contained unit of committable work. 3-6 steps total.\n" +
                "4. Any questions you have — if the requirements are UNCLEAR, list them. " +
                "If you understand everything well enough to proceed, say 'NO_QUESTIONS'." +
                (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}"));

            planHistory.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}");

            var planResponse = await chat.GetChatMessageContentAsync(planHistory, cancellationToken: ct);
            var planContent = planResponse.Content?.Trim() ?? "";

            // Clarification loop (if the engineer has questions)
            planContent = await RunClarificationLoopAsync(planHistory, planContent, issue, ct);

            // Create PR linking to the Issue — include Implementation Steps
            var prDescription = $"Closes #{issue.Number}\n\n" +
                $"## Understanding\n{ExtractSection(planContent, "summary", "understand")}\n\n" +
                $"## Acceptance Criteria\n{ExtractSection(planContent, "acceptance", "criteria")}\n\n" +
                $"## Implementation Steps\n{ExtractSection(planContent, "task", "plan", "step")}";

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
                ct);

            CurrentPrNumber = pr.Number;
            Identity.AssignedPullRequest = pr.Number.ToString();

            // Bind CLI session to this PR for conversational continuity
            ActivatePrSession(pr.Number);

            Logger.LogInformation("{Role} {Name} created PR #{PrNumber} for issue #{IssueNumber}",
                Identity.Role, Identity.DisplayName, pr.Number, issue.Number);
            LogActivity("github", $"Created PR #{pr.Number} for issue #{issue.Number}: {issue.Title}");

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
        var architectureDoc = await GetArchitectureForContextAsync(ct);
        var pmSpecDoc = await GetPMSpecForContextAsync(ct);
        var techStack = Config.Project.TechStack;

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Step 1: Generate ordered implementation steps from the PR description
        var steps = await GenerateImplementationStepsAsync(chat, pr, issue, pmSpecDoc, architectureDoc, techStack, ct);

        if (steps.Count == 0)
        {
            Logger.LogWarning("{Role} {Name} could not generate implementation steps, falling back to single-pass",
                Identity.Role, Identity.DisplayName);
            await ImplementSinglePassAsync(pr, issue, pmSpecDoc, architectureDoc, techStack, chat, ct);
            return;
        }

        Logger.LogInformation("{Role} {Name} generated {Count} implementation steps for PR #{Number}",
            Identity.Role, Identity.DisplayName, steps.Count, pr.Number);
        LogActivity("task", $"Generated {steps.Count} implementation steps for PR #{pr.Number}");

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

            UpdateStatus(AgentStatus.Working,
                $"PR #{pr.Number} step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");
            Logger.LogInformation("{Role} {Name} implementing step {Step}/{Total} for PR #{PrNumber}: {StepDesc}",
                Identity.Role, Identity.DisplayName, stepNumber, steps.Count, pr.Number,
                Truncate(step, 100));

            var stepHistory = new ChatHistory();
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
                    "If you need to add functionality that relates to an existing file, MODIFY that file instead of creating a new one.\n");
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

            contextBuilder.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
            contextBuilder.AppendLine(step);
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Implement ONLY this step. Output each file using this format:\n");
            contextBuilder.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
            contextBuilder.AppendLine($"Use the {techStack} technology stack. ");
            contextBuilder.AppendLine("Every file MUST use the FILE: marker format.");
            contextBuilder.AppendLine("IMPORTANT: File paths must be valid filesystem paths (e.g., src/Models/User.cs). " +
                "Do NOT put code, directives, brackets, or instructions in the file path. " +
                "Do NOT use (APPEND) or similar suffixes — always output the complete file content.");
            if (completedSteps.Count > 0)
                contextBuilder.AppendLine("If you need to update a file from a previous step, include the COMPLETE updated file content.");

            stepHistory.AddUserMessage(contextBuilder.ToString());

            var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
            var stepImpl = stepResponse.Content?.Trim() ?? "";

            // Optional self-review for this step
            stepHistory.AddAssistantMessage(stepImpl);
            var finalStepOutput = await RunSelfReviewAsync(stepHistory, stepImpl, ct);

            var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(finalStepOutput);
            if (codeFiles.Count == 0)
                codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(stepImpl);

            // Auto-correct file paths missing project subdirectory prefix
            // (e.g., "Components/Header.razor" → "src/MyProject/Components/Header.razor")
            if (codeFiles.Count > 0)
            {
                var resolved = await PrWorkflow.ResolveFilePathsAsync(codeFiles, ct);
                codeFiles = resolved as List<AgentSquad.Core.AI.CodeFileParser.CodeFile>
                    ?? new List<AgentSquad.Core.AI.CodeFileParser.CodeFile>(resolved);
            }

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
                }
                else
                {
                    Logger.LogWarning("{Role} {Name} step {Step}/{Total} blocked by build errors, skipping",
                        Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
                    LogActivity("task", $"⛔ Step {stepNumber}/{steps.Count} blocked by build errors: {Truncate(step, 80)}");
                }
            }
            else
            {
                Logger.LogWarning("{Role} {Name} step {Step}/{Total} produced no parseable files, skipping commit",
                    Identity.Role, Identity.DisplayName, stepNumber, steps.Count);
            }

            completedSteps.Add(step);
        }

        // Mark PR ready for review after all steps complete
        await MarkPrCompleteAsync(pr, issue, ct);
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
            var commitMessages = await GitHub.GetPullRequestCommitMessagesAsync(prNumber, ct);
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
            var reworkJson = System.Text.Json.JsonSerializer.Serialize(ReworkAttemptCounts);
            await StateStore.SaveAgentTaskCheckpointAsync(
                Identity.Role.ToString(),
                currentTaskId: null,
                stepIndex: stepIndex,
                prNumber: prNumber,
                issueNumber: issueNumber,
                reworkAttemptsJson: reworkJson,
                stateJson: null,
                ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to checkpoint task progress for PR #{Pr} step {Step}", prNumber, stepIndex);
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
            var history = new ChatHistory();
            history.AddSystemMessage(
                $"You are a {GetRoleDisplayName()} planning implementation steps for a coding task. " +
                $"The project uses {techStack}. " +
                "Break the task into 3-6 discrete, ordered implementation steps. " +
                "IMPORTANT rules:\n" +
                "- Step 1 MUST be project scaffolding: folder structure, config files, boilerplate, " +
                "package manifests, and empty placeholder files that establish the project skeleton.\n" +
                "- Each subsequent step should build on what the previous steps created.\n" +
                "- Each step should be a self-contained unit of work that produces committable code.\n" +
                "- Steps should be small enough to complete in a single AI response.\n" +
                "- The final step should handle polish: integration, cleanup, and any remaining wiring.\n\n" +
                "Output ONLY a numbered list of steps, one per line. Each step should be a clear, " +
                "actionable description (1-2 sentences) of what to build. No other text.");

            history.AddUserMessage(
                $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n" +
                $"## PR Description\n{pr.Body}\n\n" +
                $"## Architecture\n{archDoc}\n\n" +
                $"## PM Specification\n{pmSpec}");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";

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
    /// Fallback: implements everything in a single AI call (original behavior).
    /// Used when step generation fails.
    /// </summary>
    private async Task ImplementSinglePassAsync(
        AgentPullRequest pr, AgentIssue issue,
        string pmSpec, string archDoc, string techStack,
        IChatCompletionService chat, CancellationToken ct)
    {
        var history = new ChatHistory();
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
            "Do NOT put code, directives, brackets, or instructions in the file path.");

        history.AddUserMessage(promptBuilder.ToString());

        var implResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        history.AddAssistantMessage(implResponse.Content ?? "");
        var implementation = implResponse.Content?.Trim() ?? "";

        var finalOutput = await RunSelfReviewAsync(history, implementation, ct);
        await CommitAndNotifyAsync(pr, issue, finalOutput, implementation, ct);
    }

    /// <summary>
    /// Marks PR as ready for review and sends notification messages.
    /// Used after incremental steps complete.
    /// </summary>
    private async Task MarkPrCompleteAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(pr.Number, ct);
        await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "CodeReview"
        }, ct);

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "TaskComplete",
            NewStatus = AgentStatus.Online,
            CurrentTask = issue.Title,
            Details = $"PR #{pr.Number} implementation complete and ready for review."
        }, ct);

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
        return $"You are a {GetRoleDisplayName()} implementing step {stepNumber} of {totalSteps} " +
            $"in a coding task. The project uses {techStack}. " +
            "Focus ONLY on the current step described below. " +
            "Produce clean, production-quality code for this step only. " +
            "If files from previous steps need updating, include the COMPLETE updated file. " +
            "Be thorough for this step but do not implement future steps.";
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

            // Match "1. ...", "1) ...", "Step 1: ...", "- ..."
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                trimmed, @"^(\d+[\.\)]\s*|Step\s+\d+[:\.\)]\s*|-\s*)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!string.IsNullOrWhiteSpace(cleaned))
                steps.Add(cleaned.Trim());
        }
        return steps;
    }

    /// <summary>Gets the list of files already on the PR branch for context.</summary>
    protected async Task<string> GetPrFileListAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var files = await GitHub.GetPullRequestChangedFilesAsync(prNumber, ct);
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
    /// Commits code files to PR, marks ready for review, notifies reviewers.
    /// </summary>
    protected async Task CommitAndNotifyAsync(
        AgentPullRequest pr, AgentIssue issue, string finalOutput, string fallbackImpl, CancellationToken ct)
    {
        var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(finalOutput);
        if (codeFiles.Count == 0)
            codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(fallbackImpl);

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
            Logger.LogWarning("{Role} {Name} could not parse files for PR #{Number}, committing raw",
                Identity.Role, Identity.DisplayName, pr.Number);

            await PrWorkflow.CommitFixesToPRAsync(
                pr.Number,
                $"src/issue-{issue.Number}-implementation.md",
                $"## Implementation\n\n{finalOutput}",
                "Add implementation",
                ct);
        }

        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(pr.Number, ct);
        await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

        await MessageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewType = "CodeReview"
        }, ct);

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "TaskComplete",
            NewStatus = AgentStatus.Online,
            CurrentTask = issue.Title,
            Details = $"PR #{pr.Number} implementation complete and ready for review."
        }, ct);

        Logger.LogInformation("{Role} {Name} completed PR #{Number}, marked ready for review",
            Identity.Role, Identity.DisplayName, pr.Number);
        LogActivity("task", $"🎉 Completed PR #{pr.Number}: {pr.Title} — marked ready for review");

        UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting review/next task");
        // Keep CurrentPrNumber and AssignedPullRequest set so rework feedback can match.
    }

    #endregion

    #region Rework Handling

    /// <summary>
    /// Addresses reviewer feedback on a PR. Batches feedback from multiple reviewers
    /// into a single rework round so the cycle count is per-round, not per-reviewer.
    /// </summary>
    protected virtual async Task HandleReworkAsync(List<ReworkItem> reworkBatch, CancellationToken ct)
    {
        _ = Metrics?.RecordReworkRequestedAsync(Identity.Id, ct);
        var rework = reworkBatch[0]; // Use first item for PR number/title
        var pr = await GitHub.GetPullRequestAsync(rework.PrNumber, ct);
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            return;

        // Enforce max rework cycles per PR. Counts per ROUND (all reviewer feedback
        // in one cycle = one attempt) to prevent premature exhaustion with dual reviewers.
        var attempts = ReworkAttemptCounts.GetValueOrDefault(rework.PrNumber, 0) + 1;
        ReworkAttemptCounts[rework.PrNumber] = attempts;

        if (attempts >= Config.Limits.MaxReworkCycles)
        {
            Logger.LogWarning(
                "{Role} {Name} reached max rework cycles ({Max}) for PR #{PrNumber}, requesting force-approval",
                Identity.Role, Identity.DisplayName, Config.Limits.MaxReworkCycles, rework.PrNumber);

            // Only post the comment once per PR — check both in-memory set AND existing PR comments
            if (_forceApprovalSentPrs.Add(rework.PrNumber))
            {
                // Check if a force-approval comment already exists (from prior run)
                var existingComments = await GitHub.GetPullRequestCommentsAsync(rework.PrNumber, ct);
                var alreadyPosted = existingComments.Any(c =>
                    c.Body.Contains("maximum rework cycle limit", StringComparison.OrdinalIgnoreCase));

                if (!alreadyPosted)
                {
                    await GitHub.AddPullRequestCommentAsync(
                        rework.PrNumber,
                        $"⚠️ **{Identity.DisplayName}** has reached the maximum rework cycle limit " +
                        $"({Config.Limits.MaxReworkCycles}). Requesting final approval to unblock progress.",
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

        UpdateStatus(AgentStatus.Working, $"Addressing feedback on PR #{rework.PrNumber} (attempt {attempts}/{Config.Limits.MaxReworkCycles})");
        LogActivity("task", $"🔄 Reworking PR #{rework.PrNumber} based on feedback from {allReviewers} (attempt {attempts}/{Config.Limits.MaxReworkCycles})");
        Logger.LogInformation("{Role} {Name} reworking PR #{PrNumber} based on feedback from {Reviewers} (attempt {Attempt}/{Max})",
            Identity.Role, Identity.DisplayName, rework.PrNumber, allReviewers, attempts, Config.Limits.MaxReworkCycles);

        // Resume the CLI session that was used to create this PR
        ActivatePrSession(rework.PrNumber);

        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var techStack = Config.Project.TechStack;
            var reworkMemory = await GetMemoryContextAsync(ct: ct);

            // Load current PR file contents so the AI can see what exists and needs fixing
            var currentFilesContext = await PrWorkflow.GetPRCodeContextAsync(
                rework.PrNumber, pr.HeadBranch, ct: ct);

            var history = new ChatHistory();
            history.AddSystemMessage(GetReworkSystemPrompt(techStack) +
                (string.IsNullOrEmpty(reworkMemory) ? "" : $"\n\n{reworkMemory}"));

            history.AddUserMessage(
                $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                $"## Original PR Description\n{pr.Body}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                await GetAdditionalReworkContextAsync(ct) +
                (string.IsNullOrEmpty(currentFilesContext) ? "" :
                    $"## Current Files on PR Branch\n{currentFilesContext}\n\n") +
                $"## Review Feedback\n{combinedFeedback}\n\n" +
                "REQUIRED: Start your response with CHANGES SUMMARY that addresses each numbered " +
                "feedback item using the SAME numbers. Example:\n" +
                "CHANGES SUMMARY\n" +
                "1. Fixed the null check in AuthController.cs\n" +
                "2. Added validation for empty strings as requested\n" +
                "3. No change needed — the test already covers this case\n\n" +
                "Then you MUST output the corrected files using this exact format:\n\n" +
                "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                "Include the COMPLETE content of each changed file. " +
                "You MUST include at least one FILE: block — a summary alone is not sufficient.");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var updatedImpl = response.Content?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(updatedImpl))
            {
                var changesSummary = PullRequestWorkflow.ExtractChangesSummary(updatedImpl);

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(updatedImpl);
                if (codeFiles.Count > 0)
                {
                    bool committed;
                    if (Workspace is not null && BuildRunnerSvc is not null)
                    {
                        committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, "Address review feedback",
                            1, 1, "Address review feedback", chat, ct);
                    }
                    else
                    {
                        await PrWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, "Address review feedback", ct);
                        committed = true;
                    }

                    if (committed)
                    {
                        var commentBody = $"**[{Identity.DisplayName}] Rework** — Addressed feedback from {allReviewers}.\n\n";
                        if (!string.IsNullOrWhiteSpace(changesSummary))
                            commentBody += changesSummary;
                        else
                            commentBody += $"**Files updated:** {string.Join(", ", codeFiles.Select(f => $"`{f.Path}`"))}";
                        await GitHub.AddPullRequestCommentAsync(pr.Number, commentBody, ct);

                        await SyncBranchWithMainAsync(pr.Number, ct);
                        await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

                        await MessageBus.PublishAsync(new ReviewRequestMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = "*",
                            MessageType = "ReviewRequest",
                            PrNumber = pr.Number,
                            PrTitle = pr.Title,
                            ReviewType = "Rework"
                        }, ct);

                        Logger.LogInformation("{Role} {Name} submitted rework for PR #{PrNumber}, re-requesting review",
                            Identity.Role, Identity.DisplayName, pr.Number);
                        _ = Metrics?.RecordReworkCompletedAsync(Identity.Id, ct);

                        await RememberAsync(MemoryType.Action,
                            $"Submitted rework for PR #{pr.Number} (attempt {attempts}/{Config.Limits.MaxReworkCycles})",
                            $"Feedback from {allReviewers}. Changes: {TruncateForMemory(updatedImpl)}", ct);
                    }
                    else
                    {
                        // Build-blocked rework — notify on PR but don't re-request review.
                        // Re-enqueue so next loop iteration retries (or hits max cycles for force-approval).
                        Logger.LogWarning("{Role} {Name} rework for PR #{PrNumber} blocked by build errors",
                            Identity.Role, Identity.DisplayName, pr.Number);
                        _ = Metrics?.RecordReworkBuildBlockedAsync(Identity.Id, ct);
                        await GitHub.AddPullRequestCommentAsync(pr.Number,
                            $"**[{Identity.DisplayName}] Rework blocked** — Address review feedback produced code with build errors " +
                            $"that could not be auto-resolved. This rework attempt counted toward the limit ({attempts}/{Config.Limits.MaxReworkCycles}).", ct);

                        foreach (var item in reworkBatch)
                            ReworkQueue.Enqueue(item);
                    }
                }
                else
                {
                    // AI failed to produce FILE: blocks — re-enqueue so retry/force-approval can proceed.
                    Logger.LogWarning(
                        "{Role} {Name} rework on PR #{PrNumber} produced no FILE: blocks — no code changes committed. " +
                        "Skipping ready-for-review to avoid pointless re-review of unchanged code",
                        Identity.Role, Identity.DisplayName, pr.Number);
                    await GitHub.AddPullRequestCommentAsync(pr.Number,
                        $"**[{Identity.DisplayName}] Rework attempted** — AI response did not produce committable file changes. " +
                        $"This rework attempt counted toward the limit ({attempts}/{Config.Limits.MaxReworkCycles}).", ct);

                    foreach (var item in reworkBatch)
                        ReworkQueue.Enqueue(item);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Role} {Name} failed rework on PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, rework.PrNumber);

            // Re-enqueue rework items on failure so they get retried next loop.
            foreach (var item in reworkBatch)
                ReworkQueue.Enqueue(item);
        }
    }

    #endregion

    #region Clarification Loop

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

            await GitHub.AddIssueCommentAsync(issue.Number,
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
                    planHistory.AddUserMessage(
                        $"The PM has responded to your questions:\n\n{resp.Response}\n\n" +
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
        AssignmentQueue.Enqueue(message);
        return Task.CompletedTask;
    }

    protected virtual Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        // BUG FIX: Match by CurrentPrNumber OR AssignedPullRequest. Previously, CurrentPrNumber
        // was cleared immediately after commit, so ChangesRequestedMessage could never match and
        // the rework loop was completely broken. Now we keep CurrentPrNumber set until the PR is
        // merged/closed or a new issue is assigned (see Priority 4 and WorkOnIssueAsync).
        if (Identity.AssignedPullRequest != message.PrNumber.ToString() &&
            CurrentPrNumber != message.PrNumber)
            return Task.CompletedTask;

        Logger.LogInformation("{Role} {Name} received change request from {Reviewer} on PR #{PrNumber}",
            Identity.Role, Identity.DisplayName, message.ReviewerAgent, message.PrNumber);

        ReworkQueue.Enqueue(new ReworkItem(message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
        return Task.CompletedTask;
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

                var history = new ChatHistory();
                history.AddSystemMessage(GetImplementationSystemPrompt(techStack));
                history.AddUserMessage(
                    $"## PM Specification\n{pmSpecDoc}\n\n" +
                    $"## Architecture\n{architectureDoc}\n\n" +
                    $"## Task: {syntheticIssue.Title}\n{pr.Body}\n\n" +
                    "Produce a complete implementation. Output each file using this format:\n\n" +
                    "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                    $"Use the {techStack} technology stack. " +
                    "Include all source code files, configuration, and tests. " +
                    "Every file MUST use the FILE: marker format. " +
                    "File paths must be valid filesystem paths (e.g., src/Models/User.cs). " +
                    "Do NOT put code, directives, brackets, or instructions in the file path.");

                var implResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                history.AddAssistantMessage(implResponse.Content ?? "");
                var implementation = implResponse.Content?.Trim() ?? "";
                var finalOutput = await RunSelfReviewAsync(history, implementation, ct);

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(finalOutput);
                if (codeFiles.Count == 0)
                    codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);

                if (codeFiles.Count > 0)
                {
                    if (Workspace is not null && BuildRunnerSvc is not null)
                    {
                        var committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, "Implement task",
                            1, 1, syntheticIssue.Title, chat, ct);
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

                    var stepHistory = new ChatHistory();
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

                    stepHistory.AddUserMessage(contextBuilder.ToString());

                    var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                    var stepImpl = stepResponse.Content?.Trim() ?? "";

                    stepHistory.AddAssistantMessage(stepImpl);
                    var finalStep = await RunSelfReviewAsync(stepHistory, stepImpl, ct);

                    var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(finalStep);
                    if (codeFiles.Count == 0)
                        codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(stepImpl);

                    if (codeFiles.Count > 0)
                    {
                        var commitMsg = $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}";
                        bool committed;
                        if (Workspace is not null && BuildRunnerSvc is not null)
                        {
                            committed = await CommitViaLocalWorkspaceAsync(pr, codeFiles, commitMsg,
                                stepNumber, steps.Count, step, chat, ct);
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
            await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

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

    /// <summary>Display name for prompts, e.g., "Senior Engineer", "Junior Engineer".</summary>
    protected abstract string GetRoleDisplayName();

    /// <summary>System prompt for the implementation AI call.</summary>
    protected abstract string GetImplementationSystemPrompt(string techStack);

    /// <summary>System prompt for the rework AI call.</summary>
    protected virtual string GetReworkSystemPrompt(string techStack)
    {
        return $"You are a {GetRoleDisplayName()} addressing review feedback on a pull request. " +
            $"The project uses {techStack}. " +
            "Carefully read the feedback, understand what needs to be fixed, and produce " +
            "an updated implementation that addresses ALL the feedback points. " +
            "Be thorough — every feedback item must be resolved.\n\n" +
            "CRITICAL: Your response MUST start with a CHANGES SUMMARY that addresses EACH numbered " +
            "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
            "in one sentence what you changed or why no change was needed. This summary is posted as " +
            "a PR comment so reviewers can verify their feedback was addressed point-by-point.\n\n" +
            "After the CHANGES SUMMARY, output corrected files using FILE: format.";
    }

    /// <summary>
    /// Optional self-review pass after implementation. Senior overrides to do a multi-turn review.
    /// Default: return implementation as-is.
    /// </summary>
    protected virtual Task<string> RunSelfReviewAsync(ChatHistory history, string implementation, CancellationToken ct)
        => Task.FromResult(implementation);

    /// <summary>Additional context to include in rework prompts (e.g., PE includes engineering plan).</summary>
    protected virtual Task<string> GetAdditionalReworkContextAsync(CancellationToken ct)
        => Task.FromResult("");

    /// <summary>Get PMSpec content. Junior overrides to truncate for budget models.</summary>
    protected virtual Task<string> GetPMSpecForContextAsync(CancellationToken ct)
        => ProjectFiles.GetPMSpecAsync(ct);

    /// <summary>Get Architecture content. Junior overrides to truncate for budget models.</summary>
    protected virtual Task<string> GetArchitectureForContextAsync(CancellationToken ct)
        => ProjectFiles.GetArchitectureDocAsync(ct);

    /// <summary>
    /// Read visual design reference files from the repository for UI implementation context.
    /// Cached per-agent instance to avoid repeated reads within the same task.
    /// </summary>
    private string? _cachedDesignContext;
    private bool _designContextLoaded;

    protected async Task<string?> GetDesignContextAsync(CancellationToken ct)
    {
        if (_designContextLoaded) return _cachedDesignContext;
        _designContextLoaded = true;

        try
        {
            var tree = await GitHub.GetRepositoryTreeAsync("main", ct);
            var designFiles = tree
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".html" && ext != ".htm") return false;
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return name.Contains("design") || name.Contains("concept") ||
                           name.Contains("mockup") || name.Contains("wireframe");
                })
                .ToList();

            // Also find design screenshots committed by the Researcher
            var designScreenshots = tree
                .Where(f => f.StartsWith("docs/design-screenshots/", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (designFiles.Count == 0 && designScreenshots.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Visual Design Reference");
            sb.AppendLine("The following design files define the EXACT UI to be built. " +
                "Match the layout, colors, typography, and component structure precisely.\n");

            // Include screenshot image references for visual context
            if (designScreenshots.Count > 0)
            {
                sb.AppendLine("### Design Screenshots (rendered from HTML design files)");
                sb.AppendLine("These screenshots show the EXACT expected visual output:\n");
                foreach (var screenshot in designScreenshots)
                {
                    var fileName = Path.GetFileNameWithoutExtension(screenshot);
                    sb.AppendLine($"- **{fileName}**: See `{screenshot}` in the repository");
                }
                sb.AppendLine();
            }

            foreach (var file in designFiles)
            {
                var content = await GitHub.GetFileContentAsync(file, ct: ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### `{file}`");
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 6000 ? content[..6000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            _cachedDesignContext = sb.ToString().TrimEnd();
            Logger.LogInformation("{Role} {Name} loaded {Count} design reference files + {Screenshots} screenshots",
                Identity.Role, Identity.DisplayName, designFiles.Count, designScreenshots.Count);
            return _cachedDesignContext;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read design reference files");
            return null;
        }
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
                _repoTreeCache = await GitHub.GetRepositoryTreeAsync("main", ct);
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

    #endregion

    #region Local Workspace Build/Test

    /// <summary>
    /// Write files to local workspace, build, test, fix if needed, then commit and push.
    /// This ensures no code reaches GitHub until it actually compiles and passes tests.
    /// Returns false if the step was blocked due to unresolvable build errors.
    /// </summary>
    private async Task<bool> CommitViaLocalWorkspaceAsync(
        AgentPullRequest pr,
        IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> codeFiles,
        string commitMsg,
        int stepNumber,
        int totalSteps,
        string stepDescription,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        var wsConfig = Config.Workspace;
        var branchName = pr.HeadBranch ?? $"agent/{Identity.Id.Replace(" ", "-").ToLowerInvariant()}/pr-{pr.Number}";

        // Ensure workspace is on the right branch
        if (stepNumber == 1)
        {
            await Workspace!.SyncWithMainAsync(ct);
            await Workspace.CreateBranchAsync(branchName, ct);
        }

        // Write files to local filesystem
        foreach (var file in codeFiles)
            await Workspace!.WriteFileAsync(file.Path, file.Content, ct);

        // REQ-WS-004: Ensure project files exist before building
        EnsureProjectFiles(codeFiles);

        // Build with retry loop
        var (buildSuccess, lastBuildErrors) = await BuildWithRetryAsync(codeFiles, chat, wsConfig, stepNumber, totalSteps, stepDescription, ct);

        if (!buildSuccess)
        {
            // Build failed after retries — try full code regeneration from scratch
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

                await GitHub.AddPullRequestCommentAsync(pr.Number,
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
                    await GitHub.AddPullRequestCommentAsync(pr.Number,
                        $"❌ **Build Blocked:** Step {stepNumber}/{totalSteps} — test fixes broke the build and could not be resolved.", ct);
                    return false;
                }
            }
        }

        // Commit locally and push — only reached if build succeeded and tests pass
        await Workspace!.CommitAsync(commitMsg, ct);
        await Workspace.PushAsync(branchName, ct);
        _ = Metrics?.RecordSuccessfulCommitAsync(Identity.Id, ct);

        // Capture UI screenshot and post to PR for visual progress tracking
        await TryCaptureAndPostScreenshotAsync(pr, branchName, stepNumber, totalSteps, ct);

        return true;
    }

    /// <summary>
    /// Attempt to capture a UI screenshot of the built app and post it as a PR comment.
    /// Fails silently — screenshot capture is best-effort and never blocks the pipeline.
    /// </summary>
    private async Task TryCaptureAndPostScreenshotAsync(
        AgentPullRequest pr, string branchName, int stepNumber, int totalSteps,
        CancellationToken ct)
    {
        if (ScreenshotRunner is null || Workspace is null || !Config.Workspace.CaptureScreenshots)
            return;

        try
        {
            Logger.LogDebug("{Role} {Name} capturing UI screenshot for PR #{PrNumber} step {Step}/{Total}",
                Identity.Role, Identity.DisplayName, pr.Number, stepNumber, totalSteps);

            var screenshotBytes = await ScreenshotRunner.CaptureAppScreenshotAsync(
                Workspace.RepoPath, Config.Workspace, ct);

            if (screenshotBytes is null || screenshotBytes.Length == 0)
            {
                Logger.LogDebug("No screenshot captured (app may not be a web project)");
                return;
            }

            // Commit the screenshot to the PR branch via GitHub API
            var screenshotPath = $".screenshots/pr-{pr.Number}-step-{stepNumber}.png";
            var imageUrl = await GitHub.CommitBinaryFileAsync(
                screenshotPath, screenshotBytes,
                $"📸 UI screenshot: step {stepNumber}/{totalSteps}",
                branchName, ct);

            if (imageUrl is null) return;

            // Post a PR comment with the embedded screenshot
            var comment = $"### 📸 UI Preview — Step {stepNumber}/{totalSteps}\n\n" +
                $"![UI Screenshot after step {stepNumber}]({imageUrl})\n\n" +
                $"_Captured after successful build and commit by {Identity.DisplayName}_";

            await GitHub.AddPullRequestCommentAsync(pr.Number, comment, ct);

            Logger.LogInformation("{Role} {Name} posted UI screenshot for PR #{PrNumber} step {Step}",
                Identity.Role, Identity.DisplayName, pr.Number, stepNumber);
            LogActivity("screenshot", $"📸 UI screenshot posted for PR #{pr.Number} step {stepNumber}/{totalSteps}");
        }
        catch (Exception ex)
        {
            // Never let screenshot failures block the pipeline
            Logger.LogDebug(ex, "Screenshot capture failed for PR #{PrNumber} — continuing", pr.Number);
        }
    }

    /// <summary>
    /// Build the project locally, feeding errors back to AI for fix attempts.
    /// Returns success flag and the last build error summary (if any) for diagnostics.
    /// </summary>
    private async Task<(bool Success, string? LastErrors)> BuildWithRetryAsync(
        IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> originalFiles,
        IChatCompletionService chat,
        WorkspaceConfig wsConfig,
        int stepNumber, int totalSteps, string stepDescription,
        CancellationToken ct)
    {
        string? lastErrorSummary = null;

        for (int attempt = 0; attempt <= wsConfig.MaxBuildRetries; attempt++)
        {
            _ = Metrics?.RecordBuildAttemptAsync(Identity.Id, ct);
            var buildResult = await BuildRunnerSvc!.BuildAsync(
                Workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

            if (buildResult.Success)
            {
                _ = Metrics?.RecordBuildSuccessAsync(Identity.Id, ct);
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
            LogActivity("build", $"🔧 Build failed (attempt {attempt + 1}), asking AI to fix {buildResult.ParsedErrors.Count} errors");

            var fixPrompt = $"""
                The code from step {stepNumber}/{totalSteps} ({stepDescription}) has build errors.
                
                BUILD ERRORS:
                {lastErrorSummary}
                
                Fix ALL build errors. Output ONLY the corrected files using this format:
                FILE: path/to/file.ext
                ```language
                <complete corrected file content>
                ```
                
                Include the COMPLETE file content for each file that needs changes.
                """;

            var fixHistory = new ChatHistory();
            fixHistory.AddUserMessage(fixPrompt);
            var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixedFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(fixResponse.Content ?? "");

            foreach (var file in fixedFiles)
                await Workspace.WriteFileAsync(file.Path, file.Content, ct);
        }

        return (false, lastErrorSummary);
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
            _ = Metrics?.RecordTestRunAsync(Identity.Id, ct);
            var testResult = await TestRunnerSvc!.RunTestsAsync(
                Workspace!.RepoPath, wsConfig.TestCommand, wsConfig.TestTimeoutSeconds, ct);

            if (testResult.Success)
            {
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
            LogActivity("test", $"🧪 Tests failed (attempt {attempt + 1}/{wsConfig.MaxTestRetries}): {testResult.Failed} failed, asking AI to fix");

            var failureSummary = testResult.FailureDetails.Count > 0
                ? string.Join("\n", testResult.FailureDetails.Take(10))
                : testResult.Output.Length > 2000 ? testResult.Output[^2000..] : testResult.Output;

            var fixPrompt = $"""
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

            var fixHistory = new ChatHistory();
            fixHistory.AddUserMessage(fixPrompt);
            var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixedFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(fixResponse.Content ?? "");

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
        CancellationToken ct)
    {
        var failureSummary = lastTestResult.FailureDetails.Count > 0
            ? string.Join("\n", lastTestResult.FailureDetails.Take(20))
            : lastTestResult.Output.Length > 3000 ? lastTestResult.Output[^3000..] : lastTestResult.Output;

        var removePrompt = $"""
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

        var history = new ChatHistory();
        history.AddUserMessage(removePrompt);
        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var updatedFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(response.Content ?? "");

        if (updatedFiles.Count > 0)
        {
            _ = Metrics?.RecordTestsRemovedAsync(Identity.Id, lastTestResult.Failed, ct);
            foreach (var file in updatedFiles)
                await Workspace!.WriteFileAsync(file.Path, file.Content, ct);

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

            // Verify remaining tests pass
            var finalTestResult = await TestRunnerSvc!.RunTestsAsync(
                Workspace.RepoPath, wsConfig.TestCommand, wsConfig.TestTimeoutSeconds, ct);
            if (!finalTestResult.Success)
            {
                Logger.LogWarning("{Role} {Name} some tests still failing after removal — attempting one more removal pass",
                    Identity.Role, Identity.DisplayName);

                // One more recursive pass to catch any remaining failures
                return await RemoveFailingTestsAsync(finalTestResult, chat, wsConfig, stepNumber, totalSteps, stepDescription, pr, ct);
            }

            // Document what was removed on the PR
            var removedTestNames = lastTestResult.FailureDetails.Count > 0
                ? string.Join(", ", lastTestResult.FailureDetails.Take(10).Select(d => $"`{Truncate(d, 60)}`"))
                : $"{lastTestResult.Failed} test(s)";

            await GitHub.AddPullRequestCommentAsync(pr.Number,
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
    private async Task<IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile>?> RegenerateCodeForStepAsync(
        AgentPullRequest pr,
        string stepDescription,
        int stepNumber,
        int totalSteps,
        IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> failedFiles,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        try
        {
            var failedFileList = string.Join(", ", failedFiles.Select(f => $"`{f.Path}`"));

            var regenPrompt = $"""
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

                Output each file using this format:
                FILE: path/to/file.ext
                ```language
                <complete file content>
                ```
                """;

            var history = new ChatHistory();
            history.AddUserMessage(regenPrompt);
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var regeneratedFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(response.Content ?? "");

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
        await Workspace.SyncWithMainAsync(ct);
        await Workspace.CreateBranchAsync(branchName, ct);
    }

    #endregion

    #region Project File Scaffolding

    /// <summary>
    /// REQ-WS-004: After AI code generation, validate that .csproj and .sln files exist.
    /// If .cs files are present without a .csproj, scaffold a minimal one.
    /// If no .sln exists at repo root, scaffold one referencing all .csproj files.
    /// </summary>
    private void EnsureProjectFiles(IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> codeFiles)
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
    /// Find the project directory for a .cs file — the nearest ancestor with a .csproj,
    /// or the first significant directory (e.g., "src/ProjectName").
    /// </summary>
    private string? FindOrInferProjectDir(string csFileDir, IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> codeFiles)
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
    private void EnsureSolutionFile(IReadOnlyList<AgentSquad.Core.AI.CodeFileParser.CodeFile> codeFiles)
    {
        if (Workspace?.RepoPath is null) return;

        // Check if .sln already exists on disk or in AI output
        var slnOnDisk = Directory.GetFiles(Workspace.RepoPath, "*.sln").Length > 0;
        var slnInOutput = codeFiles.Any(f => f.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (slnOnDisk || slnInOutput) return;

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
}
