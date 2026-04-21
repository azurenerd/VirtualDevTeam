using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Mcp;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// DI registration helpers for the strategy framework. Consumers (Runner) call
/// <see cref="AddStrategyFramework"/> after <c>Configure&lt;StrategyFrameworkConfig&gt;(...)</c>.
/// </summary>
public static class StrategyFrameworkServiceCollectionExtensions
{
    public static IServiceCollection AddStrategyFramework(this IServiceCollection services)
    {
        services.AddSingleton<GitWorktreeManager>();
        services.AddSingleton<ExperimentTracker>();
        services.TryAddSingleton<StrategyConcurrencyGate>();
        services.AddSingleton<CandidateEvaluator>();
        services.AddSingleton<StrategyOrchestrator>();
        services.AddSingleton<RunBudgetTracker>();
        services.AddSingleton<WinnerApplyService>();
        services.AddSingleton<StrategySamplingPolicy>();
        services.AddSingleton<AdaptiveStrategySelector>();
        services.AddSingleton<ILlmJudge, NullLlmJudge>();

        // MCP server locator: default implementation probes disk for the server DLL.
        services.AddSingleton<IMcpServerLocator, DefaultMcpServerLocator>();

        // Baseline ships enabled by default (plan decision).
        services.AddSingleton<ICodeGenerationStrategy, BaselineStrategy>();

        // MCP-enhanced strategy. Active only when the master switch
        // (StrategyFrameworkConfig.Enabled) is on AND "mcp-enhanced" is listed
        // in EnabledStrategies — both default to off / present in config respectively,
        // so wiring the service here is safe.
        services.AddSingleton<ICodeGenerationStrategy, McpEnhancedStrategy>();

        // Agentic-delegation strategy (Phase 3). Wired via DI but NOT in the
        // default EnabledStrategies list — opt-in by design because it runs
        // `copilot --allow-all` inside the sandboxed worktree. Enable only on
        // trusted dev machines.
        services.AddSingleton<AgenticPromptBuilder>();
        services.AddSingleton<ICodeGenerationStrategy, AgenticDelegationStrategy>();

        // Default sink is the null sink; Runner overrides with a SignalR-bound one.
        services.AddSingleton<IStrategyEventSink>(_ => NullStrategyEventSink.Instance);

        // Phase 4: live candidate state tracking (for the dashboard /strategies page).
        // Store is always registered; the Runner adds the IStrategyBroadcaster implementation
        // and swaps IStrategyEventSink to StrategyEventBroadcaster via an explicit call.
        services.AddSingleton<CandidateStateStore>(_ => new CandidateStateStore());

        return services;
    }

    /// <summary>
    /// Called by hosts that expose a SignalR dashboard: replaces the null
    /// <see cref="IStrategyEventSink"/> with <see cref="StrategyEventBroadcaster"/>
    /// (wired against the <see cref="CandidateStateStore"/> and a provided broadcaster),
    /// then wraps it with <see cref="StrategyTaskStepBridge"/> for live task-step tracking.
    /// </summary>
    public static IServiceCollection AddStrategyDashboard(this IServiceCollection services)
    {
        // Register the broadcaster as a named inner implementation
        services.AddSingleton<StrategyEventBroadcaster>();
        // The bridge decorates the broadcaster and adds task-step tracking
        services.AddSingleton<StrategyTaskStepBridge>(sp =>
            new StrategyTaskStepBridge(
                sp.GetRequiredService<StrategyEventBroadcaster>(),
                sp.GetRequiredService<IAgentTaskTracker>(),
                sp.GetRequiredService<ILogger<StrategyTaskStepBridge>>(),
                sp.GetService<IOptions<StrategyFrameworkConfig>>()));
        // Expose the bridge as the primary event sink
        services.Replace(ServiceDescriptor.Singleton<IStrategyEventSink>(sp =>
            sp.GetRequiredService<StrategyTaskStepBridge>()));
        return services;
    }
}
