using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Extension methods for registering the App Playtester services in a dependency injection container.
/// </summary>
public static class PlaytestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the App Playtester. When <c>CopilotCli.Enabled</c> is <see langword="true"/>
    /// (the default), the CLI-agentic implementation (<see cref="CliAppPlaytester"/>) is used —
    /// it launches a Copilot CLI session with <c>--allow-all</c> that autonomously verifies
    /// each scenario via Playwright MCP tools. When disabled, falls back to the legacy
    /// <see cref="AppPlaytester"/> with JSON action plans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CliAppPlaytester"/> requires:
    /// <list type="bullet">
    ///   <item><c>IScenarioRegistry</c> — registered by <c>AddScenarios()</c></item>
    ///   <item><c>CopilotCliProcessManager</c> — registered by the Runner's CLI setup</item>
    /// </list>
    /// </para>
    /// <para>
    /// Legacy <see cref="AppPlaytester"/> additionally requires adapters and <c>IChatCompletionRunner</c>.
    /// The adapters and their registrations are kept for backward compatibility.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPlaytester(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Named HttpClient for the legacy API adapter
        services.AddHttpClient("ApiPlaytestAdapter")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        // Legacy adapters (kept for fallback path)
        services.AddTransient<WebPlaytestAdapter>();
        services.AddTransient<ApiPlaytestAdapter>();
        services.AddTransient<CliPlaytestAdapter>();
        services.AddTransient<IPlaytestAdapter>(sp => sp.GetRequiredService<WebPlaytestAdapter>());
        services.AddTransient<IPlaytestAdapter>(sp => sp.GetRequiredService<ApiPlaytestAdapter>());
        services.AddTransient<IPlaytestAdapter>(sp => sp.GetRequiredService<CliPlaytestAdapter>());

        // Route to CLI-agentic playtester when Copilot CLI is available, legacy otherwise.
        // Resolved at first use (singleton) so the config is already loaded.
        services.AddSingleton<IAppPlaytester>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            if (config.CopilotCli?.Enabled == true)
            {
                var processManager = sp.GetService<CopilotCliProcessManager>();
                if (processManager is not null)
                {
                    var logger = sp.GetRequiredService<ILogger<CliAppPlaytester>>();
                    logger.LogInformation("AppPlaytester: using CLI-agentic implementation (CopilotCli.Enabled=true)");
                    return new CliAppPlaytester(
                        sp.GetRequiredService<Scenarios.IScenarioRegistry>(),
                        processManager,
                        config.ScenarioVerification,
                        logger,
                        sp.GetService<HealthMonitor.FlowMonitorPersistence>());
                }
            }

            // Fallback to legacy JSON-based playtester
            var legacyLogger = sp.GetRequiredService<ILogger<AppPlaytester>>();
            legacyLogger.LogInformation("AppPlaytester: using legacy JSON-based implementation");
            return new AppPlaytester(
                sp.GetRequiredService<Scenarios.IScenarioRegistry>(),
                sp.GetRequiredService<IChatCompletionRunner>(),
                sp.GetServices<IPlaytestAdapter>(),
                legacyLogger);
        });

        return services;
    }
}
