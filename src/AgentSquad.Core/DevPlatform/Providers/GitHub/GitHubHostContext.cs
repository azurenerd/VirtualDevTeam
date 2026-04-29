using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// GitHub URL construction patterns. Eliminates hardcoded github.com references
/// from agent code by centralizing URL patterns here.
/// </summary>
public sealed class GitHubHostContext : IPlatformHostContext
{
    private readonly AgentSquadConfig _config;

    public GitHubHostContext(IOptions<AgentSquadConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.Value;
    }

    private string Repo => _config.Project.GitHubRepo;

    public string GetCloneUrl(string token)
        => $"https://x-access-token:{token}@github.com/{Repo}.git";

    public string GetPullRequestWebUrl(int prId)
        => $"https://github.com/{Repo}/pull/{prId}";

    public string GetWorkItemWebUrl(int workItemId)
        => $"https://github.com/{Repo}/issues/{workItemId}";

    public string GetRawFileUrl(string path, string branch)
        => $"https://raw.githubusercontent.com/{Repo}/{branch}/{path}";

    public string DefaultBranch => _config.Project.DefaultBranch;
}
