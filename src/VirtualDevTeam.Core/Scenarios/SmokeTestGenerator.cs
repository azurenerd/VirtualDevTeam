using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Generates smoke test files from approved scenarios.
/// Returns a dictionary of relative file path → file content.
/// The generated files are intended to be committed to the project repository as
/// durable CI regression artifacts after T-FINAL completes integration.
/// </summary>
/// <remarks>
/// <para>
/// Generation rules by <see cref="JourneyKind"/>:
/// <list type="bullet">
///   <item><see cref="JourneyKind.UiInteraction"/> → Playwright TypeScript test using <c>page</c> fixture.</item>
///   <item><see cref="JourneyKind.ApiCall"/> or <see cref="JourneyKind.Webhook"/> → Playwright TypeScript test using <c>request</c> fixture.</item>
///   <item><see cref="JourneyKind.CliInvocation"/> → Playwright TypeScript test using <c>child_process.execSync</c>.</item>
///   <item>Other kinds → generic Playwright test with step comments and TODO markers.</item>
/// </list>
/// </para>
/// <para>
/// Filename convention: <c>tests/scenarios/S{NN}_{title_slug}.spec.ts</c>.
/// A shared <c>tests/scenarios/playwright.config.ts</c> is also generated when any
/// Playwright test is included in the output.
/// </para>
/// <para>
/// If <paramref name="techStack"/> hints at a non-TypeScript project (Python, C#, Go, etc.)
/// the generator produces the language-appropriate format instead.
/// </para>
/// </remarks>
public sealed class SmokeTestGenerator
{
    private readonly ILogger<SmokeTestGenerator> _logger;

