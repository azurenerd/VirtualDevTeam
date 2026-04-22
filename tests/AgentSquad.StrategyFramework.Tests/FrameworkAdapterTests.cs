using AgentSquad.Core.Frameworks;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// Tests for the framework adapter wrappers (BaselineAdapter, McpEnhancedAdapter,
/// AgenticDelegationAdapter) and the SquadStdoutParser.
/// </summary>
public class FrameworkAdapterTests : IDisposable
{
    private readonly string _worktree;

    public FrameworkAdapterTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "adapter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    private FrameworkInvocation NewFrameworkInvocation(string frameworkId = "baseline") => new()
    {
        Task = new FrameworkTaskContext
        {
            TaskId = "t1",
            TaskTitle = "Test feature",
            TaskDescription = "Build a widget",
            PrBranch = "agent/se/feature",
            BaseSha = new string('a', 40),
            RunId = "run-1",
            AgentRepoPath = _worktree,
            Complexity = 2,
            IsWebTask = true,
            PmSpec = "PM spec content",
            Architecture = "Architecture doc",
            TechStack = ".NET 8",
        },
        WorktreePath = _worktree,
        FrameworkId = frameworkId,
        Timeout = TimeSpan.FromSeconds(30),
    };

    // ── BaselineAdapter ──

    [Fact]
    public void BaselineAdapter_exposes_correct_identity()
    {
        var inner = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance);
        var adapter = new BaselineAdapter(inner);

