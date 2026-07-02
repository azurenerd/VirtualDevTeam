using VirtualDevTeam.Core.AI;
using Xunit;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class AgentUsageTrackerStrategyTests
{
    [Fact]
    public void RecordStrategyCall_Accumulates()
    {
        var t = new AgentUsageTracker();
        t.RecordStrategyCall("baseline", "claude-sonnet-4.6", 1000, 200);
        t.RecordStrategyCall("baseline", "claude-sonnet-4.6", 500, 100);
        var s = t.GetStrategyStats("baseline");
        Assert.Equal(2, s.TotalCalls);
        Assert.True(s.TotalTokens > 0);
    }

    [Fact]
    public void RecordStrategyTokens_IgnoresZeroOrNegative()
    {
        var t = new AgentUsageTracker();
        t.RecordStrategyTokens("baseline", "m", 0);
        t.RecordStrategyTokens("baseline", "m", -5);
        Assert.Equal(0, t.GetStrategyStats("baseline").TotalCalls);
    }

    [Fact]
    public void GetAllStrategyStats_SegregatesPerStrategy()
    {
        var t = new AgentUsageTracker();
        t.RecordStrategyTokens("baseline", "m", 1000);
        t.RecordStrategyTokens("mcp-enhanced", "m", 2000);
        var all = t.GetAllStrategyStats();
        Assert.Equal(2, all.Count);
        Assert.Contains("baseline", all.Keys);
        Assert.Contains("mcp-enhanced", all.Keys);
    }

    [Fact]
    public void GetTotalStrategyCost_SumsAcrossStrategies()
    {
        var t = new AgentUsageTracker();
        t.RecordStrategyCall("baseline", "claude-sonnet-4.6", 1000, 500);
        t.RecordStrategyCall("mcp-enhanced", "claude-opus-4.6", 2000, 500);
        Assert.True(t.GetTotalStrategyCost() > 0);
    }

    [Fact]
    public void StrategyStats_Isolated_From_AgentStats()
    {
        var t = new AgentUsageTracker();
        t.RecordCall("pm", "m", 1000, 200);
        Assert.Equal(0, t.GetStrategyStats("pm").TotalCalls);
        Assert.Equal(1, t.GetStats("pm").TotalCalls);
    }
}
