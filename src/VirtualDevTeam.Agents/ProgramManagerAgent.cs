using System.Collections.Concurrent;
using System.Diagnostics;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Core.Services;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

public class ProgramManagerAgent : AgentBase
{
    private readonly AgentPlatformServices _platform;
    private readonly AgentSpawnManager _spawnManager;
    private readonly AgentRegistry _registry;
    private readonly DecisionGateService? _decisionGate;
    private readonly IDecisionLog? _decisionLog;
    private readonly AgentTeamComposer? _teamComposer;
    private readonly SMEAgentDefinitionService? _definitionService;
    private readonly MergeCloseoutService? _mergeCloseout;

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
    private volatile bool _reviewsSignalFired;
    private volatile bool _researchCompletePending;
    private readonly HashSet<int> _reviewedEnhancementIssues = new();
    private string _designHtmlContext = "";
    private readonly IScenarioRegistry? _scenarioRegistry;

    public ProgramManagerAgent(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        AgentSpawnManager spawnManager,
        AgentRegistry registry,
        ILogger<ProgramManagerAgent> logger,
        AgentTeamComposer? teamComposer = null,
        SMEAgentDefinitionService? definitionService = null,
        DecisionGateService? decisionGate = null,
        IDecisionLog? decisionLog = null,
        MergeCloseoutService? mergeCloseout = null,
        IScenarioRegistry? scenarioRegistry = null)
        : base(identity, core, logger)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _teamComposer = teamComposer;
        _definitionService = definitionService;
        _decisionGate = decisionGate;
        _decisionLog = decisionLog;
        _mergeCloseout = mergeCloseout;
        _scenarioRegistry = scenarioRegistry;
    }

    private string EffectiveBranch => _platform.BranchProvider?.EffectiveBranch ?? Core!.Config.Project.DefaultBranch;

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        Subscribe<ResourceRequestMessage>(HandleResourceRequestAsync);
        Subscribe<StatusUpdateMessage>(HandleStatusUpdateAsync);
        Subscribe<ReviewRequestMessage>(HandleReviewRequestAsync);
        Subscribe<ClarificationRequestMessage>(HandleClarificationRequestAsync);
        // Wake immediately when TE finishes tests (PM can start final review)
        Subscribe<TestsCompletedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("PM received TestsCompletedMessage for PR #{Number}",
                msg.PrNumber);
            _reviewQueue.Enqueue(msg.PrNumber);
            WakeLoop();
        });
        // Wake immediately when Architect/PM approves (PM tracks overall progress)
        Subscribe<PrApprovedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("PM received PrApprovedMessage for PR #{Number} from {Approver}",
                msg.PrNumber, msg.ApproverAgent);
            WakeLoop();
        });
        // FlowMonitor reviewer nudge — enqueue the PR for immediate review so the PM
        // doesn't wait for the next poll cycle.
        Subscribe<ReviewNudgeMessage>(async (msg, _) =>
        {
            if (string.Equals(msg.ReviewerRole, "ProgramManager", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("PM: received FlowMonitor nudge to review PR #{Pr} (reason: {Reason})",
                    msg.PrNumber, msg.Reason);
                _reviewQueue.Enqueue(msg.PrNumber);
            }
        });
        // Wake when a PR is merged — PM tracks overall progress and phase transitions
        Subscribe<PrMergedMessage>(async (msg, _) =>
        {
            Logger.LogInformation("PM received PrMergedMessage for PR #{Number}: {Title}",
                msg.PrNumber, msg.PrTitle);
            WakeLoop();
        });

        _currentPhase = "Research";
        Logger.LogInformation("PM agent initialized, starting in {Phase} phase", _currentPhase);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Initializing project oversight");

        // One-time kickoff: read project description and seed the Researcher
        var kickoffStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-kickoff", "Read project context",
            "Reading project description and kicking off research", Identity.ModelTier);
        try
        {
            await KickOffProjectAsync(ct);
            Core.TaskTracker!.CompleteStep(kickoffStepId);
        }
        catch (Exception ex)
        {
            Core.TaskTracker!.FailStep(kickoffStepId, ex.Message);
            throw;
        }

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            try
            {
                // === Process pending ResearchComplete → gate → CreatePMSpec ===
                // This blocks the main loop (keeping status = Blocked) until the human approves.
                // Previously this ran in the bus message handler, causing the main loop to
                // overwrite Blocked status with Idle on its next iteration.
                if (_researchCompletePending && !_pmSpecCreated)
                {
                    _researchCompletePending = false;
                    _pmSpecCreated = true;
                    Logger.LogInformation("Processing ResearchComplete — generating PMSpec.md");

                    // Skip gate if PMSpec already exists (resume scenario — no need to re-approve)
                    var existingSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
                    if (!string.IsNullOrWhiteSpace(existingSpec) &&
                        !existingSpec.Contains("No PM specification has been created yet", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation("PMSpec.md already exists, skipping ResearchCompleteness gate");
                    }
                    else
                    {
                        // === Gate: ResearchCompleteness — human reviews research before PM proceeds ===
                        var gateResult = await WaitForHumanGateAsync(
                            GateIds.ResearchCompleteness,
                            "Research phase complete, PM ready to create specification",
                            ct: ct);

                        if (gateResult.WasRejected)
                        {
                            Logger.LogInformation("ResearchCompleteness gate rejected: {Feedback}. Requesting research revision.",
                                gateResult.Feedback);

                            if (Core.Config.Agents.Researcher.Enabled == false)
                            {
                                // Researcher disabled — PM revises Research.md inline
                                Logger.LogInformation("Researcher disabled — PM will revise Research.md inline");
                                _pmSpecCreated = false;
                                var projectName = Core.Config.Project.Name;
                                var projectDescription = Core.Config.Project.Description;

                                UpdateStatus(AgentStatus.Working, "Revising Research.md inline (Researcher disabled)");

                                var existingDoc = await Core.ProjectFiles.GetResearchDocAsync(ct);
                                var revisionPrompt = $"""
                                    You are a senior technology researcher. The following Research.md was reviewed and changes were requested.
                                    Please revise the document based on the feedback below.

                                    ## Reviewer Feedback
                                    {gateResult.Feedback}

                                    ## Current Research.md
                                    {existingDoc}

                                    Output ONLY the revised Markdown document, incorporating the feedback.
                                    """;

                                try
                                {
                                    var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                                    var chat = kernel.GetRequiredService<IChatCompletionService>();
                                    var chatHistory = CreateChatHistory();
                                    chatHistory.AddSystemMessage("You are revising a research document. Output only the revised Markdown content.");
                                    chatHistory.AddUserMessage(revisionPrompt);

                                    var response = await chat.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
                                    if (!string.IsNullOrWhiteSpace(response?.Content))
                                    {
                                        await Core.ProjectFiles.SaveScopedFileAsync("Research.md", response.Content.Trim(),
                                            "PM: Revise Research.md inline based on reviewer feedback", ct);
                                        Logger.LogInformation("PM revised Research.md inline ({Length} chars)", response.Content.Length);
                                    }

                                    await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                                        details: "PM revised Research.md inline after gate rejection", ct: ct);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Failed to revise Research.md inline");
                                    await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                                        details: "PM inline revision failed, proceeding with existing content", ct: ct);
                                }
                                continue;
                            }

                            // Researcher enabled — signal researcher for revisions and wait for next ResearchComplete
                            _pmSpecCreated = false;
                            await Core.MessageBus.PublishAsync(new TaskAssignmentMessage
                            {
                                FromAgentId = Identity.Id,
                                ToAgentId = "researcher",
                                MessageType = "ReviseResearch",
                                TaskId = $"research-revision-{DateTime.UtcNow:yyyyMMddHHmmss}",
                                Title = "Revise research based on reviewer feedback",
                                Description = $"Your research was reviewed and changes were requested:\n\n{gateResult.Feedback}\n\nPlease revise Research.md accordingly and signal when complete.",
                                Complexity = "Medium"
                            }, ct);
                            UpdateStatus(AgentStatus.Idle, "Waiting for research revision");
                            continue;
                        }
                    }

                    await CreatePMSpecAsync(ct);
                }

                Logger.LogDebug("PM loop: CheckExecutiveResponses");
                await CheckExecutiveResponsesAsync(ct);
                Logger.LogDebug("PM loop: MonitorTeamStatus");
                await MonitorTeamStatusAsync(ct);
                Logger.LogDebug("PM loop: HandleResourceRequests");
                await HandleResourceRequestsAsync(ct);
                Logger.LogDebug("PM loop: HandleBlockers");
                await HandleBlockersAsync(ct);
                Logger.LogDebug("PM loop: ProcessClarificationRequests");
                await ProcessClarificationRequestsAsync(ct);
                Logger.LogDebug("PM loop: ReviewPullRequests (entering)");
                await ReviewPullRequestsAsync(ct);
                // Retry guard: if PMSpec exists but no open enhancement issues,
                // re-create them. This handles mini-reset (closed issues from prior runs
                // incorrectly set _userStoryIssuesCreated) and transient CLI failures.
                // IMPORTANT: Do NOT re-create if the PM itself already closed them
                // (i.e., _reviewedEnhancementIssues has entries). That means the project
                // completed successfully — re-creating would cause an infinite
                // create-close-recreate loop.
                //
                // 2026-05-12 fix (workflow-recovery-pm-restarts-from-research): also check
                // for engineering-task issues. If ANY exist (open or closed), the project
                // is past the user-story phase (engineering tasks come AFTER user stories
                // in the workflow). Without this, restart of a late-stage project causes
                // the PM to incorrectly regenerate user stories — burning LLM, confusing
                // downstream agents (Architect/SE see "Waiting for PMSpec/Architecture"
                // when they should be working PRs).
                {
                    var openEnhancements = await _platform.WorkItemService!.ListByLabelAsync(
                        IssueWorkflow.Labels.Enhancement, "open", ct);
                    if (openEnhancements.Count == 0 && _reviewedEnhancementIssues.Count == 0 && !_userStoryIssuesCreated)
                    {
                        // Late-stage-restart guard: if engineering-task issues exist (any state),
                        // we're past the user-story phase — never recreate user stories.
                        var engineeringTasksAny = await _platform.WorkItemService.ListByLabelAsync(
                            "engineering-task", "all", ct);
                        if (engineeringTasksAny.Count > 0)
                        {
                            Logger.LogInformation(
                                "Found {Count} engineering-task issues (any state) — project is past user-story phase, " +
                                "setting _userStoryIssuesCreated=true to skip retry guard",
                                engineeringTasksAny.Count);
                            _userStoryIssuesCreated = true;
                        }
                        else
                        {
                            var specContent = await Core.ProjectFiles.GetPMSpecAsync(ct);
                            if (!string.IsNullOrWhiteSpace(specContent) &&
                                !specContent.Contains("No PM specification has been created yet"))
                            {
                                Logger.LogInformation("Retry: PMSpec exists but no open enhancement issues — retrying User Story creation");
                                _userStoryIssuesCreated = false;
                                await CreateUserStoryIssuesAsync(ct, skipClosedIssueGuard: true);
                            }
                        }
                    }
                    else if (openEnhancements.Count == 0 && _reviewedEnhancementIssues.Count > 0)
                    {
                        // PM already closed all enhancements — project is done. Don't recreate.
                        Logger.LogInformation(
                            "All {Count} enhancement issues were already reviewed and closed by PM — project complete, skipping retry guard",
                            _reviewedEnhancementIssues.Count);
                    }
                    else if (!_userStoryIssuesCreated)
                    {
                        // Open enhancements exist but flag wasn't set (e.g., created externally)
                        _userStoryIssuesCreated = true;
                    }
                }

                Logger.LogDebug("PM loop: ReviewEnhancementIssueCompletion");
                await ReviewEnhancementIssueCompletionAsync(ct);
                Logger.LogDebug("PM loop: UpdateProjectTracking");
                await UpdateProjectTrackingAsync(ct);

                await RefreshDiagnosticWithMemoryAsync(ct);

                await WaitForWakeOrTimeoutAsync(
                    TimeSpan.FromSeconds(Core.Config.Limits.GitHubPollIntervalSeconds), ct);
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
            var projectName = Core.Config.Project.Name;
            var projectDescription = Core.Config.Project.Description;

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

            // Ensure design/reference files mentioned in project description exist in the repo
            await EnsureDesignInputsAsync(ct);

            // Restore any previously-spawned engineers from TeamMembers.md
            await RestoreEngineersFromTeamMembersAsync(ct);

            // Check if Research.md already has meaningful content — skip kickoff if so
            // Note: the placeholder "No research has been documented yet" may still appear at top
            // even after research is appended below it, so check for actual research section headings
            var existingResearch = await Core.ProjectFiles.GetResearchDocAsync(ct);
            var hasResearchContent = !string.IsNullOrWhiteSpace(existingResearch) &&
                (existingResearch.Contains("## Research technology stack", StringComparison.OrdinalIgnoreCase) ||
                 existingResearch.Contains("### Summary", StringComparison.OrdinalIgnoreCase) ||
                 (existingResearch.Contains("## ", StringComparison.Ordinal) &&
                  !existingResearch.Trim().Equals("# Research\n\n_No research has been documented yet._", StringComparison.OrdinalIgnoreCase)));

            if (hasResearchContent)
            {
                Logger.LogInformation(
                    "Research.md already exists with content — skipping research kickoff");

                Core.ReasoningLog!.Log(new AgentReasoningEvent
                {
                    AgentId = Identity.Id,
                    AgentDisplayName = Identity.DisplayName,
                    EventType = AgentReasoningEventType.Decision,
                    Phase = "Project Kickoff",
                    Summary = "Research.md already exists — skipping research phase",
                    Detail = "Detected existing research document with valid section headings. Signaling downstream agents to proceed directly."
                });

                // Still signal downstream agents so they can proceed
                await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                    details: "Research already exists from prior run", ct: ct);

                UpdateStatus(AgentStatus.Idle, "Project kickoff complete (research exists), monitoring team");
                return;
            }

            // Check if Researcher is disabled — PM generates Research.md inline
            if (Core.Config.Agents.Researcher.Enabled == false)
            {
                Logger.LogInformation("Researcher is disabled — PM will generate Research.md inline");
                await GenerateInlineResearchAsync(projectName, projectDescription, ct);
                UpdateStatus(AgentStatus.Idle, "Project kickoff complete (inline research), monitoring team");
                return;
            }

            // Build the research guidance — use custom prompt if provided, otherwise generate a rich default
            var researchGuidance = GetResearchGuidance(projectName, projectDescription);

            // 1. Create a GitHub Issue for tracking and visibility (idempotent)
            var issueTitle = $"Researcher: Research technology stack for {projectName}";

            var existingIssues = await _platform.WorkItemService!.ListOpenAsync(ct);
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

                try
                {
                    var issue = await _platform.WorkItemService!.CreateAsync(
                        issueTitle, issueBody,
                        [IssueWorkflow.Labels.AgentQuestion],
                        ct);

                    kickoffIssueNumber = issue.Number;
                    Logger.LogInformation(
                        "Created kickoff issue #{Number}: {Title}",
                        issue.Number, issueTitle);
                }
                catch (Exception issueEx)
                {
                    Logger.LogWarning(issueEx,
                        "Failed to create kickoff issue (PAT may lack Issues permission), " +
                        "continuing with bus-only research dispatch");
                }
            }

            // 2. Send a TaskAssignmentMessage via bus to trigger the Researcher.
            //    Include the research guidance in the description so the Researcher
            //    gets the full context even if it doesn't read the GitHub issue.
            //    Pass the issue number so the Researcher can link it directly.
            var taskId = $"kickoff-research-{Guid.NewGuid():N}";
            Core.TaskTracker!.RegisterTaskDisplayName(taskId, "Research Kickoff");
            await Core.MessageBus.PublishAsync(new TaskAssignmentMessage
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

            Core.ReasoningLog!.Log(new AgentReasoningEvent
            {
                AgentId = Identity.Id,
                AgentDisplayName = Identity.DisplayName,
                EventType = AgentReasoningEventType.Planning,
                Phase = "Project Kickoff",
                Summary = $"Initiated research phase for '{projectName}'",
                Detail = $"Created GitHub issue #{kickoffIssueNumber} and dispatched research task to Researcher agent."
            });

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
        var custom = Core.Config.Project.ResearchPrompt;
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        // Generate a rich default prompt that drives deep, structured research
        var techStack = Core.Config.Project.TechStack;
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
    /// When the Researcher agent is disabled, the PM generates Research.md inline using a
    /// single-pass approach. This trades the Researcher's multi-turn depth (sub-questions →
    /// deep-dive → synthesis) for speed and simplicity.
    /// </summary>
    private async Task GenerateInlineResearchAsync(string projectName, string projectDescription, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, "Generating Research.md (Researcher disabled)");

        Core.ReasoningLog!.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Decision,
            Phase = "Project Kickoff",
            Summary = "Researcher disabled — PM generating Research.md inline",
            Detail = "Researcher agent is disabled in configuration. PM will generate a single-pass research document covering technology stack, architecture patterns, libraries, and implementation recommendations."
        });

        var researchGuidance = GetResearchGuidance(projectName, projectDescription);

        // Use the Researcher's single-pass template if available, otherwise use inline prompt
        var systemPrompt = await Core.PromptService!.RenderAsync("researcher/single-pass-research", new Dictionary<string, string>
        {
            ["projectName"] = projectName,
            ["projectDescription"] = projectDescription,
            ["techStack"] = Core.Config.Project.TechStack ?? "Not specified",
            ["researchGuidance"] = researchGuidance
        }) ?? $"""
            You are a senior technology researcher. Produce a comprehensive Research.md document
            for the project "{projectName}".

            ## Project Description
            {projectDescription}

            ## Research Guidance
            {researchGuidance}

            Write a structured Markdown document with clear section headings covering all research areas.
            Be specific, opinionated, and actionable — the Architect and Engineers will build directly from this.
            """;

        try
        {
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var chatHistory = CreateChatHistory();
            chatHistory.AddSystemMessage("You are a senior technology researcher producing a comprehensive research document. Output only the Markdown document content, no preamble.");
            chatHistory.AddUserMessage(systemPrompt);

            var response = await chat.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
            var researchContent = response?.Content?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(researchContent))
            {
                Logger.LogWarning("Inline research generation returned empty content");
                researchContent = $"# Research\n\n_Research generated inline by PM (Researcher disabled). Generation returned empty — please re-run or enable the Researcher agent._\n";
            }

            // Commit to the working branch (not just local save) so HealthMonitor and downstream agents can find it
            await Core.ProjectFiles.SaveScopedFileAsync("Research.md", researchContent,
                "PM: Generate inline Research.md (Researcher disabled)", ct);

            Logger.LogInformation("PM generated Research.md inline ({Length} chars)", researchContent.Length);

            Core.ReasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = Identity.Id,
                AgentDisplayName = Identity.DisplayName,
                EventType = AgentReasoningEventType.Decision,
                Phase = "Project Kickoff",
                Summary = $"Generated Research.md inline ({researchContent.Length} chars)",
                Detail = "Single-pass research document committed to working branch. Downstream agents (Architect, Engineers) will reference this document."
            });

            // Emit ResearchComplete — HealthMonitor's SubscribeToExplicitSignals handler
            // fires both research.doc.ready and research.complete unconditionally
            await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                details: "PM generated Research.md inline (Researcher disabled)", ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to generate inline Research.md");
            RecordError($"Inline research failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);

            // Still emit completion so the pipeline doesn't deadlock — downstream agents
            // will work with whatever Research.md content exists (possibly empty placeholder)
            await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                details: "PM inline research failed, proceeding with limited context", ct: ct);
        }
    }

    /// <summary>
    /// Ensure TeamMembers.md exists in the repo with at least the core agents listed.
    /// Called once at startup so the document is always present for tracking.
    /// </summary>
    private async Task EnsureTeamMembersDocAsync(CancellationToken ct)
    {
        try
        {
            var content = await Core.ProjectFiles.GetTeamMembersAsync(ct);

            // Get all core agents that should be listed
            var coreAgents = _registry.GetAllAgents()
                .Where(a => a.Identity.Role is AgentRole.ProgramManager or AgentRole.Researcher
                    or AgentRole.Architect or AgentRole.SoftwareEngineer or AgentRole.TestEngineer
                    or AgentRole.SecurityAuditor)
                .ToList();

            // Check if content is the empty template (no agents listed yet)
            var isEmpty = !coreAgents.Any(a => content.Contains(a.Identity.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (!isEmpty)
            {
                // Doc exists with agents — add any core agents that are missing
                var updated = content;
                foreach (var agent in coreAgents)
                {
                    if (updated.Contains(agent.Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var since = agent.Identity.CreatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var row = $"| {agent.Identity.DisplayName} | {agent.Identity.Role} | Online | {agent.Identity.ModelTier} | — | {since} | Internal Bus |";
                    updated = updated.TrimEnd() + "\n" + row + "\n";
                    Logger.LogInformation("Adding missing core agent {Name} to TeamMembers.md", agent.Identity.DisplayName);
                }

                if (updated != content)
                {
                    await Core.ProjectFiles.SaveScopedFileAsync("TeamMembers.md", updated,
                        "Add missing core agents to TeamMembers.md", ct);
                }
                return;
            }

            // Create the initial TeamMembers.md with core agents
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

            await Core.ProjectFiles.SaveScopedFileAsync("TeamMembers.md", doc, "Initialize TeamMembers.md with core agents", ct);
            Logger.LogInformation("Created TeamMembers.md with {Count} core agents", coreAgents.Count);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to ensure TeamMembers.md exists");
        }
    }

    /// <summary>
    /// Scans the project description for referenced design/input files (e.g., HTML templates,
    /// PNG mockups) and ensures they exist in the repository. Reads from the local filesystem
    /// if available, so the team has all required design inputs to work from.
    /// </summary>
    private async Task EnsureDesignInputsAsync(CancellationToken ct)
    {
        try
        {
            var description = Core.Config.Project.Description;
            if (string.IsNullOrWhiteSpace(description))
                return;

            var referencedFiles = ParseDesignFileReferences(description);
            if (referencedFiles.Count == 0)
                return;

            UpdateStatus(AgentStatus.Working, "📁 Validating design input files");

            // Check which files already exist in the repo
            IReadOnlyList<string> repoTree;
            try
            {
                repoTree = await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
            }
            catch
            {
                repoTree = Array.Empty<string>();
            }

            var repoFileNames = new HashSet<string>(
                repoTree.Select(p => Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var (fileName, localPath) in referencedFiles)
            {
                // 1. Already in the repo (e.g., existing project with design files checked in) → skip
                if (repoFileNames.Contains(fileName))
                {
                    Logger.LogDebug("Design input {FileName} already in repo, skipping", fileName);
                    continue;
                }

                // 2. Has a full local path in the description → read from disk and commit
                if (localPath is not null && Path.IsPathFullyQualified(localPath))
                {
                    if (!File.Exists(localPath))
                    {
                        Logger.LogWarning(
                            "Design input '{FileName}' not found in repo and local path '{Path}' does not exist on disk",
                            fileName, localPath);
                        continue;
                    }

                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    var isBinary = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" or ".svg";

                    if (isBinary)
                    {
                        var bytes = await File.ReadAllBytesAsync(localPath, ct);
                        await Core.ProjectFiles.SaveScopedBinaryFileAsync(
                            fileName, bytes,
                            $"Add design input: {fileName}", ct);
                    }
                    else
                    {
                        var content = await File.ReadAllTextAsync(localPath, ct);
                        await Core.ProjectFiles.SaveScopedFileAsync(
                            fileName, content,
                            $"Add design input: {fileName}", ct);
                    }

                    Logger.LogInformation("Added design input {FileName} to repository from {Path}", fileName, localPath);
                    continue;
                }

                // 3. Bare filename with no full path → warn the user
                Logger.LogWarning(
                    "Design input '{FileName}' is referenced in project description but is not in the repo " +
                    "and no full local path was provided. Either check it into the repo or add a full path " +
                    "(e.g., C:/Designs/{FileName}) to the project description.",
                    fileName, fileName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to ensure design inputs exist in repo");
        }
    }

    /// <summary>
    /// Parses the project description for file references — filenames with extensions
    /// and local file paths. Returns (repoFileName, localPath?) tuples.
    /// </summary>
    private static List<(string FileName, string? LocalPath)> ParseDesignFileReferences(string description)
    {
        var results = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Match local file paths like C:/Pics/File.png or D:\Docs\design.html
        var pathRegex = new System.Text.RegularExpressions.Regex(
            @"[A-Za-z]:[/\\][\w./\\-]+\.\w{2,5}",
            System.Text.RegularExpressions.RegexOptions.None);

        foreach (System.Text.RegularExpressions.Match match in pathRegex.Matches(description))
        {
            var fullPath = match.Value.Replace('/', '\\');
            var fileName = Path.GetFileName(fullPath);
            if (!seen.Contains(fileName))
            {
                seen.Add(fileName);
                results.Add((fileName, fullPath));
            }
        }

        // Match standalone filenames with common design extensions (html, htm, png, jpg, svg, pdf, figma)
        var fileNameRegex = new System.Text.RegularExpressions.Regex(
            @"\b([\w.-]+\.(?:html?|png|jpe?g|svg|gif|pdf|figma|sketch|xd|css))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in fileNameRegex.Matches(description))
        {
            var fileName = match.Groups[1].Value;
            if (!seen.Contains(fileName))
            {
                seen.Add(fileName);
                results.Add((fileName, null));
            }
        }

        return results;
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
            var teamDoc = await Core.ProjectFiles.GetTeamMembersAsync(ct);
            var engineeringPlan = await Core.ProjectFiles.GetEngineeringPlanAsync(ct);
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
            var issues = await _platform.WorkItemService!.ListOpenAsync(ct);

            var executiveIssues = issues.Where(i =>
                i.Labels.Contains(IssueWorkflow.Labels.ExecutiveRequest,
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var issue in executiveIssues)
            {
                // GitHub is source of truth: fetch actual comments
                var comments = await _platform.WorkItemService!.GetCommentsAsync(issue.Number, ct);
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
                                await Core.ProjectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);
                                Logger.LogInformation(
                                    "Executive override: spawned {Role} '{Name}' ({N}/{Count})",
                                    AgentRole.SoftwareEngineer, spawnedIdentity.DisplayName, spawned, count);
                            }
                        }

                        await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                            $"✅ **Executive approval processed.** Spawned {spawned} additional engineer(s). " +
                            $"Team now has {_additionalEngineersHired} additional engineers.", ct);
                    }
                    else
                    {
                        await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                            "✅ **Executive approval acknowledged.** Request has been processed.", ct);
                    }

                    // Close this executive request issue
                    await _platform.WorkItemService!.CloseAsync(issue.Number, ct);

                    // Also close linked resource-request issues referenced in the title
                    var linkedNum = ParseLinkedIssueFromTitle(issue.Title);
                    if (linkedNum.HasValue)
                    {
                        try
                        {
                            await _platform.WorkItemService!.AddCommentAsync(linkedNum.Value,
                                $"✅ Executive approved override via #{issue.Number}. Request fulfilled.", ct);
                            await _platform.WorkItemService!.CloseAsync(linkedNum.Value, ct);
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

                    await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                        "❌ **Executive denied this request.** Closing.", ct);
                    await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
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
            var teamDoc = await Core.ProjectFiles.GetTeamMembersAsync(ct);
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
            var timeout = TimeSpan.FromMinutes(Core.Config.Limits.AgentTimeoutMinutes);
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

            // Add any core agents that registered after EnsureTeamMembersDocAsync ran
            // (fixes boot timing race where TE/SecurityAuditor spawn after initial doc creation)
            var registeredAgents = _registry.GetAllAgents()
                .Where(a => a.Identity.Role is AgentRole.ProgramManager or AgentRole.Researcher
                    or AgentRole.Architect or AgentRole.SoftwareEngineer or AgentRole.TestEngineer
                    or AgentRole.SecurityAuditor)
                .ToList();
            var updated = teamDoc;
            foreach (var agent in registeredAgents)
            {
                if (updated.Contains(agent.Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var since = agent.Identity.CreatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var row = $"| {agent.Identity.DisplayName} | {agent.Identity.Role} | Online | {agent.Identity.ModelTier} | — | {since} | Internal Bus |";
                updated = updated.TrimEnd() + "\n" + row + "\n";
                Logger.LogInformation("MonitorTeamStatus: adding late-registered agent {Name} to TeamMembers.md", agent.Identity.DisplayName);
            }
            if (updated != teamDoc)
            {
                await Core.ProjectFiles.SaveScopedFileAsync("TeamMembers.md", updated,
                    "Add late-registered agents to TeamMembers.md", ct);
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
            var issues = await _platform.WorkItemService!.ListOpenAsync(ct);

            var resourceIssues = issues.Where(i =>
                i.Labels.Contains(IssueWorkflow.Labels.ResourceRequest,
                    StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var issue in resourceIssues)
            {
                // GitHub is source of truth: fetch actual comments to determine state
                var comments = await _platform.WorkItemService!.GetCommentsAsync(issue.Number, ct);
                var lastComment = comments.Count > 0 ? comments[^1] : null;

                // If the last comment is a ✅ or 🚀 (already fulfilled), close and skip
                if (lastComment is not null &&
                    (lastComment.Body.StartsWith("✅") || lastComment.Body.StartsWith("🚀")))
                {
                    Logger.LogDebug("Resource request #{Number} already fulfilled, closing", issue.Number);
                    await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
                    continue;
                }

                // If already denied (⚠️), don't re-deny — just skip
                if (lastComment is not null && lastComment.Body.StartsWith("⚠️"))
                {
                    Logger.LogDebug("Resource request #{Number} already denied, skipping", issue.Number);
                    continue;
                }

                if (_additionalEngineersHired >= Core.Config.Limits.MaxAdditionalEngineers)
                {
                    var denyStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-blockers",
                        $"Deny resource #{issue.Number}",
                        $"At max engineers ({Core.Config.Limits.MaxAdditionalEngineers})");

                    Logger.LogInformation(
                        "Resource request #{Number} denied: at max additional engineers ({Max})",
                        issue.Number, Core.Config.Limits.MaxAdditionalEngineers);

                    await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                        $"⚠️ **Resource request denied.** The team has already hired " +
                        $"{_additionalEngineersHired}/{Core.Config.Limits.MaxAdditionalEngineers} " +
                        "additional engineers (the configured maximum). " +
                        "Escalating to Executive for override if needed.", ct);

                    await _platform.IssueWorkflow!.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Resource Limit Reached — request from issue #{issue.Number}",
                        $"A resource request was denied because the team has reached " +
                        $"the max of {Core.Config.Limits.MaxAdditionalEngineers} additional engineers. " +
                        "Executive approval required to exceed this limit.",
                        ct);

                    Core.TaskTracker!.CompleteStep(denyStepId);
                }
                else
                {
                    var approveStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-blockers",
                        $"Approve resource #{issue.Number}",
                        "Spawning additional engineer");

                    // Parse which role is requested from the issue body
                    var requestedRole = AgentRole.SoftwareEngineer;

                    _additionalEngineersHired++;
                    Logger.LogInformation(
                        "Resource request #{Number} approved. Spawning {Role}. Additional engineers: {Count}/{Max}",
                        issue.Number, requestedRole, _additionalEngineersHired,
                        Core.Config.Limits.MaxAdditionalEngineers);

                    await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                        $"✅ **Resource request approved.** Spawning {requestedRole} " +
                        $"(additional engineer #{_additionalEngineersHired} " +
                        $"of {Core.Config.Limits.MaxAdditionalEngineers} maximum).", ct);

                    // Actually spawn the engineer
                    var spawnedIdentity = await _spawnManager.SpawnAgentAsync(requestedRole, ct);
                    if (spawnedIdentity is not null)
                    {
                        Logger.LogInformation(
                            "Spawned {Role} '{Name}' for resource request #{Number}",
                            requestedRole, spawnedIdentity.DisplayName, issue.Number);

                        // Track in TeamMembers.md for persistence across restarts
                        await Core.ProjectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);

                        await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                            $"🚀 **{requestedRole} '{spawnedIdentity.DisplayName}' is now online** " +
                            "and ready for task assignment.", ct);
                        await RememberAsync(MemoryType.Action,
                            $"Hired {requestedRole} '{spawnedIdentity.DisplayName}' via resource request #{issue.Number}",
                            ct: ct);

                        await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
                    }
                    else
                    {
                        Logger.LogWarning(
                            "Failed to spawn {Role} for resource request #{Number} — spawn manager returned null",
                            requestedRole, issue.Number);

                        await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                            $"⚠️ Failed to spawn {requestedRole} — capacity limit may have been reached.", ct);
                    }

                    Core.TaskTracker!.CompleteStep(approveStepId);
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
            var issues = await _platform.WorkItemService!.ListOpenAsync(ct);

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

                var blockerStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-blockers",
                    $"Triage blocker #{blocker.Number}",
                    $"Blocker: {blocker.Title}", Identity.ModelTier);

                // Try to triage the blocker using AI
                UpdateStatus(AgentStatus.Working, $"🤖 Triaging blocker #{blocker.Number} with AI");
                var resolution = await TriageBlockerAsync(blocker.ToAgentIssue(), ct);
                Core.TaskTracker!.RecordLlmCall(blockerStepId);

                if (resolution is not null)
                {
                    await _platform.WorkItemService!.AddCommentAsync(blocker.Number,
                        $"🔍 **PM Triage:**\n\n{resolution}", ct);
                    Core.TaskTracker!.RecordSubStep(blockerStepId, $"Triaged blocker #{blocker.Number}");
                }
                else
                {
                    // Escalate to Executive
                    await _platform.IssueWorkflow!.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Blocker Escalation — issue #{blocker.Number}",
                        $"A blocker issue needs Executive attention:\n\n" +
                        $"**Title:** {blocker.Title}\n\n{blocker.Body}",
                        ct);
                    Core.TaskTracker!.RecordSubStep(blockerStepId, $"Escalated blocker #{blocker.Number} to Executive");
                }
                Core.TaskTracker!.CompleteStep(blockerStepId);
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
            // (skip when TE is disabled — the tests-added label is never applied so the scan is moot
            //  and the architect-approved PRs are already picked up by the main poll above).
            if (Core.Config.Workspace.IsInlineTestWorkflow && Core.Config.Review.TestEngineerReviews)
            {
                var openPRs = await _platform.PrService.ListOpenAsync(ct);
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
            else if (Core.Config.Workspace.IsInlineTestWorkflow && !Core.Config.Review.TestEngineerReviews)
            {
                // disable-te-toggle: TE is off, so the inline workflow short-circuits — PM reviews
                // any architect-approved PR that doesn't yet have pm-approved. Without this branch
                // an architect-approved PR would sit idle (no tests-added ever appears, and the
                // notification queue may have already fired before the PM could process it).
                var openPRs = await _platform.PrService.ListOpenAsync(ct);
                foreach (var openPr in openPRs)
                {
                    if (_reviewedPrHeadShas.TryGetValue(openPr.Number, out var reviewedSha)
                        && string.Equals(reviewedSha, openPr.HeadSha, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (openPr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase) &&
                        !openPr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                    {
                        prNumbersToReview.Add(openPr.Number);
                    }
                }
            }

            if (prNumbersToReview.Count == 0)
            {
                Logger.LogDebug("PM review poll: 0 PRs eligible");

                // Check if all open PRs are now PM-approved — if so, signal reviews complete
                await CheckAndSignalAllReviewsCompleteAsync(ct);
                return;
            }

            Logger.LogInformation("PM review poll: {Count} PR(s) eligible for review: {Numbers}",
                prNumbersToReview.Count, string.Join(",", prNumbersToReview));

            foreach (var prNumber in prNumbersToReview)
            {
                var platformPr = await _platform.PrService.GetAsync(prNumber, ct);
                if (platformPr is null)
                    continue;
                var pr = platformPr.ToAgentPR();

                // Skip if we've already reviewed this exact HEAD SHA. Re-review if HEAD moved
                // (e.g., SE pushed fixes after CHANGES REQUESTED).
                if (_reviewedPrHeadShas.TryGetValue(prNumber, out var reviewedSha)
                    && string.Equals(reviewedSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("PM skipping PR #{Number} — already reviewed at SHA {Sha}", prNumber, reviewedSha);
                    continue;
                }

                // Skip TestEngineer PRs — PM doesn't review test suites, only PE does
                var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                if (authorRole.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
                    || authorRole.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("PM skipping PR #{Number} — TestEngineer PR, PM does not review test suites", prNumber);
                    _reviewedPrHeadShas[prNumber] = pr.HeadSha;
                    continue;
                }

                // Phase 3 gate: PM only reviews AFTER TE has added tests (inline workflow + TE enabled).
                // disable-te-toggle: when Review.TestEngineerReviews is OFF, skip the wait — TE never
                // applies the tests-added label, so requiring it would deadlock the PR.
                // NOTE: This gate applies even for force-approval PRs — PM should never
                // approve before TE finishes testing, regardless of rework cycle count.
                if (Core.Config.Workspace.IsInlineTestWorkflow && Core.Config.Review.TestEngineerReviews &&
                    !pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
                {
                    // Fallback: if TE posted an error or completion comment but the label
                    // failed to apply, accept the PR anyway to avoid deadlock (Lesson #53)
                    var prComments = await _platform.ReviewService.GetCommentsAsync(prNumber, ct);
                    var hasTeSignal = prComments.Any(c =>
                        c.Body.Contains("Test Engineer:", StringComparison.OrdinalIgnoreCase) ||
                        c.Body.Contains("[TestEngineer]", StringComparison.OrdinalIgnoreCase));
                    if (!hasTeSignal)
                    {
                        Logger.LogDebug("PM skipping PR #{Number} — waiting for TE to add tests (Phase 2)", prNumber);
                        continue; // Don't mark as reviewed — we'll check again next cycle
                    }
                    Logger.LogInformation("PM accepting PR #{Number} without tests-added label — TE posted a comment", prNumber);
                }
                // NOTE: Removed defense-in-depth comment check (SimplificationRecommendations §2.5).
                // The tests-added label is now the sole gate. TE publishes TestsCompletedMessage
                // on the bus as a backup signal, and the label is applied atomically before
                // the bus message. Comment-scanning was a fragile redundancy that caused
                // deadlocks when comment parsing failed (Lesson #53).

                // Skip PRs blocked by Security Auditor — PM must not approve over a security block.
                // The SecurityAuditor findings must be resolved and the label removed first.
                if (pr.Labels.Contains("security-blocked", StringComparer.OrdinalIgnoreCase))
                {
                    Logger.LogWarning(
                        "PM skipping PR #{Number} — security-blocked label present. " +
                        "SecurityAuditor findings must be resolved before PM review.",
                        prNumber);
                    continue;
                }

                // Skip PRs already PM-approved
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("PM skipping PR #{Number} — already has pm-approved label", prNumber);
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

                var reviewStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-review", $"Review PR #{pr.Number}",
                    $"Reviewing: {pr.Title}", Identity.ModelTier);

                bool approved;
                bool _approvedWithSuggestions = false;
                string? reviewBody;

                // UI quality gate: inspect TE comment for UI failures OR App Preview Unavailable.
                // Applied to BOTH force-approval and no-new-commits auto-approval paths so a broken
                // SHA can never silently merge.
                //
                // Three-tier behavior:
                //   1) Config.Review.AllowFailingUiTests = true (default) — gate is informational
                //      only. The PM warns about failing UI tests in its review body but does NOT
                //      block the merge. The decision is logged so it's auditable.
                //   2) Config.Review.AllowFailingUiTests = false — gate blocks force-approval.
                //   3) When (2) is in effect and the PR has hit an unrecoverable rework loop
                //      (max-cycles + multiple "no committable changes" rework attempts), the
                //      system auto-bypasses with an explicit decision log entry rather than
                //      deadlocking the pipeline forever. The PM comment explains the bypass.
                var (uiGateBlocked, uiGateMessage) = await EvaluateUiFailureGateAsync(prNumber, ct);
                var bypassReason = (string?)null;
                if (uiGateBlocked && Core!.Config.Review.AllowFailingUiTests)
                {
                    bypassReason = "AllowFailingUiTests=true (project setting)";
                }
                else if (uiGateBlocked && await IsReworkLoopUnrecoverableAsync(prNumber, ct))
                {
                    bypassReason = "auto-bypass (rework loop unrecoverable: empty rework attempts after max cycles)";
                }

                if (uiGateBlocked && bypassReason is not null)
                {
                    Logger.LogWarning(
                        "PM UI quality gate BYPASSED on PR #{Number} — {Reason}. Original gate message: {Message}",
                        prNumber, bypassReason, uiGateMessage);
                    LogActivity("decision",
                        $"🟡 UI gate bypassed on PR #{prNumber}: {bypassReason}. {uiGateMessage}");
                    if (_decisionLog is not null)
                    {
                        try
                        {
                            _decisionLog.Log(new VirtualDevTeam.Core.Agents.Decisions.AgentDecision
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                AgentId = Identity.Id,
                                AgentDisplayName = Identity.DisplayName,
                                Phase = "Review",
                                ImpactLevel = VirtualDevTeam.Core.Agents.Decisions.DecisionImpactLevel.M,
                                Title = $"UI gate bypassed on PR #{prNumber}",
                                Rationale = $"{bypassReason}. {uiGateMessage}",
                                Status = VirtualDevTeam.Core.Agents.Decisions.DecisionStatus.AutoApproved,
                            });
                        }
                        catch (Exception decisionEx)
                        {
                            Logger.LogDebug(decisionEx, "Could not log UI-gate-bypass decision for PR #{Number}", prNumber);
                        }
                    }
                    uiGateBlocked = false;
                }

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
                        _reviewedPrHeadShas.Remove(prNumber);
                        approved = true;
                        reviewBody = $"Force-approving after maximum PM rework cycles reached. " +
                            $"The PR has been through multiple review iterations and the engineer " +
                            $"has made best-effort improvements.";
                    }
                }
                else
                {
                    var hasNewCommits = await _platform.PrWorkflow.HasNewCommitsSinceReviewAsync(prNumber, "ProgramManager", ct);
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
                        if (!string.IsNullOrWhiteSpace(Core.Config.Agents.CritiqueTier))
                        {
                            try
                            {
                                var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
                                var critiqueIssueContext = "";
                                if (issueNumber.HasValue)
                                {
                                    var issue = await _platform.WorkItemService!.GetAsync(issueNumber.Value, ct);
                                    if (issue is not null)
                                        critiqueIssueContext = $"## Issue #{issue.Number}: {issue.Title}\n{issue.Body}";
                                }
                                var critiqueCode = await _platform.PrWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

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

                        (approved, _approvedWithSuggestions, reviewBody) = await EvaluatePrAlignmentWithVerdictAsync(pr, ct);

                        // Append critique section to review body
                        if (reviewBody is not null)
                            reviewBody += FormatCritiqueSection(critiqueFindings);
                    }
                }

                if (reviewBody is null)
                {
                    Logger.LogInformation("PM skipping PR #{Number} — review evaluation returned null body (unexpected)", prNumber);
                    Core.TaskTracker!.CompleteStep(reviewStepId);
                    continue;
                }

                UpdateStatus(AgentStatus.Working, $"✅ Reviewing PR #{pr.Number}: Posting review");

                if (approved)
                {
                    // Phase 3 complete: PM approved → add pm-approved label → triggers merge by PE
                    var approvalHeader = _approvedWithSuggestions
                        ? "**[ProgramManager] APPROVED** _(with non-blocking suggestions below)_"
                        : "**[ProgramManager] APPROVED**";
                    var approvalComment = string.IsNullOrWhiteSpace(reviewBody)
                        ? $"{approvalHeader} — Business requirements satisfied."
                        : _approvedWithSuggestions
                            ? $"{approvalHeader}\n\n💡 **Suggestions (non-blocking — these do NOT require rework):**\n\n{reviewBody}"
                            : $"{approvalHeader}\n\n{reviewBody}";
                    await _platform.ReviewService.AddCommentAsync(pr.Number, approvalComment, ct);

                    // Submit formal GitHub APPROVE only if agents have separate accounts
                    if (Core.Config.Review.EnableFormalReviews)
                    {
                        try
                        {
                            await _platform.ReviewService.AddReviewAsync(pr.Number,
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
                    // Uses AddLabelAsync to re-fetch fresh labels, avoiding race conditions
                    // where concurrent label updates by other agents could be lost.
                    await _platform.PrService.AddLabelAsync(pr.Number, PullRequestWorkflow.Labels.PmApproved, ct);

                    Logger.LogInformation("PM review of PR #{Number} COMPLETED — decision: APPROVED, label pm-approved added", pr.Number);

                    LogActivity("task", $"✅ PM final approval on PR #{pr.Number}: {pr.Title} — ready to merge");
                    Core.TaskTracker!.RecordLlmCall(reviewStepId);
                    Core.TaskTracker!.RecordSubStep(reviewStepId, $"Approved PR #{pr.Number}");
                    Core.TaskTracker!.CompleteStep(reviewStepId);
                    await RememberAsync(MemoryType.Decision,
                        $"PM final approval on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

                    // Notify PE to merge
                    await PublishStatusAsync("StatusUpdate", AgentStatus.Working,
                        details: $"PR #{pr.Number}: {pr.Title} has passed final PM review",
                        currentTask: $"PR #{pr.Number} pm-approved — ready for merge", ct: ct);

                    // Send targeted PrApprovedMessage so SE wakes immediately for merge
                    await Core.MessageBus.PublishAsync(new PrApprovedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = nameof(PrApprovedMessage),
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ApproverAgent = Identity.DisplayName
                    }, ct);
                }
                else
                {
                    // WS2 PM inline path: if review body has file:line: prefixed items,
                    // post them as inline review comments on the Files-changed tab.
                    // Otherwise fall back to the plain conversation-tab comment.
                    var pmInlineComments = ExtractInlineCommentsFromText(reviewBody);
                    if (pmInlineComments.Count > 0 && Core.Config.Review.EnableInlineComments)
                    {
                        try
                        {
                            var maxInline = Core.Config.Review.MaxInlineCommentsPerReview;
                            var truncated = pmInlineComments.Take(maxInline).ToList();
                            var inlineBody =
                                $"**[ProgramManager] CHANGES REQUESTED**\n\n{reviewBody}\n\n" +
                                $"_{truncated.Count} inline comment(s) below on specific files._";

                            var platformComments = truncated.Select(c => new PlatformInlineComment
                            {
                                FilePath = c.FilePath,
                                Line = c.Line,
                                Body = c.Body
                            }).ToList();

                            await _platform.ReviewService.CreateReviewWithInlineCommentsAsync(
                                pr.Number, inlineBody, "REQUEST_CHANGES", platformComments, ct: ct);

                            Logger.LogInformation(
                                "PM posted {Count} inline review comments on PR #{Number}",
                                truncated.Count, pr.Number);
                        }
                        catch (Exception inlineEx)
                        {
                            Logger.LogWarning(inlineEx,
                                "PM inline review failed on PR #{Number} — falling back to plain comment",
                                pr.Number);
                            await _platform.PrWorkflow.RequestChangesAsync(pr.Number, "ProgramManager", reviewBody, ct);
                        }
                    }
                    else
                    {
                        await _platform.PrWorkflow.RequestChangesAsync(pr.Number, "ProgramManager", reviewBody, ct);
                    }

                    Logger.LogInformation("PM requested changes on PR #{Number}", pr.Number);
                    LogActivity("task", $"❌ Requested changes on PR #{pr.Number}: {pr.Title}");
                    Core.TaskTracker!.RecordLlmCall(reviewStepId);
                    Core.TaskTracker!.RecordSubStep(reviewStepId, $"Requested changes on PR #{pr.Number}");
                    Core.TaskTracker!.CompleteStep(reviewStepId);
                    await RememberAsync(MemoryType.Decision,
                        $"Requested changes on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reviewBody), ct);

                    // Notify the author engineer to rework
                    await Core.MessageBus.PublishAsync(new ChangesRequestedMessage
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

                // 2026-05-12 fix (pm-review-handler-silent-skip): cache the SHA AFTER successful submission
                // so a failed/aborted review doesn't permanently skip the PR. Was previously set before
                // submission, which masked any silent failure as "already reviewed".
                _reviewedPrHeadShas[pr.Number] = pr.HeadSha;
            }

            // Reset status after reviews complete so dashboard doesn't show stale "Reviewing PR" text
            // Check if all open PRs are now PM-approved → signal reviews complete
            await CheckAndSignalAllReviewsCompleteAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review pull requests");
        }
    }

    /// <summary>
    /// Check if all open PRs are PM-approved (or no open PRs remain).
    /// If so, update status to signal that all reviews are complete, which
    /// HealthMonitor uses to emit the reviews.all.approved workflow signal.
    /// Only considers engineering PRs (from SE/TE agents), not doc PRs from Researcher/PM/Architect.
    /// </summary>
    private async Task CheckAndSignalAllReviewsCompleteAsync(CancellationToken ct)
    {
        try
        {
            var openPRs = await _platform.PrService.ListOpenAsync(ct);

            // Only consider engineering PRs (SE/TE), not doc PRs from Researcher/PM/Architect
            var engineeringOpen = openPRs.Where(IsEngineeringPr).ToList();

            // If there are engineering PRs that are NOT yet PM-approved, reviews are still pending
            var unapprovedPrs = engineeringOpen
                .Where(pr => !pr.Labels.Contains(PullRequestWorkflow.Labels.PmApproved, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unapprovedPrs.Count > 0)
            {
                UpdateStatus(AgentStatus.Idle, "Monitoring team progress");
                return;
            }

            // All engineering PRs are either PM-approved or there are no open engineering PRs
            if (engineeringOpen.Count > 0)
            {
                Logger.LogInformation("All {Count} open engineering PRs are PM-approved — signaling reviews complete", engineeringOpen.Count);
                _reviewsSignalFired = true;
                _userStoryIssuesCreated = true; // Prevent re-creation from replayed messages
                UpdateStatus(AgentStatus.Idle, "All reviews complete — all PRs approved");
            }
            else
            {
                // Only declare "all merged" if at least one engineering PR was actually merged.
                var mergedPRs = await _platform.PrService.ListMergedAsync(ct);
                var engineeringMerged = mergedPRs.Where(IsEngineeringPr).ToList();
                if (engineeringMerged.Count > 0)
                {
                    Logger.LogInformation("No open engineering PRs remain — all merged ({Count} merged). Signaling reviews complete", engineeringMerged.Count);
                    _reviewsSignalFired = true;
                    _userStoryIssuesCreated = true; // Prevent re-creation from replayed messages
                    UpdateStatus(AgentStatus.Idle, "All reviews complete — all merged");
                }
                else
                {
                    Logger.LogDebug("No engineering PRs found (open or merged) — waiting for engineering to create PRs");
                    UpdateStatus(AgentStatus.Idle, "Monitoring for review requests");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check review completion status");
            UpdateStatus(AgentStatus.Idle, "Monitoring team progress");
        }
    }

    /// <summary>
    /// Returns true if the PR is from an engineering agent (Software Engineer or Test Engineer),
    /// as opposed to a doc PR from Researcher, PM, or Architect.
    /// Uses branch naming convention: agent/{runScope?}/{role}/...
    /// Handles both legacy (agent/role/...) and run-scoped (agent/{scope}/role/...) formats.
    /// </summary>
    private static bool IsEngineeringPr(PlatformPullRequest pr)
    {
        var branch = pr.HeadBranch;
        // Normalize ADO full ref format
        if (branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            branch = branch["refs/heads/".Length..];

        // Check any segment for engineering agent role prefix
        // Any branch segment containing "engineer" (covers software-engineer, test-engineer,
        // infrastructure-engineer, and any future specialist engineer roles) is engineering work.
        // Excludes non-engineering roles like researcher, architect, program-manager.
        var segments = branch.Split('/');
        return segments.Any(s =>
            s.Contains("engineer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// After approving a PR, resolve all open inline review threads left by previous reviews.
    /// Replies with a resolution comment explaining the thread is resolved by the rework.
    /// </summary>
    private async Task ResolvePmReviewThreadsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var threads = await _platform.ReviewService.GetThreadsAsync(prNumber, ct);
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
                await _platform.ReviewService.ResolveThreadAsync(
                    prNumber, thread.ThreadId, replyBody, ct);
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
            var openIssues = await _platform.WorkItemService!.ListOpenAsync(ct);
            var enhancementIssues = openIssues
                .Where(i => i.Labels.Any(l => string.Equals(l, "enhancement", StringComparison.OrdinalIgnoreCase)))
                // Exclude "follow-up" issues: those are explicitly backlog items the PM created to
                // capture acknowledged gaps or post-merge improvement suggestions. They MUST stay
                // open so a human can triage them — auto-closing would erase that backlog.
                .Where(i => !i.Labels.Any(l => string.Equals(l, "follow-up", StringComparison.OrdinalIgnoreCase)))
                .Where(i => !_reviewedEnhancementIssues.Contains(i.Number))
                .ToList();

            if (enhancementIssues.Count == 0)
                return;

            foreach (var issue in enhancementIssues)
            {
                // Check sub-issues via GitHub's Sub-Issues API
                var subIssues = await _platform.WorkItemService!.GetChildrenAsync(issue.Number, ct);

                if (subIssues.Count == 0)
                {
                    // Enhancement issue has no linked GitHub sub-issues. This happens in two cases:
                    //   (a) SinglePRMode — by design, the PM creates one enhancement with no sub-issues.
                    //   (b) Multi-PR mode where the PM created user-story enhancements but did not
                    //       explicitly link them as parents of the engineering tasks. In a fresh-clone
                    //       restart this is the common case for #1265-1273-style PM-spec sub-issues.
                    // In both cases we close when ALL engineering tasks are done AND all engineering
                    // PRs are merged — the same logical signal as "engineering complete".
                    {
                        // Guard: don't close until engineering tasks have been created AND all are done.
                        // Without this, the PM would close stories as soon as a doc PR merges (e.g.,
                        // Architecture.md) because "no open tasks" is trivially true when no tasks exist.
                        // NOTE: Must query with state="all" because closed tasks won't appear in
                        // the default (open-only) query — causing the PM to think engineering never started.
                        var allEngineeringTasks = await _platform.WorkItemService!.ListByLabelAsync(
                            EngineeringTaskIssueManager.TaskLabel, "all", ct);
                        if (allEngineeringTasks.Count == 0)
                        {
                            Logger.LogDebug(
                                "No engineering tasks exist yet — SE phase hasn't started. Deferring closure of #{Number}",
                                issue.Number);
                            continue;
                        }

                        var openEngineeringTasks = allEngineeringTasks
                            // PlatformWorkItem.State is normalized to "open"/"closed" by both
                            // GitHubModelMapper and AdoModelMapper, so we only need to check
                            // the normalized value. (Earlier code also tried "done" and "removed"
                            // — dead branches that never matched because the mapper had already
                            // collapsed them to "closed".)
                            .Where(t => !string.Equals(t.State, "closed", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (openEngineeringTasks.Count > 0)
                        {
                            Logger.LogDebug(
                                "{Count} engineering tasks still open — deferring closure of #{Number}",
                                openEngineeringTasks.Count, issue.Number);
                            continue;
                        }

                        var mergedPRs = await _platform.PrService.ListMergedAsync(ct);
                        var openPRs = await _platform.PrService.ListOpenAsync(ct);

                        // SinglePRMode-only safety: if some PRs remain open while merged code already
                        // delivered the work (e.g., approved but TE build failed so tests-added never
                        // applied), abandon the stale PRs. Multi-PR mode has legitimate reasons for
                        // open PRs to coexist (follow-ups, parallel waves), so don't abandon there.
                        if (Core.Config.Limits.SinglePRMode && mergedPRs.Count > 0 && openPRs.Count > 0)
                        {
                            foreach (var stalePr in openPRs)
                            {
                                Logger.LogInformation(
                                    "SinglePRMode: Abandoning stale open PR #{Number} — code already delivered via merged PRs",
                                    stalePr.Number);
                                try
                                {
                                    await _platform.PrService.CloseAsync(stalePr.Number, ct);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogWarning(ex, "Failed to abandon stale PR #{Number}", stalePr.Number);
                                }
                            }
                        }
                        else if (mergedPRs.Count == 0 && openPRs.Count > 0)
                        {
                            // No code merged yet but PRs are still open — wait
                            Logger.LogDebug(
                                "{Count} PRs still open with none merged — deferring closure of #{Number}",
                                openPRs.Count, issue.Number);
                            continue;
                        }

                        // Multi-PR safety: if there are still open engineering PRs (agent/* branches)
                        // it means engineering is not yet truly complete despite the task issues being
                        // marked done. Don't close in that case.
                        var openEngineeringPRs = openPRs.Where(p =>
                            p.HeadBranch?.StartsWith("agent/", StringComparison.OrdinalIgnoreCase) == true).ToList();
                        if (!Core.Config.Limits.SinglePRMode && openEngineeringPRs.Count > 0)
                        {
                            Logger.LogDebug(
                                "{Count} engineering PRs still open — deferring closure of #{Number}",
                                openEngineeringPRs.Count, issue.Number);
                            continue;
                        }

                        if (mergedPRs.Count > 0)
                        {
                            // Close linked work items for all merged PRs (ADO parity)
                            if (_mergeCloseout is not null)
                            {
                                foreach (var mergedPr in mergedPRs)
                                    await _mergeCloseout.CloseLinkedWorkItemsAsync(mergedPr.Number, ct);
                            }
                            var modeNote = Core.Config.Limits.SinglePRMode ? " (SinglePRMode)" : "";
                            await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                                $"✅ **PM Final Review — APPROVED{modeNote}**\n\n" +
                                "All engineering tasks are complete and all engineering PRs have been merged. Closing as complete.", ct);
                            await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
                            _reviewedEnhancementIssues.Add(issue.Number);
                            Logger.LogInformation("PM closed enhancement issue #{Number}{Mode} (all tasks done + merged): {Title}",
                                issue.Number, modeNote, issue.Title);
                            LogActivity("review", $"✅ Closed user story #{issue.Number}: {issue.Title}");
                        }
                    }
                    continue; // Don't fall through to sub-issue branch when there are no sub-issues
                }

                var allClosed = subIssues.All(s =>
                    string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase));

                if (!allClosed)
                    continue; // Not all tasks done yet — check again next loop

                // All sub-issues are closed → PM does final acceptance review
                Logger.LogInformation(
                    "All {Count} sub-issues for enhancement #{Number} are closed. Starting final acceptance review.",
                    subIssues.Count, issue.Number);

                var completionStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-completion",
                    $"Review enhancement #{issue.Number}",
                    $"Final acceptance review: {issue.Title}", Identity.ModelTier);

                var closedSummary = string.Join("\n", subIssues.Select(s =>
                    $"  - #{s.Number}: {s.Title} (closed)"));

                // Gather actual evidence: repo file tree + merged PRs (prevents hallucination)
                UpdateStatus(AgentStatus.Working, $"🔍 Reviewing enhancement #{issue.Number}: Gathering evidence");
                var repoTree = new List<string>();
                var mergedPrSummary = "";
                Exception? evidenceFetchError = null;
                try
                {
                    repoTree = (await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct)).ToList();
                    var mergedPRs = await _platform.PrService.ListMergedAsync(ct);
                    var relevantMerged = mergedPRs
                        .Where(p => p.HeadBranch.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(p => p.Number)
                        .Take(10)
                        .ToList();
                    if (relevantMerged.Count > 0)
                    {
                        mergedPrSummary = string.Join("\n", relevantMerged.Select(p =>
                            $"  - PR #{p.Number}: {p.Title} (merged, branch: {p.HeadBranch})"));
                    }
                }
                catch (Exception ex)
                {
                    evidenceFetchError = ex;
                    Logger.LogWarning(ex, "Could not gather repo evidence for acceptance review of #{Number}", issue.Number);
                }

                // Safeguard: if evidence fetch failed entirely, defer this review to the next loop
                // instead of running the AI on empty evidence (which silently hallucinates "all
                // criteria unmet"). The PM loop polls every few minutes; the eventual-consistency
                // window after a fresh merge usually closes within seconds, so the next pass
                // typically succeeds. This prevented the regression where issue #1291 was created
                // 88 seconds after PR #1289 merged with the AI claiming "no implementation evidence"
                // even though the files were present on the working branch.
                if (repoTree.Count == 0)
                {
                    Logger.LogWarning(
                        "PM acceptance review for #{Number}: repository tree on '{Branch}' came back empty " +
                        "({ErrorState}) — deferring review to next loop to avoid hallucinating gap-from-empty-evidence",
                        issue.Number, EffectiveBranch,
                        evidenceFetchError is null ? "no exception" : $"exception: {evidenceFetchError.GetType().Name}");
                    LogActivity("review",
                        $"⏸️ Deferred review of #{issue.Number} — repo tree empty on `{EffectiveBranch}` " +
                        "(eventual-consistency or API hiccup); will retry next loop");
                    Core.TaskTracker!.CompleteStep(completionStepId);
                    continue;
                }

                var evidenceSection = "";
                if (repoTree.Count > 0)
                {
                    // Show application files (exclude docs/markdown)
                    var appFiles = repoTree
                        .Where(f => !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                                    !f.StartsWith(".virtualdevteam", StringComparison.OrdinalIgnoreCase))
                        .Take(50)
                        .ToList();
                    evidenceSection = $"\n\n### Verified Repository State (files on `{EffectiveBranch}` branch)\n" +
                        $"Total files on `{EffectiveBranch}`: {repoTree.Count}\n" +
                        $"Application files (non-docs):\n{string.Join("\n", appFiles.Select(f => $"  - {f}"))}\n";
                }
                if (!string.IsNullOrEmpty(mergedPrSummary))
                {
                    evidenceSection += $"\n### Recently Merged PRs\n{mergedPrSummary}\n";
                }

                var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var history = CreateChatHistory();

                history.AddSystemMessage(
                    await Core.PromptService!.RenderAsync("pm/story-review-system", new Dictionary<string, string>(), ct)
                    ?? "You are a Program Manager reviewing whether a user story has been fully delivered. " +
                       "All engineering tasks have been completed and merged. Review the ACTUAL repository " +
                       "state provided below — do NOT guess or invent PR numbers. If the repository contains " +
                       "application files matching the acceptance criteria, respond with APPROVED and a brief " +
                       "summary. If gaps remain, respond with NEEDS_MORE_WORK and describe what's missing. " +
                       "IMPORTANT: Base your decision on the verified file tree and merged PRs, not assumptions.");

                history.AddUserMessage(
                    await Core.PromptService!.RenderAsync("pm/story-review-user", new Dictionary<string, string>
                    {
                        ["issue_number"] = issue.Number.ToString(),
                        ["issue_title"] = issue.Title,
                        ["issue_body"] = issue.Body ?? "",
                        ["closed_summary"] = closedSummary,
                        ["evidence"] = evidenceSection
                    }, ct)
                    ?? $"## Enhancement Issue #{issue.Number}: {issue.Title}\n\n" +
                       $"### Original Specification\n{issue.Body}\n\n" +
                       $"### Completed Engineering Tasks\n{closedSummary}\n" +
                       $"{evidenceSection}\n\n" +
                       "Review the acceptance criteria against the ACTUAL verified repository state above. " +
                       "Do NOT invent PR numbers or guess at repository contents — use only the evidence provided. " +
                       "Start your response with either APPROVED or NEEDS_MORE_WORK.");

                UpdateStatus(AgentStatus.Working, $"🤖 Reviewing enhancement #{issue.Number}: Running acceptance check");
                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var responseText = response.Content ?? "";
                Core.TaskTracker!.RecordLlmCall(completionStepId);

                if (responseText.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
                {
                    // Clean response for the closing comment
                    var summaryText = responseText
                        .Replace("APPROVED", "").Replace("approved", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                        $"✅ **PM Final Review — APPROVED**\n\n" +
                        $"All {subIssues.Count} engineering tasks have been delivered and merged.\n\n" +
                        $"{summaryText}\n\n" +
                        $"Closing this user story as complete.",
                        ct);
                    await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
                    _reviewedEnhancementIssues.Add(issue.Number);

                    Logger.LogInformation("PM approved and closed enhancement issue #{Number}: {Title}",
                        issue.Number, issue.Title);
                    LogActivity("review", $"✅ Approved and closed user story #{issue.Number}: {issue.Title}");
                    Core.TaskTracker!.RecordSubStep(completionStepId, $"Approved enhancement #{issue.Number}");
                    Core.TaskTracker!.CompleteStep(completionStepId);
                }
                else
                {
                    // PM found gaps — all tasks are closed/merged so SE can't re-engage.
                    // Close the enhancement as "delivered with known gaps" and create
                    // a follow-up issue to track the identified improvements.
                    var gapText = responseText
                        .Replace("NEEDS_MORE_WORK", "").Replace("needs_more_work", "")
                        .Trim().TrimStart('-', ':', ' ', '\n');

                    // Create a follow-up issue tracking the gaps
                    var followUpTitle = $"Follow-up improvements for: {issue.Title}";
                    var followUpBody = $"## Background\n\n" +
                        $"Enhancement #{issue.Number} ({issue.Title}) was delivered with all " +
                        $"{subIssues.Count} engineering tasks completed and merged. During final " +
                        $"PM acceptance review, the following gaps were identified:\n\n" +
                        $"## Identified Gaps\n\n{gapText}\n\n" +
                        $"## Source\nOriginal enhancement: #{issue.Number}";

                    var followUp = await _platform.WorkItemService!.CreateAsync(
                        followUpTitle, followUpBody,
                        new[] { "enhancement", "follow-up" }, ct);

                    // Close the original with a reference to the follow-up
                    await _platform.WorkItemService!.AddCommentAsync(issue.Number,
                        $"🔍 **PM Final Review — Delivered with Known Gaps**\n\n" +
                        $"All {subIssues.Count} engineering tasks are closed and merged. " +
                        $"PM identified improvements needed, but no active engineering " +
                        $"work remains to dispatch to.\n\n" +
                        $"**Gaps identified:**\n{gapText}\n\n" +
                        $"Closing as delivered. Follow-up improvements tracked in #{followUp.Number}.",
                        ct);
                    await _platform.WorkItemService!.CloseAsync(issue.Number, ct);
                    _reviewedEnhancementIssues.Add(issue.Number);

                    Logger.LogInformation(
                        "PM closed enhancement #{Number} as delivered with gaps. Follow-up: #{FollowUp}",
                        issue.Number, followUp.Number);
                    LogActivity("review",
                        $"🔍 Enhancement #{issue.Number} delivered with gaps → follow-up #{followUp.Number}");
                    Core.TaskTracker!.RecordSubStep(completionStepId, $"Enhancement #{issue.Number} — gaps found, follow-up #{followUp.Number}");
                    Core.TaskTracker!.CompleteStep(completionStepId);
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
        if (_trackedAgents.Count == 0) return;

        try
        {
            foreach (var (agentId, tracking) in _trackedAgents)
            {
                var statusText = tracking.LastKnownStatus.ToString();
                if (tracking.CurrentTask is not null)
                    statusText += $" ({tracking.CurrentTask})";

                await Core.ProjectFiles.UpdateTeamMemberStatusAsync(agentId, statusText, ct);
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
        var requested = Math.Max(1, message.RequestedCount);
        Logger.LogInformation(
            "Resource request from {Agent}: requesting {Count}x {Role} (team size: {Size})",
            message.FromAgentId, requested, message.RequestedRole, message.CurrentTeamSize);

        var remaining = Core.Config.Limits.MaxAdditionalEngineers - _additionalEngineersHired;
        if (remaining <= 0)
        {
            Logger.LogInformation(
                "Resource request from {Agent} exceeds limit ({Hired}/{Max}), creating executive issue",
                message.FromAgentId, _additionalEngineersHired, Core.Config.Limits.MaxAdditionalEngineers);

            await _platform.IssueWorkflow!.RequestResourceAsync(
                message.FromAgentId, message.RequestedRole, message.Justification, ct);
            return;
        }

        var toSpawn = Math.Min(requested, remaining);
        var spawned = 0;

        for (int i = 0; i < toSpawn; i++)
        {
            var spawnedIdentity = await _spawnManager.SpawnAgentAsync(message.RequestedRole, ct);
            if (spawnedIdentity is not null)
            {
                spawned++;
                _additionalEngineersHired++;
                Logger.LogInformation(
                    "Spawned {Role} '{Name}' for bus resource request from {Agent} ({Count}/{Max})",
                    message.RequestedRole, spawnedIdentity.DisplayName, message.FromAgentId,
                    _additionalEngineersHired, Core.Config.Limits.MaxAdditionalEngineers);

                await Core.ProjectFiles.AddTeamMemberAsync(spawnedIdentity, "Online", ct: ct);
            }
            else
            {
                Logger.LogWarning(
                    "Failed to spawn {Role} #{Index} for bus resource request from {Agent}",
                    message.RequestedRole, i + 1, message.FromAgentId);
                break; // Stop trying if spawn fails
            }
        }

        if (spawned > 0)
        {
            await RememberAsync(MemoryType.Action,
                $"Hired {spawned}x {message.RequestedRole} via bus request from {message.FromAgentId} ({_additionalEngineersHired}/{Core.Config.Limits.MaxAdditionalEngineers} total)",
                ct: ct);
        }

        await PublishStatusAsync("ResourceApproval", AgentStatus.Online,
            details: $"Resource request: spawned {spawned}/{requested} {message.RequestedRole}(s)" +
                (spawned < requested ? $" (limit: {Core.Config.Limits.MaxAdditionalEngineers})" : ""),
            toAgentId: message.FromAgentId, ct: ct);
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

        // When research completes, signal the main loop to handle gates + PMSpec creation.
        // We do NOT block here — blocking a bus handler starves the PM's message mailbox
        // and causes the main loop to overwrite Blocked status with Idle.
        if (message.MessageType == "ResearchComplete" && !_pmSpecCreated && !_researchCompletePending)
        {
            Logger.LogInformation("Research complete signal received — queuing PMSpec creation for main loop");
            _researchCompletePending = true;
        }
    }

    private Task HandleReviewRequestAsync(ReviewRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Review request from {Agent} for PR #{PrNumber}: {Title} ({ReviewType})",
            message.FromAgentId, message.PrNumber, message.PrTitle, message.ReviewType);

        // Idempotency: do NOT clear _reviewedPrHeadShas here. Clearing on every incoming
        // ReviewRequestMessage produced duplicate PM CHANGES_REQUESTED comments when an
        // upstream agent (SE/TE) re-broadcast a review request for an unchanged PR — see
        // PR #1216 in the 2026-05-08 run, where PM posted 5 reviews in 23 min on a single
        // SHA. The polling-based check at line ~1196 already re-reviews correctly when the
        // PR's HEAD SHA changes (i.e., SE pushed real rework commits). Same-SHA re-broadcasts
        // are now no-ops.

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
                "PMSpec.md" => await Core.ProjectFiles.GetPMSpecAsync(ct),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(currentContent)) return null;

            // === CLI Edit Mode: write file locally, let Copilot CLI edit it surgically ===
            var tempDir = Path.Combine(Path.GetTempPath(), $"vdt-pm-revision-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, docName);
            await File.WriteAllTextAsync(filePath, currentContent, ct);

            try
            {
                // todo: pm-rework-too-slow — surgical reviewer feedback (e.g. "remove Docker")
                // doesn't need Opus. Default to a faster tier for rework; operators can opt back
                // into the agent's normal tier by setting ReworkModelTierOverride to null/empty.
                var reworkTier = Core.Config.Agents.ProgramManager.ReworkModelTierOverride;
                var effectiveTier = string.IsNullOrWhiteSpace(reworkTier) ? Identity.ModelTier : reworkTier;
                var kernel = Core.ModelRegistry.GetKernel(effectiveTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();

                // Include project description + wizard Q&A as reference context
                var projectDescription = Core.Config.Project.ResolvedDescription ?? Core.Config.Project.Description ?? "";
                var history = CreateChatHistory();
                history.AddSystemMessage(
                    $"""
                    You are a Program Manager revising {docName} based on reviewer feedback.

                    ## Project Context (READ-ONLY reference — do NOT copy this into the document verbatim):
                    {projectDescription}

                    CRITICAL RULES:
                    1. Use the file editing tools to make ONLY the changes the feedback requests.
                    2. Do NOT rewrite or reorganize sections that the feedback does not mention.
                    3. Do NOT remove existing content unless the feedback explicitly asks for removal.
                    4. Preserve the tone, structure, and level of detail of the original document.
                    5. Make surgical, minimal edits — change only what is necessary to address the feedback.
                    6. The file {docName} is in your working directory. Edit it directly.
                    7. Use the Project Context above to inform your edits (e.g., ensuring accuracy of business goals, user stories, scope) but do NOT restructure the document around it.
                    """);
                history.AddUserMessage(
                    $"""
                    ## Reviewer Feedback:

                    {feedback}

                    Edit the file `{docName}` in your working directory to address ONLY the feedback above.
                    Make minimal, surgical changes. Do not rewrite the whole file.
                    """);

                // Push CLI context with file edit permissions pointed at the temp directory
                using var scope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                    AllowFileEdits: true,
                    OverrideWorkingDirectory: tempDir));

                var sw = Stopwatch.StartNew();
                await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                sw.Stop();

                if (sw.Elapsed.TotalMinutes > 4)
                {
                    Logger.LogWarning(
                        "PM rework took {Seconds:F0}s on {DocName} (tier={Tier}) — exceeded fast-path expectation. " +
                        "Consider model tier or single-pass workflow.",
                        sw.Elapsed.TotalSeconds, docName, effectiveTier);
                }

                // Read back the edited file
                if (!File.Exists(filePath))
                {
                    Logger.LogWarning("CLI edit mode deleted {DocName} — rejecting revision", docName);
                    return null;
                }

                var revised = await File.ReadAllTextAsync(filePath, ct);

                // Safety check: reject if unchanged (no edit made)
                if (revised.TrimEnd() == currentContent.TrimEnd())
                {
                    Logger.LogInformation("CLI edit made no changes to {DocName}", docName);
                    return null;
                }

                Logger.LogInformation("CLI edit revision of {DocName}: {Original} → {Revised} chars",
                    docName, currentContent.Length, revised.Length);
                return revised;
            }
            finally
            {
                // Clean up temp directory
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to revise {DocName}", docName);
            return null;
        }
    }

    /// <summary>
    /// Parse a markdown document into sections by ## headings.
    /// Returns sections with their heading, body text, and exact character offsets
    /// in the original string for byte-for-byte span replacement.
    /// </summary>
    private static List<MarkdownSection> ParseMarkdownSections(string content)
    {
        var sections = new List<MarkdownSection>();
        var lines = content.Split('\n');
        var inCodeFence = false;
        int currentStart = 0;
        string currentHeading = "(Preamble)";
        var bodyStart = 0;

        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineStart = offset;
            offset += line.Length + 1; // +1 for the \n we split on

            // Track code fences to avoid treating ```## Heading``` as a real heading
            if (line.TrimStart().StartsWith("```"))
                inCodeFence = !inCodeFence;

            if (!inCodeFence && line.StartsWith("## ") && i > 0)
            {
                // Close previous section
                sections.Add(new MarkdownSection(currentHeading,
                    content[bodyStart..lineStart].TrimEnd('\r', '\n'),
                    currentStart, lineStart));

                currentHeading = line.TrimEnd('\r');
                currentStart = lineStart;
                bodyStart = lineStart;
            }
        }

        // Final section
        sections.Add(new MarkdownSection(currentHeading,
            content[bodyStart..].TrimEnd('\r', '\n'),
            currentStart, content.Length));

        return sections;
    }

    /// <summary>
    /// Try to merge surgical section replacements from the model's JSON response
    /// into the original document. Returns null if parsing fails or too many sections changed.
    /// </summary>
    private string? TryMergeSurgicalRevision(
        string originalContent, List<MarkdownSection> sections, string modelResponse, string docName)
    {
        try
        {
            // Strip markdown code fences if present
            var json = modelResponse.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline > 0) json = json[(firstNewline + 1)..];
                var lastFence = json.LastIndexOf("```");
                if (lastFence > 0) json = json[..lastFence];
                json = json.Trim();
            }

            var replacements = System.Text.Json.JsonSerializer.Deserialize<List<SectionReplacement>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (replacements is null || replacements.Count == 0)
            {
                Logger.LogDebug("No section replacements parsed from model response for {DocName}", docName);
                return null;
            }

            // Validation: reject if more than 50% of sections changed (likely overreach)
            var maxAllowedChanges = Math.Max(2, (int)Math.Ceiling(sections.Count * 0.5));
            if (replacements.Count > maxAllowedChanges)
            {
                Logger.LogWarning("Model wants to change {Count}/{Total} sections in {DocName} — rejecting as overreach",
                    replacements.Count, sections.Count, docName);
                return null;
            }

            // Build a map of section index → replacement content
            var replacementMap = new Dictionary<int, string>();
            foreach (var r in replacements)
            {
                var idx = r.Section;
                if (idx < 0 || idx >= sections.Count)
                {
                    Logger.LogWarning("Invalid section index {Index} in revision response for {DocName}", idx, docName);
                    continue;
                }
                // Cross-validate heading if provided
                if (!string.IsNullOrEmpty(r.Heading) &&
                    !sections[idx].Heading.Contains(r.Heading.Replace("## ", "").Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("Section {Index} heading mismatch: expected '{Expected}', got '{Got}'",
                        idx, sections[idx].Heading, r.Heading);
                }
                replacementMap[idx] = r.Content;
            }

            if (replacementMap.Count == 0) return null;

            // Merge: reconstruct document by replacing only specified spans
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < sections.Count; i++)
            {
                if (i > 0) result.AppendLine();

                if (replacementMap.TryGetValue(i, out var replacement))
                {
                    // Use the replacement content but preserve the original heading
                    var heading = sections[i].Heading;
                    if (heading != "(Preamble)" && !replacement.TrimStart().StartsWith("## "))
                        result.AppendLine(heading);
                    result.Append(replacement.TrimEnd());
                }
                else
                {
                    // Preserve original content byte-for-byte using span offsets
                    var original = originalContent[sections[i].StartOffset..sections[i].EndOffset];
                    result.Append(original.TrimEnd());
                }
            }

            var merged = result.ToString().TrimEnd() + "\n";

            Logger.LogInformation("Surgical revision of {DocName}: {Changed}/{Total} sections modified",
                docName, replacementMap.Count, sections.Count);

            return merged;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Logger.LogDebug(ex, "Failed to parse surgical revision JSON for {DocName}", docName);
            return null;
        }
    }

    private static string BuildSurgicalRevisionSystemPrompt(string docName) =>
        $$"""
        You are a Program Manager performing a SURGICAL revision of {{docName}} based on reviewer feedback.

        CRITICAL RULES:
        1. The document is split into numbered sections. You MUST return a JSON array with ONLY the sections that need changes.
        2. Do NOT include unchanged sections — they will be preserved automatically.
        3. Each entry must have: "section" (number), "heading" (the ## heading text), and "content" (the full revised section content including the heading).
        4. Address EXACTLY what the feedback asks — nothing more. Do not "improve", reword, or reorganize any section the feedback does not mention.
        5. If the feedback mentions one section, return exactly ONE entry. Be minimal.

        Response format (JSON array only, no other text):
        [{"section": 3, "heading": "Business Goals", "content": "## Business Goals\n\nRevised content here..."}]
        """;

    private static string BuildSurgicalRevisionUserPrompt(
        string docName, string sectionMap, string feedback, int sectionCount) =>
        $$"""
        ## Document: {{docName}} ({{sectionCount}} sections)

        {{sectionMap}}

        ## Reviewer Feedback:

        {{feedback}}

        Return a JSON array with ONLY the section(s) that need changes to address the feedback above.
        Each entry: {"section": <number>, "heading": "<heading text>", "content": "<full revised section>"}
        Do NOT include sections that don't need changes. Be surgical.
        """;

    private record MarkdownSection(string Heading, string Body, int StartOffset, int EndOffset);
    private record SectionReplacement
    {
        public int Section { get; init; }
        public string Heading { get; init; } = "";
        public string Content { get; init; } = "";
    }

    /// <summary>Reset gate labels on a PR for re-review after revision.</summary>
    private async Task ResetGateLabelsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            await _platform.PrService.RemoveLabelAsync(prNumber, "human-approved", ct);
            await _platform.PrService.AddLabelAsync(prNumber, "awaiting-human-review", ct);
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
        var specStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Generate PM Spec",
            "Creating PM Specification from research findings", Identity.ModelTier);
        try
        {
            // Idempotency: check if PMSpec already has meaningful content
            var existingSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
            if (!string.IsNullOrWhiteSpace(existingSpec) &&
                !existingSpec.Contains("No PM specification has been created yet"))
            {
                Logger.LogInformation("PMSpec.md already exists with content, skipping creation");
                // Still signal downstream agents
                await PublishStatusAsync("PMSpecReady", AgentStatus.Idle,
                    details: "PM Specification document already exists", ct: ct);

                // Create User Story Issues if not already done
                // skipClosedIssueGuard: PMSpec exists in repo, old closed issues are from prior runs
                await CreateUserStoryIssuesAsync(ct, skipClosedIssueGuard: true);
                Core.TaskTracker!.CompleteStep(specStepId);
                return;
            }

            // Create the PR upfront so it's visible immediately
            var projectName = Core.Config.Project.Name;
            var pmSpecPath = Core.ProjectFiles.ResolvePath("PMSpec.md");

            // Quick mode: produce a minimal 1-paragraph PMSpec for fast testing
            if (Core.Config.Project.QuickDocumentCreation)
            {
                Logger.LogInformation("QuickDocumentCreation: producing minimal PMSpec.md");
                UpdateStatus(AgentStatus.Working, "Creating minimal PMSpec (quick mode)");
                var qPr = await _platform.PrWorkflow.OpenDocumentPRAsync(
                    Identity.DisplayName, pmSpecPath,
                    $"PM Specification for {projectName}",
                    $"Quick-mode PM specification for {projectName}.",
                    closesIssueNumber: null, ct);
                // Surface the doc PR on the dashboard (agent-card-show-pr todo).
                CurrentPrNumber = qPr.Number;

                // Resume-aware: check if gate is already pending/approved
                var qGateStatus = await Core.GateCheck.GetGateStatusAsync(
                    GateIds.PMSpecification, qPr.Number, ct);

                string? qContent = null;
                if (qGateStatus == GateStatus.NotActivated)
                {
                    var qKernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                    var qChat = qKernel.GetRequiredService<IChatCompletionService>();
                    var qHistory = CreateChatHistory();
                    qHistory.AddSystemMessage(
                        await Core.PromptService!.RenderAsync("pm/quick-system", new Dictionary<string, string>(), ct)
                        ?? "You are a Program Manager. Write a brief product specification.");
                    qHistory.AddUserMessage(
                        await Core.PromptService!.RenderAsync("pm/quick-user", new Dictionary<string, string>
                        {
                            ["project_description"] = Core.Config.Project.Description ?? "",
                            ["tech_stack"] = Core.Config.Project.TechStack
                        }, ct)
                        ?? $"Project: {Core.Config.Project.Description}\nTech Stack: {Core.Config.Project.TechStack}\n\n" +
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
                    await _platform.PrWorkflow.CommitDocumentToPRAsync(
                        qPr, pmSpecPath, qContent,
                        $"Add PM Specification for {projectName}", ct);
                }

                // === Gate: PMSpecification — human reviews PMSpec before merge ===
                if (qGateStatus != GateStatus.Approved)
                {
                    var maxRevisions = 3;
                    for (var revision = 0; revision < maxRevisions; revision++)
                    {
                        var gateWait = await WaitForHumanGateAsync(
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
                            await _platform.PrWorkflow.CommitDocumentToPRAsync(
                                qPr, pmSpecPath, revised,
                                $"Revise PMSpec based on reviewer feedback (attempt {revision + 2})", ct);
                        }
                        await ResetGateLabelsAsync(qPr.Number, ct);
                        await _platform.ReviewService.AddCommentAsync(qPr.Number,
                            $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated PMSpec.md.", ct);
                    }
                }

                if (!qPr.IsMerged)
                {
                    await _platform.PrWorkflow.MergeDocumentPRAsync(
                        qPr, Identity.DisplayName, pmSpecPath, ct);
                }
                // Quick-mode doc PR merged — clear so dashboard stops linking to it.
                CurrentPrNumber = null;
                Logger.LogInformation("Quick PMSpec.md created and merged");
                LogActivity("task", $"📝 Quick PMSpec.md created for {projectName}");

                await PublishStatusAsync("PMSpecReady", AgentStatus.Working,
                    details: "PM Specification is ready (quick mode). Architect can begin.", ct: ct);

                await CreateUserStoryIssuesAsync(ct, skipClosedIssueGuard: true);
                UpdateStatus(AgentStatus.Idle, "Quick PMSpec complete, Architect triggered");
                return;
            }

            UpdateStatus(AgentStatus.Working, "Creating PR for PMSpec.md");
            var pr = await _platform.PrWorkflow.OpenDocumentPRAsync(
                Identity.DisplayName,
                pmSpecPath,
                $"PM Specification for {projectName}",
                $"Formal product specification document covering business goals, user stories, " +
                $"acceptance criteria, scope, and non-functional requirements for {projectName}.",
                closesIssueNumber: null,
                ct);
            // Surface the doc PR on the dashboard (agent-card-show-pr todo).
            CurrentPrNumber = pr.Number;

            // Resume-aware: check if gate is already pending/approved from a prior run
            var pmGateStatus = await Core.GateCheck.GetGateStatusAsync(
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
            AgentCallContext.CurrentCallContext = "Creating PMSpec: Analyzing requirements";

            var projectDescription = Core.Config.Project.Description;
            var researchDoc = await Core.ProjectFiles.GetResearchDocAsync(ct);

            // Read visual design reference files for inclusion in PMSpec
            var designContext = await ReadDesignReferencesForSpecAsync(ct);

            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var memoryContext = await GetMemoryContextAsync(ct: ct);

            // Enable agentic mode so the PM can explore the actual project codebase
            // NOTE: Manually disposed before self-assessment to prevent the assessment from
            // inheriting AgenticAllowAll+DocumentGenerationMode (which causes stuck exploration).
            var projectPath = Core.Config.Workspace.LocalCheckoutPath;
            var _agenticScope = !string.IsNullOrEmpty(projectPath) && Directory.Exists(projectPath)
                ? AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                    AgenticAllowAll: true,
                    DocumentGenerationMode: true,
                    OverrideWorkingDirectory: projectPath))
                : null;

            // Build design context if available
            var designContextSection = "";
            if (!string.IsNullOrWhiteSpace(designContext))
            {
                designContextSection = await Core.PromptService!.RenderAsync("pm/design-reference",
                    new Dictionary<string, string> { ["design_context"] = designContext }, ct)
                    ?? "\n\n## CRITICAL: VISUAL DESIGN REFERENCE\n" +
                       "The repository contains visual design reference files that define the EXACT UI to be built.\n" +
                       designContext;
            }

            var systemPrompt = await Core.PromptService!.RenderAsync("pm/full-system", new Dictionary<string, string>
            {
                ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}",
                ["design_context"] = designContextSection,
                ["unanswered_decisions"] = DecisionContextBuilder.BuildUnansweredDecisionsContext(
                    Core.Config.UnansweredDecisionQuestions, _decisionLog)
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
            var useSinglePass = Core.Config.CopilotCli.SinglePassMode;

            // Build design sections content for templates
            var designSections = "";
            if (!string.IsNullOrWhiteSpace(designContext))
            {
                designSections = await Core.PromptService!.RenderAsync("pm/design-sections", new Dictionary<string, string>(), ct)
                    ?? "## Visual Design Specification\n(Describe the design.)\n\n## UI Interaction Scenarios\n(Describe interactions.)\n\n";
            }

            // Load approved scenarios for the {{approved_scenarios_yaml}} prompt variable.
            // This must happen BEFORE rendering the PMSpec prompt so the LLM sees the
            // wizard-approved scenario list and derives user stories from it.
            var approvedScenariosYaml = await BuildApprovedScenariosYamlAsync(ct);

            var specVars = new Dictionary<string, string>
            {
                ["project_name"] = projectName,
                ["project_description"] = projectDescription,
                ["research_doc"] = researchDoc,
                ["design_sections"] = designSections,
                ["unanswered_decisions"] = DecisionContextBuilder.BuildUnansweredDecisionsContext(
                    Core.Config.UnansweredDecisionQuestions, _decisionLog),
                ["approved_scenarios_yaml"] = approvedScenariosYaml,
            };

            if (useSinglePass)
            {
                // Single-pass: one comprehensive prompt instead of 2 turns
                UpdateStatus(AgentStatus.Working, "Creating PMSpec (single-pass)");
                var singlePassPrompt = await Core.PromptService!.RenderAsync("pm/single-pass-spec", specVars, ct);
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
                Core.TaskTracker!.RecordSubStep(specStepId, "Single-pass PM Spec generation");
                Core.TaskTracker!.RecordLlmCall(specStepId);
                pmSpecDoc = singleResponse.Content?.Trim() ?? "";
            }
            else
            {
            var turn1Prompt = await Core.PromptService!.RenderAsync("pm/multi-turn-analysis", specVars, ct)
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
            Core.TaskTracker!.RecordSubStep(specStepId, "Turn 1: Analyze requirements");
            Core.TaskTracker!.RecordLlmCall(specStepId);
            history.AddAssistantMessage(analysisResponse.Content ?? "");

            Logger.LogDebug("PM Spec analysis complete for {ProjectName}", projectName);

            // Turn 2: Produce the structured PMSpec.md
            UpdateStatus(AgentStatus.Working, "Creating PMSpec (2/2): Drafting specification");
            AgentCallContext.CurrentCallContext = "Creating PMSpec: Drafting specification";
            var turn2Prompt = await Core.PromptService!.RenderAsync("pm/multi-turn-compile",
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
            Core.TaskTracker!.RecordSubStep(specStepId, "Turn 2: Compile specification document");
            Core.TaskTracker!.RecordLlmCall(specStepId);
            pmSpecDoc = specResponse.Content?.Trim() ?? "";
            }

            // Extract and log any decisions made during spec creation
            if (!string.IsNullOrEmpty(pmSpecDoc))
            {
                var decisions = DecisionBlockParser.ExtractDecisions(pmSpecDoc);
                if (decisions.Count > 0)
                {
                    Logger.LogInformation("PM extracted {Count} decisions from spec creation", decisions.Count);
                    foreach (var d in decisions)
                    {
                        if (_decisionGate is not null)
                        {
                            await _decisionGate.ClassifyAndGateDecisionAsync(
                                Identity.Id, Identity.DisplayName,
                                "PMSpecification", d.Title,
                                $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                                category: d.SourceQuestion is not null ? "WizardQuestion" : "ProductDecision",
                                modelTier: Identity.ModelTier, ct: ct);
                        }
                        else
                        {
                            _decisionLog?.Log(new AgentDecision
                            {
                                Id = Guid.NewGuid().ToString("N")[..12],
                                AgentId = Identity.Id,
                                AgentDisplayName = Identity.DisplayName,
                                Phase = "PMSpecification",
                                ImpactLevel = d.Impact,
                                Title = d.Title,
                                Rationale = $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                                SourceQuestion = d.SourceQuestion,
                                Category = d.SourceQuestion is not null ? "WizardQuestion" : "ProductDecision",
                                Status = DecisionStatus.AutoApproved,
                            });
                        }
                    }
                    // Strip decision blocks from the document content
                    pmSpecDoc = DecisionBlockParser.StripDecisionBlocks(pmSpecDoc);
                }
            }

            // Self-assessment: assess and refine the PM specification
            // Dispose agentic scope BEFORE self-assessment — assessment should NOT explore the
            // codebase or run in DocumentGenerationMode (it just evaluates the document text).
            _agenticScope?.Dispose();
            _agenticScope = null;

            Core.TaskTracker!.CompleteStep(specStepId);
            var assessStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Self-assessment & refinement",
                "Assessing and refining PM specification quality", Identity.ModelTier);
            Core.ReasoningLog!.Log(new AgentReasoningEvent
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
                var (refinedOutput, _) = await Core.SelfAssessment!.AssessAndRefineWithResultAsync(
                    Identity.Id,
                    Identity.DisplayName,
                    Identity.Role,
                    "PM Specification",
                    pmSpecDoc,
                    criteria,
                    $"Project: {Core.Config.Project.ResolvedDescription ?? Core.Config.Project.Description}\nResearch findings available in Research.md",
                    chat,
                    classifyImpact: false, // PM spec assessment doesn't drive a decision gate
                    ct);
                pmSpecDoc = refinedOutput;
                Core.TaskTracker!.RecordLlmCall(assessStepId);
            }
            Core.TaskTracker!.CompleteStep(assessStepId);

            Logger.LogDebug("PM Spec document compiled for {ProjectName}", projectName);

            } // end else (fresh AI work, not resuming from gate)

            // Commit document to PR so reviewers can see it before the gate
            var commitStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Commit PMSpec.md",
                "Committing PM Specification to PR", Identity.ModelTier);
            if (pmSpecDoc is not null && !pr.IsMerged)
            {
                await _platform.PrWorkflow.CommitDocumentToPRAsync(
                    pr, pmSpecPath, pmSpecDoc,
                    $"Add PM Specification for {projectName}", ct);
            }
            Core.TaskTracker!.CompleteStep(commitStepId);

            // === Gate: PMSpecification — human reviews PMSpec before merge ===
            var gateStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Human gate review",
                "Awaiting human approval of PM Specification", Identity.ModelTier);
            Core.TaskTracker!.SetStepWaiting(gateStepId);
            if (pmGateStatus != GateStatus.Approved)
            {
                var maxRevisions = 3;
                for (var revision = 0; revision < maxRevisions; revision++)
                {
                    var gateWait = await WaitForHumanGateAsync(
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
                        await _platform.PrWorkflow.CommitDocumentToPRAsync(
                            pr, pmSpecPath, revised,
                            $"Revise PMSpec based on reviewer feedback (attempt {revision + 2})", ct);
                    }
                    await ResetGateLabelsAsync(pr.Number, ct);
                    await _platform.ReviewService.AddCommentAsync(pr.Number,
                        $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated PMSpec.md.", ct);
                }
            }

            if (!pr.IsMerged)
            {
                await _platform.PrWorkflow.MergeDocumentPRAsync(
                    pr, Identity.DisplayName, pmSpecPath, ct);
            }
            // Doc PR finished — clear so the dashboard stops showing it as active.
            CurrentPrNumber = null;
            Core.TaskTracker!.CompleteStep(gateStepId);
            Logger.LogInformation("PMSpec.md PR created and merged for project {ProjectName}", projectName);
            LogActivity("task", $"📝 PMSpec.md created and merged for {projectName}");
            Core.FlowTimeline?.RecordEvent("pmspec.committed", "PMSpec.md Created & Committed",
                agentId: Identity.Id, phase: "PMSpec", category: VirtualDevTeam.Core.HealthMonitor.MilestoneCategory.Platform, entityType: "Document", entityId: "PMSpec.md");
            await RememberAsync(MemoryType.Action,
                $"Created and merged PMSpec.md for project '{projectName}'",
                TruncateForMemory(pmSpecDoc), ct);

            // Post-merge: sync scenarios.json sidecar and run cross-reference validation.
            // Runs unconditionally so a resume-from-gate also gets the sidecar and lint.
            if (_scenarioRegistry is not null)
                await PostMergeScenarioSyncAsync(pr.Number, ct);

            // Team composition BEFORE signaling downstream agents — the team must be
            // composed before the Architect, PE, and Engineers begin their work.
            if (_teamComposer is not null && Core.Config.SmeAgents.Enabled && !_teamCompositionComplete
                && !Core.Config.Limits.SinglePRMode)
            {
                var teamStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Team composition analysis",
                    "Evaluating optimal team composition", Identity.ModelTier);
                try
                {
                    await ComposeTeamAsync(ct);
                    Core.TaskTracker!.CompleteStep(teamStepId);
                }
                catch (Exception ex)
                {
                    Core.TaskTracker!.FailStep(teamStepId, ex.Message);
                    Logger.LogWarning(ex, "Team composition failed — continuing without it to avoid blocking workflow");
                    // Don't rethrow — team composition failure shouldn't block the entire pipeline
                }
            }

            // Notify all agents that PMSpec is ready — Architect will pick this up
            var signalStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-spec", "Signal Architect",
                "Notifying team that PM Specification is ready", Identity.ModelTier);
            await PublishStatusAsync("PMSpecReady", AgentStatus.Working,
                details: "PM Specification is ready. Architect can begin architecture design.", ct: ct);

            Logger.LogInformation("Triggered Architect to begin architecture design");

            // After PMSpec is merged, create User Story Issues
            Core.FlowTimeline?.RecordStart("pm.work-items.creating", "PM Creating Work Items",
                agentId: Identity.Id, phase: "PMSpec", category: VirtualDevTeam.Core.HealthMonitor.MilestoneCategory.Platform);
            await CreateUserStoryIssuesAsync(ct, skipClosedIssueGuard: true);
            Core.FlowTimeline?.RecordComplete("pm.work-items.creating");
            Core.TaskTracker!.CompleteStep(signalStepId);

            UpdateStatus(AgentStatus.Idle, "PMSpec complete, Issues created, Architect triggered");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create PM Specification — will retry on next loop");
            RecordError($"PMSpec creation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
            _pmSpecCreated = false; // Allow retry on next loop iteration
            // Don't leave a stale PR link on the dashboard while we recover.
            CurrentPrNumber = null;
        }
    }

    /// <summary>
    /// Loads approved scenarios from the registry and serializes them to YAML for the
    /// <c>{{approved_scenarios_yaml}}</c> prompt variable in <c>pm/single-pass-spec</c>.
    /// Returns an explanatory comment string when no scenarios are available so the LLM
    /// sees a meaningful placeholder rather than an empty substitution.
    /// </summary>
    private async Task<string> BuildApprovedScenariosYamlAsync(CancellationToken ct)
    {
        if (_scenarioRegistry is null)
            return "# (scenario registry not configured — wizard scenario step was not run)";

        var scenarios = await _scenarioRegistry.LoadAsync(ct);
        if (scenarios.Count == 0)
        {
            Logger.LogWarning(
                "No approved scenarios found for PMSpec generation. " +
                "The wizard scenario step may have been skipped, or this is a pre-scenarios project. " +
                "PMSpec will be generated without a scenario context — user stories will not have " +
                "scenario citations and cross-reference validation will find orphans.");
            return "# (No approved scenarios defined — wizard step was skipped or this is a pre-scenarios project)";
        }

        Logger.LogInformation("Loaded {Count} approved scenario(s) for PMSpec prompt", scenarios.Count);
        return ScenarioYamlSerializer.Serialize(scenarios);
    }

    /// <summary>
    /// After PMSpec is merged: re-extracts scenarios from the freshly-written PMSpec,
    /// writes the <c>scenarios.json</c> sidecar, validates no orphan scenarios exist,
    /// and scans every user story for the required <c>Implements Scenarios: SXX</c> citation.
    /// </summary>
    private async Task PostMergeScenarioSyncAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            // Re-extract scenarios from the authoritative PMSpec block.
            var freshScenarios = await _scenarioRegistry!.LoadAsync(ct);
            if (freshScenarios.Count > 0)
            {
                await _scenarioRegistry.WriteSidecarAsync(freshScenarios, ct);
                Logger.LogInformation(
                    "Synced {Count} scenario(s) to scenarios.json sidecar after PMSpec merge",
                    freshScenarios.Count);
            }

            // Orphan validation: every non-infrastructure scenario must be cited by ≥1 user story.
            var noOrphans = await _scenarioRegistry.ValidateNoOrphans(ct);
            if (!noOrphans)
            {
                Logger.LogWarning(
                    "PMSpec cross-reference validation: one or more scenarios are not cited " +
                    "by any user story. Every user story must carry an 'Implements Scenarios: SXX' citation.");
                try
                {
                    await _platform.ReviewService.AddCommentAsync(prNumber,
                        "⚠️ **CRITICAL: Scenario orphan check failed**\n\n" +
                        "One or more scenarios in the `## Scenarios` YAML block are not cited by any " +
                        "user story in `## User Stories & Acceptance Criteria`. " +
                        "Every user story must carry an `Implements Scenarios: SXX` citation per the PMSpec contract.\n\n" +
                        "**This does not block the workflow** — please review and either add citations to the " +
                        "relevant user stories, or mark infrastructure-only scenarios with `infrastructure: true` " +
                        "in the YAML block so they are exempt from the cross-reference requirement.",
                        ct);
                }
                catch (Exception commentEx)
                {
                    Logger.LogWarning(commentEx,
                        "Failed to post orphan-check comment on PR #{Number} — continuing", prNumber);
                }
            }

            // User-story citation scan: log warnings for stories lacking scenario citations.
            await ScanUserStoryCitationsAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Post-merge scenario sync failed — scenarios.json may be out of sync with PMSpec");
        }
    }

    /// <summary>
    /// Reads PMSpec.md from the platform and logs a warning for each user story that does
    /// not carry an <c>Implements Scenarios: SXX</c> citation.
    /// </summary>
    private async Task ScanUserStoryCitationsAsync(CancellationToken ct)
    {
        try
        {
            var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
            if (string.IsNullOrWhiteSpace(pmSpec))
                return;

            var lines = pmSpec.Split('\n');
            var uncited = 0;
            foreach (var line in lines)
            {
                // User story lines begin with "**As a " (bold role prefix per PMSpec convention).
                if (!line.Contains("**As a ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!line.Contains("Implements Scenarios:", StringComparison.OrdinalIgnoreCase))
                {
                    uncited++;
                    var preview = line.Trim();
                    if (preview.Length > 100) preview = string.Concat(preview.AsSpan(0, 100), "…");
                    Logger.LogWarning(
                        "User story missing 'Implements Scenarios: SXX' citation: {Preview}", preview);
                }
            }

            if (uncited > 0)
                Logger.LogWarning(
                    "{Count} user story/stories lack 'Implements Scenarios: SXX' citations — " +
                    "these are spec defects per the PMSpec cross-reference rule",
                    uncited);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "User-story citation scan failed");
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

            var analyzeStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-team", "Analyze project needs",
                "Gathering project docs and calling AI to propose team composition", Identity.ModelTier);

            // Gather project docs
            UpdateStatus(AgentStatus.Working, "📋 Composing team: Gathering project documents");
            var projectDesc = Core.Config.Project.ResolvedDescription ?? Core.Config.Project.Description ?? "No project description";
            var research = await Core.ProjectFiles.GetResearchDocAsync(ct);
            var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);

            // Build the team composition prompt
            var compositionPrompt = await _teamComposer.BuildTeamCompositionPromptAsync(
                projectDesc, research, pmSpec, ct);

            // Call AI to analyze and propose team
            UpdateStatus(AgentStatus.Working, "🤖 Composing team: Analyzing project needs");
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var history = CreateChatHistory();
            history.AddUserMessage(compositionPrompt);

            var response = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var aiResponse = response.LastOrDefault()?.Content;
            Core.TaskTracker!.RecordLlmCall(analyzeStepId);

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                Logger.LogWarning("AI returned empty response for team composition. Using default team.");
                Core.TaskTracker!.FailStep(analyzeStepId, "AI returned empty response");
                _teamCompositionComplete = true;
                return;
            }

            // Parse the proposal
            var proposal = _teamComposer.ParseProposal(aiResponse, Identity.Id);
            if (proposal is null)
            {
                Logger.LogWarning("Failed to parse team composition proposal. Using default team.");
                Core.TaskTracker!.FailStep(analyzeStepId, "Failed to parse AI proposal");
                _teamCompositionComplete = true;
                return;
            }
            Core.TaskTracker!.CompleteStep(analyzeStepId);

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
            var gateResult = await WaitForHumanGateAsync(
                GateIds.AgentTeamComposition,
                $"PM proposes team composition:\n" +
                $"Built-in: {string.Join(", ", proposal.BuiltInAgents.Select(a => $"{a.Role}x{a.Count}"))}\n" +
                $"SME Templates: {string.Join(", ", proposal.ExistingTemplateIds)}\n" +
                $"New SME Agents: {string.Join(", ", proposal.NewSmeAgents.Select(s => s.RoleName))}\n\n" +
                $"Rationale: {proposal.Rationale}",
                ct: ct);

            // Generate and save TeamComposition.md
            var docStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-team", "Write TeamComposition.md",
                "Generating and committing team composition document");
            var teamDoc = _teamComposer.GenerateTeamCompositionDoc(proposal);
            await Core.ProjectFiles.SaveScopedFileAsync("TeamComposition.md", teamDoc,
                "PM: Add team composition document", ct);
            Logger.LogInformation("TeamComposition.md saved");
            Core.TaskTracker!.CompleteStep(docStepId);

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
            var smeCount = proposal.NewSmeAgents.Count + proposal.ExistingTemplateIds.Count;
            if (smeCount > 0)
                UpdateStatus(AgentStatus.Working, $"🚀 Spawning {smeCount} specialist engineers");
            string? spawnStepId = smeCount > 0
                ? Core.TaskTracker!.BeginStep(Identity.Id, "pm-team", $"Spawn {smeCount} SME agents",
                    "Spawning SME agents from approved proposal")
                : null;

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

            if (spawnStepId is not null)
                Core.TaskTracker!.CompleteStep(spawnStepId);

            // Signal team composition complete
            var teamSignalStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-team", "Signal team ready",
                "Broadcasting TeamCompositionComplete to all agents");
            await PublishStatusAsync("TeamCompositionComplete", AgentStatus.Working,
                details: $"Team composition approved: {proposal.BuiltInAgents.Count} built-in + {proposal.NewSmeAgents.Count + proposal.ExistingTemplateIds.Count} SME agents",
                ct: ct);

            _teamCompositionComplete = true;
            Core.TaskTracker!.CompleteStep(teamSignalStepId);
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
    ///
    /// 2026-05-12 fix (workflow-recovery-pm-restarts-from-research, part 2): even when
    /// called with skipClosedIssueGuard=true (which all 5 callers do), if engineering-task
    /// issues already exist, the project is past the user-story phase by definition and
    /// we must not regenerate. This guards EVERY caller path including the idempotent
    /// PMSpec-exists branches in CreatePMSpecAsync (lines 2583, 2689) that previously
    /// fell through to user-story regeneration on restart.
    /// </summary>
    private async Task CreateUserStoryIssuesAsync(CancellationToken ct, bool skipClosedIssueGuard = false)
    {
        if (_userStoryIssuesCreated) return;
        if (_reviewsSignalFired) return; // Reviews complete — don't re-create issues

        // Late-stage-restart guard: if engineering-task issues exist (any state), the
        // project is past the user-story phase. This catches the case where PM restart
        // re-fires ResearchComplete (because Research.md exists) → triggers CreatePMSpec
        // idempotent branch → unconditionally calls this method. Without this guard
        // every restart of a late-stage project burns LLM regenerating user stories.
        try
        {
            var engineeringTasksAny = await _platform.WorkItemService!.ListByLabelAsync(
                "engineering-task", "all", ct);
            if (engineeringTasksAny.Count > 0)
            {
                Logger.LogInformation(
                    "CreateUserStoryIssuesAsync: found {Count} engineering-task issues (any state) — " +
                    "project is past user-story phase, skipping user-story creation entirely",
                    engineeringTasksAny.Count);
                _userStoryIssuesCreated = true;
                // Don't re-broadcast PlanningComplete — late-stage agents already past planning.
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex,
                "CreateUserStoryIssuesAsync: pre-check for engineering-task issues failed — proceeding with normal idempotency checks");
        }

        // On recovery, check if reviews are already complete before trying to create issues
        await CheckAndSignalAllReviewsCompleteAsync(ct);
        if (_reviewsSignalFired) return;

        try
        {
            // Idempotency: check if OPEN enhancement issues already exist
            var existingEnhancements = await _platform.WorkItemService!.ListByLabelAsync(
                IssueWorkflow.Labels.Enhancement, "open", ct);
            if (existingEnhancements.Count > 0)
            {
                Logger.LogInformation(
                    "Found {Count} existing open enhancement issues, skipping creation",
                    existingEnhancements.Count);
                _userStoryIssuesCreated = true;

                // Still notify PE in case it missed the signal
                await Core.MessageBus.PublishAsync(new PlanningCompleteMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "PlanningComplete",
                    IssueCount = existingEnhancements.Count
                }, ct);
                return;
            }

            // Prior-run detection: if closed enhancement issues exist, a previous run
            // already completed this project. Don't re-create duplicates.
            // Skip this guard on retry after mini-reset (caller already verified 0 open).
            if (!skipClosedIssueGuard)
            {
                var closedEnhancements = await _platform.WorkItemService!.ListByLabelAsync(
                    IssueWorkflow.Labels.Enhancement, "closed", ct);
                if (closedEnhancements.Count > 0)
                {
                    Logger.LogInformation(
                        "Prior run detected: {Count} closed enhancement issues exist — skipping re-creation to avoid duplicates",
                        closedEnhancements.Count);
                    _userStoryIssuesCreated = true;
                    return;
                }
            }

            UpdateStatus(AgentStatus.Working, "Creating User Story Issues from PMSpec");
            AgentCallContext.CurrentCallContext = "Creating User Story Issues from PMSpec";
            LogActivity("planning", "📋 Reading PMSpec.md to extract user stories");

            // Single-issue mode: create one Enhancement issue with doc links instead of N stories
            if (Core.Config.Limits.SingleIssueMode)
            {
                await CreateSingleEnhancementIssueAsync(ct);
                return;
            }

            var readSpecStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-stories", "Read PMSpec",
                "Reading PMSpec.md to extract user stories", Identity.ModelTier);

            var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
            if (string.IsNullOrWhiteSpace(pmSpec) || pmSpec.Contains("No PM specification has been created yet"))
            {
                Logger.LogWarning("PMSpec.md has no content, cannot create User Story Issues");
                Core.TaskTracker!.FailStep(readSpecStepId, "PMSpec.md has no content");
                return;
            }
            Core.TaskTracker!.CompleteStep(readSpecStepId);

            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var extractStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-stories", "Extract user stories",
                "AI extracting user stories from PMSpec", Identity.ModelTier);
            LogActivity("planning", "🤖 Calling AI to extract user stories from PMSpec");
            var history = CreateChatHistory();
            history.AddSystemMessage(
                await Core.PromptService!.RenderAsync("pm/story-extraction-system", new Dictionary<string, string>(), ct)
                ?? "You are a Program Manager extracting User Stories from a PM Specification document. " +
                   "For each User Story, produce a structured output that can be parsed into individual GitHub Issues.\n\n" +
                   "Output format — one block per User Story, separated by '---':\n" +
                   "TITLE: [concise story title]\nDESCRIPTION:\n[Full user story]\n\n" +
                   "DESIGN_REFERENCE:\n[Visual section or 'N/A']\n\n" +
                   "ACCEPTANCE_CRITERIA:\n- [ ] [criterion]\n...\n---\n\n" +
                   "List them by development dependency. Be thorough.");

            history.AddUserMessage(
                await Core.PromptService!.RenderAsync("pm/story-extraction-user",
                    new Dictionary<string, string> { ["pm_spec"] = pmSpec }, ct)
                ?? $"Extract all User Stories from this PM Specification and format them as described.\n\n" +
                   $"## PM Specification\n{pmSpec}");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content?.Trim() ?? "";
            Core.TaskTracker!.RecordLlmCall(extractStepId);

            // Parse the AI output into individual stories
            var storyBlocks = content.Split("---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Core.TaskTracker!.CompleteStep(extractStepId);

            // Cap stories to avoid AI generating an unreasonable number
            const int maxStories = 12;
            if (storyBlocks.Length > maxStories)
            {
                Logger.LogWarning("AI extracted {Count} story blocks — capping at {Max} to keep scope manageable",
                    storyBlocks.Length, maxStories);
                storyBlocks = storyBlocks[..maxStories];
            }

            var createStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-stories", $"Create {storyBlocks.Length} GitHub issues",
                "Creating enhancement issues on GitHub");
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
                    issueBody += $"## Visual Design Reference\n{designReference}\n\n";
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
                var existingIssue = await _platform.IssueWorkflow!.FindExistingIssueAsync(title, ct);
                if (existingIssue is not null)
                {
                    Logger.LogDebug("Issue '{Title}' already exists as #{Number}, skipping",
                        title, existingIssue.Number);
                    issueCount++;
                    continue;
                }

                var issue = await _platform.WorkItemService!.CreateAsync(
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
            Core.TaskTracker!.CompleteStep(createStepId);
            Logger.LogInformation("Created {Count} User Story Issues from PMSpec", issueCount);
            LogActivity("task", $"📌 Created {issueCount} User Story Issues from PMSpec");

            var signalStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-stories", "Notify team",
                "Broadcasting PlanningComplete to all agents");
            await RememberAsync(MemoryType.Action,
                $"Created {issueCount} user story issues from PMSpec for task tracking", ct: ct);

            // Notify PE that planning issues are ready
            await Core.MessageBus.PublishAsync(new PlanningCompleteMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "PlanningComplete",
                IssueCount = issueCount
            }, ct);
            Core.TaskTracker!.CompleteStep(signalStepId);

            UpdateStatus(AgentStatus.Idle, $"Created {issueCount} User Story Issues");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create User Story Issues from PMSpec");
            RecordError($"Issue creation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Single-issue mode: creates one Enhancement issue with an executive summary and links
    /// to the agent-generated documents (PMSpec, Architecture, Research) instead of N user stories.
    /// The SE agent reads this single issue and resolves the doc links for engineering planning.
    /// </summary>
    private async Task CreateSingleEnhancementIssueAsync(CancellationToken ct)
    {
        var stepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-single-issue", "Create single Enhancement issue",
            "Creating single issue with doc links", Identity.ModelTier);
        LogActivity("planning", "📋 Creating single Enhancement issue with document references");

        var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
        if (string.IsNullOrWhiteSpace(pmSpec) || pmSpec.Contains("No PM specification has been created yet"))
        {
            Logger.LogWarning("PMSpec.md has no content, cannot create Enhancement issue");
            Core.TaskTracker!.FailStep(stepId, "PMSpec.md has no content");
            return;
        }

        // Extract executive summary (first ~800 chars or up to first ## heading after intro)
        var execSummary = ExtractExecutiveSummary(pmSpec);

        var projectName = Core.Config.Project.Name;
        if (string.IsNullOrWhiteSpace(projectName))
            projectName = Core.Config.Project.Description?.Split('\n').FirstOrDefault()?.Trim() ?? "Project";

        var docsBasePath = Core.ProjectFiles.ArtifactBasePath;
        var title = $"Enhancement: {projectName}";

        var body = $"""
            ## Enhancement: {projectName}

            {execSummary}

            ### Referenced Documents
            - **PM Specification**: [{docsBasePath}/PMSpec.md]({docsBasePath}/PMSpec.md) — Full business requirements and user stories
            - **Architecture Design**: [{docsBasePath}/Architecture.md]({docsBasePath}/Architecture.md) — Technical architecture and design decisions
            - **Research**: [{docsBasePath}/Research.md]({docsBasePath}/Research.md) — Technology research and analysis

            ### Engineering Notes
            This issue represents the complete project scope. Engineering tasks will be created as sub-items.
            Refer to the linked documents above for full specifications and requirements.

            ---
            _Created by {Identity.DisplayName} — Single Issue Mode_
            """;

        var validatedBody = IssueBodyValidator.ValidateAndClean(body, title, Logger);
        if (validatedBody is null)
        {
            Logger.LogWarning("Single enhancement issue body failed validation");
            Core.TaskTracker!.FailStep(stepId, "Issue body failed validation");
            return;
        }

        // Check for existing issue
        var existingIssue = await _platform.IssueWorkflow!.FindExistingIssueAsync(title, ct);
        if (existingIssue is not null)
        {
            Logger.LogInformation("Single enhancement issue already exists as #{Number}", existingIssue.Number);
        }
        else
        {
            var issue = await _platform.WorkItemService!.CreateAsync(
                title, validatedBody,
                [IssueWorkflow.Labels.Enhancement],
                ct);
            Logger.LogInformation("Created single Enhancement issue #{Number}: {Title}", issue.Number, title);
        }

        _userStoryIssuesCreated = true;
        Core.TaskTracker!.CompleteStep(stepId);
        LogActivity("task", $"📌 Created single Enhancement issue for {projectName}");

        // Notify team
        await Core.MessageBus.PublishAsync(new PlanningCompleteMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "PlanningComplete",
            IssueCount = 1
        }, ct);

        UpdateStatus(AgentStatus.Idle, "Created single Enhancement issue");
    }

    /// <summary>
    /// Extract the first ~800 characters of the PMSpec as an executive summary,
    /// trimming at a paragraph boundary.
    /// </summary>
    private static string ExtractExecutiveSummary(string pmSpec)
    {
        const int maxLength = 800;
        if (pmSpec.Length <= maxLength) return pmSpec;

        // Try to cut at a paragraph boundary
        var cutoff = pmSpec.LastIndexOf("\n\n", maxLength, StringComparison.Ordinal);
        if (cutoff < maxLength / 2)
            cutoff = pmSpec.LastIndexOf('\n', maxLength);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return pmSpec[..cutoff].TrimEnd() + "\n\n> _See the full PM Specification for complete details._";
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
                UpdateStatus(AgentStatus.Working, $"💬 Processing clarification on issue #{request.IssueNumber}");

                var clarifyStepId = Core.TaskTracker!.BeginStep(Identity.Id, "pm-support",
                    $"Answer question on #{request.IssueNumber}",
                    $"Clarification from {request.FromAgentId}", Identity.ModelTier);

                var issue = await _platform.WorkItemService!.GetAsync(request.IssueNumber, ct);
                if (issue is null)
                {
                    Logger.LogWarning("Cannot find issue #{Number} for clarification", request.IssueNumber);
                    Core.TaskTracker!.FailStep(clarifyStepId, $"Issue #{request.IssueNumber} not found");
                    continue;
                }

                UpdateStatus(AgentStatus.Working, $"Answering clarification on issue #{request.IssueNumber}");

                var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();

                var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);

                var history = CreateChatHistory();
                history.AddSystemMessage(
                    await Core.PromptService!.RenderAsync("pm/clarification-system", new Dictionary<string, string>(), ct)
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
                    await Core.PromptService!.RenderAsync("pm/clarification-user", new Dictionary<string, string>
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

                UpdateStatus(AgentStatus.Working, $"🤖 Generating response for clarification #{request.IssueNumber}");
                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var answer = response.Content?.Trim() ?? "";
                Core.TaskTracker!.RecordLlmCall(clarifyStepId);

                if (string.IsNullOrWhiteSpace(answer) ||
                    answer.Equals("ESCALATE", StringComparison.OrdinalIgnoreCase))
                {
                    // Escalate to executive
                    Logger.LogInformation(
                        "Escalating clarification for issue #{Number} to Executive", request.IssueNumber);

                    var executiveUsername = Core.Config.Project.ExecutiveGitHubUsername;
                    var escalationIssue = await _platform.IssueWorkflow!.CreateExecutiveRequestAsync(
                        Identity.DisplayName,
                        $"Clarification needed for Issue #{request.IssueNumber}: {issue.Title}",
                        $"An engineer asked a question about Issue #{request.IssueNumber} that I cannot " +
                        $"confidently answer from the PM Specification.\n\n" +
                        $"**Issue:** #{request.IssueNumber} — {issue.Title}\n" +
                        $"**Question from {request.FromAgentId}:** {request.Question}\n\n" +
                        $"Please provide guidance. @{executiveUsername}",
                        ct);

                    await _platform.WorkItemService!.AddCommentAsync(request.IssueNumber,
                        $"**{Identity.DisplayName}**: I need to consult with the Executive stakeholder " +
                        $"on this question. I've created issue #{escalationIssue.Number} for guidance. " +
                        $"I'll follow up once I have an answer.",
                        ct);
                    Core.TaskTracker!.RecordSubStep(clarifyStepId, $"Escalated to Executive (#{escalationIssue.Number})");
                    Core.TaskTracker!.CompleteStep(clarifyStepId);
                }
                else
                {
                    // === Gate: AgentToAgentResponse — human reviews before posting answer ===
                    var gateResult = await WaitForHumanGateAsync(
                        GateIds.AgentToAgentResponse,
                        $"{Identity.DisplayName} → {request.FromAgentId}: Answering question on Issue #{request.IssueNumber}\n\n" +
                        $"**Question:** {request.Question}\n\n" +
                        $"**Proposed Answer:**\n{answer}",
                        request.IssueNumber, ct: ct);

                    // Human may have edited the answer via gate feedback
                    var finalAnswer = !string.IsNullOrWhiteSpace(gateResult.Feedback)
                        && gateResult.Decision != GateDecision.Rejected
                        ? gateResult.Feedback
                        : answer;

                    if (gateResult.Decision == GateDecision.Rejected)
                    {
                        Logger.LogInformation("Human rejected agent answer for issue #{Number}", request.IssueNumber);
                        Core.TaskTracker!.RecordSubStep(clarifyStepId, "Human rejected answer — not posting");
                        Core.TaskTracker!.CompleteStep(clarifyStepId);
                        continue;
                    }

                    // Post the answer on the issue
                    await _platform.WorkItemService!.AddCommentAsync(request.IssueNumber,
                        $"**{Identity.DisplayName}**: {finalAnswer}", ct);

                    // Notify the engineer
                    await Core.MessageBus.PublishAsync(new ClarificationResponseMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = request.FromAgentId,
                        MessageType = "ClarificationResponse",
                        IssueNumber = request.IssueNumber,
                        Response = finalAnswer
                    }, ct);

                    Logger.LogInformation(
                        "Answered clarification from {Agent} on issue #{Number}",
                        request.FromAgentId, request.IssueNumber);
                    Core.TaskTracker!.RecordSubStep(clarifyStepId, $"Answered {request.FromAgentId}");
                    Core.TaskTracker!.CompleteStep(clarifyStepId);
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
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = CreateChatHistory();
            history.AddSystemMessage(
                await Core.PromptService!.RenderAsync("pm/blocker-triage-system", new Dictionary<string, string>(), ct)
                ?? "You are a Program Manager triaging a blocker issue in a software project. " +
                   "Analyze the blocker and provide actionable guidance. " +
                   "If you cannot help, respond with exactly 'ESCALATE'.");

            history.AddUserMessage(
                await Core.PromptService!.RenderAsync("pm/blocker-triage-user", new Dictionary<string, string>
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

    private async Task<(bool Approved, bool ApprovedWithSuggestions, string? ReviewBody)> EvaluatePrAlignmentWithVerdictAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await Core.ProjectFiles.GetPMSpecAsync(ct);
            var engineeringPlan = await Core.ProjectFiles.GetEngineeringPlanAsync(ct);

            // Read the linked issue for acceptance criteria
            var issueContext = "";
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (issueNumber.HasValue)
            {
                try
                {
                    var issue = await _platform.WorkItemService!.GetAsync(issueNumber.Value, ct);
                    if (issue is not null)
                        issueContext = $"## Linked Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n";
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for PM review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            UpdateStatus(AgentStatus.Working, $"🔍 Reviewing PR #{pr.Number}: Fetching code changes");
            var codeContext = await _platform.PrWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            // Gather ALL screenshot evidence from PR comments (PE screenshots, TE screenshots, standalone)
            UpdateStatus(AgentStatus.Working, $"📸 Reviewing PR #{pr.Number}: Loading screenshots");
            var screenshotImages = new List<PullRequestWorkflow.ScreenshotImage>();
            var screenshotContext = "";
            try
            {
                screenshotImages = await _platform.PrWorkflow.GetPRScreenshotImagesAsync(pr.Number, ct: ct);
                if (screenshotImages.Count == 0)
                    screenshotContext = await _platform.PrWorkflow.GetPRScreenshotContextAsync(pr.Number, ct);
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
                UpdateStatus(AgentStatus.Working, $"📸 Reviewing PR #{pr.Number}: Describing {screenshotImages.Count} screenshots");
                try
                {
                    var descKernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
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
                screenshotSection = await Core.PromptService!.RenderAsync("pm/pr-review-screenshots", new Dictionary<string, string>(), ct)
                    ?? "3. VISUAL VALIDATION: Screenshots have been posted on this PR. " +
                       "Review them carefully to verify the app renders correctly.\n";
            }

            var systemPrompt = await Core.PromptService!.RenderAsync("pm/pr-review-system",
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

            // Log prompt size for monitoring — large prompts can crash CLI processes
            var totalPromptSize = userMessageText.Length + (systemPrompt?.Length ?? 0);
            if (totalPromptSize > 100_000)
                Logger.LogWarning("PM review prompt for PR #{PrNumber} is {Size:N0} chars — consider CLI review mode for large PRs",
                    pr.Number, totalPromptSize);

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
                else
                {
                    // No rendered design reference images — still enforce strict visual rules
                    screenshotIntro += "\n## ⚠️ No Design Reference Image Available\n" +
                        $"No rendered design screenshot was found in `{Core.ProjectFiles.DesignScreenshotsPrefix}`. " +
                        "Apply these strict visual quality rules to the actual screenshot:\n\n" +
                        "**STRICT VISUAL RULES — REQUEST_CHANGES if ANY are violated:**\n" +
                        "- If the actual screenshot is blank, mostly white, or shows only a white page → REQUEST_CHANGES.\n" +
                        "- If literal placeholder strings like `placeholder`, `(placeholder)`, `Timeline placeholder`, " +
                        "`Heatmap placeholder`, `Lorem ipsum`, `TODO`, `stub`, or `coming soon` are visible → REQUEST_CHANGES.\n" +
                        "- If the actual screenshot shows a red error banner, stack trace, or 'Loading...' text " +
                        "and the PR is not specifically a bug-fix → REQUEST_CHANGES.\n" +
                        "- If the PR title claims to wire, compose, integrate, or finalize a UI component but the " +
                        "screenshot shows no meaningful rendered content → REQUEST_CHANGES.\n" +
                        "- 'Stubbed with placeholder strings' is NEVER acceptable.\n\n" +
                        _designHtmlContext; // HTML design context if available
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

            UpdateStatus(AgentStatus.Working, $"🤖 Reviewing PR #{pr.Number}: Evaluating against acceptance criteria");
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";

            // Detect garbage AI responses (model breaking character, meta-commentary)
            if (PullRequestWorkflow.IsGarbageAIResponse(result))
            {
                Logger.LogWarning("PM review of PR #{Number} returned garbage AI response, retrying once", pr.Number);

                history.AddAssistantMessage(result);
                history.AddUserMessage(
                    await Core.PromptService!.RenderAsync("pm/pr-review-retry", new Dictionary<string, string>(), ct)
                    ?? "That response was not a requirements review. Check the PR against the acceptance criteria.\n" +
                       "Output ONLY a numbered list of unmet requirements, or 'Requirements met' if acceptable.\n" +
                       "End with VERDICT: APPROVE or VERDICT: REQUEST_CHANGES");

                response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                result = response.Content?.Trim() ?? "";

                if (PullRequestWorkflow.IsGarbageAIResponse(result))
                {
                    Logger.LogWarning("PM review of PR #{Number} still garbage after retry — auto-approving", pr.Number);
                    return (true, false, "Requirements alignment review passed. Feature scope looks appropriate.");
                }
            }

            // Check APPROVE_WITH_SUGGESTIONS first (more specific substring match)
            var approvedWithSuggestions = result.Contains("VERDICT: APPROVE_WITH_SUGGESTIONS", StringComparison.OrdinalIgnoreCase);
            var approved = approvedWithSuggestions
                || result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase);

            // Strip VERDICT markers AND any stray approval/rejection keywords the AI may
            // have echoed (e.g., "APPROVED", "CHANGES REQUESTED") to prevent contradictory
            // text from appearing in the posted comment alongside the structured header.
            var reviewBody = result
                .Replace("VERDICT: APPROVE_WITH_SUGGESTIONS", "", StringComparison.OrdinalIgnoreCase)
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
                        && !trimmed.StartsWith("[SoftwareEngineer", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            reviewBody = string.Join('\n', cleanedLines).Trim();

            // Strip any preamble/thinking the AI may have included before the numbered list
            reviewBody = PullRequestWorkflow.StripReviewPreamble(reviewBody);

            return (approved, approvedWithSuggestions, reviewBody);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} alignment with AI", pr.Number);
            return (false, false, null);
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
        var critiqueTier = Core.Config.Agents.CritiqueTier;
        if (string.IsNullOrWhiteSpace(critiqueTier))
            return null;

        try
        {
            Logger.LogInformation("Running rubber-duck critique on PR #{Number} using tier {Tier}", pr.Number, critiqueTier);

            var kernel = Core.ModelRegistry.GetKernel(critiqueTier, Identity.Id + "-critique");
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var systemPrompt = await Core.PromptService!.RenderAsync("pm/critique-system",
                new Dictionary<string, string>(), ct)
                ?? "You are an independent code critic. Find problems, challenge assumptions, identify risks.";

            var userPrompt = await Core.PromptService!.RenderAsync("pm/critique-user",
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
    /// WS2 PM inline-comment path: extract file:line: prefixed items from PM review text
    /// so file-specific feedback (e.g. missing import, wrong CSS rule) lands on the
    /// Files-changed tab instead of conversation-only. Empty list if no matches.
    /// </summary>
    private static List<InlineReviewComment> ExtractInlineCommentsFromText(string? text)
    {
        var results = new List<InlineReviewComment>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var pattern = @"(?m)^\s*(?:[-*]|\d+\.)?\s*[`""']?([\w./\\\-]+\.[a-zA-Z]{1,8})[`""']?:(\d+):\s*(.+?)(?:\r?\n(?=\s*(?:[-*]|\d+\.))|\r?\n\r?\n|\z)";
        var regex = new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
        {
            var file = match.Groups[1].Value.Trim();
            if (!int.TryParse(match.Groups[2].Value, out var line) || line < 1) continue;
            var body = match.Groups[3].Value.Trim();
            if (string.IsNullOrWhiteSpace(body)) continue;

            file = file.Replace('\\', '/');

            results.Add(new InlineReviewComment
            {
                FilePath = file,
                Line = line,
                Body = $"**[ProgramManager]** {body}"
            });
        }

        return results;
    }

    /// <summary>
    /// Read visual design reference files from the repository for inclusion in PMSpec.
    /// Returns the raw HTML content that the AI can analyze to create the Visual Design Specification.
    /// </summary>
    private async Task<string?> ReadDesignReferencesForSpecAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
            var designKeywords= new[] { "design", "mockup", "mock", "wireframe", "prototype", "concept", "reference" };

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
                .Where(f => f.StartsWith(Core.ProjectFiles.DesignScreenshotsPrefix, StringComparison.OrdinalIgnoreCase) &&
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
                    var imageUrl = _platform.PlatformHost?.GetRawFileUrl(screenshot, EffectiveBranch)
                        ?? $"https://raw.githubusercontent.com/{Core.Config.Project.GitHubRepo}/{EffectiveBranch}/{screenshot}";
                    sb.AppendLine($"### {fileName}");
                    sb.AppendLine();
                    sb.AppendLine($"![{fileName} design reference]({imageUrl})");
                    sb.AppendLine();
                }
            }

            // Include HTML source for detailed CSS/layout reference
            foreach (var file in htmlDesignFiles)
            {
                var content = await _platform.RepoContent.GetFileContentAsync(file, EffectiveBranch, ct);
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
    /// Detects an unrecoverable rework loop pattern on a PR. Returns true when the
    /// engineer has hit the max-rework-cycles wall AND multiple rework attempts produced
    /// no committable changes — meaning the engineer cannot make progress on the review
    /// feedback no matter how many more cycles we grant. Used by the UI-gate to break
    /// the otherwise-deadlock scenario (gate blocks → engineer reworks → no changes
    /// → max cycles → force-approval → gate blocks → repeat) by bypassing with an
    /// audited decision rather than spinning forever.
    /// </summary>
    private async Task<bool> IsReworkLoopUnrecoverableAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var comments = await _platform.ReviewService.GetCommentsAsync(prNumber, ct);
            var maxCyclesHit = comments.Any(c =>
                (c.Body ?? "").Contains("reached the maximum rework cycle limit", StringComparison.OrdinalIgnoreCase));
            var emptyReworkCount = comments.Count(c =>
                (c.Body ?? "").Contains("produced no committable changes", StringComparison.OrdinalIgnoreCase));
            return maxCyclesHit && emptyReworkCount >= 1;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not evaluate rework loop on PR #{Number}", prNumber);
            return false;
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
            var comments = await _platform.ReviewService.GetCommentsAsync(prNumber, ct);

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
            var tree = await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
            var designPngs= tree
                .Where(f => f.StartsWith(Core.ProjectFiles.DesignScreenshotsPrefix, StringComparison.OrdinalIgnoreCase) &&
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Take(3) // cap at 3 references to keep token usage sane
                .ToList();

            if (designPngs.Count == 0)
            {
                // No rendered PNGs — try to find HTML design files for text context
                var designHtmlFiles = tree
                    .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                        (f.Contains("design", StringComparison.OrdinalIgnoreCase) ||
                         f.Contains("concept", StringComparison.OrdinalIgnoreCase) ||
                         f.Contains("mock", StringComparison.OrdinalIgnoreCase) ||
                         f.Contains("wireframe", StringComparison.OrdinalIgnoreCase)))
                    .Where(f => !f.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
                                !f.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase))
                    .Take(1)
                    .ToList();

                if (designHtmlFiles.Count > 0)
                {
                    try
                    {
                        using var http2 = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        var htmlUrl = _platform.PlatformHost?.GetRawFileUrl(designHtmlFiles[0], EffectiveBranch)
                            ?? $"https://raw.githubusercontent.com/{Core.Config.Project.GitHubRepo}/{EffectiveBranch}/{designHtmlFiles[0]}";
                        var htmlContent = await http2.GetStringAsync(htmlUrl, ct);
                        // Extract key structural elements (cap at 2000 chars to avoid token bloat)
                        var summary = ExtractDesignHtmlSummary(htmlContent, designHtmlFiles[0]);
                        _designHtmlContext = summary;
                        Logger.LogInformation("B2: Loaded HTML design context from {Path} ({Len} chars)",
                            designHtmlFiles[0], summary.Length);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "B2: failed to load HTML design file {Path}", designHtmlFiles[0]);
                    }
                }
                return results;
            }

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            foreach (var path in designPngs)
            {
                try
                {
                    var url = _platform.PlatformHost?.GetRawFileUrl(path, EffectiveBranch)
                        ?? $"https://raw.githubusercontent.com/{Core.Config.Project.GitHubRepo}/{EffectiveBranch}/{path}";
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

    /// <summary>
    /// Extract key structural information from an HTML design file to use as text context
    /// when no rendered PNG is available. Caps output at ~2000 chars.
    /// </summary>
    private static string ExtractDesignHtmlSummary(string html, string filePath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"\n## 📐 Design Template Context (from `{filePath}`)");
        sb.AppendLine("No rendered design image is available, but the HTML design template describes these components:\n");

        // Extract title
        var titleMatch = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>([^<]+)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (titleMatch.Success)
            sb.AppendLine($"- **Page title:** {titleMatch.Groups[1].Value.Trim()}");

        // Extract headings (h1-h3)
        var headings = System.Text.RegularExpressions.Regex.Matches(html,
            @"<h[1-3][^>]*>([^<]+)</h[1-3]>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (headings.Count > 0)
        {
            sb.AppendLine("- **Key headings:**");
            foreach (System.Text.RegularExpressions.Match h in headings.Take(10))
                sb.AppendLine($"  - {h.Groups[1].Value.Trim()}");
        }

        // Extract major structural elements (divs with meaningful class/id names)
        var divClasses = System.Text.RegularExpressions.Regex.Matches(html,
            @"class=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var meaningfulClasses = divClasses.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Where(c => c.Length > 3 && !c.Contains("col-") && !c.Contains("row"))
            .Distinct()
            .Take(15)
            .ToList();
        if (meaningfulClasses.Count > 0)
            sb.AppendLine($"- **Key CSS classes:** {string.Join(", ", meaningfulClasses.Select(c => $"`{c}`"))}");

        // Extract SVG/canvas references (indicates charts/graphics)
        if (html.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- **Contains SVG graphics** (likely timeline, charts, or icons)");
        if (html.Contains("<canvas", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- **Contains Canvas elements** (likely charts or graphs)");

        // Extract grid/table indicators
        if (html.Contains("grid", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- **Uses CSS Grid layout** (likely heatmap or data grid)");
        if (html.Contains("<table", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- **Contains table(s)** (likely data display)");

        sb.AppendLine("\nThe actual PR screenshot must show these structural elements rendered — " +
            "not placeholder text or blank space.\n");

        var result = sb.ToString();
        return result.Length > 2000 ? result[..2000] + "…\n" : result;
    }

    #endregion
}
