using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when a work item (engineering task) is assigned to an agent that is Offline
/// or no longer registered. This causes silent pipeline stalls because the SE leader
/// won't reassign work that appears to be owned by another agent, and no other engineer
/// will claim it.
///
/// Common causes:
/// - Specialist agent (e.g., Frontend Engineer) was manually restarted and went Offline
/// - Agent crashed and wasn't respawned by the spawn manager
/// - Agent was removed from the agent registry but its assigned tasks weren't reassigned
///
/// The detector extracts the agent name from the work item title prefix (format:
/// "AgentDisplayName: Task Title") and cross-references against the agent registry.
/// </summary>
public sealed class OfflineAgentAssignedWorkDetector : IFlowDetector
{
    public string DetectorId => "offline-agent-assigned-work";

    private readonly ILogger<OfflineAgentAssignedWorkDetector> _logger;
    private readonly TimeSpan _offlineThreshold;

    public OfflineAgentAssignedWorkDetector(
        ILogger<OfflineAgentAssignedWorkDetector> logger,
        TimeSpan? offlineThreshold = null)
    {
        _logger = logger;
        // Only fire after the agent has been offline long enough that it's unlikely to recover
        _offlineThreshold = offlineThreshold ?? TimeSpan.FromMinutes(10);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            if (!IsActiveEngineeringPhase(ctx.CurrentPhase))
                return findings;

            var workItems = await ctx.Platform.ListOpenWorkItemsAsync(ct).ConfigureAwait(false);
            if (workItems.Count == 0) return findings;

            // Build a lookup of agent display names → status
            var agentsByName = new Dictionary<string, AgentStateView>(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in ctx.Agents)
            {
                if (!string.IsNullOrWhiteSpace(agent.DisplayName))
                    agentsByName[agent.DisplayName] = agent;
            }

            // Find tasks that are assigned/in-progress but whose owning agent is offline or missing
            var assignedTasks = workItems
                .Where(wi => wi.Labels.Any(l =>
                    l.Equals("engineering-task", StringComparison.OrdinalIgnoreCase)))
                .Where(wi => wi.Labels.Any(l =>
                    l.Equals("status:assigned", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("status:in-progress", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var task in assignedTasks)
            {
                if (ct.IsCancellationRequested) break;

                var agentName = ExtractAgentName(task.Title);
                if (string.IsNullOrWhiteSpace(agentName)) continue;

                if (agentsByName.TryGetValue(agentName, out var agent))
                {
                    // Agent exists — check if it's Offline
                    if (!string.Equals(agent.Status, "Offline", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Check if it's been offline long enough
                    if (agent.StatusChangedAt is not null &&
                        ctx.Now - agent.StatusChangedAt.Value < _offlineThreshold)
                        continue;

                    var offlineDuration = agent.StatusChangedAt is not null
                        ? FormatDuration(ctx.Now - agent.StatusChangedAt.Value)
                        : "unknown duration";

                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetAgentId = agent.Id,
                        TargetDisplayName = agent.DisplayName,
                        TargetResource = $"issue#{task.Number}",
                        Summary = $"Task #{task.Number} '{Truncate(task.Title, 50)}' is assigned to " +
                                  $"{agent.DisplayName} which has been Offline for {offlineDuration}. " +
                                  $"No other engineer will claim it — pipeline is blocked.",
                        Rationale = "A work item is assigned to an agent (by title prefix and status:assigned label) " +
                                    "but that agent is Offline and unlikely to recover on its own. The SE leader's " +
                                    "task assignment loop skips work assigned to other agents, so this task will " +
                                    "remain unclaimed indefinitely. Fix options: (1) Respawn the agent via the " +
                                    "Dashboard 🔄 restart button, (2) Reassign the task by removing the agent name " +
                                    "prefix from the issue title and changing status:assigned → status:pending, or " +
                                    "(3) If the agent's role is no longer needed, close the task and redistribute.",
                        DedupKey = $"offline-agent-assigned-work:{agent.Id}:{task.Number}",
                        RecommendedFixId = $"respawn-agent:{agent.Id}",
                        RecommendedFixDescription = $"Respawn {agent.DisplayName} via dashboard restart, or reassign task #{task.Number}",
                    });
                }
                else
                {
                    // Agent not found in registry at all — even worse
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetResource = $"issue#{task.Number}",
                        Summary = $"Task #{task.Number} '{Truncate(task.Title, 50)}' is assigned to " +
                                  $"'{agentName}' which is not registered in the agent registry. " +
                                  $"No agent will claim it — pipeline is blocked.",
                        Rationale = "A work item's title prefix references an agent that doesn't exist in the " +
                                    "agent registry. The agent may have been removed, never spawned, or its name " +
                                    "was misspelled during task creation. No engineer will pick up work assigned " +
                                    "to a non-existent agent. Fix: remove the agent name prefix from the issue " +
                                    "title and change status:assigned → status:pending so the SE leader can " +
                                    "reassign it to an active engineer.",
                        DedupKey = $"offline-agent-assigned-work:missing:{agentName}:{task.Number}",
                        RecommendedFixId = $"reassign-task:{task.Number}",
                        RecommendedFixDescription = $"Reassign task #{task.Number} — agent '{agentName}' is not registered",
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OfflineAgentAssignedWorkDetector tick failed (non-fatal)");
        }
        return findings;
    }

    /// <summary>
    /// Extract the agent display name from a work item title.
    /// Expected format: "Agent Display Name: Task Title" or "Agent Display Name 1: Task Title"
    /// </summary>
    internal static string? ExtractAgentName(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var colonIdx = title.IndexOf(':');
        if (colonIdx <= 0) return null;

        var prefix = title[..colonIdx].Trim();

        // Skip prefixes that look like task IDs rather than agent names (e.g., "[T-FINAL]")
        if (prefix.StartsWith('[') || prefix.StartsWith('#'))
            return null;

        // Agent names typically contain at least one space or are known role names
        // Reject very short prefixes that are unlikely to be agent names
        if (prefix.Length < 2) return null;

        return prefix;
    }

    private static bool IsActiveEngineeringPhase(string phase) =>
        string.Equals(phase, "ParallelDevelopment", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "Testing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "Review", StringComparison.OrdinalIgnoreCase);

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{ts.TotalHours:0.0}h";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
