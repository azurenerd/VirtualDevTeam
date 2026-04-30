using AgentSquad.Core.GitHub.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Configuration;
using Octokit;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Handles merge conflicts and PR coordination between agents.
/// Checks mergeable state via Octokit and escalates persistent conflicts.
/// </summary>
public class ConflictResolver
{
    private readonly IGitHubService _github;
    private GitHubClient _client;
    private string _owner;
    private string _repo;
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(
        IGitHubService github,
        IOptions<AgentSquadConfig> config,
        ILogger<ConflictResolver> logger)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var projectConfig = config.Value.Project;
        var repoParts = projectConfig.GitHubRepo.Split('/', 2);
        if (repoParts.Length != 2)
            throw new ArgumentException($"GitHubRepo must be in 'owner/repo' format. Got: '{projectConfig.GitHubRepo}'");

        _owner = repoParts[0];
        _repo = repoParts[1];

        _client = new GitHubClient(new ProductHeaderValue("AgentSquad-ConflictResolver"))
        {
            Credentials = new Credentials(projectConfig.GitHubToken)
        };
    }

    /// <summary>
    /// Reconfigure to target a different repository. Call when user changes target repo between runs.
    /// </summary>
    public void Reconfigure(string owner, string repo, string token)
    {
        _owner = owner;
        _repo = repo;
        _client = new GitHubClient(new ProductHeaderValue("AgentSquad-ConflictResolver"))
        {
            Credentials = new Credentials(token)
        };
        _logger.LogInformation("ConflictResolver reconfigured for {Owner}/{Repo}", owner, repo);
    }

    /// <summary>
    /// Check if a branch has conflicts with the target branch by inspecting
    /// the PR mergeable state via Octokit.
    /// </summary>
    public async Task<bool> HasConflictsAsync(string headBranch, string baseBranch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        try
        {
            _logger.LogInformation("Checking conflicts between {Head} and {Base}", headBranch, baseBranch);

            var prs = await _client.PullRequest.GetAllForRepository(_owner, _repo,
                new PullRequestRequest { State = ItemStateFilter.Open });

            var matchingPr = prs.FirstOrDefault(pr =>
                string.Equals(pr.Head.Ref, headBranch, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pr.Base.Ref, baseBranch, StringComparison.OrdinalIgnoreCase));

            if (matchingPr is null)
            {
                _logger.LogDebug("No open PR found for {Head} -> {Base}, cannot determine conflict state", headBranch, baseBranch);
                return false;
            }

            // GitHub needs a moment to compute mergeable state; re-fetch the individual PR
            var pr = await _client.PullRequest.Get(_owner, _repo, matchingPr.Number);

            // Mergeable can be null if GitHub hasn't computed it yet — retry once
            if (pr.Mergeable is null)
            {
                _logger.LogDebug("Mergeable state not yet computed for PR #{Number}, retrying...", pr.Number);
                await Task.Delay(2000, ct);
                pr = await _client.PullRequest.Get(_owner, _repo, pr.Number);
            }

            var hasConflicts = pr.Mergeable == false;

            if (hasConflicts)
                _logger.LogWarning("PR #{Number} ({Head} -> {Base}) has merge conflicts", pr.Number, headBranch, baseBranch);
            else
                _logger.LogDebug("PR #{Number} ({Head} -> {Base}) is mergeable", pr.Number, headBranch, baseBranch);

            return hasConflicts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check conflicts for {Head} -> {Base}", headBranch, baseBranch);
            throw;
        }
    }

    /// <summary>
    /// Attempt to resolve conflicts by checking the PR's mergeable state and
    /// instructing the agent to rebase if needed.
    /// </summary>
    public async Task<ConflictResolution> AttemptResolutionAsync(int prNumber, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Attempting conflict resolution for PR #{Number}", prNumber);

            var pr = await _client.PullRequest.Get(_owner, _repo, prNumber);

            // If already mergeable, no action needed
            if (pr.Mergeable == true)
            {
                _logger.LogInformation("PR #{Number} is already mergeable — no conflicts", prNumber);
                return ConflictResolution.Resolved;
            }

            // Mergeable is null (unknown) or false (conflicts)
            if (pr.Mergeable is null)
            {
                await Task.Delay(2000, ct);
                pr = await _client.PullRequest.Get(_owner, _repo, prNumber);
            }

            if (pr.Mergeable == true)
            {
                return ConflictResolution.Resolved;
            }

            // PR has conflicts — post a comment instructing the agent to rebase
            var agentName = ExtractAgentName(pr.Title) ?? "Agent";
            var rebaseMessage = $"""
                ⚠️ **Merge Conflict Detected**

                @{agentName} — this PR has merge conflicts with `{pr.Base.Ref}`.

                **Action Required:** Please pull the latest changes from `{pr.Base.Ref}` and rebase your branch `{pr.Head.Ref}`:
                ```
                git fetch origin
                git rebase origin/{pr.Base.Ref}
                ```

                If you cannot resolve the conflicts automatically, please describe the conflicting files and this will be escalated.
                """;

            await _github.AddPullRequestCommentAsync(prNumber, rebaseMessage, ct);
            _logger.LogInformation("Posted rebase instruction on PR #{Number}", prNumber);

            return ConflictResolution.NeedsRebase;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attempt resolution for PR #{Number}", prNumber);
            throw;
        }
    }

    /// <summary>
    /// Escalate persistent conflicts to the Software Engineer by creating an issue.
    /// </summary>
    public async Task EscalateConflictAsync(
        int prNumber, string agentName, string conflictDetails, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictDetails);

        try
        {
            _logger.LogWarning("Escalating conflict for PR #{Number} from agent {Agent}", prNumber, agentName);

            var pr = await _github.GetPullRequestAsync(prNumber, ct);
            var prTitle = pr?.Title ?? $"PR #{prNumber}";
            var headBranch = pr?.HeadBranch ?? "unknown";
            var baseBranch = pr?.BaseBranch ?? "main";

            var issueTitle = $"🔀 Merge Conflict Escalation: PR #{prNumber} ({agentName})";
            var issueBody = $"""
                ## Merge Conflict Escalation

                **PR:** #{prNumber} — {prTitle}
                **Agent:** {agentName}
                **Branches:** `{headBranch}` → `{baseBranch}`

                ## Conflict Details
                {conflictDetails}

                ## Context
                The agent `{agentName}` was unable to resolve merge conflicts automatically after a rebase attempt.
                Manual intervention is required from the Software Engineer.

                ## Suggested Actions
                1. Review the conflicting files listed above
                2. Coordinate with `{agentName}` on which changes to keep
                3. Either resolve the conflict manually or reassign the task

                ---
                *This issue was automatically created by the ConflictResolver.*
                """;

            var labels = new[] { "merge-conflict", "escalation", "needs-attention" };
            await _github.CreateIssueAsync(issueTitle, issueBody, labels, ct);

            // Also comment on the PR that it's been escalated
            await _github.AddPullRequestCommentAsync(
                prNumber,
                $"🚨 **Conflict Escalated** — An issue has been created for Software Engineer review. " +
                $"Agent `{agentName}` was unable to resolve the merge conflicts automatically.",
                ct);

            _logger.LogInformation("Conflict escalated for PR #{Number} — issue created", prNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to escalate conflict for PR #{Number}", prNumber);
            throw;
        }
    }

    private static string? ExtractAgentName(string title)
    {
        var colonIndex = title.IndexOf(':');
        return colonIndex > 0 ? title[..colonIndex].Trim() : null;
    }
}

public enum ConflictResolution
{
    Resolved,
    NeedsRebase,
    NeedsManualResolution,
    Escalated
}
