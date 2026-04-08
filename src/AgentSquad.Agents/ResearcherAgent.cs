using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
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

    private readonly Queue<ResearchDirective> _researchQueue = new();
    private readonly List<IDisposable> _subscriptions = new();

    public ResearcherAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        ILogger<ResearcherAgent> logger)
        : base(identity, logger, memoryStore)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
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

                        // Now do the AI research work
                        UpdateStatus(AgentStatus.Working, $"Researching: {directive.Topic}");
                        Logger.LogInformation("Starting research on: {Topic}", directive.Topic);
                        LogActivity("task", $"🔬 Starting research on: {directive.Topic}");

                        var research = await ConductResearchAsync(directive, ct);

                        // Build the full Research.md content
                        var existingContent = await _projectFiles.GetResearchDocAsync(ct);
                        var newSection = FormatResearchSection(directive.Topic, research);
                        var updatedDoc = existingContent.TrimEnd() + "\n\n" + newSection + "\n";

                        // Commit final content and auto-merge
                        UpdateStatus(AgentStatus.Working, "Committing Research.md and merging PR");
                        await _prWorkflow.CommitAndMergeDocumentPRAsync(
                            pr,
                            Identity.DisplayName,
                            "Research.md",
                            updatedDoc,
                            $"Add research findings: {directive.Topic}",
                            ct);

                        Logger.LogInformation("Research.md PR created and merged for '{Topic}'", directive.Topic);
                        LogActivity("task", $"✅ Research.md merged: {directive.Topic}");
                        await RememberAsync(MemoryType.Action,
                            $"Completed research and merged Research.md for '{directive.Topic}'",
                            TruncateForMemory(research.Summary), ct);
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
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        var memoryContext = await GetMemoryContextAsync(ct: ct);
        history.AddSystemMessage(
            "You are a senior technical researcher on a software development team. " +
            "Your job is to perform deep, thorough research on assigned topics and produce structured, " +
            "actionable findings that architects and engineers can build from directly. " +
            "Go beyond surface-level recommendations — provide specific tools, version numbers, " +
            "architecture patterns, trade-offs, and real-world considerations. " +
            "Focus on practical, opinionated recommendations backed by reasoning.\n\n" +
            $"IMPORTANT: The project's technology stack has already been decided: **{_config.Project.TechStack}**. " +
            "Your research MUST target this stack. Recommend libraries, patterns, and tools that are " +
            "native to or compatible with this stack. Do NOT recommend alternative tech stacks — " +
            "the decision is final." +
            (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}"));

        var useSinglePass = _config.CopilotCli.SinglePassMode || _config.CopilotCli.FastMode;
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

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var isHeader = line.StartsWith('#') || line.StartsWith('*') || line.StartsWith("**");

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

            var bulletContent = StripBulletPrefix(line);

            switch (currentSection)
            {
                case "summary":
                    summary = string.IsNullOrEmpty(summary)
                        ? bulletContent
                        : $"{summary} {bulletContent}";
                    break;
                case "findings":
                    keyFindings.Add(bulletContent);
                    break;
                case "tools":
                    recommendedTools.Add(bulletContent);
                    break;
                case "considerations":
                    considerations.Add(bulletContent);
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
