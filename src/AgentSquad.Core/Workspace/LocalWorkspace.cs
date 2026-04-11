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
                await RunGitAsync("checkout", _defaultBranch, ct: ct);
                await RunGitAsync("reset", "--hard", $"origin/{_defaultBranch}", ct: ct);
                await RunGitAsync("clean", "-fd", ct: ct);
            }
            else
            {
                _logger.LogInformation("[{Agent}] Cloning {Repo} to {Path}", _agentId, _repoUrl, RepoPath);
                // Ensure all directories in the path exist (root + agent subdirectory)
                Directory.CreateDirectory(Path.GetDirectoryName(RepoPath)!);

                // Clone with token embedded in URL for auth
                await RunGitAsync("clone", _repoUrl, RepoPath, ct: ct, workDir: Path.GetDirectoryName(RepoPath));

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
            await RunGitAsync("fetch", "origin", branchName, ct: ct, throwOnError: false);
            await RunGitAsync("checkout", branchName, ct: ct);
            _logger.LogDebug("[{Agent}] Checked out branch {Branch}", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
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
            await RunGitAsync("push", "origin", branchName, "--force-with-lease", ct: ct);
            _logger.LogInformation("[{Agent}] Pushed branch {Branch} to origin", _agentId, branchName);
        }
        finally
        {
            _gitLock.Release();
        }
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
            await RunGitAsync("checkout", "--", ".", ct: ct);
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
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

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
