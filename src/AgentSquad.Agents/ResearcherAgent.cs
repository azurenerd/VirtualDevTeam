using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Reasoning;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

public class ResearcherAgent : AgentBase
{
    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;
    private readonly PlaywrightRunner? _playwrightRunner;
    private readonly IGateCheckService _gateCheck;
    private readonly SelfAssessmentService _selfAssessment;
    private readonly IAgentReasoningLog _reasoningLog;

    private readonly Queue<ResearchDirective> _researchQueue = new();
    private readonly List<IDisposable> _subscriptions = new();
    private string? _lastDesignSection; // Cached from ScanForDesignReferencesAsync

    public ResearcherAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        SelfAssessmentService selfAssessment,
        IAgentReasoningLog reasoningLog,
        ILogger<ResearcherAgent> logger,
        PlaywrightRunner? playwrightRunner = null)
        : base(identity, logger, memoryStore)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _selfAssessment = selfAssessment ?? throw new ArgumentNullException(nameof(selfAssessment));
        _reasoningLog = reasoningLog ?? throw new ArgumentNullException(nameof(reasoningLog));
        _playwrightRunner = playwrightRunner;
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<TaskAssignmentMessage>(
            Identity.Id, HandleTaskAssignmentAsync));

        Logger.LogInformation("Researcher agent initialized, awaiting research directives");
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for research directives from PM");

        while (!ct.IsCancellationRequested)
        {
            ResearchDirective? currentDirective = null;
            try
            {
                if (_researchQueue.TryDequeue(out var directive))
                {
                    currentDirective = directive;

                    // Idempotency: check if this topic was already researched
                    var existingDoc = await _projectFiles.GetResearchDocAsync(ct);
                    if (existingDoc.Contains($"## {directive.Topic}", StringComparison.OrdinalIgnoreCase) ||
                        existingDoc.Contains($"# {directive.Topic}", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation(
                            "Research for '{Topic}' already exists in Research.md, skipping",
                            directive.Topic);
                        currentDirective = null; // Don't re-enqueue on success

                        // Still signal completion so downstream agents aren't stuck
                        await _messageBus.PublishAsync(new StatusUpdateMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = "*",
                            MessageType = "ResearchComplete",
                            NewStatus = AgentStatus.Idle,
                            Details = $"Research already complete for: {directive.Topic}"
                        }, ct);
                    }
                    else
                    {
                        // Use the issue number passed directly from the PM's TaskAssignment
                        // instead of fragile title-based searching that could match wrong issues
                        int? relatedIssue = directive.IssueNumber;
                        if (!relatedIssue.HasValue)
                        {
                            // Fallback: search by title if PM didn't pass the number
                            try
                            {
                                var issues = await _github.GetOpenIssuesAsync(ct);
                                var matchingIssue = issues.FirstOrDefault(i =>
                                    i.Title.Contains("Research", StringComparison.OrdinalIgnoreCase) &&
                                    i.Title.Contains(directive.Topic, StringComparison.OrdinalIgnoreCase));
                                relatedIssue = matchingIssue?.Number;
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Could not find related issue for research topic");
                            }
                        }

                        // Create the PR upfront so it's visible immediately
                        UpdateStatus(AgentStatus.Working, "Creating PR for Research.md");
                        var pr = await _prWorkflow.OpenDocumentPRAsync(
                            Identity.DisplayName,
                            "Research.md",
                            $"Research findings for {directive.Topic}",
                            $"Research findings covering: {directive.Topic}",
                            relatedIssue,
                            ct);

                        // Resume-aware: check if gate is already pending/approved from a prior run
                        var gateStatus = await _gateCheck.GetGateStatusAsync(
                            GateIds.ResearchFindings, pr.Number, ct);

                        string? updatedDoc = null;

                        if (gateStatus == GateStatus.Approved)
                        {
                            // Gate was already approved (PR may already be merged)
                            Logger.LogInformation("Research gate already approved on PR #{Number}, skipping research", pr.Number);
                            LogActivity("task", $"⏩ Research gate already approved on PR #{pr.Number}, resuming");
                        }
                        else if (gateStatus == GateStatus.AwaitingApproval)
                        {
                            // Gate is waiting for human — skip AI work, go straight to waiting
                            Logger.LogInformation("Research gate already pending on PR #{Number}, skipping to gate wait", pr.Number);
                            LogActivity("task", $"⏩ Research gate already pending on PR #{pr.Number}, resuming wait");
                        }
                        else
                        {
                            // Normal path: do the AI research work
                            UpdateStatus(AgentStatus.Working, $"Researching: {directive.Topic}");
                            Logger.LogInformation("Starting research on: {Topic}", directive.Topic);
                            LogActivity("task", $"🔬 Starting research on: {directive.Topic}");

                            var research = await ConductResearchAsync(directive, ct);

                            // Build the full Research.md content (design section was cached during research)
                            var existingContent = await _projectFiles.GetResearchDocAsync(ct);
                            var newSection = FormatResearchSection(directive.Topic, research);
                            updatedDoc = existingContent.TrimEnd() + "\n\n" + newSection;
                            if (!string.IsNullOrWhiteSpace(_lastDesignSection))
                                updatedDoc += "\n\n" + _lastDesignSection;
                            updatedDoc += "\n";
                        }

                        // Commit document to PR so reviewers can see it before the gate
                        if (updatedDoc is not null && !pr.IsMerged)
                        {
                            UpdateStatus(AgentStatus.Working, "Committing Research.md for review");
                            await _prWorkflow.CommitDocumentToPRAsync(
                                pr, "Research.md", updatedDoc,
                                $"Add research findings: {directive.Topic}", ct);
                        }

                        // === Gate: ResearchFindings — human reviews before merge ===
                        if (gateStatus != GateStatus.Approved)
                        {
                            var maxRevisions = 3;
                            for (var revision = 0; revision < maxRevisions; revision++)
                            {
                                if (_gateCheck.RequiresHuman(GateIds.ResearchFindings))
                                    UpdateStatus(AgentStatus.Working, $"⏳ Awaiting human approval on PR #{pr.Number}");
                                var gateWait = await _gateCheck.WaitForGateAsync(
                                    GateIds.ResearchFindings,
                                    $"Research findings for '{directive.Topic}' ready for review",
                                    pr.Number, ct: ct);

                                if (!gateWait.WasRejected)
                                    break;

                                // Human requested changes — revise the research
                                Logger.LogInformation(
                                    "Research gate rejected on PR #{Number}, revision {Rev}: {Feedback}",
                                    pr.Number, revision + 1, gateWait.Feedback);
                                LogActivity("task", $"📝 Revising research based on feedback: {gateWait.Feedback}");
                                UpdateStatus(AgentStatus.Working, $"Revising research (attempt {revision + 2})");

                                var revisedDoc = await ReviseResearchAsync(
                                    directive, gateWait.Feedback!, ct);

                                if (revisedDoc is not null && !pr.IsMerged)
                                {
                                    await _prWorkflow.CommitDocumentToPRAsync(
                                        pr, "Research.md", revisedDoc,
                                        $"Revise research based on reviewer feedback (attempt {revision + 2})", ct);
                                }

                                // Remove human-approved label if present (reset the gate)
                                var currentPr = await _github.GetPullRequestAsync(pr.Number, ct);
                                if (currentPr is not null)
                                {
                                    var labels = currentPr.Labels?.ToList() ?? [];
                                    labels.Remove("human-approved");
                                    if (!labels.Contains("awaiting-human-review"))
                                        labels.Add("awaiting-human-review");
                                    await _github.UpdatePullRequestAsync(pr.Number, labels: labels.ToArray(), ct: ct);
                                }

                                await _github.AddPullRequestCommentAsync(pr.Number,
                                    $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated Research.md.", ct);
                            }
                        }

                        // Merge after gate approval (skip if PR already merged)
                        if (!pr.IsMerged)
                        {
                            UpdateStatus(AgentStatus.Working, "Merging Research.md PR");
                            await _prWorkflow.MergeDocumentPRAsync(
                                pr, Identity.DisplayName, "Research.md", ct);
                        }

                        Logger.LogInformation("Research.md PR created and merged for '{Topic}'", directive.Topic);
                        LogActivity("task", $"✅ Research.md merged: {directive.Topic}");
                        await RememberAsync(MemoryType.Action,
                            $"Completed research and merged Research.md for '{directive.Topic}'",
                            $"Research on '{directive.Topic}' completed and merged", ct);
                        currentDirective = null; // Don't re-enqueue on success

                        // Explicitly close the related issue (don't rely on "Closes #X" in PR body)
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

                        await _messageBus.PublishAsync(new StatusUpdateMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = "*",
                            MessageType = "ResearchComplete",
                            NewStatus = AgentStatus.Online,
                            CurrentTask = directive.TaskId,
                            Details = $"Research complete: {directive.Topic}"
                        }, ct);

                        Logger.LogInformation(
                            "Research complete for task {TaskId}: {Topic}",
                            directive.TaskId, directive.Topic);
                    }
                }
                else
                {
                    UpdateStatus(AgentStatus.Idle, "Waiting for research directives");
                    await RefreshDiagnosticWithMemoryAsync(ct);
                    await Task.Delay(5000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Research loop error, will retry after delay");
                RecordError($"Research error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                if (currentDirective is not null)
                {
                    Logger.LogInformation("Re-enqueueing failed research directive: {Topic}", currentDirective.Topic);
                    _researchQueue.Enqueue(currentDirective);
                }
                UpdateStatus(AgentStatus.Working, $"Recovering from error, will retry");
                try { await Task.Delay(15000, ct); } // Wait 15s before retry
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "Researcher loop exited");
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    #region Message Handlers

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Received research directive from {From}: {Title}",
            message.FromAgentId, message.Title);

        _researchQueue.Enqueue(new ResearchDirective
        {
            TaskId = message.TaskId,
            Topic = message.Title,
            Description = message.Description,
            IssueNumber = message.IssueNumber
        });

        return Task.CompletedTask;
    }

    #endregion

    #region Research Logic

    private async Task<ResearchResult> ConductResearchAsync(
        ResearchDirective directive, CancellationToken ct)
    {
        // Quick mode: produce a minimal 1-paragraph research summary for fast testing
        if (_config.Project.QuickDocumentCreation)
        {
            Logger.LogInformation("QuickDocumentCreation: producing minimal Research.md for '{Topic}'", directive.Topic);
            var qKernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var qChat = qKernel.GetRequiredService<IChatCompletionService>();
            var qHistory = new ChatHistory();
            qHistory.AddSystemMessage("You are a technical researcher. Produce a brief, 1-paragraph research summary.");
            qHistory.AddUserMessage(
                $"Project: {_config.Project.Description}\nTech Stack: {_config.Project.TechStack}\n" +
                $"Topic: {directive.Topic}\n\n" +
                "Write ONE concise paragraph summarizing the key technology recommendations for this project. " +
                "Be specific about libraries and patterns. Keep it under 150 words.");
            var qResponse = await qChat.GetChatMessageContentsAsync(qHistory, cancellationToken: ct);
            var quickText = string.Join("", qResponse.Select(r => r.Content ?? ""));
            return new ResearchResult
            {
                Summary = quickText,
                DetailedAnalysis = quickText,
                KeyFindings = new List<string> { quickText }
            };
        }

        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Scan for design reference files FIRST so we can include them in research context
        var designContext = await ScanForDesignReferencesAsync(ct);
        _lastDesignSection = designContext; // Cache for appending to Research.md later

        var history = new ChatHistory();
        var memoryContext = await GetMemoryContextAsync(ct: ct);

        var systemPrompt = "You are a senior technical researcher on a software development team. " +
            "Your job is to perform deep, thorough research on assigned topics and produce structured, " +
            "actionable findings that architects and engineers can build from directly. " +
            "Go beyond surface-level recommendations — provide specific tools, version numbers, " +
            "architecture patterns, trade-offs, and real-world considerations. " +
            "Focus on practical, opinionated recommendations backed by reasoning.\n\n" +
            $"IMPORTANT: The project's technology stack has already been decided: **{_config.Project.TechStack}**. " +
            "Your research MUST target this stack. Recommend libraries, patterns, and tools that are " +
            "native to or compatible with this stack. Do NOT recommend alternative tech stacks — " +
            "the decision is final." +
            (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}");

        // If we found design files, add them to system context so research covers UI implementation needs
        if (!string.IsNullOrWhiteSpace(designContext))
        {
            systemPrompt += "\n\n## VISUAL DESIGN REFERENCE\n" +
                "The repository contains visual design reference files that define the exact UI to be built. " +
                "Your research MUST include technology recommendations for implementing this specific design. " +
                "Consider: CSS layout techniques needed (Grid, Flexbox), SVG/charting libraries for any " +
                "visualizations, color theming approaches, responsive design strategies, and component " +
                "architecture that maps to the design's visual sections.\n\n" +
                designContext;
        }

        history.AddSystemMessage(systemPrompt);

        var useSinglePass = _config.CopilotCli.SinglePassMode;
        string synthesisContent;
        string detailedAnalysis;

        if (useSinglePass)
        {
            // Single-pass: one comprehensive prompt instead of 3 turns
            UpdateStatus(AgentStatus.Working, "Researching (single-pass)");
            history.AddUserMessage(
                $"Research the following topic for our software project.\n\n" +
                $"**Topic:** {directive.Topic}\n\n" +
                $"**Context:**\n{directive.Description}\n\n" +
                "Produce a comprehensive, structured research document with these sections:\n\n" +
                "1. **Executive Summary** — Concise overview of findings and primary recommendation.\n" +
                "2. **Key Findings** — Most important discoveries, one per bullet (prefixed with '- ').\n" +
                "3. **Recommended Technology Stack** — Specific tools, frameworks, libraries with versions. " +
                "Organize by layer (frontend, backend, database, infrastructure, testing).\n" +
                "4. **Architecture Recommendations** — Patterns, data flow, structural decisions.\n" +
                "5. **Security & Infrastructure** — Auth, hosting, deployment, operational concerns.\n" +
                "6. **Risks & Trade-offs** — Technical risks, bottlenecks, mitigation strategies.\n" +
                "7. **Open Questions** — Decisions needing stakeholder input.\n" +
                "8. **Implementation Recommendations** — Phasing, MVP scope, quick wins.\n\n" +
                "Use these exact section headers. Be specific, opinionated, and actionable. " +
                "Include version numbers, compatibility notes, and real-world considerations.");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            synthesisContent = response.Content ?? "";
            detailedAnalysis = synthesisContent;
        }
        else
        {
        // Turn 1: Break down the research topic into sub-questions
        UpdateStatus(AgentStatus.Working, "Researching (1/3): Identifying sub-questions");

        history.AddUserMessage(
            $"I need you to research the following topic for our software project.\n\n" +
            $"**Topic:** {directive.Topic}\n\n" +
            $"**Context:**\n{directive.Description}\n\n" +
            "Based on the context and any research guidance provided above, break this topic down " +
            "into 5-8 key sub-questions that need thorough investigation. " +
            "Prioritize them by impact on the project. " +
            "List them clearly, one per line, prefixed with a number.");

        var subQuestionsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(subQuestionsResponse.Content ?? "");

        Logger.LogDebug("Research sub-questions identified for {Topic}", directive.Topic);

        // Turn 2: Deep-dive analysis of each sub-question
        UpdateStatus(AgentStatus.Working, "Researching (2/3): Deep-dive analysis");
        history.AddUserMessage(
            "Now provide a detailed, in-depth analysis for each sub-question you identified. " +
            "For each one, cover:\n" +
            "- **Key findings** — What did you discover? Be specific.\n" +
            "- **Tools, libraries, or technologies** — Name specific packages with version numbers.\n" +
            "- **Trade-offs and alternatives** — What are the pros/cons? What did you consider and reject?\n" +
            "- **Concrete recommendations** — What should the team use and why?\n" +
            "- **Evidence and reasoning** — Why is this the right choice for this specific project?\n\n" +
            "Be thorough and specific. Include version numbers, compatibility notes, " +
            "community health indicators, and real-world considerations. " +
            "If relevant, mention what similar projects in the industry have chosen.");

        var analysisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(analysisResponse.Content ?? "");

        Logger.LogDebug("Detailed analysis complete for {Topic}", directive.Topic);

        // Turn 3: Synthesize into structured Research.md output
        UpdateStatus(AgentStatus.Working, "Researching (3/3): Synthesizing findings");
        history.AddUserMessage(
            "Now synthesize all your research into a comprehensive, structured document with these sections:\n\n" +
            "1. **Executive Summary** — A concise overview of findings and primary recommendation (3-5 sentences).\n" +
            "2. **Key Findings** — The most important discoveries, one per bullet (prefixed with '- ').\n" +
            "3. **Recommended Technology Stack** — Specific tools, frameworks, and libraries with version numbers. " +
            "Organize by layer (frontend, backend, database, infrastructure, testing, etc.).\n" +
            "4. **Architecture Recommendations** — Patterns, data flow, and structural decisions.\n" +
            "5. **Security & Infrastructure** — Auth, hosting, deployment, and operational concerns.\n" +
            "6. **Risks & Trade-offs** — Technical risks, potential bottlenecks, and mitigation strategies.\n" +
            "7. **Open Questions** — Decisions that need stakeholder input or further investigation.\n" +
            "8. **Implementation Recommendations** — Phasing, MVP scope, and quick wins.\n\n" +
            "Use these exact section headers. Be specific, opinionated, and actionable. " +
            "The Architect and Engineers will build directly from this document.");

        var synthesisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        synthesisContent = synthesisResponse.Content ?? "";
        detailedAnalysis = analysisResponse.Content ?? "";

        } // end else (multi-turn)

        // Self-assessment: assess and refine the research document
        _reasoningLog.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Generating,
            Phase = "Research",
            Summary = $"Research document generated for '{directive.Topic}'",
            Iteration = 0,
        });

        var criteria = AssessmentCriteria.GetForRole(Identity.Role);
        if (criteria is not null)
        {
            synthesisContent = await _selfAssessment.AssessAndRefineAsync(
                Identity.Id,
                Identity.DisplayName,
                Identity.Role,
                "Research",
                synthesisContent,
                criteria,
                $"Project: {_config.Project.Description}\nTech Stack: {_config.Project.TechStack}\nTopic: {directive.Topic}",
                chat,
                ct);
        }

        Logger.LogDebug("Research synthesis complete for {Topic}", directive.Topic);
        await RememberAsync(MemoryType.Decision,
            $"Technology evaluation decisions for '{directive.Topic}'",
            TruncateForMemory(synthesisContent), ct);

        return ParseResearchResult(synthesisContent, detailedAnalysis);
    }

    private static ResearchResult ParseResearchResult(string synthesis, string detailedAnalysis)
    {
        var summary = "";
        var keyFindings = new List<string>();
        var recommendedTools = new List<string>();
        var considerations = new List<string>();

        var currentSection = "";
        var lines = synthesis.Split('\n');
        var inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            // Track fenced code blocks — don't parse lines inside them as structure
            if (rawLine.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                // Append the fence line to the last item in the current list so code blocks stay intact
                AppendToLastItem(currentSection, rawLine, keyFindings, recommendedTools, considerations, ref summary);
                continue;
            }

            if (inCodeBlock)
            {
                // Inside a code block — append raw line (preserve indentation) to current item
                AppendToLastItem(currentSection, rawLine, keyFindings, recommendedTools, considerations, ref summary);
                continue;
            }

            var line = rawLine.Trim();
            var isHeader = line.StartsWith('#') || (line.StartsWith("**") && line.EndsWith("**") && !line.StartsWith("**-"));

            if (isHeader)
            {
                var lowerLine = line.ToLowerInvariant();
                if (lowerLine.Contains("summary") || lowerLine.Contains("executive summary"))
                {
                    currentSection = "summary";
                    continue;
                }
                if (lowerLine.Contains("key findings") || lowerLine.Contains("findings"))
                {
                    currentSection = "findings";
                    continue;
                }
                if (lowerLine.Contains("recommended tool") || lowerLine.Contains("technology stack")
                    || lowerLine.Contains("recommended tech"))
                {
                    currentSection = "tools";
                    continue;
                }
                if (lowerLine.Contains("architecture") && lowerLine.Contains("recommend"))
                {
                    currentSection = "tools"; // group with tech recommendations
                    continue;
                }
                if (lowerLine.Contains("risk") || lowerLine.Contains("trade-off")
                    || lowerLine.Contains("consideration") || lowerLine.Contains("security")
                    || lowerLine.Contains("open question"))
                {
                    currentSection = "considerations";
                    continue;
                }
                if (lowerLine.Contains("implementation") || lowerLine.Contains("phasing")
                    || lowerLine.Contains("mvp") || lowerLine.Contains("quick win"))
                {
                    currentSection = "findings"; // group implementation notes with findings
                    continue;
                }
                // Unknown header — keep current section
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Lines starting with bullet markers are new items; continuation lines append
            var isBulletStart = line.StartsWith("- ") || line.StartsWith("* ")
                || (line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.')
                || (line.Length > 3 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && line[2] == '.');
            var bulletContent = StripBulletPrefix(line);

            switch (currentSection)
            {
                case "summary":
                    summary = string.IsNullOrEmpty(summary)
                        ? bulletContent
                        : $"{summary} {bulletContent}";
                    break;
                case "findings":
                    if (isBulletStart || keyFindings.Count == 0)
                        keyFindings.Add(bulletContent);
                    else
                        keyFindings[^1] += " " + bulletContent;
                    break;
                case "tools":
                    if (isBulletStart || recommendedTools.Count == 0)
                        recommendedTools.Add(bulletContent);
                    else
                        recommendedTools[^1] += " " + bulletContent;
                    break;
                case "considerations":
                    if (isBulletStart || considerations.Count == 0)
                        considerations.Add(bulletContent);
                    else
                        considerations[^1] += " " + bulletContent;
                    break;
            }
        }

        return new ResearchResult
        {
            Summary = summary,
            KeyFindings = keyFindings,
            RecommendedTools = recommendedTools,
            Considerations = considerations,
            DetailedAnalysis = detailedAnalysis
        };
    }

    /// <summary>
    /// Appends a raw line to the last item in the current section's list.
    /// Used for code block lines that should stay attached to the preceding bullet.
    /// </summary>
    private static void AppendToLastItem(
        string section, string rawLine,
        List<string> findings, List<string> tools, List<string> considerations,
        ref string summary)
    {
        switch (section)
        {
            case "summary":
                summary += "\n" + rawLine;
                break;
            case "findings":
                if (findings.Count > 0) findings[^1] += "\n" + rawLine;
                break;
            case "tools":
                if (tools.Count > 0) tools[^1] += "\n" + rawLine;
                break;
            case "considerations":
                if (considerations.Count > 0) considerations[^1] += "\n" + rawLine;
                break;
        }
    }

    private static string StripBulletPrefix(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- "))
            return trimmed[2..].Trim();
        if (trimmed.StartsWith("* "))
            return trimmed[2..].Trim();
        if (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.')
            return trimmed[2..].Trim();
        if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) && trimmed[2] == '.')
            return trimmed[3..].Trim();
        return trimmed;
    }

    #endregion

    #region Document Management

    private async Task AppendToResearchDocAsync(
        string topic, ResearchResult result, CancellationToken ct)
    {
        try
        {
            var existingDoc = await _projectFiles.GetResearchDocAsync(ct);

            var newSection = FormatResearchSection(topic, result);
            var updatedDoc = existingDoc.TrimEnd() + "\n\n" + newSection + "\n";

            await _projectFiles.UpdateResearchDocAsync(updatedDoc, ct);

            Logger.LogInformation("Appended research section for '{Topic}' to Research.md", topic);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to append research for '{Topic}' to Research.md", topic);
            throw;
        }
    }

    /// <summary>
    /// Revise research based on human reviewer feedback. Reads the current Research.md,
    /// sends it along with the feedback to the AI, and returns the revised document.
    /// </summary>
    private async Task<string?> ReviseResearchAsync(
        ResearchDirective directive, string feedback, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var currentDoc = await _projectFiles.GetResearchDocAsync(ct);
        if (string.IsNullOrWhiteSpace(currentDoc))
        {
            Logger.LogWarning("No existing Research.md to revise");
            return null;
        }

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a senior technical researcher revising a research document based on human reviewer feedback. " +
            "Read the existing document and the reviewer's feedback carefully. " +
            "Make the specific changes requested while preserving the overall structure and any parts the reviewer didn't mention. " +
            $"The project's technology stack is: **{_config.Project.TechStack}**.");

        history.AddUserMessage(
            $"## Current Research.md:\n\n{currentDoc}\n\n" +
            $"## Reviewer Feedback:\n\n{feedback}\n\n" +
            "Please revise the Research.md document to address the reviewer's feedback. " +
            "Return the COMPLETE revised document (not just the changes).");

        var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
        var revisedContent = string.Join("", response.Select(r => r.Content ?? ""));

        if (string.IsNullOrWhiteSpace(revisedContent))
        {
            Logger.LogWarning("AI returned empty revision for Research.md");
            return null;
        }

        return revisedContent;
    }

    private static string FormatResearchSection(string topic, ResearchResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {topic}");
        sb.AppendLine();
        sb.AppendLine($"_Researched on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine();

        sb.AppendLine("### Summary");
        sb.AppendLine();
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        if (result.KeyFindings.Count > 0)
        {
            sb.AppendLine("### Key Findings");
            sb.AppendLine();
            foreach (var finding in result.KeyFindings)
                sb.AppendLine($"- {finding}");
            sb.AppendLine();
        }

        if (result.RecommendedTools.Count > 0)
        {
            sb.AppendLine("### Recommended Tools & Technologies");
            sb.AppendLine();
            foreach (var tool in result.RecommendedTools)
                sb.AppendLine($"- {tool}");
            sb.AppendLine();
        }

        if (result.Considerations.Count > 0)
        {
            sb.AppendLine("### Considerations & Risks");
            sb.AppendLine();
            foreach (var consideration in result.Considerations)
                sb.AppendLine($"- {consideration}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.DetailedAnalysis))
        {
            sb.AppendLine("### Detailed Analysis");
            sb.AppendLine();
            sb.AppendLine(result.DetailedAnalysis);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
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
    /// Scan the repository for visual design reference files (.html, .htm, .png, .fig, .sketch)
    /// and return a formatted section describing them for Research.md.
    /// </summary>
    private async Task<string?> ScanForDesignReferencesAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _github.GetRepositoryTreeAsync("main", ct);
            var designExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".html", ".htm"
            };
            var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"
            };
            var designKeywords = new[] { "design", "mockup", "mock", "wireframe", "prototype", "concept", "reference" };

            var designFiles = new List<(string path, string type)>();

            foreach (var filePath in tree)
            {
                var fileName = Path.GetFileName(filePath).ToLowerInvariant();
                var ext = Path.GetExtension(filePath).ToLowerInvariant();

                // Skip files deep in src/ or node_modules/
                if (filePath.Contains("node_modules") || filePath.Contains("wwwroot/lib"))
                    continue;

                var nameHasDesignKeyword = designKeywords.Any(k => fileName.Contains(k));

                if (designExtensions.Contains(ext) && nameHasDesignKeyword)
                    designFiles.Add((filePath, "html-design"));
                else if (imageExtensions.Contains(ext) && nameHasDesignKeyword)
                    designFiles.Add((filePath, "image-design"));
                else if (ext == ".html" && !filePath.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
                    designFiles.Add((filePath, "html-root")); // HTML in root is likely a design reference
            }

            if (designFiles.Count == 0)
                return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Visual Design References");
            sb.AppendLine();
            sb.AppendLine("The following design reference files were found in the repository. " +
                "These MUST be used as the canonical visual specification when building UI components.");
            sb.AppendLine();

            foreach (var (path, type) in designFiles)
            {
                sb.AppendLine($"### `{path}`");
                sb.AppendLine();

                if (type.StartsWith("html"))
                {
                    // Read HTML files to extract design details
                    var content = await _github.GetFileContentAsync(path, ct: ct);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // Extract key CSS patterns and layout structure
                        var cssClasses = ExtractCssPatterns(content);
                        var layoutDescription = ExtractLayoutStructure(content);

                        sb.AppendLine("**Type:** HTML Design Template");
                        sb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(layoutDescription))
                        {
                            sb.AppendLine("**Layout Structure:**");
                            sb.AppendLine(layoutDescription);
                            sb.AppendLine();
                        }
                        if (!string.IsNullOrWhiteSpace(cssClasses))
                        {
                            sb.AppendLine("**Key CSS Patterns:**");
                            sb.AppendLine(cssClasses);
                            sb.AppendLine();
                        }
                        sb.AppendLine("<details><summary>Full HTML Source</summary>");
                        sb.AppendLine();
                        sb.AppendLine("```html");
                        sb.AppendLine(content.Length > 8000 ? content[..8000] + "\n<!-- truncated -->" : content);
                        sb.AppendLine("```");
                        sb.AppendLine("</details>");
                    }
                }
                else
                {
                    sb.AppendLine($"**Type:** Design Image — engineers should reference this file visually");
                }
                sb.AppendLine();
            }

            Logger.LogInformation("Found {Count} visual design reference files in repository", designFiles.Count);

            // Capture screenshots of HTML design files and commit to repo
            await CaptureDesignScreenshotsAsync(designFiles, sb, ct);

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to scan for design reference files");
            return null;
        }
    }

    /// <summary>
    /// Capture PNG screenshots of HTML design files and commit them to the repo.
    /// These screenshots are embedded in Research.md, PMSpec, Architecture, and issues
    /// so all agents (and the human reviewer) see the exact intended visual output.
    /// </summary>
    private async Task CaptureDesignScreenshotsAsync(
        List<(string path, string type)> designFiles,
        System.Text.StringBuilder sb,
        CancellationToken ct)
    {
        if (_playwrightRunner is null)
        {
            Logger.LogDebug("PlaywrightRunner not available, skipping design screenshots");
            return;
        }

        var htmlDesignFiles = designFiles.Where(f => f.type.StartsWith("html")).ToList();
        if (htmlDesignFiles.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Design Visual Previews");
        sb.AppendLine();
        sb.AppendLine("The following screenshots were rendered from the HTML design reference files. " +
            "Engineers MUST match these visuals exactly.");
        sb.AppendLine();

        var screenshotCount = 0;
        foreach (var (path, _) in htmlDesignFiles)
        {
            try
            {
                var htmlContent = await _github.GetFileContentAsync(path, ct: ct);
                if (string.IsNullOrWhiteSpace(htmlContent)) continue;

                var screenshotBytes = await _playwrightRunner.CaptureHtmlScreenshotAsync(
                    htmlContent, _config.Workspace, ct: ct);
                if (screenshotBytes is null || screenshotBytes.Length == 0) continue;

                // Commit the screenshot to the repo
                var fileName = Path.GetFileNameWithoutExtension(path);
                var screenshotPath = $"docs/design-screenshots/{fileName}.png";
                var imageUrl = await _github.CommitBinaryFileAsync(
                    screenshotPath, screenshotBytes,
                    $"Add design screenshot: {fileName}.png (rendered from {path})",
                    "main", ct);

                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    sb.AppendLine($"### {Path.GetFileName(path)}");
                    sb.AppendLine();
                    sb.AppendLine($"![{fileName} design]({imageUrl})");
                    sb.AppendLine();
                    sb.AppendLine($"*Rendered from `{path}` at 1920×1080*");
                    sb.AppendLine();
                    screenshotCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to capture design screenshot for {Path}", path);
            }
        }

        if (screenshotCount > 0)
        {
            Logger.LogInformation("Captured and committed {Count} design screenshots", screenshotCount);
        }
    }

    /// <summary>
    /// Extract key CSS patterns from HTML design file (grid layouts, color schemes, font families).
    /// </summary>
    private static string ExtractCssPatterns(string html)
    {
        var patterns = new List<string>();

        // Extract grid layouts
        if (html.Contains("display:grid") || html.Contains("display: grid"))
            patterns.Add("- Uses CSS Grid layout");
        if (html.Contains("display:flex") || html.Contains("display: flex"))
            patterns.Add("- Uses Flexbox layout");

        // Extract color scheme from CSS
        var colorMatches = System.Text.RegularExpressions.Regex.Matches(html, @"(?:color|background|border-color|fill)\s*:\s*(#[0-9A-Fa-f]{3,8})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var colors = colorMatches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct()
            .Take(15)
            .ToList();
        if (colors.Count > 0)
            patterns.Add($"- Color palette: {string.Join(", ", colors)}");

        // Extract font families
        var fontMatch = System.Text.RegularExpressions.Regex.Match(html, @"font-family\s*:\s*'?([^;'""]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fontMatch.Success)
            patterns.Add($"- Font: {fontMatch.Groups[1].Value.Trim()}");

        // Extract grid template columns
        var gridColMatch = System.Text.RegularExpressions.Regex.Match(html, @"grid-template-columns\s*:\s*([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gridColMatch.Success)
            patterns.Add($"- Grid columns: `{gridColMatch.Groups[1].Value.Trim()}`");

        // Extract viewport sizing
        if (html.Contains("1920px") || html.Contains("1080px"))
            patterns.Add("- Designed for 1920×1080 screenshot resolution");

        return patterns.Count > 0 ? string.Join("\n", patterns) : "";
    }

    /// <summary>
    /// Extract high-level layout structure from HTML by analyzing major div classes and sections.
    /// </summary>
    private static string ExtractLayoutStructure(string html)
    {
        var sections = new List<string>();

        // Look for semantic class names that describe layout sections
        var classMatches = System.Text.RegularExpressions.Regex.Matches(html,
            @"class=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var majorClasses = classMatches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Where(c => !c.Contains("it") || c.Length > 4) // Skip tiny utility classes
            .Distinct()
            .Take(20)
            .ToList();

        if (majorClasses.Any(c => c.Contains("hdr") || c.Contains("header")))
            sections.Add("- **Header section** with title, subtitle, and legend");
        if (majorClasses.Any(c => c.Contains("tl-") || c.Contains("timeline")))
            sections.Add("- **Timeline/Gantt section** with SVG milestone visualization");
        if (majorClasses.Any(c => c.Contains("hm-") || c.Contains("heatmap")))
            sections.Add("- **Heatmap grid** — status rows × month columns, color-coded by category");
        if (majorClasses.Any(c => c.Contains("ship")))
            sections.Add("  - Shipped row (green tones)");
        if (majorClasses.Any(c => c.Contains("prog")))
            sections.Add("  - In Progress row (blue tones)");
        if (majorClasses.Any(c => c.Contains("carry")))
            sections.Add("  - Carryover row (yellow/amber tones)");
        if (majorClasses.Any(c => c.Contains("block")))
            sections.Add("  - Blockers row (red tones)");

        return sections.Count > 0 ? string.Join("\n", sections) : "";
    }

    #endregion
}

internal record ResearchDirective
{
    public string TaskId { get; init; } = "";
    public string Topic { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Issue number passed from PM's TaskAssignment for direct linking.</summary>
    public int? IssueNumber { get; init; }
}

internal record ResearchResult
{
    public string Summary { get; init; } = "";
    public List<string> KeyFindings { get; init; } = new();
    public List<string> RecommendedTools { get; init; } = new();
    public List<string> Considerations { get; init; } = new();
    public string DetailedAnalysis { get; init; } = "";
}
