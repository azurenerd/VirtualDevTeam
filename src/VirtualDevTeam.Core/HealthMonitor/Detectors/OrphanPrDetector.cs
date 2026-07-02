using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.10 OrphanPRDetector — an open PR labeled <c>in-progress</c> has no live agent
/// claiming it. Common after a runner restart per LessonsLearned #7: the SE worker
/// that owned the PR died, no other agent picked it up, and the PR sits in limbo.
/// The action ladder will downgrade it to human-attention via the existing
/// EscalateToHumanAction (which removes <c>in-progress</c> and applies
/// <c>agent-stuck</c>).
///
/// <para>
/// Identification: we match the PR's <c>AssignedAgent</c> display name (or, when
/// absent, parse the head branch <c>agent/&lt;name&gt;/...</c>) against the live
/// agent registry surfaced via <see cref="DetectorContext.Agents"/>. No live match
/// → orphan.
/// </para>
///
/// <para>
/// Carve-outs: PRs younger than 2 minutes are skipped (the engineer may be in the
/// middle of clone+commit and not yet registered). PRs already labeled
/// <c>agent-stuck</c>, <c>human-review-required</c>, or <c>awaiting-human-review</c>
/// are skipped — they've already been escalated.
/// </para>
/// </summary>
public sealed class OrphanPrDetector : IFlowDetector
{
    public string DetectorId => "orphan-pr";

    // NoMessyCodePlan Theme 2: reference the canonical Core constants.
    private const string InProgressLabel = "in-progress";
    private const string AgentStuckLabel = IssueWorkflow.Labels.AgentStuck;
    private const string HumanReviewLabel = "human-review-required";
    private const string AwaitingHumanLabel = "awaiting-human-review";

    private readonly ILogger<OrphanPrDetector> _logger;
    private readonly TimeSpan _gracePeriod;

    public OrphanPrDetector(
        ILogger<OrphanPrDetector> logger,
        TimeSpan? gracePeriod = null)
    {
        _logger = logger;
        _gracePeriod = gracePeriod ?? TimeSpan.FromMinutes(2);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            if (prs.Count == 0) return findings;

            var liveDisplayNames = new HashSet<string>(
                ctx.Agents.Select(a => a.DisplayName ?? a.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var pr in prs)
            {
                if (ct.IsCancellationRequested) break;
                if (!pr.Labels.Contains(InProgressLabel, StringComparer.OrdinalIgnoreCase)) continue;
                if (pr.Labels.Any(l =>
                        string.Equals(l, AgentStuckLabel, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l, HumanReviewLabel, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(l, AwaitingHumanLabel, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Grace period: don't flag freshly-created PRs while the engineer
                // may still be in the clone/push window.
                var age = ctx.Now - pr.CreatedAt;
                if (age < _gracePeriod) continue;

                var ownerName = pr.AssignedAgent ?? ExtractAgentNameFromBranch(pr.HeadBranch);
                if (string.IsNullOrEmpty(ownerName)) continue;

                if (liveDisplayNames.Contains(ownerName)) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetDisplayName = ownerName,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 70)}' is in-progress but its owner " +
                              $"'{ownerName}' is not in the live agent registry.",
                    Rationale = "The PR's assigned owner is no longer running. This commonly happens after a " +
                                "runner restart when the agent that owned the PR was an SME that wasn't re-spawned, " +
                                "or when an SE worker crashed mid-task. The existing EscalateToHumanAction will " +
                                "strip the in-progress label and apply agent-stuck so the PR becomes eligible for " +
                                "reassignment.",
                    DedupKey = $"orphan-pr:{pr.Number}",
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
            _logger.LogWarning(ex, "OrphanPrDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static string? ExtractAgentNameFromBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        // Branch convention: agent/{name-slug}/{task-slug}
        var parts = branch.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase)) return null;
        // We can't perfectly reverse the slug to "Display Name" — return the slug
        // for diagnostic context; live registry lookup uses display names so a
        // slug-only return correctly falls through to "orphan".
        return parts[1];
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
