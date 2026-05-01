using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.AI;

/// <summary>
/// Default implementation of <see cref="IChatCompletionRunner"/>.
/// Resolves a kernel from <see cref="ModelRegistry"/>, sets the ambient
/// <see cref="AgentCallContext"/>, invokes chat completion, and extracts
/// the text response — all in one call.
/// </summary>
public sealed class ChatCompletionRunner : IChatCompletionRunner
{
    private readonly ModelRegistry _modelRegistry;
    private readonly ILogger<ChatCompletionRunner> _logger;

    public ChatCompletionRunner(ModelRegistry modelRegistry, ILogger<ChatCompletionRunner> logger)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kernel = _modelRegistry.GetKernel(request.ModelTier, request.AgentId);
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var previousAgentId = AgentCallContext.CurrentAgentId;
        try
        {
            if (request.AgentId is not null)
            {
                AgentCallContext.CurrentAgentId = request.AgentId;
            }

            var response = await chatService.GetChatMessageContentsAsync(request.History, cancellationToken: ct);
            return response.FirstOrDefault()?.Content ?? "";
        }
        finally
        {
            AgentCallContext.CurrentAgentId = previousAgentId;
        }
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(
        string systemPrompt,
        string userPrompt,
        string modelTier,
        string? agentId = null,
        CancellationToken ct = default)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        return await InvokeAsync(new ChatCompletionRequest
        {
            History = history,
            ModelTier = modelTier,
            AgentId = agentId
        }, ct);
    }
}