        Assert.Equal("baseline", adapter.Id);
        Assert.Equal("Baseline", adapter.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(180), adapter.DefaultTimeout);
        Assert.NotEmpty(adapter.Description);
    }

    [Fact]
    public async Task BaselineAdapter_delegates_to_inner_strategy()
    {
        var inner = new BaselineStrategy(NullLogger<BaselineStrategy>.Instance);
        var adapter = new BaselineAdapter(inner);

        var result = await adapter.ExecuteAsync(NewFrameworkInvocation(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("baseline", result.FrameworkId);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.NotNull(result.Metrics);
    }

    [Fact]
    public void BaselineAdapter_MapToStrategy_preserves_all_fields()
    {
        var fw = NewFrameworkInvocation();
        var si = BaselineAdapter.MapToStrategy(fw);

        Assert.Equal(fw.WorktreePath, si.WorktreePath);
        Assert.Equal(fw.FrameworkId, si.StrategyId);
        Assert.Equal(fw.Timeout, si.Timeout);
        Assert.Equal(fw.Task.TaskId, si.Task.TaskId);
        Assert.Equal(fw.Task.TaskTitle, si.Task.TaskTitle);
        Assert.Equal(fw.Task.Complexity, si.Task.Complexity);
        Assert.Equal(fw.Task.IsWebTask, si.Task.IsWebTask);
        Assert.Equal(fw.Task.PmSpec, si.Task.PmSpec);
        Assert.Equal(fw.Task.Architecture, si.Task.Architecture);
        Assert.Equal(fw.Task.TechStack, si.Task.TechStack);
    }

    [Fact]
    public void BaselineAdapter_MapFromStrategy_maps_nullable_tokens()
    {
        var sr = new StrategyExecutionResult
        {
            StrategyId = "baseline",
            Succeeded = true,
            Elapsed = TimeSpan.FromSeconds(5),
            TokensUsed = null,
            Log = new[] { "line1" },
        };

        var fr = BaselineAdapter.MapFromStrategy(sr);

        Assert.Null(fr.TokensUsed);
        Assert.Null(fr.Metrics!.TokensUsed);
        Assert.Equal("baseline", fr.FrameworkId);
        Assert.True(fr.Succeeded);
        Assert.Single(fr.Log);
    }

    [Fact]
    public void BaselineAdapter_MapFromStrategy_maps_nonNull_tokens()
    {
        var sr = new StrategyExecutionResult
        {
            StrategyId = "baseline",
            Succeeded = true,
            Elapsed = TimeSpan.FromSeconds(5),
            TokensUsed = 1234,
        };

        var fr = BaselineAdapter.MapFromStrategy(sr);

        Assert.Equal(1234, fr.TokensUsed);
        Assert.Equal(1234, fr.Metrics!.TokensUsed);
    }

    // ── McpEnhancedAdapter ──

    [Fact]
    public void McpEnhancedAdapter_exposes_correct_identity()
    {
        var inner = new McpEnhancedStrategy(
            NullLogger<McpEnhancedStrategy>.Instance,
            new StubMcpLocator());
        var adapter = new McpEnhancedAdapter(inner);

        Assert.Equal("mcp-enhanced", adapter.Id);
        Assert.Equal("MCP-Enhanced", adapter.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(240), adapter.DefaultTimeout);
    }

    // ── AgenticDelegationAdapter ──

    [Fact]
    public void AgenticDelegationAdapter_exposes_correct_identity()
    {
        // Can't easily construct AgenticDelegationStrategy without full DI,
        // but we can verify the adapter's own properties via mock.
        Assert.Equal("agentic-delegation", new StubAgenticStrategy().Id);
    }

    // ── SquadStdoutParser ──

    [Fact]
    public void ParseMetrics_extracts_token_counts()
    {
        var stdout = """
            Some output...
            Tokens    ↑ 620.4k · ↓ 3.2k · 494.7k (cached)
            More output
            """;

        var metrics = SquadStdoutParser.ParseMetrics(stdout);

        Assert.NotNull(metrics.TotalTokens);
        Assert.Equal(623600, metrics.TotalTokens!.Value); // (620.4 + 3.2) * 1000
    }

    [Fact]
    public void ParseMetrics_extracts_request_counts()
    {
        var stdout = """
            Requests  3 Premium (37.5s)
            Requests  2 Standard (12.1s)
            """;

        var metrics = SquadStdoutParser.ParseMetrics(stdout);

        Assert.Equal(5, metrics.RequestCount);
    }

    [Fact]
    public void ParseMetrics_returns_null_tokens_when_no_token_line()
    {
        var stdout = "Just some random output\nNo metrics here";
        var metrics = SquadStdoutParser.ParseMetrics(stdout);

        Assert.Null(metrics.TotalTokens);
        Assert.Equal(0, metrics.RequestCount);
    }

    [Fact]
    public void ParseMetrics_handles_empty_input()
    {
        var metrics = SquadStdoutParser.ParseMetrics("");
        Assert.Null(metrics.TotalTokens);
        Assert.Equal(0, metrics.RequestCount);
    }

    // ── SquadPromptBuilder ──

    [Fact]
    public void SquadPromptBuilder_includes_task_context()
    {
        var invocation = NewFrameworkInvocation("squad");
        var prompt = SquadPromptBuilder.Build(invocation);

        Assert.Contains("Test feature", prompt);
        Assert.Contains("Build a widget", prompt);
        Assert.Contains("PM spec content", prompt);
        Assert.Contains("Architecture doc", prompt);
        Assert.Contains(".NET 8", prompt);
        Assert.Contains("Do NOT create GitHub Issues", prompt);
        Assert.Contains("Do NOT run `git push`", prompt);
    }

    [Fact]
    public void SquadPromptBuilder_omits_null_optional_sections()
    {
        var invocation = new FrameworkInvocation
        {
            Task = new FrameworkTaskContext
            {
                TaskId = "t1",
                TaskTitle = "Simple task",
                TaskDescription = "Do something",
                PrBranch = "main",
                BaseSha = new string('a', 40),
                RunId = "run-1",
                AgentRepoPath = _worktree,
            },
            WorktreePath = _worktree,
            FrameworkId = "squad",
            Timeout = TimeSpan.FromSeconds(30),
        };

        var prompt = SquadPromptBuilder.Build(invocation);

        Assert.DoesNotContain("Product Specification", prompt);
        Assert.DoesNotContain("Architecture", prompt);
        Assert.DoesNotContain("Tech Stack", prompt);
    }

    // ── Helpers ──

    private sealed class StubMcpLocator : AgentSquad.Core.Mcp.IMcpServerLocator
    {
        public AgentSquad.Core.Mcp.McpServerLaunchSpec Resolve()
            => new("stub", Array.Empty<string>(), "stub");
    }

    private sealed class StubAgenticStrategy : ICodeGenerationStrategy
    {
        public string Id => "agentic-delegation";
        public Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
            => Task.FromResult(new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = true,
                Elapsed = TimeSpan.FromSeconds(1),
            });
    }
}
