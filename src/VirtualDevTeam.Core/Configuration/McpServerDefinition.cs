namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Defines an MCP server that agents can use for tool calls.
/// Contains launch configuration and capability metadata.
/// </summary>
public record McpServerDefinition
{
    /// <summary>Unique name for this server (e.g., "github", "playwright", "fetch")</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of what this server provides</summary>
    public string Description { get; init; } = "";

    /// <summary>Command to launch the server (e.g., "npx", "uvx", "docker")</summary>
    public string Command { get; init; } = "";

    /// <summary>Arguments for the launch command (e.g., ["-y", "@modelcontextprotocol/server-github"])</summary>
    public List<string> Args { get; init; } = [];

    /// <summary>Environment variables for the server process</summary>
    public Dictionary<string, string> Env { get; init; } = new();

    /// <summary>Transport type for MCP communication</summary>
    public McpTransportType Transport { get; init; } = McpTransportType.Stdio;

    /// <summary>URL for HTTP/SSE transport servers</summary>
    public string? Url { get; init; }

    /// <summary>Runtime prerequisites (e.g., ["node", "python", "docker"])</summary>
    public List<string> RequiredRuntimes { get; init; } = [];

    /// <summary>Capability keywords this server provides (e.g., ["github-issues", "github-prs"])</summary>
    public List<string> ProvidedCapabilities { get; init; } = [];

    /// <summary>
    /// Tool names to explicitly grant via <c>--allow-tool=serverName</c>.
    /// When non-empty, <see cref="ChatCompletionRunner"/> will auto-inject
    /// the invocation context for this server on every LLM call, granting
    /// the server name as an allow-tool entry.
    /// </summary>
    /// <remarks>
    /// The Copilot CLI uses server-level grants: <c>--allow-tool=workiq</c> permits
    /// all tools on that server. Listing tools here acts as documentation of intended
    /// use and enables future fine-grained filtering.
    /// </remarks>
    public List<string> AllowedTools { get; init; } = [];
}

public enum McpTransportType
{
    Stdio,
    Http,
    Sse
}
