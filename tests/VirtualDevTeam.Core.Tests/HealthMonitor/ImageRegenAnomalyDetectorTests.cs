using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests.HealthMonitor;

[SupportedOSPlatform("windows")]
public sealed class ImageRegenAnomalyDetectorTests
{
    private const string ArtPath = "assets/sprites/turret/frame-01.png";
    private const string PreviousSha = "1111111111111111111111111111111111111111";
    private const string LatestSha = "2222222222222222222222222222222222222222";
    private static readonly DateTimeOffset T0 = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputePerceptualHash_DifferentSolidColors_ProducesDifferentHashes()
    {
        var red = SolidPng(32, 32, Color.Red, padBytes: 8 * 1024);
        var blue = SolidPng(32, 32, Color.Blue, padBytes: 8 * 1024);

        var hashRed = ImageRegenAnomalyDetector.ComputePerceptualHash(red);
        var hashBlue = ImageRegenAnomalyDetector.ComputePerceptualHash(blue);

        Assert.NotNull(hashRed);
        Assert.NotNull(hashBlue);
        Assert.NotEqual(hashRed!.Value, hashBlue!.Value);
    }

    [Fact]
    public void ComputePerceptualHash_SameBytesTwice_ProducesIdenticalHashes()
    {
        var bytes = MakePatternedPng(32, 32, padBytes: 8 * 1024);
        var hashA = ImageRegenAnomalyDetector.ComputePerceptualHash(bytes);
        var hashB = ImageRegenAnomalyDetector.ComputePerceptualHash(bytes);

        Assert.NotNull(hashA);
        Assert.Equal(hashA!.Value, hashB!.Value);
    }

    [Fact]
    public void ComputePerceptualHash_VisuallyIdenticalButReencoded_ProducesIdenticalHashes()
    {
        var encA = MakePatternedPng(32, 32, padBytes: 8 * 1024, seed: 1);
        var encB = MakePatternedPng(32, 32, padBytes: 8 * 1024, seed: 2);
        Assert.False(encA.SequenceEqual(encB), "Fixture PNGs are byte-equal — recompress harder.");

        var hashA = ImageRegenAnomalyDetector.ComputePerceptualHash(encA);
        var hashB = ImageRegenAnomalyDetector.ComputePerceptualHash(encB);

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.Equal(hashA!.Value, hashB!.Value);
    }

    [Fact]
    public void ComputePerceptualHash_KnownFixture_ProducesDeterministicValue()
    {
        var red = SolidPng(32, 32, Color.Red, padBytes: 8 * 1024);
        var hash = ImageRegenAnomalyDetector.ComputePerceptualHash(red);

        Assert.NotNull(hash);
        Assert.Equal(0x007E7E7E7E7E7E00UL, hash!.Value.StructureBits);
        Assert.Equal((byte)254, hash.Value.MeanR);
        Assert.Equal((byte)0, hash.Value.MeanG);
        Assert.Equal((byte)0, hash.Value.MeanB);
    }

