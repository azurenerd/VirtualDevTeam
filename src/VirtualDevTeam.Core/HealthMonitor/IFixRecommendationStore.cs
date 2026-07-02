namespace VirtualDevTeam.Core.HealthMonitor;

public interface IFixRecommendationStore
{
    FixRecommendation? GetRecommendation(string id);
    void UpdateRecommendationState(string id, FixRecommendationState newState, string? feedback = null);
}
