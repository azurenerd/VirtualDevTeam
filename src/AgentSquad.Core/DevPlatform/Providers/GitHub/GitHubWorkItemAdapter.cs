using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts <see cref="IGitHubService"/> Issue methods to <see cref="IWorkItemService"/>.
/// </summary>
public sealed class GitHubWorkItemAdapter : IWorkItemService
{
    private readonly IGitHubService _github;
    private readonly AgentSquadConfig _config;

    public GitHubWorkItemAdapter(IGitHubService github, IOptions<AgentSquadConfig> config)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
        _config = config.Value;
    }

    public async Task<PlatformWorkItem> CreateAsync(
        string title, string body, IReadOnlyList<string> labels,
        CancellationToken ct = default)
    {
        var issue = await _github.CreateIssueAsync(title, body, labels.ToArray(), ct);
        var workItem = GitHubModelMapper.ToPlatform(issue);

        // Link as sub-issue of the configured parent work item
        // Read lazily from config — MergeIntoConfig sets this after DI construction
        var parentWorkItemId = _config.Project.ParentWorkItemId;
        if (parentWorkItemId.HasValue)
            await _github.AddSubIssueAsync(parentWorkItemId.Value, workItem.PlatformId, ct);

        return workItem;
    }

    public async Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default)
    {
        var issue = await _github.GetIssueAsync(id, ct);
        return issue is null ? null : GitHubModelMapper.ToPlatform(issue);
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default)
    {
        var issues = await _github.GetOpenIssuesAsync(ct);
        return issues.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default)
    {
        var issues = await _github.GetAllIssuesAsync(ct);
        return issues.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(string agentName, CancellationToken ct = default)
    {
        var issues = await _github.GetIssuesForAgentAsync(agentName, ct);
        return issues.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(string label, string? state = null, CancellationToken ct = default)
    {
        var issues = state is not null
            ? await _github.GetIssuesByLabelAsync(label, state, ct)
            : await _github.GetIssuesByLabelAsync(label, ct);
        return issues.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, string? state = null,
        CancellationToken ct = default)
    {
        await _github.UpdateIssueAsync(id, title, body, labels?.ToArray(), state, ct);
    }

    public async Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default)
    {
        await _github.UpdateIssueTitleAsync(id, newTitle, ct);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        await _github.CloseIssueAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        return await _github.DeleteIssueAsync(id, ct);
    }

    public async Task AddCommentAsync(int id, string comment, CancellationToken ct = default)
    {
        await _github.AddIssueCommentAsync(id, comment, ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default)
    {
        var comments = await _github.GetIssueCommentsAsync(id, ct);
        return comments.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default)
    {
        return await _github.AddSubIssueAsync(parentId, childPlatformId, ct);
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
    {
        var subIssues = await _github.GetSubIssuesAsync(parentId, ct);
        return subIssues.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default)
    {
        return await _github.AddIssueDependencyAsync(blockedId, blockingPlatformId, ct);
    }
}
