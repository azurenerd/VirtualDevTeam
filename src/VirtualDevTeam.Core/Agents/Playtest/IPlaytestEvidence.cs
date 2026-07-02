namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Evidence produced by a single <see cref="IPlaytestAdapter.ExecuteAsync"/> call.
/// Carries the observed state (or error) and whether the action's assertion passed.
/// </summary>
public interface IPlaytestEvidence
{
    /// <summary>The <c>observation_surfaces</c> kind this evidence applies to (e.g. <c>dom_query</c>).</summary>
    string ObservationKind { get; }

    /// <summary>
    /// <see langword="true"/> when the action completed successfully and any assertion passed.
    /// <see langword="false"/> for assertion failures.
    /// </summary>
    bool Passed { get; }

    /// <summary>
    /// When <see langword="false"/>, whether the check was skipped or could not be evaluated
    /// rather than having produced a definitive failure.
    /// </summary>
    bool IsInconclusive { get; }

    /// <summary>Human-readable error or inconclusive reason; null on success.</summary>
    string? ErrorMessage { get; }
}

// ──────────────────────────────────────────────────────────────────────────────
// DOM / UI evidence
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Evidence from a CSS selector existence check (<c>dom_query</c> surface).</summary>
public sealed record DomQueryEvidence(bool Found, string Selector) : IPlaytestEvidence
{
    public string ObservationKind => "dom_query";
    public bool Passed => Found;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Found ? null : $"Selector not found: {Selector}";
}

/// <summary>Evidence from reading DOM text content (<c>dom_text</c> surface).</summary>
public sealed record DomTextEvidence(
    string Selector,
    string? ActualContent,
    string? ExpectedPattern,
    bool Matched) : IPlaytestEvidence
{
    public string ObservationKind => "dom_text";
    public bool Passed => Matched;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Matched ? null
        : $"Selector '{Selector}' text '{ActualContent}' did not match expected pattern '{ExpectedPattern}'";
}

/// <summary>
/// Evidence that a named DOM snapshot was captured (for <c>assert.selectorChanged</c>).
/// <c>Passed</c> is true when the content changed from the baseline snapshot.
/// </summary>
public sealed record DomSnapshotChangedEvidence(
    string Selector,
    string SnapshotKey,
    string? BaselineValue,
    string? CurrentValue,
    bool Changed) : IPlaytestEvidence
{
    public string ObservationKind => "dom_text";
    public bool Passed => Changed;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Changed ? null
        : $"Selector '{Selector}' value did not change from snapshot '{SnapshotKey}' (was '{BaselineValue}', still '{CurrentValue}')";
}

/// <summary>Evidence that a JavaScript EventBus event was (or was not) fired (<c>event_bus</c> surface).</summary>
public sealed record EventBusEvidence(string EventName, bool EventFired) : IPlaytestEvidence
{
    public string ObservationKind => "event_bus";
    public bool Passed => EventFired;
    public bool IsInconclusive => false;
    public string? ErrorMessage => EventFired ? null : $"EventBus event '{EventName}' was not fired";
}

/// <summary>A screenshot captured during a UI interaction step.</summary>
public sealed record ScreenshotEvidence(
    string Filename,
    byte[]? Bytes,
    string? FilePath) : IPlaytestEvidence
{
    public string ObservationKind => "screenshot";
    public bool Passed => Bytes is { Length: > 0 } || FilePath is not null;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Passed ? null : $"Screenshot '{Filename}' was not captured";
}

// ──────────────────────────────────────────────────────────────────────────────
// HTTP / API evidence
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Evidence from an HTTP response (<c>http_response</c> surface).</summary>
public sealed record HttpResponseEvidence(
    string Method,
    string Path,
    int StatusCode,
    long LatencyMs,
    string? Body,
    int ExpectedStatus,
    long? MaxLatencyMs) : IPlaytestEvidence
{
    public string ObservationKind => "http_response";
    public bool Passed => StatusCode == ExpectedStatus
                         && (MaxLatencyMs is null || LatencyMs <= MaxLatencyMs.Value);
    public bool IsInconclusive => false;
    public string? ErrorMessage
    {
        get
        {
            if (StatusCode != ExpectedStatus)
                return $"HTTP {Method} {Path} returned {StatusCode}, expected {ExpectedStatus}";
            if (MaxLatencyMs is not null && LatencyMs > MaxLatencyMs.Value)
                return $"HTTP {Method} {Path} took {LatencyMs}ms, max allowed {MaxLatencyMs}ms";
            return null;
        }
    }
}

