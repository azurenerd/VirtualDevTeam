using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Scenarios;

/// <summary>
/// Tests for <see cref="ScenarioRegistry"/>.
/// </summary>
public sealed class ScenarioRegistryTests
{
    // -------------------------------------------------------------------------
    // Test fixture helpers
    // -------------------------------------------------------------------------

    private readonly Mock<IRepositoryContentService> _repo = new();
    private readonly ProjectFileManager _fileManager;
    private readonly ScenarioRegistry _registry;

    public ScenarioRegistryTests()
    {
        _fileManager = new ProjectFileManager(
            _repo.Object,
            NullLogger<ProjectFileManager>.Instance,
            branch: "main");

        _registry = new ScenarioRegistry(
            _fileManager,
            NullLogger<ScenarioRegistry>.Instance);
    }

    private static readonly string SampleYaml = """
        - id: S01
          title: "Player starts game"
          journey_kind: ui_interaction
          actor: "Player"
          trigger: "User clicks Play"
          priority: critical
          status: approved
        - id: S02
          title: "Player builds tower"
          journey_kind: ui_interaction
          actor: "Player"
          trigger: "User clicks Build Tower"
          priority: important
          status: approved
        """;

    private static readonly string PmSpecWithBlock = $"""
        ## PM Specification

        ## User Stories
        - As a player I want to start the game. Implements Scenarios: S01
        - As a player I want to build a tower. Implements Scenarios: S02

        # scenarios
        {SampleYaml}
        ## Architecture Notes
        """;

    private void SetupFileContent(string path, string? content) =>
        _repo.Setup(r => r.GetFileContentAsync(path, "main", It.IsAny<CancellationToken>()))
             .ReturnsAsync(content);

    private void SetupNullFile(string path) =>
        _repo.Setup(r => r.GetFileContentAsync(path, "main", It.IsAny<CancellationToken>()))
             .ReturnsAsync((string?)null);

    // -------------------------------------------------------------------------
    // LoadAsync — Scenarios.md preferred
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_WithScenariosYaml_LoadsFromScenariosFile()
    {
        SetupFileContent("Scenarios.md", SampleYaml);
        SetupNullFile("scenarios.json");

        var result = await _registry.LoadAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("S01", result[0].Id);
        Assert.Equal("S02", result[1].Id);
    }

    [Fact]
    public async Task LoadAsync_NoScenariosYaml_FallsBackToPMSpec()
    {
        SetupNullFile("Scenarios.md");
        SetupNullFile("scenarios.json");
        SetupFileContent("PMSpec.md", PmSpecWithBlock);

        var result = await _registry.LoadAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("S01", result[0].Id);
    }

