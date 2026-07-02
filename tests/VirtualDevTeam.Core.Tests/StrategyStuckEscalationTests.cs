using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for the Strategy Stuck Escalation feature covering:
///   - SelectEmergencyWinner (CandidateEvaluator) — criteria ordering &amp; edge cases
///   - StrategyEvaluationStuckDetector — 3 detection conditions
///   - PromoteStrategyWinnerAction — cancellation routing
///   - MergeEscalationAction — notification routing
///   - UnmergedApprovedPrDetector Tier 2 — partial-approval stall
/// </summary>
public sealed class StrategyStuckEscalationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);

    // =======================================================================
    // SelectEmergencyWinner — CandidateEvaluator
    // =======================================================================

    [Fact]
    public void SelectEmergencyWinner_PicksHighestScoredCandidate()
    {
        var evaluator = CreateEvaluator();
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("cli", survived: true, succeeded: true, patch: "diff1",
                patchSize: 100, elapsed: TimeSpan.FromMinutes(5),
                ac: 8, design: 7, readability: 6),
            MakeCandidate("squad", survived: true, succeeded: true, patch: "diff2",
                patchSize: 200, elapsed: TimeSpan.FromMinutes(8),
                ac: 5, design: 5, readability: 5),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.NotNull(result);
        Assert.Equal("cli", result.Winner!.StrategyId);
        Assert.Contains("EMERGENCY", result.TieBreakReason!);
    }

    [Fact]
    public void SelectEmergencyWinner_FallsBackToVisualScoreWhenNoJudgeScores()
    {
        var evaluator = CreateEvaluator();
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("cli", survived: true, succeeded: true, patch: "diff1",
                patchSize: 100, elapsed: TimeSpan.FromMinutes(5),
                visualsScore: 8),
            MakeCandidate("squad", survived: true, succeeded: true, patch: "diff2",
                patchSize: 200, elapsed: TimeSpan.FromMinutes(8),
                visualsScore: 3),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.NotNull(result);
        Assert.Equal("cli", result.Winner!.StrategyId);
    }

    [Fact]
    public void SelectEmergencyWinner_FallsBackToSmallerPatchWhenNoScores()
    {
        var evaluator = CreateEvaluator();
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("cli", survived: true, succeeded: true, patch: "big-diff",
                patchSize: 5000, elapsed: TimeSpan.FromMinutes(5)),
            MakeCandidate("squad", survived: true, succeeded: true, patch: "small-diff",
                patchSize: 200, elapsed: TimeSpan.FromMinutes(5)),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.NotNull(result);
        Assert.Equal("squad", result.Winner!.StrategyId);
    }

    [Fact]
    public void SelectEmergencyWinner_ReturnsNullWhenNoCandidatesQualify()
    {
        var evaluator = CreateEvaluator();
        var candidates = new List<CandidateResult>
        {
            // Not survived AND build failed — neither condition met
            MakeCandidate("cli", survived: false, succeeded: false, patch: "diff"),
            // Empty patch
            MakeCandidate("squad", survived: true, succeeded: true, patch: ""),
            // Build failed AND not survived
            MakeCandidate("local", survived: false, succeeded: false, patch: "diff"),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.Null(result);
    }

    [Fact]
    public void SelectEmergencyWinner_QualifiesPreEvaluationCandidateByExecSucceeded()
    {
        // Pre-evaluation candidates have Survived=false but Execution.Succeeded=true
        var evaluator = CreateEvaluator();
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("squad", survived: false, succeeded: true, patch: "diff-squad", patchSize: 200),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.NotNull(result);
        Assert.Equal("squad", result!.Winner!.StrategyId);
    }

    [Fact]
    public void SelectEmergencyWinner_ReturnsNullWhenDisabledByConfig()
    {
        var evaluator = CreateEvaluator(emergencyEnabled: false);
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("cli", survived: true, succeeded: true, patch: "diff",
                patchSize: 100, elapsed: TimeSpan.FromMinutes(5), ac: 8, design: 7, readability: 6),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.Null(result);
    }

    // =======================================================================
    // StrategyEvaluationStuckDetector — 3 conditions
    // =======================================================================

    [Fact]
    public async Task Detector_ScoringStuck_Fires_WhenAllCandidatesCompletedAndNoScoringProgress()
    {
        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task1",
            ("cli", CandidateState.Completed, T0.AddMinutes(-20), null),
            ("squad", CandidateState.Completed, T0.AddMinutes(-18), null));
        InjectActiveTask(stateStore, task);

        // Set mediaCaptureTimeoutMinutes high to isolate the scoring-stuck condition
        var detector = CreateDetector(stateStore, judgeScoringTimeoutMinutes: 15, mediaCaptureTimeoutMinutes: 30);
        var ctx = DetectorCtx(now: T0);

        var findings = await detector.DetectAsync(ctx, default);

        var f = Assert.Single(findings);
        Assert.Equal("strategy-evaluation-stuck", f.DetectorId);
        Assert.Equal("strategy-stuck:task1:scoring-stuck", f.DedupKey);
        Assert.Equal(FlowFindingSeverity.Critical, f.Severity);
    }

    [Fact]
    public async Task Detector_ScoringStuck_Skips_WhenWithinThreshold()
    {
        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task1",
            ("cli", CandidateState.Completed, T0.AddMinutes(-5), null),
            ("squad", CandidateState.Completed, T0.AddMinutes(-3), null));
        InjectActiveTask(stateStore, task);

        var detector = CreateDetector(stateStore, judgeScoringTimeoutMinutes: 15);
        var ctx = DetectorCtx(now: T0);

        var findings = await detector.DetectAsync(ctx, default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Detector_MediaStuck_Fires_WhenCompletedButNoScoringStarted()
    {
        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task2",
            ("cli", CandidateState.Completed, T0.AddMinutes(-25), null),
            ("squad", CandidateState.Running, null, T0.AddMinutes(-30)));
        InjectActiveTask(stateStore, task);

        var detector = CreateDetector(stateStore, mediaCaptureTimeoutMinutes: 20);
        var ctx = DetectorCtx(now: T0);

        var findings = await detector.DetectAsync(ctx, default);

        Assert.Contains(findings, f => f.DedupKey == "strategy-stuck:task2:media-stuck");
    }

    [Fact]
    public async Task Detector_CandidateStuck_Fires_WhenRunningTooLong()
    {
        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task3",
            ("cli", CandidateState.Running, null, T0.AddMinutes(-70)));
        InjectActiveTask(stateStore, task);

        var detector = CreateDetector(stateStore);
        var ctx = DetectorCtx(now: T0);

        var findings = await detector.DetectAsync(ctx, default);

        var f = Assert.Single(findings);
        Assert.Equal("strategy-stuck:task3:candidate-stuck", f.DedupKey);
    }

    [Fact]
    public async Task Detector_Skips_WhenWinnerAlreadySelected()
    {
        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task4",
            winnerStrategyId: "cli",
            ("cli", CandidateState.Winner, T0.AddMinutes(-60), null));
        InjectActiveTask(stateStore, task);

        var detector = CreateDetector(stateStore);
        var ctx = DetectorCtx(now: T0);

        var findings = await detector.DetectAsync(ctx, default);

        Assert.Empty(findings);
    }

    // =======================================================================
    // PromoteStrategyWinnerAction
    // =======================================================================

    [Fact]
    public async Task PromoteAction_CancelsOrchestration_WhenTaskIsActive()
    {
        var mockCancellation = new Mock<IOrchestrationCancellationService>();
        mockCancellation.Setup(x => x.RequestEmergencyPromotion("run1", "task1")).Returns(true);

        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task1",
            ("cli", CandidateState.Completed, T0.AddMinutes(-20), null));
        InjectActiveTask(stateStore, task);

        var action = new PromoteStrategyWinnerAction(
            mockCancellation.Object, stateStore,
            NullLogger<PromoteStrategyWinnerAction>.Instance);

        var finding = MakeFinding("strategy-stuck:task1:scoring-stuck");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Success, result.Result);
        mockCancellation.Verify(x => x.RequestEmergencyPromotion("run1", "task1"), Times.Once);
    }

    [Fact]
    public async Task PromoteAction_ReturnsNoOp_WhenTaskNoLongerActive()
    {
        var mockCancellation = new Mock<IOrchestrationCancellationService>();
        var stateStore = new CandidateStateStore(null);
        // Don't register any task — stateStore is empty

        var action = new PromoteStrategyWinnerAction(
            mockCancellation.Object, stateStore,
            NullLogger<PromoteStrategyWinnerAction>.Instance);

        var finding = MakeFinding("strategy-stuck:task1:scoring-stuck");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.NoOp, result.Result);
        Assert.Contains("no longer active", result.Detail!);
    }

    [Fact]
    public async Task PromoteAction_ReturnsFailed_WhenDedupKeyInvalid()
    {
        var mockCancellation = new Mock<IOrchestrationCancellationService>();
        var stateStore = new CandidateStateStore(null);

        var action = new PromoteStrategyWinnerAction(
            mockCancellation.Object, stateStore,
            NullLogger<PromoteStrategyWinnerAction>.Instance);

        var finding = MakeFinding("bad-key");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Failed, result.Result);
    }

    // =======================================================================
    // MergeEscalationAction
    // =======================================================================

    [Fact]
    public async Task MergeAction_ReturnsSuccess_WithValidPrNumber()
    {
        var action = new MergeEscalationAction(
            NullLogger<MergeEscalationAction>.Instance,
            notifications: null);

        var finding = MakeFinding("pr-merge-escalation:42");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Success, result.Result);
        Assert.Equal("pr#42", result.Target);
    }

    [Fact]
    public async Task MergeAction_ReturnsFailed_WithInvalidDedupKey()
    {
        var action = new MergeEscalationAction(
            NullLogger<MergeEscalationAction>.Instance);

        var finding = MakeFinding("bad-key");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Failed, result.Result);
    }

    [Fact]
    public void MergeAction_CanHandle_OnlyMergeEscalationFindings()
    {
        var action = new MergeEscalationAction(
            NullLogger<MergeEscalationAction>.Instance);

        Assert.True(action.CanHandle(MakeFinding("pr-merge-escalation:42")));
        Assert.False(action.CanHandle(MakeFinding("strategy-stuck:task1:scoring-stuck")));
        Assert.False(action.CanHandle(MakeFinding("unmerged-approved-pr:42")));
    }

    // =======================================================================
    // UnmergedApprovedPrDetector — Tier 2 (partial-approval stall)
    // =======================================================================

    [Fact]
    public async Task UnmergedTier2_Fires_WhenPartiallyApprovedPrIsIdle()
    {
        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            stuckThreshold: TimeSpan.FromMinutes(5),
            partialApprovalThreshold: TimeSpan.FromMinutes(90));

        var pr = MakePr(42, new[] { "ready-for-review", "architect-approved" },
            updatedAt: T0.AddMinutes(-100));

        var ctx = DetectorCtx(now: T0, prs: new[] { pr });
        var findings = await detector.DetectAsync(ctx, default);

        Assert.Contains(findings, f => f.DedupKey == "pr-merge-escalation:42");
        var f = findings.First(x => x.DedupKey == "pr-merge-escalation:42");
        Assert.Contains("PM", f.Summary);
        Assert.Equal(FlowFindingSeverity.Warning, f.Severity);
    }

    [Fact]
    public async Task UnmergedTier2_Skips_WhenBothApproved()
    {
        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            stuckThreshold: TimeSpan.FromMinutes(5),
            partialApprovalThreshold: TimeSpan.FromMinutes(90));

        // Both approved — Tier 1 handles this, not Tier 2
        var pr = MakePr(42, new[] { "ready-for-review", "architect-approved", "pm-approved" },
            updatedAt: T0.AddMinutes(-100));

        var ctx = DetectorCtx(now: T0, prs: new[] { pr });
        var findings = await detector.DetectAsync(ctx, default);

        Assert.DoesNotContain(findings, f => f.DedupKey == "pr-merge-escalation:42");
    }

    [Fact]
    public async Task UnmergedTier2_Skips_WhenWithinThreshold()
    {
        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            stuckThreshold: TimeSpan.FromMinutes(5),
            partialApprovalThreshold: TimeSpan.FromMinutes(90));

        var pr = MakePr(42, new[] { "ready-for-review", "pm-approved" },
            updatedAt: T0.AddMinutes(-30));

        var ctx = DetectorCtx(now: T0, prs: new[] { pr });
        var findings = await detector.DetectAsync(ctx, default);

        Assert.DoesNotContain(findings, f => f.DedupKey == "pr-merge-escalation:42");
    }

    [Fact]
    public async Task UnmergedTier2_Skips_WhenAgentStuckLabelPresent()
    {
        var detector = new UnmergedApprovedPrDetector(
            NullLogger<UnmergedApprovedPrDetector>.Instance,
            stuckThreshold: TimeSpan.FromMinutes(5),
            partialApprovalThreshold: TimeSpan.FromMinutes(90));

        var pr = MakePr(42, new[] { "ready-for-review", "architect-approved", "agent-stuck" },
            updatedAt: T0.AddMinutes(-100));

        var ctx = DetectorCtx(now: T0, prs: new[] { pr });
        var findings = await detector.DetectAsync(ctx, default);

        Assert.DoesNotContain(findings, f => f.DedupKey == "pr-merge-escalation:42");
    }

    // =======================================================================
    // EmergencyWinnerDefault — configurable tiebreaker preference
    // =======================================================================

    [Fact]
    public void SelectEmergencyWinner_UsesConfigurableDefault()
    {
        var evaluator = CreateEvaluator(emergencyDefault: "cli");
        var candidates = new List<CandidateResult>
        {
            // Two candidates with identical scores — tiebreaker decides
            MakeCandidate("squad", survived: true, succeeded: true, patch: "diff",
                patchSize: 100, elapsed: TimeSpan.FromSeconds(10)),
            MakeCandidate("cli", survived: true, succeeded: true, patch: "diff",
                patchSize: 100, elapsed: TimeSpan.FromSeconds(10)),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        Assert.NotNull(result);
        Assert.Equal("cli", result.Winner!.StrategyId);
    }

    [Fact]
    public void SelectEmergencyWinner_EmptyDefaultSkipsPreference()
    {
        var evaluator = CreateEvaluator(emergencyDefault: "");
        var candidates = new List<CandidateResult>
        {
            MakeCandidate("squad", survived: true, succeeded: true, patch: "diff",
                patchSize: 100, elapsed: TimeSpan.FromSeconds(10)),
            MakeCandidate("cli", survived: true, succeeded: true, patch: "diff",
                patchSize: 100, elapsed: TimeSpan.FromSeconds(10)),
        };

        var result = evaluator.SelectEmergencyWinner(candidates);

        // With no preference, first in order wins (both identical)
        Assert.NotNull(result);
    }

    // =======================================================================
    // MergeEscalationAction — human gate awareness
    // =======================================================================

    [Fact]
    public async Task MergeAction_IncludesHumanGateContext_WhenRequired()
    {
        var vdtCfg = new VirtualDevTeamConfig();
        vdtCfg.HumanInteraction.Enabled = true;
        vdtCfg.HumanInteraction.Gates[GateIds.FinalPRApproval].RequiresHuman = true;

        var flowCfg = new FlowMonitorConfig { EnableAutoMerge = false };

        var action = new MergeEscalationAction(
            NullLogger<MergeEscalationAction>.Instance,
            notifications: null,
            vdtConfig: new TestOptionsMonitor<VirtualDevTeamConfig>(vdtCfg),
            flowConfig: new TestOptionsMonitor<FlowMonitorConfig>(flowCfg));

        var finding = MakeFinding("pr-merge-escalation:42");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Success, result.Result);
        Assert.Contains("Human gate: required", result.Detail!);
    }

    [Fact]
    public async Task MergeAction_IndicatesAutoMerge_WhenEnabled()
    {
        var vdtCfg = new VirtualDevTeamConfig();
        vdtCfg.HumanInteraction.Enabled = false; // auto-approve

        var flowCfg = new FlowMonitorConfig { EnableAutoMerge = true };

        var action = new MergeEscalationAction(
            NullLogger<MergeEscalationAction>.Instance,
            notifications: null,
            vdtConfig: new TestOptionsMonitor<VirtualDevTeamConfig>(vdtCfg),
            flowConfig: new TestOptionsMonitor<FlowMonitorConfig>(flowCfg));

        var finding = MakeFinding("pr-merge-escalation:42");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Success, result.Result);
        Assert.Contains("auto-merge: enabled", result.Detail!);
        Assert.Contains("Human gate: auto", result.Detail!);
    }

    // =======================================================================
    // PromoteStrategyWinnerAction — rich candidate summary in outcome
    // =======================================================================

    [Fact]
    public async Task PromoteAction_IncludesCandidateSummary_InDetail()
    {
        var mockCancellation = new Mock<IOrchestrationCancellationService>();
        mockCancellation.Setup(x => x.RequestEmergencyPromotion("run1", "task1")).Returns(true);

        var stateStore = new CandidateStateStore(null);
        var task = MakeTaskSnapshot("run1", "task1",
            ("cli", CandidateState.Completed, T0.AddMinutes(-20), null),
            ("squad", CandidateState.Running, T0.AddMinutes(-25), null));
        InjectActiveTask(stateStore, task);

        var action = new PromoteStrategyWinnerAction(
            mockCancellation.Object, stateStore,
            NullLogger<PromoteStrategyWinnerAction>.Instance);

        var finding = MakeFinding("strategy-stuck:task1:scoring-stuck");
        var result = await action.ExecuteAsync(finding, default);

        Assert.Equal(FlowActionResult.Success, result.Result);
        Assert.Contains("Candidates (2)", result.Detail!);
        Assert.Contains("cli", result.Detail!);
        Assert.Contains("squad", result.Detail!);
    }

    private static CandidateEvaluator CreateEvaluator(
        bool emergencyEnabled = true,
        string emergencyDefault = "squad")
    {
        var cfg = new StrategyFrameworkConfig
        {
            Evaluator = new EvaluatorConfig
            {
                EmergencyWinnerEnabled = emergencyEnabled,
                EmergencyWinnerDefault = emergencyDefault,
                MediaCaptureTimeoutMinutes = 20,
                JudgeScoringTimeoutMinutes = 15,
                VisualScoringTimeoutMinutes = 10,
            }
        };
        var monitor = new TestOptionsMonitor<StrategyFrameworkConfig>(cfg);

        return new CandidateEvaluator(
            NullLogger<CandidateEvaluator>.Instance,
            worktree: null!,
            cfg: monitor);
    }

    private static CandidateResult MakeCandidate(
        string strategyId,
        bool survived,
        bool succeeded,
        string patch = "",
        int patchSize = 0,
        TimeSpan? elapsed = null,
        int? ac = null,
        int? design = null,
        int? readability = null,
        int? visualsScore = null)
    {
        CandidateScore? score = (ac.HasValue || design.HasValue || readability.HasValue || visualsScore.HasValue)
            ? new CandidateScore
            {
                AcceptanceCriteriaScore = ac ?? 0,
                DesignScore = design ?? 0,
                ReadabilityScore = readability ?? 0,
                VisualsScore = visualsScore,
            }
            : null;

        return new CandidateResult
        {
            StrategyId = strategyId,
            Survived = survived,
            Patch = patch,
            PatchSizeBytes = patchSize > 0 ? patchSize : patch.Length,
            Execution = new StrategyExecutionResult
            {
                StrategyId = strategyId,
                Succeeded = succeeded,
                Elapsed = elapsed ?? TimeSpan.FromMinutes(5),
            },
            Score = score,
        };
    }

    // =======================================================================
    // Helpers — CandidateStateStore injection via reflection
    // =======================================================================

    /// <summary>
    /// Injects a TaskSnapshot directly into CandidateStateStore's private _active
    /// dictionary. This is necessary because the store only exposes event-based
    /// methods (RecordStarted, RecordCompleted) which hardcode timestamps to
    /// DateTimeOffset.UtcNow, making time-dependent detector tests impossible
    /// without reflection.
    /// </summary>
    private static void InjectActiveTask(CandidateStateStore store, TaskSnapshot task)
    {
        var field = typeof(CandidateStateStore)
            .GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CandidateStateStore._active field not found");

        var dict = (ConcurrentDictionary<(string RunId, string TaskId), TaskSnapshot>)field.GetValue(store)!;
        dict[(task.RunId, task.TaskId)] = task;
    }

    // =======================================================================
    // Helpers — StrategyEvaluationStuckDetector
    // =======================================================================

    private static StrategyEvaluationStuckDetector CreateDetector(
        CandidateStateStore stateStore,
        int judgeScoringTimeoutMinutes = 15,
        int mediaCaptureTimeoutMinutes = 20)
    {
        var cfg = new StrategyFrameworkConfig
        {
            Evaluator = new EvaluatorConfig
            {
                JudgeScoringTimeoutMinutes = judgeScoringTimeoutMinutes,
                MediaCaptureTimeoutMinutes = mediaCaptureTimeoutMinutes,
            }
        };
        var monitor = new TestOptionsMonitor<StrategyFrameworkConfig>(cfg);

        return new StrategyEvaluationStuckDetector(stateStore, monitor,
            NullLogger<StrategyEvaluationStuckDetector>.Instance);
    }

    private static TaskSnapshot MakeTaskSnapshot(
        string runId, string taskId,
        params (string strategyId, CandidateState state,
                DateTimeOffset? completedAt,
                DateTimeOffset? processStartedAt)[] candidates)
    {
        return MakeTaskSnapshot(runId, taskId, winnerStrategyId: null, candidates);
    }

    private static TaskSnapshot MakeTaskSnapshot(
        string runId, string taskId,
        string? winnerStrategyId = null,
        params (string strategyId, CandidateState state,
                DateTimeOffset? completedAt,
                DateTimeOffset? processStartedAt)[] candidates)
    {
        var dict = candidates.ToImmutableDictionary(
            c => c.strategyId,
            c => new CandidateSnapshot
            {
                StrategyId = c.strategyId,
                State = c.state,
                StartedAt = T0.AddMinutes(-60),
                CompletedAt = c.completedAt,
                ProcessStartedAt = c.processStartedAt,
            });

        return new TaskSnapshot
        {
            RunId = runId,
            TaskId = taskId,
            StartedAt = T0.AddMinutes(-60),
            Candidates = dict,
            WinnerStrategyId = winnerStrategyId,
        };
    }

    // =======================================================================
    // Helpers — FlowFinding
    // =======================================================================

    private static FlowFinding MakeFinding(string dedupKey) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DetectedAt = T0,
        DetectorId = "test-detector",
        Severity = FlowFindingSeverity.Critical,
        Summary = "Test finding",
        Rationale = "Test rationale",
        DedupKey = dedupKey,
        TargetResource = "test-resource",
    };

    // =======================================================================
    // Helpers — UnmergedApprovedPrDetector Tier 2
    // =======================================================================

    private static PullRequestView MakePr(int number, IEnumerable<string> labels,
        DateTimeOffset updatedAt) => new()
    {
        Number = number,
        Title = $"Software Engineer 1: Task {number}",
        State = "open",
        HeadBranch = $"agent/software-engineer-1/task-{number}",
        BaseBranch = "main",
        Labels = labels.ToList(),
        AssignedAgent = "Software Engineer 1",
        CreatedAt = updatedAt.AddHours(-1),
        UpdatedAt = updatedAt,
        MergeableState = "clean",
    };

    private static DetectorContext DetectorCtx(
        DateTimeOffset? now = null,
        IReadOnlyList<PullRequestView>? prs = null) => new()
    {
        Now = now ?? T0,
        Agents = Array.Empty<AgentStateView>(),
        CurrentPhase = "ParallelDevelopment",
        WorkflowSignals = Array.Empty<string>(),
        EffectiveBranch = "main",
        Platform = new TestPlatformView(
            prs ?? Array.Empty<PullRequestView>(),
            Array.Empty<WorkItemView>()),
    };

    private sealed class TestPlatformView : IPlatformView
    {
        private readonly IReadOnlyList<PullRequestView> _prs;
        private readonly IReadOnlyList<WorkItemView> _workItems;

        public TestPlatformView(IReadOnlyList<PullRequestView> prs, IReadOnlyList<WorkItemView> workItems)
        {
            _prs = prs;
            _workItems = workItems;
        }

        public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
            => Task.FromResult(_prs);
        public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
            => Task.FromResult(_workItems);
        public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewThreadView>>(Array.Empty<ReviewThreadView>());
        public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<CommitView?>(null);
    }

    /// <summary>Simple IOptionsMonitor shim for tests.</summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
