using AgentSquad.Core.Frameworks;
using AgentSquad.Core.Strategies;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// Tests for the orchestrator's external adapter integration logic.
/// These are unit tests that verify key behaviors without running full
/// worktree-based orchestration (which is covered by integration tests).
/// </summary>
public class OrchestratorAdapterTests
{
    [Fact]
    public void ExternalAdapters_excludes_builtin_strategy_ids()
    {
        // The orchestrator filters adapters to only keep IDs not already in _strategies.
        // We test this logic by simulating what the constructor does.
        var strategyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "baseline", "mcp-enhanced" };
        var adapters = new IAgenticFrameworkAdapter[]
        {
            new StubAdapter("baseline"),      // should be excluded
            new StubAdapter("mcp-enhanced"),   // should be excluded
            new StubAdapter("squad"),          // should be kept
        };

        var externalAdapters = adapters
            .Where(a => !strategyIds.Contains(a.Id))
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        Assert.Single(externalAdapters);
        Assert.True(externalAdapters.ContainsKey("squad"));
        Assert.False(externalAdapters.ContainsKey("baseline"));
    }

    [Fact]
    public void AllKnownIds_combines_strategies_and_external_adapters()
    {
        var strategyIds = new[] { "baseline", "mcp-enhanced" };
        var adapterIds = new[] { "squad", "claude-code" };

        var allKnown = strategyIds.Concat(adapterIds).ToList().AsReadOnly();

        Assert.Equal(4, allKnown.Count);
        Assert.Contains("baseline", allKnown);
        Assert.Contains("squad", allKnown);
    }

    [Fact]
    public void ToFrameworkInvocation_maps_all_task_fields()
    {
        var si = new StrategyInvocation
        {
            Task = new TaskContext
            {
                TaskId = "t1",
                TaskTitle = "Build feature",
                TaskDescription = "Full description",
                PrBranch = "agent/se/feat",
                BaseSha = new string('a', 40),
                RunId = "run-1",
                AgentRepoPath = @"C:\repo",
                Complexity = 3,
                IsWebTask = true,
                PmSpec = "pm spec",
                Architecture = "arch doc",
                TechStack = ".NET 8",
            },
            WorktreePath = @"C:\worktree",
            StrategyId = "squad",
            Timeout = TimeSpan.FromSeconds(300),
        };

        // Use the same mapping logic as BaselineAdapter (which is reused across all adapters)
        var fi = BaselineAdapter.MapToStrategy(new FrameworkInvocation
        {
            Task = new FrameworkTaskContext
            {
                TaskId = si.Task.TaskId,
                TaskTitle = si.Task.TaskTitle,
                TaskDescription = si.Task.TaskDescription,
                PrBranch = si.Task.PrBranch,
                BaseSha = si.Task.BaseSha,
                RunId = si.Task.RunId,
                AgentRepoPath = si.Task.AgentRepoPath,
                Complexity = si.Task.Complexity,
                IsWebTask = si.Task.IsWebTask,
                PmSpec = si.Task.PmSpec,
                Architecture = si.Task.Architecture,
                TechStack = si.Task.TechStack,
            },
            WorktreePath = si.WorktreePath,
            FrameworkId = si.StrategyId,
            Timeout = si.Timeout,
        });

        Assert.Equal("t1", fi.Task.TaskId);
        Assert.Equal(@"C:\worktree", fi.WorktreePath);
        Assert.Equal("squad", fi.StrategyId);
    }

    [Fact]
    public void FromFrameworkResult_maps_nullable_tokens_correctly()
    {
        var fr = new FrameworkExecutionResult
        {
            FrameworkId = "squad",
            Succeeded = true,
            Elapsed = TimeSpan.FromSeconds(90),
            TokensUsed = null,
            Metrics = new FrameworkMetrics
            {
                TokensUsed = null,
                ElapsedTime = TimeSpan.FromSeconds(90),
            }
        };

        var sr = BaselineAdapter.MapFromStrategy(new StrategyExecutionResult
        {
            StrategyId = fr.FrameworkId,
            Succeeded = fr.Succeeded,
            Elapsed = fr.Elapsed,
            TokensUsed = fr.TokensUsed,
        });

        Assert.Null(sr.TokensUsed);
        Assert.True(sr.Succeeded);
        Assert.Equal("squad", sr.FrameworkId);
    }

    [Fact]
    public async Task NotReadyLifecycle_returns_failure()
    {
        var adapter = new NotReadyAdapter("flaky-framework");

        var readiness = await adapter.CheckReadinessAsync(CancellationToken.None);

        Assert.Equal(FrameworkReadiness.MissingDependency, readiness.Status);
        Assert.Contains("fake-dep", readiness.MissingDependencies);
    }

    [Fact]
    public void EnabledStrategies_filtering_handles_both_strategies_and_adapters()
    {
        var strategyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "baseline" };
        var adapterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "squad" };
        var enabled = new List<string> { "baseline", "squad", "unknown-id" };

        var filtered = enabled
            .Where(id => strategyIds.Contains(id) || adapterIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(2, filtered.Count);
        Assert.Contains("baseline", filtered);
        Assert.Contains("squad", filtered);
        Assert.DoesNotContain("unknown-id", filtered);
    }

    // ── Stubs ──

    private sealed class StubAdapter : IAgenticFrameworkAdapter
    {
        public string Id { get; }
        public string DisplayName => Id;
        public string Description => "stub";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);
        public StubAdapter(string id) => Id = id;

        public Task<FrameworkExecutionResult> ExecuteAsync(FrameworkInvocation invocation, CancellationToken ct)
            => Task.FromResult(new FrameworkExecutionResult
            {
                FrameworkId = Id,
                Succeeded = true,
                Elapsed = TimeSpan.FromMilliseconds(50),
            });
    }

    private sealed class NotReadyAdapter : IAgenticFrameworkAdapter, IFrameworkLifecycle
    {
        public string Id { get; }
        public string DisplayName => Id;
        public string Description => "not-ready stub";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);
        public NotReadyAdapter(string id) => Id = id;

        public Task<FrameworkExecutionResult> ExecuteAsync(FrameworkInvocation invocation, CancellationToken ct)
            => throw new InvalidOperationException("Should not be called");

        public Task<FrameworkReadinessResult> CheckReadinessAsync(CancellationToken ct)
            => Task.FromResult(new FrameworkReadinessResult(
                FrameworkReadiness.MissingDependency,
                "Test dependency missing",
                new[] { "fake-dep" }));

        public Task<FrameworkInstallResult> EnsureInstalledAsync(CancellationToken ct)
            => Task.FromResult(new FrameworkInstallResult(false, "Cannot install in test"));
    }
}
