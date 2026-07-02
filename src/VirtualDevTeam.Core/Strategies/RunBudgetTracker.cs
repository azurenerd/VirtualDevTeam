using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Per-run token/request budget tracker. Tracks cumulative consumption keyed by
/// runId and trips a circuit breaker once configured caps are exceeded. All methods
/// are thread-safe.
/// </summary>
public class RunBudgetTracker
{
    private readonly ILogger<RunBudgetTracker> _logger;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ConcurrentDictionary<string, RunCounters> _runs = new();

    public RunBudgetTracker(ILogger<RunBudgetTracker> logger, IOptionsMonitor<StrategyFrameworkConfig> cfg)
    {
        _logger = logger;
        _cfg = cfg;
    }

    /// <summary>Charge tokens to a run; returns true if still under budget.</summary>
    public bool Charge(string runId, long tokens, long requests = 1)
    {
        if (string.IsNullOrEmpty(runId)) return true;
        var c = _runs.GetOrAdd(runId, _ => new RunCounters());
        Interlocked.Add(ref c.Tokens, tokens);
        Interlocked.Add(ref c.Requests, requests);
        var cfg = _cfg.CurrentValue.Budget;
        if (cfg.MaxTokensPerRun > 0 && c.Tokens > cfg.MaxTokensPerRun)
        {
            if (Interlocked.Exchange(ref c.BreakerTripped, 1) == 0)
                _logger.LogWarning("Run {Run} exceeded token budget ({Used}/{Cap}); breaker tripped", runId, c.Tokens, cfg.MaxTokensPerRun);
            return false;
        }
        return true;
    }

    public bool IsExhausted(string runId) =>
        _runs.TryGetValue(runId, out var c) && Volatile.Read(ref c.BreakerTripped) == 1;

    public RunSnapshot Snapshot(string runId) =>
        _runs.TryGetValue(runId, out var c)
            ? new RunSnapshot(c.Tokens, c.Requests, Volatile.Read(ref c.BreakerTripped) == 1)
            : new RunSnapshot(0, 0, false);

    public void Reset(string runId) => _runs.TryRemove(runId, out _);

    private sealed class RunCounters
    {
        public long Tokens;
        public long Requests;
        public int BreakerTripped;
    }
}

public readonly record struct RunSnapshot(long Tokens, long Requests, bool BreakerTripped);
