namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Centralized parser for unified diff output (git diff). Extracts per-file change
/// statistics without duplicating parsing logic across adapters.
/// </summary>
public static class PatchAnalyzer
{
    /// <summary>
    /// Parse a unified diff string into a list of file changes with line counts.
    /// Handles standard git diff output including renames, binary files, and empty patches.
    /// </summary>
    public static IReadOnlyList<FileChange> Parse(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
            return Array.Empty<FileChange>();

        var results = new List<FileChange>();
        string? currentPath = null;
        FileChangeType currentType = FileChangeType.Modified;
        int linesAdded = 0;
        int linesRemoved = 0;
        bool isBinary = false;

        foreach (var rawLine in patch.AsSpan().EnumerateLines())
        {
            var line = rawLine.ToString();

            // New file header: "diff --git a/path b/path"
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // Flush previous file
                if (currentPath is not null)
                {
                    results.Add(new FileChange
                    {
                        Path = currentPath,
                        Type = currentType,
                        LinesAdded = linesAdded,
                        LinesRemoved = linesRemoved,
                        IsBinary = isBinary,
                    });
                }

                // Extract path from "diff --git a/foo b/foo" → "foo"
                currentPath = ExtractPathFromDiffLine(line);
                currentType = FileChangeType.Modified; // default, refined by subsequent lines
                linesAdded = 0;
                linesRemoved = 0;
                isBinary = false;
                continue;
            }

            if (currentPath is null) continue;

            // Detect new/deleted files
            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                currentType = FileChangeType.Added;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                currentType = FileChangeType.Deleted;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                currentType = FileChangeType.Renamed;
            }
            else if (line.StartsWith("Binary files", StringComparison.Ordinal))
            {
                isBinary = true;
            }
            else if (line.Length > 0 && line[0] == '+' && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                linesAdded++;
            }
            else if (line.Length > 0 && line[0] == '-' && !line.StartsWith("---", StringComparison.Ordinal))
            {
                linesRemoved++;
            }
        }

        // Flush last file
        if (currentPath is not null)
        {
            results.Add(new FileChange
            {
                Path = currentPath,
                Type = currentType,
                LinesAdded = linesAdded,
                LinesRemoved = linesRemoved,
                IsBinary = isBinary,
            });
        }

        return results;
    }

    /// <summary>
    /// Extract the file path from a "diff --git a/path b/path" line.
    /// Takes the b/ path (destination) which handles renames correctly.
    /// </summary>
    internal static string ExtractPathFromDiffLine(string line)
    {
        // "diff --git a/src/foo.cs b/src/foo.cs"
        // Find the last " b/" which marks the destination path
        var bIdx = line.LastIndexOf(" b/", StringComparison.Ordinal);
        if (bIdx >= 0)
            return line[(bIdx + 3)..];

        // Fallback: take everything after "diff --git " and split on space
        var afterPrefix = line["diff --git ".Length..];
        var parts = afterPrefix.Split(' ', 2);
        if (parts.Length == 2)
        {
            var bPart = parts[1];
            return bPart.StartsWith("b/", StringComparison.Ordinal) ? bPart[2..] : bPart;
        }

        return afterPrefix.StartsWith("a/", StringComparison.Ordinal) ? afterPrefix[2..] : afterPrefix;
    }
}

/// <summary>Represents a single file change extracted from a unified diff.</summary>
public record FileChange
{
    public required string Path { get; init; }
    public required FileChangeType Type { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public bool IsBinary { get; init; }
}

public enum FileChangeType
{
    Added,
    Modified,
    Deleted,
    Renamed,
}
