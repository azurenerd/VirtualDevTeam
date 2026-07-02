using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Mcp;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Unit tests for <see cref="McpEnhancedStrategy"/> (todo <c>p2-mcp-strategy</c>).
/// Focus areas:
/// <list type="bullet">
///   <item><description>CopilotCliInvocationContext is installed with expected JSON/tools/CWD for the duration of the generator call.</description></item>
///   <item><description>Scope is restored after the generator returns, throws, or is cancelled.</description></item>
///   <item><description>AsyncLocal flows through an <c>await</c> boundary inside the generator.</description></item>
///   <item><description>Locator failure → deterministic failure result (no silent fallback to a baseline-shaped run).</description></item>
///   <item><description>Missing generator → honest failure (no marker stub).</description></item>
///   <item><description>Parallel invocations on different async flows do not cross-contaminate.</description></item>
/// </list>
/// </summary>
public class McpEnhancedStrategyTests : IDisposable
{
    private readonly string _worktree;

    public McpEnhancedStrategyTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "mcp-strat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    private StrategyInvocation NewInvocation(string? worktreeOverride = null) => new()
    {
        Task = new TaskContext
        {
            TaskId = "t-mcp-1",
            TaskTitle = "Add feature with mcp",
            TaskDescription = "",
            PrBranch = "agent/se/feature",
            BaseSha = new string('a', 40),
            RunId = "run-mcp-1",
            AgentRepoPath = worktreeOverride ?? _worktree,
        },
        WorktreePath = worktreeOverride ?? _worktree,
        StrategyId = "mcp-enhanced",
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static IMcpServerLocator FakeLocator(string dllPath = "/fake/VirtualDevTeam.McpServer.dll") =>
        new StubLocator(new McpServerLaunchSpec("dotnet", new[] { dllPath }, dllPath));

    private static IMcpServerLocator ThrowingLocator(string message = "dll not found") =>
        new StubLocator(() => throw new InvalidOperationException(message));

    [Fact]
    public async Task Installs_invocation_context_with_json_tools_and_cwd_during_generator_call()
    {
        CopilotCliInvocationContext? observed = null;
        var generator = new ObservingGenerator(ctx =>
        {
            observed = ctx;
            return Task.FromResult(new BaselineGenerationOutcome { Succeeded = true, FilesWritten = 1 });
        });

        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(observed);
        Assert.True(observed!.AllowToolUsage);
        Assert.Equal(new[] { "workspace-reader" }, observed.AllowedMcpTools);
        Assert.Equal(_worktree, observed.OverrideWorkingDirectory);
        Assert.False(string.IsNullOrWhiteSpace(observed.AdditionalMcpConfigJson));
        // Config JSON must reference the server name + dll path + --root worktree.
        Assert.Contains("workspace-reader", observed.AdditionalMcpConfigJson);
        Assert.Contains("--root", observed.AdditionalMcpConfigJson);
        Assert.Contains(_worktree.Replace("\\", "\\\\"), observed.AdditionalMcpConfigJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scope_is_disposed_after_successful_generator_call()
    {
        var generator = new ObservingGenerator(_ =>
            Task.FromResult(new BaselineGenerationOutcome { Succeeded = true }));
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.Null(AgentCallContext.CurrentInvocationContext);
    }

    [Fact]
    public async Task Scope_is_disposed_after_generator_throws()
    {
        var generator = new ObservingGenerator(_ =>
            throw new InvalidOperationException("boom"));
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("generator-exception", result.FailureReason ?? "");
        Assert.Null(AgentCallContext.CurrentInvocationContext);
    }

    [Fact]
    public async Task Scope_survives_await_boundary_inside_generator()
    {
        // Proves AsyncLocal flows through awaited continuations inside the generator —
        // essential since the real generator awaits chat.GetChatMessageContentAsync.
        CopilotCliInvocationContext? beforeAwait = null;
        CopilotCliInvocationContext? afterAwait = null;

        var generator = new ObservingGenerator(async _ =>
        {
            beforeAwait = AgentCallContext.CurrentInvocationContext;
            await Task.Delay(10);
            afterAwait = AgentCallContext.CurrentInvocationContext;
            return new BaselineGenerationOutcome { Succeeded = true };
        });

        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.NotNull(beforeAwait);
        Assert.NotNull(afterAwait);
        Assert.Same(beforeAwait, afterAwait);
    }

    [Fact]
    public async Task Locator_failure_returns_mcp_server_not_found_and_skips_generator()
    {
        var generator = new ObservingGenerator(_ =>
            Task.FromResult(new BaselineGenerationOutcome { Succeeded = true }));
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance,
            ThrowingLocator("probe exhausted"),
            generator);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("mcp-server-not-found:", result.FailureReason);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task Missing_generator_returns_honest_failure_no_stub_marker()
    {
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator: null);

        var result = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no-generator", result.FailureReason ?? "");
        // CRITICAL: mcp-enhanced must NOT write a stub marker — that would risk shipping
        // a pretend-success candidate. Baseline writes .strategy-baseline.md; mcp-enhanced
        // writes nothing.
        Assert.Empty(Directory.GetFiles(_worktree));
    }

    [Fact]
    public async Task Cancellation_propagates_without_leaving_scope_installed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var generator = new ObservingGenerator(_ =>
        {
            cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new BaselineGenerationOutcome { Succeeded = true });
        });
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => strategy.ExecuteAsync(NewInvocation(), cts.Token));

