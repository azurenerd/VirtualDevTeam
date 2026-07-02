using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Core.Diagnostics;

/// <summary>
/// A chat message exchanged between a human operator and an agent via the dashboard.
/// </summary>
public sealed record AgentChatMessage
{
    public required string Role { get; init; } // "user" or "assistant"
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Manages per-agent chat sessions where a human operator can ask questions
/// or give instructions to an agent through the dashboard. Uses the agent's
/// AI model to generate context-aware responses that include the agent's
/// current state, role expectations, memory, and project context.
/// </summary>
public sealed class AgentChatService
{
    private readonly ModelRegistry _modelRegistry;
    private readonly RequirementsCache _requirementsCache;
    private readonly AgentMemoryStore _memoryStore;
    private readonly IAgentTaskTracker? _taskTracker;
    private readonly ILogger<AgentChatService> _logger;

    // Per-agent chat histories (agentId → messages)
    private readonly Dictionary<string, List<AgentChatMessage>> _histories = new();
    private readonly object _lock = new();

    public AgentChatService(
        ModelRegistry modelRegistry,
        RequirementsCache requirementsCache,
        AgentMemoryStore memoryStore,
        ILogger<AgentChatService> logger,
        IAgentTaskTracker? taskTracker = null)
    {
        _modelRegistry = modelRegistry;
        _requirementsCache = requirementsCache;
        _memoryStore = memoryStore;
        _taskTracker = taskTracker;
        _logger = logger;
    }

    /// <summary>Get the chat history for an agent.</summary>
    public IReadOnlyList<AgentChatMessage> GetHistory(string agentId)
    {
        lock (_lock)
        {
            return _histories.TryGetValue(agentId, out var history)
                ? history.ToList()
                : [];
        }
    }

    /// <summary>Clear chat history for an agent.</summary>
    public void ClearHistory(string agentId)
    {
        lock (_lock) { _histories.Remove(agentId); }
    }

    /// <summary>
    /// Send a message from the operator to an agent and get an AI-generated response.
    /// The response is contextualised with the agent's role, current state, memory, and requirements.
    /// Operator instructions are automatically stored as agent memories.
    /// </summary>
    public async Task<AgentChatMessage> SendMessageAsync(
        IAgent agent,
        string userMessage,
        CancellationToken ct = default)
    {
        var agentId = agent.Identity.Id;

        // Record user message
        var userMsg = new AgentChatMessage { Role = "user", Content = userMessage };
        lock (_lock)
        {
            if (!_histories.TryGetValue(agentId, out var history))
            {
                history = new List<AgentChatMessage>();
                _histories[agentId] = history;
            }
            history.Add(userMsg);
        }

        // Store operator instructions as agent memories so they persist
        // across AI calls and the agent can reference them in future work
        if (LooksLikeInstruction(userMessage))
        {
            try
            {
                await _memoryStore.StoreAsync(agentId, MemoryType.Instruction,
                    $"Operator instruction: {Truncate(userMessage, 200)}",
                    userMessage, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store operator instruction as memory for {AgentId}", agentId);
            }
        }

        try
        {
            var kernel = _modelRegistry.GetKernel(agent.Identity.ModelTier, agentId);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var chatHistory = await BuildChatHistoryAsync(agent, userMessage, ct);
            var response = await chat.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);
            var responseText = response.Content?.Trim() ?? "(No response)";

            var assistantMsg = new AgentChatMessage { Role = "assistant", Content = responseText };
            lock (_lock)
            {
                if (_histories.TryGetValue(agentId, out var history))
                    history.Add(assistantMsg);
            }

            _logger.LogInformation("Agent chat for {AgentId}: user asked, agent responded ({Len} chars)",
                agentId, responseText.Length);

            return assistantMsg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get chat response for agent {AgentId}", agentId);
            var errorMsg = new AgentChatMessage
            {
                Role = "assistant",
                Content = $"⚠️ Error generating response: {ex.Message}"
            };
            lock (_lock)
            {
                if (_histories.TryGetValue(agentId, out var history))
                    history.Add(errorMsg);
            }
            return errorMsg;
        }
    }

