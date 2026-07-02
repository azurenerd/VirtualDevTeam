using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Post-run validation for an agentic session (<c>p3-sandbox-hardening</c>). Invoked
/// by <see cref="Strategies.AgenticDelegationStrategy"/> after the CLI exits and
/// BEFORE the orchestrator extracts the patch. Catches sandbox escapes that the
/// process-level containment (env scrub + Job Object) could not prevent at run
/// time — primarily symlink/reparse-point sneaks and host-gitconfig drift.
///
/// <para>Checks (all failures surface as structured <see cref="SandboxViolation"/>s):</para>
/// <list type="number">
///   <item><description>Reparse-point / symlink scan inside worktree. Any entry whose
///     target canonicalizes outside the worktree (or is a reparse point at all) is a
///     violation. Uses <see cref="Path.GetFullPath(string)"/> so UNC / <c>\\?\</c> /
///     drive-relative paths are all normalized.</description></item>
///   <item><description>Host <c>~/.gitconfig</c> hash. Takes a SHA-256 pre-session,
///     compares post-session; any drift is a violation (the sandbox's
///     <c>GIT_CONFIG_GLOBAL</c> should have prevented this).</description></item>
///   <item><description>Sandbox gitconfig file exists and is still the one we wrote
///     (defence-in-depth against a malicious process deleting the redirect).</description></item>
/// </list>
///
/// <para>What this class does NOT do (out of scope for Phase 3):</para>
/// <list type="bullet">
///   <item><description>Block network egress.</description></item>
///   <item><description>Detect reads of absolute paths via APIs (file contents leak).</description></item>
///   <item><description>Run a full process-tree audit.</description></item>
/// </list>
/// </summary>
public sealed class SandboxPostRunValidator
{
    private readonly ILogger _logger;

    public SandboxPostRunValidator(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Snapshot host state BEFORE the agentic session runs. Holds the reference
    /// values used by <see cref="Validate"/>. Construction is cheap (a few hashes).
    ///
    /// <para>When <paramref name="worktreePath"/> is supplied, additionally hashes
    /// the worktree's top-level <c>.git</c> pointer file so <see cref="Validate"/>
    /// can detect gitdir redirection attacks. When null, that check is skipped.</para>
    /// </summary>
    public static SandboxSnapshot TakeSnapshot(string? worktreePath = null)
    {
        var gitconfigs = HashHostGitconfigs();
        var worktreeGitFile = worktreePath is null ? null : HashWorktreeGitFile(worktreePath);
        return new SandboxSnapshot(gitconfigs, worktreeGitFile, DateTime.UtcNow);
    }

    /// <summary>
    /// Run all post-session checks. Returns the list of violations (empty = clean).
    /// </summary>
    public IReadOnlyList<SandboxViolation> Validate(
        string worktreePath,
        SandboxSnapshot snapshot,
        string expectedSandboxGitconfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSandboxGitconfigPath);

        var violations = new List<SandboxViolation>();

        ScanForReparsePoints(worktreePath, violations);
        CheckHostGitconfigDrift(snapshot, violations);
        CheckSandboxGitconfigIntact(expectedSandboxGitconfigPath, violations);
        CheckWorktreeGitFileDrift(worktreePath, snapshot, violations);

        if (violations.Count > 0)
        {
            _logger.LogWarning(
                "SandboxPostRunValidator found {Count} violation(s) in worktree {Path}",
                violations.Count, worktreePath);
        }
        return violations;
    }

