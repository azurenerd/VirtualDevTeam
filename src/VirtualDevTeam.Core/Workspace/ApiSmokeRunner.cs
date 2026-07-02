using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Boots the target app, discovers its OpenAPI document, and probes every declared
/// GET endpoint to verify the app starts without 5xx errors. Used as a gate before
/// marking PRs as tests-added.
/// Extracted from PlaywrightRunner to separate API testing concerns.
/// </summary>
public sealed class ApiSmokeRunner
{
    private readonly ILogger<ApiSmokeRunner> _logger;
    private readonly AppLauncher _appLauncher;

    public ApiSmokeRunner(
        ILogger<ApiSmokeRunner> logger,
        AppLauncher appLauncher)
    {
        _logger = logger;
        _appLauncher = appLauncher;
    }

    /// <summary>
    /// Boots the app, discovers its OpenAPI document, and probes every declared GET
    /// endpoint. Returns <see cref="ApiSmokeOutcome.Inconclusive"/> (not a failure)
    /// when the smoke isn't applicable (no <c>AppStartCommand</c>, app failed to start, no
    /// OpenAPI document found).
    ///
    /// post-run-target-app-smoke-test (2026-05-11): TE used to declare <c>tests-added</c>
    /// after unit + integration tests passed, without ever booting the real app. The
    /// 2026-05-11 GridGuardians run shipped a backend that 500'd on every <c>/api/config/*</c>
    /// endpoint thanks to a SQLite UNIQUE seed conflict, but TE didn't notice because the
    /// xUnit tests ran in-process with their own (correctly-seeded) test fixture. This
    /// smoke phase boots the EXACT app the engineer would deploy and probes it through
    /// the real network surface, catching seed-data conflicts, missing config, broken
    /// startup wiring, etc. before the PR moves to PM review.
    /// </summary>
    public async Task<ApiSmokeResult> RunApiSmokeTestAsync(
        string workspacePath, WorkspaceConfig config, CancellationToken ct = default)
    {
        AppLaunchResult? launchResult = null;
        var originalCommand = config.AppStartCommand;
        var probes = new List<ApiEndpointProbe>();
        try
        {
            // Auto-detect AppStartCommand when not explicitly configured.
            if (string.IsNullOrWhiteSpace(config.AppStartCommand))
            {
                var detected = _appLauncher.DetectAppStartCommand(workspacePath);
                if (detected is null)
                {
                    _logger.LogDebug("ApiSmoke: no AppStartCommand and detection failed — Inconclusive");
                    return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes, "No app start command available");
                }
                config.AppStartCommand = detected;
                _logger.LogInformation("ApiSmoke: detected AppStartCommand: {Command}", detected);
            }

            _appLauncher.EnsureSampleDataExists(workspacePath);
            await _appLauncher.RestoreDependenciesAsync(workspacePath, ct);

            var envVars = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["Logging__Console__LogLevel__Microsoft.Hosting.Lifetime"] = "Information",
                // We intentionally DON'T set DISABLE_AUTH here — for a smoke we want the auth
                // surface to be exercised normally. Endpoints that legitimately need a token
                // will return 401 (which we treat as non-failure: only 5xx is a hard block).
            };

