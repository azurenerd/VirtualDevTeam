using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Scenarios;

/// <summary>
/// Tests for <see cref="ScenarioYamlExtractor"/>.
/// </summary>
public sealed class ScenarioYamlExtractorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string MinimalScenarioYaml(
        string id = "S01",
        string title = "Player starts game",
        string journeyKind = "ui_interaction",
        string actor = "Player",
        string trigger = "User clicks Play") =>
        $"""
         - id: {id}
           title: "{title}"
           journey_kind: {journeyKind}
           actor: "{actor}"
           trigger: "{trigger}"
         """;

    // -------------------------------------------------------------------------
    // ExtractFromPmSpecMarkdown
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractFromPmSpecMarkdown_NoBlock_ReturnsEmpty()
    {
        var pmSpec = """
            ## User Stories
            - Player can start a new game
            ## Architecture
            Some arch text.
            """;

        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_EmptyBlock_ReturnsEmpty()
    {
        var pmSpec = """
            ## Scenarios
            Some narrative here.
            # scenarios
            ## Next Section
            """;

        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_FullBlock_ParsesCorrectly()
    {
        var pmSpec = $"""
            ## Scenarios
            Some narrative about scenarios.
            # scenarios
            {MinimalScenarioYaml()}
            ## User Stories
            """;

        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec);

        Assert.Single(result);
        var s = result[0];
        Assert.Equal("S01", s.Id);
        Assert.Equal("Player starts game", s.Title);
        Assert.Equal(JourneyKind.UiInteraction, s.JourneyKind);
        Assert.Equal("Player", s.Actor);
        Assert.Equal("User clicks Play", s.Trigger);
        Assert.Equal(ScenarioPriority.Important, s.Priority);  // default
        Assert.Equal(ScenarioStatus.Proposed, s.Status);       // default
        Assert.Equal(VerificationStatus.NotYetVerified, s.VerificationStatus); // default
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_WithCodeFence_ParsesCorrectly()
    {
        var pmSpec = $"""
            ## Scenarios
            ```yaml
            # scenarios
            {MinimalScenarioYaml("S02", "Player builds tower")}
            ```
            ## Next Section
            """;

        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec);

        Assert.Single(result);
        Assert.Equal("S02", result[0].Id);
        Assert.Equal("Player builds tower", result[0].Title);
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_MultipleBlocks_UsesFirstAndDoesNotThrow()
    {
        var pmSpec = $"""
            # scenarios
            {MinimalScenarioYaml("S01", "First scenario")}
            ## Middle Section
            # scenarios
            {MinimalScenarioYaml("S02", "Second scenario")}
            """;

        // Should not throw; just use first block and warn
        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec, NullLogger.Instance);

        // Result should be from the FIRST block (S01)
        Assert.Single(result);
        Assert.Equal("S01", result[0].Id);
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_MalformedYaml_SkipsInvalidScenario()
    {
        // Missing required fields → should skip gracefully, not throw
        var pmSpec = """
            # scenarios
            - id: S01
              title: "Missing actor and trigger"
              journey_kind: api_call
            """;

        // No actor/trigger → scenario should be skipped, not an exception
        var result = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec, NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractFromPmSpecMarkdown_InvalidJourneyKind_Throws()
    {
        var pmSpec = """
            # scenarios
            - id: S01
              title: "Bad kind"
              journey_kind: not_a_valid_kind
              actor: "Player"
              trigger: "Something"
            """;

        Assert.Throws<ScenarioParseException>(() =>
            ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpec));
    }

    // -------------------------------------------------------------------------
    // ExtractFromYamlString — all JourneyKind values
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("ui_interaction", JourneyKind.UiInteraction)]
    [InlineData("api_call", JourneyKind.ApiCall)]
    [InlineData("scheduled_job", JourneyKind.ScheduledJob)]
    [InlineData("event_arrival", JourneyKind.EventArrival)]
    [InlineData("webhook", JourneyKind.Webhook)]
    [InlineData("message_consume", JourneyKind.MessageConsume)]
    [InlineData("cli_invocation", JourneyKind.CliInvocation)]
    [InlineData("system_initiated", JourneyKind.SystemInitiated)]
    [InlineData("data_pipeline", JourneyKind.DataPipeline)]
    public void ExtractFromYamlString_AllJourneyKinds_ParsesCorrectly(string yamlKind, JourneyKind expectedKind)
    {
        var yaml = MinimalScenarioYaml(journeyKind: yamlKind);

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        Assert.Equal(expectedKind, result[0].JourneyKind);
    }

    [Fact]
    public void ExtractFromYamlString_InvalidJourneyKind_Throws()
    {
        var yaml = MinimalScenarioYaml(journeyKind: "flying_machine");

        var ex = Assert.Throws<ScenarioParseException>(() =>
            ScenarioYamlExtractor.ExtractFromYamlString(yaml));
        Assert.Contains("journey_kind", ex.Message);
        Assert.Contains("flying_machine", ex.Message);
    }

    // -------------------------------------------------------------------------
    // ExtractFromYamlString — observation surfaces
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractFromYamlString_ObservationSurfaces_ParsedCorrectly()
    {
        var yaml = """
            - id: S01
              title: "Test surfaces"
              journey_kind: ui_interaction
              actor: "Player"
              trigger: "Click"
              observation_surfaces:
                - kind: dom_query
                  selector: ".tower"
                - kind: event_bus
                  event_name: "tower:placed"
                - kind: http_response
                  status: "200"
            """;

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        var surfaces = result[0].ObservationSurfaces;
        Assert.Equal(3, surfaces.Count);

        Assert.Equal("dom_query", surfaces[0].Kind);
        Assert.Equal(".tower", surfaces[0].Fields["selector"]);

        Assert.Equal("event_bus", surfaces[1].Kind);
        Assert.Equal("tower:placed", surfaces[1].Fields["event_name"]);

        Assert.Equal("http_response", surfaces[2].Kind);
        Assert.Equal("200", surfaces[2].Fields["status"]);
    }

    // -------------------------------------------------------------------------
    // ExtractFromYamlString — default values for optional fields
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractFromYamlString_DefaultValues_UsedForMissingOptionalFields()
    {
        var yaml = MinimalScenarioYaml();

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        var s = result[0];
        Assert.Empty(s.Preconditions);
        Assert.Empty(s.Steps);
        Assert.Empty(s.ExpectedTerminalState);
        Assert.Empty(s.ObservationSurfaces);
        Assert.Empty(s.SubsystemsInvolved);
        Assert.Empty(s.ImplementingTasks);
        Assert.Equal(ScenarioPriority.Important, s.Priority);
        Assert.Equal(ScenarioStatus.Proposed, s.Status);
        Assert.Equal(VerificationStatus.NotYetVerified, s.VerificationStatus);
        Assert.Null(s.VerificationEvidenceUrl);
        Assert.False(s.Infrastructure);
    }

    [Fact]
    public void ExtractFromYamlString_AllOptionalListFields_ParsedCorrectly()
    {
        var yaml = """
            - id: S03
              title: "Full scenario"
              journey_kind: webhook
              actor: "Stripe webhook"
              trigger: "POST /webhooks/stripe"
              preconditions:
                - "Invoice exists"
                - "Customer linked"
              steps:
                - "1. Stripe POSTs payload"
                - "2. Service validates signature"
              expected_terminal_state:
                - "HTTP 200"
                - "DB row updated"
              subsystems_involved:
                - webhook-router
                - invoice-repository
              implementing_tasks:
                - "T05: Webhook handler"
              priority: critical
              status: approved
              verification_status: verified
              infrastructure: false
            """;

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        var s = result[0];
        Assert.Equal(new[] { "Invoice exists", "Customer linked" }, s.Preconditions);
        Assert.Equal(2, s.Steps.Count);
        Assert.Equal(2, s.ExpectedTerminalState.Count);
        Assert.Equal(new[] { "webhook-router", "invoice-repository" }, s.SubsystemsInvolved);
        Assert.Equal(new[] { "T05: Webhook handler" }, s.ImplementingTasks);
        Assert.Equal(ScenarioPriority.Critical, s.Priority);
        Assert.Equal(ScenarioStatus.Approved, s.Status);
        Assert.Equal(VerificationStatus.Verified, s.VerificationStatus);
        Assert.False(s.Infrastructure);
    }

    [Fact]
    public void ExtractFromYamlString_InfrastructureFlag_ParsedCorrectly()
    {
        var yaml = """
            - id: S99
              title: "DB migration"
              journey_kind: system_initiated
              actor: "Deployment pipeline"
              trigger: "New version deployed"
              infrastructure: true
            """;

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        Assert.True(result[0].Infrastructure);
    }

    [Fact]
    public void ExtractFromYamlString_NullEvidenceUrl_ParsedAsNull()
    {
        var yaml = """
            - id: S01
              title: "Test"
              journey_kind: api_call
              actor: "Caller"
              trigger: "POST /api/test"
              verification_evidence_url: null
            """;

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        Assert.Null(result[0].VerificationEvidenceUrl);
    }

    [Fact]
    public void ExtractFromYamlString_NiceToHavePriority_HyphenVariant_ParsedCorrectly()
    {
        var yaml = MinimalScenarioYaml() + "\n  priority: nice-to-have";

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Single(result);
        Assert.Equal(ScenarioPriority.NiceToHave, result[0].Priority);
    }

    [Fact]
    public void ExtractFromYamlString_MultipleScenarios_AllParsed()
    {
        var yaml = $"""
            {MinimalScenarioYaml("S01", "First")}
            {MinimalScenarioYaml("S02", "Second", "api_call")}
            {MinimalScenarioYaml("S03", "Third", "cli_invocation")}
            """;

        var result = ScenarioYamlExtractor.ExtractFromYamlString(yaml);

        Assert.Equal(3, result.Count);
        Assert.Equal("S01", result[0].Id);
        Assert.Equal("S02", result[1].Id);
        Assert.Equal("S03", result[2].Id);
        Assert.Equal(JourneyKind.ApiCall, result[1].JourneyKind);
        Assert.Equal(JourneyKind.CliInvocation, result[2].JourneyKind);
    }

    // -------------------------------------------------------------------------
    // ParseJourneyKind internal helper (accessible via InternalsVisibleTo)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseJourneyKind_InvalidValue_ThrowsWithHelpfulMessage()
    {
        var ex = Assert.Throws<ScenarioParseException>(() =>
            ScenarioYamlExtractor.ParseJourneyKind("something_weird"));

        Assert.Contains("something_weird", ex.Message);
        Assert.Contains("Allowed:", ex.Message);
    }
}
