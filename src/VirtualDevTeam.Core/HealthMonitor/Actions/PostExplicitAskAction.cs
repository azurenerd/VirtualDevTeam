using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// T1.2 rung-2 escalation: post a structured comment on the target agent's open PR (preferred)
/// or open issue, naming the agent and asking for explicit confirmation of progress or human
/// escalation. The bus nudge from rung 1 didn't work — this is a more visible, more durable
/// nag that surfaces in the platform UI and is searchable in audit logs.
///
/// Comment goes through <see cref="IReviewService.AddCommentAsync"/> for PRs (since the
/// review service is the canonical comment surface for PR conversations) and through
/// <see cref="IWorkItemService.AddCommentAsync"/> for issues.
///
/// This action does NOT modify code, NOT close the PR/issue, NOT reassign — it's a
/// public, persistent prompt that adds zero side-effects beyond a single comment.
///
/// Optional dependencies: if any of (<see cref="IPullRequestService"/>,
/// <see cref="IWorkItemService"/>, <see cref="IReviewService"/>) is missing the action
/// returns <see cref="FlowActionResult.Skipped"/> rather than throwing — same semantics
/// as the platform-view fallback in DetectorContext.
/// </summary>
public sealed class PostExplicitAskAction : IFlowAction
{
    public string ActionType => "post-explicit-ask";

    private readonly IPullRequestService? _pullRequestService;
    private readonly IWorkItemService? _workItemService;
    private readonly IReviewService? _reviewService;
    private readonly ILogger<PostExplicitAskAction> _logger;

    public PostExplicitAskAction(
        ILogger<PostExplicitAskAction> logger,
        IPullRequestService? pullRequestService = null,
        IWorkItemService? workItemService = null,
        IReviewService? reviewService = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _reviewService = reviewService;
    }

