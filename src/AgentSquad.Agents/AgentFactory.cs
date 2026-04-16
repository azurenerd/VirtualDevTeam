using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
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
            AgentRole.SoftwareEngineer => CreateWithDI<SoftwareEngineerAgent>(identity),
            AgentRole.TestEngineer => CreateWithDI<TestEngineerAgent>(identity),
            AgentRole.Custom => CreateWithDI<CustomAgent>(identity),
            _ => throw new ArgumentException($"Unknown agent role: {role}", nameof(role))
        };
    }

    /// <summary>
    /// Creates an SME agent from a definition. The definition is passed alongside the identity
    /// to the DI container for constructor injection.
    /// </summary>
    public IAgent CreateSme(AgentIdentity identity, SMEAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(definition);

        return ActivatorUtilities.CreateInstance<SmeAgent>(_serviceProvider, identity, definition);
    }

    private T CreateWithDI<T>(AgentIdentity identity) where T : AgentBase
    {
        return ActivatorUtilities.CreateInstance<T>(_serviceProvider, identity);
    }
}
