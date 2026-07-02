using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Belt-and-suspenders defense against the issue-reopen / duplicate-claim pattern where an
/// agent picks up an issue whose corresponding implementation was already merged.
///
/// <para>
/// Strategy: for each Working agent, scan the open work-item list for an issue that matches
/// a closed-and-merged PR title pattern. Specifically:
/// <list type="bullet">
///   <item>Extract any "<c>issue #N</c>" or "<c>#N</c>" references from the agent's
///   <c>StatusReason</c> string to identify which issue the agent is currently working on.</item>
///   <item>If that issue is on the open list, check whether any MERGED PR's title already
///   represents the same issue (title prefix match on agent display name + task slug, or
///   a "Closes #N" reference in the PR body). Because <c>IPlatformView</c> only exposes open
///   PRs, this detector uses the open-PR list to build a set of CLAIMED numbers and flags
///   duplicates where two Working agents target the same issue number simultaneously —
///   which is the most reliable signal available without merged-PR access.</item>
///   <item>Secondary: if two or more Working agents reference the same issue number in their
///   StatusReason, that's a clear duplicate-claim regardless of PR state.</item>
/// </list>
/// </para>
///
/// <para>Best-effort: if StatusReason is absent or doesn't contain an issue reference
/// the agent is silently skipped. False-positive rate is low because issue numbers are
/// globally unique and two agents claiming the same number simultaneously is always wrong.</para>
///
/// <para>Dedup key: <c>dup-task-claim:{agentId}:{issueNumber}</c></para>
/// </summary>
public sealed class AgentClaimingDuplicateTaskDetector : IFlowDetector
{
    public string DetectorId => "agent-claiming-duplicate-task";

