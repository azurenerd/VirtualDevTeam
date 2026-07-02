namespace VirtualDevTeam.Core.Preview;

/// <summary>
/// User preferences for the preview build feature. Persisted to preview-settings.json.
/// </summary>
public class PreviewSettings
{
    /// <summary>Local directory path where the working branch is cloned.</summary>
    public string ClonePath { get; set; } = "";

    /// <summary>Override for the build command. When empty, auto-detected from project.</summary>
    public string BuildCommandOverride { get; set; } = "";

    /// <summary>Override for the run/start command. When empty, auto-detected from project.</summary>
    public string RunCommandOverride { get; set; } = "";

    /// <summary>Port to run the preview app on. 0 = auto-select a free port.</summary>
    public int Port { get; set; } = 5100;

    /// <summary>Whether the user has acknowledged the security warning about running AI code.</summary>
    public bool SecurityWarningAcknowledged { get; set; } = false;
}

/// <summary>
/// Current state of the preview build process.
/// </summary>
public enum PreviewState
{
    Idle,
    Cloning,
    Building,
    Running,
    Failed,
    Stopped
}

/// <summary>
/// Snapshot of preview status for the dashboard.
/// </summary>
public record PreviewStatus
{
    public PreviewState State { get; init; } = PreviewState.Idle;
    public string? ErrorMessage { get; init; }
    public string? AppUrl { get; init; }
    public int? ProcessId { get; init; }
    public string? BranchName { get; init; }
    public string? HeadCommitSha { get; init; }
    public string? HeadCommitMessage { get; init; }
    public DateTime? LastUpdatedUtc { get; init; }
    public int ActualPort { get; init; }
}
