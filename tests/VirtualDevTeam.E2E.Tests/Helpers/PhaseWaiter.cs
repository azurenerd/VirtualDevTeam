using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.E2E.Tests.Helpers;

/// <summary>
/// Helper to wait for specific workflow phases with timeout and diagnostics.
/// </summary>
public static class PhaseWaiter
{
    /// <summary>
    /// Wait until the workflow reaches a specific phase, with per-phase timeout diagnostics.
    /// </summary>
    public static async Task<bool> WaitForPhaseAsync(
        WorkflowStateMachine workflow,
        ProjectPhase targetPhase,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        var lastReportedPhase = workflow.CurrentPhase;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var currentPhase = workflow.CurrentPhase;
            if (currentPhase != lastReportedPhase)
            {
                lastReportedPhase = currentPhase;
            }

            if (currentPhase >= targetPhase)
                return true;

            // Try to advance
            workflow.TryAdvancePhase(out _);
            await Task.Delay(100, ct);
        }

        return workflow.CurrentPhase >= targetPhase;
    }

    /// <summary>
    /// Wait until a predicate is true with polling.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null,
        int pollIntervalMs = 100,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (predicate())
                return true;
            await Task.Delay(pollIntervalMs, ct);
        }

        return predicate();
    }

    /// <summary>
    /// Wait until an async predicate is true with polling.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan? timeout = null,
        int pollIntervalMs = 200,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await predicate())
                return true;
            await Task.Delay(pollIntervalMs, ct);
        }

        return await predicate();
    }
}
