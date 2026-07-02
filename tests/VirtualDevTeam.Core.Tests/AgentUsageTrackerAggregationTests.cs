using VirtualDevTeam.Core.AI;
using Xunit;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for <see cref="AgentUsageTracker.GetAggregatedStatsByRole"/>. This view collapses
/// per-restart agent_id duplicates into one row per role so the dashboard shows a stable
/// cost view across runner restarts.
/// </summary>
public sealed class AgentUsageTrackerAggregationTests
{
    [Fact]
    public void GetAggregatedStatsByRole_CollapsesRestartedAgents()
    {
        var t = new AgentUsageTracker();
        // Simulate 3 restarts of the same Program Manager agent. Each restart produced a new
        // agent_id (`programmanager-{guid}`), with cumulative usage saved per id.
        t.RecordCall("programmanager-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "claude-opus-4.6-1m", 1000, 100);
        t.RecordCall("programmanager-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "claude-opus-4.6-1m", 2000, 200);
        t.RecordCall("programmanager-cccccccccccccccccccccccccccccccc", "claude-opus-4.6-1m", 3000, 300);

        var byRole = t.GetAggregatedStatsByRole();

        Assert.Single(byRole);
        Assert.True(byRole.ContainsKey("programmanager"));
        Assert.Equal(3, byRole["programmanager"].TotalCalls);
    }

    [Fact]
    public void GetAggregatedStatsByRole_KeepsSmeAgentsSeparate()
    {
        var t = new AgentUsageTracker();
        // SME ids are stable (persisted via sme-definitions.json) and semantically meaningful —
        // each SME role spawned distinct from siblings. We do NOT collapse them.
        t.RecordCall("sme-sme-artist-sme-7168062a7816423e8e0f4521a95ec", "x", 100, 10);
        t.RecordCall("sme-sme-game-engine-engineer-c9d73da3804b458ea2c", "x", 200, 20);

        var byRole = t.GetAggregatedStatsByRole();

        Assert.Equal(2, byRole.Count);
        Assert.Contains("sme-sme-artist-sme-7168062a7816423e8e0f4521a95ec", byRole.Keys);
        Assert.Contains("sme-sme-game-engine-engineer-c9d73da3804b458ea2c", byRole.Keys);
    }

    [Fact]
    public void GetAggregatedStatsByRole_PreservesFlowMonitorPseudoIds()
    {
        var t = new AgentUsageTracker();
        // FlowMonitor/Strategy pseudo-ids contain `:` and should pass through unchanged.
        t.RecordCall("flow-monitor:planner", "x", 100, 10);
        t.RecordCall("strategy:specialist-1501", "x", 200, 20);

        var byRole = t.GetAggregatedStatsByRole();
        Assert.Contains("flow-monitor:planner", byRole.Keys);
        Assert.Contains("strategy:specialist-1501", byRole.Keys);
    }

    [Fact]
    public void GetAggregatedStatsByRole_NonGuidSuffix_PassesThrough()
    {
        var t = new AgentUsageTracker();
        // Agent ids that don't have the 32-hex-char guid suffix shape should NOT be collapsed.
        t.RecordCall("manual-test-agent", "x", 100, 10);
        t.RecordCall("agent-123", "x", 200, 20);

        var byRole = t.GetAggregatedStatsByRole();
        Assert.Contains("manual-test-agent", byRole.Keys);
        Assert.Contains("agent-123", byRole.Keys);
    }

    [Fact]
    public void GetAggregatedStatsByRole_TotalsSumProperly()
    {
        var t = new AgentUsageTracker();
        t.RecordCall("testengineer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "x", 1000, 500);
        t.RecordCall("testengineer-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "x", 2000, 1000);
        t.RecordPremiumRequests("testengineer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 5, 1000);
        t.RecordPremiumRequests("testengineer-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 3, 500);

        var byRole = t.GetAggregatedStatsByRole();
        Assert.True(byRole.ContainsKey("testengineer"));
        var s = byRole["testengineer"];
        Assert.Equal(2, s.TotalCalls);
        Assert.Equal(8, s.PremiumRequests);    // 5 + 3
        Assert.Equal(1500, s.ApiDurationMs);   // 1000 + 500
        Assert.True(s.EstimatedCost > 0);      // both calls contribute
    }

    [Fact]
    public void GetAggregatedStatsByRole_MixedRolesAndSmes()
    {
        var t = new AgentUsageTracker();
        t.RecordCall("programmanager-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "x", 100, 10);
        t.RecordCall("programmanager-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "x", 100, 10);
        t.RecordCall("sme-sme-artist-sme-7168062a7816423e8e0f4521a95ec", "x", 100, 10);
        t.RecordCall("flow-monitor:planner", "x", 100, 10);

        var byRole = t.GetAggregatedStatsByRole();
        Assert.Equal(3, byRole.Count);
        Assert.Equal(2, byRole["programmanager"].TotalCalls);
        Assert.Equal(1, byRole["sme-sme-artist-sme-7168062a7816423e8e0f4521a95ec"].TotalCalls);
        Assert.Equal(1, byRole["flow-monitor:planner"].TotalCalls);
    }
}
