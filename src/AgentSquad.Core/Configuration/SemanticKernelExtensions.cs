namespace AgentSquad.Core.Configuration;

using AgentSquad.Core.AI;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class SemanticKernelExtensions
{
    /// <summary>
    /// Registers the <see cref="ModelRegistry"/> as a singleton, wired to the
    /// <see cref="AgentSquadConfig"/> options and the host's ILoggerFactory.
    /// Also registers the <see cref="CopilotCliProcessManager"/> if Copilot CLI is enabled.
    /// </summary>
    public static IServiceCollection AddSemanticKernelModels(this IServiceCollection services)
    {
        // StrategyConcurrencyGate is a global cap above the CopilotCliProcessManager
        // per-pool semaphores. The process manager depends on it, so we register it
        // here (idempotent) to keep DI resolvable even when the strategy framework
        // itself is disabled. AddStrategyFramework uses TryAddSingleton too, so the
        // two registration paths coexist.
        services.TryAddSingleton<StrategyConcurrencyGate>();

        // Register the Copilot CLI process manager (checks availability at startup)
        services.AddSingleton<CopilotCliProcessManager>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AgentSquadConfig>>();
            var frameworkConfig = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>();
            var gate = sp.GetRequiredService<StrategyConcurrencyGate>();
            var logger = sp.GetRequiredService<ILogger<CopilotCliProcessManager>>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<AgentSquadConfig>>();
            return new CopilotCliProcessManager(config, frameworkConfig, gate, logger, monitor);
        });
        services.AddHostedService(sp => sp.GetRequiredService<CopilotCliProcessManager>());

        // Register ModelRegistry with optional CopilotCliProcessManager
        services.AddSingleton<ModelRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var llmCallTracker = sp.GetRequiredService<ActiveLlmCallTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<AgentSquadConfig>>();
            return new ModelRegistry(config, loggerFactory, usageTracker, llmCallTracker, processManager, monitor);
        });

        return services;
    }
}
