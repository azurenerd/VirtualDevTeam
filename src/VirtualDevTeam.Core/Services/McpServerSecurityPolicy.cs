using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Services;

/// <summary>
/// Security policy for MCP servers and SME agent definitions.
/// Validates that only registered, non-dangerous servers are used
/// and that definitions meet safety constraints.
/// </summary>
public class McpServerSecurityPolicy
{
    private readonly McpServerRegistry _registry;

    private static readonly HashSet<string> BlockedServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell", "exec", "terminal", "cmd", "powershell", "bash"
    };

    public McpServerSecurityPolicy(McpServerRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Checks if a server name is allowed by policy.</summary>
    public bool IsServerAllowed(string serverName)
    {
        if (BlockedServers.Contains(serverName))
            return false;
        return _registry.Contains(serverName);
    }

    /// <summary>Validates a complete SME agent definition against security policy.</summary>
    public DefinitionValidationResult ValidateDefinition(SMEAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.DefinitionId))
            errors.Add("DefinitionId is required");

        if (string.IsNullOrWhiteSpace(definition.RoleName))
            errors.Add("RoleName is required");

        if (string.IsNullOrWhiteSpace(definition.SystemPrompt))
            errors.Add("SystemPrompt is required");

        if (definition.SystemPrompt.Length > 5000)
            errors.Add("SystemPrompt exceeds maximum length (5000 chars)");

        foreach (var server in definition.McpServers)
        {
            if (BlockedServers.Contains(server))
                errors.Add($"MCP server '{server}' is blocked by security policy");
            else if (!_registry.Contains(server))
                errors.Add($"MCP server '{server}' is not in the registry");
        }

        foreach (var url in definition.KnowledgeLinks)
        {
            if (!IsUrlSafe(url))
                errors.Add($"Knowledge link '{url}' must use HTTPS and not target private networks");
        }

        if (definition.MaxInstances < 1 || definition.MaxInstances > 10)
            errors.Add("MaxInstances must be between 1 and 10");

        var validTiers = new[] { "premium", "standard", "budget", "local" };
        if (!validTiers.Contains(definition.ModelTier, StringComparer.OrdinalIgnoreCase))
            errors.Add($"ModelTier '{definition.ModelTier}' is not valid. Use: {string.Join(", ", validTiers)}");

        return new DefinitionValidationResult(errors);
    }

    private static bool IsUrlSafe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Only allow HTTPS
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Block private/local networks
        if (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "::1")
            return false;

        if (uri.Host.StartsWith("10.") || uri.Host.StartsWith("192.168.") || uri.Host.StartsWith("172."))
            return false;

        return true;
    }
}

/// <summary>Result of validating an SME agent definition.</summary>
public record DefinitionValidationResult
{
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public DefinitionValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }
}
