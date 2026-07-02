using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.18 EmptyQueueDetector — an agent is Idle but open work matches its role.
/// Different angle from <see cref="IdleAgentPhaseStuckDetector"/>: that one is
/// PR-driven (reviewer Idle while a PR awaits *their* review). This is
/// work-item-driven (any agent Idle while *issue queue* has matching unclaimed
/// work).
///
/// <para>
/// **Match heuristics:** an issue matches an agent's queue when:
/// <list type="bullet">
///   <item>The issue is labeled <c>engineering-task</c> AND the agent's role contains "SoftwareEngineer" (or other engineering specialty), OR</item>
///   <item>The issue has no <c>status:in-progress</c> label AND no <c>status:done</c> label (i.e. it's claimable), OR</item>
///   <item>The issue's title prefix matches the agent's display name (executive-request, sme-task pattern).</item>
/// </list>
/// </para>
///
/// <para>
/// Conscious overlap: this also overlaps with IdleAgentPhaseStuckDetector for reviewer
/// roles. We keep both — they distinguish "Idle agent who SHOULD review existing PR" from
/// "Idle agent who SHOULD claim new work item." Both are common stuck patterns; both
/// deserve their own dedup key.
/// </para>
/// </summary>
public sealed class EmptyQueueDetector : IFlowDetector
{
    public string DetectorId => "empty-queue";

