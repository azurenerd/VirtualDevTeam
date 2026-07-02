using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Strategies.Preview;

namespace VirtualDevTeam.Core.Tests.Strategies;

/// <summary>
/// Tests for <see cref="ImageAssetCandidatePreviewProducer"/> covering discovery rules
/// (which directories are scanned, which extensions are accepted, the 5 KB minimum
/// size filter), the single-image fast path, the contact-sheet rendering path, the
/// 16-image cap, and the rule that images outside the conventional asset directories
/// are ignored.
/// </summary>
/// <remarks>
/// Fixture images are generated with Magick.NET (the same library the producer uses)
/// — random-noise 128×128 PNGs comfortably clear the 5 KB threshold while staying
/// cheap to build. Each test uses a fresh temp directory and disposes it on cleanup.
/// </remarks>
public sealed class ImageAssetCandidatePreviewProducerTests : IDisposable
{
    private readonly string _root;

    public ImageAssetCandidatePreviewProducerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vdt-imageasset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup; CI shutdown does the rest.
        }
    }

    [Fact]
    public async Task TryProduceAsync_EmptyWorktree_ReturnsNull()
    {
        var worktree = NewWorktree("empty");
        var artifacts = NewArtifacts("empty-art");

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryProduceAsync_OnlySubFiveKbImages_ReturnsNull()
    {
        var worktree = NewWorktree("small-only");
        // Write a tiny 1-byte file with a valid PNG extension under assets/. It's far below 5 KB.
        var assets = Path.Combine(worktree, "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "stub.png"), new byte[] { 0x00 });
        File.WriteAllBytes(Path.Combine(assets, "stub2.jpg"), new byte[3072]); // 3 KB, still below threshold

        var artifacts = NewArtifacts("small-only-art");
        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryProduceAsync_OneValidImage_ReturnsImageAssetsPreviewWithSingleAsset()
    {
        var worktree = NewWorktree("single");
        var assets = Path.Combine(worktree, "assets");
        Directory.CreateDirectory(assets);
        WriteNoisePng(Path.Combine(assets, "hero.png"));

        var artifacts = NewArtifacts("single-art");
        var sut = NewSut();
        var ctx = NewContext(worktree, artifacts);

        var result = await sut.TryProduceAsync(ctx, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("image-assets", result!.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result.Source);
        Assert.NotEmpty(result.ScreenshotBase64);
        Assert.NotNull(result.IncludedAssetPaths);
        Assert.Single(result.IncludedAssetPaths!);
        Assert.True(File.Exists(Path.Combine(artifacts, $"framework-{ctx.TaskId}-{ctx.StrategyId}-assets.png")));
    }

    [Fact]
    public async Task TryProduceAsync_FourValidImages_BuildsContactSheetWithAllFour()
    {
        var worktree = NewWorktree("quad");
        var assets = Path.Combine(worktree, "assets");
        var sprites = Path.Combine(worktree, "sprites");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(sprites);
        WriteNoisePng(Path.Combine(assets, "a.png"));
        WriteNoisePng(Path.Combine(assets, "b.png"));
        WriteNoisePng(Path.Combine(sprites, "c.png"));
        WriteNoisePng(Path.Combine(sprites, "d.png"));

        var artifacts = NewArtifacts("quad-art");
        var sut = NewSut();
        var ctx = NewContext(worktree, artifacts);

        var result = await sut.TryProduceAsync(ctx, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(CandidatePreviewSource.ImageAssets, result!.Source);
        Assert.NotNull(result.IncludedAssetPaths);
        Assert.Equal(4, result.IncludedAssetPaths!.Count);
        var output = Path.Combine(artifacts, $"framework-{ctx.TaskId}-{ctx.StrategyId}-assets.png");
        Assert.True(File.Exists(output));
        // Sanity-check the output is a valid PNG (magic bytes 89 50 4E 47).
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public async Task TryProduceAsync_ThirtyValidImages_CapsAtSixteen()
    {
        var worktree = NewWorktree("many");
        var assets = Path.Combine(worktree, "assets");
        Directory.CreateDirectory(assets);
        for (int i = 0; i < 30; i++)
        {
            // Filenames are numerically-sortable so the sort order is deterministic.
            WriteNoisePng(Path.Combine(assets, $"img-{i:D3}.png"));
        }

        var artifacts = NewArtifacts("many-art");
        var sut = NewSut();
        var ctx = NewContext(worktree, artifacts);

        var result = await sut.TryProduceAsync(ctx, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.IncludedAssetPaths);
        Assert.Equal(16, result.IncludedAssetPaths!.Count);
        // All retained paths should be the first 16 in sort order (img-000..img-015).
        for (int i = 0; i < 16; i++)
        {
            Assert.EndsWith($"img-{i:D3}.png", result.IncludedAssetPaths[i]);
        }
    }

    [Fact]
    public async Task TryProduceAsync_ImageOutsideAssetDirectories_IsNotIncluded()
    {
        var worktree = NewWorktree("outside");
        // A valid PNG in src/MyCode/ should be ignored — not under any of the conventional roots.
        var srcMyCode = Path.Combine(worktree, "src", "MyCode");
        Directory.CreateDirectory(srcMyCode);
        WriteNoisePng(Path.Combine(srcMyCode, "some.png"));

        var artifacts = NewArtifacts("outside-art");
        var sut = NewSut();

        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        // No qualifying images in conventional roots — producer declines with null.
        Assert.Null(result);
    }

    [Fact]
    public async Task TryProduceAsync_FindsImagesUnderAgentDocsReferenceImages()
    {
        var worktree = NewWorktree("agentdocs");
        // AgentDocs/<anything>/reference-images/* is one of the supported roots.
        var refDir = Path.Combine(worktree, "AgentDocs", "researcher", "reference-images");
        Directory.CreateDirectory(refDir);
        WriteNoisePng(Path.Combine(refDir, "ref.png"));

        var artifacts = NewArtifacts("agentdocs-art");
        var sut = NewSut();

        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.IncludedAssetPaths);
        Assert.Single(result.IncludedAssetPaths!);
        Assert.EndsWith("ref.png", result.IncludedAssetPaths![0]);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ImageAssetCandidatePreviewProducer(null!));
    }

    [Fact]
    public void Identity_IsStable()
    {
        var sut = NewSut();
        Assert.Equal("image-assets", sut.Id);
        Assert.Equal(10, sut.Priority);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private ImageAssetCandidatePreviewProducer NewSut() =>
        new(NullLogger<ImageAssetCandidatePreviewProducer>.Instance);

    private string NewWorktree(string suffix)
    {
        var p = Path.Combine(_root, $"wt-{suffix}");
        Directory.CreateDirectory(p);
        return p;
    }

    private string NewArtifacts(string suffix)
    {
        var p = Path.Combine(_root, $"art-{suffix}");
        Directory.CreateDirectory(p);
        return p;
    }

    private static CandidatePreviewContext NewContext(string worktree, string artifacts) => new(
        RunId: "run-test",
        TaskId: "task-1",
        StrategyId: "baseline",
        CandidateWorktreePath: worktree,
        ArtifactOutputDir: artifacts,
        PrBranchName: null,
        PrTitle: null,
        PrBody: null);

    /// <summary>
    /// Writes a 128×128 random-noise PNG (well over the 5 KB producer threshold).
    /// Noise compresses poorly so the resulting PNG is reliably 30–50 KB.
    /// </summary>
    private static void WriteNoisePng(string path)
    {
        using var img = new MagickImage(MagickColors.Black, 128, 128);
        // Two noise passes make sure random PNG compression doesn't shrink the file under 5 KB.
        img.AddNoise(NoiseType.Uniform);
        img.AddNoise(NoiseType.Random);
        img.Write(path, MagickFormat.Png);
    }
}
