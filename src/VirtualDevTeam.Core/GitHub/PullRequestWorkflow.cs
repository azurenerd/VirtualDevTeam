using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Result of an approve-and-merge attempt.
/// </summary>
public enum MergeAttemptResult
{
    Merged,
    AwaitingApprovals,
    ConflictBlocked,
    /// <summary>Code approved but waiting for Test Engineer to add tests (inline test workflow).</summary>
    AwaitingTests,
    /// <summary>All reviewers approved and PR is ready to merge, but merge was deferred (e.g. for a human gate).</summary>
    ReadyToMerge,
    /// <summary>PR is null, already closed, or already merged — no action needed.</summary>
    NotOpen,
    /// <summary>
    /// Merge hard-blocked because the Security Auditor found critical/high findings that have not
    /// been resolved. The <c>security-blocked</c> label must be removed by the SecurityAuditor
    /// (after a clean re-review) before the PR can merge. Do NOT call TryCloseAndRecreatePRAsync
    /// for this result — security findings need the PR open for human inspection.
    /// </summary>
    SecurityBlocked
}

/// <summary>
/// Manages the PR-based task assignment pattern where PRs are titled "[AgentName]: Task Title".
/// </summary>
public partial class PullRequestWorkflow
{
    private readonly IPullRequestService _prService;
    private readonly IRepositoryContentService _repoContent;
    private readonly IReviewService _reviewService;
    private readonly IBranchService _branchService;
    private readonly ILogger<PullRequestWorkflow> _logger;
    private readonly IRunBranchProvider? _branchProvider;
    private readonly IPlatformHostContext? _hostContext;
    private readonly VirtualDevTeam.Core.DevPlatform.PrReviewContextCache? _reviewContextCache;
    private readonly string _defaultBranch;

    /// <summary>
    /// The branch that PRs target and agent branches are created from.
    /// Uses the run's effective branch (working branch if set, else default).
    /// </summary>
    private string ActiveBranch => _branchProvider?.EffectiveBranch ?? _defaultBranch;

    public static class Labels
    {
        public const string ReadyForReview = "ready-for-review";
        public const string Approved = "approved";
        public const string InProgress = "in-progress";
        public const string TestsAdded = "tests-added";
        /// <summary>Architect approved (Phase 1 gate) — triggers TE testing.</summary>
        public const string ArchitectApproved = "architect-approved";
        /// <summary>PM approved (Phase 3 final gate) — triggers merge.</summary>
        public const string PmApproved = "pm-approved";
        public const string HighComplexity = "complexity-high";
        public const string MediumComplexity = "complexity-medium";
        public const string LowComplexity = "complexity-low";
        /// <summary>Review risk gating — requires human approval before agent continues.</summary>
        public const string HumanReviewRequired = "human-review-required";
        /// <summary>Durable marker for the T-FINAL integration PR. Applied at creation time
        /// so recovery can identify integration PRs by label instead of brittle title matching.</summary>
        public const string FinalIntegration = "final-integration";
        /// <summary>Applied to PRs that contain only test code (created by Test Engineer).</summary>
        public const string Tests = "tests";
        /// <summary>Applied to source PRs after the Test Engineer has processed them.</summary>
        public const string Tested = "tested";
        /// <summary>
        /// Applied by the SecurityAuditor when a PR has critical/high severity findings.
        /// Merge is hard-blocked until this label is removed (requires a clean re-review).
        /// </summary>
        public const string SecurityBlocked = "security-blocked";
        /// <summary>
        /// Applied by the SecurityAuditor when a PR has only medium/low advisory findings.
        /// PR may merge but findings should be tracked and addressed.
        /// </summary>
        public const string SecurityAdvisory = "security-advisory-open";

        /// <summary>
        /// Labels signalling that a PR has progressed past the Software Engineer's
        /// implementation phase. When any of these are present the SE must not
        /// re-enter "continue implementation" logic — further changes happen only
        /// via explicit ChangesRequested events.
        /// </summary>
        public static readonly string[] PastImplementationLabels = new[]
        {
            ReadyForReview,
            ArchitectApproved,
            PmApproved,
            Approved,
            TestsAdded
        };

