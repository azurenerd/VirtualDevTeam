namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Canonical branch-naming patterns for agent-authored work. NoMessyCodePlan Theme 2.
///
/// <para>
/// Multiple detectors + recovery paths inspect head-branch strings to identify the
/// owning agent role. Pulling the prefixes into constants prevents subtle drift
/// when the convention changes (e.g. the 2026-05-10 fix tightened SE recovery to
/// require <c>/softwareengineer</c> in addition to <c>agent/</c> — without
/// constants, only a careful grep finds all the call sites that need to follow).
/// </para>
///
/// <para>
/// **Convention:** branches are <c>agent/{role-slug}/{task-slug}</c>. The slug
/// uses lowercase, with role-display-name spaces collapsed to nothing
/// (e.g. "Software Engineer 1" → "software-engineer-1" → <c>softwareengineer</c>
/// matches the prefix infix for ANY ranked SE).
/// </para>
/// </summary>
public static class BranchPatterns
{
    /// <summary>Prefix every agent-authored branch must start with.</summary>
    public const string AgentPrefix = "agent/";

    /// <summary>
    /// Substring appearing in any SoftwareEngineer's branch slug — matches
    /// "software-engineer-1", "softwareengineer", etc. Used by the engineering-complete
    /// hard-check and the SE recovery short-circuit to filter PRs.
    /// </summary>
    public const string AgentSoftwareEngineerInfix = "/softwareengineer";
}
