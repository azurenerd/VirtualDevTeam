namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Thrown when a platform operation fails due to a conflict — e.g., duplicate PR creation,
/// PR not mergeable due to conflicts, or a work item already in terminal state.
/// Wraps provider-specific exceptions (Octokit.ApiValidationException, Octokit.PullRequestNotMergeableException, etc.)
/// so workflow logic can catch a single platform-neutral type.
/// </summary>
public class PlatformConflictException : Exception
{
    /// <summary>The kind of conflict that occurred.</summary>
    public PlatformConflictKind Kind { get; }

    public PlatformConflictException(PlatformConflictKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}

/// <summary>Classifies the type of platform conflict.</summary>
public enum PlatformConflictKind
{
    /// <summary>A resource with the same identity already exists (e.g., duplicate PR title/branch).</summary>
    AlreadyExists,

    /// <summary>A PR cannot be merged due to branch conflicts with the target.</summary>
    NotMergeable,

    /// <summary>A validation rule on the platform was violated.</summary>
    ValidationFailed,

    /// <summary>An unclassified conflict.</summary>
    Other
}
