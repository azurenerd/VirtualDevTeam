using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IWorkItemSearchService"/> stub for the local platform.
/// Searches local SQLite work items by title/body text match.
/// </summary>
public sealed class LocalWorkItemSearchService : IWorkItemSearchService
{
    private readonly LocalPlatformContext _ctx;

    public LocalWorkItemSearchService(LocalPlatformContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<IReadOnlyList<WorkItemSearchResult>> SearchAsync(
        string query, int maxResults = 10, CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT number, title, state, body
            FROM local_work_items
            WHERE run_id = @runId AND (title LIKE @pattern OR body LIKE @pattern)
            ORDER BY number DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", maxResults);

        var results = new List<WorkItemSearchResult>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var number = reader.GetInt32(0);
            results.Add(new WorkItemSearchResult(
                Id: number,
                Title: reader.GetString(1),
                State: reader.GetString(2),
                WorkItemType: "Issue",
                Url: $"/repository/local/issue/{number}",
                Body: reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return results;
    }
}