    private async Task<ChatHistory> BuildChatHistoryAsync(
        IAgent agent, string latestUserMessage, CancellationToken ct)
    {
        var id = agent.Identity;
        var diag = agent.CurrentDiagnostic;
        var roleSection = _requirementsCache.GetRoleSection(id.Role);

        // Load the agent's memory context for inclusion in the prompt
        var memoryContext = await _memoryStore.GetMemoryContextAsync(id.Id, maxEntries: 30, ct);

        // Live session/task context — gives the agent a live picture of "what am I doing
        // right now in this run" so chat answers feel current rather than detached.
        // Without this, every chat answer cited only static role expectations + memory
        // which made the responses feel like they had no awareness of the current PR/task.
        var sessionContext = BuildSessionContext(id.Id);

        var systemPrompt = $"""
            You are the AI persona of **{id.DisplayName}**, a {id.Role} agent in the VirtualDevTeam system.
            You are responding to a human operator who is monitoring and managing the agent team through the dashboard.

            ## Your Current State
            - **Status:** {agent.Status}
            - **Status Reason:** {agent.StatusReason ?? "none"}
            - **Assigned PR:** {id.AssignedPullRequest ?? "none"}
            - **Model Tier:** {id.ModelTier}

            ## Your Live Session (current task tracker)
            {(string.IsNullOrEmpty(sessionContext) ? "No active step right now — agent is idle or between tasks." : sessionContext)}

            ## Your Self-Diagnostic
            - **Summary:** {diag?.Summary ?? "not yet generated"}
            - **Justification:** {diag?.Justification ?? "not yet generated"}
            - **Compliant:** {diag?.IsCompliant ?? true}
            - **Scenario Ref:** {diag?.ScenarioRef ?? "none"}

            ## Your Memory (actions, decisions, and learnings from this session)
            {(string.IsNullOrEmpty(memoryContext) ? "No memories recorded yet — this is a fresh session or you haven't performed any actions." : memoryContext)}

            ## Your Role Requirements (from Requirements.md)
            {(string.IsNullOrEmpty(roleSection) ? "No role section loaded." : roleSection[..Math.Min(roleSection.Length, 3000)])}

            ## Instructions
            - Answer questions about what you are doing and why, using your live session context first, then memory of past actions.
            - If the operator asks "what are you working on right now?" — cite the current step and recent steps from the Live Session section above.
            - If the operator gives you instructions or corrections, acknowledge them clearly. These instructions will be stored in your memory and influence your future behavior.
            - Be honest — if you don't know something or if you think you may be doing something wrong, say so.
            - Reference specific scenario steps or requirement sections when explaining your behavior.
            - Keep responses concise but informative. Use bullet points for clarity.
            - You cannot directly execute actions from this chat — explain what your agent loop would do next.
            """;

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);

        // Add prior conversation turns (last 10 to stay within context)
        List<AgentChatMessage> priorMessages;
        lock (_lock)
        {
            priorMessages = _histories.TryGetValue(agent.Identity.Id, out var h)
                ? h.TakeLast(10).ToList()
                : [];
        }

        foreach (var msg in priorMessages)
        {
            if (msg.Content == latestUserMessage && msg.Role == "user"
                && msg == priorMessages[^1])
                continue; // Skip the latest — we'll add it below

            if (msg.Role == "user")
                history.AddUserMessage(msg.Content);
            else
                history.AddAssistantMessage(msg.Content);
        }

        history.AddUserMessage(latestUserMessage);
        return history;
    }

    /// <summary>
    /// Builds a Live-Session context block from the task tracker. Lists the current
    /// in-progress step plus the last 5 completed steps for the agent. If the task
    /// tracker isn't injected, returns an empty string and the system prompt falls
    /// back to a generic "no active step" message.
    /// </summary>
    private string BuildSessionContext(string agentId)
    {
        if (_taskTracker is null) return "";

        try
        {
            var current = _taskTracker.GetCurrentStep(agentId);
            var allSteps = _taskTracker.GetSteps(agentId);
            var recent = allSteps
                .Where(s => s.Status == AgentTaskStepStatus.Completed || s.Status == AgentTaskStepStatus.Failed)
                .OrderByDescending(s => s.CompletedAt ?? DateTime.MinValue)
                .Take(5)
                .ToList();

            if (current is null && recent.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            if (current is not null)
            {
                sb.Append("- **Current step:** ").Append(current.Name);
                if (!string.IsNullOrEmpty(current.TaskId)) sb.Append(" (task ").Append(current.TaskId).Append(')');
                if (current.Elapsed is { } elapsed) sb.Append(" — running for ").Append(FormatElapsed(elapsed));
                if (!string.IsNullOrEmpty(current.Description)) sb.Append(": ").Append(Truncate(current.Description, 200));
                sb.AppendLine();
            }
            if (recent.Count > 0)
            {
                sb.AppendLine("- **Recent completed steps (latest first):**");
                foreach (var step in recent)
                {
                    sb.Append("  - ").Append(step.Status).Append(": ").Append(step.Name);
                    if (!string.IsNullOrEmpty(step.TaskId)) sb.Append(" (task ").Append(step.TaskId).Append(')');
                    if (step.Elapsed is { } e) sb.Append(" — ").Append(FormatElapsed(e));
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build session context for {AgentId}", agentId);
            return "";
        }
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0.0}m";
        return $"{ts.TotalHours:0.0}h";
    }

    /// <summary>
    /// Heuristic: if the message contains imperative language or correction patterns,
    /// treat it as an instruction that should be stored in the agent's memory.
    /// </summary>
    private static bool LooksLikeInstruction(string message)
    {
        var lower = message.ToLowerInvariant();
        string[] patterns =
        [
            "going forward", "from now on", "always ", "never ", "make sure",
            "don't ", "do not ", "you should", "you must", "please ensure",
            "stop doing", "start doing", "change your", "instead of",
            "be sure to", "remember to", "i want you to"
        ];
        return patterns.Any(lower.Contains);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "…";
    }
}
