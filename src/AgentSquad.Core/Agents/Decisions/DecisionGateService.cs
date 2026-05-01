using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.Agents.Decisions;

/// <summary>
/// Service that classifies agent decisions by impact level, generates implementation plans
/// for gated decisions, and integrates with the gate notification system for human approval.
/// </summary>
public class DecisionGateService
{
    private readonly IDecisionLog _decisionLog;
    private readonly GateNotificationService _gateNotificationService;
    private readonly IChatCompletionRunner _chatRunner;
    private readonly DecisionGatingConfig _config;
    private readonly ILogger<DecisionGateService> _logger;

    /// <summary>Gate ID prefix for decision gates in the notification system.</summary>
    public const string DecisionGatePrefix = "Decision:";

    public DecisionGateService(
        IDecisionLog decisionLog,
        GateNotificationService gateNotificationService,
        IChatCompletionRunner chatRunner,
        IOptions<AgentSquadConfig> config,
        ILogger<DecisionGateService> logger)
    {
        _decisionLog = decisionLog;
        _gateNotificationService = gateNotificationService;
        _chatRunner = chatRunner;
        _config = config.Value.DecisionGating;
        _logger = logger;
    }

    /// <summary>
    /// Create a decision from pre-classified assessment results (inline classification).
    /// Skips the separate classification LLM call since impact was already determined during self-assessment.
    /// Still generates a plan for gated decisions (requires a separate LLM call).
    /// </summary>
    public async Task<AgentDecision> ClassifyFromAssessmentAsync(
        string agentId,
        string agentDisplayName,
        string phase,
        string title,
        string context,
        Reasoning.AssessmentResult assessment,
        string? category = null,
        string modelTier = "standard",
        CancellationToken ct = default)
    {
        if (!_config.Enabled || !assessment.HasImpactClassification)
        {
            // Fall back to separate classification if gating disabled or no inline classification
            return await ClassifyAndGateDecisionAsync(agentId, agentDisplayName, phase, title, context, category, modelTier, ct);
        }

        var impactLevel = assessment.ImpactLevel!.Value;
        var requiresGate = _config.RequiresGate(impactLevel);

        string? plan = null;
        if (requiresGate && _config.RequirePlanForGated && _config.MaxDecisionTurns >= 2)
        {
            plan = await GeneratePlanAsync(agentDisplayName, title, context,
                assessment.ImpactRationale ?? "No rationale provided",
                assessment.Alternatives, assessment.AffectedFiles, assessment.RiskAssessment,
                modelTier, ct);
        }

        var status = requiresGate ? DecisionStatus.Pending : DecisionStatus.AutoApproved;
        var decision = new AgentDecision
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            AgentId = agentId,
            AgentDisplayName = agentDisplayName,
            Phase = phase,
            ImpactLevel = impactLevel,
            Title = title,
            Rationale = assessment.ImpactRationale ?? context,
            Alternatives = assessment.Alternatives,
            AffectedFiles = assessment.AffectedFiles,
            RiskAssessment = assessment.RiskAssessment,
            Plan = plan,
            Status = status,
            Category = category,
        };

        _decisionLog.Log(decision);

        if (requiresGate)
        {
            var gateContext = FormatGateContext(decision);
            await _gateNotificationService.AddNotificationAsync(
                $"{DecisionGatePrefix}{decision.Id}",
                gateContext,
                ct: ct);

            _logger.LogWarning(
                "[{Agent}] Decision GATED [{Impact}] (inline classification): {Title} — awaiting human approval",
                agentDisplayName, impactLevel, title);
        }
        else
        {
            _logger.LogInformation(
                "[{Agent}] Decision auto-approved [{Impact}] (inline classification): {Title}",
                agentDisplayName, impactLevel, title);
        }