    [Fact]
    public async Task LoadAsync_NoPMSpec_ReturnsEmpty()
    {
        SetupNullFile("Scenarios.md");
        SetupNullFile("scenarios.json");
        // PMSpec returns a placeholder (not null) but no # scenarios block
        SetupFileContent("PMSpec.md", "# PM Specification\n\n_No PM specification has been created yet._");

        var result = await _registry.LoadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadAsync_UpdatesCurrent()
    {
        SetupFileContent("Scenarios.md", SampleYaml);
        SetupNullFile("scenarios.json");

        await _registry.LoadAsync();

        Assert.Equal(2, _registry.Current.Count);
    }

    [Fact]
    public async Task LoadAsync_RaisesChangedEvent()
    {
        SetupFileContent("Scenarios.md", SampleYaml);
        SetupNullFile("scenarios.json");

        ScenarioRegistryChangedEventArgs? raised = null;
        _registry.Changed += (_, e) => raised = e;

        await _registry.LoadAsync();

        Assert.NotNull(raised);
        Assert.Equal(2, raised!.Scenarios.Count);
    }

    // -------------------------------------------------------------------------
    // Current / Critical / FindById
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Critical_ReturnsOnlyCriticalPriorityScenarios()
    {
        SetupFileContent("Scenarios.md", SampleYaml); // S01=critical, S02=important
        SetupNullFile("scenarios.json");

        await _registry.LoadAsync();

        Assert.Single(_registry.Critical);
        Assert.Equal("S01", _registry.Critical[0].Id);
    }

    [Fact]
    public async Task FindById_ExistingId_ReturnsScenario()
    {
        SetupFileContent("Scenarios.md", SampleYaml);
        SetupNullFile("scenarios.json");

        await _registry.LoadAsync();

        var s = _registry.FindById("S02");
        Assert.NotNull(s);
        Assert.Equal("Player builds tower", s!.Title);
    }

    [Fact]
    public async Task FindById_NonExistingId_ReturnsNull()
    {
        SetupFileContent("Scenarios.md", SampleYaml);
        SetupNullFile("scenarios.json");

        await _registry.LoadAsync();

        Assert.Null(_registry.FindById("S99"));
    }

    [Fact]
    public void Current_BeforeFirstLoad_IsEmpty()
    {
        Assert.Empty(_registry.Current);
    }

    // -------------------------------------------------------------------------
    // WriteSidecarAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WriteSidecarAsync_WritesJsonToScopedPath()
    {
        var scenarios = new List<Scenario>
        {
            new() { Id = "S01", Title = "Test", JourneyKind = JourneyKind.ApiCall, Actor = "Caller", Trigger = "POST /test" },
        };

        string? writtenContent = null;
        _repo.Setup(r => r.CreateOrUpdateFileAsync(
                "scenarios.json",
                It.IsAny<string>(),
                It.IsAny<string>(),
                "main",
                It.IsAny<CancellationToken>()))
             .Callback<string, string, string, string, CancellationToken>(
                (_, content, _, _, _) => writtenContent = content)
             .Returns(Task.CompletedTask);

        await _registry.WriteSidecarAsync(scenarios);

        Assert.NotNull(writtenContent);
        Assert.Contains("S01", writtenContent);
        Assert.Contains("api_call", writtenContent); // enum serialized as snake_case
    }

    [Fact]
    public async Task WriteSidecarAsync_UpdatesCurrentAndRaisesEvent()
    {
        var scenarios = new List<Scenario>
        {
            new() { Id = "S01", Title = "Test", JourneyKind = JourneyKind.Webhook, Actor = "Stripe", Trigger = "POST" },
        };

        _repo.Setup(r => r.CreateOrUpdateFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        ScenarioRegistryChangedEventArgs? raised = null;
        _registry.Changed += (_, e) => raised = e;

        await _registry.WriteSidecarAsync(scenarios);

        Assert.Single(_registry.Current);
        Assert.NotNull(raised);
        Assert.Single(raised!.Scenarios);
    }

    // -------------------------------------------------------------------------
    // ValidateNoOrphans
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ValidateNoOrphans_AllCited_ReturnsTrue()
    {
        SetupFileContent("Scenarios.md", SampleYaml); // S01, S02
        SetupNullFile("scenarios.json");
        // PMSpec cites both
        SetupFileContent("PMSpec.md", PmSpecWithBlock);

        await _registry.LoadAsync();
        var valid = await _registry.ValidateNoOrphans();

        Assert.True(valid);
    }

    [Fact]
    public async Task ValidateNoOrphans_OrphanScenario_ReturnsFalse()
    {
        SetupFileContent("Scenarios.md", SampleYaml); // S01, S02
        SetupNullFile("scenarios.json");

        // PMSpec only cites S01 — S02 is orphaned
        const string pmSpecOnlyS01 = """
            ## User Stories
            - As a player I want to start the game. Implements Scenarios: S01
            ## Architecture
            """;
        SetupFileContent("PMSpec.md", pmSpecOnlyS01);

        await _registry.LoadAsync();
        var valid = await _registry.ValidateNoOrphans();

        Assert.False(valid);
    }

    [Fact]
    public async Task ValidateNoOrphans_InfrastructureScenario_Exempt()
    {
        const string yaml = """
            - id: S01
              title: "Normal scenario"
              journey_kind: api_call
              actor: "Caller"
              trigger: "POST /api"
              status: approved
            - id: S99
              title: "DB migration"
              journey_kind: system_initiated
              actor: "Pipeline"
              trigger: "Deploy"
              infrastructure: true
            """;

        SetupFileContent("Scenarios.md", yaml);
        SetupNullFile("scenarios.json");

        // PMSpec cites S01 but NOT S99 (infrastructure — exempt)
        const string pmSpec = """
            ## User Stories
            - Story 1. Implements Scenarios: S01
            """;
        SetupFileContent("PMSpec.md", pmSpec);

        await _registry.LoadAsync();
        var valid = await _registry.ValidateNoOrphans();

        Assert.True(valid);
    }

    [Fact]
    public async Task ValidateNoOrphans_NoScenarios_ReturnsTrue()
    {
        // Registry never loaded — no scenarios
        var valid = await _registry.ValidateNoOrphans();
        Assert.True(valid);
    }

    // -------------------------------------------------------------------------
    // Drift detection — verified via Critical log (indirect check via LoadAsync not throwing)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_DriftBetweenSidecarAndPMSpec_DoesNotThrow()
    {
        // The sidecar has S01; PMSpec # scenarios block has S01 + S02 → drift
        const string sidecarJson = """
            [
              {
                "id": "S01",
                "title": "Player starts game",
                "journey_kind": "ui_interaction",
                "actor": "Player",
                "trigger": "Click Play"
              }
            ]
            """;

        SetupNullFile("Scenarios.md");
        SetupFileContent("scenarios.json", sidecarJson);
        SetupFileContent("PMSpec.md", PmSpecWithBlock); // Has S01 + S02

        // Should complete without throwing even though there's drift
        var result = await _registry.LoadAsync();

        Assert.NotEmpty(result);
    }

    // -------------------------------------------------------------------------
    // JSON round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void JsonSerializer_RoundTrip_PreservesAllFields()
    {
        var scenarios = new List<Scenario>
        {
            new()
            {
                Id = "S01",
                Title = "Stripe webhook",
                JourneyKind = JourneyKind.Webhook,
                Actor = "Stripe",
                Trigger = "POST /webhooks/stripe",
                Priority = ScenarioPriority.Critical,
                Status = ScenarioStatus.Approved,
                VerificationStatus = VerificationStatus.Verified,
                Infrastructure = false,
                VerificationEvidenceUrl = "https://example.com/artifact",
                Preconditions = new[] { "Invoice exists" },
                Steps = new[] { "1. POST arrives", "2. Validated" },
                ExpectedTerminalState = new[] { "HTTP 200" },
                ObservationSurfaces = new[]
                {
                    new ObservationSurface
                    {
                        Kind = "http_response",
                        Fields = new Dictionary<string, string> { ["status"] = "200" },
                    },
                },
                SubsystemsInvolved = new[] { "webhook-router" },
                ImplementingTasks = new[] { "T05: Handler" },
            },
        };

        var json = ScenarioJsonSerializer.Serialize(scenarios);
        var deserialized = ScenarioJsonSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!);
        var s = deserialized[0];
        Assert.Equal("S01", s.Id);
        Assert.Equal(JourneyKind.Webhook, s.JourneyKind);
        Assert.Equal(ScenarioPriority.Critical, s.Priority);
        Assert.Equal(ScenarioStatus.Approved, s.Status);
        Assert.Equal(VerificationStatus.Verified, s.VerificationStatus);
        Assert.Equal("https://example.com/artifact", s.VerificationEvidenceUrl);
        Assert.Equal("webhook-router", s.SubsystemsInvolved[0]);
        Assert.Single(s.ObservationSurfaces);
        Assert.Equal("http_response", s.ObservationSurfaces[0].Kind);
        Assert.Equal("200", s.ObservationSurfaces[0].Fields["status"]);
    }

    [Fact]
    public void JsonSerializer_Deserialize_NullInput_ReturnsNull()
    {
        Assert.Null(ScenarioJsonSerializer.Deserialize(null));
        Assert.Null(ScenarioJsonSerializer.Deserialize(""));
        Assert.Null(ScenarioJsonSerializer.Deserialize("   "));
    }
}
