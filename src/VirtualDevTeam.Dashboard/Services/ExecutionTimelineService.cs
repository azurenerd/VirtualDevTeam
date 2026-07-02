using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Manages execution timeline milestones — PR creation, doc production, phase transitions, etc.
/// Pure data/logic service — no SignalR, no event subscriptions. The facade coordinates events.
/// </summary>
public sealed class ExecutionTimelineService
{
    private readonly List<ExecutionMilestone> _milestones = new();
    private readonly HashSet<string> _recordedMilestoneKeys = new();
    private readonly object _lock = new();
    private readonly ILogger<ExecutionTimelineService> _logger;

    public ExecutionTimelineService(ILogger<ExecutionTimelineService> logger)
    {
        _logger = logger;
    }

    /// <summary>Record a milestone if it hasn't been recorded before (deduped by category:title key).</summary>
    public void RecordMilestone(string icon, string title, string? detail, string category, string? agentName = null, DateTime? timestamp = null)
    {
        var key = $"{category}:{title}";
        lock (_lock)
        {
            if (!_recordedMilestoneKeys.Add(key))
                return;

            _milestones.Add(new ExecutionMilestone
            {
                Icon = icon,
                Title = title,
                Detail = detail,
                Category = category,
                Timestamp = timestamp ?? DateTime.UtcNow,
                IsCompleted = true,
                AgentName = agentName
            });
        }
    }

    /// <summary>Get the execution timeline milestones, oldest first.</summary>
    public IReadOnlyList<ExecutionMilestone> GetExecutionTimeline()
    {
        lock (_lock)
        {
            return _milestones.OrderBy(m => m.Timestamp).ToList();
        }
    }

    /// <summary>
    /// Detect milestones from agent activity/status events.
    /// Pass <paramref name="timestamp"/> when replaying historical entries so milestones
    /// appear at their original time (e.g. during activity-log restore on restart).
    /// </summary>
    public void DetectActivityMilestone(AgentActivityEventArgs e, string agentName, DateTime? timestamp = null)
    {
        var details = e.Details ?? "";
        var detailsLower = details.ToLowerInvariant();

        // Detect PR creation
        if (detailsLower.Contains("created pr") || detailsLower.Contains("opened pr") ||
            (e.EventType == "status" && detailsLower.Contains("pr #") && detailsLower.Contains("creat")))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("📝", $"PR {prRef} Created",
                TruncateDetail(details), "pr", agentName, timestamp);
        }

        // Detect PR merge
        if (detailsLower.Contains("merged") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("✅", $"PR {prRef} Merged",
                TruncateDetail(details), "pr", agentName, timestamp);
        }

        // Detect document creation/updates
        if (detailsLower.Contains("research.md"))
        {
            RecordMilestone("📄", "Research.md Created",
                "Produced the research document", "document", agentName, timestamp);
        }
        if (detailsLower.Contains("pmspec.md"))
        {
            RecordMilestone("📋", "PMSpec.md Created",
                "Produced the PM specification", "document", agentName, timestamp);
        }
        if (detailsLower.Contains("architecture.md") && !detailsLower.Contains("marker"))
        {
            RecordMilestone("🏛️", "Architecture.md Created",
                "Produced the architecture document", "document", agentName, timestamp);
        }
        if (detailsLower.Contains("engineering plan created") || detailsLower.Contains("engineering-task"))
        {
            RecordMilestone("📐", "Engineering Tasks Created",
                "Created engineering task issues", "document", agentName, timestamp);
        }

        // Detect issue creation
        if (detailsLower.Contains("created") && detailsLower.Contains("issue") &&
            (detailsLower.Contains("user stor") || detailsLower.Contains("task")))
        {
            RecordMilestone("🎫", "User Story Issues Created",
                TruncateDetail(details), "issues", agentName, timestamp);
        }

