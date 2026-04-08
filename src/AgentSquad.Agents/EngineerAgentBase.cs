using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
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
        ILogger<AgentBase> logger)
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

        RegisterAdditionalSubscriptions();

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
                        // Track a ready-for-review PR so rework feedback can reach us
                        var reviewPR = myTasks.FirstOrDefault(pr =>
                            string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                            && pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase));
                        if (reviewPR != null)
                        {
                            CurrentPrNumber = reviewPR.Number;
                            Identity.AssignedPullRequest = reviewPR.Number.ToString();
                            Logger.LogInformation("{Role} {Name} re-tracking PR #{PrNumber} awaiting review",
                                Identity.Role, Identity.DisplayName, reviewPR.Number);

                            // Re-broadcast review request so reviewers pick it up after restart
                            // (bus messages are in-memory and lost on restart)
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

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in Subscriptions)
            sub.Dispose();
        Subscriptions.Clear();
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
            var synced = await GitHub.UpdatePullRequestBranchAsync(prNumber, ct);
            if (synced)
            {
                Logger.LogInformation("{Role} {Name} synced PR #{PrNumber} branch with main",
                    Identity.Role, Identity.DisplayName, prNumber);
            }
            else
            {
                Logger.LogWarning("{Role} {Name} PR #{PrNumber} branch sync failed — possible merge conflict",
                    Identity.Role, Identity.DisplayName, prNumber);
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
            contextBuilder.AppendLine($"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n");
            contextBuilder.AppendLine($"## PR Description\n{pr.Body}\n");

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

            if (codeFiles.Count > 0)
            {
                var commitMsg = $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}";
                await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, commitMsg, ct);
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

        history.AddUserMessage(
            $"## PM Specification\n{pmSpec}\n\n" +
            $"## Architecture\n{archDoc}\n\n" +
            $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n" +
            $"## PR Description\n{pr.Body}\n\n" +
            "Produce a complete implementation. Output each file using this format:\n\n" +
            "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
            $"Use the {techStack} technology stack. " +
            "Include all source code files, configuration, and tests. " +
            "Every file MUST use the FILE: marker format.");

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

            await PrWorkflow.CommitCodeFilesToPRAsync(
                pr.Number, codeFiles, $"Implement issue #{issue.Number}: {issue.Title}", ct);
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
        var rework = reworkBatch[0]; // Use first item for PR number/title
        var pr = await GitHub.GetPullRequestAsync(rework.PrNumber, ct);
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            return;

        // Enforce max rework cycles per PR. Counts per ROUND (all reviewer feedback
        // in one cycle = one attempt) to prevent premature exhaustion with dual reviewers.
        var attempts = ReworkAttemptCounts.GetValueOrDefault(rework.PrNumber, 0) + 1;
        ReworkAttemptCounts[rework.PrNumber] = attempts;

        if (attempts > Config.Limits.MaxReworkCycles)
        {
            Logger.LogWarning(
                "{Role} {Name} reached max rework cycles ({Max}) for PR #{PrNumber}, requesting force-approval",
                Identity.Role, Identity.DisplayName, Config.Limits.MaxReworkCycles, rework.PrNumber);

            // Only post the comment once per PR (not for every queued rework item)
            if (_forceApprovalSentPrs.Add(rework.PrNumber))
            {
                await GitHub.AddPullRequestCommentAsync(
                    rework.PrNumber,
                    $"⚠️ **{Identity.DisplayName}** has reached the maximum rework cycle limit " +
                    $"({Config.Limits.MaxReworkCycles}). Requesting final approval to unblock progress.",
                    ct);

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

            var history = new ChatHistory();
            history.AddSystemMessage(GetReworkSystemPrompt(techStack) +
                (string.IsNullOrEmpty(reworkMemory) ? "" : $"\n\n{reworkMemory}"));

            history.AddUserMessage(
                $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                $"## Original PR Description\n{pr.Body}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                await GetAdditionalReworkContextAsync(ct) +
                $"## Review Feedback\n{combinedFeedback}\n\n" +
                "First, output a CHANGES SUMMARY section that addresses each numbered feedback item " +
                "from the reviewer. Use the same numbers (1. 2. 3.) and briefly state what you " +
                "changed or why no change was needed. Keep each response to one sentence.\n\n" +
                "Then output the corrected files using this exact format:\n\n" +
                "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                "Include the COMPLETE content of each changed file.");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var updatedImpl = response.Content?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(updatedImpl))
            {
                // Extract the changes summary (everything before first FILE: block)
                var summaryEnd = updatedImpl.IndexOf("FILE:", StringComparison.OrdinalIgnoreCase);
                var changesSummary = summaryEnd > 0
                    ? updatedImpl[..summaryEnd].Trim()
                    : null;

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(updatedImpl);
                if (codeFiles.Count > 0)
                {
                    await PrWorkflow.CommitCodeFilesToPRAsync(
                        pr.Number, codeFiles, "Address review feedback", ct);
                }
                else
                {
                    var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                    await PrWorkflow.CommitFixesToPRAsync(
                        pr.Number,
                        $"src/{taskTitle}-rework.md",
                        $"## Rework: Addressing Review Feedback\n\n" +
                        $"**Reviewers:** {allReviewers}\n\n" +
                        $"### Changes Made\n{updatedImpl}",
                        $"Address review feedback from {allReviewers}",
                        ct);
                }

                // Post a comment with the numbered changes summary
                var commentBody = $"**[{Identity.DisplayName}] Rework** — Addressed feedback from {allReviewers}.\n\n";
                if (!string.IsNullOrWhiteSpace(changesSummary))
                    commentBody += changesSummary;
                else
                {
                    var filesList = codeFiles.Count > 0
                        ? string.Join(", ", codeFiles.Select(f => $"`{f.Path}`"))
                        : "rework document";
                    commentBody += $"**Files updated:** {filesList}";
                }
                await GitHub.AddPullRequestCommentAsync(pr.Number, commentBody, ct);

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
                    ReviewType = "Rework"
                }, ct);

                Logger.LogInformation("{Role} {Name} submitted rework for PR #{PrNumber}, re-requesting review",
                    Identity.Role, Identity.DisplayName, pr.Number);

                await RememberAsync(MemoryType.Action,
                    $"Submitted rework for PR #{pr.Number} (attempt {attempts}/{Config.Limits.MaxReworkCycles})",
                    $"Feedback from {allReviewers}. Changes: {TruncateForMemory(updatedImpl)}", ct);
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
                    "Every file MUST use the FILE: marker format.");

                var implResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                history.AddAssistantMessage(implResponse.Content ?? "");
                var implementation = implResponse.Content?.Trim() ?? "";
                var finalOutput = await RunSelfReviewAsync(history, implementation, ct);

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(finalOutput);
                if (codeFiles.Count == 0)
                    codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);

                if (codeFiles.Count > 0)
                    await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, "Implement task", ct);
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
                        await PrWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}", ct);
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
            "Be thorough — every feedback item must be resolved.";
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
