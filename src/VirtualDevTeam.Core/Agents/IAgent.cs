using VirtualDevTeam.Core.Diagnostics;

namespace VirtualDevTeam.Core.Agents;

public interface IAgent
{
    AgentIdentity Identity { get; }
    AgentStatus Status { get; }
    string? StatusReason { get; }
    AgentDiagnostic? CurrentDiagnostic { get; }

    /// <summary>
    /// The PR number the agent is currently working on, if any. Null when idle or pre-PR.
    /// Engineers set this to their implementation PR; Researcher/Architect/PM set it to
    /// their document PR (Research.md / Architecture.md / PMSpec.md). Surfaced on the
    /// dashboard so operators can jump straight to the agent's active PR.
    /// </summary>
    int? CurrentPrNumber { get; }

    /// <summary>
    /// Structured details about why the agent is Blocked, populated when waiting on a
    /// human gate. Null when not blocked. See <see cref="BlockedReason"/>.
    /// </summary>
    BlockedReason? CurrentBlockedReason { get; }

    Task InitializeAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task HandleMessageAsync(AgentMessage message, CancellationToken ct = default);

    event EventHandler<AgentStatusChangedEventArgs>? StatusChanged;
    event EventHandler? ErrorsChanged;
    event EventHandler<AgentActivityEventArgs>? ActivityLogged;
    event EventHandler<DiagnosticChangedEventArgs>? DiagnosticChanged;

    IReadOnlyList<AgentLogEntry> RecentErrors { get; }
    void ClearErrors();
}

public class AgentStatusChangedEventArgs : EventArgs
{
    public required AgentIdentity Agent { get; init; }
    public required AgentStatus OldStatus { get; init; }
    public required AgentStatus NewStatus { get; init; }
    public string? Reason { get; init; }
}

public class AgentActivityEventArgs : EventArgs
{
    public required string AgentId { get; init; }
    public required string EventType { get; init; }
    public required string Details { get; init; }
}
