using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;
using Xunit;

namespace AgentSquad.Core.Tests;

public class ChatCompletionRunnerTests
{
    private readonly Mock<IChatCompletionService> _mockChat = new();
    private readonly ChatCompletionRunner _runner;

    public ChatCompletionRunnerTests()
    {
        // Build a kernel with our mock chat service
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(_mockChat.Object);
        var kernel = builder.Build();

        // Create a ModelRegistry that returns our test kernel
        var mockRegistry = CreateModelRegistryReturning(kernel);

        _runner = new ChatCompletionRunner(mockRegistry, NullLogger<ChatCompletionRunner>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_WithRequest_ReturnsResponseContent()
    {
        SetupMockResponse("Hello from AI");

        var history = new ChatHistory();
        history.AddUserMessage("test");

        var result = await _runner.InvokeAsync(new ChatCompletionRequest
        {
            History = history,
            ModelTier = "standard"
        });

        Assert.Equal("Hello from AI", result);
    }

    [Fact]
    public async Task InvokeAsync_WithSystemAndUserPrompt_BuildsHistoryAndReturns()
    {
        ChatHistory? capturedHistory = null;
        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .Callback<ChatHistory, PromptExecutionSettings?, Kernel?, CancellationToken>(
                (h, _, _, _) => capturedHistory = h)
            .ReturnsAsync(new List<ChatMessageContent>
            {
                new(AuthorRole.Assistant, "response")
            });

        var result = await _runner.InvokeAsync("sys prompt", "user prompt", "standard");

        Assert.Equal("response", result);
        Assert.NotNull(capturedHistory);
        Assert.Equal(2, capturedHistory!.Count);
        Assert.Equal(AuthorRole.System, capturedHistory[0].Role);
        Assert.Equal("sys prompt", capturedHistory[0].Content);
        Assert.Equal(AuthorRole.User, capturedHistory[1].Role);
        Assert.Equal("user prompt", capturedHistory[1].Content);
    }

    [Fact]
    public async Task InvokeAsync_EmptyResponse_ReturnsEmptyString()
    {
        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent>());

        var result = await _runner.InvokeAsync("sys", "user", "budget");

        Assert.Equal("", result);
    }

    [Fact]
    public async Task InvokeAsync_NullContent_ReturnsEmptyString()
    {
        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent>
            {
                new(AuthorRole.Assistant, content: null)
            });

        var result = await _runner.InvokeAsync("sys", "user", "standard");

        Assert.Equal("", result);
    }

    [Fact]
    public async Task InvokeAsync_SetsAgentCallContext_WhenAgentIdProvided()
    {
        string? capturedAgentId = null;
        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .Returns<ChatHistory, PromptExecutionSettings?, Kernel?, CancellationToken>(
                (_, _, _, _) =>
                {
                    // Capture the AgentCallContext inside the same async flow
                    capturedAgentId = AgentCallContext.CurrentAgentId;
                    return Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                        new List<ChatMessageContent> { new(AuthorRole.Assistant, "ok") });
                });

        await _runner.InvokeAsync("sys", "user", "standard", agentId: "test-agent-42");

        Assert.Equal("test-agent-42", capturedAgentId);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotSetAgentCallContext_WhenAgentIdNull()
    {
        SetupMockResponse("ok");
        AgentCallContext.CurrentAgentId = "previous-agent";

        await _runner.InvokeAsync(new ChatCompletionRequest
        {
            History = new ChatHistory(),
            ModelTier = "standard",
            AgentId = null
        });

        Assert.Equal("previous-agent", AgentCallContext.CurrentAgentId);
    }

    [Fact]
    public async Task InvokeAsync_NullRequest_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _runner.InvokeAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task InvokeAsync_CancellationRequested_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _runner.InvokeAsync("sys", "user", "standard", ct: cts.Token));
    }

    private void SetupMockResponse(string content)
    {
        _mockChat
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent>
            {
                new(AuthorRole.Assistant, content)
            });
    }

    /// <summary>
    /// Creates a minimal ModelRegistry that always returns the given kernel regardless of tier.
    /// Uses reflection or a test-friendly approach since ModelRegistry.GetKernel is not virtual.
    /// </summary>
    private static ModelRegistry CreateModelRegistryReturning(Kernel kernel)
    {
        // ModelRegistry needs a config with at least one model entry for the tier
        var config = new AgentSquadConfig
        {
            Models = new Dictionary<string, ModelConfig>
            {
                ["standard"] = new() { Provider = "test", Model = "test-model" },
                ["budget"] = new() { Provider = "test", Model = "test-budget" },
                ["premium"] = new() { Provider = "test", Model = "test-premium" },
            }
        };

        var loggerFactory = NullLoggerFactory.Instance;
        var usageTracker = new AgentUsageTracker(null!);
        var llmTracker = new ActiveLlmCallTracker();

        return new TestableModelRegistry(config, loggerFactory, usageTracker, llmTracker, kernel);
    }

    /// <summary>
    /// Test double that overrides GetKernel to return a pre-configured kernel.
    /// </summary>
    private class TestableModelRegistry : ModelRegistry
    {
        private readonly Kernel _kernel;

        public TestableModelRegistry(
            AgentSquadConfig config,
            ILoggerFactory loggerFactory,
            AgentUsageTracker usageTracker,
            ActiveLlmCallTracker llmCallTracker,
            Kernel kernel)
            : base(config, loggerFactory, usageTracker, llmCallTracker)
        {
            _kernel = kernel;
        }

        public override Kernel GetKernel(string modelTier, string? agentId = null) => _kernel;
    }
}
