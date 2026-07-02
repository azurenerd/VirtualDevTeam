using System.Collections.Immutable;

namespace VirtualDevTeam.Core.Strategies.MediaCapture;

/// <summary>
/// Identifies each discrete step in the evaluation + media-capture pipeline.
/// Steps are ordered by their typical execution sequence. The first step
/// (BuildGate) covers patch-apply + build verification — emitted by
/// CandidateEvaluator before the screenshot capture even runs, so the
/// dashboard strip renders as soon as a candidate hits Completed.
/// </summary>
public enum MediaCaptureStepId
{
    BuildGate,
    PlaywrightReady,
    AppDetection,
    DependencyRestore,
    AppStartup,
    McpExploration,
    DirectCapture,
    ScreenshotCapture,
    VideoRecording,
    GifGeneration,
    VideoTrimming,
    ArtifactStorage,
    Complete
}

public enum MediaCaptureStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

/// <summary>
/// Immutable snapshot of a single step's state at a point in time.
/// </summary>
public sealed record MediaCaptureStep(
    MediaCaptureStepId Id,
    MediaCaptureStepStatus Status,
    string? Detail = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    double? ElapsedMs = null);

/// <summary>
/// Full snapshot of media capture progress for a single candidate.
/// Stored on <see cref="CandidateStateStore"/> and rendered in the Strategies UI.
/// </summary>
public sealed record MediaCaptureProgressSnapshot
{
    public required ImmutableList<MediaCaptureStep> Steps { get; init; }
    public MediaCaptureStepId? CurrentStepId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double TotalElapsedMs { get; init; }
}

/// <summary>
/// Sink interface for reporting media capture progress. Implementations
/// may emit SignalR events, log, or no-op (null-object pattern).
/// </summary>
public interface IMediaCaptureProgressSink
{
    void StartStep(MediaCaptureStepId stepId, string? detail = null);
    void CompleteStep(MediaCaptureStepId stepId, string? detail = null);
    void FailStep(MediaCaptureStepId stepId, string? reason = null);
    void SkipStep(MediaCaptureStepId stepId, string? reason = null);
}

/// <summary>Null-object sink that discards all progress updates.</summary>
public sealed class NullMediaCaptureProgressSink : IMediaCaptureProgressSink
{
    public static readonly NullMediaCaptureProgressSink Instance = new();
    public void StartStep(MediaCaptureStepId stepId, string? detail = null) { }
    public void CompleteStep(MediaCaptureStepId stepId, string? detail = null) { }
    public void FailStep(MediaCaptureStepId stepId, string? reason = null) { }
    public void SkipStep(MediaCaptureStepId stepId, string? reason = null) { }
}
