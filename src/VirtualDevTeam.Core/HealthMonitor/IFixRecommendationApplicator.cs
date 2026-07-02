namespace VirtualDevTeam.Core.HealthMonitor;

public interface IFixRecommendationApplicator
{
    Task<FixApplyResult> ApplyAsync(
        FixRecommendation rec,
        FixTier tier,
        string repoRoot,
        CancellationToken ct);
}
