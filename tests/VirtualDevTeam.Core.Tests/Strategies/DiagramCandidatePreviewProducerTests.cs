using System.Diagnostics;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Strategies.Preview;
using Xunit.Abstractions;

namespace VirtualDevTeam.Core.Tests.Strategies;

/// <summary>
/// Tests for <see cref="DiagramCandidatePreviewProducer"/> covering empty-worktree
/// short-circuit, the four supported extensions (.mmd/.svg/.puml/.drawio), the mixed
/// diagrams-+-code filtering rule, and stable producer identity.
/// </summary>
/// <remarks>
/// Mermaid rendering needs Node/<c>npx</c> on PATH and downloads puppeteer-chromium on
/// first run. When Node is not detected, the mermaid-specific test bails out gracefully
/// with a clear <see cref="ITestOutputHelper"/> message rather than spuriously failing.
/// </remarks>
public sealed class DiagramCandidatePreviewProducerTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _output;

    public DiagramCandidatePreviewProducerTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "vdt-diagram-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task TryProduceAsync_SingleMermaidFile_ProducesDiagramsPreview()
    {
        var worktree = NewWorktree("mmd");
        File.WriteAllText(
            Path.Combine(worktree, "flow.mmd"),
            "graph TD\n    A[Start] --> B[End]\n");
        var artifacts = NewArtifacts("mmd-art");

        if (!await IsNodeOnPathAsync())
        {
            _output.WriteLine(
                "Skipping live mmdc verification: Node/npx not detected on PATH. " +
                "Producer should still emit a placeholder card, but we don't assert content shape here.");
            return;
        }

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("diagrams", result!.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.Diagrams, result.Source);
        Assert.NotEmpty(result.ScreenshotBase64);
        AssertScreenshotIsPng(result.ScreenshotBase64);
        Assert.NotNull(result.IncludedAssetPaths);
        Assert.Single(result.IncludedAssetPaths!);
        Assert.EndsWith("flow.mmd", result.IncludedAssetPaths![0]);
    }

    [Fact]
    public async Task TryProduceAsync_SingleSvgFile_ProducesDiagramsPreviewViaMagickNet()
    {
        var worktree = NewWorktree("svg");
        File.WriteAllText(
            Path.Combine(worktree, "logo.svg"),
            "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='200'>" +
            "<rect width='200' height='200' fill='#3498db'/>" +
            "<circle cx='100' cy='100' r='60' fill='#ecf0f1'/>" +
            "</svg>");
        var artifacts = NewArtifacts("svg-art");

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("diagrams", result!.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.Diagrams, result.Source);
        AssertScreenshotIsPng(result.ScreenshotBase64);
        Assert.NotNull(result.IncludedAssetPaths);
        Assert.Single(result.IncludedAssetPaths!);
        Assert.EndsWith("logo.svg", result.IncludedAssetPaths![0]);
    }

    [Fact]
    public async Task TryProduceAsync_SinglePumlFile_ProducesDiagramsPreviewWithPlaceholderCard()
    {
        var worktree = NewWorktree("puml");
        File.WriteAllText(
            Path.Combine(worktree, "sequence.puml"),
            "@startuml\nAlice -> Bob: hi\n@enduml\n");
        var artifacts = NewArtifacts("puml-art");

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("diagrams", result!.SourceProducerId);
        Assert.Equal(CandidatePreviewSource.Diagrams, result.Source);
        AssertScreenshotIsPng(result.ScreenshotBase64);
        Assert.NotNull(result.IncludedAssetPaths);
        // Placeholder still counts: producer emits a card for every detected diagram source.
        Assert.Single(result.IncludedAssetPaths!);
        Assert.EndsWith("sequence.puml", result.IncludedAssetPaths![0]);
    }

    [Fact]
    public async Task TryProduceAsync_MixedDiagramsAndCode_OnlyDiagramsAreIncluded()
    {
        var worktree = NewWorktree("mixed");
        // Diagram source — should be picked up.
        File.WriteAllText(
            Path.Combine(worktree, "design.svg"),
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>" +
            "<rect width='100' height='100' fill='#9b59b6'/></svg>");
        // Non-diagram source — must NOT be picked up.
        File.WriteAllText(Path.Combine(worktree, "code.ts"), "export const x = 1;\n");
        File.WriteAllText(Path.Combine(worktree, "Page.razor"), "<h1>Hi</h1>\n");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# Project\n");
        var artifacts = NewArtifacts("mixed-art");

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(worktree, artifacts), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(CandidatePreviewSource.Diagrams, result!.Source);
        Assert.NotNull(result.IncludedAssetPaths);
        // Exactly the .svg file — code/markdown files are not "diagrams".
        Assert.Single(result.IncludedAssetPaths!);
        Assert.EndsWith("design.svg", result.IncludedAssetPaths![0]);
        Assert.DoesNotContain(result.IncludedAssetPaths!,
            p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
              || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
              || p.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryProduceAsync_NonexistentWorktree_ReturnsNull()
    {
        var nonexistent = Path.Combine(_root, "does-not-exist");
        var artifacts = NewArtifacts("nx-art");

        var sut = NewSut();
        var result = await sut.TryProduceAsync(NewContext(nonexistent, artifacts), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Identity_IsStable()
    {
        var sut = NewSut();
        Assert.Equal("diagrams", sut.Id);
        // Priority is intentionally between image-assets (10) and Playwright (100).
        Assert.Equal(20, sut.Priority);
    }

    [Fact]
    public void FindDiagrams_ExcludesSvgUnderTestDataDir()
    {
        // Reproduces the reported bug: a user-provided DFD (.svg) copied verbatim into a
        // test-data folder must NOT be surfaced as a candidate-generated diagram.
        var worktree = NewWorktree("fixture-svg");
        var testData = Path.Combine(worktree, "tests", "MyProj.Tests", "TestData");
        Directory.CreateDirectory(testData);
        File.WriteAllText(Path.Combine(testData, "PrivacyDataFlow1.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        // A legitimately authored diagram at the app root SHOULD still be found.
        File.WriteAllText(Path.Combine(worktree, "architecture.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        var found = DiagramCandidatePreviewProducer.FindDiagrams(worktree);

        Assert.Contains(found, p => p.EndsWith("architecture.svg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, p => p.EndsWith("PrivacyDataFlow1.svg", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("testdata")]
    [InlineData("TestData")]
    [InlineData("test-data")]
    [InlineData("fixtures")]
    [InlineData("__fixtures__")]
    [InlineData("__snapshots__")]
    [InlineData("test-assets")]
    [InlineData("seed-data")]
    public void FindDiagrams_ExcludesAllFixtureDirVariants(string dirName)
    {
        var worktree = NewWorktree("fixture-" + dirName.Replace("_", "u"));
        var fixtureDir = Path.Combine(worktree, dirName);
        Directory.CreateDirectory(fixtureDir);
        File.WriteAllText(Path.Combine(fixtureDir, "input.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        var found = DiagramCandidatePreviewProducer.FindDiagrams(worktree);

        Assert.DoesNotContain(found, p => p.EndsWith("input.svg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DiagramCandidatePreviewProducer(null!));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DiagramCandidatePreviewProducer NewSut() =>
        new(NullLogger<DiagramCandidatePreviewProducer>.Instance);

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
    /// Decodes the base64 PNG payload and verifies the magic byte header (0x89 'P' 'N' 'G').
    /// Loud failure here means the producer emitted non-PNG bytes — a regression we want
    /// to catch fast.
    /// </summary>
    private static void AssertScreenshotIsPng(string screenshotBase64)
    {
        Assert.False(string.IsNullOrEmpty(screenshotBase64));
        var bytes = Convert.FromBase64String(screenshotBase64);
        Assert.True(bytes.Length > 8, "screenshot is shorter than the PNG header");
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    /// <summary>
    /// Quick probe for Node/npx — used to skip mermaid rendering tests gracefully when
    /// the test host doesn't have Node installed (CI without Node, fresh developer box,
    /// etc.). Returns false on any failure path; never throws.
    /// </summary>
    private static async Task<bool> IsNodeOnPathAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "node",
                Arguments = OperatingSystem.IsWindows() ? "/c node --version" : "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(timeout.Token);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
