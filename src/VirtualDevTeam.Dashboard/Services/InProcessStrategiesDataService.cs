using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Dashboard.Services;

public sealed class InProcessStrategiesDataService : IStrategiesDataService, IDisposable
{
    private readonly CandidateStateStore _store;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly IOrchestrationCancellationService? _cancellation;
    private int _lastActiveCount;

    public InProcessStrategiesDataService(
        CandidateStateStore store,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        IOrchestrationCancellationService? cancellation = null)
    {
        _store = store;
        _cfg = cfg;
        _cancellation = cancellation;
        _lastActiveCount = _store.GetActiveTasks().Count;
        _store.OnChange += OnStoreChange;
    }

    public int ActiveCount => _store.GetActiveTasks().Count;

    public event Action? OnActiveCountChanged;

    private void OnStoreChange(TaskSnapshot _)
    {
        var current = _store.GetActiveTasks().Count;
        if (current != _lastActiveCount)
        {
            _lastActiveCount = current;
            OnActiveCountChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _store.OnChange -= OnStoreChange;
    }

    public Task<IReadOnlyList<TaskSnapshot>> GetActiveTasksAsync(CancellationToken ct = default)
        => Task.FromResult(_store.GetActiveTasks());

    public Task<IReadOnlyList<TaskSnapshot>> GetRecentTasksAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult(_store.GetRecentTasks(limit));

    public Task<EnabledStrategiesInfo> GetEnabledAsync(CancellationToken ct = default)
    {
        var c = _cfg.CurrentValue;
        return Task.FromResult(new EnabledStrategiesInfo(c.Enabled, c.EnabledStrategies.ToList()));
    }

    public Task<bool> CancelOrchestrationAsync(string runId, string taskId, CancellationToken ct = default)
        => Task.FromResult(_cancellation?.RequestCancellation(runId, taskId) ?? false);

    public Task<bool> CancelCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default)
        => Task.FromResult(_cancellation?.RequestCandidateCancellation(runId, taskId, strategyId) ?? false);

    public Task<bool> ResetCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default)
        => Task.FromResult(_cancellation?.RequestCandidateReset(runId, taskId, strategyId) ?? false);
}
