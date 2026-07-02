using System.Collections.Concurrent;
using System.Text;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

/// <summary>
/// A user-defined agent whose behavior is entirely driven by its configuration:
/// role description (persona), MCP servers (tool capabilities), and knowledge links (context).
/// Custom agents receive work via <see cref="IssueAssignmentMessage"/> from the PM or PE,
/// and produce GitHub PRs with their work products.
/// </summary>
public class CustomAgent : AgentBase
{
    private readonly AgentPlatformServices _platform;

    private readonly ConcurrentQueue<IssueAssignmentMessage> _issueQueue = new();
    private readonly ConcurrentQueue<TaskAssignmentMessage> _taskQueue = new();
    private readonly HashSet<int> _processedIssues = new();

    public CustomAgent(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        ILogger<CustomAgent> logger)
        : base(identity, core, logger)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        Subscribe<IssueAssignmentMessage>(HandleIssueAssignmentAsync);
        Subscribe<TaskAssignmentMessage>(HandleTaskAssignmentAsync);
        Subscribe<StatusUpdateMessage>(HandleStatusUpdateAsync);

        Logger.LogInformation("Custom agent '{DisplayName}' initialized, awaiting assignments",
            Identity.DisplayName);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for task assignments");

        var pollInterval = TimeSpan.FromSeconds(Core.Config.Limits.GitHubPollIntervalSeconds);

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

                await WaitForWakeOrTimeoutAsync(pollInterval, ct);
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
            var item = await _platform.WorkItemService.GetAsync(assignment.IssueNumber, ct);
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
                var branchName = await _platform.PrWorkflow.CreateTaskBranchAsync(
                    Identity.DisplayName, $"issue-{assignment.IssueNumber}", ct);

                await _platform.PrWorkflow.CreateTaskPullRequestAsync(
                    agentName: Identity.DisplayName,
                    taskTitle: assignment.IssueTitle,
                    taskDescription: workProduct,
                    complexity: assignment.Complexity,
                    architectureRef: null,
                    specRef: null,
                    branchName: branchName,
                    ct: ct);

                await PublishStatusAsync("status.update", AgentStatus.Idle,
                    details: $"PR created for: {assignment.IssueTitle}",
                    currentTask: $"Completed issue #{assignment.IssueNumber}",
                    ct: ct);

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
            var taskSys = Core.PromptService is not null
                ? await Core.PromptService?.RenderAsync("custom/task-system",
                    new Dictionary<string, string> { ["display_name"] = Identity.DisplayName }, ct)
                : null;
            history.AddSystemMessage(BuildSystemPrompt(taskSys ??
                $"You are {Identity.DisplayName}, a custom agent on a software development team. " +
                $"You have been assigned a task. Produce a detailed, actionable work product."));

            var taskUser = Core.PromptService is not null
                ? await Core.PromptService?.RenderAsync("custom/task-user",
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

            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier);
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
            var items = await _platform.WorkItemService.ListOpenAsync(ct);
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
            var desc = Core.Config.Project.ResolvedDescription ?? Core.Config.Project.Description;
            if (!string.IsNullOrWhiteSpace(desc))
                sb.AppendLine($"**Project:** {Core.Config.Project.Name}\n{desc}\n");

            sb.AppendLine($"**Tech Stack:** {Core.Config.Project.TechStack}\n");

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
        var issSys = Core.PromptService is not null
            ? await Core.PromptService?.RenderAsync("custom/issue-system",
                new Dictionary<string, string> { ["display_name"] = Identity.DisplayName }, ct)
            : null;
        history.AddSystemMessage(BuildSystemPrompt(issSys ??
            $"You are {Identity.DisplayName}, a custom agent on a software development team. " +
            $"You produce high-quality work products for assigned issues. " +
            $"Your output should be complete, well-structured, and ready for implementation or review."));

        var issUser = Core.PromptService is not null
            ? await Core.PromptService?.RenderAsync("custom/issue-user",
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

        var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier);
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