            launchResult = await _appLauncher.LaunchVerifiedAppAsync(workspacePath, config, envVars, ct);
            if (launchResult is null)
            {
                _logger.LogDebug("ApiSmoke: app failed to launch — Inconclusive");
                return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes, "App failed to start (not a web project)");
            }

            var baseUri = new Uri(launchResult.VerifiedUrl);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VirtualDevTeam-ApiSmoke/1.0");

            // ── Step 1: Discover OpenAPI document ──
            var (openApiText, openApiUrl) = await TryFetchOpenApiDocumentAsync(baseUri, http, ct);
            if (openApiText is null)
            {
                _logger.LogInformation("ApiSmoke: no OpenAPI document found at well-known paths — Inconclusive");
                return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes,
                    "No /swagger/v1/swagger.json, /openapi/v1.json, or /openapi.json available");
            }

            // ── Step 2: Extract GET endpoints ──
            List<string> getPaths;
            try { getPaths = ExtractOpenApiGetPaths(openApiText); }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "ApiSmoke: failed to parse OpenAPI doc at {Url} — Inconclusive", openApiUrl);
                return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes, $"OpenAPI parse failed: {parseEx.Message}");
            }

            if (getPaths.Count == 0)
            {
                _logger.LogInformation("ApiSmoke: OpenAPI doc had 0 GET endpoints — Inconclusive");
                return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes, "No GET endpoints declared");
            }

            // Cap the probe set so a sprawling API surface doesn't drag out the gate.
            const int MaxProbes = 30;
            if (getPaths.Count > MaxProbes)
            {
                _logger.LogInformation("ApiSmoke: capping {Total} GET endpoints to first {Cap} for probe", getPaths.Count, MaxProbes);
                getPaths = getPaths.Take(MaxProbes).ToList();
            }

            // ── Step 3: Probe each endpoint ──
            foreach (var path in getPaths)
            {
                ct.ThrowIfCancellationRequested();
                var probeUrl = SubstituteOpenApiPathTemplates(baseUri, path);
                int status = -1;
                string? bodySnippet = null;
                try
                {
                    using var resp = await http.GetAsync(probeUrl, ct);
                    status = (int)resp.StatusCode;
                    if (status >= 500)
                    {
                        try
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            bodySnippet = body.Length > 500 ? body[..500] + "…" : body;
                        }
                        catch { /* body read is best-effort */ }
                    }
                }
                catch (Exception probeEx)
                {
                    bodySnippet = $"{probeEx.GetType().Name}: {probeEx.Message}";
                }
                probes.Add(new ApiEndpointProbe("GET", probeUrl.ToString(), status, bodySnippet));
            }

            var failures = probes.Where(p => p.StatusCode is < 0 or >= 500).ToList();
            if (failures.Count > 0)
            {
                _logger.LogWarning(
                    "ApiSmoke: {Failed}/{Total} endpoints failed — first failure: GET {Url} → {Status}",
                    failures.Count, probes.Count, failures[0].Url, failures[0].StatusCode);
                return new ApiSmokeResult(ApiSmokeOutcome.Fail, probes,
                    $"{failures.Count} of {probes.Count} GET endpoints returned 5xx or threw");
            }

            _logger.LogInformation(
                "ApiSmoke: all {Total} GET endpoints returned non-5xx ({Url})",
                probes.Count, openApiUrl);
            return new ApiSmokeResult(ApiSmokeOutcome.Pass, probes, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApiSmoke: probe phase threw — Inconclusive");
            return new ApiSmokeResult(ApiSmokeOutcome.Inconclusive, probes, $"Probe phase exception: {ex.Message}");
        }
        finally
        {
            config.AppStartCommand = originalCommand;
            if (launchResult is not null)
            {
                try
                {
                    if (!launchResult.Process.HasExited)
                        launchResult.Process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger.LogDebug(killEx, "ApiSmoke: failed to kill app process (non-fatal)");
                }
                try { launchResult.Process.Dispose(); } catch { }
            }
        }
    }

    private static async Task<(string? Json, string? Url)> TryFetchOpenApiDocumentAsync(
        Uri baseUri, HttpClient http, CancellationToken ct)
    {
        // Try ASP.NET Core's defaults first, then community conventions.
        var candidates = new[]
        {
            "/swagger/v1/swagger.json",
            "/openapi/v1.json",
            "/openapi.json",
            "/api/swagger/v1/swagger.json",
            "/v3/api-docs",       // springdoc default — included for cross-stack support
        };
        foreach (var path in candidates)
        {
            var url = new Uri(baseUri, path);
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(body)) continue;
                // Sanity check it's actually JSON containing a "paths" or "openapi"/"swagger" key.
                if (!(body.Contains("\"paths\"", StringComparison.OrdinalIgnoreCase) ||
                      body.Contains("\"openapi\"", StringComparison.OrdinalIgnoreCase) ||
                      body.Contains("\"swagger\"", StringComparison.OrdinalIgnoreCase)))
                    continue;
                return (body, url.ToString());
            }
            catch
            {
                // Network/timeout — try the next candidate
            }
        }
        return (null, null);
    }

    internal static List<string> ExtractOpenApiGetPaths(string openApiJson)
    {
        var paths = new List<string>();
        using var doc = JsonDocument.Parse(openApiJson);
        if (!doc.RootElement.TryGetProperty("paths", out var pathsEl) || pathsEl.ValueKind != JsonValueKind.Object)
            return paths;
        foreach (var path in pathsEl.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var verb in path.Value.EnumerateObject())
            {
                if (string.Equals(verb.Name, "get", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path.Name);
                    break;
                }
            }
        }
        return paths;
    }

    internal static Uri SubstituteOpenApiPathTemplates(Uri baseUri, string pathTemplate)
    {
        // OpenAPI uses {paramName} placeholders. Substitute with "1" — a safe sample
        // for numeric IDs, slugs, and most enums. The goal isn't a functional probe;
        // it's a "does the route handler even start without throwing".
        var substituted = System.Text.RegularExpressions.Regex.Replace(pathTemplate, @"\{[^/}]+\}", "1");
        if (!substituted.StartsWith('/')) substituted = "/" + substituted;
        return new Uri(baseUri, substituted);
    }
}
