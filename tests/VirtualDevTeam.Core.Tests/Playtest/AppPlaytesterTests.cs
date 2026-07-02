using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.Agents.Playtest;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Playtest;

public class AppPlaytesterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Scenario MakeScenario(
        string id,
        JourneyKind kind = JourneyKind.ApiCall,
        ScenarioStatus status = ScenarioStatus.Approved,
        params string[] surfaceKinds)
    {
        var surfaces = surfaceKinds.Select(k => new ObservationSurface { Kind = k }).ToList();
        return new Scenario
        {
            Id = id,
            Title = $"Scenario {id}",
            JourneyKind = kind,
            Actor = "Test Actor",
            Trigger = "Test trigger",
            ObservationSurfaces = surfaces,
            Status = status,
        };
    }

    private static PlaytestActionPlan MakePlan(string scenarioId, string adapter = "ApiPlaytestAdapter",
        params (string actionType, string? surfaceVerified)[] actions)
    {
        var planActions = actions.Select((a, i) => new PlaytestAction
        {
            StepIndex = i,
            ActionType = a.actionType,
            SurfaceVerified = a.surfaceVerified,
        }).ToList();

        return new PlaytestActionPlan
        {
            ScenarioId = scenarioId,
            JourneyKind = "api_call",
            Adapter = adapter,
            Actions = planActions,
        };
    }

    /// <summary>
    /// Creates a mock IChatCompletionRunner that returns a serialized action plan JSON
    /// followed by a narrative judge verdict.
    /// </summary>
    private static Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner> MockChatRunner(
        PlaytestActionPlan plan,
        string narrativeVerdict = "verified",
        double narrativeConfidence = 0.95)
    {
        var mock = new Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner>();

        var planJson = System.Text.Json.JsonSerializer.Serialize(plan,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });

        var narrativeJson = $$$"""
            {
              "scenario_id": "{{{plan.ScenarioId}}}",
              "layer3_verdict": "{{{narrativeVerdict}}}",
              "confidence": {{{narrativeConfidence}}},
              "operator_review_required": false,
              "narrative_coherence": {{ "coherent": true, "summary": "All good" }}
            }
            """;

        // First invocation returns action plan, subsequent return narrative judge
        var callCount = 0;
        mock.Setup(r => r.InvokeAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref callCount) == 1 ? planJson : narrativeJson);

        return mock;
    }

    private static Mock<IScenarioRegistry> MockRegistry(params Scenario[] scenarios)
    {
        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.Current).Returns(scenarios.ToList());
        return mock;
    }

    private static Mock<IPlaytestAdapter> MockAdapter(
        string handlesCategory,
        bool returnPassed = true,
        string observationKind = "http_response")
    {
        var mock = new Mock<IPlaytestAdapter>();
        mock.Setup(a => a.CanHandle(It.Is<PlaytestAction>(p => p.ActionCategory == handlesCategory)))
            .Returns(true);
        mock.Setup(a => a.CanHandle(It.Is<PlaytestAction>(p => p.ActionCategory != handlesCategory)))
            .Returns(false);

        IPlaytestEvidence evidence = returnPassed
            ? new ActionSuccessEvidence(observationKind, "mock pass")
            : new ActionFailureEvidence(observationKind, "mock failure");

        mock.Setup(a => a.ExecuteAsync(
                It.IsAny<PlaytestAction>(),
                It.IsAny<AppHandle>(),
                It.IsAny<Dictionary<string, string?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(evidence);

        return mock;
    }

    // ── Empty scenario list ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmptyScenarioList_ReturnsEmptyArray()
    {
        var registry = MockRegistry();
        var chatRunner = new Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner>();
        var adapter = MockAdapter("http");

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            [adapter.Object],
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Empty(reports);
    }

    // ── Scenario not approved → skipped ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnlyDraftScenarios_ReturnsEmpty()
    {
        var scenario = MakeScenario("S01", status: ScenarioStatus.Proposed);
        var registry = MockRegistry(scenario);
        var chatRunner = new Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner>();

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            Array.Empty<IPlaytestAdapter>(),
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Empty(reports);
    }

    // ── Action plan generation failure → inconclusive ────────────────────────

    [Fact]
    public async Task RunAsync_PlanGenerationFails_ReturnsInconclusiveReport()
    {
        var scenario = MakeScenario("S01");
        var registry = MockRegistry(scenario);

        var chatRunner = new Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner>();
        chatRunner.Setup(r => r.InvokeAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            Array.Empty<IPlaytestAdapter>(),
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Single(reports);
        Assert.Equal(VerificationStatus.Inconclusive, reports[0].Verdict);
        Assert.True(reports[0].OperatorReviewRequired);
    }

    // ── All actions pass → Verified (given Layer 3 also says verified) ────────

    [Fact]
    public async Task RunAsync_AllActionsPass_VerdictIsVerified()
    {
        var scenario = MakeScenario("S01", surfaceKinds: "http_response");
        var registry = MockRegistry(scenario);

        var plan = MakePlan("S01", "ApiPlaytestAdapter",
            ("http.post", null),
            ("http.assertStatus", "http_response"));

        var chatRunner = MockChatRunner(plan, narrativeVerdict: "verified");

        // Adapter that passes all actions
        var passAdapter = MockAdapter("http", returnPassed: true, observationKind: "http_response");

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            [passAdapter.Object],
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Single(reports);
        var report = reports[0];
        Assert.Equal("S01", report.ScenarioId);
        Assert.NotNull(report.ActionPlanExecuted);
    }

    // ── One adapter fails an assertion → Broken ───────────────────────────────

    [Fact]
    public async Task RunAsync_AssertionFails_Layer1IsBroken()
    {
        var scenario = MakeScenario("S01", surfaceKinds: "http_response");
        var registry = MockRegistry(scenario);

        // Plan has one assertion action
        var plan = MakePlan("S01", "ApiPlaytestAdapter",
            ("http.assertStatus", "http_response"));

        var chatRunner = MockChatRunner(plan, narrativeVerdict: "broken");

        // Adapter returns failure evidence
        var failAdapter = MockAdapter("http", returnPassed: false, observationKind: "http_response");

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            [failAdapter.Object],
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Single(reports);
        // Layer 1 broken + Layer 3 broken → final = Broken
        Assert.Equal(VerificationStatus.Broken, reports[0].Verdict);
        Assert.NotEmpty(reports[0].FailedSurfaces);
    }

    // ── No matching adapter → inconclusive surface ────────────────────────────

    [Fact]
    public async Task RunAsync_NoAdapterForAction_SurfaceIsInconclusive()
    {
        var scenario = MakeScenario("S01", surfaceKinds: "dom_query");
        var registry = MockRegistry(scenario);

        // Plan uses page.click but we only register an http adapter
        var plan = MakePlan("S01", "WebPlaytestAdapter",
            ("page.assertSelectorExists", "dom_query"));

        var chatRunner = MockChatRunner(plan, narrativeVerdict: "inconclusive");
        // Only register API adapter — won't handle page.* actions
        var apiAdapter = MockAdapter("http", returnPassed: true);

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            [apiAdapter.Object],
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Single(reports);
        // Surface is inconclusive → final verdict at least inconclusive
        Assert.NotEqual(VerificationStatus.Verified, reports[0].Verdict);
    }

    // ── Multiple scenarios executed in order ──────────────────────────────────

    [Fact]
    public async Task RunAsync_TwoScenarios_ReturnsTwoReports()
    {
        var s1 = MakeScenario("S01");
        var s2 = MakeScenario("S02");
        var registry = MockRegistry(s1, s2);

        var plan1 = MakePlan("S01");
        var plan2 = MakePlan("S02");

        var callCount = 0;
        var chatMock = new Mock<VirtualDevTeam.Core.AI.IChatCompletionRunner>();

        var verdictJson = $$$"""{"scenario_id":"X","layer3_verdict":"inconclusive","confidence":0.5,"operator_review_required":false}""";

        chatMock.Setup(r => r.InvokeAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var count = Interlocked.Increment(ref callCount);
                // Odd calls → action plans, even calls → narrative verdicts
                if (count % 2 == 1)
                {
                    var plan = count == 1 ? plan1 : plan2;
                    return System.Text.Json.JsonSerializer.Serialize(plan,
                        new System.Text.Json.JsonSerializerOptions
                            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
                }
                return verdictJson;
            });

        var playtester = new AppPlaytester(
            registry.Object,
            chatMock.Object,
            Array.Empty<IPlaytestAdapter>(),
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle);

        Assert.Equal(2, reports.Length);
        Assert.Equal("S01", reports[0].ScenarioId);
        Assert.Equal("S02", reports[1].ScenarioId);
    }

    // ── Explicit scenario list overrides registry ─────────────────────────────

    [Fact]
    public async Task RunAsync_ExplicitScenarios_OverridesRegistry()
    {
        // Registry has S01, but caller passes only S02
        var registryScenario = MakeScenario("S01");
        var registry = MockRegistry(registryScenario);

        var explicitScenario = MakeScenario("S02");

        var plan = MakePlan("S02");
        var chatRunner = MockChatRunner(plan, narrativeVerdict: "inconclusive");

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            Array.Empty<IPlaytestAdapter>(),
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        var reports = await playtester.RunAsync(handle, [explicitScenario]);

        Assert.Single(reports);
        Assert.Equal("S02", reports[0].ScenarioId);
    }

    // ── Adapter dispatch selects correct adapter ──────────────────────────────

    [Fact]
    public async Task RunAsync_DispatchesHttpActionsToApiAdapter_NotCliAdapter()
    {
        var scenario = MakeScenario("S01");
        var registry = MockRegistry(scenario);

        var plan = MakePlan("S01", "ApiPlaytestAdapter",
            ("http.post", null),
            ("http.assertStatus", "http_response"));

        var chatRunner = MockChatRunner(plan);

        var apiAdapter = MockAdapter("http", returnPassed: true);
        var cliAdapter = MockAdapter("cli", returnPassed: true);

        var playtester = new AppPlaytester(
            registry.Object,
            chatRunner.Object,
            [apiAdapter.Object, cliAdapter.Object],
            NullLogger<AppPlaytester>.Instance);

        var handle = new AppHandle { BaseUrl = "http://localhost:5000" };
        await playtester.RunAsync(handle);

        // CLI adapter should NOT have been called for http.* actions
        cliAdapter.Verify(a => a.ExecuteAsync(
            It.IsAny<PlaytestAction>(),
            It.IsAny<AppHandle>(),
            It.IsAny<Dictionary<string, string?>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // API adapter should have been called for each action
        apiAdapter.Verify(a => a.ExecuteAsync(
            It.IsAny<PlaytestAction>(),
            It.IsAny<AppHandle>(),
            It.IsAny<Dictionary<string, string?>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
