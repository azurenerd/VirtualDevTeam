using System.Text.Json;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class AdaptiveStrategySelectorTests : IDisposable
{
    private readonly string _dir;
    private readonly StrategyFrameworkConfig _cfg;
    private readonly AdaptiveStrategySelector _selector;

    public AdaptiveStrategySelectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adaptive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cfg = new StrategyFrameworkConfig { ExperimentDataDirectory = _dir };
        var monitor = new StubOptions(_cfg);
        var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, monitor);
        _selector = new AdaptiveStrategySelector(NullLogger<AdaptiveStrategySelector>.Instance, monitor, tracker);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteRecord(string runId, string taskId, string winner, params (string sid, bool survived)[] cands)
    {
        var rec = new ExperimentRecord
        {
            RunId = runId,
            TaskId = taskId,
            TaskTitle = "t",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Candidates = cands.Select(c => new CandidateRecord
            {
                StrategyId = c.sid,
                Succeeded = c.survived,
                ElapsedSec = 1,
                TokensUsed = 1000,
            }).ToList(),
            WinnerStrategyId = winner,
            TotalTokens = 1000 * cands.Length,
        };
        var path = Path.Combine(_dir, $"{runId}.ndjson");
        File.AppendAllText(path, JsonSerializer.Serialize(rec) + "\n");
    }

    [Fact]
    public void Disabled_is_passthrough()
    {
        _cfg.Adaptive.Enabled = false;
        WriteRecord("r1", "t1", "baseline", ("baseline", true), ("mcp-enhanced", false));
        var result = _selector.FilterByHistory(new[] { "baseline", "mcp-enhanced" });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Below_min_observations_keeps_strategy()
    {
        _cfg.Adaptive.Enabled = true;
        _cfg.Adaptive.MinObservations = 10;
        // Only 2 observations for mcp-enhanced; not enough to demote.
        WriteRecord("r1", "t1", "baseline", ("baseline", true), ("mcp-enhanced", false));
        WriteRecord("r1", "t2", "baseline", ("baseline", true), ("mcp-enhanced", false));
        var result = _selector.FilterByHistory(new[] { "baseline", "mcp-enhanced" });
        Assert.Contains("mcp-enhanced", result);
    }

    [Fact]
    public void Low_survival_drops_strategy_but_keeps_baseline()
    {
        _cfg.Adaptive.Enabled = true;
        _cfg.Adaptive.MinObservations = 5;
        _cfg.Adaptive.MinSurvivalRate = 0.5;
        // mcp-enhanced fails every time across 10 tasks.
        for (int i = 0; i < 10; i++)
            WriteRecord("r1", $"t{i}", "baseline", ("baseline", true), ("mcp-enhanced", false));

        var result = _selector.FilterByHistory(new[] { "baseline", "mcp-enhanced" });
        Assert.Contains("baseline", result);
        Assert.DoesNotContain("mcp-enhanced", result);
    }

    [Fact]
    public void High_survival_keeps_strategy()
    {
        _cfg.Adaptive.Enabled = true;
        _cfg.Adaptive.MinObservations = 5;
        _cfg.Adaptive.MinSurvivalRate = 0.5;
        for (int i = 0; i < 10; i++)
            WriteRecord("r1", $"t{i}", "mcp-enhanced", ("baseline", true), ("mcp-enhanced", true));

        var result = _selector.FilterByHistory(new[] { "baseline", "mcp-enhanced" });
        Assert.Contains("mcp-enhanced", result);
    }

    [Fact]
    public void Baseline_always_kept_even_if_survival_low()
    {
        _cfg.Adaptive.Enabled = true;
        _cfg.Adaptive.MinObservations = 5;
        _cfg.Adaptive.MinSurvivalRate = 0.9;
        for (int i = 0; i < 10; i++)
            WriteRecord("r1", $"t{i}", null!, ("baseline", false));

        var result = _selector.FilterByHistory(new[] { "baseline" });
        Assert.Contains("baseline", result);
    }

    [Fact]
    public void ComputeStats_Aggregates_across_files()
    {
        WriteRecord("r1", "t1", "baseline", ("baseline", true));
        WriteRecord("r2", "t2", "baseline", ("baseline", true));
        var stats = _selector.ComputeStats(windowSize: 50);
        Assert.Equal(2, stats["baseline"].TotalObservations);
        Assert.Equal(2, stats["baseline"].Wins);
    }

    [Fact]
    public void Missing_directory_returns_empty()
    {
        Directory.Delete(_dir, true);
        var stats = _selector.ComputeStats(50);
        Assert.Empty(stats);
    }

    private sealed class StubOptions : IOptionsMonitor<StrategyFrameworkConfig>
    {
        public StubOptions(StrategyFrameworkConfig v) { CurrentValue = v; }
        public StrategyFrameworkConfig CurrentValue { get; }
        public StrategyFrameworkConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<StrategyFrameworkConfig, string?> listener) => null;
    }
}
