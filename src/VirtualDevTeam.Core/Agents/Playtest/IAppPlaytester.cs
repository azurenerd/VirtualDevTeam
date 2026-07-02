using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Orchestrator-facing service that runs all approved scenarios in the current
/// <see cref="IScenarioRegistry"/> against a live application and returns a per-scenario
/// verdict array.
/// </summary>
/// <remarks>
/// <para>
/// The service applies a three-layer verification stack per scenario:
/// <list type="number">
///   <item><term>Layer 1 — Deterministic</term><description>Playwright/HTTP/CLI assertions against the live app.</description></item>
///   <item><term>Layer 2 — LLM Vision</term><description>Screenshot sequence analysis (marked <c>inconclusive</c> when vision is unavailable).</description></item>
///   <item><term>Layer 3 — Narrative Judge</term><description>Copilot CLI evaluates the full evidence trace for story coherence.</description></item>
/// </list>
/// The final per-scenario verdict is the <em>most conservative</em> of all three layers:
/// <c>Verified &gt; Inconclusive &gt; Broken</c> (conservative direction = Broken wins).
/// </para>
/// </remarks>
public interface IAppPlaytester
{
    /// <summary>
    /// Execute all approved scenarios from <see cref="IScenarioRegistry.Current"/> against
    /// the live application described by <paramref name="handle"/> and return one report
    /// per scenario.
    /// </summary>
    /// <param name="handle">Coordinates and credentials for connecting to the running app.</param>
    /// <param name="scenarios">
    /// Optional explicit scenario list. When <see langword="null"/>, the playtester uses
    /// <see cref="IScenarioRegistry.Current"/> filtered to <c>status == approved</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// One <see cref="PlaytestReport"/> per attempted scenario, in scenario-ID order.
    /// Never returns null; returns an empty array when the scenario registry is empty.
    /// </returns>
    Task<PlaytestReport[]> RunAsync(
        AppHandle handle,
        IReadOnlyList<Scenario>? scenarios = null,
        CancellationToken ct = default);
}
