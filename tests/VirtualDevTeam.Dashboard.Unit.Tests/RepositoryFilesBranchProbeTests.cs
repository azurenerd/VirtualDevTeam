using VirtualDevTeam.Dashboard.Components.Pages;

namespace VirtualDevTeam.Dashboard.Unit.Tests;

/// <summary>
/// Locks the branch-probe ordering used by <see cref="RepositoryFiles"/> when a deep-linked file
/// (e.g. a PR → PMSpec.md link) is not present on the loaded tree's branch. The regression these
/// guard against: clicking a markdown PR link redirected to the repository root because the file
/// lived on the working branch (e.g. AgentDocs/{workingBranch}/PMSpec.md) while the loaded tree was
/// the (often deleted) PR head branch or the default branch.
/// </summary>
public class RepositoryFilesBranchProbeTests
{
    [Fact]
    public void BuildBranchProbeOrder_OrdersQueryThenWorkingThenDefault()
    {
        var order = RepositoryFiles.BuildBranchProbeOrder(
            alreadyTried: "main", queryBranch: "agent/abc/pm", workingBranch: "agencyplugin", defaultBranch: "main");

        Assert.Equal(new[] { "agent/abc/pm", "agencyplugin" }, order);
        // "main" excluded (already tried).
        Assert.DoesNotContain("main", order);
    }

    [Fact]
    public void BuildBranchProbeOrder_ExcludesAlreadyTriedBranch()
    {
        var order = RepositoryFiles.BuildBranchProbeOrder(
            alreadyTried: "agencyplugin", queryBranch: null, workingBranch: "agencyplugin", defaultBranch: "main");

        // Working branch already loaded → only default remains.
        Assert.Equal(new[] { "main" }, order);
    }

    [Fact]
    public void BuildBranchProbeOrder_SkipsNullAndEmptyAndDeduplicates()
    {
        var order = RepositoryFiles.BuildBranchProbeOrder(
            alreadyTried: null, queryBranch: "  ", workingBranch: "agencyplugin", defaultBranch: "agencyplugin");

        // Blank query skipped; duplicate default collapsed.
        Assert.Equal(new[] { "agencyplugin" }, order);
    }

    [Fact]
    public void BuildBranchProbeOrder_EmptyWhenNothingToProbe()
    {
        var order = RepositoryFiles.BuildBranchProbeOrder(
            alreadyTried: "main", queryBranch: null, workingBranch: null, defaultBranch: "main");

        Assert.Empty(order);
    }
}
