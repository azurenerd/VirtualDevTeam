using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Lifecycle;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Shared service that builds a typed <see cref="PipelineStatusSnapshot"/> from in-process
/// services. Used by both the REST API endpoint (/api/pipeline/status) and the
/// <c>PipelineAssessmentService</c>. Extracts the inline anonymous-type logic that was
/// previously hardcoded in Program.cs.
/// </summary>
public sealed class PipelineStatusSnapshotService
{
    private readonly IPullRequestService _pullRequestService;
    private readonly IWorkItemService _workItemService;
    private readonly AgentUsageTracker _usageTracker;
    private readonly FlowTimelineTracker _timelineTracker;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private readonly ILogger<PipelineStatusSnapshotService> _logger;

    // Agents & workflow are injected as Func<> to avoid circular DI — these
    // live in Dashboard/Orchestrator projects while this service is in Core.
    private Func<IReadOnlyList<DashboardAgentInfo>>? _agentSnapshotProvider;
    private Func<string>? _currentPhaseProvider;

    private static readonly Regex WaveRx = new(
        @"\*\*Wave:\*\*\s*(W\d+)", RegexOptions.Compiled);
    private static readonly Regex DepsRx = new(
        @"\*\*Depends On:\*\*\s*((?:#\d+(?:,\s*)?)+)", RegexOptions.Compiled);
    private static readonly Regex TaskIdRx = new(
        @"\[T-?(\w+)\]", RegexOptions.Compiled);

