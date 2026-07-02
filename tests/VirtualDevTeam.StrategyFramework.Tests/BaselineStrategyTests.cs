using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Unit tests for BaselineStrategy's delegation contract: when wired with an
/// IBaselineCodeGenerator it must delegate; when not wired it must keep the
/// marker-file fallback so orchestrator unit tests / lightweight harnesses still
/// produce a non-empty patch.
/// </summary>
public class BaselineStrategyTests : IDisposable
{
    private readonly string _worktree;

    public BaselineStrategyTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "baseline-strat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    private StrategyInvocation NewInvocation() => new()
    {
        Task = new TaskContext
        {
            TaskId = "t1",
            TaskTitle = "Add feature",
            TaskDescription = "",
            PrBranch = "agent/se/feature",
            BaseSha = new string('a', 40),
            RunId = "run-1",
            AgentRepoPath = _worktree,
        },
        WorktreePath = _worktree,
        StrategyId = "baseline",
        Timeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Without_generator_falls_back_to_marker_stub()
    {
        var strategy = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("baseline", result.StrategyId);
        var marker = Path.Combine(_worktree, ".strategy-baseline.md");
        Assert.True(File.Exists(marker));
        Assert.Contains("stub", await File.ReadAllTextAsync(marker), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_generator_delegates_and_propagates_outcome()
    {
        var generator = new RecordingGenerator(succeed: true, filesWritten: 3, tokens: 1234);
        var strategy = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance, generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1234, result.TokensUsed);
        Assert.Equal(1, generator.Calls);
        // Marker must NOT be written when generator path is taken.
        Assert.False(File.Exists(Path.Combine(_worktree, ".strategy-baseline.md")));
    }

    [Fact]
    public async Task Generator_failure_propagates_with_reason()
    {
        var generator = new RecordingGenerator(succeed: false, failureReason: "kernel-resolve: missing tier");
        var strategy = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance, generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("kernel-resolve", result.FailureReason ?? "");
        Assert.Equal("baseline", result.StrategyId);
    }

    [Fact]
    public async Task Generator_exception_is_caught_and_reported()
    {
        var generator = new ThrowingGenerator(new InvalidOperationException("boom"));
        var strategy = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance, generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("generator-exception", result.FailureReason ?? "");
        Assert.Contains("InvalidOperationException", result.FailureReason ?? "");
    }

    [Fact]
    public async Task Cancellation_propagates_from_generator()
    {
        var generator = new CancellingGenerator();
        var strategy = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance, generator);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => strategy.ExecuteAsync(NewInvocation(), cts.Token));
    }

    // ── Test doubles ──

    private sealed class RecordingGenerator : IBaselineCodeGenerator
    {
        private readonly bool _succeed;
        private readonly int _files;
        private readonly long _tokens;
        private readonly string? _failureReason;
        public int Calls { get; private set; }

        public RecordingGenerator(bool succeed, int filesWritten = 0, long tokens = 0, string? failureReason = null)
        {
            _succeed = succeed;
            _files = filesWritten;
            _tokens = tokens;
            _failureReason = failureReason;
        }

        public Task<BaselineGenerationOutcome> GenerateAsync(
            string worktreePath, TaskContext task, CancellationToken ct,
            string strategyTag = "baseline-strategy",
            IProgress<VirtualDevTeam.Core.Frameworks.FrameworkActivityEvent>? activitySink = null,
            RevisionContext? revision = null)
        {
            Calls++;
            return Task.FromResult(new BaselineGenerationOutcome
            {
                Succeeded = _succeed,
                TokensUsed = _tokens,
                FailureReason = _failureReason,
            });
        }
    }

    private sealed class ThrowingGenerator : IBaselineCodeGenerator
    {
        private readonly Exception _ex;
        public ThrowingGenerator(Exception ex) => _ex = ex;
        public Task<BaselineGenerationOutcome> GenerateAsync(
            string worktreePath, TaskContext task, CancellationToken ct,
            string strategyTag = "baseline-strategy",
            IProgress<VirtualDevTeam.Core.Frameworks.FrameworkActivityEvent>? activitySink = null,
            RevisionContext? revision = null) => throw _ex;
    }

    private sealed class CancellingGenerator : IBaselineCodeGenerator
    {
        public Task<BaselineGenerationOutcome> GenerateAsync(
            string worktreePath, TaskContext task, CancellationToken ct,
            string strategyTag = "baseline-strategy",
            IProgress<VirtualDevTeam.Core.Frameworks.FrameworkActivityEvent>? activitySink = null,
            RevisionContext? revision = null)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new BaselineGenerationOutcome { Succeeded = true });
        }
    }
}
