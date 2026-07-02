using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Extracts content from Markdown files. Preserves structure but strips
/// excessive formatting, code blocks, and metadata headers.
/// </summary>
public partial class MarkdownContentExtractor : IContentExtractor
{
    public bool CanHandle(string url, string? contentType)
        => url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
           || url.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
           || contentType?.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) == true;

    public string Extract(string rawContent, string url)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return "";

        var text = rawContent;

        // Remove YAML front matter
        text = FrontMatterPattern().Replace(text, "");

        // Remove large code blocks (keep short inline code)
        text = LargeCodeBlockPattern().Replace(text, match =>
        {
            var code = match.Value;
            return code.Length > 500 ? "[code block omitted]" : code;
        });

        // Remove image syntax but keep alt text
        text = ImagePattern().Replace(text, "$1");

        // Remove link syntax but keep text
        text = LinkPattern().Replace(text, "$1");

        // Remove HTML tags embedded in markdown
        text = HtmlTagPattern().Replace(text, " ");

        // Normalize whitespace (preserve single line breaks for structure)
        text = MultipleBlankLines().Replace(text, "\n\n");

        return text.Trim();
    }

    [GeneratedRegex(@"^---\s*\n.*?\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatterPattern();

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.None)]
    private static partial Regex LargeCodeBlockPattern();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]+\)")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
