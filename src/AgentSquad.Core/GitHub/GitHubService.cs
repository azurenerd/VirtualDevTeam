using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Persistence;

namespace AgentSquad.Core.GitHub;

public class GitHubService : IGitHubService
{
    private readonly GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubService> _logger;
    private readonly RateLimitManager _rl;
    private readonly DateTime? _runStartedUtc;

    /// <summary>Label automatically added to every issue and PR created by the agent system.</summary>
    internal const string AiGeneratedLabel = "AI-Generated";

    // --- Shared in-process cache for high-frequency list queries ---
    // Multiple agents poll GetOpenIssuesAsync/GetOpenPullRequestsAsync every 60s.
    // Without caching, 7 agents × 2-5 list calls/cycle = many API calls/hour,
    // blowing through the 5000/hr GitHub rate limit. A 60s TTL cache reduces this by ~90%.
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(60);

    private IReadOnlyList<AgentIssue>? _openIssuesCache;
    private DateTime _openIssuesCacheTime;
    private readonly SemaphoreSlim _openIssuesLock = new(1, 1);

    private IReadOnlyList<AgentIssue>? _allIssuesCache;
    private DateTime _allIssuesCacheTime;
    private readonly SemaphoreSlim _allIssuesLock = new(1, 1);

    private IReadOnlyList<AgentPullRequest>? _openPrsCache;
    private DateTime _openPrsCacheTime;
    private readonly SemaphoreSlim _openPrsLock = new(1, 1);

    private IReadOnlyList<AgentPullRequest>? _allPrsCache;
    private DateTime _allPrsCacheTime;
    private readonly SemaphoreSlim _allPrsLock = new(1, 1);

    private IReadOnlyList<AgentPullRequest>? _mergedPrsCache;
    private DateTime _mergedPrsCacheTime;
    private readonly SemaphoreSlim _mergedPrsLock = new(1, 1);

    // Cache for GetIssuesByLabelAsync — keyed by "label|state"
    private readonly Dictionary<string, (IReadOnlyList<AgentIssue> Data, DateTime CachedAt)> _labelIssuesCache = new();
    private readonly SemaphoreSlim _labelIssuesLock = new(1, 1);

    // Generic cache entry for new caches
    private record CacheEntry<T>(T Data, DateTime CachedAt);

    // Repo tree cache: keyed by branch name (60s TTL — tree data changes less frequently)
    private readonly Dictionary<string, CacheEntry<IReadOnlyList<string>>> _treeCache = new();
    private readonly SemaphoreSlim _treeCacheLock = new(1, 1);
    private static readonly TimeSpan TreeCacheTtl = TimeSpan.FromSeconds(120);

    // File content cache: keyed by "path|branch"
    private readonly Dictionary<string, CacheEntry<string?>> _fileContentCache = new();
    private readonly SemaphoreSlim _fileContentCacheLock = new(1, 1);
    private static readonly TimeSpan FileContentCacheTtl = TimeSpan.FromSeconds(60);

    // Issue comments cache: keyed by issue number
    private readonly Dictionary<int, CacheEntry<IReadOnlyList<Models.IssueComment>>> _issueCommentsCache = new();
    private readonly SemaphoreSlim _issueCommentsCacheLock = new(1, 1);

    // PR comments cache: keyed by PR number
    private readonly Dictionary<int, CacheEntry<IReadOnlyList<Models.IssueComment>>> _prCommentsCache = new();
    private readonly SemaphoreSlim _prCommentsCacheLock = new(1, 1);

    public string RepositoryFullName => $"{_owner}/{_repo}";

    public GitHubService(IOptions<AgentSquadConfig> config, RateLimitManager rateLimitManager, ILogger<GitHubService> logger,
        AgentStateStore? stateStore = null)
    {
        _logger = logger;
        _rl = rateLimitManager;
        _runStartedUtc = stateStore?.RunStartedUtc;

        var projectConfig = config.Value.Project;
        var repoParts = projectConfig.GitHubRepo.Split('/', 2);
        if (repoParts.Length != 2)
            throw new ArgumentException($"GitHubRepo must be in 'owner/repo' format. Got: '{projectConfig.GitHubRepo}'");

        _owner = repoParts[0];
        _repo = repoParts[1];

        _client = new GitHubClient(new ProductHeaderValue("AgentSquad"))
        {
            Credentials = new Credentials(projectConfig.GitHubToken)
        };
    }

    /// <summary>
    /// Invalidate all list caches. Call after mutations (create/update/merge/close)
    /// so the next read sees fresh data.
    /// </summary>
    private void InvalidateListCaches()
    {
        _openIssuesCache = null;
        _allIssuesCache = null;
        _openPrsCache = null;
        _allPrsCache = null;
        _mergedPrsCache = null;
        lock (_labelIssuesCache) { _labelIssuesCache.Clear(); }
        lock (_treeCache) { _treeCache.Clear(); }
        lock (_fileContentCache) { _fileContentCache.Clear(); }
        lock (_issueCommentsCache) { _issueCommentsCache.Clear(); }
        lock (_prCommentsCache) { _prCommentsCache.Clear(); }
    }

    /// <summary>
    /// Updates rate limit tracking from the last Octokit API response.
    /// Call after any successful _client call to keep quota tracking current.
    /// </summary>
    private void TrackRateLimit()
    {
        var apiInfo = _client.GetLastApiInfo();
        if (apiInfo?.RateLimit is { } rl)
            _rl.UpdateRateLimit(rl.Remaining, rl.Reset.UtcDateTime);
    }

    // Pull Requests

    public async Task<AgentPullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch,
        string[] labels, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                _logger.LogInformation("Creating PR: {Title} ({Head} -> {Base})", title, headBranch, baseBranch);

                var pr = await _client.PullRequest.Create(_owner, _repo, new NewPullRequest(title, headBranch, baseBranch)
                {
                    Body = body
                });
                TrackRateLimit();

                // Always add labels (at minimum AI-Generated)
                {
                    var allLabels = labels.Contains(AiGeneratedLabel, StringComparer.OrdinalIgnoreCase)
                        ? labels
                        : [.. labels, AiGeneratedLabel];
                    try
                    {
                        await _client.Issue.Labels.AddToIssue(_owner, _repo, pr.Number, allLabels);
                    }
                    catch (Exception labelEx)
                    {
                        _logger.LogWarning(labelEx, "Failed to add labels to PR #{Number} — PR was created successfully", pr.Number);
                    }
                }

