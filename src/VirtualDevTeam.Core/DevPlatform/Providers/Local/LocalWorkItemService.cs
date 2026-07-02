using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IWorkItemService"/> backed by SQLite. Work items (issues) are stored
/// locally with JSON-serialized labels. Full lifecycle: create → update → close.
/// </summary>
public sealed class LocalWorkItemService : IWorkItemService
{
    private readonly LocalPlatformContext _ctx;
    private readonly ILogger<LocalWorkItemService> _logger;

    public LocalWorkItemService(LocalPlatformContext ctx, ILogger<LocalWorkItemService> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task<PlatformWorkItem> CreateAsync(
        string title, string body, IReadOnlyList<string> labels,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var labelsJson = JsonSerializer.Serialize(labels ?? Array.Empty<string>());

        int number;
        using (var conn = _ctx.CreateConnection())
        {
            // Atomic number generation: subquery inside INSERT eliminates TOCTOU race
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO local_work_items (run_id, number, title, body, state, labels_json, created_at, updated_at)
                VALUES (@runId, (SELECT COALESCE(MAX(number), 0) + 1 FROM local_work_items WHERE run_id = @runId), @title, @body, 'open', @labels, @now, @now)
                RETURNING number
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@body", body ?? "");
            cmd.Parameters.AddWithValue("@labels", labelsJson);
            cmd.Parameters.AddWithValue("@now", now);
            number = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        _logger.LogInformation("Local work item #{Number} created: {Title}", number, title);
        return MapWorkItem(number, title, body ?? "", "open", labelsJson, now, now);
    }

    public async Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT number, title, body, state, labels_json, created_at, updated_at
            FROM local_work_items WHERE run_id = @runId AND number = @number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapWorkItemFromReader(reader);
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default)
    {
        return await ListByStateAsync("open", ct);
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT number, title, body, state, labels_json, created_at, updated_at
            FROM local_work_items WHERE run_id = @runId ORDER BY number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        var results = new List<PlatformWorkItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapWorkItemFromReader(reader));
        return results;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListAllForProjectAsync(CancellationToken ct = default)
    {
        return await ListAllAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(string agentName, CancellationToken ct = default)
    {
        var all = await ListAllAsync(ct);
        return all.Where(w => w.Title.Contains(agentName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(
        string label, string? state = null, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        // "all" means no state filter (return both open and closed)
        var effectiveState = string.Equals(state, "all", StringComparison.OrdinalIgnoreCase) ? null : state;
        var stateFilter = effectiveState is not null ? " AND state = @state" : "";
        cmd.CommandText = $"""
            SELECT number, title, body, state, labels_json, created_at, updated_at
            FROM local_work_items WHERE run_id = @runId AND labels_json LIKE @labelPattern{stateFilter}
            ORDER BY number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@labelPattern", $"%\"{label}\"%");
        if (effectiveState is not null) cmd.Parameters.AddWithValue("@state", effectiveState);

        var results = new List<PlatformWorkItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapWorkItemFromReader(reader));
        return results;
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, string? state = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sets = new List<string> { "updated_at = @now" };

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();

        if (title is not null) { sets.Add("title = @title"); cmd.Parameters.AddWithValue("@title", title); }
        if (body is not null) { sets.Add("body = @body"); cmd.Parameters.AddWithValue("@body", body); }
        if (state is not null)
        {
            sets.Add("state = @state");
            cmd.Parameters.AddWithValue("@state", state);
            if (state == "closed") sets.Add("closed_at = @now");
        }
        if (labels is not null)
        {
            sets.Add("labels_json = @labels");
            cmd.Parameters.AddWithValue("@labels", JsonSerializer.Serialize(labels));
        }

        cmd.CommandText = $"UPDATE local_work_items SET {string.Join(", ", sets)} WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default)
    {
        await UpdateAsync(id, title: newTitle, ct: ct);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        await UpdateAsync(id, state: "closed", ct: ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_work_items WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", id);
        var affectedRows = await cmd.ExecuteNonQueryAsync(ct);
        return affectedRows > 0;
    }

    public async Task AddCommentAsync(int id, string comment, CancellationToken ct = default)
    {
        var wiId = await GetWorkItemIdAsync(id, ct);
        if (wiId is null) return;

        var author = AgentCallContext.CurrentAgentId ?? "system";
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_work_item_comments (work_item_id, author, body, created_at)
            VALUES (@wiId, @author, @body, @now)
            """;
        cmd.Parameters.AddWithValue("@wiId", wiId.Value);
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@body", comment);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default)
    {
        var wiId = await GetWorkItemIdAsync(id, ct);
        if (wiId is null) return new List<PlatformComment>();

        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, author, body, created_at FROM local_work_item_comments WHERE work_item_id = @wiId ORDER BY created_at";
        cmd.Parameters.AddWithValue("@wiId", wiId.Value);
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

    public async Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return false;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return new List<PlatformWorkItem>();
    }

    public async Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return false;
    }

    // ── Private helpers ──

    private async Task<IReadOnlyList<PlatformWorkItem>> ListByStateAsync(string state, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT number, title, body, state, labels_json, created_at, updated_at
            FROM local_work_items WHERE run_id = @runId AND state = @state ORDER BY number
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@state", state);
        var results = new List<PlatformWorkItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapWorkItemFromReader(reader));
        return results;
    }

    private async Task<long?> GetWorkItemIdAsync(int number, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM local_work_items WHERE run_id = @runId AND number = @number";
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@number", number);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long id ? id : null;
    }

    private static PlatformWorkItem MapWorkItemFromReader(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return MapWorkItem(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
    }

    private static PlatformWorkItem MapWorkItem(int number, string title, string body,
        string state, string labelsJson, string createdAt, string updatedAt)
    {
        var labels = JsonSerializer.Deserialize<List<string>>(labelsJson) ?? new();
        return new PlatformWorkItem
        {
            PlatformId = number,
            Number = number,
            Title = title,
            Body = body,
            State = state,
            Labels = labels,
            CreatedAt = DateTimeOffset.Parse(createdAt).DateTime,
            UpdatedAt = DateTimeOffset.Parse(updatedAt).DateTime,
        };
    }
}
