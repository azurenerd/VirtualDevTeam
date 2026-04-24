namespace AgentSquad.Core.DevPlatform.Models;

/// <summary>
/// Platform-agnostic pull request. Maps to GitHub PR or Azure DevOps Pull Request.
/// </summary>
public record PlatformPullRequest
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "open";
    public string HeadBranch { get; init; } = "";
    public string HeadSha { get; init; } = "";
    public string BaseBranch { get; init; } = "main";
    public string? AssignedAgent { get; init; }
    public string Url { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? MergedAt { get; init; }
    public bool IsMerged => MergedAt.HasValue;
    public List<string> Labels { get; init; } = new();
    public List<string> ReviewComments { get; init; } = new();
    public List<PlatformComment> Comments { get; init; } = new();
    public List<string> ChangedFiles { get; init; } = new();
    public string? MergeableState { get; init; }
}

/// <summary>
/// Platform-agnostic work item. Maps to GitHub Issue or Azure DevOps Work Item.
/// </summary>
public record PlatformWorkItem
{
    /// <summary>
    /// Platform-specific internal ID. GitHub: internal ID (for sub-issue API). ADO: work item ID.
    /// </summary>
    public long PlatformId { get; init; }

    /// <summary>
    /// Display number. GitHub: issue number. ADO: work item ID (same as PlatformId).
    /// </summary>
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "open";
    public string? AssignedAgent { get; init; }
    public string Url { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string? Author { get; init; }
    public int CommentCount { get; init; }
    public List<string> Labels { get; init; } = new();
    public List<PlatformComment> Comments { get; init; } = new();

    /// <summary>ADO-specific: work item type (Task, Bug, Story). GitHub: always "Issue".</summary>
    public string WorkItemType { get; init; } = "Issue";
}

/// <summary>Comment on a PR or work item.</summary>
public record PlatformComment
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime CreatedAt { get; init; }
}

/// <summary>Rate limit information from the platform.</summary>
public record PlatformRateLimitInfo
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public DateTime ResetAt { get; init; }
    public long TotalApiCalls { get; init; }
    public bool IsRateLimited { get; init; }
    public string PlatformName { get; init; } = "";
}

/// <summary>A file changed in a PR with its diff patch.</summary>
public record PlatformFileDiff
{
    public string FileName { get; init; } = "";
    public string? Patch { get; init; }
    public string Status { get; init; } = "";
    public int Additions { get; init; }
    public int Deletions { get; init; }
}

/// <summary>An inline review comment targeting a specific file and line.</summary>
public record PlatformInlineComment
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string Body { get; init; }
}

/// <summary>A review thread on a PR with resolution status.</summary>
public record PlatformReviewThread
{
    public long Id { get; init; }

    /// <summary>
    /// Platform-specific thread identifier for resolution operations.
    /// GitHub: GraphQL node_id (string). ADO: thread ID (numeric string).
    /// </summary>
    public string ThreadId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int? Line { get; init; }
    public string Body { get; init; } = "";
    public string Author { get; init; } = "";
    public bool IsResolved { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>A file to be committed in a batch operation.</summary>
public record PlatformFileCommit
{
    public required string Path { get; init; }
    public required string Content { get; init; }
}

/// <summary>Commit info from a PR branch.</summary>
public record PlatformCommitInfo
{
    public string Sha { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime CommittedAt { get; init; }
}
