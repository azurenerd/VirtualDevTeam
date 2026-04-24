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
    private readonly string _organization;
    private readonly string _project;
    private readonly string _repository;
    private readonly string _defaultBranch;

    public AdoHostContext(IOptions<Configuration.AgentSquadConfig> config)
    {
        var adoConfig = config.Value.DevPlatform?.AzureDevOps
            ?? throw new InvalidOperationException("AzureDevOps config is required for AdoHostContext");
        _organization = adoConfig.Organization;
        _project = adoConfig.Project;
        _repository = adoConfig.Repository;
        _defaultBranch = adoConfig.DefaultBranch ?? "main";
    }

    public string DefaultBranch => _defaultBranch;

    public string GetCloneUrl(string token)
    {
        return $"https://:{token}@dev.azure.com/{_organization}/{_project}/_git/{_repository}";
    }

    public string GetPullRequestWebUrl(int prId)
    {
        return $"https://dev.azure.com/{_organization}/{_project}/_git/{_repository}/pullrequest/{prId}";
    }

    public string GetWorkItemWebUrl(int workItemId)
    {
        return $"https://dev.azure.com/{_organization}/{_project}/_workitems/edit/{workItemId}";
    }

    public string GetRawFileUrl(string path, string branch)
    {
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{_repository}/items?path={Uri.EscapeDataString(normalizedPath)}&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch&api-version=7.1";
    }
}