        return decision;
    }

    /// <summary>
    /// Classify a decision's impact level using AI, generate a plan if gated,
    /// and either auto-approve or wait for human approval.
    /// </summary>
    /// <param name="agentId">The agent making the decision.</param>
    /// <param name="agentDisplayName">Display name of the agent.</param>
    /// <param name="phase">Current workflow phase.</param>
    /// <param name="title">Short title of the decision.</param>
    /// <param name="context">Full context about the decision (what, why, affected areas).</param>
    /// <param name="category">Category for filtering (e.g., "Architecture", "Refactoring").</param>
    /// <param name="modelTier">Model tier to use for classification AI calls.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The classified and potentially gated decision. If gated, Status will be Pending until resolved.</returns>
    public async Task<AgentDecision> ClassifyAndGateDecisionAsync(
        string agentId,
        string agentDisplayName,
        string phase,
        string title,
        string context,
        string? category = null,
        string modelTier = "standard",
        CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            // Gating disabled — classify but auto-approve everything
            var autoDecision = CreateDecision(agentId, agentDisplayName, phase, title, context, category,
                DecisionImpactLevel.XS, DecisionStatus.AutoApproved);
            _decisionLog.Log(autoDecision);
            return autoDecision;
        }

        // Turn 1: Classify impact level
        var (impactLevel, rationale, alternatives, affectedFiles, riskAssessment) =
            await ClassifyImpactAsync(agentDisplayName, phase, title, context, modelTier, ct);

        var requiresGate = _config.RequiresGate(impactLevel);

        string? plan = null;
        if (requiresGate && _config.RequirePlanForGated && _config.MaxDecisionTurns >= 2)
        {
            // Turn 2: Generate structured plan for human review
            plan = await GeneratePlanAsync(agentDisplayName, title, context, rationale,
                alternatives, affectedFiles, riskAssessment, modelTier, ct);
        }

        var status = requiresGate ? DecisionStatus.Pending : DecisionStatus.AutoApproved;
        var decision = new AgentDecision
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            AgentId = agentId,
            AgentDisplayName = agentDisplayName,
            Phase = phase,
            ImpactLevel = impactLevel,
            Title = title,
            Rationale = rationale,
            Alternatives = alternatives,
            AffectedFiles = affectedFiles,
            RiskAssessment = riskAssessment,
            Plan = plan,
            Status = status,
            Category = category,
        };

        _decisionLog.Log(decision);

        if (requiresGate)
        {
            // Create gate notification for human review
            var gateContext = FormatGateContext(decision);
            await _gateNotificationService.AddNotificationAsync(
                $"{DecisionGatePrefix}{decision.Id}",
                gateContext,
                ct: ct);

            _logger.LogWarning(
                "[{Agent}] Decision GATED [{Impact}]: {Title} — awaiting human approval",
                agentDisplayName, impactLevel, title);
        }
        else
        {
            _logger.LogInformation(
                "[{Agent}] Decision auto-approved [{Impact}]: {Title}",
                agentDisplayName, impactLevel, title);
        }

        return decision;
    }

    /// <summary>
    /// Approve a pending decision with optional feedback.
    /// </summary>
    public void ApproveDecision(string decisionId, string? feedback = null)
    {
        _decisionLog.Update(decisionId, DecisionStatus.Approved, feedback);
        _gateNotificationService.Resolve($"{DecisionGatePrefix}{decisionId}");
        _logger.LogInformation("Decision {Id} approved (feedback: {Feedback})", decisionId, feedback ?? "none");
    }

    /// <summary>
    /// Reject a pending decision with feedback explaining why.
    /// </summary>
    public void RejectDecision(string decisionId, string? feedback = null)
    {
        _decisionLog.Update(decisionId, DecisionStatus.Rejected, feedback);
        _gateNotificationService.Resolve($"{DecisionGatePrefix}{decisionId}");
        _logger.LogInformation("Decision {Id} rejected (feedback: {Feedback})", decisionId, feedback ?? "none");
    }

    /// <summary>
    /// Check if a decision has been resolved (approved or rejected).
    /// </summary>
    public bool IsDecisionResolved(string decisionId)
    {
        var decision = _decisionLog.GetDecision(decisionId);
        return decision?.Status is DecisionStatus.Approved or DecisionStatus.Rejected or DecisionStatus.AutoApproved;
    }

    /// <summary>
    /// Wait for a gated decision to be resolved by a human reviewer.
    /// Returns the updated decision with status and feedback.
    /// </summary>
    public async Task<AgentDecision> WaitForDecisionAsync(string decisionId, CancellationToken ct = default)
    {
        var timeoutMinutes = _config.GateTimeoutMinutes;
        var deadline = timeoutMinutes > 0
            ? DateTime.UtcNow.AddMinutes(timeoutMinutes)
            : DateTime.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            var decision = _decisionLog.GetDecision(decisionId);
            if (decision is null)
            {
                _logger.LogWarning("Decision {Id} not found while waiting", decisionId);
                break;
            }

            if (decision.Status is DecisionStatus.Approved or DecisionStatus.Rejected)
                return decision;

            if (DateTime.UtcNow > deadline)
            {
                _logger.LogWarning("Decision {Id} timed out after {Minutes}m — applying fallback: {Action}",
                    decisionId, timeoutMinutes, _config.TimeoutFallbackAction);

                if (_config.TimeoutFallbackAction.Equals("auto-approve", StringComparison.OrdinalIgnoreCase))
                {
                    ApproveDecision(decisionId, $"Auto-approved after {timeoutMinutes}m timeout");
                    return _decisionLog.GetDecision(decisionId)!;
                }
                // Block — leave as pending
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }

        return _decisionLog.GetDecision(decisionId) ?? throw new InvalidOperationException($"Decision {decisionId} not found");
    }

    #region AI Classification & Plan Generation

    private async Task<(DecisionImpactLevel Level, string Rationale, string? Alternatives, string? AffectedFiles, string? RiskAssessment)>
        ClassifyImpactAsync(string agentName, string phase, string title, string context, string modelTier, CancellationToken ct)
    {
        try
        {
            var chat = new ChatHistory();
            chat.AddSystemMessage(ClassificationSystemPrompt);
            chat.AddUserMessage($"""
                Agent: {agentName}
                Phase: {phase}
                Decision: {title}
                Context: {context}

                Classify this decision's impact level and provide analysis.
                Respond in this exact format:
                IMPACT: [XS|S|M|L|XL]
                RATIONALE: [Why this impact level]
                ALTERNATIVES: [What alternatives were considered]
                AFFECTED_FILES: [Files/modules impacted]
                RISK: [What could go wrong]
                """);

            var text = await _chatRunner.InvokeAsync(new ChatCompletionRequest
            {
                History = chat,
                ModelTier = modelTier
            }, ct);

            return ParseClassificationResponse(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decision classification failed for '{Title}' — defaulting to M", title);
            return (DecisionImpactLevel.M, "Classification failed — defaulted to Medium", null, null, null);
        }
    }

    private async Task<string?> GeneratePlanAsync(
        string agentName, string title, string context, string rationale,
        string? alternatives, string? affectedFiles, string? riskAssessment,
        string modelTier, CancellationToken ct)
    {
        try
        {
            var chat = new ChatHistory();
            chat.AddSystemMessage(PlanGenerationSystemPrompt);
            chat.AddUserMessage($"""
                Agent: {agentName}
                Decision: {title}
                Context: {context}
                Rationale: {rationale}
                Alternatives considered: {alternatives ?? "None documented"}
                Affected files/modules: {affectedFiles ?? "Not specified"}
                Risks: {riskAssessment ?? "Not assessed"}

                Generate a structured implementation plan for this decision that a human reviewer can evaluate.
                """);

            var result = await _chatRunner.InvokeAsync(new ChatCompletionRequest
            {
                History = chat,
                ModelTier = modelTier
            }, ct);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plan generation failed for '{Title}'", title);
            return null;
        }
    }

    internal static (DecisionImpactLevel Level, string Rationale, string? Alternatives, string? AffectedFiles, string? RiskAssessment)
        ParseClassificationResponse(string text)
    {
        var level = DecisionImpactLevel.M; // default
        string rationale = "No rationale provided";
        string? alternatives = null;
        string? affectedFiles = null;
        string? riskAssessment = null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("IMPACT:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["IMPACT:".Length..].Trim().ToUpperInvariant();
                level = value switch
                {
                    "XS" => DecisionImpactLevel.XS,
                    "S" => DecisionImpactLevel.S,
                    "M" => DecisionImpactLevel.M,
                    "L" => DecisionImpactLevel.L,
                    "XL" => DecisionImpactLevel.XL,
                    _ => DecisionImpactLevel.M,
                };
            }
            else if (trimmed.StartsWith("RATIONALE:", StringComparison.OrdinalIgnoreCase))
                rationale = trimmed["RATIONALE:".Length..].Trim();
            else if (trimmed.StartsWith("ALTERNATIVES:", StringComparison.OrdinalIgnoreCase))
                alternatives = trimmed["ALTERNATIVES:".Length..].Trim();
            else if (trimmed.StartsWith("AFFECTED_FILES:", StringComparison.OrdinalIgnoreCase))
                affectedFiles = trimmed["AFFECTED_FILES:".Length..].Trim();
            else if (trimmed.StartsWith("RISK:", StringComparison.OrdinalIgnoreCase))
                riskAssessment = trimmed["RISK:".Length..].Trim();
        }

        return (level, rationale, alternatives, affectedFiles, riskAssessment);
    }

    #endregion

    #region Helpers

    private static AgentDecision CreateDecision(
        string agentId, string agentDisplayName, string phase, string title, string context,
        string? category, DecisionImpactLevel level, DecisionStatus status) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        AgentId = agentId,
        AgentDisplayName = agentDisplayName,
        Phase = phase,
        ImpactLevel = level,
        Title = title,
        Rationale = context,
        Status = status,
        Category = category,
    };

    private static string FormatGateContext(AgentDecision d)
    {
        var parts = new List<string>
        {
            $"**Decision:** {d.Title}",
            $"**Agent:** {d.AgentDisplayName}",
            $"**Impact:** {d.ImpactLevel}",
            $"**Phase:** {d.Phase}",
            $"**Rationale:** {d.Rationale}",
        };

        if (!string.IsNullOrWhiteSpace(d.Alternatives))
            parts.Add($"**Alternatives:** {d.Alternatives}");
        if (!string.IsNullOrWhiteSpace(d.AffectedFiles))
            parts.Add($"**Affected Files:** {d.AffectedFiles}");
        if (!string.IsNullOrWhiteSpace(d.RiskAssessment))
            parts.Add($"**Risk:** {d.RiskAssessment}");
        if (!string.IsNullOrWhiteSpace(d.Plan))
            parts.Add($"\n**Implementation Plan:**\n{d.Plan}");

        return string.Join("\n", parts);
    }

    #endregion

    #region AI Prompts

    private const string ClassificationSystemPrompt = """
        You are a decision impact classifier for a multi-agent AI development system.
        Your job is to classify the impact level of an agent's decision on the project.

        Impact levels:
        - XS (Extra Small): Cosmetic changes — CSS tweaks, comment fixes, formatting, typos.
        - S (Small): Low-risk isolated changes — adding a utility method, updating a config value, simple bug fix within one file.
        - M (Medium): Moderate structural changes — refactoring a class, changing API signatures, adding a new dependency, modifying shared interfaces.
        - L (Large): Significant architectural impact — introducing a new service/module, changing database schemas, modifying core abstractions, cross-cutting concerns.
        - XL (Extra Large): Project-defining changes — restructuring the project layout, changing technology stacks, pivoting major features, splitting/merging services.

        Be precise and conservative. When in doubt between two levels, choose the higher one.
        Consider: scope of file changes, number of components affected, reversibility, risk of breaking existing functionality.
        """;

    private const string PlanGenerationSystemPrompt = """
        You are a technical planning assistant. Generate a structured implementation plan
        for a decision that a human reviewer needs to evaluate.

        Your plan should include:
        1. **Summary**: One-paragraph overview of what will change and why
        2. **Implementation Steps**: Numbered list of concrete changes
        3. **Files Affected**: List of files that will be created, modified, or deleted
        4. **Risks & Mitigations**: What could go wrong and how to handle it
        5. **Rollback Strategy**: How to undo this change if needed
        6. **Testing Approach**: How to verify the change works correctly

        Be concrete and actionable. The human reviewer should be able to understand
        exactly what will happen and make an informed approve/reject decision.
        """;

    #endregion
}
