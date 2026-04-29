namespace AgentSquad.Core.Configuration;

/// <summary>
/// Provides the effective branch for the current run. All components that need to know
/// which branch to target (PRs, file commits, workspace checkouts) read from this provider
/// instead of hardcoding "main" or reading config.Project.DefaultBranch.
/// </summary>
public interface IRunBranchProvider
{
    /// <summary>
    /// The branch all agent work should target. Returns the working branch if one is set
    /// for the current run, otherwise returns the repository's default branch (e.g., "main").
    /// </summary>
    string EffectiveBranch { get; }

    /// <summary>
    /// Short run-scoped prefix for feature branch names (e.g., first 8 chars of RunId).
    /// Used to prevent multi-user branch collisions: <c>agent/{RunScope}/{agentSlug}/{taskSlug}</c>.
    /// Null when no run is active.
    /// </summary>
    string? RunScope { get; }
}

/// <summary>
/// Singleton implementation of <see cref="IRunBranchProvider"/>. RunCoordinator calls
/// <see cref="SetForRun"/> on every run start/recover and <see cref="Reset"/> on
/// complete/fail/cancel to prevent branch leakage across runs.
/// </summary>
public class RunBranchProvider : IRunBranchProvider
{
    private readonly string _defaultBranch;
    private volatile string? _runBranch;
    private volatile string? _runScope;

    public RunBranchProvider(string defaultBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        _defaultBranch = defaultBranch;
    }

    /// <inheritdoc />
    public string EffectiveBranch => _runBranch ?? _defaultBranch;

    /// <inheritdoc />
    public string? RunScope => _runScope;

    /// <summary>
    /// Set the target branch and run scope for the current run.
    /// Called by RunCoordinator on start and recovery.
    /// </summary>
    public void SetForRun(string? targetBranch, string? runScope)
    {
        _runBranch = string.IsNullOrWhiteSpace(targetBranch) ? null : targetBranch;
        _runScope = string.IsNullOrWhiteSpace(runScope) ? null : runScope;
    }

    /// <summary>
    /// Clear the run-specific branch override and scope, reverting to defaults.
    /// Called by RunCoordinator on complete, fail, or cancel.
    /// </summary>
    public void Reset()
    {
        _runBranch = null;
        _runScope = null;
    }
}
