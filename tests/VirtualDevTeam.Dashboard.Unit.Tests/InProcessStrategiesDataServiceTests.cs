using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Dashboard.Services;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Dashboard.Unit.Tests;

public class InProcessStrategiesDataServiceTests
{
    private sealed class StaticMonitor : IOptionsMonitor<StrategyFrameworkConfig>
    {
        public StaticMonitor(StrategyFrameworkConfig v) { CurrentValue = v; }
        public StrategyFrameworkConfig CurrentValue { get; }
        public StrategyFrameworkConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<StrategyFrameworkConfig, string?> l) => null;
    }

    [Fact]
    public async Task GetActiveTasks_returns_store_snapshot()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r1", "t1", "baseline", DateTimeOffset.UtcNow));
        var svc = new InProcessStrategiesDataService(store, new StaticMonitor(new StrategyFrameworkConfig()));

        var active = await svc.GetActiveTasksAsync();

        Assert.Single(active);
        Assert.Equal("t1", active[0].TaskId);
    }

    [Fact]
    public async Task GetRecentTasks_respects_limit()
    {
        var store = new CandidateStateStore(recentCapacity: 10);
        for (int i = 0; i < 5; i++)
        {
            store.RecordStarted(new CandidateStartedEvent("r", $"t{i}", "baseline", DateTimeOffset.UtcNow));
            store.RecordWinner(new WinnerSelectedEvent("r", $"t{i}", "baseline", "x", 0));
        }
        var svc = new InProcessStrategiesDataService(store, new StaticMonitor(new StrategyFrameworkConfig()));

        var three = await svc.GetRecentTasksAsync(limit: 3);
        Assert.Equal(3, three.Count);
    }

    [Fact]
    public async Task GetEnabledAsync_reflects_live_config()
    {
        var store = new CandidateStateStore();
        var cfg = new StrategyFrameworkConfig { Enabled = true };
        cfg.EnabledStrategies.Clear();
        cfg.EnabledStrategies.Add("baseline");
        cfg.EnabledStrategies.Add("mcp-enhanced");

        var svc = new InProcessStrategiesDataService(store, new StaticMonitor(cfg));

        var info = await svc.GetEnabledAsync();

        Assert.True(info.MasterEnabled);
        Assert.Equal(new[] { "baseline", "mcp-enhanced" }, info.EnabledStrategies);
    }

    [Fact]
    public async Task GetEnabledAsync_reflects_master_off()
    {
        var store = new CandidateStateStore();
        var svc = new InProcessStrategiesDataService(store,
            new StaticMonitor(new StrategyFrameworkConfig { Enabled = false }));
        var info = await svc.GetEnabledAsync();
        Assert.False(info.MasterEnabled);
    }
}
