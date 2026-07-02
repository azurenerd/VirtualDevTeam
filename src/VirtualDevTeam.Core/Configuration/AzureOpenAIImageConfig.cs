namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Configuration for Azure OpenAI image generation (gpt-image-*).
/// </summary>
/// <remarks>
/// Configured per-project via the Develop wizard's "Image Generation (Azure OpenAI)" section
/// in step 2. Stored in <c>develop-settings.json</c> under the <c>AzureOpenAIImage</c> key
/// (without secrets), then merged into <see cref="VirtualDevTeamConfig"/> at runtime by
/// <c>DevelopSettingsService.MergeIntoConfig</c>.
///
/// Auth philosophy mirrors ProjectDashboard / SFI: <see cref="ImageAuthMethod.DefaultAzureCredential"/>
/// is the default and preferred path (Entra / Azure CLI / Managed Identity, no key needed).
/// The static API-key path exists as an emergency fallback and is read from
/// <c>VirtualDevTeam:AzureOpenAI:ImageApiKey</c> in dotnet user-secrets — never from this object.
///
/// Deployment fallback chain: the service tries <see cref="PrimaryDeployment"/> first, then walks
/// <see cref="FallbackDeployments"/> in order on transient errors (429 EngineOverloaded, 503,
/// ResourceNotFound, DeploymentNotFound). The default chain reflects the available variants in
/// supported Azure regions as of 2026-05.
/// </remarks>
public sealed class AzureOpenAIImageConfig
{
    /// <summary>
    /// Azure OpenAI resource endpoint, e.g. <c>https://my-img-resource.openai.azure.com/</c>.
    /// Required when image-gen is in use; empty disables image features.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Azure OpenAI API version. Default targets gpt-image-* compatibility.</summary>
    public string ApiVersion { get; set; } = "2025-04-01-preview";

    /// <summary>
    /// Deployment name to try first (your "best" model). Default <c>gpt-image-1.5</c> per the
    /// 2026-05-12 operator validation: gpt-image-1.5 produced dramatically more visual detail
    /// at the same prompts than gpt-image-1, gpt-image-1-mini, or gpt-image-2 — and ties
    /// gpt-image-1's RPM (~9 RPM in operator's tier). gpt-image-2 is only suitable as a
    /// last-resort fallback because of its tighter quota (2 RPM in the operator's tier).
    /// </summary>
    public string PrimaryDeployment { get; set; } = "gpt-image-1.5";

    /// <summary>
    /// Ordered list of fallback deployment names. Tried in order when the primary returns
    /// a transient error after <see cref="MaxAttemptsPerImage"/> attempts with backoff.
    /// </summary>
    public List<string> FallbackDeployments { get; set; } = new()
    {
        "gpt-image-1",
        "gpt-image-1-mini",
        "gpt-image-2",
    };

    /// <summary>
    /// Maximum attempts PER DEPLOYMENT before falling to the next deployment in the ladder.
    /// Default 3. Each retry waits according to <see cref="RetryBackoffSeconds"/>.
    /// Set to 1 to disable retries (single-shot per deployment, immediate fallback on any error).
    /// </summary>
    public int MaxAttemptsPerImage { get; set; } = 3;

    /// <summary>
    /// Backoff schedule (seconds) between retries WITHIN the same deployment, indexed by
    /// retry number minus one. Default <c>{5, 10, 15}</c>. If
    /// <see cref="MaxAttemptsPerImage"/> exceeds this list length, the last value is reused.
    /// Backoff is applied only to retryable failures (429, 503, transient timeouts) — not to
    /// hard failures (401/403/400/404) which fall straight to the next deployment.
    /// </summary>
    public List<int> RetryBackoffSeconds { get; set; } = new() { 5, 10, 15 };

    /// <summary>
    /// Vision-AI verification confidence threshold (0.0–1.0). Below this and the verifier
    /// reports DOES_NOT_MATCH, the service refines the prompt and retries. Default 0.75.
    /// </summary>
    public double VerificationConfidenceThreshold { get; set; } = 0.75;

    /// <summary>
    /// When true, every generation is verified by the vision-AI loop. When false, generations
    /// are accepted as long as the byte stream is structurally valid (file size > 5 KB).
    /// Default true; set false in cost-constrained scenarios.
    /// </summary>
    public bool EnableVerification { get; set; } = true;

    /// <summary>
    /// Auth method. <see cref="ImageAuthMethod.DefaultAzureCredential"/> is preferred (no key
    /// management); <see cref="ImageAuthMethod.ApiKey"/> reads from user-secrets as fallback.
    /// </summary>
    public ImageAuthMethod AuthMethod { get; set; } = ImageAuthMethod.DefaultAzureCredential;

    /// <summary>
    /// Returns the effective ordered deployment list to try, with <see cref="PrimaryDeployment"/>
    /// first and <see cref="FallbackDeployments"/> after, deduplicated and trimmed of empties.
    /// </summary>
    public IReadOnlyList<string> GetOrderedDeployments()
    {
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(PrimaryDeployment))
            ordered.Add(PrimaryDeployment.Trim());
        foreach (var d in FallbackDeployments)
        {
            var trimmed = d?.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                !ordered.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                ordered.Add(trimmed);
        }
        return ordered;
    }

    /// <summary>True when the config is sufficient to attempt a generation.</summary>
    public bool IsConfigured()
        => !string.IsNullOrWhiteSpace(Endpoint) && GetOrderedDeployments().Count > 0;
}

/// <summary>Authentication method for the Azure OpenAI image-generation endpoint.</summary>
public enum ImageAuthMethod
{
    /// <summary>
    /// Use <c>DefaultAzureCredential</c> — Entra / Managed Identity / Azure CLI / VS Code login.
    /// Preferred (SFI-compliant, no key management).
    /// </summary>
    DefaultAzureCredential = 0,

    /// <summary>
    /// Use a static API key (read from dotnet user-secret <c>VirtualDevTeam:AzureOpenAI:ImageApiKey</c>).
    /// Emergency fallback only.
    /// </summary>
    ApiKey = 1,
}
