using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub.Models;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Manages Issue-based communication between agents and the Executive.
/// Issue titles follow conventions: "{AgentName}: {Title}" or "Executive Request: {Title}".
/// </summary>
public class IssueWorkflow
{
    private readonly IWorkItemService _workItemService;
    private readonly ILogger<IssueWorkflow> _logger;

    public static class Labels
    {
        public const string ExecutiveRequest = "executive-request";
        public const string ResourceRequest = "resource-request";
        public const string Blocker = "blocker";
        public const string AgentQuestion = "agent-question";
        public const string AgentStuck = "agent-stuck";
        /// <summary>
        /// imggen-action-handlers: applied by <see cref="HealthMonitor.Actions.EscalateToHumanAction"/>
        /// when an <c>image-spec-mismatch</c> finding is escalated to rung 3. Signals that a PMSpec
        /// (or Architecture) image-deliverable manifest entry is missing/empty on the working branch.
        /// </summary>
        public const string ArtMissing = "art-missing";
        /// <summary>
        /// imggen-action-handlers: applied by <see cref="HealthMonitor.Actions.EscalateToHumanAction"/>
        /// when an <c>image-regen-anomaly</c> finding is escalated to rung 3. Signals that a PR's
        /// image-rework commit produced a perceptually-identical PNG (the rework was a no-op).
        /// </summary>
        public const string ArtRegenNoop = "art-regen-noop";
        /// <summary>
        /// flowmonitor-rework-size-anomaly-detector: applied by
        /// <see cref="HealthMonitor.Actions.EscalateToHumanAction"/> when a
        /// <c>doc-rework-size-anomaly</c> finding is escalated to rung 3. Signals that a doc PR's
        /// rework commit (PMSpec.md or Architecture.md) produced an unexpectedly large size delta —
        /// a likely "stub→full-rewrite" or "full→stub" regression that needs operator review.
        /// </summary>
        public const string DocRegenAnomaly = "doc-regen-anomaly";
        public const string Resolved = "resolved";
        public const string Enhancement = "enhancement";
        public const string Documentation = "documentation";
        /// <summary>
        /// The "engineering-task" label that the SE Leader applies to issues it generates
        /// from the engineering plan. This is the canonical Core-side constant — the
        /// duplicate <c>EngineeringTaskIssueManager.TaskLabel</c> in the Agents project is
        /// an alias. NoMessyCodePlan Theme 2.
        /// </summary>
        public const string EngineeringTask = "engineering-task";
    }

