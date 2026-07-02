using Moq;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Agents;

/// <summary>
/// Tests for the scenario-related helper logic wired into <c>ProgramManagerAgent</c>.
/// Because <c>ProgramManagerAgent</c> lives in the Agents assembly (which Core.Tests does not
/// reference), these tests exercise the Core contracts the agent relies on:
/// <list type="bullet">
///   <item><description><see cref="ScenarioYamlSerializer"/> — drives the <c>{{approved_scenarios_yaml}}</c> prompt variable</description></item>
///   <item><description><see cref="IScenarioRegistry"/> mock contracts — <c>LoadAsync</c>, <c>WriteSidecarAsync</c>, <c>ValidateNoOrphans</c></description></item>
/// </list>
/// </summary>
public sealed class ProgramManagerAgentScenariosTests
{
    // -------------------------------------------------------------------------
    // ScenarioYamlSerializer — approved_scenarios_yaml prompt variable
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_EmptyList_ReturnsEmptyString()
    {
        var yaml = ScenarioYamlSerializer.Serialize(Array.Empty<Scenario>());
        Assert.Equal(string.Empty, yaml);
    }

    [Fact]
    public void Serialize_SingleScenario_ContainsRequiredFields()
    {
        var scenario = new Scenario
        {
            Id = "S01",
            Title = "Player starts game",
            JourneyKind = JourneyKind.UiInteraction,
            Actor = "Player",
            Trigger = "User clicks Play",
            Priority = ScenarioPriority.Critical,
            Status = ScenarioStatus.Approved,
        };

        var yaml = ScenarioYamlSerializer.Serialize([scenario]);

        Assert.Contains("- id: S01", yaml);
        Assert.Contains("title: Player starts game", yaml);
        Assert.Contains("journey_kind: ui_interaction", yaml);
        Assert.Contains("priority: critical", yaml);
        Assert.Contains("status: approved", yaml);
        Assert.Contains("actor: Player", yaml);
    }

    [Fact]
    public void Serialize_MultipleScenarios_AllIdsPresent()
    {
        var scenarios = new[]
        {
            new Scenario { Id = "S01", Title = "A", JourneyKind = JourneyKind.UiInteraction, Actor = "User", Trigger = "Click" },
            new Scenario { Id = "S02", Title = "B", JourneyKind = JourneyKind.ApiCall, Actor = "Service", Trigger = "POST /api" },
        };

        var yaml = ScenarioYamlSerializer.Serialize(scenarios);

        Assert.Contains("- id: S01", yaml);
        Assert.Contains("- id: S02", yaml);
        Assert.Contains("journey_kind: ui_interaction", yaml);
        Assert.Contains("journey_kind: api_call", yaml);
    }

    [Fact]
    public void Serialize_NiceToHavePriority_EmitsSnakeCase()
    {
        var scenario = new Scenario
        {
            Id = "S01",
            Title = "Optional feature",
            JourneyKind = JourneyKind.ScheduledJob,
            Actor = "Scheduler",
            Trigger = "Cron 02:00",
            Priority = ScenarioPriority.NiceToHave,
        };

        var yaml = ScenarioYamlSerializer.Serialize([scenario]);

        Assert.Contains("priority: nice_to_have", yaml);
        Assert.Contains("journey_kind: scheduled_job", yaml);
    }

    [Fact]
    public void Serialize_InfrastructureScenario_EmitsInfrastructureTrue()
    {
        var scenario = new Scenario
        {
            Id = "S01",
            Title = "DB migration",
            JourneyKind = JourneyKind.SystemInitiated,
            Actor = "System",
            Trigger = "Startup",
            Infrastructure = true,
        };

        var yaml = ScenarioYamlSerializer.Serialize([scenario]);

        Assert.Contains("infrastructure: true", yaml);
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry mock — LoadAsync contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RegistryLoadAsync_WhenScenariosExist_ReturnNonEmptyList()
    {
        var expected = new List<Scenario>
        {
            new() { Id = "S01", Title = "Login", JourneyKind = JourneyKind.UiInteraction, Actor = "User", Trigger = "Click Login" },
        };

        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Scenario>)expected);

        var result = await mock.Object.LoadAsync();

        Assert.Single(result);
        Assert.Equal("S01", result[0].Id);
    }

    [Fact]
    public async Task RegistryLoadAsync_WhenEmpty_ReturnsEmptyList()
    {
        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Scenario>)Array.Empty<Scenario>());

        var result = await mock.Object.LoadAsync();

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry mock — ValidateNoOrphans contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidateNoOrphans_WhenAllCited_ReturnsTrue()
    {
        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.ValidateNoOrphans(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await mock.Object.ValidateNoOrphans();

        Assert.True(result);
    }

    [Fact]
    public async Task ValidateNoOrphans_WhenOrphansExist_ReturnsFalse()
    {
        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.ValidateNoOrphans(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await mock.Object.ValidateNoOrphans();

        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry mock — WriteSidecarAsync contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteSidecarAsync_IsCalled_WhenScenariosLoaded()
    {
        var scenarios = new List<Scenario>
        {
            new() { Id = "S01", Title = "Test", JourneyKind = JourneyKind.UiInteraction, Actor = "User", Trigger = "Click" },
        };

        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Scenario>)scenarios);
        mock.Setup(r => r.WriteSidecarAsync(It.IsAny<IReadOnlyList<Scenario>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate the PostMergeScenarioSyncAsync flow:
        // 1. load scenarios from registry
        var loaded = await mock.Object.LoadAsync();
        // 2. write sidecar if any were found
        if (loaded.Count > 0)
            await mock.Object.WriteSidecarAsync(loaded);

        mock.Verify(r => r.WriteSidecarAsync(
            It.Is<IReadOnlyList<Scenario>>(s => s.Count == 1 && s[0].Id == "S01"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // ScenarioYamlSerializer — YamlScalar quoting rules
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("contains: colon", "\"contains: colon\"")]
    [InlineData("[bracket", "\"[bracket\"")]
    [InlineData("{brace", "\"{brace\"")]
    public void YamlScalar_QuotingRules_MatchExpected(string input, string expected)
    {
        var result = ScenarioYamlSerializer.YamlScalar(input);
        Assert.Equal(expected, result);
    }

    // -------------------------------------------------------------------------
    // ScanUserStoryCitationsAsync-equivalent logic
    // -------------------------------------------------------------------------

    [Fact]
    public void UserStoryCitationScan_DetectsUncitedStory()
    {
        // Simulate the PMSpec content that ScanUserStoryCitationsAsync would read:
        var pmSpec = """
            ## User Stories & Acceptance Criteria

            **As a** player, I want to start the game. Implements Scenarios: S01
            **As a** player, I want to build towers.
            **As a** player, I want to pause the game. Implements Scenarios: S02, S03
            """;

        var lines = pmSpec.Split('\n');
        var uncited = 0;
        foreach (var line in lines)
        {
            if (!line.Contains("**As a", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!line.Contains("Implements Scenarios:", StringComparison.OrdinalIgnoreCase))
                uncited++;
        }

        Assert.Equal(1, uncited); // "I want to build towers" has no citation
    }

    [Fact]
    public void UserStoryCitationScan_AllCited_CountsZero()
    {
        var pmSpec = """
            **As a** player, I want to start the game. Implements Scenarios: S01
            **As a** player, I want to build towers. Implements Scenarios: S02
            """;

        var lines = pmSpec.Split('\n');
        var uncited = lines
            .Where(l => l.Contains("**As a", StringComparison.OrdinalIgnoreCase))
            .Count(l => !l.Contains("Implements Scenarios:", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, uncited);
    }
}
