using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Per-candidate git worktree manager. Creates a worktree at the task's base SHA,
/// hardens its git config so that candidates (including agentic --allow-all ones)
/// cannot push, cannot read a tokenized remote url, cannot run hooks, and cannot
/// use a system or user-level git config.
/// </summary>
public class GitWorktreeManager
{
    private readonly ILogger<GitWorktreeManager> _logger;

    // Per-repo lock serializing the pre-add phase (prune + extensions.worktreeConfig
    // + worktree add). Concurrent candidates that share the same agentRepoPath race
    // on `.git/config.lock` and `.git/index.lock` during these operations; val-e2e
    // captured the failure as "warning: unable to access '.git/config': Permission
    // denied; fatal: unknown error occurred while reading the configuration files"
    // which causes `git worktree add` to return 128 and the entire candidate to
    // fail before ExecuteAsync runs. Post-add, each candidate writes to its OWN
    // per-worktree config file, so the bottleneck here is just the shared main
    // repo's config — small, fast, uncontended enough that a plain semaphore is fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new();

    public GitWorktreeManager(ILogger<GitWorktreeManager> logger) => _logger = logger;

    private static SemaphoreSlim GetRepoLock(string agentRepoPath)
    {
        // In worktree-based modes, multiple agents may have different worktree paths
        // but share the same .git object store. Use git-common-dir as the lock key
        // to prevent .git/config.lock races (Critical finding C1).
        var commonDir = ResolveGitCommonDir(agentRepoPath);
        return _repoLocks.GetOrAdd(commonDir, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Resolve the shared .git directory for lock coordination.
    /// In worktree checkouts, git-common-dir points to the main repo's .git;
    /// in regular clones, it equals the local .git dir.
    /// </summary>
    internal static string ResolveGitCommonDir(string repoPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --git-common-dir")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return Path.GetFullPath(repoPath);
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return Path.IsPathRooted(output)
                    ? Path.GetFullPath(output)
                    : Path.GetFullPath(Path.Combine(repoPath, output));
            }
        }
        catch { /* fall through */ }
        return Path.GetFullPath(repoPath);
    }

    /// <summary>
    /// Creates a worktree at <paramref name="baseSha"/> under <c>{agentRepoPath}/{candidateDirName}/{taskId}/{strategyId}</c>.
    /// Returns a disposable handle that cleans up on disposal.
    /// </summary>
    public async Task<WorktreeHandle> CreateAsync(
        string agentRepoPath, string candidateDirName, string taskId, string strategyId, string baseSha,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentRepoPath);
        ArgumentException.ThrowIfNullOrEmpty(baseSha);

        var candidatesRoot = Path.Combine(agentRepoPath, candidateDirName);
        Directory.CreateDirectory(candidatesRoot);

