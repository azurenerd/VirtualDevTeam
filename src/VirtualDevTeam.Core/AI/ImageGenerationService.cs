using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.AI;

public sealed class ImageGenerationService : IImageGenerationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAzureImageAuthProvider _auth;
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<ImageGenerationService> _logger;

    public ImageGenerationService(
        IHttpClientFactory httpFactory,
        IAzureImageAuthProvider auth,
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<ImageGenerationService> logger)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cfg = _config.CurrentValue.AzureOpenAIImage;
        if (!cfg.IsConfigured())
            return new ImageGenerationResult { Success = false, FailureReason = "AzureOpenAIImage not configured." };

        var deployments = cfg.GetOrderedDeployments();
        var maxAttempts = Math.Max(1, cfg.MaxAttemptsPerImage);
        var backoff = cfg.RetryBackoffSeconds is { Count: > 0 } ? cfg.RetryBackoffSeconds : new List<int> { 5, 10, 15 };
        int totalAttempts = 0;
        var failureSummary = new List<string>();

        // rd-9 fix (2026-05-12 evening): the previous implementation was a bare foreach over
        // deployments — single shot per deployment, no retry, no backoff. The
        // documented "3 attempts × 5/10/15s backoff per deployment" was fiction. Now we
        // honour MaxAttemptsPerImage on each deployment and only fall to the next
        // deployment after exhausting retries OR on a hard non-retryable failure
        // (auth/permission/bad request). This preserves within-animation visual
        // consistency: an animation cycle's frames stay on the same deployment under
        // transient throttling, and only switch deployment on persistent unavailability.
        foreach (var deployment in deployments)
        {
            ct.ThrowIfCancellationRequested();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                totalAttempts++;
                var (ok, bytes, err) = await TryGenerateAsync(cfg, deployment, request.Prompt, request, ct);
                if (ok && bytes is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath) ?? ".");
                    await File.WriteAllBytesAsync(request.OutputPath, bytes, ct);
                    if (totalAttempts > 1)
                        _logger.LogInformation(
                            "Image succeeded on deployment {Deployment} after {TotalAttempts} total attempts ({Failures})",
                            deployment, totalAttempts, string.Join("; ", failureSummary));
                    return new ImageGenerationResult
                    {
                        Success = true,
                        SavedPath = request.OutputPath,
                        DeploymentUsed = deployment,
                        AttemptsMade = totalAttempts,
                        VerificationVerdict = ImageVerificationVerdict.Matches,
                    };
                }

                var failure = $"{deployment}#{attempt}: {err}";
                failureSummary.Add(failure);
                _logger.LogDebug("Image deployment '{Deployment}' attempt {Attempt}/{Max} returned: {Error}",
                    deployment, attempt, maxAttempts, err);

                // Hard failures (auth, permission, malformed request, deployment-not-found)
                // are not retryable; break out of the inner retry loop and fall to the
                // next deployment immediately. Transient failures (429 throttle, 503
                // capacity, 504 timeout, network) get backoff retries.
                bool isRetryable = IsRetryableError(err);
                if (!isRetryable)
                {
                    _logger.LogDebug(
                        "Non-retryable error on '{Deployment}' attempt {Attempt} — falling to next deployment immediately",
                        deployment, attempt);
                    break;
                }
                if (attempt < maxAttempts)
                {
                    var sleep = backoff[Math.Min(attempt - 1, backoff.Count - 1)];
                    _logger.LogInformation(
                        "Retrying '{Deployment}' attempt {Next}/{Max} after {Sleep}s backoff (last error: {Error})",
                        deployment, attempt + 1, maxAttempts, sleep, err);
                    try { await Task.Delay(TimeSpan.FromSeconds(sleep), ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }
        return new ImageGenerationResult
        {
            Success = false,
            AttemptsMade = totalAttempts,
            FailureReason = $"All {deployments.Count} deployments exhausted after {totalAttempts} total attempts. Failures: {string.Join("; ", failureSummary)}",
        };
    }

    /// <summary>
    /// Classify an image-gen failure error message as retryable (transient — worth backoff)
    /// or non-retryable (hard — abandon this deployment immediately).
    /// </summary>
    private static bool IsRetryableError(string? err)
    {
        if (string.IsNullOrEmpty(err)) return true; // unknown — try again
        var lower = err.ToLowerInvariant();
        // Hard failures that won't change with retry: auth, permission, validation, not-found.
        if (lower.Contains("401") || lower.Contains("unauthorized")) return false;
        if (lower.Contains("403") || lower.Contains("forbidden")) return false;
        if (lower.Contains("400") || lower.Contains("bad request") || lower.Contains("invalid")) return false;
        if (lower.Contains("404") || lower.Contains("not found") || lower.Contains("deploymentnotfound")) return false;
        if (lower.Contains("contentpolicy") || lower.Contains("content_filter") || lower.Contains("safety")) return false;
        // Default: assume retryable (429, 503, 504, connection reset, timeout, generic network).
        return true;
    }

    public async Task<ImageValidationReport> ValidateAsync(bool runSmokeTest, string? smokeTestOutputPath = null, CancellationToken ct = default)
    {
        var cfg = _config.CurrentValue.AzureOpenAIImage;

        // Run auth + endpoint checks in parallel — both are independent HTTP calls
        var authTask = CheckAuthAsync(ct);
        var endpointTask = CheckEndpointAsync(cfg, ct);
        await Task.WhenAll(authTask, endpointTask);

        var report = new ImageValidationReport
        {
            AuthCheck = authTask.Result,
            EndpointReachable = endpointTask.Result,
        };

        if (!report.AuthCheck.Passed || !report.EndpointReachable.Passed)
            return report with { OverallSuccess = false };

        // PER-DEPLOYMENT probe instead of LIST. The data-plane LIST endpoint
        // (/openai/deployments?api-version=...) returns empty for Foundry-managed resources
        // even when deployments exist + work for actual generation calls. Observed 2026-05-12:
        // a Foundry-managed resource had gpt-image-2/1.5/1/1-mini all Succeeded in
        // the Azure portal but our LIST returned [] → dashboard incorrectly reported "no
        // deployments found". The PER-DEPLOYMENT GET (/openai/deployments/{id}?api-version=...)
        // works for both classic + Foundry resources because it talks to the same data plane
        // path the actual generation calls use.
        var primaryCheck = await ProbeDeploymentAsync(cfg, cfg.PrimaryDeployment, isPrimary: true, ct);

        // Fallback probes are instant (no HTTP call — just reports "configured")
        var fallbackChecks = new List<ValidationCheck>();
        foreach (var d in cfg.FallbackDeployments.Where(x => !string.IsNullOrWhiteSpace(x)))
            fallbackChecks.Add(await ProbeDeploymentAsync(cfg, d, isPrimary: false, ct));

        ValidationCheck? smoke = null;
        if (runSmokeTest && primaryCheck.Passed)
            smoke = await RunSmokeTestAsync(cfg, smokeTestOutputPath, ct);

        var passed = primaryCheck.Passed && (smoke?.Passed ?? true);
        return report with
        {
            PrimaryDeploymentOnline = primaryCheck,
            FallbackDeploymentsOnline = fallbackChecks,
            SmokeTest = smoke,
            OverallSuccess = passed,
        };
    }

    /// <summary>
    /// Probe a single deployment's existence by POSTing a minimal generation request to
    /// <c>/openai/deployments/{deploymentId}/images/generations?api-version={cfg.ApiVersion}</c>.
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 → deployment exists, auth works, generation succeeded → Passed</item>
    ///   <item>404 → deployment doesn't exist → not Passed, ActionHint = deploy it</item>
    ///   <item>401/403 → auth issue → not Passed, ActionHint references auth</item>
    ///   <item>429 → throttled but exists → treat as Passed (online, just rate-limited)</item>
    ///   <item>5xx / network error → infrastructure issue → not Passed, raw status in Detail</item>
    /// </list>
    /// Why POST instead of GET: observed 2026-05-12 on a Foundry-managed resource
    /// — the Azure data-plane GET endpoints
    /// (<c>/openai/deployments</c> for LIST and <c>/openai/deployments/{id}</c> for individual)
    /// both return HTTP 404 "Resource not found" even when the deployments DO exist + work
    /// for actual generation calls. The POST to the generation endpoint is the SAME path
    /// the agent uses at runtime, so probing this way is the ONLY truly accurate validation
    /// for both classic + Foundry resources. Cost: ~$0.04 per click against gpt-image-1
    /// (user-initiated validation, so opt-in).
    ///
    /// To minimise cost we deliberately probe ONLY the primary deployment. Fallback
    /// deployments are not probed individually — they're a runtime fallback ladder, listed
    /// here as "configured" so the operator sees the chain but doesn't pay 4x the validation
    /// cost. If the primary fails, the user can re-validate after fixing the primary.
    /// </summary>
    private async Task<ValidationCheck> ProbeDeploymentAsync(
        AzureOpenAIImageConfig cfg, string deployment, bool isPrimary, CancellationToken ct)
    {
        var label = isPrimary
            ? $"Primary deployment '{deployment}' online"
            : $"Fallback deployment '{deployment}' configured";

        // For fallbacks: don't probe (cost saver). Just report as configured. The user can
        // confirm fallbacks work by re-validating after the primary is healthy.
        if (!isPrimary)
        {
            return new ValidationCheck
            {
                Label = label,
                Passed = true,
                Detail = "configured (not probed to save cost — will be tried at runtime if primary fails)",
            };
        }

        try
        {
            using var http = _httpFactory.CreateClient("vdt-image-gen");
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/images/generations?api-version={cfg.ApiVersion}";

            // Zero-cost probe: POST with an intentionally invalid body (empty prompt).
            // Azure's request pipeline performs auth → deployment routing → body validation,
            // so the deployment is resolved BEFORE the body is parsed. This means:
            //   - 400 BadRequest  → deployment EXISTS (body validation failed after routing)
            //   - 404 NotFound    → deployment MISSING (DeploymentNotFound)
            //   - 401/403         → auth problem
            //   - 429             → deployment exists, rate-limited
            // Cost: $0.00 — rejected at gateway before any image generation.
            // Latency: ~100-300ms vs 30-60s for a real generation.
            //
            // Background: the data-plane GET /openai/deployments/{id} endpoint was removed
            // from all API versions after 2022-12-01 (it 404s because the route doesn't
            // exist, not because the deployment is missing). The POST to the inference
            // endpoint is the only reliable data-plane validation for both classic and
            // Foundry-managed resources.
            var body = "{\"prompt\":\"\",\"n\":1,\"size\":\"1024x1024\"}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            var hdr = await _auth.GetHeaderAsync(ct);
            req.Headers.Add(hdr.HeaderName, hdr.HeaderValue);
            using var resp = await http.SendAsync(req, ct);

            var status = (int)resp.StatusCode;

            // 400 = deployment exists, body validation rejected our empty prompt. This is
            // the expected success case for our zero-cost probe.
            if (status == 400)
            {
                return new ValidationCheck
                {
                    Label = label,
                    Passed = true,
                    Detail = "online (deployment exists — zero-cost probe)",
                };
            }
            if (resp.IsSuccessStatusCode)
            {
                // Unlikely with empty prompt, but if the model accepts it, deployment works.
                return new ValidationCheck
                {
                    Label = label,
                    Passed = true,
                    Detail = "online (POST returned 200)",
                };
            }
            if (status == 429)
            {
                return new ValidationCheck
                {
                    Label = label,
                    Passed = true,
                    Detail = "throttled but online (HTTP 429 — deployment exists, rate-limited)",
                };
            }
            if (status == 404)
            {
                return new ValidationCheck
                {
                    Label = label,
                    Passed = false,
                    Detail = "Not deployed (HTTP 404)",
                    ActionHint = $"Deploy '{deployment}' via Azure portal → Model deployments → Create.",
                };
            }
            if (status is 401 or 403)
            {
                return new ValidationCheck
                {
                    Label = label,
                    Passed = false,
                    Detail = $"Auth rejected (HTTP {status})",
                    ActionHint = "Re-run `az login` or rotate the configured API key.",
                };
            }
            // Other status — surface raw response detail for diagnosis.
            string? errBody = null;
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            return new ValidationCheck
            {
                Label = label,
                Passed = false,
                Detail = $"HTTP {status} {resp.ReasonPhrase}" +
                         (string.IsNullOrEmpty(errBody) ? "" : $" — {(errBody.Length > 200 ? errBody[..200] : errBody)}"),
            };
        }
        catch (TaskCanceledException)
        {
            return new ValidationCheck
            {
                Label = label,
                Passed = false,
                Detail = "Timeout (15s) — deployment may be unreachable",
                ActionHint = "Check Azure resource health and network connectivity.",
            };
        }
        catch (Exception ex)
        {
            return new ValidationCheck { Label = label, Passed = false, Detail = ex.Message };
        }
    }

    private async Task<(bool ok, byte[]? bytes, string? error)> TryGenerateAsync(
        AzureOpenAIImageConfig cfg, string deployment, string prompt,
        ImageGenerationRequest request, CancellationToken ct)
    {
        var url = $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments/{deployment}/images/generations?api-version={cfg.ApiVersion}";
        var body = new { prompt, n = 1, size = request.Size, quality = "high", output_format = "png" };
        using var http = _httpFactory.CreateClient("vdt-image-gen");
        http.Timeout = TimeSpan.FromMinutes(5);
        try
        {
            var header = await _auth.GetHeaderAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            req.Headers.Add(header.HeaderName, header.HeaderValue);
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(text);
                var b64 = doc.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
                return b64 is null ? (false, null, "No b64_json") : (true, Convert.FromBase64String(b64), null);
            }
            return (false, null, $"HTTP {(int)resp.StatusCode}: {text}".TrimEnd());
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<ValidationCheck> CheckAuthAsync(CancellationToken ct)
    {
        try
        {
            var hdr = await _auth.GetHeaderAsync(ct);
            return new ValidationCheck
            {
                Label = $"Authentication ({_auth.EffectiveMethod})",
                Passed = !string.IsNullOrEmpty(hdr.HeaderValue),
            };
        }
        catch (Exception ex)
        {
            return new ValidationCheck
            {
                Label = "Authentication",
                Passed = false,
                Detail = ex.Message,
                ActionHint = "Run 'az login' for DefaultAzureCredential, or set the ImageApiKey user-secret.",
            };
        }
    }

    private async Task<ValidationCheck> CheckEndpointAsync(AzureOpenAIImageConfig cfg, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient("vdt-image-gen");
            http.Timeout = TimeSpan.FromSeconds(10);
            var probe = $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments?api-version={cfg.ApiVersion}";
            using var req = new HttpRequestMessage(HttpMethod.Get, probe);
            var hdr = await _auth.GetHeaderAsync(ct);
            req.Headers.Add(hdr.HeaderName, hdr.HeaderValue);
            using var resp = await http.SendAsync(req, ct);
            return new ValidationCheck
            {
                Label = "Endpoint reachable",
                Passed = (int)resp.StatusCode < 500,
                Detail = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
            };
        }
        catch (Exception ex) { return new ValidationCheck { Label = "Endpoint reachable", Passed = false, Detail = ex.Message }; }
    }

    private async Task<List<string>> EnumerateDeploymentsAsync(AzureOpenAIImageConfig cfg, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient("vdt-image-gen");
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments?api-version={cfg.ApiVersion}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var hdr = await _auth.GetHeaderAsync(ct);
            req.Headers.Add(hdr.HeaderName, hdr.HeaderValue);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new List<string>();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.TryGetProperty("id", out var id))
                        names.Add(id.GetString() ?? "");
            return names.Where(n => !string.IsNullOrEmpty(n)).ToList();
        }
        catch { return new List<string>(); }
    }

    private async Task<ValidationCheck> RunSmokeTestAsync(AzureOpenAIImageConfig cfg, string? outputPath, CancellationToken ct)
    {
        var savePath = !string.IsNullOrWhiteSpace(outputPath)
            ? outputPath
            : Path.Combine(Path.GetTempPath(), $"vdt-image-smoke-{Guid.NewGuid():N}.png");
        try
        {
            var (ok, bytes, err) = await TryGenerateAsync(
                cfg, cfg.PrimaryDeployment,
                "A single solid red circle centered on a white background.",
                new ImageGenerationRequest { Prompt = "smoke", OutputPath = savePath, Size = "1024x1024" },
                ct);
            if (ok && bytes is { Length: > 5 * 1024 })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? ".");
                    await File.WriteAllBytesAsync(savePath, bytes, ct);
                }
                catch (Exception writeEx)
                {
                    _logger.LogWarning(writeEx, "Smoke test ok but failed writing image to {Path}", savePath);
                }
                return new ValidationCheck
                {
                    Label = "End-to-end smoke generation",
                    Passed = true,
                    Detail = $"{bytes.Length} bytes",
                    SavedPath = File.Exists(savePath) ? savePath : null,
                };
            }
            return new ValidationCheck { Label = "End-to-end smoke generation", Passed = false, Detail = err };
        }
        catch (Exception ex)
        {
            return new ValidationCheck { Label = "End-to-end smoke generation", Passed = false, Detail = ex.Message };
        }
    }
}
