using AgentSquad.Core.Frameworks;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Wraps <see cref="AgenticDelegationStrategy"/> as an <see cref="IAgenticFrameworkAdapter"/>
/// so the orchestrator can treat all frameworks uniformly.
/// Built-in strategy — no lifecycle management needed.
/// </summary>
public sealed class AgenticDelegationAdapter : IAgenticFrameworkAdapter
{
    private readonly AgenticDelegationStrategy _inner;

    public AgenticDelegationAdapter(AgenticDelegationStrategy inner) => _inner = inner;

    public string Id => _inner.Id;
    public string DisplayName => "GitHub Copilot CLI";
    public string Description => "Full autonomous GitHub Copilot CLI session with tool access (--allow-all)";
    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(600);

    public async Task<FrameworkExecutionResult> ExecuteAsync(
        FrameworkInvocation invocation, CancellationToken ct)
    {
        var strategyInvocation = BaselineAdapter.MapToStrategy(invocation);
        var result = await _inner.ExecuteAsync(strategyInvocation, ct);
        return BaselineAdapter.MapFromStrategy(result);
    }
}
