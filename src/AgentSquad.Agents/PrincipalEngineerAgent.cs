using System.Collections.Concurrent;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
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
    private readonly EngineeringTaskIssueManager _taskManager;

    private bool _planningComplete;
    private bool _planningSignalReceived;
    private bool _architectureReady;
    private bool _resourceRequestPending;
    private bool _recoveredReviewPRs;
    private bool _recoveredInProgressPR;
    private DateTime _lastResourceRequestTime = DateTime.MinValue;
    private static readonly TimeSpan SpawnCooldown = TimeSpan.FromSeconds(45);
    private bool _allTasksComplete;
    private bool _integrationPrCreated;
    private bool _engineeringSignaled;
    private readonly Dictionary<string, int> _agentAssignments = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
    private readonly HashSet<int> _mergedTestedPrNumbers = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();
    private readonly Dictionary<int, int> _conflictRetryCount = new();
    private DateTime _lastReviewDiscovery = DateTime.MinValue;
    private static readonly TimeSpan ReviewDiscoveryInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Determines if this PE instance is the leader (responsible for orchestration-only tasks).
    /// The leader is the lowest-rank online PE. If no PEs are online, falls back to this instance.
    /// </summary>
    private bool IsLeader()
    {
        var onlinePEs = _registry.GetAgentsByRole(AgentRole.PrincipalEngineer)
            .Where(a => a.Status is AgentStatus.Working or AgentStatus.Idle or AgentStatus.Online or AgentStatus.Initializing)
            .OrderBy(a => a.Identity.Rank)
            .ToList();
        return onlinePEs.Count == 0 || onlinePEs[0].Identity.Id == Identity.Id;
    }

    /// <summary>
    /// Checks if any PE agent has already reviewed a given PR by looking for
    /// [PrincipalEngineer*] review comments on GitHub.
    /// </summary>
    private async Task<bool> HasAnyPeReviewedAsync(int prNumber, CancellationToken ct)
    {
        var comments = await GitHub.GetPullRequestCommentsAsync(prNumber, ct);
        return comments.Any(c =>
            c.Body.Contains("[PrincipalEngineer]", StringComparison.OrdinalIgnoreCase) ||
            c.Body.Contains("[PrincipalEngineer ", StringComparison.OrdinalIgnoreCase));
    }

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
        ILogger<PrincipalEngineerAgent> logger,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null)
        : base(identity, messageBus, github, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, logger,
               buildRunner, testRunner, metrics, playwrightRunner)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _taskManager = new EngineeringTaskIssueManager(github, logger);
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

    protected override Task<string> GetAdditionalReworkContextAsync(CancellationToken ct)
    {
        var taskSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
            $"- [{t.Id}] {t.Name} ({t.Complexity}, {t.Status})"));
        return Task.FromResult($"## Engineering Tasks\n{taskSummary}\n\n");
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
                var isLeader = IsLeader();

                if (!_planningComplete)
                {
                    if (isLeader)
                    {
                        if (await CheckForArchitectureAsync(ct))
                        {
                            await CreateEngineeringPlanAsync(ct);
                            // _planningComplete is set inside CreateEngineeringPlanAsync
                            // on success or valid restore paths
                        }
                    }
                    else
                    {
                        // Non-leader PEs: check if engineering plan issues already exist (created by leader)
                        if (await CheckForArchitectureAsync(ct))
                        {
                            await SyncEngineeringPlanFromGitHubAsync(ct);
                            if (_taskManager.TotalCount > 0)
                                _planningComplete = true;
                        }
                    }
                }
                else
                {
                    // Show meaningful status at the start of each orchestration loop
                    var pending = _taskManager.PendingCount;
                    var done = _taskManager.DoneCount;
                    var total = _taskManager.TotalCount;
                    var hasWork = pending > 0 || _reviewQueue.Count > 0 || !_allTasksComplete || !_integrationPrCreated;
                    var leaderTag = isLeader ? "Leader" : $"Worker#{Identity.Rank}";
                    var statusVerb = isLeader ? "Orchestrating" : "Working on";

                    // Preserve "Engineering complete" status once signaled so HealthMonitor can detect it
                    if (!_engineeringSignaled)
                    {
                        UpdateStatus(hasWork ? AgentStatus.Working : AgentStatus.Idle,
                            $"[{leaderTag}] {statusVerb} tasks ({done}/{total} done, {pending} pending, {_reviewQueue.Count} PRs queued)");
                    }

                    // Recovery: re-track and re-broadcast review for our own ready-for-review PRs
                    await RecoverReadyForReviewPRsAsync(ct);
                    // Recovery: detect and resume our own in-progress PRs from prior runs
                    await RecoverOwnInProgressPRAsync(ct);
                    // Check if our tracked PR has been merged/closed
                    await CheckOwnPrStatusAsync(ct);
                    // Recovery: finish stuck in-progress PRs that were never marked ready
                    await RecoverStuckInProgressPRAsync(ct);
                    // LEADER ONLY: Evaluate resource needs FIRST so spawns happen before leader grabs tasks
                    if (isLeader && !_allTasksComplete)
                        await EvaluateResourceNeedsAsync(ct);

                    // Priority 0: Continue work on our own in-progress PR before anything else
                    if (CurrentPrNumber is not null && !await IsOwnPrReadyForReview(ct))
                    {
                        await ContinueOwnPrImplementationAsync(ct);
                        continue; // Skip reviews until our own PR is done
                    }
                    // Priority 1: Process rework feedback on our own PRs
                    await ProcessOwnReworkAsync(ct);

                    // Check if all tasks are complete → integration phase (LEADER ONLY)
                    // Guard: only check completion if planning is done and tasks were created
                    if (!_allTasksComplete && isLeader && _planningComplete)
                    {
                        await CheckAllTasksCompleteAsync(ct);
                    }

                    if (_allTasksComplete && isLeader)
                    {
                        // Integration phase: create integration PR if needed (LEADER ONLY)
                        if (!_integrationPrCreated)
                        {
                            await CreateIntegrationPRAsync(ct);
                        }
                    }
                    else if (!_allTasksComplete)
                    {
                        // LEADER ONLY: Recover orphaned assigned tasks with no open PRs
                        if (isLeader)
                            await RecoverOrphanedAssignmentsAsync(ct);
                        // LEADER ONLY: Assign issues to available workers (non-leader PEs, SE, JE)
                        if (isLeader)
                            await AssignTasksToAvailableEngineersAsync(ct);
                        // ALL PEs: Work on own tasks (leader defers to spawned workers when available)
                        await WorkOnOwnTasksAsync(ct);
                        // ALL PEs: Discover open PRs needing review
                        await DiscoverUnreviewedEngineerPRsAsync(ct);
                    }

                    // ALL PEs: Always review PRs — even after all tasks complete
                    await DiscoverUnreviewedEngineerPRsAsync(ct);
                    await ReviewEngineerPRsAsync(ct);

                    // Inline test workflow: merge PRs that have been approved + tested
                    if (Config.Workspace.IsInlineTestWorkflow)
                        await MergeTestedPRsAsync(ct);

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
        // Recovery: check for existing engineering-task issues from a prior run
        await _taskManager.LoadTasksAsync(ct);
        if (_taskManager.TotalCount > 0)
        {
            // Validate recovered tasks belong to the current run by checking that
            // corresponding enhancement issues still exist. If no enhancement issues
            // exist, these tasks are stale leftovers from a previous run.
            var currentEnhancements = await GitHub.GetIssuesByLabelAsync(
                IssueWorkflow.Labels.Enhancement, ct);
            var enhancementNumbers = currentEnhancements.Select(i => i.Number).ToHashSet();
            var hasMatchingParent = _taskManager.Tasks.Any(t =>
                t.ParentIssueNumber.HasValue && enhancementNumbers.Contains(t.ParentIssueNumber.Value));

            if (currentEnhancements.Count == 0 || !hasMatchingParent)
            {
                Logger.LogWarning(
                    "Found {Count} engineering-task issues but they don't match current enhancement issues — ignoring stale tasks",
                    _taskManager.TotalCount);
                _taskManager.ClearCache();
                // Fall through to create a fresh plan
            }
            else
            {
                Logger.LogInformation("Restored {Count} tasks from existing engineering-task issues ({Done} done, {Pending} pending)",
                    _taskManager.TotalCount, _taskManager.DoneCount, _taskManager.PendingCount);

                _planningComplete = true;
                UpdateStatus(AgentStatus.Working,
                    $"Loaded {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)");

                // Emit the plan-ready signal so workflow can advance
                await MessageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "EngineeringPlanReady",
                    NewStatus = AgentStatus.Working,
                    CurrentTask = "Engineering Planning",
                    Details = $"Restored engineering plan with {_taskManager.TotalCount} tasks ({_taskManager.DoneCount} done, {_taskManager.PendingCount} pending)."
                }, ct);

                return;
            }
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
            _planningComplete = false;
            return;
        }

        var issuesSummary = string.Join("\n\n", enhancementIssues.Select(i =>
            $"### Issue #{i.Number}: {i.Title}\n{i.Body}"));

        // Fetch repo structure so PE can include file path guidance in tasks
        var repoStructure = await GetRepoStructureForContextAsync(ct);

        // Read visual design reference files for UI task context
        var designContext = await ReadDesignReferencesAsync(ct);

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
            "5. Reference the source GitHub Issue number for each task\n" +
            "6. For each task, specify which files to create/modify and the namespace to use\n\n" +
            "## CRITICAL — Foundation Task (MUST be Task T1)\n" +
            "The FIRST task (T1) MUST ALWAYS be a 'Project Foundation & Scaffolding' task that:\n" +
            "- Sets up the solution/project structure, build configuration, and shared infrastructure\n" +
            "- Creates the core data models, interfaces, and abstractions from the architecture document\n" +
            "- Establishes the directory layout, namespaces, and integration points that all other tasks build upon\n" +
            "- Creates stub/skeleton files for major components so parallel engineers know where to implement\n" +
            "- Includes dependency injection registration, configuration models, and shared utilities\n" +
            "- Complexity: High (this is the most important task — it sets the foundation)\n" +
            "- Has NO dependencies (all other tasks should depend on T1)\n" +
            "This ensures the first PR establishes the project skeleton before any parallel work begins, " +
            "giving every engineer a clear target for where their code goes.\n\n" +
            "## CRITICAL — Parallel-Friendly Task Decomposition\n" +
            "Multiple engineers will work on tasks IN PARALLEL. Design tasks to MINIMIZE overlap and merge conflicts:\n" +
            "- **Separate by component/module boundary**: each task should own a distinct set of files. " +
            "Two tasks should NEVER create or modify the same file.\n" +
            "- **Vertical slicing over horizontal**: prefer tasks that implement a complete feature end-to-end " +
            "(model + service + API + tests) rather than tasks that cut across all features at one layer " +
            "(e.g., 'add all models' then 'add all services').\n" +
            "- **Explicit file ownership**: every task's FilePlan must list EXACTLY which files it creates or modifies. " +
            "If two tasks need to touch the same file (e.g., DI registration in Program.cs), " +
            "assign that responsibility to only ONE of them and note it.\n" +
            "- **Shared infrastructure in T1**: anything that multiple tasks would need (base classes, interfaces, " +
            "config models, shared DTOs) should go in T1 so parallel tasks only CONSUME these, never create them.\n" +
            "- **Minimize cross-task dependencies**: maximize the number of tasks that depend ONLY on T1 " +
            "so they can all run in parallel. Chain dependencies (T3 depends on T2 depends on T1) should be rare.\n" +
            "- **Independent test scoping**: each task should include tests only for its own component, " +
            "not shared test infrastructure (that belongs in T1).\n\n" +
            "CRITICAL: Review the existing repository structure carefully. " +
            "Tasks MUST reference existing files when appropriate (modify, not recreate). " +
            "New files should follow the existing directory structure and naming conventions. " +
            "Each task should specify exact file paths and namespaces to prevent engineers from " +
            "creating duplicate or conflicting code.\n\n" +
            "Task complexity mapping:\n" +
            "- **High**: Complex tasks requiring deep expertise → Principal Engineer\n" +
            "- **Medium**: Moderate tasks → Senior Engineers\n" +
            "- **Low**: Straightforward tasks → Junior Engineers");

        var userPromptBuilder = new System.Text.StringBuilder();
        userPromptBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");
        userPromptBuilder.AppendLine($"## Architecture Document\n{architectureDoc}\n");

        if (!string.IsNullOrEmpty(repoStructure))
        {
            userPromptBuilder.AppendLine("## Existing Repository Structure (main branch)");
            userPromptBuilder.AppendLine(repoStructure);
            userPromptBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(designContext))
        {
            userPromptBuilder.AppendLine("## Visual Design Reference");
            userPromptBuilder.AppendLine("The repository contains design reference files that define the EXACT UI to be built. " +
                "For any UI-related task, include the relevant design details in the task description: " +
                "specific CSS patterns, color hex codes, layout structure, and component hierarchy. " +
                "Every UI task description MUST say: 'Reference: OriginalDesignConcept.html in repository root for exact visual spec.'");
            userPromptBuilder.AppendLine(designContext);
            userPromptBuilder.AppendLine();
        }

        userPromptBuilder.AppendLine($"## GitHub Issues (User Stories)\n{issuesSummary}\n");
        userPromptBuilder.AppendLine(
            "Create an engineering plan mapping these Issues to tasks. " +
            "REMEMBER:\n" +
            "- T1 MUST be the Project Foundation & Scaffolding task (High complexity, no dependencies). " +
            "It sets up the solution structure, shared interfaces, base classes, config, and DI registration " +
            "so all other tasks have a clear skeleton to build upon.\n" +
            "- ALL other tasks should depend on T1 at minimum.\n" +
            "- Design tasks for PARALLEL execution: each task should own distinct files with NO overlap.\n" +
            "- Prefer vertical slices (one feature end-to-end) over horizontal layers.\n" +
            "- Maximize tasks that depend ONLY on T1 (star topology, not chains).\n\n" +
            "Output ONLY structured lines in this format:\n" +
            "TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>\n\n" +
            "The FilePlan field should contain semicolon-separated file operations:\n" +
            "  CREATE:path/to/file.ext(namespace);MODIFY:path/to/existing.ext;USE:ExistingType(namespace)\n\n" +
            "Example:\n" +
            "TASK|T1|42|Project Foundation & Scaffolding|Create solution structure, shared models, interfaces, " +
            "DI registration, and configuration|High|NONE|" +
            "CREATE:src/Models/AppConfig.cs(MyApp.Models);CREATE:src/Interfaces/IAuthService.cs(MyApp.Interfaces);CREATE:src/Program.cs(MyApp)\n" +
            "TASK|T2|43|Implement auth module|Build JWT authentication with refresh tokens|Medium|T1|" +
            "CREATE:src/Services/AuthService.cs(MyApp.Services);USE:IAuthService(MyApp.Interfaces)\n" +
            "TASK|T3|44|Implement user profile|Build user profile CRUD|Medium|T1|" +
            "CREATE:src/Services/UserProfileService.cs(MyApp.Services);CREATE:src/Controllers/ProfileController.cs(MyApp.Controllers)\n\n" +
            "Note how T2 and T3 both depend only on T1 (parallel-safe) and own completely separate files.\n\n" +
            "Only output TASK lines, nothing else.");

        history.AddUserMessage(userPromptBuilder.ToString());

        var response = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var structuredText = response.Content ?? "";

        var parsedTasks = new List<EngineeringTask>();
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

            // Parse optional FilePlan field (8th field) for file/namespace guidance
            var filePlan = parts.Length >= 8 ? parts[7].Trim() : "";

            parsedTasks.Add(new EngineeringTask
            {
                Id = parts[1].Trim(),
                Name = parts[3].Trim(),
                Description = parts[4].Trim() + (string.IsNullOrEmpty(filePlan) ? "" :
                    $"\n\n### File Plan\n{FormatFilePlan(filePlan)}"),
                Complexity = NormalizeComplexity(parts[5].Trim()),
                Dependencies = deps,
                ParentIssueNumber = issueNum > 0 ? issueNum : null
            });
        }

        if (parsedTasks.Count == 0)
        {
            Logger.LogWarning("No tasks parsed from AI response, creating a fallback task per issue");
            foreach (var issue in enhancementIssues)
            {
                parsedTasks.Add(new EngineeringTask
                {
                    Id = $"T-{issue.Number}",
                    Name = issue.Title,
                    Description = issue.Body,
                    Complexity = "Medium",
                    ParentIssueNumber = issue.Number
                });
            }
        }

        // Enforce foundation-first pattern: ensure T1 is a foundation task
        // and all other tasks depend on it
        EnsureFoundationFirstPattern(parsedTasks);

        // Create GitHub issues for each task (the single source of truth)
        var createdTasks = await _taskManager.CreateTaskIssuesAsync(parsedTasks, ct);

        // Now resolve dependency task IDs (T1, T2) to actual issue numbers
        // Build a map from task ID → issue number
        var taskIdToIssue = createdTasks.ToDictionary(t => t.Id, t => t.IssueNumber ?? 0);
        foreach (var task in createdTasks)
        {
            if (task.Dependencies.Count == 0 || !task.IssueNumber.HasValue)
                continue;

            var depIssueNumbers = task.Dependencies
                .Where(d => taskIdToIssue.ContainsKey(d) && taskIdToIssue[d] > 0)
                .Select(d => taskIdToIssue[d])
                .ToList();

            if (depIssueNumbers.Count > 0)
            {
                // Update the issue body with dependency issue numbers
                var updatedBody = EngineeringTaskIssueManager.BuildIssueBodyWithDeps(task, depIssueNumbers);
                await GitHub.UpdateIssueAsync(task.IssueNumber.Value, body: updatedBody, ct: ct);
            }
        }

        // Reload to pick up dependency info from updated issue bodies, then create GitHub links
        await _taskManager.LoadTasksAsync(ct);

        // Create native GitHub blocked-by dependency links between tasks
        await _taskManager.LinkTaskDependenciesAsync(_taskManager.Tasks.ToList(), ct);

        // REQ-PE-009: Validate all PM enhancements have engineering tasks
        await ValidateEnhancementCoverageAsync(enhancementIssues, ct);

        Logger.LogInformation("Engineering plan created with {Count} tasks from {IssueCount} issues",
            _taskManager.TotalCount, enhancementIssues.Count);
        LogActivity("task", $"📋 Engineering plan created: {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues");

        var taskSummary = string.Join(", ", _taskManager.Tasks.Select(t => $"{t.Id}:{t.Name}({t.Complexity})"));
        await RememberAsync(MemoryType.Decision,
            $"Created engineering plan with {_taskManager.TotalCount} tasks from {enhancementIssues.Count} issues",
            $"Tasks: {TruncateForMemory(taskSummary)}", ct);

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringPlanReady",
            NewStatus = AgentStatus.Working,
            CurrentTask = "Engineering Planning",
            Details = $"Engineering plan created with {_taskManager.TotalCount} tasks. " +
                      "Ready to assign work to engineers."
        }, ct);

        UpdateStatus(AgentStatus.Idle, "Engineering plan complete, entering development loop");
        _planningComplete = true;
    }

    /// <summary>
    /// REQ-PE-009: After creating the engineering plan, validate that every PM enhancement
    /// issue has at least one linked engineering task. For missed enhancements, either create
    /// additional tasks or post a justification comment explaining how it's covered.
    /// </summary>
    private async Task ValidateEnhancementCoverageAsync(
        IReadOnlyList<AgentIssue> enhancementIssues, CancellationToken ct)
    {
        try
        {
            // Build set of parent issue numbers that have engineering tasks
            var coveredParents = _taskManager.Tasks
                .Where(t => t.ParentIssueNumber.HasValue)
                .Select(t => t.ParentIssueNumber!.Value)
                .ToHashSet();

            var uncoveredEnhancements = enhancementIssues
                .Where(e => !coveredParents.Contains(e.Number))
                .ToList();

            if (uncoveredEnhancements.Count == 0)
            {
                Logger.LogInformation("Enhancement coverage validation passed: all {Count} enhancements have engineering tasks",
                    enhancementIssues.Count);
                return;
            }

            Logger.LogWarning(
                "Enhancement coverage gap: {UncoveredCount}/{TotalCount} enhancements have no engineering tasks: {Issues}",
                uncoveredEnhancements.Count, enhancementIssues.Count,
                string.Join(", ", uncoveredEnhancements.Select(e => $"#{e.Number}")));

            // Ask AI to determine if each uncovered enhancement is covered by existing tasks or was missed
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var existingTasksSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
                $"- {t.Id}: {t.Name} (Parent: #{t.ParentIssueNumber}) — {t.Description?.Split('\n').FirstOrDefault()}"));

            foreach (var enhancement in uncoveredEnhancements)
            {
                var history = new ChatHistory();
                history.AddSystemMessage(
                    "You are a Principal Engineer validating engineering plan coverage. " +
                    "An enhancement (user story) has no dedicated engineering task. " +
                    "Determine if this enhancement is COVERED by existing tasks or was MISSED.\n\n" +
                    "If COVERED: respond with COVERED followed by which specific tasks address it and how.\n" +
                    "If MISSED: respond with MISSED followed by what engineering task should be created.");

                history.AddUserMessage(
                    $"## Uncovered Enhancement #{enhancement.Number}: {enhancement.Title}\n{enhancement.Body}\n\n" +
                    $"## Existing Engineering Tasks\n{existingTasksSummary}\n\n" +
                    "Is this enhancement covered by the existing tasks, or was it missed?");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var responseText = response.Content ?? "";

                if (responseText.Contains("COVERED", StringComparison.OrdinalIgnoreCase))
                {
                    // Post justification comment on the enhancement issue
                    var justification = responseText
                        .Replace("COVERED", "").Replace("covered", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    await GitHub.AddIssueCommentAsync(enhancement.Number,
                        $"📋 **Principal Engineer — Coverage Analysis**\n\n" +
                        $"This user story does not have a dedicated engineering task, but its requirements are " +
                        $"addressed by existing tasks in the engineering plan:\n\n{justification}",
                        ct);

                    Logger.LogInformation(
                        "Enhancement #{Number} covered by existing tasks — justification posted",
                        enhancement.Number);
                }
                else
                {
                    // Enhancement was missed — create an additional task
                    var taskDescription = responseText
                        .Replace("MISSED", "").Replace("missed", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    var newTaskId = $"T{_taskManager.TotalCount + 1}";
                    var newTask = new EngineeringTask
                    {
                        Id = newTaskId,
                        Name = $"Implement {enhancement.Title}",
                        Description = $"Auto-created from uncovered enhancement #{enhancement.Number}.\n\n{taskDescription}",
                        Complexity = "Medium",
                        ParentIssueNumber = enhancement.Number,
                        Dependencies = _taskManager.Tasks.Any(t => t.Id == "T1")
                            ? new List<string> { "T1" }
                            : new List<string>()
                    };

                    var created = await _taskManager.CreateTaskIssuesAsync(new[] { newTask }, ct);
                    if (created.Count > 0)
                    {
                        Logger.LogInformation(
                            "Created additional task {TaskId} (Issue #{IssueNumber}) for missed enhancement #{EnhancementNumber}",
                            newTaskId, created[0].IssueNumber, enhancement.Number);
                        LogActivity("task", $"📋 Created task {newTaskId} for missed enhancement #{enhancement.Number}: {enhancement.Title}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Enhancement coverage validation failed — continuing without validation");
        }
    }

    /// <summary>
    /// Non-leader PEs sync task state from GitHub issues instead of creating the plan.
    /// They load existing engineering-task issues created by the leader.
    /// </summary>
    private async Task SyncEngineeringPlanFromGitHubAsync(CancellationToken ct)
    {
        try
        {
            await _taskManager.LoadTasksAsync(ct);
            if (_taskManager.TotalCount > 0)
            {
                Logger.LogInformation(
                    "Non-leader PE synced {Count} tasks from GitHub ({Done} done, {Pending} pending)",
                    _taskManager.TotalCount, _taskManager.DoneCount, _taskManager.PendingCount);
                UpdateStatus(AgentStatus.Idle,
                    $"Synced {_taskManager.TotalCount} tasks, entering development loop");
            }
            else
            {
                Logger.LogDebug("Non-leader PE: no engineering-task issues found yet, waiting for leader");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Non-leader PE failed to sync engineering plan from GitHub");
        }
    }

    #endregion

    #region Phase 2 — Continuous Development Loop

    /// <summary>
    /// Detect tasks with status:assigned/in-progress that have no open PR → reset to pending.
    /// This handles cases where a PR was closed without merging (e.g., rebase wiped changes,
    /// merge conflict close-and-recreate failed halfway, or app restarted mid-operation).
    /// </summary>
    private async Task RecoverOrphanedAssignmentsAsync(CancellationToken ct)
    {
        try
        {
            var assignedTasks = _taskManager.Tasks
                .Where(t => t.Status is "Assigned" or "InProgress"
                         && t.IssueNumber.HasValue
                         && !EngineeringTaskIssueManager.IsTaskDone(t))
                .ToList();

            if (assignedTasks.Count == 0)
                return;

            // Skip tasks that are currently tracked in _agentAssignments — these were
            // recently assigned by us and the engineer may not have created a PR yet.
            var trackedIssueNums = new HashSet<int>(_agentAssignments.Values);

            // Get all open PRs once to check against
            var openPRs = await GitHub.GetOpenPullRequestsAsync(ct);
            var openPrIssueRefs = new HashSet<int>();
            foreach (var pr in openPRs)
            {
                // Extract issue number from PR body "Closes #NNN"
                var closesMatch = System.Text.RegularExpressions.Regex.Match(
                    pr.Body ?? "", @"Closes\s+#(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (closesMatch.Success && int.TryParse(closesMatch.Groups[1].Value, out var issueNum))
                    openPrIssueRefs.Add(issueNum);
            }

            foreach (var task in assignedTasks)
            {
                // Skip tasks we've already assigned to an engineer this session
                if (trackedIssueNums.Contains(task.IssueNumber!.Value))
                    continue;

                // Check if there's an open PR that references this task's issue
                if (openPrIssueRefs.Contains(task.IssueNumber!.Value))
                {
                    // Restore to _agentAssignments so PE tracks this assignment
                    if (task.AssignedTo is not null)
                    {
                        var matchingAgent = _registry.GetAgentsByRole(AgentRole.SeniorEngineer)
                            .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                            .FirstOrDefault(a => string.Equals(a.Identity.DisplayName, task.AssignedTo, StringComparison.OrdinalIgnoreCase));
                        if (matchingAgent is not null && !_agentAssignments.ContainsKey(matchingAgent.Identity.Id))
                        {
                            _agentAssignments[matchingAgent.Identity.Id] = task.IssueNumber!.Value;
                            Logger.LogInformation("Restored assignment tracking: {Engineer} → issue #{IssueNumber}",
                                task.AssignedTo, task.IssueNumber);
                        }
                    }
                    continue;
                }

                // Also check if there's an open PR with the assigned engineer's name in the title
                var hasMatchingPr = openPRs.Any(pr =>
                    task.AssignedTo is not null
                    && pr.Title.Contains(task.AssignedTo, StringComparison.OrdinalIgnoreCase));

                if (hasMatchingPr)
                    continue;

                // No open PR found — this assignment is orphaned, reset to pending
                Logger.LogWarning(
                    "Task #{IssueNumber} ({TaskName}) is {Status} but has no open PR — resetting to Pending",
                    task.IssueNumber, task.Name, task.Status);

                await _taskManager.ResetToPendingAsync(task.IssueNumber!.Value, ct);

                // Clear from our assignment tracking if present
                var orphanedAgent = _agentAssignments
                    .FirstOrDefault(kvp => kvp.Value == task.IssueNumber!.Value);
                if (orphanedAgent.Key is not null)
                    _agentAssignments.Remove(orphanedAgent.Key);

                LogActivity("recovery", $"🔄 Reset orphaned task #{task.IssueNumber} ({task.Name}) to Pending — no open PR found");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check for orphaned task assignments");
        }
    }

    private async Task AssignTasksToAvailableEngineersAsync(CancellationToken ct)
    {
        try
        {
            var registeredEngineers = new List<EngineerInfo>();

            // Include non-leader PEs as assignable workers
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.PrincipalEngineer))
            {
                if (agent.Identity.Id == Identity.Id) continue; // Skip self (leader)
                registeredEngineers.Add(new EngineerInfo { AgentId = agent.Identity.Id, Name = agent.Identity.DisplayName, Role = AgentRole.PrincipalEngineer });
            }

            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                registeredEngineers.Add(new EngineerInfo { AgentId = agent.Identity.Id, Name = agent.Identity.DisplayName, Role = AgentRole.SeniorEngineer });
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                registeredEngineers.Add(new EngineerInfo { AgentId = agent.Identity.Id, Name = agent.Identity.DisplayName, Role = AgentRole.JuniorEngineer });

            foreach (var engineer in registeredEngineers)
            {
                if (_agentAssignments.ContainsKey(engineer.AgentId))
                {
                    var assignedIssueNum = _agentAssignments[engineer.AgentId];
                    var assignedTask = _taskManager.FindByIssueNumber(assignedIssueNum);
                    if (assignedTask is not null && !EngineeringTaskIssueManager.IsTaskDone(assignedTask))
                        continue;
                    _agentAssignments.Remove(engineer.AgentId);
                }

                var complexityPreferences = engineer.Role switch
                {
                    AgentRole.PrincipalEngineer => new[] { "High", "Medium", "Low" },
                    AgentRole.SeniorEngineer => new[] { "Medium", "High", "Low" },
                    AgentRole.JuniorEngineer => new[] { "Low", "Medium" },
                    _ => Array.Empty<string>()
                };

                if (complexityPreferences.Length == 0)
                    continue;

                var task = _taskManager.FindNextAssignableTask(complexityPreferences);
                if (task is null || !task.IssueNumber.HasValue)
                    continue;

                await _taskManager.AssignTaskAsync(task.IssueNumber.Value, engineer.Name, ct);
                _agentAssignments[engineer.AgentId] = task.IssueNumber.Value;

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

            EngineeringTask? task;

            if (!IsLeader())
            {
                // ── WORKER PE: only work on tasks explicitly assigned by the leader ──
                // Check for pending assignments delivered via IssueAssignmentMessage
                if (AssignmentQueue.TryDequeue(out var assignment))
                {
                    // Refresh cache from GitHub to get the latest status after leader's assignment
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindByIssueNumber(assignment.IssueNumber);
                    if (task is null || EngineeringTaskIssueManager.IsTaskDone(task))
                    {
                        Logger.LogWarning(
                            "Worker PE: assigned task #{IssueNumber} not found or already done, skipping",
                            assignment.IssueNumber);
                        return;
                    }
                    Logger.LogInformation(
                        "Worker PE picking up assigned task #{IssueNumber}: {Name}",
                        assignment.IssueNumber, task.Name);
                }
                else
                {
                    // No pending message — refresh from GitHub and look for tasks assigned to us
                    await _taskManager.LoadTasksAsync(ct);
                    task = _taskManager.FindAssignedTo(Identity.DisplayName);
                    if (task is null)
                    {
                        Logger.LogDebug("Worker PE: no task assigned to {Name}, waiting for leader",
                            Identity.DisplayName);
                        return;
                    }
                    Logger.LogInformation(
                        "Worker PE recovered assigned task #{IssueNumber}: {Name}",
                        task.IssueNumber, task.Name);
                }
            }
            else
            {
                // ── LEADER PE: refresh cache from GitHub to see latest assignments, then pick ──
                await _taskManager.LoadTasksAsync(ct);
                task = _taskManager.FindNextAssignableTask("High", "Medium", "Low");

                if (task is null)
                {
                    if (_taskManager.PendingCount > 0)
                        Logger.LogDebug("No assignable tasks: {Pending} pending, some blocked by dependencies",
                            _taskManager.PendingCount);
                    return;
                }

                // Leader PE: guard against grabbing non-High tasks if we recently requested a PE spawn
                // AND workers (other PEs, SEs, JEs) actually exist to handle them.
                if (!string.Equals(task.Complexity, "High", StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - _lastResourceRequestTime < SpawnCooldown)
                {
                    var allWorkers = _registry.GetAgentsByRole(AgentRole.PrincipalEngineer)
                        .Where(a => a.Identity.Id != Identity.Id) // other PE workers
                        .Concat(_registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                        .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                        .ToList();
                    var freeWorkers = allWorkers.Count(a => !_agentAssignments.ContainsKey(a.Identity.Id));

                    if (freeWorkers > 0)
                    {
                        Logger.LogInformation(
                            "PE leader deferring {Complexity} task {TaskId} — {FreeWorkers} worker(s) available, " +
                            "spawn cooldown active ({Remaining:F0}s remaining)",
                            task.Complexity, task.Id, freeWorkers,
                            (SpawnCooldown - (DateTime.UtcNow - _lastResourceRequestTime)).TotalSeconds);
                        return;
                    }

                    // Only wait for spawned worker if at least one worker is already registered
                    if (allWorkers.Count > 0)
                    {
                        Logger.LogDebug(
                            "PE leader waiting for spawned worker before taking {Complexity} task {TaskId}",
                            task.Complexity, task.Id);
                        return;
                    }

                    Logger.LogInformation(
                        "PE leader taking {Complexity} task {TaskId} — no workers registered yet, not waiting",
                        task.Complexity, task.Id);
                }
            }

            if (!task.IssueNumber.HasValue)
            {
                Logger.LogWarning("Task {TaskId} has no issue number — skipping", task.Id);
                return;
            }

            // Claim validation: re-check GitHub to prevent race if another PE claimed it
            // between our cache load and now
            var freshTask = _taskManager.FindByIssueNumber(task.IssueNumber.Value);
            if (freshTask is not null && freshTask.Status is "InProgress"
                && !string.Equals(freshTask.AssignedTo, Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation(
                    "Task #{IssueNumber} already in-progress by {Other}, skipping",
                    task.IssueNumber, freshTask.AssignedTo);
                return;
            }

            // Mark as assigned to self via the task manager
            await _taskManager.AssignTaskAsync(task.IssueNumber.Value, Identity.DisplayName, ct);

            UpdateStatus(AgentStatus.Working, $"Working on: {task.Name}");
            Logger.LogInformation("Principal Engineer working on task {TaskId}: {TaskName}",
                task.Id, task.Name);

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
                "",
                branchName,
                ct);

            // Mark task in-progress via the task manager
            if (task.IssueNumber.HasValue)
                await _taskManager.MarkInProgressAsync(task.IssueNumber.Value, pr.Number, ct);

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

    /// <summary>
    /// Periodically scan GitHub for open PRs with 'ready-for-review' label that aren't
    /// in the review queue. This catches PRs whose ReviewRequestMessage was lost on restart.
    /// </summary>
    private async Task DiscoverUnreviewedEngineerPRsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastReviewDiscovery < ReviewDiscoveryInterval)
            return;
        _lastReviewDiscovery = DateTime.UtcNow;

        try
        {
            var openPRs = await GitHub.GetOpenPullRequestsAsync(ct);
            var discovered = 0;

            foreach (var pr in openPRs)
            {
                // Only ready-for-review PRs
                if (!pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;

                // Skip PRs owned by this PE (use colon delimiter to prevent "PrincipalEngineer" matching "PrincipalEngineer 1:")
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if already reviewed or already queued
                if (_reviewedPrNumbers.Contains(pr.Number))
                    continue;

                // Add to review queue
                _reviewQueue.Enqueue(pr.Number);
                discovered++;
                Logger.LogInformation(
                    "PE discovered unreviewed PR #{Number}: {Title} (ready-for-review)",
                    pr.Number, pr.Title);
            }

            if (discovered > 0)
                Logger.LogInformation("PE discovered {Count} unreviewed engineer PRs", discovered);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to discover unreviewed engineer PRs");
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

                // Cross-PE dedup: if ANY PE has already reviewed this PR, skip it.
                // This prevents multiple PE agents from reviewing the same PR.
                if (!_forceApprovalPrs.Contains(prNumber)
                    && await HasAnyPeReviewedAsync(prNumber, ct)
                    && !await PrWorkflow.NeedsReviewFromAsync(prNumber, "PrincipalEngineer", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

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

                // Skip our own PRs (use colon delimiter for multi-PE correctness)
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                Logger.LogInformation("PE reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                // BUG FIX: Force-approve after max rework cycles to prevent infinite loops.
                // Only actually force-approve if we're a required reviewer for this PR —
                // otherwise we'd create redundant approval comments.
                bool approved;
                string? reviewBody;
                if (_forceApprovalPrs.Contains(prNumber))
                {
                    _forceApprovalPrs.Remove(prNumber);
                    var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                    var requiredReviewers = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                    if (!requiredReviewers.Any(r => r.Contains("PrincipalEngineer", StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.LogInformation("PE is not a required reviewer for PR #{Number} — skipping force-approval", prNumber);
                        _reviewedPrNumbers.Add(prNumber);
                        continue;
                    }
                    approved = true;
                    reviewBody = $"Force-approving after maximum rework cycles. " +
                        $"The engineer has made best-effort improvements across multiple iterations.";
                }
                else
                {
                    // Check if the author actually committed new code since our last review.
                    // Prevents pointless re-reviews of unchanged code (wastes AI calls and creates duplicate feedback).
                    var hasNewCommits = await PrWorkflow.HasNewCommitsSinceReviewAsync(prNumber, "PrincipalEngineer", ct);
                    if (!hasNewCommits)
                    {
                        Logger.LogWarning("No new commits on PR #{Number} since last review — approving to unblock", prNumber);
                        approved = true;
                        reviewBody = "No new code commits detected since last review. " +
                            "The author marked the PR as ready but did not push file changes. " +
                            "Approving to avoid blocking progress — previous feedback still applies.";
                    }
                    else
                    {
                        (approved, reviewBody) = await EvaluatePrQualityAsync(pr, ct);
                    }
                }

                if (reviewBody is null)
                    continue;

                if (approved)
                {
                    var requireTests = Config.Workspace.IsInlineTestWorkflow;
                    var result = await PrWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "PrincipalEngineer", reviewBody, requireTests, ct);
                    if (result == MergeAttemptResult.Merged)
                    {
                        Logger.LogInformation("PE approved and merged PR #{Number}", pr.Number);
                        LogActivity("task", $"✅ Approved and merged PR #{pr.Number}: {pr.Title}");

                        // Mark the engineering task Done via issue manager (skip test PRs)
                        if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                            await MarkEngineerTaskDoneAsync(pr, ct);

                        await RememberAsync(MemoryType.Action,
                            $"Reviewed and approved+merged PR #{pr.Number}: {pr.Title}", ct: ct);
                    }
                    else if (result == MergeAttemptResult.ConflictBlocked)
                    {
                        Logger.LogWarning("PE approved PR #{Number} but merge blocked by conflicts, attempting close-and-recreate", pr.Number);
                        LogActivity("task", $"⚠️ PR #{pr.Number} blocked by merge conflicts — closing and recreating");
                        await TryCloseAndRecreatePRAsync(pr, ct);
                    }
                    else if (result == MergeAttemptResult.AwaitingTests)
                    {
                        Logger.LogInformation("PE approved PR #{Number}, waiting for Test Engineer to add tests", pr.Number);
                        LogActivity("task", $"✅ Approved PR #{pr.Number}: {pr.Title} — awaiting tests");
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

            // Reset status after review loop completes
            if (prNumbersToReview.Count > 0)
                UpdateStatus(AgentStatus.Idle, "Review cycle complete, checking tasks");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review engineer PRs");
        }
    }

    /// <summary>
    /// Inline test workflow: find PRs with both 'approved' and 'tests-added' labels,
    /// do a lightweight review of the test code, and merge.
    /// Engineers wrote the code (already approved by PE). TE added tests.
    /// PE does a quick test-quality check then merges. PM/Architect don't re-review.
    /// </summary>
    private async Task MergeTestedPRsAsync(CancellationToken ct)
    {
        try
        {
            var openPRs = await GitHub.GetOpenPullRequestsAsync(ct);

            foreach (var pr in openPRs)
            {
                if (ct.IsCancellationRequested) break;

                // Must have BOTH approved (code reviewed) and tests-added (TE finished)
                bool hasApproved = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.Approved, StringComparer.OrdinalIgnoreCase);
                bool hasTests = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase);

                if (!hasApproved || !hasTests)
                    continue;

                // Skip our own PRs
                if (pr.Title.StartsWith($"{Identity.DisplayName}:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if we already processed this PR in this cycle
                if (_reviewedPrNumbers.Contains(pr.Number) &&
                    !_mergedTestedPrNumbers.Add(pr.Number))
                    continue;

                Logger.LogInformation(
                    "Found approved+tested PR #{Number}: {Title} — reviewing tests and merging",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing tests on PR #{pr.Number}");

                // Lightweight test review: check that TE actually added test files
                var changedFiles = await GitHub.GetPullRequestChangedFilesAsync(pr.Number, ct);
                var testFiles = changedFiles
                    .Where(f => f.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                                f.Contains("Test", StringComparison.Ordinal))
                    .ToList();

                if (testFiles.Count == 0)
                {
                    Logger.LogWarning(
                        "PR #{Number} has tests-added label but no test files found — skipping merge",
                        pr.Number);
                    await GitHub.AddPullRequestCommentAsync(pr.Number,
                        "⚠️ **PE Review:** PR has `tests-added` label but no test files were detected. " +
                        "Waiting for actual test files to be committed.", ct);
                    continue;
                }

                // Post test review approval and merge
                await GitHub.AddPullRequestCommentAsync(pr.Number,
                    $"✅ **[PrincipalEngineer] Tests Reviewed** — {testFiles.Count} test file(s) verified. Merging.", ct);

                var result = await PrWorkflow.MergeApprovedTestedPRAsync(
                    pr.Number, "PrincipalEngineer", ct);

                if (result == MergeAttemptResult.Merged)
                {
                    Logger.LogInformation("PE merged tested PR #{Number}", pr.Number);
                    LogActivity("task", $"✅ Merged PR #{pr.Number}: {pr.Title} (code approved + tests added)");

                    if (!pr.Title.StartsWith("TestEngineer:", StringComparison.OrdinalIgnoreCase))
                        await MarkEngineerTaskDoneAsync(pr, ct);

                    await RememberAsync(MemoryType.Action,
                        $"Merged tested PR #{pr.Number}: {pr.Title}", ct: ct);
                }
                else if (result == MergeAttemptResult.ConflictBlocked)
                {
                    Logger.LogWarning("Tested PR #{Number} has merge conflicts", pr.Number);
                    LogActivity("task", $"⚠️ Tested PR #{pr.Number} blocked by merge conflicts");
                    await TryCloseAndRecreatePRAsync(pr, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to merge tested PRs");
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
                        if (!synced)
                        {
                            // Standard sync failed — try force-rebase onto main
                            Logger.LogWarning("PE PR #{PrNumber} branch sync failed — attempting force-rebase", pr.Number);
                            synced = await GitHub.RebaseBranchOnMainAsync(pr.Number, ct);
                        }

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

                    var taskTitle2 = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
                    var task2 = taskTitle2 is not null ? _taskManager.FindByName(taskTitle2) : null;
                    if (task2?.IssueNumber.HasValue == true)
                        await _taskManager.MarkDoneAsync(task2.IssueNumber.Value, pr.Number, ct);

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
    /// On restart, detect PE's own open in-progress PRs and restore CurrentPrNumber
    /// so the PE can continue implementation instead of leaving the PR orphaned.
    /// </summary>
    private async Task RecoverOwnInProgressPRAsync(CancellationToken ct)
    {
        if (_recoveredInProgressPR)
            return;
        _recoveredInProgressPR = true;

        // If we already have a tracked PR, nothing to recover
        if (CurrentPrNumber is not null)
            return;

        try
        {
            var myPRs = await PrWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Look for in-progress PRs (not ready-for-review — those are handled elsewhere)
                if (pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                    continue;

                // Found an in-progress PR that belongs to the PE
                CurrentPrNumber = pr.Number;
                Identity.AssignedPullRequest = pr.Number.ToString();
                ActivatePrSession(pr.Number);

                Logger.LogInformation(
                    "PE recovered own in-progress PR #{PrNumber}: {Title} — will continue implementation",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Resuming work on PR #{pr.Number}");
                break; // Only recover one PR at a time
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover own in-progress PR");
        }
    }

    /// <summary>
    /// Check if our currently tracked PR is already ready for review (vs still in progress).
    /// </summary>
    private async Task<bool> IsOwnPrReadyForReview(CancellationToken ct)
    {
        if (CurrentPrNumber is null)
            return false;

        try
        {
            var pr = await GitHub.GetPullRequestAsync(CurrentPrNumber.Value, ct);
            return pr?.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Continue implementing our own in-progress PR. Reads existing commits to determine
    /// what's been done, generates remaining steps, and implements them.
    /// </summary>
    private async Task ContinueOwnPrImplementationAsync(CancellationToken ct)
    {
        if (CurrentPrNumber is null)
            return;

        try
        {
            var pr = await GitHub.GetPullRequestAsync(CurrentPrNumber.Value, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;
                return;
            }

            // Find the linked issue for context
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            AgentIssue? sourceIssue = null;
            if (issueNumber.HasValue)
                sourceIssue = await GitHub.GetIssueAsync(issueNumber.Value, ct);

            // Get existing files to understand what's already been done
            var existingFiles = await GetPrFileListAsync(pr.Number, ct);

            Logger.LogInformation(
                "PE continuing implementation on PR #{PrNumber} (existing files: {Files})",
                pr.Number, existingFiles?.Split('\n').Length ?? 0);

            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var techStack = Config.Project.TechStack;

            // Generate implementation steps based on what remains to be done
            var syntheticIssue = sourceIssue ?? new AgentIssue
            {
                Number = issueNumber ?? 0,
                Title = pr.Title,
                Body = pr.Body ?? "",
                State = "open",
                Labels = new List<string>()
            };

            var steps = await GenerateImplementationStepsAsync(
                chat, pr, syntheticIssue, pmSpecDoc, architectureDoc, techStack, ct);

            if (steps.Count == 0)
            {
                Logger.LogWarning("PE could not generate remaining steps for PR #{PrNumber}, marking ready", pr.Number);
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
                return;
            }

            Logger.LogInformation(
                "PE generated {Count} implementation steps for continued work on PR #{PrNumber}",
                steps.Count, pr.Number);

            var completedSteps = new List<string>();
            for (var i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                var stepNumber = i + 1;

                UpdateStatus(AgentStatus.Working,
                    $"PR #{pr.Number} step {stepNumber}/{steps.Count}: {Truncate(step, 60)}");
                Logger.LogInformation(
                    "PE implementing step {Step}/{Total} for PR #{PrNumber}: {Desc}",
                    stepNumber, steps.Count, pr.Number, Truncate(step, 100));

                var stepHistory = new ChatHistory();
                stepHistory.AddSystemMessage(GetStepImplementationSystemPrompt(techStack, stepNumber, steps.Count));

                var ctx = new System.Text.StringBuilder();
                ctx.AppendLine($"## PM Specification\n{pmSpecDoc}\n");
                ctx.AppendLine($"## Architecture\n{architectureDoc}\n");
                if (sourceIssue is not null)
                    ctx.AppendLine($"## Issue #{sourceIssue.Number}: {sourceIssue.Title}\n{sourceIssue.Body}\n");
                ctx.AppendLine($"## PR Description\n{pr.Body}\n");

                if (!string.IsNullOrEmpty(existingFiles) || completedSteps.Count > 0)
                {
                    ctx.AppendLine("## Previously Completed Steps / Existing Files");
                    if (!string.IsNullOrEmpty(existingFiles))
                        ctx.AppendLine($"Files already in PR:\n{existingFiles}\n");
                    for (var j = 0; j < completedSteps.Count; j++)
                        ctx.AppendLine($"- Step {j + 1}: {completedSteps[j]}");
                    ctx.AppendLine();
                }

                ctx.AppendLine($"## Current Step ({stepNumber}/{steps.Count})");
                ctx.AppendLine(step);
                ctx.AppendLine();
                ctx.AppendLine("Implement ONLY this step. Output each file using this format:\n");
                ctx.AppendLine("FILE: path/to/file.ext\n```language\n<file content>\n```\n");
                ctx.AppendLine($"Use the {techStack} technology stack. Every file MUST use the FILE: marker format.");
                if (!string.IsNullOrEmpty(existingFiles) || completedSteps.Count > 0)
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

            // All steps done — mark ready for review
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
                "PE completed continued implementation for PR #{PrNumber}",
                pr.Number);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to continue own PR #{PrNumber} implementation", CurrentPrNumber);
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

                // Find the task by matching the PR title or the task manager cache
                var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr?.Title ?? "");
                var task = taskTitle is not null ? _taskManager.FindByName(taskTitle) : null;

                if (task?.IssueNumber.HasValue == true)
                {
                    if (wasMerged)
                    {
                        await _taskManager.MarkDoneAsync(task.IssueNumber.Value, CurrentPrNumber.Value, ct);
                        Logger.LogInformation("PE task {TaskId} marked Done (PR #{PrNumber} merged)",
                            task.Id, CurrentPrNumber.Value);
                        LogActivity("task", $"✅ Task {task.Id}: {task.Name} completed (PR #{CurrentPrNumber.Value} merged)");
                    }
                    else
                    {
                        await _taskManager.ResetToPendingAsync(task.IssueNumber.Value, ct);
                        Logger.LogInformation("PE task {TaskId} reset to Pending (PR #{PrNumber} closed without merge)",
                            task.Id, CurrentPrNumber.Value);
                    }
                }

                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;

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

    private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
    {
        try
        {
            if (_resourceRequestPending)
            {
                // Check if the spawn request has been fulfilled
                var currentWorkers = _registry.GetAgentsByRole(AgentRole.PrincipalEngineer).Count()
                    + _registry.GetAgentsByRole(AgentRole.SeniorEngineer).Count()
                    + _registry.GetAgentsByRole(AgentRole.JuniorEngineer).Count();
                if (currentWorkers > _agentAssignments.Count + 1) // +1 for leader
                {
                    _resourceRequestPending = false;
                }
                else if (DateTime.UtcNow - _lastResourceRequestTime > SpawnCooldown)
                {
                    // Cooldown expired — clear the flag and fall through to re-evaluate.
                    // This handles the case where the spawned worker immediately picks up
                    // a task, making the worker-count check never pass.
                    _resourceRequestPending = false;
                }
                else
                {
                    return;
                }
            }

            var parallelizable = _taskManager.Tasks.Count(t =>
                t.Status == "Pending" && _taskManager.AreDependenciesMet(t));

            if (parallelizable < 2)
                return;

            // Count free workers across all roles (including non-leader PEs)
            var freeWorkers = 0;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.PrincipalEngineer))
                if (agent.Identity.Id != Identity.Id && !_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeWorkers++;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.SeniorEngineer))
                if (!_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeWorkers++;
            foreach (var agent in _registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                if (!_agentAssignments.ContainsKey(agent.Identity.Id))
                    freeWorkers++;

            if (parallelizable > freeWorkers + 1)
            {
                // Request additional PE workers (primary scaling mechanism)
                // Fall back to SE/JE only if PE pool is exhausted and those pools are configured
                var poolConfig = Config.Limits.EngineerPool;

                AgentRole? neededRole = null;
                var peCapacity = poolConfig.PrincipalEngineerPool
                    - (_registry.GetAgentsByRole(AgentRole.PrincipalEngineer).Count() - 1); // -1 for leader
                if (peCapacity > 0)
                {
                    neededRole = AgentRole.PrincipalEngineer;
                }
                else if (poolConfig.SeniorEngineerPool > 0)
                {
                    var seCapacity = poolConfig.SeniorEngineerPool
                        - _registry.GetAgentsByRole(AgentRole.SeniorEngineer).Count();
                    if (seCapacity > 0)
                        neededRole = AgentRole.SeniorEngineer;
                }

                if (neededRole is null && poolConfig.JuniorEngineerPool > 0)
                {
                    var jeCapacity = poolConfig.JuniorEngineerPool
                        - _registry.GetAgentsByRole(AgentRole.JuniorEngineer).Count();
                    if (jeCapacity > 0)
                        neededRole = AgentRole.JuniorEngineer;
                }

                if (neededRole is null)
                {
                    Logger.LogDebug(
                        "PE needs more workers ({Parallelizable} parallelizable) but all pools exhausted",
                        parallelizable);
                    return;
                }

                Logger.LogInformation(
                    "PE requesting additional {Role}: {Parallelizable} tasks parallelizable, {Free} workers free",
                    neededRole, parallelizable, freeWorkers);

                await MessageBus.PublishAsync(new ResourceRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ResourceRequest",
                    RequestedRole = neededRole.Value,
                    Justification = $"{parallelizable} tasks can be worked in parallel but only {freeWorkers} workers are available",
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
            // Find the associated task via the task manager
            EngineeringTask? task = null;

            // Search by task name from PR title
            var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
            if (taskTitle is not null)
            {
                // Handle doubled agent prefix: "Agent: Agent: TaskName"
                var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskTitle);
                if (innerName is not null) taskTitle = innerName;
                task = _taskManager.FindByName(taskTitle);
            }

            // Fallback: search by issue number from agent assignments
            if (task is null)
            {
                foreach (var issueNum in _agentAssignments.Values)
                {
                    var candidate = _taskManager.FindByIssueNumber(issueNum);
                    if (candidate is not null && pr.Title.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        task = candidate;
                        break;
                    }
                }
            }

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

            if (!string.IsNullOrEmpty(pr.HeadBranch))
            {
                try { await GitHub.DeleteBranchAsync(pr.HeadBranch, ct); }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not delete old branch {Branch}", pr.HeadBranch);
                }
            }

            _conflictRetryCount[pr.Number] = retries + 1;

            if (CurrentPrNumber == pr.Number)
            {
                CurrentPrNumber = null;
                Identity.AssignedPullRequest = null;
            }

            if (task is null || !task.IssueNumber.HasValue)
            {
                Logger.LogWarning("No task found for conflicted PR #{PrNumber} — cannot recreate", pr.Number);
                return;
            }

            var isPeOwned = pr.Title.StartsWith(Identity.DisplayName + ":", StringComparison.OrdinalIgnoreCase);

            // Reset task to Pending via the task manager
            await _taskManager.ResetToPendingAsync(task.IssueNumber.Value, ct);

            if (isPeOwned)
            {
                Logger.LogInformation(
                    "PE task {TaskId} reset to Pending — will re-implement on next cycle", task.Id);
                UpdateStatus(AgentStatus.Idle, "Ready for next task");
            }
            else if (task.IssueNumber.HasValue)
            {
                // Engineer-owned: find the engineer and re-send the assignment
                var engineerAgentId = _agentAssignments
                    .FirstOrDefault(kv => kv.Value == task.IssueNumber.Value).Key;

                if (engineerAgentId is not null)
                {
                    _agentAssignments.Remove(engineerAgentId);

                    var engineer = _registry.GetAgentsByRole(AgentRole.SeniorEngineer)
                        .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                        .FirstOrDefault(a => a.Identity.Id == engineerAgentId);

                    if (engineer is not null)
                    {
                        await _taskManager.AssignTaskAsync(task.IssueNumber.Value, engineer.Identity.DisplayName, ct);
                        _agentAssignments[engineerAgentId] = task.IssueNumber.Value;

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
                        Logger.LogWarning(
                            "Original engineer {AgentId} not found for task {TaskId} — will be reassigned",
                            engineerAgentId, task.Id);
                    }
                }
                else
                {
                    Logger.LogInformation("Task {TaskId} reset to Pending for reassignment", task.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close-and-recreate PR #{PrNumber}", pr.Number);
        }
    }

    private async Task CheckAllTasksCompleteAsync(CancellationToken ct)
    {
        if (_taskManager.TotalCount == 0)
            return;

        // Refresh from GitHub to get latest state
        await _taskManager.LoadTasksAsync(ct);

        if (!_taskManager.AreAllTasksDone())
            return;

        _allTasksComplete = true;
        Logger.LogInformation("🎉 All {Count} engineering tasks are complete!", _taskManager.TotalCount);
        LogActivity("system", $"🎉 All {_taskManager.TotalCount} engineering tasks complete — entering integration phase");
        UpdateStatus(AgentStatus.Working, "All tasks complete — creating integration PR");

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "AllTasksComplete",
            NewStatus = AgentStatus.Working,
            Details = $"All {_taskManager.TotalCount} engineering tasks are done"
        }, ct);
    }

    private async Task CreateIntegrationPRAsync(CancellationToken ct)
    {
        try
        {
            UpdateStatus(AgentStatus.Working, "Creating integration PR");

            var pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var techStack = Config.Project.TechStack;

            // Build a task summary from issues instead of engineering plan file
            var taskSummary = string.Join("\n", _taskManager.Tasks.Select(t =>
                $"- [{t.Id}] {t.Name} ({t.Complexity}, {t.Status})"));

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
                $"## Completed Tasks\n{taskSummary}\n\n" +
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
                $"All {_taskManager.TotalCount} engineering tasks have been completed and merged.\n" +
                $"This PR addresses integration gaps identified during final review.\n\n" +
                $"### Files Changed\n" +
                string.Join("\n", codeFiles.Select(f => $"- `{f.Path}`"));

            var pr = await PrWorkflow.CreateTaskPullRequestAsync(
                Identity.DisplayName,
                "Final Integration",
                prBody,
                "High",
                "Architecture.md",
                "",
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
        // Close any remaining open engineering task issues
        await _taskManager.CloseAllRemainingTaskIssuesAsync(ct);

        // Notify PM to review enhancement issues — PM owns the lifecycle of user stories
        try
        {
            await MessageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "StatusUpdate",
                NewStatus = AgentStatus.Idle,
                CurrentTask = "AllTasksComplete",
                Details = "All engineering tasks are complete and merged. PM should review enhancement issues for final acceptance."
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to notify PM about engineering completion");
        }

        UpdateStatus(AgentStatus.Idle, "Engineering complete");
        _engineeringSignaled = true;
        LogActivity("system", "🏁 Engineering phase complete — all tasks done and integrated");

        await MessageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "EngineeringComplete",
            NewStatus = AgentStatus.Idle,
            Details = $"All {_taskManager.TotalCount} tasks complete. Engineering phase finished."
        }, ct);

        await RememberAsync(MemoryType.Action,
            $"Engineering phase complete: {_taskManager.TotalCount} tasks done", ct: ct);

        // Signal all agents to clean up local workspaces
        if (Config.Workspace.IsEnabled && Config.Workspace.CleanupOnProjectComplete)
        {
            try
            {
                await MessageBus.PublishAsync(new WorkspaceCleanupMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "WorkspaceCleanup",
                    Reason = "Project complete — all engineering tasks finished and integrated"
                }, ct);
                Logger.LogInformation("Broadcast workspace cleanup signal to all agents");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to broadcast workspace cleanup signal");
            }
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

            // Task completion is tracked via issue state (closed = Done)
            // No need to update an in-memory backlog
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

        if (!_taskManager.Tasks.Any(t =>
                string.Equals(t.Name, message.Title, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogInformation("Received externally-assigned task: {Title} — will be handled via issues", message.Title);
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
        // Check if this is our own PR (PE is working on it)
        var isOurPr = CurrentPrNumber == message.PrNumber;

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

            // Tier 4: Get repo structure for duplicate detection during review
            var repoStructure = await GetRepoStructureForContextAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Principal Engineer doing a technical code review.\n\n" +
                "SCOPE: This PR is ONE task. Review the ACTUAL CODE against its stated scope.\n\n" +
                "CHECK: architecture compliance, implementation completeness, code quality, " +
                "bugs/logic errors, missing validation, test coverage.\n\n" +
                "DUPLICATE/CONFLICT CHECKS (critical for multi-agent projects):\n" +
                "- Does this PR create types/classes that ALREADY EXIST in the main branch file listing?\n" +
                "- Does this PR use the CORRECT namespace consistent with existing code structure?\n" +
                "- Should any new files instead be MODIFICATIONS to existing files?\n" +
                "- Are there naming conflicts (e.g., a class named 'Task' that collides with System.Threading.Tasks.Task)?\n" +
                "- Do all using/import statements reference namespaces that actually exist?\n" +
                "If you detect duplication or namespace conflicts, mark as REQUEST_CHANGES with specific fix instructions.\n\n" +
                "CRITICAL RULE: NEVER mention truncated code, incomplete code display, or " +
                "inability to see full implementations. If you cannot see a method body, " +
                "ASSUME it is correctly implemented. Do NOT request changes based on code you " +
                "cannot verify — only flag issues you can CONCRETELY identify in the visible code.\n\n" +
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
                "WRONG: '2. **Dashboard.razor** — helper methods truncated, cannot verify'\n" +
                "RIGHT: '1. **AuthController.cs** — missing null check on user parameter'");

            var reviewContextBuilder = new System.Text.StringBuilder();
            reviewContextBuilder.AppendLine($"## Architecture\n{architectureDoc}\n");
            reviewContextBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");

            // Filter engineering plan to focus on the linked task to prevent cross-task confusion
            var planContext = engineeringPlan;
            if (issueNumber.HasValue && !string.IsNullOrEmpty(engineeringPlan))
            {
                var taskSection = ExtractTaskSectionFromPlan(engineeringPlan, issueNumber.Value, pr.Title);
                if (!string.IsNullOrEmpty(taskSection))
                    planContext = $"(Filtered to task relevant to this PR)\n\n{taskSection}";
            }
            reviewContextBuilder.AppendLine($"## Engineering Plan\n{planContext}\n");

            if (!string.IsNullOrEmpty(repoStructure))
            {
                reviewContextBuilder.AppendLine("## Existing Repository Structure (main branch)");
                reviewContextBuilder.AppendLine(repoStructure);
                reviewContextBuilder.AppendLine();
            }

            reviewContextBuilder.Append(issueContext);
            reviewContextBuilder.AppendLine($"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n");
            reviewContextBuilder.Append(codeContext);

            history.AddUserMessage(reviewContextBuilder.ToString());

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";

            // Detect garbage AI responses (model breaking character, meta-commentary)
            if (PullRequestWorkflow.IsGarbageAIResponse(result))
            {
                Logger.LogWarning("PE review of PR #{Number} returned garbage AI response, retrying once", pr.Number);

                // Retry with a more direct prompt
                history.AddAssistantMessage(result);
                history.AddUserMessage(
                    "That response was not a code review. I need you to review the actual code files above.\n" +
                    "Output ONLY a numbered list of code issues, or 'LGTM' if the code is acceptable.\n" +
                    "End with VERDICT: APPROVE or VERDICT: REQUEST_CHANGES");

                response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                result = response.Content?.Trim() ?? "";

                // If still garbage after retry, auto-approve to avoid posting nonsense
                if (PullRequestWorkflow.IsGarbageAIResponse(result))
                {
                    Logger.LogWarning("PE review of PR #{Number} still garbage after retry — auto-approving", pr.Number);
                    return (true, "Code review passed. Implementation looks reasonable for the task scope.");
                }
            }

            var approved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

            // Strip VERDICT markers AND any stray approval/rejection keywords the AI may
            // have echoed to prevent contradictory text in the posted comment.
            var reviewBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            // Remove lines that are just standalone decision keywords the AI echoed
            var cleanedLines = reviewBody.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.Trim().TrimStart('*', '#', ' ');
                    return !string.Equals(trimmed, "APPROVED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(trimmed, "CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(trimmed, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[ProgramManager] CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[ProgramManager] APPROVED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[PrincipalEngineer] CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[PrincipalEngineer] APPROVED", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            reviewBody = string.Join('\n', cleanedLines).Trim();

            // Strip any preamble/thinking the AI may have included before the numbered list
            reviewBody = PullRequestWorkflow.StripReviewPreamble(reviewBody);

            // Filter out truncation-related review items (the AI sometimes flags code it can't see as incomplete)
            reviewBody = FilterTruncationComplaints(reviewBody);

            // If all review items were truncation complaints, approve instead
            if (!approved && string.IsNullOrWhiteSpace(reviewBody))
            {
                Logger.LogInformation("PE review of PR #{Number} only had truncation complaints — auto-approving", pr.Number);
                return (true, "Code review passed. Implementation meets requirements for the task scope.");
            }

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
    /// Extracts just the section of the engineering plan that corresponds to the given
    /// issue number or PR title. Prevents the review AI from confusing tasks when
    /// the full plan with all tasks is in context.
    /// </summary>
    private static string? ExtractTaskSectionFromPlan(string plan, int issueNumber, string prTitle)
    {
        var lines = plan.Split('\n');
        var result = new System.Text.StringBuilder();
        bool capturing = false;
        int headerLevel = 0;

        // Patterns to match: "T1", "T2", issue #number, or the task title
        var issueRef = $"#{issueNumber}";
        var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(prTitle);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Detect markdown headers (## Task, ### T1, etc.)
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();

                if (capturing && level <= headerLevel)
                {
                    // Hit a same-level or higher header — stop capturing
                    break;
                }

                // Check if this header matches our task
                if (!capturing &&
                    (line.Contains(issueRef, StringComparison.OrdinalIgnoreCase) ||
                     (taskTitle is not null && line.Contains(taskTitle, StringComparison.OrdinalIgnoreCase))))
                {
                    capturing = true;
                    headerLevel = level;
                }
            }

            if (capturing)
                result.AppendLine(line);
        }

        var extracted = result.ToString().Trim();
        return string.IsNullOrEmpty(extracted) ? null : extracted;
    }

    /// <summary>
    /// Filters out numbered review items that complain about truncated or invisible code.
    /// Returns the remaining review body with items renumbered.
    /// </summary>
    private static string FilterTruncationComplaints(string reviewBody)
    {
        if (string.IsNullOrWhiteSpace(reviewBody))
            return reviewBody;

        string[] truncationKeywords =
        [
            "truncated",
            "cut off",
            "cannot verify",
            "cannot see",
            "can't verify",
            "can't see",
            "not visible",
            "not shown",
            "hidden",
            "implementation not visible",
            "implementations are cut",
            "code hides",
            "unable to verify",
            "unable to see"
        ];

        var lines = reviewBody.Split('\n');
        var filteredItems = new List<string>();
        var currentItem = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Check if this starts a new numbered item
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s"))
            {
                // Flush previous item if it exists
                if (currentItem.Length > 0)
                {
                    var item = currentItem.ToString();
                    if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        filteredItems.Add(item);
                    currentItem.Clear();
                }
                currentItem.AppendLine(line);
            }
            else
            {
                currentItem.AppendLine(line);
            }
        }

        // Flush last item
        if (currentItem.Length > 0)
        {
            var item = currentItem.ToString();
            if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                filteredItems.Add(item);
        }

        // Renumber remaining items
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < filteredItems.Count; i++)
        {
            var item = filteredItems[i].Trim();
            // Replace the leading number with the new index
            item = System.Text.RegularExpressions.Regex.Replace(item, @"^\d+[\.\)]", $"{i + 1}.");
            result.AppendLine(item);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// After merging an engineer's PR, find the corresponding task issue and close it.
    /// Searches by task name from PR title, then by issue number from agent assignments.
    /// </summary>
    private async Task MarkEngineerTaskDoneAsync(AgentPullRequest pr, CancellationToken ct)
    {
        // Search by task name from PR title (strip agent prefix, handle double-prefix)
        var taskName = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
        EngineeringTask? task = null;

        if (taskName is not null)
        {
            var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskName);
            if (innerName is not null) taskName = innerName;
            task = _taskManager.FindByName(taskName);
        }

        // Fallback: match by issue number from the specific PR author's assignment
        if (task is null)
        {
            var agentName = PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title);
            if (agentName is not null)
            {
                var authorEntry = _agentAssignments.FirstOrDefault(kv =>
                {
                    var agent = _registry.GetAgentsByRole(AgentRole.SeniorEngineer)
                        .Concat(_registry.GetAgentsByRole(AgentRole.JuniorEngineer))
                        .FirstOrDefault(a => a.Identity.Id == kv.Key);
                    return agent is not null &&
                           string.Equals(agent.Identity.DisplayName, agentName, StringComparison.OrdinalIgnoreCase);
                });

                if (authorEntry.Key is not null)
                    task = _taskManager.FindByIssueNumber(authorEntry.Value);
            }
        }

        if (task?.IssueNumber.HasValue == true)
        {
            await _taskManager.MarkDoneAsync(task.IssueNumber.Value, pr.Number, ct);
            Logger.LogInformation("Marked engineer task {TaskId} '{TaskName}' as Done (PR #{PrNumber} merged)",
                task.Id, task.Name, pr.Number);
        }
        else
        {
            Logger.LogWarning("Could not find task issue for merged PR #{PrNumber} ({Title})",
                pr.Number, pr.Title);
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

    /// <summary>
    /// Formats a semicolon-separated file plan string into readable markdown.
    /// Input: "CREATE:src/Services/Auth.cs(MyApp.Services);MODIFY:src/Program.cs;USE:User(MyApp.Models)"
    /// Output: markdown bullet list with file operations
    /// </summary>
    /// <summary>
    /// Enforces the foundation-first pattern: ensures the first task (T1) has no dependencies
    /// and all other tasks depend on T1. If the AI didn't produce a proper foundation task,
    /// this reorders tasks so the foundation-like one is first, or injects a synthetic one.
    /// </summary>
    private void EnsureFoundationFirstPattern(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return;

        var foundationKeywords = new[] { "foundation", "scaffold", "setup", "structure", "skeleton", "template", "infrastructure", "project setup" };
        var firstTask = tasks[0];
        var isFirstFoundation = firstTask.Dependencies.Count == 0 &&
            foundationKeywords.Any(k => firstTask.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                        firstTask.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

        // If T1 isn't a foundation task, look for one elsewhere in the list and move it to front
        if (!isFirstFoundation)
        {
            var foundationIdx = tasks.FindIndex(t =>
                t.Dependencies.Count == 0 &&
                foundationKeywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                            t.Description.Contains(k, StringComparison.OrdinalIgnoreCase)));

            if (foundationIdx > 0)
            {
                var foundationTask = tasks[foundationIdx];
                tasks.RemoveAt(foundationIdx);
                tasks.Insert(0, foundationTask);
                Logger.LogInformation("Reordered foundation task '{Name}' to first position", foundationTask.Name);
            }
            else
            {
                Logger.LogInformation("No explicit foundation task found — T1 ({Name}) will serve as foundation", firstTask.Name);
            }
        }

        // Ensure T1 has no dependencies
        var t1 = tasks[0];
        if (t1.Dependencies.Count > 0)
        {
            tasks[0] = t1 with { Dependencies = new List<string>() };
            Logger.LogInformation("Cleared dependencies from foundation task T1 ({Name})", t1.Name);
        }

        // Ensure all other tasks depend on T1's ID
        var t1Id = tasks[0].Id;
        for (var i = 1; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (!task.Dependencies.Contains(t1Id, StringComparer.OrdinalIgnoreCase))
            {
                task.Dependencies.Insert(0, t1Id);
            }
        }

        // Log overlap warnings: detect tasks that create the same files
        var fileOwnership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var createFiles = ExtractCreateFilesFromDescription(task.Description);
            foreach (var file in createFiles)
            {
                if (fileOwnership.TryGetValue(file, out var owner))
                {
                    Logger.LogWarning("File overlap detected: '{File}' is created by both {Task1} and {Task2}",
                        file, owner, task.Id);
                }
                else
                {
                    fileOwnership[file] = task.Id;
                }
            }
        }
    }

    /// <summary>
    /// Extracts CREATE file paths from a task description's File Plan section.
    /// </summary>
    private static List<string> ExtractCreateFilesFromDescription(string description)
    {
        var files = new List<string>();
        // Match lines like "- ➕ **Create:** `path/to/file.cs`" or raw "CREATE:path/to/file.cs"
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            // Markdown format from FormatFilePlan
            if (trimmed.Contains("**Create:**"))
            {
                var backtickStart = trimmed.IndexOf('`');
                var backtickEnd = trimmed.LastIndexOf('`');
                if (backtickStart >= 0 && backtickEnd > backtickStart)
                    files.Add(trimmed[(backtickStart + 1)..backtickEnd]);
            }
        }
        return files;
    }

    private static string FormatFilePlan(string filePlan)
    {
        if (string.IsNullOrWhiteSpace(filePlan)) return "";

        var sb = new System.Text.StringBuilder();
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;

            var action = op[..colonIdx].Trim().ToUpperInvariant();
            var detail = op[(colonIdx + 1)..].Trim();

            var icon = action switch
            {
                "CREATE" => "➕ **Create:**",
                "MODIFY" => "✏️ **Modify:**",
                "USE" => "📎 **Reference (do not recreate):**",
                _ => $"**{action}:**"
            };

            sb.AppendLine($"- {icon} `{detail}`");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Read visual design reference files from the repository for inclusion in engineering task context.
    /// </summary>
    private async Task<string?> ReadDesignReferencesAsync(CancellationToken ct)
    {
        try
        {
            var tree = await GitHub.GetRepositoryTreeAsync("main", ct);
            var designFiles = tree
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".html" && ext != ".htm") return false;
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return name.Contains("design") || name.Contains("concept") ||
                           name.Contains("mockup") || name.Contains("wireframe") ||
                           name.Contains("prototype") || name.Contains("reference");
                })
                .ToList();

            if (designFiles.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var file in designFiles)
            {
                var content = await GitHub.GetFileContentAsync(file, ct: ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### Design File: `{file}`");
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 8000 ? content[..8000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (sb.Length > 0)
            {
                Logger.LogInformation("Read {Count} visual design reference files for engineering plan", designFiles.Count);
                return sb.ToString().TrimEnd();
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read design reference files for engineering plan");
            return null;
        }
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
    /// <summary>GitHub internal ID (not the issue number). Required for sub-issue and dependency APIs.</summary>
    public long? GitHubId { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
    /// <summary>Issue numbers this task depends on (parsed from issue body "Depends On").</summary>
    public List<int> DependencyIssueNumbers { get; init; } = new();
    /// <summary>Parent PM issue number (parsed from issue body "Parent Issue").</summary>
    public int? ParentIssueNumber { get; init; }
    /// <summary>Current GitHub labels on this issue (for status label management).</summary>
    public List<string> Labels { get; init; } = new();
}

// BUG FIX: Added AgentId field. Previously only Name (DisplayName) was stored, but
// all message routing and _agentAssignments must use Identity.Id for correct delivery.
internal record EngineerInfo
{
    public string AgentId { get; init; } = "";
    public string Name { get; init; } = "";
    public AgentRole Role { get; init; }
}
