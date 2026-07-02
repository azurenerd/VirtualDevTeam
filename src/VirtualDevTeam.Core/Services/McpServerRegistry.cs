using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Services;

/// <summary>
/// Registry of available MCP servers loaded from configuration.
/// Provides lookup and enumeration for agents and the security policy.
/// </summary>
public class McpServerRegistry
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<McpServerRegistry> _logger;

    public McpServerRegistry(IOptionsMonitor<VirtualDevTeamConfig> config, ILogger<McpServerRegistry> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Gets all registered MCP server definitions.</summary>
    public IReadOnlyDictionary<string, McpServerDefinition> GetAll()
        => _config.CurrentValue.McpServers;

    /// <summary>Gets a specific MCP server definition by name.</summary>
    public McpServerDefinition? Get(string serverName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        return _config.CurrentValue.McpServers.TryGetValue(serverName, out var def) ? def : null;
    }

    /// <summary>Checks if a server name is registered.</summary>
    public bool Contains(string serverName)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        return _config.CurrentValue.McpServers.ContainsKey(serverName);
    }

    /// <summary>Gets all server names that provide a specific capability.</summary>
    public IReadOnlyList<string> FindByCapability(string capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return _config.CurrentValue.McpServers
            .Where(kvp => kvp.Value.ProvidedCapabilities
                .Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>Gets all registered server names.</summary>
    public IReadOnlyList<string> GetServerNames()
        => _config.CurrentValue.McpServers.Keys.ToList();
}
