using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Core.Tests.HealthMonitor;

/// <summary>
/// Wave 3 tests: AgentStuckDetector per-reason multipliers and
/// LabelTransitionTimeoutDetector development-phase detection.
/// </summary>
public sealed class DetectorEnhancementTests
{
    // ─── AgentStuckDetector: GetActivityMultiplier ───

    [Theory]
    [InlineData("Evaluating Strategy candidates", 3.0)]
    [InlineData("Running Strategy evaluation", 3.0)]
    [InlineData("Framework evaluation in progress", 3.0)]
    [InlineData("self-assessment check", 2.0)]
    [InlineData("Rework: addressing review feedback", 2.0)]
    [InlineData("CLI edit mode rework", 2.0)]
    [InlineData("build fix attempt #2", 2.0)]
    [InlineData("Creating integration PR", 1.5)]
    [InlineData("integration PR merge", 1.5)]
    [InlineData("Implementing task #5", 1.0)]
    [InlineData("Reviewing PR #12", 1.0)]
    [InlineData("", 1.0)]
    public void GetActivityMultiplier_ReturnsExpectedValue(string reason, double expected)
    {
        Assert.Equal(expected, AgentStuckDetector.GetActivityMultiplier(reason));
    }

    [Fact]
    public void GetActivityMultiplier_NullReason_Returns1()
    {
        Assert.Equal(1.0, AgentStuckDetector.GetActivityMultiplier(null!));
    }

    [Fact]
    public async Task AgentStuck_StrategyAgent_UsesTripleThreshold()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var ctx = MakeContext(now, new AgentStateView
        {
            Id = "se-1", DisplayName = "SE 1", Role = "SoftwareEngineer",
            Status = "Working", StatusReason = "Strategy evaluation",
            StatusChangedAt = now - TimeSpan.FromMinutes(60), // 60min stuck
        });

        // 60min < 30*3 = 90min effective threshold → no finding
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task AgentStuck_StrategyAgent_ExceedsTripleThreshold_Fires()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var ctx = MakeContext(now, new AgentStateView
        {
            Id = "se-1", DisplayName = "SE 1", Role = "SoftwareEngineer",
            Status = "Working", StatusReason = "Strategy evaluation",
            StatusChangedAt = now - TimeSpan.FromMinutes(100), // 100min > 90min
        });

