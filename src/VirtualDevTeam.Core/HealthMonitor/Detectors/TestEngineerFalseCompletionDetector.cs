using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.2 TEFalseCompletionDetector — Test Engineer is Idle (often with a status reason
/// like "all PRs tested") but at least one open PR with <c>architect-approved</c>
/// lacks the <c>tests-added</c> label. The TE's poll-loop has falsely concluded its
/// queue is empty while real test work is pending.
///
/// <para>
/// This maps directly to finding #12 in the tracking DB: PR #1261 merged with two
/// failing UI tests because <c>tests-added</c> was rubber-stamped. While the merge
/// gate fix lives elsewhere, this detector surfaces the upstream TE-idle anomaly so
/// the operator notices before merge happens.
/// </para>
///
/// <para>
/// Match logic: any agent with <c>Role</c> containing "TestEngineer" / "Test Engineer".
/// We don't fire if no TE is registered (e.g. a project that runs without a TE).
/// </para>
///
/// <para>
/// disable-te-toggle: when <see cref="ReviewConfig.TestEngineerReviews"/> is false the
/// detector returns immediately — TE intentionally not participating, so an idle TE +
/// architect-approved-without-tests-added PR is the desired steady state, not an anomaly.
/// </para>
/// </summary>
public sealed class TestEngineerFalseCompletionDetector : IFlowDetector
{
    public string DetectorId => "te-false-completion";

    private readonly ILogger<TestEngineerFalseCompletionDetector> _logger;
    private readonly TimeSpan _idleThreshold;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);

    public TestEngineerFalseCompletionDetector(
        ILogger<TestEngineerFalseCompletionDetector> logger,
        TimeSpan? idleThreshold = null,
        IOptionsMonitor<VirtualDevTeamConfig>? config = null,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _logger = logger;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(3);
        _config = config;
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        // disable-te-toggle: short-circuit when TE is intentionally disabled.
        if (_config?.CurrentValue?.Review?.TestEngineerReviews == false)
            return findings;

        try
        {
            var teAgents = ctx.Agents
                .Where(a => (a.Role ?? string.Empty).Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
                         || (a.Role ?? string.Empty).Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
                .Where(a => string.Equals(a.Status, "Idle", StringComparison.OrdinalIgnoreCase))
                .Where(a => a.StatusChangedAt is null || ctx.Now - a.StatusChangedAt.Value >= _idleThreshold)
                .Where(a => !HasRecentActivity(a.Id))
                .ToList();
            if (teAgents.Count == 0) return findings;

            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            var missingPRs = prs
                .Where(p => p.Labels.Contains("architect-approved", StringComparer.OrdinalIgnoreCase)
                         && !p.Labels.Contains("tests-added", StringComparer.OrdinalIgnoreCase)
                         && !p.Labels.Contains("agent-stuck", StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missingPRs.Count == 0) return findings;

            foreach (var te in teAgents)
            {
                if (ct.IsCancellationRequested) break;
                var prList = string.Join(", ", missingPRs.Take(5).Select(p => $"#{p.Number}"));
                var extra = missingPRs.Count > 5 ? $" (+{missingPRs.Count - 5} more)" : string.Empty;
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetAgentId = te.Id,
                    TargetDisplayName = te.DisplayName,
                    TargetResource = $"pr#{missingPRs[0].Number}",
                    Summary = $"{te.DisplayName} idle while {missingPRs.Count} PR(s) await test coverage: " +
                              $"{prList}{extra}",
                    Rationale = "Test Engineer reports Idle but open PRs have architect-approved without " +
                                "tests-added — the TE's poll loop has falsely concluded its queue is empty. " +
                                "Common causes: poll-predicate doesn't match the actual label state, the TE " +
                                "agent re-spawned without re-scanning open PRs, or a transient platform " +
                                "fetch error left a stale cached view. Operator should inspect the TE's " +
                                "recent log for missed PRs.",
                    DedupKey = $"te-false-completion:{te.Id}:{missingPRs[0].Number}",
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
            _logger.LogWarning(ex, "TestEngineerFalseCompletionDetector tick failed (non-fatal)");
        }
        return findings;
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
