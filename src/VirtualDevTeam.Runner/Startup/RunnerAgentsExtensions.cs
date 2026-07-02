// NoMessyCodePlan Theme 4d: Program.cs split — Agent infrastructure registration.
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Services;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Agent infrastructure that sits between core services and the agent constructors:
/// SME definition pipeline, MCP server registry + security policy, gate-check + notification
/// channels, and the Copilot CLI's MCP config manager. Notifications + GateCheck land here
/// (rather than CoreServices) because they're consumed by every agent and have meaningful
/// coupling with the SME/MCP infrastructure.
/// </summary>
public static class RunnerAgentsExtensions
{
    public static IServiceCollection AddRunnerAgents(this IServiceCollection services)
    {
        // SME agent infrastructure: MCP registry, definition service, dynamic-spawn definition generator.
        services.AddSingleton<McpServerRegistry>();
        services.AddSingleton<McpServerAvailabilityChecker>();
        services.AddSingleton<McpServerSecurityPolicy>();
        services.AddSingleton<SMEAgentDefinitionService>();
        services.AddSingleton<AgentTeamComposer>();
        services.AddSingleton<SmeDefinitionGenerator>();
        services.AddSingleton<SmeMetrics>();

        // Copilot CLI's MCP config manager — keeps ~/.config/github-copilot/mcp.json in sync with
        // the appsettings.json McpServers section + per-agent allow-lists. Hosted so it runs the
        // initial sync on startup.
        services.AddSingleton<VirtualDevTeam.Core.AI.CopilotCliMcpConfigManager>();
        services.AddHostedService(sp => sp.GetRequiredService<VirtualDevTeam.Core.AI.CopilotCliMcpConfigManager>());

        // Human-interaction gate-check service + notification channels (Email/Teams/Slack feed the
        // GateNotificationService which polls + broadcasts pending approvals to the operator).
        services.AddSingleton<GateNotificationService>();
        services.AddHostedService(sp => sp.GetRequiredService<GateNotificationService>());
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<INotificationChannel, TeamsNotificationChannel>();
        services.AddSingleton<INotificationChannel, SlackNotificationChannel>();
        services.AddSingleton<IGateCheckService, GateCheckService>();

        return services;
    }
}
