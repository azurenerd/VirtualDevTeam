using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Workspace;

/// <summary>
/// Manages a local git clone for a single agent. Each agent gets its own isolated workspace
/// directory so builds and tests don't interfere between agents.
/// All git operations use Process.Start("git", ...) — no library dependencies.
/// </summary>
public class LocalWorkspace
{
    private readonly WorkspaceConfig _config;
    private readonly string _agentId;
    private readonly string _repoUrl;
    private readonly string _defaultBranch;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gitLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Full path to the local repository clone.
    /// </summary>
    public string RepoPath { get; }

    public LocalWorkspace(
        WorkspaceConfig config,
        string agentId,
        string repoUrl,
        string defaultBranch,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        _config = config;
        _agentId = agentId;
        _repoUrl = repoUrl;
        _defaultBranch = defaultBranch;
        _logger = logger;

        // Sanitize agent ID for directory name
        var safeName = agentId.Replace(" ", "").Replace("/", "-").Replace("\\", "-");
        var repoName = repoUrl.Split('/').Last().Replace(".git", "");
        RepoPath = Path.Combine(config.RootPath!, safeName, repoName);
    }

    /// <summary>
    /// Initialize the workspace: clone if it doesn't exist, or fetch + reset if it does.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(Path.Combine(RepoPath, ".git")))
            {
                _logger.LogInformation("[{Agent}] Workspace exists at {Path}, fetching latest", _agentId, RepoPath);
                await RunGitAsync("fetch", "--all", ct: ct);
                // Self-heal any mid-rebase/merge/cherry-pick state left behind by a
                // crashed prior run before attempting checkout.
                await AbortInProgressOperationsAsync(ct);
                await RunGitAsync("reset", "--hard", "HEAD", ct: ct, throwOnError: false);
                await RunGitAsync("checkout", _defaultBranch, ct: ct);
                await RunGitAsync("reset", "--hard", $"origin/{_defaultBranch}", ct: ct);
                await RunGitAsync("clean", "-fd", ct: ct);
            }
            else
            {
                _logger.LogInformation("[{Agent}] Cloning {Repo} to {Path}", _agentId, _repoUrl, RepoPath);
                // Ensure all directories in the path exist (root + agent subdirectory)
                Directory.CreateDirectory(Path.GetDirectoryName(RepoPath)!);

                // If directory exists but has no .git (partial clone from prior failed attempt), clean it
                if (Directory.Exists(RepoPath))
                {
                    _logger.LogWarning("[{Agent}] Removing stale partial clone at {Path}", _agentId, RepoPath);
                    await ForceDeleteDirectoryAsync(RepoPath, ct);
                }

                // Clone with token embedded in URL for auth (retry up to 3 times on timeout)
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await RunGitAsync("clone", _repoUrl, RepoPath, ct: ct, workDir: Path.GetDirectoryName(RepoPath));
                        break;
                    }
                    catch (Exception ex) when (attempt < 3 && (ex is TimeoutException || ex is OperationCanceledException == false))
                    {
                        _logger.LogWarning("[{Agent}] Clone attempt {Attempt}/3 failed ({Error}), retrying...", _agentId, attempt, ex.GetType().Name);
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        await ForceDeleteDirectoryAsync(RepoPath, ct);
                    }
                }

                // Configure git user for commits
                await RunGitAsync("config", "user.name", _config.GitUserName, ct: ct);
                await RunGitAsync("config", "user.email", _config.GitUserEmail, ct: ct);
            }

            _initialized = true;
            _logger.LogInformation("[{Agent}] Workspace initialized at {Path}", _agentId, RepoPath);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Sync the workspace with the default branch (fetch + checkout + pull).
    /// Call before starting a new task.
    /// </summary>
    public async Task SyncWithMainAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("fetch", "origin", ct: ct);
            // Abort any in-progress rebase/merge/cherry-pick from a prior failed
            // operation — leftover state blocks checkout with "resolve your current
            // index first". Must happen before reset --hard because reset is also
            // blocked in some wedged states.
            await AbortInProgressOperationsAsync(ct);
            // Clean uncommitted changes before checkout to prevent
            // "Please commit your changes or stash them before you switch branches"
            await RunGitAsync("reset", "--hard", "HEAD", ct: ct);
            await RunGitAsync("checkout", _defaultBranch, ct: ct);
            await RunGitAsync("reset", "--hard", $"origin/{_defaultBranch}", ct: ct);
            await RunGitAsync("clean", "-fd", ct: ct);
            _logger.LogDebug("[{Agent}] Synced with {Branch}", _agentId, _defaultBranch);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Create and checkout a new branch from the current HEAD.
    /// </summary>
    public async Task CreateBranchAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            // Delete local branch if it exists (stale from prior run)
            var result = await RunGitAsync("branch", "--list", branchName, ct: ct, throwOnError: false);
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                await RunGitAsync("branch", "-D", branchName, ct: ct, throwOnError: false);
            }

            await RunGitAsync("checkout", "-b", branchName, ct: ct);
            _logger.LogDebug("[{Agent}] Created branch {Branch}", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Checkout an existing branch (e.g., for rework on an existing PR).
    /// </summary>
    public async Task CheckoutBranchAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            // Abort any in-progress rebase/merge/cherry-pick from a prior failed
            // operation — leftover state blocks checkout with "resolve your current
            // index first", causing infinite retry loops.
            await AbortInProgressOperationsAsync(ct);
            // Discard any dirty working tree so checkout doesn't refuse due to
            // "Please commit your changes or stash them" from a prior attempt.
            await RunGitAsync("reset", "--hard", "HEAD", ct: ct, throwOnError: false);
            await RunGitAsync("fetch", "origin", branchName, ct: ct, throwOnError: false);
            await RunGitAsync("checkout", branchName, ct: ct);
            // Reset to remote HEAD to pick up any new commits pushed by other agents
            await RunGitAsync("reset", "--hard", $"origin/{branchName}", ct: ct, throwOnError: false);
            _logger.LogDebug("[{Agent}] Checked out branch {Branch} (reset to remote HEAD)", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Detect and abort any in-progress git operation (rebase/merge/cherry-pick/revert/am/bisect)
    /// that would otherwise block subsequent checkouts with "you need to resolve your current index
    /// first". Called from SyncWithMainAsync / CheckoutBranchAsync to self-heal wedged workspaces.
    /// </summary>
    private async Task AbortInProgressOperationsAsync(CancellationToken ct)
    {
        // git dir can be the repo's .git directory, or for worktrees a pointer file.
        // Query git directly rather than probing file paths.
        var gitDirResult = await RunGitAsync("rev-parse", "--git-dir", ct: ct, throwOnError: false);
        if (!gitDirResult.Success || string.IsNullOrWhiteSpace(gitDirResult.StandardOutput))
            return;

        var gitDir = gitDirResult.StandardOutput.Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.Combine(RepoPath, gitDir);

        // Each probe: (marker path, abort command)
        var probes = new (string marker, string command)[]
        {
            (Path.Combine(gitDir, "rebase-merge"),  "rebase"),
            (Path.Combine(gitDir, "rebase-apply"),  "rebase"),
            (Path.Combine(gitDir, "MERGE_HEAD"),    "merge"),
            (Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "cherry-pick"),
            (Path.Combine(gitDir, "REVERT_HEAD"),   "revert")
        };

        foreach (var (marker, command) in probes)
        {
            var exists = File.Exists(marker) || Directory.Exists(marker);
            if (!exists) continue;

            _logger.LogWarning("[{Agent}] Detected in-progress {Op} state at {Marker} — aborting",
                _agentId, command, marker);
            await RunGitAsync(command, "--abort", ct: ct, throwOnError: false);
        }
    }

    /// <summary>
    /// Merge the default branch into the current branch (for rework after main has changed).
    /// </summary>
    public async Task<bool> MergeMainIntoBranchAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("fetch", "origin", _defaultBranch, ct: ct);
            var result = await RunGitAsync("merge", $"origin/{_defaultBranch}", "--no-edit",
                ct: ct, throwOnError: false);

            if (!result.Success)
            {
                _logger.LogWarning("[{Agent}] Merge conflict detected, aborting merge", _agentId);
                await RunGitAsync("merge", "--abort", ct: ct, throwOnError: false);
                return false;
            }

            return true;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Write a file to the local workspace. Creates parent directories as needed.
    /// </summary>
    public async Task WriteFileAsync(string relativePath, string content, CancellationToken ct = default)
    {
        EnsureInitialized();
        var fullPath = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fullPath, content, ct);
    }

    /// <summary>
    /// Read a file from the local workspace.
    /// </summary>
    public async Task<string?> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();
        var fullPath = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return null;
        return await File.ReadAllTextAsync(fullPath, ct);
    }

    /// <summary>
    /// Stage all changes and create a git commit.
    /// </summary>
    public async Task CommitAsync(string message, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            // Ensure .gitignore exists before staging — prevents bin/obj/build artifacts from being committed
            await EnsureGitignoreAsync(ct);

            await RunGitAsync("add", "-A", ct: ct);

            // Check if there's anything to commit
            var status = await RunGitAsync("status", "--porcelain", ct: ct);
            if (string.IsNullOrWhiteSpace(status.StandardOutput))
            {
                _logger.LogDebug("[{Agent}] Nothing to commit", _agentId);
                return;
            }

            await RunGitAsync("commit", "-m", message, ct: ct);
            _logger.LogDebug("[{Agent}] Committed: {Message}", _agentId, message);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Push the current branch to origin.
    /// </summary>
    public async Task PushAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            // Fetch remote state first — screenshots and other API-committed files may have
            // advanced the remote branch beyond our local tracking ref, causing --force-with-lease
            // to reject with "stale info". Fetching updates the tracking ref so the push succeeds.
            try
            {
                await RunGitAsync("fetch", "origin", branchName, null!, ct: ct, throwOnError: false);
            }
            catch
            {
                // Fetch failure is non-fatal — branch may not exist on remote yet (first push)
            }

            try
            {
                await RunGitAsync("push", "origin", branchName, "--force-with-lease", ct: ct);
            }
            catch (InvalidOperationException ex) when (IsWorkflowScopeRejection(ex.Message))
            {
                // GitHub rejected the push because the PAT lacks `workflow` scope but the
                // commit touches .github/workflows/**. Recover by stripping those files
                // from the unpushed commits and retrying once. Preserves all non-workflow
                // code so baseline/strategy output isn't thrown away over a CI-file policy.
                _logger.LogWarning("[{Agent}] Push rejected by workflow-scope policy for {Branch}; stripping .github/workflows files and retrying",
                    _agentId, branchName);
                var stripped = await StripWorkflowFilesFromUnpushedCommitsAsync(branchName, ct);
                if (!stripped)
                {
                    throw; // couldn't recover — surface original error
                }
                await RunGitAsync("push", "origin", branchName, "--force-with-lease", ct: ct);
                _logger.LogWarning("[{Agent}] Pushed {Branch} after stripping .github/workflows/** — PAT missing `workflow` scope",
                    _agentId, branchName);
            }
            catch
            {
                // If --force-with-lease still fails after fetch (e.g., API commit race),
                // rebase onto the remote and retry once.
                _logger.LogWarning("[{Agent}] Push --force-with-lease failed for {Branch}, rebasing and retrying",
                    _agentId, branchName);
                await RunGitAsync("pull", "--rebase", "origin", branchName, ct: ct, throwOnError: false);
                await RunGitAsync("push", "origin", branchName, "--force-with-lease", ct: ct);
            }
            _logger.LogInformation("[{Agent}] Pushed branch {Branch} to origin", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    private static bool IsWorkflowScopeRejection(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("workflow` scope", StringComparison.Ordinal)
            || stderr.Contains("workflow scope", StringComparison.Ordinal)
            || (stderr.Contains(".github/workflows/", StringComparison.Ordinal)
                && stderr.Contains("refusing", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Remove any .github/workflows/** files from unpushed commits on the current branch.
    /// For files Added in the unpushed range → git rm them. For files Modified → restore
    /// from origin/&lt;branch&gt;. Then amends the last commit with the cleanup. Returns true
    /// if at least one workflow file was stripped and a push retry is appropriate.
    /// </summary>
    private async Task<bool> StripWorkflowFilesFromUnpushedCommitsAsync(string branchName, CancellationToken ct)
    {
        try
        {
            // Find the range of unpushed commits: origin/<branch>..HEAD
            var remoteRef = $"origin/{branchName}";
            var hasRemote = (await RunGitAsync("rev-parse", "--verify", remoteRef, null!, ct: ct, throwOnError: false)).Success;
            var range = hasRemote ? $"{remoteRef}..HEAD" : "HEAD";

            // List workflow files that are Added or Modified in the unpushed range.
            var diffArgs = hasRemote
                ? new[] { "diff", "--name-status", range, "--", ".github/workflows/" }
                : new[] { "log", "--name-status", "--pretty=format:", "HEAD", "--", ".github/workflows/" };
            var diff = await RunGitCoreAsync(diffArgs, RepoPath, throwOnError: false, ct);
            if (!diff.Success || string.IsNullOrWhiteSpace(diff.StandardOutput))
                return false;

            var stripped = 0;
            foreach (var raw in diff.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                // Format: "A\tpath" or "M\tpath" or "D\tpath" (ignore Delete — already gone)
                var tab = line.IndexOf('\t');
                if (tab < 1) continue;
                var status = line[..tab].Trim();
                var path = line[(tab + 1)..].Trim();
                if (!path.StartsWith(".github/workflows/", StringComparison.Ordinal)) continue;

                if (status.StartsWith("A", StringComparison.Ordinal))
                {
                    await RunGitAsync("rm", "-f", "--cached", path, ct: ct, throwOnError: false);
                    var full = Path.Combine(RepoPath, path);
                    try { if (File.Exists(full)) File.Delete(full); } catch { }
                    stripped++;
                }
                else if (status.StartsWith("M", StringComparison.Ordinal) && hasRemote)
                {
                    await RunGitAsync("checkout", remoteRef, "--", path, ct: ct, throwOnError: false);
                    stripped++;
                }
            }

            if (stripped == 0)
                return false;

            // Stage + amend so the push carries the cleaned tree.
            await RunGitAsync("add", "-A", null!, null!, ct: ct, throwOnError: false);
            await RunGitAsync("commit", "--amend", "--no-edit", null!, ct: ct, throwOnError: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Agent}] Failed to strip workflow files from unpushed commits on {Branch}", _agentId, branchName);
            return false;
        }
    }

    /// <summary>
    /// Force push the current branch to origin (bypasses force-with-lease safety).
    /// Use only when retrying after a known branch divergence.
    /// </summary>
    public async Task ForcePushAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("push", "origin", branchName, "--force", ct: ct);
            _logger.LogInformation("[{Agent}] Force-pushed branch {Branch} to origin", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Pull and rebase changes from the remote tracking branch.
    /// Returns false if rebase conflicts occur.
    /// </summary>
    public async Task<bool> PullRebaseAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("fetch", "origin", branchName, ct: ct);
            var result = await RunGitAsync("rebase", $"origin/{branchName}",
                ct: ct, throwOnError: false);

            if (!result.Success)
            {
                _logger.LogWarning("[{Agent}] Rebase conflict on {Branch}, aborting", _agentId, branchName);
                await RunGitAsync("rebase", "--abort", ct: ct, throwOnError: false);
                return false;
            }

            return true;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Force-delete a directory, handling locked files on Windows by clearing read-only
    /// attributes and retrying after a short delay.
    /// </summary>
    private async Task ForceDeleteDirectoryAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return;

        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                // Clear read-only attributes that git sets on pack files
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
                }
                Directory.Delete(path, true);
                return;
            }
            catch when (retry < 2)
            {
                _logger.LogDebug("[{Agent}] Retry {Retry} deleting {Path}", _agentId, retry + 1, path);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[{Agent}] Could not force-delete {Path}: {Error}", _agentId, path, ex.Message);
            }
        }
    }

    /// <summary>
    /// Ensures a .gitignore file exists at the repo root. If missing, creates a standard
    /// .NET gitignore that prevents bin/, obj/, and other build artifacts from being committed.
    /// </summary>
    private async Task EnsureGitignoreAsync(CancellationToken ct)
    {
        var gitignorePath = Path.Combine(RepoPath, ".gitignore");
        if (File.Exists(gitignorePath)) return;

        _logger.LogInformation("[{Agent}] No .gitignore found — creating standard .NET gitignore", _agentId);
        var content = """
            ## Build results
            [Dd]ebug/
            [Rr]elease/
            x64/
            x86/
            [Ww][Ii][Nn]32/
            [Aa][Rr][Mm]/
            [Aa][Rr][Mm]64/
            bld/
            [Bb]in/
            [Oo]bj/
            [Ll]og/
            [Ll]ogs/

            ## NuGet
            *.nupkg
            *.snupkg
            **/[Pp]ackages/*
            !**/[Pp]ackages/build/

            ## Visual Studio
            .vs/
            *.suo
            *.user
            *.userosscache
            *.sln.docstates
            *.csproj.user

            ## Rider
            .idea/

            ## User-specific
            *.rsuser
            *.DotSettings.user
            launchSettings.json

            ## Build output
            publish/
            [Pp]ublish/
            **/wwwroot/dist/

            ## Node
            node_modules/
            npm-debug.log*

            ## OS
            .DS_Store
            Thumbs.db
            desktop.ini

            ## Environment
            .env
            .env.*
            appsettings.Development.json
            appsettings.Local.json

            ## Playwright
            playwright-report/
            test-results/
            """.Replace("            ", "");  // Remove indentation from raw string literal

        await File.WriteAllTextAsync(gitignorePath, content, ct);
    }

    /// <summary>
    /// Get the current branch name.
    /// </summary>
    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var result = await RunGitAsync("rev-parse", "--abbrev-ref", "HEAD", ct: ct);
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Get the SHA of the given local ref (defaults to HEAD).
    /// </summary>
    public async Task<string> GetHeadShaAsync(string @ref = "HEAD", CancellationToken ct = default)
    {
        EnsureInitialized();
        var result = await RunGitAsync("rev-parse", @ref, ct: ct);
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Fetch the given branch from origin and return its remote SHA. Returns empty
    /// string when the remote ref does not exist.
    /// </summary>
    public async Task<string> GetRemoteShaAsync(string branchName, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync("fetch", "origin", branchName, ct: ct, throwOnError: false);
            var result = await RunGitAsync("rev-parse", $"origin/{branchName}", ct: ct, throwOnError: false);
            return result.Success ? result.StandardOutput.Trim() : "";
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Get git status (porcelain format) for changed files.
    /// </summary>
    public async Task<string> GetStatusAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        var result = await RunGitAsync("status", "--porcelain", ct: ct);
        return result.StandardOutput;
    }

    /// <summary>
    /// Revert all uncommitted changes in the workspace (git checkout -- . && git clean -fd).
    /// Used to undo file writes when a build fails and the step should be skipped.
    /// </summary>
    public async Task RevertUncommittedChangesAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await _gitLock.WaitAsync(ct);
        try
        {
            // reset --hard HEAD (not `checkout -- .`) so we also clear unmerged (UU)
            // entries left behind by `git apply --3way`. The prior form left a poisoned
            // index that wedged the next checkout with "resolve your current index first".
            await RunGitAsync("reset", "--hard", "HEAD", ct: ct);
            await RunGitAsync("clean", "-fd", ct: ct);
            _logger.LogInformation("[{Agent}] Reverted all uncommitted changes", _agentId);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Delete the workspace directory.
    /// </summary>
    public Task CleanupAsync()
    {
        if (Directory.Exists(RepoPath))
        {
            _logger.LogInformation("[{Agent}] Cleaning up workspace at {Path}", _agentId, RepoPath);
            try
            {
                // Force remove read-only files (.git objects)
                ForceDeleteDirectory(RepoPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Agent}] Failed to fully clean workspace at {Path}", _agentId, RepoPath);
            }
        }

        _initialized = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nuclear option: delete the entire local clone, re-clone from remote, and check out
    /// the specified branch at the latest remote HEAD. Use when rebase/reset fails and
    /// the local repo is in an unrecoverable state.
    /// </summary>
    public async Task NukeAndRecloneAsync(string branchName, CancellationToken ct = default)
    {
        _logger.LogWarning("[{Agent}] Nuking local clone at {Path} and re-cloning for branch {Branch}",
            _agentId, RepoPath, branchName);

        // 1. Delete everything
        await CleanupAsync();

        // 2. Re-clone from scratch (sets _initialized = true)
        await InitializeAsync(ct);

        // 3. Check out the target branch at remote HEAD
        await CheckoutBranchAsync(branchName, ct);

        _logger.LogInformation("[{Agent}] Fresh clone ready on branch {Branch}", _agentId, branchName);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                $"Workspace for {_agentId} is not initialized. Call InitializeAsync() first.");
    }

    /// <summary>
    /// Execute a git command in the workspace directory.
    /// </summary>
    private async Task<ProcessResult> RunGitAsync(
        params string[] argsAndOptions)
    {
        return await RunGitCoreAsync(argsAndOptions, workDir: null, throwOnError: true, ct: default);
    }

    private async Task<ProcessResult> RunGitAsync(
        string arg1, string arg2,
        CancellationToken ct = default,
        bool throwOnError = true,
        string? workDir = null)
    {
        return await RunGitCoreAsync([arg1, arg2], workDir, throwOnError, ct);
    }

    private async Task<ProcessResult> RunGitAsync(
        string arg1, string arg2, string arg3,
        CancellationToken ct = default,
        bool throwOnError = true,
        string? workDir = null)
    {
        return await RunGitCoreAsync([arg1, arg2, arg3], workDir, throwOnError, ct);
    }

    private async Task<ProcessResult> RunGitAsync(
        string arg1, string arg2, string arg3, string arg4,
        CancellationToken ct = default,
        bool throwOnError = true,
        string? workDir = null)
    {
        return await RunGitCoreAsync([arg1, arg2, arg3, arg4], workDir, throwOnError, ct);
    }

    private async Task<ProcessResult> RunGitCoreAsync(
        string[] args,
        string? workDir,
        bool throwOnError,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(EscapeArg)),
            WorkingDirectory = workDir ?? RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        _logger.LogTrace("[{Agent}] git {Args}", _agentId, startInfo.Arguments);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(300));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Git command timed out: git {startInfo.Arguments}");
        }

        sw.Stop();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            Duration = sw.Elapsed
        };

        if (!result.Success && throwOnError)
        {
            _logger.LogWarning("[{Agent}] git {Args} failed (exit {Code}): {Stderr}",
                _agentId, startInfo.Arguments, result.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);
            throw new InvalidOperationException(
                $"Git command failed (exit {result.ExitCode}): git {startInfo.Arguments}\n{stderr}");
        }

        return result;
    }

    private static string EscapeArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }

    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            Directory.Delete(dir);
        }

        Directory.Delete(path);
    }
}
