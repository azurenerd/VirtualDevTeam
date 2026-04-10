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

            // Check 1: Same filename exists at a likely-duplicate path
            // Only warn when the parent directory structure overlaps (e.g., Pages/Index.razor
            // vs src/Pages/Index.razor). Don't warn for legitimately different directories
            // (e.g., Components/Header.razor vs Shared/Header.razor).
            if (existingByFileName.TryGetValue(fileName, out var existingPaths))
            {
                var conflicts = existingPaths
                    .Where(ep => !string.Equals(ep, filePath, StringComparison.OrdinalIgnoreCase)
                                 && IsSuspiciousDuplicate(filePath, ep))
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
    /// Auto-corrects file paths that are missing the project subdirectory prefix.
    /// When an AI generates "Components/Header.razor" but the repo already has
    /// "src/MyProject/Components/Header.razor", this rewrites to the correct full path.
    /// Files with no match in the repo tree are left unchanged.
    /// </summary>
    public async Task<IReadOnlyList<(string Path, string Content)>> ResolvePathsAsync(
        IReadOnlyList<(string Path, string Content)> files, CancellationToken ct = default)
    {
        var repoTree = await GetRepoTreeAsync(ct);
        if (repoTree.Count == 0) return files;

        // Build lookup: filename → list of full paths in repo
        var existingByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in repoTree)
        {
            var fileName = path.Split('/')[^1];
            if (!existingByFileName.ContainsKey(fileName))
                existingByFileName[fileName] = [];
            existingByFileName[fileName].Add(path);
        }

        var resolved = new List<(string Path, string Content)>(files.Count);
        foreach (var (filePath, content) in files)
        {
            var normalized = filePath.Replace('\\', '/').TrimStart('/');
            var fileName = normalized.Split('/')[^1];

            // If the exact path already exists in repo, keep it
            if (repoTree.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add((normalized, content));
                continue;
            }

            // Look for a matching file where our path is a suffix of the existing path
            if (existingByFileName.TryGetValue(fileName, out var existingPaths))
            {
                var match = existingPaths.FirstOrDefault(ep =>
                    ep.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ep, normalized, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    _logger.LogInformation(
                        "Auto-corrected file path: {Original} → {Corrected}", normalized, match);
                    resolved.Add((match, content));
                    continue;
                }
            }

            // No match — try to infer project prefix from repo structure
            var correctedPath = InferProjectPrefix(normalized, repoTree);
            if (correctedPath is not null && !string.Equals(correctedPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Inferred project prefix for file path: {Original} → {Corrected}", normalized, correctedPath);
                resolved.Add((correctedPath, content));
            }
            else
            {
                resolved.Add((normalized, content));
            }
        }

        return resolved;
    }

    /// <summary>
    /// Infers the project subdirectory prefix by finding a common root among existing repo files
    /// that share a similar directory structure with the new file path.
    /// E.g., if the repo has "src/MyProject/Components/Foo.razor" and we're adding
    /// "Components/Bar.razor", infers "src/MyProject/Components/Bar.razor".
    /// </summary>
    private static string? InferProjectPrefix(string filePath, IReadOnlyList<string> repoTree)
    {
        // Get the first directory segment of the new file (e.g., "Components" from "Components/Header.razor")
        var firstSlash = filePath.IndexOf('/');
        if (firstSlash < 0) return null;
        var firstDir = filePath[..firstSlash];

        // Find existing repo paths that contain this directory segment
        // and extract the prefix before it
        string? bestPrefix = null;
        var bestPrefixCount = 0;

        foreach (var existing in repoTree)
        {
            var idx = existing.IndexOf($"/{firstDir}/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var prefix = existing[..idx];
            // Count how many repo files share this prefix — more matches = more likely correct
            var count = repoTree.Count(t => t.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
            if (count > bestPrefixCount)
            {
                bestPrefixCount = count;
                bestPrefix = prefix;
            }
        }

        if (bestPrefix is not null && bestPrefixCount >= 2)
            return $"{bestPrefix}/{filePath}";

        return null;
    }

    /// <summary>
    /// Determines if two file paths with the same filename are likely duplicates rather
    /// than legitimately different files. A duplicate is suspected when one path is a
    /// suffix of the other (e.g., Pages/Index.razor vs src/Pages/Index.razor) — meaning
    /// an agent created the same file under a slightly different root prefix.
    /// </summary>
    private static bool IsSuspiciousDuplicate(string newPath, string existingPath)
    {
        // Normalize separators
        var a = newPath.Replace('\\', '/').TrimStart('/');
        var b = existingPath.Replace('\\', '/').TrimStart('/');

        // One path ends with the other (e.g., "Pages/Index.razor" is suffix of "src/Pages/Index.razor")
        if (a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
            b.EndsWith(a, StringComparison.OrdinalIgnoreCase))
            return true;

        // Same parent directory name (e.g., both under "Pages/")
        var aParent = GetParentDir(a);
        var bParent = GetParentDir(b);
        if (aParent is not null && bParent is not null &&
            string.Equals(aParent, bParent, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;

        static string? GetParentDir(string path)
        {
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash <= 0) return null;
            var parentStart = path.LastIndexOf('/', lastSlash - 1);
            return parentStart >= 0 ? path[(parentStart + 1)..lastSlash] : path[..lastSlash];
        }
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
