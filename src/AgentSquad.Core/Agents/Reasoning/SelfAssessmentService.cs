using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.Agents.Reasoning;

/// <summary>
/// Result of a self-assessment: whether the output passes quality criteria and what gaps remain.
/// </summary>
public record AssessmentResult
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<string> Gaps { get; init; }
    public required string Summary { get; init; }

    /// <summary>AI-reported confidence percentage (0-100). Null if not parsed or threshold disabled.</summary>
    public int? Confidence { get; init; }

    /// <summary>Severity of each gap: "critical", "major", or "minor".</summary>
    public IReadOnlyList<string> GapSeverities { get; init; } = [];

    // --- Inline decision impact classification (piggybacked on assessment) ---

    /// <summary>Impact level classified during assessment. Null if classification was not requested or failed to parse.</summary>
    public Decisions.DecisionImpactLevel? ImpactLevel { get; init; }

    /// <summary>Why this impact level was assigned.</summary>
    public string? ImpactRationale { get; init; }

    /// <summary>Alternatives the AI considered for the decision.</summary>
    public string? Alternatives { get; init; }

    /// <summary>Files or modules affected by the decision.</summary>
    public string? AffectedFiles { get; init; }

    /// <summary>Risk assessment for the decision.</summary>
    public string? RiskAssessment { get; init; }

    /// <summary>Whether inline impact classification was included in this assessment.</summary>
    public bool HasImpactClassification => ImpactLevel.HasValue;
}

/// <summary>
/// The agentic self-assessment loop. Wraps document generation with an assess → refine cycle.
/// When enabled, agents generate output, assess it against role-specific criteria,
/// and refine until criteria are met or max iterations reached.
///
/// This is the core "observe → act → iterate" pattern that makes agents self-correcting.
/// All steps are logged to <see cref="IAgentReasoningLog"/> for human observability.
/// </summary>
public class SelfAssessmentService
{
    private readonly IAgentReasoningLog _reasoningLog;
    private readonly AgenticLoopConfig _config;
    private readonly ILogger<SelfAssessmentService> _logger;

    public SelfAssessmentService(
        IAgentReasoningLog reasoningLog,
        IOptions<AgentSquadConfig> config,
        ILogger<SelfAssessmentService> logger)
    {
        _reasoningLog = reasoningLog;
        _config = config.Value.AgenticLoop;
        _logger = logger;
    }