        // Use a unique per-invocation suffix on the worktree path. This decouples
        // cleanup from path reuse: on Windows, copilot/MCP subprocesses can hold file
        // handles in a worktree after the outer strategy has completed, causing
        // `Directory.Delete` to fail and leaving a stale dir. Previously, the NEXT
        // task's `git worktree add` would then fail with "already exists". With a
        // fresh suffix per invocation, the new worktree can't collide with leftovers.
        // Stale siblings are tolerated — they're best-effort cleaned on dispose and
        // fully removed by the OS when the parent dir is pruned between runs.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var worktreePath = Path.Combine(
            candidatesRoot, SanitizeId(taskId), $"{SanitizeId(strategyId)}-{uniqueSuffix}");

        // Serialize the pre-add phase per agentRepoPath to avoid races on
        // `.git/config.lock` when concurrent candidates share the same main repo.
        // See _repoLocks comment above. Fine-grained (just the git-config +
        // worktree-add block) so candidates still run their ExecuteAsync work
        // in parallel after setup.
        var repoLock = GetRepoLock(agentRepoPath);
        await repoLock.WaitAsync(ct);
        try
        {
            // Best-effort prune of stale worktree metadata before adding. Catches the
            // case where a previous run crashed before dispose ran, leaving a dangling
            // entry in `.git/worktrees/` that `git worktree add` would reject.
            try { await RunGitAsync(agentRepoPath, new[] { "worktree", "prune" }, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pre-add worktree prune failed (non-fatal)"); }

            // Enable per-worktree config BEFORE adding any worktree so hardening writes land
            // in each worktree's own config.worktree file rather than the shared main
            // repo config. Idempotent — writing the same key/value repeatedly is a no-op.
            // Without this, `git config --local` in a linked worktree stomps the main repo's
            // config (git < 2.20 behavior) and concurrent candidates race for the last-writer
            // to win. `extensions.worktreeConfig=true` is a repo-wide flag that makes
            // `git config --worktree` write to `<gitdir>/worktrees/<name>/config.worktree`
            // for linked worktrees.
            await ConfigWithRetryAsync(agentRepoPath, new[] { "config", "--local", "extensions.worktreeConfig", "true" }, ct);

            // Enable long paths on Windows to avoid MAX_PATH failures in deep worktree paths.
            // Without this, `git worktree add` fails with "Filename too long" errors when the
            // combined path (workspace root + .candidates + taskId + strategyId + file) exceeds 260 chars.
            await ConfigWithRetryAsync(agentRepoPath, new[] { "config", "--local", "core.longpaths", "true" }, ct);

            await RunGitAsync(agentRepoPath, new[] { "worktree", "add", "--detach", worktreePath, baseSha }, ct);
        }
        finally
        {
            repoLock.Release();
        }

        // Sandbox hardening — writes to THIS worktree's config.worktree file only,
        // isolated from other concurrent candidates and from the main repo config.
        // Retry a few times with backoff — Windows can transiently lock the config
        // file right after `worktree add`.
        await ConfigWithRetryAsync(worktreePath, new[] { "config", "--worktree", "credential.helper", "" }, ct);
        await ConfigWithRetryAsync(worktreePath, new[] { "config", "--worktree", "push.default", "nothing" }, ct);
        await ConfigWithRetryAsync(worktreePath, new[] { "config", "--worktree", "core.hooksPath", "" }, ct);
        // origin.pushurl is best-effort — the remote may not exist (e.g. fresh local repos).
        try { await ConfigWithRetryAsync(worktreePath, new[] { "config", "--worktree", "remote.origin.pushurl", "file:///dev/null" }, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Skipping origin.pushurl hardening (remote missing?)"); }

        // Neutralize launchSettings.json in the worktree so that any `dotnet run` the
        // agent (Squad / mcp-enhanced / baseline) decides to invoke does NOT auto-launch
        // a real browser via `launchBrowser: true`. Mirrors PlaywrightRunner.NeutralizeLaunchSettings
        // — but applied to candidate worktrees, which PlaywrightRunner never touches.
        // The worktree is throwaway; we don't restore on disposal.
        NeutralizeLaunchSettingsForWorktree(worktreePath);

        _logger.LogInformation("Created worktree {Path} @ {Sha} for strategy {Strategy}",
            worktreePath, baseSha, strategyId);
        return new WorktreeHandle(this, agentRepoPath, worktreePath, baseSha, taskId, strategyId);
    }

    /// <summary>
    /// Move every <c>launchSettings.json</c> in the worktree aside (rename to
    /// <c>.candidate-bak</c>) so that <c>dotnet run</c> in this worktree falls back to
    /// kestrel defaults instead of auto-launching a browser. Skips test-project
    /// launchSettings (they don't trigger browser launch and the agent may need them).
    /// Best-effort: failures are logged at debug level and never thrown.
    /// </summary>
    private void NeutralizeLaunchSettingsForWorktree(string worktreePath)
    {
        try
        {
            var launchSettingsFiles = Directory.EnumerateFiles(
                worktreePath, "launchSettings.json", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(worktreePath, f)
                    .Contains("test", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in launchSettingsFiles)
            {
                try
                {
                    var backupPath = file + ".candidate-bak";
                    if (!File.Exists(backupPath))
                        File.Move(file, backupPath);
                    else
                        File.Delete(file);
                    _logger.LogInformation(
                        "Neutralized {File} in candidate worktree to suppress browser auto-launch",
                        Path.GetRelativePath(worktreePath, file));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to neutralize {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning launchSettings.json in {Path}", worktreePath);
        }
    }

    /// <summary>
    /// Restore launchSettings.json files moved aside by <see cref="NeutralizeLaunchSettingsForWorktree"/>.
    /// Called before patch extraction so the deletion doesn't leak into the diff. Safe to
    /// call when no neutralization happened (no-op).
    /// </summary>
    private void RestoreNeutralizedLaunchSettings(string worktreePath)
    {
        try
        {
            var backups = Directory.EnumerateFiles(
                worktreePath, "*.candidate-bak", SearchOption.AllDirectories).ToList();

            foreach (var backup in backups)
            {
                try
                {
                    var original = backup[..^".candidate-bak".Length];
                    if (File.Exists(original))
                    {
                        // The agent recreated/edited launchSettings.json after we moved it
                        // aside. Keep the agent's version (it may legitimately need a launch
                        // profile) and just discard our backup. Browser auto-launch is the
                        // worst-case outcome of leaving the agent's file in place.
                        File.Delete(backup);
                        _logger.LogDebug(
                            "Discarded launchSettings backup {File} — agent recreated it",
                            Path.GetRelativePath(worktreePath, backup));
                        continue;
                    }
                    File.Move(backup, original);
                    _logger.LogDebug("Restored launchSettings {File} before patch extraction",
                        Path.GetRelativePath(worktreePath, original));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to restore {Backup}", backup);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error restoring .candidate-bak files in {Path}", worktreePath);
        }
    }

    /// <summary>
    /// Extracts a patch (binary-safe) for all changes in the worktree relative to
    /// the <paramref name="baseSha"/> the worktree was created at. Returns empty
    /// string when no changes.
    ///
    /// <para>
    /// Why <c>baseSha</c> and not <c>HEAD</c>: some strategies — notably the
    /// agentic <c>copilot --allow-all</c> session — run <c>git add -A</c> +
    /// <c>git commit</c> themselves as part of their normal tool use. After
    /// such a run <c>git diff HEAD</c> is empty (all changes are already
    /// committed), but the candidate has produced real work. Diffing against
    /// the base SHA captures both cases uniformly:
    ///  - Strategy never commits → <c>git add -A</c> stages the working tree,
    ///    and <c>git diff base</c> sees the staged-but-uncommitted changes.
    ///  - Strategy committed during its run → <c>git diff base</c> walks from
    ///    the pre-run SHA to HEAD+index, covering everything in between.
    /// </para>
    ///
    /// <para>
    /// CRITICAL SECURITY NOTE: the worktree may have been mutated by a sandboxed
    /// agentic session — including its <c>.git/config</c>, <c>.gitattributes</c>,
    /// hooks, and filter configuration. We therefore run git with a full set of
    /// <c>-c</c> overrides that disable every config-controlled code-execution
    /// vector (external diff, textconv, LFS filters, custom hooks, attributes-
    /// file, mergetool drivers). Without these, a hostile worktree could run
    /// arbitrary host-side code during <c>git add</c>/<c>git diff</c>.
    /// </para>
    /// </summary>
    public async Task<string> ExtractPatchAsync(string worktreePath, string baseSha, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSha);

        // Restore any launchSettings.json we neutralized in CreateAsync. Otherwise the
        // file deletion would appear in the extracted patch and propagate to the engineer's
        // branch — deleting the file from the real workspace.
        RestoreNeutralizedLaunchSettings(worktreePath);
        // The agentic strategy materializes its per-session HOME / APPDATA /
        // LOCALAPPDATA sandbox under <worktree>/.sandbox/. Those trees can contain
        // deeply-nested copilot-CLI package files whose paths exceed Windows
        // MAX_PATH and would cause `git add -A` to fail with "Filename too long".
        // Telling git to ignore the sandbox via the worktree-local info/exclude
        // is the cleanest fix: it avoids (a) the "Filename too long" walk, and
        // (b) pathspec-exclude tricks — which themselves blow up with "paths are
        // ignored by one of your .gitignore files" when the user's global
        // core.excludesfile already lists .sandbox. The sandbox is scaffolding;
        // it is never part of the candidate's output.
        await EnsureFrameworkPathsExcludedAsync(worktreePath, ct);

        // Best-effort: stage any uncommitted/untracked work so `git diff base`
        // sees it. If the strategy already committed everything (agentic case),
        // `git add -A` is a cheap no-op.
        try
        {
            await RunGitHardenedAsync(worktreePath, new[] { "add", "-A" }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExtractPatch: git add -A failed in worktree {Path} — retrying after index.lock cleanup",
                worktreePath);

            // Retry once after clearing stale index.lock (common after Squad/CLI crashes).
            // For linked worktrees, .git is a FILE (not a directory), so the real lock
            // lives at <main-repo>/.git/worktrees/<name>/index.lock — not at
            // worktreePath/.git/index.lock. Use `git rev-parse --git-path` to resolve
            // the correct per-worktree path (same pattern as info/exclude at line 450).
            try
            {
                string? lockFile = null;
                try
                {
                    var lockRel = (await RunGitHardenedCaptureAsync(
                        worktreePath, new[] { "rev-parse", "--git-path", "index.lock" }, ct)).Trim();
                    lockFile = Path.IsPathRooted(lockRel)
                        ? lockRel
                        : Path.Combine(worktreePath, lockRel);
                }
                catch
                {
                    // Fallback: try legacy paths if rev-parse fails
                    var legacy1 = Path.Combine(worktreePath, ".git", "index.lock");
                    var legacy2 = Path.Combine(worktreePath, "index.lock");
                    lockFile = File.Exists(legacy1) ? legacy1 : File.Exists(legacy2) ? legacy2 : null;
                }

                if (lockFile is not null && File.Exists(lockFile))
                {
                    File.Delete(lockFile);
                    _logger.LogInformation("ExtractPatch: deleted stale index.lock at {LockPath}, retrying git add -A", lockFile);
                }
                await RunGitHardenedAsync(worktreePath, new[] { "add", "-A" }, ct);
                _logger.LogInformation("ExtractPatch: git add -A succeeded on retry");
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx,
                    "ExtractPatch: git add -A retry also failed in {Path} — diff will only reflect already-committed changes",
                    worktreePath);
            }
        }

        // PREVENTION LAYER 1 (cleanup-race fix, 2026-05-12): commit the staged
        // changes BEFORE extracting the patch. Uses `git diff --cached --name-only`
        // to detect STAGED changes only — NOT `git status --porcelain` which includes
        // untracked files (??). When git add -A fails silently and files remain
        // untracked, status --porcelain triggers a phantom empty commit via --allow-empty.
        try
        {
            var stagedFiles = await RunGitHardenedCaptureAsync(
                worktreePath, new[] { "diff", "--cached", "--name-only" }, ct);
            if (!string.IsNullOrWhiteSpace(stagedFiles))
            {
                _logger.LogDebug("ExtractPatch: {Count} staged file(s) — committing before patch extract",
                    stagedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
                await RunGitHardenedAsync(worktreePath, new[]
                {
                    "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
                    "-c", "user.email=framework@virtualdevteam.local",
                    "-c", "user.name=VirtualDevTeam Framework",
                    "commit", "--no-verify",
                    "-m", "framework: auto-commit pre-patch-extract",
                    "-m", $"Auto-committed by GitWorktreeManager.ExtractPatchAsync to make all working-tree changes durable in git history before patch extraction. Candidate: {Path.GetFileName(worktreePath)}.",
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExtractPatch: pre-patch auto-commit failed in worktree {Path} — patch will still be extracted but candidate work is at risk if cleanup races destroy the working tree",
                worktreePath);
        }

        // Build pathspec exclusions so that framework scaffolding committed by
        // the strategy CLI during its run (e.g., .squad/, .copilot/, .claude/)
        // is stripped from the patch. The info/exclude file only affects `git add`;
        // committed files still appear in `git diff base` without explicit excludes.
        var diffArgs = new List<string>
        {
            "diff", baseSha, "--binary", "--full-index", "--no-ext-diff", "--no-textconv",
            "--" , "." // start pathspec: include everything...
        };
        foreach (var excluded in FrameworkExcludePaths)
        {
            // Use :(exclude,glob) with **/ prefix for build-output directories so that
            // nested paths like "src/Project/bin/" are excluded, not just root-level "bin/".
            // Root-anchored framework dirs (.sandbox/, .copilot/) use simple ":!" prefix.
            var trimmed = excluded.TrimEnd('/');
            if (trimmed is "bin" or "obj" or "node_modules" or "TestResults" or "test-results" or ".vs")
                diffArgs.Add($":(exclude,glob)**/{trimmed}/**");
            else
                diffArgs.Add($":!{trimmed}");
        }

        var diff = await RunGitHardenedCaptureAsync(worktreePath, diffArgs.ToArray(), ct);

        if (diff.Length == 0)
        {
            // Keep a compact diagnostic trail for empty-patch investigations. Not
            // all empty diffs are bugs (e.g. a strategy that legitimately produced
            // no changes), but when they ARE unexpected we want enough context to
            // distinguish the reasons: did git see the files? Did HEAD advance?
            try
            {
                var porcelain = await RunGitHardenedCaptureAsync(
                    worktreePath, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, ct);
                var head = (await RunGitHardenedCaptureAsync(
                    worktreePath, new[] { "rev-parse", "--short", "HEAD" }, ct)).Trim();
                var shortBase = baseSha.Length >= 7 ? baseSha[..7] : baseSha;
                _logger.LogWarning(
                    "ExtractPatch produced EMPTY diff in {Path} (base={Base} HEAD={Head}) — porcelain-lines={PLen}, porcelain-head:\n{Porcelain}",
                    worktreePath, shortBase, head,
                    porcelain.Split('\n').Length,
                    porcelain.Length > 1024 ? porcelain[..1024] : porcelain);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ExtractPatch: empty-diff diagnostic failed");
            }
        }
        return diff;
    }

    /// <summary>
    /// Writes framework-specific directories to the worktree-local <c>info/exclude</c> file
    /// so that subsequent <c>git add -A</c> invocations skip agentic framework scaffolding
    /// without requiring pathspec-exclude tokens. Idempotent.
    /// Uses <c>git rev-parse --git-path info/exclude</c> so the correct path
    /// resolves for both normal repos and linked worktrees (where the per-
    /// worktree excludes live under <c>.git/worktrees/&lt;name&gt;/info/exclude</c>).
    /// </summary>
    internal static readonly string[] FrameworkExcludePaths = new[]
    {
        ".sandbox/",
        ".squad/",
        ".copilot/",
        ".claude/",
        ".github/agents/",
        ".github/workflows/",
        // Build output directories — strategies that run dotnet build, npm install,
        // etc. create these; they must never appear in the patch.
        "bin/",
        "obj/",
        "node_modules/",
        // Common test/build artifacts
        "TestResults/",
        "test-results/",
        ".vs/",
        // Per-strategy package caches. Agentic CLI sessions install Python tools
        // (e.g., for image processing during art tasks) which write deeply-nested
        // file trees under `pip/cache/http-v2/...` that EXCEED Windows MAX_PATH.
        // Without this exclusion, `git add -A` fails with "Filename too long" and
        // the patch comes back empty — losing the actual art output the agent produced.
        "pip/",
        ".cache/",
        ".npm/",
        // Backups created by NeutralizeLaunchSettingsForWorktree — restored before
        // patch extraction, but listed here as a defensive belt-and-suspenders.
        "*.candidate-bak",
        // Strategy candidate worktree directories. When multiple tasks run in parallel,
        // sibling candidate worktrees (.candidates-eval/T-21/squad-eval-*) contain .git
        // gitlink files (mode 160000) that trigger the symlink-or-gitlink safety gate.
        ".candidates/",
        ".candidates-eval/",
    };

    private async Task EnsureFrameworkPathsExcludedAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var excludeRel = (await RunGitHardenedCaptureAsync(
                worktreePath, new[] { "rev-parse", "--git-path", "info/exclude" }, ct)).Trim();
            if (string.IsNullOrWhiteSpace(excludeRel)) return;
            var excludeFull = Path.IsPathRooted(excludeRel)
                ? excludeRel
                : Path.Combine(worktreePath, excludeRel);
            var dir = Path.GetDirectoryName(excludeFull);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var existing = File.Exists(excludeFull)
                ? await File.ReadAllTextAsync(excludeFull, ct)
                : string.Empty;
            var existingLines = existing
                .Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .ToHashSet(StringComparer.Ordinal);

            var toAdd = FrameworkExcludePaths
                .Where(p => !existingLines.Contains(p) &&
                            !existingLines.Contains(p.TrimEnd('/')) &&
                            !existingLines.Contains("/" + p) &&
                            !existingLines.Contains("/" + p.TrimEnd('/')))
                .ToList();

            if (toAdd.Count > 0)
            {
                var prefix = (existing.Length == 0 || existing.EndsWith('\n')) ? string.Empty : "\n";
                await File.AppendAllTextAsync(excludeFull, prefix + string.Join("\n", toAdd) + "\n", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to add framework paths to worktree info/exclude; patch extraction may include framework scaffolding");
        }
    }

    /// <summary>
    /// Validates that no path in the patch touches a reserved prefix, writes to .git,
    /// or escapes the worktree (relative ../, absolute paths). Returns null when safe.
    /// </summary>
    public static string? ValidatePatchPaths(string patch, string reservedPathPrefix)
    {
        if (string.IsNullOrEmpty(patch)) return null;
        var normalizedReserved = reservedPathPrefix.Replace('\\', '/').TrimStart('/');
        string? currentDiffPath = null;
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                if (parts.Length < 4) { currentDiffPath = null; continue; }
                var aPath = parts[2].StartsWith("a/") ? parts[2][2..] : parts[2];
                var bPath = parts[3].StartsWith("b/") ? parts[3][2..] : parts[3];
                currentDiffPath = bPath;
                foreach (var path in new[] { aPath, bPath })
                {
                    var normalized = path.Replace('\\', '/');
                    if (normalized.StartsWith("../", StringComparison.Ordinal)
                        || normalized.Contains("/../", StringComparison.Ordinal)
                        || Path.IsPathRooted(normalized)
                        || normalized.StartsWith("/", StringComparison.Ordinal))
                    {
                        return $"path-escape: {path}";
                    }
                    if (!string.IsNullOrEmpty(normalizedReserved)
                        && normalized.StartsWith(normalizedReserved, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"reserved-path: {path}";
                    }
                    // Reject any path containing a ".git" segment anywhere (root OR nested).
                    // Nested repo metadata (e.g. src/.git/config) must never be written by
                    // a candidate strategy — it would corrupt the workspace or hide hooks.
                    var segments = normalized.Split('/');
                    foreach (var seg in segments)
                    {
                        if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"dotgit-write: {path}";
                        }
                    }
                }
                continue;
            }

            // Reject symlink/gitlink creation or mode-change-to-symlink within any diff. git
            // represents symlinks with mode 120000 and gitlinks (submodules) with 160000;
            // both are escape vectors that strategies should never need.
            if (line.StartsWith("new file mode 120000", StringComparison.Ordinal)
                || line.StartsWith("new mode 120000", StringComparison.Ordinal)
                || line.StartsWith("old mode 120000", StringComparison.Ordinal)
                || line.StartsWith("new file mode 160000", StringComparison.Ordinal)
                || line.StartsWith("new mode 160000", StringComparison.Ordinal))
            {
                return $"symlink-or-gitlink: {currentDiffPath ?? line.Trim()}";
            }
        }
        return null;
    }

    internal async Task RemoveWorktreeQuietAsync(string agentRepoPath, string worktreePath, CancellationToken ct)
    {
        await RemoveWorktreeQuietAsync(agentRepoPath, worktreePath, taskId: null, strategyId: null, ct);
    }

    internal async Task RemoveWorktreeQuietAsync(
        string agentRepoPath, string worktreePath, string? taskId, string? strategyId, CancellationToken ct)
    {
        // PREVENTION LAYER 3 (cleanup-race fix, 2026-05-12): preserve the worktree's
        // current HEAD as a stable ref BEFORE attempting any removal. If the worktree
        // contents are committed (Layer 1 ensures this for ExtractPatchAsync paths),
        // the commit stays git-reachable from `refs/candidates/{taskId}/{strategyId}`
        // even after the worktree is gone. Recovery requires no `git fsck` archaeology;
        // the eval / winner-apply paths can resurrect via this ref. Best-effort: if
        // anything fails (no taskId, ref-update fails, etc.), proceed with cleanup so
        // we don't strand the worktree forever.
        if (!string.IsNullOrEmpty(taskId) && !string.IsNullOrEmpty(strategyId))
        {
            try
            {
                var head = (await RunGitCaptureAsync(worktreePath, new[] { "rev-parse", "HEAD" }, ct)).Trim();
                if (!string.IsNullOrEmpty(head))
                {
                    var refName = $"refs/candidates/{SanitizeId(taskId)}/{SanitizeId(strategyId)}";
                    await RunGitAsync(agentRepoPath, new[] { "update-ref", refName, head }, ct);
                    _logger.LogDebug(
                        "Preserved candidate HEAD {Head} as {Ref} before worktree removal",
                        head[..Math.Min(8, head.Length)], refName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Best-effort candidate ref preservation failed for {Path} — worktree cleanup will continue",
                    worktreePath);
            }
        }

        // On Windows, lingering file handles from just-killed subprocesses can
        // cause `git worktree remove` or Directory.Delete to fail with sharing
        // violations. Retry with backoff before giving up. (p3-cleanup-impl)
        //
        // Retry schedule was originally 4 attempts @ 250/500/750ms (<1.5s total),
        // which was too short when MCP server child processes had just been
        // killed — OS file handles can hold locks for several seconds as the
        // descriptor table unwinds. Bumped to 6 attempts with exponential
        // backoff (250/500/1000/2000/4000ms ≈ 7.75s total) to give child-process
        // handles time to drain before giving up and leaving a ghost dir.
        // Ref: c-agents-file-lock follow-up.
        const int MaxAttempts = 6;
        Exception? lastException = null;
        var skipGitRemove = false;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (skipGitRemove) throw lastException!; // jump straight to fallback
                await RunGitAsync(agentRepoPath, new[] { "worktree", "remove", "--force", worktreePath }, ct);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;

                // "is not a working tree" means git has no record of this path —
                // the metadata is already gone, so there's nothing for `worktree
                // remove` to do. Skip retrying and go straight to the Directory.Delete
                // fallback to clean up any residual files/handles. Saves ~1s of
                // pointless retry backoff on the common stale-dir case.
                if (!skipGitRemove &&
                    ex.Message.IndexOf("is not a working tree", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    skipGitRemove = true;
                    attempt = MaxAttempts - 1; // force the fallback branch below
                }
                else if (attempt < MaxAttempts - 1)
                {
                    // Exponential backoff: 250, 500, 1000, 2000, 4000ms.
                    await Task.Delay(250 * (int)Math.Pow(2, attempt), ct);
                    continue;
                }

                _logger.LogWarning(ex, "git worktree remove failed after {Attempts} attempts; falling back to directory delete for {Path}", MaxAttempts, worktreePath);
                for (var delAttempt = 0; delAttempt < MaxAttempts; delAttempt++)
                {
                    try
                    {
                        if (Directory.Exists(worktreePath))
                            Directory.Delete(worktreePath, recursive: true);
                        break;
                    }
                    catch (IOException ioEx) when (delAttempt < MaxAttempts - 1)
                    {
                        _logger.LogDebug(ioEx, "Directory delete retry {Attempt} for {Path}", delAttempt + 1, worktreePath);
                        // Force .NET to release any managed file handles before retrying —
                        // MCP bridge clients or stray FileStreams we own may be the locker.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await Task.Delay(250 * (int)Math.Pow(2, delAttempt), ct);
                    }
                    catch (UnauthorizedAccessException uaEx) when (delAttempt < MaxAttempts - 1)
                    {
                        _logger.LogDebug(uaEx, "Directory delete access-retry {Attempt} for {Path}", delAttempt + 1, worktreePath);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await Task.Delay(250 * (int)Math.Pow(2, delAttempt), ct);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "Directory delete failed for {Path}", worktreePath);
                        break;
                    }
                }
                try { await RunGitAsync(agentRepoPath, new[] { "worktree", "prune" }, ct); } catch { /* best effort */ }
                return;
            }
        }
        // If we fell through the loop without returning, surface the final failure
        // as a debug log — worktree leaks aren't fatal to the strategy run.
        if (lastException != null)
            _logger.LogDebug(lastException, "Worktree cleanup exited with residual errors for {Path}", worktreePath);
    }

    private static string SanitizeId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.ToString();
    }

    private static async Task ConfigWithRetryAsync(string cwd, string[] args, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { await RunGitCaptureAsync(cwd, args, ct); return; }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
        throw last ?? new InvalidOperationException("git config retry exhausted");
    }

    /// <summary>Applies a unified diff patch to the worktree. Returns true on success.</summary>
    public async Task<bool> ApplyPatchAsync(string worktreePath, string patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch)) return false;
        try
        {
            var patchFile = Path.Combine(worktreePath, ".revision-patch.diff");
            await File.WriteAllTextAsync(patchFile, patch, ct);
            try
            {
                await RunGitAsync(worktreePath, new[] { "apply", "--3way", patchFile }, ct);
                return true;
            }
            finally
            {
                try { File.Delete(patchFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ApplyPatchAsync failed in {Path}", worktreePath);
            return false;
        }
    }

    private static Task RunGitAsync(string cwd, string[] args, CancellationToken ct)
        => RunGitCaptureAsync(cwd, args, ct);

    /// <summary>
    /// Runs git with hardening <c>-c</c> overrides that neutralize every
    /// config-controlled code-execution vector (hooks, external diff, textconv,
    /// LFS filters, attributesFile, mergetool drivers, custom credential
    /// helpers). Used when operating on worktrees that may have been tampered
    /// with by a sandboxed agentic session.
    ///
    /// Also scrubs HOME / USERPROFILE / XDG_CONFIG_HOME and sets
    /// <c>GIT_CONFIG_GLOBAL</c> to <c>/dev/null</c> (or Windows NUL) so the
    /// host's user-global config cannot influence the run either.
    /// </summary>
    private static Task RunGitHardenedAsync(string cwd, string[] args, CancellationToken ct)
        => RunGitHardenedCaptureAsync(cwd, args, ct);

    private static async Task<string> RunGitHardenedCaptureAsync(string cwd, string[] args, CancellationToken ct)
    {
        var hardenedArgs = BuildHardenedGitArgs(args);
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Defeat config-controlled code execution.
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_GLOBAL"] = DevNull;
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        psi.Environment["GCM_INTERACTIVE"] = "Never";
        // Remove anything that could re-point git at a config/helper.
        foreach (var k in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME",
                                  "GIT_ATTR_NOSYSTEM" /* we set our own below */ })
        {
            psi.Environment.Remove(k);
        }
        psi.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        foreach (var a in hardenedArgs) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git process start failed");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git (hardened) {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    private static readonly string DevNull =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";

    /// <summary>
    /// Prepends a fixed set of <c>-c key=value</c> overrides that neutralize
    /// every git config-controlled code-execution vector we know about.
    /// </summary>
    private static string[] BuildHardenedGitArgs(string[] tail)
    {
        // Per the Phase 3 rubber-duck: external diff / textconv / LFS filters /
        // hooks / attributes-file / mergetool drivers are all attacker-
        // controlled when the worktree is untrusted. Every knob below either
        // disables the feature outright or points it at a null sink.
        var head = new[]
        {
            "-c", "core.hooksPath=/dev/null/__virtualdevteam_no_hooks__",
            "-c", "core.fsmonitor=false",
            "-c", "diff.external=",
            "-c", "diff.noprefix=false",
            "-c", "filter.lfs.clean=",
            "-c", "filter.lfs.smudge=",
            "-c", "filter.lfs.process=",
            "-c", "filter.lfs.required=false",
            "-c", "core.attributesFile=",
            "-c", "core.sshCommand=",
            "-c", "credential.helper=",
            "-c", "advice.detachedHead=false",
            "-c", "protocol.file.allow=user",
        };
        var combined = new string[head.Length + tail.Length];
        Array.Copy(head, combined, head.Length);
        Array.Copy(tail, 0, combined, head.Length, tail.Length);
        return combined;
    }

    private static async Task<string> RunGitCaptureAsync(string cwd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Scrub git-related env that could leak credentials or alter behavior.
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git process start failed");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    /// <summary>
    /// Cleans up stale candidate worktree directories left behind after crashes.
    /// Runs <c>git worktree prune</c> to clean metadata, then scans the candidates
    /// directory for physical directories that no longer have active worktree entries.
    /// Call on startup to reclaim disk from prior crashed runs.
    /// </summary>
    public async Task CleanupStaleCandidateWorktreesAsync(
        string agentRepoPath, string candidateDirName = ".candidates", CancellationToken ct = default)
    {
        var candidatesRoot = Path.Combine(agentRepoPath, candidateDirName);
        if (!Directory.Exists(candidatesRoot))
            return;

        try
        {
            // First: prune git worktree metadata for entries whose backing dir is gone
            await RunGitAsync(agentRepoPath, new[] { "worktree", "prune" }, ct);

            // List active worktrees to know which dirs are still tracked
            var activeWorktrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "worktree list --porcelain")
                {
                    WorkingDirectory = agentRepoPath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    var listOutput = await proc.StandardOutput.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    foreach (var line in listOutput.Split('\n'))
                    {
                        if (line.StartsWith("worktree ", StringComparison.Ordinal))
                            activeWorktrees.Add(Path.GetFullPath(line["worktree ".Length..].Trim()));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not list worktrees for cleanup — skipping stale scan");
                return;
            }

            // Scan task directories under .candidates/
            var taskDirs = Directory.GetDirectories(candidatesRoot);
            var removedCount = 0;

            foreach (var taskDir in taskDirs)
            {
                // Each task dir contains strategy dirs (the actual worktrees)
                var strategyDirs = Directory.GetDirectories(taskDir);
                foreach (var stratDir in strategyDirs)
                {
                    var fullPath = Path.GetFullPath(stratDir);
                    if (!activeWorktrees.Contains(fullPath))
                    {
                        // Not tracked by git worktree — safe to remove
                        try
                        {
                            Directory.Delete(stratDir, recursive: true);
                            removedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to remove stale candidate worktree {Path}", stratDir);
                        }
                    }
                }

                // Remove empty task directory
                try
                {
                    if (Directory.Exists(taskDir) && Directory.GetFileSystemEntries(taskDir).Length == 0)
                        Directory.Delete(taskDir);
                }
                catch { /* best-effort */ }
            }

            // Remove empty candidates root
            try
            {
                if (Directory.Exists(candidatesRoot) && Directory.GetFileSystemEntries(candidatesRoot).Length == 0)
                    Directory.Delete(candidatesRoot);
            }
            catch { /* best-effort */ }

            if (removedCount > 0)
                _logger.LogInformation(
                    "Cleaned up {Count} stale candidate worktree(s) under {Path}",
                    removedCount, candidatesRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stale candidate worktree cleanup failed (non-fatal)");
        }
    }
}

/// <summary>Disposable handle that removes its worktree on disposal.</summary>
public sealed class WorktreeHandle : IAsyncDisposable
{
    private readonly GitWorktreeManager _mgr;
    public string AgentRepoPath { get; }
    public string Path { get; }
    /// <summary>
    /// The commit SHA the worktree was created at. Used by <see cref="GitWorktreeManager.ExtractPatchAsync"/>
    /// to compute the full change set even when the strategy commits mid-run
    /// (notably the agentic CLI, which invokes `git add -A && git commit`
    /// during its own tool use — making `git diff HEAD` return nothing).
    /// </summary>
    public string BaseSha { get; }
    /// <summary>Task ID for ref preservation on disposal (Layer 3 of the cleanup-race fix).</summary>
    public string? TaskId { get; }
    /// <summary>Strategy ID for ref preservation on disposal (Layer 3 of the cleanup-race fix).</summary>
    public string? StrategyId { get; }
    private int _disposed;

    internal WorktreeHandle(
        GitWorktreeManager mgr, string agentRepoPath, string path, string baseSha,
        string? taskId = null, string? strategyId = null)
    {
        _mgr = mgr;
        AgentRepoPath = agentRepoPath;
        Path = path;
        BaseSha = baseSha;
        TaskId = taskId;
        StrategyId = strategyId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _mgr.RemoveWorktreeQuietAsync(AgentRepoPath, Path, TaskId, StrategyId, CancellationToken.None);
    }
}
