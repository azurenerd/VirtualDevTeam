using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IReviewService"/> backed by SQLite. Stores PR comments, reviews,
/// and inline threads locally. No platform API calls.
/// </summary>
public sealed class LocalReviewService : IReviewService
{
    private readonly LocalPlatformContext _ctx;
    private readonly ILogger<LocalReviewService> _logger;

    public LocalReviewService(LocalPlatformContext ctx, ILogger<LocalReviewService> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task AddCommentAsync(int prId, string comment, CancellationToken ct = default)
    {
        var prInternalId = await GetPrIdAsync(prId, ct);
        if (prInternalId is null) return;

        var author = AgentCallContext.CurrentAgentId ?? "system";
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_pr_comments (pr_id, author, body, created_at)
            VALUES (@prId, @author, @body, @now)
            """;
        cmd.Parameters.AddWithValue("@prId", prInternalId.Value);
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@body", comment);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int prId, CancellationToken ct = default)
    {
        var prInternalId = await GetPrIdAsync(prId, ct);
        if (prInternalId is null) return new List<PlatformComment>();

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, author, body, created_at FROM local_pr_comments WHERE pr_id = @prId ORDER BY created_at";
        cmd.Parameters.AddWithValue("@prId", prInternalId.Value);

        var results = new List<PlatformComment>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PlatformComment
            {
                Id = reader.GetInt64(0),
                Author = reader.GetString(1),
                Body = reader.GetString(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3)).DateTime,
            });
        }
        return results;
    }

    public async Task AddReviewAsync(int prId, string body, string eventType, CancellationToken ct = default)
    {
        var prInternalId = await GetPrIdAsync(prId, ct);
        if (prInternalId is null) return;

        var author = AgentCallContext.CurrentAgentId ?? "system";
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_pr_reviews (pr_id, reviewer, state, body, created_at)
            VALUES (@prId, @reviewer, @state, @body, @now)
            """;
        cmd.Parameters.AddWithValue("@prId", prInternalId.Value);
        cmd.Parameters.AddWithValue("@reviewer", author);
        cmd.Parameters.AddWithValue("@state", eventType);
        cmd.Parameters.AddWithValue("@body", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Review added on local PR #{Number}: {EventType}", prId, eventType);
    }

    public async Task CreateReviewWithInlineCommentsAsync(
        int prId, string body, string eventType,
        IReadOnlyList<PlatformInlineComment> comments,
        string? commitId = null, CancellationToken ct = default)
    {
        var prInternalId = await GetPrIdAsync(prId, ct);
        if (prInternalId is null) return;

        var author = AgentCallContext.CurrentAgentId ?? "system";
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_pr_reviews (pr_id, reviewer, state, body, created_at)
            VALUES (@prId, @reviewer, @state, @body, @now)
            """;
        cmd.Parameters.AddWithValue("@prId", prInternalId.Value);
        cmd.Parameters.AddWithValue("@reviewer", author);
        cmd.Parameters.AddWithValue("@state", eventType);
        cmd.Parameters.AddWithValue("@body", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        // Store inline comments as threads
        if (comments is { Count: > 0 })
        {
            foreach (var ic in comments)
            {
                using var threadCmd = conn.CreateCommand();
                threadCmd.CommandText = """
                    INSERT INTO local_pr_threads (pr_id, path, line, body, author, created_at)
                    VALUES (@prId, @path, @line, @body, @author, @now)
                    """;
                threadCmd.Parameters.AddWithValue("@prId", prInternalId.Value);
                threadCmd.Parameters.AddWithValue("@path", ic.FilePath ?? "");
                threadCmd.Parameters.AddWithValue("@line", (object?)ic.Line ?? DBNull.Value);
                threadCmd.Parameters.AddWithValue("@body", ic.Body ?? "");
                threadCmd.Parameters.AddWithValue("@author", author);
                threadCmd.Parameters.AddWithValue("@now", now);
                await threadCmd.ExecuteNonQueryAsync(ct);
            }
        }

        _logger.LogDebug("Review with inline comments created on local PR #{Number}", prId);
    }

    public async Task<IReadOnlyList<PlatformReviewThread>> GetThreadsAsync(int prId, CancellationToken ct = default)
    {
        var prInternalId = await GetPrIdAsync(prId, ct);
        if (prInternalId is null) return new List<PlatformReviewThread>();

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, line, body, author, resolved, created_at FROM local_pr_threads WHERE pr_id = @prId ORDER BY created_at";
        cmd.Parameters.AddWithValue("@prId", prInternalId.Value);

        var results = new List<PlatformReviewThread>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PlatformReviewThread
            {
                Id = reader.GetInt64(0),
                ThreadId = reader.GetInt64(0).ToString(),
                FilePath = reader.GetString(1),
                Line = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Body = reader.GetString(3),
                Author = reader.IsDBNull(4) ? "system" : reader.GetString(4),
                IsResolved = reader.GetInt32(5) != 0,
                CreatedAt = DateTimeOffset.Parse(reader.GetString(6)).DateTime,
            });
        }
        return results;
    }

    public async Task ResolveThreadAsync(
        int prId, string threadId, string replyBody,
        CancellationToken ct = default)
    {
        if (!long.TryParse(threadId, out var threadIdLong))
            return;

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_pr_threads SET resolved = 1, body = @body, created_at = @now 
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", threadIdLong);
        cmd.Parameters.AddWithValue("@body", replyBody);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Thread {ThreadId} resolved", threadId);
    }

    public async Task ReplyToThreadAsync(
        int prId, string threadId, string replyBody,
        CancellationToken ct = default)
    {
        if (!long.TryParse(threadId, out var threadIdLong))
            return;

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE local_pr_threads SET body = body || char(10) || @reply 
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", threadIdLong);
        cmd.Parameters.AddWithValue("@reply", replyBody);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Reply added to thread {ThreadId}", threadId);
    }

    private async Task<long?> GetPrIdAsync(int prNumber, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM local_pull_requests WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", prNumber);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long id ? id : null;
    }
}
