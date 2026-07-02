using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Integration.Tests;

/// <summary>
/// Tests for the hardened auto-detect doc-signal heuristic in
/// <see cref="HealthMonitor"/>. Companion to bug
/// <c>healthmon-false-research-complete</c> — same family of bug as Lesson #23.
///
/// <para>
/// Before this fix, HealthMonitor.AutoDetectSignals fired <c>research.doc.ready</c> +
/// <c>research.complete</c> purely on loose status-reason substrings ("complete",
/// "monitoring"). Any agent crash whose last status reason happened to contain those
/// words pushed the WorkflowStateMachine through Research → Architecture →
/// EngineeringPlanning in seconds with NO doc artifacts ever produced.
/// </para>
///
/// <para>
/// The hardened heuristic requires:
///   1. A hard platform check via <see cref="IRepositoryContentService"/> that the
///      doc actually exists on the working branch (for <c>*.doc.ready</c>).
///   2. A positive-completion phrase (e.g., "research published") in a relevant agent's
///      status reason (for <c>*.complete</c>) — narrow phrases only.
///   3. A cooldown to prevent platform-API hammering.
/// </para>
/// </summary>
public class HealthMonitorDocSignalTests : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly AgentStateStore _stateStore;
    private readonly WorkflowStateMachine _workflow;
    private readonly InProcessMessageBus _bus;
    private readonly Mock<IRepositoryContentService> _repoContent;
    private readonly HealthMonitor _monitor;

    public HealthMonitorDocSignalTests()
    {
        _registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"healthmon-docsignal-{Guid.NewGuid():N}.db");
        _stateStore = new AgentStateStore(dbPath);
        _workflow = new WorkflowStateMachine(
            _registry,
            _stateStore,
            new Mock<IGateCheckService>().Object,
            NullLogger<WorkflowStateMachine>.Instance);
        _bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
        _repoContent = new Mock<IRepositoryContentService>(MockBehavior.Strict);

        // Cooldown 0 — tests don't want any throttling between successive calls.
        var hmConfig = new HealthMonitorConfig { AutoDetectSignals = true, DocCheckCooldownSeconds = 0 };
        var monitorCfg = new Mock<IOptionsMonitor<HealthMonitorConfig>>();
        monitorCfg.Setup(m => m.CurrentValue).Returns(hmConfig);

        _monitor = new HealthMonitor(
            _registry,
            _workflow,
            _bus,
            NullLogger<HealthMonitor>.Instance,
            Options.Create(new LimitsConfig()),
            flowMonitorPersistence: null,
            flowMonitorConfig: null,
            notifications: null,
            pullRequestService: null,
            workItemService: null,
            repoContent: _repoContent.Object,
            runBranchProvider: null,
            workflowProfile: null,
            healthMonitorConfig: monitorCfg.Object);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _bus.Dispose();
        _stateStore.Dispose();
        _registry.Dispose();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<DocSignalTestAgent> RegisterAgentAsync(AgentRole role, string? statusReason)
    {
        var agent = new DocSignalTestAgent(new AgentIdentity
        {
            Id = $"{role}-{Guid.NewGuid():N}",
            DisplayName = role.ToString(),
            Role = role,
            ModelTier = "standard"
        }, NullLogger<AgentBase>.Instance);
        if (statusReason is not null)
            agent.SetStatus(AgentStatus.Working, statusReason);
        await _registry.RegisterAsync(agent);
        return agent;
    }

    private void SetupFileExists(string path, string content)
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync(path, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
    }

    private void SetupFileMissing(string path)
    {
        _repoContent
            .Setup(r => r.GetFileContentAsync(path, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    // -----------------------------------------------------------------------
    // Research doc-signal tests (healthmon-false-research-complete)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_DoesNotFire_WhenFileMissing_RegardlessOfStatus()
    {
        // Even when a Researcher is "Working" with status, no signal fires until the file
        // exists on the branch. This is the central fix for the false-positive bug.
        SetupFileMissing("Research.md");
        await RegisterAgentAsync(AgentRole.Researcher, "Working on research");

        await _monitor.TryFireResearchDocSignalsAsync(default);

        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady),
            "research.doc.ready must NOT fire while Research.md does not exist on the branch.");
        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete),
            "research.complete must NOT fire without research.doc.ready.");
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_FiresBothSignals_WhenFileExistsAndStatusIsPositive()
    {
        // Happy path: doc on branch + Researcher status "research published" →
        // BOTH research.doc.ready AND research.complete fire.
        SetupFileExists("Research.md", "# Research");
        await RegisterAgentAsync(AgentRole.Researcher, "research published — handing off to PM");

        await _monitor.TryFireResearchDocSignalsAsync(default);

        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady),
            "research.doc.ready should fire when the file exists on the branch.");
        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete),
            "research.complete should fire when the doc-ready is set AND a positive-completion phrase is present.");
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_FiresOnlyDocReady_WhenFileExistsButNoPositivePhrase()
    {
        // File is on the branch but the Researcher's status doesn't yet match a
        // positive-completion phrase ("research complete" / "published" / "findings committed").
        // Only research.doc.ready should fire; research.complete waits for the explicit phrase.
        SetupFileExists("Research.md", "# Research");
        await RegisterAgentAsync(AgentRole.Researcher, "Writing research findings");

        await _monitor.TryFireResearchDocSignalsAsync(default);

        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady),
            "research.doc.ready fires from the hard file check alone.");
        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete),
            "research.complete must wait for a positive-completion phrase, not a generic substring.");
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_AcceptsAllPositivePhrases()
    {
        // Spot-check each phrase in the AgentStatusReasons.ResearchCompletePhrases list.
        foreach (var phrase in AgentStatusReasons.ResearchCompletePhrases)
        {
            // Fresh registry/workflow per phrase — signals are sticky.
            using var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
            var dbPath = Path.Combine(Path.GetTempPath(), $"healthmon-phrase-{Guid.NewGuid():N}.db");
            using var stateStore = new AgentStateStore(dbPath);
            var workflow = new WorkflowStateMachine(registry, stateStore,
                new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);
            using var bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
            var repoContent = new Mock<IRepositoryContentService>();
            repoContent
                .Setup(r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("# Research");
            var hmCfg = new Mock<IOptionsMonitor<HealthMonitorConfig>>();
            hmCfg.Setup(m => m.CurrentValue).Returns(new HealthMonitorConfig { DocCheckCooldownSeconds = 0 });

            using var monitor = new HealthMonitor(
                registry, workflow, bus, NullLogger<HealthMonitor>.Instance,
                Options.Create(new LimitsConfig()),
                repoContent: repoContent.Object,
                healthMonitorConfig: hmCfg.Object);

            var agent = new DocSignalTestAgent(new AgentIdentity
            {
                Id = $"r-{Guid.NewGuid():N}",
                DisplayName = "Researcher",
                Role = AgentRole.Researcher,
                ModelTier = "standard"
            }, NullLogger<AgentBase>.Instance);
            agent.SetStatus(AgentStatus.Working, $"prefix {phrase} suffix");
            await registry.RegisterAsync(agent);

            await monitor.TryFireResearchDocSignalsAsync(default);

            Assert.True(workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete),
                $"Positive-completion phrase '{phrase}' should fire research.complete.");
        }
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_NoOpWhenRepoContentNotInjected()
    {
        // Tests + standalone Dashboard host may construct HealthMonitor without
        // IRepositoryContentService. In that case the heuristic must safely no-op
        // (no false-positive signals from the legacy substring matcher).
        using var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"healthmon-noinject-{Guid.NewGuid():N}.db");
        using var stateStore = new AgentStateStore(dbPath);
        var workflow = new WorkflowStateMachine(registry, stateStore,
            new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);
        using var bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
        using var monitor = new HealthMonitor(
            registry, workflow, bus, NullLogger<HealthMonitor>.Instance,
            Options.Create(new LimitsConfig())
            // No repoContent / workflowProfile / runBranchProvider / healthMonitorConfig.
        );

        await monitor.TryFireResearchDocSignalsAsync(default);

        Assert.False(workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady));
        Assert.False(workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete));
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_RespectsCooldown()
    {
        // With a non-zero cooldown, the platform call must be made at most once until
        // the cooldown elapses. We assert the mock is called exactly once after two
        // back-to-back attempts. Cooldown of 3600s is effectively "never expires
        // during the test".
        using var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"healthmon-cooldown-{Guid.NewGuid():N}.db");
        using var stateStore = new AgentStateStore(dbPath);
        var workflow = new WorkflowStateMachine(registry, stateStore,
            new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);
        using var bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
        var repoContent = new Mock<IRepositoryContentService>();
        repoContent
            .Setup(r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var hmCfg = new Mock<IOptionsMonitor<HealthMonitorConfig>>();
        hmCfg.Setup(m => m.CurrentValue).Returns(new HealthMonitorConfig { DocCheckCooldownSeconds = 3600 });

        using var monitor = new HealthMonitor(
            registry, workflow, bus, NullLogger<HealthMonitor>.Instance,
            Options.Create(new LimitsConfig()),
            repoContent: repoContent.Object,
            healthMonitorConfig: hmCfg.Object);

        await monitor.TryFireResearchDocSignalsAsync(default);
        await monitor.TryFireResearchDocSignalsAsync(default);
        await monitor.TryFireResearchDocSignalsAsync(default);

        repoContent.Verify(
            r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Cooldown must throttle platform calls to one per window.");
    }

    [Fact]
    public async Task TryFireResearchDocSignalsAsync_KillSwitchPreventsAllAutoSignals()
    {
        // The AutoDetectSignals config switch (default true) lets operators disable
        // the heuristic entirely. The TryFire helpers themselves still run, but the
        // outer AutoDetectSignals method gates dispatch. We verify via AutoDetectSignals'
        // observable behavior: with kill-switch off, even firing a status change on
        // a Researcher whose Research.md exists does NOT push the workflow forward.
        using var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"healthmon-killswitch-{Guid.NewGuid():N}.db");
        using var stateStore = new AgentStateStore(dbPath);
        var workflow = new WorkflowStateMachine(registry, stateStore,
            new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);
        using var bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
        var repoContent = new Mock<IRepositoryContentService>();
        repoContent
            .Setup(r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Research");
        var hmCfg = new Mock<IOptionsMonitor<HealthMonitorConfig>>();
        hmCfg.Setup(m => m.CurrentValue).Returns(new HealthMonitorConfig
        {
            AutoDetectSignals = false,
            DocCheckCooldownSeconds = 0
        });

        using var monitor = new HealthMonitor(
            registry, workflow, bus, NullLogger<HealthMonitor>.Instance,
            Options.Create(new LimitsConfig()),
            repoContent: repoContent.Object,
            healthMonitorConfig: hmCfg.Object);

        // Trigger via a Researcher status change (which calls AutoDetectSignals internally).
        var agent = new DocSignalTestAgent(new AgentIdentity
        {
            Id = $"r-{Guid.NewGuid():N}",
            DisplayName = "Researcher",
            Role = AgentRole.Researcher,
            ModelTier = "standard"
        }, NullLogger<AgentBase>.Instance);
        agent.SetStatus(AgentStatus.Working, "research published");
        await registry.RegisterAsync(agent);

        // Give any async fire-and-forget a window to land.
        await Task.Delay(100);

        Assert.False(workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady),
            "Kill-switch must prevent auto-detect from firing any signal.");
        Assert.False(workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete));
        repoContent.Verify(
            r => r.GetFileContentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Kill-switch must skip the platform call entirely.");
    }

    // -----------------------------------------------------------------------
    // Architecture doc-signal tests (parity with Research)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryFireArchitectureDocSignalsAsync_DoesNotFire_WhenFileMissing()
    {
        SetupFileMissing("Architecture.md");
        await RegisterAgentAsync(AgentRole.Architect, "architecture complete");

        await _monitor.TryFireArchitectureDocSignalsAsync(default);

        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady));
        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete));
    }

    [Fact]
    public async Task TryFireArchitectureDocSignalsAsync_FiresBoth_WhenFileExistsAndPositivePhrase()
    {
        SetupFileExists("Architecture.md", "# Architecture");
        await RegisterAgentAsync(AgentRole.Architect, "architecture published — handing off to SE");

        await _monitor.TryFireArchitectureDocSignalsAsync(default);

        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady));
        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete));
    }

    [Fact]
    public async Task TryFireArchitectureDocSignalsAsync_FiresOnlyDocReady_WithoutPositivePhrase()
    {
        SetupFileExists("Architecture.md", "# Architecture");
        await RegisterAgentAsync(AgentRole.Architect, "Drafting architecture");

        await _monitor.TryFireArchitectureDocSignalsAsync(default);

        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady));
        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete));
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Minimal AgentBase subclass that exposes UpdateStatus publicly so tests can
    /// drive status reasons without running the agent loop.
    /// </summary>
    private sealed class DocSignalTestAgent : AgentBase
    {
        public DocSignalTestAgent(AgentIdentity identity, ILogger<AgentBase> logger)
            : base(identity, logger) { }

        protected override Task RunAgentLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public void SetStatus(AgentStatus status, string reason) => UpdateStatus(status, reason);
    }
}
