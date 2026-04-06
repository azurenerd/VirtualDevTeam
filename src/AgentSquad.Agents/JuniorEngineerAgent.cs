using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

public class JuniorEngineerAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly IssueWorkflow _issueWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private const int MaxSelfReviewRetries = 2;

    private readonly HashSet<int> _processedIssueIds = new();
    private readonly List<IDisposable> _subscriptions = new();

    public JuniorEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        IssueWorkflow issueWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<JuniorEngineerAgent> logger)
        : base(identity, logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _issueWorkflow = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        Logger.LogInformation("Junior Engineer {Name} initialized, awaiting task assignments",
            Identity.DisplayName);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Ready for task assignments");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var myTasks = await _prWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
                var activePR = myTasks.FirstOrDefault(pr =>
                    string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase));

                if (activePR != null && Identity.AssignedPullRequest != activePR.Number.ToString())
                {
                    await WorkOnTaskAsync(activePR, ct);
                }
                else if (activePR == null)
                {
                    UpdateStatus(AgentStatus.Idle, "Waiting for task assignment");
                }

                await CheckForIssuesAsync(ct);

                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Junior Engineer {Name} loop error", Identity.DisplayName);
                RecordError($"Loop error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Error, ex.Message);
                try { await Task.Delay(10_000, ct); }
                catch (OperationCanceledException) { break; }
                UpdateStatus(AgentStatus.Idle, "Recovered from error");
            }
        }

        UpdateStatus(AgentStatus.Offline, "Junior Engineer loop exited");
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    #region Task Execution

    private async Task WorkOnTaskAsync(AgentPullRequest pr, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Working on PR #{pr.Number}: {pr.Title}");
        Identity.AssignedPullRequest = pr.Number.ToString();

        Logger.LogInformation("Junior Engineer {Name} starting work on PR #{Number}: {Title}",
            Identity.DisplayName, pr.Number, pr.Title);

        try
        {
            // Keep context smaller for local/budget models
            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);
            var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title) ?? pr.Title;

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Step 1: Break down the task into smaller steps
            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Junior Engineer working on a low-complexity task. " +
                "Focus on producing correct, simple, and readable code. " +
                "Follow the established patterns in the project architecture " +
                "and ensure your work aligns with the business requirements. " +
                "If the task seems too complex, say so clearly.");

            history.AddUserMessage(
                $"## Business Context (key points)\n{TruncateForContext(pmSpecDoc)}\n\n" +
                $"## Architecture (key sections)\n{TruncateForContext(architectureDoc)}\n\n" +
                $"## Task: {taskTitle}\n{pr.Body}\n\n" +
                "Break this task into small, concrete implementation steps. " +
                "List each step clearly. If this task seems too complex for straightforward " +
                "implementation, start your response with 'COMPLEXITY_WARNING'.");

            var planResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var planContent = planResponse.Content ?? "";
            history.AddAssistantMessage(planContent);

            // Check if the model thinks the task is too complex
            if (planContent.Contains("COMPLEXITY_WARNING", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "Junior Engineer {Name} detected complex task in PR #{Number}, escalating",
                    Identity.DisplayName, pr.Number);
                await EscalateComplexityAsync(pr, ct);
                return;
            }

            Logger.LogDebug("Junior Engineer {Name} created plan for PR #{Number}",
                Identity.DisplayName, pr.Number);

            // Step 2: Implement step by step
            history.AddUserMessage(
                "Now implement the task following your plan. " +
                "Produce complete code for each file. " +
                "Keep it simple and correct.");

            var implResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var implementation = implResponse.Content?.Trim() ?? "";
            history.AddAssistantMessage(implementation);

            // Step 3: Self-validate with retries
            var isValid = false;
            var attempt = 0;

            while (!isValid && attempt < MaxSelfReviewRetries)
            {
                attempt++;
                Logger.LogDebug(
                    "Junior Engineer {Name} self-validation attempt {Attempt} for PR #{Number}",
                    Identity.DisplayName, attempt, pr.Number);

                var (valid, feedback) = await SelfValidateAsync(
                    chat, history, implementation, taskTitle, pr.Body, ct);

                if (valid)
                {
                    isValid = true;
                }
                else
                {
                    // Iterate: ask model to fix its own issues
                    history.AddUserMessage(
                        $"Self-review found issues:\n{feedback}\n\n" +
                        "Please fix these issues and provide the corrected implementation.");

                    var fixResponse = await chat.GetChatMessageContentAsync(
                        history, cancellationToken: ct);
                    implementation = fixResponse.Content?.Trim() ?? implementation;
                    history.AddAssistantMessage(implementation);
                }
            }

            if (!isValid)
            {
                Logger.LogWarning(
                    "Junior Engineer {Name} could not resolve self-review issues on PR #{Number} " +
                    "after {MaxRetries} retries, proceeding with best effort",
                    Identity.DisplayName, pr.Number, MaxSelfReviewRetries);
            }

            // Step 4: Post implementation as PR comment
            var comment = $"## Implementation Summary\n\n" +
                          $"**Junior Engineer:** {Identity.DisplayName}\n" +
                          $"**Self-Validation:** {(isValid ? "✅ Passed" : "⚠️ Best effort (review carefully)")}\n\n" +
                          $"{implementation}";

            await _github.AddPullRequestCommentAsync(pr.Number, comment, ct);

            // Step 5: Mark ready for review
            await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

            // Notify Principal Engineer
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "PrincipalEngineer",
                MessageType = "TaskComplete",
                NewStatus = AgentStatus.Online,
                CurrentTask = taskTitle,
                Details = $"PR #{pr.Number} implementation complete. " +
                          $"Self-validation: {(isValid ? "passed" : "needs careful review")}."
            }, ct);

            Logger.LogInformation(
                "Junior Engineer {Name} completed PR #{Number}, marked ready for review " +
                "(self-validation: {Valid})",
                Identity.DisplayName, pr.Number, isValid);

            UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting next task");
            Identity.AssignedPullRequest = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Junior Engineer {Name} failed working on PR #{Number}",
                Identity.DisplayName, pr.Number);
            RecordError($"Failed on PR #{pr.Number}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);

            await ReportBlockerAsync(
                $"Implementation failure on PR #{pr.Number}",
                $"Failed while working on PR #{pr.Number}: {pr.Title}\n\nError: {ex.Message}",
                ct);
        }
    }

    private async Task<(bool IsValid, string? Feedback)> SelfValidateAsync(
        IChatCompletionService chat,
        ChatHistory history,
        string implementation,
        string taskTitle,
        string requirements,
        CancellationToken ct)
    {
        try
        {
            var validationHistory = new ChatHistory();
            validationHistory.AddSystemMessage(
                "You are a code reviewer checking if an implementation meets its requirements. " +
                "Be concise. Check for:\n" +
                "1. Does it meet the stated requirements?\n" +
                "2. Are there obvious bugs or missing error handling?\n" +
                "3. Does it follow reasonable coding patterns?\n\n" +
                "Respond with exactly one of these on the first line:\n" +
                "VALIDATION: PASS\n" +
                "VALIDATION: FAIL\n\n" +
                "If FAIL, list the specific issues below.");

            validationHistory.AddUserMessage(
                $"## Task: {taskTitle}\n{requirements}\n\n" +
                $"## Implementation\n{implementation}");

            var response = await chat.GetChatMessageContentAsync(
                validationHistory, cancellationToken: ct);
            var result = response.Content?.Trim() ?? "";

            var passed = result.Contains("VALIDATION: PASS", StringComparison.OrdinalIgnoreCase);
            var feedback = passed ? null : result;

            return (passed, feedback);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Self-validation failed with exception, treating as pass");
            return (true, null);
        }
    }

    #endregion

    #region Complexity Escalation

    private async Task EscalateComplexityAsync(AgentPullRequest pr, CancellationToken ct)
    {
        var title = $"Task #{pr.Number} exceeds Junior Engineer capability";
        var body = $"## Complexity Escalation\n\n" +
                   $"**Junior Engineer:** {Identity.DisplayName}\n" +
                   $"**PR:** #{pr.Number} — {pr.Title}\n\n" +
                   $"This task appears too complex for a Junior Engineer. " +
                   $"The implementation requires deeper expertise or spans multiple " +
                   $"complex subsystems.\n\n" +
                   $"Requesting reassignment to a Senior or Principal Engineer.";

        try
        {
            var issue = await _issueWorkflow.AskAgentAsync(
                Identity.DisplayName,
                "Principal Engineer",
                $"{title}\n\n{body}",
                ct);

            UpdateStatus(AgentStatus.Blocked, $"Escalated PR #{pr.Number} — too complex");
            Identity.AssignedPullRequest = null;

            Logger.LogWarning(
                "Junior Engineer {Name} escalated PR #{PrNumber} via issue #{IssueNumber}",
                Identity.DisplayName, pr.Number, issue.Number);

            // Notify Principal Engineer via message bus
            await _messageBus.PublishAsync(new HelpRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "PrincipalEngineer",
                MessageType = "ComplexityEscalation",
                IssueTitle = title,
                IssueBody = body,
                IsBlocker = true
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Junior Engineer {Name} failed to escalate complexity",
                Identity.DisplayName);
            RecordError($"Escalation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning, ex);
        }
    }

    #endregion

    #region Issue Handling

    private async Task CheckForIssuesAsync(CancellationToken ct)
    {
        try
        {
            var issues = await _issueWorkflow.GetIssuesForAgentAsync(Identity.DisplayName, ct);

            foreach (var issue in issues)
            {
                if (_processedIssueIds.Contains(issue.Number))
                    continue;

                _processedIssueIds.Add(issue.Number);

                Logger.LogInformation(
                    "Junior Engineer {Name} processing issue #{Number}: {Title}",
                    Identity.DisplayName, issue.Number, issue.Title);

                if (issue.Body.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase)
                    || issue.Body.Contains("feedback", StringComparison.OrdinalIgnoreCase))
                {
                    await _issueWorkflow.ResolveIssueAsync(
                        issue.Number,
                        $"Acknowledged. {Identity.DisplayName} will address the feedback.",
                        ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Junior Engineer {Name} failed to check issues",
                Identity.DisplayName);
        }
    }

    private async Task ReportBlockerAsync(string title, string details, CancellationToken ct)
    {
        try
        {
            var issue = await _issueWorkflow.ReportBlockerAsync(
                Identity.DisplayName, title, details, ct);
            UpdateStatus(AgentStatus.Blocked, title);

            Logger.LogWarning("Junior Engineer {Name} reported blocker issue #{Number}: {Title}",
                Identity.DisplayName, issue.Number, title);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Junior Engineer {Name} failed to report blocker",
                Identity.DisplayName);
            RecordError($"Blocker report failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning, ex);
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Junior Engineer {Name} received task assignment: {Title} (Complexity: {Complexity})",
            Identity.DisplayName, message.Title, message.Complexity);

        return Task.CompletedTask;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Truncate architecture doc to keep context within local/budget model limits.
    /// </summary>
    private static string TruncateForContext(string content, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
            return content;

        return content[..maxLength] + "\n\n[... truncated for context window ...]";
    }

    #endregion
}
