namespace AgentSquad.Core.Prompts;

/// <summary>
/// Loads, parses, and renders prompt templates from .md files with YAML frontmatter
/// and {{variable}} substitution.
/// </summary>
public interface IPromptTemplateService
{
    /// <summary>
    /// Renders a template with variable substitution and fragment includes.
    /// Returns null if the template file does not exist on disk.
    /// </summary>
    /// <param name="templatePath">
    /// Relative path from prompts root, without .md extension.
    /// Example: "researcher/system-message"
    /// </param>
    /// <param name="variables">Key-value pairs for {{variable}} substitution.</param>
    /// <returns>The rendered prompt string, or null if the template file is missing.</returns>
    Task<string?> RenderAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the parsed frontmatter metadata for a template without rendering it.
    /// </summary>
    Task<PromptMetadata?> GetMetadataAsync(
        string templatePath,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all template files for a given agent role directory.
    /// Returns relative paths (e.g., "researcher/system-message").
    /// </summary>
    IReadOnlyList<string> ListTemplates(string role);

    /// <summary>
    /// Gets the raw (unrendered) content of a template file including frontmatter.
    /// Used by the dashboard editor.
    /// </summary>
    Task<string?> GetRawContentAsync(string templatePath, CancellationToken ct = default);

    /// <summary>
    /// Writes raw content to a template file on disk.
    /// Used by the dashboard editor.
    /// </summary>
    Task SaveRawContentAsync(string templatePath, string content, CancellationToken ct = default);

    /// <summary>
    /// Invalidates the cache for a specific template or all templates.
    /// Used by FileSystemWatcher for hot-reload.
    /// </summary>
    void InvalidateCache(string? templatePath = null);
}
