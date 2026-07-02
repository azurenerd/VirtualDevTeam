using System.Diagnostics;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Regression tests for three bugs surfaced during the val-e2e live run
/// against the real Copilot CLI binary (docs/StrategyFramework.md Status row
/// "val-e2e"). All three must have explicit test coverage so the
/// "684 pass / 10 pre-existing E2E red" validation rule remains meaningful
/// before flipping StrategyFramework.Enabled on a live run.
/// </summary>
public class ValE2eBugRegressionTests : IDisposable
{
    private readonly string _repo;

    public ValE2eBugRegressionTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "valbug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.email", "t@t");
        Git(_repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# valbug\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_repo)) ForceDelete(_repo); } catch { /* best effort */ }
    }

    /// <summary>
    /// Bug #1: .NET IConfiguration.Bind APPENDS list items to any default
    /// List&lt;T&gt; initializer on the target property rather than replacing
    /// it. The observed failure was `Orchestrating 4 strategies ...
    /// baseline,mcp-enhanced,baseline,mcp-enhanced` causing the same
    /// strategy to run twice per task, doubling token spend AND racing on
    /// cleanup of the shared candidate directory. Distinct() in
    /// StrategyOrchestrator.RunCandidatesAsync is the surgical fix.
    /// </summary>
    [Fact]
    public async Task Duplicated_EnabledStrategies_runs_each_strategy_exactly_once()
    {
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        var cfg = new StrategyFrameworkConfig
        {
            Enabled = true,
            // Simulates the config-binding append bug's effect.
            EnabledStrategies = new() { "dummy-counter", "dummy-counter", "dummy-counter" },
            ExperimentDataDirectory = Path.Combine(_repo, "experiment-data"),
        };
        var monitor = new StaticMonitor(cfg);
        var counter = new CountingStrategy("dummy-counter");
        var orch = BuildOrch(monitor, new ICodeGenerationStrategy[] { counter });

        var task = new TaskContext
        {
            TaskId = "T-dupe",
            TaskTitle = "Dupe test",
            TaskDescription = "",
            PrBranch = "feat/dupe",
            BaseSha = baseSha,
            RunId = "run-dupe",
            AgentRepoPath = _repo,
        };

        await orch.RunCandidatesAsync(task, CancellationToken.None);

        Assert.Equal(1, counter.InvocationCount);
    }

    /// <summary>
    /// Bug #3: RunOneAsync emitted CandidateStarted BEFORE calling
    /// GitWorktreeManager.CreateAsync. When CreateAsync threw (observed cause:
    /// concurrent `.git/config.lock` race), no CandidateCompleted event fired
    /// and the dashboard's CandidateStateStore left state=Running forever.
    /// The fix catches CreateAsync failures, emits Completed(succeeded=false),
    /// and returns a non-faulted Task so RunCandidatesAsync's WhenAll doesn't
    /// abort the entire orchestration when one candidate's worktree setup
    /// fails.
    /// </summary>
    [Fact]
    public async Task CreateAsync_failure_emits_CandidateCompleted_and_does_not_fault_WhenAll()
    {
        var cfg = new StrategyFrameworkConfig
        {
            Enabled = true,
            EnabledStrategies = new() { "dummy-never-called" },
            ExperimentDataDirectory = Path.Combine(_repo, "experiment-data"),
        };
        var monitor = new StaticMonitor(cfg);
        var spy = new CountingEventSink();
        var strategy = new CountingStrategy("dummy-never-called");

        var orch = BuildOrch(monitor, new ICodeGenerationStrategy[] { strategy }, spy);

        var task = new TaskContext
        {
            TaskId = "T-bad-sha",
            TaskTitle = "Bad base SHA",
            TaskDescription = "",
            PrBranch = "feat/bad",
            // Nonexistent SHA — guarantees `git worktree add` returns 128.
            BaseSha = "0000000000000000000000000000000000000000",
            RunId = "run-bad",
            AgentRepoPath = _repo,
        };

        // The whole orchestration must not throw even though CreateAsync fails.
        var outcome = await orch.RunCandidatesAsync(task, CancellationToken.None);

        // Strategy ExecuteAsync must NEVER be reached when worktree create fails.
        Assert.Equal(0, strategy.InvocationCount);
        // Started + Completed must both fire so the dashboard can transition the
        // candidate out of "Running".
        Assert.Equal(1, spy.StartedCount);
        Assert.Equal(1, spy.CompletedCount);
        Assert.False(spy.LastCompletedSucceeded);
        Assert.Contains("worktree-create", spy.LastFailureReason ?? "");
        // No winner (nothing survived), but the orchestration completed cleanly.
        Assert.Null(outcome.Evaluation.Winner);
    }

    /// <summary>
    /// Bug #2: concurrent candidates that share the same agentRepoPath
    /// raced on `.git/config.lock` during the pre-add phase (`git config
    /// --local extensions.worktreeConfig true` + `git worktree add`). The
    /// fix serializes the pre-add phase per-repo with a SemaphoreSlim.
    /// This test hammers the manager with 4 concurrent CreateAsync calls
    /// against the same repo and asserts all succeed.
    /// </summary>
    [Fact]
    public async Task Concurrent_CreateAsync_on_same_repo_does_not_race_on_git_config_lock()
    {
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        var worktree = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);

        var tasks = Enumerable.Range(0, 4).Select(i =>
            Task.Run(async () =>
            {
                var handle = await worktree.CreateAsync(
                    _repo, ".candidates", "Tcc", $"s{i}", baseSha, CancellationToken.None);
                await handle.DisposeAsync();
            })).ToArray();

        await Task.WhenAll(tasks);
        // Assert.* not needed — a race would throw InvalidOperationException
        // from RunGitAsync wrapping git's exit-128 config-lock error.
    }

    // --- helpers below ---

    private StrategyOrchestrator BuildOrch(
        StaticMonitor monitor,
        ICodeGenerationStrategy[] strategies,
        IStrategyEventSink? sink = null)
    {
        var worktree = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);
        var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, monitor);
        var gate = new StrategyConcurrencyGate(monitor);
        var evaluator = new CandidateEvaluator(NullLogger<CandidateEvaluator>.Instance, worktree, monitor);
        return new StrategyOrchestrator(
            NullLogger<StrategyOrchestrator>.Instance,
            worktree, evaluator, tracker, gate, monitor, strategies,
            sink);
    }

    private sealed class CountingStrategy : ICodeGenerationStrategy
    {
        public string Id { get; }
        public int InvocationCount;
        public CountingStrategy(string id) { Id = id; }
        public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
        {
            Interlocked.Increment(ref InvocationCount);
            await File.WriteAllTextAsync(Path.Combine(invocation.WorktreePath, $".{Id}.md"), "x\n", ct);
            return new StrategyExecutionResult { StrategyId = Id, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(5), TokensUsed = 10 };
        }
    }

    private sealed class CountingEventSink : IStrategyEventSink
    {
        public int StartedCount;
        public int CompletedCount;
        public bool LastCompletedSucceeded;
        public string? LastFailureReason;
        public Task EmitAsync(string eventName, object payload, CancellationToken ct)
        {
            if (payload is CandidateStartedEvent) Interlocked.Increment(ref StartedCount);
            else if (payload is CandidateCompletedEvent c)
            {
                Interlocked.Increment(ref CompletedCount);
                LastCompletedSucceeded = c.Succeeded;
                LastFailureReason = c.FailureReason;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class StaticMonitor : IOptionsMonitor<StrategyFrameworkConfig>
    {
        private readonly StrategyFrameworkConfig _v;
        public StaticMonitor(StrategyFrameworkConfig v) { _v = v; }
        public StrategyFrameworkConfig CurrentValue => _v;
        public StrategyFrameworkConfig Get(string? name) => _v;
        public IDisposable OnChange(Action<StrategyFrameworkConfig, string?> _) => new Null();
        private sealed class Null : IDisposable { public void Dispose() { } }
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} => {p.ExitCode}: {e}");
        return o;
    }

    private static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(dir, true);
    }
}
