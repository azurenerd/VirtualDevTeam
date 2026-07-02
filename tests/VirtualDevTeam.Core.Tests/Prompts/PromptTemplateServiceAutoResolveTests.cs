using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace VirtualDevTeam.Core.Tests.Prompts;

/// <summary>
/// Tests that <see cref="PromptTemplateService"/> auto-resolves the four well-known variables
/// (project_description, scenarios_yaml_block, approved_scenarios_yaml, scenarios_json) when
/// the caller does not include them in the variable dictionary.
/// </summary>
public class PromptTemplateServiceAutoResolveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDevTeamConfig _runtimeConfig;

    private static readonly Scenario SampleScenario = new()
    {
        Id = "S01",
        Title = "User logs in",
        JourneyKind = JourneyKind.UiInteraction,
        Actor = "User",
        Trigger = "User submits login form",
        Priority = ScenarioPriority.Critical,
        Status = ScenarioStatus.Approved,
        Steps = ["Navigate to /login", "Enter credentials", "Click submit"],
        ExpectedTerminalState = ["Dashboard is shown"],
    };

    public PromptTemplateServiceAutoResolveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"prompt-autoresolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _runtimeConfig = new VirtualDevTeamConfig
        {
            Prompts = new PromptsConfig { BasePath = _tempDir, HotReload = false, MaxIncludeDepth = 10 },
            Project = new ProjectConfig { Description = "A tower-defence game built with .NET" },
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void WriteTemplate(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private PromptTemplateService BuildService(IScenarioRegistry? registry = null) =>
        new(Options.Create(_runtimeConfig),
            NullLogger<PromptTemplateService>.Instance,
            registry);

    private static Mock<IScenarioRegistry> RegistryWith(params Scenario[] scenarios)
    {
        var mock = new Mock<IScenarioRegistry>();
        mock.Setup(r => r.Current).Returns(scenarios.ToList().AsReadOnly());
        return mock;
    }

    // -----------------------------------------------------------------------
    // project_description auto-fill
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenderAsync_AutoFills_ProjectDescription_WhenNotInCallerVars()
    {
        WriteTemplate("test/desc.md", "Project: {{project_description}}");
        var svc = BuildService();

        var result = await svc.RenderAsync("test/desc", []);

        Assert.Equal("Project: A tower-defence game built with .NET", result);
    }

    [Fact]
    public async Task RenderAsync_CallerDescription_Wins_OverAutoFill()
    {
        WriteTemplate("test/desc2.md", "Project: {{project_description}}");
        var svc = BuildService();

        var result = await svc.RenderAsync("test/desc2",
            new Dictionary<string, string> { ["project_description"] = "Caller override" });

        Assert.Equal("Project: Caller override", result);
    }

    [Fact]
    public async Task RenderAsync_EmptyDescription_DoesNotAutoFill()
    {
        _runtimeConfig.Project.Description = "";
        WriteTemplate("test/desc3.md", "Project: {{project_description}}");
        var svc = BuildService();

        var result = await svc.RenderAsync("test/desc3", []);

        // Variable not found → left as-is (existing undefined-variable behaviour)
        Assert.Equal("Project: {{project_description}}", result);
    }

    // -----------------------------------------------------------------------
    // scenarios_yaml_block / approved_scenarios_yaml auto-fill
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenderAsync_AutoFills_ScenariosYamlBlock_WhenNotInCallerVars()
    {
        WriteTemplate("test/syb.md", "Scenarios:\n{{scenarios_yaml_block}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/syb", []);

        Assert.NotNull(result);
        Assert.Contains("id: S01", result);
        Assert.Contains("User logs in", result);
    }

    [Fact]
    public async Task RenderAsync_AutoFills_ApprovedScenariosYaml_WhenNotInCallerVars()
    {
        WriteTemplate("test/asy.md", "Approved:\n{{approved_scenarios_yaml}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/asy", []);

        Assert.NotNull(result);
        Assert.Contains("id: S01", result);
    }

    [Fact]
    public async Task RenderAsync_ScenariosYamlBlock_And_ApprovedScenariosYaml_AreIdentical()
    {
        WriteTemplate("test/both.md", "Block:{{scenarios_yaml_block}}\nApproved:{{approved_scenarios_yaml}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/both", []);

        Assert.NotNull(result);
        // Split on the two sentinels to extract each value
        var afterBlock   = result!["Block:".Length..];
        var approvedIdx  = afterBlock.IndexOf("\nApproved:", StringComparison.Ordinal);
        var blockValue   = afterBlock[..approvedIdx];
        var approvedValue = afterBlock[(approvedIdx + "\nApproved:".Length)..];
        Assert.Equal(blockValue, approvedValue);
    }

    [Fact]
    public async Task RenderAsync_CallerScenariosYaml_Wins_OverAutoFill()
    {
        WriteTemplate("test/csyb.md", "Block:{{scenarios_yaml_block}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/csyb",
            new Dictionary<string, string> { ["scenarios_yaml_block"] = "custom-yaml" });

        Assert.Equal("Block:custom-yaml", result);
    }

    [Fact]
    public async Task RenderAsync_NoRegistry_LeavesScenarioVarsAsIs()
    {
        WriteTemplate("test/noreg.md", "{{scenarios_yaml_block}} {{approved_scenarios_yaml}} {{scenarios_json}}");
        var svc = BuildService(registry: null);

        var result = await svc.RenderAsync("test/noreg", []);

        // Without a registry the variables remain unresolved (existing undefined-var behaviour)
        Assert.Equal("{{scenarios_yaml_block}} {{approved_scenarios_yaml}} {{scenarios_json}}", result);
    }

    // -----------------------------------------------------------------------
    // scenarios_json auto-fill
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenderAsync_AutoFills_ScenariosJson_WhenNotInCallerVars()
    {
        WriteTemplate("test/sj.md", "JSON:{{scenarios_json}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/sj", []);

        Assert.NotNull(result);
        Assert.Contains("\"id\":", result);   // JSON property name (snake_case)
        Assert.Contains("S01", result);
    }

    [Fact]
    public async Task RenderAsync_CallerScenariosJson_Wins_OverAutoFill()
    {
        WriteTemplate("test/csj.md", "JSON:{{scenarios_json}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/csj",
            new Dictionary<string, string> { ["scenarios_json"] = "[]" });

        Assert.Equal("JSON:[]", result);
    }

    // -----------------------------------------------------------------------
    // Round-trip: YAML produced by auto-fill is parseable by ScenarioYamlExtractor
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AutoFilledYaml_IsRoundTrippable_ByScenarioYamlExtractor()
    {
        WriteTemplate("test/rt.md", "{{scenarios_yaml_block}}");
        var registry = RegistryWith(SampleScenario);
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/rt", []);

        var parsed = ScenarioYamlExtractor.ExtractFromYamlString(result!);
        Assert.Single(parsed);
        var s = parsed[0];
        Assert.Equal("S01", s.Id);
        Assert.Equal("User logs in", s.Title);
        Assert.Equal(JourneyKind.UiInteraction, s.JourneyKind);
        Assert.Equal(ScenarioPriority.Critical, s.Priority);
        Assert.Equal(ScenarioStatus.Approved, s.Status);
    }

    // -----------------------------------------------------------------------
    // Empty registry produces empty string (doesn't break template)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenderAsync_EmptyRegistry_YieldsEmptyStringForScenarioVars()
    {
        WriteTemplate("test/empty-reg.md", "Before|{{scenarios_yaml_block}}|After");
        var registry = RegistryWith(); // no scenarios
        var svc = BuildService(registry.Object);

        var result = await svc.RenderAsync("test/empty-reg", []);

        // ScenarioYamlSerializer returns "" for empty list → variable substituted to ""
        Assert.Equal("Before||After", result);
    }

    // -----------------------------------------------------------------------
    // Existing non-scenario callers are unaffected
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenderAsync_WithoutWellKnownVarsInTemplate_ReturnsCorrectly()
    {
        WriteTemplate("test/plain.md", "Hello {{name}}!");
        var svc = BuildService();
        var vars = new Dictionary<string, string> { ["name"] = "World" };

        var result = await svc.RenderAsync("test/plain", vars);

        Assert.Equal("Hello World!", result);
    }
}
