namespace VirtualDevTeam.Orchestrator;

using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;

/// <summary>
/// Factory for creating agent instances by role.
/// Consumers must register an implementation in DI.
/// </summary>
public interface IAgentFactory
{
    IAgent Create(AgentRole role, AgentIdentity identity);

    /// <summary>
    /// Creates an SME agent from a definition. Used for dynamically-created specialist agents.
    /// </summary>
    IAgent CreateSme(AgentIdentity identity, SMEAgentDefinition definition);
}
