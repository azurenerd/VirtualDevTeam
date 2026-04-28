namespace AgentSquad.Integration.Tests.Fakes;

using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IGitHubService"/> for integration tests.
/// Provides full CRUD for PRs, issues, branches, and files with lightweight commit tracking.
/// </summary>
public sealed class InMemoryGitHubService : IGitHubService
{
    private readonly object _lock = new();
    private int _nextNumber = 1;
    private int _nextGitHubId = 10_000;

    // Core stores
    private readonly Dictionary<int, AgentPullRequest> _prs = new();
    private readonly Dictionary<int, AgentIssue> _issues = new();
    private readonly Dictionary<string, string> _branches = new(); // branch -> headCommitSha
    private readonly Dictionary<string, InMemoryCommit> _commits = new();
    private readonly Dictionary<string, Dictionary<string, string>> _files = new(); // branch -> (path -> content)
    private readonly Dictionary<string, byte[]> _binaryFiles = new(); // "branch:path" -> bytes

    // Indexes
    private readonly Dictionary<int, List<IssueComment>> _prComments = new();
    private readonly Dictionary<int, List<IssueComment>> _issueComments = new();
    private readonly Dictionary<int, List<ReviewThread>> _reviewThreads = new();
    private readonly Dictionary<int, List<string>> _prChangedFiles = new();
    private readonly Dictionary<int, List<(string Sha, string Message, DateTime At)>> _prCommits = new();
    private readonly Dictionary<int, List<int>> _subIssues = new(); // parent -> children
    private readonly Dictionary<int, List<int>> _dependencies = new(); // blocked -> blockers

    // Configurable behaviors
    public bool ValidateInputs { get; set; } = true;

    public string RepositoryFullName { get; set; } = "test-owner/test-repo";

    public InMemoryGitHubService()
    {
        // Create initial main branch with empty tree
        var sha = MakeSha();
        _commits[sha] = new InMemoryCommit(sha, null, new Dictionary<string, string>(), "Initial commit", DateTime.UtcNow);
        _branches["main"] = sha;
        _files["main"] = new Dictionary<string, string>();
    }

    // ── Pull Requests ──────────────────────────────────────────────

    public Task<AgentPullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (ValidateInputs && !_branches.ContainsKey(headBranch))
                throw new InvalidOperationException($"Branch '{headBranch}' does not exist");
            if (ValidateInputs && !_branches.ContainsKey(baseBranch))
                throw new InvalidOperationException($"Base branch '{baseBranch}' does not exist");

