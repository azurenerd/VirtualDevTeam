using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for <see cref="ScenariosDriftDetector"/>:
/// drift event → Critical finding; no drift → no finding;
/// multiple drifts within 5-min window deduplicated by dedup_key.
/// </summary>
public class ScenariosDriftDetectorTests
{
    private readonly Mock<IScenarioRegistry> _registry;
    private readonly ScenariosDriftDetector _detector;

    public ScenariosDriftDetectorTests()
    {
        _registry = new Mock<IScenarioRegistry>();
        _registry.Setup(r => r.Current).Returns(Array.Empty<Scenario>());
        _registry.Setup(r => r.LastLoadHadDrift).Returns(false);

        _detector = new ScenariosDriftDetector(
            _registry.Object,
            NullLogger<ScenariosDriftDetector>.Instance);
    }

    private static DetectorContext MakeContext(DateTimeOffset? now = null) => new()
    {
        Now = now ?? DateTimeOffset.UtcNow,
        Agents = Array.Empty<AgentStateView>(),
        CurrentPhase = "Testing",
        WorkflowSignals = Array.Empty<string>(),
        EffectiveBranch = "main",
        Platform = NullPlatformView.Instance
    };

    private static Scenario MakeScenario(string id) => new()
    {
        Id = id,
        Title = $"Scenario {id}",
        JourneyKind = JourneyKind.UiInteraction,
        Actor = "User",
        Trigger = "Trigger"
    };

    [Fact]
    public async Task DriftEvent_EmitsCriticalFinding()
    {
        _registry.Setup(r => r.LastLoadHadDrift).Returns(true);

        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S01") }));

        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Single(findings);
        Assert.Equal("scenarios-drift", findings[0].DetectorId);
        Assert.Equal(FlowFindingSeverity.Critical, findings[0].Severity);
        Assert.Equal("scenarios-drift", findings[0].DedupKey);
        Assert.Contains("drifted", findings[0].Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoDriftEvent_EmitsNoFinding()
    {
        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ChangedEvent_WithNoDrift_EmitsNoFinding()
    {
        _registry.Setup(r => r.LastLoadHadDrift).Returns(false);
        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S01") }));

        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MultipleDriftEvents_WithinTtl_ProduceSingleFindingPerTick()
    {
        // Detector short-circuits after first event per tick (FlowMonitor handles dedup beyond that).
        _registry.Setup(r => r.LastLoadHadDrift).Returns(true);

        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S01") }));
        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S02") }));

        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Single(findings);
        Assert.Equal("scenarios-drift", findings[0].DedupKey);
    }

    [Fact]
    public async Task DriftEvents_OlderThanTtl_AreExpiredAndNotEmitted()
    {
        _registry.Setup(r => r.LastLoadHadDrift).Returns(true);
        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S01") }));

        // Tick 10 minutes after — event is beyond the 5-min TTL.
        var futureCtx = MakeContext(now: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10));
        var findings = await _detector.DetectAsync(futureCtx, CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void DetectorId_IsScenariosDrift()
    {
        Assert.Equal("scenarios-drift", _detector.DetectorId);
    }

    [Fact]
    public async Task Finding_ContainsScenarioIds_FromChangedEvent()
    {
        _registry.Setup(r => r.LastLoadHadDrift).Returns(true);
        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(new[] { MakeScenario("S01"), MakeScenario("S02") }));

        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Single(findings);
        Assert.Contains("S01", findings[0].Rationale);
        Assert.Contains("S02", findings[0].Rationale);
    }

    [Fact]
    public async Task Finding_HasNonEmptyRationaleAndSummary()
    {
        _registry.Setup(r => r.LastLoadHadDrift).Returns(true);
        _registry.Raise(r => r.Changed += null,
            new ScenarioRegistryChangedEventArgs(Array.Empty<Scenario>()));

        var findings = await _detector.DetectAsync(MakeContext(), CancellationToken.None);

        Assert.Single(findings);
        Assert.NotEmpty(findings[0].Summary);
        Assert.NotEmpty(findings[0].Rationale);
        Assert.NotNull(findings[0].Id);
    }
}
