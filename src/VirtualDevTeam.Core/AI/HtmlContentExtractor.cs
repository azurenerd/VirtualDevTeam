using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Extracts readable text from HTML content using a simplified readability approach.
/// Strips scripts, styles, navigation, and extracts main content.
/// </summary>
public partial class HtmlContentExtractor : IContentExtractor
{
    public bool CanHandle(string url, string? contentType)
        => contentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
           || url.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
           || url.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
           || (!url.Contains('.') || !HasKnownNonHtmlExtension(url));

    public string Extract(string rawContent, string url)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return "";

        var text = rawContent;

        // Remove script and style blocks entirely
        text = ScriptPattern().Replace(text, " ");
        text = StylePattern().Replace(text, " ");

        // Remove HTML comments
        text = CommentPattern().Replace(text, " ");

        // Try to extract main content areas (article, main, content divs)
        var mainContent = ExtractMainContent(text);
        if (!string.IsNullOrWhiteSpace(mainContent))
            text = mainContent;

        // Strip remaining HTML tags
        text = TagPattern().Replace(text, " ");

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Normalize whitespace
        text = WhitespacePattern().Replace(text, " ").Trim();

        return text;
    }

    private static string? ExtractMainContent(string html)
    {
        // Try common content containers in priority order
        string[] patterns =
        [
            @"<article[^>]*>(.*?)</article>",
            @"<main[^>]*>(.*?)</main>",
            @"<div[^>]*(?:class|id)\s*=\s*""[^""]*(?:content|article|post|entry)[^""]*""[^>]*>(.*?)</div>"
        ];

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (match.Success && match.Groups[1].Value.Length > 100)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static bool HasKnownNonHtmlExtension(string url)
    {
        var uri = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        return uri.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || uri.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               || uri.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
               || uri.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
               || uri.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
               || uri.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StylePattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
