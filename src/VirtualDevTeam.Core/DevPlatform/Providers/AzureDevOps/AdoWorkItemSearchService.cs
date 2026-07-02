using System.Text.RegularExpressions;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps implementation of <see cref="IWorkItemSearchService"/>.
/// Searches work items by URL, numeric ID, or WIQL text query.
/// </summary>
public sealed partial class AdoWorkItemSearchService : AdoHttpClientBase, IWorkItemSearchService
{
    private readonly IWorkItemService _workItemService;
    private readonly ILogger<AdoWorkItemSearchService> _logger;

    [GeneratedRegex(@"dev\.azure\.com/.+/_workitems/edit/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoDevAzureUrlRegex();

    [GeneratedRegex(@"visualstudio\.com/.+/_workitems/edit/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoVsUrlRegex();

    public AdoWorkItemSearchService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.VirtualDevTeamConfig> config,
        ILogger<AdoWorkItemSearchService> logger,
        IWorkItemService workItemService)
        : base(http, authProvider, config, logger)
    {
        ArgumentNullException.ThrowIfNull(workItemService);
        _workItemService = workItemService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkItemSearchResult>> SearchAsync(
        string query, int maxResults = 10, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 1. Try ADO work item URL (dev.azure.com)
        var devAzureMatch = AdoDevAzureUrlRegex().Match(query);
        if (devAzureMatch.Success && int.TryParse(devAzureMatch.Groups[1].Value, out var devAzureId))
        {
            _logger.LogDebug("Searching ADO work item by dev.azure.com URL, extracted ID {WorkItemId}", devAzureId);
            return await FetchSingleAsync(devAzureId, ct);
        }

        // 2. Try ADO work item URL (visualstudio.com)
        var vsMatch = AdoVsUrlRegex().Match(query);
        if (vsMatch.Success && int.TryParse(vsMatch.Groups[1].Value, out var vsId))
        {
            _logger.LogDebug("Searching ADO work item by visualstudio.com URL, extracted ID {WorkItemId}", vsId);
            return await FetchSingleAsync(vsId, ct);
        }

        // 3. Try pure integer ID
        if (int.TryParse(query.Trim(), out var numericId))
        {
            _logger.LogDebug("Searching ADO work item by numeric ID {WorkItemId}", numericId);
            return await FetchSingleAsync(numericId, ct);
        }

        // 4. Text search via WIQL
        _logger.LogDebug("Searching ADO work items by text query: {Query}", query);
        return await SearchByWiqlAsync(query, maxResults, ct);
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
                item.WorkItemType,
                item.Url,
                item.Body)
        ];
    }

    private async Task<IReadOnlyList<WorkItemSearchResult>> SearchByWiqlAsync(
        string query, int maxResults, CancellationToken ct)
    {
        // Escape single quotes in the query to prevent WIQL injection
        var sanitized = query.Replace("'", "''");
        var wiql = $"SELECT [System.Id], [System.Title], [System.State], [System.WorkItemType] " +
                   $"FROM WorkItems " +
                   $"WHERE [System.Title] CONTAINS '{sanitized}' " +
                   $"AND [System.State] <> 'Removed' " +
                   $"ORDER BY [System.ChangedDate] DESC";

        var queryUrl = BuildUrl($"{Project}/_apis/wit/wiql");
        var queryResult = await PostAsync<AdoWorkItemQueryResult>(queryUrl, new { query = wiql }, ct);

        if (queryResult?.WorkItems is not { Count: > 0 })
            return [];

        var ids = queryResult.WorkItems.Select(w => w.Id).Take(maxResults).ToList();
        var idsParam = string.Join(",", ids);
        var batchUrl = BuildUrl($"{Project}/_apis/wit/workitems",
            $"ids={idsParam}&fields=System.Id,System.Title,System.State,System.WorkItemType");
        var batch = await GetAsync<AdoListResponse<AdoWorkItem>>(batchUrl, ct);

        if (batch?.Value is null)
            return [];

        var results = batch.Value.Select(wi =>
        {
            var fields = wi.Fields;
            var title = fields.GetValueOrDefault("System.Title")?.ToString() ?? "";
            var state = fields.GetValueOrDefault("System.State")?.ToString() ?? "";
            var workItemType = fields.GetValueOrDefault("System.WorkItemType")?.ToString() ?? "Task";
            var url = $"https://dev.azure.com/{Organization}/{Project}/_workitems/edit/{wi.Id}";

            return new WorkItemSearchResult(wi.Id, title, state, workItemType, url);
        }).ToList();

        _logger.LogDebug("ADO WIQL search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }
}
