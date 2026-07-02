using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.9 StatusReasonStagnationDetector — agent is in <c>Working</c> state but the
/// status reason has not changed for many ticks in a row. Distinct from
/// <see cref="AgentStuckDetector"/>: that one fires when <c>StatusChangedAt</c> is
/// old; this one catches agents that update their status text repeatedly to the
/// same value (e.g. "Working on step N" loop without progress).
///
/// <para>
/// State model: this detector is a singleton and holds a per-agent hash cache of
/// the most recent status-reason hash plus a tick counter. Hash differs from prior
/// → counter resets to 1. Hash matches → counter increments. When the counter
/// reaches <see cref="_stagnationTicks"/>, the detector emits a Warning. Dedup key
/// suppresses re-emission until the hash actually changes.
/// </para>
///
/// <para>
/// Cleanup: cache entries for agents not seen this tick are pruned when the cache
/// grows beyond 256 entries (size bound, no time-based eviction needed because
/// agent IDs are stable across a run).
/// </para>
/// </summary>
public sealed class StatusReasonStagnationDetector : IFlowDetector
{
    public string DetectorId => "status-reason-stagnant";

    private readonly ILogger<StatusReasonStagnationDetector> _logger;
    private readonly int _stagnationTicks;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;

    private sealed record HashEntry(string Hash, int TicksSame, DateTimeOffset FirstSeenAt);
    private readonly ConcurrentDictionary<string, HashEntry> _cache = new();

    /// <summary>Max age of the most recent log entry for the agent to be considered "active".</summary>
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);

    public StatusReasonStagnationDetector(
        ILogger<StatusReasonStagnationDetector> logger,
        int stagnationTicks = 20,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _logger = logger;
        // Default 20 ticks (~10 min at 30s poll) — was 6 ticks (~3 min) which over-fired during
        // legitimate Strategy-framework + AI-call work that can sit on the same status reason for
        // 15-30+ minutes. Bumped after observing 34/50 findings flooded by this detector during
        // the 2026-05-11 tower-defense run (NoMessyCodePlan post-Tier-2 monitoring).
        _stagnationTicks = Math.Max(2, stagnationTicks);
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in ctx.Agents)
            {
                seenIds.Add(agent.Id);
                if (!string.Equals(agent.Status, "Working", StringComparison.OrdinalIgnoreCase))
                {
                    _cache.TryRemove(agent.Id, out _);
                    continue;
                }
                var reason = agent.StatusReason ?? string.Empty;
                var hash = ComputeHash(reason);

                var current = _cache.GetOrAdd(agent.Id, _ => new HashEntry(hash, 0, ctx.Now));
                if (!string.Equals(current.Hash, hash, StringComparison.Ordinal))
                {
                    _cache[agent.Id] = new HashEntry(hash, 1, ctx.Now);
                    continue;
                }

                var updated = current with { TicksSame = current.TicksSame + 1 };
                _cache[agent.Id] = updated;

                if (updated.TicksSame < _stagnationTicks) continue;

                // Suppress if the agent has recent log output or an active LLM call —
                // status reason may be stale but the agent is actually making progress
                if (_logService is not null)
                {
                    var lastLog = _logService.GetLatestEntryTimestamp(agent.Id);
                    if (lastLog.HasValue && (DateTime.UtcNow - lastLog.Value) < LogActivityWindow)
                        continue;
                }
                if (_llmTracker?.GetActiveCall(agent.Id) is not null)
                    continue;

                var span = ctx.Now - current.FirstSeenAt;
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetAgentId = agent.Id,
                    TargetDisplayName = agent.DisplayName,
                    TargetResource = agent.Id,
                    Summary = $"Agent {agent.DisplayName} status-reason unchanged for {updated.TicksSame} ticks " +
                              $"(~{FormatDuration(span)}): \"{Truncate(reason, 60)}\"",
                    Rationale = "The agent's Status=Working but the status reason text has not changed across " +
                                $"{updated.TicksSame} consecutive detector ticks. Distinct from AgentStuckDetector " +
                                "which only fires when StatusChangedAt is old — this catches agents that " +
                                "self-update Status with the same reason repeatedly (no real progress).",
                    DedupKey = $"status-reason-stagnant:{agent.Id}:{hash}",
                });
            }

            // Size-bounded prune: if the cache has grown beyond the observed agent set,
            // drop stale entries. Keeps memory bounded across long runs / agent churn.
            if (_cache.Count > Math.Max(256, seenIds.Count * 2))
            {
                foreach (var staleId in _cache.Keys.Where(k => !seenIds.Contains(k)).ToList())
                {
                    _cache.TryRemove(staleId, out _);
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
            _logger.LogWarning(ex, "StatusReasonStagnationDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private static string ComputeHash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{ts.TotalHours:0.0}h";
    }
}
