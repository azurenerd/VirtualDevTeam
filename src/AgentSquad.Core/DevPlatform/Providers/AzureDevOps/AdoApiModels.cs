using System.Text.Json.Serialization;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Internal DTOs for deserializing Azure DevOps REST API JSON responses.
/// These are not exposed to consumers — they are mapped to platform-agnostic models.
/// </summary>

// Generic ADO list response wrapper
internal record AdoListResponse<T>
{
    public int Count { get; init; }
    public List<T> Value { get; init; } = [];
}

// ─── Pull Requests ───

internal record AdoPullRequest
{
    public int PullRequestId { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "active"; // active, abandoned, completed
    [JsonPropertyName("sourceRefName")]
    public string SourceBranch { get; init; } = "";
    [JsonPropertyName("targetRefName")]
    public string TargetBranch { get; init; } = "";
    public AdoIdentity? CreatedBy { get; init; }
    public DateTime CreationDate { get; init; }
    public DateTime? ClosedDate { get; init; }
    public string? MergeStatus { get; init; }
    public AdoCommitRef? LastMergeSourceCommit { get; init; }
    public string Url { get; init; } = "";
    public List<AdoPrLabel>? Labels { get; init; }
    public List<AdoPrReviewer>? Reviewers { get; init; }
}

internal record AdoIdentity
{
    public string DisplayName { get; init; } = "";
    public string UniqueName { get; init; } = "";
    public string? Id { get; init; }
}

internal record AdoCommitRef
{
    public string CommitId { get; init; } = "";
    public string? Comment { get; init; }
}

internal record AdoPrLabel
{
    public string Name { get; init; } = "";
    public bool Active { get; init; } = true;
}

internal record AdoPrReviewer
{
    public string DisplayName { get; init; } = "";
    public string? UniqueName { get; init; }
    public int Vote { get; init; } // 10=Approved, 5=ApprovedWithSuggestions, 0=NoVote, -5=WaitingForAuthor, -10=Rejected
}

// ─── Work Items ───

internal record AdoWorkItem
{
    public int Id { get; init; }
    public int Rev { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();
    public string Url { get; init; } = "";
    public List<AdoWorkItemRelation>? Relations { get; init; }
}

internal record AdoWorkItemRelation
{
    public string Rel { get; init; } = "";
    public string Url { get; init; } = "";
    public Dictionary<string, string>? Attributes { get; init; }
}

internal record AdoWorkItemCreateResult
{
    public int Id { get; init; }
    public int Rev { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();
    public string Url { get; init; } = "";
}

internal record AdoWorkItemQueryResult
{
    public string QueryType { get; init; } = "";
    public List<AdoWorkItemReference> WorkItems { get; init; } = [];
}

internal record AdoWorkItemReference
{
    public int Id { get; init; }
    public string Url { get; init; } = "";
}

// ─── Git Items (Files) ───

internal record AdoGitItem
{
    public string ObjectId { get; init; } = "";
    public string GitObjectType { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Url { get; init; }
    public string? Content { get; init; }
    public int? ContentLength { get; init; }
}

internal record AdoGitTreeResponse
{
    public List<AdoGitTreeEntry> TreeEntries { get; init; } = [];
}

internal record AdoGitTreeEntry
{
    public string RelativePath { get; init; } = "";
    public string GitObjectType { get; init; } = "";
    public string ObjectId { get; init; } = "";
    public long Size { get; init; }
}

// ─── Git Pushes (Commits) ───

internal record AdoGitPushRequest
{
    public List<AdoGitRefUpdate> RefUpdates { get; init; } = [];
    public List<AdoGitCommit> Commits { get; init; } = [];
}

internal record AdoGitRefUpdate
{
    public string Name { get; init; } = "";
    public string OldObjectId { get; init; } = "";
}

internal record AdoGitCommit
{
    public string Comment { get; init; } = "";
    public List<AdoGitChange> Changes { get; init; } = [];
}

internal record AdoGitChange
{
    public string ChangeType { get; init; } = "edit"; // add, edit, delete
    public AdoGitItemDescriptor Item { get; init; } = new();
    public AdoGitNewContent? NewContent { get; init; }
}

internal record AdoGitItemDescriptor
{
    public string Path { get; init; } = "";
}

internal record AdoGitNewContent
{
    public string Content { get; init; } = "";
    public string ContentType { get; init; } = "rawtext"; // rawtext or base64encoded
}

// ─── PR Threads (Reviews) ───

internal record AdoPrThread
{
    public int Id { get; init; }
    public string Status { get; init; } = "active";
    public List<AdoPrComment> Comments { get; init; } = [];
    public AdoThreadContext? ThreadContext { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime PublishedDate { get; init; }
}

internal record AdoPrComment
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public AdoIdentity? Author { get; init; }
    public DateTime PublishedDate { get; init; }
    public int? ParentCommentId { get; init; }
    public string? CommentType { get; init; } // text, system, codeChange
}

internal record AdoThreadContext
{
    public string? FilePath { get; init; }
    public AdoCommentPosition? RightFileStart { get; init; }
    public AdoCommentPosition? RightFileEnd { get; init; }
}

internal record AdoCommentPosition
{
    public int Line { get; init; }
    public int Offset { get; init; }
}

// ─── Git Refs (Branches) ───

internal record AdoGitRefResponse
{
    public string Name { get; init; } = "";
    public string ObjectId { get; init; } = "";
}

// ─── PR Iteration / Changes ───

internal record AdoPrIteration
{
    public int Id { get; init; }
    public string? Description { get; init; }
    public AdoCommitRef? SourceRefCommit { get; init; }
    public AdoCommitRef? TargetRefCommit { get; init; }
}

internal record AdoPrChange
{
    public AdoGitItemDescriptor? Item { get; init; }
    public string? ChangeType { get; init; }
}

internal record AdoPrFileChange
{
    public string Path { get; init; } = "";
    public string ChangeType { get; init; } = "";
}

/// <summary>
/// Work item reference returned from the PR work items endpoint.
/// </summary>
internal record AdoPrWorkItemRef
{
    public string Id { get; init; } = "";
    public string? Url { get; init; }
}

/// <summary>
/// Repository info from the Git repositories endpoint.
/// Used to get the project and repo GUIDs for vstfs artifact URIs.
/// </summary>
internal record AdoRepositoryInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AdoProjectRef? Project { get; init; }
}

internal record AdoProjectRef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}
