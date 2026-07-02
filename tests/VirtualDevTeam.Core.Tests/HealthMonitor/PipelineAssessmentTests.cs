using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Tests.HealthMonitor;

/// <summary>
/// Tests for the Pipeline Assessment Layer (Wave 1):
/// <see cref="PipelineAssessmentStore"/>, <see cref="PipelineAssessmentResultParser"/>,
/// <see cref="AssessmentGrounder"/>, and <see cref="AssessmentConfig"/>.
/// </summary>
public sealed class PipelineAssessmentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateStore _stateStore;
    private readonly PipelineAssessmentStore _store;

    public PipelineAssessmentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"vdt-assessment-tests-{Guid.NewGuid():N}.db");
        _stateStore = new AgentStateStore(_dbPath);
        _store = new PipelineAssessmentStore(
            _stateStore, NullLogger<PipelineAssessmentStore>.Instance);
    }

    public void Dispose()
    {
        try { _store.Dispose(); } catch { }
        try { _stateStore.Dispose(); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ─── PipelineAssessmentStore ───

    [Fact]
    public void InsertAssessment_RoundTrips()
    {
        var assessment = MakeAssessment(healthScore: 7, status: "healthy");
        _store.InsertAssessment(assessment);

        var latest = _store.GetLatestAssessment();
        Assert.NotNull(latest);
        Assert.Equal(assessment.Id, latest.Id);
        Assert.Equal(7, latest.HealthScore);
        Assert.Equal("healthy", latest.Status);
        Assert.Equal("periodic", latest.Kind);
    }

    [Fact]
    public void GetLatestAssessment_EmptyDb_ReturnsNull()
    {
        Assert.Null(_store.GetLatestAssessment());
    }

    [Fact]
    public void GetRecentAssessments_ReturnsInReverseChronological()
    {
        _store.InsertAssessment(MakeAssessment(healthScore: 8, status: "healthy", minutesAgo: 10));
        _store.InsertAssessment(MakeAssessment(healthScore: 5, status: "warning", minutesAgo: 5));
        _store.InsertAssessment(MakeAssessment(healthScore: 3, status: "critical", minutesAgo: 1));

        var recent = _store.GetRecentAssessments(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(3, recent[0].HealthScore); // most recent first
        Assert.Equal(5, recent[1].HealthScore);
    }

    [Fact]
    public void GetRecentAssessments_EmptyDb_ReturnsEmpty()
    {
        var recent = _store.GetRecentAssessments(5);
        Assert.Empty(recent);
    }

    // ─── PipelineAssessmentResultParser ───

    [Fact]
    public void Parse_ValidJson_ReturnsSuccess()
    {
        var parser = new PipelineAssessmentResultParser(
            NullLogger<PipelineAssessmentResultParser>.Instance);
        var json = """
        {
            "healthScore": 8,
            "status": "healthy",
            "summary": "Pipeline is running smoothly",
            "issues": [],
            "recommendations": ["Keep monitoring"],
            "forwardLook": "Should complete in ~30 min"
        }
        """;

        var result = parser.Parse(json);
        Assert.True(result.IsSuccess);
        Assert.Equal("success", result.Status);
        Assert.Equal(8, result.Value!.HealthScore);
        Assert.Equal("Pipeline is running smoothly", result.Value.Summary);
        Assert.Equal("Should complete in ~30 min", result.Value.ForwardLook);
    }

    [Fact]
    public void Parse_JsonWithCodeFences_StripsAndParses()
    {
        var parser = new PipelineAssessmentResultParser(
            NullLogger<PipelineAssessmentResultParser>.Instance);
        var response = """
        Here's my assessment:

        ```json
        {
            "healthScore": 5,
            "status": "warning",
            "summary": "Agent SE-3 appears stuck",
            "issues": [
                {
                    "category": "stuck-agent",
                    "targetType": "agent",
                    "targetId": "se-3",
                    "description": "SE-3 in implementation for 90 min",
                    "severity": "warning",
                    "confidence": 0.85,
                    "recommendedAction": "Check agent logs",
                    "evidence": ["Status unchanged for 90 min"],
                    "dedupKey": "stuck-agent:agent:se-3"
                }
            ]
        }
        ```
        """;

        var result = parser.Parse(response);
        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value!.HealthScore);
        Assert.Single(result.Value.Issues!);
        Assert.Equal("se-3", result.Value.Issues![0].TargetId);
    }

    [Fact]
    public void Parse_GarbageInput_ReturnsFailed()
    {
        var parser = new PipelineAssessmentResultParser(
            NullLogger<PipelineAssessmentResultParser>.Instance);
        var result = parser.Parse("This is not JSON at all");

        Assert.False(result.IsSuccess);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsFailed()
    {
        var parser = new PipelineAssessmentResultParser(
            NullLogger<PipelineAssessmentResultParser>.Instance);

        Assert.False(parser.Parse("").IsSuccess);
        Assert.False(parser.Parse(null!).IsSuccess);
    }

    // ─── AssessmentGrounder ───

    [Fact]
    public void Ground_ValidAgentReference_PassesGrounding()
    {
        var grounder = new AssessmentGrounder(
            NullLogger<AssessmentGrounder>.Instance);
        var issues = new[]
        {
            new AssessmentIssue
            {
                Category = "stuck",
                TargetType = "agent",
                TargetId = "se-1",
                Description = "SE-1 stuck in implementation",
                Severity = "warning",
                Confidence = 0.9,
                DedupKey = "stuck:agent:se-1",
            }
        };

        var snapshot = MakeSnapshot(agentIds: new[] { "se-1", "se-2", "pm" });
        var result = grounder.Ground(issues, snapshot);

        Assert.Equal(1, result.Issues.Length);
        Assert.True(result.Issues[0].GroundingPassed);
        Assert.Equal(1.0, result.PassRate);
    }

    [Fact]
    public void Ground_InvalidAgentReference_FailsGrounding()
    {
        var grounder = new AssessmentGrounder(
            NullLogger<AssessmentGrounder>.Instance);
        var issues = new[]
        {
            new AssessmentIssue
            {
                Category = "stuck",
                TargetType = "agent",
                TargetId = "nonexistent-agent",
                Description = "This agent doesn't exist",
                Severity = "warning",
                Confidence = 0.9,
                DedupKey = "stuck:agent:nonexistent",
            }
        };

        var snapshot = MakeSnapshot(agentIds: new[] { "se-1" });
        var result = grounder.Ground(issues, snapshot);

        Assert.Equal(1, result.Issues.Length);
        Assert.False(result.Issues[0].GroundingPassed);
        Assert.Equal(0.0, result.PassRate);
    }

    [Fact]
    public void Ground_EmptyIssues_Returns100PercentPassRate()
    {
        var grounder = new AssessmentGrounder(
            NullLogger<AssessmentGrounder>.Instance);
        var snapshot = MakeSnapshot(agentIds: Array.Empty<string>());
        var result = grounder.Ground(Array.Empty<AssessmentIssue>(), snapshot);

        Assert.Empty(result.Issues);
        Assert.Equal(1.0, result.PassRate);
    }

    // ─── AssessmentConfig defaults ───

    [Fact]
    public void AssessmentConfig_Defaults_AreReasonable()
    {
        var cfg = new AssessmentConfig();

        Assert.True(cfg.Enabled);
        Assert.Equal(300, cfg.IntervalSeconds);
        Assert.Equal(90, cfg.MinIntervalSeconds);
        Assert.Equal(600, cfg.MaxIntervalSeconds);
        Assert.Equal(30, cfg.LlmTimeoutSeconds);
        Assert.Equal("budget", cfg.ModelTier);
        Assert.Equal(0.7, cfg.ConfidenceThreshold);
        Assert.True(cfg.CreateFindingsOnIssues);
        Assert.Equal(200, cfg.MaxAssessmentsPerDay);
        Assert.Equal(60, cfg.PhaseTransitionGraceSeconds);
        Assert.Equal(40000, cfg.ContextBudgetChars);
    }

    // ─── PipelineStatusSnapshot context budget ───

    [Fact]
    public void ToContextString_RespectsMaxChars()
    {
        var snapshot = MakeSnapshot(agentIds: new[] { "se-1", "se-2", "pm" });
        var context = snapshot.ToContextString(maxChars: 500);

        Assert.True(context.Length <= 500, $"Context string ({context.Length} chars) exceeds 500 char budget");
    }

    // ─── Helpers ───

    private static PipelineAssessment MakeAssessment(
        int healthScore, string status, int minutesAgo = 0)
    {
        return new PipelineAssessment
        {
            Id = Guid.NewGuid().ToString("N"),
            AssessedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            Kind = "periodic",
            HealthScore = healthScore,
            Status = status,
            Summary = $"Test assessment (score={healthScore})",
        };
    }

    private static PipelineStatusSnapshot MakeSnapshot(string[] agentIds)
    {
        return new PipelineStatusSnapshot
        {
            CurrentPhase = "ParallelDevelopment",
            Agents = agentIds.Select(id => new PipelineAgentSnapshot
            {
                AgentId = id,
                DisplayName = id.ToUpperInvariant(),
                Status = "Working",
            }).ToArray(),
            WorkItems = Array.Empty<PipelineTaskSnapshot>(),
            PullRequests = Array.Empty<PrSnapshot>(),
            TimelineSpans = Array.Empty<TimelineSpanSnapshot>(),
            Summary = new PipelineSummary(),
        };
    }
}
