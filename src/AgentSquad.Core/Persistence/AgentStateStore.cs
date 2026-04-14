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

public record WorkflowCheckpoint(
    string Phase,
    string SignalsJson,
    DateTime Timestamp);

public record AgentTaskCheckpoint(
    string AgentRole,
    string? CurrentTaskId,
    int StepIndex,
    int? PrNumber,
    int? IssueNumber,
    string? StateJson,
    DateTime Timestamp);

public record ProcessedItem(
    string AgentRole,
    string ItemType,
    string ItemId);

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

            CREATE TABLE IF NOT EXISTS workflow_state (
                id             INTEGER PRIMARY KEY CHECK (id = 1),
                phase          TEXT NOT NULL,
                signals_json   TEXT NOT NULL DEFAULT '[]',
                updated_at     DATETIME NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS agent_task_checkpoint (
                agent_role     TEXT PRIMARY KEY,
                current_task_id TEXT,
                step_index     INTEGER NOT NULL DEFAULT 0,
                pr_number      INTEGER,
                issue_number   INTEGER,
                rework_attempts_json TEXT DEFAULT '{}',
                state_json     TEXT,
                updated_at     DATETIME NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS processed_items (
                agent_role     TEXT NOT NULL,
                item_type      TEXT NOT NULL,
                item_id        TEXT NOT NULL,
                created_at     DATETIME NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (agent_role, item_type, item_id)
            );

            CREATE TABLE IF NOT EXISTS run_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ai_usage (
                agent_id          TEXT PRIMARY KEY,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                total_calls       INTEGER NOT NULL DEFAULT 0,
                estimated_cost    REAL NOT NULL DEFAULT 0,
                last_model        TEXT,
                updated_at        DATETIME NOT NULL DEFAULT (datetime('now'))
            );

            INSERT OR IGNORE INTO run_metadata (key, value)
            VALUES ('run_started_utc', datetime('now'));
            """;
        cmd.ExecuteNonQuery();

        // Read run start time (set once on first DB creation, survives restarts)
        using var readCmd = _connection.CreateCommand();
        readCmd.CommandText = "SELECT value FROM run_metadata WHERE key = 'run_started_utc'";
        var result = readCmd.ExecuteScalar();
        RunStartedUtc = result is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime() : DateTime.UtcNow;
    }

    /// <summary>
    /// The UTC timestamp when this run's database was first created.
    /// Use this to filter GitHub API results to only the current run's data.
    /// </summary>
    public DateTime RunStartedUtc { get; private set; }

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

    /// <summary>Get aggregate metric totals across all agents, grouped by metric name.</summary>
    public async Task<Dictionary<string, double>> GetAggregateMetricsAsync(
        DateTime since, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT metric_name, SUM(metric_value)
            FROM metrics
            WHERE timestamp >= @since
            GROUP BY metric_name;
            """;
        cmd.Parameters.AddWithValue("@since", since);

        var result = new Dictionary<string, double>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetDouble(1);

        return result;
    }

    /// <summary>Get metric totals per agent for a specific metric name.</summary>
    public async Task<Dictionary<string, double>> GetMetricsByAgentAsync(
        string metricName, DateTime since, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, SUM(metric_value)
            FROM metrics
            WHERE metric_name = @name AND timestamp >= @since
            GROUP BY agent_id;
            """;
        cmd.Parameters.AddWithValue("@name", metricName);
        cmd.Parameters.AddWithValue("@since", since);

        var result = new Dictionary<string, double>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetDouble(1);

        return result;
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

    // ── Workflow State ───────────────────────────────────────────────

    /// <summary>Save the current workflow phase and signals to SQLite.</summary>
    public async Task SaveWorkflowStateAsync(string phase, IEnumerable<string> signals, CancellationToken ct = default)
    {
        var signalsJson = System.Text.Json.JsonSerializer.Serialize(signals);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workflow_state (id, phase, signals_json, updated_at)
                VALUES (1, @phase, @signals, datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                phase = excluded.phase,
                signals_json = excluded.signals_json,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@phase", phase);
        cmd.Parameters.AddWithValue("@signals", signalsJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load the persisted workflow state, if any.</summary>
    public async Task<WorkflowCheckpoint?> LoadWorkflowStateAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT phase, signals_json, updated_at FROM workflow_state WHERE id = 1;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new WorkflowCheckpoint(
            Phase: reader.GetString(0),
            SignalsJson: reader.GetString(1),
            Timestamp: reader.GetDateTime(2));
    }

    // ── Agent Task Checkpoints ───────────────────────────────────────

    /// <summary>Save an agent's current task progress for crash recovery.</summary>
    public async Task SaveAgentTaskCheckpointAsync(
        string agentRole,
        string? currentTaskId,
        int stepIndex,
        int? prNumber,
        int? issueNumber,
        string? reworkAttemptsJson,
        string? stateJson,
        CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_task_checkpoint (agent_role, current_task_id, step_index, pr_number, issue_number, rework_attempts_json, state_json, updated_at)
                VALUES (@role, @task, @step, @pr, @issue, @rework, @state, datetime('now'))
            ON CONFLICT(agent_role) DO UPDATE SET
                current_task_id = excluded.current_task_id,
                step_index = excluded.step_index,
                pr_number = excluded.pr_number,
                issue_number = excluded.issue_number,
                rework_attempts_json = excluded.rework_attempts_json,
                state_json = excluded.state_json,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@role", agentRole);
        cmd.Parameters.AddWithValue("@task", (object?)currentTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@step", stepIndex);
        cmd.Parameters.AddWithValue("@pr", (object?)prNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@issue", (object?)issueNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rework", (object?)reworkAttemptsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", (object?)stateJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load an agent's task checkpoint for crash recovery.</summary>
    public async Task<AgentTaskCheckpoint?> LoadAgentTaskCheckpointAsync(string agentRole, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT agent_role, current_task_id, step_index, pr_number, issue_number, state_json, updated_at
            FROM agent_task_checkpoint
            WHERE agent_role = @role;
            """;
        cmd.Parameters.AddWithValue("@role", agentRole);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new AgentTaskCheckpoint(
            AgentRole: reader.GetString(0),
            CurrentTaskId: reader.IsDBNull(1) ? null : reader.GetString(1),
            StepIndex: reader.GetInt32(2),
            PrNumber: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            IssueNumber: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            StateJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            Timestamp: reader.GetDateTime(6));
    }

    /// <summary>Load rework attempt counts for an agent role.</summary>
    public async Task<Dictionary<int, int>> LoadReworkAttemptsAsync(string agentRole, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT rework_attempts_json FROM agent_task_checkpoint WHERE agent_role = @role;";
        cmd.Parameters.AddWithValue("@role", agentRole);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            return new Dictionary<int, int>();

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>((string)result)
               ?? new Dictionary<int, int>();
    }

    // ── Processed Items (dedup) ──────────────────────────────────────

    /// <summary>Record that an item has been processed by an agent role.</summary>
    public async Task AddProcessedItemAsync(string agentRole, string itemType, string itemId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO processed_items (agent_role, item_type, item_id)
            VALUES (@role, @type, @item);
            """;
        cmd.Parameters.AddWithValue("@role", agentRole);
        cmd.Parameters.AddWithValue("@type", itemType);
        cmd.Parameters.AddWithValue("@item", itemId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load all processed item IDs for an agent role and item type.</summary>
    public async Task<HashSet<string>> LoadProcessedItemsAsync(string agentRole, string itemType, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT item_id FROM processed_items
            WHERE agent_role = @role AND item_type = @type;
            """;
        cmd.Parameters.AddWithValue("@role", agentRole);
        cmd.Parameters.AddWithValue("@type", itemType);

        var items = new HashSet<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(reader.GetString(0));
        }
        return items;
    }

    /// <summary>Clear all checkpoints (for fresh runs).</summary>
    public async Task ClearAllCheckpointsAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM workflow_state;
            DELETE FROM agent_task_checkpoint;
            DELETE FROM processed_items;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Save AI usage stats for an agent (upsert).</summary>
    public void SaveAiUsage(string agentId, int promptTokens, int completionTokens, int totalCalls, decimal estimatedCost, string? lastModel)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_usage (agent_id, prompt_tokens, completion_tokens, total_calls, estimated_cost, last_model, updated_at)
                VALUES (@id, @pt, @ct, @tc, @ec, @lm, datetime('now'))
            ON CONFLICT(agent_id) DO UPDATE SET
                prompt_tokens = excluded.prompt_tokens,
                completion_tokens = excluded.completion_tokens,
                total_calls = excluded.total_calls,
                estimated_cost = excluded.estimated_cost,
                last_model = excluded.last_model,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@id", agentId);
        cmd.Parameters.AddWithValue("@pt", promptTokens);
        cmd.Parameters.AddWithValue("@ct", completionTokens);
        cmd.Parameters.AddWithValue("@tc", totalCalls);
        cmd.Parameters.AddWithValue("@ec", (double)estimatedCost);
        cmd.Parameters.AddWithValue("@lm", (object?)lastModel ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Load all persisted AI usage stats (for restoring after restart).</summary>
    public Dictionary<string, (int PromptTokens, int CompletionTokens, int TotalCalls, decimal EstimatedCost, string? LastModel)> LoadAllAiUsage()
    {
        var result = new Dictionary<string, (int, int, int, decimal, string?)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT agent_id, prompt_tokens, completion_tokens, total_calls, estimated_cost, last_model FROM ai_usage";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                (decimal)reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)
            );
        }
        return result;
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
