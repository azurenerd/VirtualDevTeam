using System.Net.Http.Json;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Dashboard.Services;

public sealed class HttpStrategiesDataService : IStrategiesDataService
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpStrategiesDataService> _logger;

    public HttpStrategiesDataService(HttpClient http, ILogger<HttpStrategiesDataService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskSnapshot>> GetActiveTasksAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<TaskSnapshot>>("/api/strategies/active", ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /api/strategies/active failed");
            return [];
        }
    }

    public async Task<IReadOnlyList<TaskSnapshot>> GetRecentTasksAsync(int limit = 50, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<TaskSnapshot>>($"/api/strategies/recent?limit={limit}", ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /api/strategies/recent failed");
            return [];
        }
    }

    public async Task<EnabledStrategiesInfo> GetEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<EnabledStrategiesInfo>("/api/strategies/enabled", ct).ConfigureAwait(false);
            return result ?? new EnabledStrategiesInfo(false, []);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /api/strategies/enabled failed");
            return new EnabledStrategiesInfo(false, []);
        }
    }

    public int ActiveCount => 0;

    public event Action? OnActiveCountChanged { add { } remove { } }

    public async Task<bool> CancelOrchestrationAsync(string runId, string taskId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/api/strategies/cancel/{runId}/{taskId}", null, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST /api/strategies/cancel failed");
            return false;
        }
    }

    public async Task<bool> CancelCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/api/strategies/cancel/{runId}/{taskId}/{strategyId}", null, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST /api/strategies/cancel candidate failed");
            return false;
        }
    }

    public async Task<bool> ResetCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync($"/api/strategies/reset/{runId}/{taskId}/{strategyId}", null, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST /api/strategies/reset candidate failed");
            return false;
        }
    }
}
