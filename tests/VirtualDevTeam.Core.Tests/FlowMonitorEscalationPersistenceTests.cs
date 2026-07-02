using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// T1.2 / T1.3 — tests covering the SQLite helpers that back the escalation ladder
/// (<see cref="FlowMonitorPersistence.GetAttemptCount"/>) and the verification-after-action
/// loop (<see cref="FlowMonitorPersistence.GetActedOnFindingsSince"/>,
/// <see cref="FlowMonitorPersistence.UpdateFindingSeverity"/>). The full tick orchestration
/// in FlowMonitorService is integration-tested at runtime; this file pins the lower-level
/// invariants so the routing can rely on them.
/// </summary>
public sealed class FlowMonitorEscalationPersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateStore _stateStore;
    private readonly FlowMonitorPersistence _persistence;

    public FlowMonitorEscalationPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"vdt-flow-escalation-tests-{Guid.NewGuid():N}.db");
        _stateStore = new AgentStateStore(_dbPath);
        _persistence = new FlowMonitorPersistence(
            _stateStore, NullLogger<FlowMonitorPersistence>.Instance);
    }

    public void Dispose()
    {
        try { _persistence.Dispose(); } catch { }
        try { _stateStore.Dispose(); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ---------------------------------------------------------------------
    // T1.2: GetAttemptCount drives the escalation-ladder routing
    // ---------------------------------------------------------------------

    [Fact]
    public void GetAttemptCount_EmptyDb_ReturnsZero()
    {
        var count = _persistence.GetAttemptCount("agent-stuck:agent-1", TimeSpan.FromHours(4));
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetAttemptCount_NullOrEmptyKey_ReturnsZero()
    {
        Assert.Equal(0, _persistence.GetAttemptCount("", TimeSpan.FromHours(4)));
        Assert.Equal(0, _persistence.GetAttemptCount(null!, TimeSpan.FromHours(4)));
    }

    [Fact]
    public void GetAttemptCount_CountsOnlyMatchingDedupKeyAndExcludesVerifyRows()
    {
        // Two findings with the SAME dedup_key (e.g. agent stuck repeatedly) and one
        // with a different dedup_key (different agent). We then attach actions of
        // various types to each finding.
        var stuckKey = "agent-stuck:engineer-1";
        var otherKey = "agent-stuck:pm-1";

        var f1 = NewFinding("agent-stuck", stuckKey, severity: FlowFindingSeverity.Warning);
        var f2 = NewFinding("agent-stuck", stuckKey, severity: FlowFindingSeverity.Warning);
        var f3 = NewFinding("agent-stuck", otherKey, severity: FlowFindingSeverity.Warning);

        // Insert with no dedup window so subsequent inserts aren't suppressed.
        Assert.True(_persistence.InsertFinding(f1, TimeSpan.Zero));
        Assert.True(_persistence.InsertFinding(f2, TimeSpan.Zero));
        Assert.True(_persistence.InsertFinding(f3, TimeSpan.Zero));

        // 2 real action rows on the stuckKey findings, plus 1 verify row that should NOT count.
        InsertAction(f1.Id, "kick-agent-poll", attempt: 1);
        InsertAction(f2.Id, "post-explicit-ask", attempt: 2);
        InsertAction(f1.Id, "verify-acted-on", attempt: 0); // verification, must be excluded
        InsertAction(f3.Id, "kick-agent-poll", attempt: 1); // different dedup key, must be excluded

        var count = _persistence.GetAttemptCount(stuckKey, TimeSpan.FromHours(4));

        // Two real rung-actions on the stuckKey, despite the verify row + the row on otherKey.
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetAttemptCount_RespectsTimeWindow()
    {
        var key = "agent-stuck:engineer-2";
        var finding = NewFinding("agent-stuck", key);
        Assert.True(_persistence.InsertFinding(finding, TimeSpan.Zero));

        // One recent action, one stale action (5 hours ago — outside the 4h window).
        InsertActionAt(finding.Id, "kick-agent-poll", attempt: 1, initiatedAt: DateTimeOffset.UtcNow);
        InsertActionAt(finding.Id, "kick-agent-poll", attempt: 1,
            initiatedAt: DateTimeOffset.UtcNow.AddHours(-5));

        var count = _persistence.GetAttemptCount(key, TimeSpan.FromHours(4));

        // Stale action excluded.
        Assert.Equal(1, count);
    }

    [Fact]
    public void InsertAction_AndGetRecentActions_RoundTripsAttemptCount()
    {
        var f = NewFinding("agent-stuck", "agent-stuck:engineer-3");
        Assert.True(_persistence.InsertFinding(f, TimeSpan.Zero));

        InsertAction(f.Id, "kick-agent-poll", attempt: 1);
        InsertAction(f.Id, "post-explicit-ask", attempt: 2);
        InsertAction(f.Id, "escalate-to-human", attempt: 3);

        var actions = _persistence.GetRecentActions(50);

        // Newest first: rung 3 at the top.
        Assert.Equal(3, actions.Count);
        Assert.Contains(actions, a => a.ActionType == "kick-agent-poll" && a.AttemptCount == 1);
        Assert.Contains(actions, a => a.ActionType == "post-explicit-ask" && a.AttemptCount == 2);
        Assert.Contains(actions, a => a.ActionType == "escalate-to-human" && a.AttemptCount == 3);
    }

    // ---------------------------------------------------------------------
    // T1.3: verification-after-action SQLite helpers
    // ---------------------------------------------------------------------

    [Fact]
    public void GetActedOnFindingsSince_OnlyReturnsActedOnAndRespectsCutoff()
    {
        var open = NewFinding("agent-stuck", "k1", state: FlowFindingState.Open);
        var actedRecent = NewFinding("agent-stuck", "k2", state: FlowFindingState.Open);
        var actedOld = NewFinding("agent-stuck", "k3", state: FlowFindingState.Open,
            detectedAt: DateTimeOffset.UtcNow.AddHours(-3)); // older than 1h cutoff
        var resolved = NewFinding("agent-stuck", "k4", state: FlowFindingState.Open);

        Assert.True(_persistence.InsertFinding(open, TimeSpan.Zero));
        Assert.True(_persistence.InsertFinding(actedRecent, TimeSpan.Zero));
        Assert.True(_persistence.InsertFinding(actedOld, TimeSpan.Zero));
        Assert.True(_persistence.InsertFinding(resolved, TimeSpan.Zero));

        // Two are advanced to ActedOn, one to Resolved. Open stays Open.
        _persistence.UpdateFindingState(actedRecent.Id, FlowFindingState.ActedOn);
        _persistence.UpdateFindingState(actedOld.Id, FlowFindingState.ActedOn);
        _persistence.UpdateFindingState(resolved.Id, FlowFindingState.Resolved);

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var results = _persistence.GetActedOnFindingsSince(since);

        // Only the recent ActedOn should be returned.
        Assert.Single(results);
        Assert.Equal(actedRecent.Id, results[0].Id);
        Assert.Equal(FlowFindingState.ActedOn, results[0].State);
    }

    [Fact]
    public void UpdateFindingSeverity_Persists()
    {
        var f = NewFinding("agent-stuck", "k-bump", severity: FlowFindingSeverity.Info);
        Assert.True(_persistence.InsertFinding(f, TimeSpan.Zero));

        _persistence.UpdateFindingSeverity(f.Id, FlowFindingSeverity.Critical);

        var refreshed = _persistence.GetRecentFindings(10).Single(x => x.Id == f.Id);
        Assert.Equal(FlowFindingSeverity.Critical, refreshed.Severity);
    }

    [Fact]
    public void InsertFinding_PersistsTargetDisplayName_RoundTrip()
    {
        // T1.2 — escalation actions need a display name to look up PRs/issues; the
        // schema migration must persist it across reads.
        var f = NewFinding("agent-stuck", "k-display", targetDisplayName: "Software Engineer 1");
        Assert.True(_persistence.InsertFinding(f, TimeSpan.Zero));

        var loaded = _persistence.GetRecentFindings(10).Single(x => x.Id == f.Id);
        Assert.Equal("Software Engineer 1", loaded.TargetDisplayName);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static FlowFinding NewFinding(
        string detectorId,
        string dedupKey,
        FlowFindingSeverity severity = FlowFindingSeverity.Warning,
        FlowFindingState state = FlowFindingState.Open,
        string? targetDisplayName = null,
        DateTimeOffset? detectedAt = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DetectedAt = detectedAt ?? DateTimeOffset.UtcNow,
        DetectorId = detectorId,
        Severity = severity,
        TargetAgentId = "agent-id",
        TargetResource = "agent-id",
        TargetDisplayName = targetDisplayName,
        Summary = "test summary",
        Rationale = "test rationale",
        State = state,
        DedupKey = dedupKey,
    };

    private void InsertAction(string findingId, string actionType, int attempt) =>
        InsertActionAt(findingId, actionType, attempt, DateTimeOffset.UtcNow);

    private void InsertActionAt(string findingId, string actionType, int attempt, DateTimeOffset initiatedAt) =>
        _persistence.InsertAction(new FlowAction
        {
            Id = Guid.NewGuid().ToString("N"),
            FindingId = findingId,
            ActionType = actionType,
            Target = "tgt",
            InitiatedAt = initiatedAt,
            CompletedAt = initiatedAt,
            Result = FlowActionResult.Success,
            Detail = null,
            AttemptCount = attempt,
        });
}
