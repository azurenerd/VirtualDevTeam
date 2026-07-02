namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Result of a repository tree query — contains the branch used and all file paths.
/// </summary>
public record RepositoryFileTreeResult
{
    /// <summary>The branch name that was queried.</summary>
    public required string Branch { get; init; }

    /// <summary>Flat list of file paths relative to repo root.</summary>
    public required IReadOnlyList<string> Files { get; init; }
}
