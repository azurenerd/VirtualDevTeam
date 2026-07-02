using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// Mock IChatCompletionService that matches system prompt patterns and returns pre-built content.
/// Unmatched prompts go to an optional fallback service or return a generic "OK" response.
/// </summary>
public class ScriptedChatCompletionService : IChatCompletionService
{
    private readonly List<ScriptEntry> _scripts = new();
    private readonly IChatCompletionService? _fallback;
    private readonly List<(DateTime Timestamp, string SystemPromptSnippet, string Response)> _callLog = new();
    private readonly object _logLock = new();

    /// <summary>
    /// Artificial delay per call to simulate LLM latency and allow HealthMonitor
    /// to observe intermediate agent states between calls. Default 500ms.
    /// </summary>
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// Read-only log of all calls made to this service, for test assertions.
    /// </summary>
    public IReadOnlyList<(DateTime Timestamp, string SystemPromptSnippet, string Response)> CallLog
    {
        get { lock (_logLock) { return _callLog.ToList(); } }
    }

    public ScriptedChatCompletionService(IChatCompletionService? fallback = null)
    {
        _fallback = fallback;
    }

    /// <summary>
    /// Add a scripted response: when the system prompt contains <paramref name="systemPromptContains"/>,
    /// return <paramref name="response"/>.
    /// </summary>
    public ScriptedChatCompletionService When(string systemPromptContains, string response)
    {
        _scripts.Add(new ScriptEntry(
            h => GetSystemPrompt(h).Contains(systemPromptContains, StringComparison.OrdinalIgnoreCase),
            response));
        return this;
    }

    /// <summary>
    /// Add a scripted response using a custom predicate on the full ChatHistory.
    /// </summary>
    public ScriptedChatCompletionService When(Func<ChatHistory, bool> predicate, string response)
    {
        _scripts.Add(new ScriptEntry(predicate, response));
        return this;
    }

    /// <summary>
    /// Add a scripted response matching on both system prompt and user prompt patterns.
    /// </summary>
    public ScriptedChatCompletionService When(string systemPromptContains, string userPromptContains, string response)
    {
        _scripts.Add(new ScriptEntry(
            h => GetSystemPrompt(h).Contains(systemPromptContains, StringComparison.OrdinalIgnoreCase)
                && GetLastUserMessage(h).Contains(userPromptContains, StringComparison.OrdinalIgnoreCase),
            response));
        return this;
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = GetSystemPrompt(chatHistory);

        // Simulate LLM latency so HealthMonitor can observe intermediate agent states
        if (SimulatedDelay > TimeSpan.Zero)
            await Task.Delay(SimulatedDelay, cancellationToken);

        foreach (var script in _scripts)
        {
            if (script.Predicate(chatHistory))
            {
                LogCall(systemPrompt, script.Response);
                return [new ChatMessageContent(AuthorRole.Assistant, script.Response)];
            }
        }

        // No script matched — use fallback or return generic OK
        if (_fallback is not null)
        {
            var result = await _fallback.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            LogCall(systemPrompt, result.FirstOrDefault()?.Content ?? "(empty)");
            return result;
        }

        const string defaultResponse = "OK";
        LogCall(systemPrompt, defaultResponse);
        return [new ChatMessageContent(AuthorRole.Assistant, defaultResponse)];
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming is not supported in scripted mode.");
    }

    private static string GetSystemPrompt(ChatHistory history) =>
        history.FirstOrDefault(m => m.Role == AuthorRole.System)?.Content ?? string.Empty;

    private static string GetLastUserMessage(ChatHistory history) =>
        history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? string.Empty;

    private void LogCall(string systemPromptSnippet, string response)
    {
        var snippet = systemPromptSnippet.Length > 100
            ? systemPromptSnippet[..100] + "..."
            : systemPromptSnippet;
        lock (_logLock)
        {
            _callLog.Add((DateTime.UtcNow, snippet, response));
        }
    }

    private record ScriptEntry(Func<ChatHistory, bool> Predicate, string Response);
}
