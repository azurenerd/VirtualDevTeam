using VirtualDevTeam.Core.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// IChatCompletionRunner that bypasses ModelRegistry and delegates directly
/// to a ScriptedChatCompletionService. This avoids needing real API keys
/// or Copilot CLI in E2E tests.
/// </summary>
public sealed class ScriptedChatCompletionRunner : IChatCompletionRunner
{
    private readonly ScriptedChatCompletionService _chatService;

    public ScriptedChatCompletionRunner(ScriptedChatCompletionService chatService)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
    }

    public async Task<string> InvokeAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await _chatService.GetChatMessageContentsAsync(request.History, cancellationToken: ct);
        return response.FirstOrDefault()?.Content ?? "";
    }

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
