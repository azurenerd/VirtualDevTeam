namespace AgentSquad.Core.Messaging;

/// <summary>
/// In-process publish/subscribe message bus for agent-to-agent communication.
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Publish a message. If the message's ToAgentId is "*" or null, broadcast to all subscribers.
    /// Otherwise deliver only to the targeted agent.
    /// </summary>
    Task PublishAsync<TMessage>(TMessage message, CancellationToken ct = default) where TMessage : class;

    /// <summary>
    /// Subscribe an agent to receive messages of a specific type.
    /// </summary>
    IDisposable Subscribe<TMessage>(string agentId, Func<TMessage, CancellationToken, Task> handler) where TMessage : class;

    /// <summary>
    /// Subscribe to ALL message types for an agent.
    /// </summary>
    IDisposable SubscribeAll(string agentId, Func<object, CancellationToken, Task> handler);

    /// <summary>
    /// Get count of pending messages for an agent.
    /// </summary>
    int GetPendingCount(string agentId);
}
