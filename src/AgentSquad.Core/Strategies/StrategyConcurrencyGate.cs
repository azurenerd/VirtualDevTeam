using Microsoft.Extensions.Options;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Global concurrency gate above the three <see cref="AgentSquad.Core.AI.CopilotCliProcessManager"/>
/// pools. A single uniform <see cref="SemaphoreSlim"/> caps total concurrent copilot
/// processes across strategies so single-shot + candidate + copilot-cli traffic cannot
/// collectively exceed a safe limit (defaults to 6, see
/// <see cref="ConcurrencyConfig.GlobalMaxConcurrentProcesses"/>). Acquired by the
/// process manager AFTER the per-pool semaphore — this ordering matters: pool-first
/// ordering prevents copilot-cli slots from starving baseline slots under contention.
/// Also offers a cooperative "degrade mode" hook for 429/backoff responses.
/// </summary>
public class StrategyConcurrencyGate
{
    private readonly SemaphoreSlim _global;
    private int _degraded;

    public StrategyConcurrencyGate(IOptionsMonitor<StrategyFrameworkConfig> monitor)
    {
        var initial = Math.Max(1, monitor.CurrentValue.Concurrency.GlobalMaxConcurrentProcesses);
        _global = new SemaphoreSlim(initial, initial);
    }

    public bool IsDegraded => Volatile.Read(ref _degraded) != 0;
    public void EnterDegraded() => Interlocked.Exchange(ref _degraded, 1);
    public void ExitDegraded() => Interlocked.Exchange(ref _degraded, 0);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _global.WaitAsync(ct);
        return new Release(_global);
    }

    private sealed class Release : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private int _released;
        public Release(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) _sem.Release(); }
    }
}
