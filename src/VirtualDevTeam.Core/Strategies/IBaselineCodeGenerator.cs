using VirtualDevTeam.Core.Frameworks;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Pluggable single-shot code generator the <see cref="BaselineStrategy"/> delegates to
/// when wired. Lives in Core (so the strategy can depend on the contract) but is
/// implemented in <c>VirtualDevTeam.Agents</c> (so the implementation can use the SE's
/// model registry, prompt service, and code-file parser).
///
/// The generator's only job is to write parsed FILE: blocks into <paramref name="worktreePath"/>;
/// the orchestrator extracts the patch via <c>git diff</c>. The generator MUST NOT
/// mutate paths outside the worktree — implementations are responsible for path
/// containment validation before any write.
/// </summary>
public interface IBaselineCodeGenerator
{
    /// <summary>
    /// Generate code for the supplied <paramref name="task"/> into <paramref name="worktreePath"/>.
    /// Must be safe to cancel at any point.
    /// </summary>
    /// <param name="strategyTag">
    /// Strategy identifier used for telemetry / kernel agentId ("baseline-strategy",
    /// "mcp-enhanced-strategy", etc). Defaults to "baseline-strategy" for back-compat.
    /// </param>
    Task<BaselineGenerationOutcome> GenerateAsync(
        string worktreePath, TaskContext task, CancellationToken ct,
        string strategyTag = "baseline-strategy",
        IProgress<Frameworks.FrameworkActivityEvent>? activitySink = null,
        RevisionContext? revision = null);
}

/// <summary>What the baseline generator returns after a single run.</summary>
public record BaselineGenerationOutcome
{
    public required bool Succeeded { get; init; }
    /// <summary>Number of files actually written into the worktree (after path containment).</summary>
    public int FilesWritten { get; init; }
    /// <summary>Tokens consumed (input + output) when known. 0 if the impl doesn't track them.</summary>
    public long TokensUsed { get; init; }
    /// <summary>Diagnostic when <see cref="Succeeded"/> is false.</summary>
    public string? FailureReason { get; init; }
}
