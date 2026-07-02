// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts the existing <see cref="IGitHubService"/> to the platform-agnostic
/// <see cref="IPullRequestService"/> interface. Zero behavior change — pure delegation.
/// </summary>
public sealed class GitHubPullRequestAdapter : IPullRequestService
{
    private readonly IGitHubService _github;

    public GitHubPullRequestAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public async Task<PlatformPullRequest> CreateAsync(
        string title, string body, string headBranch, string baseBranch,
        IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        try
        {
            var pr = await _github.CreatePullRequestAsync(title, body, headBranch, baseBranch, labels.ToArray(), ct);
            return GitHubModelMapper.ToPlatform(pr);
        }
        catch (Octokit.ApiValidationException ex)
        {
            throw new PlatformConflictException(
                PlatformConflictKind.AlreadyExists,
                $"PR creation failed (likely duplicate): {ex.Message}", ex);
        }
    }

    public async Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        var pr = await _github.GetPullRequestAsync(id, ct);
        return pr is null ? null : GitHubModelMapper.ToPlatform(pr);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetOpenPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetAllPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllForProjectAsync(CancellationToken ct = default)
    {
        // Use the filtered call (run-scoped) — NOT GetAllPullRequestsUnfilteredAsync
        var prs = await _github.GetAllPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetMergedPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(string agentName, CancellationToken ct = default)
    {
        var prs = await _github.GetPullRequestsForAgentAsync(agentName, ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        await _github.UpdatePullRequestAsync(id, title, body, labels?.ToArray(), ct);
    }

    public async Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default)
    {
        try
        {
            await _github.MergePullRequestAsync(id, commitMessage, ct);
        }
        catch (Octokit.PullRequestNotMergeableException ex)
        {
            throw new PlatformConflictException(
                PlatformConflictKind.NotMergeable,
                $"PR #{id} not mergeable: {ex.Message}", ex);
        }
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        await _github.ClosePullRequestAsync(id, ct);
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default)
    {
        return await _github.GetPullRequestChangedFilesAsync(prId, ct);
    }

    public async Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default)
    {
        var diffs = await _github.GetPullRequestFilesWithPatchAsync(prId, ct);
        return diffs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default)
    {
        return await _github.GetPullRequestCommitMessagesAsync(prId, ct);
    }

    public async Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default)
    {
        var commits = await _github.GetPullRequestCommitsWithDatesAsync(prId, ct);
        return commits.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default)
    {
        return await _github.IsBranchBehindMainAsync(prId, ct);
    }

    public async Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default)
    {
        return await _github.UpdatePullRequestBranchAsync(prId, ct);
    }

    public async Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default)
    {
        return await _github.RebaseBranchOnMainAsync(prId, ct);
    }

    public async Task LinkWorkItemAsync(int prId, int workItemId, CancellationToken ct = default)
    {
        // GitHub: ensure PR body contains "Closes #X" for auto-close on merge.
        // If already present, this is a no-op (idempotent).
        var pr = await _github.GetPullRequestAsync(prId, ct);
        if (pr is null) return;

        var body = pr.Body ?? "";

        // Use regex-parsed linked IDs to avoid numeric prefix false positives
        // (e.g., "Closes #12" would falsely match when checking for #123)
        var existingIds = await GetLinkedWorkItemIdsAsync(prId, ct);
        if (existingIds.Contains(workItemId))
            return; // Already linked

        var closePattern = $"Closes #{workItemId}";
        var updatedBody = string.IsNullOrWhiteSpace(body)
            ? closePattern
            : $"{body}\n\n{closePattern}";

        await _github.UpdatePullRequestAsync(prId, body: updatedBody, ct: ct);
    }

    public async Task<IReadOnlyList<int>> GetLinkedWorkItemIdsAsync(int prId, CancellationToken ct = default)
    {
        // GitHub: parse "Closes #X", "Fixes #X", "Resolves #X" patterns from PR body
        var pr = await _github.GetPullRequestAsync(prId, ct);
        if (pr?.Body is null) return Array.Empty<int>();

        var ids = new List<int>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            pr.Body,
            @"(?:closes|fixes|resolves)\s+#(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }
}
