using System.Text;
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

public class PrincipalEngineerAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly IssueWorkflow _issueWorkflow;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private bool _planningComplete;
    private readonly List<EngineeringTask> _taskBacklog = new();
    private readonly Dictionary<string, int> _agentAssignments = new(); // agentName → PR number
    private readonly HashSet<int> _processedIssueIds = new();
    private readonly List<IDisposable> _subscriptions = new();

    public PrincipalEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<PrincipalEngineerAgent> logger)
        : base(identity, logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _issueWorkflow = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        _subscriptions.Add(_messageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        Logger.LogInformation("Principal Engineer agent initialized, awaiting architecture document");
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for Architecture document");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_planningComplete)
                {
                    if (await CheckForArchitectureAsync(ct))
                    {
                        await CreateEngineeringPlanAsync(ct);
                        _planningComplete = true;
                    }
                }
                else
                {
                    await AssignTasksToAvailableEngineersAsync(ct);
                    await WorkOnOwnTasksAsync(ct);
                    await ReviewEngineerPRsAsync(ct);
                    await EvaluateResourceNeedsAsync(ct);
                    await UpdateEngineeringPlanAsync(ct);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Principal Engineer loop error, continuing after brief delay");
                RecordError($"PE error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Working, "Recovering from error");
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "Principal Engineer loop exited");
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    #region Phase 1 — Wait for Architecture and Create Plan

    private async Task<bool> CheckForArchitectureAsync(CancellationToken ct)
    {
        try
        {
            // Check GitHub Issues targeted at "Principal Engineer"
            var issues = await _issueWorkflow.GetIssuesForAgentAsync("Principal Engineer", ct);

            foreach (var issue in issues)
            {
                if (_processedIssueIds.Contains(issue.Number))
                    continue;

                if (issue.Body.Contains("Architecture document is ready", StringComparison.OrdinalIgnoreCase)
                    || issue.Body.Contains("Architecture.md", StringComparison.OrdinalIgnoreCase))
                {
                    _processedIssueIds.Add(issue.Number);

                    Logger.LogInformation(
                        "Architecture ready signal received via issue #{Number}", issue.Number);

                    // Acknowledge and close the issue
                    await _issueWorkflow.ResolveIssueAsync(
                        issue.Number,
                        "Acknowledged. Beginning engineering planning.",
                        ct);

                    return true;
                }
            }

            // Fallback: check if Architecture.md exists and has real content
            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            if (!architectureDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                && architectureDoc.Length > 200)
            {
                Logger.LogInformation("Architecture.md found with content, proceeding to planning");
                return true;
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
        // Idempotency: check if engineering plan already has real content
        var existingPlan = await _projectFiles.GetEngineeringPlanAsync(ct);
        if (!string.IsNullOrWhiteSpace(existingPlan) &&
            !existingPlan.Contains("No engineering plan has been created yet"))
        {
            Logger.LogInformation("EngineeringPlan.md already exists with content, skipping creation");
            _planningComplete = true;
            return;
        }

        UpdateStatus(AgentStatus.Working, "Creating engineering plan");
        Logger.LogInformation("Starting engineering plan creation");

        var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
        var researchDoc = await _projectFiles.GetResearchDocAsync(ct);
        var pmSpec = await _projectFiles.GetPMSpecAsync(ct);

        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a Principal Engineer creating an engineering plan from an architecture document " +
            "and PM specification. The PM spec defines the business goals, user stories, and acceptance " +
            "criteria. The architecture defines the technical design. " +
            "Break down the architecture into concrete, actionable engineering tasks that fulfill " +
            "the business requirements. " +
            "Classify each task by complexity:\n" +
            "- **High**: Complex tasks requiring deep expertise (assigned to Principal Engineer)\n" +
            "- **Medium**: Moderate tasks requiring solid experience (assigned to Senior Engineers)\n" +
            "- **Low**: Straightforward tasks suitable for guided work (assigned to Junior Engineers)\n\n" +
            "Identify dependencies between tasks. Be specific and practical. " +
            "Each task description must contain enough context for the assigned engineer to work independently.");

        // Turn 1: Analyze and identify tasks
        history.AddUserMessage(
            $"## PM Specification (Business Requirements)\n{pmSpec}\n\n" +
            $"## Architecture Document\n{architectureDoc}\n\n" +
            $"## Research Findings\n{researchDoc}\n\n" +
            "Analyze the architecture and break it down into engineering tasks. " +
            "For each task provide:\n" +
            "1. A short unique ID (T1, T2, ...)\n" +
            "2. Task name\n" +
            "3. Detailed description of what needs to be built\n" +
            "4. Complexity: High, Medium, or Low\n" +
            "5. Dependencies on other tasks (by ID)\n" +
            "6. Estimated effort (Small, Medium, Large)\n\n" +
            "List them in dependency order (tasks with no dependencies first).");

        var tasksResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(tasksResponse.Content ?? "");

        Logger.LogDebug("Task breakdown identified");

        // Turn 2: Produce structured output
        history.AddUserMessage(
            "Now produce the tasks in this exact structured format, one per line:\n" +
            "TASK|<ID>|<Name>|<Description>|<Complexity>|<Dependencies comma-separated or NONE>|<Effort>\n\n" +
            "Example:\n" +
            "TASK|T1|Set up project structure|Create solution, projects, and folder layout|Low|NONE|Small\n" +
            "TASK|T2|Implement auth module|Build JWT authentication with refresh tokens|High|T1|Large\n\n" +
            "Only output TASK lines, nothing else.");

        var structuredResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var structuredText = structuredResponse.Content ?? "";

        // Parse tasks from structured output
        _taskBacklog.Clear();
        foreach (var line in structuredText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TASK|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7)
                continue;

            var deps = parts[5].Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : parts[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            _taskBacklog.Add(new EngineeringTask
            {
                Id = parts[1].Trim(),
                Name = parts[2].Trim(),
                Description = parts[3].Trim(),
                Complexity = NormalizeComplexity(parts[4].Trim()),
                Dependencies = deps
            });
        }

        if (_taskBacklog.Count == 0)
        {
            Logger.LogWarning("No tasks parsed from AI response, creating a fallback task");
            _taskBacklog.Add(new EngineeringTask
            {
                Id = "T1",
                Name = "Review and plan implementation",
                Description = "Review architecture document and create detailed implementation plan",
                Complexity = "High"
            });
        }

        Logger.LogInformation("Engineering plan created with {Count} tasks", _taskBacklog.Count);

        // Save the engineering plan document
        var planDoc = BuildEngineeringPlanMarkdown();
        await _projectFiles.UpdateEngineeringPlanAsync(planDoc, ct);

        // Notify the team
        await _messageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringPlanReady",
            NewStatus = AgentStatus.Working,
            CurrentTask = "Engineering Planning",
            Details = $"Engineering plan created with {_taskBacklog.Count} tasks. " +
                      "Ready to assign work to engineers."
        }, ct);

        UpdateStatus(AgentStatus.Idle, "Engineering plan complete, entering development loop");
    }

    #endregion

    #region Phase 2 — Continuous Development Loop

    private async Task AssignTasksToAvailableEngineersAsync(CancellationToken ct)
    {
        try
        {
            // Read TeamMembers.md to find available engineers
            var teamDoc = await _projectFiles.GetTeamMembersAsync(ct);
            var availableEngineers = ParseAvailableEngineers(teamDoc);

            foreach (var engineer in availableEngineers)
            {
                // Skip if this engineer already has an active PR assignment
                if (_agentAssignments.ContainsKey(engineer.Name))
                {
                    var assignedPr = await _github.GetPullRequestAsync(
                        _agentAssignments[engineer.Name], ct);
                    if (assignedPr is not null
                        && string.Equals(assignedPr.State, "open", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // PR is closed/merged, remove tracking
                    _agentAssignments.Remove(engineer.Name);
                }

                // Find an unassigned task matching the engineer's complexity level
                var targetComplexity = engineer.Role switch
                {
                    AgentRole.SeniorEngineer => "Medium",
                    AgentRole.JuniorEngineer => "Low",
                    _ => null
                };

                if (targetComplexity is null)
                    continue;

                var task = FindNextAssignableTask(targetComplexity);
                if (task is null)
                    continue;

                // Build enriched task description with full context for the engineer
                var depsText = task.Dependencies.Count > 0
                    ? string.Join(", ", task.Dependencies)
                    : "None";
                var enrichedDescription = $"""
                    ## Task: {task.Name}
                    **ID:** {task.Id} | **Complexity:** {task.Complexity} | **Dependencies:** {depsText}

                    ## Description
                    {task.Description}

                    ## References
                    - See **PMSpec.md** for business goals and acceptance criteria
                    - See **Architecture.md** for system design and component boundaries
                    - See **EngineeringPlan.md** for the full task breakdown and dependencies

                    ## Deliverables
                    - Complete implementation of the task as described
                    - Unit tests covering the key functionality
                    - Code that aligns with the architecture document
                    """;

                // Create branch and PR for the task
                var branchName = await _prWorkflow.CreateTaskBranchAsync(
                    engineer.Name, $"{task.Id}-{task.Name}", ct);

                var pr = await _prWorkflow.CreateTaskPullRequestAsync(
                    engineer.Name,
                    task.Name,
                    enrichedDescription,
                    task.Complexity,
                    "Architecture.md",
                    "PMSpec.md",
                    branchName,
                    ct);

                // Track the assignment
                _agentAssignments[engineer.Name] = pr.Number;

                var taskIndex = _taskBacklog.FindIndex(t => t.Id == task.Id);
                if (taskIndex >= 0)
                {
                    _taskBacklog[taskIndex] = task with
                    {
                        Status = "Assigned",
                        AssignedTo = engineer.Name,
                        PullRequestNumber = pr.Number
                    };
                }

                Logger.LogInformation(
                    "Assigned task {TaskId} ({TaskName}) to {Engineer} via PR #{PrNumber}",
                    task.Id, task.Name, engineer.Name, pr.Number);

                // Notify the engineer via message bus with enriched context
                await _messageBus.PublishAsync(new TaskAssignmentMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = engineer.Name,
                    MessageType = "TaskAssignment",
                    TaskId = task.Id,
                    Title = task.Name,
                    Description = enrichedDescription,
                    Complexity = task.Complexity,
                    PullRequestUrl = pr.Url
                }, ct);
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
            // Find the next High-complexity task that is still Pending
            var task = _taskBacklog.FirstOrDefault(t =>
                t.Complexity == "High" && t.Status == "Pending" && AreDependenciesMet(t));

            if (task is null)
                return;

            UpdateStatus(AgentStatus.Working, $"Working on: {task.Name}");
            Logger.LogInformation("Principal Engineer working on task {TaskId}: {TaskName}",
                task.Id, task.Name);

            // Mark as in-progress
            var taskIndex = _taskBacklog.FindIndex(t => t.Id == task.Id);
            if (taskIndex >= 0)
            {
                _taskBacklog[taskIndex] = task with
                {
                    Status = "InProgress",
                    AssignedTo = Identity.DisplayName
                };
            }

            // Use Semantic Kernel to produce the implementation
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer implementing a high-complexity engineering task. " +
                "The PM Specification defines the business requirements, and the Architecture " +
                "document defines the technical design. " +
                "Produce detailed, production-quality code and design. " +
                "Include file paths, class structures, key algorithms, and tests. " +
                "Ensure the implementation fulfills the business goals from the PM spec. " +
                "Be thorough — this is the most critical part of the system.");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Task: {task.Name}\n{task.Description}\n\n" +
                "Produce a complete implementation for this task. " +
                "Include all necessary code files, their content, and a summary of what was implemented.");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var implementation = response.Content?.Trim() ?? "";

            // Create branch and PR for own work
            var branchName = await _prWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName,
                $"{task.Id}-{task.Name}",
                ct);

            var prBody = $"## Implementation: {task.Name}\n\n" +
                         $"**Task ID:** {task.Id}\n" +
                         $"**Complexity:** {task.Complexity}\n\n" +
                         $"## Details\n{implementation}";

            var pr = await _prWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                task.Name,
                prBody,
                task.Complexity,
                "Architecture.md",
                "EngineeringPlan.md",
                branchName,
                ct);

            // Mark ready for review immediately (Principal Engineer self-review goes to Architect)
            await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

            // Update task tracking
            if (taskIndex >= 0)
            {
                _taskBacklog[taskIndex] = _taskBacklog[taskIndex] with
                {
                    Status = "InProgress",
                    PullRequestNumber = pr.Number
                };
            }

            Logger.LogInformation(
                "Principal Engineer created PR #{PrNumber} for task {TaskId}", pr.Number, task.Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to work on own tasks");
        }
    }

    private async Task ReviewEngineerPRsAsync(CancellationToken ct)
    {
        try
        {
            var pendingPRs = await _prWorkflow.GetCodePRsPendingReviewAsync(ct);

            foreach (var pr in pendingPRs)
            {
                // Skip if we've already approved this PR
                if (!await _prWorkflow.NeedsReviewFromAsync(pr.Number, "PrincipalEngineer", ct))
                    continue;

                // Skip our own PRs
                var prAgent = PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title);
                if (string.Equals(prAgent, Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Logger.LogInformation(
                    "PE reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                var (approved, reviewBody) = await EvaluatePrQualityAsync(pr, ct);

                if (reviewBody is null)
                    continue;

                if (approved)
                {
                    var merged = await _prWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "PrincipalEngineer", reviewBody, ct);

                    if (merged)
                        Logger.LogInformation("PE approved and merged PR #{Number}", pr.Number);
                    else
                        Logger.LogInformation("PE approved PR #{Number}, waiting for PM approval", pr.Number);

                    // Update task backlog status
                    var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                    if (merged)
                    {
                        var matchingTask = _taskBacklog.FirstOrDefault(t =>
                            string.Equals(t.Name, taskTitle, StringComparison.OrdinalIgnoreCase));
                        if (matchingTask is not null)
                        {
                            var idx = _taskBacklog.FindIndex(t => t.Id == matchingTask.Id);
                            if (idx >= 0)
                                _taskBacklog[idx] = matchingTask with { Status = "Complete" };
                        }
                    }
                }
                else
                {
                    // Request changes — PE can make fixes directly on premium model
                    await _prWorkflow.RequestChangesAsync(pr.Number, "PrincipalEngineer", reviewBody, ct);
                    Logger.LogInformation("PE requested changes on PR #{Number}", pr.Number);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review engineer PRs");
        }
    }

    private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
    {
        try
        {
            var parallelizableTasks = _taskBacklog
                .Where(t => t.Status == "Pending" && AreDependenciesMet(t) && t.Complexity != "High")
                .ToList();

            var unassignedCount = parallelizableTasks.Count;

            // Don't request engineers if below the minimum parallelizable threshold
            if (unassignedCount < _config.Limits.MinParallelizableTasksForNewEngineer)
                return;

            var teamDoc = await _projectFiles.GetTeamMembersAsync(ct);
            var availableEngineers = ParseAvailableEngineers(teamDoc);
            var busyEngineers = _agentAssignments.Count;
            var freeEngineers = availableEngineers.Count - busyEngineers;

            // Only request if there are meaningfully more tasks than free engineers
            if (unassignedCount <= freeEngineers + 1)
                return;

            // Determine which role to request based on predominant complexity
            var pendingMedium = parallelizableTasks.Count(t => t.Complexity == "Medium");
            var pendingLow = parallelizableTasks.Count(t => t.Complexity == "Low");
            var neededRole = pendingMedium >= pendingLow
                ? AgentRole.SeniorEngineer
                : AgentRole.JuniorEngineer;

            Logger.LogInformation(
                "Requesting additional {Role}: {Unassigned} parallelizable tasks " +
                "(threshold: {Threshold}), {Free} free engineers",
                neededRole, unassignedCount,
                _config.Limits.MinParallelizableTasksForNewEngineer, freeEngineers);

            await _messageBus.PublishAsync(new ResourceRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "ProgramManager",
                MessageType = "ResourceRequest",
                RequestedRole = neededRole,
                Justification = $"There are {unassignedCount} parallelizable tasks " +
                                $"({pendingMedium} Medium, {pendingLow} Low) ready for assignment " +
                                $"but only {freeEngineers} available engineers. " +
                                $"Requesting a {neededRole} to increase throughput.",
                CurrentTeamSize = availableEngineers.Count
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate resource needs");
        }
    }

    private async Task UpdateEngineeringPlanAsync(CancellationToken ct)
    {
        try
        {
            var planDoc = BuildEngineeringPlanMarkdown();
            await _projectFiles.UpdateEngineeringPlanAsync(planDoc, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update engineering plan");
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Status update from {Agent}: {Status} — {Details}",
            message.FromAgentId, message.NewStatus, message.Details);

        // If an engineer completed a task, clear their assignment so they can be re-assigned
        if (message.MessageType == "TaskComplete"
            && _agentAssignments.ContainsKey(message.FromAgentId))
        {
            _agentAssignments.Remove(message.FromAgentId);

            if (message.CurrentTask is not null)
            {
                var task = _taskBacklog.FirstOrDefault(t => t.Id == message.CurrentTask);
                if (task is not null)
                {
                    var idx = _taskBacklog.FindIndex(t => t.Id == task.Id);
                    if (idx >= 0)
                        _taskBacklog[idx] = task with { Status = "Complete" };
                }
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        // Ignore broadcast research/architecture tasks — PE only cares about
        // engineering tasks assigned after the plan is created
        if (message.Title.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
            message.Title.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Ignoring non-engineering task assignment: {Title}", message.Title);
            return Task.CompletedTask;
        }

        Logger.LogInformation(
            "Received task assignment from {From}: {Title}",
            message.FromAgentId, message.Title);

        // If the PM or Architect assigns additional tasks, add them to the backlog
        if (!_taskBacklog.Any(t =>
                string.Equals(t.Name, message.Title, StringComparison.OrdinalIgnoreCase)))
        {
            _taskBacklog.Add(new EngineeringTask
            {
                Id = $"TX-{_taskBacklog.Count + 1}",
                Name = message.Title,
                Description = message.Description,
                Complexity = NormalizeComplexity(message.Complexity)
            });

            Logger.LogInformation("Added externally-assigned task to backlog: {Title}", message.Title);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region AI-Assisted Methods

    private async Task<(bool Approved, string? ReviewBody)> EvaluatePrQualityAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer reviewing a pull request for technical quality. " +
                "Evaluate:\n" +
                "1. Code correctness and completeness\n" +
                "2. Architecture alignment (see Architecture document)\n" +
                "3. Business requirements alignment (see PM Specification)\n" +
                "4. Error handling and edge cases\n" +
                "5. Performance considerations\n" +
                "6. Test coverage\n\n" +
                "End your review with exactly one of these verdicts on a new line:\n" +
                "VERDICT: APPROVE\n" +
                "VERDICT: REQUEST_CHANGES\n\n" +
                "Be constructive and specific with feedback.");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture Document\n{architectureDoc}\n\n" +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";
            var approved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

            // Strip the verdict line from the review body for cleanliness
            var reviewBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            return (approved, reviewBody);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} quality with AI", pr.Number);
            return (false, null);
        }
    }

    #endregion

    #region Helpers

    private EngineeringTask? FindNextAssignableTask(string targetComplexity)
    {
        return _taskBacklog.FirstOrDefault(t =>
            t.Status == "Pending"
            && string.Equals(t.Complexity, targetComplexity, StringComparison.OrdinalIgnoreCase)
            && AreDependenciesMet(t));
    }

    private bool AreDependenciesMet(EngineeringTask task)
    {
        if (task.Dependencies.Count == 0)
            return true;

        return task.Dependencies.All(depId =>
        {
            var dep = _taskBacklog.FirstOrDefault(t =>
                string.Equals(t.Id, depId, StringComparison.OrdinalIgnoreCase));
            return dep is not null && dep.Status == "Complete";
        });
    }

    private static List<EngineerInfo> ParseAvailableEngineers(string teamDoc)
    {
        var engineers = new List<EngineerInfo>();
        var lines = teamDoc.Split('\n');

        foreach (var line in lines)
        {
            if (!line.StartsWith('|') || line.Contains("---") || line.Contains("Name"))
                continue;

            var columns = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3)
                continue;

            var name = columns[0].Trim();
            var roleText = columns.Length > 1 ? columns[1].Trim() : "";
            var status = columns.Length > 2 ? columns[2].Trim() : "";

            var role = roleText.ToLowerInvariant() switch
            {
                var r when r.Contains("senior") => AgentRole.SeniorEngineer,
                var r when r.Contains("junior") => AgentRole.JuniorEngineer,
                _ => (AgentRole?)null
            };

            if (role is null)
                continue;

            // Consider engineers with Online or Working status as available for assignment
            if (status.Contains("Online", StringComparison.OrdinalIgnoreCase)
                || status.Contains("Working", StringComparison.OrdinalIgnoreCase))
            {
                engineers.Add(new EngineerInfo { Name = name, Role = role.Value });
            }
        }

        return engineers;
    }

    private static string NormalizeComplexity(string complexity)
    {
        return complexity.Trim().ToLowerInvariant() switch
        {
            "high" => "High",
            "medium" or "med" => "Medium",
            "low" => "Low",
            _ => "Medium"
        };
    }

    private string BuildEngineeringPlanMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Engineering Plan");
        sb.AppendLine();

        // Summary statistics
        var total = _taskBacklog.Count;
        var completed = _taskBacklog.Count(t => t.Status == "Complete");
        var inProgress = _taskBacklog.Count(t => t.Status is "Assigned" or "InProgress");
        var pending = _taskBacklog.Count(t => t.Status == "Pending");

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine($"**Total Tasks:** {total} | " +
                       $"**Completed:** {completed} | " +
                       $"**In Progress:** {inProgress} | " +
                       $"**Pending:** {pending}");
        sb.AppendLine();

        // Task table
        sb.AppendLine("## Tasks");
        sb.AppendLine();
        sb.AppendLine("| ID | Task | Complexity | Assigned To | PR | Status | Dependencies |");
        sb.AppendLine("|----|------|-----------|-------------|-----|--------|-------------|");

        foreach (var task in _taskBacklog)
        {
            var assignedTo = task.AssignedTo ?? "—";
            var prLink = task.PullRequestNumber.HasValue ? $"#{task.PullRequestNumber}" : "—";
            var deps = task.Dependencies.Count > 0
                ? string.Join(", ", task.Dependencies)
                : "—";

            sb.AppendLine($"| {task.Id} | {task.Name} | {task.Complexity} | " +
                          $"{assignedTo} | {prLink} | {task.Status} | {deps} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

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
    public List<string> Dependencies { get; init; } = new();
}

internal record EngineerInfo
{
    public string Name { get; init; } = "";
    public AgentRole Role { get; init; }
}
