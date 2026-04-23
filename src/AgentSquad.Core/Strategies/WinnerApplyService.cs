using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Strategies;

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

    public async Task<ApplyOutcome> ApplyAsync(
        string agentRepoPath, string branchName, string expectedBaseSha,
        string patch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patch))
            return new ApplyOutcome(false, "empty-patch", null);

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
                    "Head diverged for {Branch}: expected {Expected} but is {Actual} — refusing apply",
                    branchName, expectedBaseSha, currentHead);
                return new ApplyOutcome(false, "head-changed", currentHead);
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
            var check = await TryRunGitAsync(agentRepoPath, new[] { "apply", "--check", "--3way", tmp }, ct);
            if (!check.ok)
                return new ApplyOutcome(false, $"apply-check-failed: {check.stderr}", currentHead);

            var apply = await TryRunGitAsync(agentRepoPath, new[] { "apply", "--3way", tmp }, ct);
            if (!apply.ok)
            {
                // Roll back any partial 3-way state so the caller sees a clean tree.
                await TryRunGitAsync(agentRepoPath, new[] { "reset", "--hard", "HEAD" }, ct);
                await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd" }, ct);
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
                await TryRunGitAsync(agentRepoPath, new[] { "clean", "-fd" }, ct);
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
