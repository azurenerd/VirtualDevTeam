using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;

namespace AgentSquad.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts <see cref="IGitHubService"/> PR review methods to <see cref="IReviewService"/>.
/// </summary>
public sealed class GitHubReviewAdapter : IReviewService
{
    private readonly IGitHubService _github;

    public GitHubReviewAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public async Task AddCommentAsync(int prId, string comment, CancellationToken ct = default)
    {
        await _github.AddPullRequestCommentAsync(prId, comment, ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int prId, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prId, ct);
        return comments.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task AddReviewAsync(int prId, string body, string eventType, CancellationToken ct = default)
    {
        await _github.AddPullRequestReviewAsync(prId, body, eventType, ct);
    }

    public async Task CreateReviewWithInlineCommentsAsync(
        int prId, string body, string eventType,
        IReadOnlyList<PlatformInlineComment> comments,
        string? commitId = null, CancellationToken ct = default)
    {
        var ghComments = comments.Select(GitHubModelMapper.ToGitHub).ToList();
        await _github.CreatePullRequestReviewWithCommentsAsync(prId, body, eventType, ghComments, commitId, ct);
    }

    public async Task<IReadOnlyList<PlatformReviewThread>> GetThreadsAsync(int prId, CancellationToken ct = default)
    {
        var threads = await _github.GetPullRequestReviewThreadsAsync(prId, ct);
        return threads.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task ResolveThreadAsync(int prId, string threadId, string replyBody, CancellationToken ct = default)
    {
        // GitHub needs both commentId (long) and nodeId (string) for resolution.
        // The threadId passed here is the GraphQL NodeId. We need the numeric comment ID too.
        // Get threads to find the matching one and extract the numeric ID.
        var threads = await _github.GetPullRequestReviewThreadsAsync(prId, ct);
        var match = threads.FirstOrDefault(t => t.NodeId == threadId);
        if (match is not null)
        {
            await _github.ReplyAndResolveReviewThreadAsync(prId, match.Id, match.NodeId, replyBody, ct);
        }
    }

    public async Task ReplyToThreadAsync(int prId, string threadId, string replyBody, CancellationToken ct = default)
    {
        var threads = await _github.GetPullRequestReviewThreadsAsync(prId, ct);
        var match = threads.FirstOrDefault(t => t.NodeId == threadId);
        if (match is not null)
        {
            await _github.ReplyToReviewCommentAsync(prId, match.Id, replyBody, ct);
        }
    }
}
