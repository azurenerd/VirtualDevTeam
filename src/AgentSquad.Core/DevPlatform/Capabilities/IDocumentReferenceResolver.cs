namespace AgentSquad.Core.DevPlatform.Capabilities;

/// <summary>
/// Resolves document references (markdown links, HTML anchors, bare file paths, repo URLs)
/// found in work item bodies or comments. Used by agents in single-issue mode to retrieve
/// the full content of referenced agent-generated documents.
/// </summary>
public interface IDocumentReferenceResolver
{
    /// <summary>
    /// Scan <paramref name="textContent"/> for file references and resolve each to its content.
    /// Only <c>.md</c> files are resolved; other file types are ignored.
    /// </summary>
    Task<IReadOnlyList<ResolvedDocument>> ResolveReferencesAsync(
        string textContent,
        DocumentResolutionContext context,
        CancellationToken ct = default);
}

/// <summary>Context required by the resolver to fetch files from the repository.</summary>
/// <param name="Branch">Git branch or ref to read files from (e.g., "main").</param>
/// <param name="Platform">Current platform type (GitHub or AzureDevOps).</param>
/// <param name="RepoBaseUrl">Base URL of the repository, used to match full repo URLs back to relative paths.</param>
public record DocumentResolutionContext(
    string? Branch,
    string? Platform = null,
    string? RepoBaseUrl = null);

/// <summary>A successfully resolved document with its content.</summary>
/// <param name="Path">Relative path within the repository (e.g., "AgentDocs/101/PMSpec.md").</param>
/// <param name="Content">Full text content of the resolved document.</param>
/// <param name="SourceLink">The original link text that was resolved (for diagnostics).</param>
public record ResolvedDocument(
    string Path,
    string Content,
    string SourceLink);
