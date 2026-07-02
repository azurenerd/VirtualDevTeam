using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Generates a structured <see cref="InteractionPlan"/> for Playwright MCP testing
/// by analyzing the task context and diff. Uses a single budget-tier LLM call
/// to produce task-specific test scenarios with concrete interaction steps.
/// </summary>
public sealed class InteractionPlanGenerator
{
    private readonly IChatCompletionRunner _chatRunner;
    private readonly ILogger<InteractionPlanGenerator> _logger;
    private const string ModelTier = "budget";
    private const int MaxScenarios = 4;
    private const int MaxStepsPerScenario = 12;

    public InteractionPlanGenerator(
        IChatCompletionRunner chatRunner,
        ILogger<InteractionPlanGenerator> logger)
    {
        _chatRunner = chatRunner;
        _logger = logger;
    }

    /// <summary>
    /// Generate an interaction plan from task context and an optional diff analysis.
    /// Returns null on any failure — callers fall back to generic exploration.
    /// </summary>
    public async Task<InteractionPlan?> GenerateAsync(
        string taskTitle,
        string? taskDescription,
        DiffAnalysisResult? diffAnalysis,
        CancellationToken ct = default)
    {
        try
        {
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(taskTitle, taskDescription, diffAnalysis);

            _logger.LogInformation(
                "Generating interaction plan for task: {Task} (pattern: {Pattern})",
                taskTitle, diffAnalysis?.DetectedPattern ?? UIPatternKind.Unknown);

            var rawJson = await _chatRunner.InvokeAsync(systemPrompt, userPrompt, ModelTier,
                agentId: "interaction-plan-gen", ct: ct);

            var plan = ParsePlanResponse(rawJson, diffAnalysis);
            if (plan is null || plan.Scenarios.Count == 0)
            {
                _logger.LogDebug("Plan generation returned no usable scenarios for {Task}", taskTitle);
                return null;
            }

            _logger.LogInformation(
                "Generated interaction plan: {Count} scenarios, {Steps} total steps, pattern={Pattern}, allowsFormInput={AllowsInput}",
                plan.Scenarios.Count,
                plan.Scenarios.Sum(s => s.Steps.Count),
                plan.DetectedPattern,
                plan.AllowsFormInput);

            return plan;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interaction plan generation failed for {Task} — will fall back to generic exploration", taskTitle);
            return null;
        }
    }

    private static string BuildSystemPrompt() => """
        You are a QA test plan generator for web application UI testing.
        Given a task description and code change analysis, generate a structured interaction plan
        that a Playwright browser automation agent will follow to test the implemented UI.

        Your plan must be SPECIFIC and ACTIONABLE — not generic exploration.
        Each step tells the agent exactly what to do: what to click, what to type, what to verify.

        SAFETY RULES for test data:
        - Generate realistic but SYNTHETIC test data for all form fields
        - Use contextually appropriate fake data (e.g., "Test Project" for project names, "test@example.com" for emails)
        - NEVER include real credentials, tokens, API keys, or connection strings
        - NEVER generate steps that click delete/remove/clear/reset/logout/login buttons
        - Form submissions in isolated test environments ARE safe — the data doesn't persist
        - Mark scenarios with form inputs as "SafeWrite"
        - Mark view-only scenarios as "ReadOnly"

        OUTPUT FORMAT — respond with ONLY this JSON, no other text:
        {
          "taskSummary": "one-line summary of what's being tested",
          "scenarios": [
            {
              "name": "Scenario Name",
              "url": "/relative-path",
              "description": "What this scenario validates",
              "safety": "ReadOnly" or "SafeWrite",
              "steps": [
                {
                  "action": "Navigate|Click|Type|Select|WaitForText|Verify|Screenshot|ScrollTo|Hover",
                  "target": "button text, input label, CSS selector description, or URL",
                  "value": "text to type or option to select (null if not applicable)",
                  "expectedResult": "what should appear after this step (null if not applicable)",
                  "description": "human-readable step description"
                }
              ]
            }
          ]
        }
        """;