    public SmokeTestGenerator(ILogger<SmokeTestGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Generate smoke test content from approved scenarios.
    /// </summary>
    /// <param name="scenarios">Scenarios to generate tests for. Non-approved scenarios are skipped.</param>
    /// <param name="techStack">
    /// Optional hint for the target technology stack (e.g. "python", "dotnet", "go").
    /// When <see langword="null"/> or unrecognized, TypeScript/Playwright output is generated.
    /// </param>
    /// <returns>Dictionary of relative file path → file content.</returns>
    public IReadOnlyDictionary<string, string> Generate(
        IReadOnlyList<Scenario> scenarios,
        string? techStack = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        if (scenarios.Count == 0)
        {
            _logger.LogDebug("SmokeTestGenerator: no scenarios provided — returning empty output.");
            return new Dictionary<string, string>();
        }

        var targetLang = DetectLanguage(techStack);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool anyPlaywright = false;

        foreach (var scenario in scenarios)
        {
            var (path, content) = GenerateScenarioFile(scenario, targetLang);
            files[path] = content;
            _logger.LogDebug(
                "SmokeTestGenerator: generated {Path} for scenario {Id} ({JourneyKind})",
                path, scenario.Id, scenario.JourneyKind);

            if (targetLang == TargetLanguage.TypeScript)
                anyPlaywright = true;
        }

        if (anyPlaywright)
        {
            const string configPath = "tests/scenarios/playwright.config.ts";
            files[configPath] = GeneratePlaywrightConfig();
            _logger.LogDebug("SmokeTestGenerator: generated {Path}", configPath);
        }

        return files;
    }

    // -------------------------------------------------------------------------
    // Per-scenario file generation
    // -------------------------------------------------------------------------

    private static (string path, string content) GenerateScenarioFile(Scenario scenario, TargetLanguage lang)
    {
        var slug = TitleSlug(scenario.Title);
        return lang switch
        {
            TargetLanguage.Python  => GeneratePythonTest(scenario, slug),
            TargetLanguage.CSharp  => GenerateCSharpTest(scenario, slug),
            TargetLanguage.Go      => GenerateGoTest(scenario, slug),
            _                      => GenerateTypeScriptTest(scenario, slug),
        };
    }

    // ── TypeScript / Playwright ───────────────────────────────────────────────

    private static (string, string) GenerateTypeScriptTest(Scenario scenario, string slug)
    {
        var path = $"tests/scenarios/{scenario.Id}_{slug}.spec.ts";
        var sb = new StringBuilder();

        AppendTsHeader(sb, scenario);

        switch (scenario.JourneyKind)
        {
            case JourneyKind.UiInteraction:
            case JourneyKind.SystemInitiated:
                AppendTsUiTest(sb, scenario);
                break;

            case JourneyKind.ApiCall:
            case JourneyKind.Webhook:
                AppendTsApiTest(sb, scenario);
                break;

            case JourneyKind.CliInvocation:
                AppendTsCliTest(sb, scenario);
                break;

            default:
                AppendTsGenericTest(sb, scenario);
                break;
        }

        return (path, sb.ToString());
    }

    private static void AppendTsHeader(StringBuilder sb, Scenario scenario)
    {
        sb.AppendLine("import { test, expect } from '@playwright/test';");

        bool needsChildProcess =
            scenario.JourneyKind == JourneyKind.CliInvocation ||
            scenario.ObservationSurfaces.Any(s =>
                s.Kind is "process_exit_code" or "stdout_pattern");

        if (needsChildProcess)
            sb.AppendLine("import { execSync } from 'child_process';");

        sb.AppendLine();
    }

    private static void AppendTsUiTest(StringBuilder sb, Scenario scenario)
    {
        sb.AppendLine($"// Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"test('{scenario.Id}: {EscapeTsSingleQuote(scenario.Title)}', async ({{ page }}) => {{");

        AppendPreconditions(sb, scenario, "  ");
        AppendSteps(sb, scenario, "  ");
        AppendTerminalState(sb, scenario, "  ");
        AppendTsUiAssertions(sb, scenario, "  ");

        sb.AppendLine("});");
        sb.AppendLine();
    }

    private static void AppendTsApiTest(StringBuilder sb, Scenario scenario)
    {
        sb.AppendLine($"// Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"test('{scenario.Id}: {EscapeTsSingleQuote(scenario.Title)}', async ({{ request }}) => {{");

        AppendPreconditions(sb, scenario, "  ");
        AppendSteps(sb, scenario, "  ");
        AppendTerminalState(sb, scenario, "  ");
        AppendTsApiAssertions(sb, scenario, "  ");

        sb.AppendLine("});");
        sb.AppendLine();
    }

    private static void AppendTsCliTest(StringBuilder sb, Scenario scenario)
    {
        sb.AppendLine($"// Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"test('{scenario.Id}: {EscapeTsSingleQuote(scenario.Title)}', async () => {{");

        AppendPreconditions(sb, scenario, "  ");
        AppendSteps(sb, scenario, "  ");
        AppendTerminalState(sb, scenario, "  ");
        AppendTsCliAssertions(sb, scenario, "  ");

        sb.AppendLine("});");
        sb.AppendLine();
    }

    private static void AppendTsGenericTest(StringBuilder sb, Scenario scenario)
    {
        sb.AppendLine($"// Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"// journey_kind: {ToKebabCase(scenario.JourneyKind.ToString())}");
        sb.AppendLine($"test('{scenario.Id}: {EscapeTsSingleQuote(scenario.Title)}', async ({{ page, request }}) => {{");

        AppendPreconditions(sb, scenario, "  ");
        AppendSteps(sb, scenario, "  ");
        AppendTerminalState(sb, scenario, "  ");

        sb.AppendLine();
        sb.AppendLine("  // TODO: Add assertions for this journey kind");

        sb.AppendLine("});");
        sb.AppendLine();
    }

    // ── Assertion generation ──────────────────────────────────────────────────

    private static void AppendTsUiAssertions(StringBuilder sb, Scenario scenario, string indent)
    {
        var surfaces = scenario.ObservationSurfaces;
        if (surfaces.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}// TODO: Add Playwright selectors and assertions from observation_surfaces");
            return;
        }

        sb.AppendLine();
        foreach (var surface in surfaces)
        {
            switch (surface.Kind)
            {
                case "dom_query" when surface.Fields.TryGetValue("selector", out var sel):
                    sb.AppendLine($"{indent}// observation_surface: dom_query → \"{sel}\"");
                    sb.AppendLine($"{indent}await expect(page.locator('{EscapeTsSingleQuote(sel)}')).toBeVisible();");
                    break;

                case "dom_text" when surface.Fields.TryGetValue("selector", out var sel):
                    sb.AppendLine($"{indent}// observation_surface: dom_text → \"{sel}\"");
                    if (surface.Fields.TryGetValue("expected_change", out var change))
                        sb.AppendLine($"{indent}// expected_change: {change}");
                    sb.AppendLine($"{indent}await expect(page.locator('{EscapeTsSingleQuote(sel)}')).toBeVisible();");
                    sb.AppendLine($"{indent}// TODO: Assert text content matches expected state");
                    break;

                case "event_bus" when surface.Fields.TryGetValue("event_name", out var ev):
                    sb.AppendLine($"{indent}// observation_surface: event_bus → event '{ev}'");
                    sb.AppendLine($"{indent}// TODO: Assert EventBus.fired('{EscapeTsSingleQuote(ev)}') via page.evaluate or spy");
                    break;

                case "canvas_state":
                    sb.AppendLine($"{indent}// observation_surface: canvas_state");
                    sb.AppendLine($"{indent}// TODO: Assert canvas pixel region or API query result via page.evaluate");
                    break;

                default:
                    AppendGenericSurfaceComment(sb, surface, indent);
                    break;
            }
        }
    }

    private static void AppendTsApiAssertions(StringBuilder sb, Scenario scenario, string indent)
    {
        var surfaces = scenario.ObservationSurfaces;

        // Find the primary HTTP surface (first http_response or infer from trigger)
        var httpSurface = surfaces.FirstOrDefault(s => s.Kind == "http_response");
        var statusCode = "200";
        var maxLatency = (string?)null;

        if (httpSurface is not null)
        {
            httpSurface.Fields.TryGetValue("status", out statusCode);
            statusCode ??= "200";
            httpSurface.Fields.TryGetValue("max_latency_ms", out maxLatency);
        }

        // Infer endpoint from trigger
        var endpoint = InferEndpointFromTrigger(scenario.Trigger);

        sb.AppendLine();
        if (maxLatency is not null)
        {
            sb.AppendLine($"{indent}// observation_surface: http_response  status: {statusCode}, max_latency_ms: {maxLatency}");
        }
        else if (httpSurface is not null)
        {
            sb.AppendLine($"{indent}// observation_surface: http_response  status: {statusCode}");
        }

        sb.AppendLine($"{indent}const response = await request.post('{endpoint}', {{");
        sb.AppendLine($"{indent}  // TODO: Add request payload from scenario trigger");
        sb.AppendLine($"{indent}}});");
        sb.AppendLine($"{indent}expect(response.status()).toBe({statusCode});");

        // Additional non-HTTP surfaces
        foreach (var surface in surfaces.Where(s => s.Kind != "http_response"))
            AppendGenericSurfaceComment(sb, surface, indent);
    }

    private static void AppendTsCliAssertions(StringBuilder sb, Scenario scenario, string indent)
    {
        var exitSurface  = scenario.ObservationSurfaces.FirstOrDefault(s => s.Kind == "process_exit_code");
        var stdoutSurface = scenario.ObservationSurfaces.FirstOrDefault(s => s.Kind == "stdout_pattern");
        var command = InferCommandFromTrigger(scenario.Trigger);

        sb.AppendLine();
        sb.AppendLine($"{indent}// trigger: {scenario.Trigger}");

        if (exitSurface is not null && exitSurface.Fields.TryGetValue("expected", out var expectedExit))
            sb.AppendLine($"{indent}// observation_surface: process_exit_code expected: {expectedExit}");
        if (stdoutSurface is not null && stdoutSurface.Fields.TryGetValue("regex", out var regex))
            sb.AppendLine($"{indent}// observation_surface: stdout_pattern regex: \"{regex}\"");

        sb.AppendLine();
        sb.AppendLine($"{indent}// TODO: Adjust command and paths");
        sb.AppendLine($"{indent}let output = '';");
        sb.AppendLine($"{indent}let exitCode = 0;");
        sb.AppendLine($"{indent}try {{");
        sb.AppendLine($"{indent}  output = execSync('{EscapeTsSingleQuote(command)}', {{ encoding: 'utf8' }});");
        sb.AppendLine($"{indent}}} catch (err: any) {{");
        sb.AppendLine($"{indent}  exitCode = err.status ?? 1;");
        sb.AppendLine($"{indent}  output = err.stdout ?? '';");
        sb.AppendLine($"{indent}}}");

        if (exitSurface is not null && exitSurface.Fields.TryGetValue("expected", out var exit))
            sb.AppendLine($"{indent}expect(exitCode).toBe({exit});");

        if (stdoutSurface is not null && stdoutSurface.Fields.TryGetValue("regex", out var pat))
            sb.AppendLine($"{indent}expect(output).toMatch(/{pat}/);");

        // Remaining surfaces
        foreach (var surface in scenario.ObservationSurfaces
            .Where(s => s.Kind is not "process_exit_code" and not "stdout_pattern"))
        {
            AppendGenericSurfaceComment(sb, surface, indent);
        }
    }

    private static void AppendGenericSurfaceComment(StringBuilder sb, ObservationSurface surface, string indent)
    {
        sb.Append($"{indent}// observation_surface: {surface.Kind}");
        foreach (var (k, v) in surface.Fields)
            sb.Append($"  {k}: {v}");
        sb.AppendLine();
        sb.AppendLine($"{indent}// TODO: Add assertion for {surface.Kind}");
    }

    // ── Common step/precondition helpers ─────────────────────────────────────

    private static void AppendPreconditions(StringBuilder sb, Scenario scenario, string indent)
    {
        if (scenario.Preconditions.Count == 0) return;
        sb.AppendLine($"{indent}// Preconditions:");
        foreach (var p in scenario.Preconditions)
            sb.AppendLine($"{indent}//   {p}");
        sb.AppendLine();
    }

    private static void AppendSteps(StringBuilder sb, Scenario scenario, string indent)
    {
        if (scenario.Steps.Count == 0) return;
        foreach (var step in scenario.Steps)
            sb.AppendLine($"{indent}// {step}");
        sb.AppendLine();
    }

    private static void AppendTerminalState(StringBuilder sb, Scenario scenario, string indent)
    {
        if (scenario.ExpectedTerminalState.Count == 0) return;
        sb.AppendLine($"{indent}// Expected terminal state assertions:");
        foreach (var state in scenario.ExpectedTerminalState)
            sb.AppendLine($"{indent}//   - {state}");
    }

    // ── Playwright config ─────────────────────────────────────────────────────

    private static string GeneratePlaywrightConfig() => """
        import { defineConfig, devices } from '@playwright/test';

        // Auto-generated by VDT SmokeTestGenerator.
        // Adjust baseURL and project configuration as needed.
        export default defineConfig({
          testDir: '.',
          timeout: 30_000,
          expect: { timeout: 5_000 },
          use: {
            baseURL: process.env.BASE_URL ?? 'http://localhost:3000',
            trace: 'on-first-retry',
          },
          projects: [
            { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
          ],
        });
        """;

    // ── Python ────────────────────────────────────────────────────────────────

    private static (string, string) GeneratePythonTest(Scenario scenario, string slug)
    {
        var path = $"tests/scenarios/{scenario.Id}_{slug}.py";
        var sb = new StringBuilder();
        sb.AppendLine("import pytest");

        if (scenario.JourneyKind == JourneyKind.CliInvocation)
            sb.AppendLine("import subprocess");
        if (scenario.JourneyKind is JourneyKind.ApiCall or JourneyKind.Webhook)
            sb.AppendLine("import requests");

        sb.AppendLine();
        sb.AppendLine($"# Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"def test_{scenario.Id.ToLowerInvariant()}_{slug.Replace('-', '_')}():");
        AppendPythonBody(sb, scenario);
        return (path, sb.ToString());
    }

    private static void AppendPythonBody(StringBuilder sb, Scenario scenario)
    {
        if (scenario.Preconditions.Count > 0)
        {
            sb.AppendLine("    # Preconditions:");
            foreach (var p in scenario.Preconditions) sb.AppendLine($"    #   {p}");
            sb.AppendLine();
        }

        foreach (var step in scenario.Steps)
            sb.AppendLine($"    # {step}");
        if (scenario.Steps.Count > 0) sb.AppendLine();

        foreach (var state in scenario.ExpectedTerminalState)
            sb.AppendLine($"    # Expected: {state}");

        sb.AppendLine();
        sb.AppendLine("    # TODO: Implement assertions");
        sb.AppendLine("    pass");
        sb.AppendLine();
    }

    // ── C# / xUnit ────────────────────────────────────────────────────────────

    private static (string, string) GenerateCSharpTest(Scenario scenario, string slug)
    {
        var path = $"tests/scenarios/{scenario.Id}_{slug}.cs";
        var sb = new StringBuilder();
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine("// Auto-generated smoke test from VDT SmokeTestGenerator.");
        sb.AppendLine($"// Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine();
        sb.AppendLine($"public class {scenario.Id}_{ToPascalCase(slug)}SmokeTest");
        sb.AppendLine("{");
        sb.AppendLine("    [Fact]");
        sb.AppendLine($"    public void {scenario.Id}_{ToPascalCase(slug)}()");
        sb.AppendLine("    {");
        if (scenario.Preconditions.Count > 0)
        {
            sb.AppendLine("        // Preconditions:");
            foreach (var p in scenario.Preconditions) sb.AppendLine($"        //   {p}");
            sb.AppendLine();
        }
        foreach (var step in scenario.Steps) sb.AppendLine($"        // {step}");
        if (scenario.Steps.Count > 0) sb.AppendLine();
        foreach (var state in scenario.ExpectedTerminalState) sb.AppendLine($"        // Expected: {state}");
        sb.AppendLine();
        sb.AppendLine("        // TODO: Implement assertions");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        return (path, sb.ToString());
    }

    // ── Go ────────────────────────────────────────────────────────────────────

    private static (string, string) GenerateGoTest(Scenario scenario, string slug)
    {
        var path = $"tests/scenarios/{scenario.Id}_{slug}_test.go";
        var sb = new StringBuilder();
        sb.AppendLine("package scenarios_test");
        sb.AppendLine();
        sb.AppendLine("import \"testing\"");
        sb.AppendLine();
        sb.AppendLine($"// Auto-generated smoke test from VDT Scenario {scenario.Id}: {scenario.Title}");
        sb.AppendLine($"func Test{scenario.Id}_{ToPascalCase(slug)}(t *testing.T) {{");
        foreach (var step in scenario.Steps) sb.AppendLine($"\t// {step}");
        if (scenario.Steps.Count > 0) sb.AppendLine();
        foreach (var state in scenario.ExpectedTerminalState) sb.AppendLine($"\t// Expected: {state}");
        sb.AppendLine();
        sb.AppendLine("\tt.Skip(\"TODO: implement smoke test\")");
        sb.AppendLine("}");
        sb.AppendLine();
        return (path, sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private enum TargetLanguage { TypeScript, Python, CSharp, Go }

    private static TargetLanguage DetectLanguage(string? techStack)
    {
        if (string.IsNullOrWhiteSpace(techStack)) return TargetLanguage.TypeScript;
        var lower = techStack.ToLowerInvariant();
        if (lower.Contains("python") || lower.Contains("py") || lower.Contains("pytest"))
            return TargetLanguage.Python;
        if (lower.Contains("csharp") || lower.Contains("c#") || lower.Contains(".net") || lower.Contains("dotnet"))
            return TargetLanguage.CSharp;
        if (lower.Contains("golang") || lower.Contains(" go ") || lower.TrimEnd() == "go")
            return TargetLanguage.Go;
        return TargetLanguage.TypeScript;
    }

    private static string TitleSlug(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '_' or '-') sb.Append('-');
        }
        // Collapse consecutive dashes and trim
        var s = sb.ToString().Trim('-');
        while (s.Contains("--"))
            s = s.Replace("--", "-");
        return s;
    }

    private static string ToPascalCase(string slug) =>
        string.Concat(slug.Split('-').Select(w =>
            w.Length == 0 ? "" : char.ToUpperInvariant(w[0]) + w[1..]));

    private static string ToKebabCase(string pascalCase)
    {
        var sb = new StringBuilder();
        foreach (var ch in pascalCase)
        {
            if (char.IsUpper(ch) && sb.Length > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string EscapeTsSingleQuote(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string InferEndpointFromTrigger(string trigger)
    {
        // Try to find a URL-like fragment in the trigger text
        var parts = trigger.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var urlPart = parts.FirstOrDefault(p =>
            p.StartsWith('/') ||
            p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        return urlPart ?? "/api/endpoint";
    }

    private static string InferCommandFromTrigger(string trigger)
    {
        // Return the raw trigger text stripped of surrounding quotes
        return trigger.Trim('"', '\'');
    }
}
