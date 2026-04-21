using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Strategies;

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
        => _repoLocks.GetOrAdd(Path.GetFullPath(agentRepoPath), _ => new SemaphoreSlim(1, 1));

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

        _logger.LogInformation("Created worktree {Path} @ {Sha} for strategy {Strategy}",
            worktreePath, baseSha, strategyId);
        return new WorktreeHandle(this, agentRepoPath, worktreePath, baseSha);
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
                "ExtractPatch: git add -A failed in worktree {Path} — diff will only reflect already-committed changes",
                worktreePath);
        }

        var diff = await RunGitHardenedCaptureAsync(
            worktreePath,
            new[] { "diff", baseSha, "--binary", "--full-index", "--no-ext-diff", "--no-textconv" },
            ct);

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
    private static readonly string[] FrameworkExcludePaths = new[]
    {
        ".sandbox/",
        ".squad/",
        ".copilot/",
        ".claude/",
        ".github/agents/",
        ".github/workflows/",
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
            "-c", "core.hooksPath=/dev/null/__agentsquad_no_hooks__",
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
    private int _disposed;

    internal WorktreeHandle(GitWorktreeManager mgr, string agentRepoPath, string path, string baseSha)
    {
        _mgr = mgr;
        AgentRepoPath = agentRepoPath;
        Path = path;
        BaseSha = baseSha;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _mgr.RemoveWorktreeQuietAsync(AgentRepoPath, Path, CancellationToken.None);
    }
}
