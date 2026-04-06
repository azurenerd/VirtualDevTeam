using System.Runtime.CompilerServices;
using System.Text;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.AI;

/// <summary>
/// Implements <see cref="IChatCompletionService"/> by routing requests through the
/// GitHub Copilot CLI in non-interactive mode. Each call spawns a fresh
/// <c>copilot --allow-all --no-ask-user --silent</c> process.
/// Agents call this exactly as they would any Semantic Kernel chat completion service.
/// </summary>
public sealed class CopilotCliChatCompletionService : IChatCompletionService
{
    private readonly CopilotCliProcessManager _processManager;
    private readonly CopilotCliConfig _config;
    private readonly ILogger<CopilotCliChatCompletionService> _logger;

    public CopilotCliChatCompletionService(
        CopilotCliProcessManager processManager,
        CopilotCliConfig config,
        ILogger<CopilotCliChatCompletionService> logger)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?>
        {
            ["provider"] = "copilot-cli",
            ["model_id"] = "copilot"
        };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var prompt = FormatChatHistoryAsPrompt(chatHistory);
        _logger.LogDebug("Sending prompt to copilot CLI ({Length} chars)", prompt.Length);

        // Allow per-request model override via PromptExecutionSettings.ModelId
        var modelOverride = executionSettings?.ModelId;

        var result = await _processManager.ExecutePromptAsync(prompt, modelOverride, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("Copilot CLI request failed: {Error}", result.Error);
            throw new CopilotCliException(
                $"Copilot CLI request failed: {result.Error}");
        }

        // Parse the output based on output mode
        string parsedResponse;
        if (_config.JsonOutput)
        {
            parsedResponse = CliOutputParser.ParseJsonOutput(result.Output)
                ?? CliOutputParser.Parse(result.Output);
        }
        else
        {
            parsedResponse = CliOutputParser.Parse(result.Output);
        }

        if (string.IsNullOrWhiteSpace(parsedResponse))
        {
            _logger.LogWarning("Copilot CLI returned empty response. Raw length: {RawLength}",
                result.Output.Length);
            parsedResponse = "(No response from Copilot CLI)";
        }

        _logger.LogDebug("Received copilot response ({Length} chars)", parsedResponse.Length);

        var message = new ChatMessageContent(AuthorRole.Assistant, parsedResponse);
        return [message];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await GetChatMessageContentsAsync(
            chatHistory, executionSettings, kernel, cancellationToken);

        foreach (var result in results)
        {
            yield return new StreamingChatMessageContent(result.Role, result.Content);
        }
    }

    /// <summary>
    /// Converts a Semantic Kernel ChatHistory into a single prompt suitable for the copilot CLI.
    /// The CLI doesn't support multi-turn natively, so we flatten the conversation.
    /// </summary>
    internal static string FormatChatHistoryAsPrompt(ChatHistory chatHistory)
    {
        var sb = new StringBuilder();

        // Collect system messages as context prefix
        var systemMessages = chatHistory
            .Where(m => m.Role == AuthorRole.System)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c));

        var systemContext = string.Join("\n\n", systemMessages);
        if (!string.IsNullOrEmpty(systemContext))
        {
            sb.AppendLine("[SYSTEM CONTEXT]");
            sb.AppendLine(systemContext);
            sb.AppendLine();
        }

        // Collect conversation turns (non-system messages)
        var conversationMessages = chatHistory
            .Where(m => m.Role != AuthorRole.System)
            .ToList();

        if (conversationMessages.Count == 0)
            return sb.ToString().Trim();

        // If there's only one user message, just append it directly
        if (conversationMessages.Count == 1 && conversationMessages[0].Role == AuthorRole.User)
        {
            sb.Append(conversationMessages[0].Content);
            return sb.ToString().Trim();
        }

        // Multi-turn: format as labeled conversation
        sb.AppendLine("[CONVERSATION HISTORY]");
        foreach (var message in conversationMessages)
        {
            var roleLabel = message.Role == AuthorRole.User ? "USER" :
                           message.Role == AuthorRole.Assistant ? "ASSISTANT" : "SYSTEM";
            sb.AppendLine($"[{roleLabel}]: {message.Content}");
            sb.AppendLine();
        }

        sb.AppendLine("[INSTRUCTION]: Continue the conversation as the assistant. Respond to the last user message, taking into account the full conversation history above.");

        return sb.ToString().Trim();
    }
}
