namespace AgentSquad.Orchestrator;

using AgentSquad.Core.Agents;

/// <summary>
/// Factory for creating agent instances by role.
/// Consumers must register an implementation in DI.
/// </summary>
public interface IAgentFactory
{
    IAgent Create(AgentRole role, AgentIdentity identity);
}
