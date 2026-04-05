using System.Text.RegularExpressions;
using AgentSquad.Core.GitHub.Models;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Manages the PR-based task assignment pattern where PRs are titled "[AgentName]: Task Title".
/// </summary>
public partial class PullRequestWorkflow
{
    private readonly IGitHubService _github;
    private readonly ILogger<PullRequestWorkflow> _logger;
    private readonly string _defaultBranch;

    public static class Labels
    {
        public const string ReadyForReview = "ready-for-review";
        public const string Approved = "approved";
        public const string InProgress = "in-progress";
        public const string HighComplexity = "complexity-high";
        public const string MediumComplexity = "complexity-medium";
        public const string LowComplexity = "complexity-low";
    }

    public PullRequestWorkflow(
        IGitHubService github,
        ILogger<PullRequestWorkflow> logger,
        string defaultBranch = "main")
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultBranch = defaultBranch;
    }

    /// <summary>
    /// Parse agent name from PR title: "Senior Engineer 1: Implement auth" → "Senior Engineer 1"
    /// </summary>
    public static string? ParseAgentNameFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var match = AgentTitlePattern().Match(title);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Parse task title from PR title: "Senior Engineer 1: Implement auth" → "Implement auth"
    /// </summary>
    public static string? ParseTaskTitleFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var match = AgentTitlePattern().Match(title);
        return match.Success ? match.Groups[2].Value.Trim() : null;
    }

    /// <summary>
    /// Create a task PR assigned to a specific agent.
    /// </summary>
    public async Task<AgentPullRequest> CreateTaskPullRequestAsync(
        string agentName,
        string taskTitle,
        string taskDescription,
        string complexity,
        string? architectureRef,
        string? specRef,
        string branchName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var prTitle = $"{agentName}: {taskTitle}";
        var body = FormatPullRequestBody(agentName, complexity, branchName, taskDescription, architectureRef, specRef);
        var complexityLabel = GetComplexityLabel(complexity);

        var labels = new List<string> { Labels.InProgress };
        if (complexityLabel is not null)
            labels.Add(complexityLabel);

        _logger.LogInformation("Creating task PR '{Title}' on branch {Branch}", prTitle, branchName);

        var pr = await _github.CreatePullRequestAsync(
            prTitle, body, branchName, _defaultBranch, [.. labels], ct);

        _logger.LogInformation("Created PR #{Number} for agent {Agent}", pr.Number, agentName);
        return pr;
    }

    /// <summary>
    /// Get all open PRs assigned to a specific agent.
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetAgentTasksAsync(
        string agentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var allPrs = await _github.GetOpenPullRequestsAsync(ct);
        return allPrs
            .Where(pr => string.Equals(ParseAgentNameFromTitle(pr.Title), agentName, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Get unassigned PRs (no agent prefix in title).
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetUnassignedTasksAsync(
        CancellationToken ct = default)
    {
        var allPrs = await _github.GetOpenPullRequestsAsync(ct);
        return allPrs
            .Where(pr => ParseAgentNameFromTitle(pr.Title) is null)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Mark a PR as ready for review by adding a comment and updating labels.
    /// </summary>
    public async Task MarkReadyForReviewAsync(
        int prNumber,
        string agentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        _logger.LogInformation("Agent {Agent} marking PR #{Number} ready for review", agentName, prNumber);

        await _github.AddPullRequestCommentAsync(
            prNumber,
            $"✅ **{agentName}** has marked this PR as ready for review.\n\nAll implementation and tests are complete.",
            ct);

        // Update labels: swap in-progress for ready-for-review
        var pr = await _github.GetPullRequestAsync(prNumber, ct);
        if (pr is not null)
        {
            var updatedLabels = pr.Labels
                .Where(l => !string.Equals(l, Labels.InProgress, StringComparison.OrdinalIgnoreCase))
                .Append(Labels.ReadyForReview)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await _github.UpdatePullRequestAsync(prNumber, labels: updatedLabels, ct: ct);
        }
    }

    /// <summary>
    /// Submit a code review on a PR.
    /// </summary>
    public async Task SubmitReviewAsync(
        int prNumber,
        string reviewerAgent,
        string body,
        bool approve,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var reviewBody = $"**Review by {reviewerAgent}**\n\n{body}";
        var eventType = approve ? "APPROVE" : "REQUEST_CHANGES";

        _logger.LogInformation("Agent {Agent} submitting {ReviewType} review on PR #{Number}",
            reviewerAgent, eventType, prNumber);

        await _github.AddPullRequestReviewAsync(prNumber, reviewBody, eventType, ct);

        if (approve)
        {
            var pr = await _github.GetPullRequestAsync(prNumber, ct);
            if (pr is not null)
            {
                var updatedLabels = pr.Labels
                    .Append(Labels.Approved)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await _github.UpdatePullRequestAsync(prNumber, labels: updatedLabels, ct: ct);
            }
        }
    }

    /// <summary>
    /// Get PRs pending review (has "ready-for-review" label but not "approved").
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetPendingReviewsAsync(
        CancellationToken ct = default)
    {
        var allPrs = await _github.GetOpenPullRequestsAsync(ct);
        return allPrs
            .Where(pr =>
                pr.Labels.Contains(Labels.ReadyForReview) &&
                !pr.Labels.Contains(Labels.Approved))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Create the feature branch for a task: agent/{agent-name-slug}/{task-slug}
    /// </summary>
    public async Task<string> CreateTaskBranchAsync(
        string agentName,
        string taskSlug,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskSlug);

        var agentSlug = Slugify(agentName);
        var normalizedTaskSlug = Slugify(taskSlug);
        var branchName = $"agent/{agentSlug}/{normalizedTaskSlug}";

        _logger.LogInformation("Creating task branch {Branch} from {DefaultBranch}", branchName, _defaultBranch);

        if (await _github.BranchExistsAsync(branchName, ct))
        {
            _logger.LogWarning("Branch {Branch} already exists, reusing it", branchName);
            return branchName;
        }

        await _github.CreateBranchAsync(branchName, _defaultBranch, ct);
        return branchName;
    }

    private static string FormatPullRequestBody(
        string agentName,
        string complexity,
        string branchName,
        string taskDescription,
        string? architectureRef,
        string? specRef)
    {
        return $"""
            ## Task Assignment
            **Assigned To:** {agentName}
            **Complexity:** {complexity}
            **Branch:** `{branchName}`

            ## Requirements
            {taskDescription}

            ## References
            - Architecture: {architectureRef ?? "N/A"}
            - PM Spec: {specRef ?? "N/A"}

            ## Status
            - [ ] Implementation
            - [ ] Tests Written
            - [ ] Ready for Review
            """;
    }

    private static string? GetComplexityLabel(string complexity)
    {
        return complexity.ToLowerInvariant() switch
        {
            "high" => Labels.HighComplexity,
            "medium" => Labels.MediumComplexity,
            "low" => Labels.LowComplexity,
            _ => null
        };
    }

    internal static string Slugify(string value)
    {
        var slug = value.Trim().ToLowerInvariant();
        slug = SlugifyWhitespacePattern().Replace(slug, "-");
        slug = SlugifyInvalidCharsPattern().Replace(slug, "");
        slug = SlugifyMultipleDashPattern().Replace(slug, "-");
        return slug.Trim('-');
    }

    [GeneratedRegex(@"^(.+?):\s*(.+)$")]
    private static partial Regex AgentTitlePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SlugifyWhitespacePattern();

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugifyInvalidCharsPattern();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex SlugifyMultipleDashPattern();
}