        var findings = await detector.DetectAsync(ctx, CancellationToken.None);
        Assert.Single(findings);
        Assert.Contains("agent-stuck:se-1", findings[0].DedupKey);
    }

    [Fact]
    public async Task AgentStuck_ReworkAgent_UsesDoubleThreshold()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var ctx = MakeContext(now, new AgentStateView
        {
            Id = "se-2", DisplayName = "SE 2", Role = "SoftwareEngineer",
            Status = "Working", StatusReason = "Rework: fixing review comments",
            StatusChangedAt = now - TimeSpan.FromMinutes(45), // 45min < 60min
        });

        // 45min < 30*2 = 60min → no finding
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task AgentStuck_DefaultActivity_UsesBaseThreshold()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var ctx = MakeContext(now, new AgentStateView
        {
            Id = "se-3", DisplayName = "SE 3", Role = "SoftwareEngineer",
            Status = "Working", StatusReason = "Implementing feature X",
            StatusChangedAt = now - TimeSpan.FromMinutes(35), // 35min > 30min base
        });

        var findings = await detector.DetectAsync(ctx, CancellationToken.None);
        Assert.Single(findings);
    }

    [Fact]
    public async Task AgentStuck_IdleAgent_NotFlagged()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var ctx = MakeContext(now, new AgentStateView
        {
            Id = "se-1", DisplayName = "SE 1", Role = "SoftwareEngineer",
            Status = "Idle", StatusReason = null,
            StatusChangedAt = now - TimeSpan.FromHours(5),
        });

        var findings = await detector.DetectAsync(ctx, CancellationToken.None);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task AgentStuck_SeverityScaling()
    {
        var detector = new AgentStuckDetector(
            TimeSpan.FromMinutes(30),
            NullLogger<AgentStuckDetector>.Instance);

        var now = DateTimeOffset.UtcNow;

        // Just over threshold → Warning
        var ctx1 = MakeContext(now, new AgentStateView
        {
            Id = "a1", DisplayName = "A1", Role = "SE",
            Status = "Working", StatusReason = "task",
            StatusChangedAt = now - TimeSpan.FromMinutes(35),
        });
        var f1 = await detector.DetectAsync(ctx1, CancellationToken.None);
        Assert.Single(f1);
        Assert.Equal(FlowFindingSeverity.Warning, f1[0].Severity);

        // Over 2x threshold → Critical
        var ctx2 = MakeContext(now, new AgentStateView
        {
            Id = "a2", DisplayName = "A2", Role = "SE",
            Status = "Working", StatusReason = "task",
            StatusChangedAt = now - TimeSpan.FromMinutes(65),
        });
        var f2 = await detector.DetectAsync(ctx2, CancellationToken.None);
        Assert.Single(f2);
        Assert.Equal(FlowFindingSeverity.Critical, f2[0].Severity);
    }

    // ─── LabelTransitionTimeoutDetector: Development Phase ───

    [Fact]
    public async Task LabelTimeout_InProgress_DetectsStuckDevelopment()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            phase1Threshold: TimeSpan.FromMinutes(15),
            laterPhaseThreshold: TimeSpan.FromMinutes(30));

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(1, "SE 1: Feature X", ["in-progress"],
            createdAt: now - TimeSpan.FromMinutes(90));

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Single(findings);
        Assert.Contains("in-progress", findings[0].Summary);
        Assert.Equal("label-transition-timeout:1:in-progress", findings[0].DedupKey);
    }

    [Fact]
    public async Task LabelTimeout_InProgress_UnderThreshold_NoFinding()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(1, "SE 1: Feature X", ["in-progress"],
            createdAt: now - TimeSpan.FromMinutes(30)); // under 60min default

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task LabelTimeout_InProgress_WithReadyForReview_SkipsDevPhase()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        // Has both in-progress and ready-for-review: should classify as Phase-1, not dev
        var pr = MakePr(2, "SE 2: Task Y", ["in-progress", "ready-for-review"],
            createdAt: now - TimeSpan.FromMinutes(90));

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        // Should fire for ready-for-review phase (15min threshold), not in-progress
        Assert.Single(findings);
        Assert.Contains("ready-for-review", findings[0].DedupKey);
    }

    [Fact]
    public async Task LabelTimeout_InProgress_CustomThreshold()
    {
        var flowConfig = Options.Create(new FlowMonitorConfig
        {
            DevelopmentPhaseThresholdMinutes = 120
        });
        var monitor = new OptionsMonitorWrapper<FlowMonitorConfig>(flowConfig.Value);

        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            flowConfig: monitor);

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(1, "SE: Task", ["in-progress"],
            createdAt: now - TimeSpan.FromMinutes(90)); // 90 < 120 custom threshold

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Empty(findings); // under custom threshold
    }

    [Fact]
    public async Task LabelTimeout_ReadyForReview_StillWorks()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            phase1Threshold: TimeSpan.FromMinutes(15));

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(3, "SE: Auth", ["ready-for-review"],
            createdAt: now - TimeSpan.FromMinutes(20));

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Single(findings);
        Assert.Contains("ready-for-review", findings[0].DedupKey);
    }

    [Fact]
    public async Task LabelTimeout_AgentStuckLabel_Skipped()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance);

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(4, "SE: Stuck", ["in-progress", "agent-stuck"],
            createdAt: now - TimeSpan.FromHours(5));

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Empty(findings); // skipped due to agent-stuck label
    }

    [Fact]
    public async Task LabelTimeout_ArchitectApproved_UsesLaterThreshold()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            laterPhaseThreshold: TimeSpan.FromMinutes(30));

        var now = DateTimeOffset.UtcNow;
        var pr = MakePr(5, "SE: API", ["ready-for-review", "architect-approved"],
            createdAt: now - TimeSpan.FromMinutes(45));

        var ctx = MakeContextWithPrs(now, [pr]);
        var findings = await detector.DetectAsync(ctx, CancellationToken.None);

        Assert.Single(findings);
        Assert.Contains("architect-approved", findings[0].DedupKey);
    }

    // ─── Helpers ───

    private static DetectorContext MakeContext(DateTimeOffset now, AgentStateView agent)
    {
        return new DetectorContext
        {
            Now = now,
            Agents = [agent],
            CurrentPhase = "ParallelDevelopment",
            WorkflowSignals = [],
            EffectiveBranch = "agent/test",
            Platform = NullPlatformView.Instance,
        };
    }

    private static DetectorContext MakeContextWithPrs(
        DateTimeOffset now, IReadOnlyList<PullRequestView> prs)
    {
        return new DetectorContext
        {
            Now = now,
            Agents = [],
            CurrentPhase = "ParallelDevelopment",
            WorkflowSignals = [],
            EffectiveBranch = "agent/test",
            Platform = new FakePlatformView(prs),
        };
    }

    private static PullRequestView MakePr(
        int number, string title, string[] labels,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null)
    {
        return new PullRequestView
        {
            Number = number,
            Title = title,
            State = "Open",
            HeadBranch = $"agent/se/{number}",
            BaseBranch = "main",
            Labels = labels,
            AssignedAgent = $"se-{number}",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
    }

    /// <summary>Simple fake that returns canned PRs.</summary>
    private sealed class FakePlatformView(IReadOnlyList<PullRequestView> prs) : IPlatformView
    {
        public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct)
            => Task.FromResult(prs);

        public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkItemView>>([]);

        public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReviewThreadView>>([]);

        public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct)
            => Task.FromResult<CommitView?>(null);
    }

    /// <summary>Minimal IOptionsMonitor wrapper for tests.</summary>
    private sealed class OptionsMonitorWrapper<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
