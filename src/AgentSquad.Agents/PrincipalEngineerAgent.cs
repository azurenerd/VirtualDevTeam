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

/// <summary>
/// Principal Engineer agent — handles high-complexity tasks and orchestrates the engineering team.
/// Extends <see cref="EngineerAgentBase"/> with planning, issue assignment, PR review,
/// and resource management capabilities.
/// </summary>
public class PrincipalEngineerAgent : EngineerAgentBase
{
    private readonly AgentRegistry _registry;

    private bool _planningComplete;
    private bool _planningSignalReceived;
    private bool _architectureReady;
    private bool _resourceRequestPending;
    private readonly List<EngineeringTask> _taskBacklog = new();
    private readonly Dictionary<string, int> _agentAssignments = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();

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
        : base(identity, messageBus, github, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, config.Value, logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    protected override string GetRoleDisplayName() => "Principal Engineer";

    protected override string GetImplementationSystemPrompt(string techStack) =>
        $"You are a Principal Engineer implementing a high-complexity engineering task. " +
        $"The project uses {techStack} as its technology stack. " +
        "The PM Specification defines the business requirements, and the Architecture " +
        "document defines the technical design. The GitHub Issue contains the User Story " +
        "and acceptance criteria for this specific task. " +
        "Produce detailed, production-quality code. " +
        "Ensure the implementation fulfills the business goals from the PM spec. " +
        "Be thorough — this is the most critical part of the system.";

    protected override string GetReworkSystemPrompt(string techStack) =>
        $"You are a Principal Engineer addressing review feedback on your pull request. " +
        $"The project uses {techStack}. " +
        "You have access to the full architecture, PM spec, and engineering plan. " +
        "Carefully read the feedback, understand what needs to be fixed, and produce " +
        "an updated implementation that addresses ALL the feedback points. " +
        "Be thorough and produce production-quality fixes.";

    protected override async Task<string> GetAdditionalReworkContextAsync(CancellationToken ct)
    {
        var planDoc = await ProjectFiles.GetEngineeringPlanAsync(ct);
        return $"## Engineering Plan\n{planDoc}\n\n";
    }

    #region Lifecycle Overrides

    /// <summary>PE has a custom set of subscriptions for orchestration.</summary>
    protected override void RegisterAdditionalSubscriptions()
    {
        Subscriptions.Add(MessageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        Subscriptions.Add(MessageBus.Subscribe<ReviewRequestMessage>(
            Identity.Id, HandleReviewRequestAsync));

        Subscriptions.Add(MessageBus.Subscribe<PlanningCompleteMessage>(
            Identity.Id, HandlePlanningCompleteAsync));
    }

    /// <summary>
    /// PE has a two-phase loop: Phase 1 waits for architecture + issues,
    /// Phase 2 is continuous orchestration + own task work.
    /// </summary>
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
                    // Priority 2: Assign issues to available engineers
                    await AssignTasksToAvailableEngineersAsync(ct);
                    // Priority 3: Work on our own high-complexity tasks
                    await WorkOnOwnTasksAsync(ct);
                    // Priority 4: Review engineer PRs
                    await ReviewEngineerPRsAsync(ct);
                    // Priority 5: Check if more engineers are needed
                    await EvaluateResourceNeedsAsync(ct);
                    // Persist state
                    await UpdateEngineeringPlanAsync(ct);
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(Config.Limits.GitHubPollIntervalSeconds), ct);
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

    #endregion

    #region Phase 1 — Wait for Architecture and Create Plan

    private async Task<bool> CheckForArchitectureAsync(CancellationToken ct)
    {
        try
        {
            // Path 1: PM sent PlanningComplete AND Architecture.md has real content
            if (_planningSignalReceived)
            {
                var archDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
                if (!archDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                    && archDoc.Length > 200)
                {
                    Logger.LogInformation("Planning complete signal received and Architecture.md ready");
                    return true;
                }
                Logger.LogInformation("Planning signal received but Architecture.md not ready yet, waiting...");
            }

            // Path 2: Architect sent ArchitectureComplete via message bus AND enhancement issues exist
            // BUG FIX: Replaced issue-polling fallback. Previously the Architect created a
            // spurious GitHub Issue to notify PE ("Principal Engineer: Question from Architect").
            // Now uses the _architectureReady flag set by the ArchitectureComplete bus message.
            if (_architectureReady)
            {
                var enhancements = await GitHub.GetIssuesByLabelAsync(
                    IssueWorkflow.Labels.Enhancement, ct);
                if (_planningSignalReceived || enhancements.Count > 0)
                {
                    Logger.LogInformation(
                        "Architecture ready signal received via bus, {Count} enhancement issues found",
                        enhancements.Count);
                    return true;
                }
                Logger.LogInformation("Architecture ready but no enhancement issues yet, waiting for PM...");
            }

            // Path 3: Recovery — Architecture.md exists on disk AND enhancement issues exist
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            if (!architectureDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                && architectureDoc.Length > 200)
            {
                var enhancementIssues = await GitHub.GetIssuesByLabelAsync(
                    IssueWorkflow.Labels.Enhancement, ct);
                if (enhancementIssues.Count > 0)
                {
                    Logger.LogInformation(
                        "Architecture.md found with content and {Count} enhancement issues exist, proceeding",
                        enhancementIssues.Count);
                    return true;
                }
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
        var existingPlan = await ProjectFiles.GetEngineeringPlanAsync(ct);
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

        UpdateStatus(AgentStatus.Working, "Creating engineering plan from Issues");
        Logger.LogInformation("Starting engineering plan creation from Enhancement issues");

        var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
        var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);

        var enhancementIssues = await GitHub.GetIssuesByLabelAsync(
            IssueWorkflow.Labels.Enhancement, ct);

        if (enhancementIssues.Count == 0)
        {
            Logger.LogWarning("No enhancement issues found, cannot create engineering plan");
            return;
        }

        var issuesSummary = string.Join("\n\n", enhancementIssues.Select(i =>
            $"### Issue #{i.Number}: {i.Title}\n{i.Body}"));

        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a Principal Engineer creating an engineering plan from GitHub Issues (User Stories), " +
            "an architecture document, and a PM specification. " +
            "Each GitHub Issue represents a User Story or Feature from the PM Spec.\n\n" +
            "Your job is to:\n" +
            "1. Review each Issue and the architecture/PM spec\n" +
            "2. Map each Issue to one or more engineering tasks\n" +
            "3. Classify each task by complexity (High/Medium/Low)\n" +
            "4. Identify dependencies between tasks\n" +
            "5. Reference the source GitHub Issue number for each task\n\n" +
            "Task complexity mapping:\n" +
            "- **High**: Complex tasks requiring deep expertise → Principal Engineer\n" +
            "- **Medium**: Moderate tasks → Senior Engineers\n" +
            "- **Low**: Straightforward tasks → Junior Engineers");

        history.AddUserMessage(
            $"## PM Specification\n{pmSpec}\n\n" +
            $"## Architecture Document\n{architectureDoc}\n\n" +
            $"## GitHub Issues (User Stories)\n{issuesSummary}\n\n" +
            "Create an engineering plan mapping these Issues to tasks. " +
            "Output ONLY structured lines in this format:\n" +
            "TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>\n\n" +
            "Example:\n" +
            "TASK|T1|42|Set up project structure|Create solution, projects, and folder layout|Low|NONE\n" +
            "TASK|T2|43|Implement auth module|Build JWT authentication with refresh tokens|High|T1\n\n" +
            "Only output TASK lines, nothing else.");

        var response = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var structuredText = response.Content ?? "";

        _taskBacklog.Clear();
        var issueMap = enhancementIssues.ToDictionary(i => i.Number);

        foreach (var line in structuredText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TASK|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7)
                continue;

            var deps = parts[6].Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : parts[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            int.TryParse(parts[2].Trim().TrimStart('#'), out var issueNum);
            var issueUrl = issueMap.TryGetValue(issueNum, out var iss) ? iss.Url : null;

            _taskBacklog.Add(new EngineeringTask
            {
                Id = parts[1].Trim(),
                Name = parts[3].Trim(),
                Description = parts[4].Trim(),
                Complexity = NormalizeComplexity(parts[5].Trim()),
                Dependencies = deps,
                IssueNumber = issueNum > 0 ? issueNum : null,
                IssueUrl = issueUrl
            });
        }

        if (_taskBacklog.Count == 0)
        {
            Logger.LogWarning("No tasks parsed from AI response, creating a fallback task per issue");
            foreach (var issue in enhancementIssues)
            {
                _taskBacklog.Add(new EngineeringTask
                {
                    Id = $"T-{issue.Number}",
                    Name = issue.Title,
                    Description = issue.Body,
                    Complexity = "Medium",
                    IssueNumber = issue.Number,
                    IssueUrl = issue.Url
                });
            }
        }

        Logger.LogInformation("Engineering plan created with {Count} tasks from {IssueCount} issues",
            _taskBacklog.Count, enhancementIssues.Count);

        var planDoc = BuildEngineeringPlanMarkdown();
        await ProjectFiles.UpdateEngineeringPlanAsync(planDoc, ct);

        await MessageBus.PublishAsync(new StatusUpdateMessage
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
            var registeredEngineers = new List<EngineerInfo>();
            // BUG FIX: Collect both AgentId and DisplayName for each engineer.
            // Previously, only DisplayName was stored and used for message routing, but
            // the message bus routes by Identity.Id (e.g., "seniorengineer-abc123"), not
            // DisplayName (e.g., "Senior Engineer 1"). Messages were never delivered.
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                registeredEngineers.Add(new EngineerInfo { AgentId = agent.Identity.Id, Name = agent.Identity.DisplayName, Role = AgentRole.SeniorEngineer });
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                registeredEngineers.Add(new EngineerInfo { AgentId = agent.Identity.Id, Name = agent.Identity.DisplayName, Role = AgentRole.JuniorEngineer });

            foreach (var engineer in registeredEngineers)
            {
                // BUG FIX: Key assignments by agent.AgentId (Identity.Id), not DisplayName.
                // Previously keyed by DisplayName, but StatusUpdate.FromAgentId uses Identity.Id,
                // so completed tasks were never matched and engineers were never freed.
                if (_agentAssignments.ContainsKey(engineer.AgentId))
                {
                    var assignedIssueNum = _agentAssignments[engineer.AgentId];
                    var assignedIssue = await GitHub.GetIssueAsync(assignedIssueNum, ct);
                    if (assignedIssue is not null &&
                        string.Equals(assignedIssue.State, "open", StringComparison.OrdinalIgnoreCase))
                        continue;
                    _agentAssignments.Remove(engineer.AgentId);
                }

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

                if (task.IssueNumber.HasValue)
                {
                    var newTitle = $"{engineer.Name}: {task.Name}";
                    await GitHub.UpdateIssueTitleAsync(task.IssueNumber.Value, newTitle, ct);

                    _agentAssignments[engineer.AgentId] = task.IssueNumber.Value;

                    var taskIndex = _taskBacklog.FindIndex(t => t.Id == task.Id);
                    if (taskIndex >= 0)
                    {
                        _taskBacklog[taskIndex] = task with
                        {
                            Status = "Assigned",
                            AssignedTo = engineer.Name
                        };
                    }

                    Logger.LogInformation(
                        "Assigned issue #{IssueNumber} ({TaskName}) to {Engineer}",
                        task.IssueNumber, task.Name, engineer.Name);

                    // BUG FIX: Route IssueAssignmentMessage to engineer.AgentId (Identity.Id),
                    // NOT engineer.Name (DisplayName). The message bus delivers to mailboxes
                    // keyed by Identity.Id. Using DisplayName caused silent message loss.
                    await MessageBus.PublishAsync(new IssueAssignmentMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = engineer.AgentId,
                        MessageType = "IssueAssignment",
                        IssueNumber = task.IssueNumber.Value,
                        IssueTitle = task.Name,
                        Complexity = task.Complexity,
                        IssueUrl = task.IssueUrl
                    }, ct);
                }
                else
                {
                    Logger.LogWarning("Task {TaskId} has no linked issue, cannot assign", task.Id);
                }
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
            var task = _taskBacklog.FirstOrDefault(t =>
                t.Complexity == "High" && t.Status == "Pending" && AreDependenciesMet(t));

            if (task is null)
                return;

            UpdateStatus(AgentStatus.Working, $"Working on: {task.Name}");
            Logger.LogInformation("Principal Engineer working on task {TaskId}: {TaskName}",
                task.Id, task.Name);

            // Assign Issue to self
            if (task.IssueNumber.HasValue)
            {
                await GitHub.UpdateIssueTitleAsync(
                    task.IssueNumber.Value,
                    $"{Identity.DisplayName}: {task.Name}",
                    ct);
            }

            UpdateStatus(AgentStatus.Working, $"Planning: {task.Name}");
            var prDescription = await GenerateTaskDescriptionAsync(task, ct);

            if (task.IssueNumber.HasValue)
                prDescription = $"Closes #{task.IssueNumber}\n\n{prDescription}";

            var branchName = await PrWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName,
                $"{task.Id}-{task.Name}",
                ct);

            var pr = await PrWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                task.Name,
                prDescription,
                task.Complexity,
                "Architecture.md",
                "EngineeringPlan.md",
                branchName,
                ct);

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

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var techStack = Config.Project.TechStack;

            var issueContext = "";
            if (task.IssueNumber.HasValue)
            {
                var issue = await GitHub.GetIssueAsync(task.IssueNumber.Value, ct);
                if (issue is not null)
                    issueContext = $"\n\n## GitHub Issue #{issue.Number}: {issue.Title}\n{issue.Body}";
            }

            var history = new ChatHistory();
            history.AddSystemMessage(GetImplementationSystemPrompt(techStack));

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}" +
                issueContext +
                $"\n\n## Task: {task.Name}\n{task.Description}\n\n" +
                "Produce a complete implementation for this task. " +
                "Output each file using this exact format:\n\n" +
                "FILE: path/to/file.ext\n```language\n<file content>\n```\n\n" +
                $"Use the {techStack} technology stack. " +
                "Include all source code files, configuration, and tests. " +
                "Every file MUST use the FILE: marker format so it can be parsed and committed.");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var implementation = response.Content?.Trim() ?? "";

            var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);

            if (codeFiles.Count > 0)
            {
                Logger.LogInformation(
                    "Principal Engineer parsed {Count} code files for task {TaskId}",
                    codeFiles.Count, task.Id);

                await PrWorkflow.CommitCodeFilesToPRAsync(
                    pr.Number, codeFiles, $"Implement {task.Name}", ct);
            }
            else
            {
                Logger.LogWarning(
                    "Principal Engineer could not parse structured files for task {TaskId}, committing raw",
                    task.Id);

                await PrWorkflow.CommitFixesToPRAsync(
                    pr.Number,
                    $"src/{task.Id}-implementation.md",
                    $"## Implementation: {task.Name}\n\n{implementation}",
                    $"Add implementation for {task.Name}",
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
            var prNumbersToReview = new HashSet<int>();
            while (_reviewQueue.TryDequeue(out var prNumber))
                prNumbersToReview.Add(prNumber);

            if (prNumbersToReview.Count == 0)
                return;

            foreach (var prNumber in prNumbersToReview)
            {
                if (_reviewedPrNumbers.Contains(prNumber))
                    continue;

                if (!await PrWorkflow.NeedsReviewFromAsync(prNumber, "PrincipalEngineer", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                var pr = await GitHub.GetPullRequestAsync(prNumber, ct);
                if (pr is null)
                    continue;

                // Skip our own PRs
                if (pr.Title.StartsWith(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                Logger.LogInformation("PE reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                // BUG FIX: Force-approve after max rework cycles to prevent infinite loops.
                bool approved;
                string? reviewBody;
                if (_forceApprovalPrs.Contains(prNumber))
                {
                    approved = true;
                    reviewBody = $"Force-approving after maximum rework cycles. " +
                        $"The engineer has made best-effort improvements across multiple iterations.";
                    _forceApprovalPrs.Remove(prNumber);
                }
                else
                {
                    (approved, reviewBody) = await EvaluatePrQualityAsync(pr, ct);
                }

                if (reviewBody is null)
                    continue;

                if (approved)
                {
                    var merged = await PrWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "PrincipalEngineer", reviewBody, ct);
                    if (merged)
                        Logger.LogInformation("PE approved and merged PR #{Number}", pr.Number);
                    else
                        Logger.LogInformation("PE approved PR #{Number}, waiting for PM approval", pr.Number);
                }
                else
                {
                    await PrWorkflow.RequestChangesAsync(pr.Number, "PrincipalEngineer", reviewBody, ct);
                    Logger.LogInformation("PE requested changes on PR #{Number}", pr.Number);

                    await MessageBus.PublishAsync(new ChangesRequestedMessage
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
        while (ReworkQueue.TryDequeue(out var rework))
        {
            await HandleReworkAsync(rework, ct);
        }
    }

    private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
    {
        try
        {
            if (_resourceRequestPending)
            {
                var currentEngineers = _registry.GetAgentsByRole(AgentRole.SeniorEngineer).Count()
                    + _registry.GetAgentsByRole(AgentRole.JuniorEngineer).Count();
                if (currentEngineers > _agentAssignments.Count)
                    _resourceRequestPending = false;
                return;
            }

            var parallelizable = _taskBacklog.Count(t =>
                t.Status == "Pending" && AreDependenciesMet(t) && t.Complexity != "High");

            if (parallelizable < 2)
                return;

            var freeEngineers = 0;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                if (!_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeEngineers++;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                if (!_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeEngineers++;

            if (parallelizable > freeEngineers + 1)
            {
                var neededRole = _taskBacklog.Any(t =>
                    t.Status == "Pending" && t.Complexity == "Low" && AreDependenciesMet(t))
                    ? AgentRole.JuniorEngineer
                    : AgentRole.SeniorEngineer;

                Logger.LogInformation(
                    "PE requesting additional {Role}: {Parallelizable} tasks parallelizable, {Free} engineers free",
                    neededRole, parallelizable, freeEngineers);

                await MessageBus.PublishAsync(new ResourceRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ResourceRequest",
                    RequestedRole = neededRole,
                    Justification = $"{parallelizable} tasks can be worked in parallel but only {freeEngineers} engineers are available",
                    CurrentTeamSize = _agentAssignments.Count + 1
                }, ct);

                _resourceRequestPending = true;
            }
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
            await ProjectFiles.UpdateEngineeringPlanAsync(planDoc, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update engineering plan");
        }
    }

    #endregion

    #region PE-Specific Message Handlers

    private Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Status update from {Agent}: {Status} — {Details}",
            message.FromAgentId, message.NewStatus, message.Details);

        // BUG FIX: Handle ArchitectureComplete message from the Architect agent.
        // Previously the Architect created a spurious GitHub Issue to notify the PE, but
        // the correct path is this bus message. Sets the _architectureReady flag so
        // CheckForArchitectureAsync can proceed without polling for fake issues.
        if (string.Equals(message.MessageType, "ArchitectureComplete", StringComparison.OrdinalIgnoreCase))
        {
            _architectureReady = true;
            Logger.LogInformation("Architecture complete signal received via message bus from {Agent}",
                message.FromAgentId);
            return Task.CompletedTask;
        }

        // BUG FIX: Key _agentAssignments by agent Id (message.FromAgentId) not DisplayName.
        // Also match task by Name (case-insensitive) with Id fallback, because engineers
        // send the issue Title as CurrentTask but the backlog stores it as task Name/Id.
        if (message.MessageType == "TaskComplete"
            && _agentAssignments.ContainsKey(message.FromAgentId))
        {
            _agentAssignments.Remove(message.FromAgentId);

            if (message.CurrentTask is not null)
            {
                var task = _taskBacklog.FirstOrDefault(t =>
                    string.Equals(t.Name, message.CurrentTask, StringComparison.OrdinalIgnoreCase)
                    || t.Id == message.CurrentTask);
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

    protected override Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        if (message.Title.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
            message.Title.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("Ignoring non-engineering task assignment: {Title}", message.Title);
            return Task.CompletedTask;
        }

        Logger.LogInformation(
            "Received task assignment from {From}: {Title}",
            message.FromAgentId, message.Title);

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

        _reviewedPrNumbers.Remove(message.PrNumber);

        // BUG FIX: Track FinalApproval requests so PE auto-approves after max rework cycles.
        if (string.Equals(message.ReviewType, "FinalApproval", StringComparison.OrdinalIgnoreCase))
            _forceApprovalPrs.Add(message.PrNumber);

        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    protected override Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        var isOurPr = _taskBacklog.Any(t =>
            t.PullRequestNumber == message.PrNumber && t.Status == "InProgress" &&
            string.Equals(t.AssignedTo, Identity.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (!isOurPr)
            return Task.CompletedTask;

        Logger.LogInformation(
            "PE received change request from {Reviewer} on own PR #{PrNumber}",
            message.ReviewerAgent, message.PrNumber);

        ReworkQueue.Enqueue(new ReworkItem(message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
        return Task.CompletedTask;
    }

    private Task HandlePlanningCompleteAsync(PlanningCompleteMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Planning complete signal received from {Agent}: {Count} issues created",
            message.FromAgentId, message.IssueCount);
        _planningSignalReceived = true;
        return Task.CompletedTask;
    }

    #endregion

    #region AI-Assisted Methods

    private async Task<(bool Approved, string? ReviewBody)> EvaluatePrQualityAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var engineeringPlan = await ProjectFiles.GetEngineeringPlanAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer reviewing a pull request for technical quality " +
                "and alignment with the architecture and engineering plan.\n\n" +
                "IMPORTANT: This PR is ONE TASK — it is NOT expected to cover the entire project. " +
                "Review it ONLY against its own stated description and acceptance criteria.\n\n" +
                "Evaluate:\n" +
                "1. Does the code follow the architecture patterns?\n" +
                "2. Is the implementation complete for this task's scope?\n" +
                "3. Code quality, error handling, edge cases\n" +
                "4. Test coverage\n\n" +
                "End your review with exactly one of:\n" +
                "VERDICT: APPROVE\n" +
                "VERDICT: REQUEST_CHANGES");

            history.AddUserMessage(
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Engineering Plan\n{engineeringPlan}\n\n" +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";
            var approved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

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
            t.Complexity == targetComplexity && t.Status == "Pending" && AreDependenciesMet(t));
    }

    private bool AreDependenciesMet(EngineeringTask task)
    {
        if (task.Dependencies.Count == 0)
            return true;

        return task.Dependencies.All(depId =>
        {
            var dep = _taskBacklog.FirstOrDefault(t => t.Id == depId);
            return dep is null || dep.Status == "Complete";
        });
    }

    private async Task<string> GenerateTaskDescriptionAsync(
        EngineeringTask task, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);

            var issueContext = "";
            if (task.IssueNumber.HasValue)
            {
                var issue = await GitHub.GetIssueAsync(task.IssueNumber.Value, ct);
                if (issue is not null)
                    issueContext = $"\n\n## Source Issue #{issue.Number}\n{issue.Body}";
            }

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer writing a detailed PR description for an engineering task. " +
                "The description should be clear enough for another engineer to implement the task. " +
                "Include: summary, acceptance criteria, implementation notes, and testing approach.");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Architecture\n{architectureDoc}" +
                issueContext +
                $"\n\n## Task: {task.Name}\n{task.Description}\n\n" +
                "Write a detailed PR description with:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Notes**: Key design decisions and approach\n" +
                "4. **Testing**: What tests should cover");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            return response.Content?.Trim() ?? task.Description;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to generate task description with AI, using raw description");
            return task.Description;
        }
    }

    private static string NormalizeComplexity(string complexity)
    {
        return complexity.ToLowerInvariant() switch
        {
            "high" or "complex" or "hard" => "High",
            "medium" or "moderate" or "mid" => "Medium",
            "low" or "simple" or "easy" => "Low",
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

            if (trimmed.StartsWith("| ID") || trimmed.StartsWith("|ID"))
            {
                inTaskTable = true;
                continue;
            }

            if (inTaskTable && trimmed.StartsWith("|---"))
                continue;

            if (inTaskTable && trimmed.StartsWith('|'))
            {
                var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .ToArray();

                if (cells.Length >= 7)
                {
                    var deps = cells[7 < cells.Length ? 7 : cells.Length - 1] == "—"
                        ? new List<string>()
                        : cells[cells.Length - 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

                    var prNum = cells.Length > 5 && cells[5].StartsWith('#') && int.TryParse(cells[5][1..], out var pr) ? (int?)pr : null;
                    var issueNum = cells.Length > 4 && cells[4].StartsWith('#') && int.TryParse(cells[4][1..], out var iss) ? (int?)iss : null;
                    var assignedTo = cells[3] == "—" ? null : cells[3];

                    _taskBacklog.Add(new EngineeringTask
                    {
                        Id = cells[0],
                        Name = cells[1],
                        Complexity = NormalizeComplexity(cells[2]),
                        AssignedTo = assignedTo,
                        IssueNumber = issueNum,
                        PullRequestNumber = prNum,
                        Status = cells.Length > 6 ? cells[6] : "Pending",
                        Dependencies = deps
                    });
                }
            }
            else if (inTaskTable && !trimmed.StartsWith('|'))
            {
                break;
            }
        }
    }

    private string BuildEngineeringPlanMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Engineering Plan");
        sb.AppendLine();

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

        sb.AppendLine("## Tasks");
        sb.AppendLine();
        sb.AppendLine("| ID | Task | Complexity | Assigned To | Issue | PR | Status | Dependencies |");
        sb.AppendLine("|----|------|-----------|-------------|-------|-----|--------|-------------|");

        foreach (var task in _taskBacklog)
        {
            var assignedTo = task.AssignedTo ?? "—";
            var issueLink = task.IssueNumber.HasValue ? $"#{task.IssueNumber}" : "—";
            var prLink = task.PullRequestNumber.HasValue ? $"#{task.PullRequestNumber}" : "—";
            var deps = task.Dependencies.Count > 0
                ? string.Join(", ", task.Dependencies)
                : "—";

            sb.AppendLine($"| {task.Id} | {task.Name} | {task.Complexity} | " +
                          $"{assignedTo} | {issueLink} | {prLink} | {task.Status} | {deps} |");
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
    public int? IssueNumber { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
}

// BUG FIX: Added AgentId field. Previously only Name (DisplayName) was stored, but
// all message routing and _agentAssignments must use Identity.Id for correct delivery.
internal record EngineerInfo
{
    public string AgentId { get; init; } = "";
    public string Name { get; init; } = "";
    public AgentRole Role { get; init; }
}
