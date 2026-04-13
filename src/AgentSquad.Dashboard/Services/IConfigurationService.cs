using AgentSquad.Core.Configuration;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Abstraction over configuration management, enabling both in-process and HTTP-proxy implementations.
/// </summary>
public interface IConfigurationService
{
    /// <summary>Returns the current in-memory config snapshot.</summary>
    AgentSquadConfig GetCurrentConfig();

    /// <summary>Saves updated configuration to appsettings.json.</summary>
    Task SaveConfigAsync(AgentSquadConfig updatedConfig);

    /// <summary>Validates a GitHub PAT token against a specified repo.</summary>
    Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default);

    /// <summary>Scans GitHub repo and returns what would be cleaned up.</summary>
    Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default);

    /// <summary>Executes full 4-phase cleanup: stop agents → clean repo → reset state → restart agents.</summary>
    Task<CleanupResult> ExecuteCleanupAsync(string? caveats, CancellationToken ct = default);
}