    public IssueWorkflow(IWorkItemService workItemService, ILogger<IssueWorkflow> logger)
    {
        _workItemService = workItemService ?? throw new ArgumentNullException(nameof(workItemService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Parse the target agent name from an issue title: "Software Engineer 1: Question" → "Software Engineer 1"
    /// </summary>
    public static string? ParseAgentNameFromTitle(string title)
    {
        return PullRequestWorkflow.ParseAgentNameFromTitle(title);
    }

    /// <summary>
    /// Find an existing open issue by title prefix match. Returns null if none found.
    /// Use this before creating issues to prevent duplicates on restart.
    /// </summary>
    public async Task<AgentIssue?> FindExistingIssueAsync(
        string titlePrefix,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titlePrefix);

        var allItems = await _workItemService.ListOpenAsync(ct);
        var match = allItems.FirstOrDefault(i =>
            i.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase));
        return match?.ToAgentIssue();
    }

    /// <summary>
    /// Create an issue requesting additional resources from the PM.
    /// </summary>
    public async Task<AgentIssue> RequestResourceAsync(
        string requestingAgent,
        AgentRole requestedRole,
        string justification,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        var title = $"Executive Request: Resource Request from {requestingAgent}";

        // Idempotency: check for existing issue
        var existing = await FindExistingIssueAsync(title, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Resource request issue already exists as #{Number}, skipping", existing.Number);
            return existing;
        }

        var body = $"""
            ## Resource Request
            **From:** {requestingAgent}
            **Requested Role:** {requestedRole}

            ## Justification
            {justification}
            """;

        _logger.LogInformation("Agent {Agent} requesting {Role} resource", requestingAgent, requestedRole);

        var item = await _workItemService.CreateAsync(
            title, body,
            [Labels.ResourceRequest, Labels.ExecutiveRequest],
            ct);
        return item.ToAgentIssue();
    }

    /// <summary>
    /// Create an issue for Executive approval.
    /// </summary>
    public async Task<AgentIssue> CreateExecutiveRequestAsync(
        string fromAgent,
        string title,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var issueTitle = $"Executive Request: {title}";

        // Idempotency: check for existing issue
        var existing = await FindExistingIssueAsync(issueTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Executive request already exists as #{Number}, skipping", existing.Number);
            return existing;
        }

        var issueBody = $"""
            ## Executive Request
            **From:** {fromAgent}

            ## Details
            {body}
            """;

        _logger.LogInformation("Agent {Agent} creating executive request: {Title}", fromAgent, title);

        var item = await _workItemService.CreateAsync(
            issueTitle, issueBody,
            [Labels.ExecutiveRequest],
            ct);
        return item.ToAgentIssue();
    }

    /// <summary>
    /// Create a blocker issue to signal that an agent is stuck.
    /// </summary>
    public async Task<AgentIssue> ReportBlockerAsync(
        string agentName,
        string title,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var issueTitle = $"{agentName}: 🚫 {title}";

        // Idempotency: check for existing blocker
        var existing = await FindExistingIssueAsync(issueTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Blocker issue already exists as #{Number}, skipping", existing.Number);
            return existing;
        }

        var issueBody = $"""
            ## Blocker Report
            **Agent:** {agentName}

            ## Description
            {body}

            ## Impact
            This agent is blocked and cannot make further progress until this is resolved.
            """;

        _logger.LogWarning("Agent {Agent} reporting blocker: {Title}", agentName, title);

        var item = await _workItemService.CreateAsync(
            issueTitle, issueBody,
            [Labels.Blocker, Labels.AgentStuck],
            ct);
        return item.ToAgentIssue();
    }

    /// <summary>
    /// Create an agent question issue targeted at another agent.
    /// </summary>
    public async Task<AgentIssue> AskAgentAsync(
        string fromAgent,
        string toAgent,
        string question,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(toAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var title = $"{toAgent}: Question from {fromAgent}";

        // Idempotency: check for existing question
        var existing = await FindExistingIssueAsync(title, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Question issue already exists as #{Number}, skipping", existing.Number);
            return existing;
        }

        var body = $"""
            ## Agent Question
            **From:** {fromAgent}
            **To:** {toAgent}

            ## Question
            {question}
            """;

        _logger.LogInformation("Agent {From} asking {To} a question", fromAgent, toAgent);

        var item = await _workItemService.CreateAsync(
            title, body,
            [Labels.AgentQuestion],
            ct);
        return item.ToAgentIssue();
    }

    /// <summary>
    /// Check if an issue has been resolved (closed or has "resolved" label).
    /// </summary>
    public async Task<bool> IsIssueResolvedAsync(int issueNumber, CancellationToken ct = default)
    {
        var item = await _workItemService.GetAsync(issueNumber, ct);
        if (item is null)
            return false;

        if (string.Equals(item.State, "closed", StringComparison.OrdinalIgnoreCase))
            return true;

        return item.Labels.Contains(Labels.Resolved, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the latest comments on an issue (for checking Executive responses).
    /// </summary>
    public async Task<IReadOnlyList<IssueComment>> GetIssueResponsesAsync(
        int issueNumber,
        CancellationToken ct = default)
    {
        var comments = await _workItemService.GetCommentsAsync(issueNumber, ct);
        return comments.Select(c => new IssueComment
        {
            Author = c.Author,
            Body = c.Body,
            CreatedAt = c.CreatedAt
        }).ToList().AsReadOnly();
    }

    /// <summary>
    /// Get all open issues targeted at a specific agent (agent name appears in the title).
    /// </summary>
    public async Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(
        string agentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var allItems = await _workItemService.ListOpenAsync(ct);
        return allItems
            .Where(item =>
                string.Equals(ParseAgentNameFromTitle(item.Title), agentName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ToAgentIssue())
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Resolve and close an issue with a resolution comment.
    /// </summary>
    public async Task ResolveIssueAsync(
        int issueNumber,
        string resolutionComment,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionComment);

        _logger.LogInformation("Resolving issue #{Number}", issueNumber);

        await _workItemService.AddCommentAsync(
            issueNumber,
            $"✅ **Resolved**\n\n{resolutionComment}",
            ct);

        await _workItemService.CloseAsync(issueNumber, ct);
    }
}
