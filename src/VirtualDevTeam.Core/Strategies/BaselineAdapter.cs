using VirtualDevTeam.Core.Frameworks;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Wraps <see cref="BaselineStrategy"/> as an <see cref="IAgenticFrameworkAdapter"/>
/// so the orchestrator can treat all frameworks uniformly.
/// Built-in strategy — no lifecycle management needed.
/// </summary>
public sealed class BaselineAdapter : IAgenticFrameworkAdapter
{
    private readonly BaselineStrategy _inner;

    public BaselineAdapter(BaselineStrategy inner) => _inner = inner;

    public string Id => _inner.Id;
    public string DisplayName => "Baseline";
    public string Description => "Single-pass Copilot CLI code generation (fastest, lowest cost)";
    public TimeSpan DefaultTimeout => Timeout.InfiniteTimeSpan;

    public async Task<FrameworkExecutionResult> ExecuteAsync(
        FrameworkInvocation invocation, CancellationToken ct)
    {
        var strategyInvocation = MapToStrategy(invocation);
        var result = await _inner.ExecuteAsync(strategyInvocation, ct);
        return MapFromStrategy(result);
    }

    internal static StrategyInvocation MapToStrategy(FrameworkInvocation fw) => new()
    {
        Task = MapTaskContext(fw.Task),
        WorktreePath = fw.WorktreePath,
        StrategyId = fw.FrameworkId,
        Timeout = fw.Timeout,
        ActivitySink = fw.ActivitySink,
        Revision = fw.Revision,
    };

    internal static FrameworkExecutionResult MapFromStrategy(StrategyExecutionResult sr) => new()
    {
        FrameworkId = sr.StrategyId,
        Succeeded = sr.Succeeded,
        FailureReason = sr.FailureReason,
        Elapsed = sr.Elapsed,
        TokensUsed = sr.TokensUsed,
        Log = sr.Log,
        Metrics = new FrameworkMetrics
        {
            TokensUsed = sr.TokensUsed,
            ElapsedTime = sr.Elapsed
        }
    };

    internal static TaskContext MapTaskContext(FrameworkTaskContext ftc) => new()
    {
        TaskId = ftc.TaskId,
        TaskTitle = ftc.TaskTitle,
        TaskDescription = ftc.TaskDescription,
        PrBranch = ftc.PrBranch,
        BaseSha = ftc.BaseSha,
        RunId = ftc.RunId,
        AgentRepoPath = ftc.AgentRepoPath,
        Complexity = ftc.Complexity,
        IsWebTask = ftc.IsWebTask,
        PmSpec = ftc.PmSpec,
        Architecture = ftc.Architecture,
        TechStack = ftc.TechStack,
        IssueContext = ftc.IssueContext,
        DesignContext = ftc.DesignContext
    };
}
