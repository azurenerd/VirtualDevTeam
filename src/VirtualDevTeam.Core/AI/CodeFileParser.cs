using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.AI;

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
    /// Characters that are never valid in file path segments.
    /// Covers Windows invalid chars plus code-fragment chars that indicate parsing errors.
    /// </summary>
    private static readonly HashSet<char> InvalidPathChars =
    [
        '{', '}', '[', ']', '|', '<', '>', '?', '*', ';', '=', '!', '#', '$', '%', '^', '&',
        '"', '\'', '`', '\t', '\r', '\0'
    ];

    /// <summary>
    /// Known file extensions for code, config, and documentation files.
    /// </summary>
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".csproj", ".sln", ".props", ".targets",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".razor", ".cshtml", ".vbhtml",
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".env",
        ".py", ".pyx", ".pyi", ".rb", ".go", ".rs", ".java", ".kt", ".swift",
        ".c", ".cpp", ".h", ".hpp",
        ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd",
        ".md", ".txt", ".rst", ".adoc",
        ".sql", ".graphql", ".gql",
        ".proto", ".wasm", ".wat",
        ".dockerfile", ".dockerignore", ".editorconfig", ".gitignore", ".gitattributes",
        ".config", ".settings", ".resx", ".xaml",
        ".svg", ".ico",
    };

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

        // Deduplicate by path (last occurrence wins), validating each path
        var deduplicated = new Dictionary<string, CodeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var normalizedPath = NormalizePath(file.Path);
            if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(file.Content))
                continue;

            if (!IsValidFilePath(normalizedPath))
                continue; // Skip malformed paths — code fragments, directives, etc.

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
        normalized = normalized.Replace('\\', '/');

        // Strip AI directives like "(APPEND)", "(CREATE)", "(MODIFY)" from path
        normalized = DirectiveSuffixRegex().Replace(normalized, "").TrimEnd();

        // Strip absolute Windows path prefixes (e.g., C:/Git/VirtualDevTeam/src/...)
        var driveMatch = System.Text.RegularExpressions.Regex.Match(normalized, @"^[A-Za-z]:/");
        if (driveMatch.Success)
        {
            // Find the first path segment that looks like a project root (src/, lib/, app/, etc.)
            var srcIdx = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            if (srcIdx >= 0)
                normalized = normalized[(srcIdx + 1)..]; // keep "src/..."
            else
            {
                // Fallback: strip drive and common prefix directories
                normalized = normalized[driveMatch.Length..];
            }
        }

        return normalized;
    }

    /// <summary>
    /// Validates that a normalized path is a legitimate file path, not a code fragment or directive.
    /// </summary>
    internal static bool IsValidFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2)
            return false;

        // Reject paths that contain invalid characters (code fragments, brackets, etc.)
        foreach (var ch in path)
        {
            if (InvalidPathChars.Contains(ch))
                return false;
        }

        // Reject paths starting with @ (Razor directives like @using, @page, @inject)
        if (path.StartsWith('@'))
            return false;

        // Every path segment must be non-empty and not start with a dot followed by a space
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue; // trailing slash is okay
            if (segment.All(c => !char.IsLetterOrDigit(c)))
                return false; // segment is all punctuation
        }

        // The final segment (filename) must have a recognized extension or at least contain a dot
        var fileName = segments[^1];
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // Dotfiles like .gitignore, .editorconfig are valid
        if (fileName.StartsWith('.') && fileName.Length > 1 && !fileName[1..].Contains('.'))
        {
            // Check it's a known dotfile extension
            var asDotFile = fileName.ToLowerInvariant();
            return KnownExtensions.Contains(asDotFile) ||
                   asDotFile is ".gitignore" or ".gitattributes" or ".editorconfig" or ".dockerignore" or ".env";
        }

        // Must have a file extension
        var extIdx = fileName.LastIndexOf('.');
        if (extIdx < 0 || extIdx == fileName.Length - 1)
            return false;

        // Check the extension is recognized (if not, still allow if it looks reasonable)
        var ext = fileName[extIdx..].ToLowerInvariant();
        if (KnownExtensions.Contains(ext))
            return true;

        // Allow unknown extensions if the rest of the path looks reasonable (has path separator)
        return path.Contains('/') && ext.Length <= 8 && ext.All(c => char.IsLetterOrDigit(c) || c == '.');
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

    // AI directive suffixes like (APPEND), (CREATE), (MODIFY), (NEW) at end of path
    [GeneratedRegex(
        @"\s*\((?:APPEND|CREATE|MODIFY|NEW|UPDATE|REPLACE|DELETE)\)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveSuffixRegex();
}
