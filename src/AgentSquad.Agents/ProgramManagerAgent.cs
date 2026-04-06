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
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly Dictionary<string, AgentTracking> _trackedAgents = new();
    private readonly HashSet<int> _processedIssueIds = new();
    private int _additionalEngineersHired;
    private string? _currentPhase;
    private bool _pmSpecCreated;

    private readonly List<IDisposable> _subscriptions = new();

    public ProgramManagerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<ProgramManagerAgent> logger)
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
        UpdateStatus(AgentStatus.Idle, "Initializing project oversight");

        // One-time kickoff: read project description and seed the Researcher
        await KickOffProjectAsync(ct);

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

            // Build the research guidance — use custom prompt if provided, otherwise generate a rich default
            var researchGuidance = GetResearchGuidance(projectName, projectDescription);

            // 1. Create a GitHub Issue for tracking and visibility (idempotent)
            var issueTitle = $"Researcher: Research technology stack for {projectName}";

            var existingIssues = await _github.GetOpenIssuesAsync(ct);
            var existingKickoff = existingIssues.FirstOrDefault(i =>
                i.Title.Equals(issueTitle, StringComparison.OrdinalIgnoreCase));

            if (existingKickoff is not null)
            {
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

                Logger.LogInformation(
                    "Created kickoff issue #{Number}: {Title}",
                    issue.Number, issueTitle);
            }

            // 2. Send a TaskAssignmentMessage via bus to trigger the Researcher.
            //    Include the research guidance in the description so the Researcher
            //    gets the full context even if it doesn't read the GitHub issue.
            var taskId = $"kickoff-research-{Guid.NewGuid():N}";
            await _messageBus.PublishAsync(new TaskAssignmentMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "TaskAssignment",
                TaskId = taskId,
                Title = $"Research technology stack for {projectName}",
                Description = $"{projectDescription}\n\n## Research Guidance\n{researchGuidance}",
                Complexity = "High"
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
        return $"""
            Conduct a thorough, multi-dimensional research analysis for the project "{projectName}".
            Go beyond surface-level recommendations — the engineering team needs depth and specificity.

            ### 1. Domain & Market Research
            - What are the core domain concepts and terminology?
            - Who are the target users and what are their key workflows?
            - Are there existing products, competitors, or open-source projects solving similar problems?
            - What industry standards, regulations, or compliance requirements apply?

            ### 2. Technology Stack Evaluation
            - Evaluate at least 2-3 candidate technology stacks (frontend, backend, database, infrastructure)
            - For each candidate, provide: strengths, weaknesses, community size, maturity, learning curve
            - Recommend a primary stack with clear justification tied to the project requirements
            - Include specific version numbers and compatibility considerations

            ### 3. Architecture Patterns & Design
            - Which architecture patterns best fit this project (monolith, microservices, serverless, etc.)?
            - What data storage strategy is appropriate (relational, document, graph, hybrid)?
            - How should the system handle scalability, caching, and performance?
            - What API design approach should be used (REST, GraphQL, gRPC)?

            ### 4. Libraries, Frameworks & Dependencies
            - List specific libraries and packages for core functionality (not just categories)
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
            var pendingPRs = await _prWorkflow.GetCodePRsPendingReviewAsync(ct);

            foreach (var pr in pendingPRs)
            {
                // Skip if we've already approved this PR
                if (!await _prWorkflow.NeedsReviewFromAsync(pr.Number, "ProgramManager", ct))
                    continue;

                Logger.LogInformation("PM reviewing PR #{Number}: {Title}", pr.Number, pr.Title);
                UpdateStatus(AgentStatus.Working, $"Reviewing PR #{pr.Number}: {pr.Title}");

                var (approved, reviewBody) = await EvaluatePrAlignmentWithVerdictAsync(pr, ct);

                if (reviewBody is null)
                    continue;

                if (approved)
                {
                    var merged = await _prWorkflow.ApproveAndMaybeMergeAsync(
                        pr.Number, "ProgramManager", reviewBody, ct);
                    if (merged)
                        Logger.LogInformation("PM approved and merged PR #{Number}", pr.Number);
                    else
                        Logger.LogInformation("PM approved PR #{Number}, waiting for PE approval", pr.Number);
                }
                else
                {
                    // Request changes — PM can make fixes directly on premium model
                    await _prWorkflow.RequestChangesAsync(pr.Number, "ProgramManager", reviewBody, ct);
                    Logger.LogInformation("PM requested changes on PR #{Number}", pr.Number);
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
                Role = AgentRole.SeniorEngineer // default; updated if known
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

    #endregion

    #region AI-Assisted Methods

    /// <summary>
    /// Creates a PM Specification document from the research findings and project description.
    /// Uses a multi-turn AI conversation to produce a structured business spec, then
    /// triggers the Architect to begin architecture design.
    /// </summary>
    private async Task CreatePMSpecAsync(CancellationToken ct)
    {
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
                return;
            }

            // Create the PR upfront so it's visible immediately
            var projectName = _config.Project.Name;
            UpdateStatus(AgentStatus.Working, "Creating PR for PMSpec.md");
            var pr = await _prWorkflow.OpenDocumentPRAsync(
                Identity.DisplayName,
                "PMSpec.md",
                $"PM Specification for {projectName}",
                $"Formal product specification document covering business goals, user stories, " +
                $"acceptance criteria, scope, and non-functional requirements for {projectName}.",
                closesIssueNumber: null,
                ct);

            UpdateStatus(AgentStatus.Working, "Creating PMSpec (1/2): Analyzing requirements");

            var projectDescription = _config.Project.Description;
            var researchDoc = await _projectFiles.GetResearchDocAsync(ct);

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Program Manager creating a formal product specification document. " +
                "Your goal is to translate research findings and a project description into a " +
                "clear, actionable specification that architects and engineers can use to design " +
                "and build the system. Be thorough, specific, and business-focused.");

            // Turn 1: Analyze and identify business goals, user stories, success criteria
            history.AddUserMessage(
                $"I need you to create a PM Specification for our project.\n\n" +
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
                "Be specific and actionable. Each user story should have clear acceptance criteria.");

            var analysisResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            history.AddAssistantMessage(analysisResponse.Content ?? "");

            Logger.LogDebug("PM Spec analysis complete for {ProjectName}", projectName);

            // Turn 2: Produce the structured PMSpec.md
            UpdateStatus(AgentStatus.Working, "Creating PMSpec (2/2): Drafting specification");
            history.AddUserMessage(
                "Now compile everything into a single, structured PMSpec.md document with these exact sections:\n\n" +
                "# PM Specification: {ProjectName}\n\n" +
                "## Executive Summary\n" +
                "(2-3 sentences describing what we're building and why)\n\n" +
                "## Business Goals\n" +
                "(Numbered list of concrete business objectives)\n\n" +
                "## User Stories & Acceptance Criteria\n" +
                "(Each story as: **As a [role]**, I want [capability], so that [benefit]. " +
                "Followed by acceptance criteria as a checklist.)\n\n" +
                "## Scope\n" +
                "### In Scope\n(Bullet list)\n" +
                "### Out of Scope\n(Bullet list — explicit exclusions to prevent scope creep)\n\n" +
                "## Non-Functional Requirements\n" +
                "(Performance targets, security requirements, scalability needs, reliability SLAs)\n\n" +
                "## Success Metrics\n" +
                "(Measurable criteria for project completion)\n\n" +
                "## Constraints & Assumptions\n" +
                "(Technical constraints, timeline assumptions, dependency assumptions)\n\n" +
                $"Replace {{ProjectName}} with '{projectName}'. Use these exact section headers. " +
                "This document will be the single source of truth for business requirements.");

            var specResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            var pmSpecDoc = specResponse.Content?.Trim() ?? "";

            Logger.LogDebug("PM Spec document compiled for {ProjectName}", projectName);

            // Commit final content and auto-merge
            UpdateStatus(AgentStatus.Working, "Committing PMSpec.md and merging PR");
            await _prWorkflow.CommitAndMergeDocumentPRAsync(
                pr,
                Identity.DisplayName,
                "PMSpec.md",
                pmSpecDoc,
                $"Add PM Specification for {projectName}",
                ct);
            Logger.LogInformation("PMSpec.md PR created and merged for project {ProjectName}", projectName);

            // Notify all agents that PMSpec is ready — Architect will pick this up
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "PMSpecReady",
                NewStatus = AgentStatus.Working,
                Details = "PM Specification is ready. Architect can begin architecture design."
            }, ct);

            Logger.LogInformation("Triggered Architect to begin architecture design");

            UpdateStatus(AgentStatus.Idle, "PMSpec complete, Architect triggered");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create PM Specification — Architect may need manual trigger");
            RecordError($"PMSpec creation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
        }
    }

    private async Task<string?> TriageBlockerAsync(AgentIssue blocker, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
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

    private async Task<(bool Approved, string? ReviewBody)> EvaluatePrAlignmentWithVerdictAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
            var engineeringPlan = await _projectFiles.GetEngineeringPlanAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a Program Manager reviewing a pull request for alignment with " +
                "project requirements and the PM specification. Evaluate:\n" +
                "1. Does the PR meet the stated requirements from the PM spec?\n" +
                "2. Is the scope correct — not too broad, not too narrow?\n" +
                "3. Are there any gaps in functionality or missing edge cases?\n" +
                "4. Does the implementation approach make sense for the business goals?\n\n" +
                "Be constructive and specific with feedback.\n" +
                "End your review with exactly one of these verdicts on a new line:\n" +
                "VERDICT: APPROVE\n" +
                "VERDICT: REQUEST_CHANGES");

            history.AddUserMessage(
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
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} alignment with AI", pr.Number);
            return (false, null);
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
