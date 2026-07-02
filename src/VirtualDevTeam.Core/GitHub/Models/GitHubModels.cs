namespace VirtualDevTeam.Core.GitHub.Models;

public record AgentPullRequest
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
    public List<IssueComment> Comments { get; init; } = new();
    public List<string> ChangedFiles { get; init; } = new();
    public string? MergeableState { get; init; }
}

public record AgentIssue
{
    /// <summary>GitHub internal ID (different from Number). Required for sub-issue and dependency APIs.</summary>
    public long GitHubId { get; init; }
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
    public List<IssueComment> Comments { get; init; } = new();
}

public record IssueComment
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime CreatedAt { get; init; }
}

public record GitHubRateLimitInfo
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public DateTime ResetAt { get; init; }
    public long TotalApiCalls { get; init; }
    public bool IsRateLimited { get; init; }
}

/// <summary>A file changed in a PR, including the unified diff patch.</summary>
public record PullRequestFileDiff
{
    public string FileName { get; init; } = "";
    /// <summary>Unified diff patch (may be null/empty for binary files or very large diffs).</summary>
    public string? Patch { get; init; }
    /// <summary>File status: added, modified, removed, renamed.</summary>
    public string Status { get; init; } = "";
    public int Additions { get; init; }
    public int Deletions { get; init; }
}

/// <summary>An inline review comment targeting a specific file and line.</summary>
public record InlineReviewComment
{
    public required string FilePath { get; init; }
    /// <summary>Line number in the new (right-side) version of the file.</summary>
    public required int Line { get; init; }
    public required string Body { get; init; }
}

/// <summary>A review thread on a PR with resolution status.</summary>
public record ReviewThread
{
    public long Id { get; init; }
    /// <summary>GraphQL node_id — required for resolveReviewThread mutation.</summary>
    public string NodeId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int? Line { get; init; }
    public string Body { get; init; } = "";
    public string Author { get; init; } = "";
    public bool IsResolved { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Risk level assigned during code review.</summary>
public enum ReviewRiskLevel
{
    /// <summary>No risk assessment (disabled).</summary>
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>Structured result from an AI code review.</summary>
public record StructuredReviewResult
{
    public required string Verdict { get; init; }
    public string Summary { get; init; } = "";
    public ReviewRiskLevel RiskLevel { get; init; } = ReviewRiskLevel.None;
    public IReadOnlyList<InlineReviewComment> Comments { get; init; } = [];
}
