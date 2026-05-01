using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.AI;

/// <summary>
/// Unified abstraction for invoking AI chat completions across the codebase.
/// Encapsulates ModelRegistry kernel resolution, AgentCallContext setup,
/// and response extraction — eliminating the repeated 8-line boilerplate
/// pattern found in every agent and many Core services.
/// </summary>
public interface IChatCompletionRunner
{
    /// <summary>
    /// Invoke a chat completion using a pre-built ChatHistory.
    /// </summary>
    Task<string> InvokeAsync(ChatCompletionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Convenience: invoke with a system prompt and user prompt (single-turn).
    /// </summary>
    Task<string> InvokeAsync(string systemPrompt, string userPrompt, string modelTier, string? agentId = null, CancellationToken ct = default);
}