        /// <summary>
        /// Returns true if any PR label indicates the PR has progressed past the SE's
        /// implementation phase (ready-for-review or any downstream approval/test label).
        /// </summary>
        public static bool IsPastImplementation(IEnumerable<string>? labels)
        {
            if (labels is null) return false;
            return labels.Any(l => PastImplementationLabels.Contains(l, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns true if the PR is the T-FINAL integration PR, checking the
        /// <see cref="FinalIntegration"/> label first, then falling back to
        /// title/branch heuristics for backward compatibility with older PRs.
        /// </summary>
        public static bool IsFinalIntegrationPr(IEnumerable<string>? labels, string? title, string? headBranch)
        {
            if (labels?.Any(l => string.Equals(l, FinalIntegration, StringComparison.OrdinalIgnoreCase)) == true)
                return true;

            // Legacy fallback for PRs created before label was introduced
            if (title?.Contains("Final Integration", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (headBranch?.Contains("final-integration", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return false;
        }
    }

    private readonly ConflictDetector? _conflictDetector;
    private readonly IDevPlatformAuthProvider? _authProvider;
    private readonly string? _workspaceRootPath;

    /// <summary>
    /// Fires after a successful PR merge. Subscribers should be fast and non-blocking.
    /// Used by the checkpoint service for fire-and-forget state snapshots.
    /// </summary>
    public event Action<int, string?>? OnPRMerged;

    public PullRequestWorkflow(
        IPullRequestService prService,
        IRepositoryContentService repoContent,
        IReviewService reviewService,
        IBranchService branchService,
        ILogger<PullRequestWorkflow> logger,
        IRunBranchProvider? branchProvider = null,
        string defaultBranch = "main",
        ConflictDetector? conflictDetector = null,
        IPlatformHostContext? hostContext = null,
        IDevPlatformAuthProvider? authProvider = null,
        string? workspaceRootPath = null,
        VirtualDevTeam.Core.DevPlatform.PrReviewContextCache? reviewContextCache = null)
    {
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _repoContent = repoContent ?? throw new ArgumentNullException(nameof(repoContent));
        _reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        _branchService = branchService ?? throw new ArgumentNullException(nameof(branchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _branchProvider = branchProvider;
        _defaultBranch = defaultBranch;
        _conflictDetector = conflictDetector;
        _hostContext = hostContext;
        _workspaceRootPath = workspaceRootPath;
        _authProvider = authProvider;
        _reviewContextCache = reviewContextCache;
    }

    /// <summary>
    /// Removes all legacy .virtualdevteam/ files AND stale tracking markers from the default branch.
    /// Call on startup to prevent stale task locks from confusing a fresh run.
    /// </summary>
    public async Task CleanupStaleTaskFilesAsync(CancellationToken ct = default)
    {
        try
        {
            var allFiles = await _repoContent.GetRepositoryTreeAsync(ActiveBranch, ct);
            var staleFiles = allFiles
                .Where(f => f.StartsWith(".virtualdevteam/", StringComparison.OrdinalIgnoreCase)
                          || f.EndsWith(".task.md", StringComparison.OrdinalIgnoreCase)
                          || f.EndsWith(".tracking.md", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (staleFiles.Count == 0)
            {
                _logger.LogDebug("No stale .virtualdevteam task files found");
                return;
            }

            _logger.LogInformation("Cleaning up {Count} stale .virtualdevteam task files from {Branch}",
                staleFiles.Count, ActiveBranch);

            foreach (var file in staleFiles)
            {
                try
                {
                    await _repoContent.DeleteFileAsync(file, $"Cleanup stale task lock: {file}", ActiveBranch, ct);
                    _logger.LogDebug("Deleted stale task file: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete stale task file {File}", file);
                }
            }

            _logger.LogInformation("Cleaned up {Count} stale .virtualdevteam task files", staleFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan for stale .virtualdevteam task files");
        }
    }

    /// <summary>
    /// Auto-corrects file paths that may be missing project subdirectory prefixes.
    /// Delegates to <see cref="ConflictDetector.ResolvePathsAsync"/> when available.
    /// Returns the input unchanged if no conflict detector is configured.
    /// </summary>
    public async Task<IReadOnlyList<AI.CodeFileParser.CodeFile>> ResolveFilePathsAsync(
        IReadOnlyList<AI.CodeFileParser.CodeFile> files, CancellationToken ct = default)
    {
        if (_conflictDetector is null || files.Count == 0)
            return files;

        try
        {
            var tuples = files.Select(f => (f.Path, f.Content)).ToList();
            var resolved = await _conflictDetector.ResolvePathsAsync(tuples.AsReadOnly(), ct);

            // Map back to CodeFile records
            return resolved.Select(r => new AI.CodeFileParser.CodeFile(r.Path, r.Content)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Path resolution failed, using original paths");
            return files;
        }
    }

    /// <summary>
    /// Parse agent name from PR title: "Software Engineer 1: Implement auth" → "Software Engineer 1"
    /// </summary>
    public static string? ParseAgentNameFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var match = AgentTitlePattern().Match(title);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Parse task title from PR title: "Software Engineer 1: Implement auth" → "Implement auth"
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
        IReadOnlyList<string>? additionalLabels = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // Guard: strip agent name prefix if task title already starts with it (prevents "Agent: Agent: Task")
        if (taskTitle.StartsWith(agentName + ":", StringComparison.OrdinalIgnoreCase))
            taskTitle = taskTitle[(agentName.Length + 1)..].Trim();

        taskDescription = SanitizePrBody(taskDescription);

        var prTitle = $"{agentName}: {taskTitle}";

        // Idempotency: check if a PR with the same title already exists
        var existing = await FindExistingPullRequestAsync(prTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "PR '{Title}' already exists as #{Number}, skipping creation", prTitle, existing.Number);
            return existing;
        }

        // Cross-agent guard: check if ANY open PR already targets the same issue.
        // Different agents have different title prefixes, so the title check above misses
        // cases where e.g. SE and a Specialist both work on the same issue.
        var linkedIssue = ParseLinkedIssueNumber(taskDescription);
        if (linkedIssue.HasValue)
        {
            var openPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
            var prForSameIssue = openPrs.FirstOrDefault(pr =>
                ParseLinkedIssueNumber(pr.Body) == linkedIssue.Value);
            if (prForSameIssue is not null)
            {
                _logger.LogWarning(
                    "PR #{ExistingPR} (by {ExistingAgent}) already targets issue #{Issue} — " +
                    "skipping duplicate creation by {Agent}",
                    prForSameIssue.Number, ParseAgentNameFromTitle(prForSameIssue.Title),
                    linkedIssue.Value, agentName);
                return prForSameIssue;
            }
        }

        // Commit a task tracking marker so the branch differs from main (required for PR creation).
        // Uses AgentDocs/ path instead of .virtualdevteam/ to avoid polluting the target repo.
        var taskSlug = Slugify(taskTitle);
        var trackingPath = $"AgentDocs/.tracking/{taskSlug}.task.md";
        var trackingContent = $"# Task: {taskTitle}\n\n- Agent: {agentName}\n- Complexity: {complexity}\n- Status: in-progress\n";
        _logger.LogInformation("Committing task marker to {Branch} for '{Title}'", branchName, taskTitle);
        try
        {
            await _repoContent.CreateOrUpdateFileAsync(
                trackingPath, trackingContent, $"Start task: {taskTitle}", branchName, ct);
            _logger.LogInformation("Marker commit succeeded on {Branch}", branchName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Marker commit FAILED on {Branch} for path {Path}: {Error}",
                branchName, trackingPath, ex.Message);
            throw;
        }

        var body = FormatPullRequestBody(agentName, complexity, branchName, taskDescription, architectureRef, specRef);
        var complexityLabel = GetComplexityLabel(complexity);

        var labels = new List<string> { Labels.InProgress };
        if (complexityLabel is not null)
            labels.Add(complexityLabel);
        if (additionalLabels is not null)
            labels.AddRange(additionalLabels);

        _logger.LogInformation("Creating task PR '{Title}' on branch {Branch} (body length: {BodyLen})", prTitle, branchName, body.Length);

        AgentPullRequest pr;
        try
        {
            pr = (await _prService.CreateAsync(
                prTitle, body, branchName, ActiveBranch, [.. labels], ct)).ToAgentPR();
        }
        catch (PlatformConflictException ex) when (ex.Kind == PlatformConflictKind.AlreadyExists)
        {
            _logger.LogWarning("Task PR creation returned Validation Failed — looking for existing PR");
            var fallback = await FindExistingPullRequestAsync(prTitle, ct);
            if (fallback is not null)
                return fallback;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task PR creation FAILED for '{Title}' on branch {Branch}: {Error}",
                prTitle, branchName, ex.Message);
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

        body = SanitizePrBody(body);

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
            pr = (await _prService.CreateAsync(
                title, body, branchName, ActiveBranch, [.. prLabels], ct)).ToAgentPR();
        }
        catch (PlatformConflictException ex) when (ex.Kind == PlatformConflictKind.AlreadyExists)
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
        var openPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
        var runScope = _branchProvider?.RunScope;

        return openPrs.FirstOrDefault(pr =>
            pr.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase)
            && IsCurrentRunScopePr(pr.HeadBranch, pr.Body, runScope));
    }

    /// <summary>
    /// Get all open PRs assigned to a specific agent.
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetAgentTasksAsync(
        string agentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var allPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
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
        var allPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
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
        CancellationToken ct = default,
        string? extraMarkdown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Check if PR has already progressed past "ready for review" — any downstream label means
        // an agent already reviewed/approved/tested it. Re-posting "ready for review" at this point
        // is always a duplicate caused by polling loops re-visiting a PR they already completed.
        // Labels checked: architect-approved, pm-approved, approved, tests-added
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        if (pr is not null)
        {
            var progressLabels = new[]
            {
                Labels.ArchitectApproved,
                Labels.PmApproved,
                Labels.Approved,
                Labels.TestsAdded
            };
            var matchedLabel = progressLabels.FirstOrDefault(l =>
                pr.Labels.Contains(l, StringComparer.OrdinalIgnoreCase));
            if (matchedLabel is not null)
            {
                _logger.LogInformation(
                    "PR #{Number} already has downstream label '{Label}', skipping ready-for-review",
                    prNumber, matchedLabel);
                return;
            }
        }

        if (pr is not null && pr.Labels.Contains(Labels.ReadyForReview, StringComparer.OrdinalIgnoreCase))
        {
            // Label already exists. Only post a comment if there's been a changes-requested
            // review since the last "ready for review" comment (i.e., actual rework happened).
            var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
            var lastReadyComment = comments
                .Where(c => c.Body.Contains("has marked this PR as ready for review"))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();
            var lastChangesRequested = comments
                .Where(c => c.Body.Contains("requested changes", StringComparison.OrdinalIgnoreCase)
                          || c.Body.Contains("changes requested", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            // Only post rework-ready comment if changes were requested AFTER the last ready comment
            if (lastChangesRequested is not null &&
                (lastReadyComment is null || lastChangesRequested.CreatedAt > lastReadyComment.CreatedAt))
            {
                _logger.LogInformation("PR #{Number} has rework after changes-requested, posting rework-ready comment", prNumber);
                var reworkBody = $"✅ **{agentName}** has marked this PR as ready for review.\n\nRework complete — ready for re-review.";
                if (!string.IsNullOrWhiteSpace(extraMarkdown))
                    reworkBody += "\n\n" + extraMarkdown;
                await _reviewService.AddCommentAsync(prNumber, reworkBody, ct);
            }
            else
            {
                _logger.LogInformation("PR #{Number} already has ready-for-review label and no rework needed, skipping duplicate comment", prNumber);
            }
            return;
        }

        _logger.LogInformation("Agent {Agent} marking PR #{Number} ready for review", agentName, prNumber);

        var readyBody = $"✅ **{agentName}** has marked this PR as ready for review.\n\nAll implementation and tests are complete.";
        if (!string.IsNullOrWhiteSpace(extraMarkdown))
            readyBody += "\n\n" + extraMarkdown;

        await _reviewService.AddCommentAsync(prNumber, readyBody, ct);

        // Update labels: swap in-progress for ready-for-review
        // Re-fetch since we may have fetched earlier for duplicate check
        pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        if (pr is not null)
        {
            var updatedLabels = pr.Labels
                .Where(l => !string.Equals(l, Labels.InProgress, StringComparison.OrdinalIgnoreCase))
                .Append(Labels.ReadyForReview)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await _prService.UpdateAsync(prNumber, labels: updatedLabels, ct: ct);
        }
    }

    /// <summary>
    /// Submit a code review on a PR, optionally with inline comments.
    /// </summary>
    public async Task SubmitReviewAsync(
        int prNumber,
        string reviewerAgent,
        string body,
        bool approve,
        IReadOnlyList<InlineReviewComment>? inlineComments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var reviewBody = $"**Review by {reviewerAgent}**\n\n{body}";
        var eventType = approve ? "APPROVE" : "REQUEST_CHANGES";

        _logger.LogInformation("Agent {Agent} submitting {ReviewType} review on PR #{Number} ({InlineCount} inline comments)",
            reviewerAgent, eventType, prNumber, inlineComments?.Count ?? 0);

        if (inlineComments is { Count: > 0 })
        {
            await _reviewService.CreateReviewWithInlineCommentsAsync(
                prNumber, reviewBody, eventType,
                inlineComments.Select(c => new PlatformInlineComment { FilePath = c.FilePath, Line = c.Line, Body = c.Body }).ToList(),
                ct: ct);
        }
        else
        {
            await _reviewService.AddReviewAsync(prNumber, reviewBody, eventType, ct);
        }

        if (approve)
        {
            var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
            if (pr is not null)
            {
                var updatedLabels = pr.Labels
                    .Append(Labels.Approved)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await _prService.UpdateAsync(prNumber, labels: updatedLabels, ct: ct);
            }
        }
    }

    /// <summary>
    /// Get PRs pending review (has "ready-for-review" label but not "approved").
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetPendingReviewsAsync(
        CancellationToken ct = default)
    {
        var allPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
        return allPrs
            .Where(pr =>
                pr.Labels.Contains(Labels.ReadyForReview) &&
                !pr.Labels.Contains(Labels.Approved))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Create the feature branch for a task: agent/{runScope}/{agent-name-slug}/{task-slug}
    /// When no run scope is available (backward compat), falls back to: agent/{agent-name-slug}/{task-slug}
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

        // Cap segment lengths to avoid Windows path length issues (260 char limit).
        // refs/remotes/origin/ = 20 chars prefix, agent/ = 6, slashes = 3, runScope = 8
        // Budget: ~220 chars for the branch name leaves room for .git path overhead
        const int maxAgentSlug = 30;
        const int maxTaskSlug = 60;
        if (agentSlug.Length > maxAgentSlug)
            agentSlug = agentSlug[..maxAgentSlug].TrimEnd('-');
        if (normalizedTaskSlug.Length > maxTaskSlug)
            normalizedTaskSlug = normalizedTaskSlug[..maxTaskSlug].TrimEnd('-');

        var runScope = _branchProvider?.RunScope;
        var branchName = runScope is not null
            ? $"agent/{runScope}/{agentSlug}/{normalizedTaskSlug}"
            : $"agent/{agentSlug}/{normalizedTaskSlug}";

        _logger.LogInformation("Creating task branch {Branch} from {DefaultBranch}", branchName, ActiveBranch);

        if (await _branchService.ExistsAsync(branchName, ct))
        {
            _logger.LogWarning("Branch {Branch} already exists, reusing it", branchName);
            return branchName;
        }

        await _branchService.CreateAsync(branchName, ActiveBranch, ct);
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

        // Guard: strip agent name prefix if already present (prevents "Agent: Agent: Doc")
        if (prTitle.StartsWith(agentName + ":", StringComparison.OrdinalIgnoreCase))
            prTitle = prTitle[(agentName.Length + 1)..].Trim();

        var fullPrTitle = $"{agentName}: {prTitle}";

        // Idempotency: check if an open PR already exists
        var existing = await FindExistingPullRequestAsync(fullPrTitle, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Document PR '{Title}' already exists as #{Number}", fullPrTitle, existing.Number);
            return existing;
        }

        // NOTE: We intentionally do NOT check if the document exists on main here.
        // A prior run may have merged a stale version, and the current run needs to
        // regenerate with fresh content. Duplicate PRs on restart are preferable to
        // silently skipping document generation.

        // 1. Create feature branch
        var docSlug = Slugify(System.IO.Path.GetFileNameWithoutExtension(documentPath));
        var branchName = await CreateTaskBranchAsync(agentName, docSlug, ct);

        // 2. Clean up any stale document content from a prior run on this branch
        try
        {
            await _repoContent.DeleteFileAsync(documentPath, "Clean stale document from prior run", branchName, ct);
            _logger.LogInformation("Removed stale {Path} from branch {Branch}", documentPath, branchName);
        }
        catch
        {
            // File doesn't exist on the branch — that's expected for fresh branches
        }

        // 3. Create a tracking marker so the branch has a diff from main (required for PR creation)
        // Uses AgentDocs/ path instead of .virtualdevteam/ to avoid polluting the target repo.
        _logger.LogInformation("Creating branch marker on {Branch} for {Path}", branchName, documentPath);
        await _repoContent.CreateOrUpdateFileAsync(
            $"AgentDocs/.tracking/{docSlug}.tracking.md",
            $"# Document: {documentPath}\n\n- Agent: {agentName}\n- Status: in-progress\n",
            $"Start work on {documentPath}",
            branchName, ct);

        // 4. Build PR body with optional issue linking
        var issueRef = closesIssueNumber.HasValue
            ? $"\n\nCloses #{closesIssueNumber.Value}"
            : "";
        // Link to target branch (ActiveBranch), not the PR source branch which gets deleted after merge
        var docLink = _hostContext is not null
            ? $"[{documentPath}]({_hostContext.GetFileWebUrl(documentPath, ActiveBranch)})"
            : documentPath;
        var prBody = $"""
            ## Document: {docLink}
            **Author:** {agentName}
            **Status:** 🔄 In Progress

            {prDescription}{issueRef}
            """;

        // 5. Create PR with in-progress label (handle race condition with existing PR)
        _logger.LogInformation("Creating document PR '{Title}'", fullPrTitle);
        AgentPullRequest pr;
        try
        {
            pr = (await _prService.CreateAsync(
                fullPrTitle, prBody, branchName, ActiveBranch,
                [Labels.InProgress], ct)).ToAgentPR();
        }
        catch (PlatformConflictException ex) when (ex.Kind == PlatformConflictKind.AlreadyExists)
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
    /// Commit the document content to an existing PR's branch WITHOUT merging.
    /// Use this to make the document visible for human review before a gate.
    /// </summary>
    public async Task CommitDocumentToPRAsync(
        AgentPullRequest pr,
        string documentPath,
        string documentContent,
        string commitMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentContent);

        // Capture the pre-commit head SHA so we can verify the commit became visible
        var preCommitSha = pr.HeadSha;
        if (string.IsNullOrEmpty(preCommitSha))
        {
            var freshPr = await _prService.GetAsync(pr.Number, ct);
            preCommitSha = freshPr?.HeadSha ?? "";
        }

        _logger.LogInformation("Committing {Path} to branch {Branch} for review", documentPath, pr.HeadBranch);
        await _repoContent.CreateOrUpdateFileAsync(documentPath, documentContent, commitMessage, pr.HeadBranch, ct);

        // Wait for the commit to be visible on the PR before returning.
        // GitHub's Contents API returns 200 immediately, but the PR's commit list
        // may lag by 10-30+ seconds due to backend indexing. Without this wait,
        // downstream notifications tell the human to review before the commit is visible.
        await WaitForCommitVisibilityAsync(pr.Number, preCommitSha, ct);
    }

    /// <summary>
    /// Polls the PR until its head SHA changes from <paramref name="previousSha"/>,
    /// confirming that the latest commit is indexed and visible in the PR UI.
    /// Times out after ~45 seconds to avoid blocking the pipeline indefinitely.
    /// </summary>
    public async Task WaitForCommitVisibilityAsync(
        int prNumber, string previousSha, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(previousSha))
        {
            // No baseline to compare — give a fixed grace period
            _logger.LogDebug("No previous SHA for PR #{Number}, waiting 5s for commit indexing", prNumber);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return;
        }

        const int maxAttempts = 9;
        const int delayMs = 5_000; // 5 seconds between polls → ~45s max

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await Task.Delay(delayMs, ct);
            try
            {
                var current = await _prService.GetAsync(prNumber, ct);
                if (current is not null && !string.Equals(current.HeadSha, previousSha, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Commit visible on PR #{Number} after {Attempt} poll(s) ({Sha})",
                        prNumber, attempt, current.HeadSha?[..7]);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error polling PR #{Number} for commit visibility (attempt {Attempt})", prNumber, attempt);
            }
        }

        _logger.LogWarning("Commit visibility timeout on PR #{Number} after {Seconds}s — proceeding anyway",
            prNumber, maxAttempts * delayMs / 1000);
    }

    /// <summary>
    /// Merge an existing document PR (assumes content already committed).
    /// Cleans up tracking markers, updates labels, and auto-merges.
    /// </summary>
    public async Task MergeDocumentPRAsync(
        AgentPullRequest pr,
        string agentName,
        string documentPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pr);

        // Clean up the tracking marker file
        var docSlug = Slugify(System.IO.Path.GetFileNameWithoutExtension(documentPath));
        var trackingPath = $"AgentDocs/.tracking/{docSlug}.tracking.md";
        try
        {
            await _repoContent.DeleteFileAsync(trackingPath, "Remove tracking marker", pr.HeadBranch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete tracking file {Path} — may not exist", trackingPath);
        }

        // Update labels to show completion — include all review-pipeline labels so
        // TE safety net, Architect review, and PM review loops skip document PRs
        // (they contain only markdown docs and don't need code review or tests).
        await _prService.UpdateAsync(pr.Number,
            labels: [Labels.ReadyForReview, Labels.Approved,
                     Labels.TestsAdded, Labels.ArchitectApproved, Labels.PmApproved], ct: ct);

        // Auto-merge
        await Task.Delay(3000, ct);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _prService.MergeAsync(pr.Number,
                    $"Merge {documentPath} — approved by {agentName}", ct);
                _logger.LogInformation("Merged document PR #{Number}", pr.Number);
                try { await _branchService.DeleteAsync(pr.HeadBranch, ct); } catch { /* best-effort */ }
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "Merge attempt {Attempt}/3 failed for PR #{Number}, retrying",
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
        await _repoContent.CreateOrUpdateFileAsync(documentPath, documentContent, commitMessage, pr.HeadBranch, ct);

        // 2. Clean up the tracking marker file so it doesn't merge into main
        var docSlug = Slugify(System.IO.Path.GetFileNameWithoutExtension(documentPath));
        var trackingPath = $"AgentDocs/.tracking/{docSlug}.tracking.md";
        try
        {
            await _repoContent.DeleteFileAsync(trackingPath, "Remove tracking marker", pr.HeadBranch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete tracking file {Path} — may not exist", trackingPath);
        }

        // 3. Update PR labels and description to show completion — include all
        // review-pipeline labels so TE safety net, Architect review, and PM review
        // loops skip document PRs (they contain only markdown docs).
        await _prService.UpdateAsync(pr.Number,
            labels: [Labels.ReadyForReview, Labels.Approved,
                     Labels.TestsAdded, Labels.ArchitectApproved, Labels.PmApproved], ct: ct);

        // 4. Auto-merge (no review needed for initial docs)
        // Brief delay to let GitHub process the commit before attempting merge
        await Task.Delay(3000, ct);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _prService.MergeAsync(pr.Number,
                    $"Merge {documentPath} — auto-approved by {agentName}", ct);
                _logger.LogInformation("Auto-merged document PR #{Number}", pr.Number);
                try { await _branchService.DeleteAsync(pr.HeadBranch, ct); } catch { /* best-effort */ }
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
    public static readonly string[] DefaultReviewers = ["ProgramManager", "SoftwareEngineer"];
    public static readonly string FallbackReviewer = "Architect";

    /// <summary>
    /// Get the required reviewers for a PR, substituting the Architect when the
    /// author is one of the default reviewers (e.g., SE can't review its own PR).
    /// </summary>
    /// <summary>
    /// Determine which agents must approve a PR before it can be merged.
    /// Routing rules:
    ///   - TestEngineer PRs → only SoftwareEngineer (test quality, not business/arch review)
    ///   - Engineer PRs → ProgramManager + SoftwareEngineer
    ///   - When the author IS a default reviewer, Architect substitutes in
    /// </summary>
    public static string[] GetRequiredReviewers(string prAuthorRole)
    {
        // TestEngineer PRs need only SE approval — PM/Architect don't review test suites
        if (prAuthorRole.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase)
            || prAuthorRole.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase))
        {
            return ["SoftwareEngineer"];
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
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
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
        int prNumber, string approverAgent, string reason,
        bool requireTestsBeforeMerge = false,
        bool deferMerge = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approverAgent);

        // Post the approval comment with the review rationale
        var comment = string.IsNullOrWhiteSpace(reason)
            ? $"**[{approverAgent}] APPROVED**"
            : $"**[{approverAgent}] APPROVED**\n\n{reason}";
        await _reviewService.AddCommentAsync(prNumber, comment, ct);
        _logger.LogInformation("Agent {Agent} approved PR #{Number}", approverAgent, prNumber);

        // Determine required reviewers based on PR author
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        var authorRole = DetectAuthorRole(pr?.Title ?? "");
        var requiredReviewers = GetRequiredReviewers(authorRole);

        // Check if all required reviewers have now approved
        var approvedReviewers = await GetApprovedReviewersAsync(prNumber, ct);
        _logger.LogInformation("PR #{Number} approvals: [{Approvers}] of [{Required}]",
            prNumber, string.Join(", ", approvedReviewers), string.Join(", ", requiredReviewers));

        if (requiredReviewers.All(r => approvedReviewers.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            // All reviewers approved — update labels
            if (pr is not null)
            {
                var updatedLabels = pr.Labels
                    .Where(l => !string.Equals(l, Labels.ReadyForReview, StringComparison.OrdinalIgnoreCase))
                    .Append(Labels.Approved)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await _prService.UpdateAsync(prNumber, labels: updatedLabels, ct: ct);
            }

            // If inline test workflow is active, don't merge yet — wait for TE to add tests AND post results
            if (requireTestsBeforeMerge &&
                pr is not null &&
                !pr.Labels.Contains(Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "PR #{Number} approved by all reviewers but waiting for Test Engineer to add tests",
                    prNumber);
                await _reviewService.AddCommentAsync(prNumber,
                    "✅ **Code approved by all reviewers.** Waiting for the Test Engineer to add tests before merging.", ct);
                return MergeAttemptResult.AwaitingTests;
            }

            // Even if tests-added label is present, verify TE actually posted a test results comment.
            // The TE adds the label when pushing test files but posts results AFTER running tests.
            if (requireTestsBeforeMerge &&
                pr is not null &&
                pr.Labels.Contains(Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
            {
                var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
                bool hasTeResultComment = comments.Any(c =>
                    c.Body.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase) &&
                    (c.Body.Contains("Test Results", StringComparison.OrdinalIgnoreCase) ||
                     c.Body.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
                     c.Body.Contains("UI Test", StringComparison.OrdinalIgnoreCase)));
                if (!hasTeResultComment)
                {
                    _logger.LogInformation(
                        "PR #{Number} has tests-added label but no TE results comment yet — waiting",
                        prNumber);
                    return MergeAttemptResult.AwaitingTests;
                }
            }

            // If caller wants to defer merge (e.g. for a human gate), signal ready without merging
            if (deferMerge)
            {
                _logger.LogInformation("All reviewers approved PR #{Number}, deferring merge for human gate", prNumber);
                return MergeAttemptResult.ReadyToMerge;
            }

            // Tests already added (or not required) — merge!
            _logger.LogInformation("All reviewers approved PR #{Number}, merging", prNumber);
            return await AttemptMergeAsync(prNumber, approverAgent, approvedReviewers, pr, ct);
        }

        _logger.LogInformation("PR #{Number} still needs approval from: {Missing}",
            prNumber,
            string.Join(", ", requiredReviewers.Except(approvedReviewers, StringComparer.OrdinalIgnoreCase)));
        return MergeAttemptResult.AwaitingApprovals;
    }

    /// <summary>
    /// Merge a PR that has been approved and (if inline test workflow) has tests.
    /// Used by PE to merge PRs with both 'approved' and 'tests-added' labels.
    /// </summary>
    public async Task<MergeAttemptResult> MergeApprovedTestedPRAsync(
        int prNumber, string mergerAgent, CancellationToken ct = default)
    {
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            return MergeAttemptResult.NotOpen;

        var approvedReviewers = await GetApprovedReviewersAsync(prNumber, ct);
        return await AttemptMergeAsync(prNumber, mergerAgent, approvedReviewers, pr, ct);
    }

    /// <summary>
    /// Shared merge logic: tries merge with branch-update fallback and cleanup.
    /// </summary>
    private async Task<MergeAttemptResult> AttemptMergeAsync(
        int prNumber, string mergerAgent, List<string> approvedReviewers,
        AgentPullRequest? pr, CancellationToken ct)
    {
        // Security hard-block: never merge a PR flagged by the SecurityAuditor.
        // The security-blocked label is a gate — it must be removed by a clean
        // SecurityAuditor re-review before ANY merge path proceeds.
        if (pr is not null &&
            pr.Labels.Contains(Labels.SecurityBlocked, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "PR #{Number} merge REFUSED — security-blocked label present. " +
                "Resolve SecurityAuditor findings and request a re-review first.",
                prNumber);
            try
            {
                await _reviewService.AddCommentAsync(prNumber,
                    "🛑 **Merge blocked by SecurityAuditor.** " +
                    "The `security-blocked` label must be removed before this PR can merge. " +
                    "Address the findings in the SecurityAuditor comment above and push a fix — " +
                    "the SecurityAuditor will re-review automatically.", ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not post security-block comment on PR #{Number}", prNumber);
            }
            return MergeAttemptResult.SecurityBlocked;
        }

        try
        {
            await _prService.MergeAsync(prNumber,
                $"Merged by {mergerAgent} after approval from {string.Join(" and ", approvedReviewers)}", ct);
        }
        catch (PlatformConflictException ex) when (ex.Kind == PlatformConflictKind.NotMergeable)
        {
            // Multi-worker race guard: another SE may have just merged this PR. The
            // GitHub API surfaces "already merged" as the same NotMergeable kind we
            // get for real conflicts. Re-fetch and short-circuit if already merged
            // so we don't kick off the close-and-recreate-task path on a PR that
            // succeeded under a different worker.
            try
            {
                var freshPr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
                if (freshPr is null ||
                    !string.Equals(freshPr.State, "open", StringComparison.OrdinalIgnoreCase) ||
                    freshPr.IsMerged)
                {
                    _logger.LogInformation(
                        "PR #{Number} already merged or closed (state={State}, merged={Merged}) — treating as no-op",
                        prNumber, freshPr?.State, freshPr?.IsMerged);
                    return MergeAttemptResult.NotOpen;
                }
            }
            catch (Exception fetchEx)
            {
                _logger.LogDebug(fetchEx, "Re-fetch after NotMergeable failed for PR #{Number}", prNumber);
            }

            _logger.LogWarning("PR #{Number} not mergeable, attempting branch update", prNumber);
            var updated = await _prService.UpdateBranchAsync(prNumber, ct);
            if (!updated)
            {
                _logger.LogWarning("PR #{Number} branch update failed — attempting force-rebase onto main", prNumber);
                updated = await _prService.RebaseBranchAsync(prNumber, ct);
            }

            if (updated)
            {
                // Poll with exponential backoff — GitHub needs time to recompute mergeable status
                const int maxMergeRetries = 3;
                Exception? lastException = null;
                for (int attempt = 0; attempt < maxMergeRetries; attempt++)
                {
                    var delayMs = (attempt + 1) * 5000; // 5s, 10s, 15s
                    await Task.Delay(delayMs, ct);
                    try
                    {
                        await _prService.MergeAsync(prNumber,
                            $"Merged by {mergerAgent} after branch sync and approval from {string.Join(" and ", approvedReviewers)}", ct);
                        lastException = null;
                        break;
                    }
                    catch (PlatformConflictException retryEx) when (retryEx.Kind == PlatformConflictKind.NotMergeable)
                    {
                        lastException = retryEx;
                        _logger.LogDebug(retryEx,
                            "PR #{Number} not yet mergeable after branch update (attempt {Attempt}/{Max})",
                            prNumber, attempt + 1, maxMergeRetries);
                    }
                }

                if (lastException is not null)
                {
                    // Final defensive check: another worker may have completed the merge while we
                    // were retrying. If the PR is now merged or closed, skip the misleading
                    // "Merge blocked" comment that would otherwise spam the merged-PR thread.
                    try
                    {
                        var postRetryPr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
                        if (postRetryPr is null ||
                            !string.Equals(postRetryPr.State, "open", StringComparison.OrdinalIgnoreCase) ||
                            postRetryPr.IsMerged)
                        {
                            _logger.LogInformation(
                                "PR #{Number} merged/closed during retry loop (state={State}, merged={Merged}) — suppressing misleading merge-blocked comment",
                                prNumber, postRetryPr?.State, postRetryPr?.IsMerged);
                            return MergeAttemptResult.NotOpen;
                        }
                    }
                    catch (Exception postFetchEx)
                    {
                        _logger.LogDebug(postFetchEx, "Post-retry re-fetch failed for PR #{Number}", prNumber);
                    }

                    _logger.LogWarning(lastException, "PR #{Number} still not mergeable after branch update and {Max} retries", prNumber, maxMergeRetries);
                    var baseBranch = pr?.BaseBranch ?? "the base branch";
                    await _reviewService.AddCommentAsync(prNumber,
                        $"⚠️ **Merge blocked** — PR has conflicts with `{baseBranch}` that could not be auto-resolved. " +
                        $"Branch update was attempted but merge still failed after {maxMergeRetries} retries.", ct);
                    return MergeAttemptResult.ConflictBlocked;
                }
            }
            else
            {
                // Same defensive re-fetch — UpdateBranch + Rebase both failing on a PR that's
                // actually already merged is a common race symptom (the operations 4xx because
                // the PR is closed). Suppress the misleading comment in that case.
                try
                {
                    var postUpdatePr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
                    if (postUpdatePr is null ||
                        !string.Equals(postUpdatePr.State, "open", StringComparison.OrdinalIgnoreCase) ||
                        postUpdatePr.IsMerged)
                    {
                        _logger.LogInformation(
                            "PR #{Number} merged/closed before branch-update path completed (state={State}, merged={Merged}) — suppressing misleading merge-blocked comment",
                            prNumber, postUpdatePr?.State, postUpdatePr?.IsMerged);
                        return MergeAttemptResult.NotOpen;
                    }
                }
                catch (Exception postFetchEx)
                {
                    _logger.LogDebug(postFetchEx, "Post-update re-fetch failed for PR #{Number}", prNumber);
                }

                _logger.LogWarning("PR #{Number} branch update and rebase both failed", prNumber);
                var baseBranch = pr?.BaseBranch ?? "the base branch";
                await _reviewService.AddCommentAsync(prNumber,
                    $"⚠️ **Merge blocked** — PR has conflicts with `{baseBranch}` that require resolution. " +
                    $"The engineer should rebase and resolve conflicts.", ct);
                return MergeAttemptResult.ConflictBlocked;
            }
        }

        // Clean up the head branch after merge
        if (pr is not null && !string.IsNullOrEmpty(pr.HeadBranch))
            await _branchService.DeleteAsync(pr.HeadBranch, ct);

        // Fire-and-forget: notify checkpoint subscribers
        try { OnPRMerged?.Invoke(prNumber, pr?.Title); }
        catch (Exception evtEx) { _logger.LogDebug(evtEx, "OnPRMerged handler threw"); }

        // Proactively sync other open PRs with main to prevent merge conflicts from accumulating.
        // This runs after every successful merge so other PRs stay up-to-date.
        await SyncOpenPullRequestBranchesAsync(prNumber, ct);

        return MergeAttemptResult.Merged;
    }

    /// <summary>
    /// After a PR merge, proactively update other open PR branches with the latest main.
    /// Uses the GitHub "update branch" API (merges main into the PR branch).
    /// Only syncs branches that are behind main; failures are logged but don't block.
    /// </summary>
    private async Task SyncOpenPullRequestBranchesAsync(int justMergedPrNumber, CancellationToken ct)
    {
        try
        {
            var openPRs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
            var behindPRs = openPRs
                .Where(pr => pr.Number != justMergedPrNumber)
                .ToList();

            if (behindPRs.Count == 0)
                return;

            _logger.LogInformation(
                "Post-merge sync: updating {Count} open PR branches after merging #{MergedPR}",
                behindPRs.Count, justMergedPrNumber);

            foreach (var pr in behindPRs)
            {
                try
                {
                    // Skip PRs that are still mergeable — only sync those with actual conflicts
                    // or that are significantly behind. GitHub shows "clean" for PRs that can
                    // merge without conflicts even if behind by a few commits.
                    if (string.Equals(pr.MergeableState, "clean", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(pr.MergeableState, "unstable", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping sync for PR #{PrNumber} — mergeable state is {State}",
                            pr.Number, pr.MergeableState);
                        continue;
                    }

                    var isBehind = await _prService.IsBehindBaseAsync(pr.Number, ct);
                    if (!isBehind)
                        continue;

                    var updated = await _prService.UpdateBranchAsync(pr.Number, ct);
                    if (updated)
                    {
                        _logger.LogInformation("Synced PR #{PrNumber} branch with main", pr.Number);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Could not auto-sync PR #{PrNumber} — may need manual conflict resolution",
                            pr.Number);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to sync PR #{PrNumber} branch — will retry at merge time", pr.Number);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-merge branch sync failed — non-critical, will retry at next merge");
        }
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
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
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
    /// Centralized run-scope filter for PRs. Returns true when the PR belongs to the
    /// active run scope (branch contains the scope segment) OR has been adopted into
    /// this run via a "Closes #N" body link. When <paramref name="runScope"/> is null
    /// (pre-project state), all PRs pass.
    /// </summary>
    public static bool IsCurrentRunScopePr(string? headBranch, string? prBody, string? runScope)
    {
        if (string.IsNullOrWhiteSpace(runScope))
            return true;

        // Unknown branch info → accept (synthetic/doc PRs, backward compat)
        if (string.IsNullOrWhiteSpace(headBranch))
            return true;

        if (headBranch.Contains($"/{runScope}/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Cross-run adoption fallback: PR body links to an issue (Closes #N)
        return ParseLinkedIssueNumber(prBody).HasValue;
    }

    /// <summary>
    /// Extracts screenshot/image URLs from PR comments (posted by PE or TE agents).
    /// Returns a formatted context string describing each screenshot for AI reviewers.
    /// </summary>
    public async Task<string> GetPRScreenshotContextAsync(int prNumber, CancellationToken ct = default)
    {
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
        var screenshots = new List<(string url, string context)>();

        foreach (var comment in comments)
        {
            // Match markdown image syntax: ![alt](url)
            var matches = System.Text.RegularExpressions.Regex.Matches(
                comment.Body, @"!\[([^\]]*)\]\((https?://[^\)]+\.(?:png|jpg|jpeg|gif|webp))\)");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var alt = match.Groups[1].Value;
                var url = match.Groups[2].Value;

                // Extract surrounding context (what step, what agent posted it)
                var lines = comment.Body.Split('\n');
                var contextLines = lines
                    .Where(l => l.Contains("Step", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("Preview", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("Captured", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
                    .Take(3);
                var ctx = string.Join(" ", contextLines).Trim();
                if (string.IsNullOrEmpty(ctx)) ctx = alt;

                screenshots.Add((url, ctx));
            }
        }

        if (screenshots.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 📸 Application Screenshots from PR Comments\n");
        sb.AppendLine("The following screenshots show how the application looks when running.");
        sb.AppendLine("**IMPORTANT**: Review these screenshots carefully:");
        sb.AppendLine("- Does the app render correctly without errors?");
        sb.AppendLine("- Are there any error pages, exception messages, or blank screens?");
        sb.AppendLine("- Does the visual output match what the PR claims to implement?");
        sb.AppendLine("- If the screenshot shows an error page or unhandled exception, this is a REWORK issue.\n");

        for (var i = 0; i < screenshots.Count; i++)
        {
            var (url, ctx) = screenshots[i];
            sb.AppendLine($"### Screenshot {i + 1}: {ctx}");
            sb.AppendLine($"URL: {url}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Downloads actual screenshot images from PR comments for vision-based AI review.
    /// Returns image bytes with metadata so callers can add them as ImageContent to chat history.
    /// </summary>
    public async Task<List<ScreenshotImage>> GetPRScreenshotImagesAsync(
        int prNumber, int maxImages = 5, CancellationToken ct = default)
    {
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
        var imageInfos = new List<(string url, string context)>();

        foreach (var comment in comments)
        {
            var matches = Regex.Matches(
                comment.Body, @"!\[([^\]]*)\]\((https?://[^\)]+\.(?:png|jpg|jpeg|gif|webp))\)");
            foreach (Match match in matches)
            {
                var alt = match.Groups[1].Value;
                var url = match.Groups[2].Value;

                // Determine screenshot source for PM review annotation
                var source = comment.Body.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase)
                    ? "[Test Engineer]"
                    : comment.Body.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)
                        ? "[Engineer/Author]"
                        : "[Unknown source]";

                var lines = comment.Body.Split('\n');
                var contextLines = lines
                    .Where(l => l.Contains("Step", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("Preview", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("Captured", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
                    .Take(3);
                var ctx = string.Join(" ", contextLines).Trim();
                if (string.IsNullOrEmpty(ctx)) ctx = alt;
                if (string.IsNullOrEmpty(ctx)) ctx = $"Screenshot from PR #{prNumber}";
                ctx = $"{source} {ctx}";

                imageInfos.Add((url, ctx));
            }
        }

        if (imageInfos.Count == 0)
            return [];

        // Try local workspace first — screenshots already exist on disk from SE/TE work.
        // Match by exact filename from the PR comment URL to avoid stale/unrelated images.
        var localResults = TryResolveScreenshotsLocally(imageInfos, maxImages);
        if (localResults.Count > 0)
        {
            _logger.LogDebug("Resolved {Count} screenshots from local workspace (skipping network download)", localResults.Count);
            return localResults;
        }

        // Acquire auth token upfront for private repo downloads (graceful degradation on failure)
        string? authToken = null;
        string? authScheme = null;
        if (_authProvider is not null)
        {
            try
            {
                authToken = await _authProvider.GetTokenAsync(ct);
                authScheme = _authProvider.AuthScheme;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not acquire auth token for screenshot downloads — will attempt unauthenticated");
            }
        }

        // Download images (limit to maxImages to avoid excessive bandwidth)
        var results = new List<ScreenshotImage>();
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        foreach (var (url, context) in imageInfos.Take(maxImages))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Only attach auth to trusted GitHub hosts — never leak tokens to external domains
                if (authToken is not null && IsTrustedScreenshotHost(url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(authScheme!, authToken);
                }

                using var response = await httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Failed to download screenshot {Url}: {Status}", url, response.StatusCode);
                    continue;
                }

                // Early guard: if response is HTML, it's likely an SSO redirect page
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Downloaded HTML instead of image from {Url} (likely SSO/auth redirect) — skipping", url);
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 100) // Skip tiny/broken images
                    continue;

                // Validate image magic bytes — SSO redirects return HTML not image data
                var detectedMimeType = TryDetectImageMimeType(bytes);
                if (detectedMimeType is null)
                {
                    _logger.LogWarning("Downloaded non-image content from {Url} ({Size} bytes, starts with 0x{B0:X2}{B1:X2}{B2:X2}{B3:X2}) — skipping",
                        url, bytes.Length, bytes[0], bytes[1], bytes[2], bytes[3]);
                    continue;
                }

                // Cap image size at 2MB to avoid token explosion
                if (bytes.Length > 2 * 1024 * 1024)
                {
                    _logger.LogDebug("Skipping oversized screenshot ({Size} bytes): {Url}", bytes.Length, url);
                    continue;
                }

                results.Add(new ScreenshotImage(bytes, detectedMimeType, context, url));
                _logger.LogDebug("Downloaded screenshot ({Size} bytes, {MimeType}): {Context}", bytes.Length, detectedMimeType, context);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download screenshot from {Url}", url);
            }
        }

        return results;
    }

    /// <summary>
    /// Only send auth tokens to trusted platform-owned hosts for release asset downloads.
    /// Prevents credential leakage to arbitrary external image URLs in PR comments.
    /// Supports both GitHub and Azure DevOps hosting domains.
    /// </summary>
    private static bool IsTrustedScreenshotHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return host == "github.com"
            || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host == "githubusercontent.com"
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
            || host == "dev.azure.com"
            || host.EndsWith(".dev.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".visualstudio.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects image format from magic bytes. Returns MIME type or null if not a recognized image.
    /// </summary>
    /// <summary>
    /// Resolves screenshots from local agent workspace directories by matching the exact filename
    /// from PR comment image URLs. Avoids network calls and auth issues for private repos.
    /// </summary>
    private List<ScreenshotImage> TryResolveScreenshotsLocally(
        List<(string url, string context)> imageInfos, int maxImages)
    {
        if (string.IsNullOrEmpty(_workspaceRootPath) || !Directory.Exists(_workspaceRootPath))
            return [];

        var results = new List<ScreenshotImage>();

        foreach (var (url, context) in imageInfos.Take(maxImages))
        {
            try
            {
                // Extract filename from URL (e.g., "pr-955-ready-20260507041551-f5d31b.png")
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName))
                    continue;

                // Search known screenshot locations across all agent workspaces
                string? localPath = null;
                foreach (var agentDir in Directory.EnumerateDirectories(_workspaceRootPath))
                {
                    foreach (var repoDir in Directory.EnumerateDirectories(agentDir))
                    {
                        // Check .screenshots/, test-results/screenshots/, and repo root
                        var candidates = new[]
                        {
                            Path.Combine(repoDir, ".screenshots", fileName),
                            Path.Combine(repoDir, "test-results", "screenshots", fileName),
                            Path.Combine(repoDir, fileName),
                        };

                        localPath = candidates.FirstOrDefault(File.Exists);
                        if (localPath is not null) break;
                    }
                    if (localPath is not null) break;
                }

                if (localPath is null)
                    continue;

                var bytes = File.ReadAllBytes(localPath);
                if (bytes.Length < 100) continue;

                var mimeType = TryDetectImageMimeType(bytes);
                if (mimeType is null) continue;

                if (bytes.Length > 2 * 1024 * 1024) continue; // Same 2MB cap

                results.Add(new ScreenshotImage(bytes, mimeType, context, localPath));
                _logger.LogDebug("Resolved screenshot locally ({Size} bytes): {Path}", bytes.Length, localPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve local screenshot for {Url}", url);
            }
        }

        return results;
    }

    /// <summary>
    /// Finds a local agent workspace directory that has the given branch checked out.
    /// Scans .agents/{agentId}/{repo}/.git/HEAD for the matching branch ref.
    /// Returns the repo directory path, or null if not found.
    /// </summary>
    private string? TryFindLocalWorktreeForBranch(string branchName)
    {
        if (string.IsNullOrEmpty(_workspaceRootPath) || !Directory.Exists(_workspaceRootPath))
            return null;

        try
        {
            foreach (var agentDir in Directory.EnumerateDirectories(_workspaceRootPath))
            {
                foreach (var repoDir in Directory.EnumerateDirectories(agentDir))
                {
                    var gitHeadFile = Path.Combine(repoDir, ".git", "HEAD");
                    if (!File.Exists(gitHeadFile))
                    {
                        // May be a worktree (file .git instead of dir .git)
                        var gitFile = Path.Combine(repoDir, ".git");
                        if (File.Exists(gitFile))
                            gitHeadFile = ResolveWorktreeHead(gitFile);
                        else
                            continue;
                    }

                    if (gitHeadFile is null || !File.Exists(gitHeadFile))
                        continue;

                    var headContent = File.ReadAllText(gitHeadFile).Trim();
                    // HEAD format: "ref: refs/heads/branch-name"
                    if (headContent.Equals($"ref: refs/heads/{branchName}", StringComparison.OrdinalIgnoreCase))
                        return repoDir;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning workspace for branch {Branch}", branchName);
        }

        return null;
    }

    /// <summary>
    /// For git worktrees, the .git file points to the actual git dir. Resolve the HEAD file path.
    /// </summary>
    private static string? ResolveWorktreeHead(string gitFile)
    {
        try
        {
            var content = File.ReadAllText(gitFile).Trim();
            // Format: "gitdir: /path/to/.git/worktrees/name"
            if (content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var gitDir = content["gitdir:".Length..].Trim();
                return Path.Combine(gitDir, "HEAD");
            }
        }
        catch { }
        return null;
    }

    private static string? TryDetectImageMimeType(byte[] bytes)
    {
        if (bytes.Length < 4) return null;

        // PNG: 89 50 4E 47
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        // GIF: 47 49 46
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";

        // WebP: RIFF....WEBP
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        return null;
    }

    /// <summary>Screenshot image data for vision-based AI review.</summary>
    public record ScreenshotImage(byte[] ImageBytes, string MimeType, string Description, string SourceUrl);

    /// <summary>
    /// Verdict from <see cref="EvaluateScreenshotAgainstExpectationsAsync"/>. The caller decides what
    /// to do with a `MatchesExpectations == false` — typically: treat as capture failure, route through
    /// the "App Preview Unavailable" path, and surface the rationale to PM review so the PR doesn't
    /// approve on the back of a blank/wrong-state/error-page screenshot.
    /// </summary>
    public sealed record ScreenshotEvaluation(
        bool MatchesExpectations,
        double Confidence,
        string Observed,
        string Expected,
        IReadOnlyList<string> BlockingIssues,
        string Verdict);

    /// <summary>
    /// Uses AI vision to generate a concise summary of what a screenshot shows.
    /// Returns a short description suitable for dashboard activity cards.
    /// </summary>
    public static async Task<string> DescribeScreenshotAsync(
        ScreenshotImage screenshot,
        IChatCompletionService chat,
        CancellationToken ct = default)
    {
        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a UI screenshot analyst. Describe what you see in 1-2 sentences. " +
                "Focus on: what the page shows (title, content, layout), whether it looks like a working app " +
                "or an error page. If you see error messages, quote them. Be concise.");

            var items = new ChatMessageContentItemCollection
            {
                new TextContent("Describe this screenshot:"),
                new ImageContent(screenshot.ImageBytes, screenshot.MimeType)
            };
            history.AddUserMessage(items);

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var desc = response.FirstOrDefault()?.Content?.Trim();
            return string.IsNullOrWhiteSpace(desc) ? "(no description)" : desc;
        }
        catch
        {
            return $"(screenshot: {screenshot.ImageBytes.Length} bytes, could not describe)";
        }
    }

    /// <summary>
    /// Semantic check: does this screenshot actually show what the PR was supposed to deliver?
    /// Beyond "is the canvas blank" — also catches: rendered wrong scene, error page, partial render,
    /// loading spinner stuck, login screen leaking through, backend-error toast covering the UI, etc.
    ///
    /// <para>
    /// Returns a structured verdict the caller can gate on. <c>Confidence</c> is the AI's self-reported
    /// certainty (0.0–1.0); the caller should typically require <c>Confidence ≥ 0.6</c> before blocking
    /// a PR — otherwise an over-confident "no it doesn't match" on an unfamiliar UI could stall the run.
    /// </para>
    ///
    /// <para>
    /// **Safe-by-default:** any failure (AI unavailable, response unparseable, image too small for
    /// vision, etc.) returns <c>MatchesExpectations = true</c> with <c>Confidence = 0.0</c> so a
    /// check-failure does NOT block a PR. Only an explicit, confident "does not match" verdict blocks.
    /// </para>
    /// </summary>
    /// <param name="screenshot">The captured screenshot.</param>
    /// <param name="prTitle">PR title — concrete signal of intent (e.g. "[T5] Grid Rendering").</param>
    /// <param name="prBody">PR body — acceptance criteria + file plan + step plan.</param>
    /// <param name="chat">Vision-capable chat completion service.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ScreenshotEvaluation> EvaluateScreenshotAgainstExpectationsAsync(
        ScreenshotImage screenshot,
        string prTitle,
        string? prBody,
        IChatCompletionService chat,
        CancellationToken ct = default)
    {
        var safeOk = new ScreenshotEvaluation(
            MatchesExpectations: true,
            Confidence: 0.0,
            Observed: "(check did not run conclusively)",
            Expected: "(derived from PR title/body)",
            BlockingIssues: Array.Empty<string>(),
            Verdict: "INCONCLUSIVE");

        try
        {
            var trimmedBody = string.IsNullOrWhiteSpace(prBody)
                ? "(no body)"
                : (prBody.Length > 4000 ? prBody[..4000] + "…(truncated)" : prBody);

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are a quality gate evaluating whether a screenshot of a running application matches " +
                "what the corresponding Pull Request claims to deliver.\n\n" +
                "Decide whether the visible UI is consistent with the PR's intent. Examples of mismatch:\n" +
                "  - PR says 'Grid Rendering & Pathfinding' but screenshot shows a blank canvas\n" +
                "  - PR says 'Login form' but screenshot shows a 500 error page\n" +
                "  - PR says 'Dashboard with 3 cards' but screenshot shows only a loading spinner\n" +
                "  - PR says 'Settings page' but screenshot shows the home page (wrong route)\n" +
                "  - PR is a backend-only API change — the screenshot of any UI is INCONCLUSIVE, " +
                "    not a mismatch (return matches:true, confidence:0.0)\n\n" +
                "Return ONLY a JSON object, no markdown fences, with these exact keys:\n" +
                "{\n" +
                "  \"matches\": <true|false>,\n" +
                "  \"confidence\": <0.0..1.0 — how sure you are about the verdict>,\n" +
                "  \"observed\": \"<one sentence: what the screenshot actually shows>\",\n" +
                "  \"expected\": \"<one sentence: what the PR title/body said the deliverable was>\",\n" +
                "  \"blocking_issues\": [\"<short reason 1>\", \"<short reason 2>\"]  // empty list if matches\n" +
                "}\n\n" +
                "Confidence calibration:\n" +
                "  >= 0.8: the screenshot definitively contradicts the PR intent (blank canvas for a UI PR, " +
                "          visible error page, totally different feature)\n" +
                "  0.5–0.8: probable mismatch but ambiguous (partial render, possible loading state)\n" +
                "  0.0–0.5: PR is not UI-focused or the screenshot is not interpretable for this PR\n" +
                "Use confidence < 0.5 (returning matches:true) for backend-only / API-only PRs so they " +
                "are not falsely flagged.");

            var userPrompt =
                "PR title: " + prTitle + "\n\n" +
                "PR body:\n" + trimmedBody + "\n\n" +
                "Now evaluate the attached screenshot against the PR's stated deliverable.";

            var items = new ChatMessageContentItemCollection
            {
                new TextContent(userPrompt),
                new ImageContent(screenshot.ImageBytes, screenshot.MimeType)
            };
            history.AddUserMessage(items);

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var raw = response.FirstOrDefault()?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return safeOk;

            // Tolerate optional code fences around the JSON.
            var jsonStart = raw.IndexOf('{');
            var jsonEnd = raw.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart) return safeOk;
            var json = raw.Substring(jsonStart, jsonEnd - jsonStart + 1);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var matches = root.TryGetProperty("matches", out var m) && m.GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.0;
            var observed = root.TryGetProperty("observed", out var o) ? (o.GetString() ?? "") : "";
            var expected = root.TryGetProperty("expected", out var e) ? (e.GetString() ?? "") : "";
            var issues = new List<string>();
            if (root.TryGetProperty("blocking_issues", out var bi) && bi.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in bi.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) issues.Add(s);
                }
            }

            var verdict = matches
                ? (confidence >= 0.6 ? "MATCHES" : "INCONCLUSIVE")
                : (confidence >= 0.6 ? "DOES_NOT_MATCH" : "INCONCLUSIVE");

            return new ScreenshotEvaluation(
                MatchesExpectations: matches,
                Confidence: Math.Clamp(confidence, 0.0, 1.0),
                Observed: observed,
                Expected: expected,
                BlockingIssues: issues,
                Verdict: verdict);
        }
        catch
        {
            return safeOk;
        }
    }
    public async Task<string> GetPRCodeContextAsync(
        int prNumber, string headBranch, int maxFileSizeChars = 15000,
        int maxTotalChars = 80000, CancellationToken ct = default)
    {
        // Check shared PR review context cache first — avoids API calls entirely
        // when another agent (SE) already cached the review context from its local worktree.
        if (_reviewContextCache is not null)
        {
            var cached = _reviewContextCache.TryGetLatest(prNumber);
            if (cached is not null)
            {
                var cachedText = cached.CodeContext;
                // Apply total limit even to cached context
                if (cachedText.Length > maxTotalChars)
                {
                    _logger.LogInformation(
                        "PR #{PrNumber} cached review context ({CachedLen:N0} chars) exceeds limit ({Limit:N0}) — switching to summary mode",
                        prNumber, cachedText.Length, maxTotalChars);
                    return BuildSummaryOnlyContext(cached.ChangedFiles, cachedText.Length);
                }

                _logger.LogInformation("PR #{PrNumber} review context served from cache ({FileCount} files, SHA {Sha})",
                    prNumber, cached.ChangedFiles.Count, cached.HeadSha[..7]);
                return cachedText;
            }
        }

        var changedFiles = await _prService.GetChangedFilesAsync(prNumber, ct);
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

        // Try local-first: find the SE workspace that has this branch checked out
        var localWorktree = TryFindLocalWorktreeForBranch(headBranch);
        if (localWorktree is not null)
            _logger.LogDebug("Using local worktree for PR #{PrNumber} code context: {Path}", prNumber, localWorktree);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Files Changed in This PR\n");
        var filesRead = 0;
        var totalChars = 0;
        var truncatedToSummary = false;

        foreach (var filePath in changedFiles)
        {
            if (binaryExtensions.Contains(Path.GetExtension(filePath)))
                continue;

            // Check if we've exceeded the total char limit — switch to file-list-only mode
            if (totalChars > maxTotalChars && !truncatedToSummary)
            {
                truncatedToSummary = true;
                sb.AppendLine($"\n⚠️ **Code context truncated** — {filesRead} files shown ({totalChars:N0} chars), " +
                    $"remaining {changedFiles.Count - filesRead} files listed below without content. " +
                    $"Use your file browsing tools to read these files directly.\n");
            }

            try
            {
                if (truncatedToSummary)
                {
                    // Summary mode: just list file names
                    sb.AppendLine($"- {filePath}");
                    continue;
                }

                string? content = null;

                // Read from local workspace first (avoids API calls + no auth issues)
                if (localWorktree is not null)
                {
                    var localFile = Path.Combine(localWorktree, filePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(localFile))
                    {
                        content = await File.ReadAllTextAsync(localFile, ct);
                    }
                }

                // Fallback to GitHub API if local read failed
                if (string.IsNullOrWhiteSpace(content))
                    content = await _repoContent.GetFileContentAsync(filePath, headBranch, ct);

                if (string.IsNullOrWhiteSpace(content))
                    continue;

                filesRead++;
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
                totalChars += truncated.Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path} from branch {Branch} for review context",
                    filePath, headBranch);
            }
        }

        if (truncatedToSummary)
        {
            _logger.LogWarning(
                "PR #{PrNumber} code context truncated: {FilesRead} files with content ({TotalChars:N0} chars), " +
                "{RemainingFiles} files as summary only (limit: {Limit:N0} chars)",
                prNumber, filesRead, totalChars, changedFiles.Count - filesRead, maxTotalChars);
        }

        // Safety: if the API listed changed files but we couldn't read any, the branch
        // was likely deleted (merged PR). Return a clear indicator instead of empty context
        // that would mislead the reviewer into thinking "zero files".
        if (filesRead == 0 && changedFiles.Count > 0)
        {
            _logger.LogWarning(
                "PR #{PrNumber}: {FileCount} files listed in diff but none readable from branch '{Branch}' " +
                "(branch may be deleted after merge)",
                prNumber, changedFiles.Count, headBranch);
            return $"⚠️ UNABLE TO READ FILES: {changedFiles.Count} file(s) listed in PR diff but branch " +
                $"'{headBranch}' is not accessible (likely deleted after merge). " +
                "DO NOT review this PR — it has already been merged.\n\n" +
                $"Files that were changed: {string.Join(", ", changedFiles.Take(20))}";
        }

        var result = sb.ToString();

        // Cache the review context so other reviewers (PM, Architect, TE) can reuse it
        // without making redundant API calls for the same PR data.
        if (_reviewContextCache is not null && filesRead > 0)
        {
            _reviewContextCache.Store(prNumber, headBranch, result, changedFiles.ToList());
        }

        return result;
    }

    /// <summary>
    /// Build a file-list-only context when the full content exceeds the char limit.
    /// Tells the reviewer to use their tools to browse files directly.
    /// </summary>
    private static string BuildSummaryOnlyContext(IReadOnlyList<string> changedFiles, int originalSize)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Files Changed in This PR ({changedFiles.Count} files)\n");
        sb.AppendLine($"⚠️ Full code context ({originalSize:N0} chars) exceeds review prompt limit. " +
            "File contents are NOT included — use your file browsing tools to read these files directly.\n");
        foreach (var file in changedFiles)
            sb.AppendLine($"- {file}");
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
        await _reviewService.AddCommentAsync(prNumber, comment, ct);
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
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        if (pr is null)
            throw new InvalidOperationException($"PR #{prNumber} not found");

        _logger.LogInformation("Committing fix to PR #{Number} branch {Branch}: {Path}",
            prNumber, pr.HeadBranch, filePath);

        await _repoContent.CreateOrUpdateFileAsync(filePath, content, commitMessage, pr.HeadBranch, ct);
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
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
        if (pr is null)
            throw new InvalidOperationException($"PR #{prNumber} not found");

        _logger.LogInformation(
            "Committing {Count} code files to PR #{Number} branch {Branch}",
            files.Count, prNumber, pr.HeadBranch);

        // Run conflict detection before committing (Tier 3: pre-commit warnings)
        // Also auto-resolve mismatched paths (e.g., Components/Header.razor → src/MyProject/Components/Header.razor)
        IReadOnlyList<(string Path, string Content)>? resolvedFiles = null;
        if (_conflictDetector is not null)
        {
            try
            {
                // Auto-correct file paths that are missing the project subdirectory prefix
                var fileTuplesForCheck = files.Select(f => (f.Path, f.Content)).ToList();
                resolvedFiles = await _conflictDetector.ResolvePathsAsync(fileTuplesForCheck.AsReadOnly(), ct);

                var conflicts = await _conflictDetector.DetectConflictsAsync(resolvedFiles, ct);
                if (conflicts.Count > 0)
                {
                    // Dedup: skip if a conflict warning with the same content already exists on this PR
                    var existingComments = await _reviewService.GetCommentsAsync(prNumber, ct);
                    var alreadyWarned = existingComments.Any(c =>
                        c.Body.Contains("Conflict Detection Warnings", StringComparison.OrdinalIgnoreCase)
                        && conflicts.All(conflict => c.Body.Contains(
                            conflict.Length > 60 ? conflict[..60] : conflict, StringComparison.OrdinalIgnoreCase)));

                    if (!alreadyWarned)
                    {
                        var warningComment = "## ⚠️ Conflict Detection Warnings\n\n" +
                            string.Join("\n\n", conflicts) +
                            "\n\n_These warnings were generated automatically. Please review for potential duplicate code._";
                        await _reviewService.AddCommentAsync(prNumber, warningComment, ct);
                    }
                    _logger.LogWarning("Detected {Count} potential conflicts for PR #{Number}", conflicts.Count, prNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Conflict detection failed for PR #{Number}, proceeding with commit", prNumber);
            }
        }

        // Use resolved paths if available, otherwise fall back to original
        var fileTuples = (resolvedFiles ?? files.Select(f => (f.Path, f.Content)).ToList())
            .ToList()
            .AsReadOnly();

        await _repoContent.BatchCommitFilesAsync(
            fileTuples.Select(f => new PlatformFileCommit { Path = f.Path, Content = f.Content }).ToList(),
            commitMessage, pr.HeadBranch, ct);
    }

    /// <summary>
    /// Get code PRs that are ready for review (have "ready-for-review" label, 
    /// not yet fully approved by both PM and PE).
    /// </summary>
    public async Task<IReadOnlyList<AgentPullRequest>> GetCodePRsPendingReviewAsync(
        CancellationToken ct = default)
    {
        var allPrs = (await _prService.ListOpenAsync(ct)).ToAgentPRs();
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
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
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

            if (comment.Body.Contains("has marked this PR as ready for review", StringComparison.OrdinalIgnoreCase)
                || comment.Body.Contains("] Rework", StringComparison.OrdinalIgnoreCase))
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
        var pr = (await _prService.GetAsync(prNumber, ct))?.ToAgentPR();
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
    /// Multi-instance-aware variant of <see cref="NeedsReviewFromAsync"/>: matches the
    /// reviewer role by PREFIX so that "SoftwareEngineer 1", "SoftwareEngineer 2", and
    /// "SoftwareEngineer" all count as the same role. Use for multi-PE setups where
    /// any one SE reviewing satisfies the SoftwareEngineer role gate.
    ///
    /// Returns true if NO comment matching the role prefix exists, or if a "ready for
    /// review"/"Rework" marker was posted AFTER the most recent role review.
    /// </summary>
    public async Task<bool> RoleNeedsReviewAsync(int prNumber, string rolePrefix, CancellationToken ct = default)
    {
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
        var ordered = comments.OrderByDescending(c => c.CreatedAt).ToList();

        // Find any comment whose captured agent name starts with the role prefix
        // (case-insensitive, also accepts "{Role} N" where N is an instance number).
        DateTime? lastRoleReviewAt = null;
        foreach (var comment in ordered)
        {
            string? captured = null;
            var ap = ApprovalPattern.Match(comment.Body);
            if (ap.Success) captured = ap.Groups[1].Value.Trim();
            else
            {
                var cp = ChangesRequestedPattern.Match(comment.Body);
                if (cp.Success) captured = cp.Groups[1].Value.Trim();
            }
            if (captured is null) continue;

            // Match: exact role OR "role <whitespace> <digits>" (e.g. "SoftwareEngineer 2").
            if (string.Equals(captured, rolePrefix, StringComparison.OrdinalIgnoreCase) ||
                (captured.StartsWith(rolePrefix, StringComparison.OrdinalIgnoreCase) &&
                 captured.Length > rolePrefix.Length &&
                 char.IsWhiteSpace(captured[rolePrefix.Length])))
            {
                lastRoleReviewAt = comment.CreatedAt;
                break;
            }
        }

        if (lastRoleReviewAt is null)
            return true; // role has not reviewed at all

        // Check for rework marker after the role's latest review
        foreach (var comment in ordered)
        {
            if (comment.CreatedAt <= lastRoleReviewAt) break;
            if (comment.Body.Contains("has marked this PR as ready for review", StringComparison.OrdinalIgnoreCase) ||
                comment.Body.Contains("] Rework", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check whether any new commits were pushed to a PR since a specific reviewer's last review.
    /// Returns false if the reviewer requested changes but no new commits appeared — meaning
    /// the author claimed rework but didn't actually push code changes.
    /// </summary>
    public async Task<bool> HasNewCommitsSinceReviewAsync(int prNumber, string reviewerName, CancellationToken ct = default)
    {
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
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
        var commits = await _prService.GetCommitsWithDatesAsync(prNumber, ct);
        return commits.Any(c => c.CommittedAt > lastReviewTime.Value);
    }

    /// <summary>
    /// Check whether a specific agent has posted ANY review comment (approved or changes-requested).
    /// Returns true if the agent has reviewed, false if they have never commented.
    /// </summary>
    public async Task<bool> HasAgentReviewedAsync(int prNumber, string agentName, CancellationToken ct = default)
    {
        var comments = await _reviewService.GetCommentsAsync(prNumber, ct);
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

    /// <summary>
    /// Strips agent thinking/exploration preamble from PR body text.
    /// Copilot CLI can prepend exploration chatter before the actual markdown description.
    /// </summary>
    public static string SanitizePrBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = body.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var prefix = new List<string>();
        var index = 0;
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || IsPrMetadataPrefix(trimmed))
            {
                prefix.Add(lines[index]);
                index++;
                continue;
            }

            break;
        }

        if (index >= lines.Length)
            return body;

        var headingIndex = FindFirstMarkdownHeading(lines, index);
        if (headingIndex >= index)
            return JoinLines(prefix, lines[headingIndex..], newline);

        var contentStart = index;
        while (contentStart < lines.Length && IsExplorationPreambleLine(lines[contentStart].Trim()))
            contentStart++;

        while (contentStart < lines.Length && string.IsNullOrWhiteSpace(lines[contentStart]))
            contentStart++;

        if (contentStart > index && contentStart < lines.Length)
            return JoinLines(prefix, lines[contentStart..], newline);

        return body;
    }

    private static int FindFirstMarkdownHeading(string[] lines, int startIndex)
    {
        var inCodeFence = false;
        for (var i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (!inCodeFence && IsMarkdownHeading(trimmed))
                return i;
        }

        return -1;
    }

    private static bool IsPrMetadataPrefix(string trimmed)
        => trimmed.StartsWith("<!--", StringComparison.Ordinal)
           || trimmed.StartsWith("Closes #", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Fixes #", StringComparison.OrdinalIgnoreCase)
           || trimmed.StartsWith("Resolves #", StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkdownHeading(string trimmed)
    {
        if (!trimmed.StartsWith('#'))
            return false;

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        return hashes is > 0 and <= 6
            && hashes < trimmed.Length
            && char.IsWhiteSpace(trimmed[hashes]);
    }

    private static bool IsExplorationPreambleLine(string trimmed)
    {
        if (trimmed.Length == 0)
            return false;

        return trimmed.StartsWith("I'm exploring", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("I am exploring", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Now let me", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Let me ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Let’s ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Let's ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("I’ll ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("I'll ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("First, let me", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("First, I'll", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("First, I’ll", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Next, let me", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Next, I'll", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Next, I’ll", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinLines(IEnumerable<string> prefix, IEnumerable<string> content, string newline)
        => string.Join(newline, prefix.Concat(content)).Trim();

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
