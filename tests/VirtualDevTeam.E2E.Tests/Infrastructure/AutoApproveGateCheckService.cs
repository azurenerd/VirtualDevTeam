using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// Gate check service that auto-approves all gates instantly.
/// Used in E2E tests to bypass human approval requirements.
/// </summary>
public class AutoApproveGateCheckService : IGateCheckService
{
    private readonly HashSet<string> _checkedGates = new();
    private readonly object _lock = new();

    public bool IsEnabled => false; // All gates auto-proceed

    public bool RequiresHuman(string gateId) => false;

    public Task<GateResult> CheckGateAsync(string gateId, string context, int? resourceNumber = null, CancellationToken ct = default)
    {
        lock (_lock) { _checkedGates.Add(gateId); }
        return Task.FromResult(GateResult.Proceed);
    }

    public Task<GateCommentAssessment> AssessGateApprovalAsync(string gateId, int resourceNumber, CancellationToken ct = default)
    {
        return Task.FromResult(new GateCommentAssessment(GateDecision.Approved));
    }

    public Task<bool> IsGateApprovedAsync(string gateId, int resourceNumber, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public void ApproveGate(string gateId, int? resourceNumber = null) { }

    public void RejectGate(string gateId, string? feedback = null, int? resourceNumber = null) { }

    public void UpdateGates(HumanInteractionConfig updatedGates) { }

    public GateRejection? GetLocalRejection(string gateId, int? resourceNumber = null) => null;

    public bool IsGateApprovedLocally(string gateId, int? resourceNumber = null) => true;

    public Task<GateStatus> GetGateStatusAsync(string gateId, int resourceNumber, CancellationToken ct = default)
    {
        // Return NotActivated so agents proceed to do their LLM work.
        // WaitForGateAsync will auto-approve instantly when they reach the gate.
        return Task.FromResult(GateStatus.NotActivated);
    }

    public Task WaitForSignalAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        return Task.CompletedTask; // Auto-approve tests never wait
    }

    public ReworkInFlightState? GetReworkInFlight(string gateId, int? resourceNumber = null) => null;

    public IReadOnlyList<ReworkInFlightState> GetAllReworkInFlight() => Array.Empty<ReworkInFlightState>();

    public bool AreAllGatesDisabled() => true;

    /// <summary>All gate IDs that were checked during the test, for assertions.</summary>
    public IReadOnlySet<string> CheckedGates
    {
        get { lock (_lock) { return _checkedGates.ToHashSet(); } }
    }
}
