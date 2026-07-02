namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Identifies which capture strategy produced a screenshot or artifact.
/// Used for UI badge rendering (🟣 MCP, 🔵 C# Playwright).
/// </summary>
public enum ScreenshotCaptureSource
{
    /// <summary>AI-driven exploration via Playwright MCP tools.</summary>
    Mcp,

    /// <summary>Fast deterministic capture using C# Playwright directly.</summary>
    DirectPlaywright,
}

/// <summary>
/// A structured screenshot artifact with provenance metadata.
/// Replaces bare path strings so the UI knows which capture strategy produced each artifact.
/// </summary>
public sealed record ScreenshotArtifact(
    string Identifier,
    string? Url,
    string Label,
    ScreenshotCaptureSource Source,
    bool IsPrimary);

/// <summary>
/// Per-source summary of a capture branch's results.
/// </summary>
public sealed record CaptureSourceSummary
{
    public required ScreenshotCaptureSource Source { get; init; }
    public int ArtifactCount { get; init; }
    public int PagesDiscovered { get; init; }

    /// <summary>URLs that were actually visited/tested by this capture branch.</summary>
    public IReadOnlyList<string> TestedUrls { get; init; } = Array.Empty<string>();

    /// <summary>Tool calls observed during MCP session (null for DirectPlaywright).</summary>
    public int? ToolCallsUsed { get; init; }

    /// <summary>Wall-clock duration of this capture branch in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Error message if this branch failed (null on success).</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Aggregated capture summary across all sources for a single candidate.
/// Stored on <see cref="CandidateSnapshot.CaptureMetrics"/>.
/// </summary>
public sealed record ScreenshotCaptureSummary
{
    /// <summary>Which source provided the primary (thumbnail) screenshot.</summary>
    public ScreenshotCaptureSource PrimarySource { get; init; }

    /// <summary>Per-source breakdowns.</summary>
    public IReadOnlyList<CaptureSourceSummary> Sources { get; init; } = Array.Empty<CaptureSourceSummary>();

    /// <summary>All structured artifacts from all sources.</summary>
    public IReadOnlyList<ScreenshotArtifact> Artifacts { get; init; } = Array.Empty<ScreenshotArtifact>();

    /// <summary>Total pages discovered across all sources (deduplicated by URL).</summary>
    public int TotalUniquePages { get; init; }

    /// <summary>Total artifacts across all sources.</summary>
    public int TotalArtifacts { get; init; }

    /// <summary>
    /// The base URL the app was started on (e.g., "http://localhost:5142").
    /// Null when the app didn't start or capture was skipped.
    /// </summary>
    public string? AppBaseUrl { get; init; }

    /// <summary>
    /// Number of pages expected from the issue's Visual Verification section.
    /// 0 when no Visual Verification URLs were specified.
    /// </summary>
    public int ExpectedPageCount { get; init; }
}

/// <summary>
/// CDP-derived page analysis data collected during C# Playwright capture.
/// Used instead of Chrome DevTools MCP — C# Playwright already has full CDP access.
/// </summary>
public sealed record PageAnalysis
{
    /// <summary>True if the app serves rendered HTML UI (not just JSON/API).</summary>
    public bool IsWebUi { get; init; }

    /// <summary>True if the app appears to be API-only (JSON responses, no meaningful HTML).</summary>
    public bool IsApiOnly { get; init; }

    /// <summary>Detected page type: SPA, SSR, Static, ApiOnly, Unknown.</summary>
    public string PageType { get; init; } = "Unknown";

    /// <summary>Console errors captured during page load.</summary>
    public IReadOnlyList<string> ConsoleErrors { get; init; } = Array.Empty<string>();

    /// <summary>Failed network requests during page load.</summary>
    public IReadOnlyList<string> FailedRequests { get; init; } = Array.Empty<string>();

    /// <summary>Total network requests during page load.</summary>
    public int NetworkRequestCount { get; init; }

    /// <summary>Total network response size in bytes.</summary>
    public long NetworkResponseBytes { get; init; }
}
