namespace AgentSquad.Core.GitHub.Models;

public record AgentPullRequest
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "open";
    public string HeadBranch { get; init; } = "";
    public string BaseBranch { get; init; } = "main";
    public string? AssignedAgent { get; init; }
    public string Url { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public List<string> Labels { get; init; } = new();
    public List<string> ReviewComments { get; init; } = new();
}

public record AgentIssue
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "open";
    public string? AssignedAgent { get; init; }
    public string Url { get; init; } = "";
    public DateTime CreatedAt { get; init; }
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
}