    /// <summary>
    /// NoMessyCodePlan post-Tier-2: expanded scope. Same set as <see cref="KickAgentPollAction"/> —
    /// any agent-targeted detector whose target agent owns a PR/issue we can post a comment on.
    /// The deadlock detector is intentionally excluded: posting a comment on a PR doesn't break
    /// a wait-for cycle. status-reason-stagnant + idle-idle-cycle + ai-anomaly are skipped for
    /// the same reasons as the rung-1 handler.
    /// <para>
    /// imggen-action-handlers: <c>image-spec-mismatch</c> and <c>image-regen-anomaly</c> are
    /// added here even though they don't carry a TargetAgentId — see <see cref="CanHandle"/>
    /// and <see cref="ExecuteAsync"/> for the TargetResource (<c>pr#N</c>) fallback used to
    /// locate a PR to comment on directly. Image-spec-mismatch findings whose TargetResource
    /// is a file path (no PR) NoOp gracefully — the action still records an attempt so the
    /// rung ladder can advance to rung 3 (escalate-to-human + label) on the next tick.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> _commentableDetectorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent-stuck",
        "phase-completion-mismatch",
        "idle-agent-phase-stuck",
        "te-false-completion",
        "handoff-gap",
        "empty-queue",
        "image-spec-mismatch",
        "image-regen-anomaly",
        // flowmonitor-rework-size-anomaly-detector: doc PRs (PMSpec.md / Architecture.md) whose
        // rework cycle produced a suspiciously large size delta. Operator-facing alert; the agent
        // can't self-correct since the offending rework already shipped to the PR.
        "doc-rework-size-anomaly",
    };

    public bool CanHandle(FlowFinding finding) =>
        _commentableDetectorIds.Contains(finding.DetectorId)
        && (!string.IsNullOrEmpty(finding.TargetAgentId)
            || TryParsePrNumber(finding.TargetResource, out _));

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);

        // imggen-action-handlers: image-detector findings carry "pr#N" in TargetResource
        // instead of a TargetAgentId. Take the direct-PR comment path before the agent
        // lookup so we don't try to ListForAgentAsync(null).
        if (string.IsNullOrEmpty(finding.TargetAgentId)
            && TryParsePrNumber(finding.TargetResource, out var directPrNumber))
        {
            return await PostDirectToPrAsync(finding, directPrNumber, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(finding.TargetAgentId))
        {
            return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "no target agent id" };
        }

        // Need at least one platform service to do anything useful.
        if (_pullRequestService is null && _workItemService is null)
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Detail = "no platform services available (running in API-less mode?)",
            };
        }

        try
        {
            var agentName = finding.TargetDisplayName ?? finding.TargetAgentId;
            var comment = BuildComment(agentName, finding);

            // Prefer PR comment (the agent is most likely working on a PR right now).
            // Fall through to issue comment if no PR exists.
            //
            // 2026-05-15: PR comments DISABLED (Lesson #28). No agent parses FlowMonitor
            // comments, and they spam the PR with 6+ identical escalations per task.
            // The action still succeeds (logged internally) but no platform comment is posted.
            // Rung-3 (human label + notification) remains the effective escalation path.
            if (_pullRequestService is not null && _reviewService is not null)
            {
                var prs = await SafeListPrsForAgentAsync(agentName, ct).ConfigureAwait(false);
                var openPr = prs.FirstOrDefault(p => string.Equals(p.State, "open", StringComparison.OrdinalIgnoreCase));
                if (openPr is not null)
                {
                    // Log the comment we WOULD have posted, but don't spam the PR.
                    _logger.LogInformation(
                        "FlowMonitor: suppressed explicit-ask PR comment on PR #{Pr} for agent {Agent} (finding {Finding}). Comment: {Comment}",
                        openPr.Number, agentName, finding.Id, comment.Length > 200 ? comment[..200] + "…" : comment);
                    return new FlowActionOutcome
                    {
                        Result = FlowActionResult.Success,
                        Target = $"pr#{openPr.Number}",
                        Detail = $"Logged explicit-ask for PR #{openPr.Number} (PR comment suppressed — Lesson #28)",
                    };
                }
            }

            if (_workItemService is not null)
            {
                var issues = await SafeListWorkItemsForAgentAsync(agentName, ct).ConfigureAwait(false);
                var openIssue = issues.FirstOrDefault(i =>
                    string.Equals(i.State, "open", StringComparison.OrdinalIgnoreCase));
                if (openIssue is not null)
                {
                    // 2026-05-15: Issue comments also DISABLED (same rationale as PR comments —
                    // Lesson #28). No agent parses FlowMonitor comments on issues either.
                    _logger.LogInformation(
                        "FlowMonitor: suppressed explicit-ask comment on issue #{Issue} for agent {Agent} (finding {Finding})",
                        openIssue.Number, agentName, finding.Id);
                    return new FlowActionOutcome
                    {
                        Result = FlowActionResult.Success,
                        Target = $"issue#{openIssue.Number}",
                        Detail = $"Logged explicit-ask for issue #{openIssue.Number} (comment suppressed — Lesson #28)",
                    };
                }
            }

            return new FlowActionOutcome
            {
                Result = FlowActionResult.NoOp,
                Target = agentName,
                Detail = "No open PR or issue found for target agent — nothing to comment on",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostExplicitAskAction failed for {Agent}", finding.TargetAgentId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetAgentId,
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    private async Task<IReadOnlyList<PlatformPullRequest>> SafeListPrsForAgentAsync(
        string agentName, CancellationToken ct)
    {
        try { return await _pullRequestService!.ListForAgentAsync(agentName, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListForAgentAsync (PR) failed for {Agent} — treating as empty", agentName);
            return Array.Empty<PlatformPullRequest>();
        }
    }

    private async Task<IReadOnlyList<PlatformWorkItem>> SafeListWorkItemsForAgentAsync(
        string agentName, CancellationToken ct)
    {
        try { return await _workItemService!.ListForAgentAsync(agentName, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListForAgentAsync (work item) failed for {Agent} — treating as empty", agentName);
            return Array.Empty<PlatformWorkItem>();
        }
    }

    private static string BuildComment(string agentName, FlowFinding finding)
    {
        // Compute the duration we observed to make the ask concrete. Falls back to
        // "for a while" when the rationale doesn't carry an explicit timing string.
        var elapsed = TryExtractElapsed(finding);
        var elapsedClause = elapsed is null ? "for a while" : $"for {elapsed}";

        return
            $"⚠️ **FlowMonitor escalation (rung 2)**\n\n" +
            $"@{agentName} — FlowMonitor noticed you've been on this task {elapsedClause} " +
            $"without a status update. Could you please:\n\n" +
            $"1. Reply with a brief progress note (what's done, what's blocking), or\n" +
            $"2. Apply the `agent-stuck` label to escalate this to a human reviewer.\n\n" +
            $"_Detector: `{finding.DetectorId}` · Severity: `{finding.Severity}` · " +
            $"Finding id: `{finding.Id}`._\n\n" +
            $"<details><summary>Why this comment?</summary>\n\n" +
            $"{finding.Summary}\n\n" +
            $"{finding.Rationale}\n\n" +
            $"</details>";
    }

    private static string? TryExtractElapsed(FlowFinding finding)
    {
        // The AgentStuckDetector summary is "Agent X has been Working for Ym/Zh". Extract
        // the trailing duration token without parsing the whole sentence.
        var summary = finding.Summary ?? string.Empty;
        var idx = summary.LastIndexOf("for ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var tail = summary[(idx + 4)..].Trim().TrimEnd('.');
        return string.IsNullOrEmpty(tail) ? null : tail;
    }

    /// <summary>
    /// imggen-action-handlers: post a comment directly to the PR named in
    /// <c>finding.TargetResource</c> ("pr#N") for image-detector findings that
    /// don't have an agent assignment. Mirrors the agent-name path's logging,
    /// outcome shape, and exception handling.
    /// </summary>
    private async Task<FlowActionOutcome> PostDirectToPrAsync(
        FlowFinding finding, int prNumber, CancellationToken ct)
    {
        // 2026-05-15: PR comments DISABLED (Lesson #28). Log only, no platform comment.
        var subject = finding.TargetDisplayName ?? "(unassigned)";
        var comment = BuildComment(subject, finding);
        _logger.LogInformation(
            "FlowMonitor: suppressed direct explicit-ask PR comment on PR #{Pr} (detector {Detector}, finding {Finding}). Length: {Len}",
            prNumber, finding.DetectorId, finding.Id, comment.Length);
        return new FlowActionOutcome
        {
            Result = FlowActionResult.Success,
            Target = $"pr#{prNumber}",
            Detail = $"Logged explicit-ask for PR #{prNumber} (PR comment suppressed — Lesson #28)",
        };
    }

    /// <summary>
    /// Parses "pr#1234" → 1234 (mirrors <c>MergeApprovedPrAction.TryParsePrNumber</c>).
    /// Returns false for any other shape so the action ignores findings whose
    /// TargetResource isn't a PR reference.
    /// </summary>
    private static bool TryParsePrNumber(string? targetResource, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(targetResource)) return false;
        const string prefix = "pr#";
        if (!targetResource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(targetResource.AsSpan(prefix.Length), out number) && number > 0;
    }
}
