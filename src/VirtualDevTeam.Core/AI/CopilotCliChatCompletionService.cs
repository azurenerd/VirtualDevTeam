using System.Runtime.CompilerServices;
using System.Text;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Core.AI;

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

        // Extract image content from chat history and save to temp files for CLI --attachment.
        // The CLI natively supports vision via file attachments, but only receives text via stdin.
        var imageAttachments = ExtractImageAttachments(chatHistory);

        var prompt = FormatChatHistoryAsPrompt(chatHistory, _config);
        _logger.LogDebug("Sending prompt to copilot CLI ({Length} chars, {Images} image attachments)",
            prompt.Length, imageAttachments.Count);

        // Allow per-request model override via PromptExecutionSettings.ModelId
        // FastMode overrides model to a faster one for quick E2E testing
        var modelOverride = _config.FastMode ? _config.FastModeModel : executionSettings?.ModelId;

        // Pick up CLI session ID from the ambient call context (set by the agent)
        var sessionId = AgentCallContext.CurrentSessionId;

        // Track active LLM call for dashboard status overlay
        var agentIdForTracking = AgentCallContext.CurrentAgentId;
        var effectiveModelForTracking = modelOverride ?? _config.ModelName;
        if (agentIdForTracking is not null)
        {
            // Use explicit call context if set, otherwise extract from last user message
            var callContext = AgentCallContext.CurrentCallContext
                ?? ExtractCallContext(chatHistory);
            _llmCallTracker.NotifyCallStarted(agentIdForTracking, effectiveModelForTracking, callContext);
        }

        // Push image attachments into the invocation context so BuildArguments adds --attachment flags.
        IDisposable? attachmentScope = null;
        if (imageAttachments.Count > 0)
        {
            var existingCtx = AgentCallContext.CurrentInvocationContext;
            var mergedAttachments = existingCtx?.Attachments is { Count: > 0 }
                ? existingCtx.Attachments.Concat(imageAttachments.Select(a => a.FilePath)).ToList()
                : imageAttachments.Select(a => a.FilePath).ToList();

            attachmentScope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AdditionalMcpConfigJson: existingCtx?.AdditionalMcpConfigJson,
                AllowedMcpTools: existingCtx?.AllowedMcpTools,
                OverrideWorkingDirectory: existingCtx?.OverrideWorkingDirectory,
                AllowFileEdits: existingCtx?.AllowFileEdits ?? false,
                Attachments: mergedAttachments));
        }

        // Retry loop for transient errors (auth failures, timeouts)
        var maxRetries = _config.MaxRetries;
        CopilotCliResult? result = null;
        var stuckAttempts = 0;
        var maxStuckRetries = _config.MaxStuckRetries;
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                var forceNoWrapper = false;
                var attemptModel = modelOverride;

                // Stuck escalation: if prior attempt was killed by stuck detection,
                // escalate through rungs before counting against normal retries.
                if (stuckAttempts > 0 && stuckAttempts <= maxStuckRetries)
                {
                    var rung = stuckAttempts;
                    if (rung == 1 && sessionId is not null && (result?.HadAnyOutput ?? false))
                    {
                        // Rung 1: Resume same session — the CLI may have partial progress
                        _logger.LogWarning(
                            "Stuck retry rung 1/{Max}: resuming session {Session} (had partial output)",
                            maxStuckRetries, sessionId);
                    }
                    else if (rung <= 2)
                    {
                        // Rung 2 (or rung 1 if no session/no output): Fresh session
                        _logger.LogWarning(
                            "Stuck retry rung {Rung}/{Max}: starting fresh session (clearing session ID)",
                            Math.Min(rung, 2), maxStuckRetries);
                        sessionId = null;
                        AgentCallContext.CurrentSessionId = null;
                    }
                    else
                    {
                        // Rung 3: Fresh session + skip wrapper + optional model fallback
                        forceNoWrapper = true;
                        sessionId = null;
                        AgentCallContext.CurrentSessionId = null;

                        if (!string.IsNullOrEmpty(_config.StuckFallbackModel))
                        {
                            attemptModel = _config.StuckFallbackModel;
                            _logger.LogWarning(
                                "Stuck retry rung 3/{Max}: fresh session, no wrapper, fallback model {Model}",
                                maxStuckRetries, attemptModel);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Stuck retry rung 3/{Max}: fresh session, no wrapper",
                                maxStuckRetries);
                        }
                    }

                    // Don't count stuck retries against the normal transient retry budget
                    attempt--;
                }

                result = await _processManager.ExecutePromptAsync(
                    prompt, attemptModel, sessionId, cancellationToken,
                    forceNoWrapper: forceNoWrapper);

                if (result.IsSuccess)
                    break;

                // Stuck detection kill: escalate through the stuck retry ladder
                if (result.FailureReason == CliFailureReason.StuckNoOutput && stuckAttempts < maxStuckRetries)
                {
                    stuckAttempts++;
                    _logger.LogWarning(
                        "CLI session killed by stuck detection (no output). Escalating to stuck retry {Attempt}/{Max}",
                        stuckAttempts, maxStuckRetries);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // brief cooldown
                    continue;
                }

                // Stale session: the CLI can't find the --resume session (e.g., after DB reset).
                // Clear session ID and retry without it — the CLI will start a fresh session.
                if (!result.IsSuccess && sessionId is not null &&
                    result.Error?.Contains("No session", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogWarning(
                        "CLI session {SessionId} not found — clearing stale session and retrying without --resume",
                        sessionId);
                    sessionId = null;
                    AgentCallContext.CurrentSessionId = null;
                    continue; // retry immediately without backoff
                }

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

            // Clean up temp image files and restore invocation context
            attachmentScope?.Dispose();
            foreach (var att in imageAttachments)
            {
                try { File.Delete(att.FilePath); } catch { /* best-effort cleanup */ }
            }
        }

        if (!result!.IsSuccess)
        {
            _logger.LogWarning("Copilot CLI request failed after {Attempts} attempt(s) ({StuckRetries} stuck retries): {Error}",
                maxRetries + 1, stuckAttempts, result.Error);
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

        // Safety cap: no valid LLM response should exceed 2 MB of text.
        // Catches runaway output that slipped past parsing (e.g., raw JSONL passed as content).
        const int MaxResponseChars = 2 * 1024 * 1024;
        if (parsedResponse.Length > MaxResponseChars)
        {
            _logger.LogError(
                "Copilot CLI response exceeds safety cap ({Length} chars > {Max}). " +
                "Truncating — this likely indicates raw JSONL or runaway output leaked through parsing.",
                parsedResponse.Length, MaxResponseChars);
            parsedResponse = "(Response exceeded safety cap — likely malformed CLI output)";
        }

        // Strip meta-commentary that the copilot CLI sometimes prepends
        parsedResponse = StripMetaCommentary(parsedResponse);

        _logger.LogDebug("Received copilot response ({Length} chars)", parsedResponse.Length);

        // Record estimated usage for cost tracking
        var agentId = AgentCallContext.CurrentAgentId ?? "unknown";
        var effectiveModel = modelOverride ?? _config.ModelName;
        _usageTracker.RecordCall(agentId, effectiveModel, prompt.Length, parsedResponse.Length);

        // Extract premium request count from JSONL output (if available, best-effort)
        try
        {
            var cliUsage = CliOutputParser.ParseJsonUsage(result.Output);
            if (cliUsage is not null && cliUsage.PremiumRequests > 0)
            {
                _usageTracker.RecordPremiumRequests(agentId, cliUsage.PremiumRequests, cliUsage.TotalApiDurationMs);
                _logger.LogDebug("CLI usage for {Agent}: {PremiumRequests} premium request(s), API duration {Duration}ms",
                    agentId, cliUsage.PremiumRequests, cliUsage.TotalApiDurationMs);
            }
        }
        catch (Exception usageEx)
        {
            _logger.LogDebug(usageEx, "Failed to parse CLI usage — non-fatal, continuing with response");
        }

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
        var hasMcpServers = AgentCallContext.McpServers is { Count: > 0 };
        var allowTools = invocation?.AllowToolUsage == true || hasMcpServers;
        var allowFileEdits = invocation?.AllowFileEdits == true;

        // Critical directive: prevent CLI from acting as an interactive assistant
        sb.AppendLine("[OUTPUT FORMAT INSTRUCTIONS]");
        var agenticAllowAll = invocation?.AgenticAllowAll == true;
        var documentGenMode = invocation?.DocumentGenerationMode == true;
        if (agenticAllowAll && documentGenMode)
        {
            // Agentic document generation mode: CLI has --allow-all for codebase exploration,
            // but the response IS the deliverable document (not a summary of edits).
            // Used by PM (PMSpec.md), Architect (Architecture.md), and Researcher (Research.md).
            sb.AppendLine("You are an autonomous research agent with full read access to explore the project codebase.");
            sb.AppendLine("RULES:");
            sb.AppendLine("1. USE read tools (view, grep, glob, bash/powershell) to explore the codebase and understand existing patterns, conventions, and architecture.");
            sb.AppendLine("2. Do NOT create, edit, or write any files. Do NOT commit anything.");
            sb.AppendLine("3. After exploring, output the FULL requested document as your response.");
            sb.AppendLine("4. Your ENTIRE response text will be captured as the document content.");
            sb.AppendLine("5. Start immediately with the first heading (e.g., # Title). Do NOT include preamble.");
            sb.AppendLine("6. Do NOT include conversational framing like 'Here is...' or 'Sure, I will...'.");
            sb.AppendLine("7. Do NOT include meta-commentary about your exploration steps or tool usage in the output.");
            sb.AppendLine("8. The document must be COMPLETE and COMPREHENSIVE — do NOT output a summary or outline.");
        }
        else if (agenticAllowAll)
        {
            // Full agentic mode: CLI has --allow-all, can use shell, git rm, delete files, etc.
            sb.AppendLine("You are an autonomous coding agent with full shell access.");
            sb.AppendLine("RULES:");
            sb.AppendLine("1. USE any tools available: edit, create, view, grep, bash/powershell commands.");
            sb.AppendLine("2. You CAN and SHOULD use shell commands when needed: git rm, rm, mv, mkdir, etc.");
            sb.AppendLine("3. To DELETE files: use `git rm <path>` (removes from both working tree and git index).");
            sb.AppendLine("4. To remove files from git tracking without deleting: use `git rm --cached <path>`.");
            sb.AppendLine("5. Make precise, targeted changes. Do NOT rewrite entire files unless necessary.");
            sb.AppendLine("6. After making all changes, output a brief summary of what you changed and why.");
            sb.AppendLine("7. Do NOT include conversational framing like 'Here is...' or 'Sure, I will...'.");
        }
        else if (allowFileEdits)
        {
            // Agentic file-edit mode: let the CLI use its native edit/create tools
            sb.AppendLine("You are making surgical code edits to existing files.");
            sb.AppendLine("RULES:");
            sb.AppendLine("1. USE your built-in edit tool to make precise, targeted changes to files.");
            sb.AppendLine("2. Only edit the specific lines that need to change. Do NOT rewrite entire files.");
            sb.AppendLine("3. Use your view/grep tools to read files and understand context before editing.");
            sb.AppendLine("4. Use your create tool ONLY for genuinely new files.");
            sb.AppendLine("5. After making all edits, output a brief summary of what you changed and why.");
            sb.AppendLine("6. Do NOT include conversational framing like 'Here is...' or 'Sure, I will...'.");
        }
        else
        {
            sb.AppendLine("For this task, produce ONLY the direct requested content as plain text.");
            sb.AppendLine("RULES:");
            sb.AppendLine("1. Output the requested content DIRECTLY. Start immediately with the content itself.");
            if (allowTools)
            {
                sb.AppendLine("2. You MAY silently call the configured read-only MCP tools BEFORE producing output. Available tools include: ask_work_iq (query Microsoft 365 Copilot to read SharePoint/OneDrive documents, emails, and files — use this when the prompt references SharePoint or OneDrive URLs), read_file, list_directory, search_code. IMPORTANT: If the project description or context contains a SharePoint/OneDrive URL, you MUST call ask_work_iq with the fileUrls parameter set to an array containing that URL to retrieve the specific document content before researching. Always pass the URL via fileUrls — do NOT just mention the URL in the question text, because without fileUrls M365 Copilot may return a different document. Do NOT narrate tool calls, inspection steps, or intermediate actions in your response. Do NOT create, edit, or write files. Do NOT run shell commands.");
            }
            else
            {
                sb.AppendLine("2. Do NOT create, edit, or write any files. Do NOT use any tools or shell commands.");
            }
            sb.AppendLine("3. Do NOT include conversational framing like 'Here is...' or 'I have created...'.");
            sb.AppendLine("4. Do NOT include meta-commentary about yourself, your capabilities, or your design.");
            sb.AppendLine("5. If asked for a markdown document, output the FULL markdown — start with the first heading.");
            sb.AppendLine("6. Your ENTIRE response will be captured as the document content. Nothing else.");
        }
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
    /// (text + images). Images are now handled via CLI --attachment flags for proper vision
    /// support, so we just add a placeholder reference in the text prompt.
    /// </summary>
    private static void AppendMessageContent(StringBuilder sb, ChatMessageContent message)
    {
        // Check if message has mixed content items (text + images)
        if (message.Items is { Count: > 0 })
        {
            bool hasImageContent = false;
            foreach (var item in message.Items)
            {
                if (item is ImageContent)
                {
                    hasImageContent = true;
                    // Image is passed as a CLI --attachment for native vision support.
                    // Add a reference so the model knows an image is attached.
                    sb.AppendLine();
                    sb.AppendLine("[An image is attached to this message — analyze it using your vision capabilities]");
                    sb.AppendLine();
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
               lower.Contains("pipe") ||              // CLI process died before stdin write completed
               lower.Contains("cli crashed") ||        // Our new early-crash detection message
               lower.Contains("ioexception") ||
               lower.Contains("500") ||
               lower.Contains("server error") ||
               lower.Contains("internal server error") ||
               lower.Contains("502") ||
               lower.Contains("503") ||
               lower.Contains("504") ||
               lower.Contains("-1073740791") || // STATUS_STACK_BUFFER_OVERRUN (process crash)
               lower.Contains("-1073741819") || // STATUS_ACCESS_VIOLATION (process crash)
               lower.Contains("exited with code -");  // Any negative exit code = process crash
    }

    /// <summary>
    /// Extracts a short, human-readable context string from the ChatHistory
    /// by looking at the last user message. Falls back to the system message role.
    /// </summary>
    private static string? ExtractCallContext(ChatHistory chatHistory)
    {
        // Find the last user message — it describes what the AI is being asked to do
        var lastUser = chatHistory.LastOrDefault(m => m.Role == AuthorRole.User);
        if (lastUser?.Content is not { Length: > 0 } content)
            return null;

        // Extract first meaningful line (skip blank lines, headings)
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            // Skip common prompt prefixes/headers
            if (line.StartsWith('[') || line.StartsWith("RULES:") || line.StartsWith("For this task"))
                continue;

            // Take the first substantive line, truncated for display
            var clean = line.TrimStart('#', ' ', '-', '*');
            if (clean.Length < 5) continue;

            return clean.Length <= 120 ? clean : clean[..117] + "…";
        }

        return null;
    }

    /// <summary>Represents a temp file created for an image attachment.</summary>
    private sealed record ImageAttachment(string FilePath);

    /// <summary>
    /// Scans the chat history for <see cref="ImageContent"/> items, saves each to a temp file,
    /// and returns the list of file paths. The caller must clean up these files after the CLI call.
    /// The CLI processes images via --attachment for native vision support.
    /// </summary>
    private List<ImageAttachment> ExtractImageAttachments(ChatHistory chatHistory)
    {
        var attachments = new List<ImageAttachment>();

        foreach (var message in chatHistory)
        {
            if (message.Items is not { Count: > 0 }) continue;

            foreach (var item in message.Items)
            {
                if (item is not ImageContent imageContent) continue;

                if (imageContent.Data.HasValue && imageContent.Data.Value.Length > 0)
                {
                    try
                    {
                        var ext = (imageContent.MimeType ?? "image/png") switch
                        {
                            "image/jpeg" or "image/jpg" => ".jpg",
                            "image/gif" => ".gif",
                            "image/webp" => ".webp",
                            _ => ".png"
                        };
                        var tempPath = Path.Combine(Path.GetTempPath(), $"copilot-vision-{Guid.NewGuid():N}{ext}");
                        File.WriteAllBytes(tempPath, imageContent.Data.Value.ToArray());
                        attachments.Add(new ImageAttachment(tempPath));
                        _logger.LogDebug("Saved image attachment ({Bytes} bytes) to {Path}",
                            imageContent.Data.Value.Length, tempPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save image attachment to temp file");
                    }
                }
            }
        }

        return attachments;
    }
}
