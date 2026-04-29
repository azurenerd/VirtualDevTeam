namespace AgentSquad.Core.DevPlatform.Capabilities;

/// <summary>
/// Extension methods for IPullRequestService providing atomic label operations.
/// These avoid the read-modify-write race condition where concurrent label updates
/// can overwrite each other (last-write-wins problem).
/// </summary>
public static class PullRequestServiceExtensions
{
    /// <summary>
    /// Atomically add a label to a PR by re-fetching current labels immediately before writing.
    /// This minimizes the race window compared to using stale label state from an earlier fetch.
    /// </summary>
    public static async Task AddLabelAsync(
        this IPullRequestService prService, int prId, string label, CancellationToken ct = default)
    {
        var freshPr = await prService.GetAsync(prId, ct);
        if (freshPr is null) return;

        // If label already present, nothing to do
        if (freshPr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            return;

        var updatedLabels = freshPr.Labels
            .Append(label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await prService.UpdateAsync(prId, labels: updatedLabels, ct: ct);
    }

    /// <summary>
    /// Atomically add multiple labels to a PR by re-fetching current labels immediately before writing.
    /// </summary>
    public static async Task AddLabelsAsync(
        this IPullRequestService prService, int prId, IEnumerable<string> labels, CancellationToken ct = default)
    {
        var freshPr = await prService.GetAsync(prId, ct);
        if (freshPr is null) return;

        var updatedLabels = freshPr.Labels
            .Concat(labels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await prService.UpdateAsync(prId, labels: updatedLabels, ct: ct);
    }
}
