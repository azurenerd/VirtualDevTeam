using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Detects potential file path and type/namespace conflicts between files being committed
/// and the existing repository structure. Used to warn agents about duplicate code before
/// it gets committed, preventing the overlapping-files problem.
/// </summary>
public class ConflictDetector
{
    private readonly IGitHubService _github;
    private readonly ILogger<ConflictDetector> _logger;

    // Cache the main branch tree to avoid repeated API calls within a session
    private IReadOnlyList<string>? _cachedTree;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public ConflictDetector(IGitHubService github, ILogger<ConflictDetector> logger)
    {
        _github = github;
        _logger = logger;
    }

    /// <summary>
    /// Gets the repository file tree from main, with 5-minute caching.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRepoTreeAsync(CancellationToken ct = default)
    {
        if (_cachedTree is not null && DateTime.UtcNow < _cacheExpiry)
            return _cachedTree;

        _cachedTree = await _github.GetRepositoryTreeAsync("main", ct);
        _cacheExpiry = DateTime.UtcNow + CacheTtl;
        return _cachedTree;
    }

    /// <summary>
    /// Formats the repo tree as a condensed directory listing suitable for inclusion in prompts.
    /// Groups files by directory and shows the tree structure.
    /// </summary>
    public static string FormatTreeForPrompt(IReadOnlyList<string> treePaths, int maxLines = 150)
    {
        if (treePaths.Count == 0)
            return "(empty repository)";

        // Group by top-level directory and show structure
        var lines = new List<string>();
        var dirs = new Dictionary<string, List<string>>();

        foreach (var path in treePaths)
        {
            var parts = path.Split('/');
            var dir = parts.Length > 1 ? string.Join("/", parts[..^1]) : ".";
            var file = parts[^1];

            if (!dirs.ContainsKey(dir))
                dirs[dir] = new List<string>();
            dirs[dir].Add(file);
        }

        foreach (var (dir, files) in dirs.OrderBy(d => d.Key))
        {
            lines.Add($"{dir}/");
            foreach (var file in files.OrderBy(f => f))
            {
                lines.Add($"  {file}");
                if (lines.Count >= maxLines)
                {
                    lines.Add($"  ... and {treePaths.Count - lines.Count} more files (truncated)");
                    return string.Join("\n", lines);
                }
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Detects potential conflicts between files being committed and the existing repo.
    /// Returns a list of warning messages, or empty if no conflicts found.
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectConflictsAsync(
        IReadOnlyList<(string Path, string Content)> files, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var repoTree = await GetRepoTreeAsync(ct);
        if (repoTree.Count == 0) return warnings;

        // Build lookup structures
        var existingByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in repoTree)
        {
            var fileName = path.Split('/')[^1];
            if (!existingByFileName.ContainsKey(fileName))
                existingByFileName[fileName] = new List<string>();
            existingByFileName[fileName].Add(path);
        }

        // Extract existing types from repo tree (we'd need file content for full check,
        // but we can detect file-name-level duplicates cheaply)
        foreach (var (filePath, content) in files)
        {
            var fileName = filePath.Split('/')[^1];

            // Check 1: Same filename exists in a DIFFERENT directory
            if (existingByFileName.TryGetValue(fileName, out var existingPaths))
            {
                var conflicts = existingPaths
                    .Where(ep => !string.Equals(ep, filePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (conflicts.Count > 0)
                {
                    warnings.Add(
                        $"⚠️ **Potential duplicate file:** `{filePath}` — " +
                        $"a file named `{fileName}` already exists at: {string.Join(", ", conflicts.Select(c => $"`{c}`"))}. " +
                        "Consider modifying the existing file instead of creating a new one.");
                }
            }

            // Check 2: For C# files, parse namespace/type declarations and check for conflicts
            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var typeConflicts = DetectTypeConflicts(filePath, content, repoTree);
                warnings.AddRange(typeConflicts);
            }
        }

        return warnings;
    }

    /// <summary>
    /// Regex-based detection of type/namespace conflicts in C# files.
    /// Parses the file being committed for class/struct/record/interface/enum declarations,
    /// then checks if the same type names appear in existing repo files.
    /// </summary>
    private static List<string> DetectTypeConflicts(string filePath, string content, IReadOnlyList<string> repoTree)
    {
        var warnings = new List<string>();

        // Parse namespace from the new file
        var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)\s*[;{]");
        var newNamespace = nsMatch.Success ? nsMatch.Groups[1].Value : null;

        // Parse top-level type declarations
        var typeMatches = Regex.Matches(content,
            @"(?:public|internal|private|protected)?\s*(?:abstract|sealed|static|partial)?\s*(?:class|struct|record|interface|enum)\s+(\w+)");

        foreach (Match typeMatch in typeMatches)
        {
            var typeName = typeMatch.Groups[1].Value;
            // Skip common/generic names that are likely intentional
            if (typeName is "Program" or "Startup" or "App") continue;

            // Check if any existing file in the repo likely defines the same type
            // (by checking filenames — a file named "MyType.cs" likely defines MyType)
            var matchingFiles = repoTree
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Where(p =>
                {
                    var existingFileName = p.Split('/')[^1];
                    var nameWithoutExt = existingFileName[..^3]; // strip .cs
                    return string.Equals(nameWithoutExt, typeName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (matchingFiles.Count > 0)
            {
                warnings.Add(
                    $"⚠️ **Potential type conflict:** `{typeName}` (in `{filePath}`, namespace `{newNamespace ?? "unknown"}`) — " +
                    $"a file named `{typeName}.cs` already exists at: {string.Join(", ", matchingFiles.Select(f => $"`{f}`"))}. " +
                    "This may cause duplicate type definitions or namespace collisions.");
            }
        }

        return warnings;
    }
}
