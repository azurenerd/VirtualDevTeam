using System.Text.RegularExpressions;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Agents;

/// <summary>
/// Manages engineering tasks as work items (GitHub Issues / ADO Tasks).
/// Each task has the "engineering-task" label and structured metadata
/// in the body (complexity, dependencies, parent issue link).
/// </summary>
internal sealed partial class EngineeringTaskIssueManager
{
    /// <summary>
    /// Alias of <see cref="IssueWorkflow.Labels.EngineeringTask"/> — the Core-side canonical
    /// constant. Kept here for back-compat (callers in this project that reference
    /// <c>EngineeringTaskIssueManager.TaskLabel</c>). NoMessyCodePlan Theme 2.
    /// </summary>
    public const string TaskLabel = IssueWorkflow.Labels.EngineeringTask;
    public const string StatusPending = "status:pending";
    public const string StatusAssigned = "status:assigned";
    public const string StatusInProgress = "status:in-progress";
    public const string StatusImplementationComplete = "status:implementation-complete";
    public const string StatusBlocked = "status:blocked";

    /// <summary>
    /// Branch segments that identify NON-engineering roles whose auto-merged PRs must be
    /// excluded from "engineering already complete" recovery checks. Everything else under
    /// <c>agent/</c> is treated as engineering work (SE leader, SE workers, AND SME engineer
    /// roles such as game-developer-1, frontend-engineer, backend-engineer, …).
    ///
    /// History: the original filter accepted any <c>agent/</c> branch and false-fired on
    /// auto-merged research/pmspec/architecture PRs (fixed in f95607a by restricting to
    /// <c>softwareengineer/</c>). But that swing went too far the other way and missed
    /// SME-engineer-merged PRs — bug seen 2026-05-11 where Game Developer 1's merged
    /// T1 PR #1430 was invisible to recovery, causing SE Leader to regenerate T1 as a
    /// duplicate PR #1440. This central allowlist-by-exclusion fixes both directions.
    /// </summary>
    private static readonly string[] NonEngineerBranchSegments =
    {
        "researcher",
        "architect",
        "programmanager",
        "pm",
        "testengineer",
        "test-engineer",
        "tester",
        "executive",
        "custom",
    };

