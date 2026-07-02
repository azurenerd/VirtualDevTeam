namespace VirtualDevTeam.Core.Configuration;

using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class SemanticKernelExtensions
{
    /// <summary>
    /// Registers the <see cref="ModelRegistry"/> as a singleton, wired to the
    /// <see cref="VirtualDevTeamConfig"/> options and the host's ILoggerFactory.
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

        // Register the Copilot CLI process manager (checks availability at startup).
        //
        // CRITICAL: pass the optional dependencies (RunnerProcessJob, IAzureImageAuthProvider)
        // explicitly. Their omission caused the 2026-05-12 incident where every CLI candidate
        // saw AZURE_OPENAI_IMAGE_* env vars empty (silently returned by ApplyImageGenEnvVars'
        // null-guard at CopilotCliProcessManager.cs:151), so agentic image-gen sessions reported
        // "No Azure OpenAI credentials are available" and produced ABSENT artifacts even though
        // the wizard had configured a working endpoint+deployment.
        // Use GetService (not GetRequiredService) so the runner can still boot if the provider
        // isn't registered (e.g., in test harnesses).
        services.AddSingleton<CopilotCliProcessManager>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>();
            var frameworkConfig = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>();
            var gate = sp.GetRequiredService<StrategyConcurrencyGate>();
            var logger = sp.GetRequiredService<ILogger<CopilotCliProcessManager>>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<VirtualDevTeamConfig>>();
            var runnerJob = sp.GetService<VirtualDevTeam.Core.AI.RunnerProcessJob>();
            var imageAuth = sp.GetService<VirtualDevTeam.Core.AI.IAzureImageAuthProvider>();
            var agentLogService = sp.GetService<VirtualDevTeam.Core.AI.AgentCliLogService>();
            return new CopilotCliProcessManager(config, frameworkConfig, gate, logger, monitor, runnerJob, imageAuth, agentLogService);
        });
        services.AddHostedService(sp => sp.GetRequiredService<CopilotCliProcessManager>());

        // Register ModelRegistry with optional CopilotCliProcessManager
        services.AddSingleton<ModelRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var llmCallTracker = sp.GetRequiredService<ActiveLlmCallTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<VirtualDevTeamConfig>>();
            return new ModelRegistry(config, loggerFactory, usageTracker, llmCallTracker, processManager, monitor);
        });

        return services;
    }
}
