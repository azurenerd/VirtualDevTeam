using System.Text.RegularExpressions;

namespace AgentSquad.Core.AI;

/// <summary>
/// Parses structured AI output to extract individual code files.
/// Supports two formats:
/// 1. Fenced code blocks with file path annotations: ```language:path/to/file.ext
/// 2. FILE: markers: FILE: path/to/file.ext followed by a fenced code block
/// </summary>
public static partial class CodeFileParser
{
    /// <summary>
    /// A single code file extracted from AI output.
    /// </summary>
    public record CodeFile(string Path, string Content, string? Language = null);

    /// <summary>
    /// Parse AI output text and extract all code files with their paths and content.
    /// </summary>
    public static List<CodeFile> ParseFiles(string aiOutput)
    {
        if (string.IsNullOrWhiteSpace(aiOutput))
            return [];

        var files = new List<CodeFile>();

        // Strategy 1: Look for FILE: path markers followed by code blocks
        files.AddRange(ParseFileMarkerFormat(aiOutput));

        // Strategy 2: Look for annotated fenced code blocks (```lang:path/file.ext)
        if (files.Count == 0)
            files.AddRange(ParseAnnotatedCodeBlocks(aiOutput));

        // Strategy 3: Look for **`path/file.ext`** or ### `path/file.ext` headers followed by code blocks
        if (files.Count == 0)
            files.AddRange(ParseHeaderPathFormat(aiOutput));

        // Deduplicate by path (last occurrence wins)
        var deduplicated = new Dictionary<string, CodeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var normalizedPath = NormalizePath(file.Path);
            if (!string.IsNullOrWhiteSpace(normalizedPath) && !string.IsNullOrWhiteSpace(file.Content))
                deduplicated[normalizedPath] = file with { Path = normalizedPath };
        }

        return [.. deduplicated.Values];
    }

    /// <summary>
    /// Format: FILE: path/to/file.ext\n```lang\ncontent\n```
    /// </summary>
    private static List<CodeFile> ParseFileMarkerFormat(string text)
    {
        var files = new List<CodeFile>();
        var matches = FileMarkerRegex().Matches(text);

        foreach (Match match in matches)
        {
            var path = match.Groups["path"].Value.Trim();
            var lang = match.Groups["lang"].Value.Trim();
            var content = match.Groups["content"].Value;

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(content))
                files.Add(new CodeFile(path, content.TrimEnd(), string.IsNullOrEmpty(lang) ? null : lang));
        }

        return files;
    }

    /// <summary>
    /// Format: ```typescript:src/components/Button.tsx\ncontent\n```
    /// Also supports ```lang filepath=path/to/file\ncontent\n```
    /// </summary>
    private static List<CodeFile> ParseAnnotatedCodeBlocks(string text)
    {
        var files = new List<CodeFile>();
        var matches = AnnotatedBlockRegex().Matches(text);

        foreach (Match match in matches)
        {
            var lang = match.Groups["lang"].Value.Trim();
            var path = match.Groups["path"].Value.Trim();
            var content = match.Groups["content"].Value;

            if (string.IsNullOrWhiteSpace(path))
            {
                // Try filepath= syntax
                var fpMatch = FilePathAttrRegex().Match(match.Value);
                if (fpMatch.Success)
                    path = fpMatch.Groups["fp"].Value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(content))
                files.Add(new CodeFile(path, content.TrimEnd(), string.IsNullOrEmpty(lang) ? null : lang));
        }

        return files;
    }

    /// <summary>
    /// Format: **`path/file.ext`** or ### `path/file.ext` or #### path/file.ext
    /// followed by a fenced code block
    /// </summary>
    private static List<CodeFile> ParseHeaderPathFormat(string text)
    {
        var files = new List<CodeFile>();
        var matches = HeaderPathRegex().Matches(text);

        foreach (Match match in matches)
        {
            var path = match.Groups["path"].Value.Trim().Trim('`');
            var lang = match.Groups["lang"].Value.Trim();
            var content = match.Groups["content"].Value;

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(content) && LooksLikeFilePath(path))
                files.Add(new CodeFile(path, content.TrimEnd(), string.IsNullOrEmpty(lang) ? null : lang));
        }

        return files;
    }

    private static string NormalizePath(string path)
    {
        // Remove leading slashes, ./ prefix, quotes
        var normalized = path.Trim().Trim('"', '\'', '`');
        if (normalized.StartsWith("./"))
            normalized = normalized[2..];
        if (normalized.StartsWith('/'))
            normalized = normalized[1..];
        return normalized.Replace('\\', '/');
    }

    private static bool LooksLikeFilePath(string text)
    {
        // Must contain a dot for extension and a slash or be a dotfile
        return (text.Contains('/') || text.Contains('\\')) && text.Contains('.');
    }

    // FILE: path/to/file.ext (with optional surrounding ** or `)
    // followed by a code block
    [GeneratedRegex(
        @"(?:^|\n)\s*(?:\*\*)?FILE:\s*(?:`)?(?<path>[^\n`*]+?)(?:`)?(?:\*\*)?\s*\n\s*```(?<lang>\w*)\n(?<content>.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FileMarkerRegex();

    // ```lang:path/to/file.ext or ```lang path/to/file.ext
    [GeneratedRegex(
        @"```(?<lang>\w+)[:\s](?<path>[^\n]+?)\n(?<content>.*?)```",
        RegexOptions.Singleline)]
    private static partial Regex AnnotatedBlockRegex();

    // filepath="..." or filepath=... attribute
    [GeneratedRegex(
        @"filepath\s*=\s*[""']?(?<fp>[^\s""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex FilePathAttrRegex();

    // **`path/file.ext`** or ### path/file.ext or #### `path/file.ext` followed by code block
    [GeneratedRegex(
        @"(?:^|\n)\s*(?:#{2,5}\s*|\*\*)?(?:`)?(?<path>[^\n`*]+\.\w+)(?:`)?(?:\*\*)?\s*\n+\s*```(?<lang>\w*)\n(?<content>.*?)```",
        RegexOptions.Singleline)]
    private static partial Regex HeaderPathRegex();
}