    /// <summary>
    /// Run the agentic self-assessment loop on generated output.
    /// If agentic loop is disabled for this role, returns the original output unchanged.
    /// </summary>
    /// <param name="agentId">Agent identity for logging.</param>
    /// <param name="agentDisplayName">Human-readable agent name.</param>
    /// <param name="role">Agent role (for per-role config check).</param>
    /// <param name="phase">Current workflow phase name (for logging).</param>
    /// <param name="generatedOutput">The initial output from the agent's generation step.</param>
    /// <param name="assessmentCriteria">Role-specific criteria the output must meet.</param>
    /// <param name="contextForRefinement">Additional context to include in refinement prompts (e.g., project description, upstream docs).</param>
    /// <param name="chat">The chat completion service to use for assessment/refinement AI calls.</param>
    /// <param name="classifyImpact">When true, the assessment also classifies the decision's impact level inline (saves a separate LLM call).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assessment result containing the final output and optional impact classification.</returns>
    public async Task<(string FinalOutput, AssessmentResult? LastAssessment)> AssessAndRefineWithResultAsync(
        string agentId,
        string agentDisplayName,
        AgentRole role,
        string phase,
        string generatedOutput,
        string assessmentCriteria,
        string contextForRefinement,
        IChatCompletionService chat,
        bool classifyImpact = false,
        CancellationToken ct = default)
    {
        if (!_config.IsEnabledForRole(role))
        {
            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = agentId,
                AgentDisplayName = agentDisplayName,
                EventType = AgentReasoningEventType.Decision,
                Phase = phase,
                Summary = "Agentic loop disabled for this role — publishing first draft",
                Iteration = 0,
            });
            return (generatedOutput, null);
        }

        var roleName = role.ToString();
        var maxIterations = _config.Roles.TryGetValue(roleName, out var roleConfig)
            ? roleConfig.MaxIterations ?? _config.MaxIterations
            : _config.MaxIterations;

        var currentOutput = generatedOutput;
        AssessmentResult? lastAssessment = null;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            // --- ASSESS ---
            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = agentId,
                AgentDisplayName = agentDisplayName,
                EventType = AgentReasoningEventType.Assessing,
                Phase = phase,
                Summary = $"Self-assessing output (iteration {iteration + 1}/{maxIterations})",
                Iteration = iteration,
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Only include impact classification on the FINAL assessment (when it passes or last iteration)
            var isLastIteration = iteration == maxIterations - 1;
            var assessment = await AssessOutputAsync(currentOutput, assessmentCriteria, chat,
                classifyImpact && (isLastIteration || iteration == 0), ct);
            sw.Stop();
            lastAssessment = assessment;

            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = agentId,
                AgentDisplayName = agentDisplayName,
                EventType = AgentReasoningEventType.Assessing,
                Phase = phase,
                Summary = assessment.Passed
                    ? $"Assessment PASSED — output meets all criteria"
                    : $"Assessment found {assessment.Gaps.Count} gap(s)",
                Detail = assessment.Summary,
                Gaps = assessment.Gaps,
                Passed = assessment.Passed,
                Iteration = iteration,
                Duration = sw.Elapsed,
            });

            if (assessment.Passed)
            {
                // If we didn't classify on this iteration, do a quick re-assess with classification
                if (classifyImpact && !assessment.HasImpactClassification)
                {
                    lastAssessment = await AssessOutputAsync(currentOutput, assessmentCriteria, chat, true, ct);
                }

                _reasoningLog.Log(new AgentReasoningEvent
                {
                    AgentId = agentId,
                    AgentDisplayName = agentDisplayName,
                    EventType = AgentReasoningEventType.Decision,
                    Phase = phase,
                    Summary = $"Output approved after {iteration + 1} assessment(s) — ready to publish",
                    Iteration = iteration,
                });

                _logger.LogInformation(
                    "[{Agent}] Self-assessment passed on iteration {Iteration}",
                    agentDisplayName, iteration + 1);
                return (currentOutput, lastAssessment);
            }

            // --- CONFIDENCE THRESHOLD CHECK ---
            if (_config.ConfidenceThreshold.Enabled && assessment.Confidence.HasValue)
            {
                var hasBlockingGaps = assessment.GapSeverities.Any(s =>
                    s.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("major", StringComparison.OrdinalIgnoreCase));

                if (assessment.Confidence.Value >= _config.ConfidenceThreshold.MinConfidence && !hasBlockingGaps)
                {
                    _reasoningLog.Log(new AgentReasoningEvent
                    {
                        AgentId = agentId,
                        AgentDisplayName = agentDisplayName,
                        EventType = AgentReasoningEventType.Decision,
                        Phase = phase,
                        Summary = $"Confidence {assessment.Confidence}% ≥ {_config.ConfidenceThreshold.MinConfidence}% with only minor gaps — skipping refinement",
                        Detail = $"Gaps (minor only): {string.Join("; ", assessment.Gaps)}",
                        Passed = true,
                        Iteration = iteration,
                    });

                    _logger.LogInformation(
                        "[{Agent}] Confidence threshold met ({Confidence}% ≥ {Threshold}%), skipping refinement",
                        agentDisplayName, assessment.Confidence.Value, _config.ConfidenceThreshold.MinConfidence);
                    return (currentOutput, lastAssessment);
                }
            }

            // --- REFINE ---
            _logger.LogInformation(
                "[{Agent}] Self-assessment found {GapCount} gaps on iteration {Iteration}, refining",
                agentDisplayName, assessment.Gaps.Count, iteration + 1);

            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = agentId,
                AgentDisplayName = agentDisplayName,
                EventType = AgentReasoningEventType.Refining,
                Phase = phase,
                Summary = $"Refining output to address {assessment.Gaps.Count} gap(s)",
                Detail = string.Join("\n", assessment.Gaps.Select((g, i) => $"{i + 1}. {g}")),
                Iteration = iteration,
            });

            sw.Restart();
            currentOutput = await RefineOutputAsync(
                currentOutput, assessment, assessmentCriteria, contextForRefinement, chat, ct);
            sw.Stop();

            _reasoningLog.Log(new AgentReasoningEvent
            {
                AgentId = agentId,
                AgentDisplayName = agentDisplayName,
                EventType = AgentReasoningEventType.Refining,
                Phase = phase,
                Summary = $"Refinement complete (iteration {iteration + 1})",
                Iteration = iteration,
                Duration = sw.Elapsed,
            });
        }

        // Max iterations exhausted — publish best effort
        _reasoningLog.Log(new AgentReasoningEvent
        {
            AgentId = agentId,
            AgentDisplayName = agentDisplayName,
            EventType = AgentReasoningEventType.Decision,
            Phase = phase,
            Summary = $"Max iterations ({maxIterations}) reached — publishing best effort",
            Iteration = maxIterations,
        });

        _logger.LogWarning(
            "[{Agent}] Self-assessment exhausted {Max} iterations, publishing best effort",
            agentDisplayName, maxIterations);
        return (currentOutput, lastAssessment);
    }

    /// <summary>
    /// Run the agentic self-assessment loop on generated output.
    /// If agentic loop is disabled for this role, returns the original output unchanged.
    /// This is the backward-compatible overload that returns just the final output string.
    /// </summary>
    public async Task<string> AssessAndRefineAsync(
        string agentId,
        string agentDisplayName,
        AgentRole role,
        string phase,
        string generatedOutput,
        string assessmentCriteria,
        string contextForRefinement,
        IChatCompletionService chat,
        CancellationToken ct = default)
    {
        var (finalOutput, _) = await AssessAndRefineWithResultAsync(
            agentId, agentDisplayName, role, phase, generatedOutput,
            assessmentCriteria, contextForRefinement, chat,
            classifyImpact: false, ct);
        return finalOutput;
    }

    /// <summary>
    /// Ask the AI to assess output against specific criteria.
    /// When classifyImpact is true, also includes decision impact classification in the same call.
    /// Returns structured pass/fail with specific gaps and optional impact classification.
    /// </summary>
    private async Task<AssessmentResult> AssessOutputAsync(
        string output,
        string criteria,
        IChatCompletionService chat,
        bool classifyImpact,
        CancellationToken ct)
    {
        var history = new ChatHistory();

        var useConfidence = _config.ConfidenceThreshold.Enabled;
        var formatInstructions = useConfidence
            ? "Respond in this EXACT format:\n" +
              "VERDICT: PASS or FAIL\n" +
              "CONFIDENCE: <number 0-100>%\n" +
              "SUMMARY: One sentence overall assessment\n" +
              "GAPS:\n" +
              "- [critical|major|minor] Gap description 1\n" +
              "- [critical|major|minor] Gap description 2\n" +
              "(leave GAPS empty if PASS)\n\n" +
              "Severity guide: critical = would cause failures or missing core requirements, " +
              "major = significant quality gap, minor = polish or nice-to-have."
            : "Respond in this EXACT format:\n" +
              "VERDICT: PASS or FAIL\n" +
              "SUMMARY: One sentence overall assessment\n" +
              "GAPS:\n" +
              "- Gap description 1\n" +
              "- Gap description 2\n" +
              "(leave GAPS empty if PASS)";

        if (classifyImpact)
        {
            formatInstructions += "\n\n" + ImpactClassificationInstructions;
        }

        history.AddSystemMessage(
            "You are a strict quality assessor. Your job is to evaluate a document against specific criteria " +
            "and identify any gaps or weaknesses. Be thorough but fair — only flag genuine gaps, not stylistic preferences.\n\n" +
            formatInstructions);

        history.AddUserMessage(
            $"## Assessment Criteria\n{criteria}\n\n" +
            $"## Document to Assess\n{output}");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var responseText = response.Content ?? "";

        return ParseAssessment(responseText, classifyImpact);
    }

    /// <summary>
    /// Ask the AI to refine the output, addressing the specific gaps found by assessment.
    /// </summary>
    private async Task<string> RefineOutputAsync(
        string currentOutput,
        AssessmentResult assessment,
        string criteria,
        string context,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are refining a document to address specific quality gaps identified by a reviewer. " +
            "Produce the COMPLETE updated document — do not produce a partial diff or just the changed sections. " +
            "Preserve all existing good content while addressing the gaps.");

        var gapList = string.Join("\n", assessment.Gaps.Select((g, i) => $"{i + 1}. {g}"));

        history.AddUserMessage(
            $"## Quality Criteria\n{criteria}\n\n" +
            (string.IsNullOrEmpty(context) ? "" : $"## Additional Context\n{context}\n\n") +
            $"## Gaps to Address\n{gapList}\n\n" +
            $"## Current Document (needs refinement)\n{currentOutput}\n\n" +
            "Produce the complete refined document that addresses ALL the gaps above while preserving existing quality content.");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content ?? currentOutput;
    }

    /// <summary>Parse the structured assessment response from the AI.</summary>
    internal static AssessmentResult ParseAssessment(string response, bool parseImpact = false)
    {
        var lines = response.Split('\n', StringSplitOptions.TrimEntries);
        var passed = false;
        var summary = "";
        int? confidence = null;
        var gaps = new List<string>();
        var severities = new List<string>();
        var inGaps = false;

        // Impact classification fields
        Decisions.DecisionImpactLevel? impactLevel = null;
        string? impactRationale = null;
        string? alternatives = null;
        string? affectedFiles = null;
        string? riskAssessment = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))
            {
                var verdict = line["VERDICT:".Length..].Trim();
                passed = verdict.Contains("PASS", StringComparison.OrdinalIgnoreCase);
                inGaps = false;
            }
            else if (line.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase))
            {
                var confText = line["CONFIDENCE:".Length..].Trim().TrimEnd('%');
                if (int.TryParse(confText, out var confValue))
                    confidence = Math.Clamp(confValue, 0, 100);
                inGaps = false;
            }
            else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["SUMMARY:".Length..].Trim();
                inGaps = false;
            }
            else if (line.StartsWith("GAPS:", StringComparison.OrdinalIgnoreCase))
            {
                inGaps = true;
                var gapContent = line["GAPS:".Length..].Trim();
                if (gapContent.StartsWith("- ") || gapContent.StartsWith("* "))
                    ParseGapLine(gapContent[2..].Trim(), gaps, severities);
            }
            else if (line.StartsWith("IMPACT:", StringComparison.OrdinalIgnoreCase) && parseImpact)
            {
                inGaps = false;
                var value = line["IMPACT:".Length..].Trim().ToUpperInvariant();
                impactLevel = value switch
                {
                    "XS" => Decisions.DecisionImpactLevel.XS,
                    "S" => Decisions.DecisionImpactLevel.S,
                    "M" => Decisions.DecisionImpactLevel.M,
                    "L" => Decisions.DecisionImpactLevel.L,
                    "XL" => Decisions.DecisionImpactLevel.XL,
                    _ => null,
                };
            }
            else if (line.StartsWith("IMPACT_RATIONALE:", StringComparison.OrdinalIgnoreCase) && parseImpact)
            {
                inGaps = false;
                impactRationale = line["IMPACT_RATIONALE:".Length..].Trim();
            }
            else if (line.StartsWith("ALTERNATIVES:", StringComparison.OrdinalIgnoreCase) && parseImpact)
            {
                inGaps = false;
                alternatives = line["ALTERNATIVES:".Length..].Trim();
            }
            else if (line.StartsWith("AFFECTED_FILES:", StringComparison.OrdinalIgnoreCase) && parseImpact)
            {
                inGaps = false;
                affectedFiles = line["AFFECTED_FILES:".Length..].Trim();
            }
            else if (line.StartsWith("RISK:", StringComparison.OrdinalIgnoreCase) && parseImpact)
            {
                inGaps = false;
                riskAssessment = line["RISK:".Length..].Trim();
            }
            else if (inGaps && (line.StartsWith("- ") || line.StartsWith("* ")))
            {
                var gap = line[2..].Trim();
                if (!string.IsNullOrWhiteSpace(gap))
                    ParseGapLine(gap, gaps, severities);
            }
            else if (inGaps && line.StartsWith("(") && line.Contains("empty"))
            {
                continue;
            }
            else if (inGaps && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("-") && !line.StartsWith("*"))
            {
                var numberMatch = System.Text.RegularExpressions.Regex.Match(line, @"^\d+\.\s+(.+)");
                if (numberMatch.Success)
                    ParseGapLine(numberMatch.Groups[1].Value.Trim(), gaps, severities);
            }
        }

        // Sanity: if verdict says PASS but there are gaps, trust the gaps
        if (gaps.Count > 0) passed = false;

        return new AssessmentResult
        {
            Passed = passed,
            Gaps = gaps,
            Summary = string.IsNullOrEmpty(summary) ? (passed ? "All criteria met." : "Gaps found.") : summary,
            Confidence = confidence,
            GapSeverities = severities,
            ImpactLevel = impactLevel,
            ImpactRationale = impactRationale,
            Alternatives = alternatives,
            AffectedFiles = affectedFiles,
            RiskAssessment = riskAssessment,
        };
    }

    /// <summary>
    /// Parse a gap line, extracting optional [severity] prefix.
    /// Examples: "[critical] Missing error handling" → gap="Missing error handling", severity="critical"
    /// "Missing error handling" → gap="Missing error handling", severity="unknown"
    /// </summary>
    private static void ParseGapLine(string text, List<string> gaps, List<string> severities)
    {
        var severityMatch = System.Text.RegularExpressions.Regex.Match(text, @"^\[(critical|major|minor)\]\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (severityMatch.Success)
        {
            severities.Add(severityMatch.Groups[1].Value.ToLowerInvariant());
            gaps.Add(severityMatch.Groups[2].Value.Trim());
        }
        else
        {
            severities.Add("unknown");
            gaps.Add(text);
        }
    }

    /// <summary>
    /// Additional prompt instructions appended to assessment when inline impact classification is requested.
    /// This allows the assessment to piggyback impact classification without a separate LLM call.
    /// </summary>
    private const string ImpactClassificationInstructions = """

        Additionally, classify the IMPACT LEVEL of the changes described in this document.
        After your assessment (VERDICT/SUMMARY/GAPS), add these fields:

        IMPACT: [XS|S|M|L|XL]
        IMPACT_RATIONALE: [Why this impact level — one sentence]
        ALTERNATIVES: [What alternatives were considered, if apparent from the document]
        AFFECTED_FILES: [Files or modules that will be affected]
        RISK: [What could go wrong with these changes]

        Impact levels:
        - XS (Extra Small): Cosmetic — CSS tweaks, comment fixes, formatting, typos.
        - S (Small): Low-risk isolated — utility method, config value, simple bug fix in one file.
        - M (Medium): Moderate structural — refactoring a class, changing API signatures, new dependency.
        - L (Large): Significant architectural — new service/module, schema change, core abstraction change.
        - XL (Extra Large): Project-defining — restructure layout, change tech stack, major feature pivot.

        Be precise and conservative. When in doubt, choose the higher level.
        """;
}
