using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// post-run3-merge-bottleneck — behavioral tests for the UnmergedApprovedPrDetector +
/// MergeApprovedPrAction pair. The pair is the safety-net merger for PRs that have
/// all required reviewer approvals but haven't been merged because the engineer agent
/// is busy on something else.
///
/// Tests cover:
///   - Detector skips PRs missing either required label.
///   - Detector skips PRs with merge conflicts (those are handled by another detector).
///   - Detector respects the stuck threshold (no false positives on freshly-approved PRs).
///   - Detector skips PRs already flagged for human attention.
///   - Action re-verifies labels at execution time (race protection).
///   - Action is a no-op when the PR was already merged by the original agent.
///   - Action enforces the inline-test-workflow tests-added gate.
///   - Action returns Skipped when platform services are missing.
/// </summary>
public sealed class MergeApprovedPrFlowTests
{
    // -----------------------------------------------------------------------
    // UnmergedApprovedPrDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Detector_Fires_WhenBothApprovalLabelsPresentAndStuckPastThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestView>
        {
            MakePr(42, labels: new[] { "architect-approved", "pm-approved", "tests-added" },
                updatedAt: now.AddMinutes(-10), mergeableState: "clean"),
        };

        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            TimeSpan.FromMinutes(5));

        var findings = await detector.DetectAsync(Context(now, prs), CancellationToken.None);

        var f = Assert.Single(findings);
        Assert.Equal("unmerged-approved-pr", f.DetectorId);
        Assert.Equal(FlowFindingSeverity.Warning, f.Severity);
        Assert.Equal("pr#42", f.TargetResource);
        Assert.Equal("unmerged-approved-pr:42", f.DedupKey);
    }

    [Fact]
    public async Task Detector_Skips_WhenOnlyOneApprovalPresent()
    {
        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestView>
        {
            MakePr(43, labels: new[] { "architect-approved" }, updatedAt: now.AddMinutes(-10), mergeableState: "clean"),
            MakePr(44, labels: new[] { "pm-approved" }, updatedAt: now.AddMinutes(-10), mergeableState: "clean"),
        };

        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            TimeSpan.FromMinutes(5));

        var findings = await detector.DetectAsync(Context(now, prs), CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_Skips_WhenStillWithinStuckThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestView>
        {
            MakePr(45, labels: new[] { "architect-approved", "pm-approved" },
                updatedAt: now.AddMinutes(-2), mergeableState: "clean"),
        };

        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            TimeSpan.FromMinutes(5));

        var findings = await detector.DetectAsync(Context(now, prs), CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_Skips_WhenMergeableStateIsDirty()
    {
        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestView>
        {
            MakePr(46, labels: new[] { "architect-approved", "pm-approved" },
                updatedAt: now.AddMinutes(-30), mergeableState: "dirty"),
        };

        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            TimeSpan.FromMinutes(5));

        var findings = await detector.DetectAsync(Context(now, prs), CancellationToken.None);

        // Dirty PRs are handled by StalePullRequestConflictDetector instead — no overlap.
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_Skips_WhenAgentStuckLabelAlreadyApplied()
    {
        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestView>
        {
            MakePr(47, labels: new[] { "architect-approved", "pm-approved", "agent-stuck" },
                updatedAt: now.AddMinutes(-30), mergeableState: "clean"),
        };

        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            TimeSpan.FromMinutes(5));

        var findings = await detector.DetectAsync(Context(now, prs), CancellationToken.None);

        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // MergeApprovedPrAction
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Action_Merges_WhenAllGatesPass()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "open",
                MergedAt = null,
                Labels = new List<string> { "architect-approved", "pm-approved", "tests-added" },
                HeadBranch = "agent/software-engineer-1/auth",
                MergeableState = "clean",
            });
        pr.Setup(p => p.MergeAsync(42, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance,
            pr.Object,
            branchService: null,
            config: null);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.Success, outcome.Result);
        Assert.Equal("pr#42", outcome.Target);
        pr.Verify(p => p.MergeAsync(42, It.Is<string>(s => s.Contains("FlowMonitor")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Action_IsNoOp_WhenPrAlreadyMerged()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "closed",
                MergedAt = DateTime.UtcNow.AddMinutes(-1),
                Labels = new List<string> { "architect-approved", "pm-approved", "tests-added" },
            });

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance, pr.Object);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.NoOp, outcome.Result);
        pr.Verify(p => p.MergeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Action_ReVerifiesLabels_SkipsWhenLabelRescinded()
    {
        var pr = new Mock<IPullRequestService>();
        // Detector saw both labels — by the time the action runs, the architect rescinded.
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "open",
                Labels = new List<string> { "pm-approved", "tests-added" },
                MergeableState = "clean",
            });

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance, pr.Object);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.Skipped, outcome.Result);
        pr.Verify(p => p.MergeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Action_RequiresTestsAdded_WhenInlineTestWorkflowActive()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "open",
                Labels = new List<string> { "architect-approved", "pm-approved" }, // no tests-added
                MergeableState = "clean",
            });

        var cfg = new VirtualDevTeamConfig();
        cfg.Workspace.TestWorkflow = "inline";
        var opts = Mock.Of<IOptionsMonitor<VirtualDevTeamConfig>>(o => o.CurrentValue == cfg);

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance, pr.Object,
            branchService: null, config: opts);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.Skipped, outcome.Result);
        Assert.Contains("tests-added", outcome.Detail ?? "");
        pr.Verify(p => p.MergeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Action_AllowsMerge_WithoutTestsAdded_WhenSeparatePrWorkflow()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "open",
                Labels = new List<string> { "architect-approved", "pm-approved" }, // no tests-added
                MergeableState = "clean",
                HeadBranch = "agent/se/x",
            });
        pr.Setup(p => p.MergeAsync(42, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cfg = new VirtualDevTeamConfig();
        cfg.Workspace.TestWorkflow = "separate-pr";
        var opts = Mock.Of<IOptionsMonitor<VirtualDevTeamConfig>>(o => o.CurrentValue == cfg);

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance, pr.Object,
            branchService: null, config: opts);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.Success, outcome.Result);
    }

    [Fact]
    public async Task Action_ReturnsSkipped_WhenPullRequestServiceNotBound()
    {
        var action = new MergeApprovedPrAction(NullLogger<MergeApprovedPrAction>.Instance);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.Skipped, outcome.Result);
        Assert.Contains("not bound", outcome.Detail ?? "");
    }

    [Fact]
    public async Task Action_HandlesRace_WhenAnotherWorkerMergedBetweenGatesAndCall()
    {
        var pr = new Mock<IPullRequestService>();
        // First GetAsync: PR looks ripe.
        pr.SetupSequence(p => p.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "open",
                Labels = new List<string> { "architect-approved", "pm-approved", "tests-added" },
                MergeableState = "clean",
            })
            // Second GetAsync (after NotMergeable race): PR is now merged.
            .ReturnsAsync(new PlatformPullRequest
            {
                Number = 42,
                State = "closed",
                MergedAt = DateTime.UtcNow,
                Labels = new List<string> { "architect-approved", "pm-approved", "tests-added" },
            });

        pr.Setup(p => p.MergeAsync(42, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PlatformConflictException(PlatformConflictKind.NotMergeable, "race"));

        var action = new MergeApprovedPrAction(
            NullLogger<MergeApprovedPrAction>.Instance, pr.Object);

        var outcome = await action.ExecuteAsync(Finding(42), CancellationToken.None);

        Assert.Equal(FlowActionResult.NoOp, outcome.Result);
        Assert.Contains("another worker", outcome.Detail ?? "");
    }

    [Fact]
    public async Task Action_CanHandle_OnlyAcceptsUnmergedApprovedPrFindings()
    {
        var action = new MergeApprovedPrAction(NullLogger<MergeApprovedPrAction>.Instance);

        Assert.True(action.CanHandle(Finding(42, detector: "unmerged-approved-pr")));
        Assert.False(action.CanHandle(Finding(42, detector: "agent-stuck")));
        Assert.False(action.CanHandle(Finding(42, detector: "pr-merge-conflict")));
        // Missing resource → can't handle even if detector id matches.
        Assert.False(action.CanHandle(new FlowFinding
        {
            Id = "x", DetectedAt = DateTimeOffset.UtcNow, DetectorId = "unmerged-approved-pr",
            Severity = FlowFindingSeverity.Warning, Summary = "x", Rationale = "x",
        }));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static FlowFinding Finding(int prNumber, string detector = "unmerged-approved-pr") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DetectedAt = DateTimeOffset.UtcNow,
        DetectorId = detector,
        Severity = FlowFindingSeverity.Warning,
        TargetResource = $"pr#{prNumber}",
        Summary = $"PR #{prNumber} stuck",
        Rationale = "rationale",
        DedupKey = $"{detector}:{prNumber}",
    };

    private static PullRequestView MakePr(int number, IEnumerable<string> labels,
        DateTimeOffset updatedAt, string? mergeableState = null) => new()
    {
        Number = number,
        Title = $"PR #{number}",
        State = "open",
        HeadBranch = $"agent/software-engineer-1/task-{number}",
        BaseBranch = "main",
        Labels = labels.ToList(),
        AssignedAgent = "Software Engineer 1",
        CreatedAt = updatedAt.AddDays(-1),
        UpdatedAt = updatedAt,
        MergeableState = mergeableState,
    };

    private static DetectorContext Context(DateTimeOffset now, IReadOnlyList<PullRequestView> prs) => new()
    {
        Now = now,
        Agents = Array.Empty<AgentStateView>(),
        CurrentPhase = "ParallelDevelopment",
        WorkflowSignals = Array.Empty<string>(),
        EffectiveBranch = "main",
        Platform = new TestPlatformView(prs),
    };

    private sealed class TestPlatformView : IPlatformView
    {
        private readonly IReadOnlyList<PullRequestView> _prs;
        public TestPlatformView(IReadOnlyList<PullRequestView> prs) => _prs = prs;

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
