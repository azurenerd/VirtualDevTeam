namespace AgentSquad.Core.DevPlatform.Capabilities;

/// <summary>
/// URL construction patterns for the platform.
/// Handles clone URLs, web URLs, and raw file content URLs.
/// Eliminates hardcoded github.com/dev.azure.com references from agent code.
/// </summary>
public interface IPlatformHostContext
{
    /// <summary>
    /// Authenticated clone URL for the repository.
    /// GitHub: https://x-access-token:TOKEN@github.com/owner/repo.git
    /// ADO: https://TOKEN@dev.azure.com/org/project/_git/repo
    /// </summary>
    string GetCloneUrl(string token);

    /// <summary>
    /// Web URL for a pull request.
    /// GitHub: https://github.com/owner/repo/pull/{id}
    /// ADO: https://dev.azure.com/org/project/_git/repo/pullrequest/{id}
    /// </summary>
    string GetPullRequestWebUrl(int prId);

    /// <summary>
    /// Web URL for a work item.
    /// GitHub: https://github.com/owner/repo/issues/{id}
    /// ADO: https://dev.azure.com/org/project/_workitems/edit/{id}
    /// </summary>
    string GetWorkItemWebUrl(int workItemId);

    /// <summary>
    /// Direct URL to raw file content on a branch.
    /// GitHub: https://raw.githubusercontent.com/owner/repo/branch/path
    /// ADO: https://dev.azure.com/org/project/_apis/git/repositories/repo/items?path=...
    /// </summary>
    string GetRawFileUrl(string path, string branch);

    /// <summary>
    /// Web-browsable URL for a file on a branch.
    /// GitHub: https://github.com/owner/repo/blob/branch/path
    /// ADO: https://dev.azure.com/org/project/_git/repo?path=/path&amp;version=GBbranch
    /// </summary>
    string GetFileWebUrl(string path, string branch);

    /// <summary>Default branch name (usually "main").</summary>
    string DefaultBranch { get; }
}