    /// <summary>Matches "issue #123", "#123", "Issue: 123", etc. in status reasons.</summary>
    private static readonly Regex IssueNumberPattern = new(
        @"(?:issue\s*#?|#)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "issue-1501" in branch names (highest priority).</summary>
    private static readonly Regex BranchIssuePattern = new(
        @"issue-(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "t-1478" or "t1478" in branch names (fallback).</summary>
    private static readonly Regex BranchTaskPattern = new(
        @"\bt-?(\d{3,})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<AgentClaimingDuplicateTaskDetector> _logger;

    public AgentClaimingDuplicateTaskDetector(ILogger<AgentClaimingDuplicateTaskDetector> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var workingAgents = ctx.Agents.Where(a => a.Status == "Working").ToList();
            if (workingAgents.Count == 0) return findings;

            // Build a map: issueNumber → list of Working agents referencing it.
            var issueToAgents = new Dictionary<int, List<AgentStateView>>();
            foreach (var agent in workingAgents)
            {
                if (string.IsNullOrEmpty(agent.StatusReason)) continue;
                var issueNumbers = ExtractIssueNumbers(agent.StatusReason);
                foreach (var number in issueNumbers)
                {
                    if (!issueToAgents.TryGetValue(number, out var list))
                        issueToAgents[number] = list = new List<AgentStateView>();
                    list.Add(agent);
                }
            }

            if (issueToAgents.Count == 0) return findings;

            // Fetch the open-PR list to cross-reference claimed PRs.
            var openPrs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);

            // Build map: issueNumber → PR number from open PRs (via title "Closes #N" or
            // head-branch naming convention "agent/*/{issue-slug}").
            var issueToOpenPrAgents = BuildIssueToOpenPrMap(openPrs);

            // Case 1: Two or more Working agents reference the same issue number in StatusReason.
            foreach (var (issueNumber, agents) in issueToAgents)
            {
                if (agents.Count < 2) continue;

                var agentNames = string.Join(", ", agents.Select(a => a.DisplayName));
                foreach (var agent in agents)
                {
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetAgentId = agent.Id,
                        TargetDisplayName = agent.DisplayName,
                        TargetResource = $"issue#{issueNumber}",
                        Summary =
                            $"Multiple agents ({agentNames}) simultaneously claim issue #{issueNumber}",
                        Rationale =
                            $"Issue #{issueNumber} is referenced in the StatusReason of {agents.Count} Working " +
                            $"agents: {agentNames}. Each agent should own exactly one issue at a time. " +
                            "This indicates either (a) a duplicate task was created, (b) a previously-completed " +
                            "issue was re-opened and re-assigned, or (c) state-recovery re-claimed an already-owned " +
                            "issue. Investigate which agent legitimately owns the task and clear the other's claim.",
                        DedupKey = $"dup-task-claim:{agent.Id}:{issueNumber}",
                    });
                }
            }

            // Case 2: A single Working agent claims an issue that already has an open PR
            // claimed by a DIFFERENT agent (via PR title prefix).
            foreach (var (issueNumber, agents) in issueToAgents)
            {
                if (!issueToOpenPrAgents.TryGetValue(issueNumber, out var prOwnerDisplayName)) continue;

                foreach (var agent in agents)
                {
                    // Skip if this agent IS the PR owner — that's normal.
                    if (string.Equals(agent.DisplayName, prOwnerDisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Different agent owns the PR for this issue number.
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetAgentId = agent.Id,
                        TargetDisplayName = agent.DisplayName,
                        TargetResource = $"issue#{issueNumber}",
                        Summary =
                            $"Agent {agent.DisplayName} claims issue #{issueNumber} but {prOwnerDisplayName} already has an open PR for it",
                        Rationale =
                            $"Agent {agent.DisplayName} ({agent.Id}) references issue #{issueNumber} in its " +
                            $"StatusReason, but an open PR on the platform is already titled/assigned to " +
                            $"'{prOwnerDisplayName}' for the same issue. This suggests a duplicate task claim: " +
                            "either (a) the issue was re-opened and re-assigned after the original agent had already " +
                            "started a PR, or (b) two agents raced to claim the same task. " +
                            "The original PR owner's work would be overwritten if both agents push. " +
                            "Investigate and close the duplicate claim.",
                        DedupKey = $"dup-task-claim:{agent.Id}:{issueNumber}",
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AgentClaimingDuplicateTaskDetector tick failed (non-fatal)");
        }
        return findings;
    }

    /// <summary>
    /// Parses all issue numbers referenced in a status reason string.
    /// </summary>
    private static IEnumerable<int> ExtractIssueNumbers(string statusReason)
    {
        foreach (Match m in IssueNumberPattern.Matches(statusReason))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                yield return n;
        }
    }

    /// <summary>
    /// Extracts an issue number from an agent PR head-branch string.
    /// Branch conventions observed in production:
    /// <list type="bullet">
    ///   <item><c>agent/5048ad1f/artist-sme-1/issue-1501-…</c> → 1501</item>
    ///   <item><c>agent/.../softwareengineer/t-1478-project-foundation</c> → 1478</item>
    /// </list>
    /// Returns null if no number can be reliably parsed (best-effort, no false positives).
    /// </summary>
    internal static int? ExtractIssueNumberFromBranch(string headBranch)
    {
        if (string.IsNullOrEmpty(headBranch)) return null;

        // Prefer "issue-N" — most explicit signal.
        var m = BranchIssuePattern.Match(headBranch);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n1) && n1 > 0)
            return n1;

        // Fall back to "t-N" / "tN" — common task-slug prefix.
        m = BranchTaskPattern.Match(headBranch);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n2) && n2 > 0)
            return n2;

        return null;
    }

    /// <summary>
    /// Extracts the agent owner display name from a PR.  Uses the PR title prefix
    /// (everything before the first "<c>: </c>" separator) because PR titles follow the
    /// convention "<c>{AgentDisplayName}: {TaskTitle}</c>".
    /// Falls back to the second path segment of the head branch
    /// (<c>agent/{id}/{displayName}/…</c>) when the title has no colon.
    /// Returns null if neither heuristic yields a non-empty string.
    /// </summary>
    private static string? ExtractAgentOwnerFromPr(PullRequestView pr)
    {
        // Title prefix heuristic: "Software Engineer 1: Implement auth" → "Software Engineer 1"
        var colonIdx = pr.Title.IndexOf(": ", StringComparison.Ordinal);
        if (colonIdx > 0)
            return pr.Title[..colonIdx].Trim();

        // Branch heuristic: "agent/{id}/{displayName}/…" → segment at index 2
        var parts = pr.HeadBranch.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase))
            return parts[2]; // e.g. "softwareengineer" or "artist-sme-1"

        return null;
    }

    /// <summary>
    /// Builds a map from issue number → PR owner display name for open PRs, using the
    /// head-branch naming convention <c>agent/{id}/{name}/{task-slug}</c> to extract
    /// the issue number via <see cref="ExtractIssueNumberFromBranch"/>.
    /// Best-effort: PRs whose branch doesn't encode an issue number are silently skipped.
    /// </summary>
    private static Dictionary<int, string> BuildIssueToOpenPrMap(IReadOnlyList<PullRequestView> openPrs)
    {
        var map = new Dictionary<int, string>(); // issueNum → ownerDisplayName
        foreach (var pr in openPrs)
        {
            var issueNumber = ExtractIssueNumberFromBranch(pr.HeadBranch);
            if (issueNumber is null) continue;

            var owner = ExtractAgentOwnerFromPr(pr);
            if (string.IsNullOrEmpty(owner)) continue;

            // First-writer wins — earlier PRs in the list take precedence.
            map.TryAdd(issueNumber.Value, owner);
        }
        return map;
    }
}
