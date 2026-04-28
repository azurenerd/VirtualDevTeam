using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps PR review operations using Threads API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/pull-request-threads
/// </summary>
public sealed class AdoReviewService : AdoHttpClientBase, IReviewService
{
    private readonly ILogger<AdoReviewService> _logger;

    public AdoReviewService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.AgentSquadConfig> config,
        ILogger<AdoReviewService> logger)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
    }

    public async Task AddCommentAsync(int prId, string comment, CancellationToken ct = default)
    {
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads");

        var payload = new
        {
            comments = new[]
            {
                new { parentCommentId = 0, content = comment, commentType = 1 }
            },
            status = "active"
        };

        await PostAsync<AdoPrThread>(url, payload, ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(
        int prId, CancellationToken ct = default)
    {
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads");
        var threads = await GetAsync<AdoListResponse<AdoPrThread>>(url, ct);

        return threads?.Value
            .Where(t => !t.IsDeleted)
            .SelectMany(t => t.Comments)
            .Where(c => c.CommentType != "system")
            .Select(AdoModelMapper.ToPlatform)
            .OrderBy(c => c.CreatedAt)
            .ToList() ?? new List<PlatformComment>();
    }

    public async Task AddReviewAsync(int prId, string body, string eventType, CancellationToken ct = default)
    {
        var vote = eventType.ToLowerInvariant() switch
        {
            "approve" => 10,
            "request_changes" => -10,
            "comment" => 0,
            _ => 0
        };

        if (vote != 0)
            _logger.LogDebug("ADO review vote: {Vote} for PR #{Number}", vote, prId);

        if (!string.IsNullOrWhiteSpace(body))
            await AddCommentAsync(prId, body, ct);
    }

    public async Task CreateReviewWithInlineCommentsAsync(
        int prId, string body, string eventType,
        IReadOnlyList<PlatformInlineComment> comments, string? commitId = null,
        CancellationToken ct = default)
    {
        foreach (var comment in comments)
        {
            var url = BuildUrl(
                $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads");

            var payload = new
            {
                comments = new[]
                {
                    new { parentCommentId = 0, content = comment.Body, commentType = 1 }
                },
                threadContext = new
                {
                    filePath = comment.FilePath,
                    rightFileStart = new { line = comment.Line, offset = 1 },
                    rightFileEnd = new { line = comment.Line, offset = int.MaxValue }
                },
                status = "active"
            };

            await PostAsync<AdoPrThread>(url, payload, ct);
        }

        if (!string.IsNullOrWhiteSpace(body))
            await AddCommentAsync(prId, body, ct);

        _logger.LogInformation("Created {Count} inline review comments on ADO PR #{Number}",
            comments.Count, prId);
    }

    public async Task<IReadOnlyList<PlatformReviewThread>> GetThreadsAsync(
        int prId, CancellationToken ct = default)
    {
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads");
        var threads = await GetAsync<AdoListResponse<AdoPrThread>>(url, ct);

        return threads?.Value
            .Where(t => !t.IsDeleted && t.Comments.Any(c => c.CommentType != "system"))
            .Select(AdoModelMapper.ToPlatform)
            .ToList() ?? new List<PlatformReviewThread>();
    }

    public async Task ResolveThreadAsync(
        int prId, string threadId, string replyBody, CancellationToken ct = default)
    {
        if (!int.TryParse(threadId, out var threadIdInt))
        {
            _logger.LogWarning("Invalid ADO thread ID: {ThreadId}", threadId);
            return;
        }

        var commentUrl = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads/{threadIdInt}/comments");
        await PostAsync<object>(commentUrl, new { content = replyBody, commentType = 1 }, ct);

        var threadUrl = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads/{threadIdInt}");
        await PatchAsync<object>(threadUrl, new { status = "fixed" }, ct);

        _logger.LogInformation("Resolved ADO thread #{ThreadId} on PR #{PrNumber}", threadId, prId);
    }

    public async Task ReplyToThreadAsync(
        int prId, string threadId, string replyBody, CancellationToken ct = default)
    {
        if (!int.TryParse(threadId, out var threadIdInt))
        {
            _logger.LogWarning("Invalid ADO thread ID: {ThreadId}", threadId);
            return;
        }

        var commentUrl = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/threads/{threadIdInt}/comments");
        await PostAsync<object>(commentUrl, new { content = replyBody, commentType = 1 }, ct);

        _logger.LogDebug("Replied to ADO thread #{ThreadId} on PR #{PrNumber} (not resolved)", threadId, prId);
    }
}
