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

public class ProgramManagerAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly IssueWorkflow _issueWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly Dictionary<string, AgentTracking> _trackedAgents = new();
    private readonly HashSet<int> _processedIssueIds = new();
    private int _additionalEngineersHired;
    private string? _currentPhase;

    private readonly List<IDisposable> _subscriptions = new();

    public ProgramManagerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<ProgramManagerAgent> logger)
        : base(identity, logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _issueWorkflow = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<ResourceRequestMessage>(
            Identity.Id, HandleResourceRequestAsync));

        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        _subscriptions.Add(_messageBus.Subscribe<HelpRequestMessage>(
            Identity.Id, HandleHelpRequestAsync));

        _currentPhase = "Research";
        Logger.LogInformation("PM agent initialized, starting in {Phase} phase", _currentPhase);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, "Initializing project oversight");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckExecutiveResponsesAsync(ct);
                await MonitorTeamStatusAsync(ct);
                await HandleResourceRequestsAsync(ct);
                await HandleBlockersAsync(ct);
                await ReviewPullRequestsAsync(ct);
                await UpdateProjectTrackingAsync(ct);

                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PM loop error, continuing after brief delay");
                UpdateStatus(AgentStatus.Working, "Recovering from error");
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "PM loop exited");
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    #region Main Loop Steps

    private async Task CheckExecutiveResponsesAsync(CancellationToken ct)
    {
        try
        {
            var issues = await _github.GetOpenIssuesAsync(ct);

            var executiveIssues = issues.Where(i =>
                i.Labels.Contains(IssueWorkflow.Labels.ExecutiveRequest,
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var issue in executiveIssues)
            {
                if (issue.Comments.Count == 0)
                    continue;

                // Look for new comments we haven't processed yet
                var latestComment = issue.Comments[^1];
                if (_processedIssueIds.Contains(issue.Number)
                    && latestComment.CreatedAt <= GetLastProcessedTime(issue.Number))
                    continue;

                Logger.LogInformation(
                    "Executive response on issue #{Number}: {Title}",
                    issue.Number, issue.Title);

                _processedIssueIds.Add(issue.Number);

                // If the issue contains a resource request approval, track it
                if (issue.Labels.Contains(IssueWorkflow.Labels.ResourceRequest,
                        StringComparer.OrdinalIgnoreCase))
                {
                    var body = latestComment.Body;
                    if (body.Contains("approved", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation(
                            "Resource request approved via issue #{Number}", issue.Number);
                    }
                    else if (body.Contains("denied", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation(
                            "Resource request denied via issue #{Number}", issue.Number);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check executive responses");
        }
    }

    private async Task MonitorTeamStatusAsync(CancellationToken ct)
    {
        try
        {
            var teamDoc = await _projectFiles.GetTeamMembersAsync(ct);
            var lines = teamDoc.Split('\n');

            foreach (var line in lines)
            {
                if (!line.StartsWith('|') || line.Contains("---") || line.Contains("Name"))
                    continue;

                var columns = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 3)
                    continue;

                var name = columns[0].Trim();
                var statusText = columns[2].Trim();

                if (_trackedAgents.TryGetValue(name, out var tracked))
                {
                    var docStatus = statusText;
                    var internalStatus = tracked.LastKnownStatus.ToString();

                    if (!string.Equals(docStatus, internalStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogDebug(
                            "Status mismatch for {Agent}: doc={DocStatus}, internal={InternalStatus}",
                            name, docStatus, internalStatus);
                    }
                }
            }

            // Check for stale agents that haven't reported in a while
            var timeout = TimeSpan.FromMinutes(_config.Limits.AgentTimeoutMinutes);
            foreach (var (agentId, tracking) in _trackedAgents)
            {
                if (tracking.LastKnownStatus is AgentStatus.Working or AgentStatus.Online
                    && DateTime.UtcNow - tracking.LastStatusUpdate > timeout)
                {
                    Logger.LogWarning(
                        "Agent {AgentId} has not reported status in {Minutes} minutes",
                        agentId, timeout.TotalMinutes);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to monitor team status");
        }
    }

    private async Task HandleResourceRequestsAsync(CancellationToken ct)
    {
        try
        {
            var issues = await _github.GetOpenIssuesAsync(ct);

            var resourceIssues = issues.Where(i =>
                i.Labels.Contains(IssueWorkflow.Labels.ResourceRequest,
                    StringComparer.OrdinalIgnoreCase)
                && !_processedIssueIds.Contains(i.Number)).ToList();

            foreach (var issue in resourceIssues)
            {
                _processedIssueIds.Add(issue.Number);

                if (_additionalEngineersHired >= _config.Limits.MaxAdditionalEngineers)
                {
                    Logger.LogInformation(
                        "Resource request #{Number} denied: at max additional engineers ({Max})",
                        issue.Number, _config.Limits.MaxAdditionalEngineers);

                    await _github.AddIssueCommentAsync(issue.Number,
                        $"⚠️ **Resource request denied.** The team has already hired " +
                        $"{_additionalEngineersHired}/{_config.Limits.MaxAdditionalEngineers} " +
                        "additional engineers (the configured maximum). " +
                        "Escalating to Executive for override if needed.", ct);

                    await _issueWorkflow.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Resource Limit Reached — request from issue #{issue.Number}",
                        $"A resource request was denied because the team has reached " +
                        $"the max of {_config.Limits.MaxAdditionalEngineers} additional engineers. " +
                        "Executive approval required to exceed this limit.",
                        ct);
                }
                else
                {
                    _additionalEngineersHired++;
                    Logger.LogInformation(
                        "Resource request #{Number} approved. Additional engineers: {Count}/{Max}",
                        issue.Number, _additionalEngineersHired,
                        _config.Limits.MaxAdditionalEngineers);

                    await _github.AddIssueCommentAsync(issue.Number,
                        $"✅ **Resource request approved.** Additional engineer #{_additionalEngineersHired} " +
                        $"of {_config.Limits.MaxAdditionalEngineers} maximum approved.", ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to handle resource requests");
        }
    }

    private async Task HandleBlockersAsync(CancellationToken ct)
    {
        try
        {
            var issues = await _github.GetOpenIssuesAsync(ct);

            var blockers = issues.Where(i =>
                i.Labels.Contains(IssueWorkflow.Labels.Blocker,
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var blocker in blockers)
            {
                if (_processedIssueIds.Contains(blocker.Number))
                    continue;

                _processedIssueIds.Add(blocker.Number);

                Logger.LogWarning("Blocker detected: #{Number} — {Title}",
                    blocker.Number, blocker.Title);

                // Try to triage the blocker using AI
                var resolution = await TriageBlockerAsync(blocker, ct);

                if (resolution is not null)
                {
                    await _github.AddIssueCommentAsync(blocker.Number,
                        $"🔍 **PM Triage:**\n\n{resolution}", ct);
                }
                else
                {
                    // Escalate to Executive
                    await _issueWorkflow.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Blocker Escalation — issue #{blocker.Number}",
                        $"A blocker issue needs Executive attention:\n\n" +
                        $"**Title:** {blocker.Title}\n\n{blocker.Body}",
                        ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to handle blockers");
        }
    }

    private async Task ReviewPullRequestsAsync(CancellationToken ct)
    {
        try
        {
            var prs = await _github.GetOpenPullRequestsAsync(ct);

            var readyForReview = prs.Where(pr =>
                pr.Labels.Contains(PullRequestWorkflow.Labels.ReadyForReview,
                    StringComparer.OrdinalIgnoreCase)
                && !pr.Labels.Contains(PullRequestWorkflow.Labels.Approved,
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var pr in readyForReview)
            {
                Logger.LogInformation("Reviewing PR #{Number}: {Title}", pr.Number, pr.Title);

                var reviewResult = await EvaluatePrAlignmentAsync(pr, ct);

                if (reviewResult is not null)
                {
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"📋 **PM Requirements Review:**\n\n{reviewResult}", ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review pull requests");
        }
    }

    private async Task UpdateProjectTrackingAsync(CancellationToken ct)
    {
        try
        {
            foreach (var (agentId, tracking) in _trackedAgents)
            {
                var statusText = tracking.LastKnownStatus.ToString();
                if (tracking.CurrentTask is not null)
                    statusText += $" ({tracking.CurrentTask})";

                await _projectFiles.UpdateTeamMemberStatusAsync(agentId, statusText, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update project tracking");
        }
    }

    #endregion

    #region Message Handlers

    private async Task HandleResourceRequestAsync(
        ResourceRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Resource request from {Agent}: requesting {Role} (team size: {Size})",
            message.FromAgentId, message.RequestedRole, message.CurrentTeamSize);

        if (_additionalEngineersHired >= _config.Limits.MaxAdditionalEngineers)
        {
            Logger.LogInformation(
                "Resource request from {Agent} exceeds limit, creating executive issue",
                message.FromAgentId);

            await _issueWorkflow.RequestResourceAsync(
                message.FromAgentId, message.RequestedRole, message.Justification, ct);
        }
        else
        {
            _additionalEngineersHired++;
            Logger.LogInformation(
                "Resource request from {Agent} approved via message bus ({Count}/{Max})",
                message.FromAgentId, _additionalEngineersHired,
                _config.Limits.MaxAdditionalEngineers);

            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = message.FromAgentId,
                MessageType = "ResourceApproval",
                NewStatus = AgentStatus.Online,
                Details = $"Resource request approved: {message.RequestedRole}"
            }, ct);
        }
    }

    private Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Status update from {Agent}: {Status} — {Details}",
            message.FromAgentId, message.NewStatus, message.Details);

        if (!_trackedAgents.TryGetValue(message.FromAgentId, out var tracking))
        {
            tracking = new AgentTracking
            {
                AgentId = message.FromAgentId,
                Role = AgentRole.SeniorEngineer // default; updated if known
            };
            _trackedAgents[message.FromAgentId] = tracking;
        }

        tracking.LastKnownStatus = message.NewStatus;
        tracking.CurrentTask = message.CurrentTask;
        tracking.LastStatusUpdate = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    private async Task HandleHelpRequestAsync(HelpRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Help request from {Agent}: {Title} (blocker={IsBlocker})",
            message.FromAgentId, message.IssueTitle, message.IsBlocker);

        if (message.IsBlocker)
        {
            await _issueWorkflow.ReportBlockerAsync(
                message.FromAgentId, message.IssueTitle, message.IssueBody, ct);
        }
        else
        {
            await _issueWorkflow.AskAgentAsync(
                message.FromAgentId, Identity.DisplayName, message.IssueBody, ct);
        }
    }

    #endregion

    #region AI-Assisted Methods

    private async Task<string?> TriageBlockerAsync(AgentIssue blocker, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Program Manager triaging a blocker issue in a software project. " +
                "Analyze the blocker and provide actionable guidance. " +
                "If you cannot help, respond with exactly 'ESCALATE'.");

            history.AddUserMessage(
                $"Blocker Issue #{blocker.Number}: {blocker.Title}\n\n{blocker.Body}");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);

            var result = response.Content?.Trim();

            if (string.IsNullOrWhiteSpace(result)
                || result.Equals("ESCALATE", StringComparison.OrdinalIgnoreCase))
                return null;

            return result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to triage blocker #{Number} with AI", blocker.Number);
            return null;
        }
    }

    private async Task<string?> EvaluatePrAlignmentAsync(AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var engineeringPlan = await _projectFiles.GetEngineeringPlanAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Program Manager reviewing a pull request for alignment with " +
                "project requirements. Evaluate whether the PR description and scope align " +
                "with the engineering plan. Be concise. Note any gaps or concerns. " +
                "If everything looks good, say so briefly.");

            history.AddUserMessage(
                $"## Engineering Plan\n{engineeringPlan}\n\n" +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);

            return response.Content?.Trim();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} with AI", pr.Number);
            return null;
        }
    }

    #endregion

    #region Helpers

    private DateTime GetLastProcessedTime(int issueNumber)
    {
        // Simple tracking — if we've seen the issue, return a sentinel.
        // In a more complete implementation this would store per-issue timestamps.
        return _processedIssueIds.Contains(issueNumber)
            ? DateTime.UtcNow
            : DateTime.MinValue;
    }

    #endregion
}
