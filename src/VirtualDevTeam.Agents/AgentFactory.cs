using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualDevTeam.Agents;

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
            AgentRole.SecurityAuditor => CreateWithDI<SecurityAuditorAgent>(identity),
            AgentRole.Custom => CreateWithDI<CustomAgent>(identity),
            _ => throw new ArgumentException($"Unknown agent role: {role}", nameof(role))
        };
    }

    /// <summary>
    /// Creates an SME agent from a definition. Routes to SpecialistEngineerAgent for
    /// engineer-based templates (full rework/build/test) or SmeAgent for custom templates.
    /// </summary>
    public IAgent CreateSme(AgentIdentity identity, SMEAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(definition);

        return definition.BaseTemplate?.Equals("engineer", StringComparison.OrdinalIgnoreCase) == true
            ? ActivatorUtilities.CreateInstance<SpecialistEngineerAgent>(_serviceProvider, identity, definition)
            : ActivatorUtilities.CreateInstance<SmeAgent>(_serviceProvider, identity, definition);
    }

    private T CreateWithDI<T>(AgentIdentity identity) where T : AgentBase
    {
        return ActivatorUtilities.CreateInstance<T>(_serviceProvider, identity);
    }
}
