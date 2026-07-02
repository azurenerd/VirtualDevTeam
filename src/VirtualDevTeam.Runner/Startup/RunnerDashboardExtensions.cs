// NoMessyCodePlan Theme 4d: Program.cs split — Dashboard (UI) registration.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Dashboard.Services;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Dashboard registration split into two layers so headless mode can register
/// the data/API services (<see cref="AddRunnerDashboardServices"/>) without
/// pulling in Blazor Server components (<see cref="AddRunnerDashboardUI"/>).
/// Must register after <see cref="RunnerOrchestrationExtensions"/>
/// since <see cref="DashboardDataService"/> depends on AgentRegistry.
/// </summary>
public static class RunnerDashboardExtensions
{
    /// <summary>
    /// Registers dashboard data services needed by REST API endpoints and hosted
    /// services. Always called — even in headless mode — because the API layer
    /// (e.g. /api/dashboard/*, /api/develop/*, /api/preview/*) depends on these.
    /// Also registers SignalR so <c>IHubContext&lt;T&gt;</c> resolves for hosted
    /// services like <see cref="DashboardDataService"/> and FlowMonitorEventRelay;
    /// hubs simply have zero clients in headless mode.
    /// </summary>
    public static IServiceCollection AddRunnerDashboardServices(this IServiceCollection services)
    {
        // SignalR — required so IHubContext<T> resolves for hosted services
        // (DashboardDataService, FlowMonitorEventRelay, AgentLogRelay).
        // In headless mode no hubs are mapped; broadcasts are no-ops.
        // MaximumReceiveMessageSize raised from 32KB default to 512KB to support large
        // paste operations in the wizard description textarea (22KB+ project descriptions).
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 512 * 1024; // 512KB
        });

        // Dashboard data services consumed by API endpoints.
        services.AddSingleton<AgentSnapshotService>();
        services.AddSingleton<DiagnosticSummaryService>();
        services.AddSingleton<ExecutionTimelineService>();
        services.AddSingleton<DashboardDataService>();
        services.AddSingleton<IDashboardDataService>(sp => sp.GetRequiredService<DashboardDataService>());
        services.AddHostedService(sp => sp.GetRequiredService<DashboardDataService>());

        // Internal-nav link service + Configuration wizard service.
        services.AddSingleton<IPlatformLinkService, PlatformLinkService>();
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());
        services.AddSingleton<DevelopSettingsService>();

        // Preview build (Testing page tab 1) + test-artifact index (Testing page tab 2).
        services.AddSingleton<VirtualDevTeam.Core.Preview.PreviewBuildService>(sp =>
            new VirtualDevTeam.Core.Preview.PreviewBuildService(
                sp.GetRequiredService<ILogger<VirtualDevTeam.Core.Preview.PreviewBuildService>>(),
                sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>(),
                sp.GetService<VirtualDevTeam.Core.AI.CopilotCliProcessManager>()));
        services.AddSingleton<VirtualDevTeam.Core.Preview.TestArtifactIndexService>();

        // Strategies page artifact-server.
        services.AddSingleton<VirtualDevTeam.Core.Frameworks.CandidateArtifactService>();

        // Mode marker — bundled (Runner serves both API + UI) vs standalone (Dashboard.Host).
        services.AddSingleton(new DashboardMode(IsStandalone: false));

        // Strategies page data source (in-process when bundled).
        services.AddSingleton<IStrategiesDataService, InProcessStrategiesDataService>();

        return services;
    }

    /// <summary>
    /// Registers Blazor Server interactive rendering and UI-only services
    /// (Director CLI, prerequisite checker, scenario wizard). Skipped in
    /// headless mode since there are no browser clients.
    /// </summary>
    public static IServiceCollection AddRunnerDashboardUI(this IServiceCollection services)
    {
        // Blazor Server interactive components.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Director CLI command palette (Configuration → Director tab).
        // Registered as singleton + hosted service so it pre-warms a session on startup.
        services.AddSingleton<DirectorCliService>();
        services.AddHostedService(sp => sp.GetRequiredService<DirectorCliService>());
        services.AddSingleton<PrerequisiteCheckService>();

        // Note: Scenario registry (AddScenarios) is registered in RunnerCoreServicesExtensions —
        // do NOT duplicate here. Dashboard page injects IScenarioRegistry from the same singleton.

        // Scenario generation wizard service — uses Copilot CLI to generate scenario drafts in the wizard.
        // Factory registration is required so optional deps (CopilotCliProcessManager,
        // IPromptTemplateService) resolve to null when not registered in this host.
        services.AddSingleton<ScenarioGenerationService>(sp => new ScenarioGenerationService(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualDevTeamConfig>>(),
            sp.GetRequiredService<ILogger<ScenarioGenerationService>>(),
            sp.GetService<CopilotCliProcessManager>(),
            sp.GetService<IPromptTemplateService>()));

        return services;
    }

    /// <summary>
    /// Convenience: registers both data services and UI components.
    /// Equivalent to calling <see cref="AddRunnerDashboardServices"/> followed
    /// by <see cref="AddRunnerDashboardUI"/>. Used in non-headless mode.
    /// </summary>
    public static IServiceCollection AddRunnerDashboard(this IServiceCollection services)
    {
        return services
            .AddRunnerDashboardServices()
            .AddRunnerDashboardUI();
    }
}