        // Detect review actions
        if (detailsLower.Contains("approved") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("👍", $"PR {prRef} Approved",
                TruncateDetail(details), "review", agentName, timestamp);
        }
        if (detailsLower.Contains("changes requested") || detailsLower.Contains("requested changes"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("🔄", $"Changes Requested on PR {prRef}",
                TruncateDetail(details), "review", agentName, timestamp);
        }

        // Detect test actions
        if (detailsLower.Contains("test") && (detailsLower.Contains("created") || detailsLower.Contains("written")))
        {
            RecordMilestone("🧪", "Tests Written",
                TruncateDetail(details), "test", agentName, timestamp);
        }
    }

    /// <summary>Handle phase transition — record a phase milestone.</summary>
    public void HandlePhaseChanged(PhaseTransitionEventArgs e)
    {
        var phaseIcon = e.NewPhase switch
        {
            ProjectPhase.Research => "🔬",
            ProjectPhase.Architecture => "🏗️",
            ProjectPhase.EngineeringPlanning => "📋",
            ProjectPhase.ParallelDevelopment => "⚙️",
            ProjectPhase.Testing => "🧪",
            ProjectPhase.Review => "🔍",
            ProjectPhase.Completion => "🎉",
            _ => "▶️"
        };

        RecordMilestone(phaseIcon, $"{DiagnosticSummaryService.FormatPhase(e.NewPhase)} Phase Started",
            e.Reason, "phase", timestamp: e.Timestamp);
    }

    /// <summary>
    /// Replay the <c>activity_log</c> table to restore PR/doc/review/test milestones lost
    /// on runner restart. Runs once on the first periodic tick of
    /// <see cref="DashboardDataService"/>. Idempotent — the <c>_recordedMilestoneKeys</c>
    /// HashSet deduplicates any entries that were already recorded from live events.
    /// </summary>
    /// <param name="stateStore">The SQLite state store to query.</param>
    /// <param name="runScope">
    /// Optional run-scope identifier (e.g. first 8 chars of the current RunId).
    /// Currently used for logging context only; the activity_log table is project-scoped
    /// and does not have a run_id column, so all entries for the active project are returned.
    /// </param>
    /// <param name="agentNameResolver">
    /// Optional delegate that maps an agent ID to its display name
    /// (e.g. <c>_snapshots.GetAgentDisplayName</c>). Falls back to the raw agent ID when null.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RestoreFromActivityLogAsync(
        AgentStateStore stateStore,
        string? runScope,
        Func<string, string>? agentNameResolver = null,
        CancellationToken ct = default)
    {
        var entries = await stateStore.GetActivityLogSinceAsync(DateTime.MinValue, ct);

        if (entries.Count == 0)
        {
            _logger.LogDebug("Activity-log restore: no entries found (fresh run or empty DB)");
            return;
        }

        _logger.LogInformation(
            "Restoring timeline from {EntryCount} activity_log entries (run scope: {RunScope})",
            entries.Count, runScope ?? "n/a");

        foreach (var entry in entries)
        {
            var agentName = agentNameResolver?.Invoke(entry.AgentId) ?? entry.AgentId;
            var args = new AgentActivityEventArgs
            {
                AgentId = entry.AgentId,
                EventType = entry.EventType,
                Details = entry.Details
            };
            DetectActivityMilestone(args, agentName, entry.Timestamp);
        }

        var total = GetExecutionTimeline().Count;
        _logger.LogInformation(
            "Activity-log restore complete — {TotalMilestones} milestones in timeline after restore",
            total);
    }

    /// <summary>Clear all timeline data. Called by facade during project reset.</summary>
    public void ResetCaches()
    {
        lock (_lock)
        {
            _milestones.Clear();
            _recordedMilestoneKeys.Clear();
        }
    }

    private static string ExtractPrRef(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"#(\d+)");
        return match.Success ? $"#{match.Groups[1].Value}" : "";
    }

    private static string TruncateDetail(string text) =>
        text.Length > 120 ? text[..117] + "…" : text;
}
