using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Services;

/// <summary>
/// CRUD service for SME agent definitions. Manages templates from config
/// and custom definitions persisted to a JSON file.
/// </summary>
public class SMEAgentDefinitionService
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly McpServerSecurityPolicy _securityPolicy;
    private readonly ILogger<SMEAgentDefinitionService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private Dictionary<string, SMEAgentDefinition>? _customDefinitions;

    public SMEAgentDefinitionService(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        McpServerSecurityPolicy securityPolicy,
        ILogger<SMEAgentDefinitionService> logger)
    {
        _config = config;
        _securityPolicy = securityPolicy;
        _logger = logger;
    }

    /// <summary>Gets all available definitions (templates + custom).</summary>
    public async Task<IReadOnlyDictionary<string, SMEAgentDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        var smeConfig = _config.CurrentValue.SmeAgents;
        var result = new Dictionary<string, SMEAgentDefinition>(smeConfig.Templates);

        if (smeConfig.PersistDefinitions)
        {
            var custom = await LoadCustomDefinitionsAsync(ct);
            foreach (var (id, def) in custom)
            {
                result.TryAdd(id, def); // Templates take priority over custom with same ID
            }
        }

        return result;
    }

    /// <summary>Gets a definition by ID (checks templates first, then custom).</summary>
    public async Task<SMEAgentDefinition?> GetAsync(string definitionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        var smeConfig = _config.CurrentValue.SmeAgents;
        if (smeConfig.Templates.TryGetValue(definitionId, out var template))
            return template;

        if (smeConfig.PersistDefinitions)
        {
            var custom = await LoadCustomDefinitionsAsync(ct);
            if (custom.TryGetValue(definitionId, out var customDef))
                return customDef;
        }

        return null;
    }

    /// <summary>Saves a custom SME definition. Validates against security policy first.</summary>
    public async Task<DefinitionValidationResult> SaveAsync(SMEAgentDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var validation = _securityPolicy.ValidateDefinition(definition);
        if (!validation.IsValid)
            return validation;

        var smeConfig = _config.CurrentValue.SmeAgents;
        if (!smeConfig.PersistDefinitions)
        {
            _logger.LogWarning("SME definition persistence is disabled; definition {Id} will not be saved", definition.DefinitionId);
            return validation;
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var definitions = await LoadCustomDefinitionsAsync(ct);
            definitions[definition.DefinitionId] = definition;
            await PersistCustomDefinitionsAsync(definitions, ct);
            _logger.LogInformation("Saved SME definition {Id} ({RoleName})", definition.DefinitionId, definition.RoleName);
        }
        finally
        {
            _fileLock.Release();
        }

        return validation;
    }

    /// <summary>Deletes a custom SME definition by ID. Cannot delete templates.</summary>
    public async Task<bool> DeleteAsync(string definitionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        var smeConfig = _config.CurrentValue.SmeAgents;
        if (smeConfig.Templates.ContainsKey(definitionId))
        {
            _logger.LogWarning("Cannot delete template definition {Id}", definitionId);
            return false;
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var definitions = await LoadCustomDefinitionsAsync(ct);
            if (!definitions.Remove(definitionId))
                return false;

            await PersistCustomDefinitionsAsync(definitions, ct);
            _logger.LogInformation("Deleted SME definition {Id}", definitionId);
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Finds definitions that match the required capabilities.</summary>
    public async Task<IReadOnlyList<SMEAgentDefinition>> FindByCapabilitiesAsync(
        IEnumerable<string> requiredCapabilities, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        var required = new HashSet<string>(requiredCapabilities, StringComparer.OrdinalIgnoreCase);

        return all.Values
            .Where(def => def.Capabilities.Any(c => required.Contains(c)))
            .OrderByDescending(def => def.Capabilities.Count(c => required.Contains(c)))
            .ToList();
    }

    private async Task<Dictionary<string, SMEAgentDefinition>> LoadCustomDefinitionsAsync(CancellationToken ct)
    {
        if (_customDefinitions is not null)
            return _customDefinitions;

        var path = _config.CurrentValue.SmeAgents.DefinitionsPath;
        if (!File.Exists(path))
        {
            _customDefinitions = new();
            return _customDefinitions;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            _customDefinitions = JsonSerializer.Deserialize<Dictionary<string, SMEAgentDefinition>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load SME definitions from {Path}", path);
            _customDefinitions = new();
        }

        return _customDefinitions;
    }

    private async Task PersistCustomDefinitionsAsync(Dictionary<string, SMEAgentDefinition> definitions, CancellationToken ct)
    {
        var path = _config.CurrentValue.SmeAgents.DefinitionsPath;
        var json = JsonSerializer.Serialize(definitions, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Atomic write via temp file
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);

        _customDefinitions = definitions;
    }
}