            var number = _nextNumber++;
            var sha = _branches.GetValueOrDefault(headBranch) ?? MakeSha();
            var pr = new AgentPullRequest
            {
                Number = number,
                Title = title,
                Body = body,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                HeadSha = sha,
                State = "open",
                Labels = new List<string>(labels),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Url = $"https://github.com/{RepositoryFullName}/pull/{number}"
            };
            _prs[number] = pr;
            _prComments[number] = new List<IssueComment>();
            _reviewThreads[number] = new List<ReviewThread>();
            _prChangedFiles[number] = ComputeChangedFiles(headBranch, baseBranch);
            _prCommits[number] = new List<(string, string, DateTime)>
            {
                (sha, $"Initial commit for {title}", DateTime.UtcNow)
            };
            return Task.FromResult(Clone(pr));
        }
    }

    public Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_prs.TryGetValue(number, out var pr) ? Clone(pr) : null);
        }
    }

    public Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentPullRequest>>(
                _prs.Values.Where(p => p.State == "open").Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentPullRequest>>(_prs.Values.Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsForAgentAsync(string agentName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentPullRequest>>(
                _prs.Values
                    .Where(p => p.Title.StartsWith(agentName + ":", StringComparison.OrdinalIgnoreCase)
                             || p.Title.StartsWith(agentName + " ", StringComparison.OrdinalIgnoreCase))
                    .Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<IssueComment>> GetPullRequestCommentsAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_prComments.TryGetValue(prNumber, out var comments))
                return Task.FromResult<IReadOnlyList<IssueComment>>(comments.ToList());
            return Task.FromResult<IReadOnlyList<IssueComment>>(Array.Empty<IssueComment>());
        }
    }

    public Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            if (!_prComments.ContainsKey(prNumber))
                _prComments[prNumber] = new List<IssueComment>();
            _prComments[prNumber].Add(new IssueComment
            {
                Id = Interlocked.Increment(ref _nextGitHubId),
                Author = "test-bot",
                Body = comment,
                CreatedAt = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }
    }

    public Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            // Store as a comment for simplicity
            if (!_prComments.ContainsKey(prNumber))
                _prComments[prNumber] = new List<IssueComment>();
            _prComments[prNumber].Add(new IssueComment
            {
                Id = Interlocked.Increment(ref _nextGitHubId),
                Author = "test-reviewer",
                Body = $"[{eventType}] {body}",
                CreatedAt = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }
    }

    public Task UpdatePullRequestAsync(int prNumber, string? title = null, string? body = null, string[]? labels = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            var pr = _prs[prNumber];
            _prs[prNumber] = pr with
            {
                Title = title ?? pr.Title,
                Body = body ?? pr.Body,
                Labels = labels != null ? new List<string>(labels) : pr.Labels,
                UpdatedAt = DateTime.UtcNow
            };
            return Task.CompletedTask;
        }
    }

    public Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            var pr = _prs[prNumber];
            if (ValidateInputs && pr.State != "open")
                throw new InvalidOperationException($"PR #{prNumber} is not open (state={pr.State})");

            // Merge files from head branch to base branch
            if (_files.TryGetValue(pr.HeadBranch, out var headFiles) &&
                _files.TryGetValue(pr.BaseBranch, out var baseFiles))
            {
                foreach (var (path, content) in headFiles)
                    baseFiles[path] = content;
            }

            // Create merge commit
            var sha = MakeSha();
            var baseTree = _files.GetValueOrDefault(pr.BaseBranch) ?? new Dictionary<string, string>();
            _commits[sha] = new InMemoryCommit(sha, _branches.GetValueOrDefault(pr.BaseBranch), 
                new Dictionary<string, string>(baseTree), commitMessage ?? $"Merge PR #{prNumber}", DateTime.UtcNow);
            _branches[pr.BaseBranch] = sha;

            _prs[prNumber] = pr with { State = "closed", MergedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            return Task.CompletedTask;
        }
    }

    public Task ClosePullRequestAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            var pr = _prs[prNumber];
            _prs[prNumber] = pr with { State = "closed", UpdatedAt = DateTime.UtcNow };
            return Task.CompletedTask;
        }
    }

    // ── Issues ──────────────────────────────────────────────────────

    public Task<AgentIssue> CreateIssueAsync(string title, string body, string[] labels, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var number = _nextNumber++;
            var issue = new AgentIssue
            {
                GitHubId = Interlocked.Increment(ref _nextGitHubId),
                Number = number,
                Title = title,
                Body = body,
                State = "open",
                Labels = new List<string>(labels),
                CreatedAt = DateTime.UtcNow,
                Url = $"https://github.com/{RepositoryFullName}/issues/{number}"
            };
            _issues[number] = issue;
            _issueComments[number] = new List<IssueComment>();
            return Task.FromResult(Clone(issue));
        }
    }

    public Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_issues.TryGetValue(number, out var issue) ? Clone(issue) : null);
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentIssue>>(
                _issues.Values.Where(i => i.State == "open").Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetAllIssuesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentIssue>>(_issues.Values.Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(string agentName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentIssue>>(
                _issues.Values
                    .Where(i => i.Title.StartsWith(agentName + ":", StringComparison.OrdinalIgnoreCase)
                             || i.Title.StartsWith(agentName + " ", StringComparison.OrdinalIgnoreCase))
                    .Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentIssue>>(
                _issues.Values.Where(i => i.Labels.Contains(label)).Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, string state, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentIssue>>(
                _issues.Values.Where(i => i.Labels.Contains(label) && i.State == state).Select(Clone).ToList());
        }
    }

    public Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureIssueExists(issueNumber);
            if (!_issueComments.ContainsKey(issueNumber))
                _issueComments[issueNumber] = new List<IssueComment>();
            _issueComments[issueNumber].Add(new IssueComment
            {
                Id = Interlocked.Increment(ref _nextGitHubId),
                Author = "test-bot",
                Body = comment,
                CreatedAt = DateTime.UtcNow
            });
            // Update comment count
            var issue = _issues[issueNumber];
            _issues[issueNumber] = issue with { CommentCount = issue.CommentCount + 1 };
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(int issueNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_issueComments.TryGetValue(issueNumber, out var comments))
                return Task.FromResult<IReadOnlyList<IssueComment>>(comments.ToList());
            return Task.FromResult<IReadOnlyList<IssueComment>>(Array.Empty<IssueComment>());
        }
    }

    public Task UpdateIssueAsync(int issueNumber, string? title = null, string? body = null, string[]? labels = null, string? state = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureIssueExists(issueNumber);
            var issue = _issues[issueNumber];
            _issues[issueNumber] = issue with
            {
                Title = title ?? issue.Title,
                Body = body ?? issue.Body,
                Labels = labels != null ? new List<string>(labels) : issue.Labels,
                State = state ?? issue.State,
                UpdatedAt = DateTime.UtcNow,
                ClosedAt = state == "closed" ? DateTime.UtcNow : issue.ClosedAt
            };
            return Task.CompletedTask;
        }
    }

    public Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureIssueExists(issueNumber);
            var issue = _issues[issueNumber];
            _issues[issueNumber] = issue with { Title = newTitle, UpdatedAt = DateTime.UtcNow };
            return Task.CompletedTask;
        }
    }

    public Task CloseIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureIssueExists(issueNumber);
            var issue = _issues[issueNumber];
            _issues[issueNumber] = issue with { State = "closed", ClosedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            return Task.CompletedTask;
        }
    }

    public Task<bool> DeleteIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_issues.Remove(issueNumber));
        }
    }

    // ── File Management ─────────────────────────────────────────────

    public Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var b = branch ?? "main";
            if (_files.TryGetValue(b, out var tree) && tree.TryGetValue(path, out var content))
                return Task.FromResult<string?>(content);
            return Task.FromResult<string?>(null);
        }
    }

    public Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var key = $"{branch ?? "main"}:{path}";
            if (_binaryFiles.TryGetValue(key, out var bytes))
                return Task.FromResult<byte[]?>(bytes.ToArray());
            // Fall back to text content encoded as UTF8
            var b = branch ?? "main";
            if (_files.TryGetValue(b, out var tree) && tree.TryGetValue(path, out var content))
                return Task.FromResult<byte[]?>(System.Text.Encoding.UTF8.GetBytes(content));
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var b = branch ?? "main";
            EnsureBranchExists(b);
            if (!_files.ContainsKey(b))
                _files[b] = new Dictionary<string, string>();
            _files[b][path] = content;
            RecordCommit(b, commitMessage);
            return Task.CompletedTask;
        }
    }

    public Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var b = branch ?? "main";
            if (_files.TryGetValue(b, out var tree))
                tree.Remove(path);
            RecordCommit(b, commitMessage);
            return Task.CompletedTask;
        }
    }

    public Task BatchCommitFilesAsync(IReadOnlyList<(string Path, string Content)> files, string commitMessage, string branch, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureBranchExists(branch);
            if (!_files.ContainsKey(branch))
                _files[branch] = new Dictionary<string, string>();
            foreach (var (path, content) in files)
                _files[branch][path] = content;
            RecordCommit(branch, commitMessage);
            return Task.CompletedTask;
        }
    }

    public Task<string?> CommitBinaryFileAsync(string path, byte[] content, string commitMessage, string branch, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureBranchExists(branch);
            _binaryFiles[$"{branch}:{path}"] = content.ToArray();
            RecordCommit(branch, commitMessage);
            return Task.FromResult<string?>($"https://raw.githubusercontent.com/{RepositoryFullName}/{branch}/{path}");
        }
    }

    // ── Branches ────────────────────────────────────────────────────

    public Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_branches.ContainsKey(branchName))
                return Task.CompletedTask; // idempotent
            EnsureBranchExists(fromBranch);
            _branches[branchName] = _branches[fromBranch];
            // Copy file tree from source branch
            _files[branchName] = _files.TryGetValue(fromBranch, out var srcFiles)
                ? new Dictionary<string, string>(srcFiles)
                : new Dictionary<string, string>();
            return Task.CompletedTask;
        }
    }

    public Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_branches.ContainsKey(branchName));
        }
    }

    public Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _branches.Remove(branchName);
            _files.Remove(branchName);
            return Task.CompletedTask; // no-op if missing
        }
    }

    public Task<IReadOnlyList<string>> ListBranchesAsync(string? prefix = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var branches = _branches.Keys.AsEnumerable();
            if (prefix != null)
                branches = branches.Where(b => b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<IReadOnlyList<string>>(branches.ToList());
        }
    }

    public Task CleanRepoToBaselineAsync(IReadOnlyList<string> preserveFiles, string commitMessage, string branch = "main", CancellationToken ct = default)
    {
        lock (_lock)
        {
            var preserve = new HashSet<string>(preserveFiles, StringComparer.OrdinalIgnoreCase);
            if (_files.TryGetValue(branch, out var tree))
            {
                var toRemove = tree.Keys.Where(k => !preserve.Contains(k)).ToList();
                foreach (var key in toRemove)
                    tree.Remove(key);
            }
            RecordCommit(branch, commitMessage);
            return Task.CompletedTask;
        }
    }

    public Task<bool> UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_prs.TryGetValue(prNumber, out var pr)) return Task.FromResult(false);
            // Simulate merging base into head
            if (_files.TryGetValue(pr.BaseBranch, out var baseFiles) &&
                _files.TryGetValue(pr.HeadBranch, out var headFiles))
            {
                foreach (var (path, content) in baseFiles)
                {
                    if (!headFiles.ContainsKey(path))
                        headFiles[path] = content;
                }
            }
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsBranchBehindMainAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_prs.TryGetValue(prNumber, out var pr)) return Task.FromResult(false);
            // Compare head commit ancestry
            var headSha = _branches.GetValueOrDefault(pr.HeadBranch);
            var mainSha = _branches.GetValueOrDefault(pr.BaseBranch);
            return Task.FromResult(headSha != mainSha && headSha != null && mainSha != null);
        }
    }

    public Task<bool> RebaseBranchOnMainAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_prs.TryGetValue(prNumber, out var pr)) return Task.FromResult(false);
            // Simulate rebase: take files unique to head, apply on top of main
            if (_files.TryGetValue(pr.BaseBranch, out var baseFiles) &&
                _files.TryGetValue(pr.HeadBranch, out var headFiles))
            {
                var merged = new Dictionary<string, string>(baseFiles);
                foreach (var (path, content) in headFiles)
                    merged[path] = content;
                _files[pr.HeadBranch] = merged;
                var sha = MakeSha();
                _branches[pr.HeadBranch] = sha;
                _commits[sha] = new InMemoryCommit(sha, _branches[pr.BaseBranch], merged, "Rebase onto main", DateTime.UtcNow);
            }
            return Task.FromResult(true);
        }
    }

    // ── PR File Inspection ──────────────────────────────────────────

    public Task<IReadOnlyList<AgentPullRequest>> GetMergedPullRequestsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AgentPullRequest>>(
                _prs.Values.Where(p => p.IsMerged).Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_prChangedFiles.TryGetValue(prNumber, out var files))
                return Task.FromResult<IReadOnlyList<string>>(files.ToList());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    public Task<IReadOnlyList<string>> GetPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_prCommits.TryGetValue(prNumber, out var commits))
                return Task.FromResult<IReadOnlyList<string>>(commits.Select(c => c.Message).ToList());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    public Task<IReadOnlyList<PullRequestFileDiff>> GetPullRequestFilesWithPatchAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_prChangedFiles.TryGetValue(prNumber, out var changedFiles))
                return Task.FromResult<IReadOnlyList<PullRequestFileDiff>>(Array.Empty<PullRequestFileDiff>());

            var diffs = changedFiles.Select(f => new PullRequestFileDiff
            {
                FileName = f,
                Status = "modified",
                Patch = null, // simplified — no real diff generation
                Additions = 10,
                Deletions = 0
            }).ToList();
            return Task.FromResult<IReadOnlyList<PullRequestFileDiff>>(diffs);
        }
    }

    public Task CreatePullRequestReviewWithCommentsAsync(
        int prNumber, string body, string eventType,
        IReadOnlyList<InlineReviewComment> comments, string? commitId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsurePrExists(prNumber);
            if (!_reviewThreads.ContainsKey(prNumber))
                _reviewThreads[prNumber] = new List<ReviewThread>();

            foreach (var comment in comments)
            {
                _reviewThreads[prNumber].Add(new ReviewThread
                {
                    Id = Interlocked.Increment(ref _nextGitHubId),
                    NodeId = $"RT_{_nextGitHubId}",
                    FilePath = comment.FilePath,
                    Line = comment.Line,
                    Body = comment.Body,
                    Author = "test-reviewer",
                    IsResolved = false,
                    CreatedAt = DateTime.UtcNow
                });
            }

            // Also add as a comment
            if (!_prComments.ContainsKey(prNumber))
                _prComments[prNumber] = new List<IssueComment>();
            _prComments[prNumber].Add(new IssueComment
            {
                Id = Interlocked.Increment(ref _nextGitHubId),
                Author = "test-reviewer",
                Body = $"[{eventType}] {body} ({comments.Count} inline comments)",
                CreatedAt = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<ReviewThread>> GetPullRequestReviewThreadsAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_reviewThreads.TryGetValue(prNumber, out var threads))
                return Task.FromResult<IReadOnlyList<ReviewThread>>(threads.ToList());
            return Task.FromResult<IReadOnlyList<ReviewThread>>(Array.Empty<ReviewThread>());
        }
    }

    public Task ReplyAndResolveReviewThreadAsync(int prNumber, long commentId, string nodeId, string replyBody, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_reviewThreads.TryGetValue(prNumber, out var threads))
            {
                for (int i = 0; i < threads.Count; i++)
                {
                    if (threads[i].NodeId == nodeId)
                    {
                        threads[i] = threads[i] with { IsResolved = true };
                        break;
                    }
                }
            }
            return Task.CompletedTask;
        }
    }

    public Task ReplyToReviewCommentAsync(int prNumber, long commentId, string replyBody, CancellationToken ct = default)
    {
        // Reply without resolving — no state change needed for in-memory mock
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>> GetPullRequestCommitsWithDatesAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_prCommits.TryGetValue(prNumber, out var commits))
                return Task.FromResult<IReadOnlyList<(string, string, DateTime)>>(commits.ToList());
            return Task.FromResult<IReadOnlyList<(string, string, DateTime)>>(Array.Empty<(string, string, DateTime)>());
        }
    }

    // ── Sub-Issues and Dependencies ─────────────────────────────────

    public Task<bool> AddSubIssueAsync(int parentIssueNumber, long childIssueGitHubId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var child = _issues.Values.FirstOrDefault(i => i.GitHubId == childIssueGitHubId);
            if (child == null) return Task.FromResult(false);
            if (!_subIssues.ContainsKey(parentIssueNumber))
                _subIssues[parentIssueNumber] = new List<int>();
            _subIssues[parentIssueNumber].Add(child.Number);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<AgentIssue>> GetSubIssuesAsync(int parentIssueNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_subIssues.TryGetValue(parentIssueNumber, out var childNumbers))
                return Task.FromResult<IReadOnlyList<AgentIssue>>(Array.Empty<AgentIssue>());
            var children = childNumbers
                .Where(n => _issues.ContainsKey(n))
                .Select(n => Clone(_issues[n]))
                .ToList();
            return Task.FromResult<IReadOnlyList<AgentIssue>>(children);
        }
    }

    public Task<bool> AddIssueDependencyAsync(int blockedIssueNumber, long blockingIssueGitHubId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var blocker = _issues.Values.FirstOrDefault(i => i.GitHubId == blockingIssueGitHubId);
            if (blocker == null) return Task.FromResult(false);
            if (!_dependencies.ContainsKey(blockedIssueNumber))
                _dependencies[blockedIssueNumber] = new List<int>();
            _dependencies[blockedIssueNumber].Add(blocker.Number);
            return Task.FromResult(true);
        }
    }

    // ── Repository Structure ────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string branch = "main", CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(branch, out var tree))
                return Task.FromResult<IReadOnlyList<string>>(tree.Keys.ToList());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    public Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_commits.TryGetValue(commitSha, out var commit))
                return Task.FromResult<IReadOnlyList<string>>(commit.Tree.Keys.ToList());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    // ── Rate Limiting ───────────────────────────────────────────────

    public Task<GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new GitHubRateLimitInfo
        {
            Remaining = 5000,
            Limit = 5000,
            ResetAt = DateTime.UtcNow.AddMinutes(60),
            TotalApiCalls = 0,
            IsRateLimited = false
        });
    }

    // ── Test Helpers ────────────────────────────────────────────────

    /// <summary>Seed files on a branch for test setup.</summary>
    public void SeedFiles(string branch, Dictionary<string, string> files)
    {
        lock (_lock)
        {
            if (!_branches.ContainsKey(branch))
            {
                var sha = MakeSha();
                _branches[branch] = sha;
                _commits[sha] = new InMemoryCommit(sha, null, new Dictionary<string, string>(files), "Seed", DateTime.UtcNow);
            }
            _files[branch] = new Dictionary<string, string>(files);
        }
    }

    /// <summary>Seed a PR directly for test setup.</summary>
    public AgentPullRequest SeedPullRequest(string title, string headBranch, string baseBranch = "main",
        string state = "open", string[]? labels = null, Dictionary<string, string>? changedFiles = null)
    {
        lock (_lock)
        {
            if (!_branches.ContainsKey(headBranch))
                _branches[headBranch] = _branches.GetValueOrDefault("main") ?? MakeSha();
            if (!_files.ContainsKey(headBranch))
                _files[headBranch] = new Dictionary<string, string>();

            var number = _nextNumber++;
            var sha = _branches.GetValueOrDefault(headBranch) ?? MakeSha();
            var pr = new AgentPullRequest
            {
                Number = number,
                Title = title,
                Body = $"Test PR: {title}",
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                HeadSha = sha,
                State = state,
                Labels = new List<string>(labels ?? Array.Empty<string>()),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                MergedAt = state == "closed" ? DateTime.UtcNow : null,
                Url = $"https://github.com/{RepositoryFullName}/pull/{number}"
            };
            _prs[number] = pr;
            _prComments[number] = new List<IssueComment>();
            _reviewThreads[number] = new List<ReviewThread>();
            _prChangedFiles[number] = changedFiles?.Keys.ToList() ?? new List<string>();
            _prCommits[number] = new List<(string, string, DateTime)>
            {
                (sha, $"Commit for {title}", DateTime.UtcNow)
            };

            if (changedFiles != null)
            {
                foreach (var (path, content) in changedFiles)
                    _files[headBranch][path] = content;
            }

            return Clone(pr);
        }
    }

    /// <summary>Seed an issue directly for test setup.</summary>
    public AgentIssue SeedIssue(string title, string body = "", string state = "open", string[]? labels = null)
    {
        lock (_lock)
        {
            var number = _nextNumber++;
            var issue = new AgentIssue
            {
                GitHubId = Interlocked.Increment(ref _nextGitHubId),
                Number = number,
                Title = title,
                Body = body,
                State = state,
                Labels = new List<string>(labels ?? Array.Empty<string>()),
                CreatedAt = DateTime.UtcNow,
                Url = $"https://github.com/{RepositoryFullName}/issues/{number}"
            };
            _issues[number] = issue;
            _issueComments[number] = new List<IssueComment>();
            return Clone(issue);
        }
    }

    /// <summary>Get count of stored entities for assertions.</summary>
    public (int Prs, int Issues, int Branches) GetCounts()
    {
        lock (_lock)
        {
            return (_prs.Count, _issues.Count, _branches.Count);
        }
    }

    // ── Private Helpers ─────────────────────────────────────────────

    private string MakeSha() => Guid.NewGuid().ToString("N")[..12];

    private void RecordCommit(string branch, string message)
    {
        var sha = MakeSha();
        var tree = _files.TryGetValue(branch, out var t) ? new Dictionary<string, string>(t) : new();
        _commits[sha] = new InMemoryCommit(sha, _branches.GetValueOrDefault(branch), tree, message, DateTime.UtcNow);
        _branches[branch] = sha;
    }

    private List<string> ComputeChangedFiles(string headBranch, string baseBranch)
    {
        var headFiles = _files.GetValueOrDefault(headBranch) ?? new Dictionary<string, string>();
        var baseFiles = _files.GetValueOrDefault(baseBranch) ?? new Dictionary<string, string>();
        return headFiles.Keys
            .Where(k => !baseFiles.ContainsKey(k) || baseFiles[k] != headFiles[k])
            .ToList();
    }

    private void EnsurePrExists(int number)
    {
        if (ValidateInputs && !_prs.ContainsKey(number))
            throw new InvalidOperationException($"PR #{number} does not exist");
    }

    private void EnsureIssueExists(int number)
    {
        if (ValidateInputs && !_issues.ContainsKey(number))
            throw new InvalidOperationException($"Issue #{number} does not exist");
    }

    private void EnsureBranchExists(string branch)
    {
        if (ValidateInputs && !_branches.ContainsKey(branch))
            throw new InvalidOperationException($"Branch '{branch}' does not exist");
    }

    // Deep-clone records to prevent test code from mutating service state
    private static AgentPullRequest Clone(AgentPullRequest pr) => pr with
    {
        Labels = new List<string>(pr.Labels),
        ReviewComments = new List<string>(pr.ReviewComments),
        Comments = new List<IssueComment>(pr.Comments),
        ChangedFiles = new List<string>(pr.ChangedFiles)
    };

    private static AgentIssue Clone(AgentIssue issue) => issue with
    {
        Labels = new List<string>(issue.Labels),
        Comments = new List<IssueComment>(issue.Comments)
    };

    private sealed record InMemoryCommit(
        string Sha,
        string? ParentSha,
        Dictionary<string, string> Tree,
        string Message,
        DateTime Timestamp);
}
