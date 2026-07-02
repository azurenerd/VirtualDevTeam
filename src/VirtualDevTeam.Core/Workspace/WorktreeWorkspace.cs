using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Worktree-based workspace for Worktree and InPlace modes.
/// Uses <see cref="SharedCloneManager"/> to create lightweight git worktrees
/// that share a single .git object store. Much faster than full clones for
/// large repositories (~seconds vs minutes).
/// </summary>
public class WorktreeWorkspace : IAgentWorkspace
{
    private readonly SharedCloneManager _sharedCloneManager;
    private readonly string _agentSlug;
    private readonly string _defaultBranch;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gitLock = new(1, 1);
    private readonly IReadOnlyList<string>? _sparsePaths;
    private readonly PushFailureTracker? _pushFailureTracker;
    private readonly string? _agentPushRemote;
    private string _worktreePath = "";
    private string _branchName;
    private bool _initialized;

    /// <inheritdoc/>
    public string RepoPath => _worktreePath;

    /// <inheritdoc/>
    public WorkspaceMode Mode { get; }

    public WorktreeWorkspace(
        SharedCloneManager sharedCloneManager,
        string agentSlug,
        string defaultBranch,
        WorkspaceMode mode,
        IReadOnlyList<string>? sparsePaths,
        ILogger logger,
        PushFailureTracker? pushFailureTracker = null,
        string? agentPushRemote = null)
    {
        ArgumentNullException.ThrowIfNull(sharedCloneManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSlug);

        _sharedCloneManager = sharedCloneManager;
        _agentSlug = agentSlug;
        _defaultBranch = defaultBranch;
        Mode = mode;
        _sparsePaths = sparsePaths;
        _logger = logger;
        _pushFailureTracker = pushFailureTracker;
        _agentPushRemote = agentPushRemote;
        _branchName = $"vdt/{agentSlug}";
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _sharedCloneManager.EnsureReadyAsync(ct);
        _worktreePath = await _sharedCloneManager.CreateWorktreeAsync(
            _branchName, _agentSlug, _sparsePaths, ct);
        _initialized = true;

        _logger.LogInformation(
            "WorktreeWorkspace initialized for {Agent} at {Path} (mode: {Mode})",
            _agentSlug, _worktreePath, Mode);
    }