                InvalidateListCaches();
                var returnLabels = labels.Contains(AiGeneratedLabel, StringComparer.OrdinalIgnoreCase)
                    ? labels.ToList()
                    : [.. labels, AiGeneratedLabel];
                return MapPullRequest(pr, returnLabels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create PR: {Title}", title);
                throw;
            }
        }, ct);
    }

    public async Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var pr = await _client.PullRequest.Get(_owner, _repo, number);
                TrackRateLimit();
                var labels = pr.Labels.Select(l => l.Name).ToList();
                var reviewComments = await GetReviewCommentsAsync(number);
                var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, number);
                var mappedComments = comments.Select(c => new Models.IssueComment
                {
                    Id = c.Id,
                    Author = c.User.Login,
                    Body = c.Body,
                    CreatedAt = c.CreatedAt.UtcDateTime
                }).ToList();
                return MapPullRequest(pr, labels, reviewComments, mappedComments);
            }
            catch (NotFoundException)
            {
                _logger.LogWarning("PR #{Number} not found", number);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get PR #{Number}", number);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default)
    {
        if (_openPrsCache is { } cached && DateTime.UtcNow - _openPrsCacheTime < ListCacheTtl)
            return cached;

        await _openPrsLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_openPrsCache is { } cached2 && DateTime.UtcNow - _openPrsCacheTime < ListCacheTtl)
                return cached2;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                    new PullRequestRequest { State = ItemStateFilter.Open });
                TrackRateLimit();
                return prs.Select(pr => MapPullRequest(pr, pr.Labels.Select(l => l.Name).ToList())).ToList();
            }, ct);

            _openPrsCache = result;
            _openPrsCacheTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get open PRs");
            throw;
        }
        finally { _openPrsLock.Release(); }
    }

    public async Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsAsync(CancellationToken ct = default)
    {
        if (_allPrsCache is { } cached && DateTime.UtcNow - _allPrsCacheTime < ListCacheTtl)
            return cached;

        await _allPrsLock.WaitAsync(ct);
        try
        {
            if (_allPrsCache is { } cached2 && DateTime.UtcNow - _allPrsCacheTime < ListCacheTtl)
                return cached2;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                    new PullRequestRequest { State = ItemStateFilter.All, SortDirection = SortDirection.Descending });
                TrackRateLimit();
                var mapped = prs.Select(pr => MapPullRequest(pr, pr.Labels.Select(l => l.Name).ToList())).ToList();
                if (_runStartedUtc.HasValue)
                    mapped = mapped.Where(pr => pr.CreatedAt >= _runStartedUtc.Value).ToList();
                return mapped;
            }, ct);

            _allPrsCache = result;
            _allPrsCacheTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all PRs");
            throw;
        }
        finally { _allPrsLock.Release(); }
    }

    public async Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsForAgentAsync(
        string agentName, CancellationToken ct = default)
    {
        try
        {
            var allOpen = await GetOpenPullRequestsAsync(ct);
            var prefix = $"{agentName}:";
            return allOpen.Where(pr => pr.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PRs for agent {Agent}", agentName);
            throw;
        }
    }

    public async Task<IReadOnlyList<AgentPullRequest>> GetMergedPullRequestsAsync(CancellationToken ct = default)
    {
        if (_mergedPrsCache is { } cached && DateTime.UtcNow - _mergedPrsCacheTime < ListCacheTtl)
            return cached;

        await _mergedPrsLock.WaitAsync(ct);
        try
        {
            if (_mergedPrsCache is { } cached2 && DateTime.UtcNow - _mergedPrsCacheTime < ListCacheTtl)
                return cached2;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                    new PullRequestRequest { State = ItemStateFilter.Closed, SortDirection = SortDirection.Descending });
                TrackRateLimit();
                return (IReadOnlyList<AgentPullRequest>)prs
                    .Where(pr => pr.Merged)
                    .Select(pr => MapPullRequest(pr, pr.Labels.Select(l => l.Name).ToList()))
                    .ToList();
            }, ct);

            _mergedPrsCache = result;
            _mergedPrsCacheTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get merged PRs");
            throw;
        }
        finally { _mergedPrsLock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var files = await _client.PullRequest.Files(_owner, _repo, prNumber);
                TrackRateLimit();
                return files.Select(f => f.FileName).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get changed files for PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync<IReadOnlyList<string>>(async _ =>
        {
            try
            {
                var commits = await _client.PullRequest.Commits(_owner, _repo, prNumber);
                TrackRateLimit();
                return commits.Select(c => c.Commit.Message).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get commit messages for PR #{Number}", prNumber);
                return Array.Empty<string>();
            }
        }, ct);
    }

    public async Task<IReadOnlyList<PullRequestFileDiff>> GetPullRequestFilesWithPatchAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var files = await _client.PullRequest.Files(_owner, _repo, prNumber);
                TrackRateLimit();
                return files.Select(f => new PullRequestFileDiff
                {
                    FileName = f.FileName,
                    Patch = f.Patch,
                    Status = f.Status,
                    Additions = f.Additions,
                    Deletions = f.Deletions
                }).ToList() as IReadOnlyList<PullRequestFileDiff>;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get files with patch for PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task CreatePullRequestReviewWithCommentsAsync(
        int prNumber, string body, string eventType,
        IReadOnlyList<InlineReviewComment> comments, string? commitId = null,
        CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var reviewEvent = eventType.ToUpperInvariant() switch
                {
                    "APPROVE" => PullRequestReviewEvent.Approve,
                    "REQUEST_CHANGES" => PullRequestReviewEvent.RequestChanges,
                    "COMMENT" => PullRequestReviewEvent.Comment,
                    _ => PullRequestReviewEvent.Comment
                };

                // Resolve commit SHA if not provided
                if (string.IsNullOrEmpty(commitId))
                {
                    var pr = await _client.PullRequest.Get(_owner, _repo, prNumber);
                    commitId = pr.Head.Sha;
                    TrackRateLimit();
                }

                // Get file patches to map line numbers to diff positions
                var files = await _client.PullRequest.Files(_owner, _repo, prNumber);
                TrackRateLimit();
                var patchesByFile = files.ToDictionary(f => f.FileName, f => f.Patch, StringComparer.OrdinalIgnoreCase);

                var review = new PullRequestReviewCreate
                {
                    Body = body,
                    Event = reviewEvent,
                    CommitId = commitId
                };

                // Map each inline comment to a diff position
                var mappedCount = 0;
                foreach (var comment in comments)
                {
                    if (!patchesByFile.TryGetValue(comment.FilePath, out var patch))
                    {
                        _logger.LogDebug("Inline comment on {File}:{Line} skipped — file not in PR diff",
                            comment.FilePath, comment.Line);
                        continue;
                    }

                    var position = DiffPositionMapper.MapLineToPosition(patch, comment.Line);
                    if (position is null)
                    {
                        _logger.LogDebug("Inline comment on {File}:{Line} skipped — line not in diff",
                            comment.FilePath, comment.Line);
                        continue;
                    }

                    review.Comments.Add(new DraftPullRequestReviewComment(
                        comment.Body, comment.FilePath, position.Value));
                    mappedCount++;
                }

                _logger.LogInformation(
                    "Submitting {Event} review on PR #{Number} with {Mapped}/{Total} inline comments",
                    eventType, prNumber, mappedCount, comments.Count);

                await _client.PullRequest.Review.Create(_owner, _repo, prNumber, review);
                TrackRateLimit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit review with inline comments on PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<ReviewThread>> GetPullRequestReviewThreadsAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var comments = await _client.PullRequest.ReviewComment.GetAll(_owner, _repo, prNumber);
                TrackRateLimit();

                // Group by InReplyToId to form threads — root comments have InReplyToId == 0
                // For simplicity, return each root comment as a thread
                return comments
                    .Where(c => c.InReplyToId == null || c.InReplyToId == 0)
                    .Select(c => new ReviewThread
                    {
                        Id = c.Id,
                        FilePath = c.Path ?? "",
                        Line = c.OriginalPosition,
                        Body = c.Body ?? "",
                        Author = c.User?.Login ?? "",
                        CreatedAt = c.CreatedAt.UtcDateTime
                    })
                    .ToList() as IReadOnlyList<ReviewThread>;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get review threads for PR #{Number}", prNumber);
                return Array.Empty<ReviewThread>();
            }
        }, ct);
    }

    public async Task<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>> GetPullRequestCommitsWithDatesAsync(
        int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>>(async _ =>
        {
            try
            {
                var commits = await _client.PullRequest.Commits(_owner, _repo, prNumber);
                TrackRateLimit();
                return commits
                    .Select(c => (c.Sha, c.Commit.Message, c.Commit.Author.Date.UtcDateTime))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get commits with dates for PR #{Number}", prNumber);
                return Array.Empty<(string, string, DateTime)>();
            }
        }, ct);
    }

    public async Task<IReadOnlyList<Models.IssueComment>> GetPullRequestCommentsAsync(int prNumber, CancellationToken ct = default)
    {
        await _prCommentsCacheLock.WaitAsync(ct);
        try
        {
            if (_prCommentsCache.TryGetValue(prNumber, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < ListCacheTtl)
                return cached.Data;
        }
        finally { _prCommentsCacheLock.Release(); }

        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, prNumber);
                TrackRateLimit();
                var result = comments.Select(c => new Models.IssueComment
                {
                    Id = c.Id,
                    Author = c.User.Login,
                    Body = c.Body,
                    CreatedAt = c.CreatedAt.UtcDateTime
                }).ToList();

                await _prCommentsCacheLock.WaitAsync(ct);
                try { _prCommentsCache[prNumber] = new CacheEntry<IReadOnlyList<Models.IssueComment>>(result, DateTime.UtcNow); }
                finally { _prCommentsCacheLock.Release(); }

                return (IReadOnlyList<Models.IssueComment>)result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get comments for PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Issue.Comment.Create(_owner, _repo, prNumber, comment);
                TrackRateLimit();
                _logger.LogDebug("Added comment to PR #{Number}", prNumber);

                // Invalidate PR comments cache for this PR
                await _prCommentsCacheLock.WaitAsync(ct);
                try { _prCommentsCache.Remove(prNumber); }
                finally { _prCommentsCacheLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add comment to PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var reviewEvent = eventType.ToUpperInvariant() switch
                {
                    "APPROVE" => PullRequestReviewEvent.Approve,
                    "REQUEST_CHANGES" => PullRequestReviewEvent.RequestChanges,
                    "COMMENT" => PullRequestReviewEvent.Comment,
                    _ => PullRequestReviewEvent.Comment
                };

                var review = new PullRequestReviewCreate { Body = body, Event = reviewEvent };
                await _client.PullRequest.Review.Create(_owner, _repo, prNumber, review);
                TrackRateLimit();
                _logger.LogInformation("Submitted {Event} review on PR #{Number}", eventType, prNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit review on PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task UpdatePullRequestAsync(
        int prNumber, string? title = null, string? body = null,
        string[]? labels = null, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var update = new PullRequestUpdate();
                if (title is not null) update.Title = title;
                if (body is not null) update.Body = body;

                await _client.PullRequest.Update(_owner, _repo, prNumber, update);
                TrackRateLimit();

                if (labels is not null)
                {
                    await _client.Issue.Labels.ReplaceAllForIssue(_owner, _repo, prNumber, labels);
                }

                _logger.LogInformation("Updated PR #{Number}", prNumber);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task ClosePullRequestAsync(int prNumber, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.PullRequest.Update(_owner, _repo, prNumber,
                    new PullRequestUpdate { State = ItemState.Closed });
                TrackRateLimit();
                _logger.LogInformation("Closed PR #{Number}", prNumber);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    public async Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var merge = new MergePullRequest
                {
                    MergeMethod = PullRequestMergeMethod.Squash
                };
                if (commitMessage is not null)
                    merge.CommitMessage = commitMessage;

                await _client.PullRequest.Merge(_owner, _repo, prNumber, merge);
                TrackRateLimit();
                _logger.LogInformation("Merged PR #{Number}", prNumber);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge PR #{Number}", prNumber);
                throw;
            }
        }, ct);
    }

    // Issues

    public async Task<AgentIssue> CreateIssueAsync(
        string title, string body, string[] labels, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                _logger.LogInformation("Creating issue: {Title}", title);

                var newIssue = new NewIssue(title) { Body = body };
                foreach (var label in labels)
                    newIssue.Labels.Add(label);
                if (!labels.Contains(AiGeneratedLabel, StringComparer.OrdinalIgnoreCase))
                    newIssue.Labels.Add(AiGeneratedLabel);

                var issue = await _client.Issue.Create(_owner, _repo, newIssue);
                TrackRateLimit();
                InvalidateListCaches();
                return MapIssue(issue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create issue: {Title}", title);
                throw;
            }
        }, ct);
    }

    public async Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var issue = await _client.Issue.Get(_owner, _repo, number);
                TrackRateLimit();
                var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, number);
                return MapIssue(issue, comments.ToList());
            }
            catch (NotFoundException)
            {
                _logger.LogWarning("Issue #{Number} not found", number);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get issue #{Number}", number);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default)
    {
        if (_openIssuesCache is { } cached && DateTime.UtcNow - _openIssuesCacheTime < ListCacheTtl)
            return cached;

        await _openIssuesLock.WaitAsync(ct);
        try
        {
            if (_openIssuesCache is { } cached2 && DateTime.UtcNow - _openIssuesCacheTime < ListCacheTtl)
                return cached2;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var issues = await _client.Issue.GetAllForRepository(_owner, _repo,
                    new RepositoryIssueRequest { State = ItemStateFilter.Open });
                TrackRateLimit();
                return (IReadOnlyList<AgentIssue>)issues.Where(i => i.PullRequest == null).Select(i => MapIssue(i)).ToList();
            }, ct);

            _openIssuesCache = result;
            _openIssuesCacheTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get open issues");
            throw;
        }
        finally { _openIssuesLock.Release(); }
    }

    public async Task<IReadOnlyList<AgentIssue>> GetAllIssuesAsync(CancellationToken ct = default)
    {
        if (_allIssuesCache is { } cached && DateTime.UtcNow - _allIssuesCacheTime < ListCacheTtl)
            return cached;

        await _allIssuesLock.WaitAsync(ct);
        try
        {
            if (_allIssuesCache is { } cached2 && DateTime.UtcNow - _allIssuesCacheTime < ListCacheTtl)
                return cached2;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var request = new RepositoryIssueRequest { State = ItemStateFilter.All, SortDirection = SortDirection.Descending };
                if (_runStartedUtc.HasValue)
                    request.Since = new DateTimeOffset(_runStartedUtc.Value, TimeSpan.Zero);
                var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request);
                TrackRateLimit();
                return (IReadOnlyList<AgentIssue>)issues.Where(i => i.PullRequest == null).Select(i => MapIssue(i)).ToList();
            }, ct);

            _allIssuesCache = result;
            _allIssuesCacheTime = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all issues");
            throw;
        }
        finally { _allIssuesLock.Release(); }
    }

    public async Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(
        string agentName, CancellationToken ct = default)
    {
        try
        {
            var allOpen = await GetOpenIssuesAsync(ct);
            var prefix = $"{agentName}:";
            return allOpen.Where(i => i.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get issues for agent {Agent}", agentName);
            throw;
        }
    }

    public async Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Issue.Comment.Create(_owner, _repo, issueNumber, comment);
                TrackRateLimit();
                _logger.LogDebug("Added comment to issue #{Number}", issueNumber);

                // Invalidate issue comments cache for this issue
                await _issueCommentsCacheLock.WaitAsync(ct);
                try { _issueCommentsCache.Remove(issueNumber); }
                finally { _issueCommentsCacheLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add comment to issue #{Number}", issueNumber);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<Models.IssueComment>> GetIssueCommentsAsync(int issueNumber, CancellationToken ct = default)
    {
        await _issueCommentsCacheLock.WaitAsync(ct);
        try
        {
            if (_issueCommentsCache.TryGetValue(issueNumber, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < ListCacheTtl)
                return cached.Data;
        }
        finally { _issueCommentsCacheLock.Release(); }

        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, issueNumber);
                TrackRateLimit();
                var result = comments.Select(c => new Models.IssueComment
                {
                    Id = c.Id,
                    Author = c.User.Login,
                    Body = c.Body,
                    CreatedAt = c.CreatedAt.UtcDateTime
                }).ToList();

                await _issueCommentsCacheLock.WaitAsync(ct);
                try { _issueCommentsCache[issueNumber] = new CacheEntry<IReadOnlyList<Models.IssueComment>>(result, DateTime.UtcNow); }
                finally { _issueCommentsCacheLock.Release(); }

                return (IReadOnlyList<Models.IssueComment>)result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get comments for issue #{Number}", issueNumber);
                throw;
            }
        }, ct);
    }

    public async Task CloseIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Issue.Update(_owner, _repo, issueNumber, new IssueUpdate { State = ItemState.Closed });
                TrackRateLimit();
                _logger.LogInformation("Closed issue #{Number}", issueNumber);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close issue #{Number}", issueNumber);
                throw;
            }
        }, ct);
    }

    public async Task<bool> DeleteIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                // Get the issue's node_id for GraphQL
                var issue = await _client.Issue.Get(_owner, _repo, issueNumber);
                TrackRateLimit();
                var nodeId = issue.NodeId;

                // Use GitHub GraphQL API to permanently delete the issue
                var query = $$"""{"query":"mutation { deleteIssue(input: {issueId: \"{{nodeId}}\"}) { clientMutationId } }"}""";
                var body = new StringContent(query, Encoding.UTF8, "application/json");

                var token = _client.Credentials.GetToken();
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentSquad/1.0");

                var response = await http.PostAsync("https://api.github.com/graphql", body, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode && !responseBody.Contains("\"errors\""))
                {
                    _logger.LogInformation("Deleted issue #{Number} via GraphQL", issueNumber);
                    InvalidateListCaches();
                    return true;
                }

                // If GraphQL delete fails (permissions), fall back to closing
                _logger.LogWarning("GraphQL deleteIssue failed for #{Number} ({Status}: {Body}), falling back to close",
                    issueNumber, response.StatusCode, responseBody);
                await CloseIssueAsync(issueNumber, ct);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete issue #{Number}, falling back to close", issueNumber);
                try { await CloseIssueAsync(issueNumber, ct); } catch { /* best effort */ }
                return false;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default)
    {
        var cacheKey = $"{label}|open";
        await _labelIssuesLock.WaitAsync(ct);
        try
        {
            if (_labelIssuesCache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.CachedAt < ListCacheTtl)
                return entry.Data;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var request = new RepositoryIssueRequest
                {
                    State = ItemStateFilter.Open,
                    Filter = IssueFilter.All
                };
                request.Labels.Add(label);

                var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request);
                TrackRateLimit();
                return (IReadOnlyList<AgentIssue>)issues
                    .Where(i => i.PullRequest is null)
                    .Select(i => MapIssue(i))
                    .ToList();
            }, ct);

            _labelIssuesCache[cacheKey] = (result, DateTime.UtcNow);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get issues with label {Label}", label);
            throw;
        }
        finally { _labelIssuesLock.Release(); }
    }

    public async Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Issue.Update(_owner, _repo, issueNumber, new IssueUpdate { Title = newTitle });
                TrackRateLimit();
                _logger.LogInformation("Updated issue #{Number} title to '{Title}'", issueNumber, newTitle);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update issue #{Number} title", issueNumber);
                throw;
            }
        }, ct);
    }

    public async Task UpdateIssueAsync(int issueNumber, string? title = null, string? body = null, string[]? labels = null, string? state = null, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var update = new IssueUpdate();
                if (title is not null) update.Title = title;
                if (body is not null) update.Body = body;
                if (state is not null)
                    update.State = string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
                        ? ItemState.Closed : ItemState.Open;
                if (labels is not null)
                {
                    // IssueUpdate.Labels starts null; use ClearLabels() then add
                    update.ClearLabels();
                    foreach (var label in labels)
                        update.AddLabel(label);
                }

                await _client.Issue.Update(_owner, _repo, issueNumber, update);
                TrackRateLimit();
                _logger.LogInformation("Updated issue #{Number}", issueNumber);
                InvalidateListCaches();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update issue #{Number}", issueNumber);
                throw;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, string state, CancellationToken ct = default)
    {
        var cacheKey = $"{label}|{state.ToLowerInvariant()}";
        await _labelIssuesLock.WaitAsync(ct);
        try
        {
            if (_labelIssuesCache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.CachedAt < ListCacheTtl)
                return entry.Data;

            var result = await _rl.ExecuteAsync(async _ =>
            {
                var stateFilter = string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
                    ? ItemStateFilter.Closed
                    : string.Equals(state, "all", StringComparison.OrdinalIgnoreCase)
                        ? ItemStateFilter.All
                        : ItemStateFilter.Open;

                var request = new RepositoryIssueRequest
                {
                    State = stateFilter,
                    Filter = IssueFilter.All
                };
                request.Labels.Add(label);

                var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request);
                TrackRateLimit();
                return (IReadOnlyList<AgentIssue>)issues
                    .Where(i => i.PullRequest is null)
                    .Select(i => MapIssue(i))
                    .ToList();
            }, ct);

            _labelIssuesCache[cacheKey] = (result, DateTime.UtcNow);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get issues with label {Label} state {State}", label, state);
            throw;
        }
        finally { _labelIssuesLock.Release(); }
    }

    // File Management

    public async Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        var cacheKey = $"{path}|{branch ?? "default"}";

        await _fileContentCacheLock.WaitAsync(ct);
        try
        {
            if (_fileContentCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < FileContentCacheTtl)
                return cached.Data;
        }
        finally { _fileContentCacheLock.Release(); }

        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var contents = branch is not null
                    ? await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, branch)
                    : await _client.Repository.Content.GetAllContents(_owner, _repo, path);
                TrackRateLimit();

                var file = contents.FirstOrDefault();
                var result = file?.Content ?? (file?.EncodedContent is not null
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent))
                    : null);

                await _fileContentCacheLock.WaitAsync(ct);
                try { _fileContentCache[cacheKey] = new CacheEntry<string?>(result, DateTime.UtcNow); }
                finally { _fileContentCacheLock.Release(); }

                return result;
            }
            catch (NotFoundException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get file content: {Path}", path);
                throw;
            }
        }, ct);
    }

    public async Task CreateOrUpdateFileAsync(
        string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    string? existingSha = null;

                    try
                    {
                        var existing = branch is not null
                            ? await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, branch)
                            : await _client.Repository.Content.GetAllContents(_owner, _repo, path);
                        TrackRateLimit();

                        existingSha = existing.FirstOrDefault()?.Sha;
                    }
                    catch (NotFoundException)
                    {
                        // File doesn't exist yet — will create
                    }

                    if (existingSha is not null)
                    {
                        var update = new UpdateFileRequest(commitMessage, content, existingSha);
                        if (branch is not null) update.Branch = branch;
                        await _client.Repository.Content.UpdateFile(_owner, _repo, path, update);
                        _logger.LogDebug("Updated file: {Path}", path);
                    }
                    else
                    {
                        var create = new CreateFileRequest(commitMessage, content);
                        if (branch is not null) create.Branch = branch;
                        await _client.Repository.Content.CreateFile(_owner, _repo, path, create);
                        _logger.LogDebug("Created file: {Path}", path);
                    }

                    // Invalidate file content cache for this path
                    var fileCacheKey = $"{path}|{branch ?? "default"}";
                    await _fileContentCacheLock.WaitAsync(ct);
                    try { _fileContentCache.Remove(fileCacheKey); }
                    finally { _fileContentCacheLock.Release(); }

                    return; // Success
                }
                catch (ApiException ex) when (attempt < maxRetries &&
                    (ex.Message.Contains("expected", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("409", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("sha", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("SHA conflict on {Path} (attempt {Attempt}/{Max}), retrying",
                        path, attempt, maxRetries);
                    await Task.Delay(500 * attempt, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create/update file: {Path}", path);
                    throw;
                }
            }
        }, ct);
    }

    public async Task DeleteFileAsync(
        string path, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var existing = branch is not null
                    ? await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, branch)
                    : await _client.Repository.Content.GetAllContents(_owner, _repo, path);
                TrackRateLimit();

                var sha = existing.FirstOrDefault()?.Sha;
                if (sha is null) return;

                var delete = new DeleteFileRequest(commitMessage, sha);
                if (branch is not null) delete.Branch = branch;
                await _client.Repository.Content.DeleteFile(_owner, _repo, path, delete);
                _logger.LogDebug("Deleted file: {Path}", path);
            }
            catch (NotFoundException)
            {
                // File doesn't exist — nothing to delete
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file: {Path}", path);
                throw;
            }
        }, ct);
    }

    // BUG FIX: The Contents API (CreateOrUpdateFileAsync) creates one commit per file,
    // which floods PR history with N commits for N files in a single logical step.
    // BatchCommitFilesAsync uses the Git Trees API to commit all files atomically in one commit.
    public async Task BatchCommitFilesAsync(
        IReadOnlyList<(string Path, string Content)> files,
        string commitMessage,
        string branch,
        CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            ArgumentNullException.ThrowIfNull(files);
            if (files.Count == 0) return;

            // 1. Get the current commit SHA at the tip of the branch
            var branchRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{branch}");
            TrackRateLimit();
            var latestCommitSha = branchRef.Object.Sha;
            var baseCommit = await _client.Git.Commit.Get(_owner, _repo, latestCommitSha);

            // 2. Create blobs for each file and build the tree items
            var treeItems = new List<NewTreeItem>();
            foreach (var (path, content) in files)
            {
                var blob = new NewBlob
                {
                    Content = content,
                    Encoding = EncodingType.Utf8
                };
                var blobResult = await _client.Git.Blob.Create(_owner, _repo, blob);

                treeItems.Add(new NewTreeItem
                {
                    Path = path,
                    Mode = "100644", // regular file
                    Type = TreeType.Blob,
                    Sha = blobResult.Sha
                });
            }

            // 3. Create a new tree based on the current commit's tree
            var newTree = new NewTree { BaseTree = baseCommit.Tree.Sha };
            foreach (var item in treeItems)
                newTree.Tree.Add(item);

            var treeResult = await _client.Git.Tree.Create(_owner, _repo, newTree);

            // 4. Create the commit pointing to the new tree
            var newCommit = new NewCommit(commitMessage, treeResult.Sha, latestCommitSha);
            var commitResult = await _client.Git.Commit.Create(_owner, _repo, newCommit);

            // 5. Update the branch reference to point to the new commit
            await _client.Git.Reference.Update(_owner, _repo, $"heads/{branch}",
                new ReferenceUpdate(commitResult.Sha));

            _logger.LogInformation(
                "Batch-committed {Count} files to branch {Branch} in a single commit: {Message}",
                files.Count, branch, commitMessage);
        }, ct);
    }

    public async Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage, string branch,
        CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            // 1. Get the current commit SHA at the tip of the branch
            var branchRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{branch}");
            TrackRateLimit();
            var latestCommitSha = branchRef.Object.Sha;
            var baseCommit = await _client.Git.Commit.Get(_owner, _repo, latestCommitSha);

            // 2. Create a base64-encoded blob for the binary file
            var blob = new NewBlob
            {
                Content = Convert.ToBase64String(content),
                Encoding = EncodingType.Base64
            };
            var blobResult = await _client.Git.Blob.Create(_owner, _repo, blob);

            // 3. Build the tree with the single binary file
            var newTree = new NewTree { BaseTree = baseCommit.Tree.Sha };
            newTree.Tree.Add(new NewTreeItem
            {
                Path = path,
                Mode = "100644",
                Type = TreeType.Blob,
                Sha = blobResult.Sha
            });
            var treeResult = await _client.Git.Tree.Create(_owner, _repo, newTree);

            // 4. Create the commit
            var newCommit = new NewCommit(commitMessage, treeResult.Sha, latestCommitSha);
            var commitResult = await _client.Git.Commit.Create(_owner, _repo, newCommit);

            // 5. Update the branch reference
            await _client.Git.Reference.Update(_owner, _repo, $"heads/{branch}",
                new ReferenceUpdate(commitResult.Sha));

            _logger.LogInformation("Committed binary file {Path} ({Size} bytes) to {Branch}",
                path, content.Length, branch);

            // Return the raw URL using the commit SHA (permanent, survives branch deletion)
            return $"https://raw.githubusercontent.com/{_owner}/{_repo}/{commitResult.Sha}/{path}";
        }, ct);
    }

    // Branches

    public async Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var source = await _client.Git.Reference.Get(_owner, _repo, $"heads/{fromBranch}");
                TrackRateLimit();
                var newRef = new NewReference($"refs/heads/{branchName}", source.Object.Sha);
                await _client.Git.Reference.Create(_owner, _repo, newRef);
                _logger.LogInformation("Created branch {Branch} from {Source}", branchName, fromBranch);
            }
            // BUG FIX: Gracefully handle "Reference already exists" instead of throwing.
            // When SpawnAgentAsync started agents twice (duplicate loop bug), the second
            // kickoff tried to create the same branch, causing an ApiValidationException.
            // Even after fixing the duplicate loop, this resilience prevents crashes on
            // restarts where the branch from a prior run still exists.
            catch (ApiValidationException ex) when (ex.Message.Contains("Reference already exists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Branch {Branch} already exists, reusing", branchName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create branch {Branch}", branchName);
                throw;
            }
        }, ct);
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Git.Reference.Get(_owner, _repo, $"heads/{branchName}");
                TrackRateLimit();
                return true;
            }
            catch (NotFoundException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check if branch {Branch} exists", branchName);
                throw;
            }
        }, ct);
    }

    public async Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                await _client.Git.Reference.Delete(_owner, _repo, $"heads/{branchName}");
                TrackRateLimit();
                _logger.LogInformation("Deleted branch {Branch}", branchName);
            }
            catch (NotFoundException)
            {
                _logger.LogDebug("Branch {Branch} already deleted or not found", branchName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete branch {Branch}", branchName);
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string? prefix = null, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync<IReadOnlyList<string>>(async _ =>
        {
            try
            {
                var refs = await _client.Git.Reference.GetAll(_owner, _repo);
                TrackRateLimit();
                var branches = refs
                    .Where(r => r.Ref.StartsWith("refs/heads/"))
                    .Select(r => r.Ref.Replace("refs/heads/", ""))
                    .Where(b => prefix is null || b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return branches;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list branches with prefix {Prefix}", prefix);
                return Array.Empty<string>();
            }
        }, ct);
    }

    public async Task CleanRepoToBaselineAsync(IReadOnlyList<string> preserveFiles, string commitMessage, string branch = "main", CancellationToken ct = default)
    {
        await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                // Get current commit SHA for the branch
                var branchRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{branch}");
                TrackRateLimit();
                var currentSha = branchRef.Object.Sha;

                // Get current tree
                var commit = await _client.Git.Commit.Get(_owner, _repo, currentSha);
                var tree = await _client.Git.Tree.GetRecursive(_owner, _repo, commit.Tree.Sha);

                // Build new tree with only preserved files (blobs only, not trees)
                var preserveSet = new HashSet<string>(preserveFiles, StringComparer.OrdinalIgnoreCase);
                var keepItems = tree.Tree
                    .Where(item => item.Type == TreeType.Blob && preserveSet.Contains(item.Path))
                    .Select(item => new NewTreeItem
                    {
                        Path = item.Path,
                        Mode = item.Mode,
                        Type = TreeType.Blob,
                        Sha = item.Sha
                    })
                    .ToList();

                var newTree = new NewTree();
                foreach (var item in keepItems)
                    newTree.Tree.Add(item);

                var createdTree = await _client.Git.Tree.Create(_owner, _repo, newTree);

                // Create new commit
                var newCommit = await _client.Git.Commit.Create(_owner, _repo,
                    new NewCommit(commitMessage, createdTree.Sha, currentSha));

                // Update branch ref
                await _client.Git.Reference.Update(_owner, _repo, $"heads/{branch}",
                    new ReferenceUpdate(newCommit.Sha, true));

                _logger.LogInformation("Repo cleaned to baseline: kept {Count} files in single commit", keepItems.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean repo to baseline");
                throw;
            }
        }, ct);
    }

    public async Task<bool> UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                // Use the REST API: PUT /repos/{owner}/{repo}/pulls/{pull_number}/update-branch
                // Octokit doesn't have a built-in method for this, so use the Connection directly
                var response = await _client.Connection.Put<object>(
                    new Uri($"repos/{_owner}/{_repo}/pulls/{prNumber}/update-branch", UriKind.Relative),
                    new { expected_head_sha = (string?)null });
                TrackRateLimit();

                _logger.LogInformation("Updated PR #{PrNumber} branch with latest main", prNumber);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                // 422 can mean "already up to date" OR "merge conflict".
                // Check the message to distinguish.
                var msg = ex.Message ?? "";
                if (msg.Contains("already up-to-date", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("already up to date", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("no update is needed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("PR #{PrNumber} branch is already up to date with main", prNumber);
                    return true; // Not a conflict — branch is fine
                }

                _logger.LogWarning("PR #{PrNumber} branch update failed — merge conflict (422: {Message})",
                    prNumber, msg.Length > 200 ? msg[..200] : msg);
                return false;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // 202 Accepted means the update was queued successfully
                _logger.LogInformation("PR #{PrNumber} branch update accepted (async)", prNumber);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update PR #{PrNumber} branch", prNumber);
                return false;
            }
        }, ct);
    }

    public async Task<bool> IsBranchBehindMainAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var pr = await _client.PullRequest.Get(_owner, _repo, prNumber);
                TrackRateLimit();
                var baseSha = pr.Base.Sha;
                var headRef = pr.Head.Ref;

                // Compare: how many commits is main ahead of the PR branch?
                var comparison = await _client.Repository.Commit.Compare(
                    _owner, _repo, headRef, pr.Base.Ref);

                if (comparison.AheadBy > 0)
                {
                    _logger.LogDebug("PR #{PrNumber} branch is {Count} commits behind main",
                        prNumber, comparison.AheadBy);
                    return true;
                }

                _logger.LogDebug("PR #{PrNumber} branch is up to date with main", prNumber);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check if PR #{PrNumber} is behind main — assuming yes", prNumber);
                return true; // Assume behind if check fails
            }
        }, ct);
    }

    public async Task<bool> RebaseBranchOnMainAsync(int prNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                // 1. Get the PR to find its branch name
                var pr = await _client.PullRequest.Get(_owner, _repo, prNumber);
                TrackRateLimit();
                var branchName = pr.Head.Ref;

                // 2. Get all files changed in this PR (includes status: added, modified, removed)
                var prFiles = await _client.PullRequest.Files(_owner, _repo, prNumber);
                if (prFiles.Count == 0)
                {
                    _logger.LogWarning("PR #{PrNumber} has no changed files — nothing to rebase", prNumber);
                    return false;
                }

                // 3. Read current content of each non-removed file from the PR branch
                var filesToCommit = new List<(string Path, string Content)>();
                var filesToRemove = new List<string>();

                foreach (var file in prFiles)
                {
                    if (string.Equals(file.Status, "removed", StringComparison.OrdinalIgnoreCase))
                    {
                        filesToRemove.Add(file.FileName);
                        continue;
                    }

                    try
                    {
                        var content = await GetFileContentAsync(file.FileName, branchName, ct);
                        if (content is not null)
                        {
                            filesToCommit.Add((file.FileName, content));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read {File} from branch {Branch} during rebase — skipping",
                            file.FileName, branchName);
                    }
                }

                if (filesToCommit.Count == 0 && filesToRemove.Count == 0)
                {
                    _logger.LogWarning("PR #{PrNumber} rebase found no files to commit", prNumber);
                    return false;
                }

                // 4. Get main's HEAD info (but DON'T reset the branch yet)
                var mainRef = await _client.Git.Reference.Get(_owner, _repo, "heads/main");
                var mainCommitSha = mainRef.Object.Sha;
                var mainCommit = await _client.Git.Commit.Get(_owner, _repo, mainCommitSha);

                // 5. Build the new tree from main's tree + PR's files (pre-flight check)
                var treeItems = new List<NewTreeItem>();
                foreach (var (path, content) in filesToCommit)
                {
                    var blob = new NewBlob { Content = content, Encoding = EncodingType.Utf8 };
                    var blobResult = await _client.Git.Blob.Create(_owner, _repo, blob);

                    treeItems.Add(new NewTreeItem
                    {
                        Path = path,
                        Mode = "100644",
                        Type = TreeType.Blob,
                        Sha = blobResult.Sha
                    });
                }

                // Handle removed files by setting their SHA to null (deletes from tree)
                foreach (var removedPath in filesToRemove)
                {
                    treeItems.Add(new NewTreeItem
                    {
                        Path = removedPath,
                        Mode = "100644",
                        Type = TreeType.Blob,
                        Sha = null // null SHA = delete this file
                    });
                }

                var newTree = new NewTree { BaseTree = mainCommit.Tree.Sha };
                foreach (var item in treeItems)
                    newTree.Tree.Add(item);

                var treeResult = await _client.Git.Tree.Create(_owner, _repo, newTree);

                // Safety check: if the new tree is identical to main's tree, the PR would have
                // zero changes (all files already match main). Abort WITHOUT resetting the branch
                // to avoid creating a zero-diff PR that gets auto-closed.
                if (treeResult.Sha == mainCommit.Tree.Sha)
                {
                    _logger.LogWarning(
                        "PR #{PrNumber} rebase aborted — all {FileCount} files already match main (tree SHA identical). " +
                        "The PR's changes may have already been incorporated via another merged PR.",
                        prNumber, filesToCommit.Count);
                    return false;
                }

                // 6. Create the commit FIRST (parent = main HEAD), THEN do one atomic ref update.
                // This avoids the dangerous window where the branch is at main HEAD with no new commit.
                // Git commits are objects — they don't require the branch to point anywhere specific.
                var newCommit = new NewCommit($"Rebase: {pr.Title}", treeResult.Sha, mainCommitSha);
                var commitResult = await _client.Git.Commit.Create(_owner, _repo, newCommit);

                // 7. Single atomic force-push: move branch directly to the new commit
                // If this fails, branch still has its old code (safe). No intermediate reset.
                await _client.Git.Reference.Update(_owner, _repo, $"heads/{branchName}",
                    new ReferenceUpdate(commitResult.Sha, true)); // force update

                _logger.LogInformation(
                    "Rebased PR #{PrNumber} ({FileCount} files) onto main — branch {Branch} is now conflict-free",
                    prNumber, filesToCommit.Count, branchName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rebase PR #{PrNumber} branch onto main", prNumber);
                return false;
            }
        }, ct);
    }

    // Rate Limiting

    public async Task<Models.GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
    {
        try
        {
            var rateLimit = await _client.RateLimit.GetRateLimits();
            var core = rateLimit.Resources.Core;
            return new Models.GitHubRateLimitInfo
            {
                Remaining = core.Remaining,
                Limit = core.Limit,
                ResetAt = core.Reset.UtcDateTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get rate limit info");
            throw;
        }
    }

    // Sub-Issues & Dependencies (raw REST API — not yet in Octokit.net)

    public async Task<bool> AddSubIssueAsync(int parentIssueNumber, long childIssueGitHubId, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var uri = new Uri($"repos/{_owner}/{_repo}/issues/{parentIssueNumber}/sub_issues", UriKind.Relative);
                await _client.Connection.Post<object>(uri, new { sub_issue_id = childIssueGitHubId }, "application/json", "application/vnd.github+json");
                TrackRateLimit();
                _logger.LogInformation("Linked issue (ID {ChildId}) as sub-issue of #{ParentNumber}",
                    childIssueGitHubId, parentIssueNumber);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                // 422 may mean the sub-issue link already exists
                _logger.LogDebug("Sub-issue link may already exist for #{ParentNumber} ← ID {ChildId}: {Message}",
                    parentIssueNumber, childIssueGitHubId, ex.Message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add sub-issue link: #{ParentNumber} ← ID {ChildId}",
                    parentIssueNumber, childIssueGitHubId);
                return false;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<AgentIssue>> GetSubIssuesAsync(int parentIssueNumber, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync<IReadOnlyList<AgentIssue>>(async _ =>
        {
            try
            {
                var uri = new Uri($"repos/{_owner}/{_repo}/issues/{parentIssueNumber}/sub_issues?per_page=100", UriKind.Relative);
                var response = await _client.Connection.Get<List<Issue>>(uri, null);
                TrackRateLimit();
                if (response.Body is List<Issue> issues)
                    return issues.Select(i => MapIssue(i)).ToList();

                // Fallback: parse raw JSON if Octokit can't deserialize directly
                _logger.LogDebug("Sub-issues response for #{Number} returned non-list type, returning empty",
                    parentIssueNumber);
                return Array.Empty<AgentIssue>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get sub-issues for #{Number}", parentIssueNumber);
                return Array.Empty<AgentIssue>();
            }
        }, ct);
    }

    public async Task<bool> AddIssueDependencyAsync(int blockedIssueNumber, long blockingIssueGitHubId, CancellationToken ct = default)
    {
        return await _rl.ExecuteAsync(async _ =>
        {
            try
            {
                var uri = new Uri($"repos/{_owner}/{_repo}/issues/{blockedIssueNumber}/dependencies/blocked_by", UriKind.Relative);
                await _client.Connection.Post<object>(uri, new { issue_id = blockingIssueGitHubId }, "application/json", "application/vnd.github+json");
                TrackRateLimit();
                _logger.LogInformation("Added blocked-by dependency: #{BlockedNumber} is blocked by ID {BlockingId}",
                    blockedIssueNumber, blockingIssueGitHubId);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                _logger.LogDebug("Dependency link may already exist for #{BlockedNumber} blocked-by ID {BlockingId}: {Message}",
                    blockedIssueNumber, blockingIssueGitHubId, ex.Message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add dependency: #{BlockedNumber} blocked-by ID {BlockingId}",
                    blockedIssueNumber, blockingIssueGitHubId);
                return false;
            }
        }, ct);
    }

    // Mapping helpers

    private async Task<List<string>> GetReviewCommentsAsync(int prNumber)
    {
        try
        {
            var comments = await _client.PullRequest.ReviewComment.GetAll(_owner, _repo, prNumber);
            return comments.Select(c => $"{c.User.Login}: {c.Body}").ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static AgentPullRequest MapPullRequest(
        PullRequest pr, List<string>? labels = null, List<string>? reviewComments = null,
        List<Models.IssueComment>? comments = null)
    {
        var agentName = ExtractAgentName(pr.Title);
        return new AgentPullRequest
        {
            Number = pr.Number,
            Title = pr.Title,
            Body = pr.Body ?? "",
            State = pr.State.StringValue,
            HeadBranch = pr.Head.Ref,
            BaseBranch = pr.Base.Ref,
            AssignedAgent = agentName,
            Url = pr.HtmlUrl,
            CreatedAt = pr.CreatedAt.UtcDateTime,
            UpdatedAt = pr.UpdatedAt.UtcDateTime,
            MergedAt = pr.MergedAt?.UtcDateTime,
            MergeableState = pr.MergeableState?.StringValue,
            Labels = labels ?? pr.Labels.Select(l => l.Name).ToList(),
            ReviewComments = reviewComments ?? new List<string>(),
            Comments = comments ?? new List<Models.IssueComment>()
        };
    }

    private static AgentIssue MapIssue(Issue issue, List<Octokit.IssueComment>? comments = null)
    {
        var agentName = ExtractAgentName(issue.Title);
        return new AgentIssue
        {
            GitHubId = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body ?? "",
            State = issue.State.StringValue,
            AssignedAgent = agentName,
            Url = issue.HtmlUrl,
            CreatedAt = issue.CreatedAt.UtcDateTime,
            UpdatedAt = issue.UpdatedAt?.UtcDateTime,
            ClosedAt = issue.ClosedAt?.UtcDateTime,
            Author = issue.User?.Login,
            CommentCount = issue.Comments,
            Labels = issue.Labels.Select(l => l.Name).ToList(),
            Comments = comments?.Select(c => new Models.IssueComment
            {
                Id = c.Id,
                Author = c.User.Login,
                Body = c.Body,
                CreatedAt = c.CreatedAt.UtcDateTime
            }).ToList() ?? new List<Models.IssueComment>()
        };
    }

    private static string? ExtractAgentName(string title)
    {
        var colonIndex = title.IndexOf(':');
        return colonIndex > 0 ? title[..colonIndex].Trim() : null;
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string branch = "main", CancellationToken ct = default)
    {
        await _treeCacheLock.WaitAsync(ct);
        try
        {
            if (_treeCache.TryGetValue(branch, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < TreeCacheTtl)
                return cached.Data;
        }
        finally { _treeCacheLock.Release(); }

        return await _rl.ExecuteAsync<IReadOnlyList<string>>(async _ =>
        {
            try
            {
                var branchRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{branch}");
                TrackRateLimit();
                var commitSha = branchRef.Object.Sha;
                var commit = await _client.Git.Commit.Get(_owner, _repo, commitSha);
                var tree = await _client.Git.Tree.GetRecursive(_owner, _repo, commit.Tree.Sha);

                var result = tree.Tree
                    .Where(item => item.Type.Value == TreeType.Blob)
                    .Select(item => item.Path)
                    .OrderBy(p => p)
                    .ToList();

                await _treeCacheLock.WaitAsync(ct);
                try { _treeCache[branch] = new CacheEntry<IReadOnlyList<string>>(result, DateTime.UtcNow); }
                finally { _treeCacheLock.Release(); }

                return (IReadOnlyList<string>)result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get repository tree for branch {Branch}", branch);
                return Array.Empty<string>();
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        return await _rl.ExecuteAsync<IReadOnlyList<string>>(async _ =>
        {
            try
            {
                var commit = await _client.Git.Commit.Get(_owner, _repo, commitSha);
                TrackRateLimit();
                var tree = await _client.Git.Tree.GetRecursive(_owner, _repo, commit.Tree.Sha);

                return tree.Tree
                    .Where(item => item.Type.Value == TreeType.Blob)
                    .Select(item => item.Path)
                    .OrderBy(p => p)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get repository tree for commit {CommitSha}", commitSha);
                return Array.Empty<string>();
            }
        }, ct);
    }
}
