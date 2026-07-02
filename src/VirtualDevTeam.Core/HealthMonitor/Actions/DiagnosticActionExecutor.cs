using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Executes the allowlisted set of operator-approved FlowMonitor recommendation actions.
/// Keep this surface intentionally narrow: only recommendation apply/dismiss lives here.
/// </summary>
public sealed class DiagnosticActionExecutor : IDiagnosticActionExecutor
{
    private readonly IFixRecommendationStore _recommendations;
    private readonly IFixRecommendationApplicator _applicator;
    private readonly GateNotificationService _notifications;
    private readonly ILogger<DiagnosticActionExecutor> _logger;

    public DiagnosticActionExecutor(
        IFixRecommendationStore recommendations,
        IFixRecommendationApplicator applicator,
        GateNotificationService notifications,
        ILogger<DiagnosticActionExecutor> logger)
    {
        _recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        _applicator = applicator ?? throw new ArgumentNullException(nameof(applicator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<DiagnosticActionResult?> ExecuteAsync(DiagnosticActionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Kind switch
        {
            DiagnosticActionKind.ApplyRecommendation => ApplyRecommendationAsync(request, ct),
            DiagnosticActionKind.DismissRecommendation => Task.FromResult(DismissRecommendation(request)),
            _ => throw new NotSupportedException($"Diagnostic action '{request.Kind}' is not allowlisted."),
        };
    }

    private async Task<DiagnosticActionResult?> ApplyRecommendationAsync(DiagnosticActionRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.RepoRoot);

        var rec = _recommendations.GetRecommendation(request.RecommendationId);
        if (rec is null)
            return null;

        _recommendations.UpdateRecommendationState(rec.Id, FixRecommendationState.ApprovedForCoding);

        var classification = rec.FixTier.HasValue && rec.AffectedFiles is { Count: > 0 }
            ? new FixClassification
            {
                Tier = rec.FixTier.Value,
                AffectedFiles = rec.AffectedFiles,
                Rationale = "(persisted)"
            }
            : FixClassifier.Classify(rec);

        DiagnosticActionResult result;
        try
        {
            switch (classification.Tier)
            {
                case FixTier.Live:
                case FixTier.DeferredRestart:
                {
                    _logger.LogInformation(
                        "DiagnosticActionExecutor: applying recommendation {Id} via {Tier} path ({Files} files)",
                        rec.Id,
                        classification.Tier,
                        classification.AffectedFiles.Count);

                    var applyResult = await _applicator.ApplyAsync(rec, classification.Tier, request.RepoRoot, ct);
                    _recommendations.UpdateRecommendationState(rec.Id, applyResult.State);
                    result = new DiagnosticActionResult
                    {
                        RecommendationId = rec.Id,
                        State = applyResult.State,
                        Tier = classification.Tier,
                        Detail = applyResult.Detail,
                        RestartRequired = applyResult.State is FixRecommendationState.Coded or FixRecommendationState.StagedForNextRestart,
                    };
                    break;
                }
                case FixTier.Blocked:
                {
                    var stagedPath = await StagePlanForNextBootAsync(rec, request.RepoRoot, ct);
                    _recommendations.UpdateRecommendationState(rec.Id, FixRecommendationState.StagedForNextRestart);
                    result = new DiagnosticActionResult
                    {
                        RecommendationId = rec.Id,
                        State = FixRecommendationState.StagedForNextRestart,
                        Tier = classification.Tier,
                        Detail = stagedPath is null
                            ? "🔴 Fix marked for next restart, but staged file write failed — check logs."
                            : $"🔴 Fix staged for next runner boot at `FixRecommendations/staged/{Path.GetFileName(stagedPath)}`. Restart to apply.",
                        RestartRequired = true,
                    };
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown fix tier: {classification.Tier}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiagnosticActionExecutor: apply path threw for recommendation {Id}", rec.Id);
            _recommendations.UpdateRecommendationState(rec.Id, FixRecommendationState.AppliedFailed);
            result = new DiagnosticActionResult
            {
                RecommendationId = rec.Id,
                State = FixRecommendationState.AppliedFailed,
                Tier = classification.Tier,
                Detail = $"Apply failed: {ex.Message}",
                RestartRequired = false,
            };
        }

        _notifications.Resolve($"flow-monitor:fix:{rec.Id}", resourceNumber: null);
        return result;
    }

    private DiagnosticActionResult? DismissRecommendation(DiagnosticActionRequest request)
    {
        var rec = _recommendations.GetRecommendation(request.RecommendationId);
        if (rec is null)
            return null;

        _recommendations.UpdateRecommendationState(rec.Id, FixRecommendationState.Rejected);
        _notifications.Resolve($"flow-monitor:fix:{rec.Id}", resourceNumber: null);

        return new DiagnosticActionResult
        {
            RecommendationId = rec.Id,
            State = FixRecommendationState.Rejected,
            Tier = rec.FixTier,
            Detail = "Fix recommendation dismissed.",
            RestartRequired = false,
        };
    }

    private static async Task<string?> StagePlanForNextBootAsync(
        FixRecommendation rec,
        string repoRoot,
        CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(repoRoot, "FixRecommendations", "staged");
            Directory.CreateDirectory(dir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"{stamp}-{rec.Id}.md";
            var fullPath = Path.Combine(dir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"id: {rec.Id}");
            sb.AppendLine($"finding_id: {rec.FindingId}");
            sb.AppendLine($"detector_id: {rec.DetectorId}");
            sb.AppendLine($"severity: {rec.Severity}");
            sb.AppendLine($"confidence: {rec.Confidence:0.00}");
            sb.AppendLine($"tier: {(rec.FixTier?.ToString() ?? nameof(FixTier.Blocked))}");
            if (rec.AffectedFiles is { Count: > 0 })
                sb.AppendLine($"affected_files: [{string.Join(", ", rec.AffectedFiles.Select(f => "\"" + f + "\""))}]");
            sb.AppendLine($"staged_at: {DateTimeOffset.UtcNow:o}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(rec.PlanMarkdown);

            await File.WriteAllTextAsync(fullPath, sb.ToString(), ct);
            return fullPath;
        }
        catch
        {
            return null;
        }
    }
}
