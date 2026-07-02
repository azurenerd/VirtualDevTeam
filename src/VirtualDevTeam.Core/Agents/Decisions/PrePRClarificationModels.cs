namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// A single pre-PR clarification question with AI-proposed answer.
/// Generated before implementation begins to surface agent assumptions for human validation.
/// </summary>
public record PrePRQuestion
{
    /// <summary>The clarification question (e.g., "Should we use static HTML or a Blazor server?").</summary>
    public required string Question { get; init; }

    /// <summary>AI's proposed answer based on available context (PMSpec, architecture, tech stack).</summary>
    public required string ProposedAnswer { get; init; }

    /// <summary>AI-assessed impact level of this decision.</summary>
    public required DecisionImpactLevel ImpactLevel { get; init; }

    /// <summary>Category for grouping (e.g., "Architecture", "Testing", "UX", "Scope").</summary>
    public required string Category { get; init; }

    /// <summary>Final answer after human review (null until approved; defaults to ProposedAnswer if auto-approved).</summary>
    public string? FinalAnswer { get; set; }
}

/// <summary>
/// A complete set of pre-PR clarification questions for a single engineering task.
/// </summary>
public record PrePRClarificationSet
{
    /// <summary>Unique identifier for this question set.</summary>
    public required string Id { get; init; }

    /// <summary>Agent that generated these questions.</summary>
    public required string AgentId { get; init; }

    /// <summary>Display name of the agent.</summary>
    public required string AgentDisplayName { get; init; }

    /// <summary>Issue number this clarification is for.</summary>
    public required int IssueNumber { get; init; }

    /// <summary>Issue title for display context.</summary>
    public required string IssueTitle { get; init; }

    /// <summary>The questions with proposed answers.</summary>
    public required List<PrePRQuestion> Questions { get; init; }

    /// <summary>When the questions were generated.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Whether the set has been finalized (approved or auto-approved).</summary>
    public bool IsFinalized { get; set; }

    /// <summary>When the set was finalized.</summary>
    public DateTime? FinalizedAt { get; set; }

    /// <summary>Whether this was auto-approved (gate disabled) vs human-approved.</summary>
    public bool WasAutoApproved { get; set; }
}
