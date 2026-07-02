namespace VirtualDevTeam.Core.AI;

using System.Text.Json;
using System.Text.Json.Serialization;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Manages the Copilot CLI's MCP configuration file (~/.config/github-copilot/mcp.json).
/// On startup, writes VirtualDevTeam-managed server definitions. On shutdown, cleans them up.
/// </summary>
public sealed class CopilotCliMcpConfigManager : IHostedService, IDisposable
{
    private const string VirtualDevTeamTag = "__virtualdevteam_managed";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly McpServerRegistry _registry;
    private readonly VirtualDevTeamConfig _config;
    private readonly ILogger<CopilotCliMcpConfigManager> _logger;
    private readonly HashSet<string> _managedServers = new(StringComparer.OrdinalIgnoreCase);
    private string? _configFilePath;

    public CopilotCliMcpConfigManager(
        McpServerRegistry registry,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<CopilotCliMcpConfigManager> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the resolved path to the CLI MCP config file.
    /// </summary>
    public string? ConfigFilePath => _configFilePath;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _configFilePath = DetectConfigFilePath();
        if (_configFilePath is null)
        {
            _logger.LogWarning("Could not detect Copilot CLI MCP config path. MCP auto-config disabled.");
            return;
        }

        _logger.LogInformation("MCP config path: {Path}", _configFilePath);

        try
        {
            await SyncServersToConfigAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync MCP servers to CLI config. Agents may still work if servers are pre-configured.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_configFilePath is null || _managedServers.Count == 0) return;

        try
        {
            await CleanupManagedServersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up managed MCP servers from config.");
        }
    }

    /// <summary>
    /// Ensures all VirtualDevTeam-registered MCP servers are written to the CLI config file.
    /// Existing non-managed entries are preserved. Idempotent.
    /// </summary>
    public async Task SyncServersToConfigAsync(CancellationToken ct = default)
    {
        if (_configFilePath is null) return;

        var existing = await ReadConfigAsync(ct);
        var servers = _registry.GetAll();
        var changed = false;

        foreach (var (name, definition) in servers)
        {
            var entry = ConvertToConfigEntry(definition);
            if (entry is null) continue;

            // Mark as managed
            entry.Metadata = new Dictionary<string, string> { [VirtualDevTeamTag] = "true" };

            if (existing.McpServers.TryGetValue(name, out var current))
            {
                // Only update if it's one we manage
                if (current.Metadata?.ContainsKey(VirtualDevTeamTag) == true)
                {
                    existing.McpServers[name] = entry;
                    changed = true;
                }
                else
                {
                    _logger.LogDebug("Skipping MCP server '{Name}' — already configured by user.", name);
                }
            }
            else
            {
                existing.McpServers[name] = entry;
                changed = true;
            }

            _managedServers.Add(name);
        }

        if (changed)
        {
            await WriteConfigAsync(existing, ct);
            _logger.LogInformation("Synced {Count} MCP server(s) to CLI config.", _managedServers.Count);
        }
    }

    /// <summary>
    /// Configures MCP servers for a specific agent, ensuring its required servers are in the config.
    /// Call this before spawning an agent that needs specific MCP servers.
    /// </summary>
    public async Task EnsureServersConfiguredAsync(IReadOnlyList<string> serverNames, CancellationToken ct = default)
    {
        if (_configFilePath is null || serverNames.Count == 0) return;

        var existing = await ReadConfigAsync(ct);
        var changed = false;

        foreach (var name in serverNames)
        {
            if (existing.McpServers.ContainsKey(name)) continue;

            var definition = _registry.Get(name);
            if (definition is null)
            {
                _logger.LogWarning("MCP server '{Name}' requested but not found in registry.", name);
                continue;
            }

            var entry = ConvertToConfigEntry(definition);
            if (entry is null) continue;

            entry.Metadata = new Dictionary<string, string> { [VirtualDevTeamTag] = "true" };
            existing.McpServers[name] = entry;
            _managedServers.Add(name);
            changed = true;
        }

        if (changed)
        {
            await WriteConfigAsync(existing, ct);
        }
    }

    private async Task CleanupManagedServersAsync(CancellationToken ct)
    {
        if (_configFilePath is null) return;

        var config = await ReadConfigAsync(ct);
        var removed = 0;

        foreach (var name in _managedServers)
        {
            if (config.McpServers.TryGetValue(name, out var entry)
                && entry.Metadata?.ContainsKey(VirtualDevTeamTag) == true)
            {
                config.McpServers.Remove(name);
                removed++;
            }
        }

        if (removed > 0)
        {
            await WriteConfigAsync(config, ct);
            _logger.LogInformation("Cleaned up {Count} managed MCP server(s) from CLI config.", removed);
        }

        _managedServers.Clear();
    }

