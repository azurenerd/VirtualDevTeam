namespace AgentSquad.Core.Strategies.Contracts;

/// <summary>
/// Frozen SignalR event contract for candidate lifecycle. Dashboard (Phase 4) builds
/// against these payload shapes; orchestrator emits them. Event names are stable.
/// </summary>
public static class StrategyEvents
{
    public const string CandidateStarted  = "candidate:started";
    public const string CandidateCompleted = "candidate:completed";
    public const string CandidateScored   = "candidate:scored";
    public const string WinnerSelected    = "winner:selected";
    public const string GateStarted       = "gate:started";
    public const string GateCompleted     = "gate:completed";
}

public record CandidateStartedEvent(string RunId, string TaskId, string StrategyId, DateTimeOffset At);
public record CandidateCompletedEvent(string RunId, string TaskId, string StrategyId, bool Succeeded, string? FailureReason, double ElapsedSec, long? TokensUsed);
public record GateEvent(string RunId, string TaskId, string StrategyId, string GateId, bool? Passed, string? Detail);
public record CandidateScoredEvent(string RunId, string TaskId, string StrategyId, int AcScore, int DesignScore, int ReadabilityScore);
public record WinnerSelectedEvent(string RunId, string TaskId, string StrategyId, string TieBreakReason, double EvaluationElapsedSec);
