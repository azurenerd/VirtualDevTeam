using VirtualDevTeam.Dashboard.Components.Shared;
using VirtualDevTeam.Dashboard.Services;
using Moq;
using Xunit;

namespace VirtualDevTeam.Dashboard.Unit.Tests;

/// <summary>
/// Tests for <see cref="MarkdownBody"/>'s anchor-href rewriting helper. The page-level
/// integration is covered by bUnit elsewhere; here we exercise the pure string transformation
/// directly so we can pin down every supported URL shape without spinning up the render pipeline.
///
/// All tests stub <see cref="IPlatformLinkService"/> with InternalNavigationDefault = true so we
/// see the actual route the helper proposes. Counter-test at the end verifies that flipping the
/// flag off (operator prefers GitHub UI) leaves anchors untouched.
/// </summary>
public sealed class MarkdownBodyAnchorRewriteTests
{
    private static IPlatformLinkService BuildLinkService(bool internalNavigationDefault = true)
    {
        var mock = new Mock<IPlatformLinkService>();
        mock.Setup(s => s.InternalNavigationDefault).Returns(internalNavigationDefault);
        mock.Setup(s => s.BuildPullRequestUrl(It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<int, string?>((n, _) => internalNavigationDefault ? $"/repository/pull-request/{n}" : $"https://github.com/owner/repo/pull/{n}");
        mock.Setup(s => s.BuildIssueUrl(It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<int, string?>((n, _) => internalNavigationDefault ? $"/repository/issue/{n}" : $"https://github.com/owner/repo/issues/{n}");
        mock.Setup(s => s.BuildFileUrl(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns<string, string?, string?>((path, branch, _) =>
            {
                if (!internalNavigationDefault) return $"https://github.com/owner/repo/blob/{branch ?? "main"}/{path}";
                var qs = string.IsNullOrWhiteSpace(branch) ? "" : $"?branch={System.Uri.EscapeDataString(branch!)}";
                return $"/repository/files/{path.TrimStart('/')}{qs}";
            });
        return mock.Object;
    }

    [Fact]
    public void TryRewriteHref_GitHubPullRequest_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref("https://github.com/octocat/Hello-World/pull/42", svc);
        Assert.Equal("/repository/pull-request/42", result);
    }

    [Fact]
    public void TryRewriteHref_GitHubPullRequest_WithFragmentAndQuery_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref(
            "https://github.com/octocat/Hello-World/pull/42#issuecomment-7?foo=bar", svc);
        Assert.Equal("/repository/pull-request/42", result);
    }

    [Fact]
    public void TryRewriteHref_GitHubIssue_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref("https://github.com/owner/repo/issues/1234", svc);
        Assert.Equal("/repository/issue/1234", result);
    }

    [Fact]
    public void TryRewriteHref_GitHubBlob_RoutesInternalWithBranch()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref(
            "https://github.com/owner/repo/blob/main/src/Foo.cs", svc);
        Assert.Equal("/repository/files/src/Foo.cs?branch=main", result);
    }

    [Fact]
    public void TryRewriteHref_GitHubBlob_LineAnchorStripped()
    {
        var svc = BuildLinkService();
        // The internal file viewer doesn't yet honour line anchors; we strip them rather than
        // routing the agent to a broken URL.
        var result = MarkdownBody.TryRewriteHref(
            "https://github.com/owner/repo/blob/main/src/Foo.cs#L12", svc);
        Assert.Equal("/repository/files/src/Foo.cs?branch=main", result);
    }

    [Fact]
    public void TryRewriteHref_GitHubBlob_BranchWithSlash_AmbiguousButAccepts()
    {
        var svc = BuildLinkService();
        // GitHub URLs are inherently ambiguous when the branch contains a slash:
        // `/blob/feature/x/src/Foo.cs` could mean branch="feature" path="x/src/Foo.cs"
        // OR branch="feature/x" path="src/Foo.cs". Without consulting the repo's ref list
        // we can't disambiguate, so we always pick the first segment as the branch and the
        // rest as the path. This is the right choice for the >95% case where branches don't
        // contain slashes; the rare slash-in-branch URL still produces a sensible (if not
        // perfectly accurate) internal route.
        var result = MarkdownBody.TryRewriteHref(
            "https://github.com/o/r/blob/feature/sub/src/Foo.cs", svc);
        Assert.Equal("/repository/files/sub/src/Foo.cs?branch=feature", result);
    }

    [Fact]
    public void TryRewriteHref_AdoPullRequest_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref(
            "https://dev.azure.com/myorg/MyProject/_git/MyRepo/pullrequest/55", svc);
        Assert.Equal("/repository/pull-request/55", result);
    }

    [Fact]
    public void TryRewriteHref_AdoWorkItem_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref(
            "https://dev.azure.com/myorg/MyProject/_workitems/edit/9876", svc);
        Assert.Equal("/repository/issue/9876", result);
    }

