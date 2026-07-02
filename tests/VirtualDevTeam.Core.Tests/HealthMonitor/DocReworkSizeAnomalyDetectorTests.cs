using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests.HealthMonitor;

/// <summary>
/// Tests for <see cref="DocReworkSizeAnomalyDetector"/>. Mirror the structure of
/// <c>ImageRegenAnomalyDetectorTests</c>: pure unit tests for the threshold logic and
/// classification helpers, plus end-to-end tests that wire in mock platform services
/// to drive the full detector loop.
/// </summary>
public sealed class DocReworkSizeAnomalyDetectorTests
{
    private const string PmSpecPath = "AgentDocs/MyProject/PMSpec.md";
    private const string ArchPath = "AgentDocs/MyProject/Architecture.md";
    private const string PreviousSha = "1111111111111111111111111111111111111111";
    private const string LatestSha = "2222222222222222222222222222222222222222";
    private static readonly DateTimeOffset T0 = new(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------
    // ClassifyDocPr — title heuristic
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ProgramManager: PM Specification for MyProject", "PMSpec.md")]
    [InlineData("ProgramManager: PM Spec for MyProject", "PMSpec.md")]
    [InlineData("programmanager: pm specification for myproject", "PMSpec.md")]
    [InlineData("Architect: Architecture design for 'MyProject'", "Architecture.md")]
    [InlineData("architect: architecture for myproject", "Architecture.md")]
    [InlineData("Software Engineer 1: Implement loot system", null)]
    [InlineData("ProgramManager: User Story for MyProject", null)]
    [InlineData("Architect: Some other doc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ClassifyDocPr_RecognizesDocPrPrefixes(string? title, string? expectedBasename)
    {
        Assert.Equal(expectedBasename, DocReworkSizeAnomalyDetector.ClassifyDocPr(title));
    }

    // ---------------------------------------------------------------------
    // MatchesDocBasename — file path heuristic
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("PMSpec.md", "PMSpec.md", true)]
    [InlineData("AgentDocs/MyProject/PMSpec.md", "PMSpec.md", true)]
    [InlineData("agentdocs\\myproject\\pmspec.md", "PMSpec.md", true)]
    [InlineData("AgentDocs/MyProject/OldPMSpec.md", "PMSpec.md", false)]
    [InlineData("AgentDocs/MyProject/PMSpec.md.bak", "PMSpec.md", false)]
    [InlineData("AgentDocs/MyProject/Architecture.md", "Architecture.md", true)]
    [InlineData("Architecture.md", "Architecture.md", true)]
    [InlineData("AgentDocs/MyProject/PMSpec.md", "Architecture.md", false)]
    [InlineData("", "PMSpec.md", false)]
    public void MatchesDocBasename_AcceptsExactBasenameOnly(string path, string basename, bool expected)
    {
        Assert.Equal(expected, DocReworkSizeAnomalyDetector.MatchesDocBasename(path, basename));
    }

    // ---------------------------------------------------------------------
    // AssessSizeDelta — pure threshold logic
    // ---------------------------------------------------------------------

    [Fact]
    public void AssessSizeDelta_BelowAllThresholds_ReturnsNull()
    {
        Assert.Null(DocReworkSizeAnomalyDetector.AssessSizeDelta(1000, 1100));
    }

    [Fact]
    public void AssessSizeDelta_AbsDeltaTooSmall_NoFinding()
    {
        // 100→200 = ratio 2.0× growth, |Δ|=100. Below CriticalAbsDelta(2000) and below WarningAbsDelta(500).
        Assert.Null(DocReworkSizeAnomalyDetector.AssessSizeDelta(100, 200));
    }

    [Fact]
    public void AssessSizeDelta_RatioTooSmall_NoFinding()
    {
        // 10000→10600 = ratio 1.06×, |Δ|=600. Above WarningAbsDelta but ratio below WarningRatio(1.3).
        Assert.Null(DocReworkSizeAnomalyDetector.AssessSizeDelta(10000, 10600));
    }

    [Fact]
    public void AssessSizeDelta_WarningGrowth_FlagsWarning()
    {
        // 10000→14000 = ratio 1.4× growth, |Δ|=4000. Above WarningRatio(1.3) and WarningAbsDelta(500),
        // but below CriticalRatio(2.0). → Warning.
        var a = DocReworkSizeAnomalyDetector.AssessSizeDelta(10000, 14000);
        Assert.NotNull(a);
        Assert.Equal(FlowFindingSeverity.Warning, a!.Value.Severity);
        Assert.Equal(4000, a.Value.AbsDelta);
    }

    [Fact]
    public void AssessSizeDelta_CriticalGrowth_StubToFullRewrite_FlagsCritical()
    {
        // The motivating real-world case: PMSpec.md grew from 64 → 31500 chars.
        var a = DocReworkSizeAnomalyDetector.AssessSizeDelta(64, 31500);
        Assert.NotNull(a);
        Assert.Equal(FlowFindingSeverity.Critical, a!.Value.Severity);
        Assert.True(a.Value.Ratio > 100.0, $"ratio should be ~492×, was {a.Value.Ratio}");
        Assert.Equal(31436, a.Value.AbsDelta);
    }

    [Fact]
    public void AssessSizeDelta_CriticalShrink_FullToStub_FlagsCritical()
    {
        // Symmetric: a full rewrite that shrinks the doc to a stub is equally pathological.
        var a = DocReworkSizeAnomalyDetector.AssessSizeDelta(31500, 64);
        Assert.NotNull(a);
        Assert.Equal(FlowFindingSeverity.Critical, a!.Value.Severity);
        Assert.True(a.Value.Ratio > 100.0, $"ratio should be ~492×, was {a.Value.Ratio}");
        Assert.Equal(31436, a.Value.AbsDelta);
    }

    [Fact]
    public void AssessSizeDelta_BoundaryRatio_NotFlagged()
    {
        // ratio exactly 1.3 should NOT flag (strict greater-than). |Δ|=600 above WarningAbsDelta.
        // 1000 → 1300 = ratio exactly 1.3 (NOT > 1.3), |Δ|=300 (also < 500). Either way, no finding.
        Assert.Null(DocReworkSizeAnomalyDetector.AssessSizeDelta(1000, 1300));
    }

    [Fact]
    public void AssessSizeDelta_ZeroOldSize_HandlesDivisionGracefully()
    {
        // 0 → 5000: should compute a finite ratio (treats old=0 as 1) and still flag Critical
        // because |Δ|=5000 > 2000 and effective ratio is 5000.
        var a = DocReworkSizeAnomalyDetector.AssessSizeDelta(0, 5000);
        Assert.NotNull(a);
        Assert.Equal(FlowFindingSeverity.Critical, a!.Value.Severity);
    }

    // ---------------------------------------------------------------------
    // Detector — end-to-end with mock platform services
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Detector_NoPlatformServices_NoFindingNoThrow()
    {
        var detector = new DocReworkSizeAnomalyDetector(
            NullLogger<DocReworkSizeAnomalyDetector>.Instance,
            prService: null,
            contentService: null);

        var ctx = ContextWith();
        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_NonDocPrTitle_Skipped()
    {
        var detector = BuildDetector(out var ctx,
            path: "src/some-engineering-file.cs",
            previousContent: "x", latestContent: "x".PadRight(50000),
            prTitle: "Software Engineer 1: Implement loot system");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_PmSpecPr_StubToFullRewrite_EmitsCriticalFinding()
    {
        var stub = "## TBD";
        var fullRewrite = new string('x', 31500);

        var detector = BuildDetector(out var ctx,
            path: PmSpecPath,
            previousContent: stub,
            latestContent: fullRewrite);

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal("doc-rework-size-anomaly", f.DetectorId);
        Assert.Equal(FlowFindingSeverity.Critical, f.Severity);
        Assert.Equal("pr#101", f.TargetResource);
        Assert.Equal($"doc-rework-size-anomaly:101:{PmSpecPath}", f.DedupKey);
        Assert.Contains("31500", f.Rationale);
        Assert.Contains(LatestSha, f.Rationale);
        Assert.Contains(PreviousSha, f.Rationale);
    }

    [Fact]
    public async Task Detector_ArchitecturePr_FullToStub_EmitsCriticalFinding()
    {
        var full = new string('x', 31500);
        var stub = "## TBD";

        var detector = BuildDetector(out var ctx,
            path: ArchPath,
            previousContent: full,
            latestContent: stub,
            prTitle: "Architect: Architecture design for 'MyProject'");

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal(FlowFindingSeverity.Critical, f.Severity);
        Assert.Equal($"doc-rework-size-anomaly:101:{ArchPath}", f.DedupKey);
    }

    [Fact]
    public async Task Detector_ModerateGrowth_EmitsWarningFinding()
    {
        // 5000 → 7000 chars: ratio 1.4× growth, |Δ|=2000. Above WarningRatio(1.3) and
        // WarningAbsDelta(500), but the ratio is below CriticalRatio(2.0), so it's a Warning.
        var oldDoc = new string('x', 5000);
        var newDoc = new string('x', 7000);

        var detector = BuildDetector(out var ctx,
            path: PmSpecPath,
            previousContent: oldDoc,
            latestContent: newDoc);

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal(FlowFindingSeverity.Warning, f.Severity);
    }

    [Fact]
    public async Task Detector_BelowAllThresholds_NoFinding()
    {
        // 5000 → 5300: ratio 1.06×, |Δ|=300. Both bounds insufficient.
        var detector = BuildDetector(out var ctx,
            path: PmSpecPath,
            previousContent: new string('x', 5000),
            latestContent: new string('x', 5300));

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_FileAddedInOnlyOneCommit_NoFinding()
    {
        // previousContent=null simulates "file did not exist at the previous commit".
        // Per spec: skip if only 1 commit touches the file (no rework).
        var prService = new Mock<IPullRequestService>(MockBehavior.Loose);
        var contentService = new Mock<IRepositoryContentService>(MockBehavior.Loose);

        prService.Setup(s => s.GetCommitsWithDatesAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformCommitInfo>
            {
                new() { Sha = PreviousSha, Message = "Initial", CommittedAt = T0.UtcDateTime.AddMinutes(-5) },
                new() { Sha = LatestSha,   Message = "Add doc", CommittedAt = T0.UtcDateTime },
            });
        prService.Setup(s => s.GetFileDiffsAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformFileDiff>
            {
                new() { FileName = PmSpecPath, Status = "added", Additions = 100 },
            });
        contentService.Setup(s => s.GetFileContentAsync(PmSpecPath, LatestSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new string('x', 31500));
        contentService.Setup(s => s.GetFileContentAsync(PmSpecPath, PreviousSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var detector = new DocReworkSizeAnomalyDetector(
            NullLogger<DocReworkSizeAnomalyDetector>.Instance,
            prService.Object,
            contentService.Object);

        var ctx = ContextWith(BuildPr(101, "ProgramManager: PM Specification for MyProject"));
        var findings = await detector.DetectAsync(ctx, default);

        Assert.Empty(findings);
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

        var detector = new DocReworkSizeAnomalyDetector(
            NullLogger<DocReworkSizeAnomalyDetector>.Instance,
            prService.Object,
            contentService.Object);

        var ctx = ContextWith(BuildPr(101, "ProgramManager: PM Specification for MyProject"));
        var findings = await detector.DetectAsync(ctx, default);

        Assert.Empty(findings);
        prService.Verify(s => s.GetFileDiffsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        contentService.Verify(s => s.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Detector_DocPrButOtherFilesChanged_OnlyDocFileFlagged()
    {
        // PR diff contains the PMSpec.md AND other files. Only PMSpec.md should be inspected.
        var oldDoc = "## TBD";
        var newDoc = new string('x', 31500);

        var prService = new Mock<IPullRequestService>(MockBehavior.Loose);
        var contentService = new Mock<IRepositoryContentService>(MockBehavior.Loose);

        prService.Setup(s => s.GetCommitsWithDatesAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformCommitInfo>
            {
                new() { Sha = PreviousSha, Message = "Initial PMSpec",   CommittedAt = T0.UtcDateTime.AddMinutes(-5) },
                new() { Sha = LatestSha,   Message = "Rework per feedback", CommittedAt = T0.UtcDateTime },
            });
        prService.Setup(s => s.GetFileDiffsAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformFileDiff>
            {
                new() { FileName = PmSpecPath,                            Status = "modified" },
                new() { FileName = "AgentDocs/MyProject/style-anchor.png", Status = "added" },
                new() { FileName = "AgentDocs/MyProject/notes.md",         Status = "modified" },
            });
        contentService.Setup(s => s.GetFileContentAsync(PmSpecPath, LatestSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newDoc);
        contentService.Setup(s => s.GetFileContentAsync(PmSpecPath, PreviousSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldDoc);

        var detector = new DocReworkSizeAnomalyDetector(
            NullLogger<DocReworkSizeAnomalyDetector>.Instance,
            prService.Object,
            contentService.Object);

        var ctx = ContextWith(BuildPr(101, "ProgramManager: PM Specification for MyProject"));
        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal(PmSpecPath, ExtractPathFromDedupKey(f.DedupKey!));
        // Confirm the other files weren't fetched.
        contentService.Verify(s => s.GetFileContentAsync("AgentDocs/MyProject/notes.md", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Detector_DedupKey_IsStableAcrossTicks()
    {
        var oldDoc = "## TBD";
        var newDoc = new string('x', 31500);

        var detector = BuildDetector(out var ctx,
            path: PmSpecPath,
            previousContent: oldDoc,
            latestContent: newDoc);

        var first = await detector.DetectAsync(ctx, default);
        var second = await detector.DetectAsync(ctx, default);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].DedupKey, second[0].DedupKey);
    }

    [Fact]
    public async Task Detector_RepoRootDoc_AlsoMatched()
    {
        // PMSpec.md may be at the repo root (legacy layout) instead of under AgentDocs/.
        var oldDoc = "## TBD";
        var newDoc = new string('x', 31500);

        var detector = BuildDetector(out var ctx,
            path: "PMSpec.md",
            previousContent: oldDoc,
            latestContent: newDoc);

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal("doc-rework-size-anomaly:101:PMSpec.md", f.DedupKey);
    }

    // ---------------------------------------------------------------------
    // Test helpers
    // ---------------------------------------------------------------------

    private static DocReworkSizeAnomalyDetector BuildDetector(
        out DetectorContext ctx,
        string path,
        string previousContent,
        string latestContent,
        string prTitle = "ProgramManager: PM Specification for MyProject",
        int prNumber = 101)
    {
        var prService = new Mock<IPullRequestService>(MockBehavior.Loose);
        var contentService = new Mock<IRepositoryContentService>(MockBehavior.Loose);

        prService.Setup(s => s.GetCommitsWithDatesAsync(prNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformCommitInfo>
            {
                new() { Sha = PreviousSha, Message = "Initial",            CommittedAt = T0.UtcDateTime.AddMinutes(-5) },
                new() { Sha = LatestSha,   Message = "Rework per feedback", CommittedAt = T0.UtcDateTime },
            });

        prService.Setup(s => s.GetFileDiffsAsync(prNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformFileDiff>
            {
                new() { FileName = path, Status = "modified", Additions = 1, Deletions = 1 },
            });

        contentService.Setup(s => s.GetFileContentAsync(path, LatestSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestContent);
        contentService.Setup(s => s.GetFileContentAsync(path, PreviousSha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousContent);

        var detector = new DocReworkSizeAnomalyDetector(
            NullLogger<DocReworkSizeAnomalyDetector>.Instance,
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
        HeadBranch = $"agent/program-manager/doc-{number}",
        BaseBranch = "main",
        Labels = new[] { "in-progress" },
        AssignedAgent = title.StartsWith("Architect", StringComparison.OrdinalIgnoreCase) ? "Architect" : "Program Manager",
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

    private static string ExtractPathFromDedupKey(string dedupKey)
    {
        // dedup format: "doc-rework-size-anomaly:{prNumber}:{path}"
        var parts = dedupKey.Split(':', 3);
        return parts.Length == 3 ? parts[2] : dedupKey;
    }

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
}
