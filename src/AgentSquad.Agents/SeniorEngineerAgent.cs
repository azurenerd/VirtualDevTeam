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

public class SeniorEngineerAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly IssueWorkflow _issueWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly HashSet<int> _processedIssueIds = new();
    private readonly List<IDisposable> _subscriptions = new();

    public SeniorEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        IssueWorkflow issueWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<SeniorEngineerAgent> logger)
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

        Logger.LogInformation("Senior Engineer {Name} initialized, awaiting task assignments",
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
                Logger.LogError(ex, "Senior Engineer {Name} loop error", Identity.DisplayName);
                RecordError($"Loop error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Error, ex.Message);
                try { await Task.Delay(10_000, ct); }
                catch (OperationCanceledException) { break; }
                UpdateStatus(AgentStatus.Idle, "Recovered from error");
            }
        }

        UpdateStatus(AgentStatus.Offline, "Senior Engineer loop exited");
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

        Logger.LogInformation("Senior Engineer {Name} starting work on PR #{Number}: {Title}",
            Identity.DisplayName, pr.Number, pr.Title);

        try
        {
            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var researchDoc = await _projectFiles.GetResearchDocAsync(ct);
            var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            // Turn 1: Analyze requirements and plan approach
            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Senior Engineer implementing a medium-complexity engineering task. " +
                "You produce clean, well-structured code that follows the project architecture " +
                "and fulfills the business requirements from the PM specification. " +
                "Include proper error handling, logging, and unit tests. " +
                "Be thorough and practical.");

            history.AddUserMessage(
                $"## PM Specification (Business Requirements)\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Research Context\n{researchDoc}\n\n" +
                $"## Task: {PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title)}\n{pr.Body}\n\n" +
                "First, analyze the requirements and outline your implementation plan. " +
                "Identify key components, interfaces, and dependencies.");

            var analysisResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            history.AddAssistantMessage(analysisResponse.Content ?? "");

            Logger.LogDebug("Senior Engineer {Name} completed analysis for PR #{Number}",
                Identity.DisplayName, pr.Number);

            // Turn 2: Produce implementation
            history.AddUserMessage(
                "Now produce the complete implementation. Include:\n" +
                "1. All code files with their full paths\n" +
                "2. Key class structures and method implementations\n" +
                "3. Error handling and edge cases\n" +
                "4. Unit test stubs or full tests\n\n" +
                "Be production-ready.");

            var implementationResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            history.AddAssistantMessage(implementationResponse.Content ?? "");
            var implementation = implementationResponse.Content?.Trim() ?? "";

            // Turn 3: Self-review
            history.AddUserMessage(
                "Review your own implementation critically. Check for:\n" +
                "1. Missing error handling\n" +
                "2. Architecture alignment issues\n" +
                "3. Edge cases not covered\n" +
                "4. Any bugs or logic errors\n\n" +
                "If you find issues, provide the corrected implementation. " +
                "If it looks good, confirm with a brief summary.");

            var reviewResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var finalOutput = reviewResponse.Content?.Trim() ?? implementation;

            // Post implementation summary as PR comment
            var comment = $"## Implementation Summary\n\n" +
                          $"**Senior Engineer:** {Identity.DisplayName}\n\n" +
                          $"{finalOutput}";

            await _github.AddPullRequestCommentAsync(pr.Number, comment, ct);

            // Mark PR as ready for review
            await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

            // Notify Principal Engineer
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "PrincipalEngineer",
                MessageType = "TaskComplete",
                NewStatus = AgentStatus.Online,
                CurrentTask = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title),
                Details = $"PR #{pr.Number} implementation complete and ready for review."
            }, ct);

            Logger.LogInformation(
                "Senior Engineer {Name} completed PR #{Number}, marked ready for review",
                Identity.DisplayName, pr.Number);

            UpdateStatus(AgentStatus.Idle, $"Completed PR #{pr.Number}, awaiting next task");
            Identity.AssignedPullRequest = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Senior Engineer {Name} failed working on PR #{Number}",
                Identity.DisplayName, pr.Number);
            RecordError($"Failed on PR #{pr.Number}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);

            await ReportBlockerAsync(
                $"Implementation failure on PR #{pr.Number}",
                $"Failed while working on PR #{pr.Number}: {pr.Title}\n\nError: {ex.Message}",
                ct);
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
                    "Senior Engineer {Name} processing issue #{Number}: {Title}",
                    Identity.DisplayName, issue.Number, issue.Title);

                // Handle guidance/feedback from Principal Engineer or Architect
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
            Logger.LogWarning(ex, "Senior Engineer {Name} failed to check issues",
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

            Logger.LogWarning("Senior Engineer {Name} reported blocker issue #{Number}: {Title}",
                Identity.DisplayName, issue.Number, title);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Senior Engineer {Name} failed to report blocker",
                Identity.DisplayName);
            RecordError($"Blocker report failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning, ex);
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Senior Engineer {Name} received task assignment: {Title} (Complexity: {Complexity})",
            Identity.DisplayName, message.Title, message.Complexity);

        return Task.CompletedTask;
    }

    #endregion
}
