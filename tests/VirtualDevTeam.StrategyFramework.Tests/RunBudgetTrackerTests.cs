using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class RunBudgetTrackerTests
{
    private sealed class StaticMonitor : IOptionsMonitor<StrategyFrameworkConfig>
    {
        private readonly StrategyFrameworkConfig _v;
        public StaticMonitor(StrategyFrameworkConfig v) { _v = v; }
        public StrategyFrameworkConfig CurrentValue => _v;
        public StrategyFrameworkConfig Get(string? name) => _v;
        public IDisposable OnChange(Action<StrategyFrameworkConfig, string?> _) => new Null();
        private sealed class Null : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Charge_trips_breaker_when_cap_exceeded_and_stays_tripped()
    {
        var cfg = new StrategyFrameworkConfig();
        cfg.Budget.MaxTokensPerRun = 100;
        var tracker = new RunBudgetTracker(NullLogger<RunBudgetTracker>.Instance, new StaticMonitor(cfg));

        Assert.True(tracker.Charge("run", 50));
        Assert.False(tracker.IsExhausted("run"));
        Assert.False(tracker.Charge("run", 60)); // 110 > 100
        Assert.True(tracker.IsExhausted("run"));
        Assert.False(tracker.Charge("run", 1));
    }

    [Fact]
    public void Charge_uncapped_when_max_zero()
    {
        var cfg = new StrategyFrameworkConfig();
        cfg.Budget.MaxTokensPerRun = 0;
        var tracker = new RunBudgetTracker(NullLogger<RunBudgetTracker>.Instance, new StaticMonitor(cfg));
        Assert.True(tracker.Charge("r", 1_000_000));
        Assert.False(tracker.IsExhausted("r"));
    }

    [Fact]
    public void Snapshot_returns_zero_for_unknown_run()
    {
        var cfg = new StrategyFrameworkConfig();
        var tracker = new RunBudgetTracker(NullLogger<RunBudgetTracker>.Instance, new StaticMonitor(cfg));
        var s = tracker.Snapshot("never-used");
        Assert.Equal(0, s.Tokens);
        Assert.False(s.BreakerTripped);
    }
}
