using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// T1.2 rung-3 escalation: the bus nudge (rung 1) and explicit comment (rung 2) didn't
/// move the agent. Time to involve a human. This action does two things:
///
///   1. Applies the <c>agent-stuck</c> label to the target's open PR (preferred) or
///      open issue. The label is the standard "needs human attention" signal that
///      VirtualDevTeam already understands and that the dashboard surfaces in red.
///   2. Emits a <see cref="GateNotificationService"/> notification that is
///      <b>NOT auto-resolved</b> — unlike rung-1/-2 actions which self-resolve once
///      logged. The bell icon stays lit until a human dismisses it on the Approvals
///      page, ensuring the escalation isn't missed.
///
/// Side-effects beyond labeling: none. We do NOT close the PR/issue, reassign anyone,
/// modify code, or restart anything. Operator decides next steps.
///
/// Optional dependencies: any of (<see cref="IPullRequestService"/>,
/// <see cref="IWorkItemService"/>, <see cref="GateNotificationService"/>) being null
/// degrades to <see cref="FlowActionResult.Skipped"/>. The action prefers to do
/// something partial (e.g. label without notify) rather than nothing.
/// </summary>
public sealed class EscalateToHumanAction : IFlowAction
{
    public string ActionType => "escalate-to-human";

    // NoMessyCodePlan Theme 2: reference the canonical Core constants instead of duplicating
    // the literal label strings — was previously redefined in 3 separate files.
    private const string StuckLabel = IssueWorkflow.Labels.AgentStuck;
    // imggen-action-handlers: per-detector label overrides for the image-generation detectors.
    // Other detectors fall back to <see cref="StuckLabel"/>.
    private const string ArtMissingLabel = IssueWorkflow.Labels.ArtMissing;
    private const string ArtRegenNoopLabel = IssueWorkflow.Labels.ArtRegenNoop;
    // flowmonitor-rework-size-anomaly-detector: per-detector label override for the
    // doc-rework-size-anomaly detector (PMSpec.md / Architecture.md size-delta findings).
    private const string DocRegenAnomalyLabel = IssueWorkflow.Labels.DocRegenAnomaly;

    private readonly IPullRequestService? _pullRequestService;
    private readonly IWorkItemService? _workItemService;
    private readonly GateNotificationService? _notifications;
    private readonly ILogger<EscalateToHumanAction> _logger;

    public EscalateToHumanAction(
        ILogger<EscalateToHumanAction> logger,
        IPullRequestService? pullRequestService = null,
        IWorkItemService? workItemService = null,
        GateNotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _notifications = notifications;
    }

