namespace VirtualDevTeam.Core.Strategies.Contracts;

using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// Frozen SignalR event contract for candidate lifecycle. Dashboard (Phase 4) builds
/// against these payload shapes; orchestrator emits them. Event names are stable.
/// </summary>
public static class StrategyEvents
{
    public const string CandidateStarted     = "candidate:started";
    public const string CandidateCompleted   = "candidate:completed";
    public const string CandidateEvaluated   = "candidate:evaluated";
    public const string CandidateScored      = "candidate:scored";
    public const string WinnerSelected       = "winner:selected";
    public const string GateStarted          = "gate:started";
    public const string GateCompleted        = "gate:completed";
    public const string CandidateDetail      = "candidate:detail";
    public const string CandidateActivity    = "candidate:activity";
    public const string CandidateInitialScored   = "candidate:initial-scored";
    public const string CandidateRevisionStarted = "candidate:revision-started";
    public const string CandidateRevisionCompleted = "candidate:revision-completed";
    public const string EvaluationProgress   = "evaluation:progress";
    public const string CandidateRetryStarted    = "candidate:retry-started";
    public const string CandidateRetryCompleted  = "candidate:retry-completed";
    public const string OrchestrationCancelled   = "orchestration:cancelled";
    public const string CandidateVideoReady      = "candidate:video-ready";
    public const string MediaCaptureProgress     = "candidate:media-progress";
    public const string TaskPrLinked             = "task:pr-linked";
    public const string CandidateAnalyzerUpdate  = "candidate:analyzer-update";
}

/// <summary>
/// Emitted when the engineering PR backing a strategy task is created (or already
/// known). Lets the Frameworks dashboard surface a clickable link from each strategy
/// task back to the resulting PR. Emitted before strategies start running so the
/// link is visible throughout the run, and re-emitted when the PR number changes.
/// </summary>
public record TaskPrLinkedEvent(
    string RunId,
    string TaskId,
    int PrNumber,
    string? PrUrl,
    string? PrTitle);

public record CandidateStartedEvent(string RunId, string TaskId, string StrategyId, DateTimeOffset At, string? Wave = null, string? TaskTitle = null);
public record CandidateCompletedEvent(string RunId, string TaskId, string StrategyId, bool Succeeded, string? FailureReason, double ElapsedSec, long? TokensUsed);
public record GateEvent(string RunId, string TaskId, string StrategyId, string GateId, bool? Passed, string? Detail);

/// <summary>
/// Emitted after build-gate evaluation for every candidate (whether it survived or not).
/// Carries the screenshot and gate result. Distinct from <see cref="CandidateScoredEvent"/>
/// which requires real LLM judge scores.
/// </summary>
public record CandidateEvaluatedEvent(
    string RunId, string TaskId, string StrategyId,
    bool Survived, string? FailedGate, string? FailureDetail,
    string? ScreenshotBase64,
    string? JudgeSkippedReason,
    string? VideoPath = null,
    IReadOnlyList<string>? ScreenshotPaths = null,
    string? AnimatedGifPath = null,
    CandidatePreviewSource? PreviewSource = null,
    IReadOnlyList<string>? IncludedAssetPaths = null,
    string? SecondaryPreviewBase64 = null,
    IReadOnlyList<string>? SecondaryAssetPaths = null,
    CandidatePreviewSource? SecondaryPreviewSource = null,
    ScreenshotCaptureSummary? CaptureMetrics = null,
    PageAnalysis? PageAnalysis = null,
    string? AppBaseUrl = null);

public record CandidateScoredEvent(
    string RunId, string TaskId, string StrategyId,
    int AcScore, int DesignScore, int ReadabilityScore,
    int? VisualsScore = null,
    string? ScreenshotBase64 = null,
    string? Feedback = null,
    string? AcFeedback = null, string? DesignFeedback = null,
    string? ReadabilityFeedback = null, string? VisualsFeedback = null,
    CandidatePreviewSource? PreviewSource = null,
    IReadOnlyList<string>? IncludedAssetPaths = null,
    string? SecondaryPreviewBase64 = null,
    IReadOnlyList<string>? SecondaryAssetPaths = null,
    CandidatePreviewSource? SecondaryPreviewSource = null);
public record WinnerSelectedEvent(string RunId, string TaskId, string StrategyId, string TieBreakReason, double EvaluationElapsedSec);

