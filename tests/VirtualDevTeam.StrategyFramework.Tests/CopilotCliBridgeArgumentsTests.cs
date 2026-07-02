using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Covers the CLI MCP bridge (<c>p2-cli-mcp-bridge</c>):
/// argv construction via <see cref="CopilotCliProcessManager.BuildArguments"/> and
/// context-scoped ambient state through <see cref="AgentCallContext.PushInvocationContext"/>.
/// </summary>
public class CopilotCliBridgeArgumentsTests
{
    private static CopilotCliProcessManager NewManager(
        Action<CopilotCliConfig>? configure = null)
    {
        var cfg = new VirtualDevTeamConfig();
        cfg.CopilotCli.ModelName = "claude-opus-4.6";
        cfg.CopilotCli.SilentMode = true;
        configure?.Invoke(cfg.CopilotCli);
        return new CopilotCliProcessManager(
            Options.Create(cfg),
            NullLogger<CopilotCliProcessManager>.Instance);
    }

    [Fact]
    public void Default_context_emits_no_additional_mcp_config_or_allow_tool()
    {
        // Baseline: no invocation context → argv mirrors legacy behaviour.
        var mgr = NewManager();
        var args = mgr.BuildArguments();
        Assert.DoesNotContain("--additional-mcp-config", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--allow-tool=", StringComparison.Ordinal));
        // Core flags still present.
        Assert.Contains("--no-ask-user", args);
        Assert.Contains("--no-auto-update", args);
        Assert.Contains("--model", args);
        Assert.Contains("claude-opus-4.6", args);
    }

    [Fact]
    public void Model_flag_is_two_adjacent_argv_entries()
    {
        var mgr = NewManager();
        var args = mgr.BuildArguments(modelOverride: "gpt-5.2");
        var modelIdx = args.ToList().IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gpt-5.2", args[modelIdx + 1]);
    }

    [Fact]
    public void Invocation_context_emits_inline_json_and_allow_tools()
    {
        var mgr = NewManager();
        var json = "{\"mcpServers\":{\"workspace-reader\":{\"command\":\"dotnet\",\"args\":[\"X\"]}}}";
        using var scope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
            AdditionalMcpConfigJson: json,
            AllowedMcpTools: new[] { "workspace-reader" }));

        var args = mgr.BuildArguments();

