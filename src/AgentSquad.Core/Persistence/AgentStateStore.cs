using Microsoft.Data.Sqlite;

namespace AgentSquad.Core.Persistence;

public record AgentCheckpoint(
    string AgentId,
    string Role,
    string Status,
    string? CurrentTask,
    string? SerializedState,
    DateTime Timestamp);

public record ActivityLogEntry(
    long Id,
    string AgentId,
    DateTime Timestamp,
    string EventType,
    string Details);

public record MetricEntry(
    string AgentId,
    DateTime Timestamp,
    string MetricName,
    double Value);

/// <summary>
/// SQLite-based persistence for agent state recovery, activity logging, and metrics.
/// </summary>
public class AgentStateStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public AgentStateStore(string dbPath = "agentsquad.db")
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_state (
                agent_id     TEXT PRIMARY KEY,
                role         TEXT NOT NULL,
                status       TEXT NOT NULL,
                current_task TEXT,
                model_tier   TEXT,
                last_checkpoint DATETIME NOT NULL DEFAULT (datetime('now')),
                serialized_state TEXT
            );

            CREATE TABLE IF NOT EXISTS activity_log (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id   TEXT NOT NULL,
                timestamp  DATETIME NOT NULL DEFAULT (datetime('now')),
                event_type TEXT NOT NULL,
                details    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_activity_log_agent
                ON activity_log (agent_id, timestamp DESC);

            CREATE TABLE IF NOT EXISTS metrics (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id     TEXT NOT NULL,
                timestamp    DATETIME NOT NULL DEFAULT (datetime('now')),
                metric_name  TEXT NOT NULL,
                metric_value REAL NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_metrics_agent_name
                ON metrics (agent_id, metric_name, timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Save or update an agent's checkpoint state.</summary>
    public async Task SaveCheckpointAsync(
        string agentId,
        string role,
        string status,
        string? currentTask,
        string? serializedState,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_state (agent_id, role, status, current_task, serialized_state, last_checkpoint)
                VALUES (@id, @role, @status, @task, @state, datetime('now'))
            ON CONFLICT(agent_id) DO UPDATE SET
                role = excluded.role,
                status = excluded.status,
                current_task = excluded.current_task,
                serialized_state = excluded.serialized_state,
                last_checkpoint = excluded.last_checkpoint;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@task", (object?)currentTask ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", (object?)serializedState ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load the most recent checkpoint for an agent.</summary>
    public async Task<AgentCheckpoint?> LoadCheckpointAsync(string agentId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, role, status, current_task, serialized_state, last_checkpoint
            FROM agent_state
            WHERE agent_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new AgentCheckpoint(
            AgentId: reader.GetString(0),
            Role: reader.GetString(1),
            Status: reader.GetString(2),
            CurrentTask: reader.IsDBNull(3) ? null : reader.GetString(3),
            SerializedState: reader.IsDBNull(4) ? null : reader.GetString(4),
            Timestamp: reader.GetDateTime(5));
    }

    /// <summary>Append an entry to the activity log.</summary>
    public async Task LogActivityAsync(string agentId, string eventType, string details, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity_log (agent_id, event_type, details)
            VALUES (@id, @type, @details);
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@details", details);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get the most recent activity log entries for an agent.</summary>
    public async Task<IReadOnlyList<ActivityLogEntry>> GetRecentActivityAsync(
        string agentId,
        int count = 50,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_id, timestamp, event_type, details
            FROM activity_log
            WHERE agent_id = @id
            ORDER BY timestamp DESC
            LIMIT @count;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@count", count);

        var entries = new List<ActivityLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new ActivityLogEntry(
                Id: reader.GetInt64(0),
                AgentId: reader.GetString(1),
                Timestamp: reader.GetDateTime(2),
                EventType: reader.GetString(3),
                Details: reader.GetString(4)));
        }

        return entries;
    }

    /// <summary>Record a numeric metric data point.</summary>
    public async Task RecordMetricAsync(string agentId, string metricName, double value, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metrics (agent_id, metric_name, metric_value)
            VALUES (@id, @name, @value);
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@name", metricName);
        cmd.Parameters.AddWithValue("@value", value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get metric entries for an agent filtered by name and time range.</summary>
    public async Task<IReadOnlyList<MetricEntry>> GetMetricsAsync(
        string agentId,
        string metricName,
        DateTime since,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, timestamp, metric_name, metric_value
            FROM metrics
            WHERE agent_id = @id AND metric_name = @name AND timestamp >= @since
            ORDER BY timestamp DESC;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@name", metricName);
        cmd.Parameters.AddWithValue("@since", since);

        var entries = new List<MetricEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new MetricEntry(
                AgentId: reader.GetString(0),
                Timestamp: reader.GetDateTime(1),
                MetricName: reader.GetString(2),
                Value: reader.GetDouble(3)));
        }

        return entries;
    }

    /// <summary>Remove entries older than the specified retention period.</summary>
    public async Task PruneOldEntriesAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - retention;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM activity_log WHERE timestamp < @cutoff;
            DELETE FROM metrics WHERE timestamp < @cutoff;
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

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
