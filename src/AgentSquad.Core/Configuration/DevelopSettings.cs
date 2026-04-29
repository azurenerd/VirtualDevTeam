namespace AgentSquad.Core.Configuration;

/// <summary>
/// User-specific develop settings stored in develop-settings.json (gitignored, reset-safe).
/// PATs are NEVER stored here — they use .NET User Secrets.
/// </summary>
public class DevelopSettings
{
    public string Platform { get; set; } = "AzureDevOps"; // "GitHub" or "AzureDevOps"
    public GitHubRepoSettings GitHub { get; set; } = new();
    public AdoRepoSettings AzureDevOps { get; set; } = new();
    public string AuthMethod { get; set; } = "Pat";
    public string Description { get; set; } = "";
    public string TechStack { get; set; } = "";
    public string ExecutiveUsername { get; set; } = "";
    public int? ParentWorkItemId { get; set; }
    public bool CreateNewRepo { get; set; } = false;
    public string NewRepoName { get; set; } = "";

    /// <summary>Base folder for agent-generated docs (default: "AgentDocs").</summary>
    public string DocsFolderPath { get; set; } = "AgentDocs";

    /// <summary>When true, PM creates 1 issue with doc links instead of N user stories.</summary>
    public bool SingleIssueMode { get; set; } = false;
}

public class GitHubRepoSettings
{
    public string Repo { get; set; } = ""; // "owner/repo" format
    public string DefaultBranch { get; set; } = "main";
}

public class AdoRepoSettings
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
}
