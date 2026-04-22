using System.IO;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="AgenticDelegationStrategy"/> and <see cref="AgenticPromptBuilder"/>
/// (<c>p3-agentic-strategy</c>). These cover prompt shape and strategy plumbing
/// without requiring a real <c>copilot</c> binary — end-to-end happens in
/// <c>p3-test-orphan-cleanup</c> using the fake-CLI.
/// </summary>
public class AgenticDelegationStrategyTests : IDisposable
{
    private readonly string _worktree;

    public AgenticDelegationStrategyTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "as-ads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    [Fact]
    public void StrategyId_is_stable()
    {
        var strat = NewStrategy();
        Assert.Equal("copilot-cli", strat.Id);
    }

    [Fact]
    public void PromptBuilder_includes_task_fields_and_safety_constraints()
    {
        var invocation = NewInvocation();
        var prompt = new AgenticPromptBuilder().Build(invocation);

        Assert.Contains(invocation.Task.TaskTitle, prompt);
        Assert.Contains(invocation.Task.TaskDescription, prompt);
        Assert.Contains(invocation.WorktreePath, prompt);
        Assert.Contains(invocation.Task.PrBranch, prompt);

        // Safety constraints must be present.
        Assert.Contains("ONLY inside this worktree", prompt);
        Assert.Contains("git push", prompt);
        Assert.Contains("--allow-all", prompt);
    }

    [Fact]
    public void PromptBuilder_includes_optional_pm_spec_and_architecture_when_set()
    {
        var invocation = NewInvocation(t => t with
        {
            PmSpec = "PM-SPEC-MARKER",
            Architecture = "ARCH-MARKER",
            DesignContext = "DESIGN-MARKER",
        });
        var prompt = new AgenticPromptBuilder().Build(invocation);

        Assert.Contains("PM-SPEC-MARKER", prompt);
        Assert.Contains("ARCH-MARKER", prompt);
        Assert.Contains("DESIGN-MARKER", prompt);
    }

    [Fact]
    public void PromptBuilder_omits_unset_optional_sections()
    {
        var invocation = NewInvocation();
        var prompt = new AgenticPromptBuilder().Build(invocation);

        Assert.DoesNotContain("Product Spec", prompt);
        Assert.DoesNotContain("## Architecture (context)", prompt);
        Assert.DoesNotContain("UI / Design context", prompt);
    }

    // ── helpers ──

    private AgenticDelegationStrategy NewStrategy()
    {
        var cfg = Options.Create(new StrategyFrameworkConfig());
        var agentSquadCfg = Options.Create(new AgentSquadConfig());
        var mgr = new CopilotCliProcessManager(
            agentSquadCfg, cfg,
            NullLogger<CopilotCliProcessManager>.Instance);
        return new AgenticDelegationStrategy(
            NullLogger<AgenticDelegationStrategy>.Instance,
            mgr, cfg);
    }

    private StrategyInvocation NewInvocation(Func<TaskContext, TaskContext>? customize = null)
    {
        var task = new TaskContext
        {
            TaskId = "T-42",
            TaskTitle = "Add a README",
            TaskDescription = "Create a README.md with Hello World.",
            PrBranch = "agent/se1/readme",
            BaseSha = "deadbeef",
            RunId = "R-1",
            AgentRepoPath = _worktree,
        };
        if (customize is not null) task = customize(task);
        return new StrategyInvocation
        {
            Task = task,
            WorktreePath = _worktree,
            StrategyId = "copilot-cli",
            Timeout = TimeSpan.FromMinutes(5),
        };
    }
}
