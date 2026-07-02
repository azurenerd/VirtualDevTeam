using VirtualDevTeam.Core.Frameworks;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Wraps <see cref="McpEnhancedStrategy"/> as an <see cref="IAgenticFrameworkAdapter"/>
/// so the orchestrator can treat all frameworks uniformly.
/// Built-in strategy — no lifecycle management needed.
/// </summary>
public sealed class McpEnhancedAdapter : IAgenticFrameworkAdapter
{
    private readonly McpEnhancedStrategy _inner;

    public McpEnhancedAdapter(McpEnhancedStrategy inner) => _inner = inner;

    public string Id => _inner.Id;
    public string DisplayName => "MCP-Enhanced";
    public string Description => "Copilot CLI + MCP workspace-reader tools for richer context";
    public TimeSpan DefaultTimeout => Timeout.InfiniteTimeSpan;

    public async Task<FrameworkExecutionResult> ExecuteAsync(
        FrameworkInvocation invocation, CancellationToken ct)
    {
        var strategyInvocation = BaselineAdapter.MapToStrategy(invocation);
        var result = await _inner.ExecuteAsync(strategyInvocation, ct);
        return BaselineAdapter.MapFromStrategy(result);
    }
}
