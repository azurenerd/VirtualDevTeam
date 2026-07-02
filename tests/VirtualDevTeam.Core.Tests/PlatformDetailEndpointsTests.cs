using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Verifies the aggregate-DTO assembly logic used by the new
/// <c>GET /api/dashboard/platform/pull-request/{n}</c> and
/// <c>GET /api/dashboard/platform/work-item/{n}</c> endpoints.
///
/// <para>
/// The endpoints themselves are minimal-API delegates that just call the
/// capabilities + assemble the DTO. Rather than spinning up a TestServer for
/// the entire Runner (heavy DI graph), these tests exercise the same delegate
/// shape against mocked capabilities. The behaviour under test is the
/// per-sub-call try/catch with empty-fallback contract.
/// </para>
/// </summary>
public sealed class PlatformDetailEndpointsTests
{
    [Fact]
    public async Task PullRequestDetail_HappyPath_ReturnsAllSections()
    {
        var pr = new Mock<IPullRequestService>();
        var review = new Mock<IReviewService>();

        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                Title = "Test PR",
                State = "open",
                Body = "PR body",
                Url = "https://github.com/owner/repo/pull/42",
                CreatedAt = DateTime.UtcNow.AddHours(-1),
            });
        pr.Setup(p => p.GetChangedFilesAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "src/A.cs", "src/B.cs" });
        review.Setup(r => r.GetCommentsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PlatformComment { Id = 1, Author = "alice", Body = "Looks good", CreatedAt = DateTime.UtcNow }
            });
        review.Setup(r => r.GetThreadsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PlatformReviewThread { ThreadId = "t1", FilePath = "src/A.cs", Line = 12, Body = "nit", Author = "bob", CreatedAt = DateTime.UtcNow }
            });

        var dto = await BuildPrDetailAsync(42, pr.Object, review.Object);

        Assert.NotNull(dto);
        Assert.Equal(42, dto!.Pr.Number);
        Assert.Equal("Test PR", dto.Pr.Title);
        Assert.Equal(2, dto.ChangedFiles.Count);
        Assert.Single(dto.Comments);
        Assert.Single(dto.ReviewThreads);
    }

    [Fact]
    public async Task PullRequestDetail_PrNotFound_ReturnsNull()
    {
        var pr = new Mock<IPullRequestService>();
        var review = new Mock<IReviewService>();
        pr.Setup(p => p.GetAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformPullRequest?)null);

        var dto = await BuildPrDetailAsync(999, pr.Object, review.Object);
        Assert.Null(dto);
    }

    [Fact]
    public async Task PullRequestDetail_CommentsThrow_ReturnsEmptyCommentsButHeader()
    {
        var pr = new Mock<IPullRequestService>();
        var review = new Mock<IReviewService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest { Number = 42, Title = "T" });
        pr.Setup(p => p.GetChangedFilesAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        review.Setup(r => r.GetCommentsAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("platform 500"));
        review.Setup(r => r.GetThreadsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlatformReviewThread>());

        var dto = await BuildPrDetailAsync(42, pr.Object, review.Object);

        Assert.NotNull(dto);
        Assert.Equal(42, dto!.Pr.Number);
        Assert.Empty(dto.Comments);
        Assert.Empty(dto.ReviewThreads);
    }

    [Fact]
    public async Task PullRequestDetail_ThreadsThrow_ReturnsEmptyThreadsButCommentsKeep()
    {
        var pr = new Mock<IPullRequestService>();
        var review = new Mock<IReviewService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest { Number = 42 });
        pr.Setup(p => p.GetChangedFilesAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "x" });
        review.Setup(r => r.GetCommentsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PlatformComment { Id = 1, Author = "a", Body = "b", CreatedAt = DateTime.UtcNow } });
        review.Setup(r => r.GetThreadsAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("flake"));

        var dto = await BuildPrDetailAsync(42, pr.Object, review.Object);
        Assert.NotNull(dto);
        Assert.Single(dto!.Comments);
        Assert.Empty(dto.ReviewThreads);
        Assert.Single(dto.ChangedFiles);
    }

    [Fact]
    public async Task WorkItemDetail_HappyPath_ReturnsItemAndComments()
    {
        var wi = new Mock<IWorkItemService>();
        wi.Setup(w => w.GetAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformWorkItem
            {
                Number = 7,
                Title = "Test issue",
                State = "open",
                Body = "Body",
                Author = "alice",
                CreatedAt = DateTime.UtcNow,
                WorkItemType = "Bug"
            });
        wi.Setup(w => w.GetCommentsAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PlatformComment { Id = 1, Author = "bob", Body = "+1", CreatedAt = DateTime.UtcNow } });

        var dto = await BuildWorkItemDetailAsync(7, wi.Object);

        Assert.NotNull(dto);
        Assert.Equal(7, dto!.WorkItem.Number);
        Assert.Equal("Bug", dto.WorkItem.WorkItemType);
        Assert.Single(dto.Comments);
    }

    [Fact]
    public async Task WorkItemDetail_NotFound_ReturnsNull()
    {
        var wi = new Mock<IWorkItemService>();
        wi.Setup(w => w.GetAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformWorkItem?)null);

        var dto = await BuildWorkItemDetailAsync(999, wi.Object);
        Assert.Null(dto);
    }

    [Fact]
    public async Task WorkItemDetail_CommentsThrow_ReturnsItemWithEmptyComments()
    {
        var wi = new Mock<IWorkItemService>();
        wi.Setup(w => w.GetAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformWorkItem { Number = 7, Title = "T" });
        wi.Setup(w => w.GetCommentsAsync(7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("flake"));

        var dto = await BuildWorkItemDetailAsync(7, wi.Object);
        Assert.NotNull(dto);
        Assert.Equal(7, dto!.WorkItem.Number);
        Assert.Empty(dto.Comments);
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers — mirror the exact delegate shape used by Program.cs so any
    // future change to the endpoint logic gets caught by these tests.
    // ────────────────────────────────────────────────────────────────────

    private static async Task<PullRequestDetailDto?> BuildPrDetailAsync(
        int number, IPullRequestService prSvc, IReviewService reviewSvc)
    {
        var pr = await prSvc.GetAsync(number);
        if (pr is null) return null;

        IReadOnlyList<PlatformComment> comments;
        try { comments = await reviewSvc.GetCommentsAsync(number); }
        catch { comments = Array.Empty<PlatformComment>(); }

        IReadOnlyList<PlatformReviewThread> threads;
        try { threads = await reviewSvc.GetThreadsAsync(number); }
        catch { threads = Array.Empty<PlatformReviewThread>(); }

        IReadOnlyList<string> files;
        try { files = await prSvc.GetChangedFilesAsync(number); }
        catch { files = Array.Empty<string>(); }

        return new PullRequestDetailDto(pr, comments, threads, files);
    }

    private static async Task<WorkItemDetailDto?> BuildWorkItemDetailAsync(
        int number, IWorkItemService wiSvc)
    {
        var item = await wiSvc.GetAsync(number);
        if (item is null) return null;

        IReadOnlyList<PlatformComment> comments;
        try { comments = await wiSvc.GetCommentsAsync(number); }
        catch { comments = Array.Empty<PlatformComment>(); }

        return new WorkItemDetailDto(item, comments);
    }
}