    [Fact]
    public async Task Detector_DifferentColors_NoFinding()
    {
        var red = SolidPng(32, 32, Color.Red, padBytes: 8 * 1024);
        var blue = SolidPng(32, 32, Color.Blue, padBytes: 8 * 1024);

        var detector = BuildDetector(out var ctx, ArtPath, previousBytes: red, latestBytes: blue);
        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_IdenticalBytes_EmitsWarningFinding()
    {
        var bytes = MakePatternedPng(32, 32, padBytes: 8 * 1024);
        var detector = BuildDetector(out var ctx, ArtPath, previousBytes: bytes, latestBytes: bytes);

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal("image-regen-anomaly", f.DetectorId);
        Assert.Equal(FlowFindingSeverity.Warning, f.Severity);
        Assert.Equal("pr#101", f.TargetResource);
        Assert.Equal($"image-regen-anomaly:101:{ArtPath}", f.DedupKey);
        Assert.Contains("pHash", f.Rationale);
        Assert.Contains(PreviousSha, f.Rationale);
        Assert.Contains(LatestSha, f.Rationale);
    }

    [Fact]
    public async Task Detector_VisuallyIdenticalButReencoded_EmitsFinding()
    {
        var oldBytes = MakePatternedPng(32, 32, padBytes: 8 * 1024, seed: 1);
        var newBytes = MakePatternedPng(32, 32, padBytes: 8 * 1024, seed: 2);
        Assert.False(oldBytes.SequenceEqual(newBytes), "Fixture PNGs must not be byte-equal.");

        var detector = BuildDetector(out var ctx, ArtPath, previousBytes: oldBytes, latestBytes: newBytes);
        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
        Assert.Equal("image-regen-anomaly", findings[0].DetectorId);
    }

    [Fact]
    public async Task Detector_TinyPng_SkippedNoFinding()
    {
        var tiny = SolidPng(2, 2, Color.Red, padBytes: 0);
        Assert.True(tiny.Length < ImageRegenAnomalyDetector.MinSizeBytes,
            $"Fixture must be below MinSizeBytes; got {tiny.Length}.");

        var detector = BuildDetector(out var ctx, ArtPath, previousBytes: tiny, latestBytes: tiny);
        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_NonArtPath_Skipped()
    {
        var bytes = MakePatternedPng(32, 32, padBytes: 8 * 1024);
        var detector = BuildDetector(out var ctx,
            "src/some-engineering-file.png",
            previousBytes: bytes, latestBytes: bytes,
            prTitle: "Software Engineer 1: Implement loot system");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_ArtistPrTitle_ScansEvenOutsideArtPaths()
    {
        var bytes = MakePatternedPng(32, 32, padBytes: 8 * 1024);
        var detector = BuildDetector(out var ctx,
            "scratch/preview.png",
            previousBytes: bytes, latestBytes: bytes,
            prTitle: "Artist 1: Cannon turret sprite sheet");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    [Fact]
    public async Task Detector_DedupKey_IsStableAcrossTicks()
    {
        var bytes = MakePatternedPng(32, 32, padBytes: 8 * 1024);
        var detector = BuildDetector(out var ctx, ArtPath, previousBytes: bytes, latestBytes: bytes);

        var first = await detector.DetectAsync(ctx, default);
        var second = await detector.DetectAsync(ctx, default);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].DedupKey, second[0].DedupKey);
    }

    [Fact]
    public async Task Detector_SingleCommitPr_NotARework_NoFinding()
    {
        var prService = new Mock<IPullRequestService>(MockBehavior.Strict);
        var contentService = new Mock<IRepositoryContentService>(MockBehavior.Loose);

        prService.Setup(s => s.GetCommitsWithDatesAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformCommitInfo>
            {
                new() { Sha = LatestSha, Message = "Initial", CommittedAt = T0.UtcDateTime },
            });

        var detector = new ImageRegenAnomalyDetector(
            NullLogger<ImageRegenAnomalyDetector>.Instance,
            prService.Object,
            contentService.Object);

        var ctx = ContextWith(BuildPr(101, "Artist 1: Sprite sheet"));
        var findings = await detector.DetectAsync(ctx, default);

        Assert.Empty(findings);
        prService.Verify(s => s.GetFileDiffsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        contentService.Verify(s => s.GetFileBytesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Detector_NoPlatformServices_NoFindingNoThrow()
    {
        var detector = new ImageRegenAnomalyDetector(
            NullLogger<ImageRegenAnomalyDetector>.Instance,
            prService: null,
            contentService: null);

        var ctx = ContextWith();
        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    private static ImageRegenAnomalyDetector BuildDetector(
        out DetectorContext ctx,
        string path,
        byte[] previousBytes,
        byte[] latestBytes,
        string prTitle = "Artist 1: Sprite sheet for turret",
        int prNumber = 101)
    {
        var prService = new Mock<IPullRequestService>(MockBehavior.Loose);
        var contentService = new Mock<IRepositoryContentService>(MockBehavior.Loose);

        prService.Setup(s => s.GetCommitsWithDatesAsync(prNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformCommitInfo>
            {
                new() { Sha = PreviousSha, Message = "Initial gen",  CommittedAt = T0.UtcDateTime.AddMinutes(-5) },
                new() { Sha = LatestSha,   Message = "Rework per fb", CommittedAt = T0.UtcDateTime },
            });

        prService.Setup(s => s.GetFileDiffsAsync(prNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformFileDiff>
            {
                new() { FileName = path, Status = "modified", Additions = 1, Deletions = 1 },
            });

        contentService.Setup(s => s.GetFileBytesAsync(path, LatestSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestBytes);
        contentService.Setup(s => s.GetFileBytesAsync(path, PreviousSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousBytes);

        var detector = new ImageRegenAnomalyDetector(
            NullLogger<ImageRegenAnomalyDetector>.Instance,
            prService.Object,
            contentService.Object);

        ctx = ContextWith(BuildPr(prNumber, prTitle));
        return detector;
    }

    private static PullRequestView BuildPr(int number, string title) => new()
    {
        Number = number,
        Title = title,
        State = "open",
        HeadBranch = $"agent/artist-1/task-{number}",
        BaseBranch = "main",
        Labels = new[] { "in-progress" },
        AssignedAgent = "Artist 1",
        CreatedAt = T0.AddMinutes(-30),
        UpdatedAt = T0,
        MergeableState = "clean",
    };

    private static DetectorContext ContextWith(params PullRequestView[] prs) => new()
    {
        Now = T0,
        Agents = Array.Empty<AgentStateView>(),
        CurrentPhase = "ParallelDevelopment",
        WorkflowSignals = Array.Empty<string>(),
        EffectiveBranch = "main",
        Platform = new InMemoryPlatformView(prs),
    };

    private sealed class InMemoryPlatformView : IPlatformView
    {
        private readonly IReadOnlyList<PullRequestView> _prs;
        public InMemoryPlatformView(IReadOnlyList<PullRequestView> prs) { _prs = prs; }
        public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
            => Task.FromResult(_prs);
        public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemView>>(Array.Empty<WorkItemView>());
        public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewThreadView>>(Array.Empty<ReviewThreadView>());
        public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<CommitView?>(null);
    }

    private static byte[] SolidPng(int width, int height, Color color, int padBytes)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return InjectPadding(ms.ToArray(), padBytes, seed: 0);
    }

    private static byte[] MakePatternedPng(int width, int height, int padBytes, int seed = 0)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var redBrush = new SolidBrush(Color.Red);
            using var greenBrush = new SolidBrush(Color.Lime);
            using var blueBrush = new SolidBrush(Color.Blue);
            g.FillRectangle(redBrush, 0, 0, width / 2, height / 2);
            g.FillRectangle(greenBrush, width / 2, 0, width / 2, height / 2);
            g.FillRectangle(blueBrush, 0, height / 2, width / 2, height / 2);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return InjectPadding(ms.ToArray(), padBytes, seed);
    }

    private static byte[] InjectPadding(byte[] png, int padBytes, int seed)
    {
        if (padBytes <= 0) return png;
        const int iendChunkLen = 12;
        if (png.Length < iendChunkLen) return png;
        var insertAt = png.Length - iendChunkLen;

        var keyword = System.Text.Encoding.ASCII.GetBytes("Comment");
        var seedBytes = BitConverter.GetBytes(seed);
        var pad = new byte[padBytes];
        for (var i = 0; i < pad.Length; i++)
        {
            pad[i] = (byte)((i + seedBytes[i % 4]) & 0xFF);
        }
        var dataLen = keyword.Length + 1 + pad.Length;

        using var ms = new MemoryStream();
        ms.Write(png, 0, insertAt);
        ms.WriteByte((byte)((dataLen >> 24) & 0xFF));
        ms.WriteByte((byte)((dataLen >> 16) & 0xFF));
        ms.WriteByte((byte)((dataLen >> 8) & 0xFF));
        ms.WriteByte((byte)(dataLen & 0xFF));
        ms.Write(new byte[] { (byte)'t', (byte)'E', (byte)'X', (byte)'t' }, 0, 4);
        ms.Write(keyword, 0, keyword.Length);
        ms.WriteByte(0);
        ms.Write(pad, 0, pad.Length);
        ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
        ms.Write(png, insertAt, iendChunkLen);
        return ms.ToArray();
    }
}
