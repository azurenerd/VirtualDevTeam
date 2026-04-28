using System.Text.Json;
using AgentSquad.Core.DevPlatform.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="DevelopSettings"/> from develop-settings.json.
/// Thread-safe via SemaphoreSlim; uses atomic temp-file-then-move writes.
/// </summary>
public sealed class DevelopSettingsService : IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<DevelopSettingsService> _logger;
    private readonly AgentSquadConfig? _existingConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DevelopSettingsService(
        ILogger<DevelopSettingsService> logger,
        IOptions<AgentSquadConfig>? existingConfig = null,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _existingConfig = existingConfig?.Value;
        _filePath = filePath ?? Path.Combine(Directory.GetCurrentDirectory(), "develop-settings.json");
    }

    /// <summary>
    /// Reads develop-settings.json. Returns defaults if the file doesn't exist.
    /// </summary>
    public async Task<DevelopSettings> LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                if (_existingConfig is not null)
                {
                    _logger.LogInformation(
                        "Develop settings file not found at {Path}, pre-populating from existing config", _filePath);
                    var seeded = CreateFromExistingConfig(_existingConfig);
                    // Persist so subsequent loads use the file
                    var seedJson = JsonSerializer.Serialize(seeded, JsonOptions);
                    var tempPath = _filePath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, seedJson, ct);
                    File.Move(tempPath, _filePath, overwrite: true);
                    return seeded;
                }

                _logger.LogDebug("Develop settings file not found at {Path}, returning defaults", _filePath);
                return new DevelopSettings();
            }

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var settings = JsonSerializer.Deserialize<DevelopSettings>(json, JsonOptions);
            _logger.LogDebug("Loaded develop settings from {Path}", _filePath);
            return settings ?? new DevelopSettings();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize develop settings from {Path}, returning defaults", _filePath);
            return new DevelopSettings();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes develop-settings.json atomically (write to temp, then move).
    /// </summary>
    public async Task SaveAsync(DevelopSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogInformation("Saved develop settings to {Path}", _filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Overlays develop settings onto the runtime <see cref="AgentSquadConfig"/>.
    /// Only touches project-level fields (description, tech stack, repo settings).
    /// Does NOT modify PATs, model config, agent config, limits, or any non-project fields.
    /// </summary>
    public void MergeIntoConfig(AgentSquadConfig config, DevelopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.Description))
            config.Project.Description = settings.Description;

        if (!string.IsNullOrWhiteSpace(settings.TechStack))
            config.Project.TechStack = settings.TechStack;

        if (!string.IsNullOrWhiteSpace(settings.ExecutiveUsername))
            config.Project.ExecutiveGitHubUsername = settings.ExecutiveUsername;

        config.Project.ParentWorkItemId = settings.ParentWorkItemId;

        // Platform-specific repo settings
        if (string.Equals(settings.Platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            config.DevPlatform.Platform = DevPlatformType.GitHub;

            if (!string.IsNullOrWhiteSpace(settings.GitHub.Repo))
                config.Project.GitHubRepo = settings.GitHub.Repo;

            if (!string.IsNullOrWhiteSpace(settings.GitHub.DefaultBranch))
                config.Project.DefaultBranch = settings.GitHub.DefaultBranch;
        }
        else if (string.Equals(settings.Platform, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
        {
            config.DevPlatform.Platform = DevPlatformType.AzureDevOps;
            config.DevPlatform.AzureDevOps ??= new AzureDevOpsConfig();

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Organization))
                config.DevPlatform.AzureDevOps.Organization = settings.AzureDevOps.Organization;

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Project))
                config.DevPlatform.AzureDevOps.Project = settings.AzureDevOps.Project;

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Repository))
                config.DevPlatform.AzureDevOps.Repository = settings.AzureDevOps.Repository;

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.DefaultBranch))
                config.DevPlatform.AzureDevOps.DefaultBranch = settings.AzureDevOps.DefaultBranch;
        }

        _logger.LogDebug("Merged develop settings into config (platform={Platform})", settings.Platform);
    }

    /// <summary>
    /// Creates initial DevelopSettings from existing AgentSquadConfig.
    /// Called on first load when develop-settings.json doesn't exist.
    /// </summary>
    public DevelopSettings CreateFromExistingConfig(AgentSquadConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var settings = new DevelopSettings();

        // Map platform
        settings.Platform = config.DevPlatform.Platform switch
        {
            DevPlatformType.GitHub => "GitHub",
            DevPlatformType.AzureDevOps => "AzureDevOps",
            _ => "GitHub"
        };

        // Map auth method
        settings.AuthMethod = config.DevPlatform.AuthMethod switch
        {
            DevPlatformAuthMethod.Pat => "Pat",
            DevPlatformAuthMethod.AzureCliBearer => "AzureCliBearer",
            DevPlatformAuthMethod.ServicePrincipal => "ServicePrincipal",
            _ => "Pat"
        };

        // Map GitHub settings
        settings.GitHub = new GitHubRepoSettings
        {
            Repo = config.Project.GitHubRepo,
            DefaultBranch = config.Project.DefaultBranch
        };

        // Map ADO settings
        settings.AzureDevOps = new AdoRepoSettings
        {
            Organization = config.DevPlatform.AzureDevOps?.Organization ?? "",
            Project = config.DevPlatform.AzureDevOps?.Project ?? "",
            Repository = config.DevPlatform.AzureDevOps?.Repository ?? "",
            DefaultBranch = config.DevPlatform.AzureDevOps?.DefaultBranch ?? config.Project.DefaultBranch
        };

        // Map project settings — never import PATs
        settings.Description = config.Project.Description;
        settings.TechStack = config.Project.TechStack;
        settings.ExecutiveUsername = config.Project.ExecutiveGitHubUsername;
        settings.ParentWorkItemId = config.Project.ParentWorkItemId;

        return settings;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }
}
