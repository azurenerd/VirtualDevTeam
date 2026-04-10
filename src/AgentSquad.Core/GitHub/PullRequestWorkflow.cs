using System.Text.RegularExpressions;
using AgentSquad.Core.AI;
using AgentSquad.Core.GitHub.Models;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Result of an approve-and-merge attempt.
/// </summary>
public enum MergeAttemptResult
{
    Merged,
    AwaitingApprovals,
    ConflictBlocked
}

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

    private readonly ConflictDetector? _conflictDetector;

    public PullRequestWorkflow(
        IGitHubService github,
        ILogger<PullRequestWorkflow> logger,
        string defaultBranch = "main",
        ConflictDetector? conflictDetector = null)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultBranch = defaultBranch;
        _conflictDetector = conflictDetector;
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
    /// Returns an existing open PR if one with the same title already exists.
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

        // Idempotency: check if a PR with the same title already exists
        var existing = await FindExistingPullRequestAsync(prTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "PR '{Title}' already exists as #{Number}, skipping creation", prTitle, existing.Number);
            return existing;
        }

        // Commit a task tracking file so the branch differs from main (required for PR creation)
        var taskSlug = Slugify(taskTitle);
        var trackingPath = $".agentsquad/{taskSlug}.task";
        var trackingContent = $"agent: {agentName}\ntask: {taskTitle}\ncomplexity: {complexity}\nstatus: in-progress\n";
        _logger.LogInformation("Committing task marker to {Branch} for '{Title}'", branchName, taskTitle);
        await _github.CreateOrUpdateFileAsync(
            trackingPath, trackingContent, $"Start task: {taskTitle}", branchName, ct);

        var body = FormatPullRequestBody(agentName, complexity, branchName, taskDescription, architectureRef, specRef);
        var complexityLabel = GetComplexityLabel(complexity);

        var labels = new List<string> { Labels.InProgress };
        if (complexityLabel is not null)
            labels.Add(complexityLabel);

        _logger.LogInformation("Creating task PR '{Title}' on branch {Branch}", prTitle, branchName);

        AgentPullRequest pr;
        try
        {
            pr = await _github.CreatePullRequestAsync(
                prTitle, body, branchName, _defaultBranch, [.. labels], ct);
        }
        catch (Octokit.ApiValidationException)
        {
            _logger.LogWarning("Task PR creation returned Validation Failed — looking for existing PR");
            var fallback = await FindExistingPullRequestAsync(prTitle, ct);
            if (fallback is not null)
                return fallback;
            throw;
        }

        _logger.LogInformation("Created PR #{Number} for agent {Agent}", pr.Number, agentName);
        return pr;
    }

    /// <summary>
    /// Creates a PR for a branch that was already pushed via git (local workspace mode).
    /// Unlike <see cref="CreateTaskPullRequestAsync"/>, does NOT commit a task marker file
    /// because the branch already has real code commits from the local workspace.
    /// </summary>
    public async Task<AgentPullRequest> CreatePrForPushedBranchAsync(
        string branchName,
        string title,
        string body,
        IReadOnlyList<string>? labels = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // Idempotency: check if a PR with the same title already exists
        var existing = await FindExistingPullRequestAsync(title, ct);
        if (existing is not null)
        {
            _logger.LogInformation("PR '{Title}' already exists as #{Number}, returning existing",
                title, existing.Number);
            return existing;
        }

        var prLabels = labels?.ToList() ?? [Labels.InProgress];

        _logger.LogInformation("Creating PR for pushed branch '{Branch}': {Title}", branchName, title);

        AgentPullRequest pr;
        try
        {
            pr = await _github.CreatePullRequestAsync(
                title, body, branchName, _defaultBranch, [.. prLabels], ct);
        }
        catch (Octokit.ApiValidationException)
        {
            _logger.LogWarning("PR creation for pushed branch returned Validation Failed — looking for existing PR");
            var fallback = await FindExistingPullRequestAsync(title, ct);
            if (fallback is not null)
                return fallback;
            throw;
        }

        _logger.LogInformation("Created PR #{Number} for pushed branch '{Branch}'", pr.Number, branchName);
        return pr;
    }

    /// <summary>
    /// Find an existing open PR by title prefix match. Returns null if none found.
    /// </summary>
    public async Task<AgentPullRequest?> FindExistingPullRequestAsync(
        string titlePrefix,
        CancellationToken ct = default)
    {
        var openPrs = await _github.GetOpenPullRequestsAsync(ct);
        return openPrs.FirstOrDefault(pr =>
            pr.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Create a branch and an in-progress PR for a document before work begins.
    /// The PR is created with "in-progress" label so it's visible immediately.
    /// Returns the PR (existing or new).
    /// </summary>
    public async Task<AgentPullRequest> OpenDocumentPRAsync(
        string agentName,
        string documentPath,
        string prTitle,
        string prDescription,
        int? closesIssueNumber = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);

        var fullPrTitle = $"{agentName}: {prTitle}";

        // Idempotency: check if PR already exists
        var existing = await FindExistingPullRequestAsync(fullPrTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Document PR '{Title}' already exists as #{Number}", fullPrTitle, existing.Number);
            return existing;
        }

        // 1. Create feature branch
        var docSlug = Slugify(System.IO.Path.GetFileNameWithoutExtension(documentPath));
        var branchName = await CreateTaskBranchAsync(agentName, docSlug, ct);

        // 2. Clean up any stale document content from a prior run on this branch
        try
        {
            await _github.DeleteFileAsync(documentPath, "Clean stale document from prior run", branchName, ct);
            _logger.LogInformation("Removed stale {Path} from branch {Branch}", documentPath, branchName);
        }
        catch
        {
            // File doesn't exist on the branch — that's expected for fresh branches
        }

        // 3. Create a tracking marker so the branch has a diff from main (required for PR creation)
        // We do NOT commit a WIP placeholder into the actual document — only the final content goes there.
        _logger.LogInformation("Creating branch marker on {Branch} for {Path}", branchName, documentPath);
        await _github.CreateOrUpdateFileAsync(
            $".agentsquad/{docSlug}.tracking",
            $"agent: {agentName}\ndocument: {documentPath}\nstatus: in-progress\n",
            $"Start work on {documentPath}",
            branchName, ct);

        // 4. Build PR body with optional issue linking
        var issueRef = closesIssueNumber.HasValue
            ? $"\n\nCloses #{closesIssueNumber.Value}"
            : "";
        var prBody = $"""
            ## Document: {documentPath}
            **Author:** {agentName}
            **Status:** 🔄 In Progress

            {prDescription}{issueRef}
            """;

        // 5. Create PR with in-progress label (handle race condition with existing PR)
        _logger.LogInformation("Creating document PR '{Title}'", fullPrTitle);
        AgentPullRequest pr;
        try
        {
            pr = await _github.CreatePullRequestAsync(
                fullPrTitle, prBody, branchName, _defaultBranch,
                [Labels.InProgress], ct);
        }
        catch (Octokit.ApiValidationException)
        {
            // A PR already exists for this head→base (API caching race).
            // Fall back to finding the existing open PR.
            _logger.LogWarning("PR creation returned Validation Failed — looking for existing PR");
            var fallback = await FindExistingPullRequestAsync(fullPrTitle, ct);
            if (fallback is not null)
                return fallback;

            // If we still can't find it, re-throw
            throw;
        }

        _logger.LogInformation("Created document PR #{Number}: {Title}", pr.Number, fullPrTitle);
        return pr;
    }

    /// <summary>
    /// Commit the final document content to an existing PR's branch, then auto-merge.
    /// Call this after the agent finishes generating the document content.
    /// </summary>
    public async Task CommitAndMergeDocumentPRAsync(
        AgentPullRequest pr,
        string agentName,
        string documentPath,
        string documentContent,
        string commitMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentContent);

        // 1. Commit final content to the PR branch
        _logger.LogInformation("Committing final {Path} to branch {Branch}", documentPath, pr.HeadBranch);
        await _github.CreateOrUpdateFileAsync(documentPath, documentContent, commitMessage, pr.HeadBranch, ct);

        // 2. Clean up the tracking marker file so it doesn't merge into main
        var docSlug = Slugify(System.IO.Path.GetFileNameWithoutExtension(documentPath));
        var trackingPath = $".agentsquad/{docSlug}.tracking";
        try
        {
            await _github.DeleteFileAsync(trackingPath, "Remove tracking marker", pr.HeadBranch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete tracking file {Path} — may not exist", trackingPath);
        }

        // 3. Update PR labels and description to show completion
        await _github.UpdatePullRequestAsync(pr.Number,
            labels: [Labels.ReadyForReview, Labels.Approved], ct: ct);

        // 4. Auto-merge (no review needed for initial docs)
        // Brief delay to let GitHub process the commit before attempting merge
        await Task.Delay(3000, ct);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _github.MergePullRequestAsync(pr.Number,
                    $"Merge {documentPath} — auto-approved by {agentName}", ct);
                _logger.LogInformation("Auto-merged document PR #{Number}", pr.Number);
                try { await _github.DeleteBranchAsync(pr.HeadBranch, ct); } catch { /* best-effort */ }
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "Merge attempt {Attempt}/3 failed for PR #{Number}, retrying after delay",
                    attempt, pr.Number);
                await Task.Delay(5000 * attempt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "All merge attempts failed for document PR #{Number}", pr.Number);
            }
        }
    }

    /// <summary>
    /// Legacy convenience: Create a branch, commit a document file, create a PR, and auto-merge it all at once.
    /// Prefer OpenDocumentPRAsync + CommitAndMergeDocumentPRAsync for real-time visibility.
    /// </summary>
    public async Task<AgentPullRequest> CreateAndMergeDocumentPRAsync(
        string agentName,
        string documentPath,
        string documentContent,
        string commitMessage,
        string prTitle,
        string prDescription,
        int? closesIssueNumber = null,
        CancellationToken ct = default)
    {
        var pr = await OpenDocumentPRAsync(agentName, documentPath, prTitle, prDescription, closesIssueNumber, ct);
        await CommitAndMergeDocumentPRAsync(pr, agentName, documentPath, documentContent, commitMessage, ct);
        return pr;
    }

    // ── Code PR Review Workflow ────────────────────────────────────────

    /// <summary>
    /// Approval comment marker format: **[AgentName] APPROVED**\n\nreason
    /// </summary>
    private static readonly Regex ApprovalPattern = new(
        @"\*\*\[(.+?)\]\s*APPROVED\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Changes-requested comment marker: **[AgentName] CHANGES REQUESTED** — details
    /// </summary>
    private static readonly Regex ChangesRequestedPattern = new(
        @"\*\*\[(.+?)\]\s*CHANGES\s*REQUESTED\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The default agents required to approve code PRs before merge.
    /// When the PR author is one of the reviewers, the Architect substitutes in.
    /// </summary>
    public static readonly string[] DefaultReviewers = ["ProgramManager", "PrincipalEngineer"];
    public static readonly string FallbackReviewer = "Architect";

    /// <summary>
    /// Get the required reviewers for a PR, substituting the Architect when the
    /// author is one of the default reviewers (e.g., PE can't review its own PR).
    /// </summary>
    /// <summary>
    /// Determine which agents must approve a PR before it can be merged.
    /// Routing rules:
    ///   - TestEngineer PRs → only PrincipalEngineer (test quality, not business/arch review)
    ///   - Engineer PRs (Senior/Junior/PE) → ProgramManager + PrincipalEngineer
    ///   - When the author IS a default reviewer, Architect substitutes in
    /// </summary>
    public static string[] GetRequiredReviewers(string prAuthorRole)
    {
        // TestEngineer PRs need only PE approval — PM/Architect don't review test suites
        if (prAuthorRole.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
            || prAuthorRole.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
        {
            return ["PrincipalEngineer"];
        }

        if (DefaultReviewers.Any(r => string.Equals(r, prAuthorRole, StringComparison.OrdinalIgnoreCase)))
        {
            return DefaultReviewers
                .Where(r => !string.Equals(r, prAuthorRole, StringComparison.OrdinalIgnoreCase))
                .Append(FallbackReviewer)
                .ToArray();
        }
        return DefaultReviewers;
    }

    /// <summary>
    /// Check whether a specific agent has posted an approval comment on a PR.
    /// Only considers the most recent comment from that agent (if they requested changes
    /// after approving, the approval is revoked).
    /// </summary>
    public async Task<bool> HasAgentApprovedAsync(int prNumber, string agentName, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
        // Walk comments in reverse to find the most recent action by this agent
        foreach (var comment in comments.OrderByDescending(c => c.CreatedAt))
        {
            var approvalMatch = ApprovalPattern.Match(comment.Body);
            if (approvalMatch.Success &&
                string.Equals(approvalMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
                return true;

            var changesMatch = ChangesRequestedPattern.Match(comment.Body);
            if (changesMatch.Success &&
                string.Equals(changesMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return false;
    }

    /// <summary>
    /// Get all agents that have currently approved a PR (considering most-recent-comment logic).
    /// Checks all possible reviewers (default + fallback) for approvals.
    /// </summary>
    public async Task<List<string>> GetApprovedReviewersAsync(int prNumber, CancellationToken ct = default)
    {
        var allPossibleReviewers = DefaultReviewers.Append(FallbackReviewer).Distinct(StringComparer.OrdinalIgnoreCase);
        var approved = new List<string>();
        foreach (var reviewer in allPossibleReviewers)
        {
            if (await HasAgentApprovedAsync(prNumber, reviewer, ct))
                approved.Add(reviewer);
        }
        return approved;
    }

    /// <summary>
    /// Post an approval comment and merge if this is the last required reviewer.
    /// The required reviewer list is dynamic — when the PR author is a default reviewer,
    /// the Architect substitutes in. Returns true if the PR was merged.
    /// </summary>
    public async Task<MergeAttemptResult> ApproveAndMaybeMergeAsync(
        int prNumber, string approverAgent, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approverAgent);

        // Post the approval comment with the review rationale
        var comment = string.IsNullOrWhiteSpace(reason)
            ? $"**[{approverAgent}] APPROVED**"
            : $"**[{approverAgent}] APPROVED**\n\n{reason}";
        await _github.AddPullRequestCommentAsync(prNumber, comment, ct);
        _logger.LogInformation("Agent {Agent} approved PR #{Number}", approverAgent, prNumber);

        // Determine required reviewers based on PR author
        var pr = await _github.GetPullRequestAsync(prNumber, ct);
        var authorRole = DetectAuthorRole(pr?.Title ?? "");
        var requiredReviewers = GetRequiredReviewers(authorRole);

        // Check if all required reviewers have now approved
        var approvedReviewers = await GetApprovedReviewersAsync(prNumber, ct);
        _logger.LogInformation("PR #{Number} approvals: [{Approvers}] of [{Required}]",
            prNumber, string.Join(", ", approvedReviewers), string.Join(", ", requiredReviewers));

        if (requiredReviewers.All(r => approvedReviewers.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            // All reviewers approved — merge!
            _logger.LogInformation("All reviewers approved PR #{Number}, merging", prNumber);

            // Update labels
            if (pr is not null)
            {
                var updatedLabels = pr.Labels
                    .Where(l => !string.Equals(l, Labels.ReadyForReview, StringComparison.OrdinalIgnoreCase))
                    .Append(Labels.Approved)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await _github.UpdatePullRequestAsync(prNumber, labels: updatedLabels, ct: ct);
            }

            try
            {
                await _github.MergePullRequestAsync(prNumber,
                    $"Merged by {approverAgent} after dual approval from {string.Join(" and ", approvedReviewers)}", ct);
            }
            catch (Octokit.PullRequestNotMergeableException)
            {
                // Branch is behind main or has conflicts — try to update
                _logger.LogWarning("PR #{Number} not mergeable, attempting branch update", prNumber);
                var updated = await _github.UpdatePullRequestBranchAsync(prNumber, ct);
                if (!updated)
                {
                    // Standard merge-update failed — try force-rebase onto main
                    _logger.LogWarning("PR #{Number} branch update failed — attempting force-rebase onto main", prNumber);
                    updated = await _github.RebaseBranchOnMainAsync(prNumber, ct);
                }

                if (updated)
                {
                    // Wait briefly for GitHub to process the merge commit
                    await Task.Delay(5000, ct);
                    try
                    {
                        await _github.MergePullRequestAsync(prNumber,
                            $"Merged by {approverAgent} after branch sync and dual approval from {string.Join(" and ", approvedReviewers)}", ct);
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogWarning(retryEx, "PR #{Number} still not mergeable after branch update — may have real conflicts", prNumber);
                        await _github.AddPullRequestCommentAsync(prNumber,
                            $"⚠️ **Merge blocked** — PR has conflicts with `main` that could not be auto-resolved. " +
                            $"Branch update was attempted but merge still failed.", ct);
                        return MergeAttemptResult.ConflictBlocked;
                    }
                }
                else
                {
                    _logger.LogWarning("PR #{Number} branch update and rebase both failed — merge conflicts require manual resolution", prNumber);
                    await _github.AddPullRequestCommentAsync(prNumber,
                        $"⚠️ **Merge blocked** — PR has conflicts with `main` that require resolution. " +
                        $"The engineer should rebase and resolve conflicts.", ct);
                    return MergeAttemptResult.ConflictBlocked;
                }
            }

            // Clean up the head branch after squash merge
            if (pr is not null && !string.IsNullOrEmpty(pr.HeadBranch))
                await _github.DeleteBranchAsync(pr.HeadBranch, ct);

            return MergeAttemptResult.Merged;
        }

        _logger.LogInformation("PR #{Number} still needs approval from: {Missing}",
            prNumber,
            string.Join(", ", requiredReviewers.Except(approvedReviewers, StringComparer.OrdinalIgnoreCase)));
        return MergeAttemptResult.AwaitingApprovals;
    }

    /// <summary>
    /// Get the latest unaddressed CHANGES_REQUESTED feedback on a PR.
    /// Walks all comments, tracks each reviewer's latest action, and returns the first
    /// reviewer whose most recent comment is CHANGES_REQUESTED (not superseded by APPROVED).
    /// Returns null if all reviewers' latest actions are APPROVED or no reviews exist.
    /// </summary>
    public async Task<(string Reviewer, string Feedback)?> GetPendingChangesRequestedAsync(
        int prNumber, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
        var latestByAgent = new Dictionary<string, (bool IsApproval, string Body)>(StringComparer.OrdinalIgnoreCase);

        // Walk forward so later comments overwrite earlier ones per agent
        foreach (var comment in comments.OrderBy(c => c.CreatedAt))
        {
            var approvalMatch = ApprovalPattern.Match(comment.Body);
            if (approvalMatch.Success)
            {
                latestByAgent[approvalMatch.Groups[1].Value.Trim()] = (true, comment.Body);
                continue;
            }
            var changesMatch = ChangesRequestedPattern.Match(comment.Body);
            if (changesMatch.Success)
            {
                latestByAgent[changesMatch.Groups[1].Value.Trim()] = (false, comment.Body);
            }
        }

        foreach (var (agent, (isApproval, body)) in latestByAgent)
        {
            if (!isApproval)
            {
                var dashIdx = body.IndexOf('—');
                var feedback = dashIdx >= 0 ? body[(dashIdx + 1)..].Trim() : body;
                return (agent, feedback);
            }
        }
        return null;
    }

    /// <summary>
    /// Detect the author's agent role from the PR title (format: "AgentRole: Task title").
    /// </summary>
    public static string DetectAuthorRole(string prTitle)
    {
        var colonIdx = prTitle.IndexOf(':');
        if (colonIdx > 0)
            return prTitle[..colonIdx].Trim();
        return "";
    }

    private static readonly Regex ClosesIssuePattern = new(
        @"[Cc]loses?\s+#(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Parse linked issue number from PR body text (e.g., "Closes #108").
    /// </summary>
    public static int? ParseLinkedIssueNumber(string? prBody)
    {
        if (string.IsNullOrWhiteSpace(prBody))
            return null;
        var match = ClosesIssuePattern.Match(prBody);
        return match.Success && int.TryParse(match.Groups[1].Value, out var num) ? num : null;
    }

    /// <summary>
    /// Reads the actual code files from a PR's changed file list and returns them as a
    /// formatted context string suitable for inclusion in an AI review prompt.
    /// Skips non-code files (markdown, images, config) and truncates large files.
    /// </summary>
    public async Task<string> GetPRCodeContextAsync(
        int prNumber, string headBranch, int maxFileSizeChars = 15000, CancellationToken ct = default)
    {
        var changedFiles = await _github.GetPullRequestChangedFilesAsync(prNumber, ct);
        if (changedFiles.Count == 0)
            return "";

        // Skip only known binary extensions — include everything else
        var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
            ".woff", ".woff2", ".ttf", ".eot", ".otf",
            ".zip", ".tar", ".gz", ".7z", ".rar",
            ".dll", ".exe", ".bin", ".obj", ".pdb",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx"
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Files Changed in This PR\n");

        foreach (var filePath in changedFiles)
        {
            if (binaryExtensions.Contains(Path.GetExtension(filePath)))
                continue;

            try
            {
                var content = await _github.GetFileContentAsync(filePath, headBranch, ct);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var ext = Path.GetExtension(filePath).TrimStart('.');
                string truncated;
                if (content.Length > maxFileSizeChars)
                {
                    // Truncate at last newline before limit to avoid cutting mid-line
                    var cutPoint = content.LastIndexOf('\n', maxFileSizeChars);
                    if (cutPoint <= 0) cutPoint = maxFileSizeChars;
                    truncated = content[..cutPoint];
                }
                else
                {
                    truncated = content;
                }

                sb.AppendLine($"### {filePath}");
                sb.AppendLine($"```{ext}");
                sb.AppendLine(truncated);
                sb.AppendLine("```\n");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path} from branch {Branch} for review context",
                    filePath, headBranch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Post a changes-requested comment on a PR.
    /// </summary>
    public async Task RequestChangesAsync(
        int prNumber, string reviewerAgent, string details, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(details);

        var comment = $"**[{reviewerAgent}] CHANGES REQUESTED**\n\n{details}";
        await _github.AddPullRequestCommentAsync(prNumber, comment, ct);
        _logger.LogInformation("Agent {Agent} requested changes on PR #{Number}", reviewerAgent, prNumber);
    }

    /// <summary>
    /// Commit a fix directly to a PR's branch. Used by PM/PE when they want to
    /// fix issues they found during review rather than sending back to the author.
    /// </summary>
    public async Task CommitFixesToPRAsync(
        int prNumber,
        string filePath,
        string content,
        string commitMessage,
        CancellationToken ct = default)
    {
        var pr = await _github.GetPullRequestAsync(prNumber, ct);
        if (pr is null)
            throw new InvalidOperationException($"PR #{prNumber} not found");

        _logger.LogInformation("Committing fix to PR #{Number} branch {Branch}: {Path}",
            prNumber, pr.HeadBranch, filePath);

        await _github.CreateOrUpdateFileAsync(filePath, content, commitMessage, pr.HeadBranch, ct);
    }

    /// <summary>
    /// Commit multiple source code files to a PR's branch in sequence.
    /// Used by engineering agents to commit parsed code files from AI output.
    /// </summary>
    // BUG FIX: Previously used CreateOrUpdateFileAsync per file, which created one commit per file,
    // flooding PR history (e.g., 18 commits for Step 1/5 instead of 1). Now uses BatchCommitFilesAsync
    // to commit all files for a step in a single atomic commit.
    public async Task CommitCodeFilesToPRAsync(
        int prNumber,
        IReadOnlyList<AI.CodeFileParser.CodeFile> files,
        string commitMessage,
        CancellationToken ct = default)
    {
        var pr = await _github.GetPullRequestAsync(prNumber, ct);
        if (pr is null)
            throw new InvalidOperationException($"PR #{prNumber} not found");

        _logger.LogInformation(
            "Committing {Count} code files to PR #{Number} branch {Branch}",
            files.Count, prNumber, pr.HeadBranch);

        // Run conflict detection before committing (Tier 3: pre-commit warnings)
        if (_conflictDetector is not null)
        {
            try
            {
                var fileTuplesForCheck = files.Select(f => (f.Path, f.Content)).ToList();
                var conflicts = await _conflictDetector.DetectConflictsAsync(fileTuplesForCheck.AsReadOnly(), ct);
                if (conflicts.Count > 0)
                {
                    var warningComment = "## ⚠️ Conflict Detection Warnings\n\n" +
                        string.Join("\n\n", conflicts) +
                        "\n\n_These warnings were generated automatically. Please review for potential duplicate code._";
                    await _github.AddPullRequestCommentAsync(prNumber, warningComment, ct);
                    _logger.LogWarning("Detected {Count} potential conflicts for PR #{Number}", conflicts.Count, prNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Conflict detection failed for PR #{Number}, proceeding with commit", prNumber);
            }
        }

        // Convert to the tuple format expected by BatchCommitFilesAsync
        var fileTuples = files
            .Select(f => (f.Path, f.Content))
            .ToList()
            .AsReadOnly();

        try
        {
            await _github.BatchCommitFilesAsync(fileTuples, commitMessage, pr.HeadBranch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Batch commit failed for PR #{Number}, falling back to per-file commits",
                prNumber);

            // Fallback: commit files individually (original behavior)
            foreach (var file in files)
            {
                try
                {
                    await _github.CreateOrUpdateFileAsync(
                        file.Path, file.Content,
                        $"{commitMessage}: {file.Path}",
                        pr.HeadBranch, ct);
                }
                catch (Exception fileEx)
                {
                    _logger.LogWarning(fileEx,
                        "Failed to commit file {Path} to PR #{Number}, continuing with remaining files",
                        file.Path, prNumber);
                }
            }
        }
    }

    /// <summary>
    /// Get code PRs that are ready for review (have "ready-for-review" label, 
    /// not yet fully approved by both PM and PE).
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetCodePRsPendingReviewAsync(
        CancellationToken ct = default)
    {
        var allPrs = await _github.GetOpenPullRequestsAsync(ct);
        var pending = new List<AgentPullRequest>();

        foreach (var pr in allPrs)
        {
            if (!pr.Labels.Contains(Labels.ReadyForReview, StringComparer.OrdinalIgnoreCase))
                continue;

            // Skip PRs still marked in-progress (not yet ready)
            if (pr.Labels.Contains(Labels.InProgress, StringComparer.OrdinalIgnoreCase))
                continue;

            // Skip doc PRs (they have both ready-for-review AND approved)
            if (pr.Labels.Contains(Labels.Approved, StringComparer.OrdinalIgnoreCase))
                continue;

            pending.Add(pr);
        }

        return pending.AsReadOnly();
    }

    /// <summary>
    /// Check if a specific agent still needs to review a PR.
    /// Returns true if: (a) the agent has never reviewed, OR (b) a new "ready for review"
    /// marker was posted AFTER the agent's last review (indicating rework was done).
    /// </summary>
    public async Task<bool> NeedsReviewFromAsync(int prNumber, string agentName, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
        var ordered = comments.OrderByDescending(c => c.CreatedAt).ToList();

        // Find the agent's most recent review comment and whether it was an approval
        DateTime? lastReviewTime = null;
        bool lastActionWasApproval = false;
        foreach (var comment in ordered)
        {
            var approvalMatch = ApprovalPattern.Match(comment.Body);
            if (approvalMatch.Success &&
                string.Equals(approvalMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
            {
                lastReviewTime = comment.CreatedAt;
                lastActionWasApproval = true;
                break;
            }

            var changesMatch = ChangesRequestedPattern.Match(comment.Body);
            if (changesMatch.Success &&
                string.Equals(changesMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
            {
                lastReviewTime = comment.CreatedAt;
                lastActionWasApproval = false;
                break;
            }
        }

        // Never reviewed → needs review
        if (lastReviewTime is null)
            return true;

        // Check if rework happened after this agent's last review
        bool reworkHappenedSince = false;
        foreach (var comment in ordered)
        {
            if (comment.CreatedAt <= lastReviewTime)
                break;

            if (comment.Body.Contains("has marked this PR as ready for review", StringComparison.OrdinalIgnoreCase))
            {
                reworkHappenedSince = true;
                break;
            }
        }

        // No rework since last review → no re-review needed regardless of verdict
        if (!reworkHappenedSince)
            return false;

        // Rework happened. If this agent requested changes, they need to re-review.
        if (!lastActionWasApproval)
            return true;

        // This agent APPROVED but rework happened (triggered by a different reviewer).
        // Only re-review if this agent is the SOLE required reviewer for this PR.
        // Otherwise, let the reviewer who requested changes handle it.
        var pr = await _github.GetPullRequestAsync(prNumber, ct);
        if (pr is not null)
        {
            var authorRole = DetectAuthorRole(pr.Title);
            var requiredReviewers = GetRequiredReviewers(authorRole);
            if (requiredReviewers.Length == 1 &&
                string.Equals(requiredReviewers[0], agentName, StringComparison.OrdinalIgnoreCase))
            {
                // Sole reviewer — must re-review after rework
                return true;
            }
        }

        // Multi-reviewer setup and this agent already approved — skip
        return false;
    }

    /// <summary>
    /// Check whether any new commits were pushed to a PR since a specific reviewer's last review.
    /// Returns false if the reviewer requested changes but no new commits appeared — meaning
    /// the author claimed rework but didn't actually push code changes.
    /// </summary>
    public async Task<bool> HasNewCommitsSinceReviewAsync(int prNumber, string reviewerName, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
        var ordered = comments.OrderByDescending(c => c.CreatedAt).ToList();

        // Find this reviewer's last "CHANGES REQUESTED" comment
        DateTime? lastReviewTime = null;
        foreach (var comment in ordered)
        {
            var changesMatch = ChangesRequestedPattern.Match(comment.Body);
            if (changesMatch.Success &&
                string.Equals(changesMatch.Groups[1].Value.Trim(), reviewerName, StringComparison.OrdinalIgnoreCase))
            {
                lastReviewTime = comment.CreatedAt;
                break;
            }
        }

        // Never requested changes → treat as new (first review)
        if (lastReviewTime is null)
            return true;

        // Get PR commits and check if any are newer than the last review
        var commits = await _github.GetPullRequestCommitsWithDatesAsync(prNumber, ct);
        return commits.Any(c => c.CommittedAt > lastReviewTime.Value);
    }

    /// <summary>
    /// Check whether a specific agent has posted ANY review comment (approved or changes-requested).
    /// Returns true if the agent has reviewed, false if they have never commented.
    /// </summary>
    public async Task<bool> HasAgentReviewedAsync(int prNumber, string agentName, CancellationToken ct = default)
    {
        var comments = await _github.GetPullRequestCommentsAsync(prNumber, ct);
        foreach (var comment in comments.OrderByDescending(c => c.CreatedAt))
        {
            var approvalMatch = ApprovalPattern.Match(comment.Body);
            if (approvalMatch.Success &&
                string.Equals(approvalMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
                return true;

            var changesMatch = ChangesRequestedPattern.Match(comment.Body);
            if (changesMatch.Success &&
                string.Equals(changesMatch.Groups[1].Value.Trim(), agentName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── End Code PR Review Workflow ─────────────────────────────────────

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

    /// <summary>
    /// Strips preamble/thinking from AI review responses. The Copilot CLI sometimes returns
    /// the model's reasoning ("Let me examine...", "Let me check...", "Based on my analysis...")
    /// before the actual review content. This extracts only the numbered feedback list or
    /// approval sentence.
    /// </summary>
    public static string StripReviewPreamble(string reviewBody)
    {
        if (string.IsNullOrWhiteSpace(reviewBody))
            return reviewBody;

        var lines = reviewBody.Split('\n');

        // Find the first line that starts a numbered list item (e.g., "1.", "1)")
        // or a horizontal rule (---, ___), which separates thinking from content.
        int contentStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            // Horizontal rule — content starts on the next non-empty line
            if (trimmed.Length >= 3 && (trimmed.All(c => c == '-') || trimmed.All(c => c == '_') || trimmed.All(c => c == '*')))
            {
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[j]))
                    {
                        contentStart = j;
                        break;
                    }
                }
                if (contentStart >= 0) break;
            }

            // First numbered list item
            if (NumberedItemPattern().IsMatch(trimmed))
            {
                contentStart = i;
                break;
            }
        }

        if (contentStart > 0)
            return string.Join('\n', lines[contentStart..]).Trim();

        return reviewBody;
    }

    /// <summary>
    /// Detects when an AI response is meta-commentary about itself rather than actual review content.
    /// This happens when the Copilot CLI's underlying model "breaks character" and responds
    /// as a generic AI assistant instead of performing the requested task.
    /// </summary>
    public static bool IsGarbageAIResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        // Patterns that indicate the model is talking about itself rather than reviewing code
        string[] garbagePatterns =
        [
            "I'm powered by",
            "I'm an interactive AI",
            "my actual design",
            "my operating model",
            "my core instructions",
            "my guidelines about",
            "What would you actually like me to do",
            "What's your actual goal",
            "What I actually do",
            "I need you to be explicit",
            "conflicts with my",
            "violated my guidelines",
            "isn't a sustainable pattern",
            "I can help with",
            "I'm designed to",
            "I'm happy to help",
            "view files, edit code, run builds",
            "Use tools (",
            "Explain my work transparently",
            "Acknowledge limitations clearly",
            "Follow my core instructions",
            "conflicting instruction",
            "conflicting \"directive\"",
            "what you're testing an integration",
            "If you need a **code review**",
            "If you need **structured review output**",
        ];

        var lower = response.ToLowerInvariant();
        int hitCount = 0;
        foreach (var pattern in garbagePatterns)
        {
            if (lower.Contains(pattern.ToLowerInvariant()))
                hitCount++;
        }

        // Two or more garbage patterns = definitely not a real review
        return hitCount >= 2;
    }

    /// <summary>
    /// Extracts a numbered changes summary from an AI rework response.
    /// Only extracts content following an explicit "CHANGES SUMMARY" header to avoid
    /// picking up AI reasoning steps that happen to be numbered.
    /// Returns null if no explicit summary header found.
    /// </summary>
    public static string? ExtractChangesSummary(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return null;

        var lines = aiResponse.Split('\n');

        // Find the CHANGES SUMMARY header and the first FILE: block
        int summaryHeaderIdx = -1;
        int firstFileIdx = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("CHANGES SUMMARY", StringComparison.OrdinalIgnoreCase))
                summaryHeaderIdx = i;

            if (firstFileIdx < 0 && trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
                firstFileIdx = i;
        }

        // Only extract when the AI included the explicit header we asked for
        if (summaryHeaderIdx >= 0)
        {
            int end = firstFileIdx > summaryHeaderIdx ? firstFileIdx : lines.Length;
            var summaryLines = lines[(summaryHeaderIdx + 1)..end]
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (summaryLines.Length > 0)
                return string.Join('\n', summaryLines).Trim();
        }

        return null;
    }

    [GeneratedRegex(@"^\d+[\.\)]\s")]
    private static partial Regex NumberedItemPattern();

    [GeneratedRegex(@"^(.+?):\s*(.+)$")]
    private static partial Regex AgentTitlePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SlugifyWhitespacePattern();

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SlugifyInvalidCharsPattern();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex SlugifyMultipleDashPattern();
}