        Assert.Null(AgentCallContext.CurrentInvocationContext);
    }

    [Fact]
    public async Task Parallel_invocations_on_different_flows_do_not_cross_contaminate()
    {
        // Two strategy runs on different async flows (Task.WhenAll) must see isolated
        // ambient contexts — the orchestrator launches baseline + mcp-enhanced this way.
        var wt1 = Path.Combine(Path.GetTempPath(), "mcp-p1-" + Guid.NewGuid().ToString("N"));
        var wt2 = Path.Combine(Path.GetTempPath(), "mcp-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wt1); Directory.CreateDirectory(wt2);

        try
        {
            CopilotCliInvocationContext? seen1 = null, seen2 = null;

            var gen1 = new ObservingGenerator(async _ =>
            {
                await Task.Delay(25);
                seen1 = AgentCallContext.CurrentInvocationContext;
                return new BaselineGenerationOutcome { Succeeded = true };
            });
            var gen2 = new ObservingGenerator(async _ =>
            {
                await Task.Delay(25);
                seen2 = AgentCallContext.CurrentInvocationContext;
                return new BaselineGenerationOutcome { Succeeded = true };
            });

            var s1 = new McpEnhancedStrategy(NullLogger<McpEnhancedStrategy>.Instance, FakeLocator("/a/a.dll"), gen1);
            var s2 = new McpEnhancedStrategy(NullLogger<McpEnhancedStrategy>.Instance, FakeLocator("/b/b.dll"), gen2);

            await Task.WhenAll(
                s1.ExecuteAsync(NewInvocation(wt1), CancellationToken.None),
                s2.ExecuteAsync(NewInvocation(wt2), CancellationToken.None));

            Assert.NotNull(seen1);
            Assert.NotNull(seen2);
            Assert.Equal(wt1, seen1!.OverrideWorkingDirectory);
            Assert.Equal(wt2, seen2!.OverrideWorkingDirectory);
            Assert.Contains("/a/a.dll", seen1.AdditionalMcpConfigJson);
            Assert.Contains("/b/b.dll", seen2.AdditionalMcpConfigJson);
        }
        finally
        {
            try { Directory.Delete(wt1, true); } catch { }
            try { Directory.Delete(wt2, true); } catch { }
        }
    }

    [Fact]
    public async Task Strategy_id_is_mcp_enhanced()
    {
        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator());
        Assert.Equal("mcp-enhanced", strategy.Id);
        // Smoke: ExecuteAsync with no generator returns the correct id in the result.
        var r = await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);
        Assert.Equal("mcp-enhanced", r.StrategyId);
    }

    [Fact]
    public async Task Generator_receives_mcp_enhanced_strategy_tag()
    {
        // Prevents regressing to "baseline-strategy" agent-id tagging that would make
        // mcp-enhanced CLI sessions indistinguishable from baseline in usage telemetry.
        string? capturedTag = null;
        var generator = new TagCapturingGenerator(tag => capturedTag = tag);

        var strategy = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance, FakeLocator(), generator);

        await strategy.ExecuteAsync(NewInvocation(), CancellationToken.None);

        Assert.Equal("mcp-enhanced-strategy", capturedTag);
    }

    // ── Test doubles ──

    private sealed class StubLocator : IMcpServerLocator
    {
        private readonly Func<McpServerLaunchSpec> _factory;
        public StubLocator(McpServerLaunchSpec fixedSpec) : this(() => fixedSpec) { }
        public StubLocator(Func<McpServerLaunchSpec> factory) => _factory = factory;
        public McpServerLaunchSpec Resolve() => _factory();
    }

    private sealed class ObservingGenerator : IBaselineCodeGenerator
    {
        private readonly Func<CopilotCliInvocationContext?, Task<BaselineGenerationOutcome>> _body;
        public int Calls { get; private set; }

        public ObservingGenerator(Func<CopilotCliInvocationContext?, Task<BaselineGenerationOutcome>> body)
        {
            _body = body;
        }

        public Task<BaselineGenerationOutcome> GenerateAsync(
            string worktreePath, TaskContext task, CancellationToken ct,
            string strategyTag = "baseline-strategy",
            IProgress<VirtualDevTeam.Core.Frameworks.FrameworkActivityEvent>? activitySink = null,
            RevisionContext? revision = null)
        {
            Calls++;
            return _body(AgentCallContext.CurrentInvocationContext);
        }
    }

    private sealed class TagCapturingGenerator : IBaselineCodeGenerator
    {
        private readonly Action<string> _onTag;
        public TagCapturingGenerator(Action<string> onTag) => _onTag = onTag;

        public Task<BaselineGenerationOutcome> GenerateAsync(
            string worktreePath, TaskContext task, CancellationToken ct,
            string strategyTag = "baseline-strategy",
            IProgress<VirtualDevTeam.Core.Frameworks.FrameworkActivityEvent>? activitySink = null,
            RevisionContext? revision = null)
        {
            _onTag(strategyTag);
            return Task.FromResult(new BaselineGenerationOutcome { Succeeded = true });
        }
    }
}
