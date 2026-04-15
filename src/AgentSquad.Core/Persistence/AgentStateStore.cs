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

            CREATE TABLE IF NOT EXISTS gate_approvals (
                gate_id     TEXT PRIMARY KEY,
                approved_at DATETIME NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS cli_sessions (
                agent_id   TEXT NOT NULL,
                pr_number  INTEGER NOT NULL,
                session_id TEXT NOT NULL,
                created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (agent_id, pr_number)
            );

            CREATE TABLE IF NOT EXISTS gate_notifications (
                id              TEXT PRIMARY KEY,
                gate_id         TEXT NOT NULL,
                gate_name       TEXT NOT NULL,
                context         TEXT NOT NULL,
                resource_number INTEGER,
                resource_type   TEXT,
                github_url      TEXT,
                is_read         INTEGER NOT NULL DEFAULT 0,
                is_resolved     INTEGER NOT NULL DEFAULT 0,
                created_at      DATETIME NOT NULL DEFAULT (datetime('now')),
                resolved_at     DATETIME
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

    /// <summary>The UTC timestamp of the most recent runner boot.</summary>
    public DateTime LastBootUtc { get; private set; }

    /// <summary>Record the current time as the runner's boot time. Call on each runner startup.</summary>
    public void RecordBoot()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_metadata (key, value) VALUES ('last_boot_utc', datetime('now'))
            ON CONFLICT(key) DO UPDATE SET value = datetime('now');
            """;
        cmd.ExecuteNonQuery();
        LastBootUtc = DateTime.UtcNow;
    }

    /// <summary>Read the last boot time from the database.</summary>
    public DateTime GetLastBootUtc()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM run_metadata WHERE key = 'last_boot_utc'";
        var result = cmd.ExecuteScalar();
        if (result is string s)
            return DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();
        return RunStartedUtc; // fallback to first run start
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

    /// <summary>Load all agent checkpoints from the database.</summary>
    public async Task<IReadOnlyList<AgentCheckpoint>> LoadAllCheckpointsAsync(CancellationToken ct = default)
    {
        var results = new List<AgentCheckpoint>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, role, status, current_task, serialized_state, last_checkpoint
            FROM agent_state;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AgentCheckpoint(
                AgentId: reader.GetString(0),
                Role: reader.GetString(1),
                Status: reader.GetString(2),
                CurrentTask: reader.IsDBNull(3) ? null : reader.GetString(3),
                SerializedState: reader.IsDBNull(4) ? null : reader.GetString(4),
                Timestamp: reader.GetDateTime(5)));
        }
        return results;
    }

    /// <summary>Get the most recent activity entry per agent.</summary>
    public Dictionary<string, (string EventType, string Details, DateTime Timestamp)> GetLatestActivityPerAgent()
    {
        var result = new Dictionary<string, (string, string, DateTime)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.agent_id, a.event_type, a.details, a.timestamp
            FROM activity_log a
            INNER JOIN (SELECT agent_id, MAX(id) as max_id FROM activity_log GROUP BY agent_id) latest
                ON a.id = latest.max_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3));
        }
        return result;
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
            DELETE FROM gate_approvals;
            DELETE FROM gate_notifications;
            DELETE FROM run_metadata WHERE key = 'project_complete_notified';
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

    // ── Gate Approvals ──────────────────────────────────────────────

    /// <summary>Save a gate approval to SQLite.</summary>
    public void SaveGateApproval(string gateId, DateTime approvedAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gate_approvals (gate_id, approved_at)
                VALUES (@id, @at)
            ON CONFLICT(gate_id) DO UPDATE SET approved_at = excluded.approved_at;
            """;
        cmd.Parameters.AddWithValue("@id", gateId);
        cmd.Parameters.AddWithValue("@at", approvedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Load all persisted gate approvals.</summary>
    public Dictionary<string, DateTime> LoadGateApprovals()
    {
        var result = new Dictionary<string, DateTime>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT gate_id, approved_at FROM gate_approvals;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetDateTime(1);
        }
        return result;
    }

    /// <summary>Clear all gate approvals (for reset).</summary>
    public void ClearGateApprovals()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM gate_approvals;";
        cmd.ExecuteNonQuery();
    }

    // ── Gate Notifications ───────────────────────────────────────────

    /// <summary>Save a gate notification to SQLite.</summary>
    public void SaveGateNotification(string id, string gateId, string gateName, string context,
        int? resourceNumber, string? resourceType, string? githubUrl)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO gate_notifications (id, gate_id, gate_name, context, resource_number, resource_type, github_url)
            VALUES (@id, @gateId, @gateName, @context, @resNum, @resType, @url);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@gateId", gateId);
        cmd.Parameters.AddWithValue("@gateName", gateName);
        cmd.Parameters.AddWithValue("@context", context);
        cmd.Parameters.AddWithValue("@resNum", (object?)resourceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@resType", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)githubUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Update notification read/resolved status.</summary>
    public void UpdateGateNotification(string id, bool isRead, bool isResolved, DateTime? resolvedAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE gate_notifications
            SET is_read = @read, is_resolved = @resolved, resolved_at = @resolvedAt
            WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@read", isRead ? 1 : 0);
        cmd.Parameters.AddWithValue("@resolved", isResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("@resolvedAt", (object?)resolvedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Load all gate notifications from SQLite.</summary>
    public List<(string Id, string GateId, string GateName, string Context, int? ResourceNumber,
        string? ResourceType, string? GitHubUrl, bool IsRead, bool IsResolved, DateTime CreatedAt, DateTime? ResolvedAt)>
        LoadGateNotifications()
    {
        var result = new List<(string, string, string, string, int?, string?, string?, bool, bool, DateTime, DateTime?)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, gate_id, gate_name, context, resource_number, resource_type, github_url,
                   is_read, is_resolved, created_at, resolved_at
            FROM gate_notifications
            ORDER BY created_at DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7) != 0,
                reader.GetInt32(8) != 0,
                reader.GetDateTime(9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10)
            ));
        }
        return result;
    }

    /// <summary>Remove resolved notifications older than cutoff.</summary>
    public void PurgeResolvedNotifications(DateTime cutoff)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM gate_notifications WHERE is_resolved = 1 AND resolved_at < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clear all gate notifications (for reset).</summary>
    public void ClearGateNotifications()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM gate_notifications;";
        cmd.ExecuteNonQuery();
    }

    // ── Run Metadata helpers ─────────────────────────────────────────

    /// <summary>Get a value from run_metadata by key.</summary>
    public string? GetRunMetadata(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM run_metadata WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Set a value in run_metadata (upsert).</summary>
    public void SetRunMetadata(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_metadata (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    // ── CLI Session Persistence ────────────────────────────────────────

    /// <summary>Persist a CLI session ID mapping for an agent + PR number.</summary>
    public async Task SaveCliSessionAsync(string agentId, int prNumber, string sessionId, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cli_sessions (agent_id, pr_number, session_id)
            VALUES (@agentId, @prNumber, @sessionId)
            ON CONFLICT(agent_id, pr_number) DO UPDATE SET session_id = excluded.session_id;
            """;
        cmd.Parameters.AddWithValue("@agentId", agentId);
        cmd.Parameters.AddWithValue("@prNumber", prNumber);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load all CLI session ID mappings for an agent.</summary>
    public async Task<Dictionary<int, string>> LoadCliSessionsAsync(string agentId, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT pr_number, session_id FROM cli_sessions WHERE agent_id = @agentId;";
        cmd.Parameters.AddWithValue("@agentId", agentId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetInt32(0)] = reader.GetString(1);
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
