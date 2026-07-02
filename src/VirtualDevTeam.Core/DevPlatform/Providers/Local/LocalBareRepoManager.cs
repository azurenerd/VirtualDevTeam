using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Creates and manages a local bare git repository that serves as the "remote" for
/// agent worktrees in LocalDevPlatform mode. Agents push/pull against this bare repo
/// instead of GitHub/ADO, enabling self-merge without branch policies.
///
/// The bare repo is initialized from the upstream enterprise repo and can optionally
/// mirror agent branches back to the upstream for visibility.
/// </summary>
public sealed class LocalBareRepoManager
{
    private readonly ILogger<LocalBareRepoManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _branchLocks = new(StringComparer.OrdinalIgnoreCase);
    private string? _bareRepoPath;
    private bool _initialized;

    public LocalBareRepoManager(ILogger<LocalBareRepoManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Absolute path to the local bare repo (.git directory).</summary>
    public string? BareRepoPath => _bareRepoPath;

    /// <summary>Whether the bare repo has been initialized.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Initialize the local bare repo. If it already exists, validates it.
    /// If not, clones from the upstream repo URL as a bare clone.
    /// </summary>
    public async Task InitializeAsync(string basePath, string repoName, string? upstreamUrl, CancellationToken ct = default)
    {
        var repoDir = Path.Combine(basePath, $"{repoName}.git");
        _bareRepoPath = repoDir;

        if (Directory.Exists(repoDir))
        {
            var headFile = Path.Combine(repoDir, "HEAD");
            if (File.Exists(headFile))
            {
                _initialized = true;
                _logger.LogInformation("LocalBareRepoManager: existing bare repo at {Path}", repoDir);

                // DO NOT fetch from upstream on restart — bare clone refspec maps
                // refs/heads/*:refs/heads/* which overwrites local branches directly,
                // destroying any merge commits accumulated during the LDP run.
                // The bare repo has everything it needs from the initial clone.

                // Ensure stale scratch worktrees don't block pushes (Lesson: snapshot
                // restore can leave worktrees with agent branches checked out, causing
                // "refusing to update checked out branch" on push)
                await PruneAndAllowPushesAsync(repoDir, ct);
                return;
            }
        }

        Directory.CreateDirectory(basePath);

        if (!string.IsNullOrEmpty(upstreamUrl))
        {
            // Never log URLs that may contain credentials — redact for safety
            var safeUrl = RedactUrl(upstreamUrl);
            _logger.LogInformation("LocalBareRepoManager: cloning bare repo from {Url} to {Path}", safeUrl, repoDir);
            await RunGitAsync(basePath, $"clone --bare \"{upstreamUrl}\" \"{repoDir}\"", ct);
        }
        else
        {
            _logger.LogInformation("LocalBareRepoManager: creating empty bare repo at {Path}", repoDir);
            Directory.CreateDirectory(repoDir);
            await RunGitAsync(repoDir, "init --bare", ct);
        }

        await PruneAndAllowPushesAsync(repoDir, ct);
        _initialized = true;
        _logger.LogInformation("LocalBareRepoManager: bare repo initialized at {Path}", repoDir);
    }

    /// <summary>
    /// Merge a branch into the target branch within the bare repo.
    /// Uses a temporary worktree for the merge operation.
    /// </summary>
    public async Task MergeBranchAsync(string sourceBranch, string targetBranch, string commitMessage, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null)
            throw new InvalidOperationException("Bare repo not initialized");

        // Prune stale worktrees from prior crashes before creating new ones
        await PruneStaleWorktreesAsync();

        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-merge-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_bareRepoPath, $"worktree add \"{tempWorktree}\" {targetBranch}", ct);

            // Set git identity in temp worktree (required for merge commits on clean machines/CI)
            await RunGitAsync(tempWorktree, "config user.name \"VirtualDevTeam\"", ct);
            await RunGitAsync(tempWorktree, "config user.email \"virtualdevteam@noreply.github.com\"", ct);

