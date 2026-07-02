using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Integration.Tests.Fakes;

namespace VirtualDevTeam.E2E.Tests.Helpers;

/// <summary>
/// Custom assertion helpers for E2E workflow tests.
/// </summary>
public static class AssertionHelpers
{
    /// <summary>Assert that a file exists in the InMemoryGitHubService on the given branch.</summary>
    public static async Task AssertFileExistsAsync(
        InMemoryGitHubService github, string path, string? branch = null)
    {
        var content = await github.GetFileContentAsync(path, branch);
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    /// <summary>Assert that a file contains specific text.</summary>
    public static async Task AssertFileContainsAsync(
        InMemoryGitHubService github, string path, string expectedContent, string? branch = null)
    {
        var content = await github.GetFileContentAsync(path, branch);
        Assert.NotNull(content);
        Assert.Contains(expectedContent, content);
    }

    /// <summary>Assert the expected number of open PRs exist.</summary>
    public static async Task AssertOpenPrCountAsync(
        InMemoryGitHubService github, int expectedCount)
    {
        var prs = await github.GetOpenPullRequestsAsync();
        Assert.Equal(expectedCount, prs.Count);
    }

    /// <summary>Assert that at least one PR was merged.</summary>
    public static async Task AssertAnyPrMergedAsync(InMemoryGitHubService github)
    {
        var prs = await github.GetAllPullRequestsAsync();
        Assert.Contains(prs, pr => pr.IsMerged);
    }

    /// <summary>Assert that all issues are closed.</summary>
    public static async Task AssertAllIssuesClosedAsync(InMemoryGitHubService github)
    {
        var issues = await github.GetAllIssuesAsync();
        if (issues.Count > 0)
        {
            Assert.All(issues, issue =>
                Assert.True(issue.State == "closed",
                    $"Issue #{issue.Number} '{issue.Title}' is still {issue.State}"));
        }
    }

    /// <summary>Assert that a specific number of issues were created.</summary>
    public static async Task AssertIssueCountAsync(
        InMemoryGitHubService github, int expectedCount)
    {
        var issues = await github.GetAllIssuesAsync();
        Assert.Equal(expectedCount, issues.Count);
    }

    /// <summary>Assert issue count is at least the minimum.</summary>
    public static async Task AssertMinIssueCountAsync(
        InMemoryGitHubService github, int minCount)
    {
        var issues = await github.GetAllIssuesAsync();
        Assert.True(issues.Count >= minCount,
            $"Expected at least {minCount} issues, got {issues.Count}");
    }
}
