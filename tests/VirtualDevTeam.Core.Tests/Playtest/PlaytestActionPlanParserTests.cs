using System.Text.Json;
using VirtualDevTeam.Core.Agents.Playtest;

namespace VirtualDevTeam.Core.Tests.Playtest;

public class PlaytestActionPlanParserTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── Parsing well-formed plans ─────────────────────────────────────────────

    [Fact]
    public void Parse_UiInteractionPlan_ReturnsCorrectAdapter()
    {
        var json = """
            {
              "scenario_id": "S03",
              "journey_kind": "ui_interaction",
              "adapter": "WebPlaytestAdapter",
              "precondition_check": "assert.selectorExists('.hud-gold')",
              "actions": [
                {
                  "step_index": 0,
                  "scenario_step": "1. Player clicks tile",
                  "action_type": "page.click",
                  "params": { "selector": ".playfield-tile" },
                  "captures_snapshot": false,
                  "snapshot_key": null,
                  "surface_verified": null
                }
              ],
              "terminal_assertions": [
                {
                  "surface_index": 0,
                  "surface_kind": "dom_query",
                  "action_type": "assert.selectorExists",
                  "params": { "selector": ".tower-sprite" }
                }
              ],
              "final_screenshot": "s03_final.png"
            }
            """;

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);

        Assert.NotNull(plan);
        Assert.Equal("S03", plan.ScenarioId);
        Assert.Equal("WebPlaytestAdapter", plan.Adapter);
        Assert.Equal("ui_interaction", plan.JourneyKind);
        Assert.Single(plan.Actions);
        Assert.Single(plan.TerminalAssertions);
        Assert.Equal("s03_final.png", plan.FinalScreenshot);
    }

    [Fact]
    public void Parse_ApiCallPlan_ReturnsHttpActions()
    {
        var json = """
            {
              "scenario_id": "S08",
              "journey_kind": "webhook",
              "adapter": "ApiPlaytestAdapter",
              "precondition_check": null,
              "actions": [
                {
                  "step_index": 0,
                  "scenario_step": "1. POST to webhook",
                  "action_type": "http.post",
                  "params": { "path": "/webhooks/stripe", "bodyJson": "{}" },
                  "captures_snapshot": false,
                  "snapshot_key": null,
                  "surface_verified": null
                },
                {
                  "step_index": 1,
                  "scenario_step": "Assert status",
                  "action_type": "http.assertStatus",
                  "params": { "expectedStatus": 200, "maxLatencyMs": 5000 },
                  "captures_snapshot": false,
                  "snapshot_key": null,
                  "surface_verified": "http_response"
                }
              ],
              "terminal_assertions": [
                {
                  "surface_index": 0,
                  "surface_kind": "http_response",
                  "action_type": "http.assertStatus",
                  "params": { "expectedStatus": 200 }
                }
              ],
              "final_screenshot": null
            }
            """;

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);

        Assert.NotNull(plan);
        Assert.Equal("S08", plan.ScenarioId);
        Assert.Equal("ApiPlaytestAdapter", plan.Adapter);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Null(plan.FinalScreenshot);

        var postAction = plan.Actions[0];
        Assert.Equal("http.post", postAction.ActionType);
        Assert.Equal("http", postAction.ActionCategory);
        Assert.Equal("post", postAction.ActionVerb);
        Assert.Equal("/webhooks/stripe", postAction.GetParam("path"));
    }

    [Fact]
    public void Parse_CliPlan_ReturnsCliActions()
    {
        var json = """
            {
              "scenario_id": "S04",
              "journey_kind": "cli_invocation",
              "adapter": "CliPlaytestAdapter",
              "precondition_check": null,
              "actions": [
                {
                  "step_index": 0,
                  "scenario_step": "1–6. Full CLI invocation",
                  "action_type": "cli.run",
                  "params": { "binary": "myapp", "args": ["upload", "--file=customers.csv"] },
                  "captures_snapshot": false,
                  "snapshot_key": null,
                  "surface_verified": null
                },
                {
                  "step_index": 1,
                  "scenario_step": "Assert exit code",
                  "action_type": "cli.assertExitCode",
                  "params": { "expected": 0 },
                  "captures_snapshot": false,
                  "snapshot_key": null,
                  "surface_verified": "process_exit_code"
                }
              ],
              "terminal_assertions": [],
              "final_screenshot": null
            }
            """;

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);

        Assert.NotNull(plan);
        Assert.Equal("CliPlaytestAdapter", plan.Adapter);
        var runAction = plan.Actions[0];
        Assert.Equal("cli", runAction.ActionCategory);
        Assert.Equal("run", runAction.ActionVerb);
        Assert.Equal("myapp", runAction.GetParam("binary"));
    }

    // ── PlaytestAction helpers ────────────────────────────────────────────────

    [Fact]
    public void GetParam_StringValue_ReturnsString()
    {
        var action = new PlaytestAction
        {
            StepIndex = 0,
            ActionType = "page.click",
            Params = new Dictionary<string, JsonElement>
            {
                ["selector"] = JsonDocument.Parse("\"#submit-btn\"").RootElement,
            }
        };

        Assert.Equal("#submit-btn", action.GetParam("selector"));
    }

    [Fact]
    public void GetParam_AbsentKey_ReturnsNull()
    {
        var action = new PlaytestAction { StepIndex = 0, ActionType = "page.goto", Params = [] };
        Assert.Null(action.GetParam("missing"));
    }

    [Fact]
    public void GetIntParam_NumberValue_ReturnsInt()
    {
        var action = new PlaytestAction
        {
            StepIndex = 0,
            ActionType = "page.waitForSelector",
            Params = new Dictionary<string, JsonElement>
            {
                ["timeout"] = JsonDocument.Parse("5000").RootElement,
            }
        };

        Assert.Equal(5000, action.GetIntParam("timeout"));
    }

    [Fact]
    public void GetIntParam_AbsentKey_ReturnsFallback()
    {
        var action = new PlaytestAction { StepIndex = 0, ActionType = "x", Params = [] };
        Assert.Equal(42, action.GetIntParam("timeout", 42));
    }

    [Theory]
    [InlineData("page.click", "page", "click")]
    [InlineData("assert.selectorExists", "assert", "selectorExists")]
    [InlineData("http.post", "http", "post")]
    [InlineData("cli.run", "cli", "run")]
    [InlineData("simpleAction", "simpleAction", "simpleAction")]
    public void ActionCategory_And_ActionVerb_SplitCorrectly(string actionType, string expectedCategory, string expectedVerb)
    {
        var action = new PlaytestAction { StepIndex = 0, ActionType = actionType };
        Assert.Equal(expectedCategory, action.ActionCategory);
        Assert.Equal(expectedVerb, action.ActionVerb);
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyActions_ReturnsEmptyList()
    {
        var json = """
            {
              "scenario_id": "S01",
              "journey_kind": "api_call",
              "adapter": "ApiPlaytestAdapter",
              "actions": [],
              "terminal_assertions": []
            }
            """;

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);

        Assert.NotNull(plan);
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.TerminalAssertions);
    }

    [Fact]
    public void Parse_NullPreconditionCheck_IsNullInModel()
    {
        var json = """
            {
              "scenario_id": "S01",
              "journey_kind": "api_call",
              "adapter": "ApiPlaytestAdapter",
              "precondition_check": null,
              "actions": [],
              "terminal_assertions": []
            }
            """;

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);
        Assert.Null(plan!.PreconditionCheck);
    }

    [Fact]
    public void Parse_WithMarkdownFences_CanBeStrippedAndParsed()
    {
        // Simulate an LLM response that wraps JSON in markdown fences despite instructions
        var rawLlmOutput = "```json\n{\"scenario_id\":\"S01\",\"journey_kind\":\"api_call\",\"adapter\":\"ApiPlaytestAdapter\",\"actions\":[],\"terminal_assertions\":[]}\n```";

        // Strip as AppPlaytester does
        var json = rawLlmOutput.Trim();
        if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
        if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(json, _options);
        Assert.NotNull(plan);
        Assert.Equal("S01", plan.ScenarioId);
    }
}
