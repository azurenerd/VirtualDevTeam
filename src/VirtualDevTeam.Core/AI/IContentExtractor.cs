namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Extracts meaningful text content from raw fetched data based on content type.
/// Implementations handle HTML, Markdown, OpenAPI specs, etc.
/// </summary>
public interface IContentExtractor
{
    /// <summary>Whether this extractor can handle the given URL and content type.</summary>
    bool CanHandle(string url, string? contentType);

    /// <summary>Extracts clean, meaningful text from raw content.</summary>
    string Extract(string rawContent, string url);
}
