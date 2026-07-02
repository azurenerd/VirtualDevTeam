using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// disable-te-toggle: end-to-end behavioral tests for the Test Engineer toggle.
/// Verifies every consumer respects <see cref="ReviewConfig.TestEngineerReviews"/>:
/// settings round-trip, spawn gate, workflow-state-machine gate, FlowMonitor detectors,
/// and the toggle handler's mid-run flip plumbing.
/// </summary>
public sealed class DisableTestEngineerToggleTests
{
    // ── Settings model + propagation ────────────────────────────────────────

    [Fact]
    public void AgentReviewerSettings_TestEngineerReviews_DefaultsToTrue()
    {
        var settings = new AgentReviewerSettings();
        Assert.True(settings.TestEngineerReviews);
    }

    [Fact]
    public void ReviewConfig_TestEngineerReviews_DefaultsToTrue()
    {
        var review = new ReviewConfig();
        Assert.True(review.TestEngineerReviews);
    }

    [Fact]
    public void DevelopSettingsService_MergeIntoConfig_PropagatesTestEngineerReviewsFalse()
    {
        var svc = new DevelopSettingsService(NullLogger<DevelopSettingsService>.Instance);
        var config = new VirtualDevTeamConfig();
        var settings = new DevelopSettings
        {
            AgentReviewers = new AgentReviewerSettings
            {
                PmReviews = true,
                ArchitectReviews = true,
                EngineerReviews = true,
                TestEngineerReviews = false, // ← the new toggle
            }
        };

        svc.MergeIntoConfig(config, settings);

        Assert.False(config.Review.TestEngineerReviews);
        Assert.True(config.Review.PmReviews); // others unaffected
    }

    [Fact]
    public void DevelopSettingsService_MergeIntoConfig_PropagatesTestEngineerReviewsTrue()
    {
        var svc = new DevelopSettingsService(NullLogger<DevelopSettingsService>.Instance);
        var config = new VirtualDevTeamConfig
        {
            Review = new ReviewConfig { TestEngineerReviews = false } // start FALSE
        };
        var settings = new DevelopSettings
        {
            AgentReviewers = new AgentReviewerSettings { TestEngineerReviews = true }
        };

        svc.MergeIntoConfig(config, settings);

        Assert.True(config.Review.TestEngineerReviews);
    }

    // ── AgentSpawnManager: TE spawn refused when toggle is off ──────────────

    [Fact]
    public void CanSpawn_TestEngineer_ReturnsFalse_WhenTestEngineerReviewsOff()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var manager = BuildManager(monitor);

