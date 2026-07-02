using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IPullRequestService"/> backed by SQLite metadata + a local bare git repository.
/// Agents create, review, and merge PRs locally without touching the enterprise platform.
/// Git is the authority for code; SQLite tracks PR lifecycle metadata.
/// </summary>
public sealed class LocalPullRequestService : IPullRequestService
{
    private readonly LocalPlatformContext _ctx;
    private readonly ILogger<LocalPullRequestService> _logger;

    public LocalPullRequestService(LocalPlatformContext ctx, ILogger<LocalPullRequestService> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task<PlatformPullRequest> CreateAsync(
        string title, string body, string headBranch, string baseBranch,
        IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        // Ensure AI-Generated label is always present (required by Timeline, cleanup, etc.)
        var allLabels = labels.Contains("AI-Generated", StringComparer.OrdinalIgnoreCase)
            ? labels
            : labels.Concat(new[] { "AI-Generated" }).ToList();
        var now = DateTimeOffset.UtcNow.ToString("O");
        long id;
        int number;

        using (var conn = _ctx.CreateConnection())
        {
            // Atomic number generation: subquery inside INSERT eliminates TOCTOU race
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_pull_requests (run_id, number, title, body, state, head_branch, base_branch, created_at, updated_at)
                VALUES (@runId, (SELECT COALESCE(MAX(number), 0) + 1 FROM local_pull_requests WHERE run_id = @runId), @title, @body, 'open', @head, @base, @now, @now)
                RETURNING id, number
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@body", body ?? "");
            cmd.Parameters.AddWithValue("@head", headBranch);
            cmd.Parameters.AddWithValue("@base", baseBranch);
            cmd.Parameters.AddWithValue("@now", now);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            id = reader.GetInt64(0);
            number = reader.GetInt32(1);
        }

        if (labels.Count > 0)
            await SetLabelsAsync(number, labels, ct);

        // Populate file diff from git
        await PopulateFileDiffAsync(id, baseBranch, headBranch, ct);

        _logger.LogInformation("Local PR #{Number} created: {Title} ({Head} → {Base})", number, title, headBranch, baseBranch);
        return MapPr(id, number, title, body ?? "", "open", headBranch, baseBranch, now, now, null, labels);
    }

    public async Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, number, title, body, state, head_branch, base_branch, created_at, updated_at, merged_at
            FROM local_pull_requests WHERE run_id = @runId AND number = @number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return await MapPrFromReaderAsync(reader, ct);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default)
    {
        return await ListByStateAsync("open", ct);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, number, title, body, state, head_branch, base_branch, created_at, updated_at, merged_at
            FROM local_pull_requests WHERE run_id = @runId
            ORDER BY number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        var results = new List<PlatformPullRequest>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(await MapPrFromReaderAsync(reader, ct));
        return results;
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default)
    {
        return await ListByStateAsync("merged", ct);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllForProjectAsync(CancellationToken ct = default)
    {
        return await ListAllAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(string agentName, CancellationToken ct = default)
    {
        var all = await ListAllAsync(ct);
        return all.Where(p => p.Title.Contains(agentName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sets = new List<string> { "updated_at = @now" };

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();

        if (title is not null) { sets.Add("title = @title"); cmd.Parameters.AddWithValue("@title", title); }
        if (body is not null) { sets.Add("body = @body"); cmd.Parameters.AddWithValue("@body", body); }

        cmd.CommandText = $"UPDATE local_pull_requests SET {string.Join(", ", sets)} WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        if (labels is not null)
            await SetLabelsAsync(id, labels, ct);
    }

    public async Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default)
    {
        var pr = await GetAsync(id, ct);
        if (pr is null) throw new InvalidOperationException("PR not found");
        if (pr.State != "open") throw new InvalidOperationException($"PR is {pr.State}, not open");

        try
        {
            var msg = commitMessage ?? $"Merge PR #{id}: {pr.Title}";
            await _ctx.BareRepo.MergeBranchAsync(pr.HeadBranch, pr.BaseBranch, msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git merge failed for local PR #{Number}", id);
            throw new PlatformConflictException(PlatformConflictKind.NotMergeable,
                $"Merge conflict: {ex.Message}", ex);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        long? prInternalId;

        using (var conn = _ctx.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE local_pull_requests SET state = 'merged', merged_at = @now, updated_at = @now 
                WHERE run_id = @runId AND number = @number
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            cmd.Parameters.AddWithValue("@number", id);
            cmd.Parameters.AddWithValue("@now", now);
            var result = await cmd.ExecuteScalarAsync(ct);
            prInternalId = result is long internalId ? internalId : null;
        }

        // Refresh file diff from the merge commit — shows exactly what was introduced
        if (prInternalId.HasValue)
        {
            try
            {
                // Get the merge commit SHA (current HEAD of the target branch after merge)
                var mergeCommitSha = await _ctx.BareRepo.GetBranchHeadAsync(pr.BaseBranch, ct);
                if (!string.IsNullOrEmpty(mergeCommitSha))
                {
                    // Diff the merge commit against its first parent = what the PR introduced
                    var numstat = await _ctx.BareRepo.GetDiffNumstatAsync($"{mergeCommitSha}~1", mergeCommitSha, ct);
                    if (!string.IsNullOrWhiteSpace(numstat))
                    {
                        using var conn2 = _ctx.CreateConnection();
                        using var delCmd = conn2.CreateCommand();
                        delCmd.CommandText = "DELETE FROM local_pr_files WHERE pr_id = @prId";
                        delCmd.Parameters.AddWithValue("@prId", prInternalId.Value);
                        await delCmd.ExecuteNonQueryAsync(ct);

                        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var parts = line.Split('\t');
                            if (parts.Length < 3) continue;
                            var adds = int.TryParse(parts[0], out var a) ? a : 0;
                            var dels = int.TryParse(parts[1], out var d) ? d : 0;
                            var path = parts[2];
                            var status = adds > 0 && dels > 0 ? "modified" : adds > 0 ? "added" : "removed";
                            using var insCmd = conn2.CreateCommand();
                            insCmd.CommandText = "INSERT OR REPLACE INTO local_pr_files (pr_id, path, status, additions, deletions) VALUES (@prId, @path, @status, @adds, @dels)";
                            insCmd.Parameters.AddWithValue("@prId", prInternalId.Value);
                            insCmd.Parameters.AddWithValue("@path", path);
                            insCmd.Parameters.AddWithValue("@status", status);
                            insCmd.Parameters.AddWithValue("@adds", adds);
                            insCmd.Parameters.AddWithValue("@dels", dels);
                            await insCmd.ExecuteNonQueryAsync(ct);
                        }
                        _logger.LogDebug("Refreshed file diff for PR #{Number} from merge commit {Sha}", id, mergeCommitSha[..8]);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh file diff from merge commit for PR #{Number}", id);
            }
        }

        _logger.LogInformation("Local PR #{Number} merged: {Title}", id, pr.Title);

        // Clean up the head branch from the bare repo after successful merge.
        // Mirrors GitHub's auto-delete-branch-on-merge behavior. Prevents branch
        // accumulation in the local bare repo across many PRs (lesson #155).
        try
        {
            await _ctx.BareRepo.DeleteBranchAsync(pr.HeadBranch, ct);
            _logger.LogDebug("Deleted merged branch {Branch} from local bare repo", pr.HeadBranch);
        }
        catch (Exception ex)
        {
            // Non-fatal — branch may already be deleted or protected
            _logger.LogDebug(ex, "Could not delete merged branch {Branch} from bare repo (non-fatal)", pr.HeadBranch);
        }

        // Auto-close linked work items (mirrors GitHub's "Closes #N" auto-close on merge)
        await AutoCloseLinkedWorkItemsAsync(id, pr.Body, now, ct);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_pull_requests SET state = 'closed', closed_at = @now, updated_at = @now
            WHERE run_id = @runId AND number = @number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Local PR #{Number} closed", id);
    }

    public async Task SetStateAsync(int id, string state, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("O");
        cmd.CommandText = state switch
        {
            "merged" => "UPDATE local_pull_requests SET state = 'merged', merged_at = @now, updated_at = @now WHERE run_id = @runId AND number = @number",
            "closed" => "UPDATE local_pull_requests SET state = 'closed', closed_at = @now, updated_at = @now WHERE run_id = @runId AND number = @number",
            "open" => "UPDATE local_pull_requests SET state = 'open', merged_at = NULL, closed_at = NULL, updated_at = @now WHERE run_id = @runId AND number = @number",
            _ => throw new ArgumentException($"Invalid PR state: {state}")
        };
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Local PR #{Number} state set to {State} (admin)", id, state);
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default)
    {
        // Refresh diff from git for open PRs — commits pushed after PR creation
        // (e.g., strategy framework applies winner code) make the cached data stale.
        await RefreshFileDiffIfOpenAsync(prId, ct);

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT path FROM local_pr_files WHERE pr_id = (SELECT id FROM local_pull_requests WHERE run_id = @runId AND number = @number)";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", prId);
        var results = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default)
    {
        // Refresh diff from git for open PRs — commits pushed after PR creation
        // (e.g., strategy framework applies winner code) make the cached data stale.
        await RefreshFileDiffIfOpenAsync(prId, ct);

        // Read base/head branches for on-the-fly patch generation
        string? baseBranch = null, headBranch = null;
        using (var branchConn = _ctx.CreateConnection())
        using (var branchCmd = branchConn.CreateCommand())
        {
            branchCmd.CommandText = "SELECT base_branch, head_branch FROM local_pull_requests WHERE run_id = @runId AND number = @number";
            branchCmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            branchCmd.Parameters.AddWithValue("@number", prId);
            using var branchReader = await branchCmd.ExecuteReaderAsync(ct);
            if (await branchReader.ReadAsync(ct))
            {
                baseBranch = branchReader.GetString(0);
                headBranch = branchReader.GetString(1);
            }
        }

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, status, additions, deletions FROM local_pr_files WHERE pr_id = (SELECT id FROM local_pull_requests WHERE run_id = @runId AND number = @number)";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", prId);
        var results = new List<PlatformFileDiff>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PlatformFileDiff
            {
                FileName = reader.GetString(0),
                Status = reader.GetString(1),
                Additions = reader.GetInt32(2),
                Deletions = reader.GetInt32(3),
            });
        }

        // Populate Patch content on-the-fly from git diff (not stored in DB to avoid bloat)
        if (baseBranch is not null && headBranch is not null && _ctx.BareRepo.IsInitialized)
        {
            foreach (var diff in results)
            {
                try
                {
                    diff.Patch = await _ctx.BareRepo.GetFilePatchAsync(baseBranch, headBranch, diff.FileName, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get patch for {File} in PR #{Number}", diff.FileName, prId);
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return new List<string>();
    }

    public async Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return new List<PlatformCommitInfo>();
    }

    public async Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return false;
    }

    public async Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default)
    {
        // GitHub semantics: "update branch" merges base INTO head (not a rebase).
        // Uses MergeBranchIntoAsync which has auto-resolve for content conflicts.
        try
        {
            var pr = await GetAsync(prId, ct);
            if (pr is null) return false;
            await _ctx.BareRepo.MergeBranchIntoAsync(pr.HeadBranch, pr.BaseBranch, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateBranchAsync (merge-into) failed for local PR #{Number}", prId);
            return false;
        }
    }

    public async Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default)
    {
        // Force path: rebase head onto base (rewrites the PR branch). This is a stronger
        // operation than UpdateBranchAsync and is only used as a fallback.
        try
        {
            var pr = await GetAsync(prId, ct);
            if (pr is null) return false;
            await _ctx.BareRepo.RebaseBranchOntoAsync(pr.HeadBranch, pr.BaseBranch, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RebaseBranchAsync failed for local PR #{Number}", prId);
            return false;
        }
    }

    public async Task LinkWorkItemAsync(int prId, int workItemId, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();

        // Resolve work item number → internal row id
        using var wiCmd = conn.CreateCommand();
        wiCmd.CommandText = "SELECT id FROM local_work_items WHERE run_id = @runId AND number = @wiNumber";
        wiCmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        wiCmd.Parameters.AddWithValue("@wiNumber", workItemId);
        var wiRowId = await wiCmd.ExecuteScalarAsync(ct);
        if (wiRowId is null)
        {
            _logger.LogDebug("LinkWorkItemAsync: work item #{WiNumber} not found for run {RunId}", workItemId, _ctx.RunId);
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO local_work_item_links (work_item_id, linked_pr_number, link_type)
            VALUES (@wiId, @prNumber, 'closes')
            """;
        cmd.Parameters.AddWithValue("@wiId", (long)wiRowId);
        cmd.Parameters.AddWithValue("@prNumber", prId);
        await cmd.ExecuteNonQueryAsync(ct);

        // Also append "Closes #N" to the PR body so the timeline's ParseLinkedIssueNumber
        // can find the link (GitHub does this natively; Local must do it explicitly).
        try
        {
            using var bodyCmd = conn.CreateCommand();
            bodyCmd.CommandText = "SELECT body FROM local_pull_requests WHERE run_id = @runId AND number = @prNumber";
            bodyCmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            bodyCmd.Parameters.AddWithValue("@prNumber", prId);
            var currentBody = await bodyCmd.ExecuteScalarAsync(ct) as string ?? "";
            var closesRef = $"Closes #{workItemId}";
            if (!currentBody.Contains(closesRef, StringComparison.OrdinalIgnoreCase))
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE local_pull_requests SET body = @body WHERE run_id = @runId AND number = @prNumber";
                updateCmd.Parameters.AddWithValue("@body", currentBody + $"\n\n{closesRef}");
                updateCmd.Parameters.AddWithValue("@runId", _ctx.RunId);
                updateCmd.Parameters.AddWithValue("@prNumber", prId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to append Closes #{WiNumber} to PR #{PrNumber} body", workItemId, prId);
        }

        _logger.LogDebug("Linked PR #{PrNumber} → work item #{WiNumber}", prId, workItemId);
    }

    public async Task<IReadOnlyList<int>> GetLinkedWorkItemIdsAsync(int prId, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT wi.number
            FROM local_work_item_links l
            JOIN local_work_items wi ON wi.id = l.work_item_id
            WHERE l.linked_pr_number = @prNumber AND wi.run_id = @runId
            """;
        cmd.Parameters.AddWithValue("@prNumber", prId);
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);

        var results = new List<int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetInt32(0));
        return results;
    }

    // ── Private helpers ──

    /// <summary>
    /// For open PRs, re-computes the file diff from git. Commits pushed after PR
    /// creation (e.g., strategy framework applying winner code, self-assessment fixes)
    /// make the cached <c>local_pr_files</c> rows stale. Merged/closed PRs keep their
    /// cached data since branches may be deleted.
    /// </summary>
    private async Task RefreshFileDiffIfOpenAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, state, base_branch, head_branch
                FROM local_pull_requests WHERE run_id = @runId AND number = @number
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            cmd.Parameters.AddWithValue("@number", prNumber);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;

            var internalId = reader.GetInt64(0);
            var state = reader.GetString(1);
            if (state != "open") return; // merged/closed PRs use cached data

            var baseBranch = reader.GetString(2);
            var headBranch = reader.GetString(3);
            reader.Close();

            await PopulateFileDiffAsync(internalId, baseBranch, headBranch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh file diff for PR #{Number}", prNumber);
        }
    }

    private async Task PopulateFileDiffAsync(long prInternalId, string baseBranch, string headBranch, CancellationToken ct)
    {
        try
        {
            var numstat = await _ctx.BareRepo.GetDiffNumstatAsync(baseBranch, headBranch, ct);
            if (string.IsNullOrWhiteSpace(numstat)) return;

            using var conn = _ctx.CreateConnection();

            // Clear existing entries
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM local_pr_files WHERE pr_id = @prId";
            delCmd.Parameters.AddWithValue("@prId", prInternalId);
            await delCmd.ExecuteNonQueryAsync(ct);

            // Parse numstat: "additions\tdeletions\tpath" per line (binary files show "-\t-\tpath")
            foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                var adds = int.TryParse(parts[0], out var a) ? a : 0;
                var dels = int.TryParse(parts[1], out var d) ? d : 0;
                var path = parts[2];
                var status = adds > 0 && dels > 0 ? "modified" : adds > 0 ? "added" : "removed";

                using var insCmd = conn.CreateCommand();
                insCmd.CommandText = """
                    INSERT OR REPLACE INTO local_pr_files (pr_id, path, status, additions, deletions)
                    VALUES (@prId, @path, @status, @adds, @dels)
                    """;
                insCmd.Parameters.AddWithValue("@prId", prInternalId);
                insCmd.Parameters.AddWithValue("@path", path);
                insCmd.Parameters.AddWithValue("@status", status);
                insCmd.Parameters.AddWithValue("@adds", adds);
                insCmd.Parameters.AddWithValue("@dels", dels);
                await insCmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to populate file diff for PR internal id {PrId}", prInternalId);
        }
    }

    private async Task<IReadOnlyList<PlatformPullRequest>> ListByStateAsync(string state, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, number, title, body, state, head_branch, base_branch, created_at, updated_at, merged_at
            FROM local_pull_requests WHERE run_id = @runId AND state = @state
            ORDER BY number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@state", state);
        var results = new List<PlatformPullRequest>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(await MapPrFromReaderAsync(reader, ct));
        return results;
    }

    /// <summary>
    /// Mirrors GitHub's auto-close behavior: when a PR is merged, close any work items
    /// linked via the <c>local_work_item_links</c> table OR referenced by "Closes #N" in
    /// the PR body. Without this, merged PRs leave work items open and the SE can't
    /// detect task completion on restart.
    /// </summary>
    private async Task AutoCloseLinkedWorkItemsAsync(int prNumber, string? prBody, string now, CancellationToken ct)
    {
        try
        {
            var issueNumbers = new HashSet<int>();

            // Source 1: explicit DB links
            var linked = await GetLinkedWorkItemIdsAsync(prNumber, ct);
            foreach (var n in linked) issueNumbers.Add(n);

            // Source 2: parse "Closes #N" from PR body (covers pre-fix PRs without DB links)
            if (!string.IsNullOrEmpty(prBody))
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(prBody,
                        @"(?:Closes|Fixes|Resolves)\s+#(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out var num))
                        issueNumbers.Add(num);
                }
            }

            if (issueNumbers.Count == 0) return;

            using var conn = _ctx.CreateConnection();
            foreach (var issueNum in issueNumbers)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE local_work_items SET state = 'closed', closed_at = @now, updated_at = @now
                    WHERE run_id = @runId AND number = @number AND state = 'open'
                    """;
                cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
                cmd.Parameters.AddWithValue("@number", issueNum);
                cmd.Parameters.AddWithValue("@now", now);
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 0)
                    _logger.LogInformation("Auto-closed work item #{Number} on merge of PR #{PrNumber}", issueNum, prNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to auto-close linked work items for PR #{PrNumber}", prNumber);
        }
    }

    private async Task<long?> GetPrIdAsync(int number, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM local_pull_requests WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", number);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long id ? id : null;
    }

    private async Task<PlatformPullRequest> MapPrFromReaderAsync(SqliteDataReader reader, CancellationToken ct)
    {
        var id = reader.GetInt64(0);
        var number = reader.GetInt32(1);
        var headBranch = reader.GetString(5);
        var state = reader.GetString(4);

        // Resolve HEAD SHA from bare repo for open PRs. Without this, HeadSha is always ""
        // and the PM's SHA-dedup (which skips re-review when HeadSha matches) never detects
        // new commits after rework — causing PRs to stall for hours in awaiting-pm-approval.
        string headSha = "";
        if (string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
        {
            try { headSha = await _ctx.BareRepo.GetBranchHeadAsync(headBranch, ct) ?? ""; }
            catch { /* best effort — empty SHA falls through to review */ }
        }

        var labels = await GetLabelsByPrIdAsync(id, ct);
        return MapPr(id, number,
            reader.GetString(2), reader.GetString(3), state,
            headBranch, reader.GetString(6),
            reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            labels, headSha);
    }

    private async Task<IReadOnlyList<string>> GetLabelsByPrIdAsync(long prId, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT label FROM local_pr_labels WHERE pr_id = @prId";
        cmd.Parameters.AddWithValue("@prId", prId);
        var labels = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            labels.Add(reader.GetString(0));
        return labels;
    }

    private async Task SetLabelsAsync(int number, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var prId = await GetPrIdAsync(number, ct);
        if (prId is null) return;

        using var conn = _ctx.CreateConnection();
        using var tx = conn.BeginTransaction();
        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM local_pr_labels WHERE pr_id = @prId";
        delCmd.Parameters.AddWithValue("@prId", prId.Value);
        delCmd.Transaction = tx;
        await delCmd.ExecuteNonQueryAsync(ct);

        foreach (var label in labels)
        {
            using var insCmd = conn.CreateCommand();
            insCmd.CommandText = "INSERT INTO local_pr_labels (pr_id, label) VALUES (@prId, @label)";
            insCmd.Parameters.AddWithValue("@prId", prId.Value);
            insCmd.Parameters.AddWithValue("@label", label);
            insCmd.Transaction = tx;
            await insCmd.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    private PlatformPullRequest MapPr(long id, int number, string title, string body,
        string state, string headBranch, string baseBranch,
        string createdAt, string updatedAt, string? mergedAt,
        IReadOnlyList<string>? labels = null, string? headSha = null)
    {
        return new PlatformPullRequest
        {
            Number = number,
            Title = title,
            Body = body,
            State = state,
            HeadBranch = headBranch,
            HeadSha = headSha ?? "",
            BaseBranch = baseBranch,
            Url = $"/repository/pull-request/{number}",
            Labels = (labels ?? Array.Empty<string>()).ToList(),
            CreatedAt = DateTimeOffset.Parse(createdAt).DateTime,
            UpdatedAt = DateTimeOffset.Parse(updatedAt).DateTime,
            MergedAt = mergedAt is not null ? DateTimeOffset.Parse(mergedAt).DateTime : null,
        };
    }
}
