namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Canonical agent status-reason phrases. NoMessyCodePlan Theme 2.
///
/// <para>
/// **Why centralised:** the HealthMonitor's auto-detect heuristics in
/// <c>AutoDetectSignals</c> match agent status reasons via case-insensitive
/// substring matching. The matched phrases used to live as inline string literals
/// scattered across the file — when an agent role's canonical "I'm done" phrase
/// drifted, the heuristic silently stopped firing. Pulling these into constants
/// gives one place to update, one place to grep, and a documentation surface for
/// agent authors who need to know what phrases the orchestrator listens for.
/// </para>
///
/// <para>
/// **Semantics:** these are NOT exact-match strings. The matcher is
/// <c>StatusReason.Contains(constant, StringComparison.OrdinalIgnoreCase)</c>, so
/// agents may emit longer messages like "engineering complete — 4 PRs merged" and
/// still trigger the heuristic. Keep the constants short and unambiguous; don't
/// embed punctuation that an agent's free-form text might lack.
/// </para>
///
/// <para>
/// **Migration policy (Theme 2):** new code should pass these constants to
/// <c>HealthMonitor.HasReasonContaining</c> or other substring-matchers rather than
/// repeating the phrase as a literal. Existing literals are migrated opportunistically.
/// </para>
/// </summary>
public static class AgentStatusReasons
{
    // --- Researcher phase ---
    public const string ResearchComplete = "research complete";
    public const string ResearchPublished = "research published";
    public const string ResearchFindingsCommitted = "research findings committed";
    public const string Monitoring = "monitoring";
    public const string WaitingForResearchDirectives = "waiting for research directives";

    /// <summary>
    /// Positive-completion phrases for the Researcher phase. Used by the HealthMonitor's
    /// hardened doc-signal heuristic to require an explicit "I'm done" statement before
    /// firing <c>research.complete</c>. Loose substrings like just "complete" or "monitoring"
    /// are NOT in this list — they caused false positives (see Lesson #23 / fix-rec for
    /// healthmon-false-research-complete).
    /// </summary>
    public static readonly string[] ResearchCompletePhrases =
    {
        ResearchComplete,
        ResearchPublished,
        ResearchFindingsCommitted
    };

    // --- Architect phase ---
    public const string ArchitectureComplete = "architecture complete";
    public const string ArchitecturePublished = "architecture published";
    public const string ArchitectureCommitted = "architecture committed";

    /// <summary>
    /// Positive-completion phrases for the Architect phase. Companion to
    /// <see cref="ResearchCompletePhrases"/> for the same hardened heuristic.
    /// </summary>
    public static readonly string[] ArchitectureCompletePhrases =
    {
        ArchitectureComplete,
        ArchitecturePublished,
        ArchitectureCommitted
    };

    // --- Program Manager phase ---
    public const string PmSpec = "pmspec";
    public const string PmSpecAlt = "pm spec";
    public const string Specification = "specification";
    public const string WritingSpec = "writing spec";
    public const string KickoffComplete = "kickoff complete";
    public const string MonitoringTeam = "monitoring team";

    // --- Engineering phase ---
    public const string EngineeringComplete = "engineering complete";
    public const string AllTasksComplete = "all tasks complete";
    public const string AllTasksDone = "all tasks done";

    // --- Engineering idle states ---
    public const string Complete = "complete";
    public const string NoTask = "no task";
    public const string NoAssigned = "no assigned";

    // --- Integration phase ---
    public const string IntegrationPr = "integration pr";
}