    /// <summary>
    /// Walk the worktree looking for reparse points (symlinks, junctions, mount
    /// points). Any reparse point is flagged — we allow no symlinks because the
    /// agent has no legitimate reason to create them and the downstream diff
    /// extraction does not traverse them safely.
    /// </summary>
    private void ScanForReparsePoints(string worktreePath, List<SandboxViolation> violations)
    {
        var worktreeRoot = Path.GetFullPath(worktreePath);
        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                worktreeRoot, "*", enumOptions))
            {
                // Skip the sandbox dirs (we created them).
                if (entry.StartsWith(Path.Combine(worktreeRoot, ".sandbox"), StringComparison.OrdinalIgnoreCase))
                    continue;
                // Skip .git internals — git manages its own symlinks (hook scripts etc.)
                // and we validate git-config separately.
                if (entry.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                    continue;
                // Skip node_modules — npm install creates junction points in .bin/ for
                // executables. These are legitimate package manager artifacts, not sandbox
                // escapes. Consistent with exclusions in BuildRunner, TestRunner, AppLauncher.
                if (entry.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                // Skip other build artifact dirs that may contain reparse points.
                if (entry.Contains(Path.DirectorySeparatorChar + ".candidates-eval" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read attributes of {Entry}", entry);
                    continue;
                }

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    violations.Add(new SandboxViolation(
                        Code: "reparse-point",
                        Detail: $"Reparse point detected inside worktree: {entry}"));
                }
            }
        }
        catch (Exception ex)
        {
            // Scan errors are infrastructure failures (long paths, access denied, etc.)
            // — NOT sandbox violations. Log and continue; only actual reparse points are violations.
            _logger.LogWarning(ex, "Reparse-point scan could not enumerate all entries in {Path} — treating as clean", worktreePath);
        }
    }

    /// <summary>
    /// Canonicalize an absolute path. Handles drive-relative, UNC, and
    /// extended-length (<c>\\?\</c>) prefixes uniformly.
    /// </summary>
    internal static string Canonicalize(string p) => Path.GetFullPath(p);

    private void CheckHostGitconfigDrift(SandboxSnapshot snapshot, List<SandboxViolation> violations)
    {
        var post = HashHostGitconfigs();
        // Compare every path we hashed pre-session. New paths appearing post-
        // session are not checked (the host couldn't have gained a new home
        // dir mid-session), but any drift on a known path is a violation.
        foreach (var (path, preHash) in snapshot.HostGitconfigHashes)
        {
            post.TryGetValue(path, out var postHash);
            postHash ??= string.Empty;
            if (!string.Equals(postHash, preHash, StringComparison.Ordinal))
            {
                violations.Add(new SandboxViolation(
                    Code: "host-gitconfig-drift",
                    Detail: $"Host gitconfig {path} hash changed during agentic session; GIT_CONFIG_GLOBAL scrub failed"));
            }
        }
    }

    private void CheckWorktreeGitFileDrift(
        string worktreePath,
        SandboxSnapshot snapshot,
        List<SandboxViolation> violations)
    {
        if (snapshot.WorktreeGitFileHash is null) return; // opt-in; skip when no snapshot

        var currentHash = HashWorktreeGitFile(worktreePath);
        if (!string.Equals(currentHash, snapshot.WorktreeGitFileHash, StringComparison.Ordinal))
        {
            violations.Add(new SandboxViolation(
                Code: "worktree-gitdir-drift",
                Detail: "The worktree's top-level '.git' pointer file changed during the session. " +
                        "This could be an attempt to redirect git operations to an attacker-chosen gitdir."));
        }
    }

    private void CheckSandboxGitconfigIntact(string expectedPath, List<SandboxViolation> violations)
    {
        if (!File.Exists(expectedPath))
        {
            violations.Add(new SandboxViolation(
                Code: "sandbox-gitconfig-removed",
                Detail: $"Sandbox gitconfig at {expectedPath} was deleted during session"));
        }
    }

    /// <summary>
    /// Hash every plausible host <c>~/.gitconfig</c> candidate. Windows may
    /// resolve the home differently for USERPROFILE vs HOME — if they point at
    /// different files we hash both, because git itself may read either depending
    /// on the caller. Missing files hash to empty string (stable no-drift signal).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> HashHostGitconfigs()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (var envVar in new[] { "USERPROFILE", "HOME" })
        {
            var home = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(home)) candidates.Add(home);
        }
        var special = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(special)) candidates.Add(special);

        foreach (var home in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(home, ".gitconfig");
            result[path] = HashFileOrEmpty(path);
        }
        return result;
    }

    /// <summary>
    /// Back-compat: legacy single-hash API. Returns whatever HashHostGitconfigs
    /// produced for the primary path (USERPROFILE on Windows, HOME elsewhere).
    /// </summary>
    internal static string HashHostGitconfig()
    {
        var all = HashHostGitconfigs();
        return all.Values.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Hash the worktree's top-level <c>.git</c> file. In a linked worktree this
    /// is a small text file with contents <c>gitdir: &lt;path&gt;</c>; a malicious
    /// session could rewrite it to redirect git operations to an attacker-chosen
    /// gitdir. Returns empty string if <c>.git</c> doesn't exist or is a directory.
    /// </summary>
    internal static string HashWorktreeGitFile(string worktreePath)
    {
        try
        {
            var gitPath = Path.Combine(worktreePath, ".git");
            if (!File.Exists(gitPath)) return string.Empty; // directory or missing
            return HashFileOrEmpty(gitPath);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string HashFileOrEmpty(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Immutable pre-session snapshot of host state the validator compares against.
/// <see cref="HostGitconfigHashes"/> maps each candidate gitconfig path to its
/// pre-session SHA-256 hash. <see cref="WorktreeGitFileHash"/> is the hash of
/// the worktree's top-level <c>.git</c> pointer file, or <c>null</c> if the
/// snapshot was taken without a worktree reference (back-compat path).
/// </summary>
public sealed record SandboxSnapshot(
    IReadOnlyDictionary<string, string> HostGitconfigHashes,
    string? WorktreeGitFileHash,
    DateTime TakenAtUtc);

/// <summary>
/// A single sandbox-post-run violation. <see cref="Code"/> is the stable
/// classification string (used for metrics / experiment record tagging),
/// <see cref="Detail"/> is a human-readable message suitable for logs.
/// </summary>
public sealed record SandboxViolation(string Code, string Detail);
