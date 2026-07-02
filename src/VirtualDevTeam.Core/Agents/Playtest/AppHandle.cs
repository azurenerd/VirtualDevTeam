using System.Diagnostics;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Carries the runtime coordinates needed for adapters to connect to the running
/// application under test. One <see cref="AppHandle"/> is created per playtest run
/// and shared across all scenario executions within that run.
/// </summary>
/// <remarks>
/// <para>
/// Not all fields are relevant for all <see cref="AppTargetType"/> values:
/// <list type="bullet">
///   <item><term><see cref="BaseUrl"/></term><description>Required for Web and Api targets.</description></item>
///   <item><term><see cref="ProcessHandle"/></term><description>Non-null when the playtester started the app itself; callers should dispose / kill on completion.</description></item>
///   <item><term><see cref="DbConnectionString"/></term><description>Optional. When present, enables db_row / db_count assertion surfaces.</description></item>
///   <item><term><see cref="CliBinaryPath"/></term><description>Required for Cli targets.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed record AppHandle
{
    /// <summary>Base URL (e.g. <c>http://localhost:5150</c>) for Web and Api targets.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Classification of the running application.</summary>
    public AppTargetType TargetType { get; init; } = AppTargetType.Web;

    /// <summary>
    /// The process started by the playtester, if any. Callers are responsible
    /// for terminating this process after the run completes.
    /// </summary>
    public Process? ProcessHandle { get; init; }

    /// <summary>
    /// Optional database connection string. When set, <see cref="ApiPlaytestAdapter"/>
    /// attempts to execute <c>db_row</c> and <c>db_count</c> surface checks directly.
    /// When null, those surfaces are marked <c>inconclusive</c>.
    /// </summary>
    public string? DbConnectionString { get; init; }

    /// <summary>
    /// Absolute path to the CLI binary for <see cref="AppTargetType.Cli"/> targets.
    /// May include arguments that should always be prepended (e.g. <c>dotnet run --project …</c>).
    /// </summary>
    public string? CliBinaryPath { get; init; }

    /// <summary>
    /// Local workspace path — used by adapters to resolve relative screenshot/artifact paths
    /// and to locate seed data files.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Directory where screenshot files are written during the run.
    /// Defaults to <c>{WorkspacePath}/test-results/playtest-screenshots</c> when null.
    /// </summary>
    public string? ScreenshotOutputDir { get; init; }

    /// <summary>
    /// Optional free-form adapter configuration JSON (from the <c>playtest_context_json</c>
    /// variable in the verify-scenario-user prompt). Adapters may parse this for extra options.
    /// </summary>
    public string? PlaytestContextJson { get; init; }
}
