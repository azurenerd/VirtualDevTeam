using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub.Models;

namespace AgentSquad.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Bidirectional mapping between GitHub models and platform-agnostic models.
/// </summary>
public static class GitHubModelMapper
{
    public static PlatformPullRequest ToPlatform(AgentPullRequest pr) => new()
    {
        Number = pr.Number,
        Title = pr.Title,
        Body = pr.Body,
        State = pr.State,
        HeadBranch = pr.HeadBranch,
        HeadSha = pr.HeadSha,
        BaseBranch = pr.BaseBranch,
        AssignedAgent = pr.AssignedAgent,
        Url = pr.Url,
        CreatedAt = pr.CreatedAt,
        UpdatedAt = pr.UpdatedAt,
        MergedAt = pr.MergedAt,
        Labels = pr.Labels,
        ReviewComments = pr.ReviewComments,
        Comments = pr.Comments.Select(ToPlatform).ToList(),
        ChangedFiles = pr.ChangedFiles,
        MergeableState = pr.MergeableState
    };

    public static PlatformWorkItem ToPlatform(AgentIssue issue) => new()
    {
        PlatformId = issue.GitHubId,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        State = issue.State,
        AssignedAgent = issue.AssignedAgent,
        Url = issue.Url,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ClosedAt = issue.ClosedAt,
        Author = issue.Author,
        CommentCount = issue.CommentCount,
        Labels = issue.Labels,
        Comments = issue.Comments.Select(ToPlatform).ToList(),
        WorkItemType = "Issue"
    };

    public static PlatformComment ToPlatform(IssueComment comment) => new()
    {
        Id = comment.Id,
        Author = comment.Author,
        Body = comment.Body,
        CreatedAt = comment.CreatedAt
    };

    public static PlatformFileDiff ToPlatform(PullRequestFileDiff diff) => new()
    {
        FileName = diff.FileName,
        Patch = diff.Patch,
        Status = diff.Status,
        Additions = diff.Additions,
        Deletions = diff.Deletions
    };

    public static PlatformReviewThread ToPlatform(ReviewThread thread) => new()
    {
        Id = thread.Id,
        ThreadId = thread.NodeId,
        FilePath = thread.FilePath,
        Line = thread.Line,
        Body = thread.Body,
        Author = thread.Author,
        IsResolved = thread.IsResolved,
        CreatedAt = thread.CreatedAt
    };

    public static PlatformRateLimitInfo ToPlatform(GitHubRateLimitInfo info) => new()
    {
        Remaining = info.Remaining,
        Limit = info.Limit,
        ResetAt = info.ResetAt,
        TotalApiCalls = info.TotalApiCalls,
        IsRateLimited = info.IsRateLimited,
        PlatformName = "GitHub"
    };

    public static PlatformInlineComment ToPlatform(InlineReviewComment comment) => new()
    {
        FilePath = comment.FilePath,
        Line = comment.Line,
        Body = comment.Body
    };

    public static InlineReviewComment ToGitHub(PlatformInlineComment comment) => new()
    {
        FilePath = comment.FilePath,
        Line = comment.Line,
        Body = comment.Body
    };

    public static PlatformCommitInfo ToPlatform((string Sha, string Message, DateTime CommittedAt) commit) => new()
    {
        Sha = commit.Sha,
        Message = commit.Message,
        CommittedAt = commit.CommittedAt
    };
}
