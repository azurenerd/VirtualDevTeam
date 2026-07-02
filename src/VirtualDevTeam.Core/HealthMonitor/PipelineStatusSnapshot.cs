namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Typed snapshot of the full pipeline status — shared between the REST API
/// endpoint (/api/pipeline/status) and the PipelineAssessmentService.
/// Replaces the inline anonymous types in Program.cs with a strongly-typed model.
/// </summary>
public sealed record PipelineStatusSnapshot
{
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CurrentPhase { get; init; }
    public PipelineAgentSnapshot[]? Agents { get; init; }
    public PipelineTaskSnapshot[]? WorkItems { get; init; }
    public PrSnapshot[]? PullRequests { get; init; }
    public TimelineSpanSnapshot[]? TimelineSpans { get; init; }
    public PipelineSummary? Summary { get; init; }

    /// <summary>Serialize for LLM context with budget enforcement.</summary>
    public string ToContextString(int maxChars = 40000)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"## Pipeline Status (as of {ComputedAt:yyyy-MM-dd HH:mm:ss} UTC)");
        sb.AppendLine($"Phase: {CurrentPhase ?? "Unknown"}");
        sb.AppendLine();

        // Summary first (always fits)
        if (Summary is not null)
        {
            sb.AppendLine("### Summary");
            sb.AppendLine($"- Tasks: {Summary.TotalTasks} total — {FormatDict(Summary.TasksByStatus)}");
            sb.AppendLine($"- PRs: {Summary.TotalPRs} total — {FormatDict(Summary.PrsByState)}");
            sb.AppendLine($"- Total cost: ${Summary.TotalCost:F2}");
            sb.AppendLine();
        }

        // Agents (compact)
        if (Agents is { Length: > 0 })
        {
            sb.AppendLine("### Agents");
            foreach (var a in Agents)
            {
                sb.Append($"- [{a.AgentId}] {a.DisplayName}: {a.Status}");
                if (!string.IsNullOrEmpty(a.StatusReason)) sb.Append($" ({a.StatusReason})");
                sb.Append($" | since {a.DurationSeconds:F0}s ago");
                if (!string.IsNullOrEmpty(a.CurrentTaskName)) sb.Append($" | task: {a.CurrentTaskName}");
                if (!string.IsNullOrEmpty(a.CurrentStepName)) sb.Append($" | step: {a.CurrentStepName}");
                if (a.AiCallElapsedSeconds.HasValue) sb.Append($" | AI call: {a.AiCallElapsedSeconds:F0}s ({a.ActiveModel})");
                if (a.CurrentPrNumber.HasValue) sb.Append($" | PR #{a.CurrentPrNumber}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        // Work items with linked PRs
        if (WorkItems is { Length: > 0 })
        {
            sb.AppendLine("### Work Items / Tasks");
            foreach (var t in WorkItems)
            {
                sb.Append($"- #{t.Number} [{t.TaskId ?? "?"}] {t.Title}: {t.Status}");
                if (!string.IsNullOrEmpty(t.Wave)) sb.Append($" (Wave {t.Wave})");
                if (t.Dependencies is { Length: > 0 }) sb.Append($" depends on: {string.Join(", ", t.Dependencies.Select(d => $"#{d}"))}");
                sb.Append($" | {t.ElapsedMinutes:F0}min");
                sb.AppendLine();

                if (t.LinkedPRs is { Length: > 0 })
                {
                    foreach (var pr in t.LinkedPRs)
                    {
                        sb.Append($"  └─ PR #{pr.Number}: {pr.State}");
                        sb.Append($" | {pr.ElapsedMinutes:F0}min");
                        if (pr.NextActor is not null) sb.Append($" | next: {pr.NextActor}");
                        if (pr.Labels is { Length: > 0 }) sb.Append($" | labels: {string.Join(", ", pr.Labels)}");
                        if (pr.MissingRequirements is { Length: > 0 }) sb.Append($" | missing: {string.Join(", ", pr.MissingRequirements)}");
                        sb.AppendLine();
                    }
                }
            }
            sb.AppendLine();
        }

        // Timeline spans (budget-managed)
        if (TimelineSpans is { Length: > 0 })
        {
            var remaining = maxChars - sb.Length - 500; // reserve 500 for footer
            AppendTimelineSpans(sb, TimelineSpans, remaining);
        }

        // Truncate if over budget
        if (sb.Length > maxChars)
        {
            sb.Length = maxChars - 100;
            sb.AppendLine();
            sb.AppendLine("[... truncated to fit context budget ...]");
        }

        return sb.ToString();
    }

    private static void AppendTimelineSpans(System.Text.StringBuilder sb, TimelineSpanSnapshot[] spans, int charBudget)
    {
        sb.AppendLine("### Timeline Spans (step-level detail)");

        // Tier 1: in-progress spans (always include)
        var inProgress = spans.Where(s => s.IsInProgress).ToList();
        // Tier 2: completed in last 2h, capped at 50
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        var recent = spans
            .Where(s => !s.IsInProgress && s.StartedAtUtc >= cutoff)
            .OrderByDescending(s => s.StartedAtUtc)
            .Take(50)
            .ToList();
        var omitted = spans.Length - inProgress.Count - recent.Count;

        if (inProgress.Count > 0)
        {
            sb.AppendLine("#### In-Progress");
            foreach (var s in inProgress)
            {
                if (sb.Length > charBudget) { sb.AppendLine("[... remaining spans omitted for budget ...]"); return; }
                AppendSpan(sb, s);
            }
        }

        if (recent.Count > 0)
        {
            sb.AppendLine("#### Recent (last 2h)");
            foreach (var s in recent)
            {
                if (sb.Length > charBudget) { sb.AppendLine("[... remaining spans omitted for budget ...]"); return; }
                AppendSpan(sb, s);
            }
        }

        if (omitted > 0)
        {
            // Category distribution for omitted spans
            var oldSpans = spans.Where(s => !s.IsInProgress && s.StartedAtUtc < cutoff).ToList();
            var catDist = oldSpans
                .GroupBy(s => s.Category ?? "Work")
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            sb.AppendLine($"[{omitted} older spans omitted. Distribution: {string.Join(", ", catDist)}]");
        }
        sb.AppendLine();
    }

    private static void AppendSpan(System.Text.StringBuilder sb, TimelineSpanSnapshot s)
    {
        var indent = string.IsNullOrEmpty(s.ParentSpanId) ? "- " : "  └─ ";
        sb.Append($"{indent}[{s.Category}] {s.Description}");
        if (s.IsInProgress)
            sb.Append($" | RUNNING for {s.ElapsedSeconds:F0}s");
        else if (s.DurationSeconds.HasValue)
            sb.Append($" | took {s.DurationSeconds:F0}s");
        if (!string.IsNullOrEmpty(s.AgentId)) sb.Append($" | agent: {s.AgentId}");
        sb.AppendLine();
    }

    private static string FormatDict(Dictionary<string, int>? dict)
    {
        if (dict is null || dict.Count == 0) return "none";
        return string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}

// ── Sub-records ──────────────────────────────────────────────────────

public sealed record PipelineAgentSnapshot
{
    public string? AgentId { get; init; }
    public string? DisplayName { get; init; }
    public string? Role { get; init; }
    public string? Status { get; init; }
    public string? StatusReason { get; init; }
    public double DurationSeconds { get; init; }
    public int? CurrentPrNumber { get; init; }
    public string? CurrentTaskName { get; init; }
    public string? CurrentStepName { get; init; }
    public string? ActiveModel { get; init; }
    public double? AiCallElapsedSeconds { get; init; }
    public string? Specialty { get; init; }
    public double EstimatedCost { get; init; }
    public int AiCalls { get; init; }
}

public sealed record PipelineTaskSnapshot
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public string? TaskId { get; init; }
    public string? Status { get; init; }
    public string? Wave { get; init; }
    public int[]? Dependencies { get; init; }
    public double ElapsedMinutes { get; init; }
    public PrSnapshot[]? LinkedPRs { get; init; }
}

public sealed record PrSnapshot
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public string[]? Labels { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
    public double ElapsedMinutes { get; init; }
    public string? NextActor { get; init; }
    public string[]? MissingRequirements { get; init; }
    public bool IsReadyForMerge { get; init; }
    public bool IsMerged { get; init; }
    public PrLifecycleStageSnapshot[]? Stages { get; init; }
}

public sealed record PrLifecycleStageSnapshot
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Icon { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Actor { get; init; }
    public string? SkipReason { get; init; }
}

public sealed record TimelineSpanSnapshot
{
    public string? Id { get; init; }
    public string? EventType { get; init; }
    public string? Description { get; init; }
    public string? AgentId { get; init; }
    public string? Phase { get; init; }
    public string? Category { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? ParentSpanId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public bool IsInProgress { get; init; }
    public double? DurationSeconds { get; init; }
    public double ElapsedSeconds { get; init; }
}

public sealed record PipelineSummary
{
    public int TotalTasks { get; init; }
    public Dictionary<string, int>? TasksByStatus { get; init; }
    public int TotalPRs { get; init; }
    public Dictionary<string, int>? PrsByState { get; init; }
    public double TotalCost { get; init; }
}
