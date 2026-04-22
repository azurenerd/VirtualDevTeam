namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Pre/post execution gating for external framework adapters.
/// V1 scope: log-based gates (pre-execution confirmation, post-execution review).
/// Mid-execution gating via SDK hooks is future work.
/// </summary>
public sealed class FrameworkExecutionGate
{
    /// <summary>
    /// Determines whether an external framework requires pre-execution confirmation.
    /// V1: always returns true for external (non-built-in) frameworks to ensure
    /// visibility when delegating to a framework that AgentSquad cannot inspect mid-flight.
    /// </summary>
    public static bool RequiresPreExecutionGate(string frameworkId)
    {
        return frameworkId switch
        {
            "baseline" or "mcp-enhanced" or "copilot-cli" or "agentic-delegation" => false,
            _ => true, // External frameworks get pre-execution gate by default
        };
    }

    /// <summary>
    /// Creates a pre-execution summary for logging/dashboard display before an
    /// external framework runs. This is the "are you sure?" checkpoint.
    /// </summary>
    public static FrameworkGateEvent CreatePreExecutionGate(
        string frameworkId, string taskId, string taskTitle, TimeSpan timeout)
    {
        return new FrameworkGateEvent
        {
            FrameworkId = frameworkId,
            TaskId = taskId,
            GateType = FrameworkGateType.PreExecution,
            Summary = $"About to delegate task '{taskTitle}' to {frameworkId} " +
                      $"(timeout: {timeout.TotalSeconds}s). External framework will run " +
                      "autonomously within the worktree sandbox.",
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Creates a post-execution summary for review. Includes output metrics and
    /// a recommendation on whether to accept the result.
    /// </summary>
    public static FrameworkGateEvent CreatePostExecutionGate(
        string frameworkId, string taskId, FrameworkExecutionResult result)
    {
        var status = result.Succeeded ? "completed successfully" : $"failed: {result.FailureReason}";
        var tokenInfo = result.TokensUsed.HasValue
            ? $", tokens: {result.TokensUsed.Value:N0}"
            : ", tokens: unknown";

        return new FrameworkGateEvent
        {
            FrameworkId = frameworkId,
            TaskId = taskId,
            GateType = FrameworkGateType.PostExecution,
            Summary = $"{frameworkId} {status} in {result.Elapsed.TotalSeconds:F1}s{tokenInfo}. " +
                      $"Files modified: {result.Metrics?.FilesModified ?? 0}. " +
                      "Patch will be evaluated by the standard pipeline.",
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>Gate checkpoint event for framework execution.</summary>
public sealed class FrameworkGateEvent
{
    public required string FrameworkId { get; init; }
    public required string TaskId { get; init; }
    public required FrameworkGateType GateType { get; init; }
    public required string Summary { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>Types of framework execution gates.</summary>
public enum FrameworkGateType
{
    /// <summary>Before the framework starts executing.</summary>
    PreExecution,

    /// <summary>After the framework finishes, before patch is evaluated.</summary>
    PostExecution,
}
