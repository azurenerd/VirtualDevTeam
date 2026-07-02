using System.Text.RegularExpressions;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// GitHub implementation of <see cref="IWorkItemSearchService"/>.
/// Searches issues by URL, numeric ID, or text query (via listing + filtering).
/// </summary>
public sealed partial class GitHubWorkItemSearchAdapter : IWorkItemSearchService
{
    private readonly IWorkItemService _workItemService;
    private readonly ILogger<GitHubWorkItemSearchAdapter> _logger;

    [GeneratedRegex(@"github\.com/.+/.+/issues/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubIssueUrlRegex();

    public GitHubWorkItemSearchAdapter(
        IWorkItemService workItemService,
        ILogger<GitHubWorkItemSearchAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(workItemService);
        ArgumentNullException.ThrowIfNull(logger);
        _workItemService = workItemService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkItemSearchResult>> SearchAsync(
        string query, int maxResults = 10, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 1. Try GitHub issue URL
        var urlMatch = GitHubIssueUrlRegex().Match(query);
        if (urlMatch.Success && int.TryParse(urlMatch.Groups[1].Value, out var urlId))
        {
            _logger.LogDebug("Searching GitHub issue by URL, extracted ID {IssueId}", urlId);
            return await FetchSingleAsync(urlId, ct);
        }

        // 2. Try pure integer ID
        if (int.TryParse(query.Trim(), out var numericId))
        {
            _logger.LogDebug("Searching GitHub issue by numeric ID {IssueId}", numericId);
            return await FetchSingleAsync(numericId, ct);
        }

        // 3. Text search: list all issues and filter by title match
        _logger.LogDebug("Searching GitHub issues by text query: {Query}", query);
        var allItems = await _workItemService.ListAllAsync(ct);
        var queryLower = query.Trim().ToLowerInvariant();

        var results = allItems
            .Where(wi => wi.Title.Contains(queryLower, StringComparison.OrdinalIgnoreCase)
                      || wi.Body.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .Select(wi => new WorkItemSearchResult(
                wi.Number,
                wi.Title,
                wi.State,
                "Issue",
                wi.Url))
            .ToList();

        _logger.LogDebug("GitHub text search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }

    private async Task<IReadOnlyList<WorkItemSearchResult>> FetchSingleAsync(int id, CancellationToken ct)
    {
        var item = await _workItemService.GetAsync(id, ct);
        if (item is null)
            return [];

        return
        [
            new WorkItemSearchResult(
                item.Number,
                item.Title,
                item.State,
                "Issue",
                item.Url,
                item.Body)
        ];
    }
}
