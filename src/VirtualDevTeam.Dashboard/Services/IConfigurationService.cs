using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Abstraction over configuration management, enabling both in-process and HTTP-proxy implementations.
/// </summary>
public interface IConfigurationService
{
    /// <summary>Returns the current in-memory config snapshot.</summary>
    VirtualDevTeamConfig GetCurrentConfig();

    /// <summary>
    /// Re-reads appsettings.json from disk and clears any in-memory cache,
    /// so the next GetCurrentConfig call returns the latest on-disk values.
    /// </summary>
    VirtualDevTeamConfig RefreshFromDisk();

    /// <summary>Saves updated configuration to appsettings.json.</summary>
    Task SaveConfigAsync(VirtualDevTeamConfig updatedConfig);

    /// <summary>
    /// Persists only secrets (PAT/tokens) to .NET User Secrets without touching appsettings.json.
    /// Use from the Develop wizard to save authentication tokens for future sessions.
    /// </summary>
    Task PersistSecretsOnlyAsync(
        string? gitHubToken = null,
        string? adoPat = null,
        string? adoBearerToken = null,
        string? imageApiKey = null);

    /// <summary>Validates a GitHub PAT token against a specified repo.</summary>
    Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default);

    /// <summary>
    /// Validates the Azure OpenAI image-generation configuration.
    /// </summary>
    Task<VirtualDevTeam.Core.AI.ImageValidationReport> ValidateImageGenAsync(
        ImageGenValidationRequest request,
        CancellationToken ct = default);

    /// <summary>Scans GitHub repo and returns what would be cleaned up.</summary>
    Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default);

    /// <summary>Executes full 4-phase cleanup: stop agents → clean repo → reset state → restart agents.</summary>
    Task<CleanupResult> ExecuteCleanupAsync(string? caveats, CancellationToken ct = default);
}

/// <summary>Request body for ValidateImageGenAsync.</summary>
public sealed class ImageGenValidationRequest
{
    public bool RunSmokeTest { get; set; } = true;
    public DevelopAzureOpenAIImageSettings? Settings { get; set; }
    public string? ApiKey { get; set; }
}
