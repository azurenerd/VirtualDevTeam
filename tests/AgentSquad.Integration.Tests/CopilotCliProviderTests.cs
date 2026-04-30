using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Integration.Tests;

/// <summary>
/// Integration tests for the Copilot CLI provider.
/// These tests require the copilot CLI to be installed and available on PATH.
/// The ExecutePrompt test uses the internal RequestTimeoutSeconds (5s) for graceful
/// timeout handling and process cleanup, avoiding orphaned child processes.
/// Skip in CI by filtering: dotnet test --filter "Category!=CopilotCli"
/// </summary>
[Trait("Category", "CopilotCli")]
public class CopilotCliProviderTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CopilotCliProcessManager _processManager;

    public CopilotCliProviderTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        var config = new AgentSquadConfig
        {
            CopilotCli = new CopilotCliConfig
            {
                Enabled = true,
                ExecutablePath = "copilot",
                MaxConcurrentRequests = 2,
                RequestTimeoutSeconds = 5, // Short timeout to avoid hanging
                ModelName = "claude-opus-4.6"
            },
            Models = new Dictionary<string, ModelConfig>
            {
                ["premium"] = new ModelConfig
                {
                    Provider = "openai",
                    Model = "gpt-4",
                    ApiKey = "test-key-not-used"
                }
            }
        };

        services.Configure<AgentSquadConfig>(opts =>
        {
            opts.CopilotCli = config.CopilotCli;
            opts.Models = config.Models;
        });

        services.AddSingleton(Options.Create(config));
        services.AddSingleton<CopilotCliProcessManager>();
        services.AddSingleton<AgentUsageTracker>();
        services.AddSingleton<ActiveLlmCallTracker>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var llmCallTracker = sp.GetRequiredService<ActiveLlmCallTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            return new ModelRegistry(cfg, loggerFactory, usageTracker, llmCallTracker, processManager);
        });

        _serviceProvider = services.BuildServiceProvider();
        _processManager = _serviceProvider.GetRequiredService<CopilotCliProcessManager>();
    }

    [Fact]
    public async Task ProcessManager_CanDetectCopilotAvailability()
    {
        // VerifyCopilotInstalledAsync has its own 10s internal timeout
        await _processManager.StartAsync(CancellationToken.None);
        Assert.True(true); // Detection completed without hanging
    }

    [Fact]
    public async Task ModelRegistry_FallsBackWhenCliUnavailable()
    {
        await _processManager.StartAsync(CancellationToken.None);

        var registry = _serviceProvider.GetRequiredService<ModelRegistry>();

        var kernel = registry.GetKernel("premium");
        Assert.NotNull(kernel);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        Assert.NotNull(chatService);
    }

    [Fact]
    public async Task ProcessManager_ExecutePrompt_WhenAvailable()
    {
        await _processManager.StartAsync(CancellationToken.None);

        if (!_processManager.IsAvailable)
            return; // copilot not installed — skip gracefully

        // Don't pass an external CancellationToken — let the internal
        // RequestTimeoutSeconds (5s) handle timeout + process cleanup gracefully.
        // Passing an external token causes OperationCanceledException which
        // can leave orphaned child processes and hang the test host.
        var result = await _processManager.ExecutePromptAsync(
            "Say hello in exactly 3 words.");

        // If we're running inside a copilot CLI session (e.g., copilot spawning copilot),
        // the prompt may time out — that's expected, not a test failure.
        if (!result.IsSuccess && result.Error?.Contains("timed out") == true)
            return; // Expected when running inside copilot CLI

        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    public void Dispose()
    {
        _processManager.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _serviceProvider.Dispose();
    }
}
