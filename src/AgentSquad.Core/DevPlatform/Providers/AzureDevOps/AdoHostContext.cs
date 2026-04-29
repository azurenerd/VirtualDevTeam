using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Config;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps URL patterns for clone, web, and raw file URLs.
/// Constructs dev.azure.com URLs from the configured org/project/repo.
/// </summary>
public sealed class AdoHostContext : IPlatformHostContext
{
    private readonly Configuration.AgentSquadConfig _config;

    public AdoHostContext(IOptions<Configuration.AgentSquadConfig> config)
    {
        var adoConfig = config.Value.DevPlatform?.AzureDevOps
            ?? throw new InvalidOperationException("AzureDevOps config is required for AdoHostContext");
        _config = config.Value;
    }

    private AzureDevOpsConfig Ado => _config.DevPlatform?.AzureDevOps
        ?? throw new InvalidOperationException("AzureDevOps config is required");

    public string DefaultBranch => Ado.DefaultBranch ?? "main";

    public string GetCloneUrl(string token)
    {
        return $"https://:{token}@dev.azure.com/{Ado.Organization}/{Ado.Project}/_git/{Ado.Repository}";
    }

    public string GetPullRequestWebUrl(int prId)
    {
        return $"https://dev.azure.com/{Ado.Organization}/{Ado.Project}/_git/{Ado.Repository}/pullrequest/{prId}";
    }

    public string GetWorkItemWebUrl(int workItemId)
    {
        return $"https://dev.azure.com/{Ado.Organization}/{Ado.Project}/_workitems/edit/{workItemId}";
    }

    public string GetRawFileUrl(string path, string branch)
    {
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"https://dev.azure.com/{Ado.Organization}/{Ado.Project}/_apis/git/repositories/{Ado.Repository}/items?path={Uri.EscapeDataString(normalizedPath)}&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch&api-version=7.1";
    }
}
