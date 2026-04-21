namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Optional lifecycle management for external frameworks that require installation,
/// version checks, or dependency validation before execution.
/// Internal strategies (baseline, mcp-enhanced) do not need this.
/// </summary>
public interface IFrameworkLifecycle
{
    /// <summary>
    /// Check whether the framework and all its dependencies are available.
    /// Called before execution to provide early failure with actionable messages.
    /// </summary>
    Task<FrameworkReadinessResult> CheckReadinessAsync(CancellationToken ct);

    /// <summary>
    /// Attempt to install or update the framework. Only called when
    /// <see cref="CheckReadinessAsync"/> returns <see cref="FrameworkReadiness.InstallRequired"/>.
    /// </summary>
    Task<FrameworkInstallResult> EnsureInstalledAsync(CancellationToken ct);
}
