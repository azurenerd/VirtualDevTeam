namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Immutable per-invocation context for a single copilot CLI call. One <see cref="AsyncLocal{T}"/>
/// slot holds this object so that <see cref="CopilotCliProcessManager.BuildArguments"/> and
/// <see cref="CopilotCliChatCompletionService.FormatChatHistoryAsPrompt"/> always see a
/// consistent view — tool-permission flags on the CLI cannot drift away from the prompt
/// wording that describes them.
/// </summary>
/// <param name="AdditionalMcpConfigJson">
/// Inline JSON passed verbatim via <c>--additional-mcp-config</c>. Null/empty suppresses.
/// </param>
/// <param name="AllowedMcpTools">
/// Server names emitted as <c>--allow-tool=&lt;name&gt;</c>. Non-empty presence is ALSO what
/// flips the prompt's rule #2 from strict "no tools" to "read-only MCP tools permitted".
/// This derivation is intentional: there is no separate boolean to keep in sync.
/// </param>
/// <param name="OverrideWorkingDirectory">
/// Per-invocation CWD for the spawned process. Falls back to the global config default.
/// </param>
public sealed record CopilotCliInvocationContext(
    string? AdditionalMcpConfigJson = null,
    IReadOnlyList<string>? AllowedMcpTools = null,
    string? OverrideWorkingDirectory = null,
    bool AllowFileEdits = false,
    IReadOnlyList<string>? Attachments = null,
    /// <summary>When true, the CLI process runs with --allow-all in agentic mode (full shell + git access). Used for rework where file deletion, git rm, etc. are needed.</summary>
    bool AgenticAllowAll = false,
    /// <summary>
    /// When true, suppresses injection of Azure image-generation env vars into the CLI process.
    /// Set for judge/review/scoring paths that never generate images — avoids 8s auth delay
    /// from DefaultAzureCredential timeout and reduces log spam.
    /// </summary>
    bool SuppressImageGenEnv = false,
    /// <summary>
    /// When true AND AgenticAllowAll is true, the output format instructions tell the AI
    /// to output the FULL document content instead of a brief summary. Used by PM, Architect,
    /// and Researcher agents that need agentic codebase exploration but whose response IS
    /// the deliverable document (not a summary of file edits).
    /// </summary>
    bool DocumentGenerationMode = false)
{
    /// <summary>True iff any CLI-level tool permission has been granted for this call.</summary>
    public bool AllowToolUsage => AllowedMcpTools is { Count: > 0 } || AllowFileEdits || AgenticAllowAll;
}

/// <summary>
/// Ambient context that flows the current agent ID through async call chains.
/// Set at the agent loop entry point; read by CopilotCliChatCompletionService
/// to attribute AI usage to the correct agent without changing every call site.
/// </summary>
public static class AgentCallContext
{
    private static readonly AsyncLocal<string?> _currentAgentId = new();
    private static readonly AsyncLocal<string?> _currentModel = new();
    private static readonly AsyncLocal<string?> _currentSessionId = new();
    private static readonly AsyncLocal<string?> _currentCallContext = new();

    /// <summary>The agent ID currently executing in this async context.</summary>
    public static string? CurrentAgentId
    {
        get => _currentAgentId.Value;
        set => _currentAgentId.Value = value;
    }

    /// <summary>The model name the current agent is using.</summary>
    public static string? CurrentModel
    {
        get => _currentModel.Value;
        set => _currentModel.Value = value;
    }

    /// <summary>
    /// Human-readable description of what the current LLM call is doing.
    /// Agents set this before invoking the kernel so the dashboard can show
    /// a meaningful status instead of generic "AI call in progress".
    /// </summary>
    public static string? CurrentCallContext
    {
        get => _currentCallContext.Value;
        set => _currentCallContext.Value = value;
    }

    /// <summary>
    /// The Copilot CLI session ID for the current async context.
    /// When set, the CLI process is launched with <c>--resume=sessionId</c>
    /// so that prior conversation context is available.
    /// </summary>
    public static string? CurrentSessionId
    {
        get => _currentSessionId.Value;
        set => _currentSessionId.Value = value;
    }

    private static readonly AsyncLocal<IReadOnlyList<string>?> _mcpServers = new();

    /// <summary>
    /// MCP server names configured for the current agent's async context.
    /// When set, each server name is passed as <c>--mcp-server name</c> to the CLI process.
    /// </summary>
    public static IReadOnlyList<string>? McpServers
    {
        get => _mcpServers.Value;
        set => _mcpServers.Value = value;
    }

    private static readonly AsyncLocal<CopilotCliInvocationContext?> _invocationContext = new();

    /// <summary>
    /// Per-invocation CLI context (inline MCP config, allowed tools, CWD override).
    /// Prefer <see cref="Push"/> over direct assignment so that the value is always
    /// cleared after the scoped operation completes — raw assignment leaves the value
    /// in the AsyncLocal slot and risks bleeding into subsequent calls on the same
    /// logical async flow.
    /// </summary>
    public static CopilotCliInvocationContext? CurrentInvocationContext => _invocationContext.Value;

    /// <summary>
    /// Install <paramref name="ctx"/> as the ambient invocation context and return a
    /// disposable that restores the previous value on dispose. Safe for nesting and
    /// for concurrent unrelated flows: AsyncLocal isolation guarantees per-flow state.
    /// </summary>
    /// <example>
    /// <code>
    /// using var _ = AgentCallContext.PushInvocationContext(new(
    ///     AdditionalMcpConfigJson: json,
    ///     AllowedMcpTools: new[] { "workspace-reader" },
    ///     OverrideWorkingDirectory: candidateRoot));
    /// var result = await kernel.InvokePromptAsync(prompt);
    /// </code>
    /// </example>
    public static IDisposable PushInvocationContext(CopilotCliInvocationContext? ctx)
    {
        var previous = _invocationContext.Value;
        _invocationContext.Value = ctx;
        return new InvocationContextScope(previous);
    }

    private sealed class InvocationContextScope : IDisposable
    {
        private readonly CopilotCliInvocationContext? _previous;
        private bool _disposed;

        public InvocationContextScope(CopilotCliInvocationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _invocationContext.Value = _previous;
        }
    }
}