    private static string BuildUserPrompt(
        string taskTitle, string? taskDescription, DiffAnalysisResult? diff)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Task: {taskTitle}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(taskDescription))
        {
            // Extract acceptance criteria section if present
            var normalized = taskDescription.Replace("\\n", "\n");
            var acIdx = normalized.IndexOf("Acceptance Criteria", StringComparison.OrdinalIgnoreCase);

            if (acIdx >= 0)
            {
                var acSection = normalized[acIdx..];
                var nextSection = acSection.IndexOf("\n## ", 5, StringComparison.OrdinalIgnoreCase);
                if (nextSection > 0) acSection = acSection[..nextSection];
                sb.AppendLine("## Acceptance Criteria");
                sb.AppendLine(acSection);
                sb.AppendLine();
            }

            // Include first ~1500 chars of description for context
            var descExcerpt = normalized.Length > 1500 ? normalized[..1500] + "..." : normalized;
            sb.AppendLine("## Task Description (excerpt)");
            sb.AppendLine(descExcerpt);
            sb.AppendLine();
        }

        if (diff is not null)
        {
            sb.AppendLine("## Code Change Analysis");
            sb.AppendLine(DiffAnalyzer.BuildSummary(diff));
            sb.AppendLine();

            // Include button texts as navigational hints
            if (diff.DetectedPattern == UIPatternKind.Wizard)
            {
                sb.AppendLine("NOTE: This is a WIZARD / multi-step form. Generate scenarios that:");
                sb.AppendLine("- Fill each step's form fields with synthetic test data");
                sb.AppendLine("- Click Next/Continue to advance through ALL steps");
                sb.AppendLine("- Take a screenshot after each step transition");
                sb.AppendLine("- Include a validation scenario (click Next without filling required fields)");
                sb.AppendLine();
            }
            else if (diff.DetectedPattern == UIPatternKind.CrudForm)
            {
                sb.AppendLine("NOTE: This is a CRUD form. Generate scenarios that:");
                sb.AppendLine("- Fill all form fields with synthetic test data");
                sb.AppendLine("- Verify field validation (try submitting empty required fields)");
                sb.AppendLine("- Submit the form and verify success state");
                sb.AppendLine();
            }
            else if (diff.DetectedPattern == UIPatternKind.Dashboard)
            {
                sb.AppendLine("NOTE: This is a DASHBOARD. Generate scenarios that:");
                sb.AppendLine("- Navigate to each dashboard section/tab");
                sb.AppendLine("- Verify charts/tables/metrics render with data");
                sb.AppendLine("- Interact with filters, dropdowns, and date pickers");
                sb.AppendLine();
            }
            else if (diff.DetectedPattern == UIPatternKind.DataTable)
            {
                sb.AppendLine("NOTE: This is a DATA TABLE. Generate scenarios that:");
                sb.AppendLine("- Verify the table renders with rows");
                sb.AppendLine("- Click column headers to test sorting");
                sb.AppendLine("- Use filter/search controls if present");
                sb.AppendLine("- Test pagination if present");
                sb.AppendLine();
            }
        }

        sb.AppendLine($"Generate up to {MaxScenarios} test scenarios, each with up to {MaxStepsPerScenario} steps.");
        sb.AppendLine("Focus on the NEW functionality introduced by this task.");
        sb.AppendLine("Include at least one scenario that tests the PRIMARY new feature end-to-end.");
        sb.AppendLine("If the task adds form elements, include a scenario that fills and submits them.");

        return sb.ToString();
    }

    internal InteractionPlan? ParsePlanResponse(string rawJson, DiffAnalysisResult? diff)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        // Strip markdown code fences if present
        var json = rawJson.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) json = json[..lastFence];
            json = json.Trim();
        }

        // Find the JSON object boundaries
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            _logger.LogDebug("No JSON object found in plan response");
            return null;
        }
        json = json[start..(end + 1)];

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var taskSummary = root.TryGetProperty("taskSummary", out var ts) ? ts.GetString() : null;

            var scenarios = new List<TestScenario>();
            if (root.TryGetProperty("scenarios", out var scenariosEl) && scenariosEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var scenarioEl in scenariosEl.EnumerateArray())
                {
                    if (scenarios.Count >= MaxScenarios) break;

                    var name = scenarioEl.TryGetProperty("name", out var n) ? n.GetString() ?? "Unnamed" : "Unnamed";
                    var url = scenarioEl.TryGetProperty("url", out var u) ? u.GetString() ?? "/" : "/";
                    var desc = scenarioEl.TryGetProperty("description", out var d) ? d.GetString() : null;

                    // Normalize URL: force relative, reject external absolute URLs
                    url = NormalizeScenarioUrl(url);
                    var safetyStr = scenarioEl.TryGetProperty("safety", out var sf) ? sf.GetString() : "ReadOnly";
                    var safety = safetyStr switch
                    {
                        "SafeWrite" => SafetyLevel.SafeWrite,
                        "Destructive" => SafetyLevel.Destructive,
                        _ => SafetyLevel.ReadOnly,
                    };

                    // Skip destructive scenarios entirely
                    if (safety == SafetyLevel.Destructive) continue;

                    var steps = new List<InteractionStep>();
                    if (scenarioEl.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stepEl in stepsEl.EnumerateArray())
                        {
                            if (steps.Count >= MaxStepsPerScenario) break;

                            var actionStr = stepEl.TryGetProperty("action", out var a) ? a.GetString() : null;
                            var target = stepEl.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                            var value = stepEl.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
                            var expected = stepEl.TryGetProperty("expectedResult", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetString() : null;
                            var stepDesc = stepEl.TryGetProperty("description", out var sd) ? sd.GetString() : null;

                            if (!TryParseAction(actionStr, out var action)) continue;

                            // Safety filter: never allow Type or Select in ReadOnly scenarios
                            if ((action == InteractionAction.Type || action == InteractionAction.Select)
                                && safety == SafetyLevel.ReadOnly)
                                continue;

                            // Safety filter: reject dangerous click targets
                            if (action == InteractionAction.Click && IsDangerousTarget(target))
                                continue;

                            // Safety filter: reject credential-like values
                            if (value != null && IsCredentialLikeValue(value))
                                continue;

                            // Safety filter: cap target/value length to prevent prompt injection
                            if (target.Length > 500) target = target[..500];
                            if (value != null && value.Length > 500) value = value[..500];

                            steps.Add(new InteractionStep
                            {
                                Action = action,
                                Target = target,
                                Value = value,
                                ExpectedResult = expected,
                                Description = stepDesc,
                            });
                        }
                    }

                    if (steps.Count > 0)
                    {
                        scenarios.Add(new TestScenario
                        {
                            Name = name,
                            Url = url,
                            Description = desc,
                            Steps = steps,
                            Safety = safety,
                        });
                    }
                }
            }

            return new InteractionPlan
            {
                Scenarios = scenarios,
                TaskSummary = taskSummary,
                DetectedPattern = diff?.DetectedPattern ?? UIPatternKind.Unknown,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse interaction plan JSON");
            return null;
        }
    }

    private static bool TryParseAction(string? actionStr, out InteractionAction action)
    {
        action = InteractionAction.Navigate;
        if (string.IsNullOrWhiteSpace(actionStr)) return false;

        return Enum.TryParse(actionStr.Trim(), ignoreCase: true, out action);
    }

    /// <summary>
    /// Normalizes a scenario URL to be relative. Rejects external absolute URLs,
    /// strips localhost-like prefixes, and ensures the path starts with '/'.
    /// </summary>
    private static string NormalizeScenarioUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";

        url = url.Trim();

        // If it's a relative path without leading slash, add one
        if (!url.StartsWith('/') && !url.Contains("://"))
            return "/" + url;

        // If it's an absolute URL, check if it's localhost (acceptable) or external (reject)
        if (url.Contains("://"))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // Allow localhost/127.0.0.1 — strip to path only
                if (uri.Host is "localhost" or "127.0.0.1" || uri.Host.StartsWith("localhost:"))
                    return uri.AbsolutePath;

                // Reject external URLs — fall back to root
                return "/";
            }
            return "/";
        }

        return url;
    }

    private static readonly string[] DangerousTargetTerms =
    [
        "delete", "remove", "destroy", "logout", "log-out", "sign-out", "signout",
        "login", "log-in", "sign-in", "signin", "auth", "oauth",
        "payment", "checkout", "billing", "unsubscribe",
        "drop", "truncate", "purge", "reset-all", "clear-all", "wipe"
    ];

    /// <summary>
    /// Returns true if a click target contains terms associated with destructive or auth actions.
    /// </summary>
    private static bool IsDangerousTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var lower = target.ToLowerInvariant();
        return Array.Exists(DangerousTargetTerms, term => lower.Contains(term));
    }

    private static readonly string[] CredentialPatterns =
    [
        "password", "secret", "token", "api_key", "apikey", "api-key",
        "bearer ", "basic ", "connection_string", "connectionstring",
        "-----BEGIN", "eyJ", "ghp_", "gho_", "sk-", "pk_",
    ];

    /// <summary>
    /// Returns true if a value looks like it could be a credential, token, or secret.
    /// </summary>
    private static bool IsCredentialLikeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var lower = value.ToLowerInvariant();
        return Array.Exists(CredentialPatterns, pattern => lower.Contains(pattern));
    }
}
