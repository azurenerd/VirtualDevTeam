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

    protected readonly HashSet<int> ProcessedIssueIds = new();
    protected readonly ConcurrentQueue<ReworkItem> ReworkQueue = new();
    protected readonly ConcurrentQueue<IssueAssignmentMessage> AssignmentQueue = new();
    protected readonly ConcurrentQueue<ClarificationResponseMessage> ClarificationResponses = new();
    protected readonly List<IDisposable> Subscriptions = new();
    // BUG FIX: Track rework attempts per PR to enforce MaxReworkCycles limit.
    // Without this, PM could keep requesting changes and the engineer would loop forever.
    protected readonly Dictionary<int, int> ReworkAttemptCounts = new();
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
        AgentSquadConfig config,
        ILogger<AgentBase> logger)
        : base(identity, logger)
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        GitHub = github ?? throw new ArgumentNullException(nameof(github));
        PrWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        IssueWf = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        ProjectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        Models = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    #region Lifecycle

    protected override Task OnInitializeAsync(CancellationToken ct)
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

        Logger.LogInformation("{Role} {Name} initialized, awaiting task assignments",
            Identity.Role, Identity.DisplayName);
        return Task.CompletedTask;
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
                if (ReworkQueue.TryDequeue(out var rework))
                {
                    await HandleReworkAsync(rework, ct);
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
                        }
                        else if (activePR == null)
                        {
                            UpdateStatus(AgentStatus.Idle, "Waiting for task assignment");
                        }
                    }
                }

                await CheckForIssuesAsync(ct);

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
            var planHistory = new ChatHistory();
            planHistory.AddSystemMessage(
                $"You are a {GetRoleDisplayName()} analyzing a GitHub Issue (User Story) before starting work. " +
                $"The project uses {techStack}. " +
                "Read the Issue carefully and produce:\n" +
                "1. A summary of what you understand needs to be built\n" +
                "2. The acceptance criteria extracted from the Issue\n" +
                "3. A high-level task list of what you plan to do\n" +
                "4. Any questions you have — if the requirements are UNCLEAR, list them. " +
                "If you understand everything well enough to proceed, say 'NO_QUESTIONS'.");

            planHistory.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}");

            var planResponse = await chat.GetChatMessageContentAsync(planHistory, cancellationToken: ct);
            var planContent = planResponse.Content?.Trim() ?? "";

            // Clarification loop (if the engineer has questions)
            planContent = await RunClarificationLoopAsync(planHistory, planContent, issue, ct);

            // Create PR linking to the Issue
            var prDescription = $"Closes #{issue.Number}\n\n" +
                $"## Understanding\n{ExtractSection(planContent, "summary", "understand")}\n\n" +
                $"## Acceptance Criteria\n{ExtractSection(planContent, "acceptance", "criteria")}\n\n" +
                $"## Planned Approach\n{ExtractSection(planContent, "task", "plan")}";

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

            Logger.LogInformation("{Role} {Name} created PR #{PrNumber} for issue #{IssueNumber}",
                Identity.Role, Identity.DisplayName, pr.Number, issue.Number);

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
    /// Core implementation logic: uses AI to produce code, commits to PR, marks ready for review.
    /// Subclasses can override to add extra turns or validation steps.
    /// </summary>
    protected virtual async Task ImplementAndCommitAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        var architectureDoc = await GetArchitectureForContextAsync(ct);
        var pmSpecDoc = await GetPMSpecForContextAsync(ct);
        var techStack = Config.Project.TechStack;

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(GetImplementationSystemPrompt(techStack));

        history.AddUserMessage(
            $"## PM Specification\n{pmSpecDoc}\n\n" +
            $"## Architecture\n{architectureDoc}\n\n" +
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

        // Optional self-review pass (Senior does this, Junior skips)
        var finalOutput = await RunSelfReviewAsync(history, implementation, ct);

        await CommitAndNotifyAsync(pr, issue, finalOutput, implementation, ct);
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

        UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting review/next task");
        // Keep CurrentPrNumber and AssignedPullRequest set so rework feedback can match.
        // They will be cleared when the next issue assignment starts in WorkOnIssueAsync.
    }

    #endregion

    #region Rework Handling

    /// <summary>
    /// Addresses reviewer feedback on a PR. Uses AI to produce fixes, commits, and re-requests review.
    /// </summary>
    protected virtual async Task HandleReworkAsync(ReworkItem rework, CancellationToken ct)
    {
        var pr = await GitHub.GetPullRequestAsync(rework.PrNumber, ct);
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            return;

        // BUG FIX: Enforce max rework cycles per PR. Without this limit, the PM/PE review
        // loop could request changes indefinitely (observed in monitoring: PE stuck in
        // infinite rework on PR #59 for 20+ min). After max cycles, force-approve instead.
        var attempts = ReworkAttemptCounts.GetValueOrDefault(rework.PrNumber, 0) + 1;
        ReworkAttemptCounts[rework.PrNumber] = attempts;

        if (attempts > Config.Limits.MaxReworkCycles)
        {
            Logger.LogWarning(
                "{Role} {Name} reached max rework cycles ({Max}) for PR #{PrNumber}, requesting force-approval",
                Identity.Role, Identity.DisplayName, Config.Limits.MaxReworkCycles, rework.PrNumber);

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
            return;
        }

        UpdateStatus(AgentStatus.Working, $"Addressing feedback on PR #{rework.PrNumber} (attempt {attempts}/{Config.Limits.MaxReworkCycles})");
        Logger.LogInformation("{Role} {Name} reworking PR #{PrNumber} based on feedback from {Reviewer} (attempt {Attempt}/{Max})",
            Identity.Role, Identity.DisplayName, rework.PrNumber, rework.Reviewer, attempts, Config.Limits.MaxReworkCycles);

        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var techStack = Config.Project.TechStack;

            var history = new ChatHistory();
            history.AddSystemMessage(GetReworkSystemPrompt(techStack));

            history.AddUserMessage(
                $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                $"## Original PR Description\n{pr.Body}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                await GetAdditionalReworkContextAsync(ct) +
                $"## Review Feedback from {rework.Reviewer}\n{rework.Feedback}\n\n" +
                "Please provide the corrected files that address all the feedback. " +
                "Output each file using this exact format:\n\n" +
                "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                "Include the COMPLETE content of each changed file.");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var updatedImpl = response.Content?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(updatedImpl))
            {
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
                        $"**Reviewer:** {rework.Reviewer}\n\n" +
                        $"### Changes Made\n{updatedImpl}",
                        $"Address review feedback from {rework.Reviewer}",
                        ct);
                }

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
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Role} {Name} failed rework on PR #{PrNumber}",
                Identity.Role, Identity.DisplayName, rework.PrNumber);

            // BUG FIX: Re-enqueue rework item on failure so it gets retried next loop.
            // Previously, TryDequeue removed the item and a failed AI call meant the
            // rework feedback was permanently lost — the PR would stay in limbo forever.
            ReworkQueue.Enqueue(rework);
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
    /// Uses the same AI pattern as issue-driven work but with the PR as context.
    /// </summary>
    protected async Task WorkOnLegacyPrAsync(AgentPullRequest pr, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Working on PR #{pr.Number}: {pr.Title}");
        Identity.AssignedPullRequest = pr.Number.ToString();

        Logger.LogInformation("{Role} {Name} starting work on PR #{Number}: {Title}",
            Identity.Role, Identity.DisplayName, pr.Number, pr.Title);

        try
        {
            var architectureDoc = await GetArchitectureForContextAsync(ct);
            var pmSpecDoc = await GetPMSpecForContextAsync(ct);
            var techStack = Config.Project.TechStack;

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(GetImplementationSystemPrompt(techStack));

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Task: {PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title)}\n{pr.Body}\n\n" +
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
            {
                await PrWorkflow.CommitCodeFilesToPRAsync(
                    pr.Number, codeFiles, "Implement task", ct);
            }
            else
            {
                var taskSlug = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title) ?? "implementation";
                await PrWorkflow.CommitFixesToPRAsync(
                    pr.Number,
                    $"src/{taskSlug}-implementation.md",
                    $"## Implementation\n\n{finalOutput}",
                    "Add implementation",
                    ct);
            }

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
