using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.DevPlatform.Capabilities;

/// <summary>
/// Parses document references from work item body text (markdown and HTML) and resolves
/// each to its file content via <see cref="IRepositoryContentService"/>.
/// </summary>
public partial class DocumentReferenceResolver : IDocumentReferenceResolver
{
    private readonly IRepositoryContentService _repoContent;
    private readonly ILogger<DocumentReferenceResolver> _logger;

    public DocumentReferenceResolver(
        IRepositoryContentService repoContent,
        ILogger<DocumentReferenceResolver> logger)
    {
        _repoContent = repoContent ?? throw new ArgumentNullException(nameof(repoContent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ResolvedDocument>> ResolveReferencesAsync(
        string textContent,
        DocumentResolutionContext context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(textContent))
            return [];

        // Extract all candidate paths from the text
        var candidatePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ExtractMarkdownLinks(textContent, candidatePaths);
        ExtractHtmlAnchors(textContent, candidatePaths);
        ExtractBareFilePaths(textContent, candidatePaths);

        if (context.RepoBaseUrl is not null)
            ExtractRepoUrls(textContent, context.RepoBaseUrl, candidatePaths);

        if (candidatePaths.Count == 0)
            return [];

        _logger.LogDebug("Found {Count} candidate doc references to resolve", candidatePaths.Count);

        // Resolve each path in parallel
        var tasks = candidatePaths.Select(async kvp =>
        {
            try
            {
                var content = await _repoContent.GetFileContentAsync(kvp.Key, context.Branch, ct);
                if (content is not null)
                {
                    _logger.LogDebug("Resolved document reference: {Path}", kvp.Key);
                    return new ResolvedDocument(kvp.Key, content, kvp.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve document reference: {Path}", kvp.Key);
            }

            return null;
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    /// <summary>Extract markdown links: [text](path/to/file.md)</summary>
    private static void ExtractMarkdownLinks(string text, Dictionary<string, string> paths)
    {
        foreach (Match match in MarkdownLinkRegex().Matches(text))
        {
            var path = match.Groups[1].Value;
            if (IsMarkdownFilePath(path))
                paths.TryAdd(NormalizePath(path), match.Value);
        }
    }

    /// <summary>Extract HTML anchors: &lt;a href="path/to/file.md"&gt;</summary>
    private static void ExtractHtmlAnchors(string text, Dictionary<string, string> paths)
    {
        foreach (Match match in HtmlAnchorRegex().Matches(text))
        {
            var path = match.Groups[1].Value;
            if (IsMarkdownFilePath(path))
                paths.TryAdd(NormalizePath(path), match.Value);
        }
    }

    /// <summary>Extract bare file paths that look like agent doc references.</summary>
    private static void ExtractBareFilePaths(string text, Dictionary<string, string> paths)
    {
        foreach (Match match in BareFilePathRegex().Matches(text))
        {
            var path = match.Groups[1].Value;
            if (IsMarkdownFilePath(path))
                paths.TryAdd(NormalizePath(path), path);
        }
    }

    /// <summary>Extract full repository URLs and convert them to relative paths.</summary>
    private static void ExtractRepoUrls(string text, string repoBaseUrl, Dictionary<string, string> paths)
    {
        // GitHub: https://github.com/owner/repo/blob/main/path/to/file.md
        // ADO: https://dev.azure.com/org/project/_git/repo?path=/path/to/file.md
        var normalizedBase = repoBaseUrl.TrimEnd('/');

        // GitHub blob URLs
        var ghPattern = $@"{Regex.Escape(normalizedBase)}/blob/[^/]+/([^\s""'>\)]+\.md)";
        foreach (Match match in Regex.Matches(text, ghPattern, RegexOptions.IgnoreCase))
        {
            paths.TryAdd(NormalizePath(match.Groups[1].Value), match.Value);
        }

        // ADO ?path= URLs
        var adoPattern = @"[?&]path=/?([^\s""'>&]+\.md)";
        foreach (Match match in Regex.Matches(text, adoPattern, RegexOptions.IgnoreCase))
        {
            paths.TryAdd(NormalizePath(match.Groups[1].Value), match.Value);
        }
    }

    private static bool IsMarkdownFilePath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        && !path.Contains('#');

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    // Markdown link: [anything](path.md)
    [GeneratedRegex(@"\[(?:[^\]]*)\]\(([^)]+\.md)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    // HTML anchor: <a href="path.md"> (handles single or double quotes)
    [GeneratedRegex(@"<a\s+[^>]*href=[""']([^""']+\.md)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAnchorRegex();

    // Bare file path: AgentDocs/xxx/anything.md or standalone file.md references on their own line
    [GeneratedRegex(@"(?:^|\s)((?:AgentDocs|\.agentsquad)/[^\s""'<>]+\.md|(?:PMSpec|Research|Architecture|EngineeringPlan|TeamMembers|TeamComposition)\.md)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BareFilePathRegex();
}
