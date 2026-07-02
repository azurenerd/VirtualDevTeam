using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// T1.6: Result of applying a fix recommendation. Mirrors the four terminal states the
/// approve endpoint can transition a recommendation into. Always populated, never thrown —
/// callers inspect <see cref="State"/> + <see cref="Detail"/> to render operator feedback.
/// </summary>
public sealed record FixApplyResult
{
    /// <summary>Terminal state for the recommendation after this apply attempt.</summary>
    public required FixRecommendationState State { get; init; }

    /// <summary>One-line summary suitable for an operator notification body.</summary>
    public required string Detail { get; init; }

    /// <summary>Files that were actually modified by the CLI run (post-`git status` scan).</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Files the CLI touched that were NOT in the recommendation's allowlist (scope violation).</summary>
    public IReadOnlyList<string> OutOfScopeFiles { get; init; } = Array.Empty<string>();

    /// <summary>Path to a saved diff/log artifact for failed applies, or null on success.</summary>
    public string? FailureArtifactPath { get; init; }
}

/// <summary>
/// T1.6: Applies a <see cref="FixRecommendation"/> by launching a constrained Copilot CLI
/// session pointed at the repo root, then verifying scope via <c>git status --porcelain</c>
/// snapshots taken before/after the run.
///
/// Two routing paths come through here:
/// <list type="bullet">
///   <item><see cref="FixTier.Live"/> — successful apply transitions to
///         <see cref="FixRecommendationState.Applied"/>. The runner does NOT restart;
///         IOptionsMonitor / file watchers pick up the change automatically.</item>
///   <item><see cref="FixTier.DeferredRestart"/> — successful apply transitions to
///         <see cref="FixRecommendationState.Coded"/>. The operator must click the existing
///         Restart Runner button to activate the change.</item>
/// </list>
///
/// Scope verification works by:
/// <list type="number">
///   <item>Snapshot <c>git status --porcelain</c> before the CLI session.</item>
///   <item>Run the CLI with a system prompt explicitly listing every allowed file.</item>
///   <item>Snapshot <c>git status --porcelain</c> after.</item>
///   <item>Compute the set of newly-modified files; reject if any fall outside the
///         recommendation's <see cref="FixRecommendation.AffectedFiles"/> allowlist.</item>
/// </list>
///
/// On scope violation or build failure, the recommendation transitions to
/// <see cref="FixRecommendationState.AppliedFailed"/> and a <c>{id}-failed.diff</c> artifact
/// is written under <c>/FixRecommendations/</c> for the operator to triage. Files the CLI
/// modified are NOT auto-reverted: rolling back would risk destroying intentional human
/// changes that happened to be in the working tree at the same moment.
/// </summary>
public sealed class LiveFixApplicator : IFixRecommendationApplicator
{
    private readonly CopilotCliProcessManager _cli;
    private readonly ILogger<LiveFixApplicator> _logger;

    /// <summary>Wall-clock cap on a single apply session. Long enough for a 10-file edit; short
    /// enough that a runaway CLI doesn't block the dashboard for hours.</summary>
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(8);

    public LiveFixApplicator(
        CopilotCliProcessManager cli,
        ILogger<LiveFixApplicator> logger)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Apply a fix. Caller has already determined the tier (so the routing decision lives in
    /// one place — the approve endpoint). Returns a result describing the terminal state.
    /// Never throws under normal failure (CLI exit codes, scope violations, build errors all
    /// flow through the result). Throws only on argument/precondition errors.
    /// </summary>
    public async Task<FixApplyResult> ApplyAsync(
        FixRecommendation rec,
        FixTier tier,
        string repoRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rec);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);

        if (tier == FixTier.Blocked)
            throw new InvalidOperationException(
                "LiveFixApplicator does not handle Blocked tier — caller should have routed to StagedFixApplicator.");

