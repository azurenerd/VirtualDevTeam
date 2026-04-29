using System.Collections.Concurrent;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// A user-defined agent whose behavior is entirely driven by its configuration:
/// role description (persona), MCP servers (tool capabilities), and knowledge links (context).
/// Custom agents receive work via <see cref="IssueAssignmentMessage"/> from the PM or PE,
/// and produce GitHub PRs with their work products.
/// </summary>
public class CustomAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IWorkItemService _workItems;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;
    private readonly IGateCheckService _gateCheck;
    private readonly IPromptTemplateService? _promptService;

    private readonly ConcurrentQueue<IssueAssignmentMessage> _issueQueue = new();
    private readonly ConcurrentQueue<TaskAssignmentMessage> _taskQueue = new();
    private readonly HashSet<int> _processedIssues = new();
    private readonly List<IDisposable> _subscriptions = new();

    public CustomAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        ILogger<CustomAgent> logger,
        RoleContextProvider? roleContextProvider = null,
        IPromptTemplateService? promptService = null,
        IWorkItemService? workItemService = null)
        : base(identity, logger, memoryStore, roleContextProvider)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _workItems = workItemService ?? throw new ArgumentNullException(nameof(workItemService));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _promptService = promptService;
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<IssueAssignmentMessage>(
            Identity.Id, HandleIssueAssignmentAsync));

        _subscriptions.Add(_messageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        Logger.LogInformation("Custom agent '{DisplayName}' initialized, awaiting assignments",
            Identity.DisplayName);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for task assignments");

        var pollInterval = TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            try
            {
                if (_issueQueue.TryDequeue(out var issueAssignment))
                {
                    UpdateStatus(AgentStatus.Working, $"Working on issue #{issueAssignment.IssueNumber}");
                    await ProcessIssueAssignmentAsync(issueAssignment, ct);
                    UpdateStatus(AgentStatus.Idle, "Waiting for next assignment");
                }
                else if (_taskQueue.TryDequeue(out var taskAssignment))
                {
                    UpdateStatus(AgentStatus.Working, $"Working on: {taskAssignment.Title}");
                    await ProcessTaskAssignmentAsync(taskAssignment, ct);
                    UpdateStatus(AgentStatus.Idle, "Waiting for next assignment");
                }
                else
                {
                    await PollForAssignedIssuesAsync(ct);
                }

                await Task.Delay(pollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Custom agent '{DisplayName}' loop error, retrying...",
                    Identity.DisplayName);
                LogActivity("error", $"Agent loop error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private Task HandleIssueAssignmentAsync(IssueAssignmentMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("Custom agent '{DisplayName}' received issue assignment: #{IssueNumber} - {Title}",
            Identity.DisplayName, msg.IssueNumber, msg.IssueTitle);
        _issueQueue.Enqueue(msg);
        return Task.CompletedTask;
    }

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("Custom agent '{DisplayName}' received task assignment: {Title}",
            Identity.DisplayName, msg.Title);
        _taskQueue.Enqueue(msg);
        return Task.CompletedTask;
    }

    private Task HandleStatusUpdateAsync(StatusUpdateMessage msg, CancellationToken ct)
    {
        Logger.LogDebug("Custom agent '{DisplayName}' received status update from {From}: {Status}",
            Identity.DisplayName, msg.FromAgentId, msg.NewStatus);
        return Task.CompletedTask;
    }

    private async Task ProcessIssueAssignmentAsync(IssueAssignmentMessage assignment, CancellationToken ct)
    {
        if (_processedIssues.Contains(assignment.IssueNumber))
        {
            Logger.LogDebug("Issue #{IssueNumber} already processed, skipping", assignment.IssueNumber);
            return;
        }

        LogActivity("work", $"Starting work on issue #{assignment.IssueNumber}: {assignment.IssueTitle}");

        try
        {
            var item = await _workItems.GetAsync(assignment.IssueNumber, ct);
            var issue = item?.ToAgentIssue();
            if (issue is null)
            {
                Logger.LogWarning("Could not find issue #{IssueNumber}", assignment.IssueNumber);
                return;
            }

            var projectContext = await GatherProjectContextAsync(ct);
            var workProduct = await GenerateWorkProductAsync(issue, projectContext, ct);

            if (!string.IsNullOrWhiteSpace(workProduct))
            {
                var branchName = await _prWorkflow.CreateTaskBranchAsync(
                    Identity.DisplayName, $"issue-{assignment.IssueNumber}", ct);

                await _prWorkflow.CreateTaskPullRequestAsync(
                    agentName: Identity.DisplayName,
                    taskTitle: assignment.IssueTitle,
                    taskDescription: workProduct,
                    complexity: assignment.Complexity,
                    architectureRef: null,
                    specRef: null,
                    branchName: branchName,
                    ct: ct);

                await _messageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "status.update",
                    NewStatus = AgentStatus.Idle,
                    CurrentTask = $"Completed issue #{assignment.IssueNumber}",
                    Details = $"PR created for: {assignment.IssueTitle}"
                }, ct);

                await RememberAsync(MemoryType.Action,
                    $"Completed issue #{assignment.IssueNumber}: {assignment.IssueTitle}",
                    ct: ct);
            }

            _processedIssues.Add(assignment.IssueNumber);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process issue #{IssueNumber}", assignment.IssueNumber);
            LogActivity("error", $"Failed to process issue #{assignment.IssueNumber}: {ex.Message}");
        }
    }

    private async Task ProcessTaskAssignmentAsync(TaskAssignmentMessage task, CancellationToken ct)
    {
        LogActivity("work", $"Starting task: {task.Title}");

        try
        {
            var projectContext = await GatherProjectContextAsync(ct);

            var history = CreateChatHistory();
            var taskSys = _promptService is not null
                ? await _promptService.RenderAsync("custom/task-system",
                    new Dictionary<string, string> { ["display_name"] = Identity.DisplayName }, ct)
                : null;
            history.AddSystemMessage(BuildSystemPrompt(taskSys ??
                $"You are {Identity.DisplayName}, a custom agent on a software development team. " +
                $"You have been assigned a task. Produce a detailed, actionable work product."));

            var taskUser = _promptService is not null
                ? await _promptService.RenderAsync("custom/task-user",
                    new Dictionary<string, string>
                    {
                        ["task_title"] = task.Title,
                        ["task_description"] = task.Description,
                        ["project_context"] = projectContext
                    }, ct)
                : null;
            history.AddUserMessage(taskUser ??
                $"## Task: {task.Title}\n\n{task.Description}\n\n" +
                $"## Project Context\n{projectContext}\n\n" +
                "Produce your work product. Be thorough and specific.");

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            SetAgentCallContext();
            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var result = response.FirstOrDefault()?.Content ?? "";

            if (!string.IsNullOrWhiteSpace(result))
            {
                LogActivity("work", $"Task completed: {task.Title}");
                await RememberAsync(MemoryType.Action,
                    $"Completed task: {task.Title}", details: result[..Math.Min(500, result.Length)], ct: ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process task: {Title}", task.Title);
            LogActivity("error", $"Failed task: {task.Title} — {ex.Message}");
        }
    }

    /// <summary>
    /// Polls GitHub for open issues that mention this agent's name in the title.
    /// </summary>
    private async Task PollForAssignedIssuesAsync(CancellationToken ct)
    {
        try
        {
            var items = await _workItems.ListOpenAsync(ct);
            var issues = items.ToAgentIssues();

            foreach (var issue in issues)
            {
                if (_processedIssues.Contains(issue.Number))
                    continue;

                if (issue.Title.Contains(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("Found assigned issue #{Number}: {Title}", issue.Number, issue.Title);
                    _issueQueue.Enqueue(new IssueAssignmentMessage
                    {
                        FromAgentId = "system",
                        ToAgentId = Identity.Id,
                        MessageType = "issue.assignment",
                        IssueNumber = issue.Number,
                        IssueTitle = issue.Title,
                        Complexity = "medium",
                        IssueUrl = issue.Url
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to poll for assigned issues");
        }
    }

    private async Task<string> GatherProjectContextAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();

        try
        {
            var desc = _config.Project.Description;
            if (!string.IsNullOrWhiteSpace(desc))
                sb.AppendLine($"**Project:** {_config.Project.Name}\n{desc}\n");

            sb.AppendLine($"**Tech Stack:** {_config.Project.TechStack}\n");

            var memory = await GetMemoryContextAsync(ct: ct);
            if (!string.IsNullOrWhiteSpace(memory))
                sb.AppendLine($"**Your Memory:**\n{memory}\n");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to gather full project context");
        }

        return sb.ToString();
    }

    private async Task<string> GenerateWorkProductAsync(
        AgentIssue issue, string projectContext, CancellationToken ct)
    {
        var history = CreateChatHistory();
        var issSys = _promptService is not null
            ? await _promptService.RenderAsync("custom/issue-system",
                new Dictionary<string, string> { ["display_name"] = Identity.DisplayName }, ct)
            : null;
        history.AddSystemMessage(BuildSystemPrompt(issSys ??
            $"You are {Identity.DisplayName}, a custom agent on a software development team. " +
            $"You produce high-quality work products for assigned issues. " +
            $"Your output should be complete, well-structured, and ready for implementation or review."));

        var issUser = _promptService is not null
            ? await _promptService.RenderAsync("custom/issue-user",
                new Dictionary<string, string>
                {
                    ["issue_number"] = issue.Number.ToString(),
                    ["issue_title"] = issue.Title,
                    ["issue_body"] = issue.Body ?? "",
                    ["project_context"] = projectContext
                }, ct)
            : null;
        history.AddUserMessage(issUser ??
            $"## Issue #{issue.Number}: {issue.Title}\n\n" +
            $"{issue.Body}\n\n" +
            $"## Project Context\n{projectContext}\n\n" +
            "Analyze this issue and produce your work product. Include all necessary detail.");

        var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        SetAgentCallContext();
        var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
        return response.FirstOrDefault()?.Content ?? "";
    }

    private void SetAgentCallContext()
    {
        AgentCallContext.CurrentAgentId = Identity.Id;
        AgentCallContext.CurrentModel = Identity.ModelTier;

        if (RoleContext is not null)
        {
            var mcpServers = RoleContext.GetMcpServers(Identity.Role, Identity.CustomAgentName);
            if (mcpServers.Count > 0)
                AgentCallContext.McpServers = mcpServers;
        }
    }
}
