using System.Diagnostics;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// End-to-end orchestrator test against a real (throwaway) git repo. Creates a temp
/// repo, seeds a commit, runs the orchestrator with two dummy strategies, and
/// verifies: both worktrees created, patches extracted, evaluator picks a winner,
/// experiment record is written, worktrees cleaned up.
/// </summary>
public class OrchestratorIntegrationTests : IDisposable
{
    private readonly string _repo;
    private readonly string _expDir;

    public OrchestratorIntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "orch-test-" + Guid.NewGuid().ToString("N"));
        _expDir = Path.Combine(_repo, "experiment-data");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.email", "t@t");
        Git(_repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# test\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_repo)) ForceDelete(_repo); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Orchestrator_runs_baseline_plus_dummy_and_writes_record()
    {
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        // Baseline is excluded from orchestration (removed from UI — never compete),
        // so use two dummy strategies to verify multi-candidate evaluation.
        var cfg = new StrategyFrameworkConfig
        {
            Enabled = true,
            EnabledStrategies = new() { "dummy-alpha", "dummy-good" },
            ExperimentDataDirectory = _expDir,
        };
        var monitor = new StaticMonitor(cfg);

        var worktree = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);
        var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, monitor);
        var gate = new StrategyConcurrencyGate(monitor);
        var evaluator = new CandidateEvaluator(NullLogger<CandidateEvaluator>.Instance, worktree, monitor);

        var strategies = new ICodeGenerationStrategy[]
        {
            new DummyStrategy("dummy-alpha"),
            new DummyStrategy("dummy-good"),
        };

        var orch = new StrategyOrchestrator(
            NullLogger<StrategyOrchestrator>.Instance,
            worktree, evaluator, tracker, gate, monitor, strategies);

        var task = new TaskContext
        {
            TaskId = "T1",
            TaskTitle = "Test",
            TaskDescription = "",
            PrBranch = "feat/t1",
            BaseSha = baseSha,
            RunId = "run-int",
            AgentRepoPath = _repo,
        };

        var outcome = await orch.RunCandidatesAsync(task, CancellationToken.None);

        Assert.NotNull(outcome.Evaluation.Winner);
        Assert.Equal(2, outcome.Evaluation.Candidates.Count);
        Assert.Contains(outcome.Evaluation.Candidates, c => c.Survived);

        // Experiment file exists and has 1 line
        var file = tracker.ResolveFile("run-int");
        Assert.True(File.Exists(file));
        Assert.Single(File.ReadAllLines(file));

        // No leaked candidate worktrees (cleanup happened in `finally`).
        var candidatesRoot = Path.Combine(_repo, cfg.CandidateDirectoryName);
        if (Directory.Exists(candidatesRoot))
        {
            var strays = Directory.EnumerateDirectories(candidatesRoot, "*", SearchOption.AllDirectories)
                .Where(p => Directory.EnumerateFiles(p).Any()).ToList();
            Assert.Empty(strays);
        }
    }

    [Fact]
    public async Task Orchestrator_rejects_strategy_writing_to_reserved_path()
    {
        // A strategy that writes inside the reserved evaluator path should fail Gate2 and
        // never become a winner — the path-validation hook fires before the build step.
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        var cfg = new StrategyFrameworkConfig
        {
            Enabled = true,
            EnabledStrategies = new() { "rogue-reserved", "dummy-good" },
            ExperimentDataDirectory = _expDir,
        };
        var monitor = new StaticMonitor(cfg);

        var worktree = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);
        var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, monitor);
        var gate = new StrategyConcurrencyGate(monitor);
        var evaluator = new CandidateEvaluator(NullLogger<CandidateEvaluator>.Instance, worktree, monitor);

        var strategies = new ICodeGenerationStrategy[]
        {
            new ReservedPathStrategy("rogue-reserved", cfg.Evaluator.ReservedPathPrefix),
            new DummyStrategy("dummy-good"),
        };

        var orch = new StrategyOrchestrator(
            NullLogger<StrategyOrchestrator>.Instance,
            worktree, evaluator, tracker, gate, monitor, strategies);

        var task = new TaskContext
        {
            TaskId = "T-reserved",
            TaskTitle = "Reserved",
            TaskDescription = "",
            PrBranch = "feat/reserved",
            BaseSha = baseSha,
            RunId = "run-reserved",
            AgentRepoPath = _repo,
        };

        var outcome = await orch.RunCandidatesAsync(task, CancellationToken.None);

        var rogue = outcome.Evaluation.Candidates.Single(c => c.StrategyId == "rogue-reserved");
        Assert.False(rogue.Survived);
        Assert.Equal("gate2-build", rogue.FailedGate);
        Assert.Contains("reserved-path", rogue.FailureDetail ?? "");
        // The other candidate should still be selectable as the winner.
        Assert.NotNull(outcome.Evaluation.Winner);
        Assert.Equal("dummy-good", outcome.Evaluation.Winner!.StrategyId);
    }

    private sealed class ReservedPathStrategy : ICodeGenerationStrategy
    {
        public string Id { get; }
        private readonly string _reservedPrefix;
        public ReservedPathStrategy(string id, string reservedPrefix)
        { Id = id; _reservedPrefix = reservedPrefix; }
        public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
        {
            var rel = _reservedPrefix.Replace('\\', '/').TrimEnd('/') + "/leaked.txt";
            var full = Path.Combine(invocation.WorktreePath, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, "should not be allowed\n", ct);
            return new StrategyExecutionResult { StrategyId = Id, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(5), TokensUsed = 50 };
        }
    }

    private sealed class DummyStrategy : ICodeGenerationStrategy
    {
        public string Id { get; }
        public DummyStrategy(string id) { Id = id; }
        public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path.Combine(invocation.WorktreePath, $".{Id}.md"), "hello\n", ct);
            return new StrategyExecutionResult { StrategyId = Id, Succeeded = true, Elapsed = TimeSpan.FromMilliseconds(5), TokensUsed = 100 };
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
