using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// CLI-agentic implementation of <see cref="IAppPlaytester"/>.
/// Instead of generating a JSON action plan and executing it deterministically,
/// this launches a Copilot CLI session with <c>--allow-all</c> that autonomously
/// verifies each scenario using Playwright MCP tools.
///
/// This replaces the brittle 3-layer pipeline (JSON plan → deterministic executor → JSON judge)
/// with a single agentic session per scenario. The CLI agent navigates, interacts, and verifies
/// on its own — eliminating JSON schema mismatch failures entirely.
/// </summary>
public sealed class CliAppPlaytester : IAppPlaytester
{
    private readonly IScenarioRegistry _scenarioRegistry;
    private readonly CopilotCliProcessManager _processManager;
    private readonly ScenarioVerificationConfig _verifyConfig;
    private readonly FlowMonitorPersistence? _flowPersistence;
    private readonly ILogger<CliAppPlaytester> _logger;

    /// <summary>Fallback stall window (seconds) when config is unset. Replaces the old hard 180s wall-clock cap.</summary>
    private const int DefaultStuckSeconds = 300;

    public CliAppPlaytester(
        IScenarioRegistry scenarioRegistry,
        CopilotCliProcessManager processManager,
        ScenarioVerificationConfig verifyConfig,
        ILogger<CliAppPlaytester> logger,
        FlowMonitorPersistence? flowPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(scenarioRegistry);
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(logger);

        _scenarioRegistry = scenarioRegistry;
        _processManager = processManager;
        _verifyConfig = verifyConfig ?? new ScenarioVerificationConfig();
        _flowPersistence = flowPersistence;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PlaytestReport[]> RunAsync(
        AppHandle handle,
        IReadOnlyList<Scenario>? scenarios = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var scenariosToRun = (scenarios
            ?? _scenarioRegistry.Current
                .Where(s => s.Status == ScenarioStatus.Approved)
                .ToList())
            .ToList();

        if (scenariosToRun.Count == 0)
        {
            _logger.LogWarning("CliAppPlaytester: no approved scenarios to run");
            return [];
        }

        _logger.LogInformation(
            "CliAppPlaytester: starting CLI-agentic playtest — {Count} scenario(s), app at {Url}",
            scenariosToRun.Count, handle.BaseUrl);

        var reports = new List<PlaytestReport>(scenariosToRun.Count);

        foreach (var scenario in scenariosToRun)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "CliAppPlaytester: verifying scenario {Id} — {Title}",
                scenario.Id, scenario.Title);

            var report = await VerifyScenarioAsync(scenario, handle, ct);
            reports.Add(report);

            _logger.LogInformation(
                "CliAppPlaytester: scenario {Id} → {Verdict} (confidence: {Confidence:P0})",
                scenario.Id, report.Verdict, report.Confidence);
        }

        _logger.LogInformation(
            "CliAppPlaytester: run complete — {Verified} verified, {Broken} broken, {Inconclusive} inconclusive",
            reports.Count(r => r.Verdict == VerificationStatus.Verified),
            reports.Count(r => r.Verdict == VerificationStatus.Broken),
            reports.Count(r => r.Verdict == VerificationStatus.Inconclusive));

