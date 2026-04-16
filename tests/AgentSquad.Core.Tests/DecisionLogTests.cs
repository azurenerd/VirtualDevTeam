using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentSquad.Core.Tests;

public class DecisionLogTests
{
    private readonly DecisionLog _log = new(NullLogger<DecisionLog>.Instance);

    private static AgentDecision CreateTestDecision(
        string id = "test-001",
        string agentId = "pm-1",
        DecisionImpactLevel level = DecisionImpactLevel.M,
        DecisionStatus status = DecisionStatus.AutoApproved) => new()
    {
        Id = id,
        AgentId = agentId,
        AgentDisplayName = "Program Manager",
        Phase = "Architecture",
        ImpactLevel = level,
        Title = "Test Decision",
        Rationale = "Test rationale",
        Status = status,
    };

    [Fact]
    public void Log_StoresDecision()
    {
        var decision = CreateTestDecision();
        _log.Log(decision);

        var result = _log.GetDecision("test-001");
        Assert.NotNull(result);
        Assert.Equal("Test Decision", result.Title);
        Assert.Equal(DecisionImpactLevel.M, result.ImpactLevel);
    }

    [Fact]
    public void GetDecisions_ReturnsAgentDecisions()
    {
        _log.Log(CreateTestDecision("d1", "pm-1"));
        _log.Log(CreateTestDecision("d2", "pm-1"));
        _log.Log(CreateTestDecision("d3", "arch-1"));

        var pmDecisions = _log.GetDecisions("pm-1");
        Assert.Equal(2, pmDecisions.Count);

        var archDecisions = _log.GetDecisions("arch-1");
        Assert.Single(archDecisions);
    }

    [Fact]
    public void GetAllDecisions_ReturnsAllOrderedByTimestampDesc()
    {
        _log.Log(CreateTestDecision("d1"));
        _log.Log(CreateTestDecision("d2"));

        var all = _log.GetAllDecisions();
        Assert.Equal(2, all.Count);
        // Most recent first
        Assert.True(all[0].CreatedAt >= all[1].CreatedAt);
    }

    [Fact]
    public void GetDecisionsByMinLevel_FiltersCorrectly()
    {
        _log.Log(CreateTestDecision("xs", level: DecisionImpactLevel.XS));
        _log.Log(CreateTestDecision("s", level: DecisionImpactLevel.S));
        _log.Log(CreateTestDecision("m", level: DecisionImpactLevel.M));
        _log.Log(CreateTestDecision("l", level: DecisionImpactLevel.L));
        _log.Log(CreateTestDecision("xl", level: DecisionImpactLevel.XL));

        var large = _log.GetDecisionsByMinLevel(DecisionImpactLevel.L);
        Assert.Equal(2, large.Count);

        var medium = _log.GetDecisionsByMinLevel(DecisionImpactLevel.M);
        Assert.Equal(3, medium.Count);
    }

    [Fact]
    public void GetPendingDecisions_ReturnsOnlyPending()
    {
        _log.Log(CreateTestDecision("d1", status: DecisionStatus.Pending));
        _log.Log(CreateTestDecision("d2", status: DecisionStatus.AutoApproved));
        _log.Log(CreateTestDecision("d3", status: DecisionStatus.Pending));

        var pending = _log.GetPendingDecisions();
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void Update_ChangesStatusAndFeedback()
    {
        _log.Log(CreateTestDecision("d1", status: DecisionStatus.Pending));

        _log.Update("d1", DecisionStatus.Approved, "Looks good");

        var updated = _log.GetDecision("d1");
        Assert.NotNull(updated);
        Assert.Equal(DecisionStatus.Approved, updated.Status);
        Assert.Equal("Looks good", updated.HumanFeedback);
        Assert.NotNull(updated.ResolvedAt);
    }

    [Fact]
    public void GetCountsByLevel_ReturnsCorrectCounts()
    {
        _log.Log(CreateTestDecision("xs1", level: DecisionImpactLevel.XS));
        _log.Log(CreateTestDecision("xs2", level: DecisionImpactLevel.XS));
        _log.Log(CreateTestDecision("l1", level: DecisionImpactLevel.L));

        var counts = _log.GetCountsByLevel();
        Assert.Equal(2, counts[DecisionImpactLevel.XS]);
        Assert.Equal(0, counts[DecisionImpactLevel.S]);
        Assert.Equal(0, counts[DecisionImpactLevel.M]);
        Assert.Equal(1, counts[DecisionImpactLevel.L]);
        Assert.Equal(0, counts[DecisionImpactLevel.XL]);
    }

    [Fact]
    public void GetAgentIds_ReturnsDistinctAgents()
    {
        _log.Log(CreateTestDecision("d1", "pm-1"));
        _log.Log(CreateTestDecision("d2", "arch-1"));

        var ids = _log.GetAgentIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("pm-1", ids);
        Assert.Contains("arch-1", ids);
    }

    [Fact]
    public void ClearAll_RemovesEverything()
    {
        _log.Log(CreateTestDecision("d1"));
        _log.Log(CreateTestDecision("d2", "arch-1"));

        _log.ClearAll();

        Assert.Empty(_log.GetAllDecisions());
        Assert.Empty(_log.GetAgentIds());
        Assert.Null(_log.GetDecision("d1"));
    }

    [Fact]
    public void OnDecisionChanged_FiresOnLogAndUpdate()
    {
        var events = new List<AgentDecision>();
        _log.OnDecisionChanged += d => events.Add(d);

        _log.Log(CreateTestDecision("d1", status: DecisionStatus.Pending));
        _log.Update("d1", DecisionStatus.Approved);

        Assert.Equal(2, events.Count);
        Assert.Equal(DecisionStatus.Pending, events[0].Status);
        Assert.Equal(DecisionStatus.Approved, events[1].Status);
    }
}

public class DecisionGatingConfigTests
{
    [Theory]
    [InlineData("L", DecisionImpactLevel.L)]
    [InlineData("M", DecisionImpactLevel.M)]
    [InlineData("XS", DecisionImpactLevel.XS)]
    [InlineData("XL", DecisionImpactLevel.XL)]
    [InlineData("S", DecisionImpactLevel.S)]
    public void GetMinimumGateLevel_ParsesCorrectly(string input, DecisionImpactLevel expected)
    {
        var config = new DecisionGatingConfig { Enabled = true, MinimumGateLevel = input };
        Assert.Equal(expected, config.GetMinimumGateLevel());
    }

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    public void GetMinimumGateLevel_ReturnsNullForDisabled(string input)
    {
        var config = new DecisionGatingConfig { Enabled = true, MinimumGateLevel = input };
        Assert.Null(config.GetMinimumGateLevel());
    }

