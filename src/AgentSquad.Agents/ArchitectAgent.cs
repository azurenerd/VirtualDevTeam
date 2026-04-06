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

public class ArchitectAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly IssueWorkflow _issueWorkflow;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly Queue<ArchitectureDirective> _taskQueue = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly List<IDisposable> _subscriptions = new();

    private bool _architectureComplete;

    public ArchitectAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<ArchitectAgent> logger)
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
        // Only listen for PMSpecReady — NOT generic TaskAssignments.
        // The PM sends a dedicated TaskAssignment after PMSpec is created,
        // but we gate on StatusUpdateMessage to avoid starting before PMSpec exists.
        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        Logger.LogInformation("Architect agent initialized, awaiting PMSpec completion");
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for PMSpec to be ready");

        while (!ct.IsCancellationRequested)
        {
            ArchitectureDirective? currentDirective = null;
            try
            {
                if (!_architectureComplete && _taskQueue.TryDequeue(out var directive))
                {
                    currentDirective = directive;
                    await DesignArchitectureAsync(directive, ct);
                    _architectureComplete = true;
                    currentDirective = null; // Don't re-enqueue on success
                }

                if (_architectureComplete)
                {
                    await ReviewPRsForArchitectureAsync(ct);
                }
                else
                {
                    UpdateStatus(AgentStatus.Idle, "Waiting for PMSpec to be ready");
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
                Logger.LogError(ex, "Architect loop error, will retry after delay");
                RecordError($"Architect error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                if (currentDirective is not null)
                {
                    Logger.LogInformation("Re-enqueueing failed architecture directive");
                    _taskQueue.Enqueue(currentDirective);
                }
                UpdateStatus(AgentStatus.Working, "Recovering from error, will retry");
                try { await Task.Delay(15000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "Architect loop exited");
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    #region Message Handlers

    private Task HandleStatusUpdateAsync(StatusUpdateMessage message, CancellationToken ct)
    {
        // Only react to PMSpecReady — this means Research is done AND PMSpec exists
        if (!string.Equals(message.MessageType, "PMSpecReady", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        Logger.LogInformation(
            "PMSpec ready signal received from {From}, queuing architecture design",
            message.FromAgentId);

        _taskQueue.Enqueue(new ArchitectureDirective
        {
            TaskId = $"architecture-design-{Guid.NewGuid():N}",
            Title = "Design system architecture",
            Description = message.Details ?? "PMSpec and Research are ready. Design the architecture."
        });

        return Task.CompletedTask;
    }

    #endregion

    #region Architecture Design

    private async Task DesignArchitectureAsync(ArchitectureDirective directive, CancellationToken ct)
    {
        // Idempotency: check if Architecture.md already has real content
        var existingArch = await _projectFiles.GetArchitectureDocAsync(ct);
        if (!string.IsNullOrWhiteSpace(existingArch) &&
            !existingArch.Contains("No architecture document has been created yet"))
        {
            Logger.LogInformation("Architecture.md already exists with content, skipping design");
            // Still signal downstream so PE isn't stuck
            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ArchitectureComplete",
                NewStatus = AgentStatus.Idle,
                Details = "Architecture design already complete. Architecture.md is ready for review."
            }, ct);
            return;
        }

        // Find any related architecture issue to link
        int? relatedIssue = null;
        try
        {
            var issues = await _github.GetOpenIssuesAsync(ct);
            var archIssue = issues.FirstOrDefault(i =>
                i.Title.Contains("Architecture", StringComparison.OrdinalIgnoreCase) ||
                i.Title.Contains("architecture", StringComparison.OrdinalIgnoreCase));
            relatedIssue = archIssue?.Number;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not find related issue for architecture");
        }

        // Create the PR upfront so it's visible immediately
        UpdateStatus(AgentStatus.Working, "Creating PR for Architecture.md");
        var pr = await _prWorkflow.OpenDocumentPRAsync(
            Identity.DisplayName,
            "Architecture.md",
            $"System Architecture for {directive.Title}",
            "Complete system architecture document covering components, data model, " +
            "API contracts, infrastructure, security, and scaling strategy.",
            relatedIssue,
            ct);

        UpdateStatus(AgentStatus.Working, "Designing (1/5): Key architectural decisions");
        Logger.LogInformation("Starting architecture design for task {TaskId}: {Title}",
            directive.TaskId, directive.Title);

        // 1. Read PM specs and Research.md
        var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
        var research = await _projectFiles.GetResearchDocAsync(ct);

        // 2. Use Semantic Kernel multi-turn conversation to design architecture
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a senior software architect on a development team. " +
            "Your job is to design a complete, well-structured system architecture based on " +
            "the PM specification (business requirements) and research findings. " +
            "Ensure the architecture supports all business goals, user stories, and " +
            "non-functional requirements from the PM spec. Be thorough, specific, and practical. " +
            "Focus on producing actionable architecture that engineers can implement directly.");

        // Turn 1: Identify key architectural decisions
        history.AddUserMessage(
            $"I need you to design the system architecture for our project.\n\n" +
            $"**Task:** {directive.Title}\n\n" +
            $"**Description:** {directive.Description}\n\n" +
            $"## PM Specification (Business Requirements)\n{pmSpec}\n\n" +
            $"## Research Findings\n{research}\n\n" +
            "First, identify the key architectural decisions we need to make. " +
            "For each decision, explain the options, trade-offs, and your recommendation. " +
            "Ensure the architecture supports all business goals and user stories from the PM Spec. " +
            "List them clearly.");

        var decisionsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(decisionsResponse.Content ?? "");

        Logger.LogDebug("Architectural decisions identified for {TaskId}", directive.TaskId);

        // Turn 2: Design system components and interactions
        UpdateStatus(AgentStatus.Working, "Designing (2/5): Components & interactions");
        history.AddUserMessage(
            "Now design the system components based on those decisions. For each component, cover:\n" +
            "- Name and responsibility (single responsibility principle)\n" +
            "- Public interfaces / API surface\n" +
            "- Dependencies on other components\n" +
            "- Data it owns or manages\n\n" +
            "Also describe the data flow between components for the primary use cases.");

        var componentsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(componentsResponse.Content ?? "");

        Logger.LogDebug("System components designed for {TaskId}", directive.TaskId);

        // Turn 3: Data model, API contracts, and infrastructure
        UpdateStatus(AgentStatus.Working, "Designing (3/5): Data model & APIs");
        history.AddUserMessage(
            "Now define:\n" +
            "1. **Data Model** — key entities, their relationships, and storage strategy.\n" +
            "2. **API Contracts** — endpoints/interfaces, request/response shapes, and error handling.\n" +
            "3. **Infrastructure Requirements** — hosting, networking, storage, CI/CD, and monitoring needs.\n\n" +
            "Be specific with types, field names, and configurations where applicable.");

        var contractsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(contractsResponse.Content ?? "");

        Logger.LogDebug("Data model and contracts defined for {TaskId}", directive.TaskId);

        // Turn 4: Security, scaling, and risk mitigation
        UpdateStatus(AgentStatus.Working, "Designing (4/5): Security & scaling");
        history.AddUserMessage(
            "Now address cross-cutting concerns:\n" +
            "1. **Security Considerations** — authentication, authorization, data protection, input validation.\n" +
            "2. **Scaling Strategy** — horizontal/vertical scaling, caching, load balancing, bottleneck mitigation.\n" +
            "3. **Risks & Mitigations** — technical risks, dependency risks, and concrete mitigation strategies.\n\n" +
            "Be practical and prioritize the highest-impact concerns.");

        var risksResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(risksResponse.Content ?? "");

        Logger.LogDebug("Cross-cutting concerns addressed for {TaskId}", directive.TaskId);

        // Turn 5: Compile into structured Architecture.md
        UpdateStatus(AgentStatus.Working, "Designing (5/5): Compiling Architecture.md");
        history.AddUserMessage(
            "Now compile everything into a single, structured Architecture.md document with these exact sections:\n\n" +
            "# Architecture\n\n" +
            "## Overview & Goals\n" +
            "(High-level summary of the architecture and what it aims to achieve)\n\n" +
            "## System Components\n" +
            "(Each component with its responsibilities)\n\n" +
            "## Component Interactions\n" +
            "(Data flow and communication patterns between components)\n\n" +
            "## Data Model\n" +
            "(Entities, relationships, storage)\n\n" +
            "## API Contracts\n" +
            "(Endpoints, interfaces, request/response shapes)\n\n" +
            "## Infrastructure Requirements\n" +
            "(Hosting, networking, storage, CI/CD)\n\n" +
            "## Technology Stack Decisions\n" +
            "(Chosen technologies with justification)\n\n" +
            "## Security Considerations\n" +
            "(Auth, data protection, validation)\n\n" +
            "## Scaling Strategy\n" +
            "(How the system scales)\n\n" +
            "## Risks & Mitigations\n" +
            "(Key risks and how to address them)\n\n" +
            "Use these exact section headers. Be thorough and specific. " +
            "This document will be the single source of truth for the engineering team.");

        var architectureResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var architectureDoc = architectureResponse.Content?.Trim() ?? "";

        Logger.LogDebug("Architecture document compiled for {TaskId}", directive.TaskId);

        // Commit final content and auto-merge
        UpdateStatus(AgentStatus.Working, "Committing Architecture.md and merging PR");
        await _prWorkflow.CommitAndMergeDocumentPRAsync(
            pr,
            Identity.DisplayName,
            "Architecture.md",
            architectureDoc,
            $"Add system architecture for {directive.Title}",
            ct);

        Logger.LogInformation("Architecture.md PR created and merged for task {TaskId}", directive.TaskId);

        // 4. Create Issue for Principal Engineer
        await _issueWorkflow.AskAgentAsync(
            Identity.DisplayName,
            "Principal Engineer",
            "Architecture document is ready for review. " +
            "Please review Architecture.md and begin engineering planning.",
            ct);

        Logger.LogInformation("Created issue for Principal Engineer to review architecture");

        // 5. Notify PM via message bus
        await _messageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ArchitectureComplete",
            NewStatus = AgentStatus.Online,
            CurrentTask = directive.TaskId,
            Details = "Architecture design complete. Architecture.md is ready for review."
        }, ct);

        UpdateStatus(AgentStatus.Idle, "Architecture complete, monitoring PRs for alignment");
    }

    #endregion

    #region PR Review for Architectural Alignment

    private async Task ReviewPRsForArchitectureAsync(CancellationToken ct)
    {
        try
        {
            var prs = await _github.GetOpenPullRequestsAsync(ct);

            var readyForReview = prs.Where(pr =>
                pr.Labels.Contains(PullRequestWorkflow.Labels.ReadyForReview,
                    StringComparer.OrdinalIgnoreCase)
                && !pr.Labels.Contains(PullRequestWorkflow.Labels.Approved,
                    StringComparer.OrdinalIgnoreCase)
                && !_reviewedPrNumbers.Contains(pr.Number)).ToList();

            foreach (var pr in readyForReview)
            {
                Logger.LogInformation("Reviewing PR #{Number} for architectural alignment: {Title}",
                    pr.Number, pr.Title);

                var review = await EvaluateArchitecturalAlignmentAsync(pr, ct);

                if (review is not null)
                {
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"🏗️ **Architecture Review:**\n\n{review}", ct);
                }

                _reviewedPrNumbers.Add(pr.Number);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review PRs for architectural alignment");
        }
    }

    private async Task<string?> EvaluateArchitecturalAlignmentAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a software architect reviewing a pull request for alignment with " +
                "the project's architecture document. Evaluate whether the PR's scope, approach, " +
                "and design follow the architectural decisions and component boundaries defined " +
                "in the architecture. Be concise and actionable. " +
                "Flag any deviations, missing considerations, or potential architectural issues. " +
                "If everything aligns well, say so briefly and note any positive patterns.");

            history.AddUserMessage(
                $"## Architecture Document\n{architectureDoc}\n\n" +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}");

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);

            return response.Content?.Trim();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} for architecture alignment",
                pr.Number);
            return null;
        }
    }

    #endregion
}

internal record ArchitectureDirective
{
    public string TaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
}
