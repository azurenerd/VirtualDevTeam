using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentSquad.Orchestrator;

public static class OrchestratorServiceExtensions
{
    public static IServiceCollection AddOrchestrator(this IServiceCollection services)
    {
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<HealthMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
        services.AddSingleton<DeadlockDetector>();
        services.AddSingleton<AgentSpawnManager>();
        services.AddSingleton<WorkflowStateMachine>();
        return services;
    }
}
