using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Scenarios;

/// <summary>
/// Tests for <see cref="SmokeTestGenerator"/>.
/// </summary>
public sealed class SmokeTestGeneratorTests
{
    private readonly SmokeTestGenerator _generator =
        new(NullLogger<SmokeTestGenerator>.Instance);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Scenario MakeUiScenario(string id = "S03", string title = "Player builds first tower") =>
        new()
        {
            Id = id,
            Title = title,
            JourneyKind = JourneyKind.UiInteraction,
            Actor = "Player",
            Trigger = "User clicks 'Build Tower' button",
            Preconditions = ["S01 has completed (game has started)", "Player has ≥ 100 gold"],
            Steps =
            [
                "1. Player clicks on empty tile in playfield",
                "2. Tower placement preview appears",
                "3. Player clicks 'Confirm'",
                "4. Tower sprite renders at chosen tile",
                "5. Gold counter decreases by tower cost",
            ],
            ExpectedTerminalState =
            [
                "DOM contains <tower-sprite> at clicked tile coordinates",
                "Gold counter element shows new value",
                "EventBus has fired 'tower:placed' event",
            ],
            ObservationSurfaces =
            [
                new ObservationSurface
                {
                    Kind = "dom_query",
                    Fields = new Dictionary<string, string> { ["selector"] = ".tower-sprite[data-tile='5,7']" },
                },
                new ObservationSurface
                {
                    Kind = "dom_text",
                    Fields = new Dictionary<string, string>
                    {
                        ["selector"] = ".hud-gold",
                        ["expected_change"] = "decreased_by_cost",
                    },
                },
                new ObservationSurface
                {
                    Kind = "event_bus",
                    Fields = new Dictionary<string, string> { ["event_name"] = "tower:placed" },
                },
            ],
        };

    private static Scenario MakeApiScenario() =>
        new()
        {
            Id = "S08",
            Title = "Stripe webhook marks invoice paid",
            JourneyKind = JourneyKind.Webhook,
            Actor = "Stripe webhook",
            Trigger = "POST /webhooks/stripe with charge.succeeded payload",
            Steps =
            [
                "1. Stripe POSTs charge.succeeded payload to /webhooks/stripe",
                "2. Service validates Stripe-Signature header",
                "3. Service transitions invoice from 'pending' to 'paid'",
                "4. Service responds 200 OK to Stripe within 5s",
            ],
            ExpectedTerminalState = ["HTTP response: 200 OK within 5000ms"],
            ObservationSurfaces =
            [
                new ObservationSurface
                {
                    Kind = "http_response",
                    Fields = new Dictionary<string, string>
                    {
                        ["status"] = "200",
                        ["max_latency_ms"] = "5000",
                    },
                },
                new ObservationSurface
                {
                    Kind = "db_row",
                    Fields = new Dictionary<string, string>
                    {
                        ["query"] = "SELECT status FROM invoices WHERE id='INV-123'",
                        ["expected"] = "paid",
                    },
                },
            ],
        };

    private static Scenario MakeCliScenario() =>
        new()
        {
            Id = "S04",
            Title = "Operator uploads CSV via CLI",
            JourneyKind = JourneyKind.CliInvocation,
            Actor = "CLI user",
            Trigger = "myapp upload --file=customers.csv --tenant=acme",
            Steps =
            [
                "1. CLI parses arguments",
                "2. CLI authenticates against API",
                "3. CLI exits with code 0",
            ],
            ExpectedTerminalState = ["Exit code: 0", "Stdout contains: 'Uploaded N rows successfully'"],
            ObservationSurfaces =
            [
                new ObservationSurface
                {
                    Kind = "process_exit_code",
                    Fields = new Dictionary<string, string> { ["expected"] = "0" },
                },
                new ObservationSurface
                {
                    Kind = "stdout_pattern",
                    Fields = new Dictionary<string, string> { ["regex"] = "Uploaded \\d+ rows successfully" },
                },
            ],
        };

