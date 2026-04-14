using System.Text.RegularExpressions;
using System.Net.Http.Json;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Provides engineering plan data for the dashboard visualization.
/// In embedded mode: queries GitHub issues directly.
/// In standalone mode: fetches pre-built plan from Runner API.
/// </summary>
public sealed partial class EngineeringPlanDataService
{
    private readonly IGitHubService _github;
    private readonly AgentStateStore _stateStore;
    private readonly ILogger<EngineeringPlanDataService> _logger;
    private readonly HttpClient? _httpClient;

    private EngineeringPlanViewModel? _cache;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);

    public EngineeringPlanDataService(IGitHubService github, AgentStateStore stateStore, ILogger<EngineeringPlanDataService> logger)
    {
        _github = github;
        _stateStore = stateStore;
        _logger = logger;
    }

    public EngineeringPlanDataService(IGitHubService github, AgentStateStore stateStore, IHttpClientFactory httpClientFactory, ILogger<EngineeringPlanDataService> logger)
        : this(github, stateStore, logger)
    {
        _httpClient = httpClientFactory.CreateClient("RunnerApi");
    }

    public async Task<EngineeringPlanViewModel> GetPlanAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cache is not null && DateTime.UtcNow - _lastFetchUtc < CacheExpiry)
            return _cache;

        // Standalone mode: fetch from Runner API
        if (_httpClient is not null)
        {
            try
            {
                var plan = await _httpClient.GetFromJsonAsync<EngineeringPlanViewModel>("/api/dashboard/engineering-plan", ct);
                if (plan is not null)
                {
                    _cache = plan;
                    _lastFetchUtc = DateTime.UtcNow;
                    return _cache;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch engineering plan from Runner API, using cache");
                return _cache ?? new EngineeringPlanViewModel();
            }
        }

        try
        {
            var openTasks = await _github.GetIssuesByLabelAsync("engineering-task", "open", ct);
            var closedTasks = await _github.GetIssuesByLabelAsync("engineering-task", "closed", ct);
            var allTasks = openTasks.Concat(closedTasks)
                .Where(i => i.CreatedAt >= _stateStore.RunStartedUtc)
                .ToList();

            // Also fetch enhancement issues for parent grouping
            var openEnhancements = await _github.GetIssuesByLabelAsync("enhancement", "open", ct);
            var closedEnhancements = await _github.GetIssuesByLabelAsync("enhancement", "closed", ct);
            var allEnhancements = openEnhancements.Concat(closedEnhancements)
                .Where(i => i.CreatedAt >= _stateStore.RunStartedUtc)
                .ToList();

            var nodes = new List<TaskNode>();
            var edges = new List<TaskEdge>();

            // Build enhancement (parent) nodes
            var enhancementMap = new Dictionary<int, TaskNode>();
            foreach (var issue in allEnhancements)
            {
                var node = new TaskNode
                {
                    Id = $"enhancement-{issue.Number}",
                    IssueNumber = issue.Number,
                    Title = CleanTitle(issue.Title),
                    Description = TruncateBody(issue.Body),
                    Status = issue.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "Done" : "Open",
                    NodeType = "enhancement",
                    Complexity = "",
                    IssueUrl = issue.Url,
                    CreatedAt = issue.CreatedAt,
                    ClosedAt = issue.ClosedAt,
                    Labels = issue.Labels
                };
                enhancementMap[issue.Number] = node;
                nodes.Add(node);
            }

            // Build task nodes
            foreach (var issue in allTasks)
            {
                var parsed = ParseTaskBody(issue.Body);
                var status = DetermineStatus(issue);
                var assignee = parsed.Assignee ?? issue.AssignedAgent;

                var node = new TaskNode
                {
                    Id = parsed.TaskId ?? $"task-{issue.Number}",
                    IssueNumber = issue.Number,
                    Title = CleanTitle(issue.Title),
                    Description = TruncateBody(issue.Body),
                    Status = status,
                    NodeType = "task",
                    Complexity = parsed.Complexity ?? "Medium",
                    AssignedTo = assignee,
                    PullRequestNumber = parsed.PrNumber,
                    ParentIssueNumber = parsed.ParentIssue,
                    IssueUrl = issue.Url,
                    CreatedAt = issue.CreatedAt,
                    ClosedAt = issue.ClosedAt,
                    Labels = issue.Labels
                };
                nodes.Add(node);

                // Dependency edges
                foreach (var depIssue in parsed.DependencyIssueNumbers)
                {
                    var depNode = nodes.FirstOrDefault(n => n.IssueNumber == depIssue);
                    var depId = depNode?.Id ?? $"task-{depIssue}";
                    edges.Add(new TaskEdge
                    {
                        Source = depId,
                        Target = node.Id,
                        Label = "blocks"
                    });
                }

                // Parent-child edges (enhancement → task)
                if (parsed.ParentIssue.HasValue && enhancementMap.ContainsKey(parsed.ParentIssue.Value))
                {
                    edges.Add(new TaskEdge
                    {
                        Source = $"enhancement-{parsed.ParentIssue.Value}",
                        Target = node.Id,
                        Label = "parent"
                    });
                }
            }

            var stats = new PlanStats
            {
                Total = nodes.Count(n => n.NodeType == "task"),
                Done = nodes.Count(n => n.NodeType == "task" && n.Status == "Done"),
                InProgress = nodes.Count(n => n.NodeType == "task" && n.Status == "InProgress"),
                Pending = nodes.Count(n => n.NodeType == "task" && n.Status == "Pending"),
                Blocked = nodes.Count(n => n.NodeType == "task" && n.Status == "Blocked"),
                EnhancementCount = allEnhancements.Count
            };

            _cache = new EngineeringPlanViewModel { Nodes = nodes, Edges = edges, Stats = stats };
            _lastFetchUtc = DateTime.UtcNow;
            return _cache;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load engineering plan data");
            return _cache ?? new EngineeringPlanViewModel();
        }
    }

    private static string DetermineStatus(AgentIssue issue)
    {
        if (issue.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
            return "Done";

        if (issue.Labels.Any(l => l.Equals("status:in-progress", StringComparison.OrdinalIgnoreCase)))
            return "InProgress";

        if (issue.Labels.Any(l => l.Equals("status:assigned", StringComparison.OrdinalIgnoreCase)))
            return "Assigned";

        return "Pending";
    }

    private static string CleanTitle(string title)
    {
        // Strip agent prefix like "PrincipalEngineer: " or "Senior Engineer 1: "
        var match = Regex.Match(title, @"^[\w\s]+\d*:\s*(.+)$");
        return match.Success ? match.Groups[1].Value : title;
    }

    private static string TruncateBody(string body) =>
        body.Length > 300 ? body[..300] + "..." : body;

    private static ParsedTaskBody ParseTaskBody(string body)
    {
        var result = new ParsedTaskBody();

        // Task ID: **Task ID:** T1
        var idMatch = Regex.Match(body, @"\*\*Task ID:\*\*\s*(\S+)", RegexOptions.IgnoreCase);
        if (idMatch.Success) result.TaskId = idMatch.Groups[1].Value;

        // Complexity: **Complexity:** High
        var complexityMatch = Regex.Match(body, @"\*\*Complexity:\*\*\s*(\w+)", RegexOptions.IgnoreCase);
        if (complexityMatch.Success) result.Complexity = complexityMatch.Groups[1].Value;

        // Dependencies: **Depends On:** #336, #337
        var depsMatch = Regex.Match(body, @"\*\*Depends On:\*\*\s*(.+)", RegexOptions.IgnoreCase);
        if (depsMatch.Success)
        {
            result.DependencyIssueNumbers = Regex.Matches(depsMatch.Groups[1].Value, @"#(\d+)")
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();
        }

        // Parent Issue: **Parent Issue:** #332
        var parentMatch = Regex.Match(body, @"\*\*Parent Issue:\*\*\s*#(\d+)", RegexOptions.IgnoreCase);
        if (parentMatch.Success) result.ParentIssue = int.Parse(parentMatch.Groups[1].Value);

        // Assignee: **Assigned To:** PrincipalEngineer
        var assigneeMatch = Regex.Match(body, @"\*\*Assigned To:\*\*\s*(.+)", RegexOptions.IgnoreCase);
        if (assigneeMatch.Success) result.Assignee = assigneeMatch.Groups[1].Value.Trim();

        // PR: **Pull Request:** #385
        var prMatch = Regex.Match(body, @"\*\*Pull Request:\*\*\s*#(\d+)", RegexOptions.IgnoreCase);
        if (prMatch.Success) result.PrNumber = int.Parse(prMatch.Groups[1].Value);

        return result;
    }

    private record ParsedTaskBody
    {
        public string? TaskId { get; set; }
        public string? Complexity { get; set; }
        public List<int> DependencyIssueNumbers { get; set; } = new();
        public int? ParentIssue { get; set; }
        public string? Assignee { get; set; }
        public int? PrNumber { get; set; }
    }
}

// ── View Models ──────────────────────────────────────────────────────────

public sealed record EngineeringPlanViewModel
{
    public List<TaskNode> Nodes { get; init; } = new();
    public List<TaskEdge> Edges { get; init; } = new();
    public PlanStats Stats { get; init; } = new();
}

public sealed record TaskNode
{
    public required string Id { get; init; }
    public int IssueNumber { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string NodeType { get; init; } = "task"; // "task" or "enhancement"
    public string Complexity { get; init; } = "Medium";
    public string? AssignedTo { get; init; }
    public int? PullRequestNumber { get; init; }
    public int? ParentIssueNumber { get; init; }
    public string IssueUrl { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public List<string> Labels { get; init; } = new();
}

public sealed record TaskEdge
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public string Label { get; init; } = "blocks";
}

public sealed record PlanStats
{
    public int Total { get; init; }
    public int Done { get; init; }
    public int InProgress { get; init; }
    public int Pending { get; init; }
    public int Blocked { get; init; }
    public int EnhancementCount { get; init; }
    public double ProgressPercent => Total > 0 ? Math.Round(Done * 100.0 / Total) : 0;
}