    [Fact]
    public void TryRewriteHref_AdoGitFile_RoutesInternal()
    {
        var svc = BuildLinkService();
        var result = MarkdownBody.TryRewriteHref(
            "https://dev.azure.com/myorg/MyProject/_git/MyRepo?path=/src/Foo.cs", svc);
        Assert.Equal("/repository/files/src/Foo.cs", result);
    }

    [Fact]
    public void TryRewriteHref_AdoGitFile_UrlEncodedPath_RoutesInternal()
    {
        var svc = BuildLinkService();
        // ADO URLs in the wild often percent-encode the leading slash.
        var result = MarkdownBody.TryRewriteHref(
            "https://dev.azure.com/org/proj/_git/repo?path=%2Fsrc%2Ffoo%20bar.cs", svc);
        Assert.Equal("/repository/files/src/foo bar.cs", result);
    }

    [Theory]
    [InlineData("https://example.com/some/page")]
    [InlineData("https://google.com/search?q=foo")]
    [InlineData("https://docs.microsoft.com/azure")]
    [InlineData("relative/path/no-scheme")]
    [InlineData("#anchor-only")]
    public void TryRewriteHref_NonPlatformUrl_ReturnsNull(string href)
    {
        var svc = BuildLinkService();
        Assert.Null(MarkdownBody.TryRewriteHref(href, svc));
    }

    [Fact]
    public void TryRewriteHref_EmptyOrNull_ReturnsNull()
    {
        var svc = BuildLinkService();
        Assert.Null(MarkdownBody.TryRewriteHref("", svc));
        Assert.Null(MarkdownBody.TryRewriteHref("   ", svc));
    }

    [Fact]
    public void RewritePlatformAnchorHrefs_RendersMultipleLinks_InOneBody()
    {
        var svc = BuildLinkService();
        var html =
            "<p>See <a href=\"https://github.com/owner/repo/pull/42\">PR #42</a> and " +
            "issue <a href=\"https://github.com/owner/repo/issues/7\">#7</a>. " +
            "Source: <a href=\"https://github.com/owner/repo/blob/main/src/Foo.cs#L10\">Foo.cs</a>. " +
            "External: <a href=\"https://example.com\">example</a>.</p>";

        var result = MarkdownBody.RewritePlatformAnchorHrefs(html, svc);

        Assert.Contains("href=\"/repository/pull-request/42\"", result);
        Assert.Contains("href=\"/repository/issue/7\"", result);
        Assert.Contains("href=\"/repository/files/src/Foo.cs?branch=main\"", result);
        Assert.Contains("href=\"https://example.com\"", result); // unchanged
    }

    [Fact]
    public void RewritePlatformAnchorHrefs_FlagOff_LeavesEverythingAlone()
    {
        var svc = BuildLinkService(internalNavigationDefault: false);
        var html = "<p>See <a href=\"https://github.com/owner/repo/pull/42\">PR #42</a>.</p>";
        var result = MarkdownBody.RewritePlatformAnchorHrefs(html, svc);
        // Operator preferred GitHub UI — anchor untouched.
        Assert.Equal(html, result);
    }

    [Fact]
    public void RewritePlatformAnchorHrefs_NullLinkService_LeavesEverythingAlone()
    {
        var html = "<p>See <a href=\"https://github.com/owner/repo/pull/42\">PR #42</a>.</p>";
        var result = MarkdownBody.RewritePlatformAnchorHrefs(html, null);
        Assert.Equal(html, result);
    }

    [Fact]
    public void RewritePlatformAnchorHrefs_NoAnchorsInBody_ReturnsInputVerbatim()
    {
        var svc = BuildLinkService();
        var html = "<p>Just text, no links here.</p>";
        Assert.Equal(html, MarkdownBody.RewritePlatformAnchorHrefs(html, svc));
    }

    [Fact]
    public void RewritePlatformAnchorHrefs_PreservesOtherAnchorAttributes()
    {
        var svc = BuildLinkService();
        var html = "<a class=\"link\" data-test=\"x\" href=\"https://github.com/o/r/pull/3\" title=\"hi\">PR</a>";
        var result = MarkdownBody.RewritePlatformAnchorHrefs(html, svc);
        Assert.Contains("class=\"link\"", result);
        Assert.Contains("data-test=\"x\"", result);
        Assert.Contains("title=\"hi\"", result);
        Assert.Contains("href=\"/repository/pull-request/3\"", result);
    }
}