/// <summary>
/// Emitted after evaluation with the full execution summary for a candidate.
/// Carries file changes parsed from the patch, diagnostic logs, metrics, and judge reasoning.
/// Separate from <see cref="CandidateEvaluatedEvent"/> to keep the lightweight event small
/// and avoid breaking existing SignalR subscribers.
/// </summary>
public record CandidateDetailEvent(
    string RunId,
    string TaskId,
    string StrategyId,
    CandidateExecutionSummary Summary);

/// <summary>
/// Emitted during execution with real-time activity updates from framework adapters.
/// High-frequency — dashboard should handle granularly (append, not full refresh).
/// </summary>
public record CandidateActivityEvent(
    string RunId,
    string TaskId,
    string StrategyId,
    ActivityEntry Activity);

/// <summary>A single activity log entry from a running framework.</summary>
public record ActivityEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Message,
    Dictionary<string, object>? Metadata = null);

/// <summary>
/// Emitted after the initial judge scoring round, before revision begins.
/// Carries the initial scores and judge feedback for the candidate.
/// </summary>
public record CandidateInitialScoredEvent(
    string RunId, string TaskId, string StrategyId,
    int AcScore, int DesignScore, int ReadabilityScore,
    int? VisualsScore,
    string? Feedback,
    string? ScreenshotBase64,
    string? AcFeedback = null, string? DesignFeedback = null, string? ReadabilityFeedback = null, string? VisualsFeedback = null);

/// <summary>Emitted when a candidate begins its revision attempt using judge feedback.</summary>
public record CandidateRevisionStartedEvent(
    string RunId, string TaskId, string StrategyId,
    DateTimeOffset At);

/// <summary>
/// Emitted when a candidate's revision attempt completes (success or failure).
/// </summary>
public record CandidateRevisionCompletedEvent(
    string RunId, string TaskId, string StrategyId,
    bool Succeeded, string? FailureReason,
    double RevisionElapsedSec, long? TokensUsed);

/// <summary>
/// Emitted at phase transitions during orchestration so the dashboard can show
/// what step the evaluation is at (e.g., "2/3 candidates complete", "Judging...").
/// </summary>
public record EvaluationProgressEvent(
    string RunId, string TaskId,
    string Phase,
    int CompletedCandidates,
    int TotalCandidates,
    string? Detail);

/// <summary>Emitted when a gate-failed candidate begins a retry attempt.</summary>
public record CandidateRetryStartedEvent(
    string RunId, string TaskId, string StrategyId,
    string FailedGate,
    DateTimeOffset At);

/// <summary>Emitted when a gate-failed candidate's retry attempt completes.</summary>
public record CandidateRetryCompletedEvent(
    string RunId, string TaskId, string StrategyId,
    bool Succeeded, string? FailureReason,
    double RetryElapsedSec, long? TokensUsed);

/// <summary>Emitted when a user cancels an orchestration from the dashboard.</summary>
public record OrchestrationCancelledEvent(
    string RunId, string TaskId,
    string Reason,
    DateTimeOffset At);

/// <summary>
/// Emitted when async background video capture completes for a candidate.
/// Dashboard can update the candidate gallery with the video path.
/// </summary>
public record CandidateVideoReadyEvent(
    string RunId, string TaskId, string StrategyId,
    string? VideoPath, bool Failed, string? FailureReason,
    string? AnimatedGifPath = null);

/// <summary>
/// Emitted at each step of the media capture pipeline so the dashboard can
/// show a real-time progress tracker with per-step timing.
/// </summary>
public record MediaCaptureProgressEvent(
    string RunId, string TaskId, string StrategyId,
    VirtualDevTeam.Core.Strategies.MediaCapture.MediaCaptureStepId StepId,
    VirtualDevTeam.Core.Strategies.MediaCapture.MediaCaptureStepStatus Status,
    string? Detail,
    DateTimeOffset At,
    double? ElapsedMs);

/// <summary>
/// Emitted periodically by the AgenticStreamAnalyzer to push live monitoring state
/// to the dashboard. Fired on state transitions (build pass/fail, tests pass, nudge sent)
/// and every 5 tool calls.
/// </summary>
public record CandidateAnalyzerUpdateEvent(
    string RunId, string TaskId, string StrategyId,
    int ToolCallCount, bool BuildPassed, bool TestsPassed,
    int BuildFailCount, string? AnalyzerVerdict, bool NudgeSent);
