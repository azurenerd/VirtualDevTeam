using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.7 HandoffGapDetector — a PR has unresolved review threads (an implicit
/// CHANGES_REQUESTED handoff to the engineer), but the engineer that owns it is
/// Idle and hasn't transitioned to Working within the handoff window. Common
/// cause: the bus message that should have woken the engineer was lost, or the
/// engineer's poll loop hasn't run yet.
///
/// <para>
/// **Why this is distinct from <see cref="IdleAgentPhaseStuckDetector"/>:**
/// that one fires when a *reviewer* is idle while a PR awaits *their* review.
/// This one fires when the *engineer* is idle while a PR has *open feedback*
/// addressed to them. Both can be true at once; their dedup keys differ so the
/// audit log captures both perspectives.
/// </para>
///
/// <para>
/// **Implementation note:** since <see cref="IPlatformView"/> doesn't surface
/// per-message dispatch state, we use unresolved review threads as the
/// stand-in for "outstanding feedback addressed to the engineer." A single
/// thread is enough to count as a handoff (one inline comment = one
/// rework expectation).
/// </para>
/// </summary>
public sealed class HandoffGapDetector : IFlowDetector
{
    public string DetectorId => "handoff-gap";

    private readonly ILogger<HandoffGapDetector> _logger;
    private readonly TimeSpan _handoffWindow;

    public HandoffGapDetector(
        ILogger<HandoffGapDetector> logger,
        TimeSpan? handoffWindow = null)
    {
        _logger = logger;
        _handoffWindow = handoffWindow ?? TimeSpan.FromMinutes(3);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            if (prs.Count == 0) return findings;

            foreach (var pr in prs)
            {
                if (ct.IsCancellationRequested) break;
                // Only care about PRs that have been past initial review (i.e. reviewer
                // engagement has happened). PRs in agent-stuck / human-review states
                // already escalated.
                if (!pr.Labels.Any(l =>
                    string.Equals(l, "ready-for-review", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "architect-approved", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "pm-approved", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (pr.Labels.Any(l =>
                    string.Equals(l, "agent-stuck", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "human-review-required", StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Find engineer ownership.
                if (string.IsNullOrWhiteSpace(pr.AssignedAgent)) continue;

                // Find the live agent matching this assignment.
                var owner = ctx.Agents.FirstOrDefault(a =>
                    string.Equals(a.DisplayName, pr.AssignedAgent, StringComparison.OrdinalIgnoreCase));
                if (owner is null) continue;
                if (!string.Equals(owner.Status, "Idle", StringComparison.OrdinalIgnoreCase)) continue;
                if (owner.StatusChangedAt is null) continue;
                if (ctx.Now - owner.StatusChangedAt.Value < _handoffWindow) continue;

                // Has there been any open feedback (unresolved review thread)?
                var threads = await ctx.Platform.ListUnresolvedThreadsAsync(pr.Number, ct).ConfigureAwait(false);
                if (threads.Count == 0) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetAgentId = owner.Id,
                    TargetDisplayName = owner.DisplayName,
                    TargetResource = $"pr#{pr.Number}",
                    Summary = $"{owner.DisplayName} idle for {FormatDuration(ctx.Now - owner.StatusChangedAt.Value)} " +
                              $"while PR #{pr.Number} has {threads.Count} unresolved review thread(s) addressed to them.",
                    Rationale = "Reviewer feedback exists on the engineer's PR but the engineer's status remained " +
                                "Idle past the handoff window. The bus message that should have woken them may " +
                                "have been dropped (in-process channel pressure, agent re-spawn during emit), " +
                                "or the engineer's poll-loop predicate doesn't match the current PR state. The " +
                                "escalation ladder will nudge → comment → escalate.",
                    DedupKey = $"handoff-gap:{owner.Id}:{pr.Number}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HandoffGapDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{ts.TotalHours:0.0}h";
    }
}
