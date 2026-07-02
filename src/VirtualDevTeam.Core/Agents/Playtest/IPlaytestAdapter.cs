namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// A pluggable strategy for executing one <see cref="PlaytestAction"/> against a running application.
/// Three concrete adapters cover the three major scenario surface types:
/// <list type="bullet">
///   <item><see cref="WebPlaytestAdapter"/> — Playwright-backed browser automation.</item>
///   <item><see cref="ApiPlaytestAdapter"/> — HttpClient + optional DB queries.</item>
///   <item><see cref="CliPlaytestAdapter"/> — <c>Process.Start</c> with stdout/stderr capture.</item>
/// </list>
/// </summary>
/// <remarks>
/// The <see cref="AppPlaytester"/> holds all registered adapters in a list and calls
/// <see cref="CanHandle"/> to select the first adapter capable of executing each action.
/// Adapters must be stateless across calls but may carry mutable state within a single
/// <see cref="ExecuteAsync"/> invocation (e.g. a browser page context between steps of the
/// same scenario). The caller is responsible for lifecycle management — adapters are instantiated
/// per playtest run via DI.
/// </remarks>
public interface IPlaytestAdapter
{
    /// <summary>
    /// Returns <see langword="true"/> when this adapter is capable of executing
    /// the given <paramref name="action"/>. Adapters decide based on the
    /// <see cref="PlaytestAction.ActionCategory"/> and/or the action plan's adapter field.
    /// </summary>
    bool CanHandle(PlaytestAction action);

    /// <summary>
    /// Execute the action and return evidence of the observed outcome.
    /// Should never throw — exceptions must be caught and returned as
    /// <see cref="ActionFailureEvidence"/>.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="handle">The running application's coordinates.</param>
    /// <param name="snapshots">
    /// Mutable dictionary used to store named DOM/state snapshots between steps
    /// (e.g. for <c>assert.selectorChanged</c> checks). The adapter reads and writes this dict.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IPlaytestEvidence> ExecuteAsync(
        PlaytestAction action,
        AppHandle handle,
        Dictionary<string, string?> snapshots,
        CancellationToken ct = default);
}
