using System.Collections.Concurrent;
using System.Text;
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
    private readonly IGateCheckService _gateCheck;
    private readonly SelfAssessmentService _selfAssessment;
    private readonly IAgentReasoningLog _reasoningLog;
    private readonly IPromptTemplateService _promptService;
    private readonly DecisionGateService? _decisionGate;
    private readonly IAgentTaskTracker _taskTracker;

    private readonly Queue<ArchitectureDirective>_taskQueue = new();
    private readonly HashSet<int> _reviewedPrNumbers = new();
    private readonly ConcurrentQueue<int> _reviewQueue = new();
    private readonly HashSet<int> _forceApprovalPrs = new();
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
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        SelfAssessmentService selfAssessment,
        IAgentReasoningLog reasoningLog,
        IPromptTemplateService promptService,
        IAgentTaskTracker taskTracker,
        ILogger<ArchitectAgent> logger,
        RoleContextProvider? roleContextProvider = null,
        DecisionGateService? decisionGate = null)
        : base(identity, logger, memoryStore, roleContextProvider)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _issueWorkflow = issueWorkflow ?? throw new ArgumentNullException(nameof(issueWorkflow));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _selfAssessment = selfAssessment ?? throw new ArgumentNullException(nameof(selfAssessment));
        _reasoningLog = reasoningLog ?? throw new ArgumentNullException(nameof(reasoningLog));
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _taskTracker = taskTracker ?? throw new ArgumentNullException(nameof(taskTracker));
        _decisionGate = decisionGate;
    }

    protected override async Task OnInitializeAsync(CancellationToken ct)
    {
        // Only listen for PMSpecReady — NOT generic TaskAssignments.
        // The PM sends a dedicated TaskAssignment after PMSpec is created,
        // but we gate on StatusUpdateMessage to avoid starting before PMSpec exists.
        _subscriptions.Add(_messageBus.Subscribe<StatusUpdateMessage>(
            Identity.Id, HandleStatusUpdateAsync));

        _subscriptions.Add(_messageBus.Subscribe<ReviewRequestMessage>(
            Identity.Id, HandleReviewRequestAsync));

        // Recovery: check if Architecture.md already exists from a prior run
        // Must verify content has real architecture sections, not just any file content
        try
        {
            var archDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            if (!string.IsNullOrWhiteSpace(archDoc)
                && !archDoc.Contains("No architecture document has been created yet", StringComparison.OrdinalIgnoreCase)
                && archDoc.Length > 200
                && archDoc.Contains("## System Components", StringComparison.OrdinalIgnoreCase))
            {
                _architectureComplete = true;
                Logger.LogInformation("Architect recovered: Architecture.md already exists with valid sections, moving to PR review mode");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to check for existing Architecture.md during init");
        }

        Logger.LogInformation("Architect agent initialized, awaiting PMSpec completion");
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        if (_architectureComplete)
            UpdateStatus(AgentStatus.Idle, "Architecture complete, monitoring PRs");
        else
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

    private Task HandleReviewRequestAsync(ReviewRequestMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Review request from {Agent} for PR #{PrNumber}: {Title} ({ReviewType})",
            message.FromAgentId, message.PrNumber, message.PrTitle, message.ReviewType);

        // Clear reviewed flag so reworked PRs get re-reviewed
        _reviewedPrNumbers.Remove(message.PrNumber);

        // Track FinalApproval requests so Architect auto-approves after max rework cycles
        if (string.Equals(message.ReviewType, "FinalApproval", StringComparison.OrdinalIgnoreCase))
            _forceApprovalPrs.Add(message.PrNumber);

        _reviewQueue.Enqueue(message.PrNumber);
        return Task.CompletedTask;
    }

    #endregion

    #region Architecture Design

    /// <summary>Revise Architecture.md based on reviewer feedback using AI.</summary>
    private async Task<string?> ReviseArchitectureAsync(
        ArchitectureDirective directive, string feedback, CancellationToken ct)
    {
        try
        {
            var currentContent = await _projectFiles.GetArchitectureDocAsync(ct);
            if (string.IsNullOrWhiteSpace(currentContent)) return null;

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();

            var revSys = await _promptService.RenderAsync("architect/revision-system",
                new Dictionary<string, string>(), ct)
                ?? "You are a senior software architect revising Architecture.md based on human reviewer feedback. " +
                   "Make the specific changes requested while preserving the overall structure.";
            history.AddSystemMessage(revSys);

            var revUser = await _promptService.RenderAsync("architect/revision-user",
                new Dictionary<string, string>
                {
                    ["current_content"] = currentContent,
                    ["feedback"] = feedback
                }, ct)
                ?? $"## Current Architecture.md:\n\n{currentContent}\n\n" +
                   $"## Reviewer Feedback:\n\n{feedback}\n\n" +
                   "Revise the Architecture.md to address the feedback. Return the COMPLETE revised document.";
            history.AddUserMessage(revUser);

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var revised = string.Join("", response.Select(r => r.Content ?? ""));
            return string.IsNullOrWhiteSpace(revised) ? null : revised;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to revise Architecture.md");
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

    private async Task DesignArchitectureAsync(ArchitectureDirective directive, CancellationToken ct)
    {
        // Idempotency: check if Architecture.md already has real architectural content
        var existingArch = await _projectFiles.GetArchitectureDocAsync(ct);
        if (!string.IsNullOrWhiteSpace(existingArch) &&
            !existingArch.Contains("No architecture document has been created yet") &&
            existingArch.Contains("## System Components", StringComparison.OrdinalIgnoreCase))
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

        // Quick mode: produce a minimal 1-paragraph architecture for fast testing
        if (_config.Project.QuickDocumentCreation)
        {
            Logger.LogInformation("QuickDocumentCreation: producing minimal Architecture.md");
            UpdateStatus(AgentStatus.Working, "Creating minimal Architecture (quick mode)");
            var qPr = await _prWorkflow.OpenDocumentPRAsync(
                Identity.DisplayName, "Architecture.md",
                $"System Architecture for {directive.Title}",
                "Quick-mode architecture document.", relatedIssue, ct);

            var qKernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var qChat = qKernel.GetRequiredService<IChatCompletionService>();
            var qHistory = CreateChatHistory();

            var qSys = await _promptService.RenderAsync("architect/quick-system",
                new Dictionary<string, string>(), ct)
                ?? "You are a software architect. Write a brief architecture document.";
            qHistory.AddSystemMessage(qSys);

            var qUser = await _promptService.RenderAsync("architect/quick-user",
                new Dictionary<string, string>
                {
                    ["project_description"] = _config.Project.Description,
                    ["tech_stack"] = _config.Project.TechStack
                }, ct)
                ?? $"Project: {_config.Project.Description}\nTech Stack: {_config.Project.TechStack}\n\n" +
                   "Write a concise architecture document with these sections (1-2 sentences each): " +
                   "## System Components (list main components), ## Data Model (key entities), " +
                   "## Project Structure (folder layout — the repo root IS the solution root; " +
                   "place .sln at root, project files under ProjectName/ subfolder; " +
                   "NEVER create multiple levels of same-named folders), ## Technology Choices. " +
                   "Keep the entire document under 300 words. Be specific about file paths and component names.";
            qHistory.AddUserMessage(qUser);
            var qResp = await qChat.GetChatMessageContentAsync(qHistory, cancellationToken: ct);
            var qContent = $"# System Architecture: {directive.Title}\n\n{qResp.Content?.Trim() ?? ""}";

            await _prWorkflow.CommitAndMergeDocumentPRAsync(
                qPr, Identity.DisplayName, "Architecture.md", qContent,
                $"Add system architecture for {directive.Title}", ct);
            Logger.LogInformation("Quick Architecture.md created and merged");
            LogActivity("task", $"✅ Quick Architecture.md merged: {directive.Title}");

            if (relatedIssue.HasValue)
            {
                try { await _github.CloseIssueAsync(relatedIssue.Value, ct); }
                catch { /* best effort */ }
            }

            await _messageBus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id, ToAgentId = "*",
                MessageType = "ArchitectureComplete",
                NewStatus = AgentStatus.Working,
                Details = "Architecture design complete (quick mode). PE can begin engineering planning."
            }, ct);
            UpdateStatus(AgentStatus.Idle, "Quick architecture complete");
            return;
        }

        // Create the PR upfront so it's visible immediately
        UpdateStatus(AgentStatus.Working, "Creating PR for Architecture.md");
        string? createPrStepId = null;
        try { createPrStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Create architecture PR", "Opening PR for Architecture.md"); } catch { }
        var pr = await _prWorkflow.OpenDocumentPRAsync(
            Identity.DisplayName,
            "Architecture.md",
            $"System Architecture for {directive.Title}",
            "Complete system architecture document covering components, data model, " +
            "API contracts, infrastructure, security, and scaling strategy.",
            relatedIssue,
            ct);
        try { if (createPrStepId is not null) _taskTracker.CompleteStep(createPrStepId); } catch { }

        // Resume-aware: check if gate is already pending/approved from a prior run
        var gateStatus = await _gateCheck.GetGateStatusAsync(
            GateIds.ArchitectureDesign, pr.Number, ct);

        string? architectureDoc = null;

        if (gateStatus == GateStatus.Approved)
        {
            Logger.LogInformation("Architecture gate already approved on PR #{Number}, skipping design", pr.Number);
            LogActivity("task", $"⏩ Architecture gate already approved on PR #{pr.Number}, resuming");
        }
        else if (gateStatus == GateStatus.AwaitingApproval)
        {
            Logger.LogInformation("Architecture gate already pending on PR #{Number}, skipping to gate wait", pr.Number);
            LogActivity("task", $"⏩ Architecture gate already pending on PR #{pr.Number}, resuming wait");
        }
        else
        {

        UpdateStatus(AgentStatus.Working, "Starting architecture design");
        Logger.LogInformation("Starting architecture design for task {TaskId}: {Title}",
            directive.TaskId, directive.Title);
        LogActivity("task", $"🏗️ Starting architecture design: {directive.Title}");

        // 1. Read PM specs and Research.md
        string? readCtxStepId = null;
        try { readCtxStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Read context (PMSpec, Research)", "Reading PM specification and research findings"); } catch { }
        var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
        var research = await _projectFiles.GetResearchDocAsync(ct);

        // 1b. Read visual design reference files directly for architecture decisions
        var designContext = await ReadDesignReferencesAsync(ct);
        try { if (readCtxStepId is not null) _taskTracker.CompleteStep(readCtxStepId); } catch { }

        // 2. Use Semantic Kernel multi-turn conversation to design architecture
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = CreateChatHistory();
        var memoryContext = await GetMemoryContextAsync(ct: ct);

        var sysVars = new Dictionary<string, string>
        {
            ["tech_stack"] = _config.Project.TechStack,
            ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}",
            ["design_context"] = ""
        };

        if (!string.IsNullOrWhiteSpace(designContext))
        {
            var designPrompt = await _promptService.RenderAsync("architect/design-reference",
                new Dictionary<string, string> { ["design_context"] = designContext }, ct);
            sysVars["design_context"] = designPrompt ??
                "\n\n## VISUAL DESIGN REFERENCE\n" +
                "The repository contains visual design reference files that define the exact UI layout. " +
                "Your architecture MUST define components that map directly to the visual sections in this design. " +
                "Include a '## UI Component Architecture' section in your output that maps each visual section " +
                "from the design to a specific component, its CSS layout strategy, data bindings, and interactions.\n\n" +
                designContext;
        }

        var systemPrompt = await _promptService.RenderAsync("architect/full-system", sysVars, ct)
            ?? "You are a senior software architect on a development team. " +
               "Your job is to design a complete, well-structured system architecture based on " +
               "the PM specification (business requirements) and research findings. " +
               "Ensure the architecture supports all business goals, user stories, and " +
               "non-functional requirements from the PM spec. Be thorough, specific, and practical. " +
               "Focus on producing actionable architecture that engineers can implement directly.\n\n" +
               $"IMPORTANT: The project's technology stack has already been decided: **{_config.Project.TechStack}**. " +
               "Your architecture MUST use this stack. Design all components, patterns, and " +
               "infrastructure around this technology. Do NOT recommend or use alternative stacks." +
               (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}") +
               (string.IsNullOrWhiteSpace(designContext)
                   ? ""
                   : "\n\n## VISUAL DESIGN REFERENCE\n" +
                     "The repository contains visual design reference files that define the exact UI layout. " +
                     "Your architecture MUST define components that map directly to the visual sections in this design. " +
                     "Include a '## UI Component Architecture' section in your output that maps each visual section " +
                     "from the design to a specific component, its CSS layout strategy, data bindings, and interactions.\n\n" +
                     designContext);

        history.AddSystemMessage(systemPrompt);

        var useSinglePass = _config.CopilotCli.SinglePassMode;
        string? designStepId = null;
        try { designStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Multi-turn architecture design", "Designing architecture via AI conversation", Identity.ModelTier); } catch { }

        if (useSinglePass)
        {
            // Single-pass mode: one comprehensive prompt instead of 5 conversational turns
            UpdateStatus(AgentStatus.Working, "Designing architecture (single-pass)");

            var singlePassVars = new Dictionary<string, string>
            {
                ["task_title"] = directive.Title,
                ["task_description"] = directive.Description,
                ["tech_stack"] = _config.Project.TechStack,
                ["pm_spec"] = pmSpec ?? "",
                ["research"] = research ?? ""
            };
            var singlePassUser = await _promptService.RenderAsync("architect/single-pass-user", singlePassVars, ct)
                ?? $"I need you to design the complete system architecture for our project.\n\n" +
                   $"**Task:** {directive.Title}\n\n" +
                   $"**Description:** {directive.Description}\n\n" +
                   $"**Technology Stack (mandatory):** {_config.Project.TechStack}\n\n" +
                   $"## PM Specification (Business Requirements)\n{pmSpec}\n\n" +
                   $"## Research Findings\n{research}\n\n" +
                   "Produce a complete, structured Architecture.md document with ALL of these sections:\n\n" +
                   "# Architecture\n\n" +
                   "## Overview & Goals\n(High-level summary)\n\n" +
                   "## System Components\n(Each component with responsibilities, interfaces, dependencies, data)\n\n" +
                   "## Component Interactions\n(Data flow and communication patterns)\n\n" +
                   "## Data Model\n(Entities, relationships, storage)\n\n" +
                   "## API Contracts\n(Endpoints, request/response shapes, error handling)\n\n" +
                   "## Infrastructure Requirements\n(Hosting, networking, storage, CI/CD)\n\n" +
                   "## Technology Stack Decisions\n(Chosen technologies with justification)\n\n" +
                   "## Security Considerations\n(Auth, data protection, validation)\n\n" +
                   "## Scaling Strategy\n(How the system scales)\n\n" +
                   "## Risks & Mitigations\n(Key risks and how to address them)\n\n" +
                   "Use these exact section headers. Be thorough and specific. " +
                   "All decisions must use the mandatory technology stack. " +
                   "This document will be the single source of truth for the engineering team.";
            history.AddUserMessage(singlePassUser);

            var singleResponse = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);
            architectureDoc = singleResponse.Content?.Trim() ?? "";
            try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Single-pass architecture design"); } } catch { }
        }
        else
        {
        // Turn 1: Identify key architectural decisions
        var turnVars = new Dictionary<string, string>
        {
            ["task_title"] = directive.Title,
            ["task_description"] = directive.Description,
            ["tech_stack"] = _config.Project.TechStack,
            ["pm_spec"] = pmSpec ?? "",
            ["research"] = research ?? ""
        };
        var turn1Prompt = await _promptService.RenderAsync("architect/multi-turn-decisions", turnVars, ct)
            ?? $"I need you to design the system architecture for our project.\n\n" +
               $"**Task:** {directive.Title}\n\n" +
               $"**Description:** {directive.Description}\n\n" +
               $"**Technology Stack (mandatory):** {_config.Project.TechStack}\n\n" +
               $"## PM Specification (Business Requirements)\n{pmSpec}\n\n" +
               $"## Research Findings\n{research}\n\n" +
               "First, identify the key architectural decisions we need to make. " +
               "For each decision, explain the options, trade-offs, and your recommendation. " +
               "Ensure the architecture supports all business goals and user stories from the PM Spec. " +
               "All decisions must use the mandatory technology stack specified above. " +
               "List them clearly.";
        history.AddUserMessage(turn1Prompt);

        var decisionsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(decisionsResponse.Content ?? "");
        try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Turn 1/5: Key architectural decisions"); } } catch { }

        Logger.LogDebug("Architectural decisions identified for {TaskId}", directive.TaskId);
        await RememberAsync(MemoryType.Decision,
            $"Key architectural decisions for '{directive.Title}'",
            TruncateForMemory(decisionsResponse.Content ?? ""), ct);

        // Turn 2: Design system components and interactions
        UpdateStatus(AgentStatus.Working, "Designing (2/5): Components & interactions");
        var turn2Prompt = await _promptService.RenderAsync("architect/multi-turn-components",
            new Dictionary<string, string>(), ct)
            ?? "Now design the system components based on those decisions. For each component, cover:\n" +
               "- Name and responsibility (single responsibility principle)\n" +
               "- Public interfaces / API surface\n" +
               "- Dependencies on other components\n" +
               "- Data it owns or manages\n\n" +
               "Also describe the data flow between components for the primary use cases.";
        history.AddUserMessage(turn2Prompt);

        var componentsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(componentsResponse.Content ?? "");
        try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Turn 2/5: Components & interactions"); } } catch { }

        Logger.LogDebug("System components designed for {TaskId}", directive.TaskId);

        // Turn 3: Data model, API contracts, and infrastructure
        UpdateStatus(AgentStatus.Working, "Designing (3/5): Data model & APIs");
        var turn3Prompt = await _promptService.RenderAsync("architect/multi-turn-data-model",
            new Dictionary<string, string>(), ct)
            ?? "Now define:\n" +
               "1. **Data Model** — key entities, their relationships, and storage strategy.\n" +
               "2. **API Contracts** — endpoints/interfaces, request/response shapes, and error handling.\n" +
               "3. **Infrastructure Requirements** — hosting, networking, storage, CI/CD, and monitoring needs.\n\n" +
               "Be specific with types, field names, and configurations where applicable.";
        history.AddUserMessage(turn3Prompt);

        var contractsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(contractsResponse.Content ?? "");
        try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Turn 3/5: Data model & APIs"); } } catch { }

        Logger.LogDebug("Data model and contracts defined for {TaskId}", directive.TaskId);

        // Turn 4: Security, scaling, and risk mitigation
        UpdateStatus(AgentStatus.Working, "Designing (4/5): Security & scaling");
        var turn4Prompt = await _promptService.RenderAsync("architect/multi-turn-cross-cutting",
            new Dictionary<string, string>(), ct)
            ?? "Now address cross-cutting concerns:\n" +
               "1. **Security Considerations** — authentication, authorization, data protection, input validation.\n" +
               "2. **Scaling Strategy** — horizontal/vertical scaling, caching, load balancing, bottleneck mitigation.\n" +
               "3. **Risks & Mitigations** — technical risks, dependency risks, and concrete mitigation strategies.\n\n" +
               "Be practical and prioritize the highest-impact concerns.";
        history.AddUserMessage(turn4Prompt);

        var risksResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(risksResponse.Content ?? "");
        try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Turn 4/5: Security & scaling"); } } catch { }

        Logger.LogDebug("Cross-cutting concerns addressed for {TaskId}", directive.TaskId);

        // Turn 5: Compile into structured Architecture.md
        UpdateStatus(AgentStatus.Working, "Designing (5/5): Compiling Architecture.md");
        var turn5Prompt = await _promptService.RenderAsync("architect/multi-turn-compile",
            new Dictionary<string, string>(), ct)
            ?? "Now compile everything into a single, structured Architecture.md document with these exact sections:\n\n" +
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
               "This document will be the single source of truth for the engineering team.";
        history.AddUserMessage(turn5Prompt);

        var architectureResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        architectureDoc = architectureResponse.Content?.Trim() ?? "";
        try { if (designStepId is not null) { _taskTracker.RecordLlmCall(designStepId); _taskTracker.RecordSubStep(designStepId, "Turn 5/5: Compiling Architecture.md"); } } catch { }

        } // end else (multi-turn)

        // Self-assessment: assess and refine the architecture document
        try { if (designStepId is not null) _taskTracker.CompleteStep(designStepId); } catch { }
        string? assessStepId = null;
        try { assessStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Self-assessment & impact classification", "Assessing and refining architecture output", Identity.ModelTier); } catch { }
        _reasoningLog.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Generating,
            Phase = "Architecture",
            Summary = $"Architecture document generated for '{directive.Title}'",
            Iteration = 0,
        });

        var criteria = AssessmentCriteria.GetForRole(Identity.Role);
        AgentSquad.Core.Agents.Reasoning.AssessmentResult? assessmentResult = null;
        if (criteria is not null)
        {
            // Use inline classification to save a separate LLM call
            var (refinedOutput, assessment) = await _selfAssessment.AssessAndRefineWithResultAsync(
                Identity.Id,
                Identity.DisplayName,
                Identity.Role,
                "Architecture",
                architectureDoc,
                criteria,
                $"Project: {_config.Project.Description}\nPM Spec and Research available for reference",
                chat,
                classifyImpact: _decisionGate is not null,
                ct);
            architectureDoc = refinedOutput;
            assessmentResult = assessment;
        }

        Logger.LogDebug("Architecture document compiled for {TaskId}", directive.TaskId);

        // Use inline classification from assessment if available, otherwise fall back to separate call
        if (_decisionGate is not null && architectureDoc is not null)
        {
            AgentDecision archDecision;
            if (assessmentResult?.HasImpactClassification == true)
            {
                archDecision = await _decisionGate.ClassifyFromAssessmentAsync(
                    agentId: Identity.Id,
                    agentDisplayName: Identity.DisplayName,
                    phase: "Architecture",
                    title: $"Architecture design for '{directive.Title}'",
                    context: $"Architecture document defines system components, data models, technology choices, " +
                             $"and project structure for the project. Key sections include system components, " +
                             $"API contracts, and technology stack decisions. " +
                             $"Document length: {architectureDoc.Length} chars.",
                    assessment: assessmentResult,
                    category: "Architecture",
                    modelTier: Identity.ModelTier,
                    ct: ct);
            }
            else
            {
                archDecision = await _decisionGate.ClassifyAndGateDecisionAsync(
                    agentId: Identity.Id,
                    agentDisplayName: Identity.DisplayName,
                    phase: "Architecture",
                    title: $"Architecture design for '{directive.Title}'",
                    context: $"Architecture document defines system components, data models, technology choices, " +
                             $"and project structure for the project. Key sections include system components, " +
                             $"API contracts, and technology stack decisions. " +
                             $"Document length: {architectureDoc.Length} chars.",
                    category: "Architecture",
                    modelTier: Identity.ModelTier,
                    ct: ct);
            }

            if (archDecision.Status == DecisionStatus.Pending)
            {
                Logger.LogInformation("Architecture decision gated — waiting for human approval");
                archDecision = await _decisionGate.WaitForDecisionAsync(archDecision.Id, ct);
            }

            if (archDecision.Status == DecisionStatus.Rejected)
            {
                Logger.LogWarning("Architecture decision REJECTED: {Feedback}", archDecision.HumanFeedback);
                // Store feedback for potential rework in the gate revision loop below
                await RememberAsync(MemoryType.Decision,
                    "Architecture decision rejected",
                    archDecision.HumanFeedback ?? "No feedback provided", ct);
            }
        }
        try { if (assessStepId is not null) _taskTracker.CompleteStep(assessStepId); } catch { }

        } // end else (fresh AI work, not resuming from gate)

        // Commit document to PR so reviewers can see it before the gate
        if (architectureDoc is not null && !pr.IsMerged)
        {
            UpdateStatus(AgentStatus.Working, "Committing Architecture.md for review");
            string? commitStepId = null;
            try { commitStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Commit Architecture.md", "Committing architecture document to PR"); } catch { }
            await _prWorkflow.CommitDocumentToPRAsync(
                pr, "Architecture.md", architectureDoc,
                $"Add system architecture for {directive.Title}", ct);
            try { if (commitStepId is not null) _taskTracker.CompleteStep(commitStepId); } catch { }
        }

        // === Gate: ArchitectureDesign — human reviews architecture before merge ===
        if (gateStatus != GateStatus.Approved)
        {
            string? gateStepId = null;
            try { gateStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Human gate review", $"Awaiting human approval on PR #{pr.Number}"); } catch { }
            try { if (gateStepId is not null) _taskTracker.SetStepWaiting(gateStepId); } catch { }
            var maxRevisions = 3;
            for (var revision = 0; revision < maxRevisions; revision++)
            {
                if (_gateCheck.RequiresHuman(GateIds.ArchitectureDesign))
                    UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{pr.Number}");
                var gateWait = await _gateCheck.WaitForGateAsync(
                    GateIds.ArchitectureDesign,
                    "Architecture.md ready for human review before merge",
                    pr.Number, ct: ct);

                if (!gateWait.WasRejected)
                    break;

                Logger.LogInformation("Architecture gate rejected on PR #{Number}: {Feedback}", pr.Number, gateWait.Feedback);
                LogActivity("task", $"📝 Revising architecture based on feedback: {gateWait.Feedback}");
                UpdateStatus(AgentStatus.Working, $"Revising Architecture.md (attempt {revision + 2})");

                var revised = await ReviseArchitectureAsync(directive, gateWait.Feedback!, ct);
                if (revised is not null && !pr.IsMerged)
                {
                    await _prWorkflow.CommitDocumentToPRAsync(
                        pr, "Architecture.md", revised,
                        $"Revise architecture based on reviewer feedback (attempt {revision + 2})", ct);
                }
                await ResetGateLabelsAsync(pr.Number, ct);
                await _github.AddPullRequestCommentAsync(pr.Number,
                    $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated Architecture.md.", ct);
            }
            try { if (gateStepId is not null) _taskTracker.CompleteStep(gateStepId); } catch { }
        }

        // Merge after gate approval (skip if PR already merged)
        string? mergeStepId = null;
        if (!pr.IsMerged)
        {
            UpdateStatus(AgentStatus.Working, "Merging Architecture.md PR");
            try { mergeStepId = _taskTracker.BeginStep(Identity.Id, directive.TaskId, "Merge PR", "Merging Architecture.md PR"); } catch { }
            await _prWorkflow.MergeDocumentPRAsync(
                pr, Identity.DisplayName, "Architecture.md", ct);
            try { if (mergeStepId is not null) _taskTracker.CompleteStep(mergeStepId); } catch { }
        }

        Logger.LogInformation("Architecture.md PR created and merged for task {TaskId}", directive.TaskId);
        LogActivity("task", $"✅ Architecture.md merged: {directive.Title}");
        await RememberAsync(MemoryType.Action,
            $"Created and merged Architecture.md for '{directive.Title}'",
            TruncateForMemory(architectureDoc), ct);

        // Explicitly close the related issue
        if (relatedIssue.HasValue)
        {
            try
            {
                await _github.CloseIssueAsync(relatedIssue.Value, ct);
                Logger.LogInformation("Closed related issue #{IssueNumber}", relatedIssue.Value);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to close issue #{IssueNumber}", relatedIssue.Value);
            }
        }

        // BUG FIX: Removed unnecessary GitHub Issue creation for PE notification.
        // The Architect was creating an issue titled "Software Engineer: Question from Architect"
        // to tell the PE that Architecture.md was ready. This was wrong because: (a) it's not a
        // question, (b) the PE should be notified via message bus not a GitHub Issue, and (c) the
        // issue was never closed, cluttering the issue tracker. The StatusUpdateMessage broadcast
        // below (ArchitectureComplete) is the correct notification mechanism.

        // Notify all agents via message bus that architecture is complete
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

                var pr = await _github.GetPullRequestAsync(prNumber, ct);
                if (pr is null)
                    continue;

                // Skip TestEngineer PRs — architecture review not needed for test suites
                var authorRole = PullRequestWorkflow.DetectAuthorRole(pr.Title);
                if (authorRole.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
                    || authorRole.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                // Skip PRs already architect-approved (Phase 1 complete)
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                // Dedup across restarts: check GitHub comments to see if we already reviewed
                // BUT always process force-approval PRs regardless
                if (!_forceApprovalPrs.Contains(prNumber) &&
                    !await _prWorkflow.NeedsReviewFromAsync(prNumber, "Architect", ct))
                {
                    _reviewedPrNumbers.Add(prNumber);
                    continue;
                }

                Logger.LogInformation("Reviewing PR #{Number} for architectural alignment: {Title}",
                    pr.Number, pr.Title);

                // Force-approve after max rework cycles — only if Architect is a required reviewer
                StructuredReviewResult reviewResult;
                if (_forceApprovalPrs.Contains(prNumber))
                {
                    _forceApprovalPrs.Remove(prNumber);
                    var requiredReviewers = PullRequestWorkflow.GetRequiredReviewers(authorRole);
                    if (!requiredReviewers.Any(r => r.Contains("Architect", StringComparison.OrdinalIgnoreCase)))
                    {
                        Logger.LogInformation("Architect is not a required reviewer for PR #{Number} — skipping force-approval", prNumber);
                        _reviewedPrNumbers.Add(prNumber);
                        continue;
                    }
                    reviewResult = new StructuredReviewResult
                    {
                        Verdict = "APPROVED",
                        Summary = "Force-approving after maximum rework cycles reached. " +
                            "The engineer has made best-effort improvements across multiple iterations.",
                        RiskLevel = ReviewRiskLevel.Low,
                        Comments = []
                    };
                }
                else
                {
                    var hasNewCommits = await _prWorkflow.HasNewCommitsSinceReviewAsync(prNumber, "Architect", ct);
                    if (!hasNewCommits)
                    {
                        Logger.LogWarning("No new commits on PR #{Number} since last Architect review — approving to unblock", prNumber);
                        reviewResult = new StructuredReviewResult
                        {
                            Verdict = "APPROVED",
                            Summary = "No new code commits detected since last review. " +
                                "The author marked the PR as ready but did not push file changes. " +
                                "Approving to avoid blocking progress — previous feedback still applies.",
                            RiskLevel = ReviewRiskLevel.Low,
                            Comments = []
                        };
                    }
                    else
                    {
                        reviewResult = await EvaluateArchitecturalAlignmentAsync(pr, ct);
                    }
                }

                var verdict = reviewResult.Verdict;
                var reasoning = reviewResult.Summary;
                var riskSuffix = _config.Review.EnableRiskAssessment
                    ? $"\n\n⚠️ **Risk Level**: {reviewResult.RiskLevel.ToString().ToUpperInvariant()}"
                    : "";

                // Check human risk gate BEFORE adding phase-transition labels
                if (verdict == "APPROVED" && _config.Review.MinRiskLevelForHumanReview != ReviewRiskLevel.None
                    && reviewResult.RiskLevel >= _config.Review.MinRiskLevelForHumanReview)
                {
                    Logger.LogInformation(
                        "PR #{Number} risk level {Risk} >= gate threshold {Threshold} — adding human-review-required label",
                        pr.Number, reviewResult.RiskLevel, _config.Review.MinRiskLevelForHumanReview);

                    var gateLabels = pr.Labels
                        .Append(PullRequestWorkflow.Labels.HumanReviewRequired)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    await _github.UpdatePullRequestAsync(pr.Number, labels: gateLabels, ct: ct);
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"**[Architect] APPROVED** (pending human review)\n\n" +
                        $"🏗️ Architecture Review: {reasoning}{riskSuffix}\n\n" +
                        $"⏳ **Human review required** — risk level `{reviewResult.RiskLevel}` meets or exceeds " +
                        $"the configured threshold `{_config.Review.MinRiskLevelForHumanReview}`. " +
                        $"Remove the `{PullRequestWorkflow.Labels.HumanReviewRequired}` label to proceed.", ct);

                    // Submit inline comments as a review even though we're gating
                    if (reviewResult.Comments.Count > 0 && _config.Review.EnableInlineComments)
                    {
                        await SubmitInlineReviewCommentsAsync(pr.Number, reviewResult, "COMMENT", ct);
                    }

                    LogActivity("task", $"⏳ PR #{pr.Number} approved but held for human review (risk: {reviewResult.RiskLevel})");
                    _reviewedPrNumbers.Add(pr.Number);
                    continue; // Don't add architect-approved label yet
                }

                if (verdict == "APPROVED")
                {
                    // Phase 1 complete: Architect approved → add architect-approved label, do NOT merge.
                    // The TE will pick up the PR next (Phase 2), then PM reviews last (Phase 3).
                    var approvalComment = $"**[Architect] APPROVED**\n\n🏗️ Architecture Review: {reasoning}";
                    await _github.AddPullRequestCommentAsync(pr.Number, approvalComment, ct);

                    // Submit inline comments as a GitHub review
                    if (reviewResult.Comments.Count > 0 && _config.Review.EnableInlineComments)
                    {
                        await SubmitInlineReviewCommentsAsync(pr.Number, reviewResult, "APPROVE", ct);
                    }

                    Logger.LogInformation("Architect approved PR #{Number}", pr.Number);

                    // Add architect-approved label
                    var updatedLabels = pr.Labels
                        .Where(l => !string.Equals(l, PullRequestWorkflow.Labels.ReadyForReview, StringComparison.OrdinalIgnoreCase))
                        .Append(PullRequestWorkflow.Labels.ArchitectApproved)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);

                    LogActivity("task", $"✅ Approved PR #{pr.Number}: {pr.Title} — TE testing next");
                    await RememberAsync(MemoryType.Decision,
                        $"Architecture review approved PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reasoning), ct);

                    // Notify TE via bus that PR is ready for testing
                    await _messageBus.PublishAsync(new StatusUpdateMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "StatusUpdate",
                        NewStatus = AgentStatus.Working,
                        CurrentTask = $"PR #{pr.Number} architect-approved — ready for TE testing",
                        Details = $"PR #{pr.Number}: {pr.Title} has passed architecture review"
                    }, ct);
                }
                else if (verdict == "REWORK")
                {
                    await _prWorkflow.RequestChangesAsync(
                        pr.Number, "Architect", $"🏗️ Architecture Review: {reasoning}{riskSuffix}", ct);

                    // Submit inline comments as a REQUEST_CHANGES review
                    if (reviewResult.Comments.Count > 0 && _config.Review.EnableInlineComments)
                    {
                        await SubmitInlineReviewCommentsAsync(pr.Number, reviewResult, "REQUEST_CHANGES", ct);
                    }

                    Logger.LogInformation("Architect requested changes on PR #{Number}", pr.Number);
                    LogActivity("task", $"❌ Requested changes on PR #{pr.Number}: {pr.Title}");
                    await RememberAsync(MemoryType.Decision,
                        $"Architecture review requested changes on PR #{pr.Number}: {pr.Title}",
                        TruncateForMemory(reasoning), ct);

                    // Notify the PR author via bus so they can start rework
                    await _messageBus.PublishAsync(new ChangesRequestedMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ChangesRequested",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewerAgent = "Architect",
                        Feedback = reasoning
                    }, ct);
                }
                else
                {
                    // Fallback: post as informational comment if AI didn't produce a clear verdict
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"🏗️ **Architecture Review (Advisory):**\n\n{reasoning}", ct);
                    Logger.LogWarning("Architect review for PR #{Number} produced unclear verdict: {Verdict}",
                        pr.Number, verdict);
                }

                _reviewedPrNumbers.Add(pr.Number);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to review PRs for architectural alignment");
        }
    }

    /// <summary>
    /// Submits inline review comments as a GitHub PR review.
    /// Falls back gracefully if the GitHub API call fails.
    /// </summary>
    private async Task SubmitInlineReviewCommentsAsync(
        int prNumber, StructuredReviewResult reviewResult, string eventType, CancellationToken ct)
    {
        try
        {
            var maxComments = _config.Review.MaxInlineCommentsPerReview;
            var comments = reviewResult.Comments
                .Take(maxComments)
                .ToList();

            if (comments.Count == 0) return;

            var reviewBody = $"🏗️ **Architect Inline Review** — {reviewResult.Verdict}\n\n" +
                $"{reviewResult.Summary}\n\n" +
                $"_{comments.Count} inline comment(s) below_";

            await _github.CreatePullRequestReviewWithCommentsAsync(
                prNumber, reviewBody, eventType, comments, ct: ct);

            Logger.LogInformation(
                "Submitted {Count} inline review comments on PR #{Number}",
                comments.Count, prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to submit inline review comments on PR #{Number} — review body was still posted",
                prNumber);
        }
    }

    private async Task<StructuredReviewResult> EvaluateArchitecturalAlignmentAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var architectureDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);

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
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for architecture review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            var codeContext = await _prWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            // Get screenshot images for vision-based review
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
                Logger.LogDebug(ex, "Could not fetch screenshots for PR #{Number}", pr.Number);
            }

            var hasScreenshots = screenshotImages.Count > 0 || !string.IsNullOrEmpty(screenshotContext);

            var history = CreateChatHistory();
            var reviewSysVars = new Dictionary<string, string>
            {
                ["screenshot_instructions"] = hasScreenshots
                    ? "ALSO CHECK: Screenshots are provided — you can SEE them embedded in this message. " +
                      "Verify the app renders correctly without errors.\n" +
                      "  - Error pages, unhandled exceptions, blank screens visible in screenshots = REWORK.\n" +
                      "  - The visual output should match what the PR description says it implements.\n" +
                      "  - EXCEPTION: If the PR explicitly states that data files (e.g., data.json) are NOT part of this task " +
                      "and will be provided by a separate task, a 'file not found' error for data files is acceptable — " +
                      "do NOT flag it as REWORK. However, if the PR INCLUDES a data.json file and the screenshot " +
                      "shows a schema/validation error, that IS a REWORK issue (the file doesn't match the data model).\n"
                    : ""
            };

            var useInlineComments = _config.Review.EnableInlineComments;
            var useRiskAssessment = _config.Review.EnableRiskAssessment;

            var reviewSys = await _promptService.RenderAsync("architect/pr-review-system", reviewSysVars, ct)
                ?? "You are a software architect reviewing a PR for architecture alignment.\n\n" +
                   "SCOPE: This PR is ONE task. Review only the parts it touches against the architecture doc.\n\n" +
                   "CHECK: component boundaries, folder structure, tech stack compliance, architectural patterns.\n" +
                   "ALSO CHECK FILE COMPLETENESS: Compare the actual files in the PR against the acceptance criteria " +
                   "and file plan in the linked issue. If the acceptance criteria list specific files or components " +
                   "that should be created (e.g., Models, Interfaces, Layouts, CSS, config files) and those files " +
                   "are MISSING from the PR, this is a REWORK issue. A PR that delivers only 2 of 15 expected files " +
                   "is incomplete regardless of whether those 2 files are architecturally correct.\n" +
                   (hasScreenshots
                       ? "ALSO CHECK: Screenshots are provided — you can SEE them embedded in this message. " +
                         "Verify the app renders correctly without errors.\n" +
                         "  - Error pages, unhandled exceptions, blank screens visible in screenshots = REWORK.\n" +
                         "  - The visual output should match what the PR description says it implements.\n" +
                         "  - EXCEPTION: If the PR states data files are NOT part of this task, a 'file not found' " +
                         "error for data files is acceptable. But if the PR INCLUDES data files and the screenshot " +
                         "shows a schema/validation error, that IS a REWORK issue.\n"
                       : "") +
                   "IGNORE: code quality, null checks, naming, tests.\n\n" +
                   "IMPORTANT: Code may appear truncated in your review context due to length limits — " +
                   "this is a tooling limitation, NOT a code defect. Do NOT flag truncated code.\n\n" +
                   "Only request REWORK for real architectural violations (wrong boundaries, wrong tech stack, " +
                   "wrong patterns), MISSING files/components listed in acceptance criteria, " +
                   "OR runtime errors visible in screenshots. Minor issues → APPROVE.\n\n" +
                   "RESPONSE FORMAT — you MUST respond with ONLY a JSON object, nothing else.\n" +
                   "Do NOT include any text before or after the JSON. Do NOT wrap in markdown fences.\n" +
                   "The JSON schema is:\n" +
                   "- \"verdict\": string, either \"APPROVED\" or \"REWORK\"\n" +
                   "- \"summary\": string, brief 1-2 sentence assessment\n" +
                   (useRiskAssessment ? "- \"riskLevel\": string, either \"LOW\", \"MEDIUM\", or \"HIGH\"\n" : "") +
                   (useInlineComments
                       ? "- \"comments\": array of objects with:\n" +
                         "  - \"file\": string, relative file path (e.g. \"src/Services/MyService.cs\")\n" +
                         "  - \"line\": integer, line number in the new file where the comment applies\n" +
                         "  - \"priority\": string, one of \"🔴 Critical\", \"🟠 Important\", \"🟡 Suggestion\", \"🟢 Nit\"\n" +
                         "  - \"body\": string, description of the issue\n"
                       : "") +
                   "\nExample response:\n" +
                   "{\"verdict\":\"REWORK\",\"summary\":\"CSS class names don't match architecture spec.\"" +
                   (useRiskAssessment ? ",\"riskLevel\":\"MEDIUM\"" : "") +
                   (useInlineComments ? ",\"comments\":[{\"file\":\"wwwroot/css/app.css\",\"line\":15,\"priority\":\"🔴 Critical\",\"body\":\"Uses .cur instead of .apr per architecture\"}]" : "") +
                   "}\n\n" +
                   "PRIORITY GUIDE: 🔴 Critical = must fix (breaks architecture, missing files, security). " +
                   "🟠 Important = should fix (wrong patterns, significant gaps). " +
                   "🟡 Suggestion = worth considering. 🟢 Nit = minor.\n" +
                   (useRiskAssessment
                       ? "RISK GUIDE: LOW = cosmetic/minor. MEDIUM = modifies shared models/APIs. HIGH = breaking changes, security, major rework.\n"
                       : "") +
                   "\nYour entire response must be parseable as JSON. Start with { and end with }.";
            history.AddSystemMessage(reviewSys);

            var userMessageText =
                $"## Architecture Document\n{architectureDoc}\n\n" +
                $"## PM Specification\n{pmSpec}\n\n" +
                issueContext +
                $"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n\n" +
                codeContext;

            // Add screenshots as vision content if available
            if (screenshotImages.Count > 0)
            {
                var items = new ChatMessageContentItemCollection();
                var screenshotIntro = "\n\n## 📸 Application Screenshots\n" +
                    "LOOK AT EACH IMAGE for errors, blank screens, or broken UI.\n\n";
                for (var i = 0; i < screenshotImages.Count; i++)
                    screenshotIntro += $"Screenshot {i + 1}: {screenshotImages[i].Description}\n";

                items.Add(new TextContent(userMessageText + screenshotIntro));

                foreach (var img in screenshotImages)
                {
                    items.Add(new ImageContent(img.ImageBytes, img.MimeType)
                    {
                        ModelId = $"screenshot: {img.Description}"
                    });
                }

                history.AddUserMessage(items);
            }
            else
            {
                history.AddUserMessage(userMessageText +
                    (string.IsNullOrEmpty(screenshotContext) ? "" : $"\n\n{screenshotContext}"));
            }

            var response = await chat.GetChatMessageContentAsync(
                history, cancellationToken: ct);

            var text = response.Content?.Trim() ?? "";

            // Try to parse as structured JSON first
            var structuredResult = TryParseStructuredReview(text);
            if (structuredResult != null)
            {
                Logger.LogInformation(
                    "Architect review of PR #{Number}: {Verdict} (risk={Risk}, {CommentCount} inline comments)",
                    pr.Number, structuredResult.Verdict, structuredResult.RiskLevel, structuredResult.Comments.Count);
                return structuredResult;
            }

            // Fallback: parse as plain text (original format)
            Logger.LogWarning("Architect AI didn't return valid JSON for PR #{Number}, falling back to text parsing. Raw response (first 500 chars): {RawResponse}",
                pr.Number, text.Length > 500 ? text[..500] : text);
            text = PullRequestWorkflow.StripReviewPreamble(text);

            var firstLine = text.Split('\n', 2)[0].Trim();
            string verdict;
            string reasoning;

            if (firstLine.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase))
            {
                verdict = "APPROVED";
                reasoning = text.Length > firstLine.Length
                    ? text[(firstLine.Length + 1)..].Trim()
                    : firstLine;
            }
            else if (firstLine.StartsWith("REWORK", StringComparison.OrdinalIgnoreCase))
            {
                verdict = "REWORK";
                reasoning = text.Length > firstLine.Length
                    ? text[(firstLine.Length + 1)..].Trim()
                    : firstLine;
            }
            else
            {
                // AI didn't follow format — default to REWORK (fail closed)
                verdict = text.Contains("APPROVED", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("REWORK", StringComparison.OrdinalIgnoreCase)
                    ? "APPROVED" : "REWORK";
                reasoning = text;
                Logger.LogWarning("Architect AI didn't start with APPROVED/REWORK, inferred {Verdict}", verdict);
            }

            reasoning = PullRequestWorkflow.StripReviewPreamble(reasoning);

            return new StructuredReviewResult
            {
                Verdict = verdict,
                Summary = reasoning,
                RiskLevel = ReviewRiskLevel.Medium, // Unknown risk from text fallback
                Comments = []
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} for architecture alignment",
                pr.Number);
            return new StructuredReviewResult
            {
                Verdict = "UNKNOWN",
                Summary = "Architecture review failed due to an internal error.",
                RiskLevel = ReviewRiskLevel.Medium,
                Comments = []
            };
        }
    }

    /// <summary>
    /// Attempts to parse AI response as structured JSON review result.
    /// Returns null if parsing fails (caller should fall back to text parsing).
    /// </summary>
    private static StructuredReviewResult? TryParseStructuredReview(string text)
    {
        try
        {
            var json = text.Trim();

            // Strip markdown fences if present (```json ... ``` or ``` ... ```)
            if (json.Contains("```"))
            {
                // Find JSON content between fences
                var fenceStart = json.IndexOf("```");
                var afterFence = json.IndexOf('\n', fenceStart);
                if (afterFence >= 0)
                {
                    var fenceEnd = json.IndexOf("```", afterFence);
                    if (fenceEnd > afterFence)
                    {
                        json = json[(afterFence + 1)..fenceEnd].Trim();
                    }
                    else
                    {
                        json = json[(afterFence + 1)..].Trim();
                    }
                }
            }

            // Try to find JSON object boundaries — handle nested braces properly
            var startBrace = json.IndexOf('{');
            if (startBrace < 0) return null;

            // Find matching closing brace (handle nesting)
            var depth = 0;
            var endBrace = -1;
            var inString = false;
            var escape = false;
            for (var i = startBrace; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { endBrace = i; break; } }
            }
            if (endBrace < 0) return null;
            json = json[startBrace..(endBrace + 1)];

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdict = root.TryGetProperty("verdict", out var v)
                ? v.GetString()?.Trim().ToUpperInvariant() ?? "REWORK"
                : "REWORK";

            // Normalize verdict
            if (verdict != "APPROVED" && verdict != "REWORK")
                verdict = verdict.Contains("APPROV") ? "APPROVED" : "REWORK";

            var summary = root.TryGetProperty("summary", out var s)
                ? s.GetString()?.Trim() ?? ""
                : "";

            var riskLevel = ReviewRiskLevel.Medium;
            if (root.TryGetProperty("riskLevel", out var r))
            {
                var riskStr = r.GetString()?.Trim().ToUpperInvariant() ?? "";
                riskLevel = riskStr switch
                {
                    "LOW" => ReviewRiskLevel.Low,
                    "HIGH" => ReviewRiskLevel.High,
                    _ => ReviewRiskLevel.Medium
                };
            }

            var comments = new List<InlineReviewComment>();
            if (root.TryGetProperty("comments", out var commentsArr)
                && commentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in commentsArr.EnumerateArray())
                {
                    var file = c.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var line = c.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var priority = c.TryGetProperty("priority", out var p) ? p.GetString() ?? "" : "";
                    var body = c.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(body) && line > 0)
                    {
                        comments.Add(new InlineReviewComment
                        {
                            FilePath = file,
                            Line = line,
                            Body = $"{priority}: {body}".Trim()
                        });
                    }
                }
            }

            return new StructuredReviewResult
            {
                Verdict = verdict,
                Summary = summary,
                RiskLevel = riskLevel,
                Comments = comments
            };
        }
        catch
        {
            return null;
        }
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
    /// Read visual design reference files from the repository for architecture decisions.
    /// </summary>
    private async Task<string?> ReadDesignReferencesAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _github.GetRepositoryTreeAsync("main", ct);
            var designKeywords = new[] { "design", "mockup", "mock", "wireframe", "prototype", "concept", "reference" };

            var htmlDesignFiles = tree
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (ext != ".html" && ext != ".htm") return false;
                    // HTML in root or with design keywords
                    return !f.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                           designKeywords.Any(k => name.Contains(k));
                })
                .ToList();

            if (htmlDesignFiles.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var file in htmlDesignFiles)
            {
                var content = await _github.GetFileContentAsync(file, ct: ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### Design File: `{file}`");
                sb.AppendLine();
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 6000 ? content[..6000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read design reference files for architecture");
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
