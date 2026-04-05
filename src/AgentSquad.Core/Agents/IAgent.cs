namespace AgentSquad.Core.Agents;

public interface IAgent
{
    AgentIdentity Identity { get; }
    AgentStatus Status { get; }

    Task InitializeAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task HandleMessageAsync(AgentMessage message, CancellationToken ct = default);

    event EventHandler<AgentStatusChangedEventArgs>? StatusChanged;
}

public class AgentStatusChangedEventArgs : EventArgs
{
    public required AgentIdentity Agent { get; init; }
    public required AgentStatus OldStatus { get; init; }
    public required AgentStatus NewStatus { get; init; }
    public string? Reason { get; init; }
}
