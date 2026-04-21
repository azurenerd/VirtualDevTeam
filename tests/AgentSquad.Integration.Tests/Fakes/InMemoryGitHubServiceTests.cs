namespace AgentSquad.Integration.Tests.Fakes;

using AgentSquad.Core.GitHub.Models;

public class InMemoryGitHubServiceTests
{
    private readonly InMemoryGitHubService _svc = new();

    // ── Issues ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIssue_AssignsIncrementingNumbers()
    {
        var i1 = await _svc.CreateIssueAsync("Issue 1", "body", ["bug"]);
        var i2 = await _svc.CreateIssueAsync("Issue 2", "body", ["enhancement"]);

        Assert.True(i2.Number > i1.Number);
        Assert.Equal("open", i1.State);
        Assert.Contains("bug", i1.Labels);
    }

    [Fact]
    public async Task GetIssuesByLabel_FiltersCorrectly()
    {
        await _svc.CreateIssueAsync("Bug 1", "", ["bug"]);
        await _svc.CreateIssueAsync("Feature 1", "", ["enhancement"]);
        await _svc.CreateIssueAsync("Bug 2", "", ["bug", "critical"]);

        var bugs = await _svc.GetIssuesByLabelAsync("bug");
        Assert.Equal(2, bugs.Count);
    }

    [Fact]
    public async Task CloseIssue_SetsStateAndClosedAt()
    {
        var issue = await _svc.CreateIssueAsync("To close", "", []);
        await _svc.CloseIssueAsync(issue.Number);

        var closed = await _svc.GetIssueAsync(issue.Number);
        Assert.NotNull(closed);
        Assert.Equal("closed", closed.State);
        Assert.NotNull(closed.ClosedAt);
    }

    [Fact]
    public async Task DeleteIssue_RemovesFromStore()
    {
        var issue = await _svc.CreateIssueAsync("To delete", "", []);
        var deleted = await _svc.DeleteIssueAsync(issue.Number);
        Assert.True(deleted);

        var result = await _svc.GetIssueAsync(issue.Number);
        Assert.Null(result);
    }

    [Fact]
    public async Task IssueComments_TrackCountCorrectly()
    {
        var issue = await _svc.CreateIssueAsync("Commented", "", []);
        await _svc.AddIssueCommentAsync(issue.Number, "First comment");
        await _svc.AddIssueCommentAsync(issue.Number, "Second comment");

        var updated = await _svc.GetIssueAsync(issue.Number);
        Assert.NotNull(updated);
        Assert.Equal(2, updated.CommentCount);

        var comments = await _svc.GetIssueCommentsAsync(issue.Number);
        Assert.Equal(2, comments.Count);
        Assert.Equal("First comment", comments[0].Body);
    }

    // ── Pull Requests ───────────────────────────────────────────────

