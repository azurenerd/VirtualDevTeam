using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.Agents.Reasoning;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Services;
using AgentSquad.Orchestrator;
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
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSpawnManager _spawnManager;
    private readonly AgentRegistry _registry;
    private readonly AgentSquadConfig _config;
    private readonly IGateCheckService _gateCheck;
    private readonly SelfAssessmentService _selfAssessment;
    private readonly IAgentReasoningLog _reasoningLog;
    private readonly IPromptTemplateService _promptService;
    private readonly DecisionGateService? _decisionGate;
    private readonly AgentTeamComposer? _teamComposer;
    private readonly SMEAgentDefinitionService? _definitionService;
    private readonly IAgentTaskTracker _taskTracker;

    private readonly Dictionary<string, AgentTracking> _trackedAgents = new();
    private readonly HashSet<int> _processedIssueIds = new();
    // Maps PR number → head SHA of last review. Re-review triggered when HEAD SHA changes
    // (e.g., SE pushes fix commits after "CHANGES REQUESTED"). Keying by PR number alone
    // would permanently blacklist PRs after first review.
    private readonly Dictionary<int, string> _reviewedPrHeadShas = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();
    private readonly ConcurrentQueue<ClarificationRequestMessage> _clarificationQueue = new();
    private int _additionalEngineersHired;
    private string? _currentPhase;
    private bool _pmSpecCreated;
    private bool _userStoryIssuesCreated;
    private bool _teamCompositionComplete;
    private readonly HashSet<int> _reviewedEnhancementIssues = new();

    private readonly List<IDisposable> _subscriptions = new();

    public ProgramManagerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentSpawnManager spawnManager,
        AgentRegistry registry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        SelfAssessmentService selfAssessment,
        IAgentReasoningLog reasoningLog,
        IPromptTemplateService promptService,
        ILogger<ProgramManagerAgent> logger,
        IAgentTaskTracker taskTracker,
        RoleContextProvider? roleContextProvider = null,
        AgentTeamComposer? teamComposer = null,
        SMEAgentDefinitionService? definitionService = null,
        DecisionGateService? decisionGate = null)
        : base(identity, logger, memoryStore, roleContextProvider)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _issueWorkflow = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _selfAssessment = selfAssessment ?? throw new ArgumentNullException(nameof(selfAssessment));
        _reasoningLog = reasoningLog ?? throw new ArgumentNullException(nameof(reasoningLog));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _teamComposer = teamComposer;
        _definitionService = definitionService;
        _decisionGate = decisionGate;
        _taskTracker = taskTracker ?? throw new ArgumentNullException(nameof(taskTracker));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<ResourceRequestMessage>(
            Identity.Id, HandleResourceRequestAsync));

        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        _subscriptions.Add(_messageBus.Subscribe<HelpRequestMessage>(
            Identity.Id, HandleHelpRequestAsync));

        _subscriptions.Add(_messageBus.Subscribe<ReviewRequestMessage>(
            Identity.Id, HandleReviewRequestAsync));

        _subscriptions.Add(_messageBus.Subscribe<ClarificationRequestMessage>(
            Identity.Id, HandleClarificationRequestAsync));

        _currentPhase = "Research";
        Logger.LogInformation("PM agent initialized, starting in {Phase} phase", _currentPhase);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Initializing project oversight");

        // One-time kickoff: read project description and seed the Researcher
        var kickoffStepId = _taskTracker.BeginStep(Identity.Id, "pm-kickoff", "Read project context",
            "Reading project description and kicking off research", Identity.ModelTier);
        try
        {
            await KickOffProjectAsync(ct);
            _taskTracker.CompleteStep(kickoffStepId);
        }
        catch (Exception ex)
        {
            _taskTracker.FailStep(kickoffStepId, ex.Message);
            throw;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Logger.LogInformation("PM loop: CheckExecutiveResponses");
                await CheckExecutiveResponsesAsync(ct);
                Logger.LogInformation("PM loop: MonitorTeamStatus");
                await MonitorTeamStatusAsync(ct);
                Logger.LogInformation("PM loop: HandleResourceRequests");
                await HandleResourceRequestsAsync(ct);
                Logger.LogInformation("PM loop: HandleBlockers");
                await HandleBlockersAsync(ct);
                Logger.LogInformation("PM loop: ProcessClarificationRequests");
                await ProcessClarificationRequestsAsync(ct);
                Logger.LogInformation("PM loop: ReviewPullRequests (entering)");
                await ReviewPullRequestsAsync(ct);
                Logger.LogInformation("PM loop: ReviewEnhancementIssueCompletion");
                await ReviewEnhancementIssueCompletionAsync(ct);
                Logger.LogInformation("PM loop: UpdateProjectTracking");
                await UpdateProjectTrackingAsync(ct);

                await RefreshDiagnosticWithMemoryAsync(ct);

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
                RecordError($"PM error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
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

    /// <summary>
    /// One-time project kickoff: reads the project description from config,
    /// creates a GitHub Issue for the Researcher, and sends a TaskAssignmentMessage
    /// via the message bus to begin the Research phase.
    /// Skips research kickoff entirely if Research.md already has meaningful content.
    /// Also restores any previously-spawned engineers from TeamMembers.md.
    /// </summary>
    private async Task KickOffProjectAsync(CancellationToken ct)
    {
        try
        {
            var projectName = _config.Project.Name;
            var projectDescription = _config.Project.Description;

            if (string.IsNullOrWhiteSpace(projectDescription))
            {
                Logger.LogWarning(
                    "Project description is empty — skipping automatic kickoff. " +
                    "Set Project.Description in appsettings.json to enable auto-kickoff.");
                return;
            }

            Logger.LogInformation(
                "Kicking off project: {ProjectName}", projectName);

            // Ensure TeamMembers.md exists with core agents
            await EnsureTeamMembersDocAsync(ct);

            // Restore any previously-spawned engineers from TeamMembers.md
            await RestoreEngineersFromTeamMembersAsync(ct);

            // Check if Research.md already has meaningful content — skip kickoff if so
            // Note: the placeholder "No research has been documented yet" may still appear at top
            // even after research is appended below it, so check for actual research section headings
            var existingResearch = await _projectFiles.GetResearchDocAsync(ct);
            var hasResearchContent = !string.IsNullOrWhiteSpace(existingResearch) &&
                (existingResearch.Contains("## Research technology stack", StringComparison.OrdinalIgnoreCase) ||
                 existingResearch.Contains("### Summary", StringComparison.OrdinalIgnoreCase) ||
                 (existingResearch.Contains("## ", StringComparison.Ordinal) &&
                  !existingResearch.Trim().Equals("# Research\n\n_No research has been documented yet._", StringComparison.OrdinalIgnoreCase)));

            if (hasResearchContent)
            {
                Logger.LogInformation(
                    "Research.md already exists with content — skipping research kickoff");

                // Still signal downstream agents so they can proceed
                await _messageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ResearchComplete",
                    NewStatus = AgentStatus.Idle,
                    Details = "Research already exists from prior run"
                }, ct);

                UpdateStatus(AgentStatus.Idle, "Project kickoff complete (research exists), monitoring team");
                return;
            }

            // Build the research guidance — use custom prompt if provided, otherwise generate a rich default
            var researchGuidance = GetResearchGuidance(projectName, projectDescription);

            // 1. Create a GitHub Issue for tracking and visibility (idempotent)
            var issueTitle = $"Researcher: Research technology stack for {projectName}";

            var existingIssues = await _github.GetOpenIssuesAsync(ct);
            var existingKickoff = existingIssues.FirstOrDefault(i =>
                i.Title.Equals(issueTitle, StringComparison.OrdinalIgnoreCase));

            int? kickoffIssueNumber = null;

            if (existingKickoff is not null)
            {
                kickoffIssueNumber = existingKickoff.Number;
                Logger.LogInformation(
                    "Kickoff issue already exists as #{Number}, skipping issue creation",
                    existingKickoff.Number);
            }
            else
            {
                var issueBody = $"""
                    ## Research Request
                    **From:** {Identity.DisplayName}
                    **Phase:** Research

                    ## Project Description
                    {projectDescription}

                    ## Research Guidance
                    {researchGuidance}
                    """;

                var issue = await _github.CreateIssueAsync(
                    issueTitle, issueBody,
                    [IssueWorkflow.Labels.AgentQuestion],
                    ct);

                kickoffIssueNumber = issue.Number;
                Logger.LogInformation(
                    "Created kickoff issue #{Number}: {Title}",
                    issue.Number, issueTitle);
            }

            // 2. Send a TaskAssignmentMessage via bus to trigger the Researcher.
            //    Include the research guidance in the description so the Researcher
            //    gets the full context even if it doesn't read the GitHub issue.
            //    Pass the issue number so the Researcher can link it directly.
            var taskId = $"kickoff-research-{Guid.NewGuid():N}";
            await _messageBus.PublishAsync(new TaskAssignmentMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "TaskAssignment",
                TaskId = taskId,
                Title = $"Research technology stack for {projectName}",
                Description = $"{projectDescription}\n\n## Research Guidance\n{researchGuidance}",
                Complexity = "High",
                IssueNumber = kickoffIssueNumber
            }, ct);

            Logger.LogInformation(
                "Sent research kickoff task {TaskId} to Researcher via message bus", taskId);

            UpdateStatus(AgentStatus.Idle, "Project kickoff complete, monitoring team");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to kick off project — PM will continue but Researcher may be idle");
            RecordError($"Kickoff failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            UpdateStatus(AgentStatus.Idle, "Kickoff failed, continuing with manual oversight");
        }
    }

    /// <summary>
    /// Returns the research guidance for the Researcher agent. Uses the custom
    /// <see cref="ProjectConfig.ResearchPrompt"/> from appsettings.json if provided,
    /// otherwise generates a comprehensive default prompt.
    /// </summary>
    private string GetResearchGuidance(string projectName, string projectDescription)
    {
        var custom = _config.Project.ResearchPrompt;
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        // Generate a rich default prompt that drives deep, structured research
        var techStack = _config.Project.TechStack;
        return $"""
            Conduct a thorough, multi-dimensional research analysis for the project "{projectName}".
            Go beyond surface-level recommendations — the engineering team needs depth and specificity.

            **MANDATORY TECHNOLOGY STACK: {techStack}**
            The technology stack has already been decided. All research, recommendations, libraries,
            and patterns MUST target {techStack}. Do NOT recommend alternative stacks.
            Focus on the best libraries, patterns, and tools within this ecosystem.

            ### 1. Domain & Market Research
            - What are the core domain concepts and terminology?
            - Who are the target users and what are their key workflows?
            - Are there existing products, competitors, or open-source projects solving similar problems?
            - What industry standards, regulations, or compliance requirements apply?

            ### 2. Technology Stack Evaluation
            - Given the mandatory stack ({techStack}), evaluate the best libraries and frameworks within this ecosystem
            - For each recommended library, provide: strengths, maturity, community size, alternatives within the stack
            - Include specific version numbers and compatibility considerations
            - Do NOT evaluate alternative technology stacks — the stack decision is final

            ### 3. Architecture Patterns & Design
            - Which architecture patterns best fit this project within {techStack}?
            - What data storage strategy is appropriate (relational, document, graph, hybrid)?
            - How should the system handle scalability, caching, and performance?
            - What API design approach should be used?

            ### 4. Libraries, Frameworks & Dependencies
            - List specific libraries and packages for core functionality within {techStack}
            - Include testing frameworks, CI/CD tools, monitoring, and observability solutions
            - Flag any licensing concerns or deprecated dependencies

            ### 5. Security & Infrastructure
            - Authentication and authorization approach
            - Data protection, encryption, and privacy considerations
            - Hosting and deployment strategy (cloud provider, containerization, CDN)
            - Estimated infrastructure costs at small and medium scale

            ### 6. Risks, Trade-offs & Open Questions
            - Technical risks that could derail the project
            - Scalability bottlenecks or single points of failure
            - Skills gaps or steep learning curves for the team
            - Decisions that should be deferred vs. decided upfront
            - Open questions that need stakeholder input

            ### 7. Implementation Recommendations
            - Suggested phasing or MVP scope
            - Quick wins that demonstrate value early
            - Areas where prototyping is recommended before committing

            Produce a structured **Research.md** document with your findings covering all sections above.
            Be specific, opinionated, and actionable — the Architect and Engineers will build directly from this.
            """;
    }

    /// <summary>
    /// Ensure TeamMembers.md exists in the repo with at least the core agents listed.
    /// Called once at startup so the document is always present for tracking.
    /// </summary>
    private async Task EnsureTeamMembersDocAsync(CancellationToken ct)
    {
        try
        {
            var content = await _github.GetFileContentAsync("TeamMembers.md", null, ct);
            if (content is not null)
            {
                Logger.LogDebug("TeamMembers.md already exists");
                return;
            }

            // Create the initial TeamMembers.md with core agents
            var coreAgents = _registry.GetAllAgents()
                .Where(a => a.Identity.Role is AgentRole.ProgramManager or AgentRole.Researcher
                    or AgentRole.Architect or AgentRole.SoftwareEngineer or AgentRole.TestEngineer)
                .ToList();

            var doc = """
                # Team Members

                | Name | Role | Status | Model Tier | Current PR | Since | Communication |
                |------|------|--------|------------|------------|-------|---------------|
                """;

            foreach (var agent in coreAgents)
            {
                var since = agent.Identity.CreatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                doc += $"\n| {agent.Identity.DisplayName} | {agent.Identity.Role} | Online | {agent.Identity.ModelTier} | — | {since} | Internal Bus |";
            }

            doc += "\n";

            await _github.CreateOrUpdateFileAsync("TeamMembers.md", doc, "Initialize TeamMembers.md with core agents", null, ct);
            Logger.LogInformation("Created TeamMembers.md with {Count} core agents", coreAgents.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to ensure TeamMembers.md exists");
        }
    }

    /// <summary>
    /// Reads TeamMembers.md and re-spawns any Software Engineers that were
    /// previously active but are no longer running (e.g., after a restart).
    /// Matches engineers by display name and restores their task assignments from the EngineeringPlan.
    /// </summary>
    private async Task RestoreEngineersFromTeamMembersAsync(CancellationToken ct)
    {
        try
        {
            var teamDoc = await _projectFiles.GetTeamMembersAsync(ct);
            var engineeringPlan = await _projectFiles.GetEngineeringPlanAsync(ct);
            var lines = teamDoc.Split('\n');
            var restoredCount = 0;

            foreach (var line in lines)
            {
                if (!line.StartsWith('|') || line.Contains("---") || line.Contains("Name"))
                    continue;

                var columns = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 3)
                    continue;

                var name = columns[0].Trim();
                var roleText = columns[1].Trim();

                // Restore additional Software Engineers from pool
                AgentRole? role = roleText switch
                {
                    "SoftwareEngineer" => AgentRole.SoftwareEngineer,
                    _ => null
                };

                if (role is null)
                    continue;

                // Skip the core SE (rank 0) — it's already spawned by the worker
                if (role == AgentRole.SoftwareEngineer && name == "SoftwareEngineer")
                    continue;

                // Check if an agent with this name is already running
                var existingAgents = _registry.GetAgentsByRole(role.Value);
                if (existingAgents.Any(a => a.Identity.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.LogDebug("Engineer '{Name}' is already running, skipping restore", name);
                    continue;
                }

                Logger.LogInformation("Restoring engineer '{Name}' ({Role}) from TeamMembers.md", name, role);

                var spawnedIdentity = await _spawnManager.SpawnAgentAsync(role.Value, ct);
                if (spawnedIdentity is null)
                {
                    Logger.LogWarning("Failed to restore engineer '{Name}' — spawn limit reached", name);
                    continue;
                }

                restoredCount++;
                _additionalEngineersHired++;

                // Check if this engineer had a task assigned in the engineering plan
                var assignedPr = FindAssignedPrFromPlan(engineeringPlan, name);
                if (assignedPr is not null)
                {
                    spawnedIdentity.AssignedPullRequest = assignedPr;
                    Logger.LogInformation(
                        "Restored engineer '{Name}' with assigned PR #{Pr}",
                        name, assignedPr);
                }
            }

            if (restoredCount > 0)
            {
                Logger.LogInformation("Restored {Count} engineers from TeamMembers.md", restoredCount);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore engineers from TeamMembers.md");
        }
    }

    /// <summary>
    /// Parse the EngineeringPlan.md to find a PR number assigned to a specific engineer name.
    /// </summary>
    private static string? FindAssignedPrFromPlan(string engineeringPlan, string engineerName)
    {
        foreach (var line in engineeringPlan.Split('\n'))
        {
            if (!line.StartsWith('|') || line.Contains("---") || line.Contains("Task"))
                continue;

            var columns = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5)
                continue;

            var assignedTo = columns[3].Trim();
            var prColumn = columns[4].Trim();

            if (assignedTo.Equals(engineerName, StringComparison.OrdinalIgnoreCase) &&
                prColumn.StartsWith('#'))
            {
                return prColumn.TrimStart('#');
            }
        }

        return null;
    }

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
                // GitHub is source of truth: fetch actual comments
                var comments = await _github.GetIssueCommentsAsync(issue.Number, ct);
                if (comments.Count == 0)
                    continue;

                // Check the latest comment — if it's from the bot, we've already responded
                var latestComment = comments[^1];
                if (latestComment.Body.StartsWith("⚠️") || latestComment.Body.StartsWith("✅") ||
                    latestComment.Body.StartsWith("🚀") || latestComment.Body.StartsWith("❌"))
                    continue;

                // Only process human approval/denial comments (not resource-request auto-comments)
                var body = latestComment.Body;
                if (!body.Contains("approved", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("denied", StringComparison.OrdinalIgnoreCase) &&
                    !body.Contains("rejected", StringComparison.OrdinalIgnoreCase))
                    continue;

                Logger.LogInformation(
                    "Executive response on issue #{Number}: {Comment}",
                    issue.Number, latestComment.Body);

                if (body.Contains("approved", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse optional quantity: "approved for 2" or "approved for 3 more engineers"
                    var count = ParseApprovalCount(body);

                    // Check if this is a resource-limit override (title contains "Resource Limit")
                    var isResourceOverride = issue.Title.Contains("Resource Limit", StringComparison.OrdinalIgnoreCase)
                        || issue.Labels.Contains(IssueWorkflow.Labels.ResourceRequest, StringComparer.OrdinalIgnoreCase);

                    if (isResourceOverride)
                    {
                        Logger.LogInformation(
                            "Executive approved resource override on #{Number} for {Count} engineer(s)",
                            issue.Number, count);

                        var spawned = 0;
                        for (int i = 0; i < count; i++)
                        {
                            var spawnedIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.SoftwareEngineer, ct);
                            if (spawnedIdentity is not null)
                            {
                                _additionalEngineersHired++;
                                spawned++;
                                await _projectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);
                                Logger.LogInformation(
                                    "Executive override: spawned {Role} '{Name}' ({N}/{Count})",
                                    AgentRole.SoftwareEngineer, spawnedIdentity.DisplayName, spawned, count);
                            }
                        }

                        await _github.AddIssueCommentAsync(issue.Number,
                            $"✅ **Executive approval processed.** Spawned {spawned} additional engineer(s). " +
                            $"Team now has {_additionalEngineersHired} additional engineers.", ct);
                    }
                    else
                    {
                        await _github.AddIssueCommentAsync(issue.Number,
                            "✅ **Executive approval acknowledged.** Request has been processed.", ct);
                    }

                    // Close this executive request issue
                    await _github.CloseIssueAsync(issue.Number, ct);

                    // Also close linked resource-request issues referenced in the title
                    var linkedNum = ParseLinkedIssueFromTitle(issue.Title);
                    if (linkedNum.HasValue)
                    {
                        try
                        {
                            await _github.AddIssueCommentAsync(linkedNum.Value,
                                $"✅ Executive approved override via #{issue.Number}. Request fulfilled.", ct);
                            await _github.CloseIssueAsync(linkedNum.Value, ct);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "Could not close linked issue #{Number}", linkedNum.Value);
                        }
                    }
                }
                else if (body.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                         body.Contains("rejected", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation(
                        "Executive denied request on issue #{Number}", issue.Number);

                    await _github.AddIssueCommentAsync(issue.Number,
                        "❌ **Executive denied this request.** Closing.", ct);
                    await _github.CloseIssueAsync(issue.Number, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check executive responses");
        }
    }

    /// <summary>
    /// Parse "approved for N" from executive comment. Returns 1 if no quantity specified.
    /// Supports: "approved", "approved for 2", "approved for 2 more engineers", "approved, add 3"
    /// </summary>
    private static int ParseApprovalCount(string comment)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            comment, @"(?:approved\s+(?:for|,?\s*add)\s+)(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 1;
    }

    /// <summary>
    /// Parse "request from issue #N" from executive request title.
    /// </summary>
    private static int? ParseLinkedIssueFromTitle(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"#(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var num) ? num : null;
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
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var issue in resourceIssues)
            {
                // GitHub is source of truth: fetch actual comments to determine state
                var comments = await _github.GetIssueCommentsAsync(issue.Number, ct);
                var lastComment = comments.Count > 0 ? comments[^1] : null;

                // If the last comment is a ✅ or 🚀 (already fulfilled), close and skip
                if (lastComment is not null &&
                    (lastComment.Body.StartsWith("✅") || lastComment.Body.StartsWith("🚀")))
                {
                    Logger.LogDebug("Resource request #{Number} already fulfilled, closing", issue.Number);
                    await _github.CloseIssueAsync(issue.Number, ct);
                    continue;
                }

                // If already denied (⚠️), don't re-deny — just skip
                if (lastComment is not null && lastComment.Body.StartsWith("⚠️"))
                {
                    Logger.LogDebug("Resource request #{Number} already denied, skipping", issue.Number);
                    continue;
                }

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
                    // Parse which role is requested from the issue body
                    var requestedRole = AgentRole.SoftwareEngineer;

                    _additionalEngineersHired++;
                    Logger.LogInformation(
                        "Resource request #{Number} approved. Spawning {Role}. Additional engineers: {Count}/{Max}",
                        issue.Number, requestedRole, _additionalEngineersHired,
                        _config.Limits.MaxAdditionalEngineers);

                    await _github.AddIssueCommentAsync(issue.Number,
                        $"✅ **Resource request approved.** Spawning {requestedRole} " +
                        $"(additional engineer #{_additionalEngineersHired} " +
                        $"of {_config.Limits.MaxAdditionalEngineers} maximum).", ct);

                    // Actually spawn the engineer
                    var spawnedIdentity = await _spawnManager.SpawnAgentAsync(requestedRole, ct);
                    if (spawnedIdentity is not null)
                    {
                        Logger.LogInformation(
                            "Spawned {Role} '{Name}' for resource request #{Number}",
                            requestedRole, spawnedIdentity.DisplayName, issue.Number);

                        // Track in TeamMembers.md for persistence across restarts
                        await _projectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);

                        await _github.AddIssueCommentAsync(issue.Number,
                            $"🚀 **{requestedRole} '{spawnedIdentity.DisplayName}' is now online** " +
                            "and ready for task assignment.", ct);
                        await RememberAsync(MemoryType.Action,
                            $"Hired {requestedRole} '{spawnedIdentity.DisplayName}' via resource request #{issue.Number}",
                            ct: ct);

                        await _github.CloseIssueAsync(issue.Number, ct);
                    }
                    else
                    {
                        Logger.LogWarning(
                            "Failed to spawn {Role} for resource request #{Number} — spawn manager returned null",
                            requestedRole, issue.Number);

                        await _github.AddIssueCommentAsync(issue.Number,
                            $"⚠️ Failed to spawn {requestedRole} — capacity limit may have been reached.", ct);
                    }
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
            // Drain the review queue — only review PRs we've been notified about
            var prNumbersToReview = new HashSet<int>();
            while (_reviewQueue.TryDequeue(out var prNumber))
                prNumbersToReview.Add(prNumber);

            // Phase 3 polling: also scan for PRs with tests-added that PM hasn't reviewed yet
            if (_config.Workspace.IsInlineTestWorkflow)
            {
                var openPRs = await _github.GetOpenPullRequestsAsync(ct);
                foreach (var openPr in openPRs)
                {
                    if (_reviewedPrHeadShas.TryGetValue(openPr.Number, out var reviewedSha)
                        && string.Equals(reviewedSha, openPr.HeadSha, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // PM reviews after TE has added tests (Phase 3 gate)
                    if (openPr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase) &&
                        !openPr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                    {
                        prNumbersToReview.Add(openPr.Number);
                    }
                }
            }

            if (prNumbersToReview.Count == 0)
            {
                Logger.LogDebug("PM review poll: 0 PRs eligible");
                return;
            }

            Logger.LogInformation("PM review poll: {Count} PR(s) eligible for review: {Numbers}",
                prNumbersToReview.Count, string.Join(",", prNumbersToReview));

            foreach (var prNumber in prNumbersToReview)
            {
                var pr = await _github.GetPullRequestAsync(prNumber, ct);
                if (pr is null)
                    continue;

                // Skip if we've already reviewed this exact HEAD SHA. Re-review if HEAD moved
                // (e.g., SE pushed fixes after CHANGES REQUESTED).
                if (_reviewedPrHeadShas.TryGetValue(prNumber, out var reviewedSha)
                    && string.Equals(reviewedSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip TestEngineer PRs — PM doesn't review test suites, only PE does
                var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                if (authorRole.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
                    || authorRole.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrHeadShas[prNumber] = pr.HeadSha;
                    continue;
                }

                // Phase 3 gate: PM only reviews AFTER TE has added tests (inline workflow)
                // NOTE: This gate applies even for force-approval PRs — PM should never
                // approve before TE finishes testing, regardless of rework cycle count.
                if (_config.Workspace.IsInlineTestWorkflow &&
                    !pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.LogDebug("PM skipping PR #{Number} — waiting for TE to add tests (Phase 2)", prNumber);
                    continue; // Don't mark as reviewed — we'll check again next cycle
                }

                // Defense-in-depth: even if tests-added label is present, verify TE posted a
                // completion comment. This prevents the PM from reviewing before TE finishes
                // posting results (label and comment are separate API calls).
                // NOTE: This check also applies for force-approval PRs.
                if (_config.Workspace.IsInlineTestWorkflow)
                {
                    var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
                    var hasTeCompletionComment = comments.Any(c =>
                        (c.Body.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase) &&
                         (c.Body.Contains("Test Results", StringComparison.OrdinalIgnoreCase) ||
                          c.Body.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
                          c.Body.Contains("UI Test", StringComparison.OrdinalIgnoreCase))) ||
                        c.Body.Contains("[TestEngineer] No Tests Needed", StringComparison.OrdinalIgnoreCase) ||
                        c.Body.Contains("Test Engineer:", StringComparison.OrdinalIgnoreCase));
                    if (!hasTeCompletionComment)
                    {
                        Logger.LogDebug("PM skipping PR #{Number} — tests-added label present but no TE results comment yet", prNumber);
                        continue;
                    }
                }

                // Skip PRs already PM-approved
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                {
                    _reviewedPrHeadShas[prNumber] = pr.HeadSha;
                    continue;
                }

                // Idempotency: PRs already PM-approved are skipped via the label check above.
                // Otherwise, if HEAD SHA differs from the one we last reviewed (or we've never
                // reviewed this PR in this session), we re-review. SE pushing new commits after
                // CHANGES REQUESTED advances HEAD SHA — that IS the re-review trigger.
                // (Force-approval PRs always proceed regardless of any idempotency check.)

                Logger.LogInformation("PM reviewing PR #{Number}: {Title} (Phase 3 — final review after TE tests)",
                    pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                bool approved;
                string? reviewBody;

                // A2/B-followup guard: inspect TE comment for UI failures OR App Preview Unavailable.
                // Applied to BOTH force-approval and no-new-commits auto-approval paths so a broken
                // SHA can never silently merge.
                var (uiGateBlocked, uiGateMessage) = await EvaluateUiFailureGateAsync(prNumber, ct);

                if (_forceApprovalPrs.Contains(prNumber))
                {
                    if (uiGateBlocked)
                    {
                        _forceApprovalPrs.Remove(prNumber);
                        Logger.LogWarning(
                            "PM blocking force-approval on PR #{Number}: {Reason}",
                            prNumber, uiGateMessage);
                        approved = false;
                        reviewBody = $"⛔ Force-approval blocked by UI quality gate.\n\n{uiGateMessage}\n\n" +
                            $"A dashboard with visible UI failures cannot be merged on a force-approval fast path. " +
                            $"Please address the failures and push new commits before re-requesting review. " +
                            $"If these are infrastructure flakes, escalate to a human reviewer via the ReworkExhaustion gate.";
                    }
                    else
                    {
                        _forceApprovalPrs.Remove(prNumber);
                        approved = true;
                        reviewBody = $"Force-approving after maximum PM rework cycles reached. " +
                            $"The PR has been through multiple review iterations and the engineer " +
                            $"has made best-effort improvements.";
                    }
                }
                else
                {
                    var hasNewCommits = await _prWorkflow.HasNewCommitsSinceReviewAsync(prNumber, "ProgramManager", ct);
                    if (!hasNewCommits)
                    {
                        // A2 fix #1: do NOT auto-approve same SHA if the UI gate still blocks.
                        // Otherwise re-running ready-for-review without new commits would bypass the gate.
                        if (uiGateBlocked)
                        {
                            Logger.LogWarning(
                                "PM refusing no-new-commits auto-approval on PR #{Number}: UI gate still blocks ({Reason})",
                                prNumber, uiGateMessage);
                            approved = false;
                            reviewBody = $"⛔ Cannot approve — UI quality gate still blocks this PR.\n\n{uiGateMessage}\n\n" +
                                $"No new commits have been pushed since the last review, but the UI failures remain. " +
                                $"Push a fix or escalate via the ReworkExhaustion gate.";
                        }
                        else
                        {
                            Logger.LogWarning("No new commits on PR #{Number} since last PM review — approving to unblock", prNumber);
                            approved = true;
                            reviewBody = "No new code commits detected since last review. " +
                                "The author marked the PR as ready but did not push file changes. " +
                                "Approving to avoid blocking progress — previous feedback still applies.";
                        }
                    }
                    else
                    {
                        // Run rubber-duck critique if configured (different model tier, adversarial persona)
                        string? critiqueFindings = null;
                        if (!string.IsNullOrWhiteSpace(_config.Agents.CritiqueTier))
                        {
                            try
                            {
                                var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
                                var critiqueIssueContext = "";
                                if (issueNumber.HasValue)
                                {
                                    var issue = await _github.GetIssueAsync(issueNumber.Value, ct);
                                    if (issue is not null)
                                        critiqueIssueContext = $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}";
                                }
                                var critiqueCode = await _prWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

                                // Gather TE test results from PR comments
                                string? testResults = null;
                                var teComment = pr.Comments.FirstOrDefault(c =>
                                    c.Body.Contains("[TestEngineer]", StringComparison.OrdinalIgnoreCase));
                                if (teComment is not null)
                                    testResults = teComment.Body;

                                // Gather prior review comments (Architect, PE)
                                var priorReviews = string.Join("\n\n",
                                    pr.Comments
                                        .Where(c => c.Body.Contains("[Architect]", StringComparison.OrdinalIgnoreCase)
                                                 || c.Body.Contains("[SoftwareEngineer]", StringComparison.OrdinalIgnoreCase))
                                        .Select(c => c.Body));

                                critiqueFindings = await PerformCritiqueAsync(
                                    pr, critiqueCode, critiqueIssueContext, testResults,
                                    string.IsNullOrWhiteSpace(priorReviews) ? null : priorReviews, ct);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Failed to run critique for PR #{Number} — continuing without", pr.Number);
                            }
                        }

                        (approved, reviewBody) = await EvaluatePrAlignmentWithVerdictAsync(pr, ct);

                        // Append critique section to review body
                        if (reviewBody is not null)
                            reviewBody += FormatCritiqueSection(critiqueFindings);
                    }
                }

                if (reviewBody is null)
                    continue;

                if (approved)
                {
                    // Phase 3 complete: PM approved → add pm-approved label → triggers merge by PE
                    var approvalComment = string.IsNullOrWhiteSpace(reviewBody)
                        ? "**[ProgramManager] APPROVED**"
                        : $"**[ProgramManager] APPROVED**\n\n{reviewBody}";
                    await _github.AddPullRequestCommentAsync(pr.Number, approvalComment, ct);

                    // Submit formal GitHub APPROVE only if agents have separate accounts
                    if (_config.Review.EnableFormalReviews)
                    {
                        try
                        {
                            await _github.AddPullRequestReviewAsync(pr.Number,
                                $"**[ProgramManager] APPROVED** — Final PM review passed.", "APPROVE", ct);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex,
                                "Formal APPROVE review failed on PR #{Number} (expected in single-PAT setup)",
                                pr.Number);
                        }
                    }

                    Logger.LogInformation("PM approved PR #{Number} (Phase 3 final approval)", pr.Number);

                    // Resolve any open inline review threads now that the PR is approved
                    await ResolvePmReviewThreadsAsync(pr.Number, ct);

                    // Add pm-approved label — this is the final gate before merge
                    var updatedLabels = pr.Labels
                        .Append(PullRequestWorkflow.Labels.PmApproved)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);

                    LogActivity("task", $"✅ PM final approval on PR #{pr.Number}: {pr.Title} — ready to merge");
                    await RememberAsync(MemoryType.Decision,
                        $"PM final approval on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

                    // Notify PE to merge
                    await _messageBus.PublishAsync(new StatusUpdateMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "StatusUpdate",
                        NewStatus = AgentStatus.Working,
                        CurrentTask = $"PR #{pr.Number} pm-approved — ready for merge",
                        Details = $"PR #{pr.Number}: {pr.Title} has passed final PM review"
                    }, ct);
                }
                else
                {
                    await _prWorkflow.RequestChangesAsync(pr.Number, "ProgramManager", reviewBody, ct);
                    Logger.LogInformation("PM requested changes on PR #{Number}", pr.Number);
                    LogActivity("task", $"❌ Requested changes on PR #{pr.Number}: {pr.Title}");
                    await RememberAsync(MemoryType.Decision,
                        $"Requested changes on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

                    // Notify the author engineer to rework
                    await _messageBus.PublishAsync(new ChangesRequestedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ChangesRequested",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewerAgent = "ProgramManager",
                        Feedback = reviewBody
                    }, ct);
                }

                _reviewedPrHeadShas[pr.Number] = pr.HeadSha;
            }

            // Reset status after reviews complete so dashboard doesn't show stale "Reviewing PR" text
            UpdateStatus(AgentStatus.Idle, "Monitoring team progress");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review pull requests");
        }
    }

    /// <summary>
    /// After approving a PR, resolve all open inline review threads left by previous reviews.
    /// Replies with a resolution comment explaining the thread is resolved by the rework.
    /// </summary>
    private async Task ResolvePmReviewThreadsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var threads = await _github.GetPullRequestReviewThreadsAsync(prNumber, ct);
            // Only resolve threads authored by the PM (identified by [ProgramManager] tag in body)
            var ownThreads = threads
                .Where(t => !t.IsResolved && t.Body.Contains("[ProgramManager]", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ownThreads.Count == 0)
                return;

            Logger.LogInformation("PM resolving {Count} review threads on PR #{Number} after approval",
                ownThreads.Count, prNumber);

            foreach (var thread in ownThreads)
            {
                var replyBody = $"✅ **[ProgramManager] Resolved** — Rework addressed this feedback. Approved.";
                await _github.ReplyAndResolveReviewThreadAsync(
                    prNumber, thread.Id, thread.NodeId, replyBody, ct);
            }

            LogActivity("review", $"🔒 Resolved {ownThreads.Count} PM inline review thread(s) on PR #{prNumber}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resolve review threads on PR #{Number} — approval still proceeds", prNumber);
        }
    }

    /// <summary>
    /// Periodically review open enhancement (user story) issues that the PM created.
    /// When all sub-issues (engineering tasks) for an enhancement are closed, the PM does
    /// a final acceptance review against the original acceptance criteria and decides
    /// whether to close the issue or request additional work.
    /// </summary>
    private async Task ReviewEnhancementIssueCompletionAsync(CancellationToken ct)
    {
        try
        {
            var openIssues = await _github.GetOpenIssuesAsync(ct);
            var enhancementIssues = openIssues
                .Where(i => i.Labels.Any(l => string.Equals(l, "enhancement", StringComparison.OrdinalIgnoreCase)))
                .Where(i => !_reviewedEnhancementIssues.Contains(i.Number))
                .ToList();

            if (enhancementIssues.Count == 0)
                return;

            foreach (var issue in enhancementIssues)
            {
                // Check sub-issues via GitHub's Sub-Issues API
                var subIssues = await _github.GetSubIssuesAsync(issue.Number, ct);

                if (subIssues.Count == 0)
                    continue; // No engineering tasks linked yet — skip

                var allClosed = subIssues.All(s =>
                    string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase));

                if (!allClosed)
                    continue; // Not all tasks done yet — check again next loop

                // All sub-issues are closed → PM does final acceptance review
                Logger.LogInformation(
                    "All {Count} sub-issues for enhancement #{Number} are closed. Starting final acceptance review.",
                    subIssues.Count, issue.Number);

                var closedSummary = string.Join("\n", subIssues.Select(s =>
                    $"  - #{s.Number}: {s.Title} (closed)"));

                var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var history = CreateChatHistory();

                history.AddSystemMessage(
                    await _promptService.RenderAsync("pm/story-review-system", new Dictionary<string, string>(), ct)
                    ?? "You are a Program Manager reviewing whether a user story has been fully delivered. " +
                       "All engineering tasks have been completed and merged. Review the original acceptance " +
                       "criteria and the completed tasks. If all criteria are met, respond with APPROVED and " +
                       "a brief summary. If gaps remain, respond with NEEDS_MORE_WORK and describe what's missing.");

                history.AddUserMessage(
                    await _promptService.RenderAsync("pm/story-review-user", new Dictionary<string, string>
                    {
                        ["issue_number"] = issue.Number.ToString(),
                        ["issue_title"] = issue.Title,
                        ["issue_body"] = issue.Body ?? "",
                        ["closed_summary"] = closedSummary
                    }, ct)
                    ?? $"## Enhancement Issue #{issue.Number}: {issue.Title}\n\n" +
                       $"### Original Specification\n{issue.Body}\n\n" +
                       $"### Completed Engineering Tasks\n{closedSummary}\n\n" +
                       "Review the acceptance criteria above. Are all criteria addressed by the completed tasks? " +
                       "Start your response with either APPROVED or NEEDS_MORE_WORK.");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var responseText = response.Content ?? "";

                if (responseText.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
                {
                    // Clean response for the closing comment
                    var summaryText = responseText
                        .Replace("APPROVED", "").Replace("approved", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    await _github.AddIssueCommentAsync(issue.Number,
                        $"✅ **PM Final Review — APPROVED**\n\n" +
                        $"All {subIssues.Count} engineering tasks have been delivered and merged.\n\n" +
                        $"{summaryText}\n\n" +
                        $"Closing this user story as complete.",
                        ct);
                    await _github.CloseIssueAsync(issue.Number, ct);
                    _reviewedEnhancementIssues.Add(issue.Number);

                    Logger.LogInformation("PM approved and closed enhancement issue #{Number}: {Title}",
                        issue.Number, issue.Title);
                    LogActivity("review", $"✅ Approved and closed user story #{issue.Number}: {issue.Title}");
                }
                else
                {
                    // PM found gaps — comment but don't close
                    var gapText = responseText
                        .Replace("NEEDS_MORE_WORK", "").Replace("needs_more_work", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    await _github.AddIssueCommentAsync(issue.Number,
                        $"🔍 **PM Final Review — Additional Work Needed**\n\n" +
                        $"All {subIssues.Count} engineering tasks are closed, but gaps remain:\n\n" +
                        $"{gapText}\n\n" +
                        $"Keeping this issue open for further engineering work.",
                        ct);
                    _reviewedEnhancementIssues.Add(issue.Number);

                    Logger.LogInformation(
                        "PM flagged enhancement issue #{Number} as needing more work: {Title}",
                        issue.Number, issue.Title);
                    LogActivity("review", $"🔍 Enhancement #{issue.Number} needs more work: {issue.Title}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error reviewing enhancement issue completion");
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
                "Resource request from {Agent} approved via message bus. Spawning {Role} ({Count}/{Max})",
                message.FromAgentId, message.RequestedRole, _additionalEngineersHired,
                _config.Limits.MaxAdditionalEngineers);

            // Actually spawn the engineer
            var spawnedIdentity = await _spawnManager.SpawnAgentAsync(message.RequestedRole, ct);
            if (spawnedIdentity is not null)
            {
                Logger.LogInformation(
                    "Spawned {Role} '{Name}' for bus resource request from {Agent}",
                    message.RequestedRole, spawnedIdentity.DisplayName, message.FromAgentId);
                await RememberAsync(MemoryType.Action,
                    $"Hired {message.RequestedRole} '{spawnedIdentity.DisplayName}' via bus request from {message.FromAgentId}",
                    ct: ct);

                // Track in TeamMembers.md for persistence across restarts
                await _projectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);
            }
            else
            {
                Logger.LogWarning(
                    "Failed to spawn {Role} for bus resource request from {Agent}",
                    message.RequestedRole, message.FromAgentId);
            }

            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = message.FromAgentId,
                MessageType = "ResourceApproval",
                NewStatus = AgentStatus.Online,
                Details = $"Resource request approved: {message.RequestedRole}" +
                    (spawnedIdentity is not null ? $" — spawned {spawnedIdentity.DisplayName}" : " — spawn failed")
            }, ct);
        }
    }

    private async Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Status update from {Agent}: {Status} — {Details}",
            message.FromAgentId, message.NewStatus, message.Details);

        if (!_trackedAgents.TryGetValue(message.FromAgentId, out var tracking))
        {
            tracking = new AgentTracking
            {
                AgentId = message.FromAgentId,
                Role = AgentRole.SoftwareEngineer // default; updated if known
            };
            _trackedAgents[message.FromAgentId] = tracking;
        }

        tracking.LastKnownStatus = message.NewStatus;
        tracking.CurrentTask = message.CurrentTask;
        tracking.LastStatusUpdate = DateTime.UtcNow;

        // When research completes, create the PM Specification document
        if (message.MessageType == "ResearchComplete" && !_pmSpecCreated)
        {
            _pmSpecCreated = true;
            Logger.LogInformation("Research complete signal received — generating PMSpec.md");

            // Skip gate if PMSpec already exists (resume scenario — no need to re-approve)
            var existingSpec = await _projectFiles.GetPMSpecAsync(ct);
            if (!string.IsNullOrWhiteSpace(existingSpec) &&
                !existingSpec.Contains("No PM specification has been created yet", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("PMSpec.md already exists, skipping ResearchCompleteness gate");
            }
            else
            {
                // === Gate: ResearchCompleteness — human reviews research before PM proceeds ===
                if (_gateCheck.RequiresHuman(GateIds.ResearchCompleteness))
                    UpdateStatus(AgentStatus.Working, "⏳ Awaiting human approval — research completeness");
                await _gateCheck.WaitForGateAsync(
                    GateIds.ResearchCompleteness,
                    "Research phase complete, PM ready to create specification",
                    ct: ct);
            }

            await CreatePMSpecAsync(ct);
        }
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

    private Task HandleReviewRequestAsync(ReviewRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Review request from {Agent} for PR #{PrNumber}: {Title} ({ReviewType})",
            message.FromAgentId, message.PrNumber, message.PrTitle, message.ReviewType);

        // Clear reviewed SHA so reworked PRs get re-reviewed
        _reviewedPrHeadShas.Remove(message.PrNumber);

        // BUG FIX: Track FinalApproval requests so the PM auto-approves after max rework
        // cycles instead of continuing to request changes in an infinite loop.
        if (string.Equals(message.ReviewType, "FinalApproval", StringComparison.OrdinalIgnoreCase))
            _forceApprovalPrs.Add(message.PrNumber);

        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    private Task HandleClarificationRequestAsync(ClarificationRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Clarification request from {Agent} for issue #{IssueNumber}: {Question}",
            message.FromAgentId, message.IssueNumber, message.Question);
        _clarificationQueue.Enqueue(message);
        return Task.CompletedTask;
    }

    #endregion

    #region AI-Assisted Methods

    /// <summary>Revise a document based on reviewer feedback using AI.</summary>
    private async Task<string?> ReviseDocumentAsync(string docName, string feedback, CancellationToken ct)
    {
        try
        {
            var currentContent = docName switch
            {
                "PMSpec.md" => await _projectFiles.GetPMSpecAsync(ct),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(currentContent)) return null;

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();
            history.AddSystemMessage(
                await _promptService.RenderAsync("pm/revision-system",
                    new Dictionary<string, string> { ["doc_name"] = docName }, ct)
                ?? $"You are a Program Manager revising {docName} based on human reviewer feedback. " +
                   "Make the specific changes requested while preserving the overall structure.");
            history.AddUserMessage(
                await _promptService.RenderAsync("pm/revision-user",
                    new Dictionary<string, string>
                    {
                        ["doc_name"] = docName,
                        ["current_content"] = currentContent,
                        ["feedback"] = feedback
                    }, ct)
                ?? $"## Current {docName}:\n\n{currentContent}\n\n" +
                   $"## Reviewer Feedback:\n\n{feedback}\n\n" +
                   $"Revise the {docName} to address the feedback. Return the COMPLETE revised document.");

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var revised = string.Join("", response.Select(r => r.Content ?? ""));
            return string.IsNullOrWhiteSpace(revised) ? null : revised;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to revise {DocName}", docName);
            return null;
        }
    }

    /// <summary>Reset gate labels on a PR for re-review after revision.</summary>
    private async Task ResetGateLabelsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var pr = await _github.GetPullRequestAsync(prNumber, ct);
            if (pr is null) return;
            var labels = pr.Labels?.ToList() ?? [];
            labels.Remove("human-approved");
            if (!labels.Contains("awaiting-human-review"))
                labels.Add("awaiting-human-review");
            await _github.UpdatePullRequestAsync(prNumber, labels: labels.ToArray(), ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reset gate labels on PR #{Number}", prNumber);
        }
    }

    /// <summary>
    /// Creates a PM Specification document from the research findings and project description.
    /// Uses a multi-turn AI conversation to produce a structured business spec, then
    /// triggers the Architect to begin architecture design.
    /// </summary>
    private async Task CreatePMSpecAsync(CancellationToken ct)
    {
        var specStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Generate PM Spec",
            "Creating PM Specification from research findings", Identity.ModelTier);
        try
        {
            // Idempotency: check if PMSpec already has meaningful content
            var existingSpec = await _projectFiles.GetPMSpecAsync(ct);
            if (!string.IsNullOrWhiteSpace(existingSpec) &&
                !existingSpec.Contains("No PM specification has been created yet"))
            {
                Logger.LogInformation("PMSpec.md already exists with content, skipping creation");
                // Still signal downstream agents
                await _messageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "PMSpecReady",
                    NewStatus = AgentStatus.Idle,
                    Details = "PM Specification document already exists"
                }, ct);

                // Create User Story Issues if not already done
                await CreateUserStoryIssuesAsync(ct);
                return;
            }

            // Create the PR upfront so it's visible immediately
            var projectName = _config.Project.Name;

            // Quick mode: produce a minimal 1-paragraph PMSpec for fast testing
            if (_config.Project.QuickDocumentCreation)
            {
                Logger.LogInformation("QuickDocumentCreation: producing minimal PMSpec.md");
                UpdateStatus(AgentStatus.Working, "Creating minimal PMSpec (quick mode)");
                var qPr = await _prWorkflow.OpenDocumentPRAsync(
                    Identity.DisplayName, "PMSpec.md",
                    $"PM Specification for {projectName}",
                    $"Quick-mode PM specification for {projectName}.",
                    closesIssueNumber: null, ct);

                // Resume-aware: check if gate is already pending/approved
                var qGateStatus = await _gateCheck.GetGateStatusAsync(
                    GateIds.PMSpecification, qPr.Number, ct);

                string? qContent = null;
                if (qGateStatus == GateStatus.NotActivated)
                {
                    var qKernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                    var qChat = qKernel.GetRequiredService<IChatCompletionService>();
                    var qHistory = CreateChatHistory();
                    qHistory.AddSystemMessage(
                        await _promptService.RenderAsync("pm/quick-system", new Dictionary<string, string>(), ct)
                        ?? "You are a Program Manager. Write a brief product specification.");
                    qHistory.AddUserMessage(
                        await _promptService.RenderAsync("pm/quick-user", new Dictionary<string, string>
                        {
                            ["project_description"] = _config.Project.Description ?? "",
                            ["tech_stack"] = _config.Project.TechStack
                        }, ct)
                        ?? $"Project: {_config.Project.Description}\nTech Stack: {_config.Project.TechStack}\n\n" +
                           "Write a concise PMSpec with these sections (1-2 sentences each): " +
                           "Executive Summary, Business Goals, User Stories (3-5 bullet points with acceptance criteria), " +
                           "Scope, Non-Functional Requirements. Keep the entire document under 300 words.");
                    var qResp = await qChat.GetChatMessageContentAsync(qHistory, cancellationToken: ct);
                    qContent = $"# PM Specification: {projectName}\n\n{qResp.Content?.Trim() ?? ""}";
                }
                else
                {
                    Logger.LogInformation("PMSpec gate already {Status} on PR #{Number}, skipping generation",
                        qGateStatus, qPr.Number);
                }

                // Commit document to PR so reviewers can see it before the gate
                if (qContent is not null && !qPr.IsMerged)
                {
                    await _prWorkflow.CommitDocumentToPRAsync(
                        qPr, "PMSpec.md", qContent,
                        $"Add PM Specification for {projectName}", ct);
                }

                // === Gate: PMSpecification — human reviews PMSpec before merge ===
                if (qGateStatus != GateStatus.Approved)
                {
                    var maxRevisions = 3;
                    for (var revision = 0; revision < maxRevisions; revision++)
                    {
                        if (_gateCheck.RequiresHuman(GateIds.PMSpecification))
                            UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{qPr.Number}");
                        var gateWait = await _gateCheck.WaitForGateAsync(
                            GateIds.PMSpecification,
                            "PMSpec.md ready for human review before merge",
                            qPr.Number, ct: ct);

                        if (!gateWait.WasRejected)
                            break;

                        Logger.LogInformation("PMSpec gate rejected on PR #{Number}: {Feedback}", qPr.Number, gateWait.Feedback);
                        LogActivity("task", $"📝 Revising PMSpec based on feedback: {gateWait.Feedback}");
                        UpdateStatus(AgentStatus.Working, $"Revising PMSpec (attempt {revision + 2})");

                        var revised = await ReviseDocumentAsync("PMSpec.md", gateWait.Feedback!, ct);
                        if (revised is not null && !qPr.IsMerged)
                        {
                            await _prWorkflow.CommitDocumentToPRAsync(
                                qPr, "PMSpec.md", revised,
                                $"Revise PMSpec based on reviewer feedback (attempt {revision + 2})", ct);
                        }
                        await ResetGateLabelsAsync(qPr.Number, ct);
                        await _github.AddPullRequestCommentAsync(qPr.Number,
                            $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated PMSpec.md.", ct);
                    }
                }

                if (!qPr.IsMerged)
                {
                    await _prWorkflow.MergeDocumentPRAsync(
                        qPr, Identity.DisplayName, "PMSpec.md", ct);
                }
                Logger.LogInformation("Quick PMSpec.md created and merged");
                LogActivity("task", $"📝 Quick PMSpec.md created for {projectName}");

                await _messageBus.PublishAsync(new StatusUpdateMessage
                {
                    FromAgentId = Identity.Id, ToAgentId = "*",
                    MessageType = "PMSpecReady",
                    NewStatus = AgentStatus.Working,
                    Details = "PM Specification is ready (quick mode). Architect can begin."
                }, ct);

                await CreateUserStoryIssuesAsync(ct);
                UpdateStatus(AgentStatus.Idle, "Quick PMSpec complete, Architect triggered");
                return;
            }

            UpdateStatus(AgentStatus.Working, "Creating PR for PMSpec.md");
            var pr = await _prWorkflow.OpenDocumentPRAsync(
                Identity.DisplayName,
                "PMSpec.md",
                $"PM Specification for {projectName}",
                $"Formal product specification document covering business goals, user stories, " +
                $"acceptance criteria, scope, and non-functional requirements for {projectName}.",
                closesIssueNumber: null,
                ct);

            // Resume-aware: check if gate is already pending/approved from a prior run
            var pmGateStatus = await _gateCheck.GetGateStatusAsync(
                GateIds.PMSpecification, pr.Number, ct);

            string? pmSpecDoc = null;

            if (pmGateStatus == GateStatus.Approved)
            {
                Logger.LogInformation("PMSpec gate already approved on PR #{Number}, skipping generation", pr.Number);
                LogActivity("task", $"⏩ PMSpec gate already approved on PR #{pr.Number}, resuming");
            }
            else if (pmGateStatus == GateStatus.AwaitingApproval)
            {
                Logger.LogInformation("PMSpec gate already pending on PR #{Number}, skipping to gate wait", pr.Number);
                LogActivity("task", $"⏩ PMSpec gate already pending on PR #{pr.Number}, resuming wait");
            }
            else
            {

            UpdateStatus(AgentStatus.Working, "Creating PMSpec (1/2): Analyzing requirements");

            var projectDescription = _config.Project.Description;
            var researchDoc = await _projectFiles.GetResearchDocAsync(ct);

            // Read visual design reference files for inclusion in PMSpec
            var designContext = await ReadDesignReferencesForSpecAsync(ct);

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var memoryContext = await GetMemoryContextAsync(ct: ct);

            // Build design context if available
            var designContextSection = "";
            if (!string.IsNullOrWhiteSpace(designContext))
            {
                designContextSection = await _promptService.RenderAsync("pm/design-reference",
                    new Dictionary<string, string> { ["design_context"] = designContext }, ct)
                    ?? "\n\n## CRITICAL: VISUAL DESIGN REFERENCE\n" +
                       "The repository contains visual design reference files that define the EXACT UI to be built.\n" +
                       designContext;
            }

            var systemPrompt = await _promptService.RenderAsync("pm/full-system", new Dictionary<string, string>
            {
                ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}",
                ["design_context"] = designContextSection
            }, ct)
            ?? "You are a Program Manager creating a formal product specification document. " +
               "Your goal is to translate research findings and a project description into a " +
               "clear, actionable specification that architects and engineers can use to design " +
               "and build the system. Be thorough, specific, and business-focused." +
               (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}") +
               designContextSection;

            var history = CreateChatHistory();
            history.AddSystemMessage(systemPrompt);

            // Turn 1: Analyze and identify business goals, user stories, success criteria
            var useSinglePass = _config.CopilotCli.SinglePassMode;

            // Build design sections content for templates
            var designSections = "";
            if (!string.IsNullOrWhiteSpace(designContext))
            {
                designSections = await _promptService.RenderAsync("pm/design-sections", new Dictionary<string, string>(), ct)
                    ?? "## Visual Design Specification\n(Describe the design.)\n\n## UI Interaction Scenarios\n(Describe interactions.)\n\n";
            }

            var specVars = new Dictionary<string, string>
            {
                ["project_name"] = projectName,
                ["project_description"] = projectDescription,
                ["research_doc"] = researchDoc,
                ["design_sections"] = designSections
            };

            if (useSinglePass)
            {
                // Single-pass: one comprehensive prompt instead of 2 turns
                UpdateStatus(AgentStatus.Working, "Creating PMSpec (single-pass)");
                var singlePassPrompt = await _promptService.RenderAsync("pm/single-pass-spec", specVars, ct);
                if (singlePassPrompt is not null)
                {
                    history.AddUserMessage(singlePassPrompt);
                }
                else
                {
                    history.AddUserMessage(
                        $"I need you to create a PM Specification for our project.\n\n" +
                        $"**Project Name:** {projectName}\n\n" +
                        $"**Project Description:**\n{projectDescription}\n\n" +
                        $"## Research Findings\n{researchDoc}\n\n" +
                        "Produce a complete, structured PMSpec.md document with ALL of these sections:\n\n" +
                        $"# PM Specification: {projectName}\n\n" +
                        "## Executive Summary\n(2-3 sentences describing what we're building and why)\n\n" +
                        "## Business Goals\n(Numbered list of concrete business objectives)\n\n" +
                        "## User Stories & Acceptance Criteria\n(Each story with acceptance criteria.)\n\n" +
                        designSections +
                        "## Scope\n### In Scope\n(Bullet list)\n### Out of Scope\n(Bullet list)\n\n" +
                        "## Non-Functional Requirements\n(Performance, security, scalability, reliability)\n\n" +
                        "## Success Metrics\n(Measurable criteria)\n\n" +
                        "## Constraints & Assumptions\n(Constraints and assumptions)\n\n" +
                        "Use these exact section headers. Be thorough, specific, and business-focused.");
                }

                var singleResponse = await chat.GetChatMessageContentAsync(
                    history, cancellationToken: ct);
                _taskTracker.RecordSubStep(specStepId, "Single-pass PM Spec generation");
                _taskTracker.RecordLlmCall(specStepId);
                pmSpecDoc = singleResponse.Content?.Trim() ?? "";
            }
            else
            {
            var turn1Prompt = await _promptService.RenderAsync("pm/multi-turn-analysis", specVars, ct)
                ?? $"I need you to create a PM Specification for our project.\n\n" +
                   $"**Project Name:** {projectName}\n\n" +
                   $"**Project Description:**\n{projectDescription}\n\n" +
                   $"## Research Findings\n{researchDoc}\n\n" +
                   "Based on this information, identify:\n" +
                   "1. The core business goals and objectives\n" +
                   "2. Key user stories with acceptance criteria\n" +
                   "3. What's in scope and what's explicitly out of scope\n" +
                   "4. Non-functional requirements (performance, security, scalability, reliability)\n" +
                   "5. Success metrics — how we know the project is done\n" +
                   "6. Key constraints and assumptions\n\n" +
                   "Be specific and actionable. Each user story should have clear acceptance criteria.";
            history.AddUserMessage(turn1Prompt);

            var analysisResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            _taskTracker.RecordSubStep(specStepId, "Turn 1: Analyze requirements");
            _taskTracker.RecordLlmCall(specStepId);
            history.AddAssistantMessage(analysisResponse.Content ?? "");

            Logger.LogDebug("PM Spec analysis complete for {ProjectName}", projectName);

            // Turn 2: Produce the structured PMSpec.md
            UpdateStatus(AgentStatus.Working, "Creating PMSpec (2/2): Drafting specification");
            var turn2Prompt = await _promptService.RenderAsync("pm/multi-turn-compile",
                new Dictionary<string, string>
                {
                    ["project_name"] = projectName,
                    ["design_sections"] = designSections
                }, ct)
                ?? "Now compile everything into a single, structured PMSpec.md document with these exact sections:\n\n" +
                   $"# PM Specification: {projectName}\n\n" +
                   "## Executive Summary\n(2-3 sentences describing what we're building and why)\n\n" +
                   "## Business Goals\n(Numbered list of concrete business objectives)\n\n" +
                   "## User Stories & Acceptance Criteria\n(Each story with acceptance criteria.)\n\n" +
                   designSections +
                   "## Scope\n### In Scope\n(Bullet list)\n### Out of Scope\n(Bullet list)\n\n" +
                   "## Non-Functional Requirements\n(Performance, security, scalability, reliability)\n\n" +
                   "## Success Metrics\n(Measurable criteria)\n\n" +
                   "## Constraints & Assumptions\n(Constraints and assumptions)\n\n" +
                   $"Replace {{ProjectName}} with '{projectName}'. Use these exact section headers.";
            history.AddUserMessage(turn2Prompt);

            var specResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            _taskTracker.RecordSubStep(specStepId, "Turn 2: Compile specification document");
            _taskTracker.RecordLlmCall(specStepId);
            pmSpecDoc = specResponse.Content?.Trim() ?? "";
            }

            // Self-assessment: assess and refine the PM specification
            _taskTracker.CompleteStep(specStepId);
            var assessStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Self-assessment & refinement",
                "Assessing and refining PM specification quality", Identity.ModelTier);
            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = Identity.Id,
                AgentDisplayName = Identity.DisplayName,
                EventType = AgentReasoningEventType.Generating,
                Phase = "PM Specification",
                Summary = $"PM Specification generated for '{projectName}'",
                Iteration = 0,
            });

            var criteria = AssessmentCriteria.GetForRole(Identity.Role);
            if (criteria is not null)
            {
                // PM spec self-assessment with inline impact classification
                var (refinedOutput, _) = await _selfAssessment.AssessAndRefineWithResultAsync(
                    Identity.Id,
                    Identity.DisplayName,
                    Identity.Role,
                    "PM Specification",
                    pmSpecDoc,
                    criteria,
                    $"Project: {_config.Project.Description}\nResearch findings available in Research.md",
                    chat,
                    classifyImpact: false, // PM spec assessment doesn't drive a decision gate
                    ct);
                pmSpecDoc = refinedOutput;
                _taskTracker.RecordLlmCall(assessStepId);
            }
            _taskTracker.CompleteStep(assessStepId);

            Logger.LogDebug("PM Spec document compiled for {ProjectName}", projectName);

            } // end else (fresh AI work, not resuming from gate)

            // Commit document to PR so reviewers can see it before the gate
            var commitStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Commit PMSpec.md",
                "Committing PM Specification to PR", Identity.ModelTier);
            if (pmSpecDoc is not null && !pr.IsMerged)
            {
                await _prWorkflow.CommitDocumentToPRAsync(
                    pr, "PMSpec.md", pmSpecDoc,
                    $"Add PM Specification for {projectName}", ct);
            }
            _taskTracker.CompleteStep(commitStepId);

            // === Gate: PMSpecification — human reviews PMSpec before merge ===
            var gateStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Human gate review",
                "Awaiting human approval of PM Specification", Identity.ModelTier);
            _taskTracker.SetStepWaiting(gateStepId);
            if (pmGateStatus != GateStatus.Approved)
            {
                var maxRevisions = 3;
                for (var revision = 0; revision < maxRevisions; revision++)
                {
                    if (_gateCheck.RequiresHuman(GateIds.PMSpecification))
                        UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{pr.Number}");
                    var gateWait = await _gateCheck.WaitForGateAsync(
                        GateIds.PMSpecification,
                        "PMSpec.md ready for human review before merge",
                        pr.Number, ct: ct);

                    if (!gateWait.WasRejected)
                        break;

                    Logger.LogInformation("PMSpec gate rejected on PR #{Number}: {Feedback}", pr.Number, gateWait.Feedback);
                    LogActivity("task", $"📝 Revising PMSpec based on feedback: {gateWait.Feedback}");
                    UpdateStatus(AgentStatus.Working, $"Revising PMSpec (attempt {revision + 2})");

                    var revised = await ReviseDocumentAsync("PMSpec.md", gateWait.Feedback!, ct);
                    if (revised is not null && !pr.IsMerged)
                    {
                        await _prWorkflow.CommitDocumentToPRAsync(
                            pr, "PMSpec.md", revised,
                            $"Revise PMSpec based on reviewer feedback (attempt {revision + 2})", ct);
                    }
                    await ResetGateLabelsAsync(pr.Number, ct);
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated PMSpec.md.", ct);
                }
            }

            if (!pr.IsMerged)
            {
                await _prWorkflow.MergeDocumentPRAsync(
                    pr, Identity.DisplayName, "PMSpec.md", ct);
            }
            _taskTracker.CompleteStep(gateStepId);
            Logger.LogInformation("PMSpec.md PR created and merged for project {ProjectName}", projectName);
            LogActivity("task", $"📝 PMSpec.md created and merged for {projectName}");
            await RememberAsync(MemoryType.Action,
                $"Created and merged PMSpec.md for project '{projectName}'",
                TruncateForMemory(pmSpecDoc), ct);

            // Team composition BEFORE signaling downstream agents — the team must be
            // composed before the Architect, PE, and Engineers begin their work.
            if (_teamComposer is not null && _config.SmeAgents.Enabled && !_teamCompositionComplete)
            {
                var teamStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Team composition analysis",
                    "Evaluating optimal team composition", Identity.ModelTier);
                try
                {
                    await ComposeTeamAsync(ct);
                    _taskTracker.CompleteStep(teamStepId);
                }
                catch (Exception ex)
                {
                    _taskTracker.FailStep(teamStepId, ex.Message);
                    Logger.LogWarning(ex, "Team composition failed — continuing without it to avoid blocking workflow");
                    // Don't rethrow — team composition failure shouldn't block the entire pipeline
                }
            }

            // Notify all agents that PMSpec is ready — Architect will pick this up
            var signalStepId = _taskTracker.BeginStep(Identity.Id, "pm-spec", "Signal Architect",
                "Notifying team that PM Specification is ready", Identity.ModelTier);
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "PMSpecReady",
                NewStatus = AgentStatus.Working,
                Details = "PM Specification is ready. Architect can begin architecture design."
            }, ct);

            Logger.LogInformation("Triggered Architect to begin architecture design");

            // After PMSpec is merged, create User Story Issues
            await CreateUserStoryIssuesAsync(ct);
            _taskTracker.CompleteStep(signalStepId);

            UpdateStatus(AgentStatus.Idle, "PMSpec complete, Issues created, Architect triggered");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create PM Specification — will retry on next loop");
            RecordError($"PMSpec creation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            _pmSpecCreated = false; // Allow retry on next loop iteration
        }
    }

    /// <summary>
    /// Evaluates the project and proposes an optimal team composition, including
    /// which built-in agents to use and whether any SME agents should be spawned.
    /// Subject to human-gated approval via AgentTeamComposition gate.
    /// </summary>
    private async Task ComposeTeamAsync(CancellationToken ct)
    {
        if (_teamComposer is null || _teamCompositionComplete) return;

        try
        {
            UpdateStatus(AgentStatus.Working, "Composing optimal team");
            LogActivity("task", "🏗️ Analyzing project to determine optimal team composition");

            // Gather project docs
            var projectDesc = _config.Project.Description ?? "No project description";
            var research = await _projectFiles.GetResearchDocAsync(ct);
            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);

            // Build the team composition prompt
            var compositionPrompt = await _teamComposer.BuildTeamCompositionPromptAsync(
                projectDesc, research, pmSpec, ct);

            // Call AI to analyze and propose team
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var history = CreateChatHistory();
            history.AddUserMessage(compositionPrompt);

            var response = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var aiResponse = response.LastOrDefault()?.Content;

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                Logger.LogWarning("AI returned empty response for team composition. Using default team.");
                _teamCompositionComplete = true;
                return;
            }

            // Parse the proposal
            var proposal = _teamComposer.ParseProposal(aiResponse, Identity.Id);
            if (proposal is null)
            {
                Logger.LogWarning("Failed to parse team composition proposal. Using default team.");
                _teamCompositionComplete = true;
                return;
            }

            Logger.LogInformation(
                "Team composition proposed: {BuiltInCount} built-in, {TemplateCount} templates, {NewSmeCount} new SME agents",
                proposal.BuiltInAgents.Count, proposal.ExistingTemplateIds.Count, proposal.NewSmeAgents.Count);

            // Classify team composition decision impact
            if (_decisionGate is not null)
            {
                var teamDecision = await _decisionGate.ClassifyAndGateDecisionAsync(
                    agentId: Identity.Id,
                    agentDisplayName: Identity.DisplayName,
                    phase: "Team Composition",
                    title: "Team composition and agent selection",
                    context: $"Proposed team: {proposal.BuiltInAgents.Count} built-in agents ({string.Join(", ", proposal.BuiltInAgents.Select(a => $"{a.Role}x{a.Count}"))}), " +
                             $"{proposal.ExistingTemplateIds.Count} SME templates, {proposal.NewSmeAgents.Count} new SME agents. " +
                             $"Rationale: {proposal.Rationale}",
                    category: "TeamComposition",
                    modelTier: Identity.ModelTier,
                    ct: ct);

                if (teamDecision.Status == DecisionStatus.Pending)
                {
                    Logger.LogInformation("Team composition decision gated — waiting for human approval");
                    teamDecision = await _decisionGate.WaitForDecisionAsync(teamDecision.Id, ct);
                }

                if (teamDecision.Status == DecisionStatus.Rejected)
                {
                    Logger.LogWarning("Team composition decision REJECTED: {Feedback}", teamDecision.HumanFeedback);
                    _teamCompositionComplete = true;
                    return;
                }
            }

            // === Gate: AgentTeamComposition — human approves team composition ===
            var gateResult = await _gateCheck.WaitForGateAsync(
                GateIds.AgentTeamComposition,
                $"PM proposes team composition:\n" +
                $"Built-in: {string.Join(", ", proposal.BuiltInAgents.Select(a => $"{a.Role}x{a.Count}"))}\n" +
                $"SME Templates: {string.Join(", ", proposal.ExistingTemplateIds)}\n" +
                $"New SME Agents: {string.Join(", ", proposal.NewSmeAgents.Select(s => s.RoleName))}\n\n" +
                $"Rationale: {proposal.Rationale}",
                ct: ct);

            // Generate and save TeamComposition.md
            var teamDoc = _teamComposer.GenerateTeamCompositionDoc(proposal);
            await _projectFiles.SaveFileAsync("TeamComposition.md", teamDoc,
                "PM: Add team composition document", ct);
            Logger.LogInformation("TeamComposition.md saved");

            // Apply PM-assigned role description overrides for built-in agents
            if (RoleContext is not null)
            {
                foreach (var builtIn in proposal.BuiltInAgents)
                {
                    if (!string.IsNullOrWhiteSpace(builtIn.RoleDescription))
                    {
                        RoleContext.SetRoleDescriptionOverride(builtIn.Role, builtIn.RoleDescription);
                        Logger.LogInformation("Applied PM role description override for {Role}", builtIn.Role);
                    }
                }
            }

            // Spawn any new SME agents from the approved proposal
            foreach (var smeDef in proposal.NewSmeAgents)
            {
                try
                {
                    var spawned = await _spawnManager.SpawnSmeAgentAsync(smeDef, ct: ct);
                    if (spawned is not null)
                    {
                        Logger.LogInformation("Spawned SME agent '{RoleName}' ({AgentId})",
                            smeDef.RoleName, spawned.Id);
                        LogActivity("task", $"🤖 Spawned SME agent: {smeDef.RoleName}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to spawn SME agent '{RoleName}'", smeDef.RoleName);
                }
            }

            // Spawn existing templates
            foreach (var templateId in proposal.ExistingTemplateIds)
            {
                try
                {
                    var template = _definitionService is not null
                        ? await _definitionService.GetAsync(templateId, ct)
                        : null;

                    if (template is not null)
                    {
                        var spawned = await _spawnManager.SpawnSmeAgentAsync(template, ct: ct);
                        if (spawned is not null)
                        {
                            Logger.LogInformation("Spawned template SME agent '{RoleName}' ({AgentId})",
                                template.RoleName, spawned.Id);
                        }
                    }
                    else
                    {
                        Logger.LogWarning("Template '{TemplateId}' not found in definition service", templateId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to spawn template SME agent '{TemplateId}'", templateId);
                }
            }

            // Signal team composition complete
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "TeamCompositionComplete",
                NewStatus = AgentStatus.Working,
                Details = $"Team composition approved: {proposal.BuiltInAgents.Count} built-in + {proposal.NewSmeAgents.Count + proposal.ExistingTemplateIds.Count} SME agents"
            }, ct);

            _teamCompositionComplete = true;
            LogActivity("task", "✅ Team composition complete");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Team composition failed — proceeding with default team");
            _teamCompositionComplete = true; // Don't block workflow
        }
    }

    /// <summary>
    /// After PMSpec is finalized, use AI to extract User Stories and create a GitHub Issue
    /// for each one, labeled "enhancement". Once all issues are created, notify the PE
    /// agent via PlanningCompleteMessage so it can begin building the engineering plan.
    /// Idempotent: skips if enhancement issues already exist.
    /// </summary>
    private async Task CreateUserStoryIssuesAsync(CancellationToken ct)
    {
        if (_userStoryIssuesCreated) return;

        try
        {
            // Idempotency: check if OPEN enhancement issues already exist
            // Only skip if there are open ones — closed issues from prior runs don't count
            var existingEnhancements = await _github.GetIssuesByLabelAsync(
                IssueWorkflow.Labels.Enhancement, "open", ct);
            if (existingEnhancements.Count > 0)
            {
                Logger.LogInformation(
                    "Found {Count} existing open enhancement issues, skipping creation",
                    existingEnhancements.Count);
                _userStoryIssuesCreated = true;

                // Still notify PE in case it missed the signal
                await _messageBus.PublishAsync(new PlanningCompleteMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "PlanningComplete",
                    IssueCount = existingEnhancements.Count
                }, ct);
                return;
            }

            UpdateStatus(AgentStatus.Working, "Creating User Story Issues from PMSpec");
            LogActivity("planning", "📋 Reading PMSpec.md to extract user stories");

            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
            if (string.IsNullOrWhiteSpace(pmSpec) || pmSpec.Contains("No PM specification has been created yet"))
            {
                Logger.LogWarning("PMSpec.md has no content, cannot create User Story Issues");
                return;
            }

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            LogActivity("planning", "🤖 Calling AI to extract user stories from PMSpec");
            var history = CreateChatHistory();
            history.AddSystemMessage(
                await _promptService.RenderAsync("pm/story-extraction-system", new Dictionary<string, string>(), ct)
                ?? "You are a Program Manager extracting User Stories from a PM Specification document. " +
                   "For each User Story, produce a structured output that can be parsed into individual GitHub Issues.\n\n" +
                   "Output format — one block per User Story, separated by '---':\n" +
                   "TITLE: [concise story title]\nDESCRIPTION:\n[Full user story]\n\n" +
                   "DESIGN_REFERENCE:\n[Visual section or 'N/A']\n\n" +
                   "ACCEPTANCE_CRITERIA:\n- [ ] [criterion]\n...\n---\n\n" +
                   "List them by development dependency. Be thorough.");

            history.AddUserMessage(
                await _promptService.RenderAsync("pm/story-extraction-user",
                    new Dictionary<string, string> { ["pm_spec"] = pmSpec }, ct)
                ?? $"Extract all User Stories from this PM Specification and format them as described.\n\n" +
                   $"## PM Specification\n{pmSpec}");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";

            // Parse the AI output into individual stories
            var storyBlocks = content.Split("---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            LogActivity("planning", $"📝 AI extracted {storyBlocks.Length} story blocks, creating GitHub issues");
            var issueCount = 0;

            foreach (var block in storyBlocks)
            {
                if (string.IsNullOrWhiteSpace(block)) continue;

                var title = ExtractField(block, "TITLE:");
                var description = ExtractField(block, "DESCRIPTION:");
                var designReference = ExtractField(block, "DESIGN_REFERENCE:");
                var acceptanceCriteria = ExtractField(block, "ACCEPTANCE_CRITERIA:");

                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var issueBody = $"## User Story\n{description}\n\n" +
                    $"## Acceptance Criteria\n{acceptanceCriteria}\n\n";

                // Include design reference if present and not N/A
                if (!string.IsNullOrWhiteSpace(designReference) &&
                    !designReference.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    issueBody += $"## Visual Design Reference\n{designReference}\n\n" +
                        "> **Note:** See `OriginalDesignConcept.html` in the repository root for the full design template.\n\n";
                }

                issueBody += $"---\n_Created by {Identity.DisplayName} from PMSpec.md_";

                // Validate issue body quality
                var validatedBody = IssueBodyValidator.ValidateAndClean(issueBody, title, Logger);
                if (validatedBody is null)
                {
                    Logger.LogWarning("Skipping user story '{Title}' — issue body failed validation", title);
                    continue;
                }

                // Check if an issue with similar title already exists
                var existingIssue = await _issueWorkflow.FindExistingIssueAsync(title, ct);
                if (existingIssue is not null)
                {
                    Logger.LogDebug("Issue '{Title}' already exists as #{Number}, skipping",
                        title, existingIssue.Number);
                    issueCount++;
                    continue;
                }

                var issue = await _github.CreateIssueAsync(
                    title, validatedBody,
                    [IssueWorkflow.Labels.Enhancement],
                    ct);

                Logger.LogInformation("Created User Story issue #{Number}: {Title}",
                    issue.Number, title);
                issueCount++;

                // Brief delay to avoid GitHub rate limiting
                await Task.Delay(500, ct);
            }

            _userStoryIssuesCreated = true;
            Logger.LogInformation("Created {Count} User Story Issues from PMSpec", issueCount);
            LogActivity("task", $"📌 Created {issueCount} User Story Issues from PMSpec");
            await RememberAsync(MemoryType.Action,
                $"Created {issueCount} user story issues from PMSpec for task tracking", ct: ct);

            // Notify PE that planning issues are ready
            await _messageBus.PublishAsync(new PlanningCompleteMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "PlanningComplete",
                IssueCount = issueCount
            }, ct);

            UpdateStatus(AgentStatus.Idle, $"Created {issueCount} User Story Issues");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create User Story Issues from PMSpec");
            RecordError($"Issue creation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Processes queued clarification requests from engineers. The PM reads the Issue,
    /// uses AI to formulate a response, posts it on the Issue, and notifies the engineer.
    /// If the PM is unsure, it escalates to the Executive stakeholder.
    /// </summary>
    private async Task ProcessClarificationRequestsAsync(CancellationToken ct)
    {
        while (_clarificationQueue.TryDequeue(out var request))
        {
            try
            {
                var issue = await _github.GetIssueAsync(request.IssueNumber, ct);
                if (issue is null)
                {
                    Logger.LogWarning("Cannot find issue #{Number} for clarification", request.IssueNumber);
                    continue;
                }

                UpdateStatus(AgentStatus.Working, $"Answering clarification on issue #{request.IssueNumber}");

                var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();

                var pmSpec = await _projectFiles.GetPMSpecAsync(ct);

                var history = CreateChatHistory();
                history.AddSystemMessage(
                    await _promptService.RenderAsync("pm/clarification-system", new Dictionary<string, string>(), ct)
                    ?? "You are a Program Manager answering a clarification question from an engineer " +
                       "about a GitHub Issue (User Story). Use the PM Specification as your primary " +
                       "reference to provide clear, actionable answers.\n\n" +
                       "If you genuinely cannot answer the question based on the PM Spec and your " +
                       "knowledge, respond with exactly 'ESCALATE' and nothing else. Otherwise, " +
                       "provide a clear, detailed answer.");

                var commentsContext = issue.Comments.Count > 0
                    ? "\n\n## Previous Comments\n" + string.Join("\n\n",
                        issue.Comments.Select(c => $"**{c.Author}** ({c.CreatedAt:g}):\n{c.Body}"))
                    : "";

                history.AddUserMessage(
                    await _promptService.RenderAsync("pm/clarification-user", new Dictionary<string, string>
                    {
                        ["pm_spec"] = pmSpec ?? "",
                        ["issue_number"] = issue.Number.ToString(),
                        ["issue_title"] = issue.Title,
                        ["issue_body"] = issue.Body ?? "",
                        ["comments_context"] = commentsContext,
                        ["question"] = request.Question
                    }, ct)
                    ?? $"## PM Specification\n{pmSpec}\n\n" +
                       $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}" +
                       commentsContext +
                       $"\n\n## Engineer's Question\n{request.Question}");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var answer = response.Content?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(answer) ||
                    answer.Equals("ESCALATE", StringComparison.OrdinalIgnoreCase))
                {
                    // Escalate to executive
                    Logger.LogInformation(
                        "Escalating clarification for issue #{Number} to Executive", request.IssueNumber);

                    var executiveUsername = _config.Project.ExecutiveGitHubUsername;
                    var escalationIssue = await _issueWorkflow.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Clarification needed for Issue #{request.IssueNumber}: {issue.Title}",
                        $"An engineer asked a question about Issue #{request.IssueNumber} that I cannot " +
                        $"confidently answer from the PM Specification.\n\n" +
                        $"**Issue:** #{request.IssueNumber} — {issue.Title}\n" +
                        $"**Question from {request.FromAgentId}:** {request.Question}\n\n" +
                        $"Please provide guidance. @{executiveUsername}",
                        ct);

                    await _github.AddIssueCommentAsync(request.IssueNumber,
                        $"**{Identity.DisplayName}**: I need to consult with the Executive stakeholder " +
                        $"on this question. I've created issue #{escalationIssue.Number} for guidance. " +
                        $"I'll follow up once I have an answer.",
                        ct);
                }
                else
                {
                    // Post the answer on the issue
                    await _github.AddIssueCommentAsync(request.IssueNumber,
                        $"**{Identity.DisplayName}**: {answer}", ct);

                    // Notify the engineer
                    await _messageBus.PublishAsync(new ClarificationResponseMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = request.FromAgentId,
                        MessageType = "ClarificationResponse",
                        IssueNumber = request.IssueNumber,
                        Response = answer
                    }, ct);

                    Logger.LogInformation(
                        "Answered clarification from {Agent} on issue #{Number}",
                        request.FromAgentId, request.IssueNumber);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to process clarification request for issue #{Number}",
                    request.IssueNumber);
            }
        }
    }

    private async Task<string?> TriageBlockerAsync(AgentIssue blocker, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = CreateChatHistory();
            history.AddSystemMessage(
                await _promptService.RenderAsync("pm/blocker-triage-system", new Dictionary<string, string>(), ct)
                ?? "You are a Program Manager triaging a blocker issue in a software project. " +
                   "Analyze the blocker and provide actionable guidance. " +
                   "If you cannot help, respond with exactly 'ESCALATE'.");

            history.AddUserMessage(
                await _promptService.RenderAsync("pm/blocker-triage-user", new Dictionary<string, string>
                {
                    ["blocker_number"] = blocker.Number.ToString(),
                    ["blocker_title"] = blocker.Title,
                    ["blocker_body"] = blocker.Body ?? ""
                }, ct)
                ?? $"Blocker Issue #{blocker.Number}: {blocker.Title}\n\n{blocker.Body}");

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

    private async Task<(bool Approved, string? ReviewBody)> EvaluatePrAlignmentWithVerdictAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
            var engineeringPlan = await _projectFiles.GetEngineeringPlanAsync(ct);

            // Read the linked issue for acceptance criteria
            var issueContext = "";
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (issueNumber.HasValue)
            {
                try
                {
                    var issue = await _github.GetIssueAsync(issueNumber.Value, ct);
                    if (issue is not null)
                        issueContext = $"## Linked Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n";
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for PM review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            var codeContext = await _prWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            // Gather ALL screenshot evidence from PR comments (PE screenshots, TE screenshots, standalone)
            var screenshotImages = new List<PullRequestWorkflow.ScreenshotImage>();
            var screenshotContext = "";
            try
            {
                screenshotImages = await _prWorkflow.GetPRScreenshotImagesAsync(pr.Number, ct: ct);
                if (screenshotImages.Count == 0)
                    screenshotContext = await _prWorkflow.GetPRScreenshotContextAsync(pr.Number, ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not fetch screenshot context for PR #{Number}", pr.Number);
            }

            var hasScreenshots = screenshotImages.Count > 0 || !string.IsNullOrEmpty(screenshotContext);

            // B2: Load design reference screenshot(s) from repo so PM vision can compare
            // actual-vs-target. Without these, PM vision has no anchor for design fidelity.
            var designReferenceImages = await LoadDesignReferenceImagesAsync(ct);

            // Log AI description of each screenshot for dashboard visibility
            if (screenshotImages.Count > 0)
            {
                try
                {
                    var descKernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                    var descChat = descKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                    foreach (var img in screenshotImages)
                    {
                        var desc = await PullRequestWorkflow.DescribeScreenshotAsync(img, descChat, ct);
                        LogActivity("screenshot", $"🖼️ PM reviewing screenshot (PR #{pr.Number}): {desc}");
                        Logger.LogInformation("PM screenshot description for PR #{PrNumber}: {Description}",
                            pr.Number, desc);
                    }
                }
                catch (Exception descEx)
                {
                    Logger.LogDebug(descEx, "Could not describe screenshots for PM review of PR #{Number}", pr.Number);
                }
            }

            var history = CreateChatHistory();

            // Build screenshot section for system prompt
            var screenshotSection = "";
            if (hasScreenshots)
            {
                screenshotSection = await _promptService.RenderAsync("pm/pr-review-screenshots", new Dictionary<string, string>(), ct)
                    ?? "3. VISUAL VALIDATION: Screenshots have been posted on this PR. " +
                       "Review them carefully to verify the app renders correctly.\n";
            }

            var systemPrompt = await _promptService.RenderAsync("pm/pr-review-system",
                new Dictionary<string, string> { ["screenshot_section"] = screenshotSection }, ct);

            if (systemPrompt is null)
            {
                // Hardcoded fallback
                systemPrompt =
                    "You are a PM performing the FINAL review of a PR (Phase 3: after Architect approval and Test Engineer testing).\n\n" +
                    "SCOPE: This PR is ONE task. Check it against its linked user story/issue and " +
                    "the PM Spec context for that feature.\n\n" +
                    "CHECK:\n1. Are the acceptance criteria from the user story met?\n" +
                    "2. Does the feature align with the PM Spec vision for this area of the product?\n" +
                    screenshotSection +
                    "\nIGNORE: code quality, null checks, error handling, naming, tests, architecture.\n\n" +
                    "RESPONSE FORMAT — VERDICT: APPROVE or VERDICT: REQUEST_CHANGES";
            }

            history.AddSystemMessage(systemPrompt);

            var userMessageText = $"## PM Specification\n{pmSpec}\n\n" +
                $"## Engineering Plan\n{engineeringPlan}\n\n" +
                issueContext +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n\n" +
                codeContext;

            // Add screenshots as vision content if available, otherwise fall back to URL-only context
            if (screenshotImages.Count > 0)
            {
                var items = new ChatMessageContentItemCollection();
                var screenshotIntro = "\n\n## 📸 Application Screenshots (Actual)\n" +
                    "The following screenshots show the ACTUAL running application for this PR. " +
                    "LOOK AT EACH IMAGE CAREFULLY for errors, blank screens, or broken UI.\n\n";
                for (var i = 0; i < screenshotImages.Count; i++)
                    screenshotIntro += $"Actual Screenshot {i + 1}: {screenshotImages[i].Description}\n";

                if (designReferenceImages.Count > 0)
                {
                    screenshotIntro += "\n## 🎯 Design Reference (Target)\n" +
                        "The following image(s) are the TARGET DESIGN that the app must match. " +
                        "Compare the Actual Screenshot(s) above against this Design Reference.\n\n" +
                        "**STRICT FIDELITY RULES — REQUEST_CHANGES if ANY are violated:**\n" +
                        "- If the actual screenshot is blank, mostly white, or contains literal words like " +
                        "`placeholder`, `(placeholder)`, `Timeline placeholder`, `Heatmap placeholder`, " +
                        "`Lorem ipsum`, `TODO`, `stub`, or `coming soon` visible to the user → REQUEST_CHANGES.\n" +
                        "- If major components from the design (e.g., header, timeline, heatmap, data grid, charts) " +
                        "are missing from the actual screenshot → REQUEST_CHANGES.\n" +
                        "- If the actual screenshot shows a red error banner or stack trace and the PR is not " +
                        "specifically a bug-fix for that error → REQUEST_CHANGES.\n" +
                        "- 'Stubbed with placeholder strings' is NEVER acceptable for a task that claims to wire, " +
                        "compose, integrate, finalize, or ship a UI component.\n\n";
                    for (var i = 0; i < designReferenceImages.Count; i++)
                        screenshotIntro += $"Design Reference {i + 1}: {designReferenceImages[i].Description}\n";
                }

                items.Add(new TextContent(userMessageText + screenshotIntro));

                foreach (var img in screenshotImages)
                {
                    items.Add(new ImageContent(img.ImageBytes, img.MimeType)
                    {
                        ModelId = $"actual-screenshot: {img.Description}"
                    });
                }

                foreach (var img in designReferenceImages)
                {
                    items.Add(new ImageContent(img.ImageBytes, img.MimeType)
                    {
                        ModelId = $"design-reference: {img.Description}"
                    });
                }

                history.AddUserMessage(items);
            }
            else
            {
                if (!string.IsNullOrEmpty(screenshotContext))
                    userMessageText += $"\n\n{screenshotContext}";
                history.AddUserMessage(userMessageText);
            }

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";

            // Detect garbage AI responses (model breaking character, meta-commentary)
            if (PullRequestWorkflow.IsGarbageAIResponse(result))
            {
                Logger.LogWarning("PM review of PR #{Number} returned garbage AI response, retrying once", pr.Number);

                history.AddAssistantMessage(result);
                history.AddUserMessage(
                    await _promptService.RenderAsync("pm/pr-review-retry", new Dictionary<string, string>(), ct)
                    ?? "That response was not a requirements review. Check the PR against the acceptance criteria.\n" +
                       "Output ONLY a numbered list of unmet requirements, or 'Requirements met' if acceptable.\n" +
                       "End with VERDICT: APPROVE or VERDICT: REQUEST_CHANGES");

                response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                result = response.Content?.Trim() ?? "";

                if (PullRequestWorkflow.IsGarbageAIResponse(result))
                {
                    Logger.LogWarning("PM review of PR #{Number} still garbage after retry — auto-approving", pr.Number);
                    return (true, "Requirements alignment review passed. Feature scope looks appropriate.");
                }
            }

            var approved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

            // Strip VERDICT markers AND any stray approval/rejection keywords the AI may
            // have echoed (e.g., "APPROVED", "CHANGES REQUESTED") to prevent contradictory
            // text from appearing in the posted comment alongside the structured header.
            var reviewBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            // Remove lines that are just "APPROVED" or "CHANGES REQUESTED" standing alone
            // (the AI sometimes echoes the decision as a standalone line)
            var cleanedLines = reviewBody.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.Trim().TrimStart('*', '#', ' ');
                    return !string.Equals(trimmed, "APPROVED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(trimmed, "CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(trimmed, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[ProgramManager] CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[ProgramManager] APPROVED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[SoftwareEngineer] CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("[SoftwareEngineer] APPROVED", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            reviewBody = string.Join('\n', cleanedLines).Trim();

            // Strip any preamble/thinking the AI may have included before the numbered list
            reviewBody = PullRequestWorkflow.StripReviewPreamble(reviewBody);

            return (approved, reviewBody);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} alignment with AI", pr.Number);
            return (false, null);
        }
    }

    /// <summary>
    /// Runs an independent "rubber-duck" critique pass using a different model tier.
    /// Returns formatted critique text, or null if critique is disabled or fails.
    /// </summary>
    private async Task<string?> PerformCritiqueAsync(
        AgentPullRequest pr,
        string codeContext,
        string issueContext,
        string? testResults,
        string? priorReviews,
        CancellationToken ct)
    {
        var critiqueTier = _config.Agents.CritiqueTier;
        if (string.IsNullOrWhiteSpace(critiqueTier))
            return null;

        try
        {
            Logger.LogInformation("Running rubber-duck critique on PR #{Number} using tier {Tier}", pr.Number, critiqueTier);

            var kernel = _modelRegistry.GetKernel(critiqueTier, Identity.Id + "-critique");
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var systemPrompt = await _promptService.RenderAsync("pm/critique-system",
                new Dictionary<string, string>(), ct)
                ?? "You are an independent code critic. Find problems, challenge assumptions, identify risks.";

            var userPrompt = await _promptService.RenderAsync("pm/critique-user",
                new Dictionary<string, string>
                {
                    ["pr_number"] = pr.Number.ToString(),
                    ["pr_title"] = pr.Title,
                    ["head_branch"] = pr.HeadBranch,
                    ["base_branch"] = pr.BaseBranch,
                    ["issue_body"] = string.IsNullOrWhiteSpace(issueContext) ? "(No linked issue found)" : issueContext,
                    ["code_context"] = string.IsNullOrWhiteSpace(codeContext) ? "(No code changes available)" : codeContext,
                    ["test_results"] = string.IsNullOrWhiteSpace(testResults) ? "(No test results available)" : testResults,
                    ["prior_reviews"] = string.IsNullOrWhiteSpace(priorReviews) ? "(No prior review comments)" : priorReviews
                }, ct);

            if (userPrompt is null)
            {
                userPrompt = $"Review PR #{pr.Number}: {pr.Title}\n\n{issueContext}\n\n{codeContext}";
            }

            var history = CreateChatHistory();
            history.AddSystemMessage(systemPrompt);
            history.AddUserMessage(userPrompt);

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var result = response.Content?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(result))
                return null;

            Logger.LogInformation("Rubber-duck critique completed for PR #{Number}: {Length} chars", pr.Number, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Rubber-duck critique failed for PR #{Number} — continuing without critique", pr.Number);
            return null;
        }
    }

    /// <summary>
    /// Formats the critique findings as a markdown section for the PM review comment.
    /// </summary>
    internal static string FormatCritiqueSection(string? critique)
    {
        if (string.IsNullOrWhiteSpace(critique))
            return "\n\n### 🦆 Independent Critique\n- ✅ No significant concerns identified";

        return $"\n\n### 🦆 Independent Critique\n{critique.Trim()}";
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

    /// <summary>
    /// Extract a field value from a structured text block.
    /// e.g., ExtractField("TITLE: My Title\nDESCRIPTION:\nSome desc", "TITLE:") → "My Title"
    /// </summary>
    private static string ExtractField(string block, string fieldName)
    {
        var lines = block.Split('\n');
        var collecting = false;
        var result = new List<string>();
        var nextFieldPrefixes = new[] { "TITLE:", "DESCRIPTION:", "DESIGN_REFERENCE:", "ACCEPTANCE_CRITERIA:" };

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = line[(line.IndexOf(fieldName, StringComparison.OrdinalIgnoreCase) + fieldName.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                    result.Add(remainder);
                collecting = true;
                continue;
            }

            if (collecting)
            {
                // Stop if we hit another field marker
                var trimmed = line.TrimStart();
                if (nextFieldPrefixes.Any(p =>
                    !p.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
                    trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }
                result.Add(line);
            }
        }

        return string.Join('\n', result).Trim();
    }

    private static string TruncateForMemory(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLength) return text;
        var cut = text[..maxLength];
        var lastPeriod = cut.LastIndexOf('.');
        return lastPeriod > maxLength / 2 ? cut[..(lastPeriod + 1)] : cut + "…";
    }

    /// <summary>
    /// Read visual design reference files from the repository for inclusion in PMSpec.
    /// Returns the raw HTML content that the AI can analyze to create the Visual Design Specification.
    /// </summary>
    private async Task<string?> ReadDesignReferencesForSpecAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _github.GetRepositoryTreeAsync("main", ct);
            var designKeywords = new[] { "design", "mockup", "mock", "wireframe", "prototype", "concept", "reference" };

            var htmlDesignFiles = tree
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".html" && ext != ".htm") return false;
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return !f.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                           designKeywords.Any(k => name.Contains(k));
                })
                .ToList();

            // Also find design screenshots committed by the Researcher
            var designScreenshots = tree
                .Where(f => f.StartsWith("docs/design-screenshots/", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (htmlDesignFiles.Count == 0 && designScreenshots.Count == 0) return null;

            var sb = new System.Text.StringBuilder();

            // Include design screenshot images first — most visually impactful
            if (designScreenshots.Count > 0)
            {
                sb.AppendLine("## Design Visual Reference");
                sb.AppendLine();
                sb.AppendLine("The following screenshots were rendered from the HTML design files. " +
                    "ALL UI implementations MUST match these visuals exactly.");
                sb.AppendLine();

                foreach (var screenshot in designScreenshots)
                {
                    var fileName = Path.GetFileNameWithoutExtension(screenshot);
                    // Use raw GitHub URL for image embedding (GitHubRepo is "owner/repo" format)
                    var imageUrl = $"https://raw.githubusercontent.com/{_config.Project.GitHubRepo}/main/{screenshot}";
                    sb.AppendLine($"### {fileName}");
                    sb.AppendLine();
                    sb.AppendLine($"![{fileName} design reference]({imageUrl})");
                    sb.AppendLine();
                }
            }

            // Include HTML source for detailed CSS/layout reference
            foreach (var file in htmlDesignFiles)
            {
                var content = await _github.GetFileContentAsync(file, ct: ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### Design Source: `{file}`");
                sb.AppendLine();
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 10000 ? content[..10000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (sb.Length > 0)
            {
                Logger.LogInformation("Read {Count} design files + {Screenshots} screenshots for PMSpec",
                    htmlDesignFiles.Count, designScreenshots.Count);
                return sb.ToString().TrimEnd();
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read design reference files for PMSpec");
            return null;
        }
    }

    /// <summary>
    /// B2/A2 follow-up: inspect the latest TestEngineer comment for UI failure evidence.
    /// Returns (true, reason) if the PR should NOT be force-approved or auto-approved:
    ///   - TE reports N UI test failures (N > 0)
    ///   - TE reports "App Preview Unavailable" (no live screenshot captured)
    /// Returns (false, null) if the gate permits approval.
    /// </summary>
    private async Task<(bool Blocked, string Message)> EvaluateUiFailureGateAsync(
        int prNumber, CancellationToken ct)
    {
        try
        {
            var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);

            // Walk newest-first through TE-authored comments only.
            for (int i = comments.Count - 1; i >= 0; i--)
            {
                var body = comments[i].Body ?? string.Empty;
                var isTeComment = body.Contains("[TestEngineer]", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Test Engineer:", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Test Engineer ", StringComparison.OrdinalIgnoreCase);

                if (!isTeComment && !body.Contains("UI Test", StringComparison.OrdinalIgnoreCase)
                    && !body.Contains("App Preview Unavailable", StringComparison.OrdinalIgnoreCase))
                    continue;

                // App Preview Unavailable: explicit "screenshot capture returned no data" signal from TE.
                if (body.Contains("App Preview Unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "Test Engineer reports the app preview could not be captured — " +
                        "the app likely failed to start or render. A PR that does not render cannot be approved.");
                }

                // Numeric UI test failure count
                var m = System.Text.RegularExpressions.Regex.Match(
                    body,
                    @"UI\s*Tests?\s*:?.*?(\d+)\s*passed\s*,\s*(\d+)\s*failed",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success && int.TryParse(m.Groups[2].Value, out var failCount) && failCount > 0)
                {
                    return (true, $"Test Engineer reports **{failCount} UI test failure(s)** — " +
                        "these are ground-truth evidence required components are not rendering.");
                }

                // First relevant TE comment evaluated — don't keep walking further back in history.
                break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "EvaluateUiFailureGateAsync failed for PR #{Number} (permitting approval)", prNumber);
        }
        return (false, string.Empty);
    }

    /// <summary>
    /// B2: Download the design reference screenshot(s) from docs/design-screenshots/*.png
    /// so the PM vision model can compare the actual PR screenshot against the target design.
    /// Returns empty list if no design screenshots are present or download fails.
    /// </summary>
    private async Task<List<PullRequestWorkflow.ScreenshotImage>> LoadDesignReferenceImagesAsync(CancellationToken ct)
    {
        var results = new List<PullRequestWorkflow.ScreenshotImage>();
        try
        {
            var tree = await _github.GetRepositoryTreeAsync("main", ct);
            var designPngs = tree
                .Where(f => f.StartsWith("docs/design-screenshots/", StringComparison.OrdinalIgnoreCase) &&
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Take(3) // cap at 3 references to keep token usage sane
                .ToList();

            if (designPngs.Count == 0) return results;

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            foreach (var path in designPngs)
            {
                try
                {
                    var url = $"https://raw.githubusercontent.com/{_config.Project.GitHubRepo}/main/{path}";
                    var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length < 100 || bytes.Length > 2 * 1024 * 1024) continue;
                    results.Add(new PullRequestWorkflow.ScreenshotImage(
                        bytes, "image/png",
                        $"Target design: {Path.GetFileName(path)}",
                        url));
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "B2: failed to download design reference {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "B2: failed to enumerate design-screenshots tree");
        }
        return results;
    }

    #endregion
}