    private readonly ILogger<EmptyQueueDetector> _logger;
    private readonly TimeSpan _idleThreshold;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);
    private static readonly Regex DepsPattern = new(@"deps?=([0-9 ,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EmptyQueueDetector(
        ILogger<EmptyQueueDetector> logger,
        TimeSpan? idleThreshold = null,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _logger = logger;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(6);
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            // Don't flag idle engineers before tasks are ready for claiming.
            // During EngineeringPlanning, tasks exist but aren't assigned yet.
            if (string.Equals(ctx.CurrentPhase, "Initialization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ctx.CurrentPhase, "Research", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ctx.CurrentPhase, "Architecture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ctx.CurrentPhase, "EngineeringPlanning", StringComparison.OrdinalIgnoreCase))
                return findings;

            var workItems = await ctx.Platform.ListOpenWorkItemsAsync(ct).ConfigureAwait(false);
            if (workItems.Count == 0) return findings;

            // Build a set of open issue numbers for dependency resolution.
            var openIssueNumbers = new HashSet<int>(workItems.Select(w => w.Number));

            // Cache the claimable set once per tick — filter out dependency-blocked tasks.
            var claimableEngTasks = workItems
                .Where(IsClaimableEngineeringTask)
                .Where(t => !HasUnmetDependencies(t, openIssueNumbers))
                .ToList();
            if (claimableEngTasks.Count == 0) return findings;

            // Precompute capability keywords for every engineer-role agent in the team
            // (idle OR working — we need them all so we know who an idle agent's peers are).
            // Mirrors SpecialistEngineerAgent.ExtractCapabilityKeywords so the score comparison
            // here is apples-to-apples with the agent's own self-claim decision.
            var peerKeywordsByAgentId = ctx.Agents
                .Where(a => IsEngineerRole(a.Role))
                .ToDictionary(
                    a => a.Id,
                    a => ExtractCapabilityKeywords(a.Capabilities),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var agent in ctx.Agents)
            {
                if (!string.Equals(agent.Status, "Idle", StringComparison.OrdinalIgnoreCase)) continue;
                if (agent.StatusChangedAt is null) continue;
                if (ctx.Now - agent.StatusChangedAt.Value < _idleThreshold) continue;
                if (!IsEngineerRole(agent.Role)) continue;

                // Skip if agent has recent log activity or active LLM call —
                // agent is aware of tasks and may be processing dependencies/claims.
                if (HasRecentActivity(agent.Id)) continue;

                // Peer-deferral check (2026-05-12): mirror SpecialistEngineerAgent's self-claim
                // logic. For each claimable task, compute THIS agent's keyword match score
                // and the best peer engineer's score. If a peer strictly beats us on EVERY
                // claimable task, our idleness is correct (we're correctly deferring) and we
                // should NOT escalate. Only escalate when at least one task has us as the
                // best (or tied-best) match and we're STILL not claiming it.
                var myKeywords = peerKeywordsByAgentId.GetValueOrDefault(agent.Id) ?? new HashSet<string>();
                var myWinnableTasks = claimableEngTasks
                    .Where(task =>
                    {
                        var mine = ScoreTask(myKeywords, task);
                        // Generalists (no caps) act as the universal last-resort; any task
                        // counts as "winnable" since no specialist beats them on score 0.
                        if (myKeywords.Count == 0) return true;
                        if (mine == 0) return false;
                        var bestPeer = peerKeywordsByAgentId
                            .Where(kvp => !string.Equals(kvp.Key, agent.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(kvp => ScoreTask(kvp.Value, task))
                            .DefaultIfEmpty(0)
                            .Max();
                        return mine >= bestPeer;
                    })
                    .ToList();

                if (myWinnableTasks.Count == 0)
                {
                    _logger.LogDebug(
                        "empty-queue: {Agent} idle but all {Count} claimable tasks belong to a higher-scoring peer — skipping escalation",
                        agent.DisplayName, claimableEngTasks.Count);
                    continue;
                }

                // Pick the first task this agent would actually win for diagnostic context.
                var match = myWinnableTasks[0];
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetAgentId = agent.Id,
                    TargetDisplayName = agent.DisplayName,
                    TargetResource = $"issue#{match.Number}",
                    Summary = $"{agent.DisplayName} idle while {myWinnableTasks.Count} engineering task(s) " +
                              $"match their capabilities (e.g. #{match.Number} '{Truncate(match.Title, 60)}').",
                    Rationale = "An Idle engineer agent's claim loop has not picked up tasks that look claimable " +
                                "(open, no status:in-progress, no assigned-agent prefix in title), and at least " +
                                "one of those tasks is NOT covered by a higher-scoring peer specialist. Common " +
                                "causes: the claim-query predicate doesn't match the issue's label set, " +
                                "dependency-resolution treats the task as blocked, or the agent crashed mid-poll " +
                                "and was re-spawned without rescanning. Operator should check the agent's recent " +
                                "log for skipped-task reasons.",
                    DedupKey = $"empty-queue:{agent.Id}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmptyQueueDetector tick failed (non-fatal)");
        }
        return findings;
    }

    /// <summary>
    /// Same extraction rule used by <c>SpecialistEngineerAgent.ExtractCapabilityKeywords</c>:
    /// lowercase, split on common separators, drop tokens ≤3 chars. Kept here as a private
    /// duplicate (rather than referenced) to avoid creating a new dependency from Core down
    /// to Agents just for one helper.
    /// </summary>
    private static HashSet<string> ExtractCapabilityKeywords(IReadOnlyList<string> caps) =>
        caps
            .SelectMany(c => c.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ',' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 3)
            .ToHashSet();

    /// <summary>Score one task against a keyword set — count of substring matches in the title.</summary>
    private static int ScoreTask(IEnumerable<string> keywords, WorkItemView task)
    {
        if (!keywords.Any()) return 0;
        var text = task.Title.ToLowerInvariant();
        return keywords.Count(kw => text.Contains(kw));
    }

    private static bool IsClaimableEngineeringTask(WorkItemView item)
    {
        if (!string.Equals(item.State, "open", StringComparison.OrdinalIgnoreCase)) return false;
        var hasEngTaskLabel = item.Labels.Any(l =>
            string.Equals(l, "engineering-task", StringComparison.OrdinalIgnoreCase));
        if (!hasEngTaskLabel) return false;

        // T-FINAL has a special lifecycle — not self-claimed via the normal loop.
        // It's triggered by CreateIntegrationPRAsync when all tasks complete.
        if (item.Title.Contains("Final Integration", StringComparison.OrdinalIgnoreCase)
            || item.Title.Contains("[T-FINAL]", StringComparison.OrdinalIgnoreCase))
            return false;

        // Skip items already in progress or done.
        if (item.Labels.Any(l =>
            string.Equals(l, "status:in-progress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l, "in-progress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l, "status:done", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l, "agent-stuck", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Convention: claimed tasks have the agent display name prepended to the title
        // (e.g. "Frontend Engineer 1: [T1] Project Foundation"). If the title contains
        // a colon after a display-name-like prefix, the task is already claimed even if
        // the status:in-progress label hasn't been written yet (race window).
        var colonIdx = item.Title.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = item.Title.Substring(0, colonIdx).Trim();
            // Agent display names are multi-word (e.g. "Frontend Engineer 1", "SoftwareEngineer 2").
            // Exclude task-ID prefixes like "[T1]" which also contain colons.
            if (!prefix.StartsWith("[") && prefix.Contains(' '))
                return false;
        }

        return true;
    }

    private static bool IsEngineerRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        return role.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Software Engineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("FrontendEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("BackendEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("ContentEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("InfraEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Specialist", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Checks if a task has unmet dependencies by parsing <c>deps=14</c> or <c>deps=14 15 16</c>
    /// from the issue title. If ALL referenced issues are closed (not in <paramref name="openIssueNumbers"/>),
    /// the dependencies are met. If ANY are still open, the task is blocked.
    /// </summary>
    private bool HasUnmetDependencies(WorkItemView task, HashSet<int> openIssueNumbers)
    {
        var match = DepsPattern.Match(task.Title);
        if (!match.Success) return false;

        var depNumbers = match.Groups[1].Value
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
            .Where(n => n > 0);

        foreach (var dep in depNumbers)
        {
            if (openIssueNumbers.Contains(dep))
            {
                _logger.LogDebug(
                    "empty-queue: {Task} blocked by open dependency #{Dep}",
                    task.Title, dep);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the agent has recent CLI log entries or an active LLM call,
    /// indicating the agent is working even though its status shows Idle.
    /// </summary>
    private bool HasRecentActivity(string agentId)
    {
        if (_logService is not null)
        {
            var lastLog = _logService.GetLatestEntryTimestamp(agentId);
            if (lastLog.HasValue && (DateTimeOffset.UtcNow - lastLog.Value) < LogActivityWindow)
                return true;
        }
        if (_llmTracker is not null)
        {
            var activeCall = _llmTracker.GetActiveCall(agentId);
            if (activeCall is not null) return true;
        }
        return false;
    }
}