    [Fact]
    public void GetMinimumGateLevel_ReturnsNullWhenGlobalDisabled()
    {
        var config = new DecisionGatingConfig { Enabled = false, MinimumGateLevel = "L" };
        Assert.Null(config.GetMinimumGateLevel());
    }

    [Fact]
    public void RequiresGate_GatesAtAndAboveThreshold()
    {
        var config = new DecisionGatingConfig { Enabled = true, MinimumGateLevel = "L" };

        Assert.False(config.RequiresGate(DecisionImpactLevel.XS));
        Assert.False(config.RequiresGate(DecisionImpactLevel.S));
        Assert.False(config.RequiresGate(DecisionImpactLevel.M));
        Assert.True(config.RequiresGate(DecisionImpactLevel.L));
        Assert.True(config.RequiresGate(DecisionImpactLevel.XL));
    }

    [Fact]
    public void RequiresGate_MediumThresholdGatesMAndAbove()
    {
        var config = new DecisionGatingConfig { Enabled = true, MinimumGateLevel = "M" };

        Assert.False(config.RequiresGate(DecisionImpactLevel.XS));
        Assert.False(config.RequiresGate(DecisionImpactLevel.S));
        Assert.True(config.RequiresGate(DecisionImpactLevel.M));
        Assert.True(config.RequiresGate(DecisionImpactLevel.L));
        Assert.True(config.RequiresGate(DecisionImpactLevel.XL));
    }

    [Fact]
    public void RequiresGate_NeverGatesWhenDisabled()
    {
        var config = new DecisionGatingConfig { Enabled = false, MinimumGateLevel = "XS" };

        Assert.False(config.RequiresGate(DecisionImpactLevel.XL));
    }

    [Fact]
    public void DefaultConfig_HasSensibleDefaults()
    {
        var config = new DecisionGatingConfig();

        Assert.False(config.Enabled);
        Assert.Equal("L", config.MinimumGateLevel);
        Assert.True(config.RequirePlanForGated);
        Assert.Equal(2, config.MaxDecisionTurns);
        Assert.Equal(0, config.GateTimeoutMinutes);
        Assert.Equal("auto-approve", config.TimeoutFallbackAction);
    }
}

public class DecisionGateServiceParsingTests
{
    [Fact]
    public void ParseClassificationResponse_ParsesAllFields()
    {
        var response = """
            IMPACT: L
            RATIONALE: This changes the core authentication module affecting all API endpoints
            ALTERNATIVES: Could use middleware instead, or add a wrapper layer
            AFFECTED_FILES: src/Auth/AuthService.cs, src/API/Controllers/
            RISK: May break existing auth tokens, requires migration
            """;

        var (level, rationale, alternatives, affectedFiles, risk) =
            DecisionGateService.ParseClassificationResponse(response);

        Assert.Equal(DecisionImpactLevel.L, level);
        Assert.Contains("core authentication", rationale);
        Assert.Contains("middleware", alternatives);
        Assert.Contains("AuthService.cs", affectedFiles);
        Assert.Contains("migration", risk);
    }

    [Theory]
    [InlineData("IMPACT: XS", DecisionImpactLevel.XS)]
    [InlineData("IMPACT: S", DecisionImpactLevel.S)]
    [InlineData("IMPACT: M", DecisionImpactLevel.M)]
    [InlineData("IMPACT: L", DecisionImpactLevel.L)]
    [InlineData("IMPACT: XL", DecisionImpactLevel.XL)]
    public void ParseClassificationResponse_AllLevels(string line, DecisionImpactLevel expected)
    {
        var (level, _, _, _, _) = DecisionGateService.ParseClassificationResponse(line);
        Assert.Equal(expected, level);
    }

    [Fact]
    public void ParseClassificationResponse_DefaultsToMediumOnUnrecognized()
    {
        var (level, _, _, _, _) = DecisionGateService.ParseClassificationResponse("IMPACT: HUGE");
        Assert.Equal(DecisionImpactLevel.M, level);
    }

    [Fact]
    public void ParseClassificationResponse_DefaultsToMediumOnEmptyInput()
    {
        var (level, rationale, _, _, _) = DecisionGateService.ParseClassificationResponse("");
        Assert.Equal(DecisionImpactLevel.M, level);
        Assert.Equal("No rationale provided", rationale);
    }
}