    /// <summary>
    /// Returns <c>true</c> if the given head branch looks like an engineering-task PR
    /// produced by an SE leader, SE worker, or SME engineer role. Returns <c>false</c>
    /// for non-engineer roles (research/pmspec/architecture/etc.) and for branches that
    /// don't follow the <c>agent/{name-or-runId}/{role-slug}/{task-slug}</c> convention.
    /// See <see cref="NonEngineerBranchSegments"/> for the exclusion list and rationale.
    /// </summary>
    /// <param name="headBranch">Head branch name (may be null).</param>
    public static bool IsEngineeringPrBranch(string? headBranch)
    {
        if (string.IsNullOrEmpty(headBranch)) return false;
        if (!headBranch.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var nonEngineerRole in NonEngineerBranchSegments)
        {
            if (headBranch.Contains($"/{nonEngineerRole}/", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private readonly IWorkItemService _workItems;
    private readonly ILogger _logger;

    // In-memory cache refreshed from GitHub on LoadTasksAsync
    private List<EngineeringTask> _cache = new();
    private bool _cacheLoaded;

    // Tasks freshly created via CreateTaskIssuesAsync that may not yet be visible
    // in the GitHub API due to indexing delay. Merged back into _cache on LoadTasksAsync.
    private readonly Dictionary<int, EngineeringTask> _pendingVisibilityTasks = new();

    // Scope filter: when set, LoadTasksAsync only returns tasks whose ParentIssueNumber
    // is in this set. This prevents stale tasks from prior runs from polluting the cache.
    private HashSet<int>? _enhancementScope;

    public EngineeringTaskIssueManager(IWorkItemService workItems, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(logger);
        _workItems = workItems;
        _logger = logger;
    }

    /// <summary>Test-only constructor: creates a manager without a work item service for unit testing cache-only methods.</summary>
    internal EngineeringTaskIssueManager(ILogger logger)
    {
        _workItems = null!;
        _logger = logger;
    }

    /// <summary>
    /// Set the enhancement issue scope for the current run. Only engineering-task issues
    /// whose ParentIssueNumber is in this set will be loaded. Call this before LoadTasksAsync
    /// to filter out stale tasks from previous runs.
    /// </summary>
    public void SetEnhancementScope(IEnumerable<int> enhancementIssueNumbers)
    {
        _enhancementScope = new HashSet<int>(enhancementIssueNumbers);
        _logger.LogInformation("Set enhancement scope filter with {Count} issue numbers", _enhancementScope.Count);
    }

    /// <summary>All tasks (cached). Call <see cref="LoadTasksAsync"/> first.</summary>
    public IReadOnlyList<EngineeringTask> Tasks => _cache;

    // ── Loading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Load all engineering-task issues from GitHub (open + closed) into the in-memory cache.
    /// Call this once at startup and after major state changes.
    /// </summary>
    public async Task LoadTasksAsync(CancellationToken ct = default)
    {
        var openItems = await _workItems.ListByLabelAsync(TaskLabel, "open", ct);
        var closedItems = await _workItems.ListByLabelAsync(TaskLabel, "closed", ct);

        var allTasks = openItems.Concat(closedItems)
            .Select(item => MapIssueToTask(item.ToAgentIssue(), _logger))
            .OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Track ALL issue numbers seen from GitHub (before scope filtering) so we can
        // correctly remove pending-visibility tasks that GitHub now knows about (including
        // closed ones that the scope filter would otherwise exclude).
        var allGitHubIssueNumbers = new HashSet<int>(
            allTasks.Where(t => t.IssueNumber.HasValue).Select(t => t.IssueNumber!.Value));
        // Also track which of those are closed, so we can update pending task status
        var closedIssueNumbers = new HashSet<int>(
            closedItems.Select(i => i.Number));

        // Apply enhancement scope filter to exclude stale tasks from prior runs.
        // Tasks without a ParentIssueNumber (e.g., cross-cutting foundation tasks) are
        // kept — they're not stale, just unscoped. Removing them breaks wave gating
        // and dependency resolution for all downstream tasks.
        if (_enhancementScope is not null && _enhancementScope.Count > 0)
        {
            var before = allTasks.Count;
            allTasks = allTasks
                .Where(t => !t.ParentIssueNumber.HasValue
                    || _enhancementScope.Contains(t.ParentIssueNumber.Value))
                .ToList();
            if (before != allTasks.Count)
                _logger.LogInformation("Filtered {Before} → {After} tasks using enhancement scope ({Excluded} stale tasks excluded)",
                    before, allTasks.Count, before - allTasks.Count);
        }

        // Merge back freshly-created tasks that aren't yet visible in the GitHub API
        // (eventual-consistency delay). Remove from pending set once they appear.
        // Use the pre-filter issue set so closed tasks aren't re-added as pending.
        var loadedIssueNumbers = new HashSet<int>(allTasks.Where(t => t.IssueNumber.HasValue).Select(t => t.IssueNumber!.Value));
        var mergedFromPending = 0;
        foreach (var (issueNum, pendingTask) in _pendingVisibilityTasks)
        {
            // If GitHub knows about this issue (even if scope-filtered), don't re-add
            if (loadedIssueNumbers.Contains(issueNum) || allGitHubIssueNumbers.Contains(issueNum))
                continue;
            allTasks.Add(pendingTask);
            mergedFromPending++;
        }
        // Remove tasks that are now visible from the pending set (use full GitHub set)
        foreach (var issueNum in allGitHubIssueNumbers)
            _pendingVisibilityTasks.Remove(issueNum);
        if (mergedFromPending > 0)
        {
            allTasks = allTasks.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogWarning("Merged {Count} freshly-created tasks not yet visible in GitHub API back into cache",
                mergedFromPending);
        }

        // Detect duplicate task IDs (broken invariant that causes foundation-task misrouting)
        var idGroups = allTasks.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (idGroups.Count > 0)
        {
            foreach (var g in idGroups)
                _logger.LogWarning("Duplicate task ID '{TaskId}' detected across issues: {IssueNumbers}",
                    g.Key, string.Join(", ", g.Select(t => $"#{t.IssueNumber}")));
        }

        _cache = allTasks;
        _cacheLoaded = true;
        _logger.LogInformation("Loaded {Count} engineering tasks from work items ({Open} open, {Closed} closed)",
            _cache.Count, openItems.Count, closedItems.Count);
    }

    /// <summary>Clear the in-memory cache (e.g., when stale tasks from a prior run are detected).</summary>
    public void ClearCache()
    {
        _cache = new();
        _cacheLoaded = false;
        _logger.LogInformation("Cleared engineering task cache");
    }

    /// <summary>True if <see cref="LoadTasksAsync"/> has been called.</summary>
    public bool IsLoaded => _cacheLoaded;

    // ── Task Creation ────────────────────────────────────────────────────

    /// <summary>
    /// Create GitHub issues for a list of engineering tasks generated by the AI.
    /// Links each task as a sub-issue of its parent PM issue using GitHub's Sub-Issues API.
    /// Returns the created tasks with IssueNumber and GitHubId populated.
    /// </summary>
    public async Task<List<EngineeringTask>> CreateTaskIssuesAsync(
        IReadOnlyList<EngineeringTask> tasks, CancellationToken ct = default)
    {
        var created = new List<EngineeringTask>();

        foreach (var task in tasks)
        {
            var body = BuildIssueBody(task);
            var validatedBody = IssueBodyValidator.ValidateAndClean(body, task.Name, _logger);
            if (validatedBody is null)
            {
                _logger.LogWarning("Skipping task {TaskId} — issue body failed validation", task.Id);
                continue;
            }
            var labels = new[] { TaskLabel, $"complexity:{task.Complexity.ToLowerInvariant()}", StatusPending };

            try
            {
                var item = await _workItems.CreateAsync(
                    $"[{task.Id}] {task.Name}", validatedBody, labels, ct);
                var issue = item.ToAgentIssue();

                var updatedTask = task with
                {
                    IssueNumber = issue.Number,
                    GitHubId = issue.GitHubId,
                    IssueUrl = issue.Url,
                    Status = "Pending",
                    Labels = labels.ToList()
                };
                created.Add(updatedTask);
                _cache.Add(updatedTask);
                // Track for merge-back in LoadTasksAsync (API indexing delay)
                _pendingVisibilityTasks[issue.Number] = updatedTask;

                // Link as sub-issue of parent PM enhancement issue
                if (task.ParentIssueNumber.HasValue && issue.GitHubId > 0)
                {
                    await _workItems.AddChildAsync(task.ParentIssueNumber.Value, issue.GitHubId, ct);
                }

                _logger.LogInformation("Created engineering task issue #{Number} for {TaskId}: {Name}",
                    issue.Number, task.Id, task.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create issue for task {TaskId}", task.Id);
            }
        }

        return created;
    }

    /// <summary>
    /// Create blocked-by dependency links between engineering tasks using GitHub's Dependencies API.
    /// Call after CreateTaskIssuesAsync and after resolving dependency issue numbers.
    /// </summary>
    public async Task LinkTaskDependenciesAsync(IReadOnlyList<EngineeringTask> tasks, CancellationToken ct = default)
    {
        // Build map from issue number → GitHubId for dependency resolution
        var issueNumToGitHubId = _cache
            .Where(t => t.IssueNumber.HasValue && t.GitHubId.HasValue)
            .ToDictionary(t => t.IssueNumber!.Value, t => t.GitHubId!.Value);

        foreach (var task in tasks)
        {
            if (!task.IssueNumber.HasValue || task.DependencyIssueNumbers.Count == 0)
                continue;

            foreach (var depIssueNum in task.DependencyIssueNumbers)
            {
                if (issueNumToGitHubId.TryGetValue(depIssueNum, out var blockingGitHubId))
                {
                    await _workItems.AddDependencyAsync(task.IssueNumber.Value, blockingGitHubId, ct);
                }
            }
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────

    /// <summary>Find the next assignable task matching complexity preferences with met dependencies.</summary>
    public EngineeringTask? FindNextAssignableTask(params string[] complexityPreferences)
    {
        foreach (var complexity in complexityPreferences)
        {
            var task = _cache.FirstOrDefault(t =>
                string.Equals(t.Complexity, complexity, StringComparison.OrdinalIgnoreCase)
                && t.Status == "Pending"
                && IsWaveEligible(t)
                && AreDependenciesMet(t));
            if (task is not null)
                return task;
        }
        return null;
    }

    /// <summary>Check if all dependency issues for a task are closed (Done).</summary>
    public bool AreDependenciesMet(EngineeringTask task, bool useRelaxation = false, HashSet<string>? sharedFiles = null)
    {
        if (task.DependencyIssueNumbers.Count == 0)
            return true;

        return task.DependencyIssueNumbers.All(depIssueNum =>
        {
            var dep = _cache.FirstOrDefault(t => t.IssueNumber == depIssueNum);
            if (dep is null || IsTaskDone(dep))
                return true;

            // If relaxation is enabled, check typed dependencies
            if (useRelaxation && task.DependencyTypes.Count > 0)
            {
                // Find the task ID for this dependency issue number
                var depTaskId = dep.Id;
                if (task.DependencyTypes.TryGetValue(depTaskId, out var depType))
                {
                    return SoftwareEngineerAgent.CanRelaxDependency(
                        depType, dep, sharedFiles ?? new HashSet<string>());
                }
            }

            return false;
        });
    }

    /// <summary>Check if ALL engineering tasks are done.</summary>
    public bool AreAllTasksDone() => _cache.Count > 0 && _cache.All(IsTaskDone);

    /// <summary>
    /// Checks wave-level ordering: a task is wave-eligible only when all tasks in earlier
    /// waves are DONE (PR merged and issue closed). This prevents later-wave tasks from
    /// branching off stale main before earlier-wave PRs have merged, which causes
    /// guaranteed merge conflicts on any shared files.
    ///
    /// 2026-05-16 fix: Previously used IsTaskPastImplementation (Done OR ImplementationComplete),
    /// which released wave N+1 as soon as wave N PRs were PUSHED — before they merged into main.
    /// Later-wave engineers then branched from stale main, causing PRs #1829/#1831/#1832 to
    /// conflict when the earlier PRs finally merged. Now requires IsTaskDone (PR merged).
    ///
    /// Deadlock prevention: also accepts IsTaskPastImplementation as a fallback for tasks
    /// that have been implementation-complete for &gt;30 minutes (merge is stuck, not pending).
    /// This prevents a failed/slow merge from blocking all subsequent waves indefinitely.
    /// </summary>
    public bool IsWaveEligible(EngineeringTask task)
    {
        if (string.IsNullOrEmpty(task.Wave))
            return true;

        var taskWave = ParseWaveNumber(task.Wave);
        if (taskWave <= 0)
            return true; // W0 tasks are always eligible

        // All non-integration tasks in earlier waves must be DONE (PR merged, issue closed)
        // OR implementation-complete for >30 min (deadlock prevention for stuck merges)
        return !_cache.Any(t =>
            t.Id != task.Id
            && !string.IsNullOrEmpty(t.Wave)
            && ParseWaveNumber(t.Wave) < taskWave
            && !IsTaskDone(t)
            && !IsImplementationCompleteWithGracePeriod(t)
            && !string.Equals(t.Id, "T-FINAL", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if a task is implementation-complete AND has been in that state for
    /// longer than the grace period (30 min). This is the deadlock-prevention fallback:
    /// if a PR is stuck in review/merge for &gt;30 min, later waves should proceed rather
    /// than waiting indefinitely. The pre-publish rebase (SyncBranchWithMainAsync) will
    /// handle any resulting conflicts.
    /// </summary>
    private bool IsImplementationCompleteWithGracePeriod(EngineeringTask task)
    {
        if (!IsImplementationComplete(task)) return false;
        // Track when we first saw this task as implementation-complete
        if (_implCompleteTimestamps.TryAdd(task.Id, DateTime.UtcNow))
            return false; // Just noticed — grace period starts now
        return (DateTime.UtcNow - _implCompleteTimestamps[task.Id]).TotalMinutes > 30;
    }

    private readonly Dictionary<string, DateTime> _implCompleteTimestamps = new();

    private static int ParseWaveNumber(string? wave)
    {
        if (string.IsNullOrEmpty(wave)) return 0;
        if (wave.StartsWith('W') && int.TryParse(wave.AsSpan(1), out var num))
            return num;
        return 0;
    }

    public int PendingCount => _cache.Count(t => t.Status == "Pending");
    public int InProgressCount => _cache.Count(t => t.Status is "Assigned" or "InProgress");
    public int DoneCount => _cache.Count(IsTaskDone);
    public int TotalCount => _cache.Count;

    /// <summary>
    /// Generate the next collision-safe task ID by scanning all known tasks.
    /// Returns IDs like "T8", "T9", etc. Thread-safe within single-threaded agent loop.
    /// </summary>
    public string NextAvailableTaskId()
    {
        var maxNum = _cache
            .Where(t => t.Id is not null &&
                        t.Id.StartsWith("T", StringComparison.OrdinalIgnoreCase) &&
                        !t.Id.StartsWith("T-", StringComparison.OrdinalIgnoreCase))
            .Select(t => int.TryParse(t.Id.AsSpan(1), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        // Also check pending-visibility tasks (freshly created, may not be in _cache after reload)
        foreach (var (_, pt) in _pendingVisibilityTasks)
        {
            if (pt.Id?.StartsWith("T", StringComparison.OrdinalIgnoreCase) == true &&
                !pt.Id.StartsWith("T-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pt.Id.AsSpan(1), out var pn) && pn > maxNum)
                maxNum = pn;
        }
        return $"T{maxNum + 1}";
    }

    /// <summary>Test-only: seed the in-memory cache directly without GitHub calls.</summary>
    internal void SeedCacheForTesting(IEnumerable<EngineeringTask> tasks)
    {
        _cache = tasks.ToList();
        _cacheLoaded = true;
    }

    public static bool IsTaskDone(EngineeringTask task) =>
        string.Equals(task.Status, "Done", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(task.Status, "Complete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(task.Status, "closed", StringComparison.OrdinalIgnoreCase);

    // ── Status Transitions ───────────────────────────────────────────────

    /// <summary>Assign a task to an engineer: update title, set status:assigned label.</summary>
    public async Task AssignTaskAsync(int issueNumber, string engineerName, CancellationToken ct = default)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];
        var newTitle = $"{engineerName}: {task.Name}";
        var newLabels = ReplaceStatusLabel(task.Labels, StatusAssigned);

        await _workItems.UpdateAsync(issueNumber, title: newTitle, labels: newLabels, ct: ct);

        _cache[idx] = task with { Status = "Assigned", AssignedTo = engineerName };
        _logger.LogInformation("Assigned task issue #{Number} ({Name}) to {Engineer}",
            issueNumber, task.Name, engineerName);
    }

    /// <summary>Mark a task in-progress and record its PR number in a comment.</summary>
    public async Task MarkInProgressAsync(int issueNumber, int prNumber, CancellationToken ct = default)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];
        var newLabels = ReplaceStatusLabel(task.Labels, StatusInProgress);

        await _workItems.UpdateAsync(issueNumber, labels: newLabels, ct: ct);
        await _workItems.AddCommentAsync(issueNumber, $"PR #{prNumber} created for this task.", ct);

        _cache[idx] = task with { Status = "InProgress", PullRequestNumber = prNumber };
    }

    /// <summary>Mark a task Done by closing the issue.</summary>
    public async Task MarkDoneAsync(int issueNumber, int? prNumber = null, CancellationToken ct = default)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];
        try
        {
            await _workItems.CloseAsync(issueNumber, ct);
        }
        catch (Exception ex)
        {
            // Issue may already be closed (e.g., via "Closes #N" in PR merge) — that's fine
            _logger.LogWarning(ex, "CloseIssueAsync failed for #{Number} (may already be closed), marking done in cache anyway", issueNumber);
        }

        _cache[idx] = task with
        {
            Status = "Done",
            PullRequestNumber = prNumber ?? task.PullRequestNumber
        };

        _logger.LogInformation("Marked task issue #{Number} ({Name}) as Done",
            issueNumber, task.Name);
    }

    /// <summary>
    /// Mark a task as blocked on the platform. The issue stays open and is not counted as Done,
    /// so later waves and final integration remain blocked until an operator resolves it.
    /// </summary>
    public async Task MarkBlockedAsync(int issueNumber, string reason, CancellationToken ct = default)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];
        if (IsTaskDone(task)) return;

        var newLabels = ReplaceStatusLabel(task.Labels, StatusBlocked);

        await _workItems.UpdateAsync(issueNumber, labels: newLabels, ct: ct);
        await _workItems.AddCommentAsync(issueNumber, reason, ct);

        _cache[idx] = task with
        {
            Status = "Blocked",
            Labels = newLabels.ToList()
        };

        _logger.LogWarning("Marked task issue #{Number} ({Name}) as Blocked: {Reason}",
            issueNumber, task.Name, reason);
    }

    /// <summary>
    /// Mark a task as implementation-complete: PR is ready for review but not yet merged.
    /// Keeps the issue OPEN (does not close it) so wave gating remains enforced.
    /// This status prevents re-development on restart while allowing the wave gate
    /// to block later-wave tasks until this task's PR is actually merged.
    /// </summary>
    public async Task MarkImplementationCompleteAsync(int issueNumber, int? prNumber = null, CancellationToken ct = default)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];
        // Already in a terminal state — don't regress
        if (IsTaskDone(task)) return;

        var newLabels = ReplaceStatusLabel(task.Labels, StatusImplementationComplete);

        // Keep issue OPEN but update label to signal "don't re-develop"
        await _workItems.UpdateAsync(issueNumber, labels: newLabels, ct: ct);

        _cache[idx] = task with
        {
            Status = "ImplementationComplete",
            Labels = newLabels.ToList(),
            PullRequestNumber = prNumber ?? task.PullRequestNumber
        };

        _logger.LogInformation("Marked task issue #{Number} ({Name}) as ImplementationComplete (PR ready, awaiting merge)",
            issueNumber, task.Name);
    }

    /// <summary>True if a task's implementation is done (PR ready/approved) but may not be merged yet.</summary>
    public static bool IsImplementationComplete(EngineeringTask task) =>
        string.Equals(task.Status, "ImplementationComplete", StringComparison.OrdinalIgnoreCase);

    public static bool IsTaskBlocked(EngineeringTask task) =>
        string.Equals(task.Status, "Blocked", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if a task should NOT be re-developed (either implementation complete or fully done/merged).
    /// Use for "skip this task" checks. Do NOT use for wave gating — use IsTaskDone for that.
    /// </summary>
    public static bool IsTaskPastImplementation(EngineeringTask task) =>
        IsTaskDone(task) || IsImplementationComplete(task);

    /// <summary>
    /// Reset a task to Pending (e.g., after a failed PR close-and-recreate).
    /// </summary>
    /// <param name="allowReopen">
    /// If false (default), refuses to reopen a closed issue and instead drops it from the
    /// local task cache. Only the orphan-without-implementation recovery path should pass
    /// true. Live evidence 2026-05-12 22:55: agent recovery was unconditionally setting
    /// state="open" on closed issues, causing duplicate work after operator-managed
    /// cleanup. Closed = intentional terminal state unless explicitly stated otherwise.
    /// Filed as todo flowmonitor-detect-issue-reopen-from-recovery.
    /// </param>
    public async Task ResetToPendingAsync(int issueNumber, CancellationToken ct = default, bool allowReopen = false)
    {
        var idx = _cache.FindIndex(t => t.IssueNumber == issueNumber);
        if (idx < 0) return;

        var task = _cache[idx];

        // Pre-check: if the issue is currently closed AND the caller didn't explicitly
        // opt in to reopen, refuse and remove from cache so recovery loops don't keep
        // re-trying. Refusing avoids the reopen-on-recovery regression that wasted ~$1
        // of LLM budget on duplicate scaffolding work in the 2026-05-12 22:55 incident.
        if (!allowReopen)
        {
            try
            {
                var currentIssue = await _workItems.GetAsync(issueNumber, ct);
                if (currentIssue is not null &&
                    string.Equals(currentIssue.State, "closed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "ResetToPendingAsync REFUSED for issue #{Number} ({Name}): issue is CLOSED. " +
                        "Not reopening — closed issues are intentional terminal state. Removing from " +
                        "local task cache so recovery does not re-attempt this task. " +
                        "Pass allowReopen:true ONLY for orphan-recovery (closed-without-implementation) paths.",
                        issueNumber, task.Name);
                    _cache.RemoveAt(idx);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "ResetToPendingAsync pre-check could not fetch issue #{Number} state — proceeding with reset",
                    issueNumber);
            }
        }

        var newLabels = ReplaceStatusLabel(task.Labels, StatusPending);

        // Strip agent prefix from title back to just task name
        var cleanTitle = $"[{task.Id}] {task.Name}";
        await _workItems.UpdateAsync(issueNumber, title: cleanTitle, labels: newLabels, state: "open", ct: ct);

        _cache[idx] = task with
        {
            Status = "Pending",
            AssignedTo = null,
            PullRequestNumber = null
        };

        _logger.LogInformation(
            "Reset task issue #{Number} ({Name}) to Pending{Reopen}",
            issueNumber, task.Name, allowReopen ? " (allowReopen=true, may have reopened a closed issue)" : "");
    }

    /// <summary>Find a task by its issue number.</summary>
    public EngineeringTask? FindByIssueNumber(int issueNumber) =>
        _cache.FirstOrDefault(t => t.IssueNumber == issueNumber);

    /// <summary>Find a task by its task ID (e.g., "T1").</summary>
    public EngineeringTask? FindById(string taskId) =>
        _cache.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find a task by name (for matching from PR titles).</summary>
    public EngineeringTask? FindByName(string name) =>
        _cache.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find the first non-done task assigned to a specific engineer.
    /// Used by worker PEs to discover tasks assigned to them by the leader.
    /// </summary>
    public EngineeringTask? FindAssignedTo(string engineerName) =>
        _cache.FirstOrDefault(t =>
            !IsTaskPastImplementation(t)
            && t.Status is "Assigned" or "InProgress"
            && string.Equals(t.AssignedTo, engineerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Close any remaining open engineering task issues. Called during pipeline completion
    /// to ensure no task issues are left open (catches edge cases where MarkDoneAsync
    /// wasn't called or GitHub auto-close via "Closes #N" didn't fire).
    /// </summary>
    public async Task CloseAllRemainingTaskIssuesAsync(CancellationToken ct = default)
    {
        var remaining = _cache.Where(t =>
            !string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase) &&
            t.IssueNumber.HasValue).ToList();

        foreach (var task in remaining)
        {
            try
            {
                await _workItems.CloseAsync(task.IssueNumber!.Value, ct);
                await _workItems.AddCommentAsync(task.IssueNumber!.Value,
                    "✅ Closing — engineering pipeline complete. All tasks delivered.", ct);
                var idx = _cache.FindIndex(t => t.IssueNumber == task.IssueNumber);
                if (idx >= 0) _cache[idx] = task with { Status = "Done" };
                _logger.LogInformation("Closed remaining task issue #{Number} ({Name}) during pipeline completion",
                    task.IssueNumber, task.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close task issue #{Number} during cleanup", task.IssueNumber);
            }
        }
    }

    // ── Issue Body Parsing & Building ────────────────────────────────────

    private static string BuildIssueBody(EngineeringTask task)
    {
        return BuildIssueBodyWithDeps(task, task.DependencyIssueNumbers);
    }

    internal static string BuildIssueBodyWithDeps(EngineeringTask task, List<int> depIssueNumbers)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {task.Name}");
        sb.AppendLine();
        sb.AppendLine(task.Description);
        sb.AppendLine();
        sb.AppendLine("## Metadata");
        sb.AppendLine($"- **Task ID:** {task.Id}");
        sb.AppendLine($"- **Complexity:** {task.Complexity}");
        sb.AppendLine($"- **Wave:** {task.Wave}");

        if (task.ParentIssueNumber.HasValue)
            sb.AppendLine($"- **Parent Issue:** #{task.ParentIssueNumber}");

        if (depIssueNumbers.Count > 0)
            sb.AppendLine($"- **Depends On:** {string.Join(", ", depIssueNumbers.Select(n => $"#{n}"))}");

        // Serialize typed dependencies for round-trip persistence
        if (task.DependencyTypes.Count > 0)
        {
            var typedDeps = string.Join(", ", task.DependencyTypes.Select(kv => $"{kv.Key}({kv.Value})"));
            sb.AppendLine($"- **Dependency Types:** {typedDeps}");
        }

        if (task.OwnedFiles.Count > 0)
            sb.AppendLine($"- **Owned Files:** {string.Join(", ", task.OwnedFiles)}");

        sb.AppendLine();
        sb.AppendLine("_Created by Software Engineer._");
        return sb.ToString();
    }

    internal static EngineeringTask MapIssueToTask(AgentIssue issue, ILogger? logger = null)
    {
        // Normalize body: ADO stores HTML, but parsers expect markdown-style text.
        // Strip HTML tags so regex patterns like **Parent Issue:** #N work on both platforms.
        var normalizedBody = NormalizeHtmlBody(issue.Body);

        var taskId = ParseTaskId(issue.Title) ?? $"T-{issue.Number}";
        var taskName = ParseTaskName(issue.Title) ?? issue.Title;
        var complexity = ParseComplexityFromLabels(issue.Labels);
        var status = issue.State.Equals("closed", StringComparison.OrdinalIgnoreCase)
            ? "Done"
            : ParseStatusFromLabels(issue.Labels);
        var assignedTo = ParseAssignedAgent(issue.Title);
        var deps = ParseDependencies(normalizedBody, logger);
        var parentIssue = ParseParentIssue(normalizedBody);
        var wave = ParseWave(normalizedBody);
        var depTypes = ParseDependencyTypes(normalizedBody);
        var ownedFiles = ParseOwnedFiles(normalizedBody);

        return new EngineeringTask
        {
            Id = taskId,
            Name = taskName,
            Description = ParseDescription(normalizedBody),
            Complexity = complexity,
            Status = status,
            AssignedTo = assignedTo,
            IssueNumber = issue.Number,
            GitHubId = issue.GitHubId > 0 ? issue.GitHubId : null,
            IssueUrl = issue.Url,
            DependencyIssueNumbers = deps,
            ParentIssueNumber = parentIssue,
            Labels = issue.Labels.ToList(),
            Wave = wave,
            DependencyTypes = depTypes,
            OwnedFiles = ownedFiles
        };
    }

    // ── Parsers ──────────────────────────────────────────────────────────

    /// <summary>Parse "[T1] Task name" → "T1"</summary>
    internal static string? ParseTaskId(string title)
    {
        var match = TaskIdPattern().Match(title);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parse "[T1] Task name" or "Agent: Task name" → "Task name"</summary>
    internal static string? ParseTaskName(string title)
    {
        // Try "[T1] Name" format first
        var match = TaskIdPattern().Match(title);
        if (match.Success)
        {
            var afterBracket = title[(match.Index + match.Length)..].Trim();
            // May still have agent prefix: "Agent: Name"
            var agentMatch = AgentPrefixPattern().Match(afterBracket);
            return agentMatch.Success ? agentMatch.Groups[1].Value.Trim() : afterBracket;
        }

        // Try "Agent: Name" format
        var agentMatch2 = AgentPrefixPattern().Match(title);
        return agentMatch2.Success ? agentMatch2.Groups[1].Value.Trim() : null;
    }

    /// <summary>Parse agent name from "[T1] Agent: Name" or "Agent: Name"</summary>
    internal static string? ParseAssignedAgent(string title)
    {
        // Strip task ID prefix if present
        var match = TaskIdPattern().Match(title);
        var remaining = match.Success ? title[(match.Index + match.Length)..].Trim() : title;

        // Look for "Agent: TaskName" pattern
        var agentMatch = AgentPrefixPattern().Match(remaining);
        if (agentMatch.Success)
        {
            var agentName = remaining[..agentMatch.Index].Trim();
            if (agentName.EndsWith(':'))
                agentName = agentName[..^1].Trim();
            // The pattern captures what's after the colon; the agent name is before it
            var colonIdx = remaining.IndexOf(':');
            if (colonIdx > 0)
                return remaining[..colonIdx].Trim();
        }
        return null;
    }

    internal static string ParseComplexityFromLabels(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            if (label.StartsWith("complexity:", StringComparison.OrdinalIgnoreCase))
                return label["complexity:".Length..] switch
                {
                    "high" => "High",
                    "medium" => "Medium",
                    "low" => "Low",
                    var x => char.ToUpperInvariant(x[0]) + x[1..]
                };
        }
        return "Medium";
    }

    internal static string ParseStatusFromLabels(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            if (string.Equals(label, StatusImplementationComplete, StringComparison.OrdinalIgnoreCase))
                return "ImplementationComplete";
            if (string.Equals(label, StatusBlocked, StringComparison.OrdinalIgnoreCase))
                return "Blocked";
            if (string.Equals(label, StatusAssigned, StringComparison.OrdinalIgnoreCase))
                return "Assigned";
            if (string.Equals(label, StatusInProgress, StringComparison.OrdinalIgnoreCase))
                return "InProgress";
            if (string.Equals(label, StatusPending, StringComparison.OrdinalIgnoreCase))
                return "Pending";
        }
        return "Pending";
    }

    internal static List<int> ParseDependencies(string? body, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new();

        // Try all matches — pick the first that contains at least one issue number.
        // Using Matches() (not Match()) guards against a false positive match on prose text
        // (e.g. "this task depends on auth" before the actual metadata section).
        foreach (var m in DependsOnPattern().Matches(body).Cast<Match>())
        {
            var nums = IssueNumberPattern().Matches(m.Groups[1].Value)
                .Select(n => int.Parse(n.Groups[1].Value))
                .ToList();
            if (nums.Count > 0)
                return nums;
        }

        // Fallback: markdown list format — "Depends On:\n- #N\n- #M"
        // Collects #N references from list-item lines immediately following the header.
        var headerMatch = DependsOnHeaderPattern().Match(body);
        if (headerMatch.Success)
        {
            var rest = body[(headerMatch.Index + headerMatch.Length)..];
            var listDeps = new List<int>();
            foreach (var line in rest.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith('-') && !trimmed.StartsWith('*'))
                    break;
                listDeps.AddRange(IssueNumberPattern().Matches(trimmed)
                    .Select(n => int.Parse(n.Groups[1].Value)));
            }
            if (listDeps.Count > 0)
                return listDeps;
        }

        // Defensive: warn if body mentions "depends on" but nothing was parsed —
        // catches future regex regressions where the format changes but the pattern doesn't.
        if (logger is not null && body.Contains("depends on", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning(
                "ParseDependencies returned empty for body containing 'depends on' — possible regex format mismatch. Excerpt: {Excerpt}",
                body.Length > 300 ? body[..300] : body);

        return new();
    }

    internal static int? ParseParentIssue(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var match = ParentIssuePattern().Match(body);
        // Fallback: plain "Parent Issue:" without bold markers
        if (!match.Success)
            match = ParentIssuePatternPlain().Match(body);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    internal static string ParseDescription(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        // Extract text between the first heading and "## Metadata"
        var lines = body.Split('\n');
        var desc = new System.Text.StringBuilder();
        bool pastFirstHeading = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("## Metadata"))
                break;
            if (line.TrimStart().StartsWith("## ") && !pastFirstHeading)
            {
                pastFirstHeading = true;
                continue;
            }
            if (pastFirstHeading)
                desc.AppendLine(line);
        }

        return desc.ToString().Trim();
    }

    /// <summary>Parse "- **Wave:** W1" from issue body metadata. Default "W1" if not found.</summary>
    internal static string ParseWave(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "W1";
        var match = WavePattern().Match(body);
        if (match.Success)
            return match.Groups[1].Value.Trim();
        // Fallback: plain "Wave: W1" (no bold) — handles edge cases where HTML stripping lost markers
        var fallback = WavePatternPlain().Match(body);
        return fallback.Success ? fallback.Groups[1].Value.Trim() : "W1";
    }

    /// <summary>Parse "- **Dependency Types:** T1(files), T3(api)" from issue body metadata.</summary>
    internal static Dictionary<string, string> ParseDependencyTypes(string? body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
            return result;

        var match = DependencyTypesPattern().Match(body);
        if (!match.Success) return result;

        // Parse "T1(files), T3(api)" format
        var entries = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var parenStart = entry.IndexOf('(');
            if (parenStart > 0 && entry.EndsWith(')'))
            {
                var taskId = entry[..parenStart].Trim();
                var depType = entry[(parenStart + 1)..^1].Trim().ToLowerInvariant();
                result[taskId] = depType;
            }
        }
        return result;
    }

    /// <summary>Parse "- **Owned Files:** file1, file2" from issue body metadata.</summary>
    internal static List<string> ParseOwnedFiles(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new();

        var match = OwnedFilesPattern().Match(body);
        if (!match.Success) return new();

        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();
    }

    // ── HTML → text normalization ──────────────────────────────────────
    /// <summary>
    /// Convert HTML body (from ADO) back to markdown-like text so regex parsers work.
    /// Plain markdown text passes through unchanged.
    /// </summary>
    internal static string NormalizeHtmlBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        // If not HTML, return as-is
        if (!body.Contains('<')) return body;

        var text = body;
        // Convert <strong> → **
        text = Regex.Replace(text, @"<strong>(.*?)</strong>", "**$1**");
        // Convert <b> → ** (ADO sometimes uses <b> instead of <strong>)
        text = Regex.Replace(text, @"<b>(.*?)</b>", "**$1**");
        // Convert <em> → *
        text = Regex.Replace(text, @"<em>(.*?)</em>", "*$1*");
        // Convert <h2 ...> → ## 
        text = Regex.Replace(text, @"<h2[^>]*>(.*?)</h2>", "## $1");
        // Convert <h3 ...> → ### 
        text = Regex.Replace(text, @"<h3[^>]*>(.*?)</h3>", "### $1");
        // Convert <li> items → - prefixed lines
        text = Regex.Replace(text, @"<li>(.*?)</li>", "- $1");
        // Strip remaining HTML tags
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Collapse multiple blank lines
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string[] ReplaceStatusLabel(List<string> currentLabels, string newStatus)
    {
        var result = currentLabels
            .Where(l => !l.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            .Append(newStatus)
            .ToArray();
        return result;
    }

    // ── Regex patterns ───────────────────────────────────────────────────

    [GeneratedRegex(@"\[([A-Z0-9\-]+)\]\s*")]
    private static partial Regex TaskIdPattern();

    [GeneratedRegex(@"^.+?:\s*(.+)$")]
    private static partial Regex AgentPrefixPattern();

    // Unified "Depends On" pattern: handles all markdown decoration variants:
    //   **Depends On:** #N   (colon inside bold — standard generated format)
    //   **Depends On**: #N   (colon outside bold — manually-edited variant)
    //   *Depends On:* #N     (italic)
    //   __Depends On:__ #N   (underscore bold)
    //   Depends On: #N       (plain, after a list dash that the plain fallback catches)
    // Uses Matches() at the call site so prose "depends on" is harmless (no #N → skipped).
    [GeneratedRegex(@"(?:\*{1,2}|_{1,2})?Depends\s+On:?[ \t]*(?:\*{1,2}|_{1,2})?[ \t]*([^\n\r]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DependsOnPattern();

    // Header-only marker for multi-line list format: "Depends On:\n- #N\n- #M"
    [GeneratedRegex(@"(?:\*{1,2}|_{1,2})?Depends\s+On:?\s*(?:\*{1,2}|_{1,2})?[ \t]*\r?\n", RegexOptions.IgnoreCase)]
    private static partial Regex DependsOnHeaderPattern();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex IssueNumberPattern();

    [GeneratedRegex(@"\*\*Parent Issue:\*\*\s*#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ParentIssuePattern();

    [GeneratedRegex(@"(?:^|-)\s*Parent Issue:\s*#(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ParentIssuePatternPlain();

    [GeneratedRegex(@"\*\*Wave:\*\*\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex WavePattern();

    [GeneratedRegex(@"(?:^|-)\s*Wave:\s*(W\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WavePatternPlain();

    [GeneratedRegex(@"\*\*Dependency Types:\*\*\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex DependencyTypesPattern();

    [GeneratedRegex(@"\*\*Owned Files:\*\*\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedFilesPattern();
}
