using AgentSquad.Core.Agents;
using AgentSquad.Core.GitHub.Models;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Manages Issue-based communication between agents and the Executive.
/// Issue titles follow conventions: "{AgentName}: {Title}" or "Executive Request: {Title}".
/// </summary>
public class IssueWorkflow
{
    private readonly IGitHubService _github;
    private readonly ILogger<IssueWorkflow> _logger;

    public static class Labels
    {
        public const string ExecutiveRequest = "executive-request";
        public const string ResourceRequest = "resource-request";
        public const string Blocker = "blocker";
        public const string AgentQuestion = "agent-question";
        public const string AgentStuck = "agent-stuck";
        public const string Resolved = "resolved";
        public const string Enhancement = "enhancement";
        public const string Documentation = "documentation";
    }

    public IssueWorkflow(IGitHubService github, ILogger<IssueWorkflow> logger)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
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

        var allIssues = await _github.GetOpenIssuesAsync(ct);
        return allIssues.FirstOrDefault(i =>
            i.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase));
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

        return await _github.CreateIssueAsync(
            title, body,
            [Labels.ResourceRequest, Labels.ExecutiveRequest],
            ct);
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

        return await _github.CreateIssueAsync(
            issueTitle, issueBody,
            [Labels.ExecutiveRequest],
            ct);
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

        return await _github.CreateIssueAsync(
            issueTitle, issueBody,
            [Labels.Blocker, Labels.AgentStuck],
            ct);
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

        return await _github.CreateIssueAsync(
            title, body,
            [Labels.AgentQuestion],
            ct);
    }

    /// <summary>
    /// Check if an issue has been resolved (closed or has "resolved" label).
    /// </summary>
    public async Task<bool> IsIssueResolvedAsync(int issueNumber, CancellationToken ct = default)
    {
        var issue = await _github.GetIssueAsync(issueNumber, ct);
        if (issue is null)
            return false;

        if (string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase))
            return true;

        return issue.Labels.Contains(Labels.Resolved, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the latest comments on an issue (for checking Executive responses).
    /// </summary>
    public async Task<IReadOnlyList<IssueComment>> GetIssueResponsesAsync(
        int issueNumber,
        CancellationToken ct = default)
    {
        // Fetch the issue which includes embedded comments
        var issue = await _github.GetIssueAsync(issueNumber, ct);
        return issue?.Comments.AsReadOnly() ?? (IReadOnlyList<IssueComment>)[];
    }

    /// <summary>
    /// Get all open issues targeted at a specific agent (agent name appears in the title).
    /// </summary>
    public async Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(
        string agentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var allIssues = await _github.GetOpenIssuesAsync(ct);
        return allIssues
            .Where(issue =>
                string.Equals(ParseAgentNameFromTitle(issue.Title), agentName, StringComparison.OrdinalIgnoreCase))
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

        await _github.AddIssueCommentAsync(
            issueNumber,
            $"✅ **Resolved**\n\n{resolutionComment}",
            ct);

        await _github.CloseIssueAsync(issueNumber, ct);
    }
}
