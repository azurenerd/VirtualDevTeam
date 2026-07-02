using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Central coordinator for a shared .git object store used in Worktree and InPlace modes.
/// Serializes worktree create/remove operations to avoid .git/config.lock races (Lesson #5).
/// In Clone mode, this service is registered but unused.
/// </summary>
public class SharedCloneManager
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<SharedCloneManager> _logger;
    private readonly SemaphoreSlim _hostGitLock = new(1, 1);
    private DateTime _lastFetch = DateTime.MinValue;
    private readonly TimeSpan _fetchCooldown = TimeSpan.FromMinutes(5);
    private string? _resolvedHostRepoPath;

    /// <summary>Marker file placed in every VDT-created worktree for safety validation.</summary>
    public const string WorktreeMarkerFileName = ".vdt-worktree-id";

    public SharedCloneManager(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<SharedCloneManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets the absolute path to the host repository (the shared .git owner).
    /// In InPlace mode: the operator's existing checkout.
    /// In Worktree mode: a canonical clone created at startup.
    /// </summary>
    public string HostRepoPath => _resolvedHostRepoPath
        ?? throw new InvalidOperationException("SharedCloneManager not initialized. Call EnsureReadyAsync first.");

    /// <summary>
    /// Gets the root directory where agent worktrees are created.
    /// </summary>
    public string WorktreeRoot
    {
        get
        {
            var wsConfig = _config.CurrentValue.Workspace;
            if (!string.IsNullOrWhiteSpace(wsConfig.WorktreeRoot))
                return wsConfig.WorktreeRoot;

            // Default: %LOCALAPPDATA%\VDT\worktrees\{repoHash}
            var repoHash = HostRepoPath.GetHashCode().ToString("x8");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VDT", "worktrees", repoHash);
        }
    }

    /// <summary>
    /// Ensure the shared repository is ready (clone if Worktree mode, validate if InPlace).
    /// Must be called once at startup before any worktree creation.
    /// </summary>
    public async Task<string> EnsureReadyAsync(CancellationToken ct = default)
    {
        var wsConfig = _config.CurrentValue.Workspace;

        switch (wsConfig.WorkspaceMode)
        {
            case WorkspaceMode.InPlace:
                return await InitializeInPlaceAsync(wsConfig, ct);
            case WorkspaceMode.Worktree:
                return await InitializeWorktreeModeAsync(wsConfig, ct);
            default:
                _logger.LogDebug("SharedCloneManager: Clone mode — no shared repo to initialize");
                _resolvedHostRepoPath = "";
                return "";
        }
    }

    /// <summary>
    /// Create a lightweight worktree for an agent, branched from the default branch.
    /// Thread-safe: serialized via _hostGitLock to prevent .git/config.lock races.
    /// </summary>
    public async Task<string> CreateWorktreeAsync(
        string branch, string agentSlug,
        IReadOnlyList<string>? sparseAdditions = null,
        CancellationToken ct = default)
    {
        var worktreePath = Path.Combine(WorktreeRoot, agentSlug);

        await _hostGitLock.WaitAsync(ct);
        try
        {
            // Fetch if cooldown elapsed
            await FetchIfStaleAsync(ct);

            // Remove stale worktree at this path if it exists
            if (Directory.Exists(worktreePath))
            {
                _logger.LogInformation("Removing stale worktree at {Path}", worktreePath);
                await RunGitInHostAsync($"worktree remove --force \"{worktreePath}\"", ct, throwOnError: false);
                if (Directory.Exists(worktreePath))
                    ForceDeleteDirectory(worktreePath);
            }

            // Create branch pointing at origin/{effectiveBranch} — use working branch if set, else default
            var effectiveBranch = _config.CurrentValue.Project?.WorkingBranch;
            if (string.IsNullOrWhiteSpace(effectiveBranch))
                effectiveBranch = _config.CurrentValue.Project?.DefaultBranch ?? "main";
            var baseRef = $"origin/{effectiveBranch}";
            await RunGitInHostAsync($"branch -f {branch} {baseRef}", ct, throwOnError: false);

            // Create worktree — no checkout first, then apply sparse + checkout
            var useSparse = (_config.CurrentValue.Workspace.SparseCheckoutPaths?.Count ?? 0) > 0
                         || (sparseAdditions?.Count ?? 0) > 0;

            if (useSparse)
            {
                await RunGitInHostAsync($"worktree add --no-checkout \"{worktreePath}\" {branch}", ct);
                await ConfigureSparseCheckoutAsync(worktreePath, sparseAdditions, ct);
                await RunGitAsync(worktreePath, $"checkout {branch}", ct);
            }
            else
            {
                await RunGitInHostAsync($"worktree add \"{worktreePath}\" {branch}", ct);
            }

            // Stamp with marker file for safety validation (Critical finding C2)
            var markerId = $"{agentSlug}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            await File.WriteAllTextAsync(Path.Combine(worktreePath, WorktreeMarkerFileName), markerId, ct);

            _logger.LogInformation(
                "Created worktree for {Agent} at {Path} (branch: {Branch}, sparse: {Sparse})",
                agentSlug, worktreePath, branch, useSparse);
        }
        finally
        {
            _hostGitLock.Release();
        }

        return worktreePath;
    }

    /// <summary>
    /// Remove a worktree and clean up its directory.
    /// </summary>
    public async Task RemoveWorktreeAsync(string worktreePath, CancellationToken ct = default)
    {
        await _hostGitLock.WaitAsync(ct);
        try
        {
            await RunGitInHostAsync($"worktree remove --force \"{worktreePath}\"", ct, throwOnError: false);
            if (Directory.Exists(worktreePath))
                ForceDeleteDirectory(worktreePath);

            _logger.LogInformation("Removed worktree at {Path}", worktreePath);
        }
        finally
        {
            _hostGitLock.Release();
        }
    }

    /// <summary>
    /// Prune stale worktrees that no longer have a valid working directory.
    /// </summary>
    public async Task PruneStaleWorktreesAsync(CancellationToken ct = default)
    {
        await _hostGitLock.WaitAsync(ct);
        try
        {
            await RunGitInHostAsync("worktree prune", ct, throwOnError: false);
            _logger.LogInformation("Pruned stale worktrees");
        }
        finally
        {
            _hostGitLock.Release();
        }
    }

    /// <summary>
    /// Validate that a path is a VDT-created worktree (has marker file).
    /// Use before any destructive operations to prevent accidentally touching
    /// the operator's working tree (Critical finding C2).
    /// </summary>
    public static bool IsVdtWorktree(string path)
    {
        return File.Exists(Path.Combine(path, WorktreeMarkerFileName));
    }

    #region Private helpers

    private async Task<string> InitializeInPlaceAsync(WorkspaceConfig wsConfig, CancellationToken ct)
    {
        var checkoutPath = wsConfig.LocalCheckoutPath
            ?? throw new InvalidOperationException(
                "InPlace mode requires LocalCheckoutPath to be set in WorkspaceConfig");

        if (!Directory.Exists(checkoutPath))
            throw new DirectoryNotFoundException($"InPlace checkout path does not exist: {checkoutPath}");

        var gitDir = Path.Combine(checkoutPath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
            throw new InvalidOperationException($"InPlace path is not a git repository: {checkoutPath}");

        if (wsConfig.RequireCleanHostTree)
        {
            var status = await RunGitAsync(checkoutPath, "status --porcelain", ct);
            if (!string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException(
                    $"InPlace checkout has uncommitted changes. Commit or stash them first, " +
                    $"or set RequireCleanHostTree=false.\n{status}");
        }

        _resolvedHostRepoPath = checkoutPath;
        Directory.CreateDirectory(WorktreeRoot);
        _logger.LogInformation(
            "SharedCloneManager: InPlace mode initialized. Host={Host}, WorktreeRoot={Root}",
            checkoutPath, WorktreeRoot);
        return checkoutPath;
    }

    private async Task<string> InitializeWorktreeModeAsync(WorkspaceConfig wsConfig, CancellationToken ct)
    {
        var rootPath = wsConfig.RootPath ?? ".agents";
        var canonicalClonePath = Path.Combine(rootPath, ".shared-clone");

        if (Directory.Exists(Path.Combine(canonicalClonePath, ".git")))
        {
            _logger.LogInformation("SharedCloneManager: using existing canonical clone at {Path}", canonicalClonePath);
            _resolvedHostRepoPath = canonicalClonePath;
            await FetchIfStaleAsync(ct);
        }
        else
        {
            Directory.CreateDirectory(canonicalClonePath);
            var cfg = _config.CurrentValue;
            // Use GetGitCloneUrl() to support LDP mode (returns bare repo path) and all platforms
            var repoUrl = cfg.GetGitCloneUrl();

            var cloneFlags = wsConfig.CloneFlags;
            _logger.LogInformation(
                "SharedCloneManager: cloning {Url} to {Path} (flags: {Flags})",
                repoUrl, canonicalClonePath, cloneFlags);

            var cloneArgs = $"clone {cloneFlags} \"{repoUrl}\" \"{canonicalClonePath}\"".Trim();
            await RunGitAsync(null, cloneArgs, ct);
            _resolvedHostRepoPath = canonicalClonePath;
        }

        Directory.CreateDirectory(WorktreeRoot);
        _logger.LogInformation(
            "SharedCloneManager: Worktree mode initialized. Host={Host}, WorktreeRoot={Root}",
            canonicalClonePath, WorktreeRoot);
        return canonicalClonePath;
    }

    private async Task FetchIfStaleAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetch < _fetchCooldown)
            return;

        await RunGitInHostAsync("fetch --prune --quiet", ct, throwOnError: false);
        _lastFetch = DateTime.UtcNow;
    }

    private async Task ConfigureSparseCheckoutAsync(
        string worktreePath, IReadOnlyList<string>? additionalPaths, CancellationToken ct)
    {
        await RunGitAsync(worktreePath, "sparse-checkout init --cone", ct);

        var patterns = new List<string>(_config.CurrentValue.Workspace.SparseCheckoutPaths ?? []);
        if (additionalPaths is not null)
            patterns.AddRange(additionalPaths);

        // Always include root build files
        patterns.AddRange(["*.sln", "*.csproj", "Directory.Build.props", "global.json",
                          "package.json", "package-lock.json", "tsconfig.json"]);

        var unique = patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unique.Count > 0)
        {
            await RunGitAsync(worktreePath, $"sparse-checkout set {string.Join(' ', unique)}", ct);
            _logger.LogDebug("Sparse checkout set with {Count} patterns: [{Patterns}]",
                unique.Count, string.Join(", ", unique));
        }
    }

    private Task<string> RunGitInHostAsync(string args, CancellationToken ct, bool throwOnError = true)
    {
        return RunGitAsync(HostRepoPath, args, ct, throwOnError);
    }

    private async Task<string> RunGitAsync(string? workingDir, string args, CancellationToken ct, bool throwOnError = true)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {args}");

        // Read stdout and stderr CONCURRENTLY to avoid pipe deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        if (throwOnError && process.ExitCode != 0)
        {
            _logger.LogWarning("git {Args} failed (exit {Code}): {Stderr}", args, process.ExitCode, stderr);
            throw new InvalidOperationException($"git {args} failed (exit {process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    private static void ForceDeleteDirectory(string path)
    {
        try
        {
            // Remove read-only attributes that git sets
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort — git worktree prune will clean up later
        }
    }

    #endregion
}
