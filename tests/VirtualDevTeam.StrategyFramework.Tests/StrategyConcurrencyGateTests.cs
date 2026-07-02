using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class StrategyConcurrencyGateTests
{
    private sealed class StaticMonitor : IOptionsMonitor<StrategyFrameworkConfig>
    {
        private readonly StrategyFrameworkConfig _v;
        public StaticMonitor(StrategyFrameworkConfig v) { _v = v; }
        public StrategyFrameworkConfig CurrentValue => _v;
        public StrategyFrameworkConfig Get(string? name) => _v;
        public IDisposable OnChange(Action<StrategyFrameworkConfig, string?> listener) => new Null();
        private sealed class Null : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Acquire_blocks_beyond_global_cap_and_releases()
    {
        var cfg = new StrategyFrameworkConfig();
        cfg.Concurrency.GlobalMaxConcurrentProcesses = 2;
        var gate = new StrategyConcurrencyGate(new StaticMonitor(cfg));

        var a = await gate.AcquireAsync(CancellationToken.None);
        var b = await gate.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await gate.AcquireAsync(cts.Token));

        a.Dispose();
        var c = await gate.AcquireAsync(CancellationToken.None); // now available again
        c.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Degrade_flag_toggles()
    {
        var cfg = new StrategyFrameworkConfig();
        var gate = new StrategyConcurrencyGate(new StaticMonitor(cfg));
        Assert.False(gate.IsDegraded);
        gate.EnterDegraded();
        Assert.True(gate.IsDegraded);
        gate.ExitDegraded();
        Assert.False(gate.IsDegraded);
    }
}