    /// <summary>
    /// NoMessyCodePlan post-Tier-2: expanded scope. Same set as the lower rungs — any agent-targeted
    /// detector that has escalated to rung 3 without resolution. The deadlock detector emits its own
    /// human-facing channel; we don't duplicate.
    /// <para>
    /// imggen-action-handlers: <c>image-spec-mismatch</c> and <c>image-regen-anomaly</c> are added
    /// here even though they don't carry a TargetAgentId — see <see cref="CanHandle"/> and
    /// <see cref="TryLabelAsync"/> for the TargetResource (<c>pr#N</c>) fallback. Each maps to a
    /// dedicated label via <see cref="LabelForFinding"/>: <c>art-missing</c> for image-spec-mismatch
    /// and <c>art-regen-noop</c> for image-regen-anomaly. Image-spec-mismatch findings whose
    /// TargetResource is a file path (no PR) skip labeling but still emit the human notification.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> _escalatableDetectorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent-stuck",
        "phase-completion-mismatch",
        "idle-agent-phase-stuck",
        "te-false-completion",
        "handoff-gap",
        "empty-queue",
        "image-spec-mismatch",
        "image-regen-anomaly",
        // flowmonitor-rework-size-anomaly-detector: when rung-2 comment doesn't move the
        // operator, label the doc PR with the per-detector label `doc-regen-anomaly` (see
        // LabelForFinding) so it's filterable in the platform UI.
        "doc-rework-size-anomaly",
        // agent-disappearance: system-level finding (no target agent/PR) — escalate via
        // notification only (no label application). Critical severity.
        "agent-disappearance",
    };

    public bool CanHandle(FlowFinding finding) =>
        _escalatableDetectorIds.Contains(finding.DetectorId)
        && (!string.IsNullOrEmpty(finding.TargetAgentId)
            || TryParsePrNumber(finding.TargetResource, out _)
            || finding.DetectorId == "agent-disappearance");

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrEmpty(finding.TargetAgentId))
        {
            return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "no target agent id" };
        }

        try
        {
            var labelToApply = LabelForFinding(finding);
            var agentName = finding.TargetDisplayName ?? HumanizeAgentId(finding.TargetAgentId);
            var labeledTarget = await TryLabelAsync(agentName, finding, labelToApply, ct).ConfigureAwait(false);
            var notified = TryEmitHumanNotification(finding, agentName ?? "(unassigned)", labeledTarget, labelToApply);

            // Compose result: whichever happened (label, notify, or both) is reported.
            // If neither happened we skip — there's nothing useful to log.
            if (labeledTarget is null && !notified)
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = agentName,
                    Detail = "No open PR/issue to label and no notification service registered",
                };
            }

            var detailParts = new List<string>();
            if (labeledTarget is not null) detailParts.Add($"applied `{labelToApply}` label to {labeledTarget}");
            if (notified) detailParts.Add("emitted non-auto-resolving human notification");

            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Target = labeledTarget ?? agentName,
                Detail = string.Join(" + ", detailParts),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EscalateToHumanAction failed for {Agent}", finding.TargetAgentId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetAgentId,
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Resolve target PR/issue and apply the per-finding escalation label.
    /// Returns the human-readable target identifier (e.g. "pr#42"), or null if no
    /// platform service is available or no open work was found.
    /// <para>
    /// imggen-action-handlers: when <paramref name="agentName"/> is null but the
    /// finding's <c>TargetResource</c> is "pr#N" we label that PR directly. Used by
    /// the image-regen-anomaly detector which carries the PR in TargetResource and
    /// has no agent assignment.
    /// </para>
    /// </summary>
    private async Task<string?> TryLabelAsync(string? agentName, FlowFinding finding, string labelToApply, CancellationToken ct)
    {
        // imggen-action-handlers: direct-PR fallback when no agent name is available.
        if (string.IsNullOrEmpty(agentName) && _pullRequestService is not null
            && TryParsePrNumber(finding.TargetResource, out var directPrNumber))
        {
            try
            {
                var pr = await _pullRequestService.GetAsync(directPrNumber, ct).ConfigureAwait(false);
                if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                var newLabels = MergeLabel(pr.Labels, labelToApply);
                await _pullRequestService.UpdateAsync(directPrNumber, labels: newLabels, ct: ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "FlowMonitor: applied `{Label}` to PR #{Pr} (detector {Detector}, no agent assignment)",
                    labelToApply, directPrNumber, finding.DetectorId);
                return $"pr#{directPrNumber}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed direct-PR label apply ({Label} → pr#{Pr})", labelToApply, directPrNumber);
                return null;
            }
        }

        if (string.IsNullOrEmpty(agentName)) return null;

        // Prefer PR labeling — that's where engineers are usually wedged.
        if (_pullRequestService is not null)
        {
            var prs = await SafeListPrsForAgentAsync(agentName, ct).ConfigureAwait(false);
            var openPr = prs.FirstOrDefault(p => string.Equals(p.State, "open", StringComparison.OrdinalIgnoreCase));
            if (openPr is not null)
            {
                var newLabels = MergeLabel(openPr.Labels, labelToApply);
                try
                {
                    await _pullRequestService.UpdateAsync(openPr.Number, labels: newLabels, ct: ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "FlowMonitor: applied `{Label}` to PR #{Pr} for agent {Agent}",
                        labelToApply, openPr.Number, agentName);
                    return $"pr#{openPr.Number}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply {Label} to PR #{Pr}", labelToApply, openPr.Number);
                }
            }
        }

        if (_workItemService is not null)
        {
            var issues = await SafeListWorkItemsForAgentAsync(agentName, ct).ConfigureAwait(false);
            var openIssue = issues.FirstOrDefault(i =>
                string.Equals(i.State, "open", StringComparison.OrdinalIgnoreCase));
            if (openIssue is not null)
            {
                var newLabels = MergeLabel(openIssue.Labels, labelToApply);
                try
                {
                    await _workItemService.UpdateAsync(openIssue.Number, labels: newLabels, ct: ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "FlowMonitor: applied `{Label}` to issue #{Issue} for agent {Agent}",
                        labelToApply, openIssue.Number, agentName);
                    return $"issue#{openIssue.Number}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply {Label} to issue #{Issue}", labelToApply, openIssue.Number);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Emit a non-auto-resolving human notification. Distinct gateId prefix
    /// (<c>flow-monitor:escalate:</c>) keeps these separate from the rung-1/-2
    /// audit-trail notifications which DO auto-resolve.
    /// </summary>
    private bool TryEmitHumanNotification(FlowFinding finding, string agentName, string? labeledTarget, string labelToApply)
    {
        if (_notifications is null) return false;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"**{agentName}** appears to be stuck and may need help.\n");
            sb.AppendLine($"**What happened:** {finding.Summary}\n");

            // Diagnostic checklist — shows WHY the agent is stuck
            if (finding.Diagnostics.Count > 0)
            {
                sb.AppendLine("**Checks:**");
                foreach (var d in finding.Diagnostics)
                {
                    var icon = d.Passed ? "✅" : "❌";
                    sb.AppendLine($"- {icon} {d.CheckName}: {d.Detail}");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(finding.RecommendedFixDescription))
            {
                sb.AppendLine($"**Suggested fix:** {finding.RecommendedFixDescription}\n");
            }

            if (!string.IsNullOrEmpty(finding.Rationale))
            {
                sb.AppendLine($"**Why:** {finding.Rationale}\n");
            }

            sb.AppendLine(labeledTarget is null
                ? "_No open PR or issue was found for this agent._"
                : $"_Marked {labeledTarget} with `{labelToApply}` label._");

            var context = sb.ToString();

            // Fire-and-forget — never block the action loop on a notification emit.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _notifications.AddNotificationAsync(
                        gateId: $"flow-monitor:escalate:{finding.Id}",
                        context: context,
                        resourceNumber: null).ConfigureAwait(false);
                    // DELIBERATE: do NOT call _notifications.Resolve(...) — this is the
                    // whole point of rung 3. The bell stays lit until a human dismisses.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Escalation notification emit failed (non-fatal)");
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryEmitHumanNotification setup failed (non-fatal)");
            return false;
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

    /// <summary>
    /// post-run-stuck-label-cleanup: remove the escalation label that this action
    /// applied to a PR or issue. Called by FlowMonitor's verification-after-action loop
    /// when the originating finding's condition is confirmed cleared.
    ///
    /// Target lookup uses <c>priorAction.Target</c> (which was set to "pr#42" or "issue#42"
    /// at action time). The label removed is <see cref="LabelForFinding"/> for the
    /// originating finding, so per-detector overrides (e.g. <c>art-missing</c>,
    /// <c>art-regen-noop</c>) are removed correctly. Idempotent — if the label isn't
    /// present, no-op. Failures are swallowed; the caller already considers the finding Resolved.
    /// </summary>
    public async Task UndoAsync(FlowFinding finding, FlowAction priorAction, CancellationToken ct)
    {
        var target = priorAction.Target ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target)) return;
        var labelToRemove = LabelForFinding(finding);

        try
        {
            if (target.StartsWith("pr#", StringComparison.OrdinalIgnoreCase) && _pullRequestService is not null)
            {
                if (!int.TryParse(target.AsSpan(3), out var prNumber)) return;
                var pr = await _pullRequestService.GetAsync(prNumber, ct).ConfigureAwait(false);
                if (pr is null || !pr.Labels.Contains(labelToRemove, StringComparer.OrdinalIgnoreCase)) return;
                var newLabels = pr.Labels.Where(l => !string.Equals(l, labelToRemove, StringComparison.OrdinalIgnoreCase)).ToList();
                await _pullRequestService.UpdateAsync(prNumber, labels: newLabels, ct: ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "FlowMonitor: removed `{Label}` from PR #{Pr} (verification-after-action confirmed condition cleared)",
                    labelToRemove, prNumber);
            }
            else if (target.StartsWith("issue#", StringComparison.OrdinalIgnoreCase) && _workItemService is not null)
            {
                if (!int.TryParse(target.AsSpan(6), out var issueNumber)) return;
                var item = await _workItemService.GetAsync(issueNumber, ct).ConfigureAwait(false);
                if (item is null || !item.Labels.Contains(labelToRemove, StringComparer.OrdinalIgnoreCase)) return;
                var newLabels = item.Labels.Where(l => !string.Equals(l, labelToRemove, StringComparison.OrdinalIgnoreCase)).ToList();
                await _workItemService.UpdateAsync(issueNumber, labels: newLabels, ct: ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "FlowMonitor: removed `{Label}` from issue #{Issue} (verification-after-action confirmed condition cleared)",
                    labelToRemove, issueNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UndoAsync failed for {Target} — non-fatal, finding already marked Resolved", target);
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

    /// <summary>
    /// Merge <paramref name="newLabel"/> into the existing label set without duplicating.
    /// Platform label-update APIs replace the whole label set atomically (see
    /// "Concurrent label writes" in the project conventions), so we have to send the
    /// full desired set on every update.
    /// </summary>
    private static IReadOnlyList<string> MergeLabel(IReadOnlyList<string> existing, string newLabel)
    {
        if (existing.Any(l => string.Equals(l, newLabel, StringComparison.OrdinalIgnoreCase)))
            return existing;
        var merged = new List<string>(existing.Count + 1);
        merged.AddRange(existing);
        merged.Add(newLabel);
        return merged;
    }

    /// <summary>
    /// imggen-action-handlers: resolve the escalation label to apply for a given finding.
    /// Image-detector findings get dedicated labels so operators can filter for the specific
    /// failure mode; everything else falls back to the canonical <see cref="StuckLabel"/>.
    /// </summary>
    internal static string LabelForFinding(FlowFinding finding) => finding.DetectorId switch
    {
        "image-spec-mismatch" => ArtMissingLabel,
        "image-regen-anomaly" => ArtRegenNoopLabel,
        "doc-rework-size-anomaly" => DocRegenAnomalyLabel,
        _ => StuckLabel,
    };

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

    /// <summary>
    /// Converts a raw agent ID (e.g. "softwareengineer-74bf6b42c9a84f8b87d17aca6adb358d")
    /// into a human-readable display name (e.g. "Software Engineer") by stripping
    /// the 32-char hex GUID suffix and mapping known role prefixes.
    /// </summary>
    internal static string HumanizeAgentId(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return "Unknown Agent";

        // Strip 32-char hex GUID suffix: "softwareengineer-74bf6b42..." → "softwareengineer"
        var stripped = agentId;
        var lastDash = agentId.LastIndexOf('-');
        if (lastDash > 0 && agentId.Length - lastDash - 1 == 32)
        {
            var suffix = agentId[(lastDash + 1)..];
            if (suffix.All(c => char.IsAsciiHexDigit(c)))
                stripped = agentId[..lastDash];
        }

        return stripped.ToLowerInvariant() switch
        {
            "softwareengineer" => "Software Engineer",
            "architect" => "Architect",
            "programmanager" => "Program Manager",
            "testengineer" => "Test Engineer",
            "securityauditor" => "Security Auditor",
            "researcher" => "Researcher",
            _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                stripped.Replace("-", " ")),
        };
    }
}
