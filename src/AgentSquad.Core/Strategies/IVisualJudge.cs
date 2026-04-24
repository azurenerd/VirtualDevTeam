namespace AgentSquad.Core.Strategies;

/// <summary>
/// Vision-based judge interface. Scores candidate screenshots on visual quality (0-10).
/// Separated from <see cref="ILlmJudge"/> because vision calls require multimodal models
/// and have different token budget characteristics (images are large).
/// </summary>
public interface IVisualJudge
{
    Task<VisualJudgeResult> ScoreAsync(VisualJudgeInput input, CancellationToken ct);
}

public record VisualJudgeInput
{
    public required string TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required string TaskDescription { get; init; }
    /// <summary>Candidate id → PNG screenshot bytes. Only candidates with screenshots are included.</summary>
    public required IReadOnlyDictionary<string, byte[]> CandidateScreenshots { get; init; }
}

public record VisualJudgeResult
{
    /// <summary>Per-candidate visual scores. Empty when the judge failed or had no input.</summary>
    public required IReadOnlyDictionary<string, VisualScore> Scores { get; init; }
    public string? Error { get; init; }
    public long TokensUsed { get; init; }
    public bool IsFallback => Scores.Count == 0;
}

public record VisualScore
{
    public int Score { get; init; }
    public string Reasoning { get; init; } = "";
}

/// <summary>
/// Default no-op visual judge. Returns empty scores — candidates that should have had
/// screenshots will receive VisualsScore = 0 (penalized). Non-visual tasks exclude
/// the visual score entirely (null).
/// </summary>
public sealed class NullVisualJudge : IVisualJudge
{
    public Task<VisualJudgeResult> ScoreAsync(VisualJudgeInput input, CancellationToken ct) =>
        Task.FromResult(new VisualJudgeResult { Scores = new Dictionary<string, VisualScore>() });
}
