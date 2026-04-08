using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub.Models;

namespace AgentSquad.Core.GitHub;

public class GitHubService : IGitHubService
{
    private readonly GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(IOptions<AgentSquadConfig> config, ILogger<GitHubService> logger)
    {
        _logger = logger;

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

    // Pull Requests

    public async Task<AgentPullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch,
        string[] labels, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Creating PR: {Title} ({Head} -> {Base})", title, headBranch, baseBranch);

            var pr = await _client.PullRequest.Create(_owner, _repo, new NewPullRequest(title, headBranch, baseBranch)
            {
                Body = body
            });

            if (labels.Length > 0)
            {
                await _client.Issue.Labels.AddToIssue(_owner, _repo, pr.Number, labels);
            }

            return MapPullRequest(pr, labels.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PR: {Title}", title);
            throw;
        }
    }

    public async Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default)
    {
        try
        {
            var pr = await _client.PullRequest.Get(_owner, _repo, number);
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
    }

    public async Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default)
    {
        try
        {
            var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                new PullRequestRequest { State = ItemStateFilter.Open });

            return prs.Select(pr => MapPullRequest(pr, pr.Labels.Select(l => l.Name).ToList())).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get open PRs");
            throw;
        }
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
        try
        {
            var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                new PullRequestRequest { State = ItemStateFilter.Closed, SortDirection = SortDirection.Descending });
            return prs
                .Where(pr => pr.Merged)
                .Select(pr => MapPullRequest(pr, pr.Labels.Select(l => l.Name).ToList()))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get merged PRs");
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct = default)
    {
        try
        {
            var files = await _client.PullRequest.Files(_owner, _repo, prNumber);
            return files.Select(f => f.FileName).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get changed files for PR #{Number}", prNumber);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct = default)
    {
        try
        {
            var commits = await _client.PullRequest.Commits(_owner, _repo, prNumber);
            return commits.Select(c => c.Commit.Message).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get commit messages for PR #{Number}", prNumber);
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<Models.IssueComment>> GetPullRequestCommentsAsync(int prNumber, CancellationToken ct = default)
    {
        try
        {
            var comments = await _client.Issue.Comment.GetAllForIssue(_owner, _repo, prNumber);
            return comments.Select(c => new Models.IssueComment
            {
                Id = c.Id,
                Author = c.User.Login,
                Body = c.Body,
                CreatedAt = c.CreatedAt.UtcDateTime
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get comments for PR #{Number}", prNumber);
            throw;
        }
    }

    public async Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default)
    {
        try
        {
            await _client.Issue.Comment.Create(_owner, _repo, prNumber, comment);
            _logger.LogDebug("Added comment to PR #{Number}", prNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add comment to PR #{Number}", prNumber);
            throw;
        }
    }

    public async Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default)
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
            _logger.LogInformation("Submitted {Event} review on PR #{Number}", eventType, prNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit review on PR #{Number}", prNumber);
            throw;
        }
    }

    public async Task UpdatePullRequestAsync(
        int prNumber, string? title = null, string? body = null,
        string[]? labels = null, CancellationToken ct = default)
    {
        try
        {
            var update = new PullRequestUpdate();
            if (title is not null) update.Title = title;
            if (body is not null) update.Body = body;

            await _client.PullRequest.Update(_owner, _repo, prNumber, update);

            if (labels is not null)
            {
                await _client.Issue.Labels.ReplaceAllForIssue(_owner, _repo, prNumber, labels);
            }

            _logger.LogInformation("Updated PR #{Number}", prNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update PR #{Number}", prNumber);
            throw;
        }
    }

    public async Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default)
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
            _logger.LogInformation("Merged PR #{Number}", prNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to merge PR #{Number}", prNumber);
            throw;
        }
    }

    // Issues

    public async Task<AgentIssue> CreateIssueAsync(
        string title, string body, string[] labels, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Creating issue: {Title}", title);

            var newIssue = new NewIssue(title) { Body = body };
            foreach (var label in labels)
                newIssue.Labels.Add(label);

            var issue = await _client.Issue.Create(_owner, _repo, newIssue);
            return MapIssue(issue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create issue: {Title}", title);
            throw;
        }
    }

    public async Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default)
    {
        try
        {
            var issue = await _client.Issue.Get(_owner, _repo, number);
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
    }

    public async Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default)
    {
        try
        {
            var issues = await _client.Issue.GetAllForRepository(_owner, _repo,
                new RepositoryIssueRequest { State = ItemStateFilter.Open });

            // Filter out pull requests (GitHub API returns PRs as issues too)
            return issues.Where(i => i.PullRequest == null).Select(i => MapIssue(i)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get open issues");
            throw;
        }
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
        try
        {
            await _client.Issue.Comment.Create(_owner, _repo, issueNumber, comment);
            _logger.LogDebug("Added comment to issue #{Number}", issueNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add comment to issue #{Number}", issueNumber);
            throw;
        }
    }

    public async Task CloseIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        try
        {
            await _client.Issue.Update(_owner, _repo, issueNumber, new IssueUpdate { State = ItemState.Closed });
            _logger.LogInformation("Closed issue #{Number}", issueNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close issue #{Number}", issueNumber);
            throw;
        }
    }

    public async Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default)
    {
        try
        {
            var request = new RepositoryIssueRequest
            {
                State = ItemStateFilter.Open,
                Filter = IssueFilter.All
            };
            request.Labels.Add(label);

            var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request);
            return issues
                .Where(i => i.PullRequest is null) // Exclude PRs from issue list
                .Select(i => MapIssue(i))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get issues with label {Label}", label);
            throw;
        }
    }

    public async Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default)
    {
        try
        {
            await _client.Issue.Update(_owner, _repo, issueNumber, new IssueUpdate { Title = newTitle });
            _logger.LogInformation("Updated issue #{Number} title to '{Title}'", issueNumber, newTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update issue #{Number} title", issueNumber);
            throw;
        }
    }

    // File Management

    public async Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        try
        {
            var contents = branch is not null
                ? await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, branch)
                : await _client.Repository.Content.GetAllContents(_owner, _repo, path);

            var file = contents.FirstOrDefault();
            return file?.Content ?? (file?.EncodedContent is not null
                ? Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent))
                : null);
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
    }

    public async Task CreateOrUpdateFileAsync(
        string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
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
    }

    public async Task DeleteFileAsync(
        string path, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        try
        {
            var existing = branch is not null
                ? await _client.Repository.Content.GetAllContentsByRef(_owner, _repo, path, branch)
                : await _client.Repository.Content.GetAllContents(_owner, _repo, path);

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
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return;

        // 1. Get the current commit SHA at the tip of the branch
        var branchRef = await _client.Git.Reference.Get(_owner, _repo, $"heads/{branch}");
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
    }

    // Branches

    public async Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default)
    {
        try
        {
            var source = await _client.Git.Reference.Get(_owner, _repo, $"heads/{fromBranch}");
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
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        try
        {
            await _client.Git.Reference.Get(_owner, _repo, $"heads/{branchName}");
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
    }

    public async Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
    {
        try
        {
            await _client.Git.Reference.Delete(_owner, _repo, $"heads/{branchName}");
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
    }

    public async Task<bool> UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct = default)
    {
        try
        {
            // Use the REST API: PUT /repos/{owner}/{repo}/pulls/{pull_number}/update-branch
            // Octokit doesn't have a built-in method for this, so use the Connection directly
            var response = await _client.Connection.Put<object>(
                new Uri($"repos/{_owner}/{_repo}/pulls/{prNumber}/update-branch", UriKind.Relative),
                new { expected_head_sha = (string?)null });

            _logger.LogInformation("Updated PR #{PrNumber} branch with latest main", prNumber);
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            _logger.LogWarning("PR #{PrNumber} branch update failed — possible merge conflict", prNumber);
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
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body ?? "",
            State = issue.State.StringValue,
            AssignedAgent = agentName,
            Url = issue.HtmlUrl,
            CreatedAt = issue.CreatedAt.UtcDateTime,
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
}
