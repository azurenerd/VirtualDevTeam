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
    private bool _recoveredReviewPRs;
    private DateTime _lastResourceRequestTime = DateTime.MinValue;
    private static readonly TimeSpan SpawnCooldown = TimeSpan.FromSeconds(45);
    private bool _allTasksComplete;
    private bool _integrationPrCreated;
    private readonly List<EngineeringTask> _taskBacklog = new();
    private readonly Dictionary<string, int> _agentAssignments = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();
    private readonly Dictionary<int, int> _conflictRetryCount = new();

    public PrincipalEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentRegistry registry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        ILogger<PrincipalEngineerAgent> logger)
        : base(identity, messageBus, github, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, logger)
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
                    // Recovery: re-track and re-broadcast review for our own ready-for-review PRs
                    await RecoverReadyForReviewPRsAsync(ct);
                    // Check if our tracked PR has been merged/closed
                    await CheckOwnPrStatusAsync(ct);
                    // Recovery: finish stuck in-progress PRs that were never marked ready
                    await RecoverStuckInProgressPRAsync(ct);
                    // Priority 1: Process rework feedback on our own PRs
                    await ProcessOwnReworkAsync(ct);

                    // Check if all tasks are complete → integration phase
                    if (!_allTasksComplete)
                    {
                        await CheckAllTasksCompleteAsync(ct);
                    }

                    if (_allTasksComplete)
                    {
                        // Integration phase: create integration PR if needed
                        if (!_integrationPrCreated)
                        {
                            await CreateIntegrationPRAsync(ct);
                        }
                    }
                    else
                    {
                        // Priority 2: Assign issues to available engineers
                        await AssignTasksToAvailableEngineersAsync(ct);
                        // Priority 3: Work on our own tasks (any complexity)
                        await WorkOnOwnTasksAsync(ct);
                        // Priority 4: Review engineer PRs
                        await ReviewEngineerPRsAsync(ct);
                        // Priority 5: Check if more engineers are needed
                        await EvaluateResourceNeedsAsync(ct);
                    }

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

            // Path 3: Recovery — Architecture.md exists on disk with real content AND enhancement issues exist
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            if (!architectureDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                && architectureDoc.Length > 200
                && architectureDoc.Contains("## System Components", StringComparison.OrdinalIgnoreCase))
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

                // Reconcile task statuses against actual GitHub PR state
                await ReconcileTaskStatusesAsync(ct);

                _planningComplete = true;
                UpdateStatus(AgentStatus.Working, $"Recovered {_taskBacklog.Count} tasks from engineering plan");
                return;
            }
            Logger.LogWarning("Existing EngineeringPlan.md has no tasks — regenerating");
        }

        UpdateStatus(AgentStatus.Working, "Creating engineering plan from Issues");
        Logger.LogInformation("Starting engineering plan creation from Enhancement issues");
        LogActivity("task", "📋 Starting engineering plan creation from Enhancement issues");

        var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
        var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);

        var enhancementIssues = await GitHub.GetIssuesByLabelAsync(
            IssueWorkflow.Labels.Enhancement, ct);

        if (enhancementIssues.Count == 0)
        {
            Logger.LogWarning("No open enhancement issues found — PM may not have created them yet, will retry");
            UpdateStatus(AgentStatus.Idle, "Waiting for PM to create User Story Issues");
            _planningComplete = false; // Force re-check on next loop
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
        LogActivity("task", $"📋 Engineering plan created: {_taskBacklog.Count} tasks from {enhancementIssues.Count} issues");

        var taskSummary = string.Join(", ", _taskBacklog.Select(t => $"{t.Id}:{t.Name}({t.Complexity})"));
        await RememberAsync(MemoryType.Decision,
            $"Created engineering plan with {_taskBacklog.Count} tasks from {enhancementIssues.Count} issues",
            $"Tasks: {TruncateForMemory(taskSummary)}", ct);

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

                var complexityPreferences = engineer.Role switch
                {
                    AgentRole.SeniorEngineer => new[] { "Medium", "High", "Low" },
                    AgentRole.JuniorEngineer => new[] { "Low", "Medium" },
                    _ => Array.Empty<string>()
                };

                if (complexityPreferences.Length == 0)
                    continue;

                var task = FindNextAssignableTask(complexityPreferences);
                if (task is null)
                    continue;

                // Ensure the task has an open issue (create if missing or closed)
                var updated = await EnsureTaskHasOpenIssueAsync(task, ct);
                if (updated is null)
                    continue;
                task = updated;

                // At this point, task is guaranteed to have an IssueNumber (either original or auto-created)
                var newTitle = $"{engineer.Name}: {task.Name}";
                await GitHub.UpdateIssueTitleAsync(task.IssueNumber!.Value, newTitle, ct);

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
            // Don't start a new task if PE already has a PR in progress
            if (CurrentPrNumber is not null)
                return;

            // PE prefers High complexity but falls through to any available work
            var task = FindNextAssignableTask("High", "Medium", "Low");

            if (task is null)
            {
                var pending = _taskBacklog.Count(t => t.Status == "Pending");
                var blocked = _taskBacklog.Count(t => t.Status == "Pending" && !AreDependenciesMet(t));
                if (pending > 0)
                    Logger.LogDebug("No assignable tasks: {Pending} pending, {Blocked} blocked by dependencies", pending, blocked);
                return;
            }

            // Guard: don't grab non-High tasks if we recently requested an engineer spawn.
            // Give new engineers time to spin up and claim work before PE takes it.
            if (!string.Equals(task.Complexity, "High", StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _lastResourceRequestTime < SpawnCooldown)
            {
                // Check if there are free engineers who could take this instead
                var freeEngineers = _registry.GetAgentsByRole(AgentRole.SeniorEngineer)
                    .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                    .Count(a => !_agentAssignments.ContainsKey(a.Identity.Id));

                if (freeEngineers > 0)
                {
                    Logger.LogInformation(
                        "PE deferring {Complexity} task {TaskId} — {FreeEngineers} engineer(s) available, " +
                        "spawn cooldown active ({Remaining:F0}s remaining)",
                        task.Complexity, task.Id, freeEngineers,
                        (SpawnCooldown - (DateTime.UtcNow - _lastResourceRequestTime)).TotalSeconds);
                    return;
                }
                // No free engineers yet but cooldown active — still wait
                Logger.LogDebug(
                    "PE waiting for spawned engineer before taking {Complexity} task {TaskId}",
                    task.Complexity, task.Id);
                return;
            }

            // Ensure the task has an open issue (create if missing or closed)
            var updated = await EnsureTaskHasOpenIssueAsync(task, ct);
            if (updated is not null)
                task = updated;

            UpdateStatus(AgentStatus.Working, $"Working on: {task.Name}");
            Logger.LogInformation("Principal Engineer working on task {TaskId}: {TaskName}",
                task.Id, task.Name);
            LogActivity("task", $"🔨 Working on task {task.Id}: {task.Name}");

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

            // Track this PR so PE doesn't start another task concurrently
            CurrentPrNumber = pr.Number;
            Identity.AssignedPullRequest = pr.Number.ToString();

            // Bind CLI session to this PR for conversational continuity
            ActivatePrSession(pr.Number);

            Logger.LogInformation(
                "Principal Engineer created PR #{PrNumber} for task {TaskId}, starting implementation",
                pr.Number, task.Id);

            // Use incremental step-by-step implementation (same pattern as EngineerAgentBase)
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var techStack = Config.Project.TechStack;

            // Build a synthetic issue for the step generation
            AgentIssue? sourceIssue = null;
            if (task.IssueNumber.HasValue)
                sourceIssue = await GitHub.GetIssueAsync(task.IssueNumber.Value, ct);

            var syntheticIssue = sourceIssue ?? new AgentIssue
            {
                Number = task.IssueNumber ?? 0,
                Title = task.Name,
                Body = task.Description,
                State = "open",
                Labels = new List<string>()
            };

            // Generate implementation steps
            var steps = await GenerateImplementationStepsAsync(
                chat, pr, syntheticIssue, pmSpecDoc, architectureDoc, techStack, ct);

            if (steps.Count > 0)
            {
                Logger.LogInformation(
                    "Principal Engineer generated {Count} implementation steps for task {TaskId}",
                    steps.Count, task.Id);

                var completedSteps = new List<string>();
                for (var i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = steps[i];
                    var stepNumber = i + 1;

                    UpdateStatus(AgentStatus.Working,
                        $"PR #{pr.Number} step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");
                    Logger.LogInformation(
                        "PE implementing step {Step}/{Total} for task {TaskId}: {Desc}",
                        stepNumber, steps.Count, task.Id, Truncate(step, 100));

                    var stepHistory = new ChatHistory();
                    stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

                    var ctx = new System.Text.StringBuilder();
                    ctx.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                    ctx.AppendLine($"## Architecture\n{architectureDoc}\n");
                    if (sourceIssue is not null)
                        ctx.AppendLine($"## Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}\n");
                    ctx.AppendLine($"## Task: {task.Name}\n{task.Description}\n");
                    ctx.AppendLine($"## PR Description\n{pr.Body}\n");

                    if (completedSteps.Count > 0)
                    {
                        ctx.AppendLine("## Previously Completed Steps");
                        for (var j = 0; j < completedSteps.Count; j++)
                            ctx.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                        ctx.AppendLine();
                        var existingFiles = await GetPrFileListAsync(pr.Number, ct);
                        if (!string.IsNullOrEmpty(existingFiles))
                            ctx.AppendLine($"## Files already in this PR\n{existingFiles}\n");
                    }

                    ctx.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                    ctx.AppendLine(step);
                    ctx.AppendLine();
                    ctx.AppendLine("Implement ONLY this step. Output each file using this format:\n");
                    ctx.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
                    ctx.AppendLine($"Use the {techStack} technology stack. Every file MUST use the FILE: marker format.");
                    if (completedSteps.Count > 0)
                        ctx.AppendLine("If updating a file from a previous step, include the COMPLETE updated file content.");

                    stepHistory.AddUserMessage(ctx.ToString());

                    var stepResponse = await chat.GetChatMessageContentAsync(stepHistory, cancellationToken: ct);
                    var stepImpl = stepResponse.Content?.Trim() ?? "";

                    var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(stepImpl);
                    if (codeFiles.Count > 0)
                    {
                        await PrWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, $"Step {stepNumber}/{steps.Count}: {Truncate(step, 72)}", ct);
                        Logger.LogInformation(
                            "PE committed {FileCount} files for step {Step}/{Total} on PR #{PrNumber}",
                            codeFiles.Count, stepNumber, steps.Count, pr.Number);
                    }

                    completedSteps.Add(step);
                }
            }
            else
            {
                // Fallback: single-pass implementation
                Logger.LogWarning("PE could not generate steps for task {TaskId}, using single-pass", task.Id);

                var history = new ChatHistory();
                history.AddSystemMessage(GetImplementationSystemPrompt(techStack));
                var issueContext = sourceIssue is not null
                    ? $"\n\n## GitHub Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}"
                    : "";

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

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var implementation = response.Content?.Trim() ?? "";

                var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(implementation);
                if (codeFiles.Count > 0)
                    await PrWorkflow.CommitCodeFilesToPRAsync(pr.Number, codeFiles, $"Implement {task.Name}", ct);
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

                // Skip NeedsReviewFromAsync for force-approval — there's no new rework,
                // but we need to approve to unblock the engineer.
                if (!_forceApprovalPrs.Contains(prNumber)
                    && !await PrWorkflow.NeedsReviewFromAsync(prNumber, "PrincipalEngineer", ct))
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
                    var result = await PrWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "PrincipalEngineer", reviewBody, ct);
                    if (result == MergeAttemptResult.Merged)
                    {
                        Logger.LogInformation("PE approved and merged PR #{Number}", pr.Number);
                        LogActivity("task", $"✅ Approved and merged PR #{pr.Number}: {pr.Title}");

                        await RememberAsync(MemoryType.Action,
                            $"Reviewed and approved+merged PR #{pr.Number}: {pr.Title}", ct: ct);
                    }
                    else if (result == MergeAttemptResult.ConflictBlocked)
                    {
                        Logger.LogWarning("PE approved PR #{Number} but merge blocked by conflicts, attempting close-and-recreate", pr.Number);
                        LogActivity("task", $"⚠️ PR #{pr.Number} blocked by merge conflicts — closing and recreating");
                        await TryCloseAndRecreatePRAsync(pr, ct);
                    }
                    else
                    {
                        Logger.LogInformation("PE approved PR #{Number}, waiting for PM approval", pr.Number);
                        LogActivity("task", $"✅ Approved PR #{pr.Number}, waiting for PM approval");
                    }
                }
                else
                {
                    await PrWorkflow.RequestChangesAsync(pr.Number, "PrincipalEngineer", reviewBody, ct);
                    Logger.LogInformation("PE requested changes on PR #{Number}", pr.Number);
                    LogActivity("task", $"❌ Requested changes on PR #{pr.Number}: {pr.Title}");

                    await RememberAsync(MemoryType.Decision,
                        $"Requested changes on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

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
        // Drain and batch rework items per PR (same logic as base class loop)
        var batches = new Dictionary<int, List<ReworkItem>>();
        while (ReworkQueue.TryDequeue(out var rework))
        {
            if (!batches.TryGetValue(rework.PrNumber, out var list))
            {
                list = new List<ReworkItem>();
                batches[rework.PrNumber] = list;
            }
            list.Add(rework);
        }
        foreach (var batch in batches.Values)
        {
            await HandleReworkAsync(batch, ct);
        }
    }

    /// <summary>
    /// On restart, check for our own PRs that are ready-for-review.
    /// Instead of blindly re-broadcasting, check PR comments for unaddressed feedback:
    /// - If CHANGES_REQUESTED exists → populate ReworkQueue directly
    /// - If all required reviewers approved → attempt merge
    /// - If no reviews yet → re-broadcast ReviewRequestMessage
    /// </summary>
    private async Task RecoverReadyForReviewPRsAsync(CancellationToken ct)
    {
        if (_recoveredReviewPRs)
            return;
        _recoveredReviewPRs = true;

        try
        {
            var myPRs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase)
                    || !pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;

                // Track this PR
                CurrentPrNumber = pr.Number;
                Identity.AssignedPullRequest = pr.Number.ToString();

                // Check for unaddressed CHANGES_REQUESTED feedback on GitHub
                var pendingFeedback = await PrWorkflow.GetPendingChangesRequestedAsync(pr.Number, ct);
                if (pendingFeedback is { } pending)
                {
                    // Populate rework queue directly — no need to re-broadcast
                    ReworkQueue.Enqueue(new ReworkItem(pr.Number, pr.Title, pending.Feedback, pending.Reviewer));
                    Logger.LogInformation(
                        "PE recovered unaddressed feedback on PR #{PrNumber} from {Reviewer}",
                        pr.Number, pending.Reviewer);
                    UpdateStatus(AgentStatus.Working, $"Processing recovered feedback on PR #{pr.Number}");
                    continue;
                }

                // No unaddressed changes — check if all reviewers approved (maybe we can merge)
                var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                var required = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                var approved = await PrWorkflow.GetApprovedReviewersAsync(pr.Number, ct);

                if (required.All(r => approved.Contains(r, StringComparer.OrdinalIgnoreCase)))
                {
                    // All approved — try merge (with branch sync fallback)
                    Logger.LogInformation("PE PR #{PrNumber} has all approvals, merging", pr.Number);
                    try
                    {
                        await GitHub.MergePullRequestAsync(pr.Number,
                            $"Merged after dual approval from {string.Join(" and ", approved)}", ct);
                    }
                    catch (Octokit.PullRequestNotMergeableException)
                    {
                        Logger.LogWarning("PE PR #{PrNumber} not mergeable, syncing branch with main", pr.Number);
                        var synced = await GitHub.UpdatePullRequestBranchAsync(pr.Number, ct);
                        if (synced)
                        {
                            await Task.Delay(5000, ct);
                            try
                            {
                                await GitHub.MergePullRequestAsync(pr.Number,
                                    $"Merged after branch sync and dual approval from {string.Join(" and ", approved)}", ct);
                            }
                            catch (Exception retryEx)
                            {
                                Logger.LogWarning(retryEx, "PE PR #{PrNumber} still not mergeable after sync", pr.Number);
                                await TryCloseAndRecreatePRAsync(pr, ct);
                                continue;
                            }
                        }
                        else
                        {
                            Logger.LogWarning("PE PR #{PrNumber} has real merge conflicts, attempting close-and-recreate", pr.Number);
                            await TryCloseAndRecreatePRAsync(pr, ct);
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(pr.HeadBranch))
                        await GitHub.DeleteBranchAsync(pr.HeadBranch, ct);

                    var taskIdx = _taskBacklog.FindIndex(t => t.PullRequestNumber == pr.Number);
                    if (taskIdx >= 0)
                        _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with { Status = "Done" };

                    CurrentPrNumber = null;
                    Identity.AssignedPullRequest = null;
                    UpdateStatus(AgentStatus.Idle, "Ready for next task");
                    continue;
                }

                // Partial or no reviews — re-broadcast for missing reviewers
                await MessageBus.PublishAsync(new ReviewRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ReviewRequest",
                    PrNumber = pr.Number,
                    PrTitle = pr.Title,
                    ReviewType = "Recovery"
                }, ct);

                Logger.LogInformation("PE re-broadcast review request for own PR #{PrNumber}: {Title}",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Idle, $"PR #{pr.Number} awaiting review");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover ready-for-review PRs");
        }
    }

    /// <summary>
    /// Detect and recover from the case where WorkOnOwnTasksAsync created a PR and
    /// committed code but failed before calling MarkReadyForReviewAsync. The task is
    /// InProgress, the PR is open with "in-progress" label, but no one is reviewing it.
    /// Fix: mark it ready-for-review and broadcast the review request.
    /// </summary>
    private async Task RecoverStuckInProgressPRAsync(CancellationToken ct)
    {
        try
        {
            // Only act if we have a tracked PR
            if (CurrentPrNumber is null)
                return;

            var pr = await GitHub.GetPullRequestAsync(CurrentPrNumber.Value, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                return;

            // Only recover in-progress PRs (not already ready-for-review)
            if (pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                return;

            // Must have at least some code committed (updated after creation)
            if (pr.UpdatedAt is null || pr.UpdatedAt <= pr.CreatedAt.AddMinutes(1))
                return;

            Logger.LogInformation(
                "PE recovering stuck in-progress PR #{PrNumber} — marking ready for review",
                pr.Number);
            LogActivity("system", $"🔄 Recovering stuck PR #{pr.Number} — marking ready for review");

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

            UpdateStatus(AgentStatus.Idle, $"PR #{pr.Number} ready for review (recovered)");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover stuck in-progress PR");
        }
    }

    /// <summary>
    /// Check if our currently tracked PR has been merged or closed, and clear state so
    /// the PE can move on to the next task.
    /// </summary>
    private async Task CheckOwnPrStatusAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is null)
            return;

        try
        {
            var pr = await GitHub.GetPullRequestAsync(CurrentPrNumber.Value, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                var wasMerged = pr?.IsMerged == true;
                Logger.LogInformation("PE own PR #{PrNumber} is no longer open ({State}, merged={Merged}), clearing tracking",
                    CurrentPrNumber.Value, pr?.State ?? "unknown", wasMerged);

                // Mark the backlog task as Done if merged, or reset to Pending if closed without merge
                var taskIdx = _taskBacklog.FindIndex(t => t.PullRequestNumber == CurrentPrNumber.Value);
                if (taskIdx >= 0)
                {
                    if (wasMerged)
                    {
                        _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with { Status = "Done" };
                        Logger.LogInformation("PE task {TaskId} marked Done (PR #{PrNumber} merged)",
                            _taskBacklog[taskIdx].Id, CurrentPrNumber.Value);
                        LogActivity("task", $"✅ Task {_taskBacklog[taskIdx].Id}: {_taskBacklog[taskIdx].Name} completed (PR #{CurrentPrNumber.Value} merged)");
                    }
                    else
                    {
                        _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with { Status = "Pending", PullRequestNumber = null };
                        Logger.LogInformation("PE task {TaskId} reset to Pending (PR #{PrNumber} closed without merge)",
                            _taskBacklog[taskIdx].Id, CurrentPrNumber.Value);
                    }
                }

                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;

                // If the integration PR was merged, signal engineering complete
                if (wasMerged && _allTasksComplete && _integrationPrCreated)
                {
                    await SignalEngineeringCompleteAsync(ct);
                }
                else
                {
                    UpdateStatus(AgentStatus.Idle, "Ready for next task");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check own PR #{PrNumber} status", CurrentPrNumber);
        }
    }

    /// <summary>
    /// After restoring the task backlog from the engineering plan, reconcile task statuses
    /// against actual GitHub PR state. InProgress tasks whose PRs are merged → Done.
    /// InProgress tasks with no PR → reset to Pending.
    /// </summary>
    private async Task ReconcileTaskStatusesAsync(CancellationToken ct)
    {
        try
        {
            // Get all PRs (open + merged) to reconcile
            var openPrs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            var mergedPrs = await GitHub.GetMergedPullRequestsAsync(ct);
            var myMergedPrs = mergedPrs
                .Where(pr => pr.Title.StartsWith(Identity.DisplayName + ":", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var mergedPrTitles = myMergedPrs
                .Select(pr => PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var mergedPrNumbers = myMergedPrs.Select(pr => pr.Number).ToHashSet();
            var openPrNumbers = openPrs.Select(pr => pr.Number).ToHashSet();

            var reconciled = 0;
            for (var i = 0; i < _taskBacklog.Count; i++)
            {
                var task = _taskBacklog[i];

                // Handle "Assigned" tasks whose engineers haven't started (no PR yet)
                // Reset to Pending so the PE can re-assign or self-assign
                if (string.Equals(task.Status, "Assigned", StringComparison.OrdinalIgnoreCase)
                    && !task.PullRequestNumber.HasValue)
                {
                    _taskBacklog[i] = task with { Status = "Pending", AssignedTo = null };
                    Logger.LogInformation("Reconciled task {TaskId} '{TaskName}' to Pending (was Assigned, no PR)",
                        task.Id, task.Name);
                    reconciled++;
                    continue;
                }

                if (!string.Equals(task.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Match by PR number or by task name in PR title
                var isMerged = (task.PullRequestNumber.HasValue && mergedPrNumbers.Contains(task.PullRequestNumber.Value))
                    || mergedPrTitles.Contains(task.Name);

                if (isMerged)
                {
                    _taskBacklog[i] = task with { Status = "Done" };
                    Logger.LogInformation("Reconciled task {TaskId} '{TaskName}' to Done (PR merged)",
                        task.Id, task.Name);
                    reconciled++;
                }
                // PR number set but PR is closed (not merged) and not open — reset to Pending
                else if (task.PullRequestNumber.HasValue
                    && !openPrNumbers.Contains(task.PullRequestNumber.Value)
                    && !mergedPrNumbers.Contains(task.PullRequestNumber.Value))
                {
                    _taskBacklog[i] = task with { Status = "Pending", PullRequestNumber = null, AssignedTo = null };
                    Logger.LogInformation(
                        "Reconciled task {TaskId} '{TaskName}' to Pending (PR #{PrNumber} closed without merge)",
                        task.Id, task.Name, task.PullRequestNumber);
                    reconciled++;
                }
                else if (!task.PullRequestNumber.HasValue
                    && !openPrs.Any(pr => pr.Title.Contains(task.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _taskBacklog[i] = task with { Status = "Pending" };
                    Logger.LogInformation("Reconciled task {TaskId} '{TaskName}' to Pending (no PR found)", task.Id, task.Name);
                    reconciled++;
                }
            }

            if (reconciled > 0)
            {
                Logger.LogInformation("Reconciled {Count} task statuses against GitHub PR state", reconciled);
                LogActivity("system", $"🔄 Reconciled {reconciled} task statuses from GitHub");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reconcile task statuses against GitHub");
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
                _lastResourceRequestTime = DateTime.UtcNow;
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

    /// <summary>
    /// Close a PR that has unresolvable merge conflicts and reset the associated task
    /// so it can be re-implemented from a clean branch off latest main.
    /// Max 1 retry per task to prevent infinite close-and-recreate loops.
    /// </summary>
    private async Task TryCloseAndRecreatePRAsync(AgentPullRequest pr, CancellationToken ct)
    {
        const int MaxConflictRetries = 1;

        _conflictRetryCount.TryGetValue(pr.Number, out var retries);
        if (retries >= MaxConflictRetries)
        {
            Logger.LogWarning(
                "PR #{PrNumber} already retried {Retries} time(s) for conflicts — giving up",
                pr.Number, retries);
            await GitHub.AddPullRequestCommentAsync(pr.Number,
                $"⛔ **Permanently blocked** — This PR has been closed and recreated {retries} time(s) " +
                $"but continues to hit merge conflicts. Requires manual intervention.", ct);
            return;
        }

        try
        {
            // Find the associated task in the backlog
            var taskIdx = _taskBacklog.FindIndex(t => t.PullRequestNumber == pr.Number);

            // Fallback: engineer PRs don't have PullRequestNumber tracked in backlog.
            // Search by issue number from _agentAssignments or by task name in PR title.
            if (taskIdx < 0)
            {
                var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                if (!string.IsNullOrEmpty(taskTitle))
                {
                    taskIdx = _taskBacklog.FindIndex(t =>
                        t.Name.Contains(taskTitle, StringComparison.OrdinalIgnoreCase)
                        || taskTitle.Contains(t.Name, StringComparison.OrdinalIgnoreCase));
                }
            }
            if (taskIdx < 0)
            {
                // Search by issue number from agent assignments
                var issueNum = _agentAssignments.Values
                    .FirstOrDefault(v => _taskBacklog.Any(t => t.IssueNumber == v
                        && pr.Title.Contains(t.Name, StringComparison.OrdinalIgnoreCase)));
                if (issueNum > 0)
                    taskIdx = _taskBacklog.FindIndex(t => t.IssueNumber == issueNum);
            }
            var task = taskIdx >= 0 ? _taskBacklog[taskIdx] : null;

            // Close the conflicted PR with an explanation
            var closeComment =
                $"🔄 **Closing due to unresolvable merge conflicts.**\n\n" +
                $"This PR's branch has conflicts with `main` that cannot be auto-resolved. " +
                $"The task will be re-implemented on a fresh branch from latest `main`.";

            await GitHub.AddPullRequestCommentAsync(pr.Number, closeComment, ct);
            await GitHub.ClosePullRequestAsync(pr.Number, ct);

            Logger.LogInformation(
                "Closed conflicted PR #{PrNumber} ({Title}), will recreate from clean main",
                pr.Number, pr.Title);
            LogActivity("task", $"🔄 Closed conflicted PR #{pr.Number} — will recreate from clean branch");

            // Delete the old branch to avoid naming conflicts
            if (!string.IsNullOrEmpty(pr.HeadBranch))
            {
                try { await GitHub.DeleteBranchAsync(pr.HeadBranch, ct); }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not delete old branch {Branch}", pr.HeadBranch);
                }
            }

            // Track the retry count under the task (keyed by original PR number)
            _conflictRetryCount[pr.Number] = retries + 1;

            // Reset PE tracking state
            if (CurrentPrNumber == pr.Number)
            {
                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;
            }

            if (task is null)
            {
                Logger.LogWarning("No task found for conflicted PR #{PrNumber} — cannot recreate", pr.Number);
                return;
            }

            // Determine who owned the PR to decide how to reassign
            var isPeOwned = pr.Title.StartsWith(Identity.DisplayName + ":", StringComparison.OrdinalIgnoreCase);

            // Reset task to Pending so it gets picked up again with a fresh branch
            _taskBacklog[taskIdx] = task with
            {
                Status = "Pending",
                PullRequestNumber = null,
                AssignedTo = isPeOwned ? null : task.AssignedTo
            };

            if (isPeOwned)
            {
                // PE-owned: it will be picked up by WorkOnOwnTasksAsync on the next cycle
                Logger.LogInformation(
                    "PE task {TaskId} reset to Pending — will re-implement on next cycle", task.Id);
                UpdateStatus(AgentStatus.Idle, "Ready for next task");
            }
            else if (task.AssignedTo is not null && task.IssueNumber.HasValue)
            {
                // Engineer-owned: find the engineer and re-send the assignment
                var engineerAgentId = _agentAssignments
                    .FirstOrDefault(kv => kv.Value == task.IssueNumber.Value).Key;

                if (engineerAgentId is not null)
                {
                    // Remove old assignment so the engineer starts fresh
                    _agentAssignments.Remove(engineerAgentId);

                    // Re-assign: update the issue title back and send new assignment
                    var engineer = _registry.GetAgentsByRole(AgentRole.SeniorEngineer)
                        .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                        .FirstOrDefault(a => a.Identity.Id == engineerAgentId);

                    if (engineer is not null)
                    {
                        _agentAssignments[engineerAgentId] = task.IssueNumber.Value;
                        _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with
                        {
                            Status = "Assigned",
                            AssignedTo = engineer.Identity.DisplayName
                        };

                        await MessageBus.PublishAsync(new IssueAssignmentMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = engineerAgentId,
                            MessageType = "IssueAssignment",
                            IssueNumber = task.IssueNumber.Value,
                            IssueTitle = task.Name,
                            Complexity = task.Complexity,
                            IssueUrl = task.IssueUrl
                        }, ct);

                        Logger.LogInformation(
                            "Re-assigned task {TaskId} (issue #{IssueNumber}) to {Engineer} after conflict recovery",
                            task.Id, task.IssueNumber, engineer.Identity.DisplayName);
                    }
                    else
                    {
                        // Engineer not found — leave as Pending for PE to pick up
                        Logger.LogWarning(
                            "Original engineer {AgentId} not found for task {TaskId} — will be reassigned",
                            engineerAgentId, task.Id);
                        _agentAssignments.Remove(engineerAgentId);
                        _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with
                        {
                            Status = "Pending",
                            AssignedTo = null
                        };
                    }
                }
                else
                {
                    // Can't find the engineer by issue — leave as Pending
                    Logger.LogWarning(
                        "Could not find engineer for issue #{IssueNumber} — task {TaskId} set to Pending",
                        task.IssueNumber, task.Id);
                    _taskBacklog[taskIdx] = _taskBacklog[taskIdx] with
                    {
                        Status = "Pending",
                        AssignedTo = null
                    };
                }
            }
            else
            {
                // No assignment info — just leave as Pending
                Logger.LogInformation("Task {TaskId} reset to Pending for reassignment", task.Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close-and-recreate PR #{PrNumber}", pr.Number);
        }
    }

    private async Task CheckAllTasksCompleteAsync(CancellationToken ct)
    {
        if (_taskBacklog.Count == 0)
            return;

        var allDone = _taskBacklog.All(t => IsTaskDone(t));

        if (!allDone)
            return;

        _allTasksComplete = true;
        Logger.LogInformation("🎉 All {Count} engineering tasks are complete!", _taskBacklog.Count);
        LogActivity("system", $"🎉 All {_taskBacklog.Count} engineering tasks complete — entering integration phase");
        UpdateStatus(AgentStatus.Working, "All tasks complete — creating integration PR");

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "AllTasksComplete",
            NewStatus = AgentStatus.Working,
            Details = $"All {_taskBacklog.Count} engineering tasks are done"
        }, ct);
    }

    private async Task CreateIntegrationPRAsync(CancellationToken ct)
    {
        try
        {
            UpdateStatus(AgentStatus.Working, "Creating integration PR");

            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var engineeringPlan = await ProjectFiles.GetEngineeringPlanAsync(ct);
            var techStack = Config.Project.TechStack;

            // Get list of all repo files to review against the spec
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer performing final integration review. " +
                $"The project uses {techStack}. " +
                "All individual task PRs have been merged to main. Your job is to:\n" +
                "1. Review the architecture and PM spec for any missing wiring, imports, or configuration\n" +
                "2. Identify integration gaps (broken cross-module references, missing route registration, missing DI wiring)\n" +
                "3. Generate any integration fix files needed\n\n" +
                "Output each file using: FILE: path/to/file.ext\n```language\n<content>\n```\n\n" +
                "If no integration fixes are needed, output ONLY the text: NO_INTEGRATION_FIXES_NEEDED");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpecDoc}\n\n" +
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## Engineering Plan\n{engineeringPlan}\n\n" +
                "Review the merged work against these documents. " +
                "Generate any missing integration files (config, wiring, startup registration, etc.).");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var integrationContent = response.Content?.Trim() ?? "";

            var codeFiles = AgentSquad.Core.AI.CodeFileParser.ParseFiles(integrationContent);

            if (codeFiles.Count == 0 ||
                integrationContent.Contains("NO_INTEGRATION_FIXES_NEEDED", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("No integration fixes needed — all tasks cleanly integrated");
                LogActivity("task", "✅ No integration fixes needed — signaling completion");
                _integrationPrCreated = true;

                // Signal completion directly
                await SignalEngineeringCompleteAsync(ct);
                return;
            }

            // Create integration branch and PR
            var branchName = await PrWorkflow.CreateTaskBranchAsync(
                Identity.DisplayName, "final-integration", ct);

            var prBody = $"## Final Integration PR\n\n" +
                $"All {_taskBacklog.Count} engineering tasks have been completed and merged.\n" +
                $"This PR addresses integration gaps identified during final review.\n\n" +
                $"### Files Changed\n" +
                string.Join("\n", codeFiles.Select(f => $"- `{f.Path}`"));

            var pr = await PrWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                "Final Integration",
                prBody,
                "High",
                "Architecture.md",
                "EngineeringPlan.md",
                branchName,
                ct);

            await PrWorkflow.CommitCodeFilesToPRAsync(
                pr.Number, codeFiles, "Integration fixes: wiring, config, and cross-module references", ct);

            CurrentPrNumber = pr.Number;
            Identity.AssignedPullRequest = pr.Number.ToString();
            _integrationPrCreated = true;

            // Sync and mark ready for review
            await SyncBranchWithMainAsync(pr.Number, ct);
            await PrWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);

            await MessageBus.PublishAsync(new ReviewRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ReviewRequest",
                PrNumber = pr.Number,
                PrTitle = pr.Title,
                ReviewType = "Integration"
            }, ct);

            Logger.LogInformation("Created integration PR #{PrNumber} with {FileCount} fixes",
                pr.Number, codeFiles.Count);
            LogActivity("task", $"📦 Created integration PR #{pr.Number} with {codeFiles.Count} fixes");

            await RememberAsync(MemoryType.Action,
                $"Created integration PR #{pr.Number} with {codeFiles.Count} integration fixes", ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create integration PR");
            RecordError($"Integration PR failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            // Signal completion anyway so the pipeline doesn't get stuck
            _integrationPrCreated = true;
            await SignalEngineeringCompleteAsync(ct);
        }
    }

    private async Task SignalEngineeringCompleteAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Engineering complete");
        LogActivity("system", "🏁 Engineering phase complete — all tasks done and integrated");

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringComplete",
            NewStatus = AgentStatus.Idle,
            Details = $"All {_taskBacklog.Count} tasks complete. Engineering phase finished."
        }, ct);

        await RememberAsync(MemoryType.Action,
            $"Engineering phase complete: {_taskBacklog.Count} tasks done", ct: ct);
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

            // Read the linked issue for acceptance criteria
            var issueContext = "";
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (issueNumber.HasValue)
            {
                try
                {
                    var issue = await GitHub.GetIssueAsync(issueNumber.Value, ct);
                    if (issue is not null)
                        issueContext = $"## Linked Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n";
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for PE review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            var codeContext = await PrWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer doing a technical code review.\n\n" +
                "SCOPE: This PR is ONE task. Review the ACTUAL CODE against its stated scope.\n\n" +
                "CHECK: architecture compliance, implementation completeness, code quality, " +
                "bugs/logic errors, missing validation, test coverage.\n\n" +
                "IMPORTANT: Code may appear truncated in your review context due to length limits — " +
                "this is a tooling limitation, NOT a code defect. Do NOT flag truncated code.\n\n" +
                "Only request changes for issues that are significant AND fixable. " +
                "Minor style preferences → APPROVE. Complete rewrites needed → APPROVE with caveat.\n\n" +
                "RESPONSE FORMAT — your ENTIRE response must be ONLY:\n" +
                "- If requesting changes: a **numbered list** (1. 2. 3.) starting on the FIRST line. " +
                "Each item states the issue with **bold** file/method names. Nothing before the list. " +
                "No preamble, no thinking, no analysis narration, no 'Let me check', no descriptions of " +
                "what you examined.\n" +
                "- If approving: one sentence only.\n" +
                "- Last line: VERDICT: APPROVE or VERDICT: REQUEST_CHANGES\n\n" +
                "WRONG: 'Let me review the code... Based on my analysis... 1. Issue'\n" +
                "RIGHT: '1. **AuthController.cs** — missing null check on user parameter'");

            history.AddUserMessage(
                $"## Architecture\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Engineering Plan\n{engineeringPlan}\n\n" +
                issueContext +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n\n" +
                codeContext);

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";
            var approved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

            var reviewBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            // Strip any preamble/thinking the AI may have included before the numbered list
            reviewBody = PullRequestWorkflow.StripReviewPreamble(reviewBody);

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

    /// <summary>
    /// Find the next assignable task, trying preferred complexity first then falling back.
    /// Engineers should not sit idle when there's available work at any complexity level.
    /// </summary>
    private EngineeringTask? FindNextAssignableTask(params string[] complexityPreferences)
    {
        foreach (var complexity in complexityPreferences)
        {
            var task = _taskBacklog.FirstOrDefault(t =>
                string.Equals(t.Complexity, complexity, StringComparison.OrdinalIgnoreCase)
                && t.Status == "Pending" && AreDependenciesMet(t));
            if (task is not null)
                return task;
        }
        return null;
    }

    private static bool IsTaskDone(EngineeringTask task) =>
        task.Status is "Done" or "Complete";

    private bool AreDependenciesMet(EngineeringTask task)
    {
        if (task.Dependencies.Count == 0)
            return true;

        return task.Dependencies.All(depId =>
        {
            var dep = _taskBacklog.FirstOrDefault(t => t.Id == depId);
            return dep is null || IsTaskDone(dep);
        });
    }

    /// <summary>
    /// Ensure a task has its own open GitHub issue. If the task's current issue is
    /// closed (e.g., shared issue #52 closed by an earlier PR) or missing, create a new one.
    /// Returns the updated task with valid IssueNumber, or null if issue creation failed.
    /// </summary>
    private async Task<EngineeringTask?> EnsureTaskHasOpenIssueAsync(
        EngineeringTask task, CancellationToken ct)
    {
        if (task.IssueNumber.HasValue)
        {
            try
            {
                var issue = await GitHub.GetIssueAsync(task.IssueNumber.Value, ct);
                if (issue is not null &&
                    string.Equals(issue.State, "open", StringComparison.OrdinalIgnoreCase))
                    return task; // Issue is open, nothing to do
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to check issue #{IssueNumber} state for task {TaskId}",
                    task.IssueNumber, task.Id);
            }

            // Issue is closed or gone — need a fresh issue
            Logger.LogInformation(
                "Task {TaskId} issue #{IssueNumber} is closed, creating new issue",
                task.Id, task.IssueNumber);
        }

        try
        {
            var issueBody = $"## Task: {task.Name}\n\n{task.Description}\n\n" +
                $"**Complexity:** {task.Complexity}\n" +
                $"**Task ID:** {task.Id}\n\n" +
                $"_Auto-created by Principal Engineer._";
            var newIssue = await GitHub.CreateIssueAsync(
                task.Name, issueBody,
                new[] { "enhancement", task.Complexity.ToLowerInvariant() }, ct);

            task = task with { IssueNumber = newIssue.Number, IssueUrl = $"#{newIssue.Number}" };
            var idx = _taskBacklog.FindIndex(t => t.Id == task.Id);
            if (idx >= 0)
                _taskBacklog[idx] = task;

            Logger.LogInformation(
                "Created issue #{IssueNumber} for task {TaskId}: {TaskName}",
                newIssue.Number, task.Id, task.Name);
            return task;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create issue for task {TaskId}", task.Id);
            return null;
        }
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
                "Include:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: An ordered, numbered list of discrete implementation steps. " +
                "Step 1 MUST be scaffolding (folder structure, config, boilerplate). Each subsequent step " +
                "builds on the previous. Each step should be a self-contained committable unit of work. " +
                "3-6 steps total. Be specific about what each step produces.\n" +
                "4. **Testing**: What tests should cover");

            history.AddUserMessage(
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Architecture\n{architectureDoc}" +
                issueContext +
                $"\n\n## Task: {task.Name}\n{task.Description}\n\n" +
                "Write a detailed PR description with:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: Ordered, numbered list of discrete steps. " +
                "Step 1 = scaffolding. Each step is a committable unit. 3-6 steps.\n" +
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
        var completed = _taskBacklog.Count(t => IsTaskDone(t));
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
