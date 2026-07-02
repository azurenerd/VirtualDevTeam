using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.DevPlatform.Models;
using Markdig;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps Work Item operations using the WIT REST API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/wit/work-items
/// </summary>
public sealed class AdoWorkItemService : AdoHttpClientBase, IWorkItemService
{
    private readonly ILogger<AdoWorkItemService> _logger;
    private readonly DevPlatformConfig _platformConfig;
    private readonly VirtualDevTeamConfig _config;
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _configMonitor;
    private readonly VirtualDevTeam.Core.Persistence.AgentStateStore? _stateStore;

    // --- In-memory read cache (same pattern as GitHubService, commit 1659913) ---
    private readonly Dictionary<string, (DateTime Expires, object Value)> _readCache = new();
    private readonly object _cacheLock = new();
    private GitHubCacheConfig CacheConfig => _configMonitor.CurrentValue.GitHubCache;
    private TimeSpan ListOpenTtl => !CacheConfig.Enabled ? TimeSpan.Zero : TimeSpan.FromSeconds(CacheConfig.ListOpenTtlSeconds);
    private TimeSpan GetByNumberTtl => !CacheConfig.Enabled ? TimeSpan.Zero : TimeSpan.FromSeconds(CacheConfig.GetByNumberTtlSeconds);
    private TimeSpan ListByLabelTtl => !CacheConfig.Enabled ? TimeSpan.Zero : TimeSpan.FromSeconds(CacheConfig.ListByLabelTtlSeconds);

