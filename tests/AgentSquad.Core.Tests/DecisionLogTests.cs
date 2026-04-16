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

/// <summary>
/// Tests for inline impact classification piggybacked on self-assessment responses.
/// </summary>
public class InlineImpactClassificationTests
{
    [Fact]
    public void ParseAssessment_WithImpactClassification_ExtractsAllFields()
    {
        var response = """
            VERDICT: PASS
            SUMMARY: All criteria met
            GAPS:
            IMPACT: L
            IMPACT_RATIONALE: Introduces new service module with cross-cutting concerns
            ALTERNATIVES: Could use existing middleware pattern instead
            AFFECTED_FILES: src/Services/NewAuth.cs, src/Startup.cs
            RISK: Breaking changes to existing auth consumers
            """;

        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: true);

        Assert.True(result.Passed);
        Assert.True(result.HasImpactClassification);
        Assert.Equal(DecisionImpactLevel.L, result.ImpactLevel);
        Assert.Contains("new service module", result.ImpactRationale);
        Assert.Contains("middleware", result.Alternatives);
        Assert.Contains("NewAuth.cs", result.AffectedFiles);
        Assert.Contains("Breaking changes", result.RiskAssessment);
    }

    [Fact]
    public void ParseAssessment_WithoutImpactFlag_IgnoresImpactFields()
    {
        var response = """
            VERDICT: PASS
            SUMMARY: All criteria met
            GAPS:
            IMPACT: XL
            IMPACT_RATIONALE: Major restructure
            """;

        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: false);

        Assert.True(result.Passed);
        Assert.False(result.HasImpactClassification);
        Assert.Null(result.ImpactLevel);
        Assert.Null(result.ImpactRationale);
    }

    [Fact]
    public void ParseAssessment_WithImpact_PreservesAssessmentFields()
    {
        var response = """
            VERDICT: FAIL
            CONFIDENCE: 72%
            SUMMARY: Missing error handling
            GAPS:
            - [critical] No error handling for null inputs
            - [minor] Missing XML documentation
            IMPACT: M
            IMPACT_RATIONALE: Moderate refactoring of existing class
            """;

        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: true);

        Assert.False(result.Passed);
        Assert.Equal(72, result.Confidence);
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains("No error handling", result.Gaps[0]);
        Assert.Equal("critical", result.GapSeverities[0]);
        Assert.Equal(DecisionImpactLevel.M, result.ImpactLevel);
    }

    [Fact]
    public void ParseAssessment_MissingImpactInResponse_ReturnsNullLevel()
    {
        var response = """
            VERDICT: PASS
            SUMMARY: Looks good
            GAPS:
            """;

        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: true);

        Assert.True(result.Passed);
        Assert.False(result.HasImpactClassification);
        Assert.Null(result.ImpactLevel);
    }

    [Theory]
    [InlineData("IMPACT: XS", DecisionImpactLevel.XS)]
    [InlineData("IMPACT: S", DecisionImpactLevel.S)]
    [InlineData("IMPACT: M", DecisionImpactLevel.M)]
    [InlineData("IMPACT: L", DecisionImpactLevel.L)]
    [InlineData("IMPACT: XL", DecisionImpactLevel.XL)]
    public void ParseAssessment_AllImpactLevels(string impactLine, DecisionImpactLevel expected)
    {
        var response = $"VERDICT: PASS\nSUMMARY: OK\nGAPS:\n{impactLine}";
        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: true);
        Assert.Equal(expected, result.ImpactLevel);
    }

    [Fact]
    public void ParseAssessment_UnrecognizedImpactLevel_ReturnsNull()
    {
        var response = "VERDICT: PASS\nSUMMARY: OK\nGAPS:\nIMPACT: MASSIVE";
        var result = AgentSquad.Core.Agents.Reasoning.SelfAssessmentService.ParseAssessment(response, parseImpact: true);
        Assert.Null(result.ImpactLevel);
    }

    [Fact]
    public void AssessmentResult_HasImpactClassification_FalseByDefault()
    {
        var result = new AgentSquad.Core.Agents.Reasoning.AssessmentResult
        {
            Passed = true,
            Gaps = [],
            Summary = "OK",
        };

        Assert.False(result.HasImpactClassification);
        Assert.Null(result.ImpactLevel);
    }

    [Fact]
    public void AssessmentResult_HasImpactClassification_TrueWhenSet()
    {
        var result = new AgentSquad.Core.Agents.Reasoning.AssessmentResult
        {
            Passed = true,
            Gaps = [],
            Summary = "OK",
            ImpactLevel = DecisionImpactLevel.L,
            ImpactRationale = "Big change",
        };

        Assert.True(result.HasImpactClassification);
        Assert.Equal(DecisionImpactLevel.L, result.ImpactLevel);
    }
}
