using AgentSquad.Core.Agents;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSquad.Agents;

/// <summary>
/// Factory that creates the correct agent type based on role using DI.
/// </summary>
public class AgentFactory : IAgentFactory
{
    private readonly IServiceProvider _serviceProvider;

    public AgentFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IAgent Create(AgentRole role, AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return role switch
        {
            AgentRole.ProgramManager => CreateWithDI<ProgramManagerAgent>(identity),
            AgentRole.Researcher => CreateWithDI<ResearcherAgent>(identity),
            AgentRole.Architect => CreateWithDI<ArchitectAgent>(identity),
            AgentRole.PrincipalEngineer => CreateWithDI<PrincipalEngineerAgent>(identity),
            AgentRole.SeniorEngineer => CreateWithDI<SeniorEngineerAgent>(identity),
            AgentRole.JuniorEngineer => CreateWithDI<JuniorEngineerAgent>(identity),
            AgentRole.TestEngineer => CreateWithDI<TestEngineerAgent>(identity),
            _ => throw new ArgumentException($"Unknown agent role: {role}", nameof(role))
        };
    }

    private T CreateWithDI<T>(AgentIdentity identity) where T : AgentBase
    {
        return ActivatorUtilities.CreateInstance<T>(_serviceProvider, identity);
    }
}