            // Bare repo branches are local refs — no origin/ prefix
            try
            {
                await RunGitAsync(tempWorktree, $"merge --no-ff -m \"{commitMessage.Replace("\"", "\\\"")}\" {sourceBranch}", ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
                                                     || ex.Message.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase))
            {
                // Abort the failed merge
                try { await RunGitAsync(tempWorktree, "merge --abort", CancellationToken.None); } catch { }

                // Rebase fallback: rebase source onto target, then retry the merge
                _logger.LogInformation("Merge conflict {Source}→{Target}, attempting rebase fallback", sourceBranch, targetBranch);
                if (await TryRebaseAndMergeAsync(tempWorktree, sourceBranch, targetBranch, commitMessage, ct))
                {
                    _logger.LogInformation("Rebase fallback succeeded for {Source}→{Target}", sourceBranch, targetBranch);
                    return;
                }

                throw new InvalidOperationException(
                    $"Merge conflict: {sourceBranch} → {targetBranch}. Rebase fallback also failed. {ex.Message}", ex);
            }

            _logger.LogInformation("Merged {Source} into {Target} in local bare repo", sourceBranch, targetBranch);
        }
        finally
        {
            await SafeRemoveWorktreeAsync(tempWorktree);
        }
    }

    /// <summary>
    /// Rebase fallback: create a temp worktree on the source branch, rebase onto target,
    /// then retry the merge with the rebased source.
    /// </summary>
    private async Task<bool> TryRebaseAndMergeAsync(string targetWorktree, string sourceBranch, string targetBranch, string commitMessage, CancellationToken ct)
    {
        var rebaseWorktree = Path.Combine(Path.GetTempPath(), $"vdt-rebase-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_bareRepoPath!, $"worktree add \"{rebaseWorktree}\" {sourceBranch}", ct);
            await RunGitAsync(rebaseWorktree, "config user.name \"VirtualDevTeam\"", ct);
            await RunGitAsync(rebaseWorktree, "config user.email \"virtualdevteam@noreply.github.com\"", ct);

            try
            {
                await RunGitAsync(rebaseWorktree, $"rebase {targetBranch}", ct);
            }
            catch (InvalidOperationException)
            {
                try { await RunGitAsync(rebaseWorktree, "rebase --abort", CancellationToken.None); } catch { }
                // Detach HEAD before returning so the branch is freed for future worktree operations
                try { await RunGitAsync(rebaseWorktree, "checkout --detach HEAD", CancellationToken.None); } catch { }
                _logger.LogWarning("Rebase of {Source} onto {Target} also conflicted", sourceBranch, targetBranch);
                return false;
            }

            // Update the source branch ref to the rebased SHA so the merge below
            // picks up the rebased commits (not the pre-rebase ones).
            var rebasedSha = (await RunGitCaptureAsync(rebaseWorktree, "rev-parse HEAD", ct)).Trim();
            if (!string.IsNullOrWhiteSpace(rebasedSha))
            {
                // Detach HEAD in the rebase worktree first — git refuses to force-update
                // a branch checked out in any worktree.
                try { await RunGitAsync(rebaseWorktree, $"checkout --detach {rebasedSha}", ct); } catch { }
                await RunGitAsync(_bareRepoPath!, $"branch -f {sourceBranch} {rebasedSha}", ct);
            }

            try { await RunGitAsync(targetWorktree, "reset --hard HEAD", CancellationToken.None); } catch { }

            try
            {
                await RunGitAsync(targetWorktree, $"merge --no-ff -m \"{commitMessage.Replace("\"", "\\\"")}\" {sourceBranch}", ct);
                return true;
            }
            catch (InvalidOperationException)
            {
                try { await RunGitAsync(targetWorktree, "merge --abort", CancellationToken.None); } catch { }
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rebase fallback failed for {Source}→{Target}", sourceBranch, targetBranch);
            return false;
        }
        finally
        {
            await SafeRemoveWorktreeAsync(rebaseWorktree);
        }
    }

    /// <summary>
    /// Rebase sourceBranch onto targetBranch without merging.
    /// Used by LocalPullRequestService.UpdateBranchAsync to resolve conflicts
    /// before the next merge attempt.
    /// Thread-safe: concurrent rebases on the same branch are serialized.
    /// </summary>
    public async Task RebaseBranchOntoAsync(string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null)
            throw new InvalidOperationException("Bare repo not initialized");

        // Prune stale worktrees from prior crashes before creating new ones
        await PruneStaleWorktreesAsync();

        // Serialize concurrent rebases on the same source branch to prevent
        // "branch is already checked out" errors from parallel worktree creation.
        var semaphore = _branchLocks.GetOrAdd(sourceBranch, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            var rebaseWorktree = Path.Combine(Path.GetTempPath(), $"vdt-rebase-{Guid.NewGuid():N}");
            try
            {
                await RunGitAsync(_bareRepoPath, $"worktree add \"{rebaseWorktree}\" {sourceBranch}", ct);
                await RunGitAsync(rebaseWorktree, "config user.name \"VirtualDevTeam\"", ct);
                await RunGitAsync(rebaseWorktree, "config user.email \"virtualdevteam@noreply.github.com\"", ct);
                await RunGitAsync(rebaseWorktree, $"rebase {targetBranch}", ct);

                // Update the branch ref in the bare repo to point to the rebased commits.
                // Without this, the worktree's HEAD moves but the branch pointer stays at
                // the pre-rebase commit, making the rebase have no effect on the next merge.
                var rebasedSha = (await RunGitCaptureAsync(rebaseWorktree, "rev-parse HEAD", ct)).Trim();
                if (!string.IsNullOrWhiteSpace(rebasedSha))
                {
                    // Detach HEAD in the worktree first — git refuses to force-update a branch
                    // that is checked out in any worktree. Detaching frees the branch name so
                    // the subsequent branch -f succeeds.
                    try { await RunGitAsync(rebaseWorktree, $"checkout --detach {rebasedSha}", ct); } catch { }
                    await RunGitAsync(_bareRepoPath, $"branch -f {sourceBranch} {rebasedSha}", ct);
                }

                _logger.LogInformation("Rebased {Source} onto {Target} in local bare repo (new HEAD: {Sha})",
                    sourceBranch, targetBranch, rebasedSha?[..Math.Min(rebasedSha.Length, 8)]);
            }
            finally
            {
                await SafeRemoveWorktreeAsync(rebaseWorktree);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Merge targetBranch INTO sourceBranch (like GitHub's "Update branch" button).
    /// Creates a merge commit on the source branch incorporating changes from target.
    /// This is the non-destructive alternative to rebase — it preserves history and
    /// handles conflicts that rebase cannot (e.g., when multiple commits touch the same file).
    /// </summary>
    public async Task MergeBranchIntoAsync(string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null)
            throw new InvalidOperationException("Bare repo not initialized");

        // Prune stale worktrees from prior crashes before creating new ones
        await PruneStaleWorktreesAsync();

        var semaphore = _branchLocks.GetOrAdd(sourceBranch, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            var mergeWorktree = Path.Combine(Path.GetTempPath(), $"vdt-merge-into-{Guid.NewGuid():N}");
            try
            {
                await RunGitAsync(_bareRepoPath, $"worktree add \"{mergeWorktree}\" {sourceBranch}", ct);
                await RunGitAsync(mergeWorktree, "config user.name \"VirtualDevTeam\"", ct);
                await RunGitAsync(mergeWorktree, "config user.email \"virtualdevteam@noreply.github.com\"", ct);

                try
                {
                    await RunGitAsync(mergeWorktree,
                        $"merge --no-edit -m \"Merge {targetBranch} into {sourceBranch}\" {targetBranch}", ct);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
                                                          || ex.Message.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase))
                {
                    // Most agent conflicts are ADDITIVE — both sides add new lines to shared
                    // files (Program.cs DI registrations, CSS rules, imports). Resolve by
                    // accepting main's version for conflicting files. The PR's unique new files
                    // are unaffected. This is far better than re-implementing the entire task
                    // from scratch (30-60 min wasted per conflict).
                    _logger.LogInformation(
                        "Merge {Target} into {Source} had conflicts — accepting target version to unblock",
                        targetBranch, sourceBranch);

                    string conflictedFiles = "";
                    try
                    {
                        conflictedFiles = (await RunGitCaptureAsync(mergeWorktree, "diff --name-only --diff-filter=U", ct)).Trim();
                        _logger.LogWarning("Conflicted files during update-branch: {Files}", conflictedFiles);
                    }
                    catch { /* best effort */ }

                    try
                    {
                        await RunGitAsync(mergeWorktree, "checkout --theirs .", ct);
                        await RunGitAsync(mergeWorktree, "add -A", ct);

                        var commitMsg = $"Merge {targetBranch} into {sourceBranch} (auto-resolved)";
                        if (!string.IsNullOrEmpty(conflictedFiles))
                            commitMsg += $"\\n\\nConflicted files (accepted {targetBranch} version):\\n{conflictedFiles}";

                        await RunGitAsync(mergeWorktree,
                            $"commit --no-edit -m \"{commitMsg.Replace("\"", "\\\"")}\"", ct);

                        _logger.LogInformation(
                            "Auto-resolved merge {Target} INTO {Source} — accepted target for conflicted files",
                            targetBranch, sourceBranch);
                    }
                    catch (Exception resolveEx)
                    {
                        _logger.LogWarning(resolveEx, "Auto-resolve failed, aborting merge");
                        try { await RunGitAsync(mergeWorktree, "merge --abort", CancellationToken.None); } catch { }
                        throw new InvalidOperationException(
                            $"Merge conflict: {targetBranch} into {sourceBranch}. Auto-resolve failed.", ex);
                    }
                }

                _logger.LogInformation("Merged {Target} INTO {Source} in local bare repo (update-branch)",
                    targetBranch, sourceBranch);
            }
            finally
            {
                await SafeRemoveWorktreeAsync(mergeWorktree);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Get the HEAD SHA for a branch in the bare repo.</summary>
    public async Task<string?> GetBranchHeadAsync(string branchName, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return null;
        try
        {
            var output = await RunGitCaptureAsync(_bareRepoPath, $"rev-parse refs/heads/{branchName}", ct);
            return output?.Trim();
        }
        catch { return null; }
    }

    /// <summary>List all branches in the bare repo.</summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return Array.Empty<string>();
        var output = await RunGitCaptureAsync(_bareRepoPath, "branch --list --format=%(refname:short)", ct);
        return output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
    }

    /// <summary>Delete a branch from the bare repo.</summary>
    public async Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return;
        await RunGitAsync(_bareRepoPath, $"branch -D {branchName}", ct);
    }

    /// <summary>Compute the diff between two refs (for PR file change listing).</summary>
    public async Task<string> GetDiffAsync(string baseRef, string headRef, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return "";
        return await RunGitCaptureAsync(_bareRepoPath, $"diff {baseRef}..{headRef} --stat", ct) ?? "";
    }

    /// <summary>Compute the diff with numstat for machine parsing (additions/deletions per file).</summary>
    public async Task<string> GetDiffNumstatAsync(string baseRef, string headRef, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return "";
        return await RunGitCaptureAsync(_bareRepoPath, $"diff {baseRef}..{headRef} --numstat", ct) ?? "";
    }

    /// <summary>Get the unified patch content for a single file between two refs.</summary>
    public async Task<string?> GetFilePatchAsync(string baseRef, string headRef, string filePath, CancellationToken ct = default)
    {
        if (!_initialized || _bareRepoPath is null) return null;
        try
        {
            return await RunGitCaptureAsync(_bareRepoPath, $"diff {baseRef}..{headRef} -- \"{filePath}\"", ct);
        }
        catch (InvalidOperationException)
        {
            // File may not exist in one of the refs (e.g., new file vs deleted file)
            return null;
        }
    }

    /// <summary>
    /// Prune stale worktrees and set receive.denyCurrentBranch=ignore so that
    /// pushes succeed even when a branch is checked out in a leftover worktree
    /// (e.g., after snapshot restore or crash recovery).
    /// </summary>
    private async Task PruneAndAllowPushesAsync(string repoDir, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(repoDir, "worktree prune", ct);
            await RunGitAsync(repoDir, "config receive.denyCurrentBranch ignore", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalBareRepoManager: failed to prune/configure bare repo (non-fatal)");
        }
    }

    /// <summary>
    /// Prune stale worktree references before creating new ones.
    /// Cleans up leftover temp directories from prior crashes and removes
    /// their git metadata so branches are freed for new worktree checkouts.
    /// </summary>
    private async Task PruneStaleWorktreesAsync()
    {
        if (_bareRepoPath is null) return;
        try
        {
            // Clean up any orphaned vdt-merge/vdt-rebase temp directories
            var tempDir = Path.GetTempPath();
            foreach (var prefix in new[] { "vdt-merge-", "vdt-rebase-" })
            {
                foreach (var dir in Directory.GetDirectories(tempDir, $"{prefix}*"))
                {
                    try
                    {
                        // Only remove dirs that are NOT currently being used by this process
                        // (they have .git files pointing back to the bare repo)
                        var gitFile = Path.Combine(dir, ".git");
                        if (File.Exists(gitFile) && File.ReadAllText(gitFile).Contains(_bareRepoPath.Replace("\\", "/")))
                        {
                            Directory.Delete(dir, recursive: true);
                            _logger.LogDebug("Cleaned up stale worktree dir: {Dir}", dir);
                        }
                    }
                    catch { /* best effort — dir may be locked by another process */ }
                }
            }

            await RunGitAsync(_bareRepoPath, "worktree prune", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Worktree prune failed (non-fatal)");
        }
    }

    /// <summary>
    /// Robustly remove a temp worktree: detach HEAD to free the branch,
    /// remove via git, then delete the physical directory as fallback.
    /// </summary>
    private async Task SafeRemoveWorktreeAsync(string worktreePath)
    {
        if (_bareRepoPath is null) return;
        try
        {
            // Detach HEAD first so the branch is freed even if worktree remove fails
            try { await RunGitAsync(worktreePath, "checkout --detach HEAD", CancellationToken.None); } catch { }

            // Try git worktree remove
            try { await RunGitAsync(_bareRepoPath, $"worktree remove \"{worktreePath}\" --force", CancellationToken.None); } catch { }

            // Physical dir cleanup as fallback
            if (Directory.Exists(worktreePath))
            {
                try { Directory.Delete(worktreePath, recursive: true); } catch { }
            }

            // Final prune to clean any remaining metadata
            try { await RunGitAsync(_bareRepoPath, "worktree prune", CancellationToken.None); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fully clean up worktree {Path}", worktreePath);
        }
    }

    private async Task RunGitAsync(string workDir, string args, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Allow bare repo operations (git safe.bareRepository=explicit blocks by default)
        if (workDir == _bareRepoPath)
            psi.Environment["GIT_DIR"] = workDir;

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"git {args} failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    private async Task<string> RunGitCaptureAsync(string workDir, string args, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Allow bare repo operations
        if (workDir == _bareRepoPath)
            psi.Environment["GIT_DIR"] = workDir;

        using var proc = Process.Start(psi)!;
        // Read stdout and stderr CONCURRENTLY to avoid pipe deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
        var output = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args} failed (exit {proc.ExitCode}): {stderr}");
        }
        return output;
    }

    /// <summary>Strip credentials from git URLs before logging.</summary>
    private static string RedactUrl(string url)
    {
        // Matches https://user:token@host/... or https://x-access-token:ghp_xxx@host/...
        var idx = url.IndexOf('@');
        if (idx > 0)
        {
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0 && schemeEnd < idx)
                return url[..(schemeEnd + 3)] + "***@" + url[(idx + 1)..];
        }
        return url;
    }
}
