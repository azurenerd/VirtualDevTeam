namespace AgentSquad.Core.Agents.Decisions;

/// <summary>
/// A classified agent decision with rich context for human review.
/// Unlike <see cref="Reasoning.AgentReasoningEvent"/>, decisions capture the full rationale,
/// alternatives considered, risk assessment, and implementation plan for gated decisions.
/// </summary>
public record AgentDecision
{
    /// <summary>Unique identifier for this decision.</summary>
    public required string Id { get; init; }

    /// <summary>Agent that made this decision.</summary>
    public required string AgentId { get; init; }

    /// <summary>Display name of the agent.</summary>
    public required string AgentDisplayName { get; init; }

    /// <summary>Workflow phase when the decision was made.</summary>
    public required string Phase { get; init; }

    /// <summary>AI-classified impact level.</summary>
    public required DecisionImpactLevel ImpactLevel { get; init; }

    /// <summary>Short title describing the decision (e.g., "Refactor AuthService to use repository pattern").</summary>
    public required string Title { get; init; }

    /// <summary>Why this decision was made — the reasoning behind the choice.</summary>
    public required string Rationale { get; init; }

    /// <summary>What alternatives were considered and why they were rejected.</summary>
    public string? Alternatives { get; init; }

    /// <summary>Files or modules that would be impacted by this decision.</summary>
    public string? AffectedFiles { get; init; }

    /// <summary>What could go wrong — risks and mitigation strategies.</summary>
    public string? RiskAssessment { get; init; }

    /// <summary>Structured implementation plan (generated for gated decisions via extra AI turn).</summary>
    public string? Plan { get; init; }

    /// <summary>Current status in the gating workflow.</summary>
    public DecisionStatus Status { get; init; } = DecisionStatus.Pending;

    /// <summary>When the decision was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the decision was resolved (approved/rejected/auto-approved).</summary>
    public DateTime? ResolvedAt { get; init; }

    /// <summary>Human reviewer's feedback (provided on approval or rejection).</summary>
    public string? HumanFeedback { get; init; }

    /// <summary>Category of the decision for filtering (e.g., "Architecture", "Refactoring", "Dependency").</summary>
    public string? Category { get; init; }

    /// <summary>Whether this decision requires human gate based on config threshold.</summary>
    public bool RequiresGate => Status == DecisionStatus.Pending;
}
