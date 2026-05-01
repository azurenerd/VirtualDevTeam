using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.AI;

/// <summary>
/// Encapsulates all parameters for a single chat completion invocation.
/// Use this with <see cref="IChatCompletionRunner"/> to eliminate the
/// repeated kernel-resolve → context-set → invoke → extract pattern.
/// </summary>
public sealed record ChatCompletionRequest
{
    /// <summary>The conversation history to send.</summary>
    public required ChatHistory History { get; init; }

    /// <summary>
    /// Model tier to use (e.g. "premium", "standard", "budget").
    /// Maps to a configured kernel via <see cref="Configuration.ModelRegistry"/>.
    /// </summary>
    public required string ModelTier { get; init; }

    /// <summary>
    /// Optional agent ID for usage attribution and per-agent model overrides.
    /// When set, <see cref="AgentCallContext.CurrentAgentId"/> is updated.
    /// </summary>
    public string? AgentId { get; init; }
}
