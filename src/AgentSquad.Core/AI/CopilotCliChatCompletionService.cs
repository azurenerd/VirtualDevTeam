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
    private readonly AgentUsageTracker _usageTracker;
    private readonly ActiveLlmCallTracker _llmCallTracker;
    private readonly ILogger<CopilotCliChatCompletionService> _logger;

    public CopilotCliChatCompletionService(
        CopilotCliProcessManager processManager,
        CopilotCliConfig config,
        AgentUsageTracker usageTracker,
        ActiveLlmCallTracker llmCallTracker,
        ILogger<CopilotCliChatCompletionService> logger)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _llmCallTracker = llmCallTracker ?? throw new ArgumentNullException(nameof(llmCallTracker));
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

        var prompt = FormatChatHistoryAsPrompt(chatHistory, _config);
        _logger.LogDebug("Sending prompt to copilot CLI ({Length} chars)", prompt.Length);

        // Allow per-request model override via PromptExecutionSettings.ModelId
        // FastMode overrides model to a faster one for quick E2E testing
        var modelOverride = _config.FastMode ? _config.FastModeModel : executionSettings?.ModelId;

        // Pick up CLI session ID from the ambient call context (set by the agent)
        var sessionId = AgentCallContext.CurrentSessionId;

        // Track active LLM call for dashboard status overlay
        var agentIdForTracking = AgentCallContext.CurrentAgentId;
        var effectiveModelForTracking = modelOverride ?? _config.ModelName;
        if (agentIdForTracking is not null)
            _llmCallTracker.NotifyCallStarted(agentIdForTracking, effectiveModelForTracking);

        // Retry loop for transient errors (auth failures, timeouts)
        var maxRetries = _config.MaxRetries;
        CopilotCliResult? result = null;
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                result = await _processManager.ExecutePromptAsync(prompt, modelOverride, sessionId, cancellationToken);

                if (result.IsSuccess)
                    break;

                if (attempt < maxRetries && IsTransientError(result.Error))
                {
                    var backoffSeconds = attempt switch { 0 => 5, 1 => 15, _ => 30 };
                    _logger.LogWarning(
                        "Transient error on attempt {Attempt}/{MaxRetries}, retrying in {Backoff}s: {Error}",
                        attempt + 1, maxRetries, backoffSeconds, result.Error);
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
                    continue;
                }

                // Non-transient error or retries exhausted
                break;
            }
        }
        finally
        {
            if (agentIdForTracking is not null)
                _llmCallTracker.NotifyCallCompleted(agentIdForTracking);
        }

        if (!result!.IsSuccess)
        {
            _logger.LogWarning("Copilot CLI request failed after {Attempts} attempt(s): {Error}",
                maxRetries + 1, result.Error);
            throw CopilotCliException.FromCliError(result.Error ?? "Unknown error");
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

        // Strip meta-commentary that the copilot CLI sometimes prepends
        parsedResponse = StripMetaCommentary(parsedResponse);

        _logger.LogDebug("Received copilot response ({Length} chars)", parsedResponse.Length);

        // Record estimated usage for cost tracking
        var agentId = AgentCallContext.CurrentAgentId ?? "unknown";
        var effectiveModel = modelOverride ?? _config.ModelName;
        _usageTracker.RecordCall(agentId, effectiveModel, prompt.Length, parsedResponse.Length);

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
    /// <remarks>
    /// Rule #2 of the [OUTPUT FORMAT INSTRUCTIONS] header is the tool-permission rule.
    /// It flips based on <see cref="AgentCallContext.CurrentInvocationContext"/>:
    /// <list type="bullet">
    ///   <item>No context / no allowed tools → strict "Do NOT use any tools or shell commands".</item>
    ///   <item>Context with allowed MCP tools → "MAY call read-only MCP tools silently; no narration; still no file writes or shell commands".</item>
    /// </list>
    /// Keeping prompt-state + CLI-arg-state in a single source (the invocation context)
    /// is deliberate: allowing CLI tool flags without updating the prompt would leave the
    /// model disinclined to use the tools despite permission; updating the prompt without
    /// granting CLI permission would invite tool-call failures.
    /// </remarks>
    internal static string FormatChatHistoryAsPrompt(ChatHistory chatHistory, CopilotCliConfig? config = null)
    {
        var sb = new StringBuilder();

        var invocation = AgentCallContext.CurrentInvocationContext;
        var allowTools = invocation?.AllowToolUsage == true;

        // Critical directive: prevent CLI from acting as an interactive assistant
        sb.AppendLine("[OUTPUT FORMAT INSTRUCTIONS]");
        sb.AppendLine("For this task, produce ONLY the direct requested content as plain text.");
        sb.AppendLine("RULES:");
        sb.AppendLine("1. Output the requested content DIRECTLY. Start immediately with the content itself.");
        if (allowTools)
        {
            sb.AppendLine("2. You MAY silently call the configured read-only MCP tools (e.g. read_file, list_directory, search_code) to inspect workspace context BEFORE producing output. Do NOT narrate tool calls, inspection steps, or intermediate actions in your response. Do NOT create, edit, or write files. Do NOT run shell commands.");
        }
        else
        {
            sb.AppendLine("2. Do NOT create, edit, or write any files. Do NOT use any tools or shell commands.");
        }
        sb.AppendLine("3. Do NOT include conversational framing like 'Here is...' or 'I have created...'.");
        sb.AppendLine("4. Do NOT include meta-commentary about yourself, your capabilities, or your design.");
        sb.AppendLine("5. If asked for a markdown document, output the FULL markdown — start with the first heading.");
        sb.AppendLine("6. Your ENTIRE response will be captured as the document content. Nothing else.");
        sb.AppendLine();

        // Fast mode: inject brevity constraint
        if (config?.FastMode == true)
        {
            sb.AppendLine("[SPEED MODE — ACTIVE]");
            sb.AppendLine("Respond as concisely as possible. MAXIMUM 500 words. Skip examples, skip detailed explanations.");
            sb.AppendLine("Use bullet points. Prioritize structure and actionable content over comprehensiveness.");
            sb.AppendLine("This is a test run — focus on correct structure, not depth.");
            sb.AppendLine();
        }

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
            AppendMessageContent(sb, conversationMessages[0]);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("[REMINDER]: Output the content directly. Do NOT describe what you would create. Start your response with the actual content.");
            return sb.ToString().Trim();
        }

        // Multi-turn: format as labeled conversation
        sb.AppendLine("[CONVERSATION HISTORY]");
        foreach (var message in conversationMessages)
        {
            var roleLabel = message.Role == AuthorRole.User ? "USER" :
                           message.Role == AuthorRole.Assistant ? "ASSISTANT" : "SYSTEM";
            sb.Append($"[{roleLabel}]: ");
            AppendMessageContent(sb, message);
            sb.AppendLine();
        }

        sb.AppendLine("[INSTRUCTION]: Continue the conversation as the assistant. Respond to the last user message, taking into account the full conversation history above.");
        sb.AppendLine("[REMINDER]: Output the content directly. Do NOT describe what you would create. Do NOT include meta-commentary about yourself. Start your response with the actual requested content.");

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Appends message content to the StringBuilder, handling both plain text and mixed content
    /// (text + images). ImageContent items are embedded as base64 data URIs so vision-capable
    /// models can analyze them.
    /// </summary>
    private static void AppendMessageContent(StringBuilder sb, ChatMessageContent message)
    {
        // Check if message has mixed content items (text + images)
        if (message.Items is { Count: > 0 })
        {
            bool hasImageContent = false;
            foreach (var item in message.Items)
            {
                if (item is ImageContent imageContent)
                {
                    hasImageContent = true;
                    // Embed image as base64 data URI for vision analysis
                    if (imageContent.Data.HasValue && imageContent.Data.Value.Length > 0)
                    {
                        var base64 = Convert.ToBase64String(imageContent.Data.Value.ToArray());
                        var mimeType = imageContent.MimeType ?? "image/png";
                        sb.AppendLine();
                        sb.AppendLine($"[EMBEDDED IMAGE ({mimeType}, {imageContent.Data.Value.Length} bytes)]");
                        sb.AppendLine($"data:{mimeType};base64,{base64}");
                        sb.AppendLine();
                    }
                    else if (imageContent.Uri is not null)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"[IMAGE URL]: {imageContent.Uri}");
                        sb.AppendLine();
                    }
                }
                else if (item is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                {
                    sb.AppendLine(textContent.Text);
                }
            }

            // Fallback: if no image items found, use plain Content
            if (!hasImageContent && !string.IsNullOrEmpty(message.Content))
            {
                sb.AppendLine(message.Content);
            }
        }
        else
        {
            // Simple text-only message
            sb.AppendLine(message.Content);
        }
    }

    /// <summary>
    /// Detects and strips meta-commentary that the copilot CLI sometimes prepends.
    /// The CLI may respond with "I've created the document..." or "Here's the file..."
    /// instead of outputting the content directly. This method extracts the actual content.
    /// </summary>
    internal static string StripMetaCommentary(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        // Patterns that indicate the AI is describing what it did instead of outputting content
        string[] metaPrefixPatterns =
        [
            "i've created", "i have created", "i'll create", "i will create",
            "here is the", "here's the", "here are the",
            "let me create", "let me write", "let me generate",
            "the document has been", "the file has been", "the content has been",
            "i've written", "i have written", "i've generated",
            "now let me", "file location:", "file created",
            "written to the session", "created successfully",
            "saved to:", "output saved"
        ];

        var firstLine = response.Split('\n', 2)[0].Trim().ToLowerInvariant();

        // If the first line is meta-commentary, try to find the real content start
        if (metaPrefixPatterns.Any(p => firstLine.Contains(p)))
        {
            // Look for the first markdown heading as the start of real content
            var headingIndex = response.IndexOf("\n#", StringComparison.Ordinal);
            if (headingIndex >= 0)
            {
                return response[(headingIndex + 1)..].Trim();
            }

            // Look for the first substantial markdown (bold, bullet, etc.)
            var lines = response.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith('#') || trimmed.StartsWith("**") ||
                    trimmed.StartsWith("- ") || trimmed.StartsWith("* ") ||
                    trimmed.StartsWith("| ") || trimmed.StartsWith("```"))
                {
                    return string.Join('\n', lines[i..]).Trim();
                }
            }
        }

        // Check for trailing meta-commentary ("I've saved this to...", "The file is at...")
        var lastLines = response.Split('\n');
        var endTrimIndex = lastLines.Length;
        for (int i = lastLines.Length - 1; i >= Math.Max(0, lastLines.Length - 5); i--)
        {
            var lower = lastLines[i].Trim().ToLowerInvariant();
            if (metaPrefixPatterns.Any(p => lower.Contains(p)) ||
                lower.Contains("session-state") || lower.Contains(".copilot/") ||
                lower.StartsWith("you can copy") || lower.StartsWith("⚠️"))
            {
                endTrimIndex = i;
            }
            else if (!string.IsNullOrWhiteSpace(lastLines[i]))
            {
                break;
            }
        }

        if (endTrimIndex < lastLines.Length)
        {
            return string.Join('\n', lastLines[..endTrimIndex]).Trim();
        }

        return response;
    }

    /// <summary>
    /// Determines if a CLI error is transient and worth retrying.
    /// Auth token expiry, rate limits, and process timeouts are transient.
    /// </summary>
    internal static bool IsTransientError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        var lower = error.ToLowerInvariant();
        return lower.Contains("authentication") ||
               lower.Contains("unauthorized") ||
               lower.Contains("401") ||
               lower.Contains("403") ||
               lower.Contains("rate limit") ||
               lower.Contains("too many requests") ||
               lower.Contains("429") ||
               lower.Contains("timeout") ||
               lower.Contains("timed out") ||
               lower.Contains("connection") ||
               lower.Contains("network") ||
               lower.Contains("502") ||
               lower.Contains("503") ||
               lower.Contains("504");
    }
}
