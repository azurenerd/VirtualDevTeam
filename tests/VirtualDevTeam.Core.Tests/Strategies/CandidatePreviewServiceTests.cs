using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Strategies.Preview;

namespace VirtualDevTeam.Core.Tests.Strategies;

/// <summary>
/// Unit tests for <see cref="CandidatePreviewService"/> — the chain orchestrator that
/// picks the first successful producer (lowest <see cref="ICandidatePreviewProducer.Priority"/>
/// first) and falls back to a <see cref="CandidatePreviewSource.NoVisualContent"/>
/// placeholder when every producer declines. Also covers mixed-content layering: when
/// the winner is ImageAssets AND the worktree has a runnable app, the chain runs the
/// Playwright producer too and swaps so primary=Playwright, secondary=ImageAssets.
/// </summary>
public class CandidatePreviewServiceTests : IDisposable
{
    private readonly string _scratchRoot;

    public CandidatePreviewServiceTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(), "vdt-cps-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private CandidatePreviewContext NewContext(string? worktree = null) => new(
        RunId: "run-1",
        TaskId: "task-1",
        StrategyId: "baseline",
        CandidateWorktreePath: worktree ?? _scratchRoot,
        ArtifactOutputDir: Path.Combine(_scratchRoot, "artifacts"),
        PrBranchName: null,
        PrTitle: null,
        PrBody: null);

    [Fact]
    public async Task ProduceAsync_HighPriorityProducerReturnsResult_LowerPriorityProducersNotCalled()
    {
        var winner = new RecordingProducer(id: "image", priority: 1, result: NewPreview("image", CandidatePreviewSource.ImageAssets));
        var loser = new RecordingProducer(id: "playwright", priority: 100, result: NewPreview("playwright", CandidatePreviewSource.PlaywrightScreenshot));
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { loser, winner }, // intentionally reversed registration order
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(), CancellationToken.None);

        // No launchSettings.json under the empty scratch worktree → no mixed-content
        // layering, the image winner stays primary and the playwright producer is never
        // called.
        Assert.Equal("image", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.Source);
        Assert.Null(result.SecondaryPreview);
        Assert.Equal(1, winner.CallCount);
        Assert.Equal(0, loser.CallCount);
    }

    [Fact]
    public async Task ProduceAsync_AllProducersReturnNull_ReturnsNoVisualContentPlaceholderWithOnePixelPng()
    {
        var p1 = new RecordingProducer(id: "image", priority: 1, result: null);
        var p2 = new RecordingProducer(id: "diagram", priority: 50, result: null);
        var p3 = new RecordingProducer(id: "playwright", priority: 100, result: null);
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { p1, p2, p3 },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(), CancellationToken.None);

        Assert.Equal("none", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.NoVisualContent, result.Source);
        Assert.Null(result.SecondaryPreview);
        var bytes = Convert.FromBase64String(result.ScreenshotBase64);
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
        Assert.Equal(1, p1.CallCount);
        Assert.Equal(1, p2.CallCount);
        Assert.Equal(1, p3.CallCount);
    }

    [Fact]
    public async Task ProduceAsync_ProducerThrows_NextProducerInPriorityOrderIsStillTried()
    {
        var throwing = new ThrowingProducer(id: "image", priority: 1, ex: new InvalidOperationException("simulated"));
        var recovery = new RecordingProducer(id: "diagram", priority: 50, result: NewPreview("diagram", CandidatePreviewSource.Diagrams));
        var lastChance = new RecordingProducer(id: "playwright", priority: 100, result: NewPreview("playwright", CandidatePreviewSource.PlaywrightScreenshot));
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { throwing, recovery, lastChance },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(), CancellationToken.None);

        Assert.Equal("diagram", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.Diagrams, result.Source);
        Assert.Equal(1, throwing.CallCount);
        Assert.Equal(1, recovery.CallCount);
        Assert.Equal(0, lastChance.CallCount);
    }

    [Fact]
    public async Task ProduceAsync_AllProducersThrow_FallsBackToNoVisualContent()
    {
        var p1 = new ThrowingProducer(id: "image", priority: 1, ex: new InvalidOperationException("a"));
        var p2 = new ThrowingProducer(id: "playwright", priority: 100, ex: new InvalidOperationException("b"));
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { p1, p2 },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(), CancellationToken.None);

        Assert.Equal("none", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.NoVisualContent, result.Source);
    }

    [Fact]
    public async Task ProduceAsync_RespectsCancellation_DoesNotSwallowOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { new CancelAwareProducer() },
            NullLogger<CandidatePreviewService>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ProduceAsync(NewContext(), cts.Token));
    }

    [Fact]
    public void Constructor_NullProducers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CandidatePreviewService(null!, NullLogger<CandidatePreviewService>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CandidatePreviewService(Array.Empty<ICandidatePreviewProducer>(), null!));
    }

    // ── Mixed-content (mixed-content-pr-handling) ────────────────────────────

    [Fact]
    public async Task ProduceAsync_MixedContent_PrimaryBecomesPlaywright_SecondaryBecomesImageAssets()
    {
        // Arrange — a worktree with launchSettings.json present (the cheap mixed-content
        // proxy used by CandidatePreviewService) AND both producers returning a result.
        var worktree = Path.Combine(_scratchRoot, "mixed");
        var propsDir = Path.Combine(worktree, "src", "MyApp", "Properties");
        Directory.CreateDirectory(propsDir);
        await File.WriteAllTextAsync(Path.Combine(propsDir, "launchSettings.json"), "{}", CancellationToken.None);

        var imagePreview = NewPreview("image-assets", CandidatePreviewSource.ImageAssets) with
        {
            IncludedAssetPaths = new[] { @"assets\sprite-1.png", @"assets\sprite-2.png" },
        };
        var playwrightPreview = NewPreview("playwright", CandidatePreviewSource.PlaywrightScreenshot);

        var imageProducer = new RecordingProducer(id: "image-assets", priority: 10, result: imagePreview);
        var playwrightProducer = new RecordingProducer(id: "playwright", priority: 100, result: playwrightPreview);
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { imageProducer, playwrightProducer },
            NullLogger<CandidatePreviewService>.Instance);

        // Act
        var result = await sut.ProduceAsync(NewContext(worktree), CancellationToken.None);

        // Assert — Playwright bubbled up as PRIMARY, image producer's result attached as SECONDARY.
        Assert.Equal("playwright", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.PlaywrightScreenshot, result.Source);
        Assert.NotNull(result.SecondaryPreview);
        Assert.Equal("image-assets", result.SecondaryPreview!.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.SecondaryPreview.Source);
        Assert.NotNull(result.SecondaryPreview.IncludedAssetPaths);
        Assert.Equal(2, result.SecondaryPreview.IncludedAssetPaths!.Count);

        // Both producers should have been called: image (chain winner), then playwright
        // (mixed-content second pass).
        Assert.Equal(1, imageProducer.CallCount);
        Assert.Equal(1, playwrightProducer.CallCount);
    }

    [Fact]
    public async Task ProduceAsync_AssetsButNoLaunchSettings_NoSecondaryPreviewAttached()
    {
        // Arrange — worktree has NO launchSettings.json, so the mixed-content path is
        // skipped entirely; image producer wins and Playwright is never consulted.
        var worktree = Path.Combine(_scratchRoot, "assets-only");
        Directory.CreateDirectory(worktree);

        var imageProducer = new RecordingProducer(
            id: "image-assets", priority: 10,
            result: NewPreview("image-assets", CandidatePreviewSource.ImageAssets));
        var playwrightProducer = new RecordingProducer(
            id: "playwright", priority: 100,
            result: NewPreview("playwright", CandidatePreviewSource.PlaywrightScreenshot));
        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { imageProducer, playwrightProducer },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(worktree), CancellationToken.None);

        Assert.Equal("image-assets", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.Source);
        Assert.Null(result.SecondaryPreview);
        Assert.Equal(1, imageProducer.CallCount);
        Assert.Equal(0, playwrightProducer.CallCount);
    }

    [Fact]
    public async Task ProduceAsync_MixedContentLaunchSettings_ButPlaywrightDeclines_PrimaryStaysImageAssets()
    {
        // Arrange — launchSettings present, but the Playwright producer declines (e.g. browser
        // not installed). Primary stays as the image winner; no secondary attached.
        var worktree = Path.Combine(_scratchRoot, "mixed-no-pw");
        var propsDir = Path.Combine(worktree, "Properties");
        Directory.CreateDirectory(propsDir);
        await File.WriteAllTextAsync(Path.Combine(propsDir, "launchSettings.json"), "{}", CancellationToken.None);

        var imageProducer = new RecordingProducer(
            id: "image-assets", priority: 10,
            result: NewPreview("image-assets", CandidatePreviewSource.ImageAssets));
        var playwrightProducer = new RecordingProducer(
            id: "playwright", priority: 100,
            result: null); // declines

        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { imageProducer, playwrightProducer },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(worktree), CancellationToken.None);

        Assert.Equal("image-assets", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.Source);
        Assert.Null(result.SecondaryPreview);
        Assert.Equal(1, imageProducer.CallCount);
        Assert.Equal(1, playwrightProducer.CallCount); // attempted, but declined
    }

    [Fact]
    public async Task ProduceAsync_MixedContent_PlaywrightThrows_PrimaryStaysImageAssets()
    {
        var worktree = Path.Combine(_scratchRoot, "mixed-pw-throws");
        var propsDir = Path.Combine(worktree, "Properties");
        Directory.CreateDirectory(propsDir);
        await File.WriteAllTextAsync(Path.Combine(propsDir, "launchSettings.json"), "{}", CancellationToken.None);

        var imageProducer = new RecordingProducer(
            id: "image-assets", priority: 10,
            result: NewPreview("image-assets", CandidatePreviewSource.ImageAssets));
        var playwrightProducer = new ThrowingProducer(
            id: "playwright", priority: 100,
            ex: new InvalidOperationException("simulated browser failure"));

        var sut = new CandidatePreviewService(
            new ICandidatePreviewProducer[] { imageProducer, playwrightProducer },
            NullLogger<CandidatePreviewService>.Instance);

        var result = await sut.ProduceAsync(NewContext(worktree), CancellationToken.None);

        // The Playwright failure during the mixed-content second pass must not lose the
        // primary image preview — it just means no secondary is attached.
        Assert.Equal("image-assets", result.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.Source);
        Assert.Null(result.SecondaryPreview);
        Assert.Equal(1, imageProducer.CallCount);
        Assert.Equal(1, playwrightProducer.CallCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CandidatePreview NewPreview(string id, CandidatePreviewSource source) => new()
    {
        SourceProducerId = id,
        ScreenshotBase64 = "iVBORw0KGgo=", // arbitrary non-empty base64
        Source = source,
    };

    private sealed class RecordingProducer : ICandidatePreviewProducer
    {
        private readonly CandidatePreview? _result;

        public RecordingProducer(string id, int priority, CandidatePreview? result)
        {
            Id = id;
            Priority = priority;
            _result = result;
        }

        public int Priority { get; }
        public string Id { get; }
        public int CallCount { get; private set; }

        public Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingProducer : ICandidatePreviewProducer
    {
        private readonly Exception _ex;

        public ThrowingProducer(string id, int priority, Exception ex)
        {
            Id = id;
            Priority = priority;
            _ex = ex;
        }

        public int Priority { get; }
        public string Id { get; }
        public int CallCount { get; private set; }

        public Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
        {
            CallCount++;
            throw _ex;
        }
    }

    private sealed class CancelAwareProducer : ICandidatePreviewProducer
    {
        public int Priority => 1;
        public string Id => "cancel-aware";

        public Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<CandidatePreview?>(null);
        }
    }
}
