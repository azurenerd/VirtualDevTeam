using System.Net.Http.Json;
using System.Text.Json;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// HTTP-proxy implementation of <see cref="IConfigurationService"/> for standalone dashboard mode.
/// Forwards all configuration operations to the Runner's REST API.
/// </summary>
public sealed class HttpConfigurationService : IConfigurationService
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpConfigurationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HttpConfigurationService(HttpClient http, ILogger<HttpConfigurationService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public AgentSquadConfig GetCurrentConfig()
    {
        // Synchronous call — fetch cached config from Runner
        try
        {
            var response = _http.GetAsync("/api/configuration/current").GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var config = response.Content.ReadFromJsonAsync<AgentSquadConfig>(JsonOptions).GetAwaiter().GetResult();
            return config ?? new AgentSquadConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch configuration from Runner — returning defaults");
            return new AgentSquadConfig();
        }
    }

    public async Task SaveConfigAsync(AgentSquadConfig updatedConfig)
    {
        var response = await _http.PostAsJsonAsync("/api/configuration/save", updatedConfig);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Configuration saved via Runner API");
    }

    public async Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default)
    {
        var request = new { Token = token, RepoFullName = repoFullName };
        var response = await _http.PostAsJsonAsync("/api/configuration/validate-pat", request, ct);

        if (!response.IsSuccessStatusCode)
            return new PatValidationResult { Success = false, Error = $"Runner returned {response.StatusCode}" };

        return await response.Content.ReadFromJsonAsync<PatValidationResult>(JsonOptions, ct)
            ?? new PatValidationResult { Success = false, Error = "Empty response from Runner" };
    }

    public async Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/api/configuration/cleanup/scan", ct);

        if (!response.IsSuccessStatusCode)
            return new CleanupSummary { Error = $"Runner returned {response.StatusCode}" };

        return await response.Content.ReadFromJsonAsync<CleanupSummary>(JsonOptions, ct)
            ?? new CleanupSummary { Error = "Empty response from Runner" };
    }

    public async Task<CleanupResult> ExecuteCleanupAsync(string? caveats, CancellationToken ct = default)
    {
        var request = new { Caveats = caveats };
        var response = await _http.PostAsJsonAsync("/api/configuration/cleanup/execute", request, ct);

        if (!response.IsSuccessStatusCode)
            return new CleanupResult { Success = false, Phase = "Error", Errors = [$"Runner returned {response.StatusCode}"] };

        return await response.Content.ReadFromJsonAsync<CleanupResult>(JsonOptions, ct)
            ?? new CleanupResult { Success = false, Phase = "Error", Errors = ["Empty response from Runner"] };
    }
}
