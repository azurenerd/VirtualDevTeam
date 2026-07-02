using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// 2026-05-11 Tier-2 batch — focused behavioral tests for the 11 new detectors.
/// Each detector gets a "fires when condition met" test and a "skips when carve-out applies"
/// test. Coverage favors the safety properties (no false positives, dedup keys) over
/// exhaustive branch coverage.
/// </summary>
public sealed class Tier2DetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------
    // T2.1 IdleAgentPhaseStuckDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IdleAgentPhaseStuckDetector_Fires_WhenArchitectIdleWhilePrAwaitsApproval()
    {
        var detector = new IdleAgentPhaseStuckDetector(
            NullLogger<IdleAgentPhaseStuckDetector>.Instance, TimeSpan.FromMinutes(5));

        var ctx = Context(
            agents: new[] { Idle("arch-1", "Architect 1", "Architect", T0.AddMinutes(-10)) },
            prs: new[] { Pr(42, new[] { "ready-for-review" }, T0.AddMinutes(-15)) });

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal("idle-agent-phase-stuck", f.DetectorId);
        Assert.Equal("pr#42", f.TargetResource);
    }

    [Fact]
    public async Task IdleAgentPhaseStuckDetector_Skips_WhenArchitectApprovalAlreadyApplied()
    {
        var detector = new IdleAgentPhaseStuckDetector(
            NullLogger<IdleAgentPhaseStuckDetector>.Instance, TimeSpan.FromMinutes(5));

        var ctx = Context(
            agents: new[] { Idle("arch-1", "Architect 1", "Architect", T0.AddMinutes(-10)) },
            prs: new[] { Pr(42, new[] { "ready-for-review", "architect-approved" }, T0.AddMinutes(-15)) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.2 TestEngineerFalseCompletionDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TeFalseCompletion_Fires_WhenTeIdleButApprovedPrLacksTests()
    {
        var detector = new TestEngineerFalseCompletionDetector(
            NullLogger<TestEngineerFalseCompletionDetector>.Instance, TimeSpan.FromMinutes(3));

        var ctx = Context(
            agents: new[] { Idle("te-1", "Test Engineer", "TestEngineer", T0.AddMinutes(-5)) },
            prs: new[] { Pr(42, new[] { "architect-approved" }, T0) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    [Fact]
    public async Task TeFalseCompletion_Skips_WhenTestsAddedLabelPresent()
    {
        var detector = new TestEngineerFalseCompletionDetector(
            NullLogger<TestEngineerFalseCompletionDetector>.Instance, TimeSpan.FromMinutes(3));

        var ctx = Context(
            agents: new[] { Idle("te-1", "Test Engineer", "TestEngineer", T0.AddMinutes(-5)) },
            prs: new[] { Pr(42, new[] { "architect-approved", "tests-added" }, T0) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.3 LabelTransitionTimeoutDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LabelTransitionTimeout_Fires_OnPhase1StallPastThreshold()
    {
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            phase1Threshold: TimeSpan.FromMinutes(15));

        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: new[] { Pr(42, new[] { "ready-for-review" }, T0.AddMinutes(-20)) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
        Assert.Equal("label-transition-timeout", findings[0].DetectorId);
    }

    [Fact]
    public async Task LabelTransitionTimeout_Skips_WhenAllApprovalsPresent()
    {
        // Final-merge phase is handled by UnmergedApprovedPrDetector, not this one.
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            phase1Threshold: TimeSpan.FromMinutes(15));

        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: new[] { Pr(42, new[] { "architect-approved", "pm-approved", "tests-added" }, T0.AddMinutes(-60)) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.4 ReworkSaturationDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReworkSaturation_Fires_WhenManyUnresolvedThreadsExist()
    {
        var detector = new ReworkSaturationDetector(
            NullLogger<ReworkSaturationDetector>.Instance, threadThreshold: 5);

        var threads = Enumerable.Range(1, 5).Select(i =>
            new ReviewThreadView { ThreadId = $"t{i}", FilePath = "x.cs", Line = i, Author = "PM", CreatedAt = T0 })
            .ToList();
        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: new[] { Pr(42, new[] { "ready-for-review" }, T0) },
            threadsByPr: new Dictionary<int, IReadOnlyList<ReviewThreadView>> { [42] = threads });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    // -----------------------------------------------------------------------
    // T2.7 HandoffGapDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HandoffGap_Fires_WhenEngineerIdleAndPrHasOpenThreads()
    {
        var detector = new HandoffGapDetector(
            NullLogger<HandoffGapDetector>.Instance, TimeSpan.FromMinutes(3));

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-10)) },
            prs: new[]
            {
                new PullRequestView
                {
                    Number = 42, Title = "feat: x", State = "open",
                    HeadBranch = "agent/software-engineer-1/x", BaseBranch = "main",
                    Labels = new List<string> { "ready-for-review" },
                    AssignedAgent = "Software Engineer 1",
                    CreatedAt = T0.AddHours(-1), UpdatedAt = T0.AddMinutes(-10),
                    MergeableState = "clean",
                }
            },
            threadsByPr: new Dictionary<int, IReadOnlyList<ReviewThreadView>>
            {
                [42] = new[] { new ReviewThreadView { ThreadId = "t1", FilePath = "a", Line = 1, Author = "PM", CreatedAt = T0 } }
            });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    // -----------------------------------------------------------------------
    // T2.8 PhaseAdvancementWatchdog
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PhaseAdvancementWatchdog_FiresCritical_WhenWorkClosedButPhaseStuck()
    {
        var detector = new PhaseAdvancementWatchdog(
            NullLogger<PhaseAdvancementWatchdog>.Instance);

        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: Array.Empty<PullRequestView>(),
            workItems: Array.Empty<WorkItemView>(),
            currentPhase: "ParallelDevelopment");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
        Assert.Equal(FlowFindingSeverity.Critical, findings[0].Severity);
    }

    [Fact]
    public async Task PhaseAdvancementWatchdog_Skips_WhenOpenEngTaskRemains()
    {
        var detector = new PhaseAdvancementWatchdog(
            NullLogger<PhaseAdvancementWatchdog>.Instance);

        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { Issue(99, new[] { "engineering-task" }) },
            currentPhase: "ParallelDevelopment");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.9 StatusReasonStagnationDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StatusReasonStagnation_Fires_AfterRepeatedSameReason()
    {
        var detector = new StatusReasonStagnationDetector(
            NullLogger<StatusReasonStagnationDetector>.Instance, stagnationTicks: 3);

        var ctx = Context(
            agents: new[] { Working("se-1", "SE 1", "SoftwareEngineer", T0.AddMinutes(-1), reason: "Working step A") },
            prs: Array.Empty<PullRequestView>());

        Assert.Empty(await detector.DetectAsync(ctx, default));
        Assert.Empty(await detector.DetectAsync(ctx, default));
        var third = await detector.DetectAsync(ctx, default);
        Assert.Single(third);
    }

    [Fact]
    public async Task StatusReasonStagnation_ResetsCounter_WhenReasonChanges()
    {
        var detector = new StatusReasonStagnationDetector(
            NullLogger<StatusReasonStagnationDetector>.Instance, stagnationTicks: 3);

        var agent1 = Working("se-1", "SE 1", "SoftwareEngineer", T0.AddMinutes(-1), reason: "A");
        var agent2 = agent1 with { StatusReason = "B" };

        await detector.DetectAsync(Context(agents: new[] { agent1 }, prs: Array.Empty<PullRequestView>()), default);
        await detector.DetectAsync(Context(agents: new[] { agent1 }, prs: Array.Empty<PullRequestView>()), default);
        // Reason changed — counter resets to 1.
        await detector.DetectAsync(Context(agents: new[] { agent2 }, prs: Array.Empty<PullRequestView>()), default);
        var findings = await detector.DetectAsync(Context(agents: new[] { agent2 }, prs: Array.Empty<PullRequestView>()), default);
        Assert.Empty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.10 OrphanPrDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task OrphanPr_Fires_WhenInProgressPrHasNoLiveOwner()
    {
        var detector = new OrphanPrDetector(
            NullLogger<OrphanPrDetector>.Instance, TimeSpan.FromMinutes(2));

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0) },
            prs: new[]
            {
                new PullRequestView
                {
                    Number = 42, Title = "Ghost PR", State = "open",
                    HeadBranch = "agent/software-engineer-2/x", BaseBranch = "main",
                    Labels = new List<string> { "in-progress" },
                    AssignedAgent = "Software Engineer 2", // not in live agents
                    CreatedAt = T0.AddMinutes(-10), UpdatedAt = T0.AddMinutes(-10),
                }
            });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    [Fact]
    public async Task OrphanPr_Skips_WhenAlreadyEscalated()
    {
        var detector = new OrphanPrDetector(NullLogger<OrphanPrDetector>.Instance);

        var ctx = Context(
            agents: Array.Empty<AgentStateView>(),
            prs: new[]
            {
                new PullRequestView
                {
                    Number = 42, Title = "Ghost PR", State = "open",
                    HeadBranch = "agent/x/y", BaseBranch = "main",
                    Labels = new List<string> { "in-progress", "agent-stuck" },
                    AssignedAgent = "X", CreatedAt = T0.AddHours(-1), UpdatedAt = T0.AddHours(-1),
                }
            });

        Assert.Empty(await detector.DetectAsync(ctx, default));
    }

    // -----------------------------------------------------------------------
    // T2.11 IdleIdleCycleDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IdleIdleCycle_Fires_AfterEnoughAlternationsInWindow()
    {
        var detector = new IdleIdleCycleDetector(
            NullLogger<IdleIdleCycleDetector>.Instance,
            window: TimeSpan.FromMinutes(5), transitionsThreshold: 6);

        for (int i = 0; i < 6; i++)
        {
            var status = (i % 2 == 0) ? "Working" : "Idle";
            var ctx = Context(
                agents: new[] { Agent("se-1", "SE 1", "SoftwareEngineer", status, T0.AddSeconds(i * 30)) },
                prs: Array.Empty<PullRequestView>(),
                now: T0.AddSeconds(i * 30));
            await detector.DetectAsync(ctx, default);
        }

        var final = Context(
            agents: new[] { Agent("se-1", "SE 1", "SoftwareEngineer", "Working", T0.AddSeconds(180)) },
            prs: Array.Empty<PullRequestView>(),
            now: T0.AddSeconds(200));
        var findings = await detector.DetectAsync(final, default);
        Assert.NotEmpty(findings);
    }

    // -----------------------------------------------------------------------
    // T2.18 EmptyQueueDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmptyQueue_Fires_WhenEngineerIdleWithClaimableTasks()
    {
        var detector = new EmptyQueueDetector(
            NullLogger<EmptyQueueDetector>.Instance, TimeSpan.FromMinutes(4));

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-10)) },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { Issue(99, new[] { "engineering-task" }) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    [Fact]
    public async Task EmptyQueue_Skips_WhenIssueAlreadyInProgress()
    {
        var detector = new EmptyQueueDetector(NullLogger<EmptyQueueDetector>.Instance);

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-10)) },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { Issue(99, new[] { "engineering-task", "status:in-progress" }) });

        Assert.Empty(await detector.DetectAsync(ctx, default));
    }

    [Fact]
    public async Task EmptyQueue_Skips_WhenPeerScoresHigherForTask()
    {
        // Regression for 2026-05-12 false positives: Game Engine Engineer 1 idle while
        // the Artist SME's art task #1501 is "claimable" — but the Artist has a strictly
        // higher capability-match score, so Game Engine Engineer's idleness is CORRECT
        // (it's deferring to the Artist via SpecialistEngineerAgent.RunAdditionalLoopWorkAsync).
        // The detector should mirror that deferral and skip the escalation.
        var detector = new EmptyQueueDetector(NullLogger<EmptyQueueDetector>.Instance, TimeSpan.FromMinutes(4));

        var idleGameEngineer = AgentWithCaps(
            "ge-1", "Game Engine Engineer 1", "SoftwareEngineer", "Idle", T0.AddMinutes(-10),
            new[] { "frontend", "phaser", "typescript", "gamedev", "pathfinding", "canvas", "webgl", "animation", "touch-input", "game-simulation" });
        var artistSme = AgentWithCaps(
            "artist-1", "Artist SME 1", "SoftwareEngineer", "Working", T0,
            new[] { "art", "sprites", "image-generation", "ui-design", "game-assets", "sprite-sheets", "animation-frames" });

        // Task title mentions "art assets" + "sprite" — Artist's keywords match 4-5x; Game Engine
        // Engineer's keywords match maybe "animation" once (lower).
        var artTask = Issue(1501, new[] { "engineering-task", "art" }, title: "[T10b] Generate core sprite art assets (REST-based)");

        var ctx = Context(
            agents: new[] { idleGameEngineer, artistSme },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { artTask });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);   // peer-deferral correctly suppressed the escalation
    }

    [Fact]
    public async Task EmptyQueue_Fires_WhenIdleAgentIsBestMatchForTask()
    {
        // Counter-test: when the idle agent IS the best match for a claimable task and
        // still isn't claiming, the escalation should fire. This is the true "stuck" state.
        var detector = new EmptyQueueDetector(NullLogger<EmptyQueueDetector>.Instance, TimeSpan.FromMinutes(4));

        // Use the real Artist SME's capability list (verified in production 2026-05-12)
        // so the keyword extraction rule produces realistic tokens.
        var idleArtist = AgentWithCaps(
            "artist-1", "Artist SME 1", "SoftwareEngineer", "Idle", T0.AddMinutes(-10),
            new[] { "art", "sprites", "image-generation", "ui-design", "game-assets", "sprite-sheets" });
        var gameEngineer = AgentWithCaps(
            "ge-1", "Game Engine Engineer 1", "SoftwareEngineer", "Working", T0,
            new[] { "frontend", "phaser" });

        // Title contains "sprite" + "assets" — Artist's split caps include both as tokens
        // (sprite from sprite-sheets, assets from game-assets). Game Engineer's keywords don't match.
        var artTask = Issue(1501, new[] { "engineering-task" }, title: "Generate core sprite art assets bundle");

        var ctx = Context(
            agents: new[] { idleArtist, gameEngineer },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { artTask });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);   // Artist is best match + idle → real stuck condition
    }

    [Fact]
    public async Task EmptyQueue_Fires_WhenGeneralistIdle_NoSpecialistMatch()
    {
        // Generalist engineer (no caps) should always escalate when idle with claimable tasks
        // — they're the universal last-resort, never deferring on score.
        var detector = new EmptyQueueDetector(NullLogger<EmptyQueueDetector>.Instance, TimeSpan.FromMinutes(4));

        var idleGeneralist = AgentWithCaps(
            "se-1", "Software Engineer 1", "SoftwareEngineer", "Idle", T0.AddMinutes(-10),
            Array.Empty<string>());

        var ctx = Context(
            agents: new[] { idleGeneralist },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { Issue(99, new[] { "engineering-task" }, title: "Refactor user service") });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    // -----------------------------------------------------------------------
    // PipelineStallDetector — stale status:blocked + all-idle stall
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PipelineStall_Fires_WhenTaskBlockedButNoOpenPRs()
    {
        var detector = new PipelineStallDetector(
            NullLogger<PipelineStallDetector>.Instance, TimeSpan.FromMinutes(5));

        var blockedTask = Issue(16, new[] { "engineering-task", "status:blocked" },
            title: "[T-16] Agent Workflow Engine and Tools");

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-15)) },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { blockedTask });

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Equal("pipeline-stall", f.DetectorId);
        Assert.Contains("stale status:blocked", f.Summary);
        Assert.Equal(FlowFindingSeverity.Critical, f.Severity);
        Assert.Equal($"remove-label:16:status:blocked", f.RecommendedFixId);
    }

    [Fact]
    public async Task PipelineStall_Skips_WhenBlockedTaskHasOpenPR()
    {
        var detector = new PipelineStallDetector(
            NullLogger<PipelineStallDetector>.Instance, TimeSpan.FromMinutes(5));

        var blockedTask = Issue(16, new[] { "engineering-task", "status:blocked" },
            title: "[T-16] Agent Workflow Engine and Tools");
        var activePr = Pr(5, new[] { "in-progress" }, T0, title: "SoftwareEngineer 4: Agent Workflow Engine and Tools");

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-15)) },
            prs: new[] { activePr },
            workItems: new[] { blockedTask });

        var findings = await detector.DetectAsync(ctx, default);
        // Should NOT fire stale-blocked because there's an active PR working on it
        Assert.DoesNotContain(findings, f => f.Summary.Contains("stale status:blocked"));
    }

    [Fact]
    public async Task PipelineStall_AllIdleStall_Fires_WhenAllEngineersIdleWithClaimableTasks()
    {
        var detector = new PipelineStallDetector(
            NullLogger<PipelineStallDetector>.Instance, TimeSpan.FromMinutes(5));

        var claimableTask = Issue(17, new[] { "engineering-task" },
            title: "[T-17] Review Trigger and Monitoring");

        var ctx = Context(
            agents: new[]
            {
                Idle("se-lead", "SoftwareEngineer", "SoftwareEngineer", T0.AddMinutes(-15)),
                Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-12)),
                Idle("se-2", "Software Engineer 2", "SoftwareEngineer", T0.AddMinutes(-10)),
            },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { claimableTask });

        var findings = await detector.DetectAsync(ctx, default);
        var f = Assert.Single(findings);
        Assert.Contains("Pipeline stall", f.Summary);
        Assert.Contains("all 3 engineers idle", f.Summary);
        Assert.Equal(FlowFindingSeverity.Critical, f.Severity);
    }

    [Fact]
    public async Task PipelineStall_Skips_DuringEarlyPhases()
    {
        var detector = new PipelineStallDetector(
            NullLogger<PipelineStallDetector>.Instance, TimeSpan.FromMinutes(5));

        var blockedTask = Issue(16, new[] { "engineering-task", "status:blocked" });

        var ctx = Context(
            agents: new[] { Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-15)) },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { blockedTask },
            currentPhase: "Architecture");

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task PipelineStall_Skips_WhenEngineersNotIdleLongEnough()
    {
        var detector = new PipelineStallDetector(
            NullLogger<PipelineStallDetector>.Instance, TimeSpan.FromMinutes(10));

        var claimableTask = Issue(17, new[] { "engineering-task" });

        var ctx = Context(
            agents: new[]
            {
                Idle("se-1", "Software Engineer 1", "SoftwareEngineer", T0.AddMinutes(-5)),
                Idle("se-2", "Software Engineer 2", "SoftwareEngineer", T0.AddMinutes(-3)),
            },
            prs: Array.Empty<PullRequestView>(),
            workItems: new[] { claimableTask });

        var findings = await detector.DetectAsync(ctx, default);
        // All-idle finding should not fire — engineers haven't been idle long enough
        Assert.DoesNotContain(findings, f => f.Summary.Contains("Pipeline stall"));
    }

    // -----------------------------------------------------------------------
    // T2.21 AiAnomalyDetector
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AiAnomaly_NoOp_WhenChatRunnerMissing()
    {
        var detector = new AiAnomalyDetector(
            NullLogger<AiAnomalyDetector>.Instance,
            chatRunner: null);

        var ctx = Context(
            agents: new[] { Idle("a", "A", "PM", T0.AddMinutes(-10)) },
            prs: Array.Empty<PullRequestView>());

        Assert.Empty(await detector.DetectAsync(ctx, default));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AgentStateView Idle(string id, string name, string role, DateTimeOffset since) =>
        Agent(id, name, role, "Idle", since);

    private static AgentStateView Working(string id, string name, string role, DateTimeOffset since, string? reason = null) =>
        new()
        {
            Id = id, DisplayName = name, Role = role, Status = "Working",
            StatusReason = reason, StatusChangedAt = since,
        };

    private static AgentStateView Agent(string id, string name, string role, string status, DateTimeOffset since) =>
        new()
        {
            Id = id, DisplayName = name, Role = role, Status = status,
            StatusChangedAt = since,
        };

    /// <summary>
    /// Agent view with a capability list — used by peer-deferral-aware detectors
    /// (EmptyQueueDetector). Capabilities default to empty; the agent is treated as a
    /// generalist when the list is empty.
    /// </summary>
    private static AgentStateView AgentWithCaps(string id, string name, string role, string status, DateTimeOffset since, IEnumerable<string> caps) =>
        new()
        {
            Id = id, DisplayName = name, Role = role, Status = status,
            StatusChangedAt = since,
            Capabilities = caps.ToList(),
        };

    private static PullRequestView Pr(int number, IEnumerable<string> labels, DateTimeOffset updatedAt, string? title = null) => new()
    {
        Number = number, Title = title ?? $"PR #{number}", State = "open",
        HeadBranch = $"agent/software-engineer-1/task-{number}",
        BaseBranch = "main",
        Labels = labels.ToList(),
        AssignedAgent = "Software Engineer 1",
        CreatedAt = updatedAt.AddHours(-1),
        UpdatedAt = updatedAt,
        MergeableState = "clean",
    };

    private static WorkItemView Issue(int number, IEnumerable<string> labels, string? title = null) => new()
    {
        Number = number, Title = title ?? $"Task #{number}", State = "open",
        Labels = labels.ToList(),
        AssignedAgent = null,
        CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddMinutes(-30),
    };

    private static DetectorContext Context(
        IReadOnlyList<AgentStateView> agents,
        IReadOnlyList<PullRequestView> prs,
        IReadOnlyList<WorkItemView>? workItems = null,
        IReadOnlyDictionary<int, IReadOnlyList<ReviewThreadView>>? threadsByPr = null,
        string currentPhase = "ParallelDevelopment",
        IReadOnlyList<string>? signals = null,
        DateTimeOffset? now = null) => new()
        {
            Now = now ?? T0,
            Agents = agents,
            CurrentPhase = currentPhase,
            WorkflowSignals = signals ?? Array.Empty<string>(),
            EffectiveBranch = "main",
            Platform = new TestPlatformView(prs, workItems ?? Array.Empty<WorkItemView>(),
                threadsByPr ?? new Dictionary<int, IReadOnlyList<ReviewThreadView>>()),
        };

    private sealed class TestPlatformView : IPlatformView
    {
        private readonly IReadOnlyList<PullRequestView> _prs;
        private readonly IReadOnlyList<WorkItemView> _workItems;
        private readonly IReadOnlyDictionary<int, IReadOnlyList<ReviewThreadView>> _threads;

        public TestPlatformView(IReadOnlyList<PullRequestView> prs, IReadOnlyList<WorkItemView> workItems,
            IReadOnlyDictionary<int, IReadOnlyList<ReviewThreadView>> threads)
        {
            _prs = prs;
            _workItems = workItems;
            _threads = threads;
        }

        public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
            => Task.FromResult(_prs);
        public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
            => Task.FromResult(_workItems);
        public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult(_threads.TryGetValue(prNumber, out var t) ? t : (IReadOnlyList<ReviewThreadView>)Array.Empty<ReviewThreadView>());
        public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<CommitView?>(null);
    }
}