        Assert.False(manager.CanSpawn(AgentRole.TestEngineer));
    }

    [Fact]
    public void CanSpawn_TestEngineer_ReturnsTrue_WhenTestEngineerReviewsOn()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var manager = BuildManager(monitor);

        Assert.True(manager.CanSpawn(AgentRole.TestEngineer));
    }

    [Fact]
    public void CanSpawn_TestEngineer_ReflectsHotReloadedToggle_WithoutRebuildingManager()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var manager = BuildManager(monitor);

        Assert.True(manager.CanSpawn(AgentRole.TestEngineer));

        // Operator flips toggle off via Configuration page
        monitor.Set(MakeConfig(testEngineerReviews: false));

        Assert.False(manager.CanSpawn(AgentRole.TestEngineer));
    }

    [Fact]
    public void CanSpawn_OtherRoles_Unaffected_WhenTestEngineerReviewsOff()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var manager = BuildManager(monitor);

        Assert.True(manager.CanSpawn(AgentRole.ProgramManager));
        Assert.True(manager.CanSpawn(AgentRole.Architect));
        Assert.True(manager.CanSpawn(AgentRole.Researcher));
        Assert.True(manager.CanSpawn(AgentRole.SoftwareEngineer));
    }

    // ── WorkflowStateMachine: Testing gate auto-satisfies ──────────────────

    [Fact]
    public void WorkflowStateMachine_TestingGate_AutoSatisfies_WhenTestEngineerReviewsOff()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var workflow = BuildWorkflow(monitor);

        // Force into Testing phase to evaluate its gates
        workflow.ForcePhase(ProjectPhase.Testing, "test setup");

        var conditions = workflow.GetCurrentGates();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, c => Assert.True(c.IsMet,
            $"Testing gate '{c.Name}' should auto-satisfy when TE disabled but was unmet"));
    }

    [Fact]
    public void WorkflowStateMachine_TestingGate_RequiresSignal_WhenTestEngineerReviewsOn()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var workflow = BuildWorkflow(monitor);

        workflow.ForcePhase(ProjectPhase.Testing, "test setup");

        var conditions = workflow.GetCurrentGates();
        // Without the TestCoverageMet signal the gate is unmet (existing behavior preserved)
        Assert.Contains(conditions, c => !c.IsMet);
    }

    // ── TestEngineerFalseCompletionDetector: short-circuits when toggle off ─

    [Fact]
    public async Task TeFalseCompletion_Skips_WhenTestEngineerReviewsOff()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var detector = new TestEngineerFalseCompletionDetector(
            NullLogger<TestEngineerFalseCompletionDetector>.Instance,
            TimeSpan.FromMinutes(3),
            monitor);

        var ctx = MakeContext(
            agents: new[] { Idle("te-1", "Test Engineer", "TestEngineer") },
            prs: new[] { Pr(42, new[] { "architect-approved" }) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task TeFalseCompletion_Fires_WhenTestEngineerReviewsOn_AndConditionMet()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var detector = new TestEngineerFalseCompletionDetector(
            NullLogger<TestEngineerFalseCompletionDetector>.Instance,
            TimeSpan.FromMinutes(3),
            monitor);

        var ctx = MakeContext(
            agents: new[] { Idle("te-1", "Test Engineer", "TestEngineer") },
            prs: new[] { Pr(42, new[] { "architect-approved" }) });

        var findings = await detector.DetectAsync(ctx, default);
        Assert.Single(findings);
    }

    // ── LabelTransitionTimeoutDetector: TE-bypass for final-merge phase ─────

    [Fact]
    public async Task LabelTransitionTimeout_Skips_WhenAllApprovalsPresent_TeOff()
    {
        // With TE off, "all approvals" = architect-approved + pm-approved (no tests-added).
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            laterPhaseThreshold: TimeSpan.FromMinutes(15),
            config: monitor);

        var ctx = MakeContext(
            agents: Array.Empty<AgentStateView>(),
            prs: new[] { Pr(42, new[] { "architect-approved", "pm-approved" }, ageMinutes: 60) });

        var findings = await detector.DetectAsync(ctx, default);
        // TE-bypass: the final-merge phase is reached, this detector defers to UnmergedApprovedPrDetector.
        Assert.Empty(findings);
    }

    [Fact]
    public async Task LabelTransitionTimeout_Fires_WhenTeOnAndTestsAddedMissing()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var detector = new LabelTransitionTimeoutDetector(
            NullLogger<LabelTransitionTimeoutDetector>.Instance,
            laterPhaseThreshold: TimeSpan.FromMinutes(15),
            config: monitor);

        var ctx = MakeContext(
            agents: Array.Empty<AgentStateView>(),
            prs: new[] { Pr(42, new[] { "architect-approved", "pm-approved" }, ageMinutes: 60) });

        var findings = await detector.DetectAsync(ctx, default);
        // TE on: still waiting for tests-added → flagged.
        Assert.Single(findings);
    }

    // ── TestEngineerToggleHandler: hot-reload reactions ─────────────────────

    [Fact]
    public async Task TestEngineerToggleHandler_OnFlipOff_TerminatesRunningTeAgent()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);

        // Pre-register a TE agent
        var teAgent = new Mock<IAgent>();
        teAgent.SetupGet(a => a.Identity).Returns(new AgentIdentity
        {
            Id = "te-1",
            DisplayName = "Test Engineer",
            Role = AgentRole.TestEngineer,
            ModelTier = "standard",
            Rank = 0,
        });
        teAgent.Setup(a => a.StopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await registry.RegisterAsync(teAgent.Object);

        var spawnManager = BuildManager(monitor, registry);
        var sp = new ServiceProviderStub();
        var handler = new TestEngineerToggleHandler(
            monitor, registry, spawnManager,
            NullLogger<TestEngineerToggleHandler>.Instance, sp);

        await handler.StartAsync(default);

        // Flip OFF
        monitor.Set(MakeConfig(testEngineerReviews: false));

        // The handler does its work on a background task; wait briefly.
        for (int i = 0; i < 30 && registry.GetAgentsByRole(AgentRole.TestEngineer).Count > 0; i++)
            await Task.Delay(100);

        Assert.Empty(registry.GetAgentsByRole(AgentRole.TestEngineer));

        await handler.StopAsync(default);
        handler.Dispose();
    }

    [Fact]
    public async Task TestEngineerToggleHandler_OnFlipOn_DoesNotSpawnIfTeAlreadyPresent()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: false));
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);

        // Pre-register a TE agent (simulating it survived the OFF state somehow)
        var teAgent = new Mock<IAgent>();
        teAgent.SetupGet(a => a.Identity).Returns(new AgentIdentity
        {
            Id = "te-existing",
            DisplayName = "Test Engineer",
            Role = AgentRole.TestEngineer,
            ModelTier = "standard",
            Rank = 0,
        });
        await registry.RegisterAsync(teAgent.Object);

        var spawnManager = BuildManager(monitor, registry);
        var sp = new ServiceProviderStub();
        var handler = new TestEngineerToggleHandler(
            monitor, registry, spawnManager,
            NullLogger<TestEngineerToggleHandler>.Instance, sp);

        await handler.StartAsync(default);

        // Flip ON
        monitor.Set(MakeConfig(testEngineerReviews: true));

        // Give the handler a chance to run.
        await Task.Delay(300);

        // Still exactly one TE — handler did NOT double-spawn.
        Assert.Single(registry.GetAgentsByRole(AgentRole.TestEngineer));

        await handler.StopAsync(default);
        handler.Dispose();
    }

    [Fact]
    public async Task TestEngineerToggleHandler_NoOp_WhenToggleUnchanged()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(testEngineerReviews: true));
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var spawnManager = BuildManager(monitor, registry);
        var sp = new ServiceProviderStub();
        var handler = new TestEngineerToggleHandler(
            monitor, registry, spawnManager,
            NullLogger<TestEngineerToggleHandler>.Instance, sp);

        await handler.StartAsync(default);

        // Fire OnChange with the SAME value — handler should ignore it.
        monitor.Set(MakeConfig(testEngineerReviews: true));
        await Task.Delay(150);

        // No agents spawned (registry remains empty), no exceptions.
        Assert.Empty(registry.GetAllAgents());

        await handler.StopAsync(default);
        handler.Dispose();
    }

    // ── Test helpers ────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    private static VirtualDevTeamConfig MakeConfig(bool testEngineerReviews) => new()
    {
        Review = new ReviewConfig { TestEngineerReviews = testEngineerReviews },
        Limits = new LimitsConfig { EngineerPool = new EngineerPoolConfig { SoftwareEngineerPool = 3 } }
    };

    private static AgentSpawnManager BuildManager(
        IOptionsMonitor<VirtualDevTeamConfig> monitor,
        AgentRegistry? registry = null)
    {
        registry ??= new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var factory = new Mock<IAgentFactory>().Object;
        var gateCheck = new Mock<IGateCheckService>().Object;
        return new AgentSpawnManager(
            registry, factory, gateCheck, monitor, NullLogger<AgentSpawnManager>.Instance);
    }

    private static WorkflowStateMachine BuildWorkflow(IOptionsMonitor<VirtualDevTeamConfig> monitor)
    {
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"te-toggle-test-{Guid.NewGuid():N}.db");
        var stateStore = new VirtualDevTeam.Core.Persistence.AgentStateStore(dbPath);
        var gateCheck = new Mock<IGateCheckService>().Object;
        return new WorkflowStateMachine(
            registry, stateStore, gateCheck, NullLogger<WorkflowStateMachine>.Instance, monitor);
    }

    private static AgentStateView Idle(string id, string name, string role) => new()
    {
        Id = id, DisplayName = name, Role = role, Status = "Idle",
        StatusChangedAt = T0.AddMinutes(-10),
    };

    private static PullRequestView Pr(int number, IEnumerable<string> labels, int ageMinutes = 60) => new()
    {
        Number = number, Title = $"PR #{number}", State = "open",
        HeadBranch = $"agent/software-engineer-1/task-{number}",
        BaseBranch = "main",
        Labels = labels.ToList(),
        AssignedAgent = "Software Engineer 1",
        CreatedAt = T0.AddMinutes(-ageMinutes - 1),
        UpdatedAt = T0.AddMinutes(-ageMinutes),
        MergeableState = "clean",
    };

    private static DetectorContext MakeContext(
        IReadOnlyList<AgentStateView> agents,
        IReadOnlyList<PullRequestView> prs) => new()
        {
            Now = T0,
            Agents = agents,
            CurrentPhase = "ParallelDevelopment",
            WorkflowSignals = Array.Empty<string>(),
            EffectiveBranch = "main",
            Platform = new TestPlatformView(prs),
        };

    private sealed class TestPlatformView : IPlatformView
    {
        private readonly IReadOnlyList<PullRequestView> _prs;
        public TestPlatformView(IReadOnlyList<PullRequestView> prs) { _prs = prs; }
        public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
            => Task.FromResult(_prs);
        public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemView>>(Array.Empty<WorkItemView>());
        public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewThreadView>>(Array.Empty<ReviewThreadView>());
        public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
            => Task.FromResult<CommitView?>(null);
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Test fake mirroring AgentSpawnManagerHotReloadTests.MutableOptionsMonitor&lt;T&gt;.</summary>
    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _current;
        private readonly List<Action<T, string?>> _listeners = new();

        public MutableOptionsMonitor(T initial) { _current = initial; }

        public T CurrentValue => _current;
        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_listeners) _listeners.Add(listener);
            return new Subscription(this, listener);
        }

        public void Set(T value)
        {
            _current = value;
            Action<T, string?>[] snapshot;
            lock (_listeners) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(value, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly MutableOptionsMonitor<T> _owner;
            private readonly Action<T, string?> _listener;
            public Subscription(MutableOptionsMonitor<T> owner, Action<T, string?> listener)
            {
                _owner = owner; _listener = listener;
            }
            public void Dispose() { lock (_owner._listeners) _owner._listeners.Remove(_listener); }
        }
    }
}
