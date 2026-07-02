using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.AI;

public interface IAzureImageAuthProvider
{
    Task<ImageAuthHeader> GetHeaderAsync(CancellationToken ct = default);
    bool IsConfigured { get; }
    ImageAuthMethod EffectiveMethod { get; }

    /// <summary>
    /// Returns the environment variables that a child Copilot CLI process needs in order to
    /// call the Azure OpenAI image-generation REST endpoint directly (no MCP wrapper, no
    /// helper SDK). Returns null when image-gen isn't configured for this project, in which
    /// case no variables should be injected.
    /// </summary>
    /// <remarks>
    /// Design choice (2026-05-12): we replaced the MCP-wrapper approach with prompt-driven
    /// REST calls because the wrapper failed to bind in piped-stdin Copilot CLI sessions and
    /// the agent already calls REST natively when given credentials + a recipe in the prompt.
    /// Env-var injection is the simplest, most portable way to plumb auth into the child process.
    ///
    /// Variables emitted:
    ///   AZURE_OPENAI_IMAGE_ENDPOINT       — base URL, e.g. https://my-resource.openai.azure.com/
    ///   AZURE_OPENAI_IMAGE_API_VERSION    — API version, e.g. 2025-04-01-preview
    ///   AZURE_OPENAI_IMAGE_DEPLOYMENTS    — ordered CSV (primary first), e.g. "gpt-image-2,gpt-image-1.5,gpt-image-1,gpt-image-1-mini"
    ///   AZURE_OPENAI_IMAGE_API_KEY        — only when EffectiveMethod == ApiKey
    ///   AZURE_OPENAI_IMAGE_BEARER         — only when EffectiveMethod == DefaultAzureCredential (fresh snapshot, ~1h TTL)
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>?> GetEnvironmentForChildProcessAsync(CancellationToken ct = default);
}

public sealed record ImageAuthHeader(string HeaderName, string HeaderValue);

public sealed class AzureImageAuthProvider : IAzureImageAuthProvider
{
    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    private const string ApiKeySecretName = "VirtualDevTeam:AzureOpenAI:ImageApiKey";

    private static readonly Lazy<TokenCredential> _credential = new(() =>
        new DefaultAzureCredential(includeInteractiveCredentials: false));

    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly IConfiguration _rawConfig;
    private readonly ILogger<AzureImageAuthProvider> _logger;
    private AccessToken? _cachedToken;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AzureImageAuthProvider(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        IConfiguration rawConfig,
        ILogger<AzureImageAuthProvider> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _rawConfig = rawConfig ?? throw new ArgumentNullException(nameof(rawConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ImageAuthMethod EffectiveMethod
    {
        get
        {
            var configured = _config.CurrentValue.AzureOpenAIImage.AuthMethod;
            if (configured == ImageAuthMethod.ApiKey && !string.IsNullOrEmpty(GetApiKey()))
                return ImageAuthMethod.ApiKey;
            return ImageAuthMethod.DefaultAzureCredential;
        }
    }

    public bool IsConfigured
    {
        get
        {
            var img = _config.CurrentValue.AzureOpenAIImage;
            if (!img.IsConfigured()) return false;
            return EffectiveMethod == ImageAuthMethod.ApiKey
                ? !string.IsNullOrEmpty(GetApiKey())
                : true;
        }
    }

    public async Task<ImageAuthHeader> GetHeaderAsync(CancellationToken ct = default)
    {
        if (EffectiveMethod == ImageAuthMethod.ApiKey)
        {
            var key = GetApiKey();
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException(
                    $"Image-gen auth set to ApiKey but {ApiKeySecretName} user-secret is missing.");
            return new ImageAuthHeader("api-key", key);
        }
        var token = await AcquireTokenAsync(ct);
        return new ImageAuthHeader("Authorization", $"Bearer {token}");
    }

    private string? GetApiKey() => _rawConfig[ApiKeySecretName];

    public async Task<IReadOnlyDictionary<string, string>?> GetEnvironmentForChildProcessAsync(
        CancellationToken ct = default)
    {
        var img = _config.CurrentValue.AzureOpenAIImage;
        if (!img.IsConfigured())
            return null;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AZURE_OPENAI_IMAGE_ENDPOINT"] = img.Endpoint.TrimEnd('/'),
            ["AZURE_OPENAI_IMAGE_API_VERSION"] = img.ApiVersion,
            ["AZURE_OPENAI_IMAGE_DEPLOYMENTS"] = string.Join(",", img.GetOrderedDeployments()),
        };

        if (EffectiveMethod == ImageAuthMethod.ApiKey)
        {
            var key = GetApiKey();
            if (!string.IsNullOrEmpty(key))
                env["AZURE_OPENAI_IMAGE_API_KEY"] = key;
            else
                _logger.LogWarning(
                    "AzureOpenAIImage auth method is ApiKey but {Secret} is unset — child process will lack credentials",
                    ApiKeySecretName);
        }
        else
        {
            try
            {
                var token = await AcquireTokenAsync(ct);
                env["AZURE_OPENAI_IMAGE_BEARER"] = token;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to acquire DefaultAzureCredential token for child-process env injection — image gen will fail in agent sessions");
            }
        }

        return env;
    }

    private async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is { } t && t.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return t.Token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } t2 && t2.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return t2.Token;

            var ctx = new TokenRequestContext(new[] { CognitiveServicesScope });
            var fresh = await _credential.Value.GetTokenAsync(ctx, ct);
            _cachedToken = fresh;
            _logger.LogDebug("Acquired Azure Cognitive Services token (expires {Expiry}).", fresh.ExpiresOn);
            return fresh.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