/// <summary>
/// Evidence from a JSON body path check (<c>http_response</c> sub-assertion).
/// </summary>
public sealed record HttpBodyPathEvidence(
    string JsonPath,
    string? ActualValue,
    string ExpectedValue) : IPlaytestEvidence
{
    public string ObservationKind => "http_response";
    public bool Passed => string.Equals(ActualValue, ExpectedValue, StringComparison.OrdinalIgnoreCase);
    public bool IsInconclusive => false;
    public string? ErrorMessage => Passed ? null
        : $"JSON path '{JsonPath}' was '{ActualValue}', expected '{ExpectedValue}'";
}

// ──────────────────────────────────────────────────────────────────────────────
// DB evidence
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Evidence from a database row assertion (<c>db_row</c> surface).
/// Always <c>inconclusive</c> when no connection string is available.
/// </summary>
public sealed record DbRowEvidence(
    string Sql,
    string? ActualRowJson,
    string? ExpectedJson,
    bool Matched,
    bool IsInconclusive,
    string? ErrorMessage) : IPlaytestEvidence
{
    public string ObservationKind => "db_row";
    public bool Passed => Matched && !IsInconclusive;
}

/// <summary>
/// Evidence from a database count assertion (<c>db_count</c> surface).
/// Always <c>inconclusive</c> when no connection string is available.
/// </summary>
public sealed record DbCountEvidence(
    string Sql,
    long? ActualCount,
    string? ExpectedChange,
    bool Matched,
    bool IsInconclusive,
    string? ErrorMessage) : IPlaytestEvidence
{
    public string ObservationKind => "db_count";
    public bool Passed => Matched && !IsInconclusive;
}

// ──────────────────────────────────────────────────────────────────────────────
// CLI evidence
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Evidence captured by running a CLI command (<c>cli_invocation</c> scenarios).</summary>
public sealed record CliRunEvidence(
    string Binary,
    IReadOnlyList<string> Args,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration) : IPlaytestEvidence
{
    public string ObservationKind => "cli_run";
    public bool Passed => true; // Execution itself succeeded; assertions are separate
    public bool IsInconclusive => false;
    public string? ErrorMessage => null;
}

/// <summary>Evidence from checking the process exit code (<c>process_exit_code</c> surface).</summary>
public sealed record ProcessExitCodeEvidence(int ExitCode, int Expected) : IPlaytestEvidence
{
    public string ObservationKind => "process_exit_code";
    public bool Passed => ExitCode == Expected;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Passed ? null : $"Exit code was {ExitCode}, expected {Expected}";
}

/// <summary>Evidence from matching stdout against a regex pattern (<c>stdout_pattern</c> surface).</summary>
public sealed record StdoutPatternEvidence(
    string Pattern,
    string ActualStdout,
    bool Matched) : IPlaytestEvidence
{
    public string ObservationKind => "stdout_pattern";
    public bool Passed => Matched;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Matched ? null
        : $"Stdout did not match pattern '{Pattern}'. Stdout snippet: '{(ActualStdout.Length > 200 ? ActualStdout[..200] : ActualStdout)}'";
}

/// <summary>Evidence from matching stderr against a regex pattern.</summary>
public sealed record StderrPatternEvidence(
    string Pattern,
    string ActualStderr,
    bool Matched) : IPlaytestEvidence
{
    public string ObservationKind => "stderr_pattern";
    public bool Passed => Matched;
    public bool IsInconclusive => false;
    public string? ErrorMessage => Matched ? null
        : $"Stderr did not match pattern '{Pattern}'. Stderr snippet: '{(ActualStderr.Length > 200 ? ActualStderr[..200] : ActualStderr)}'";
}

// ──────────────────────────────────────────────────────────────────────────────
// Generic / fallback evidence types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Inconclusive evidence — produced when the adapter cannot evaluate a surface check
/// (e.g. <c>db_row</c> without a connection string, <c>canvas_state</c> not yet supported).
/// </summary>
public sealed record InconclusiveEvidence(string ObservationKind, string Reason) : IPlaytestEvidence
{
    public bool Passed => false;
    public bool IsInconclusive => true;
    public string? ErrorMessage => Reason;
}

/// <summary>
/// Successful non-asserting action evidence (navigation, fill, click, wait).
/// Used to populate the evidence trace for narrative coherence assessment.
/// </summary>
public sealed record ActionSuccessEvidence(string ObservationKind, string? Detail = null) : IPlaytestEvidence
{
    public bool Passed => true;
    public bool IsInconclusive => false;
    public string? ErrorMessage => null;
}

/// <summary>
/// An action that failed with an exception or adapter-level error.
/// </summary>
public sealed record ActionFailureEvidence(string ObservationKind, string? ErrorMessage) : IPlaytestEvidence
{
    public bool Passed => false;
    public bool IsInconclusive => false;
}