        var allowedFiles = rec.AffectedFiles ?? Array.Empty<string>();
        if (allowedFiles.Count == 0)
        {
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = "Cannot apply: recommendation has no affected files. Re-classify and try again.",
            };
        }

        // Snapshot the working tree BEFORE so we can diff against it after.
        var preSnapshot = await CaptureGitStatusAsync(repoRoot, ct).ConfigureAwait(false);
        if (preSnapshot is null)
        {
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = "Cannot apply: pre-apply git snapshot failed (not a git repo or git not on PATH).",
            };
        }

        // Build the constrained prompt. The CLI MUST see the allowlist explicitly; we cannot
        // rely on it to infer scope from prose alone.
        var prompt = BuildConstrainedPrompt(rec, allowedFiles);

        _logger.LogInformation(
            "T1.6 LiveFixApplicator: applying {RecId} ({Tier}) — {FileCount} files in scope",
            rec.Id, tier, allowedFiles.Count);

        // Run the CLI in agentic mode (Pool=Agentic + AllowAll=true) with the repo root as
        // the working directory. Agentic mode is required so the CLI's edit tools are
        // permitted to write files — the SingleShot pool runs in advice-only mode.
        AgenticSessionResult cliResult;
        try
        {
            using var timeoutCts = new CancellationTokenSource(ApplyTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var options = new CopilotCliRequestOptions
            {
                Pool = CopilotCliPool.Agentic,
                AllowAll = true,
                CloseStdinAfterPrompt = true,
                WorkingDirectory = repoRoot,
                Timeout = ApplyTimeout,
            };

            cliResult = await _cli.ExecuteAgenticSessionAsync(prompt, options, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "T1.6 LiveFixApplicator: CLI execution threw for {RecId}", rec.Id);
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = $"CLI invocation failed: {ex.Message}",
            };
        }

        if (!cliResult.Succeeded)
        {
            _logger.LogWarning(
                "T1.6 LiveFixApplicator: CLI returned failure for {RecId}: {Reason} — {Error}",
                rec.Id, cliResult.FailureReason, cliResult.ErrorMessage ?? "(no detail)");
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = $"CLI session failed ({cliResult.FailureReason}): {cliResult.ErrorMessage ?? "unknown"}",
            };
        }

        // Snapshot AFTER the CLI run.
        var postSnapshot = await CaptureGitStatusAsync(repoRoot, ct).ConfigureAwait(false);
        if (postSnapshot is null)
        {
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = "Cannot verify apply: post-apply git snapshot failed.",
            };
        }

        // Compute the delta — files that appear in post but not pre, or whose status changed.
        var modifiedFiles = ComputeModifiedFiles(preSnapshot, postSnapshot);
        if (modifiedFiles.Count == 0)
        {
            // CLI claimed success but didn't actually change anything. Treat as failure so the
            // operator knows to retry or rework — silent no-ops are worse than visible failures.
            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail = "CLI reported success but no files changed. Review the plan and rework.",
            };
        }

        // Scope check: every modified file must be on the allowlist (case-insensitive,
        // forward-slash-normalised). Anything outside is a scope violation.
        var allowed = new HashSet<string>(
            allowedFiles.Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var outOfScope = modifiedFiles
            .Where(f => !allowed.Contains(NormalizePath(f)))
            .ToArray();

        if (outOfScope.Length > 0)
        {
            var artifactPath = await SaveFailureArtifactAsync(
                rec.Id, repoRoot, modifiedFiles, outOfScope, allowedFiles, cliResult.LogBuffer, ct).ConfigureAwait(false);

            _logger.LogWarning(
                "T1.6 LiveFixApplicator: scope violation on {RecId}. Out of scope: {OutOfScope}",
                rec.Id, string.Join(", ", outOfScope));

            return new FixApplyResult
            {
                State = FixRecommendationState.AppliedFailed,
                Detail =
                    $"⛔ CLI exceeded scope. Modified {outOfScope.Length} unauthorised file(s): " +
                    $"{string.Join(", ", outOfScope.Take(3))}" +
                    (outOfScope.Length > 3 ? $", +{outOfScope.Length - 3} more" : "") +
                    ". Diff saved for review; runner state untouched.",
                ModifiedFiles = modifiedFiles,
                OutOfScopeFiles = outOfScope,
                FailureArtifactPath = artifactPath,
            };
        }

        // Success path — choose terminal state by tier.
        var terminalState = tier == FixTier.Live
            ? FixRecommendationState.Applied
            : FixRecommendationState.Coded;

        var detail = tier == FixTier.Live
            ? $"✅ Fix applied — config auto-reloaded. {modifiedFiles.Count} file(s) modified."
            : $"📝 Fix coded — restart runner to activate. {modifiedFiles.Count} file(s) modified.";

        _logger.LogInformation(
            "T1.6 LiveFixApplicator: {RecId} → {State} ({FileCount} files in scope)",
            rec.Id, terminalState, modifiedFiles.Count);

        return new FixApplyResult
        {
            State = terminalState,
            Detail = detail,
            ModifiedFiles = modifiedFiles,
        };
    }

    /// <summary>
    /// Build the CLI prompt. Includes the full plan markdown plus a hard scope rule that
    /// names each allowed file explicitly. The CLI's own --allow-all flag opens the edit
    /// tools; the prompt prevents misuse.
    /// </summary>
    private static string BuildConstrainedPrompt(FixRecommendation rec, IReadOnlyList<string> allowedFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Apply this fix plan");
        sb.AppendLine();
        sb.AppendLine("You are applying a fix to a working repository. Apply ONLY the changes described in the plan below.");
        sb.AppendLine();
        sb.AppendLine("## Hard scope rules (you MUST obey)");
        sb.AppendLine();
        sb.AppendLine("1. Modify ONLY the following files (absolute scope):");
        foreach (var f in allowedFiles)
            sb.AppendLine($"   - `{f}`");
        sb.AppendLine();
        sb.AppendLine("2. Do NOT create new files unless the plan explicitly says so.");
        sb.AppendLine("3. Do NOT modify any file not listed above.");
        sb.AppendLine("4. Do NOT run `git commit`, `git push`, `git reset`, or `git checkout` — leave those to the operator.");
        sb.AppendLine("5. Do NOT install packages, run `dotnet restore`, or modify `.csproj` / `package.json`.");
        sb.AppendLine("6. Make the smallest possible edit that achieves the plan's stated outcome.");
        sb.AppendLine("7. After the edit, summarise in one sentence what you changed.");
        sb.AppendLine();
        sb.AppendLine("If the plan asks for changes outside this scope, STOP and report 'Scope violation: cannot proceed.' instead of editing.");
        sb.AppendLine();
        sb.AppendLine("## Fix plan");
        sb.AppendLine();
        sb.AppendLine(rec.PlanMarkdown);
        return sb.ToString();
    }

    /// <summary>
    /// Run <c>git status --porcelain</c> and return a dictionary keyed by relative path with
    /// the two-character status code as value. Returns null if git is unavailable or repo
    /// is not a git working tree.
    /// </summary>
    private async Task<Dictionary<string, string>?> CaptureGitStatusAsync(string repoRoot, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "status", "--porcelain", "-uall" },
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (p.ExitCode != 0) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 4) continue;
                // Porcelain v1 format: "XY <space> path" (or "XY <space> path -> renamed-path")
                var status = line.Substring(0, 2);
                var path = line.Substring(3).TrimEnd('\r').Trim();
                // Handle renames: "old -> new" — track the new path.
                var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) path = path.Substring(arrow + 4);
                // Strip surrounding quotes that git emits for paths with special chars.
                if (path.StartsWith('"') && path.EndsWith('"') && path.Length >= 2)
                    path = path.Substring(1, path.Length - 2);
                dict[NormalizePath(path)] = status;
            }
            return dict;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "T1.6 LiveFixApplicator: git status failed in {RepoRoot}", repoRoot);
            return null;
        }
    }

    /// <summary>
    /// Diff two git status snapshots. A file is considered "modified" if it's in post and
    /// either absent from pre, or has a different status code. We don't try to reverse the
    /// porcelain code into a human verb — only the path matters for scope checking.
    /// </summary>
    private static IReadOnlyList<string> ComputeModifiedFiles(
        Dictionary<string, string> pre, Dictionary<string, string> post)
    {
        var changed = new List<string>();
        foreach (var (path, status) in post)
        {
            if (!pre.TryGetValue(path, out var preStatus) || preStatus != status)
                changed.Add(path);
        }
        return changed;
    }

    /// <summary>
    /// Save a JSON+text artifact under <c>FixRecommendations/{id}-failed.diff</c> capturing
    /// what the CLI did wrong. The operator can inspect this from the dashboard's plan-file
    /// link or the filesystem to decide whether to revert manually.
    /// </summary>
    private async Task<string?> SaveFailureArtifactAsync(
        string recId,
        string repoRoot,
        IReadOnlyList<string> modifiedFiles,
        IReadOnlyList<string> outOfScopeFiles,
        IReadOnlyList<string> allowedFiles,
        string cliOutput,
        CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(repoRoot, "FixRecommendations");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{recId}-failed.diff");

            var sb = new StringBuilder();
            sb.AppendLine($"# Fix apply failure — {DateTimeOffset.UtcNow:o}");
            sb.AppendLine($"# Recommendation: {recId}");
            sb.AppendLine();
            sb.AppendLine("## Allowed scope");
            foreach (var f in allowedFiles) sb.AppendLine($"- {f}");
            sb.AppendLine();
            sb.AppendLine("## Files actually modified");
            foreach (var f in modifiedFiles) sb.AppendLine($"- {f}");
            sb.AppendLine();
            sb.AppendLine("## Out-of-scope (rejected)");
            foreach (var f in outOfScopeFiles) sb.AppendLine($"- {f}");
            sb.AppendLine();
            sb.AppendLine("## CLI output");
            sb.AppendLine("```");
            sb.AppendLine(cliOutput);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## git diff (working tree)");
            sb.AppendLine("```");
            sb.AppendLine(await CaptureGitDiffAsync(repoRoot, ct).ConfigureAwait(false));
            sb.AppendLine("```");

            await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "T1.6 LiveFixApplicator: failed to write failure artifact for {RecId}", recId);
            return null;
        }
    }

    private async Task<string> CaptureGitDiffAsync(string repoRoot, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "diff", "--no-color" },
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "(git unavailable)";
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return stdout.Length > 50_000 ? stdout.Substring(0, 50_000) + "\n... (truncated)" : stdout;
        }
        catch
        {
            return "(git diff capture failed)";
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');
}
