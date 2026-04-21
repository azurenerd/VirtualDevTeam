using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Phase 5 (adaptive). Reads historical experiment records from the on-disk
/// ndjson corpus written by <see cref="ExperimentTracker"/> and computes, per
/// strategy id, running win-rate / survival-rate / token-cost statistics.
///
/// Intended use: the orchestrator (or operator) can query this to auto-tune
/// <see cref="StrategyFrameworkConfig.EnabledStrategies"/> — e.g. demote a
/// strategy whose survival-rate is under a threshold over the last N tasks.
///
/// This implementation is feature-flagged (OFF by default) via
/// <see cref="StrategyFrameworkConfig.Adaptive.Enabled"/>. When OFF, the
/// selector is a no-op and the enabled list passes through unchanged. This
/// matches the RESUME-doc sequencing: adaptive tuning only becomes useful
/// once <c>val-e2e</c> has produced real data. Until then, the class exists,
/// is unit-tested against a fixture corpus, and is wired into DI but inert.
/// </summary>
public sealed class AdaptiveStrategySelector
{
    private readonly ILogger<AdaptiveStrategySelector> _logger;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ExperimentTracker _tracker;

    public AdaptiveStrategySelector(
        ILogger<AdaptiveStrategySelector> logger,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ExperimentTracker tracker)
    {
        _logger = logger;
        _cfg = cfg;
        _tracker = tracker;
    }

    /// <summary>
    /// Filter the passed-in list by historical performance. When adaptive mode
    /// is off, returns the input unchanged. When on, drops any strategy whose
    /// survival rate over the last <see cref="AdaptiveConfig.WindowSize"/>
    /// tasks is below <see cref="AdaptiveConfig.MinSurvivalRate"/>. Baseline is
    /// always kept as a safety net (so we always have a default answer).
    /// </summary>
    public IReadOnlyList<string> FilterByHistory(IReadOnlyList<string> enabled)
    {
        var cfg = _cfg.CurrentValue.Adaptive;
        if (cfg is null || !cfg.Enabled) return enabled;

        var stats = ComputeStats(cfg.WindowSize);
        var kept = new List<string>();
        foreach (var id in enabled)
        {
            if (string.Equals(id, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(id);
                continue;
            }

            if (!stats.TryGetValue(id, out var s) || s.TotalObservations < cfg.MinObservations)
            {
                // Not enough data yet — keep exploring it.
                kept.Add(id);
                continue;
            }

            if (s.SurvivalRate < cfg.MinSurvivalRate)
            {
                _logger.LogInformation(
                    "AdaptiveStrategySelector: dropping {Strategy} (survival={Rate:P1} over {N} tasks < min {Min:P1})",
                    id, s.SurvivalRate, s.TotalObservations, cfg.MinSurvivalRate);
                continue;
            }

            kept.Add(id);
        }
        return kept;
    }

    /// <summary>
    /// Scan all ndjson files in <see cref="ExperimentTracker.ResolveDirectory"/>,
    /// read up to <paramref name="windowSize"/> most-recent records per strategy,
    /// and roll up per-strategy statistics. Records with unparseable JSON are
    /// skipped (corruption-tolerant). Ndjson files that don't exist yet (fresh
    /// install) yield an empty map.
    /// </summary>
    public Dictionary<string, StrategyStats> ComputeStats(int windowSize)
    {
        var dir = _tracker.ResolveDirectory();
        if (!Directory.Exists(dir)) return new();

        // Read all records across all runs, ordered newest-first.
        var records = new List<ExperimentRecord>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.ndjson"))
        {
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<ExperimentRecord>(line);
                        if (rec is not null) records.Add(rec);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogDebug(ex, "Skipping corrupt ndjson line in {File}", file);
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable experiment file {File}", file);
            }
        }

        // Per strategy: take the most recent N observations.
        var ordered = records.OrderByDescending(r => r.CompletedAt).ToList();
        var per = new Dictionary<string, StrategyStatsBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in ordered)
        {
            foreach (var cand in rec.Candidates)
            {
                if (!per.TryGetValue(cand.StrategyId, out var b))
                {
                    b = new StrategyStatsBuilder();
                    per[cand.StrategyId] = b;
                }
                if (b.Count >= windowSize) continue;
                b.Add(cand, won: string.Equals(rec.WinnerStrategyId, cand.StrategyId, StringComparison.OrdinalIgnoreCase));
            }
        }

        return per.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Build(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StrategyStatsBuilder
    {
        public int Count;
        public int Wins;
        public int Survived;
        public long TokensSum;

        public void Add(CandidateRecord c, bool won)
        {
            Count++;
            if (won) Wins++;
            if (c.Succeeded) Survived++;
            TokensSum += c.TokensUsed ?? 0;
        }

        public StrategyStats Build() => new()
        {
            TotalObservations = Count,
            Wins = Wins,
            Survivors = Survived,
            WinRate = Count == 0 ? 0 : (double)Wins / Count,
            SurvivalRate = Count == 0 ? 0 : (double)Survived / Count,
            AverageTokens = Count == 0 ? 0 : (double)TokensSum / Count,
        };
    }
}

public sealed record StrategyStats
{
    public int TotalObservations { get; init; }
    public int Wins { get; init; }
    public int Survivors { get; init; }
    public double WinRate { get; init; }
    public double SurvivalRate { get; init; }
    public double AverageTokens { get; init; }
}
