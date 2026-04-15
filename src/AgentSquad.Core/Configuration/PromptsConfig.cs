namespace AgentSquad.Core.Configuration;

/// <summary>
/// Configuration for the prompt template system.
/// Controls where prompt .md files are loaded from and hot-reload behavior.
/// </summary>
public class PromptsConfig
{
    /// <summary>
    /// Root directory for prompt template files, relative to the application working directory.
    /// Templates are organized as {BasePath}/{role}/{prompt-name}.md
    /// </summary>
    public string BasePath { get; set; } = "prompts";

    /// <summary>
    /// Enable FileSystemWatcher to automatically invalidate cached templates when files change.
    /// Useful during development and for dashboard UI editing. Defaults to true.
    /// </summary>
    public bool HotReload { get; set; } = true;

    /// <summary>
    /// Maximum depth for nested fragment includes ({{> path/to/fragment}}).
    /// Prevents infinite recursion from circular includes.
    /// </summary>
    public int MaxIncludeDepth { get; set; } = 10;
}
