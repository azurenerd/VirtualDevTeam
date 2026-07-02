using System.Reflection;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Verifies <see cref="CopilotCliChatCompletionService.FormatChatHistoryAsPrompt"/> flips
/// its tool-permission rule in lockstep with <see cref="AgentCallContext.CurrentInvocationContext"/>.
/// The invariant guarded here: prompt wording and CLI tool permission are ALWAYS consistent.
/// </summary>
public class CopilotCliPromptBridgeTests
{
    private static string Format(ChatHistory history)
    {
        // FormatChatHistoryAsPrompt is internal — reach it via reflection since the
        // test project has InternalsVisibleTo the Core project already.
        var m = typeof(CopilotCliChatCompletionService)
            .GetMethod("FormatChatHistoryAsPrompt",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object?[] { history, null })!;
    }

    private static ChatHistory Simple(string userMsg)
    {
        var h = new ChatHistory();
        h.AddUserMessage(userMsg);
        return h;
    }

    [Fact]
    public void Default_prompt_forbids_all_tool_use()
    {
        var prompt = Format(Simple("hello"));
        Assert.Contains("Do NOT use any tools or shell commands", prompt);
        Assert.DoesNotContain("MAY silently call", prompt);
    }

    [Fact]
    public void Prompt_permits_read_only_mcp_tools_when_context_grants_them()
    {
        using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
            AllowedMcpTools: new[] { "workspace-reader" }));
        var prompt = Format(Simple("hello"));

        Assert.Contains("MAY silently call", prompt);
        Assert.Contains("read-only MCP tools", prompt);
        // Writes and shell still forbidden even when tools are allowed.
        Assert.Contains("Do NOT create, edit, or write files", prompt);
        Assert.Contains("Do NOT run shell commands", prompt);
        // Anti-narration clause prevents the model from describing its inspections in
        // the final response — otherwise tool use pollutes document output.
        Assert.Contains("Do NOT narrate tool calls", prompt);
    }

    [Fact]
    public void Prompt_returns_to_strict_after_scope_disposes()
    {
        using (AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                   AllowedMcpTools: new[] { "workspace-reader" })))
        {
            var inside = Format(Simple("hello"));
            Assert.Contains("MAY silently call", inside);
        }
        var after = Format(Simple("hello"));
        Assert.Contains("Do NOT use any tools or shell commands", after);
        Assert.DoesNotContain("MAY silently call", after);
    }

    [Fact]
    public void Context_without_allowed_tools_keeps_strict_prompt()
    {
        // A context object is present but has no allow-list — AllowToolUsage is false,
        // so the prompt must stay strict. Covers the "don't accidentally relax on
        // empty array" edge case.
        using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
            AllowedMcpTools: Array.Empty<string>()));
        var prompt = Format(Simple("hello"));
        Assert.Contains("Do NOT use any tools or shell commands", prompt);
    }
}
