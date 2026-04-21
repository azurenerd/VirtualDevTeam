namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Core adapter interface for any agentic code-generation framework.
/// Each adapter wraps a specific tool (Copilot CLI baseline, Squad, Claude Code, etc.)
/// and exposes a uniform execution surface for the orchestrator.
/// </summary>
public interface IAgenticFrameworkAdapter
{
    /// <summary>Stable identifier used in config, logs, experiment records (e.g., "squad", "baseline").</summary>
    string Id { get; }

    /// <summary>Human-readable name for dashboard/UI display (e.g., "Squad", "Baseline").</summary>
    string DisplayName { get; }

    /// <summary>Short description shown in the configuration UI.</summary>
    string Description { get; }

    /// <summary>Default wall-clock timeout for this framework. Overridable via config.</summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>
    /// Execute code generation inside the given worktree. Must be safe to cancel at any point.
    /// The adapter must not mutate files outside <paramref name="invocation"/>.WorktreePath.
    /// </summary>
    Task<FrameworkExecutionResult> ExecuteAsync(
        FrameworkInvocation invocation, CancellationToken ct);
}
