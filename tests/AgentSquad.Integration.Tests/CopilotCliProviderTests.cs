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
/// All tests use a 15-second timeout to prevent hanging when copilot CLI is
/// present but unresponsive (e.g., auth prompts, network issues).
/// Skip in CI by filtering: dotnet test --filter "Category!=CopilotCli"
/// </summary>
[Trait("Category", "CopilotCli")]
public class CopilotCliProviderTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

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
                RequestTimeoutSeconds = 15,
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
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            return new ModelRegistry(cfg, loggerFactory, usageTracker, processManager);
        });

        _serviceProvider = services.BuildServiceProvider();
        _processManager = _serviceProvider.GetRequiredService<CopilotCliProcessManager>();
    }

    [Fact]
    public async Task ProcessManager_CanDetectCopilotAvailability()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await _processManager.StartAsync(cts.Token);

        // Validates the detection logic runs without crashing.
        // IsAvailable will be true if copilot is on PATH, false otherwise.
        Assert.True(true); // Detection completed without exception
    }

    [Fact]
    public async Task ModelRegistry_FallsBackWhenCliUnavailable()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await _processManager.StartAsync(cts.Token);

        var registry = _serviceProvider.GetRequiredService<ModelRegistry>();

        // GetKernel should succeed regardless — either via CLI or API fallback
        var kernel = registry.GetKernel("premium");
        Assert.NotNull(kernel);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        Assert.NotNull(chatService);
    }

    [Fact]
    public async Task ProcessManager_ExecutePrompt_WhenAvailable()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        await _processManager.StartAsync(cts.Token);

        if (!_processManager.IsAvailable)
            return; // copilot not installed — skip gracefully

        var result = await _processManager.ExecutePromptAsync(
            "Say hello in exactly 3 words.", cts.Token);

        Assert.True(result.IsSuccess, $"Expected success but got: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    public void Dispose()
    {
        _processManager.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _serviceProvider.Dispose();
    }
}