        var cfgIdx = args.ToList().IndexOf("--additional-mcp-config");
        Assert.True(cfgIdx >= 0, "--additional-mcp-config flag missing");
        // The JSON must be passed as a SINGLE argv element, not split on braces/quotes.
        Assert.Equal(json, args[cfgIdx + 1]);
        Assert.Contains("--allow-tool=workspace-reader", args);
    }

    [Fact]
    public void Multiple_allowed_tools_each_get_their_own_flag()
    {
        var mgr = NewManager();
        using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
            AllowedMcpTools: new[] { "workspace-reader", "docs-indexer" }));

        var args = mgr.BuildArguments();

        Assert.Contains("--allow-tool=workspace-reader", args);
        Assert.Contains("--allow-tool=docs-indexer", args);
    }

    [Fact]
    public void Context_scope_restores_previous_value_on_dispose()
    {
        // Verifies the IDisposable scope contract: after dispose, the ambient context
        // reverts to whatever it was before Push — critical for preventing cross-call
        // state bleed when one agent uses MCP and a subsequent agent on the same
        // async flow must not inherit those flags.
        var mgr = NewManager();

        using (AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                   AllowedMcpTools: new[] { "server-a" })))
        {
            var inside = mgr.BuildArguments();
            Assert.Contains("--allow-tool=server-a", inside);
        }

        var after = mgr.BuildArguments();
        Assert.DoesNotContain(after, a => a.StartsWith("--allow-tool=", StringComparison.Ordinal));
        Assert.Null(AgentCallContext.CurrentInvocationContext);
    }

    [Fact]
    public void Nested_contexts_restore_outer_value_after_inner_dispose()
    {
        var mgr = NewManager();
        using (AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                   AllowedMcpTools: new[] { "outer" })))
        {
            using (AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                       AllowedMcpTools: new[] { "inner" })))
            {
                Assert.Contains("--allow-tool=inner", mgr.BuildArguments());
            }
            var outer = mgr.BuildArguments();
            Assert.Contains("--allow-tool=outer", outer);
            Assert.DoesNotContain("--allow-tool=inner", outer);
        }
    }

    [Fact]
    public void AllowToolUsage_derives_from_allowed_tools_presence()
    {
        Assert.False(new CopilotCliInvocationContext().AllowToolUsage);
        Assert.False(new CopilotCliInvocationContext(
            AllowedMcpTools: Array.Empty<string>()).AllowToolUsage);
        Assert.True(new CopilotCliInvocationContext(
            AllowedMcpTools: new[] { "x" }).AllowToolUsage);
    }

    [Fact]
    public void AllowFileEdits_enables_AllowToolUsage()
    {
        var ctx = new CopilotCliInvocationContext(AllowFileEdits: true);
        Assert.True(ctx.AllowFileEdits);
        Assert.True(ctx.AllowToolUsage);
    }

    [Fact]
    public void Legacy_AdditionalArgs_with_quotes_is_rejected_eagerly()
    {
        // Old behaviour silently appended a raw string. With ArgumentList we cannot
        // faithfully tokenise quoted values without a real shell — so we fail fast
        // rather than silently change semantics.
        var mgr = NewManager(c => c.AdditionalArgs = "--foo \"hello world\"");
        var ex = Assert.Throws<InvalidOperationException>(() => mgr.BuildArguments());
        Assert.Contains("AdditionalArgList", ex.Message);
    }

    [Fact]
    public void Legacy_AdditionalArgs_without_quotes_passes_through_as_tokens()
    {
        var mgr = NewManager(c => c.AdditionalArgs = "--foo bar --baz");
        var args = mgr.BuildArguments();
        Assert.Contains("--foo", args);
        Assert.Contains("bar", args);
        Assert.Contains("--baz", args);
    }

    [Fact]
    public void AdditionalArgList_entries_each_become_one_argv_element()
    {
        var mgr = NewManager(c => c.AdditionalArgList.AddRange(new[]
        {
            "--raw",
            "value with spaces and \"quotes\"",
        }));

        var args = mgr.BuildArguments();

        Assert.Contains("--raw", args);
        Assert.Contains("value with spaces and \"quotes\"", args);
    }

    [Fact]
    public void JsonOutput_emits_flag_and_value_as_two_entries()
    {
        var mgr = NewManager(c => c.JsonOutput = true);
        var args = mgr.BuildArguments();
        var i = args.ToList().IndexOf("--output-format");
        Assert.True(i >= 0);
        Assert.Equal("json", args[i + 1]);
    }

    [Fact]
    public void Excluded_tools_emit_flag_and_value_pairs()
    {
        var mgr = NewManager(c => c.ExcludedTools.AddRange(new[] { "shell", "write" }));
        var args = mgr.BuildArguments().ToList();
        // Verify pairing, not just presence: each --excluded-tools must be immediately
        // followed by the tool name — otherwise the CLI consumes the wrong token.
        var positions = Enumerable.Range(0, args.Count)
            .Where(i => args[i] == "--excluded-tools").ToList();
        Assert.Equal(2, positions.Count);
        Assert.Equal("shell", args[positions[0] + 1]);
        Assert.Equal("write", args[positions[1] + 1]);
    }

    [Fact]
    public async Task Invocation_context_is_isolated_across_parallel_flows()
    {
        // AsyncLocal isolation: two concurrent Task.Run flows each set their own
        // invocation context; neither should see the other's values.
        var mgr = NewManager();
        var t1 = Task.Run(() =>
        {
            using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AllowedMcpTools: new[] { "flow-1" }));
            Thread.Sleep(50);
            return mgr.BuildArguments().ToList();
        });
        var t2 = Task.Run(() =>
        {
            using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AllowedMcpTools: new[] { "flow-2" }));
            Thread.Sleep(50);
            return mgr.BuildArguments().ToList();
        });
        var (a1, a2) = (await t1, await t2);

        Assert.Contains("--allow-tool=flow-1", a1);
        Assert.DoesNotContain("--allow-tool=flow-2", a1);
        Assert.Contains("--allow-tool=flow-2", a2);
        Assert.DoesNotContain("--allow-tool=flow-1", a2);
    }
}