    // -------------------------------------------------------------------------
    // Empty input
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_EmptyList_ReturnsEmptyDictionary()
    {
        var result = _generator.Generate([]);
        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // UI scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_UiScenario_ProducesExpectedFilePath()
    {
        var result = _generator.Generate([MakeUiScenario()]);

        Assert.Contains("tests/scenarios/S03_player-builds-first-tower.spec.ts", result.Keys,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_UiScenario_ContainsTestDeclaration()
    {
        var result = _generator.Generate([MakeUiScenario()]);
        var content = result.Values.First(v => v.Contains("S03:"));

        Assert.Contains("test('S03:", content);
    }

    [Fact]
    public void Generate_UiScenario_ContainsDomQueryAssertion()
    {
        var result = _generator.Generate([MakeUiScenario()]);
        var content = result.Values.First(v => v.Contains("S03:"));

        Assert.Contains("toBeVisible", content);
        Assert.Contains(".tower-sprite", content);
    }

    [Fact]
    public void Generate_UiScenario_ContainsEventBusSurfaceComment()
    {
        var result = _generator.Generate([MakeUiScenario()]);
        var content = result.Values.First(v => v.Contains("S03:"));

        Assert.Contains("tower:placed", content);
        Assert.Contains("event_bus", content);
    }

    [Fact]
    public void Generate_UiScenario_ContainsStepsAsComments()
    {
        var result = _generator.Generate([MakeUiScenario()]);
        var content = result.Values.First(v => v.Contains("S03:"));

        Assert.Contains("// 1. Player clicks on empty tile", content);
        Assert.Contains("// 2. Tower placement preview appears", content);
    }

    [Fact]
    public void Generate_UiScenario_ContainsPreconditionsAsComments()
    {
        var result = _generator.Generate([MakeUiScenario()]);
        var content = result.Values.First(v => v.Contains("S03:"));

        Assert.Contains("S01 has completed", content);
    }

    [Fact]
    public void Generate_UiScenario_IncludesPlaywrightConfig()
    {
        var result = _generator.Generate([MakeUiScenario()]);

        Assert.Contains("tests/scenarios/playwright.config.ts", result.Keys,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_UiScenario_PlaywrightConfigContainsDefineConfig()
    {
        var result = _generator.Generate([MakeUiScenario()]);

        Assert.Contains("defineConfig", result["tests/scenarios/playwright.config.ts"]);
    }

    // -------------------------------------------------------------------------
    // API / Webhook scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_ApiScenario_ContainsRequestPost()
    {
        var result = _generator.Generate([MakeApiScenario()]);
        var content = result.Values.First(v => v.Contains("S08:"));

        Assert.Contains("request.post(", content);
    }

    [Fact]
    public void Generate_ApiScenario_ContainsStatusAssertion()
    {
        var result = _generator.Generate([MakeApiScenario()]);
        var content = result.Values.First(v => v.Contains("S08:"));

        Assert.Contains("expect(response.status()).toBe(200)", content);
    }

    [Fact]
    public void Generate_ApiScenario_ContainsLatencyComment()
    {
        var result = _generator.Generate([MakeApiScenario()]);
        var content = result.Values.First(v => v.Contains("S08:"));

        Assert.Contains("max_latency_ms: 5000", content);
    }

    [Fact]
    public void Generate_ApiScenario_ContainsDbRowSurfaceComment()
    {
        var result = _generator.Generate([MakeApiScenario()]);
        var content = result.Values.First(v => v.Contains("S08:"));

        Assert.Contains("db_row", content);
    }

    // -------------------------------------------------------------------------
    // CLI scenario
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_CliScenario_ContainsExecSync()
    {
        var result = _generator.Generate([MakeCliScenario()]);
        var content = result.Values.First(v => v.Contains("S04:"));

        Assert.Contains("execSync", content);
    }

    [Fact]
    public void Generate_CliScenario_ContainsChildProcessImport()
    {
        var result = _generator.Generate([MakeCliScenario()]);
        var content = result.Values.First(v => v.Contains("S04:"));

        Assert.Contains("child_process", content);
    }

    [Fact]
    public void Generate_CliScenario_ContainsExitCodeAssertion()
    {
        var result = _generator.Generate([MakeCliScenario()]);
        var content = result.Values.First(v => v.Contains("S04:"));

        Assert.Contains("expect(exitCode).toBe(0)", content);
    }

    [Fact]
    public void Generate_CliScenario_ContainsStdoutPatternAssertion()
    {
        var result = _generator.Generate([MakeCliScenario()]);
        var content = result.Values.First(v => v.Contains("S04:"));

        Assert.Contains("toMatch(", content);
        Assert.Contains("Uploaded", content);
    }

    // -------------------------------------------------------------------------
    // Mixed scenario list
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_MixedList_ProducesOneFilePerScenarioPlusConfig()
    {
        var scenarios = new List<Scenario> { MakeUiScenario(), MakeApiScenario(), MakeCliScenario() };
        var result = _generator.Generate(scenarios);

        // 3 spec files + 1 playwright.config.ts
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Generate_MixedList_EachScenarioHasSeparateFile()
    {
        var result = _generator.Generate([MakeUiScenario(), MakeApiScenario(), MakeCliScenario()]);

        Assert.Contains(result.Keys, k => k.Contains("S03"));
        Assert.Contains(result.Keys, k => k.Contains("S08"));
        Assert.Contains(result.Keys, k => k.Contains("S04"));
    }

    // -------------------------------------------------------------------------
    // No observation_surfaces
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_NoObservationSurfaces_ContainsStepsAndTodoOnly()
    {
        var scenario = new Scenario
        {
            Id = "S10",
            Title = "User views dashboard",
            JourneyKind = JourneyKind.UiInteraction,
            Actor = "User",
            Trigger = "User navigates to /dashboard",
            Steps = ["1. Page renders", "2. Charts load"],
            ExpectedTerminalState = ["Dashboard visible"],
        };

        var result = _generator.Generate([scenario]);
        var content = result.Values.First(v => v.Contains("S10:"));

        Assert.Contains("// 1. Page renders", content);
        Assert.Contains("// 2. Charts load", content);
        Assert.Contains("TODO", content);
    }

    // -------------------------------------------------------------------------
    // Python tech-stack
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_PythonTechStack_ProducesPyFile()
    {
        var result = _generator.Generate([MakeCliScenario()], techStack: "python");
        Assert.Contains(result.Keys, k => k.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_PythonTechStack_ContainsPytestImport()
    {
        var result = _generator.Generate([MakeCliScenario()], techStack: "python");
        var content = result.Values.First();
        Assert.Contains("import pytest", content);
    }

    // -------------------------------------------------------------------------
    // C# tech-stack
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_CSharpTechStack_ProducesCsFile()
    {
        var result = _generator.Generate([MakeUiScenario()], techStack: "dotnet");
        Assert.Contains(result.Keys, k => k.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_CSharpTechStack_ContainsXunitFact()
    {
        var result = _generator.Generate([MakeUiScenario()], techStack: "dotnet");
        var content = result.Values.First();
        Assert.Contains("[Fact]", content);
    }

    // -------------------------------------------------------------------------
    // Go tech-stack
    // -------------------------------------------------------------------------

    [Fact]
    public void Generate_GoTechStack_ProducesGoTestFile()
    {
        var result = _generator.Generate([MakeUiScenario()], techStack: "go");
        Assert.Contains(result.Keys, k => k.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generate_GoTechStack_ContainsTestingPackage()
    {
        var result = _generator.Generate([MakeUiScenario()], techStack: "go");
        var content = result.Values.First();
        Assert.Contains("import \"testing\"", content);
    }
}
