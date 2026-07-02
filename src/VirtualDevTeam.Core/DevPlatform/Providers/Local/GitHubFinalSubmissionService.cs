using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.DevPlatform.Providers.Local;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Publishes the final integration result from Local Dev Mode to the real platform (GitHub).
/// Uses <c>gh</c> CLI to push code and create a PR — no direct API dependency.
/// The PR is NOT merged — a human reviews and merges it.
/// Persists submission state to the local DB for idempotency across restarts.
/// </summary>
/// <remarks>
/// ADO parity: No <c>AdoFinalSubmissionService</c> exists yet. If Local mode targets an
/// Azure DevOps repo, a parallel implementation using <c>az repos pr create</c> would be
/// needed. The merge strategy in <see cref="MergeInTempWorktreeAndPushAsync"/> (prefer
/// vdt-local/main single merge over individual branch merges) is git-level logic and
/// would transfer directly to an ADO implementation.
/// </remarks>
public sealed class GitHubFinalSubmissionService : IFinalSubmissionService
{
    private readonly LocalPlatformContext _ctx;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private readonly ILogger<GitHubFinalSubmissionService> _logger;

    public GitHubFinalSubmissionService(
        LocalPlatformContext ctx,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<GitHubFinalSubmissionService> logger)
    {
        _ctx = ctx;
        _config = config;
        _logger = logger;
    }

    public async Task<PlatformPullRequest> SubmitFinalPRAsync(
        string branchName, string title, string body, string baseBranch,
        CancellationToken ct = default)
    {
        // Check for existing submission first (idempotency)
        var existing = await GetExistingSubmissionAsync(ct);
        if (existing is not null)
        {
            _logger.LogInformation("Final PR already submitted as #{Number} — reusing", existing.Number);
            return existing;
        }

        var cfg = _config.Value;
        var repo = cfg.Project.GitHubRepo;
        if (string.IsNullOrWhiteSpace(repo))
            throw new InvalidOperationException("No GitHub repo configured — cannot submit final PR");

        // Defensive: head branch must differ from base branch (GitHub rejects head==base PRs)
        if (string.Equals(branchName, baseBranch, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = $"vdt/final/{baseBranch}";
            _logger.LogWarning(
                "Final submission branch {Branch} equals base {Base} — using fallback {Fallback}",
                branchName, baseBranch, fallback);
            branchName = fallback;
        }

        // Step 1: Merge agent work and push the working branch to the real remote.
        // branchName = the working branch (e.g., "behumphr") — VDT pushes directly to it.
        // baseBranch = the protected branch (e.g., "main") — target of the review PR.
        _logger.LogInformation("Pushing branch {Branch} to GitHub remote {Repo}", branchName, repo);
        await PushBranchToRemoteAsync(branchName, repo, ct);

        // Step 2: Create the PR: workingBranch → main (or DefaultBranch)
        _logger.LogInformation("Creating final PR on {Repo}: {Title} ({Head} → {Base})",
            repo, title, branchName, baseBranch);
        var prNumber = await CreatePRViaGhCliAsync(repo, branchName, baseBranch, title, body, ct);

        // Step 3: Persist submission state
        await PersistSubmissionAsync(prNumber, branchName, title, ct);

        var pr = new PlatformPullRequest
        {
            Number = prNumber,
            Title = title,
            Body = body,
            State = "open",
            HeadBranch = branchName,
            BaseBranch = baseBranch,
            Url = $"https://github.com/{repo}/pull/{prNumber}",
            Labels = new List<string> { "final-integration", "awaiting-human-review" },
        };

        _logger.LogInformation("✅ Final PR #{Number} created on GitHub: {Url}", prNumber, pr.Url);
        return pr;
    }

    public async Task<PlatformPullRequest?> GetExistingSubmissionAsync(CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr_number, branch_name, title, submitted_at
            FROM local_final_submissions WHERE run_id = @runId
            ORDER BY submitted_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var cfg = _config.Value;
            var repo = cfg.Project.GitHubRepo ?? "";
            var prNumber = reader.GetInt32(0);
            var branchName = reader.GetString(1);
            var title = reader.GetString(2);

            // Verify the PR is still open on GitHub before reusing.
            // A prior iteration may have closed this PR during reset.
            if (!await IsPrOpenOnGitHubAsync(repo, prNumber, ct))
            {
                _logger.LogWarning(
                    "Final PR #{Number} from DB is no longer open on GitHub — clearing stale submission record",
                    prNumber);
                await ClearSubmissionAsync(prNumber, ct);
                return null;
            }

            return new PlatformPullRequest
            {
                Number = prNumber,
                Title = title,
                State = "open",
                HeadBranch = branchName,
                Url = $"https://github.com/{repo}/pull/{prNumber}",
            };
        }
        catch
        {
            // Table may not exist yet — that's fine, no submission
            return null;
        }
    }

