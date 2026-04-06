using System.Collections.Concurrent;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;
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
    private readonly AgentRegistry _registry;
    private readonly AgentSquadConfig _config;

    private bool _planningComplete;
    private bool _resourceRequestPending;
    private readonly List<EngineeringTask> _taskBacklog = new();
    private readonly Dictionary<string, int> _agentAssignments = new(); // agentName → PR number
    private readonly HashSet<int> _processedIssueIds = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();
    private readonly ConcurrentQueue<ReworkItem> _reworkQueue = new();
    private readonly List<IDisposable> _subscriptions = new();

    public PrincipalEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentRegistry registry,
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
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        _subscriptions.Add(_messageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        _subscriptions.Add(_messageBus.Subscribe<ReviewRequestMessage>(
            Identity.Id, HandleReviewRequestAsync));

        _subscriptions.Add(_messageBus.Subscribe<ChangesRequestedMessage>(
            Identity.Id, HandleChangesRequestedAsync));

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
                    // Priority 1: Process rework feedback on our own PRs
                    await ProcessOwnReworkAsync(ct);
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
            RestoreTaskBacklogFromPlan(existingPlan);
            if (_taskBacklog.Count > 0)
            {
                Logger.LogInformation("Restored {Count} tasks from existing engineering plan", _taskBacklog.Count);
                _planningComplete = true;
                return;
            }
            Logger.LogWarning("Existing EngineeringPlan.md has no tasks — regenerating");
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
            // Discover engineers from the in-process AgentRegistry
            var registeredEngineers = new List<EngineerInfo>();
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                registeredEngineers.Add(new EngineerInfo { Name = agent.Identity.DisplayName, Role = AgentRole.SeniorEngineer });
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                registeredEngineers.Add(new EngineerInfo { Name = agent.Identity.DisplayName, Role = AgentRole.JuniorEngineer });

            foreach (var engineer in registeredEngineers)
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

                // Generate rich PR description using AI + project documents
                var enrichedDescription = await GenerateTaskDescriptionAsync(task, ct);

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

            // Generate rich PR description using AI + project documents
            UpdateStatus(AgentStatus.Working, $"Planning: {task.Name}");
            var prDescription = await GenerateTaskDescriptionAsync(task, ct);

            var branchName = await _prWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName,
                $"{task.Id}-{task.Name}",
                ct);

            var pr = await _prWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                task.Name,
                prDescription,
                task.Complexity,
                "Architecture.md",
                "EngineeringPlan.md",
                branchName,
                ct);

            // Mark as in-progress with PR number
            var taskIndex = _taskBacklog.FindIndex(t => t.Id == task.Id);
            if (taskIndex >= 0)
            {
                _taskBacklog[taskIndex] = task with
                {
                    Status = "InProgress",
                    AssignedTo = Identity.DisplayName,
                    PullRequestNumber = pr.Number
                };
            }

            Logger.LogInformation(
                "Principal Engineer created PR #{PrNumber} for task {TaskId}, starting implementation",
                pr.Number, task.Id);

            // Now do the AI work
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);
            var techStack = _config.Project.TechStack;

            var history = new ChatHistory();
            history.AddSystemMessage(
                $"You are a Principal Engineer implementing a high-complexity engineering task. " +
                $"The project uses {techStack} as its technology stack. " +
                "The PM Specification defines the business requirements, and the Architecture " +
                "document defines the technical design. " +
                "Produce detailed, production-quality code. " +
                "Ensure the implementation fulfills the business goals from the PM spec. " +
                "Be thorough — this is the most critical part of the system.");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Task: {task.Name}\n{task.Description}\n\n" +
                "Produce a complete implementation for this task. " +
                "Output each file using this exact format:\n\n" +
                "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                $"Use the {techStack} technology stack. " +
                "Include all source code files, configuration, and tests. " +
                "Every file MUST use the FILE: marker format so it can be parsed and committed.");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var implementation = response.Content?.Trim() ?? "";

            // Parse and commit code files
            var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);

            if (codeFiles.Count > 0)
            {
                Logger.LogInformation(
                    "Principal Engineer parsed {Count} code files for task {TaskId}",
                    codeFiles.Count, task.Id);

                await _prWorkflow.CommitCodeFilesToPRAsync(
                    pr.Number, codeFiles, $"Implement {task.Name}", ct);
            }
            else
            {
                // Fallback: commit raw output
                Logger.LogWarning(
                    "Principal Engineer could not parse structured files for task {TaskId}, committing raw",
                    task.Id);

                await _prWorkflow.CommitFixesToPRAsync(
                    pr.Number,
                    $"src/{task.Id}-implementation.md",
                    $"## Implementation: {task.Name}\n\n{implementation}",
                    $"Add implementation for {task.Name}",
                    ct);
            }

            // Mark ready for review
            await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

            // Notify PM and Architect to review this PR
            await _messageBus.PublishAsync(new ReviewRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ReviewRequest",
                PrNumber = pr.Number,
                PrTitle = pr.Title,
                ReviewType = "CodeReview"
            }, ct);

            Logger.LogInformation(
                "Principal Engineer completed implementation for PR #{PrNumber} (task {TaskId})",
                pr.Number, task.Id);
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
            // Drain the review queue — only review PRs we've been notified about
            var prNumbersToReview = new HashSet<int>();
            while (_reviewQueue.TryDequeue(out var prNumber))
                prNumbersToReview.Add(prNumber);

            if (prNumbersToReview.Count == 0)
                return;

            foreach (var prNumber in prNumbersToReview)
            {
                if (_reviewedPrNumbers.Contains(prNumber))
                    continue;

                // Skip if we've already posted a review comment (GitHub check)
                if (!await _prWorkflow.NeedsReviewFromAsync(prNumber, "PrincipalEngineer", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                var pr = await _github.GetPullRequestAsync(prNumber, ct);
                if (pr is null)
                    continue;

                // Skip our own PRs — PE self-approves via MarkReadyForReviewAsync
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
                    await _prWorkflow.RequestChangesAsync(pr.Number, "PrincipalEngineer", reviewBody, ct);
                    Logger.LogInformation("PE requested changes on PR #{Number}", pr.Number);

                    // Notify the author engineer to rework
                    await _messageBus.PublishAsync(new ChangesRequestedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ChangesRequested",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewerAgent = "PrincipalEngineer",
                        Feedback = reviewBody
                    }, ct);
                }

                _reviewedPrNumbers.Add(prNumber);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review engineer PRs");
        }
    }

    private async Task ProcessOwnReworkAsync(CancellationToken ct)
    {
        while (_reworkQueue.TryDequeue(out var rework))
        {
            var pr = await _github.GetPullRequestAsync(rework.PrNumber, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                continue;

            UpdateStatus(AgentStatus.Working, $"Addressing feedback on PR #{rework.PrNumber}");
            Logger.LogInformation(
                "PE reworking own PR #{PrNumber} based on feedback from {Reviewer}",
                rework.PrNumber, rework.Reviewer);

            try
            {
                var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();

                var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
                var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);
                var planDoc = await _projectFiles.GetEngineeringPlanAsync(ct);
                var techStack = _config.Project.TechStack;

                var history = new ChatHistory();
                history.AddSystemMessage(
                    $"You are a Principal Engineer addressing review feedback on your pull request. " +
                    $"The project uses {techStack}. " +
                    "You have access to the full architecture, PM spec, and engineering plan. " +
                    "Carefully read the feedback, understand what needs to be fixed, and produce " +
                    "an updated implementation that addresses ALL the feedback points. " +
                    "Be thorough and produce production-quality fixes.");

                history.AddUserMessage(
                    $"## PR #{rework.PrNumber}: {rework.PrTitle}\n" +
                    $"## Original PR Description\n{pr.Body}\n\n" +
                    $"## Architecture\n{architectureDoc}\n\n" +
                    $"## PM Specification\n{pmSpecDoc}\n\n" +
                    $"## Engineering Plan\n{planDoc}\n\n" +
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
                        await _prWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, "Address review feedback", ct);
                    }
                    else
                    {
                        var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                        await _prWorkflow.CommitFixesToPRAsync(
                            pr.Number,
                            $"src/{taskTitle}-rework.md",
                            $"## Rework: Addressing Review Feedback\n\n" +
                            $"**Reviewer:** {rework.Reviewer}\n\n" +
                            $"### Changes Made\n{updatedImpl}",
                            $"Address review feedback from {rework.Reviewer}",
                            ct);
                    }

                    await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

                    await _messageBus.PublishAsync(new ReviewRequestMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ReviewRequest",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewType = "Rework"
                    }, ct);

                    Logger.LogInformation(
                        "PE submitted rework for PR #{PrNumber}, re-requesting review",
                        pr.Number);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PE failed rework on PR #{PrNumber}", rework.PrNumber);
            }
        }
    }

    private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
    {
        try
        {
            // Don't re-request if we already have a pending request
            if (_resourceRequestPending)
            {
                // Check if engineers have appeared since last request
                var currentEngineers = _registry.GetAgentsByRole(AgentRole.SeniorEngineer).Count
                                     + _registry.GetAgentsByRole(AgentRole.JuniorEngineer).Count;
                if (currentEngineers > 0)
                    _resourceRequestPending = false;
                return;
            }

            var parallelizableTasks = _taskBacklog
                .Where(t => t.Status == "Pending" && AreDependenciesMet(t) && t.Complexity != "High")
                .ToList();

            var unassignedCount = parallelizableTasks.Count;

            // Request engineers if there are any non-High pending tasks and no engineers exist
            var engineerCount = _registry.GetAgentsByRole(AgentRole.SeniorEngineer).Count
                             + _registry.GetAgentsByRole(AgentRole.JuniorEngineer).Count;
            var busyEngineers = _agentAssignments.Count;
            var freeEngineers = engineerCount - busyEngineers;

            // Need engineers if there are pending tasks and not enough free engineers
            if (unassignedCount == 0 || unassignedCount <= freeEngineers)
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

            // Create a GitHub issue so PM can discover and act on it
            var justification = $"There are {unassignedCount} parallelizable tasks " +
                                $"({pendingMedium} Medium, {pendingLow} Low) ready for assignment " +
                                $"but only {freeEngineers} available engineers. " +
                                $"Requesting a {neededRole} to increase throughput.";

            await _issueWorkflow.RequestResourceAsync(
                Identity.DisplayName, neededRole, justification, ct);

            // Also broadcast via message bus for immediate notification
            await _messageBus.PublishAsync(new ResourceRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ResourceRequest",
                RequestedRole = neededRole,
                Justification = justification,
                CurrentTeamSize = engineerCount
            }, ct);

            _resourceRequestPending = true;
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
            if (_taskBacklog.Count == 0)
                return;

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

    private Task HandleReviewRequestAsync(ReviewRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Review request from {Agent} for PR #{PrNumber}: {Title} ({ReviewType})",
            message.FromAgentId, message.PrNumber, message.PrTitle, message.ReviewType);

        // Clear reviewed flag so reworked PRs get re-reviewed
        _reviewedPrNumbers.Remove(message.PrNumber);
        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    private Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        // Check if this PR belongs to us by matching backlog tasks
        var isOurPr = _taskBacklog.Any(t =>
            t.PullRequestNumber == message.PrNumber && t.Status == "InProgress" &&
            string.Equals(t.AssignedTo, Identity.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (!isOurPr)
            return Task.CompletedTask;

        Logger.LogInformation(
            "PE received change request from {Reviewer} on own PR #{PrNumber}",
            message.ReviewerAgent, message.PrNumber);

        _reworkQueue.Enqueue(new ReworkItem(message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
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
                "You are a Principal Engineer reviewing a pull request for technical quality.\n\n" +
                "IMPORTANT: This PR is ONE TASK from the Engineering Plan — it is NOT expected to " +
                "implement the entire architecture or PM spec. Review it ONLY against its own stated " +
                "description, acceptance criteria, and the specific task it addresses. Do NOT request " +
                "changes because the PR doesn't cover other tasks or the full system.\n\n" +
                "Evaluate:\n" +
                "1. Code correctness and completeness FOR THIS TASK's scope\n" +
                "2. Architecture alignment for the components this task touches\n" +
                "3. Error handling and edge cases within this task's scope\n" +
                "4. Performance considerations\n" +
                "5. Test coverage for the changes made\n\n" +
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

    /// <summary>
    /// Uses AI to generate a rich PR description with acceptance criteria from project docs.
    /// </summary>
    private async Task<string> GenerateTaskDescriptionAsync(
        EngineeringTask task, CancellationToken ct)
    {
        var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
        var pmSpecDoc = await _projectFiles.GetPMSpecAsync(ct);
        var planDoc = await _projectFiles.GetEngineeringPlanAsync(ct);

        var depsText = task.Dependencies.Count > 0
            ? string.Join(", ", task.Dependencies)
            : "None";

        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a Principal Engineer writing a detailed pull request description for an engineering task. " +
            "Your output will be used as the PR body on GitHub and must give an engineer everything they need " +
            "to implement this task successfully without asking questions.");

        history.AddUserMessage(
            $"## Project Documents\n\n" +
            $"### PM Specification (business goals, user stories, acceptance criteria)\n{pmSpecDoc}\n\n" +
            $"### Architecture Document (technical design, component boundaries)\n{architectureDoc}\n\n" +
            $"### Engineering Plan (full task breakdown)\n{planDoc}\n\n" +
            $"---\n\n" +
            $"## Task to describe\n" +
            $"**ID:** {task.Id}\n" +
            $"**Name:** {task.Name}\n" +
            $"**Complexity:** {task.Complexity}\n" +
            $"**Dependencies:** {depsText}\n\n" +
            "Write a PR description with the following sections:\n\n" +
            "## Overview\nA clear 2-3 sentence summary of what this PR delivers and why it matters.\n\n" +
            "## Detailed Requirements\nSpecific, actionable requirements derived from the PM Spec and Architecture. " +
            "Reference the relevant user stories and architectural components.\n\n" +
            "## Technical Approach\nHow this should be implemented — key files, components, patterns, and integration points.\n\n" +
            "## Acceptance Criteria\nA numbered checklist of specific, testable criteria that must pass for this PR to be complete. " +
            "Each criterion should be clear enough that a reviewer can verify it.\n\n" +
            "## Out of Scope\nExplicitly call out what is NOT part of this task to prevent scope creep.\n\n" +
            "Output ONLY the PR description markdown. Do not wrap in code fences.");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content?.Trim() ?? $"## {task.Name}\n\n{task.Description}";
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

    private void RestoreTaskBacklogFromPlan(string planMarkdown)
    {
        _taskBacklog.Clear();
        var lines = planMarkdown.Split('\n');
        var inTaskTable = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Detect the task table header row
            if (trimmed.StartsWith("| ID") && trimmed.Contains("Task") && trimmed.Contains("Complexity"))
            {
                inTaskTable = true;
                continue;
            }

            // Skip separator row
            if (inTaskTable && trimmed.StartsWith("|--"))
                continue;

            // Parse task rows
            if (inTaskTable && trimmed.StartsWith('|'))
            {
                var cells = trimmed.Split('|', StringSplitOptions.TrimEntries)
                    .Where(c => c.Length > 0).ToArray();

                if (cells.Length >= 7)
                {
                    var deps = cells[6] == "—" || string.IsNullOrWhiteSpace(cells[6])
                        ? new List<string>()
                        : cells[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

                    var prNum = cells[4].StartsWith('#') && int.TryParse(cells[4][1..], out var pr) ? (int?)pr : null;
                    var assignedTo = cells[3] == "—" ? null : cells[3];

                    _taskBacklog.Add(new EngineeringTask
                    {
                        Id = cells[0],
                        Name = cells[1],
                        Complexity = NormalizeComplexity(cells[2]),
                        AssignedTo = assignedTo,
                        PullRequestNumber = prNum,
                        Status = cells[5],
                        Dependencies = deps
                    });
                }
            }
            else if (inTaskTable && !trimmed.StartsWith('|'))
            {
                break; // End of table
            }
        }

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
