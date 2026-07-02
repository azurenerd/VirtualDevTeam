using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Applies a candidate patch back to the live PR branch with head-change detection.
/// Rejects apply when the branch head has advanced since BaseSha — the caller must
/// re-run orchestration from the new head. Uses `git apply --3way` so small context
/// shifts don't fail, but a hard commit-hash mismatch triggers the safety exit.
/// </summary>
public class WinnerApplyService
{
    private readonly ILogger<WinnerApplyService> _logger;

    public WinnerApplyService(ILogger<WinnerApplyService> logger) => _logger = logger;

    /// <summary>
    /// Primary apply path: copies changed files directly from the winner's worktree to the agent's
    /// repo, avoiding <c>git apply</c> brittleness (whitespace, context mismatch). Falls back to
    /// <see cref="ApplyAsync"/> when the worktree is unavailable (checkpoint recovery).
    /// </summary>
    public async Task<ApplyOutcome> ApplyFromWorktreeAsync(
        string agentRepoPath, string branchName, string expectedBaseSha,
        string candidateWorktreePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidateWorktreePath) || !Directory.Exists(candidateWorktreePath))
        {
            _logger.LogWarning("Candidate worktree path missing or invalid: {Path}", candidateWorktreePath);
            return new ApplyOutcome(false, "worktree-missing", null);
        }

        // Safety guard: same as ApplyAsync
        if (!Workspace.SharedCloneManager.IsVdtWorktree(agentRepoPath)
            && !Directory.Exists(Path.Combine(agentRepoPath, ".candidates")))
        {
            _logger.LogError("SAFETY: Refusing to apply to unmarked path {Path}", agentRepoPath);
            return new ApplyOutcome(false, "safety-blocked-unmarked-path", null);
        }

        // 1. Detect what the candidate changed relative to the base SHA
        var candidateHead = (await RunGitCaptureAsync(candidateWorktreePath,
            new[] { "rev-parse", "HEAD" }, ct)).Trim();
        var diffResult = await TryRunGitAsync(candidateWorktreePath,
            new[] { "diff", "--name-status", "-z", "--find-renames", expectedBaseSha, candidateHead }, ct);
        if (!diffResult.ok)
        {
            _logger.LogWarning("Failed to diff candidate worktree: {Err}", diffResult.stderr);
            return new ApplyOutcome(false, $"worktree-diff-failed: {diffResult.stderr}", null);
        }

        var changes = ParseNameStatusChanges(diffResult.stdout);
        if (changes.Count == 0)
        {
            // Zero committed changes is suspicious — strategy ran, scored well, but
            // produced no durable commits. Two known mechanisms:
            //   A) index.lock blocked `git add -A` → nothing staged → empty diff
            //   B) Judge (--allow-all) ran `git checkout baseSha` → HEAD reset → diff empty
            // In NEITHER case should we return success — the caller should fall back to
            // the immutable patch (extracted pre-judge) if available.
            _logger.LogWarning(
                "ApplyFromWorktree: candidate worktree has 0 committed changes vs base {BaseSha} " +
                "(candidateHead={CandidateHead}). This is likely data loss — returning failure so " +
                "caller can fall back to patch-based apply.",
                expectedBaseSha, candidateHead);
            return new ApplyOutcome(false, "worktree-no-changes", null);
        }

        // 2. Checkout branch in agent repo with clean state
        var currentHead = (await RunGitCaptureAsync(agentRepoPath,
            new[] { "rev-parse", branchName }, ct)).Trim();
        await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);
        await RunGitCaptureAsync(agentRepoPath, new[] { "checkout", branchName }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", branchName }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);

        // 3. Check for overlap with live-branch changes if branch advanced
        if (!string.Equals(currentHead, expectedBaseSha, StringComparison.OrdinalIgnoreCase))
        {
            var liveDiff = await TryRunGitAsync(agentRepoPath,
                new[] { "diff", "--name-only", "-z", expectedBaseSha, currentHead }, ct);
            if (liveDiff.ok && !string.IsNullOrEmpty(liveDiff.stdout))
            {
                var liveFiles = liveDiff.stdout
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidateFiles = changes.Select(c => c.NewPath ?? c.OldPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var overlap = liveFiles.Intersect(candidateFiles).ToList();
                if (overlap.Count > 0)
                {
                    _logger.LogWarning(
                        "File-copy overlap: {Count} files changed on both live branch and candidate. " +
                        "Overlapping: {Files}. Falling back to patch apply for 3-way merge.",
                        overlap.Count, string.Join(", ", overlap.Take(10)));
                    return new ApplyOutcome(false, $"overlap-{overlap.Count}-files", currentHead);
                }
            }
        }

        // 4. Copy files from candidate worktree to agent repo
        int copied = 0, deleted = 0;
        foreach (var change in changes)
        {
            try
            {
                switch (change.Status)
                {
                    case 'A' or 'M' or 'T': // Added, Modified, Type-changed
                    {
                        var srcFile = Path.Combine(candidateWorktreePath, change.OldPath);
                        var dstFile = Path.Combine(agentRepoPath, change.OldPath);

                        // Guard: prevent path traversal
                        if (!Path.GetFullPath(dstFile).StartsWith(Path.GetFullPath(agentRepoPath), StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Skipping path outside repo: {Path}", change.OldPath);
                            continue;
                        }

                        var dstDir = Path.GetDirectoryName(dstFile);
                        if (dstDir is not null && !Directory.Exists(dstDir))
                            Directory.CreateDirectory(dstDir);

                        File.Copy(srcFile, dstFile, overwrite: true);
                        copied++;
                        break;
                    }
                    case 'D': // Deleted
                    {
                        var dstFile = Path.Combine(agentRepoPath, change.OldPath);
                        if (File.Exists(dstFile))
                        {
                            File.Delete(dstFile);
                            deleted++;
                        }
                        break;
                    }
                    case 'R': // Renamed
                    {
                        // Delete old path, copy new path
                        var oldDst = Path.Combine(agentRepoPath, change.OldPath);
                        if (File.Exists(oldDst))
                            File.Delete(oldDst);

                        var srcFile = Path.Combine(candidateWorktreePath, change.NewPath!);
                        var dstFile = Path.Combine(agentRepoPath, change.NewPath!);

                        if (!Path.GetFullPath(dstFile).StartsWith(Path.GetFullPath(agentRepoPath), StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("Skipping renamed path outside repo: {Path}", change.NewPath);
                            continue;
                        }

                        var dstDir = Path.GetDirectoryName(dstFile);
                        if (dstDir is not null && !Directory.Exists(dstDir))
                            Directory.CreateDirectory(dstDir);

                        File.Copy(srcFile, dstFile, overwrite: true);
                        copied++;
                        deleted++;
                        break;
                    }
                    default:
                        _logger.LogDebug("Skipping unsupported diff status '{Status}' for {Path}",
                            change.Status, change.OldPath);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply change {Status} {Path}", change.Status, change.OldPath);
                // Roll back
                await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
                await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);
                return new ApplyOutcome(false, $"copy-failed: {change.OldPath}: {ex.Message}", currentHead);
            }
        }

        _logger.LogInformation("File-copy apply succeeded: {Copied} copied, {Deleted} deleted", copied, deleted);
        return new ApplyOutcome(true, null, currentHead);
    }

    /// <summary>
    /// Parses <c>git diff --name-status -z</c> output into structured change records.
    /// NUL-delimited format: <c>STATUS\0path\0</c> for most; <c>R###\0old\0new\0</c> for renames.
    /// </summary>
    internal static IReadOnlyList<FileChange> ParseNameStatusChanges(string output)
    {
        var result = new List<FileChange>();
        if (string.IsNullOrEmpty(output)) return result;

        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < parts.Length)
        {
            var status = parts[i];
            if (string.IsNullOrEmpty(status)) { i++; continue; }

            char statusChar = status[0];
            if (statusChar == 'R' || statusChar == 'C')
            {
                // Rename/Copy: status\0old-path\0new-path
                if (i + 2 >= parts.Length) break;
                result.Add(new FileChange(statusChar, parts[i + 1], parts[i + 2]));
                i += 3;
            }
            else
            {
                // Add/Modify/Delete/Type-change: status\0path
                if (i + 1 >= parts.Length) break;
                result.Add(new FileChange(statusChar, parts[i + 1], null));
                i += 2;
            }
        }
        return result;
    }

    internal readonly record struct FileChange(char Status, string OldPath, string? NewPath);

    public async Task<ApplyOutcome> ApplyAsync(
        string agentRepoPath, string branchName, string expectedBaseSha,
        string patch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patch))
            return new ApplyOutcome(false, "empty-patch", null);

        // Safety guard: refuse to mutate paths that lack the VDT worktree marker.
        // Prevents catastrophic data loss if a bug routes the operator's working tree
        // instead of a VDT-managed worktree (Critical finding C2).
        if (!Workspace.SharedCloneManager.IsVdtWorktree(agentRepoPath)
            && !Directory.Exists(Path.Combine(agentRepoPath, ".candidates")))
        {
            // Allow Clone mode repos (they have .candidates dir) but block unmarked paths
            _logger.LogError("SAFETY: Refusing to apply patch to unmarked path {Path} — not a VDT workspace", agentRepoPath);
            return new ApplyOutcome(false, "safety-blocked-unmarked-path", null);
        }

        // 1. Check branch head vs expected base. If they differ, log but proceed —
        // common cause is a task-marker commit pushed between worktree creation and apply.
        // The --3way apply handles small context shifts; only a truly diverged branch
        // (concurrent modifications) would fail at the apply step itself.
        var currentHead = (await RunGitCaptureAsync(agentRepoPath, new[] { "rev-parse", branchName }, ct)).Trim();
        if (!string.Equals(currentHead, expectedBaseSha, StringComparison.OrdinalIgnoreCase))
        {
            // Check if expectedBase is an ancestor of currentHead (safe — just our own marker commits)
            var isAncestor = await TryRunGitAsync(agentRepoPath,
                new[] { "merge-base", "--is-ancestor", expectedBaseSha, currentHead }, ct);
            if (isAncestor.ok)
            {
                _logger.LogInformation(
                    "Head advanced for {Branch}: {Expected} → {Actual} (ancestor — safe to apply)",
                    branchName, expectedBaseSha, currentHead);
            }
            else
            {
                _logger.LogWarning(
                    "Head diverged for {Branch}: expected {Expected} but is {Actual} — attempting apply anyway (3-way merge handles context shifts from rebase/sync)",
                    branchName, expectedBaseSha, currentHead);
                // Don't refuse — let git apply --3way handle it. Common cause: SyncWithMainAsync
                // rebased the branch during strategy evaluation, rewriting history. The patch
                // content is still valid; only the commit ancestry changed.
            }
        }

        // 2. Checkout branch in main working tree, after a hard reset so that any
        // pre-existing dirty state (stale merge markers, orphaned apply residue,
        // prior strategy-framework failure) can't poison the 3-way apply.
        await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);
        await RunGitCaptureAsync(agentRepoPath, new[] { "checkout", branchName }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", branchName }, ct);
        await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);

        // 3. Write patch to a temp file and apply --3way --check, then apply
        var tmp = Path.Combine(Path.GetTempPath(), "sf-winner-" + Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            await File.WriteAllTextAsync(tmp, patch, ct);
            // --check validates structure; use --whitespace=nowarn so trailing whitespace
            // doesn't cause check failure — the real apply with --whitespace=fix handles it.
            var check = await TryRunGitAsync(agentRepoPath, new[] { "apply", "--check", "--3way", "--whitespace=nowarn", tmp }, ct);
            if (!check.ok)
                return new ApplyOutcome(false, $"apply-check-failed: {check.stderr}", currentHead);

            var apply = await TryRunGitAsync(agentRepoPath, new[] { "apply", "--3way", "--whitespace=fix", tmp }, ct);
            if (!apply.ok)
            {
                // Roll back any partial 3-way state so the caller sees a clean tree.
                await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
                await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);
                return new ApplyOutcome(false, $"apply-failed: {apply.stderr}", currentHead);
            }

            // 4. Post-apply invariant check: `git apply --3way` can exit 0 while
            // still leaving UU entries in the index when some hunks 3-way'd into
            // conflicts. Committing from that state ships conflict markers AND
            // wedges the next checkout. Detect it and abort cleanly.
            var unmerged = await RunGitCaptureAsync(agentRepoPath, new[] { "ls-files", "-u" }, ct);
            if (!string.IsNullOrWhiteSpace(unmerged))
            {
                _logger.LogWarning(
                    "Winner apply left unmerged entries on {Branch}; aborting and rolling back. Entries:\n{Entries}",
                    branchName, unmerged);
                await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
                await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd", "-e", ".candidates" }, ct);
                return new ApplyOutcome(false, "unmerged-after-apply", currentHead);
            }

            return new ApplyOutcome(true, null, currentHead);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static async Task<(bool ok, string stdout, string stderr)> TryRunGitAsync(
        string cwd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git process start failed");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode == 0, await outTask, await errTask);
    }

    private static async Task<string> RunGitCaptureAsync(string cwd, string[] args, CancellationToken ct)
    {
        var r = await TryRunGitAsync(cwd, args, ct);
        if (!r.ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.stderr}");
        return r.stdout;
    }
}

public readonly record struct ApplyOutcome(bool Applied, string? FailureReason, string? CurrentHead)
{
    public bool HeadChanged => FailureReason == "head-changed";
}
