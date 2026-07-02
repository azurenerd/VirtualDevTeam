namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Configuration for headless mode — running VDT without the Blazor dashboard.
/// Events are streamed as JSONL to stdout for consumption by external tools.
/// </summary>
public class HeadlessConfig
{
    /// <summary>
    /// When true, skip Blazor/SignalR registration and stream events to stdout.
    /// Set via CLI: <c>vdt start --headless</c>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, auto-approve all human gates (PMSpec, Architecture, etc.).
    /// Set via CLI: <c>vdt start --auto-approve</c>
    /// </summary>
    public bool AutoApproveAllGates { get; set; }

    /// <summary>
    /// Output format for stdout events. Default: "jsonl" (one JSON object per line).
    /// Future: "text" (human-readable log lines).
    /// </summary>
    public string OutputFormat { get; set; } = "jsonl";
}
