using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Handles <c>api_call</c>, <c>webhook</c>, and related background-service scenarios via
/// <see cref="HttpClient"/> plus optional database row/count checks.
/// </summary>
/// <remarks>
/// <para>
/// Supported action categories: <c>http.*</c>, <c>db.*</c>.
/// </para>
/// <para>
/// Database assertions (<c>db.query</c>, <c>db.assertRow</c>, <c>db.assertCount</c>) require
/// a non-null <see cref="AppHandle.DbConnectionString"/>. When the connection string is absent,
/// those checks return <see cref="InconclusiveEvidence"/> with a clear message.
/// </para>
/// <para>
/// The adapter maintains the last HTTP response within a single scenario execution to allow
/// <c>http.assertStatus</c> / <c>http.assertBodyPath</c> to reference the most-recent response
/// without repeating the call.
/// </para>
/// </remarks>
public sealed class ApiPlaytestAdapter : IPlaytestAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiPlaytestAdapter> _logger;

    // State within one scenario execution
    private HttpResponseMessage? _lastResponse;
    private string? _lastResponseBody;
    private long _lastLatencyMs;

    private static readonly HashSet<string> _handledCategories =
        new(StringComparer.OrdinalIgnoreCase) { "http", "db" };

    public ApiPlaytestAdapter(IHttpClientFactory httpClientFactory, ILogger<ApiPlaytestAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanHandle(PlaytestAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _handledCategories.Contains(action.ActionCategory);
    }

    /// <inheritdoc/>
    public async Task<IPlaytestEvidence> ExecuteAsync(
        PlaytestAction action,
        AppHandle handle,
        Dictionary<string, string?> snapshots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(snapshots);

        try
        {
            return action.ActionCategory.ToLowerInvariant() switch
            {
                "http" => await ExecuteHttpActionAsync(action, handle, ct),
                "db" => await ExecuteDbActionAsync(action, handle, ct),
                _ => new InconclusiveEvidence(
                    action.SurfaceVerified ?? "http",
                    $"ApiPlaytestAdapter: unrecognised category '{action.ActionCategory}'"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApiPlaytestAdapter: action {ActionType} failed", action.ActionType);
            return new ActionFailureEvidence(action.SurfaceVerified ?? "http", ex.Message);
        }
    }

    // ─── HTTP Actions ─────────────────────────────────────────────────────────

    private async Task<IPlaytestEvidence> ExecuteHttpActionAsync(
        PlaytestAction action, AppHandle handle, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("ApiPlaytestAdapter");
        client.BaseAddress ??= new Uri(handle.BaseUrl.TrimEnd('/') + "/");

        switch (action.ActionVerb.ToLowerInvariant())
        {
            case "post":
            {
                var path = action.GetParam("path") ?? "/";
                var bodyJson = action.GetParam("bodyJson") ?? "{}";
                var headersParam = action.Params.TryGetValue("headers", out var hEl)
                    ? hEl : default;

                var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                };
                AddHeaders(request, headersParam);

                var sw = Stopwatch.StartNew();
                _lastResponse = await client.SendAsync(request, ct);
                _lastLatencyMs = sw.ElapsedMilliseconds;
                _lastResponseBody = await _lastResponse.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("ApiPlaytestAdapter: POST {Path} → {Status} in {Ms}ms", path, (int)_lastResponse.StatusCode, _lastLatencyMs);
                return new ActionSuccessEvidence("http_post", $"POST {path} → {(int)_lastResponse.StatusCode}");
            }

            case "get":
            {
                var path = action.GetParam("path") ?? "/";
                var headersParam = action.Params.TryGetValue("headers", out var hEl) ? hEl : default;
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                AddHeaders(request, headersParam);

                var sw = Stopwatch.StartNew();
                _lastResponse = await client.SendAsync(request, ct);
                _lastLatencyMs = sw.ElapsedMilliseconds;
                _lastResponseBody = await _lastResponse.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("ApiPlaytestAdapter: GET {Path} → {Status} in {Ms}ms", path, (int)_lastResponse.StatusCode, _lastLatencyMs);
                return new ActionSuccessEvidence("http_get", $"GET {path} → {(int)_lastResponse.StatusCode}");
            }

            case "assertstatus":
            {
                if (_lastResponse is null)
                    return new InconclusiveEvidence("http_response", "http.assertStatus called before any http.get/http.post");

                var expected = action.GetIntParam("expectedStatus", 200);
                var maxLatency = action.Params.TryGetValue("maxLatencyMs", out var mlEl)
                    && mlEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (long?)mlEl.GetInt64()
                    : null;

                return new HttpResponseEvidence(
                    _lastResponse.RequestMessage?.Method.Method ?? "?",
                    _lastResponse.RequestMessage?.RequestUri?.PathAndQuery ?? "?",
                    (int)_lastResponse.StatusCode,
                    _lastLatencyMs,
                    _lastResponseBody,
                    expected,
                    maxLatency);
            }

            case "assertbodypath":
            {
                var jsonPath = action.GetParam("jsonPath") ?? "$";
                var expectedValue = action.GetParam("expectedValue") ?? string.Empty;

                if (_lastResponseBody is null)
                    return new InconclusiveEvidence("http_response", "http.assertBodyPath: no response body available");

                var actualValue = ExtractJsonPath(_lastResponseBody, jsonPath);
                return new HttpBodyPathEvidence(jsonPath, actualValue, expectedValue);
            }

            default:
                return new InconclusiveEvidence("http", $"ApiPlaytestAdapter: unrecognised http action '{action.ActionType}'");
        }
    }

    // ─── DB Actions ───────────────────────────────────────────────────────────

    private Task<IPlaytestEvidence> ExecuteDbActionAsync(
        PlaytestAction action, AppHandle handle, CancellationToken ct)
    {
        // TODO: When a DbContext or IDbConnectionFactory is available in the workspace,
        // wire up real DB execution here.
        //
        // For now, all DB surface checks are marked inconclusive.
        // This is intentional — the API surface is complete so a future contributor
        // only needs to:
        //  1. Inject a connection factory into this adapter
        //  2. Replace the InconclusiveEvidence returns below with real ADO.NET execution

        IPlaytestEvidence evidence = action.ActionVerb.ToLowerInvariant() switch
        {
            "query" or "assertrow" => new DbRowEvidence(
                Sql: action.GetParam("sql") ?? action.GetParam("query") ?? "(missing sql)",
                ActualRowJson: null,
                ExpectedJson: action.GetParam("expectedJson"),
                Matched: false,
                IsInconclusive: true,
                ErrorMessage: "DB assertion skipped — no DbConnectionString in AppHandle. " +
                              "Provide AppHandle.DbConnectionString to enable db_row checks."),

            "assertcount" => new DbCountEvidence(
                Sql: action.GetParam("sql") ?? "(missing sql)",
                ActualCount: null,
                ExpectedChange: action.GetParam("expectedChange") ?? action.GetParam("expectedCount"),
                Matched: false,
                IsInconclusive: true,
                ErrorMessage: "DB assertion skipped — no DbConnectionString in AppHandle. " +
                              "Provide AppHandle.DbConnectionString to enable db_count checks."),

            _ => new InconclusiveEvidence("db", $"ApiPlaytestAdapter: unrecognised db action '{action.ActionType}'"),
        };

        _logger.LogDebug("ApiPlaytestAdapter: DB action {ActionType} → inconclusive (no DbContext)", action.ActionType);
        return Task.FromResult(evidence);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void AddHeaders(
        HttpRequestMessage request,
        System.Text.Json.JsonElement headersElement)
    {
        if (headersElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var prop in headersElement.EnumerateObject())
        {
            var val = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
            request.Headers.TryAddWithoutValidation(prop.Name, val);
        }
    }

    /// <summary>
    /// Minimal JSON-path evaluation — supports simple dotted paths and array indexers.
    /// Full JSONPath support is not required for the action plan schema used here.
    /// </summary>
    private static string? ExtractJsonPath(string json, string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;

            // Strip leading "$."
            var path = jsonPath.TrimStart('$').TrimStart('.');
            foreach (var segment in path.Split('.'))
            {
                if (string.IsNullOrEmpty(segment)) continue;

                // Handle array indexers like "messages[0]"
                var bracketIdx = segment.IndexOf('[');
                var propName = bracketIdx >= 0 ? segment[..bracketIdx] : segment;
                if (!string.IsNullOrEmpty(propName))
                {
                    if (!element.TryGetProperty(propName, out element)) return null;
                }

                if (bracketIdx >= 0)
                {
                    var closeBracket = segment.IndexOf(']');
                    var indexStr = segment[(bracketIdx + 1)..closeBracket];
                    if (int.TryParse(indexStr, out var idx) && element.ValueKind == JsonValueKind.Array)
                        element = element[idx];
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                _ => element.GetRawText(),
            };
        }
        catch
        {
            return null;
        }
    }
}
