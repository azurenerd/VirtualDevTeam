namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Describes WHERE and HOW T-FINAL (or a test runner) should observe evidence that a
/// scenario's expected terminal state was reached.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Kind"/> field names the surface type. Known kinds:
/// <list type="bullet">
///   <item><term>dom_query</term><description>CSS selector that must match a DOM element.</description></item>
///   <item><term>dom_text</term><description>CSS selector whose text content must satisfy a condition.</description></item>
///   <item><term>event_bus</term><description>An event that must have been fired on the client event bus.</description></item>
///   <item><term>canvas_state</term><description>A pixel region or canvas API query.</description></item>
///   <item><term>http_response</term><description>Expected HTTP status + optional body shape.</description></item>
///   <item><term>db_row</term><description>SQL query that must return a row matching <c>expected</c>.</description></item>
///   <item><term>db_count</term><description>SQL COUNT query with an expected delta or absolute value.</description></item>
///   <item><term>queue_message</term><description>A message that must appear on a queue/topic.</description></item>
///   <item><term>log_line</term><description>A log line matching a regex pattern.</description></item>
///   <item><term>process_exit_code</term><description>Process exit code (CLI scenarios).</description></item>
///   <item><term>stdout_pattern</term><description>Regex matched against process stdout (CLI scenarios).</description></item>
///   <item><term>file_artifact</term><description>A file that must exist (and optionally match content).</description></item>
/// </list>
/// </para>
/// <para>
/// All surface-kind-specific parameters are stored in <see cref="Fields"/> as string key-value
/// pairs so the schema is open-ended (T-FINAL drivers read what they need).
/// </para>
/// </remarks>
public sealed record ObservationSurface
{
    /// <summary>
    /// Surface kind identifier (e.g., <c>dom_query</c>, <c>http_response</c>, <c>db_row</c>).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Surface-kind-specific parameters. The key set varies by <see cref="Kind"/>.
    /// Values are always strings; numeric or boolean fields are stored as their string representation.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}
