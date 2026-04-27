using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// HTTP-proxy implementation of <see cref="IConfigurationService"/> for standalone dashboard mode.
/// Forwards configuration operations to the Runner's REST API when available.
/// Falls back to direct file I/O for save and read when the Runner is unreachable.
/// Creates a fresh HttpClient per request via IHttpClientFactory to avoid stale connection issues
/// when the polling service (HttpDashboardDataService) shares the same handler pool.
/// </summary>
public sealed class HttpConfigurationService : IConfigurationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _clientName;
    private readonly ILogger<HttpConfigurationService> _logger;
    private readonly string? _localAppSettingsPath;
    private AgentSquadConfig? _cachedConfig;

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null // preserve PascalCase
    };

    public HttpConfigurationService(
        IHttpClientFactory httpFactory,
        string clientName,
        ILogger<HttpConfigurationService> logger,
        string? localAppSettingsPath = null)
    {
        _httpFactory = httpFactory;
        _clientName = clientName;
        _logger = logger;
        _localAppSettingsPath = localAppSettingsPath;
    }

    private HttpClient CreateClient() => _httpFactory.CreateClient(_clientName);

    public AgentSquadConfig GetCurrentConfig()
    {
        // Return cached config if available (refreshed on save)
        if (_cachedConfig is not null)
            return _cachedConfig;

        // First call — try Runner, then fall back to local file
        try
        {
            var config = Task.Run(async () =>
            {
                using var client = CreateClient();
                var response = await client.GetAsync("/api/configuration/current");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AgentSquadConfig>(JsonReadOptions);
            }).GetAwaiter().GetResult();

            _cachedConfig = config ?? new AgentSquadConfig();
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch configuration from Runner — trying local file");
        }

        // Fallback: read from local appsettings.json
        var localConfig = ReadLocalConfig();
        if (localConfig is not null)
        {
            _cachedConfig = localConfig;
            return _cachedConfig;
        }

        return new AgentSquadConfig();
    }

    public async Task SaveConfigAsync(AgentSquadConfig updatedConfig)
    {
        // Serialize on the calling thread (safe, CPU-bound)
        var json = JsonSerializer.Serialize(updatedConfig);
        _logger.LogInformation("SaveConfigAsync: {Bytes} bytes, dispatching to thread pool...", json.Length);

        // Try the Runner API first
        var saved = await Task.Run(async () =>
        {
            try
            {
                using var client = CreateClient();
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await client.PostAsync("/api/configuration/save", content);
                sw.Stop();

                _logger.LogInformation("SaveConfigAsync: response {Status} in {Ms}ms", response.StatusCode, sw.ElapsedMilliseconds);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Runner API unavailable for save — falling back to local file");
                return false;
            }
        });

        if (!saved)
        {
            // Fallback: write directly to the Runner's appsettings.json
            await SaveLocalAsync(updatedConfig);
        }

        _cachedConfig = updatedConfig;
        _logger.LogInformation("Configuration saved successfully");
    }

    public async Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var request = new { Token = token, RepoFullName = repoFullName };
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/configuration/validate-pat", request, ct);

            if (!response.IsSuccessStatusCode)
                return new PatValidationResult { Success = false, Error = $"Runner returned {response.StatusCode}" };

            return await response.Content.ReadFromJsonAsync<PatValidationResult>(JsonReadOptions, ct)
                ?? new PatValidationResult { Success = false, Error = "Empty response from Runner" };
        }, ct);
    }

    public async Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var client = CreateClient();
            var response = await client.GetAsync("/api/configuration/cleanup/scan", ct);

            if (!response.IsSuccessStatusCode)
                return new CleanupSummary { Error = $"Runner returned {response.StatusCode}" };

            return await response.Content.ReadFromJsonAsync<CleanupSummary>(JsonReadOptions, ct)
                ?? new CleanupSummary { Error = "Empty response from Runner" };
        }, ct);
    }

    public async Task<CleanupResult> ExecuteCleanupAsync(string? caveats, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var request = new { Caveats = caveats };
            using var client = CreateClient();
            var response = await client.PostAsJsonAsync("/api/configuration/cleanup/execute", request, ct);

            if (!response.IsSuccessStatusCode)
                return new CleanupResult { Success = false, Phase = "Error", Errors = [$"Runner returned {response.StatusCode}"] };

            return await response.Content.ReadFromJsonAsync<CleanupResult>(JsonReadOptions, ct)
                ?? new CleanupResult { Success = false, Phase = "Error", Errors = ["Empty response from Runner"] };
        }, ct);
    }

    // ── Local file I/O fallback ──

    private AgentSquadConfig? ReadLocalConfig()
    {
        if (_localAppSettingsPath is null || !File.Exists(_localAppSettingsPath))
            return null;

        try
        {
            var json = File.ReadAllText(_localAppSettingsPath);
            var root = JsonNode.Parse(json)?.AsObject();
            var section = root?["AgentSquad"];
            if (section is null) return null;

            return section.Deserialize<AgentSquadConfig>(JsonReadOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local appsettings.json at {Path}", _localAppSettingsPath);
            return null;
        }
    }

    private async Task SaveLocalAsync(AgentSquadConfig updatedConfig)
    {
        if (_localAppSettingsPath is null)
            throw new InvalidOperationException(
                "Cannot save configuration: Runner API is unavailable and no local appsettings path is configured.");

        _logger.LogInformation("Saving configuration directly to {Path}", _localAppSettingsPath);

        // Persist secrets to User Secrets before stripping from the JSON file
        await PersistSecretsToUserSecretsAsync(updatedConfig);

        // Read existing file to preserve non-AgentSquad sections
        JsonObject root;
        if (File.Exists(_localAppSettingsPath))
        {
            var existing = await File.ReadAllTextAsync(_localAppSettingsPath);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var configJson = JsonSerializer.SerializeToNode(updatedConfig, JsonWriteOptions);
        StripSecrets(configJson);
        root["AgentSquad"] = configJson;

        var output = root.ToJsonString(JsonWriteOptions);

        // Atomic write: temp file then move (avoids partial writes and file-watcher IOExceptions on Windows)
        var tempPath = _localAppSettingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, output);
        File.Move(tempPath, _localAppSettingsPath, overwrite: true);

        _logger.LogInformation("Configuration saved to local file {Path}", _localAppSettingsPath);
    }

    /// <summary>
    /// Strips secrets from serialized config so they are not persisted to appsettings.json.
    /// Mirrors the logic in ConfigurationService.StripSecrets.
    /// </summary>
    private static void StripSecrets(JsonNode? configNode)
    {
        if (configNode is not JsonObject config) return;

        if (config["Project"] is JsonObject project && project.ContainsKey("GitHubToken"))
            project["GitHubToken"] = "";

        if (config["Models"] is JsonObject models)
        {
            foreach (var (_, tierNode) in models)
            {
                if (tierNode is JsonObject tier && tier.ContainsKey("ApiKey"))
                    tier["ApiKey"] = "";
            }
        }

        if (config["McpServers"] is JsonObject mcpServers)
        {
            foreach (var (_, serverNode) in mcpServers)
            {
                if (serverNode is not JsonObject server) continue;
                if (server["Env"] is not JsonObject env) continue;
                foreach (var (key, _) in env)
                {
                    if (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        env[key] = "";
                    }
                }
            }
        }

        // Strip ADO secrets from DevPlatform section
        if (config["DevPlatform"] is JsonObject devPlatform &&
            devPlatform["AzureDevOps"] is JsonObject azureDevOps)
        {
            if (azureDevOps.ContainsKey("Pat"))
                azureDevOps["Pat"] = "";
            if (azureDevOps.ContainsKey("TenantId"))
                azureDevOps["TenantId"] = "";
        }
    }

    /// <summary>
    /// Persists secret values to .NET User Secrets so they survive restarts.
    /// Discovers project UserSecretsIds from csproj files relative to the local appsettings path.
    /// Throws on failure so the caller can abort the save rather than lose secrets.
    /// </summary>
    private async Task PersistSecretsToUserSecretsAsync(AgentSquadConfig config)
    {
        var secrets = CollectSecrets(config);
        if (secrets.Count == 0) return;

        var runnerDir = _localAppSettingsPath is not null ? Path.GetDirectoryName(_localAppSettingsPath) : null;
        var dashboardDir = runnerDir is not null ? Path.GetFullPath(Path.Combine(runnerDir, "..", "AgentSquad.Dashboard")) : null;

        var secretsIds = new HashSet<string>();
        foreach (var dir in new[] { runnerDir, dashboardDir })
        {
            if (dir is null) continue;
            var id = ReadUserSecretsIdFromCsproj(dir);
            if (id is not null) secretsIds.Add(id);
        }

        if (secretsIds.Count == 0)
        {
            _logger.LogWarning("No UserSecretsId found in project files — secrets will not be persisted");
            return;
        }

        foreach (var secretsId in secretsIds)
            await MergeUserSecretsAsync(secretsId, secrets);

        _logger.LogInformation("Persisted {Count} secret(s) to {Stores} User Secrets store(s)",
            secrets.Count, secretsIds.Count);
    }

    private static Dictionary<string, string> CollectSecrets(AgentSquadConfig config)
    {
        var secrets = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(config.Project.GitHubToken))
            secrets["AgentSquad:Project:GitHubToken"] = config.Project.GitHubToken;

        if (!string.IsNullOrEmpty(config.DevPlatform?.AzureDevOps?.Pat))
            secrets["AgentSquad:DevPlatform:AzureDevOps:Pat"] = config.DevPlatform!.AzureDevOps!.Pat;

        if (!string.IsNullOrEmpty(config.DevPlatform?.AzureDevOps?.TenantId))
            secrets["AgentSquad:DevPlatform:AzureDevOps:TenantId"] = config.DevPlatform!.AzureDevOps!.TenantId;

        if (config.Models is not null)
        {
            foreach (var (tier, model) in config.Models)
            {
                if (!string.IsNullOrEmpty(model.ApiKey))
                    secrets[$"AgentSquad:Models:{tier}:ApiKey"] = model.ApiKey;
            }
        }

        if (config.McpServers is not null)
        {
            foreach (var (serverName, server) in config.McpServers)
            {
                if (server.Env is null) continue;
                foreach (var (key, value) in server.Env)
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    if (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        secrets[$"AgentSquad:McpServers:{serverName}:Env:{key}"] = value;
                    }
                }
            }
        }

        return secrets;
    }

    private static string? ReadUserSecretsIdFromCsproj(string projectDir)
    {
        if (!Directory.Exists(projectDir)) return null;
        var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj is null) return null;

        var content = File.ReadAllText(csproj);
        var startTag = "<UserSecretsId>";
        var endTag = "</UserSecretsId>";
        var start = content.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += startTag.Length;
        var end = content.IndexOf(endTag, start, StringComparison.Ordinal);
        if (end < 0) return null;

        return content[start..end].Trim();
    }

    private static async Task MergeUserSecretsAsync(string secretsId, Dictionary<string, string> secrets)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var secretsDir = Path.Combine(appData, "Microsoft", "UserSecrets", secretsId);
        var secretsPath = Path.Combine(secretsDir, "secrets.json");

        JsonObject existing;
        if (File.Exists(secretsPath))
        {
            var json = await File.ReadAllTextAsync(secretsPath);
            existing = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        else
        {
            existing = new JsonObject();
        }

        foreach (var (key, value) in secrets)
            existing[key] = value;

        Directory.CreateDirectory(secretsDir);
        var output = existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = secretsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, output);
        File.Move(tempPath, secretsPath, overwrite: true);
    }
}
