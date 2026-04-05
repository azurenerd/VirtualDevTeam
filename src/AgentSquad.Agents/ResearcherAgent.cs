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
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly Queue<ResearchDirective> _researchQueue = new();
    private readonly List<IDisposable> _subscriptions = new();

    public ResearcherAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<ResearcherAgent> logger)
        : base(identity, logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
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
        UpdateStatus(AgentStatus.Online, "Waiting for research directives from PM");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_researchQueue.TryDequeue(out var directive))
                {
                    UpdateStatus(AgentStatus.Working, $"Researching: {directive.Topic}");
                    Logger.LogInformation("Starting research on: {Topic}", directive.Topic);

                    var research = await ConductResearchAsync(directive, ct);

                    await AppendToResearchDocAsync(directive.Topic, research, ct);

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
                else
                {
                    UpdateStatus(AgentStatus.Online, "Waiting for research directives");
                    await Task.Delay(5000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Research loop error, continuing after brief delay");
                UpdateStatus(AgentStatus.Working, "Recovering from error");
                try { await Task.Delay(5000, ct); }
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
            Description = message.Description
        });

        return Task.CompletedTask;
    }

    #endregion

    #region Research Logic

    private async Task<ResearchResult> ConductResearchAsync(
        ResearchDirective directive, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Turn 1: Break down the research topic into sub-questions
        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a senior technical researcher on a software development team. " +
            "Your job is to perform deep research on assigned topics and produce structured, " +
            "actionable findings. Be thorough but concise. Focus on practical recommendations " +
            "for the engineering team.");

        history.AddUserMessage(
            $"I need you to research the following topic for our software project.\n\n" +
            $"**Topic:** {directive.Topic}\n\n" +
            $"**Context:** {directive.Description}\n\n" +
            "First, break this topic down into 3-5 key sub-questions we need to answer. " +
            "List them clearly, one per line, prefixed with a number.");

        var subQuestionsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(subQuestionsResponse.Content ?? "");

        Logger.LogDebug("Research sub-questions identified for {Topic}", directive.Topic);

        // Turn 2: Analyze each sub-question in depth
        history.AddUserMessage(
            "Now provide a detailed analysis for each sub-question you identified. " +
            "For each one, cover:\n" +
            "- Key findings and insights\n" +
            "- Relevant tools, libraries, or technologies\n" +
            "- Trade-offs and considerations\n" +
            "- Concrete recommendations\n\n" +
            "Be thorough and specific. Include version numbers, links to documentation " +
            "patterns, and real-world considerations where applicable.");

        var analysisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(analysisResponse.Content ?? "");

        Logger.LogDebug("Detailed analysis complete for {Topic}", directive.Topic);

        // Turn 3: Synthesize into structured output
        history.AddUserMessage(
            "Now synthesize your research into a structured output with these exact sections:\n\n" +
            "1. **Summary** — A 2-3 sentence executive summary of your findings.\n" +
            "2. **Key Findings** — A bullet list of the most important discoveries (one per line, prefixed with '- ').\n" +
            "3. **Recommended Tools & Technologies** — A bullet list of specific tools, libraries, or frameworks you recommend (one per line, prefixed with '- ').\n" +
            "4. **Considerations & Risks** — A bullet list of important caveats, risks, or trade-offs (one per line, prefixed with '- ').\n\n" +
            "Use these exact section headers. Be concise but thorough.");

        var synthesisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        var synthesisContent = synthesisResponse.Content ?? "";

        Logger.LogDebug("Research synthesis complete for {Topic}", directive.Topic);

        return ParseResearchResult(synthesisContent, analysisResponse.Content ?? "");
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

            if (line.Contains("Summary", StringComparison.OrdinalIgnoreCase)
                && (line.StartsWith('#') || line.StartsWith('*') || line.StartsWith("**")))
            {
                currentSection = "summary";
                continue;
            }

            if (line.Contains("Key Findings", StringComparison.OrdinalIgnoreCase)
                && (line.StartsWith('#') || line.StartsWith('*') || line.StartsWith("**")))
            {
                currentSection = "findings";
                continue;
            }

            if (line.Contains("Recommended Tools", StringComparison.OrdinalIgnoreCase)
                && (line.StartsWith('#') || line.StartsWith('*') || line.StartsWith("**")))
            {
                currentSection = "tools";
                continue;
            }

            if (line.Contains("Considerations", StringComparison.OrdinalIgnoreCase)
                && (line.StartsWith('#') || line.StartsWith('*') || line.StartsWith("**")))
            {
                currentSection = "considerations";
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

    #endregion
}

internal record ResearchDirective
{
    public string TaskId { get; init; } = "";
    public string Topic { get; init; } = "";
    public string Description { get; init; } = "";
}

internal record ResearchResult
{
    public string Summary { get; init; } = "";
    public List<string> KeyFindings { get; init; } = new();
    public List<string> RecommendedTools { get; init; } = new();
    public List<string> Considerations { get; init; } = new();
    public string DetailedAnalysis { get; init; } = "";
}