        return [.. reports];
    }

    // ─── Per-scenario CLI verification ───────────────────────────────────────

    private async Task<PlaytestReport> VerifyScenarioAsync(
        Scenario scenario,
        AppHandle handle,
        CancellationToken ct)
    {
        // Precondition: app must have a URL for the CLI agent to navigate to
        if (string.IsNullOrWhiteSpace(handle.BaseUrl))
        {
            return MakeReport(scenario, VerificationStatus.Inconclusive, 0.0,
                executionError: "No app URL available — cannot run CLI verification",
                ambiguityNote: "AppHandle.BaseUrl is empty. The app may not be running.");
        }

        var prompt = BuildVerificationPrompt(scenario, handle);

        // Some complex apps can take many minutes (45+) to verify. We deliberately
        // remove the hard wall-clock cap (WallClockTimeoutSeconds=0 => Infinite) and rely solely on the
        // stall watchdog: the session is only killed if it produces NO meaningful log output for
        // StuckSeconds (default 5 min), which is the real "something is stuck" signal.
        var stuckSeconds = _verifyConfig.StuckSeconds > 0 ? _verifyConfig.StuckSeconds : DefaultStuckSeconds;
        var wallClock = _verifyConfig.WallClockTimeoutSeconds <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(_verifyConfig.WallClockTimeoutSeconds);

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            WorkingDirectory = handle.WorkspacePath ?? Path.GetTempPath(),
            Timeout = wallClock,
            StuckSecondsOverride = _verifyConfig.StuckSeconds,
            WatchdogMode = CopilotCliWatchdogMode.Agentic,
        };

        AgenticSessionResult result;
        try
        {
            result = await _processManager.ExecuteAgenticSessionAsync(prompt, options, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CliAppPlaytester: CLI session failed for {Id}", scenario.Id);
            return MakeReport(scenario, VerificationStatus.Inconclusive, 0.0,
                executionError: $"CLI session error: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "CliAppPlaytester: CLI session did not succeed for {Id}: {Reason} — {Error}",
                scenario.Id, result.FailureReason, result.ErrorMessage);

            // A genuine stall (no log output for StuckSeconds) is the only "stuck" condition now that the
            // wall-clock cap is removed. Surface it to FlowMonitor so an operator is alerted instead of the
            // PR silently shipping with an Inconclusive scenario.
            if (result.FailureReason == AgenticFailureReason.StuckNoOutput)
                RaiseStuckFinding(scenario, handle, stuckSeconds);

            return MakeReport(scenario, VerificationStatus.Inconclusive, 0.0,
                executionError: $"{result.FailureReason}: {result.ErrorMessage}",
                rawOutput: result.LogBuffer);
        }

        // Parse the CLI output for the verdict
        var finalOutput = CliOutputParser.ParseJsonOutput(result.LogBuffer);
        if (string.IsNullOrWhiteSpace(finalOutput))
            finalOutput = CliOutputParser.Parse(result.LogBuffer);

        return ParseVerdict(scenario, finalOutput, result);
    }

    // ─── Prompt builder ──────────────────────────────────────────────────────

    private static string BuildVerificationPrompt(Scenario scenario, AppHandle handle)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("# Scenario Verification Task");
        sb.AppendLine();
        sb.AppendLine("You are a QA verifier. Your job is to verify ONE scenario against a running application.");
        sb.AppendLine("You have access to Playwright MCP tools to navigate and interact with the app.");
        sb.AppendLine();

        // App info
        sb.AppendLine("## Application Under Test");
        sb.AppendLine($"- **URL**: {handle.BaseUrl}");
        sb.AppendLine($"- **Type**: {handle.TargetType}");
        if (!string.IsNullOrWhiteSpace(handle.WorkspacePath))
            sb.AppendLine($"- **Workspace**: {handle.WorkspacePath}");
        sb.AppendLine();

        // Scenario details
        sb.AppendLine("## Scenario to Verify");
        sb.AppendLine($"- **ID**: {scenario.Id}");
        sb.AppendLine($"- **Title**: {scenario.Title}");
        sb.AppendLine($"- **Journey Kind**: {scenario.JourneyKind}");
        sb.AppendLine($"- **Actor**: {scenario.Actor}");
        sb.AppendLine($"- **Trigger**: {scenario.Trigger}");
        sb.AppendLine($"- **Priority**: {scenario.Priority}");
        sb.AppendLine();

        if (scenario.Preconditions.Count > 0)
        {
            sb.AppendLine("### Preconditions");
            foreach (var pre in scenario.Preconditions)
                sb.AppendLine($"- {pre}");
            sb.AppendLine();
        }

        if (scenario.Steps.Count > 0)
        {
            sb.AppendLine("### Steps");
            for (int i = 0; i < scenario.Steps.Count; i++)
                sb.AppendLine($"{i + 1}. {scenario.Steps[i]}");
            sb.AppendLine();
        }

        if (scenario.ExpectedTerminalState.Count > 0)
        {
            sb.AppendLine("### Expected Terminal State (Acceptance Criteria)");
            foreach (var state in scenario.ExpectedTerminalState)
                sb.AppendLine($"- {state}");
            sb.AppendLine();
        }

        if (scenario.ObservationSurfaces.Count > 0)
        {
            sb.AppendLine("### Observation Surfaces");
            foreach (var surface in scenario.ObservationSurfaces)
            {
                sb.AppendLine($"- **{surface.Kind}**");
                foreach (var (key, value) in surface.Fields)
                    sb.AppendLine($"  - {key}: {value}");
            }
            sb.AppendLine();
        }

        // Instructions
        sb.AppendLine("## Verification Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Navigate to the application URL using Playwright browser tools.");
        sb.AppendLine("2. Follow the scenario steps in order.");
        sb.AppendLine("3. At each step, observe the actual behavior.");
        sb.AppendLine("4. Check each acceptance criterion in the Expected Terminal State.");
        sb.AppendLine("5. Report your findings.");
        sb.AppendLine();

        // Rules
        sb.AppendLine("## Rules");
        sb.AppendLine("- Do NOT modify any files in the workspace.");
        sb.AppendLine("- Do NOT run any build or install commands.");
        sb.AppendLine("- ONLY use browser/navigation tools to verify the running app.");
        if (!scenario.InteractiveValidationSafe)
        {
            sb.AppendLine("- ⚠️ THIS SCENARIO INVOLVES POTENTIALLY DESTRUCTIVE ACTIONS. Test as much as possible — navigate to the relevant UI, fill forms, verify preconditions — but STOP BEFORE executing any irreversible action (delete, archive, purge, revoke, disable, drop, remove permanently). Verify the confirmation dialog/prompt exists but do NOT click the final confirm button.");
            sb.AppendLine("- Do NOT interact with external production systems (Azure, ADO, GitHub, AWS, databases) in any way that modifies state. Read-only queries and connection tests are allowed.");
        }
        sb.AppendLine("- If you cannot navigate to the URL, report Inconclusive.");
        sb.AppendLine("- If you observe unmet acceptance criteria, report Broken.");
        sb.AppendLine("- Do NOT infer pass from absence of errors — verify positively.");
        sb.AppendLine("- If a criterion cannot be observed (e.g., backend-only), note it but don't fail.");
        sb.AppendLine();

        // Verdict format
        sb.AppendLine("## Required Output Format");
        sb.AppendLine();
        sb.AppendLine("After completing your verification, you MUST end your response with EXACTLY this block:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("=== SCENARIO VERDICT ===");
        sb.AppendLine("SCENARIO_ID: S01");
        sb.AppendLine("RESULT: Verified");
        sb.AppendLine("CONFIDENCE: 85");
        sb.AppendLine("NOTES: Brief description of what you observed");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("RESULT must be one of: Verified, Broken, Inconclusive");
        sb.AppendLine("CONFIDENCE must be 0-100 (integer).");
        sb.AppendLine("NOTES should briefly describe what you observed and which criteria passed/failed.");
        sb.AppendLine();
        sb.AppendLine("This verdict block is MANDATORY. Always include it as the very last thing in your response.");

        return sb.ToString();
    }

    // ─── Verdict parser ──────────────────────────────────────────────────────

    private static readonly Regex VerdictBlockRegex = new(
        @"===\s*SCENARIO\s+VERDICT\s*===",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResultRegex = new(
        @"RESULT\s*:\s*(Verified|Broken|Inconclusive|Pass(?:ed)?|Fail(?:ed)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConfidenceRegex = new(
        @"CONFIDENCE\s*:\s*(\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NotesRegex = new(
        @"NOTES\s*:\s*(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private PlaytestReport ParseVerdict(Scenario scenario, string output, AgenticSessionResult session)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning("CliAppPlaytester: empty output for {Id} — marking Inconclusive", scenario.Id);
            return MakeReport(scenario, VerificationStatus.Inconclusive, 0.0,
                executionError: "CLI session produced no output",
                rawOutput: session.LogBuffer);
        }

        // Try to find the verdict block marker
        var verdictBlockMatch = VerdictBlockRegex.Match(output);
        var searchText = verdictBlockMatch.Success
            ? output[verdictBlockMatch.Index..]
            : GetLastLines(output, 30); // fallback: search last 30 lines

        // Parse RESULT
        var resultMatch = ResultRegex.Match(searchText);
        VerificationStatus verdict;
        if (resultMatch.Success)
        {
            verdict = ParseResultKeyword(resultMatch.Groups[1].Value);
        }
        else
        {
            // Last-resort: scan for verdict keywords in the last lines
            verdict = InferVerdictFromText(searchText);
            _logger.LogWarning(
                "CliAppPlaytester: no RESULT marker found for {Id}, inferred {Verdict} from text",
                scenario.Id, verdict);
        }

        // Parse CONFIDENCE
        double confidence = 0.5; // default
        var confMatch = ConfidenceRegex.Match(searchText);
        if (confMatch.Success && int.TryParse(confMatch.Groups[1].Value, out var confInt))
        {
            confidence = Math.Clamp(confInt / 100.0, 0.0, 1.0);
        }

        // Parse NOTES
        string? notes = null;
        var notesMatch = NotesRegex.Match(searchText);
        if (notesMatch.Success)
        {
            notes = notesMatch.Groups[1].Value.Trim();
        }

        return MakeReport(scenario, verdict, confidence,
            ambiguityNote: notes,
            rawOutput: session.LogBuffer);
    }

    private static VerificationStatus ParseResultKeyword(string keyword)
    {
        return keyword.Trim().ToLowerInvariant() switch
        {
            "verified" or "pass" or "passed" => VerificationStatus.Verified,
            "broken" or "fail" or "failed" => VerificationStatus.Broken,
            _ => VerificationStatus.Inconclusive,
        };
    }

    /// <summary>
    /// Last-resort: scan text for strong verdict keywords.
    /// Only triggered when the CLI didn't produce a RESULT marker.
    /// </summary>
    private static VerificationStatus InferVerdictFromText(string text)
    {
        var lower = text.ToLowerInvariant();

        // Look for strong negative signals first (fail/broken = bad)
        if (lower.Contains("scenario is broken") || lower.Contains("verdict: broken") ||
            lower.Contains("scenario failed") || lower.Contains("verdict: fail"))
            return VerificationStatus.Broken;

        // Look for strong positive signals
        if (lower.Contains("scenario is verified") || lower.Contains("verdict: verified") ||
            lower.Contains("all criteria met") || lower.Contains("all acceptance criteria") ||
            lower.Contains("scenario passed") || lower.Contains("verdict: pass"))
            return VerificationStatus.Verified;

        return VerificationStatus.Inconclusive;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetLastLines(string text, int lineCount)
    {
        var lines = text.Split('\n');
        var start = Math.Max(0, lines.Length - lineCount);
        return string.Join('\n', lines[start..]);
    }

    /// <summary>
    /// Raises a Critical FlowMonitor finding when a scenario's live verification session stalled (no
    /// meaningful log output for <paramref name="stuckSeconds"/>). Best-effort and deduped per scenario.
    /// </summary>
    private void RaiseStuckFinding(Scenario scenario, AppHandle handle, int stuckSeconds)
    {
        if (!_verifyConfig.RaiseFlowMonitorFindingOnStuck || _flowPersistence is null)
            return;

        try
        {
            var minutes = Math.Round(stuckSeconds / 60.0, 1);
            var finding = new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = DateTimeOffset.UtcNow,
                DetectorId = "scenario-verification-stuck",
                Severity = FlowFindingSeverity.Critical,
                TargetAgentId = AgentCallContext.CurrentAgentId,
                TargetResource = scenario.Id,
                Summary = $"Live verification of scenario {scenario.Id} stalled (no log activity for {minutes} min)",
                Rationale =
                    $"The AppPlaytester agentic session verifying scenario '{scenario.Title}' ({scenario.Id}) produced no " +
                    $"meaningful output for {stuckSeconds}s and was killed by the stall watchdog. There is no wall-clock cap " +
                    $"on verification, so this fired only because output went completely silent — the app under test " +
                    $"({(string.IsNullOrWhiteSpace(handle.BaseUrl) ? "unknown URL" : handle.BaseUrl)}) may be unresponsive, " +
                    $"the browser/Playwright MCP tooling may have hung, or the agent is blocked.",
                DedupKey = $"scenario-verification-stuck:{scenario.Id}",
            };

            var inserted = _flowPersistence.InsertFinding(finding, TimeSpan.FromHours(1));
            if (inserted)
                _logger.LogWarning(
                    "Raised FlowMonitor finding: scenario {Id} verification stalled (no output for {Seconds}s)",
                    scenario.Id, stuckSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to raise FlowMonitor stuck finding for scenario {Id}", scenario.Id);
        }
    }

    private static PlaytestReport MakeReport(
        Scenario scenario,
        VerificationStatus verdict,
        double confidence,
        string? executionError = null,
        string? ambiguityNote = null,
        string? rawOutput = null)
    {
        return new PlaytestReport
        {
            ScenarioId = scenario.Id,
            Title = scenario.Title,
            JourneyKind = scenario.JourneyKind.ToString().ToLowerInvariant(),
            Priority = scenario.Priority.ToString().ToLowerInvariant(),
            Verdict = verdict,
            Confidence = confidence,
            OperatorReviewRequired = verdict != VerificationStatus.Verified,
            AmbiguityNote = ambiguityNote,
            ExecutionError = executionError,
            // CLI-agentic mode doesn't produce these artifacts
            ActionPlanExecuted = null,
            Evidence = [],
            FailedSurfaces = [],
            Layer2VisionNote = null,
            NarrativeAssessment = null,
            // All layers are collapsed into one CLI session
            Layer1Result = verdict,
            Layer2Result = VerificationStatus.Inconclusive,
            Layer3Result = VerificationStatus.Inconclusive,
        };
    }
}
