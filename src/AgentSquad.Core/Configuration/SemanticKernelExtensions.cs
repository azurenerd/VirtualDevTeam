namespace AgentSquad.Core.Configuration;

using AgentSquad.Core.AI;
using Microsoft.Extensions.DependencyInjection;
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
        // Register the Copilot CLI process manager (checks availability at startup)
        services.AddSingleton<CopilotCliProcessManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CopilotCliProcessManager>());

        // Register ModelRegistry with optional CopilotCliProcessManager
        services.AddSingleton<ModelRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var llmCallTracker = sp.GetRequiredService<ActiveLlmCallTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            return new ModelRegistry(config, loggerFactory, usageTracker, llmCallTracker, processManager);
        });

        return services;
    }
}
