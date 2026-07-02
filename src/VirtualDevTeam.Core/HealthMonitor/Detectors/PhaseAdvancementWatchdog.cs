using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.8 PhaseAdvancementWatchdog — the forward-stall counterpart to
/// <see cref="PhaseCompletionMismatchDetector"/>. Where that one catches phase
/// advancing without work being complete, this catches work being complete
/// without phase advancing.
///
/// <para>
/// Specifically: all <c>engineering-task</c> work items are closed AND there are
/// no open Software-Engineer PRs, but the workflow phase is still
/// <c>ParallelDevelopment</c>. Either the <c>engineering.all.complete</c> signal
/// never fired, or the WorkflowStateMachine refused to advance on a guard. Either
/// way the operator should investigate.
/// </para>
///
/// <para>
/// Severity: Critical — a phase-stall blocks the whole run, not just one task.
/// Triggers the T1.5 FixRecommendation flow automatically when no action handles
/// the finding (this detector intentionally has no paired action — the right fix
/// requires reading the workflow signals + state machine, which is operator work).
/// </para>
/// </summary>
public sealed class PhaseAdvancementWatchdog : IFlowDetector
{
    public string DetectorId => "phase-advancement-watchdog";

    private readonly ILogger<PhaseAdvancementWatchdog> _logger;
    private readonly TimeSpan _stallThreshold;

    public PhaseAdvancementWatchdog(
        ILogger<PhaseAdvancementWatchdog> logger,
        TimeSpan? stallThreshold = null)
    {
        _logger = logger;
        _stallThreshold = stallThreshold ?? TimeSpan.FromMinutes(5);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            // Only meaningful in the ParallelDevelopment phase.
            if (!string.Equals(ctx.CurrentPhase, "ParallelDevelopment", StringComparison.OrdinalIgnoreCase))
                return findings;

            var workItems = await ctx.Platform.ListOpenWorkItemsAsync(ct).ConfigureAwait(false);
            var openEngTasks = workItems.Count(w =>
                w.Labels.Any(l => string.Equals(l, "engineering-task", StringComparison.OrdinalIgnoreCase)));
            if (openEngTasks > 0) return findings;

            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            var openEngPrs = prs.Count(p =>
                !string.IsNullOrEmpty(p.HeadBranch) &&
                p.HeadBranch.Contains("/softwareengineer", StringComparison.OrdinalIgnoreCase));
            if (openEngPrs > 0) return findings;

            // Conditions met: no work but phase hasn't advanced. Use the signals as
            // additional context.
            var hasCompleteSignal = ctx.WorkflowSignals.Any(s =>
                string.Equals(s, "engineering.all.complete", StringComparison.OrdinalIgnoreCase));

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = ctx.Now,
                DetectorId = DetectorId,
                Severity = FlowFindingSeverity.Critical,
                TargetResource = "workflow-phase",
                Summary = $"Phase still {ctx.CurrentPhase} but 0 open engineering tasks + 0 open SE PRs. " +
                          (hasCompleteSignal ? "engineering.all.complete signal IS set." : "engineering.all.complete signal NOT set."),
                Rationale = "Workflow is stuck — all engineering work is closed on the platform but the phase " +
                            "has not advanced. " +
                            (hasCompleteSignal
                                ? "The completion signal fired but the WorkflowStateMachine guards refused to advance "
                                  + "— inspect the state machine's GateCondition predicates for ParallelDevelopment→Testing."
                                : "The completion signal never fired — inspect HealthMonitor.AutoDetectSignals "
                                  + "or the agent that should emit it."),
                DedupKey = "phase-advancement-watchdog:ParallelDevelopment",
            });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PhaseAdvancementWatchdog tick failed (non-fatal)");
        }
        return findings;
    }
}
