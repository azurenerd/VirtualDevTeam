using AgentSquad.Core.Agents;
using AgentSquad.Orchestrator;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Manages execution timeline milestones — PR creation, doc production, phase transitions, etc.
/// Pure data/logic service — no SignalR, no event subscriptions. The facade coordinates events.
/// </summary>
public sealed class ExecutionTimelineService
{
    private readonly List<ExecutionMilestone> _milestones = new();
    private readonly HashSet<string> _recordedMilestoneKeys = new();
    private readonly object _lock = new();

    /// <summary>Record a milestone if it hasn't been recorded before (deduped by category:title key).</summary>
    public void RecordMilestone(string icon, string title, string? detail, string category, string? agentName = null)
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
                Timestamp = DateTime.UtcNow,
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

    /// <summary>Detect milestones from agent activity/status events.</summary>
    public void DetectActivityMilestone(AgentActivityEventArgs e, string agentName)
    {
        var details = e.Details ?? "";
        var detailsLower = details.ToLowerInvariant();

        // Detect PR creation
        if (detailsLower.Contains("created pr") || detailsLower.Contains("opened pr") ||
            (e.EventType == "status" && detailsLower.Contains("pr #") && detailsLower.Contains("creat")))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("📝", $"PR {prRef} Created",
                $"{agentName}: {TruncateDetail(details)}", "pr", agentName);
        }

        // Detect PR merge
        if (detailsLower.Contains("merged") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("✅", $"PR {prRef} Merged",
                $"{agentName}: {TruncateDetail(details)}", "pr", agentName);
        }

        // Detect document creation/updates
        if (detailsLower.Contains("research.md"))
        {
            RecordMilestone("📄", "Research.md Created",
                $"{agentName} produced the research document", "document", agentName);
        }
        if (detailsLower.Contains("pmspec.md"))
        {
            RecordMilestone("📋", "PMSpec.md Created",
                $"{agentName} produced the PM specification", "document", agentName);
        }
        if (detailsLower.Contains("architecture.md") && !detailsLower.Contains("marker"))
        {
            RecordMilestone("🏛️", "Architecture.md Created",
                $"{agentName} produced the architecture document", "document", agentName);
        }
        if (detailsLower.Contains("engineering plan created") || detailsLower.Contains("engineering-task"))
        {
            RecordMilestone("📐", "Engineering Tasks Created",
                $"{agentName} created engineering task issues", "document", agentName);
        }

        // Detect issue creation
        if (detailsLower.Contains("created") && detailsLower.Contains("issue") &&
            (detailsLower.Contains("user stor") || detailsLower.Contains("task")))
        {
            RecordMilestone("🎫", "User Story Issues Created",
                $"{agentName}: {TruncateDetail(details)}", "issues", agentName);
        }

        // Detect review actions
        if (detailsLower.Contains("approved") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("👍", $"PR {prRef} Approved",
                $"{agentName}: {TruncateDetail(details)}", "review", agentName);
        }
        if (detailsLower.Contains("changes requested") || detailsLower.Contains("requested changes"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("🔄", $"Changes Requested on PR {prRef}",
                $"{agentName}: {TruncateDetail(details)}", "review", agentName);
        }

        // Detect test actions
        if (detailsLower.Contains("test") && (detailsLower.Contains("created") || detailsLower.Contains("written")))
        {
            RecordMilestone("🧪", "Tests Written",
                $"{agentName}: {TruncateDetail(details)}", "test", agentName);
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
            e.Reason, "phase");
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