    private async Task<bool> IsPrOpenOnGitHubAsync(string repo, int prNumber, CancellationToken ct)
    {
        try
        {
            var state = await RunCaptureAsync(
                "gh", $"pr view {prNumber} --repo {repo} --json state --jq .state", ct);
            return string.Equals(state?.Trim(), "OPEN", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check PR #{Number} state on GitHub — treating as stale", prNumber);
            return false;
        }
    }

    private async Task ClearSubmissionAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM local_final_submissions WHERE run_id = @runId AND pr_number = @prNumber";
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            cmd.Parameters.AddWithValue("@prNumber", prNumber);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear stale final submission record for PR #{Number}", prNumber);
        }
    }

    private async Task PushBranchToRemoteAsync(string branchName, string repo, CancellationToken ct)
    {
        var token = await RunCaptureAsync("gh", "auth token", ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Failed to get GitHub token from gh CLI — ensure gh auth login is done");

        var cfg = _config.Value;
        var inPlaceCheckout = cfg.Workspace.LocalCheckoutPath;

        if (!string.IsNullOrWhiteSpace(inPlaceCheckout) && Directory.Exists(Path.Combine(inPlaceCheckout, ".git")))
        {
            // InPlace mode: merge agent branches in a TEMPORARY worktree, push as the working branch.
            await MergeInTempWorktreeAndPushAsync(inPlaceCheckout, branchName, repo, token, ct);
        }
        else
        {
            // Fallback: push from bare repo (Clone mode — bare repo has the merged code)
            if (_ctx.BareRepo.BareRepoPath is null)
                throw new InvalidOperationException("Local bare repo not initialized");
            await PushFromBareRepoAsync(branchName, repo, token, ct);
        }
    }

    private async Task MergeInTempWorktreeAndPushAsync(
        string inPlaceCheckout, string branchName,
        string repo, string token, CancellationToken ct)
    {
        // branchName = the working branch (e.g., "behumphr") that receives merged agent code.
        // We fetch it from origin, merge agent work into it, and push it back.
        _logger.LogInformation("Final submission: fetching latest from origin in {Path}", inPlaceCheckout);
        var defaultBranch = _config.Value.Project.DefaultBranch ?? "main";
        var startRef = branchName; // start worktree from the working branch
        try
        {
            await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {branchName}", token, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("couldn't find remote ref"))
        {
            // Working branch doesn't exist on remote yet — start from default branch
            _logger.LogWarning("Working branch '{Branch}' not found on remote — starting from '{Default}'",
                branchName, defaultBranch);
            await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {defaultBranch}", token, ct);
            startRef = defaultBranch;
        }

        // Step 2: Add the local bare repo as a remote so we can access agent branches
        // (In Local mode, agent pushes go to the bare repo, not GitHub)
        var bareRepoPath = _ctx.BareRepo.BareRepoPath;
        if (bareRepoPath is not null)
        {
            try
            {
                await RunGitInDirAsync(inPlaceCheckout, "remote remove vdt-local", null, ct);
            }
            catch { /* remote may not exist yet — that's fine */ }
            await RunGitInDirAsync(inPlaceCheckout, $"remote add vdt-local \"{bareRepoPath}\"", null, ct);
            await RunGitInDirAsync(inPlaceCheckout, "fetch vdt-local", null, ct);
        }

        // Step 3: Create a temporary DETACHED worktree from origin/{startRef} — operator's
        // checkout untouched. We use --detach (not -b <branch>) deliberately: the push below
        // uses `HEAD:refs/heads/{branch}`, so no named local branch is ever needed. Creating a
        // branch here only leaks a `vdt-temp-final-*` ref into the operator's checkout that the
        // worktree-remove cleanup never deleted. Detached HEAD merges + pushes are fully
        // supported; `worktree remove` alone then leaves zero residue.
        var tempDir = Path.Combine(Path.GetTempPath(), $"vdt-final-{Guid.NewGuid():N}");
        var pushSucceeded = false;
        try
        {
            _logger.LogInformation("Final submission: creating temp worktree at {Path} from origin/{Base}", tempDir, startRef);
            await RunGitInDirAsync(inPlaceCheckout,
                $"worktree add --detach \"{tempDir}\" origin/{startRef}", null, ct);

            // Configure git identity for merge commits in the temp worktree
            try
            {
                await RunGitInDirAsync(tempDir, "config user.name \"VirtualDevTeam\"", null, ct);
                await RunGitInDirAsync(tempDir, "config user.email \"virtualdevteam@noreply.github.com\"", null, ct);
            }
            catch { /* best-effort — may already be configured globally */ }

            // Step 4: Merge all local changes into the final branch.
            // Strategy: when a bare repo is available, prefer merging its working branch
            // (where PRs merge in Local mode) directly — it already has all merged PRs
            // integrated sequentially and avoids conflicts from missing/partial individual
            // branch refs (which are deleted after local merge).
            // Try candidates in order: working branch (branchName), then defaultBranch.
            // In Local mode, PRs merge into the working branch, not necessarily "main".
            bool mergedViaMainBranch = false;
            if (bareRepoPath is not null)
            {
                var mergedPrCount = GetMergedPrBranches().Count;
                if (mergedPrCount > 0)
                {
                    // Try working branch first (PRs merge here in Local mode), then defaultBranch
                    var candidates = new List<string> { branchName };
                    if (!string.Equals(defaultBranch, branchName, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(defaultBranch);

                    foreach (var candidate in candidates)
                    {
                        if (mergedViaMainBranch) break;
                        try
                        {
                            await RunGitInDirAsync(tempDir, $"merge vdt-local/{candidate} --no-edit --allow-unrelated-histories", null, ct);

                            // Verify the merge introduced actual changes
                            var diffStat = await RunGitCaptureInDirAsync(tempDir,
                                $"diff --stat origin/{startRef}..HEAD", ct);
                            if (string.IsNullOrWhiteSpace(diffStat))
                            {
                                _logger.LogWarning(
                                    "Merge of vdt-local/{Branch} produced no diff against origin/{Base} — trying next candidate",
                                    candidate, startRef);
                                // Reset to pre-merge state before trying next candidate
                                try { await RunGitInDirAsync(tempDir, "reset --hard HEAD~1", null, ct); }
                                catch { /* merge may have been a no-op fast-forward */ }
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Final submission: merged vdt-local/{Branch} — all {Count} local PRs included",
                                    candidate, mergedPrCount);
                                mergedViaMainBranch = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to merge vdt-local/{Branch} — trying next candidate",
                                candidate);
                            try { await RunGitInDirAsync(tempDir, "merge --abort", null, ct); }
                            catch { try { await RunGitInDirAsync(tempDir, "reset --merge", null, ct); } catch { } }
                        }
                    }
                }
            }

            if (!mergedViaMainBranch)
            {
                // Fallback: merge individual agent branches
                var runScope = _ctx.RunId[..8];
                var branchRunScope = cfg_BranchRunScope();
                var scopeFilter = !string.IsNullOrEmpty(branchRunScope) ? branchRunScope : runScope;

                var mergedPrBranches = GetMergedPrBranches();

                var remotePrefix = bareRepoPath is not null ? "vdt-local" : "origin";
                var allBranches = await RunGitCaptureInDirAsync(inPlaceCheckout,
                    $"branch -r --list \"{remotePrefix}/agent/*\"", ct);
                var agentBranches = (allBranches ?? "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(b => b.Contains($"/{scopeFilter}/", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (mergedPrBranches.Count > 0)
                {
                    var before = agentBranches.Count;
                    agentBranches = agentBranches
                        .Where(remoteBranch =>
                        {
                            var localRef = remoteBranch.Contains('/')
                                ? remoteBranch[(remoteBranch.IndexOf('/') + 1)..]
                                : remoteBranch;
                            return mergedPrBranches.Any(mb =>
                                localRef.Equals(mb, StringComparison.OrdinalIgnoreCase));
                        })
                        .ToList();
                    _logger.LogInformation(
                        "Filtered agent branches from {Before} to {After} using merged PR branches ({MergedCount} merged PRs)",
                        before, agentBranches.Count, mergedPrBranches.Count);
                }

                if (agentBranches.Count == 0)
                {
                    _logger.LogWarning("No agent branches matched run scope {Scope} in {Remote} — trying origin", scopeFilter, remotePrefix);
                    allBranches = await RunGitCaptureInDirAsync(inPlaceCheckout,
                        "branch -r --list \"origin/agent/*\"", ct);
                    agentBranches = (allBranches ?? "")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(b => b.Contains($"/{scopeFilter}/", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                _logger.LogInformation("Final submission: merging {Count} agent branch(es) into {Branch}",
                    agentBranches.Count, branchName);

                var merged = new List<string>();
                var failed = new List<(string Branch, string Error)>();

                foreach (var branch in agentBranches)
                {
                    try
                    {
                        await RunGitInDirAsync(tempDir, $"merge {branch} --no-edit --allow-unrelated-histories", null, ct);
                        merged.Add(branch);
                        _logger.LogInformation("Merged {Branch} into final branch", branch);
                    }
                    catch (Exception ex)
                    {
                        try { await RunGitInDirAsync(tempDir, "merge --abort", null, ct); }
                        catch { try { await RunGitInDirAsync(tempDir, "reset --merge", null, ct); } catch { } }
                        failed.Add((branch, ex.Message));
                        _logger.LogWarning(ex, "Failed to merge {Branch} into final branch", branch);
                    }
                }

                if (failed.Count > 0)
                {
                    var failList = string.Join("\n", failed.Select(f => $"  - {f.Branch}: {f.Error}"));
                    throw new InvalidOperationException(
                        $"Final submission aborted: {failed.Count} agent branch(es) failed to merge:\n{failList}\n\n" +
                        $"Successfully merged: {merged.Count} branch(es). Fix conflicts manually or re-run T-FINAL.");
                }

                if (merged.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Final submission aborted: no agent branches were found to merge. " +
                        "Ensure agent code was pushed to GitHub before final submission.");
                }
            }

            // Step 5: Push from the temp worktree to the working branch
            _logger.LogInformation("Final submission: pushing to working branch {Branch} on GitHub", branchName);
            await PushFromDirAsync(tempDir, branchName, repo, token, ct);
            pushSucceeded = true;

            _logger.LogInformation("✅ Working branch {Branch} pushed to github.com/{Repo}",
                branchName, repo);
        }
        finally
        {
            // Clean up temp worktree — but ONLY when the push succeeded. With a detached HEAD,
            // the merge commit lives solely in this worktree's HEAD until it's pushed; removing
            // the worktree after a push FAILURE would discard that work. On failure we keep the
            // worktree and log its path so the merge result can be recovered/inspected.
            if (!pushSucceeded)
            {
                _logger.LogWarning(
                    "Final submission push did not complete — preserving temp worktree for recovery at {Path}. " +
                    "Remove it manually (git worktree remove --force) once resolved.", tempDir);
            }
            else
            {
                try
                {
                    await RunGitInDirAsync(inPlaceCheckout, $"worktree remove \"{tempDir}\" --force", null, ct);
                    _logger.LogDebug("Cleaned up temp worktree at {Path}", tempDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp worktree at {Path} — manual cleanup may be needed", tempDir);
                    // Try direct directory delete as fallback
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        // Best-effort: prune any LEGACY vdt-temp-final-* branches left in the operator's checkout
        // by pre---detach versions of this service. Only runs after a successful push (the only
        // path that exits the try/finally normally), so origin/{branchName} reflects the pushed work.
        await CleanupLegacyTempBranchesAsync(inPlaceCheckout, branchName, token, ct);
    }

    /// <summary>
    /// Best-effort cleanup of LEGACY <c>vdt-temp-final-*</c> branches left in the operator's
    /// checkout by pre-<c>--detach</c> versions of this service (which created a named temp branch
    /// via <c>worktree add -b</c> but only removed the worktree, never the branch). Current code
    /// uses a detached worktree so it creates no such branch.
    /// <para>
    /// Safety: a branch is deleted ONLY when it is provably redundant — reachable from the
    /// just-pushed final branch (<c>origin/{branchName}</c>), so its commits are preserved on the
    /// remote. We refresh <c>origin/{branchName}</c> first; if that fails or the branch is not an
    /// ancestor, we KEEP the branch. Checked-out branches can't be deleted by <c>branch -D</c> and
    /// fail harmlessly. Never throws — cleanup must never fail a successful submission.
    /// </para>
    /// </summary>
    private async Task CleanupLegacyTempBranchesAsync(
        string inPlaceCheckout, string branchName, string token, CancellationToken ct)
    {
        try
        {
            // Drop admin entries for temp worktrees whose directory is already gone.
            try { await RunGitInDirAsync(inPlaceCheckout, "worktree prune", null, ct); } catch { /* best-effort */ }

            // Refresh the remote-tracking ref so the ancestor check below is accurate.
            try { await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {branchName}", token, ct); }
            catch { return; /* can't verify reachability without a current ref — keep branches */ }

            var listing = await RunGitCaptureInDirAsync(inPlaceCheckout,
                "for-each-ref --format=%(refname:short) refs/heads/vdt-temp-final-*", ct);
            var branches = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var b in branches)
            {
                ct.ThrowIfCancellationRequested();

                // Reachable from origin/{branchName} ⇒ commits preserved on the remote ⇒ safe to delete.
                bool reachable;
                try
                {
                    await RunGitInDirAsync(inPlaceCheckout,
                        $"merge-base --is-ancestor {b} origin/{branchName}", null, ct);
                    reachable = true; // exit 0
                }
                catch { reachable = false; } // exit 1 (not ancestor) or error → keep the branch

                if (!reachable) continue;

                try
                {
                    await RunGitInDirAsync(inPlaceCheckout, $"branch -D {b}", null, ct);
                    _logger.LogInformation(
                        "Cleaned up legacy temp branch {Branch} (reachable from origin/{Final})", b, branchName);
                }
                catch (Exception ex)
                {
                    // Checked out or otherwise undeletable — leave it; not worth failing over.
                    _logger.LogDebug(ex, "Skipped deleting legacy temp branch {Branch}", b);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy temp-branch cleanup skipped (non-fatal)");
        }
    }

    /// <summary>Get the branch run scope from config (matches agent branch naming convention).</summary>
    private string? cfg_BranchRunScope()
    {
        // BranchProvider uses first 8 chars of a hash for run scope in branch names
        // e.g., agent/70f50d28/frontend-engineer-1/...
        // The RunId in LocalPlatformContext may differ from the branch scope.
        // Try to extract from existing merged PRs in the local DB.
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT head_branch FROM local_pull_requests
                WHERE run_id = @runId AND head_branch LIKE 'agent/%' AND state = 'merged'
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            var result = cmd.ExecuteScalar() as string;
            if (result is not null)
            {
                // Extract run scope: agent/{runScope}/...
                var parts = result.Split('/');
                if (parts.Length >= 2) return parts[1];
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns the set of head_branch values from merged PRs in the current run.
    /// Used to filter agent branches so only code from winning/merged PRs is included
    /// in the final submission — old/superseded/failed PR branches are excluded.
    /// </summary>
    private List<string> GetMergedPrBranches()
    {
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT head_branch FROM local_pull_requests
                WHERE run_id = @runId AND state = 'merged' AND head_branch LIKE 'agent/%'
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            var branches = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var branch = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(branch))
                    branches.Add(branch);
            }
            _logger.LogInformation("Found {Count} merged PR branches for final submission filtering", branches.Count);
            return branches;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query merged PR branches — will merge all agent branches");
            return new List<string>();
        }
    }

    private async Task PushFromBareRepoAsync(string branchName, string repo, string token, CancellationToken ct)
    {
        // In Local mode, PRs merge into the WORKING branch (branchName), not defaultBranch.
        // Push the working branch from the bare repo to the same branch on the remote.
        // If branchName doesn't exist in the bare repo (legacy layout), fall back to defaultBranch.
        var defaultBranch = _config.Value.Project.DefaultBranch ?? "main";
        var sourceBranch = branchName;

        // Verify the source branch exists in the bare repo; fall back to defaultBranch if not
        try
        {
            var verifyPsi = new ProcessStartInfo("git",
                $"rev-parse --verify refs/heads/{branchName}")
            {
                WorkingDirectory = _ctx.BareRepo.BareRepoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            verifyPsi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
            using var verifyProc = Process.Start(verifyPsi)!;
            await verifyProc.WaitForExitAsync(ct);
            if (verifyProc.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Branch {Branch} not found in bare repo — falling back to {Default}",
                    branchName, defaultBranch);
                sourceBranch = defaultBranch;
            }
        }
        catch
        {
            sourceBranch = defaultBranch;
        }

        var remoteUrl = $"https://github.com/{repo}.git";
        // Use --force: bare repo pushing to raw URL has no tracking ref for --force-with-lease.
        var psi = new ProcessStartInfo("git",
            $"push \"{remoteUrl}\" refs/heads/{sourceBranch}:refs/heads/{branchName} --force")
        {
            WorkingDirectory = _ctx.BareRepo.BareRepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
        SetGitAuthEnv(psi, token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        await stdoutTask;
        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            stderr = stderr.Replace(token.Trim(), "***");
            throw new InvalidOperationException($"git push failed (exit {proc.ExitCode}): {stderr}");
        }
        _logger.LogInformation("Pushed {Branch} to github.com/{Repo} from bare repo", branchName, repo);
    }

    private async Task PushFromDirAsync(string workDir, string branchName, string repo, string token, CancellationToken ct)
    {
        var remoteUrl = $"https://github.com/{repo}.git";
        // Use --force (not --force-with-lease) because this temp worktree has no
        // tracking ref for the raw push URL, so --force-with-lease always fails with
        // "stale info". Final submission is a deliberate overwrite of the working branch.
        var psi = new ProcessStartInfo("git",
            $"push \"{remoteUrl}\" HEAD:refs/heads/{branchName} --force")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        SetGitAuthEnv(psi, token);

        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(120));
        using var proc2 = Process.Start(psi)!;
        var stderrTask2 = proc2.StandardError.ReadToEndAsync(cts2.Token);
        var stdoutTask2 = proc2.StandardOutput.ReadToEndAsync(cts2.Token);
        await proc2.WaitForExitAsync(cts2.Token);
        await stdoutTask2;
        if (proc2.ExitCode != 0)
        {
            var stderr2 = await stderrTask2;
            stderr2 = stderr2.Replace(token.Trim(), "***");
            throw new InvalidOperationException($"git push failed (exit {proc2.ExitCode}): {stderr2}");
        }
    }

    private static void SetGitAuthEnv(ProcessStartInfo psi, string token)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "echo";
        var authHeader = $"Authorization: Basic {Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token.Trim()}"))}";
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader";
        psi.Environment["GIT_CONFIG_VALUE_0"] = authHeader;
    }

    /// <summary>
    /// Ensures the configured working branch exists on the GitHub remote.
    /// If it doesn't exist, creates it from the default branch so the final PR
    /// can target it. This is the correct behavior — the user configured this
    /// branch as their working branch, so it should be created automatically.
    /// </summary>
    private async Task EnsureRemoteBranchExistsAsync(
        string repo, string workingBranch, string defaultBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingBranch) || workingBranch == defaultBranch)
            return; // targeting the default branch — nothing to create

        var exists = await RemoteBranchExistsAsync(repo, workingBranch, ct);
        if (exists)
        {
            _logger.LogInformation("Working branch '{Branch}' exists on remote", workingBranch);
            return;
        }

        _logger.LogInformation(
            "Working branch '{Branch}' does not exist on remote — creating from '{Default}'",
            workingBranch, defaultBranch);

        // Get the default branch HEAD SHA via GitHub API
        try
        {
            var sha = await RunCaptureAsync("gh",
                $"api repos/{repo}/git/ref/heads/{defaultBranch} --jq .object.sha", ct);
            sha = sha?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(sha))
                throw new InvalidOperationException($"Could not resolve SHA for {defaultBranch}");

            // Create the branch via GitHub API
            var createRef = $"api repos/{repo}/git/refs -f ref=refs/heads/{workingBranch} -f sha={sha}";
            await RunCaptureAsync("gh", createRef, ct);

            _logger.LogInformation("✅ Created working branch '{Branch}' on remote from {Default} ({Sha})",
                workingBranch, defaultBranch, sha[..Math.Min(8, sha.Length)]);
        }
        catch (Exception ex)
        {
            // If creation fails (e.g., race condition — someone else just created it), re-check
            var recheckExists = await RemoteBranchExistsAsync(repo, workingBranch, ct);
            if (recheckExists)
            {
                _logger.LogInformation("Working branch '{Branch}' was created concurrently — proceeding", workingBranch);
                return;
            }

            throw new InvalidOperationException(
                $"Unable to create working branch '{workingBranch}' on remote from '{defaultBranch}'. " +
                $"The branch may be protected or your token may not have push permission. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks whether a branch exists on the GitHub remote using `gh api`.
    /// Returns false if the branch is missing or the check fails (safe fallback).
    /// </summary>
    private async Task<bool> RemoteBranchExistsAsync(string repo, string branch, CancellationToken ct)
    {
        try
        {
            var output = await RunCaptureAsync("gh",
                $"api repos/{repo}/branches/{branch} --jq .name", ct);
            return !string.IsNullOrWhiteSpace(output?.Trim());
        }
        catch
        {
            // API error or branch not found — assume it doesn't exist
            return false;
        }
    }

    private async Task RunGitInDirAsync(string workDir, string args, string? token, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (token is not null) SetGitAuthEnv(psi, token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var proc = Process.Start(psi)!;
        // Read pipes concurrently to prevent pipe deadlock (Lesson #44)
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        var stderr = await stderrTask;
        await stdoutTask;
        if (proc.ExitCode != 0)
        {
            if (token is not null) stderr = stderr.Replace(token.Trim(), "***");
            throw new InvalidOperationException($"git {args.Split(' ')[0]} failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    private async Task<string> RunGitCaptureInDirAsync(string workDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        return output;
    }

    private async Task<int> CreatePRViaGhCliAsync(
        string repo, string headBranch, string baseBranch,
        string title, string body, CancellationToken ct)
    {
        // Truncate body for CLI (gh can handle long bodies but shell has limits)
        var truncBody = body.Length > 60_000 ? body[..60_000] + "\n\n...(truncated)" : body;

        // Write body to temp file to avoid shell escaping issues
        var bodyFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(bodyFile, truncBody, ct);

            // Try to create labels (they may not exist in the target repo)
            foreach (var label in new[] { "final-integration", "awaiting-human-review", "AI-Generated" })
            {
                try
                {
                    await RunCaptureAsync("gh",
                        $"label create \"{label}\" --repo \"{repo}\" --force --color 0E8A16",
                        ct);
                }
                catch
                {
                    _logger.LogDebug("Could not create label '{Label}' — may already exist or insufficient permissions", label);
                }
            }

            var output = await RunCaptureAsync("gh",
                $"pr create --repo \"{repo}\" --head \"{headBranch}\" --base \"{baseBranch}\" " +
                $"--title \"{title.Replace("\"", "\\\"")}\" --body-file \"{bodyFile}\" " +
                $"--label \"final-integration\" --label \"awaiting-human-review\" --label \"AI-Generated\"",
                ct);

            // gh pr create outputs the PR URL like: https://github.com/owner/repo/pull/123
            var prUrl = output?.Trim() ?? "";
            var prNum = ExtractPrNumber(prUrl);
            if (prNum <= 0)
                throw new InvalidOperationException($"Failed to parse PR number from gh output: {prUrl}");

            return prNum;
        }
        finally
        {
            try { File.Delete(bodyFile); } catch { }
        }
    }

    private async Task PersistSubmissionAsync(int prNumber, string branchName, string title, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();

        // Ensure table exists
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_final_submissions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                pr_number INTEGER NOT NULL,
                branch_name TEXT NOT NULL,
                title TEXT NOT NULL,
                submitted_at TEXT NOT NULL
            )
            """;
        await createCmd.ExecuteNonQueryAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_final_submissions (run_id, pr_number, branch_name, title, submitted_at)
            VALUES (@runId, @prNumber, @branch, @title, @now)
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@prNumber", prNumber);
        cmd.Parameters.AddWithValue("@branch", branchName);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static int ExtractPrNumber(string url)
    {
        // Parse "https://github.com/owner/repo/pull/123" → 123
        var parts = url.Split('/');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var num))
            return num;
        return -1;
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"{exe} {args.Split(' ')[0]}... failed (exit {proc.ExitCode}): {stderr}");
        }
        return output;
    }

    private static async Task RunAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(cts.Token);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"{exe} failed (exit {proc.ExitCode}): {stderr}");
        }
    }
}
