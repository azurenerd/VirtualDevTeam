using VirtualDevTeam.Core.GitHub.Models;

namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Extension methods to convert between platform-agnostic models and legacy GitHub models.
/// Enables gradual strangler-pattern migration: agents can mix platform and legacy calls
/// without changing all downstream method signatures at once.
/// </summary>
public static class PlatformModelExtensions
{
    /// <summary>Convert PlatformPullRequest → AgentPullRequest for legacy method compatibility.</summary>
    public static AgentPullRequest ToAgentPR(this PlatformPullRequest p) => new()
    {
        Number = p.Number,
        Title = p.Title,
        Body = p.Body,
        State = p.State,
        HeadBranch = p.HeadBranch,
        HeadSha = p.HeadSha,
        BaseBranch = p.BaseBranch,
        AssignedAgent = p.AssignedAgent,
        Url = p.Url,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        MergedAt = p.MergedAt,
        Labels = p.Labels,
        ReviewComments = p.ReviewComments,
        ChangedFiles = p.ChangedFiles,
        MergeableState = p.MergeableState,
        Comments = p.Comments.Select(c => new IssueComment
        {
            Id = c.Id, Author = c.Author, Body = c.Body, CreatedAt = c.CreatedAt
        }).ToList()
    };

    /// <summary>Convert AgentPullRequest → PlatformPullRequest.</summary>
    public static PlatformPullRequest ToPlatformPR(this AgentPullRequest p) => new()
    {
        Number = p.Number,
        Title = p.Title,
        Body = p.Body,
        State = p.State,
        HeadBranch = p.HeadBranch,
        HeadSha = p.HeadSha,
        BaseBranch = p.BaseBranch,
        AssignedAgent = p.AssignedAgent,
        Url = p.Url,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        MergedAt = p.MergedAt,
        Labels = p.Labels,
        ReviewComments = p.ReviewComments,
        ChangedFiles = p.ChangedFiles,
        MergeableState = p.MergeableState,
        Comments = p.Comments.Select(c => new PlatformComment
        {
            Id = c.Id, Author = c.Author, Body = c.Body, CreatedAt = c.CreatedAt
        }).ToList()
    };

    /// <summary>Convert PlatformWorkItem → AgentIssue for legacy method compatibility.</summary>
    public static AgentIssue ToAgentIssue(this PlatformWorkItem w) => new()
    {
        GitHubId = w.PlatformId,
        Number = w.Number,
        Title = w.Title,
        Body = w.Body,
        State = w.State,
        AssignedAgent = w.AssignedAgent,
        Url = w.Url,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
        ClosedAt = w.ClosedAt,
        Author = w.Author,
        CommentCount = w.CommentCount,
        Labels = w.Labels,
        Comments = w.Comments.Select(c => new IssueComment
        {
            Id = c.Id, Author = c.Author, Body = c.Body, CreatedAt = c.CreatedAt
        }).ToList()
    };

    /// <summary>Convert AgentIssue → PlatformWorkItem.</summary>
    public static PlatformWorkItem ToPlatformWorkItem(this AgentIssue i) => new()
    {
        PlatformId = i.GitHubId,
        Number = i.Number,
        Title = i.Title,
        Body = i.Body,
        State = i.State,
        AssignedAgent = i.AssignedAgent,
        Url = i.Url,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
        ClosedAt = i.ClosedAt,
        Author = i.Author,
        CommentCount = i.CommentCount,
        Labels = i.Labels,
        Comments = i.Comments.Select(c => new PlatformComment
        {
            Id = c.Id, Author = c.Author, Body = c.Body, CreatedAt = c.CreatedAt
        }).ToList()
    };

    /// <summary>Batch convert PlatformPullRequest list → AgentPullRequest list.</summary>
    public static List<AgentPullRequest> ToAgentPRs(this IEnumerable<PlatformPullRequest> prs)
        => prs.Select(p => p.ToAgentPR()).ToList();

    /// <summary>Batch convert PlatformWorkItem list → AgentIssue list.</summary>
    public static List<AgentIssue> ToAgentIssues(this IEnumerable<PlatformWorkItem> items)
        => items.Select(w => w.ToAgentIssue()).ToList();
}