    [Fact]
    public async Task CreatePR_RequiresExistingBranch()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreatePullRequestAsync("Test PR", "body", "missing-branch", "main", []));
    }

    [Fact]
    public async Task CreateAndMergePR_CopiesFilesToBase()
    {
        await _svc.CreateBranchAsync("feature", "main");
        await _svc.CreateOrUpdateFileAsync("src/app.cs", "class App {}", "Add app", "feature");

        var pr = await _svc.CreatePullRequestAsync("Test: Feature", "body", "feature", "main", ["in-progress"]);
        Assert.Equal("open", pr.State);

        await _svc.MergePullRequestAsync(pr.Number, "Merge feature");

        var merged = await _svc.GetPullRequestAsync(pr.Number);
        Assert.NotNull(merged);
        Assert.Equal("closed", merged.State);
        Assert.True(merged.IsMerged);

        // File should be in main now
        var content = await _svc.GetFileContentAsync("src/app.cs");
        Assert.Equal("class App {}", content);
    }

    [Fact]
    public async Task MergePR_RejectsAlreadyClosed()
    {
        await _svc.CreateBranchAsync("feat2", "main");
        var pr = await _svc.CreatePullRequestAsync("Test", "body", "feat2", "main", []);
        await _svc.ClosePullRequestAsync(pr.Number);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.MergePullRequestAsync(pr.Number));
    }

    [Fact]
    public async Task GetPullRequestsForAgent_MatchesTitlePrefix()
    {
        await _svc.CreateBranchAsync("b1");
        await _svc.CreateBranchAsync("b2");
        await _svc.CreatePullRequestAsync("SoftwareEngineer: Task 1", "", "b1", "main", []);
        await _svc.CreatePullRequestAsync("Architect: Design", "", "b2", "main", []);

        var sePrs = await _svc.GetPullRequestsForAgentAsync("SoftwareEngineer");
        Assert.Single(sePrs);
        Assert.Contains("Task 1", sePrs[0].Title);
    }

    // ── Reviews ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewWithInlineComments_CreatesThreads()
    {
        var pr = _svc.SeedPullRequest("SE: Task", "feat-branch", changedFiles: new()
        {
            { "src/file.cs", "content" }
        });

        var comments = new List<InlineReviewComment>
        {
            new() { FilePath = "src/file.cs", Line = 10, Body = "Consider error handling" },
            new() { FilePath = "src/file.cs", Line = 25, Body = "Add null check" }
        };

        await _svc.CreatePullRequestReviewWithCommentsAsync(pr.Number, "Review", "COMMENT", comments);

        var threads = await _svc.GetPullRequestReviewThreadsAsync(pr.Number);
        Assert.Equal(2, threads.Count);
        Assert.False(threads[0].IsResolved);
    }

    [Fact]
    public async Task ResolveReviewThread_SetsResolved()
    {
        var pr = _svc.SeedPullRequest("SE: Task", "feat-resolve");
        var comment = new InlineReviewComment { FilePath = "f.cs", Line = 1, Body = "Fix this" };
        await _svc.CreatePullRequestReviewWithCommentsAsync(pr.Number, "Review", "COMMENT", [comment]);

        var threads = await _svc.GetPullRequestReviewThreadsAsync(pr.Number);
        Assert.Single(threads);

        await _svc.ReplyAndResolveReviewThreadAsync(pr.Number, threads[0].Id, threads[0].NodeId, "Fixed");

        threads = await _svc.GetPullRequestReviewThreadsAsync(pr.Number);
        Assert.True(threads[0].IsResolved);
    }

    // ── Branches ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateBranch_IsIdempotent()
    {
        await _svc.CreateBranchAsync("idempotent-branch");
        await _svc.CreateBranchAsync("idempotent-branch"); // should not throw

        var exists = await _svc.BranchExistsAsync("idempotent-branch");
        Assert.True(exists);
    }

    [Fact]
    public async Task DeleteBranch_RemovesBranchAndFiles()
    {
        await _svc.CreateBranchAsync("temp");
        await _svc.CreateOrUpdateFileAsync("f.txt", "data", "add", "temp");
        await _svc.DeleteBranchAsync("temp");

        Assert.False(await _svc.BranchExistsAsync("temp"));
        Assert.Null(await _svc.GetFileContentAsync("f.txt", "temp"));
    }

    [Fact]
    public async Task ListBranches_FiltersPrefix()
    {
        await _svc.CreateBranchAsync("agent/se-1/task-1");
        await _svc.CreateBranchAsync("agent/se-2/task-2");
        await _svc.CreateBranchAsync("feature/other");

        var agentBranches = await _svc.ListBranchesAsync("agent/");
        Assert.Equal(2, agentBranches.Count);
    }

    // ── Files ───────────────────────────────────────────────────────

    [Fact]
    public async Task BatchCommitFiles_AddsMultipleFilesAtomically()
    {
        var files = new List<(string, string)>
        {
            ("src/a.cs", "class A {}"),
            ("src/b.cs", "class B {}"),
            ("README.md", "# Hello")
        };
        await _svc.BatchCommitFilesAsync(files, "Add files", "main");

        Assert.Equal("class A {}", await _svc.GetFileContentAsync("src/a.cs"));
        Assert.Equal("class B {}", await _svc.GetFileContentAsync("src/b.cs"));
        Assert.Equal("# Hello", await _svc.GetFileContentAsync("README.md"));
    }

    [Fact]
    public async Task GetRepositoryTree_ReturnsAllFiles()
    {
        _svc.SeedFiles("main", new()
        {
            { "src/a.cs", "a" },
            { "src/b.cs", "b" },
            { "docs/README.md", "readme" }
        });

        var tree = await _svc.GetRepositoryTreeAsync("main");
        Assert.Equal(3, tree.Count);
        Assert.Contains("src/a.cs", tree);
    }

    [Fact]
    public async Task CleanRepoToBaseline_PreservesOnlySpecifiedFiles()
    {
        _svc.SeedFiles("main", new()
        {
            { "data.json", "{}" },
            { "src/app.cs", "code" },
            { "docs/README.md", "readme" }
        });

        await _svc.CleanRepoToBaselineAsync(["data.json"], "Clean", "main");

        var tree = await _svc.GetRepositoryTreeAsync("main");
        Assert.Single(tree);
        Assert.Equal("data.json", tree[0]);
    }

    // ── Sub-Issues ──────────────────────────────────────────────────

    [Fact]
    public async Task SubIssues_TrackParentChildRelationship()
    {
        var parent = await _svc.CreateIssueAsync("Parent", "", []);
        var child1 = await _svc.CreateIssueAsync("Child 1", "", []);
        var child2 = await _svc.CreateIssueAsync("Child 2", "", []);

        Assert.True(await _svc.AddSubIssueAsync(parent.Number, child1.GitHubId));
        Assert.True(await _svc.AddSubIssueAsync(parent.Number, child2.GitHubId));

        var subs = await _svc.GetSubIssuesAsync(parent.Number);
        Assert.Equal(2, subs.Count);
    }

    // ── Test Helpers ────────────────────────────────────────────────

    [Fact]
    public void SeedPullRequest_CreatesCompleteState()
    {
        var pr = _svc.SeedPullRequest("SE: Task", "branch-1", labels: ["in-progress"],
            changedFiles: new() { { "src/file.cs", "code" } });

        Assert.Equal("open", pr.State);
        Assert.Contains("in-progress", pr.Labels);
        var counts = _svc.GetCounts();
        Assert.Equal(1, counts.Prs);
    }

    [Fact]
    public async Task Clone_PreventsMutatingServiceState()
    {
        var issue = _svc.SeedIssue("Original", labels: ["bug"]);

        // Mutating the returned clone should NOT affect the stored issue
        issue.Labels.Add("should-not-persist");

        var fresh = await _svc.GetIssueAsync(issue.Number);
        Assert.NotNull(fresh);
        Assert.DoesNotContain("should-not-persist", fresh.Labels);
    }

    // ── Rate Limit ──────────────────────────────────────────────────

    [Fact]
    public async Task GetRateLimit_ReturnsUnlimited()
    {
        var rl = await _svc.GetRateLimitAsync();
        Assert.Equal(5000, rl.Remaining);
        Assert.False(rl.IsRateLimited);
    }
}
