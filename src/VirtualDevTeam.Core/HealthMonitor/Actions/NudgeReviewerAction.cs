using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Messaging;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// FlowMonitor action that publishes a <see cref="ReviewNudgeMessage"/> directly to a
/// reviewer agent's bus subscription when a PR has been waiting for their approval label
/// beyond the <c>missing-reviewer</c> detector's threshold.
///
/// <para>
/// <b>Idempotency:</b> a static <c>_lastNudge</c> table keyed on (PR, role) prevents
/// re-nudging the same pair within a 5-minute cooldown window, regardless of the
/// escalation ladder's rate cap, so a flapping detector cannot spam the reviewer.
/// </para>
///
/// <para>
/// <b>Resilience:</b> the action completes successfully even when no agent has subscribed
/// to <see cref="ReviewNudgeMessage"/> — the in-process bus silently drops messages with
/// no active subscribers.
/// </para>
/// </summary>
public sealed class NudgeReviewerAction : IFlowAction
{
    public string ActionType => "nudge-reviewer";

    // Keyed by "{prNumber}:{role}" so different reviewer roles for the same PR each
    // have their own cooldown window.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastNudge = new();
    private static readonly TimeSpan _nudgeCooldown = TimeSpan.FromMinutes(5);

    private readonly IMessageBus _messageBus;
    private readonly ILogger<NudgeReviewerAction> _logger;

    public NudgeReviewerAction(IMessageBus messageBus, ILogger<NudgeReviewerAction> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    /// <summary>
    /// Handles findings from <c>MissingReviewerDetector</c> — identified by the
    /// <c>missing-reviewer:</c> DedupKey prefix and a populated TargetAgentId.
    /// </summary>
    public bool CanHandle(FlowFinding finding) =>
        finding.DedupKey?.StartsWith("missing-reviewer:", StringComparison.OrdinalIgnoreCase) == true
        && !string.IsNullOrEmpty(finding.TargetAgentId);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(finding.TargetAgentId))
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Detail = "no target agent id — reviewer agent may not be running yet",
            };
        }

        // DedupKey format: "missing-reviewer:{prNumber}:{role}"
        var parts = finding.DedupKey!.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var prNumber))
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Detail = $"unparseable DedupKey '{finding.DedupKey}' — expected 'missing-reviewer:{{prNumber}}:{{role}}'",
            };
        }

        var role = parts[2];
        var nudgeKey = $"{prNumber}:{role}";
        var now = DateTimeOffset.UtcNow;

        // Idempotency: skip if we already nudged this (PR, role) pair within the cooldown.
        if (_lastNudge.TryGetValue(nudgeKey, out var lastAt) && (now - lastAt) < _nudgeCooldown)
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Target = finding.TargetAgentId,
                Detail = $"Cooldown active — last nudge for PR #{prNumber}/{role} was {(int)(now - lastAt).TotalMinutes}m ago (cooldown: {_nudgeCooldown.TotalMinutes:0}m)",
            };
        }

        try
        {
            var reason = finding.Summary;

            await _messageBus.PublishAsync(new ReviewNudgeMessage
            {
                FromAgentId = "flow-monitor",
                ToAgentId = finding.TargetAgentId,
                MessageType = "ReviewNudge",
                PrNumber = prNumber,
                ReviewerRole = role,
                Reason = reason,
            }, ct).ConfigureAwait(false);

            _lastNudge[nudgeKey] = now;

            // Purge stale entries (> 2 h old) to prevent unbounded growth on long runs.
            foreach (var key in _lastNudge.Keys.ToList())
            {
                if (_lastNudge.TryGetValue(key, out var ts) && (now - ts) > TimeSpan.FromHours(2))
                    _lastNudge.TryRemove(key, out _);
            }

            _logger.LogInformation(
                "NudgeReviewerAction: ReviewNudgeMessage sent to {Agent} ({DisplayName}) for PR #{PrNumber} (role: {Role})",
                finding.TargetAgentId, finding.TargetDisplayName ?? "unknown", prNumber, role);

            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Target = finding.TargetAgentId,
                Detail = $"ReviewNudgeMessage published to {finding.TargetDisplayName ?? finding.TargetAgentId} for PR #{prNumber}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NudgeReviewerAction failed publishing to agent {Agent}", finding.TargetAgentId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetAgentId,
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }
}