    /// <inheritdoc/>
    public async Task SyncWithMainAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // Abort any stale rebase/merge state from a prior crash
            await AbortInProgressOperationsAsync(ct);
            // Clean untracked files that could conflict with rebase (build artifacts
            // that exist on the target branch but are untracked on the current branch).
            await RunGitAsync("clean -fd", ct, throwOnError: false);
            await RunGitAsync($"fetch origin {_defaultBranch}", ct);
            await RunGitAsync($"rebase origin/{_defaultBranch}", ct);
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task CreateBranchAsync(string branchName, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // Use -B (force) instead of -b to handle stale branches from
            // previously closed-and-recreated PRs. Without this, the agent gets
            // "fatal: a branch named '...' already exists" and falls back to
            // legacy code-gen, wasting the strategy framework.
            await RunGitAsync($"checkout -B {branchName}", ct);
            _branchName = branchName;
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task CheckoutBranchAsync(string branchName, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // Abort any stuck rebase/merge from prior failed ops
            await RunGitAsync("rebase --abort", ct, throwOnError: false);
            await RunGitAsync("merge --abort", ct, throwOnError: false);
            // Clean dirty state — reset tracked files and remove untracked files
            // that would block checkout (e.g., build artifacts like data.json, package-lock.json
            // that exist on the target branch but are untracked on the current branch).
            await RunGitAsync("reset --hard HEAD", ct, throwOnError: false);
            await RunGitAsync("clean -fd", ct, throwOnError: false);
            // Fetch the branch from remote (may have been created via API)
            await RunGitAsync($"fetch origin {branchName}", ct, throwOnError: false);

            // Try normal checkout first; if the branch is occupied by another worktree,
            // fall back to detached HEAD on the remote ref (avoids one-branch-per-worktree conflict).
            try
            {
                await RunGitAsync($"checkout {branchName}", ct);
            }
            catch
            {
                _logger.LogInformation(
                    "Branch {Branch} occupied by another worktree — using detached HEAD on origin/{Branch}",
                    branchName, branchName);
                await RunGitAsync($"checkout --detach origin/{branchName}", ct);
            }

            // Reset to remote HEAD to pick up API-committed tracking markers
            await RunGitAsync($"reset --hard origin/{branchName}", ct, throwOnError: false);
            _branchName = branchName;
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task<bool> MergeMainIntoBranchAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync($"fetch origin {_defaultBranch}", ct);
            var result = await RunGitAsync($"merge origin/{_defaultBranch}", ct, throwOnError: false);
            if (result.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))
            {
                await RunGitAsync("merge --abort", ct, throwOnError: false);
                return false;
            }
            return true;
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task WriteFileAsync(string relativePath, string content, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_worktreePath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(fullPath, content, ct);
    }

    /// <inheritdoc/>
    public async Task<string> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_worktreePath, relativePath);
        return await File.ReadAllTextAsync(fullPath, ct);
    }

    /// <inheritdoc/>
    public async Task CommitAsync(string message, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("add -A", ct);
            await RunGitAsync($"commit -m \"{message.Replace("\"", "\\\"")}\"", ct, throwOnError: false);
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task PushAsync(string branchName, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            var remote = !string.IsNullOrWhiteSpace(_agentPushRemote) ? _agentPushRemote : "origin";

            // Safety guard: if using "origin" (no redirect), verify it's not a real platform
            // host when we should be pushing to a local bare repo. This prevents agent/*
            // branches from leaking to GitHub/ADO in Local mode (lesson #155).
            if (remote == "origin" && string.IsNullOrWhiteSpace(_agentPushRemote))
            {
                var originUrl = await GetOriginUrlAsync(ct);
                if (!string.IsNullOrEmpty(originUrl) &&
                    (originUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
                     originUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogError(
                        "REFUSING agent push to remote platform ({Origin}) — AgentPushRemote is not set. " +
                        "In Local mode, agent pushes must target the local bare repo, not the platform remote. " +
                        "Check DevelopSettingsService.MergeIntoConfig AgentPushRemote logic.",
                        originUrl);
                    throw new InvalidOperationException(
                        $"Agent push blocked: AgentPushRemote not configured but origin points to platform ({originUrl}). " +
                        "This would leak agent branches to GitHub/ADO.");
                }
            }

            // Fetch first — server-side API commits (tracking markers) may have
            // advanced the remote branch beyond our local state.
            await RunGitAsync($"fetch {remote} {branchName}", ct, throwOnError: false);

            // Use refspec push (HEAD:refs/heads/{branch}) which works regardless of
            // whether we're on a named branch or detached HEAD. This avoids the
            // "src refspec does not match" error when detached.
            var pushRef = $"HEAD:refs/heads/{branchName}";

            try
            {
                await RunGitAsync($"push {remote} {pushRef}", ct);
            }
            catch
            {
                // Push rejected (likely "fetch first" from API-committed tracking markers).
                // Rebase onto remote and retry once.
                _logger.LogWarning("Push failed for {Branch}, rebasing and retrying", branchName);
                await RunGitAsync($"pull --rebase {remote} {branchName}", ct, throwOnError: false);
                try
                {
                    await RunGitAsync($"push {remote} {pushRef}", ct);
                }
                catch (Exception retryEx)
                {
                    // Record repeated push failure for FlowMonitor detection
                    _pushFailureTracker?.RecordFailure(
                        _agentSlug, null, branchName,
                        retryEx.Message);
                    throw;
                }
            }
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task ForcePushAsync(string branchName, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            var remote = !string.IsNullOrWhiteSpace(_agentPushRemote) ? _agentPushRemote : "origin";

            // Same safety guard as PushAsync — prevent leaking to platform in Local mode
            if (remote == "origin" && string.IsNullOrWhiteSpace(_agentPushRemote))
            {
                var originUrl = await GetOriginUrlAsync(ct);
                if (!string.IsNullOrEmpty(originUrl) &&
                    (originUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
                     originUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogError("REFUSING agent force-push to remote platform — AgentPushRemote not set");
                    throw new InvalidOperationException(
                        $"Agent force-push blocked: origin points to platform ({originUrl}) but AgentPushRemote not configured.");
                }
            }

            await RunGitAsync($"push --force-with-lease {remote} HEAD:refs/heads/{branchName}", ct);
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task<bool> PullRebaseAsync(string branchName, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            var result = await RunGitAsync($"pull --rebase origin {branchName}", ct, throwOnError: false);
            if (result.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))
            {
                await RunGitAsync("rebase --abort", ct, throwOnError: false);
                return false;
            }
            return true;
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        return (await RunGitAsync("rev-parse --abbrev-ref HEAD", ct)).Trim();
    }

    /// <inheritdoc/>
    public async Task<string> GetHeadShaAsync(string @ref = "HEAD", CancellationToken ct = default)
    {
        return (await RunGitAsync($"rev-parse {@ref}", ct)).Trim();
    }

    /// <inheritdoc/>
    public async Task<string> GetRemoteShaAsync(string branchName, CancellationToken ct = default)
    {
        await RunGitAsync("fetch origin", ct, throwOnError: false);
        return (await RunGitAsync($"rev-parse origin/{branchName}", ct)).Trim();
    }

    /// <inheritdoc/>
    public async Task<string> GetStatusAsync(CancellationToken ct = default)
    {
        return await RunGitAsync("status --short", ct);
    }

    /// <inheritdoc/>
    public async Task<List<string>> GetChangedFilePathsAsync(CancellationToken ct = default)
    {
        var output = await RunGitAsync("diff --name-only HEAD", ct);
        var staged = await RunGitAsync("diff --name-only --cached", ct);
        var untracked = await RunGitAsync("ls-files --others --exclude-standard", ct);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Concat(staged.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Concat(untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<List<string>> GetDiffFileListVsMainAsync(CancellationToken ct = default)
    {
        var output = await RunGitAsync($"diff --name-only origin/{_defaultBranch}...HEAD", ct, throwOnError: false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <inheritdoc/>
    public async Task RevertFilesAsync(IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        var files = string.Join(" ", relativePaths.Select(p => $"\"{p}\""));
        if (!string.IsNullOrWhiteSpace(files))
            await RunGitAsync($"checkout HEAD -- {files}", ct, throwOnError: false);
    }

    /// <inheritdoc/>
    public async Task RevertUncommittedChangesAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("reset --hard HEAD", ct);
            await RunGitAsync("clean -fd", ct);
        }
        finally { _gitLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync()
    {
        if (string.IsNullOrWhiteSpace(_worktreePath)) return;
        try
        {
            await _sharedCloneManager.RemoveWorktreeAsync(_worktreePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up worktree at {Path}", _worktreePath);
        }
    }

    /// <inheritdoc/>
    public async Task NukeAndRecloneAsync(string branchName, CancellationToken ct = default)
    {
        _logger.LogInformation("Nuking and recreating worktree for {Agent}", _agentSlug);

        await _sharedCloneManager.RemoveWorktreeAsync(_worktreePath, ct);

        _branchName = branchName;
        _worktreePath = await _sharedCloneManager.CreateWorktreeAsync(
            branchName, _agentSlug, _sparsePaths, ct);

        _logger.LogInformation("Worktree recreated at {Path} (~2s vs ~30-120s for re-clone)", _worktreePath);
    }

    #region Private helpers

    private async Task<string> RunGitAsync(string args, CancellationToken ct, bool throwOnError = true)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _worktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {args}");

        // Read stdout and stderr CONCURRENTLY to avoid pipe deadlock.
        // Sequential reads can hang when stderr fills its 4KB buffer before
        // stdout is fully consumed, blocking the child process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        if (throwOnError && process.ExitCode != 0)
        {
            _logger.LogWarning("git {Args} in {Dir} failed (exit {Code}): {Stderr}",
                args, _worktreePath, process.ExitCode, stderr);
            throw new InvalidOperationException($"git {args} failed (exit {process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Abort any in-progress git operations (rebase, merge, cherry-pick, revert)
    /// left behind by a crashed prior run. Same pattern as LocalWorkspace.
    /// </summary>
    private async Task AbortInProgressOperationsAsync(CancellationToken ct)
    {
        var gitDirResult = await RunGitAsync("rev-parse --git-dir", ct, throwOnError: false);
        if (string.IsNullOrWhiteSpace(gitDirResult)) return;

        var gitDir = gitDirResult.Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.Combine(_worktreePath, gitDir);

        var probes = new (string marker, string command)[]
        {
            (Path.Combine(gitDir, "rebase-merge"),     "rebase"),
            (Path.Combine(gitDir, "rebase-apply"),     "rebase"),
            (Path.Combine(gitDir, "MERGE_HEAD"),       "merge"),
            (Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "cherry-pick"),
            (Path.Combine(gitDir, "REVERT_HEAD"),      "revert")
        };

        foreach (var (marker, command) in probes)
        {
            var exists = File.Exists(marker) || Directory.Exists(marker);
            if (!exists) continue;

            _logger.LogWarning("[{Agent}] Detected stale {Op} state at {Marker} — aborting",
                _agentSlug, command, marker);
            await RunGitAsync($"{command} --abort", ct, throwOnError: false);
        }
    }

    /// <summary>
    /// Get the origin remote URL to validate push targets.
    /// Returns empty string on failure (best-effort).
    /// </summary>
    private async Task<string> GetOriginUrlAsync(CancellationToken ct)
    {
        try
        {
            var url = await RunGitAsync("remote get-url origin", ct, throwOnError: false);
            return url?.Trim() ?? "";
        }
        catch { return ""; }
    }

    #endregion
}
