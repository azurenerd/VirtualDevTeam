namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Rich file content result with metadata for binary detection, size limits, and truncation.
/// Used by the file browser UI to properly display files without downloading gigabytes.
/// </summary>
public record RepositoryFileContentResult
{
    /// <summary>Full path of the file relative to repository root.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the file is binary (images, compiled assets, etc.).</summary>
    public required bool IsBinary { get; init; }

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Text content of the file. Null if binary or if file exceeds size limit.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>Whether the content was truncated due to size limits.</summary>
    public bool WasTruncated { get; init; }

    /// <summary>MIME-like content type hint (e.g., "text/csharp", "image/png").</summary>
    public string? ContentType { get; init; }

    /// <summary>File name extracted from path.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>File extension including the dot (e.g., ".cs").</summary>
    public string Extension => System.IO.Path.GetExtension(Path);

    /// <summary>Known binary file extensions.</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg",
        ".exe", ".dll", ".so", ".dylib", ".obj", ".o", ".lib", ".a",
        ".zip", ".gz", ".tar", ".7z", ".rar", ".bz2",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv",
        ".bin", ".dat", ".db", ".sqlite", ".mdb",
        ".class", ".pyc", ".pdb", ".nupkg", ".snupkg"
    };

    /// <summary>
    /// Detect whether a file path is likely binary based on its extension.
    /// </summary>
    public static bool IsBinaryPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && BinaryExtensions.Contains(ext);
    }

    /// <summary>
    /// Infer a content type string from a file extension for syntax highlighting hints.
    /// </summary>
    public static string InferContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "text/csharp",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".jsx" or ".tsx" => "text/typescriptreact",
            ".py" => "text/python",
            ".json" => "application/json",
            ".xml" or ".csproj" or ".props" or ".targets" => "text/xml",
            ".yaml" or ".yml" => "text/yaml",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".scss" or ".sass" => "text/scss",
            ".razor" => "text/razor",
            ".sql" => "text/sql",
            ".sh" or ".bash" => "text/shell",
            ".ps1" => "text/powershell",
            ".dockerfile" or ".containerfile" => "text/dockerfile",
            ".go" => "text/go",
            ".rs" => "text/rust",
            ".java" => "text/java",
            ".rb" => "text/ruby",
            ".swift" => "text/swift",
            ".kt" or ".kts" => "text/kotlin",
            ".toml" => "text/toml",
            ".ini" or ".cfg" => "text/ini",
            ".env" => "text/env",
            ".gitignore" or ".editorconfig" => "text/plain",
            _ => "text/plain"
        };
    }
}
