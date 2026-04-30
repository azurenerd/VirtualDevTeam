using Microsoft.Data.Sqlite;

namespace AgentSquad.Core.Persistence;

/// <summary>
/// Types of memories an agent can store.
/// </summary>
public enum MemoryType
{
    /// <summary>An action the agent performed (created PR, reviewed code, assigned task).</summary>
    Action,
    /// <summary>A decision and the reasoning behind it (chose X over Y because…).</summary>
    Decision,
    /// <summary>Something the agent learned (tests need pattern X, module Y depends on Z).</summary>
    Learning,
    /// <summary>An instruction from the human operator via chat.</summary>
    Instruction
}

/// <summary>
/// A single memory entry stored by an agent.
/// </summary>
public sealed record AgentMemoryEntry
{
    public long Id { get; init; }
    public required string AgentId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required MemoryType Type { get; init; }
    public required string Summary { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// SQLite-backed persistent memory store for agents. Each agent records meaningful
/// actions, decisions, learnings, and operator instructions so that AI calls can
/// include relevant history even though each Copilot CLI process is stateless.
/// </summary>
public sealed class AgentMemoryStore : IDisposable
{
    private SqliteConnection _connection;
    private readonly object _dbLock = new();
    private bool _disposed;

    public AgentMemoryStore(string dbPath = "agentsquad.db")
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeTable();
    }

    /// <summary>
    /// Reconfigure to use a different database file. Closes the current connection
    /// and opens a new one. Call when the target repo changes between runs.
    /// </summary>
    public void Reconfigure(string newDbPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_dbLock)
        {
            _connection.Close();
            _connection.Dispose();
            _connection = new SqliteConnection($"Data Source={newDbPath}");
            _connection.Open();
            InitializeTable();
        }
    }

    private void InitializeTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_memory (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id   TEXT NOT NULL,
                timestamp  DATETIME NOT NULL DEFAULT (datetime('now')),
                type       TEXT NOT NULL,
                summary    TEXT NOT NULL,
                details    TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_agent_memory_agent
                ON agent_memory (agent_id, timestamp DESC);

            CREATE INDEX IF NOT EXISTS idx_agent_memory_type
                ON agent_memory (agent_id, type, timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Store a new memory entry for an agent.</summary>
    public async Task StoreAsync(
        string agentId,
        MemoryType type,
        string summary,
        string? details = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(summary);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_memory (agent_id, type, summary, details)
            VALUES (@id, @type, @summary, @details);
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@type", type.ToString());
        cmd.Parameters.AddWithValue("@summary", summary);
        cmd.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get the most recent memories for an agent, optionally filtered by type.</summary>
    public async Task<IReadOnlyList<AgentMemoryEntry>> GetRecentAsync(
        string agentId,
        int count = 30,
        MemoryType? type = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var cmd = _connection.CreateCommand();
        var whereClause = type.HasValue
            ? "WHERE agent_id = @id AND type = @type"
            : "WHERE agent_id = @id";

        cmd.CommandText = $"""
            SELECT id, agent_id, timestamp, type, summary, details
            FROM agent_memory
            {whereClause}
            ORDER BY timestamp DESC
            LIMIT @count;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@count", count);
        if (type.HasValue)
            cmd.Parameters.AddWithValue("@type", type.Value.ToString());

        var entries = new List<AgentMemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new AgentMemoryEntry
            {
                Id = reader.GetInt64(0),
                AgentId = reader.GetString(1),
                Timestamp = reader.GetDateTime(2),
                Type = Enum.Parse<MemoryType>(reader.GetString(3)),
                Summary = reader.GetString(4),
                Details = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return entries;
    }

    /// <summary>
    /// Format recent memories into a prompt-friendly context block.
    /// Returns empty string if no memories exist.
    /// </summary>
    public async Task<string> GetMemoryContextAsync(
        string agentId,
        int maxEntries = 20,
        CancellationToken ct = default)
    {
        var memories = await GetRecentAsync(agentId, maxEntries, ct: ct);
        if (memories.Count == 0) return "";

        var lines = new List<string>(memories.Count + 2);
        lines.Add("[YOUR MEMORY — actions, decisions, and learnings from this session]");

        // Reverse so oldest first (chronological)
        for (int i = memories.Count - 1; i >= 0; i--)
        {
            var m = memories[i];
            var tag = m.Type switch
            {
                MemoryType.Action => "ACTION",
                MemoryType.Decision => "DECISION",
                MemoryType.Learning => "LEARNING",
                MemoryType.Instruction => "⚡INSTRUCTION",
                _ => "NOTE"
            };
            var time = m.Timestamp.ToString("HH:mm");
            var detail = m.Details is not null ? $" — {m.Details}" : "";
            lines.Add($"[{time}][{tag}] {m.Summary}{detail}");
        }

        lines.Add("[END MEMORY]");
        return string.Join('\n', lines);
    }

    /// <summary>Get all operator instructions for an agent (these take priority in prompts).</summary>
    public async Task<IReadOnlyList<AgentMemoryEntry>> GetInstructionsAsync(
        string agentId,
        CancellationToken ct = default)
    {
        return await GetRecentAsync(agentId, count: 50, type: MemoryType.Instruction, ct: ct);
    }

    /// <summary>Count total memories for an agent.</summary>
    public async Task<int> CountAsync(string agentId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_memory WHERE agent_id = @id;";
        cmd.Parameters.AddWithValue("@id", agentId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>Remove old memories beyond a retention count (keep most recent N).</summary>
    public async Task PruneAsync(string agentId, int keepCount = 100, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var cmd = _connection.CreateCommand();
        // Keep instructions forever, prune other types
        cmd.CommandText = """
            DELETE FROM agent_memory
            WHERE agent_id = @id
              AND type != 'Instruction'
              AND id NOT IN (
                  SELECT id FROM agent_memory
                  WHERE agent_id = @id AND type != 'Instruction'
                  ORDER BY timestamp DESC
                  LIMIT @keep
              );
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@keep", keepCount);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Clear all memories (for fresh runs).</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memory;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Close();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
