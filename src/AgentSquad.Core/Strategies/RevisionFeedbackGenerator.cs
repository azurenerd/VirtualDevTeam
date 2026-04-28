using System.Text;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Generates adversarial "rubber duck" feedback for the revision round.
/// Uses a DIFFERENT model tier than the judge to provide genuine perspective diversity.
/// Called after the initial judge scoring, before frameworks attempt their revision.
/// </summary>
public class RevisionFeedbackGenerator
{
    private readonly ModelRegistry _models;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ILogger<RevisionFeedbackGenerator> _logger;

    public RevisionFeedbackGenerator(
        ModelRegistry models,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILogger<RevisionFeedbackGenerator> logger)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates adversarial feedback for a single candidate's code given its judge scores.
    /// Returns a concise critique string (1-3 paragraphs) or empty string on failure.
    /// </summary>
    public async Task<string> GenerateFeedbackAsync(
        string taskTitle,
        string taskDescription,
        string strategyId,
        string patch,
        CandidateScore scores,
        CancellationToken ct)
    {
        var tier = _cfg.CurrentValue.RevisionRound.FeedbackModelTier;
        if (string.IsNullOrWhiteSpace(tier)) tier = "standard";

        IChatCompletionService chat;
        try
        {
            chat = _models.GetKernel(tier, $"revision-feedback/{strategyId}")
                .GetRequiredService<IChatCompletionService>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RevisionFeedbackGenerator could not resolve chat for tier {Tier}", tier);
            return "";
        }

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(taskTitle, taskDescription, strategyId, patch, scores);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        try
        {
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var feedback = response.Content?.Trim() ?? "";
            _logger.LogDebug(
                "RevisionFeedbackGenerator produced {Len} chars for {Strategy}",
                feedback.Length, strategyId);
            return feedback;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RevisionFeedbackGenerator failed for {Strategy}", strategyId);
            return "";
        }
    }

    private static string BuildSystemPrompt() =>
        """
        You are an independent code critic providing a "rubber duck" second opinion on code quality.
        Your role is adversarial — challenge the code from a different angle than the primary judge.

        Focus on:
        - Missing edge cases or error handling the judge may have overlooked
        - Simpler alternatives to the current implementation
        - User experience issues (if it's a web/UI task)
        - Files that should exist but are missing (data files, configs, assets)
        - Whether the code would actually work end-to-end if someone ran it

        Be specific and actionable. Reference actual file names, function names, or line-level issues.
        Keep your critique to 2-3 focused paragraphs. Don't repeat what the judge already said.
        """;

    private static string BuildUserPrompt(
        string taskTitle, string taskDescription, string strategyId,
        string patch, CandidateScore scores)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine($"## Task: {taskTitle}");
        if (!string.IsNullOrWhiteSpace(taskDescription))
        {
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(taskDescription);
        }

        sb.AppendLine();
        sb.AppendLine($"## Candidate: {strategyId}");
        sb.AppendLine();
        sb.AppendLine("### Judge Scores (0-10):");
        sb.AppendLine($"- Acceptance Criteria: {scores.AcceptanceCriteriaScore}");
        sb.AppendLine($"- Design: {scores.DesignScore}");
        sb.AppendLine($"- Readability: {scores.ReadabilityScore}");
        if (scores.VisualsScore.HasValue)
            sb.AppendLine($"- Visuals: {scores.VisualsScore.Value}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(scores.Feedback))
        {
            sb.AppendLine("### Judge Feedback:");
            sb.AppendLine(scores.Feedback);
            sb.AppendLine();
        }

        sb.AppendLine("### Code Patch:");
        // Truncate very large patches to stay within token budget
        var truncatedPatch = patch.Length > 30_000
            ? patch[..30_000] + $"\n[... truncated {patch.Length - 30_000} chars ...]"
            : patch;
        sb.AppendLine(truncatedPatch);

        sb.AppendLine();
        sb.AppendLine("Provide your independent critique. Focus on issues the judge may have missed.");
        return sb.ToString();
    }
}
