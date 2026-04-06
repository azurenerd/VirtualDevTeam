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
