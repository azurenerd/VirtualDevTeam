namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Classification of a CLI output line for display styling.
/// </summary>
public enum LogLineClassification
{
    /// <summary>AI assistant response content (magenta/purple).</summary>
    Assistant,
    /// <summary>Tool execution starting (cyan/teal).</summary>
    ToolStart,
    /// <summary>Tool execution completed (green).</summary>
    ToolComplete,
    /// <summary>System/session messages (blue).</summary>
    System,
    /// <summary>Lifecycle/meta information (gray italic).</summary>
    Lifecycle,
    /// <summary>Error output from stderr or error markers (red).</summary>
    Error,
    /// <summary>Raw/unclassified output (dimmed gray).</summary>
    Raw,
    /// <summary>Call boundary marker (visual divider between CLI calls).</summary>
    CallBoundary
}

/// <summary>
/// A single classified log entry from an agent's CLI session.
/// </summary>
public sealed record AgentCliLogEntry(
    long Sequence,
    DateTime TimestampUtc,
    string Text,
    LogLineClassification Classification,
    string? CallId,
    string? ToolName = null,
    bool? ToolSuccess = null,
    string? ToolOutput = null);

/// <summary>
/// Metadata for a CLI call boundary marker.
/// </summary>
public sealed record CallBoundaryInfo(
    string CallId,
    string? PromptPreview,
    string? Model,
    string? WorkingDirectory,
    string? SessionId);