    public PipelineStatusSnapshotService(
        IPullRequestService pullRequestService,
        IWorkItemService workItemService,
        AgentUsageTracker usageTracker,
        FlowTimelineTracker timelineTracker,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<PipelineStatusSnapshotService> logger)
    {
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _usageTracker = usageTracker;
        _timelineTracker = timelineTracker;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Register the agent snapshot provider (called from Runner DI setup where Dashboard
    /// services are available). This avoids a hard dependency on DashboardDataService.
    /// </summary>
    public void SetAgentSnapshotProvider(Func<IReadOnlyList<DashboardAgentInfo>> provider)
        => _agentSnapshotProvider = provider;

    /// <summary>
    /// Register the workflow phase provider (called from Runner DI setup).
    /// </summary>
    public void SetPhaseProvider(Func<string> provider)
        => _currentPhaseProvider = provider;

    /// <summary>
    /// Build a full pipeline snapshot. Best-effort — platform errors result in partial data.
    /// </summary>
    public async Task<PipelineStatusSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = _config.Value;

        // Agents
        var agentDtos = BuildAgentSnapshots(now);

        // Work items + PRs (best-effort)
        IReadOnlyList<PlatformWorkItem> allWorkItems;
        IReadOnlyList<PlatformPullRequest> allPRs;
        try
        {
            var wiTask = _workItemService.ListAllAsync(ct);
            var prTask = _pullRequestService.ListAllAsync(ct);
            await Task.WhenAll(wiTask, prTask);
            allWorkItems = await wiTask;
            allPRs = await prTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineStatusSnapshotService: platform query failed — partial data");
            allWorkItems = Array.Empty<PlatformWorkItem>();
            allPRs = Array.Empty<PlatformPullRequest>();
        }

        // Build PR-to-issue lookup
        var prsByIssue = new Dictionary<int, List<PlatformPullRequest>>();
        foreach (var pr in allPRs)
        {
            var linked = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (linked.HasValue)
            {
                if (!prsByIssue.ContainsKey(linked.Value))
                    prsByIssue[linked.Value] = new();
                prsByIssue[linked.Value].Add(pr);
            }
        }

        // Check for peer review agents
        var hasPeerReview = agentDtos.Count(a =>
            string.Equals(a.Role, "SoftwareEngineer", StringComparison.OrdinalIgnoreCase)) > 1;

        // Build task snapshots
        var engTasks = allWorkItems
            .Where(wi => wi.Labels.Any(l => l.Equals("engineering-task", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(wi => wi.Number)
            .ToList();

        var taskSnapshots = new List<PipelineTaskSnapshot>();
        foreach (var wi in engTasks)
        {
            var linkedPrs = prsByIssue.GetValueOrDefault(wi.Number);
            var prSnapshots = BuildPrSnapshots(linkedPrs, cfg, hasPeerReview, now);

            var taskElapsed = wi.ClosedAt.HasValue
                ? (wi.ClosedAt.Value - wi.CreatedAt)
                : (DateTime.UtcNow - wi.CreatedAt);

            taskSnapshots.Add(new PipelineTaskSnapshot
            {
                Number = wi.Number,
                Title = wi.Title,
                TaskId = ParseTaskId(wi.Title),
                Status = DeriveTaskStatus(wi, linkedPrs),
                Wave = ParseWave(wi.Body),
                Dependencies = ParseDeps(wi.Body).ToArray(),
                ElapsedMinutes = Math.Round(taskElapsed.TotalMinutes, 1),
                LinkedPRs = prSnapshots,
            });
        }

        // Timeline spans
        var timelineSpans = BuildTimelineSpans();

        // Summary
        var statusGroups = taskSnapshots
            .GroupBy(t => t.Status ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        var prStates = allPRs
            .GroupBy(p => p.IsMerged ? "merged"
                : p.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open")
            .ToDictionary(g => g.Key, g => g.Count());

        return new PipelineStatusSnapshot
        {
            ComputedAt = now,
            CurrentPhase = _currentPhaseProvider?.Invoke(),
            Agents = agentDtos.ToArray(),
            WorkItems = taskSnapshots.ToArray(),
            PullRequests = allPRs.Select(pr => BuildFlatPrSnapshot(pr, cfg, hasPeerReview, now)).ToArray(),
            TimelineSpans = timelineSpans,
            Summary = new PipelineSummary
            {
                TotalTasks = engTasks.Count,
                TasksByStatus = statusGroups,
                TotalPRs = allPRs.Count,
                PrsByState = prStates,
                TotalCost = (double)_usageTracker.GetTotalCost(),
            },
        };
    }

    private List<PipelineAgentSnapshot> BuildAgentSnapshots(DateTimeOffset now)
    {
        var agents = _agentSnapshotProvider?.Invoke();
        if (agents is null || agents.Count == 0) return new();

        return agents.Select(a => new PipelineAgentSnapshot
        {
            AgentId = a.Id,
            DisplayName = a.DisplayName,
            Role = a.Role,
            Status = a.Status,
            StatusReason = a.StatusReason,
            DurationSeconds = a.LastStatusChange.HasValue
                ? (now - a.LastStatusChange.Value).TotalSeconds : 0,
            CurrentPrNumber = a.CurrentPrNumber,
            CurrentTaskName = a.CurrentTaskName,
            CurrentStepName = a.CurrentStepName,
            ActiveModel = a.ActiveModel,
            AiCallElapsedSeconds = a.AiCallElapsedSeconds,
            Specialty = a.Specialty,
            EstimatedCost = a.EstimatedCost,
            AiCalls = a.AiCalls,
        }).ToList();
    }

    private PrSnapshot[] BuildPrSnapshots(
        List<PlatformPullRequest>? linkedPrs,
        VirtualDevTeamConfig cfg,
        bool hasPeerReview,
        DateTimeOffset now)
    {
        if (linkedPrs is null || linkedPrs.Count == 0) return Array.Empty<PrSnapshot>();

        return linkedPrs.OrderBy(p => p.Number).Select(pr =>
            BuildFlatPrSnapshot(pr, cfg, hasPeerReview, now)).ToArray();
    }

    private static PrSnapshot BuildFlatPrSnapshot(
        PlatformPullRequest pr,
        VirtualDevTeamConfig cfg,
        bool hasPeerReview,
        DateTimeOffset now)
    {
        var lifecycle = PrLifecycleCalculator.Compute(pr, cfg, comments: null, hasPeerReview);
        var elapsed = pr.IsMerged && pr.MergedAt.HasValue
            ? (pr.MergedAt.Value - pr.CreatedAt)
            : (now.UtcDateTime - pr.CreatedAt);

        return new PrSnapshot
        {
            Number = pr.Number,
            Title = pr.Title,
            State = pr.IsMerged ? "merged"
                : pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open",
            Labels = pr.Labels.ToArray(),
            CreatedAt = pr.CreatedAt,
            MergedAt = pr.MergedAt,
            ElapsedMinutes = Math.Round(elapsed.TotalMinutes, 1),
            NextActor = lifecycle.NextRequiredActor,
            MissingRequirements = lifecycle.MissingRequirements?.ToArray(),
            IsReadyForMerge = lifecycle.IsReadyForMerge,
            IsMerged = lifecycle.IsMerged,
            Stages = lifecycle.Stages.Select(s => new PrLifecycleStageSnapshot
            {
                Id = s.Id,
                Name = s.Name,
                Icon = s.Icon,
                Status = s.Status.ToString(),
                CompletedAt = s.CompletedAt,
                Actor = s.Actor,
                SkipReason = s.SkipReason,
            }).ToArray(),
        };
    }

    private TimelineSpanSnapshot[] BuildTimelineSpans()
    {
        var spans = _timelineTracker.GetTimeline();
        if (spans.Count == 0) return Array.Empty<TimelineSpanSnapshot>();

        return spans.Select(s => new TimelineSpanSnapshot
        {
            Id = s.Id,
            EventType = s.EventType,
            Description = s.Description,
            AgentId = s.AgentId,
            Phase = s.Phase,
            Category = s.Category.ToString(),
            EntityType = s.EntityType,
            EntityId = s.EntityId,
            ParentSpanId = s.ParentSpanId,
            StartedAtUtc = s.StartedAtUtc,
            IsInProgress = s.IsInProgress,
            DurationSeconds = s.Duration?.TotalSeconds,
            ElapsedSeconds = s.ElapsedSinceStart.TotalSeconds,
        }).ToArray();
    }

    // ── Parsing helpers (same as Program.cs inline logic) ──

    private static string? ParseWave(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var m = WaveRx.Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<int> ParseDeps(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new();
        var m = DepsRx.Match(body);
        if (!m.Success) return new();
        return Regex.Matches(m.Groups[1].Value, @"#(\d+)")
            .Select(dm => int.Parse(dm.Groups[1].Value))
            .ToList();
    }

    private static string? ParseTaskId(string title)
    {
        var m = TaskIdRx.Match(title);
        return m.Success ? $"T-{m.Groups[1].Value}" : null;
    }

    private static string DeriveTaskStatus(PlatformWorkItem wi, List<PlatformPullRequest>? linkedPrs)
    {
        var labels = wi.Labels;
        if (labels.Any(l => l.Equals("status:done", StringComparison.OrdinalIgnoreCase))
            || wi.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
            return "done";
        if (labels.Any(l => l.Equals("status:blocked", StringComparison.OrdinalIgnoreCase)))
            return "blocked";
        if (labels.Any(l => l.Equals("status:in-progress", StringComparison.OrdinalIgnoreCase)
                         || l.Equals("in-progress", StringComparison.OrdinalIgnoreCase)))
            return "in-progress";
        if (linkedPrs?.Any(p => p.State.Equals("open", StringComparison.OrdinalIgnoreCase)) == true)
            return "in-progress";
        if (linkedPrs?.Any(p => p.IsMerged) == true)
            return "done";
        return "pending";
    }
}

/// <summary>
/// Lightweight DTO for agent info needed by the snapshot service.
/// Avoids direct dependency on Dashboard's AgentSnapshot type (different project).
/// Populated by the Runner's DI setup from DashboardDataService.GetAllAgentSnapshots().
/// </summary>
public sealed record DashboardAgentInfo
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Role { get; init; }
    public string? Status { get; init; }
    public string? StatusReason { get; init; }
    public DateTimeOffset? LastStatusChange { get; init; }
    public int? CurrentPrNumber { get; init; }
    public string? CurrentTaskName { get; init; }
    public string? CurrentStepName { get; init; }
    public string? ActiveModel { get; init; }
    public double? AiCallElapsedSeconds { get; init; }
    public string? Specialty { get; init; }
    public double EstimatedCost { get; init; }
    public int AiCalls { get; init; }
}
