namespace AgentSquad.Core.AI;

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
    /// The Copilot CLI session ID for the current async context.
    /// When set, the CLI process is launched with <c>--resume=sessionId</c>
    /// so that prior conversation context is available.
    /// </summary>
    public static string? CurrentSessionId
    {
        get => _currentSessionId.Value;
        set => _currentSessionId.Value = value;
    }
}
