using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.1 IdleAgentPhaseStuckDetector — reviewer agent (PM / Architect / TE / SE-as-reviewer)
/// has been Idle for ≥ threshold while at least one open PR is waiting in a label state
/// that requires that agent's review/output. This is the single most-impactful new
/// detector per the fm-11 research synthesis — it fires for the most common stall pattern.
///
/// <para>
/// Roles + gates (mirrors the canonical PR flow):
/// </para>
/// <list type="bullet">
///   <item>Architect Idle + PR has <c>ready-for-review</c> + no <c>architect-approved</c></item>
///   <item>PM Idle + PR has <c>architect-approved</c> + <c>tests-added</c> + no <c>pm-approved</c></item>
///   <item>Test Engineer Idle + PR has <c>architect-approved</c> + no <c>tests-added</c></item>
///   <item>Software Engineer (as final approver) Idle + PR has all 3 review labels + no merge</item>
/// </list>
///
/// <para>
/// **Note:** the final-approver case overlaps with the existing
/// <see cref="UnmergedApprovedPrDetector"/>. We keep both — different threshold (this one
/// is keyed on the *agent's* idle time, not on PR idle time) — and dedup keys differ.
/// </para>
/// </summary>
public sealed class IdleAgentPhaseStuckDetector : IFlowDetector
{
    public string DetectorId => "idle-agent-phase-stuck";

    private readonly ILogger<IdleAgentPhaseStuckDetector> _logger;
    private readonly TimeSpan _idleThreshold;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);

    public IdleAgentPhaseStuckDetector(
        ILogger<IdleAgentPhaseStuckDetector> logger,
        TimeSpan? idleThreshold = null,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _logger = logger;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(15);
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            if (prs.Count == 0) return findings;

            foreach (var agent in ctx.Agents)
            {
                if (!string.Equals(agent.Status, "Idle", StringComparison.OrdinalIgnoreCase)) continue;
                if (agent.StatusChangedAt is null) continue;
                var idleFor = ctx.Now - agent.StatusChangedAt.Value;
                if (idleFor < _idleThreshold) continue;

                // Skip if agent has recent log activity or active LLM call
                if (HasRecentActivity(agent.Id)) continue;

                var role = agent.Role ?? string.Empty;
                foreach (var pr in prs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!ExpectedToActOn(role, pr, out var reason)) continue;

                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Warning,
                        TargetAgentId = agent.Id,
                        TargetDisplayName = agent.DisplayName,
                        TargetResource = $"pr#{pr.Number}",
                        Summary = $"{agent.DisplayName} idle for {FormatDuration(idleFor)} while PR #{pr.Number} " +
                                  $"awaits their action ({reason}).",
                        Rationale = "Reviewer/processor agent has been Idle longer than the phase-stuck threshold " +
                                    "while an open PR is in a label state requiring their output. Common causes: " +
                                    "agent's review-poll loop predicate doesn't match the actual PR state, the " +
                                    "agent crashed and was re-spawned without picking up open work, or a message " +
                                    "bus dispatch was lost. The escalation ladder will nudge → comment → escalate.",
                        DedupKey = $"idle-agent-phase-stuck:{agent.Id}:{pr.Number}",
                    });
                    // One finding per agent per tick — escalation ladder handles the rest.
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IdleAgentPhaseStuckDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static bool ExpectedToActOn(string role, PullRequestView pr, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(role)) return false;

        bool has(string label) => pr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        if (role.Contains("Architect", StringComparison.OrdinalIgnoreCase))
        {
            if (has("ready-for-review") && !has("architect-approved"))
            {
                reason = "ready-for-review awaiting architect approval";
                return true;
            }
        }
        else if (role.Contains("ProgramManager", StringComparison.OrdinalIgnoreCase)
              || role.Contains("Program Manager", StringComparison.OrdinalIgnoreCase)
              || role.Contains("ProjectManager", StringComparison.OrdinalIgnoreCase)
              || role.Equals("PM", StringComparison.OrdinalIgnoreCase))
        {
            if (has("architect-approved") && has("tests-added") && !has("pm-approved"))
            {
                reason = "architect-approved + tests-added awaiting PM approval";
                return true;
            }
        }
        else if (role.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
              || role.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
        {
            if (has("architect-approved") && !has("tests-added"))
            {
                reason = "architect-approved awaiting TE tests-added";
                return true;
            }
        }
        return false;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{ts.TotalHours:0.0}h";
    }

    private bool HasRecentActivity(string agentId)
    {
        if (_logService is not null)
        {
            var lastLog = _logService.GetLatestEntryTimestamp(agentId);
            if (lastLog.HasValue && (DateTime.UtcNow - lastLog.Value) < LogActivityWindow)
                return true;
        }
        return _llmTracker?.GetActiveCall(agentId) is not null;
    }
}
