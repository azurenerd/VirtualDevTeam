using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.AI;

/// <summary>
/// Uses a budget-tier AI model to produce focused knowledge summaries from raw content.
/// Summaries are tailored to the agent's role for maximum relevance within the context budget.
/// </summary>
public class AiKnowledgeSummarizer
{
    private readonly IChatCompletionRunner _chatRunner;
    private readonly ILogger<AiKnowledgeSummarizer> _logger;

    // Use budget tier for summarization to minimize cost
    private const string SummarizationTier = "budget";
    private const int MaxInputChars = 8000;

    public AiKnowledgeSummarizer(IChatCompletionRunner chatRunner, ILogger<AiKnowledgeSummarizer> logger)
    {
        _chatRunner = chatRunner ?? throw new ArgumentNullException(nameof(chatRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Summarizes raw content for a specific agent role, focusing on relevant information.
    /// </summary>
    /// <param name="rawContent">The raw text content to summarize.</param>
    /// <param name="roleName">The agent's role name for relevance targeting.</param>
    /// <param name="rolePrompt">The agent's system prompt (used to determine what's relevant).</param>
    /// <param name="maxOutputChars">Maximum characters in the output summary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A focused summary string, or the truncated raw content if AI summarization fails.</returns>
    public async Task<string> SummarizeForRoleAsync(
        string rawContent,
        string roleName,
        string? rolePrompt,
        int maxOutputChars = 1000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return "";

        // If content is already short enough, return it directly
        if (rawContent.Length <= maxOutputChars)
            return rawContent;

        // Truncate input to avoid overwhelming the summarizer
        var input = rawContent.Length > MaxInputChars
            ? rawContent[..MaxInputChars] + "..."
            : rawContent;

        var roleContext = string.IsNullOrWhiteSpace(rolePrompt)
            ? roleName
            : rolePrompt[..Math.Min(rolePrompt.Length, 200)];

        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a concise summarization assistant. Extract the most important and relevant information " +
                "from the provided content. Focus on facts, patterns, rules, and key concepts. " +
                "Output a dense, information-rich summary. No preamble, no meta-commentary.");

            history.AddUserMessage(
                $"Summarize the following content for an AI agent whose role is: {roleName}\n" +
                $"Focus on information directly relevant to their expertise: {roleContext}\n" +
                $"Produce a concise summary (max {maxOutputChars} chars) with key facts, patterns, and rules.\n\n" +
                $"Content:\n{input}");

            var summary = await _chatRunner.InvokeAsync(new ChatCompletionRequest
            {
                History = history,
                ModelTier = SummarizationTier,
                AgentId = "knowledge-summarizer"
            }, ct);

            if (!string.IsNullOrWhiteSpace(summary))
            {
                // Enforce max length
                if (summary.Length > maxOutputChars)
                    summary = summary[..maxOutputChars] + "...";

                _logger.LogDebug("AI summarized {InputChars} chars to {OutputChars} chars for {Role}",
                    rawContent.Length, summary.Length, roleName);
                return summary;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI summarization failed for {Role}, falling back to truncation", roleName);
        }

        // Fallback: simple truncation
        return rawContent[..Math.Min(rawContent.Length, maxOutputChars)] + "...";
    }
}