    /// <summary>
    /// Detects the Copilot CLI MCP config file path based on the current platform.
    /// </summary>
    internal static string? DetectConfigFilePath()
    {
        // Try XDG_CONFIG_HOME first (Linux), then standard locations
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfig))
        {
            return Path.Combine(xdgConfig, "github-copilot", "mcp.json");
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                return Path.Combine(appData, "github-copilot", "mcp.json");
        }

        // macOS and Linux default
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            return Path.Combine(home, ".config", "github-copilot", "mcp.json");

        return null;
    }

    private static McpConfigEntry? ConvertToConfigEntry(McpServerDefinition definition)
    {
        if (definition.Transport == McpTransportType.Stdio)
        {
            return new McpConfigEntry
            {
                Type = "stdio",
                Command = ResolvePlaceholders(definition.Command),
                Args = definition.Args?.Select(ResolvePlaceholders).ToList(),
                Env = definition.Env?.ToDictionary(kv => kv.Key, kv => ResolvePlaceholders(kv.Value))
            };
        }

        if (definition.Transport is McpTransportType.Http or McpTransportType.Sse)
        {
            // For HTTP/SSE transports, the "command" is the URL
            return new McpConfigEntry
            {
                Type = definition.Transport == McpTransportType.Sse ? "sse" : "http",
                Url = ResolvePlaceholders(definition.Command)
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves <c>${PLACEHOLDER}</c> tokens in an MCP definition string against:
    /// <list type="number">
    ///   <item><description><c>${VDT_REPO_ROOT}</c> → the VirtualDevTeam source-tree root.</description></item>
    ///   <item><description><c>${VirtualDevTeam:AzureOpenAIImage:DeploymentsJson}</c> → JSON-array string of the configured deployment fallback chain (Primary first, then Fallbacks, deduped).</description></item>
    ///   <item><description><c>${VirtualDevTeam:AzureOpenAIImage:Endpoint}</c> / <c>:ApiVersion</c> / <c>:AuthMethod</c> → corresponding config values.</description></item>
    ///   <item><description><c>${VirtualDevTeam:AzureOpenAI:ImageApiKey}</c> → resolved from <see cref="IConfiguration"/> (user-secret).</description></item>
    ///   <item><description>Any other <c>${VARIABLE}</c> → environment variable; falls back to empty string if unset.</description></item>
    /// </list>
    /// Unresolved placeholders collapse to empty string so the Env dict never carries the
    /// literal <c>${VAR}</c> token through to the spawned MCP process.
    /// </summary>
    internal static string ResolvePlaceholders(string? input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${", StringComparison.Ordinal))
            return input ?? "";

        return System.Text.RegularExpressions.Regex.Replace(input, @"\$\{([^}]+)\}", m =>
        {
            var key = m.Groups[1].Value;

            if (string.Equals(key, "VDT_REPO_ROOT", StringComparison.Ordinal))
                return GetVdtRepoRoot() ?? "";

            // Live config injection — pulled from the static helper so test paths can override.
            var resolved = _liveConfigResolver?.Invoke(key);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            // Fall back to environment variable.
            return Environment.GetEnvironmentVariable(key) ?? "";
        });
    }

    private static Func<string, string?>? _liveConfigResolver;

    /// <summary>
    /// Hook used by the runner at startup to inject live config (user-secrets, AzureOpenAIImage,
    /// etc.) into placeholder resolution. Should be set once before <see cref="StartAsync"/>.
    /// </summary>
    public static void SetLiveConfigResolver(Func<string, string?> resolver)
        => _liveConfigResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    private static string? GetVdtRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "VirtualDevTeam.sln")))
                    return dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                dir = dir.Parent;
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private async Task<McpConfigFile> ReadConfigAsync(CancellationToken ct)
    {
        if (_configFilePath is null || !File.Exists(_configFilePath))
            return new McpConfigFile();

        try
        {
            var json = await File.ReadAllTextAsync(_configFilePath, ct);
            return JsonSerializer.Deserialize<McpConfigFile>(json, JsonOptions) ?? new McpConfigFile();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse MCP config at {Path}. Starting fresh.", _configFilePath);
            return new McpConfigFile();
        }
    }

    private async Task WriteConfigAsync(McpConfigFile config, CancellationToken ct)
    {
        if (_configFilePath is null) return;

        var dir = Path.GetDirectoryName(_configFilePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(_configFilePath, json, ct);
    }

    public void Dispose()
    {
        _managedServers.Clear();
    }
}

/// <summary>
/// Represents the Copilot CLI mcp.json config file structure.
/// </summary>
internal sealed class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpConfigEntry> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A single MCP server entry in the CLI config.
/// </summary>
internal sealed class McpConfigEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Internal metadata used to track managed entries. Not part of the CLI spec.
    /// </summary>
    [JsonPropertyName("_metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}
