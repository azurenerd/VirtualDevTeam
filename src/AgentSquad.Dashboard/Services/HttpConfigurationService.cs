using System.Net.Http.Json;
using System.Text.Json;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// HTTP-proxy implementation of <see cref="IConfigurationService"/> for standalone dashboard mode.
/// Forwards all configuration operations to the Runner's REST API.
/// Creates a fresh HttpClient per request via IHttpClientFactory to avoid stale connection issues
/// when the polling service (HttpDashboardDataService) shares the same handler pool.
/// </summary>
public sealed class HttpConfigurationService : IConfigurationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _clientName;
    private readonly ILogger<HttpConfigurationService> _logger;
    private AgentSquadConfig? _cachedConfig;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HttpConfigurationService(IHttpClientFactory httpFactory, string clientName, ILogger<HttpConfigurationService> logger)
    {
        _httpFactory = httpFactory;
        _clientName = clientName;
        _logger = logger;
    }

    private HttpClient CreateClient() => _httpFactory.CreateClient(_clientName);

    public AgentSquadConfig GetCurrentConfig()
    {
        // Return cached config if available (refreshed on save)
        if (_cachedConfig is not null)
            return _cachedConfig;

        // First call — fetch from Runner on a thread-pool thread to avoid
        // blocking the Blazor Server synchronization context.
        try
        {
            var config = Task.Run(async () =>
            {
                using var client = CreateClient();
                var response = await client.GetAsync("/api/configuration/current");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AgentSquadConfig>(JsonOptions);
            }).GetAwaiter().GetResult();

            _cachedConfig = config ?? new AgentSquadConfig();
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch configuration from Runner — returning defaults");
            return new AgentSquadConfig();
        }
    }

    public async Task SaveConfigAsync(AgentSquadConfig updatedConfig)
    {
        // Serialize on the calling thread (safe, CPU-bound)
        var json = System.Text.Json.JsonSerializer.Serialize(updatedConfig);
        _logger.LogInformation("SaveConfigAsync: {Bytes} bytes, dispatching to thread pool...", json.Length);

        // Run the HTTP call on a thread-pool thread to completely bypass the
        // Blazor Server synchronization context, which can interfere with
        // SocketsHttpHandler I/O on some Windows/.NET combinations.
        await Task.Run(async () =>
        {
            using var client = CreateClient();
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.PostAsync("/api/configuration/save", content);
            sw.Stop();

            _logger.LogInformation("SaveConfigAsync: response {Status} in {Ms}ms", response.StatusCode, sw.ElapsedMilliseconds);
            response.EnsureSuccessStatusCode();
        });

        _cachedConfig = updatedConfig;
        _logger.LogInformation("Configuration saved via Runner API");
    }

    public async Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var request = new { Token = token, RepoFullName = repoFullName };
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/configuration/validate-pat", request, ct);

            if (!response.IsSuccessStatusCode)
                return new PatValidationResult { Success = false, Error = $"Runner returned {response.StatusCode}" };

            return await response.Content.ReadFromJsonAsync<PatValidationResult>(JsonOptions, ct)
                ?? new PatValidationResult { Success = false, Error = "Empty response from Runner" };
        }, ct);
    }

    public async Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var client = CreateClient();
            var response = await client.GetAsync("/api/configuration/cleanup/scan", ct);

            if (!response.IsSuccessStatusCode)
                return new CleanupSummary { Error = $"Runner returned {response.StatusCode}" };

            return await response.Content.ReadFromJsonAsync<CleanupSummary>(JsonOptions, ct)
                ?? new CleanupSummary { Error = "Empty response from Runner" };
        }, ct);
    }

    public async Task<CleanupResult> ExecuteCleanupAsync(string? caveats, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var request = new { Caveats = caveats };
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/configuration/cleanup/execute", request, ct);

            if (!response.IsSuccessStatusCode)
                return new CleanupResult { Success = false, Phase = "Error", Errors = [$"Runner returned {response.StatusCode}"] };

            return await response.Content.ReadFromJsonAsync<CleanupResult>(JsonOptions, ct)
                ?? new CleanupResult { Success = false, Phase = "Error", Errors = ["Empty response from Runner"] };
        }, ct);
    }
}
