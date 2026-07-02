using VirtualDevTeam.Core.HealthMonitor;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

public interface IDiagnosticActionExecutor
{
    Task<DiagnosticActionResult?> ExecuteAsync(DiagnosticActionRequest request, CancellationToken ct = default);
}

public sealed record DiagnosticActionRequest
{
    public required DiagnosticActionKind Kind { get; init; }
    public required string RecommendationId { get; init; }
    public string? RepoRoot { get; init; }
}

public sealed record DiagnosticActionResult
{
    public required string RecommendationId { get; init; }
    public required FixRecommendationState State { get; init; }
    public FixTier? Tier { get; init; }
    public required string Detail { get; init; }
    public bool RestartRequired { get; init; }
}

public enum DiagnosticActionKind
{
    ApplyRecommendation,
    DismissRecommendation,
}
