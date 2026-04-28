using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Shared HTTP client base for all ADO REST API services.
/// Handles authentication (PAT or bearer), retry with exponential backoff,
/// rate limiting (429 response handling), and JSON serialization.
/// </summary>
public class AdoHttpClientBase : IDisposable
{
    private readonly HttpClient _http;
    private readonly IDevPlatformAuthProvider _authProvider;
    private readonly DevPlatformConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _rateLimiter = new(10, 10);
    private bool _disposed;

    // ADO rate tracking
    private int _remaining = 200;
    private long _totalCalls;
    private DateTime _windowResetUtc = DateTime.UtcNow.AddMinutes(1);
    private bool _isRateLimited;

    public int Remaining => _remaining;
    public long TotalCalls => Interlocked.Read(ref _totalCalls);
    public bool IsRateLimited => _isRateLimited;
    public DateTime WindowResetUtc => _windowResetUtc;

    // ADO API version to use across all requests
    private const string ApiVersion = "7.1";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public AdoHttpClientBase(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<AgentSquad.Core.Configuration.AgentSquadConfig> config,
        ILogger logger)
    {
        _http = http;
        _authProvider = authProvider;
        _config = config.Value.DevPlatform ?? new DevPlatformConfig();
        _logger = logger;

        var adoConfig = _config.AzureDevOps;
        if (adoConfig is not null && !string.IsNullOrEmpty(adoConfig.Organization))
        {
            _http.BaseAddress = new Uri($"https://dev.azure.com/{adoConfig.Organization}/");
        }
    }

    /// <summary>Base URL for the configured ADO organization.</summary>
    public string BaseUrl => _http.BaseAddress?.ToString() ?? "https://dev.azure.com/unknown/";

    /// <summary>The configured ADO project name.</summary>
    public string Project => _config.AzureDevOps?.Project ?? "";

    /// <summary>The configured ADO repository name.</summary>
    public string Repository => _config.AzureDevOps?.Repository ?? "";

    /// <summary>The configured ADO organization.</summary>
    public string Organization => _config.AzureDevOps?.Organization ?? "";

    /// <summary>Build an API URL with the standard api-version query parameter.</summary>
    protected string BuildUrl(string path, string? extraQuery = null)
    {
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{path}{separator}api-version={ApiVersion}";
        if (!string.IsNullOrEmpty(extraQuery))
            url += $"&{extraQuery}";
        return url;
    }

    /// <summary>Build an API URL using a preview API version (e.g., "7.1-preview").</summary>
    protected string BuildPreviewUrl(string path, string? extraQuery = null)
    {
        var separator = path.Contains('?') ? "&" : "?";
        var url = $"{path}{separator}api-version={ApiVersion}-preview";
        if (!string.IsNullOrEmpty(extraQuery))
            url += $"&{extraQuery}";
        return url;
    }

    /// <summary>Send a GET request with authentication and retry logic.</summary>
    protected async Task<T?> GetAsync<T>(string url, CancellationToken ct = default, bool suppressNotFound = false)
    {
        var response = await SendWithRetryAsync(HttpMethod.Get, url, null, ct);
        if (suppressNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        await EnsureSuccessOrLogBodyAsync(response, url);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>Send a POST request with a JSON body.</summary>
    protected async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct = default)
    {
        var content = JsonContent.Create(body, options: JsonOptions);
        var response = await SendWithRetryAsync(HttpMethod.Post, url, content, ct);
        await EnsureSuccessOrLogBodyAsync(response, url);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>Send a POST request with a JSON body, no response body expected.</summary>
    protected async Task PostAsync(string url, object body, CancellationToken ct = default)
    {
        var content = JsonContent.Create(body, options: JsonOptions);
        var response = await SendWithRetryAsync(HttpMethod.Post, url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Send a PATCH request with a JSON body.</summary>
    protected async Task<T?> PatchAsync<T>(string url, object body, CancellationToken ct = default,
        string contentType = "application/json")
    {
        HttpContent content;
        if (contentType == "application/json-patch+json")
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            content = new StringContent(json, Encoding.UTF8, contentType);
        }
        else
        {
            content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await SendWithRetryAsync(HttpMethod.Patch, url, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>Send a DELETE request.</summary>
    protected async Task DeleteAsync(string url, CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync(HttpMethod.Delete, url, null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Send a PUT request with a JSON body.</summary>
    protected async Task<T?> PutAsync<T>(string url, object body, CancellationToken ct = default)
    {
        var content = JsonContent.Create(body, options: JsonOptions);
        var response = await SendWithRetryAsync(HttpMethod.Put, url, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>Send raw content (for binary files).</summary>
    protected async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string url, HttpContent content, CancellationToken ct = default)
    {
        return await SendWithRetryAsync(method, url, content, ct);
    }

    /// <summary>Core send method with authentication, rate limiting, and retry.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int maxRetries = 3;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            await _rateLimiter.WaitAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (content is not null)
                {
                    // Clone content for retries — HttpContent can only be sent once
                    if (attempt > 0 && content is JsonContent)
                    {
                        var json = await content.ReadAsStringAsync(ct);
                        request.Content = new StringContent(json, Encoding.UTF8, content.Headers.ContentType?.MediaType ?? "application/json");
                    }
                    else
                    {
                        request.Content = content;
                    }
                }

                // Apply auth header
                var token = await _authProvider.GetTokenAsync(ct);
                if (_config.AuthMethod == DevPlatformAuthMethod.Pat)
                {
                    var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                Interlocked.Increment(ref _totalCalls);
                var response = await _http.SendAsync(request, ct);

                // Track rate limit headers (ADO uses Retry-After + X-RateLimit-* headers)
                UpdateRateLimitFromResponse(response);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _isRateLimited = true;
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    _logger.LogWarning("ADO rate limited (429). Retry after {Seconds}s (attempt {Attempt}/{Max})",
                        retryAfter.TotalSeconds, attempt + 1, maxRetries + 1);

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryAfter, ct);
                        continue;
                    }
                }

                if (response.StatusCode >= HttpStatusCode.InternalServerError && attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning("ADO server error {Status}. Retrying in {Delay}s (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, delay.TotalSeconds, attempt + 1, maxRetries + 1);
                    await Task.Delay(delay, ct);
                    continue;
                }

                _isRateLimited = false;
                return response;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        throw new HttpRequestException($"ADO API request failed after {maxRetries + 1} attempts: {method} {url}");
    }

    private void UpdateRateLimitFromResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            if (int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
                _remaining = remaining;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            if (long.TryParse(resetValues.FirstOrDefault(), out var resetEpoch))
                _windowResetUtc = DateTimeOffset.FromUnixTimeSeconds(resetEpoch).UtcDateTime;
        }
    }

    /// <summary>Log the response body on non-success status codes for diagnostics, then throw.</summary>
    private async Task EnsureSuccessOrLogBodyAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;

        var body = "(empty)";
        try { body = await response.Content.ReadAsStringAsync(); } catch { /* best effort */ }
        _logger.LogError("ADO API {Status} for {Url}: {Body}",
            (int)response.StatusCode, url, body.Length > 2000 ? body[..2000] : body);
        response.EnsureSuccessStatusCode(); // still throws
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rateLimiter.Dispose();
        _http.Dispose();
    }
}