    public AdoWorkItemService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<AdoWorkItemService> logger,
        IOptionsMonitor<VirtualDevTeamConfig> configMonitor,
        VirtualDevTeam.Core.Persistence.AgentStateStore? stateStore = null)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
        _platformConfig = config.Value.DevPlatform ?? new DevPlatformConfig();
        _config = config.Value;
        _configMonitor = configMonitor;
        _stateStore = stateStore;
    }

    private bool TryGetCached<T>(string key, out T? value)
    {
        lock (_cacheLock)
        {
            if (_readCache.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.Expires)
            {
                value = (T)entry.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private void SetCached<T>(string key, T value, TimeSpan ttl) where T : notnull
    {
        if (ttl == TimeSpan.Zero) return;
        lock (_cacheLock)
            _readCache[key] = (DateTime.UtcNow + ttl, value);
    }

    private void InvalidateCache()
    {
        lock (_cacheLock)
            _readCache.Clear();
    }

    // Read lazily — RunStartedUtc is null at DI construction, set later when wizard starts a run
    private DateTime? _runStartedUtc => _stateStore?.RunStartedUtc;

    private string DefaultWorkItemType => _platformConfig.AzureDevOps?.DefaultWorkItemType ?? "Task";

    /// <summary>WIQL date clause to scope queries to the current run (excludes stale items from prior runs).
    /// ADO WIQL requires date-only format (no time component) for CreatedDate comparisons.</summary>
    private string RunScopeDateClause =>
        _runStartedUtc.HasValue
            ? $" AND [System.CreatedDate] >= '{_runStartedUtc.Value:yyyy-MM-dd}'"
            : "";

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Convert markdown body to HTML for ADO's Description field.</summary>
    private static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        // If content already looks like HTML, pass through
        if (markdown.TrimStart().StartsWith('<')) return markdown;
        return Markdown.ToHtml(markdown, MarkdownPipeline);
    }

    public async Task<PlatformWorkItem> CreateAsync(
        string title, string body, IReadOnlyList<string> labels,
        CancellationToken ct = default)
    {
        // Map "enhancement" label to User Story type in ADO
        var type = labels.Any(l => l.Equals("enhancement", StringComparison.OrdinalIgnoreCase))
            ? (_platformConfig.AzureDevOps?.ExecutiveWorkItemType ?? "User Story")
            : DefaultWorkItemType;
        var url = BuildUrl($"{Project}/_apis/wit/workitems/${Uri.EscapeDataString(type)}");

        var patchDoc = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/System.Description", value = ToHtml(body) }
        };

        if (labels.Count > 0)
            patchDoc.Add(new { op = "add", path = "/fields/System.Tags", value = string.Join("; ", labels) });

        // Link as child of the configured parent work item (Hierarchy-Reverse = child→parent)
        // Read lazily from config — MergeIntoConfig sets this after DI construction
        var parentWorkItemId = _config.Project.ParentWorkItemId;
        if (parentWorkItemId.HasValue)
        {
            patchDoc.Add(new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{BaseUrl}{Project}/_apis/wit/workitems/{parentWorkItemId.Value}"
                }
            });
        }

        var result = await PatchAsync<AdoWorkItemCreateResult>(url, patchDoc, ct, "application/json-patch+json")
            ?? throw new InvalidOperationException("ADO returned null for work item creation");

        _logger.LogInformation("Created ADO work item #{Id}: {Title}", result.Id, title);
        InvalidateCache();
        return await GetAsync(result.Id, ct)
            ?? throw new InvalidOperationException($"Failed to fetch created work item #{result.Id}");
    }

    public async Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default)
    {
        var cacheKey = $"Get|{id}";
        if (TryGetCached<PlatformWorkItem>(cacheKey, out var cached)) return cached;

        var url = BuildUrl($"{Project}/_apis/wit/workitems/{id}", "$expand=Relations");
        var wi = await GetAsync<AdoWorkItem>(url, ct);
        var result = wi is not null ? AdoModelMapper.ToPlatform(wi, Organization, Project) : null;
        if (result is not null) SetCached(cacheKey, result, GetByNumberTtl);
        return result;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default)
    {
        const string cacheKey = "ListOpen";
        if (TryGetCached<IReadOnlyList<PlatformWorkItem>>(cacheKey, out var cached)) return cached!;

        var result = await QueryWorkItemsAsync(
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Project}' AND [System.State] <> 'Closed' AND [System.State] <> 'Removed' AND [System.State] <> 'Done'{RunScopeDateClause} ORDER BY [System.CreatedDate] DESC",
            ct);
        SetCached(cacheKey, result, ListOpenTtl);
        return result;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default)
    {
        const string cacheKey = "ListAll";
        if (TryGetCached<IReadOnlyList<PlatformWorkItem>>(cacheKey, out var cached)) return cached!;

        var result = await QueryWorkItemsAsync(
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Project}' ORDER BY [System.CreatedDate] DESC",
            ct);
        SetCached(cacheKey, result, ListOpenTtl);
        return result;
    }

    // ADO's ListAllAsync already has no run-scope filter, so this is a direct delegate.
    public Task<IReadOnlyList<PlatformWorkItem>> ListAllForProjectAsync(CancellationToken ct = default)
        => ListAllAsync(ct);

    public async Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(
        string agentName, CancellationToken ct = default)
    {
        var cacheKey = $"ListForAgent|{agentName}";
        if (TryGetCached<IReadOnlyList<PlatformWorkItem>>(cacheKey, out var cached)) return cached!;

        var result = await QueryWorkItemsAsync(
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Project}' AND [System.AssignedTo] CONTAINS '{agentName}'{RunScopeDateClause} ORDER BY [System.CreatedDate] DESC",
            ct);
        SetCached(cacheKey, result, ListOpenTtl);
        return result;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(
        string label, string? state = null, CancellationToken ct = default)
    {
        var cacheKey = $"ListByLabel|{label}|{state ?? "any"}";
        if (TryGetCached<IReadOnlyList<PlatformWorkItem>>(cacheKey, out var cached)) return cached!;

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Project}' AND [System.Tags] CONTAINS '{label}'";
        if (state == "open")
            wiql += " AND [System.State] <> 'Closed' AND [System.State] <> 'Removed' AND [System.State] <> 'Done'";
        else if (state == "closed")
            wiql += " AND ([System.State] = 'Closed' OR [System.State] = 'Removed' OR [System.State] = 'Done')";
        wiql += RunScopeDateClause;
        wiql += " ORDER BY [System.CreatedDate] DESC";

        var result = await QueryWorkItemsAsync(wiql, ct);
        SetCached(cacheKey, result, ListByLabelTtl);
        return result;
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, string? state = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/wit/workitems/{id}");
        var patchDoc = new List<object>();

        if (title is not null)
            patchDoc.Add(new { op = "replace", path = "/fields/System.Title", value = title });
        if (body is not null)
            patchDoc.Add(new { op = "replace", path = "/fields/System.Description", value = ToHtml(body) });
        if (state is not null)
        {
            var mappedState = MapToAdoState(state);
            patchDoc.Add(new { op = "replace", path = "/fields/System.State", value = mappedState });
        }
        if (labels is not null)
            patchDoc.Add(new { op = "replace", path = "/fields/System.Tags", value = string.Join("; ", labels) });

        if (patchDoc.Count > 0)
            await PatchAsync<AdoWorkItem>(url, patchDoc, ct, "application/json-patch+json");
        InvalidateCache();
    }

    public async Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default)
    {
        await UpdateAsync(id, title: newTitle, ct: ct);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        await UpdateAsync(id, state: "closed", ct: ct);
        _logger.LogInformation("Closed ADO work item #{Number}", id);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        try
        {
            // ADO supports permanent deletion via DELETE with destroy=true
            // Requires PAT with "Work Items (Read, Write & Manage)" scope
            var url = BuildUrl($"{Project}/_apis/wit/workitems/{id}", "destroy=true");
            await DeleteAsync(url, ct);
            _logger.LogInformation("Permanently deleted ADO work item #{Number}", id);
            InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            // Fall back to closing if hard delete fails (e.g., insufficient permissions)
            _logger.LogWarning(ex, "Hard delete failed for work item #{Number}, falling back to close", id);
            await CloseAsync(id, ct);
            return true;
        }
    }

    public async Task AddCommentAsync(int id, string comment, CancellationToken ct = default)
    {
        // ADO work item comments render HTML, not markdown. Convert before posting.
        var htmlComment = Markdown.ToHtml(comment, MarkdownPipeline);
        var url = BuildPreviewUrl($"{Project}/_apis/wit/workitems/{id}/comments");
        await PostAsync<object>(url, new { text = htmlComment }, ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default)
    {
        var url = BuildPreviewUrl($"{Project}/_apis/wit/workitems/{id}/comments", "$top=200");
        var response = await GetAsync<AdoListResponse<AdoPrComment>>(url, ct);
        return response?.Value.Select(AdoModelMapper.ToPlatform).ToList()
            ?? new List<PlatformComment>();
    }

    public async Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/wit/workitems/{parentId}");
        var patchDoc = new List<object>
        {
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Forward",
                    url = $"{BaseUrl}{Project}/_apis/wit/workitems/{childPlatformId}"
                }
            }
        };

        await PatchAsync<AdoWorkItem>(url, patchDoc, ct, "application/json-patch+json");
        return true;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
    {
        // Get the parent work item with relations expanded
        var url = BuildUrl($"{Project}/_apis/wit/workitems/{parentId}", "$expand=Relations");
        var parent = await GetAsync<AdoWorkItem>(url, ct);

        if (parent?.Relations is null)
            return new List<PlatformWorkItem>();

        var childIds = parent.Relations
            .Where(r => r.Rel == "System.LinkTypes.Hierarchy-Forward")
            .Select(r =>
            {
                // URL format: .../workitems/{id}
                var lastSlash = r.Url.LastIndexOf('/');
                return int.TryParse(r.Url[(lastSlash + 1)..], out var id) ? id : 0;
            })
            .Where(id => id > 0)
            .ToList();

        if (childIds.Count == 0)
            return new List<PlatformWorkItem>();

        var idsParam = string.Join(",", childIds);
        var batchUrl = BuildUrl($"{Project}/_apis/wit/workitems",
            $"ids={idsParam}&$expand=Relations");
        var batch = await GetAsync<AdoListResponse<AdoWorkItem>>(batchUrl, ct);

        return batch?.Value.Select(w => AdoModelMapper.ToPlatform(w, Organization, Project)).ToList()
            ?? new List<PlatformWorkItem>();
    }

    public async Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/wit/workitems/{blockedId}");
        var patchDoc = new List<object>
        {
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Dependency-Forward",
                    url = $"{BaseUrl}{Project}/_apis/wit/workitems/{blockingPlatformId}",
                    attributes = new { comment = "VirtualDevTeam dependency" }
                }
            }
        };

        await PatchAsync<AdoWorkItem>(url, patchDoc, ct, "application/json-patch+json");
        return true;
    }

    private string MapToAdoState(string virtualDevTeamState)
    {
        var mappings = _platformConfig.AzureDevOps?.StateMappings;
        if (mappings is not null && mappings.TryGetValue(virtualDevTeamState, out var adoState))
            return adoState;

        var closedState = _platformConfig.AzureDevOps?.ClosedStateName ?? "Closed";

        return virtualDevTeamState.ToLowerInvariant() switch
        {
            "open" => "New",
            "inprogress" or "in_progress" => "Active",
            "blocked" => "Active",
            "closed" or "resolved" or "done" => closedState,
            _ => "New"
        };
    }

    private async Task<IReadOnlyList<PlatformWorkItem>> QueryWorkItemsAsync(string wiql, CancellationToken ct)
    {
        var queryUrl = BuildUrl($"{Project}/_apis/wit/wiql");
        var queryResult = await PostAsync<AdoWorkItemQueryResult>(queryUrl, new { query = wiql }, ct);

        if (queryResult?.WorkItems is not { Count: > 0 })
            return new List<PlatformWorkItem>();

        var ids = queryResult.WorkItems.Select(w => w.Id).Take(200).ToList();
        var idsParam = string.Join(",", ids);
        var batchUrl = BuildUrl($"{Project}/_apis/wit/workitems",
            $"ids={idsParam}&$expand=Relations");
        var batch = await GetAsync<AdoListResponse<AdoWorkItem>>(batchUrl, ct);

        var items = batch?.Value.Select(w => AdoModelMapper.ToPlatform(w, Organization, Project)).ToList()
            ?? new List<PlatformWorkItem>();

        // Post-fetch precision filter: WIQL CreatedDate is date-only (no time component),
        // so same-day items from prior runs leak through. Filter by exact RunStartedUtc.
        if (_runStartedUtc.HasValue)
        {
            var before = items.Count;
            items = items.Where(w => w.CreatedAt >= _runStartedUtc.Value).ToList();
            if (items.Count < before)
                _logger.LogDebug("Run-scope post-filter removed {Removed} stale work items (pre: {Before}, post: {After})",
                    before - items.Count, before, items.Count);
        }

        return items;
    }
}
