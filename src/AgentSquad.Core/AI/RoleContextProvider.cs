using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.AI;

/// <summary>
/// Provides per-agent role context built from configuration: custom role descriptions,
/// knowledge link summaries, and MCP server names. Context is initialized once per agent
/// and cached for the session to avoid repeated fetches and summarization.
/// Uses content-type-aware extraction and optional AI-powered summarization.
/// </summary>
public class RoleContextProvider
{
    private readonly IOptionsMonitor<AgentSquadConfig> _configMonitor;
    private readonly ILogger<RoleContextProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IContentExtractor> _extractors;
    private readonly AiKnowledgeSummarizer? _summarizer;

    // Cache knowledge summaries per agent key (role name or custom agent name)
    private readonly ConcurrentDictionary<string, string> _knowledgeCache = new();
    private readonly ConcurrentDictionary<string, bool> _initialized = new();

    private const int MaxPerLinkBytes = 50_000;
    private const int FetchTimeoutSeconds = 10;

    public RoleContextProvider(
        IOptionsMonitor<AgentSquadConfig> configMonitor,
        ILogger<RoleContextProvider> logger,
        HttpClient? httpClient = null,
        AiKnowledgeSummarizer? summarizer = null,
        IEnumerable<IContentExtractor>? extractors = null)
    {
        _configMonitor = configMonitor ?? throw new ArgumentNullException(nameof(configMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _summarizer = summarizer;
        _extractors = extractors?.ToList() ?? CreateDefaultExtractors();
    }

    /// <summary>
    /// Initializes knowledge for an agent role by fetching and summarizing knowledge links.
    /// Should be called once during agent initialization.
    /// </summary>
    public async Task InitializeForAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        await InitializeForAgentAsync(role, customAgentName: null, ct);
    }

    /// <summary>
    /// Initializes knowledge for an agent (built-in or custom) by fetching and summarizing knowledge links.
    /// For custom agents, pass the custom agent name to locate the correct config entry.
    /// </summary>
    public async Task InitializeForAgentAsync(AgentRole role, string? customAgentName, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(role, customAgentName);

        if (_initialized.TryGetValue(cacheKey, out var done) && done)
            return;

        var config = GetAgentConfig(role, customAgentName);
        if (config.KnowledgeLinks.Count > 0)
        {
            var knowledge = await FetchAndSummarizeLinksAsync(cacheKey, config.KnowledgeLinks, config, ct);
            _knowledgeCache[cacheKey] = knowledge;
            _logger.LogInformation("Initialized knowledge context for {CacheKey}: {CharCount} chars from {LinkCount} links",
                cacheKey, knowledge.Length, config.KnowledgeLinks.Count);
        }

        _initialized[cacheKey] = true;
    }

    /// <summary>
    /// Returns the composite role context string to prepend to system prompts.
    /// Includes custom role description and cached knowledge summaries.
    /// Returns empty string if no customization is configured.
    /// </summary>
    public string GetRoleSystemContext(AgentRole role)
    {
        return GetRoleSystemContext(role, customAgentName: null);
    }

    /// <summary>
    /// Returns the composite role context string for a specific agent (built-in or custom).
    /// Uses tier-based knowledge budgets when model tier is available.
    /// </summary>
    public string GetRoleSystemContext(AgentRole role, string? customAgentName, string? modelTier = null)
    {
        var cacheKey = GetCacheKey(role, customAgentName);
        var config = GetAgentConfig(role, customAgentName);
        var sb = new StringBuilder();

        var maxRoleDescChars = KnowledgeBudget.GetMaxRoleDescriptionChars(modelTier ?? config.ModelTier);

        // Inject custom role description
        var roleDesc = config.RoleDescription?.Trim();
        if (!string.IsNullOrEmpty(roleDesc))
        {
            if (roleDesc.Length > maxRoleDescChars)
            {
                roleDesc = roleDesc[..maxRoleDescChars] + "...";
                _logger.LogWarning("Role description for {CacheKey} truncated to {MaxChars} chars", cacheKey, maxRoleDescChars);
            }
            sb.AppendLine("[ROLE CUSTOMIZATION]");
            sb.AppendLine(roleDesc);
            sb.AppendLine();
        }

        // Inject cached knowledge context
        if (_knowledgeCache.TryGetValue(cacheKey, out var knowledge) && !string.IsNullOrWhiteSpace(knowledge))
        {
            sb.AppendLine("[ROLE KNOWLEDGE]");
            sb.AppendLine(knowledge);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns the MCP server names configured for the given agent role.
    /// </summary>
    public IReadOnlyList<string> GetMcpServers(AgentRole role)
    {
        return GetMcpServers(role, customAgentName: null);
    }

    /// <summary>
    /// Returns the MCP server names for a specific agent (built-in or custom).
    /// </summary>
    public IReadOnlyList<string> GetMcpServers(AgentRole role, string? customAgentName)
    {
        var config = GetAgentConfig(role, customAgentName);
        return config.McpServers
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Clears cached knowledge for a role, forcing re-fetch on next initialization.
    /// </summary>
    public void InvalidateCache(AgentRole role)
    {
        InvalidateCache(role, customAgentName: null);
    }

    /// <summary>
    /// Clears cached knowledge for a specific agent.
    /// </summary>
    public void InvalidateCache(AgentRole role, string? customAgentName)
    {
        var cacheKey = GetCacheKey(role, customAgentName);
        _knowledgeCache.TryRemove(cacheKey, out _);
        _initialized.TryRemove(cacheKey, out _);
    }

    private static string GetCacheKey(AgentRole role, string? customAgentName)
    {
        if (role == AgentRole.Custom && !string.IsNullOrWhiteSpace(customAgentName))
            return $"custom:{customAgentName}";
        return role.ToString();
    }

    private AgentConfig GetAgentConfig(AgentRole role, string? customAgentName = null)
    {
        var agents = _configMonitor.CurrentValue.Agents;

        if (role == AgentRole.Custom && !string.IsNullOrWhiteSpace(customAgentName))
        {
            var custom = agents.CustomAgents.FirstOrDefault(c =>
                string.Equals(c.Name, customAgentName, StringComparison.OrdinalIgnoreCase));
            return custom ?? new AgentConfig();
        }

        return role switch
        {
            AgentRole.ProgramManager => agents.ProgramManager,
            AgentRole.Researcher => agents.Researcher,
            AgentRole.Architect => agents.Architect,
            AgentRole.SoftwareEngineer => agents.SoftwareEngineer,
            AgentRole.TestEngineer => agents.TestEngineer,
            _ => new AgentConfig()
        };
    }

    private async Task<string> FetchAndSummarizeLinksAsync(
        string cacheKey, List<string> links, AgentConfig config, CancellationToken ct)
    {
        var summaries = new List<string>();
        var maxKnowledgeChars = KnowledgeBudget.GetMaxKnowledgeChars(config.ModelTier);
        var maxPerLinkChars = KnowledgeBudget.GetMaxPerLinkChars(config.ModelTier);
        var totalChars = 0;

        foreach (var url in links)
        {
            if (totalChars >= maxKnowledgeChars)
            {
                _logger.LogWarning("Knowledge budget exhausted for {CacheKey} after {Count} links", cacheKey, summaries.Count);
                break;
            }

            try
            {
                var (content, contentType) = await FetchUrlContentAsync(url, ct);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                // Use content-type-aware extraction
                var extracted = ExtractContent(content, url, contentType);
                if (string.IsNullOrWhiteSpace(extracted))
                    continue;

                // AI-powered summarization for long content, with fallback to truncation
                string summary;
                if (_summarizer is not null && extracted.Length > maxPerLinkChars * 2)
                {
                    summary = await _summarizer.SummarizeForRoleAsync(
                        extracted,
                        cacheKey,
                        config.RoleDescription,
                        maxPerLinkChars,
                        ct);
                }
                else
                {
                    summary = TruncateToSummary(extracted, url, maxPerLinkChars);
                }

                var remaining = maxKnowledgeChars - totalChars;
                if (summary.Length > remaining)
                    summary = summary[..remaining] + "...";

                summaries.Add(summary);
                totalChars += summary.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch knowledge link for {CacheKey}: {Url}", cacheKey, url);
            }
        }

        return string.Join("\n\n", summaries);
    }

    private string ExtractContent(string rawContent, string url, string? contentType)
    {
        // Find a suitable extractor
        foreach (var extractor in _extractors)
        {
            if (extractor.CanHandle(url, contentType))
            {
                try
                {
                    return extractor.Extract(rawContent, url);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Extractor {Type} failed for {Url}, trying next", extractor.GetType().Name, url);
                }
            }
        }

        // Fallback: basic HTML strip
        return System.Text.RegularExpressions.Regex.Replace(rawContent, @"<[^>]+>", " ");
    }

    private async Task<(string Content, string? ContentType)> FetchUrlContentAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Invalid knowledge link URL: {Url}", url);
            return ("", null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;

            // Read with size limit
            var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[MaxPerLinkBytes];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, MaxPerLinkBytes), cts.Token);
            var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            return (content, contentType);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out fetching knowledge link: {Url}", url);
            return ("", null);
        }
    }

    /// <summary>
    /// Produces a compact summary from extracted content.
    /// Takes the first N chars as a representative excerpt.
    /// </summary>
    private static string TruncateToSummary(string content, string url, int maxChars = 800)
    {
        var text = content;

        if (text.Length > maxChars)
            text = text[..maxChars] + "...";

        return $"[Source: {url}]\n{text}";
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "AgentSquad-KnowledgeFetcher/1.0");
        client.Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds * 2);
        return client;
    }

    private static List<IContentExtractor> CreateDefaultExtractors() =>
    [
        new MarkdownContentExtractor(),
        new HtmlContentExtractor()
    ];
}
