using AgentSquad.Core.Configuration;
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
    DateTime Timestamp,
    string RunId = "_global");

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

/// <summary>Flat DTO for persisting a completed strategy task to SQLite.</summary>
public class StrategyTaskRecord
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? WinnerStrategyId { get; init; }
    public string? TieBreakReason { get; init; }
    public double? EvaluationElapsedSec { get; init; }
    public List<StrategyCandidateRecord> Candidates { get; set; } = new();
}

/// <summary>Flat DTO for persisting a strategy candidate to SQLite.</summary>
public class StrategyCandidateRecord
{
    public required string StrategyId { get; init; }
    public required string State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? ElapsedSec { get; init; }
    public bool? Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public long? TokensUsed { get; init; }
    public int? AcScore { get; init; }
    public int? DesignScore { get; init; }
    public int? ReadabilityScore { get; init; }
    /// <summary>Visual quality score (0-10). Null when not applicable.</summary>
    public int? VisualsScore { get; init; }
    public bool? Survived { get; init; }
    public string? JudgeSkippedReason { get; init; }
    public string? ExecutionSummaryJson { get; init; }
    public string? ScreenshotBase64 { get; init; }
    public int? InitialAcScore { get; set; }
    public int? InitialDesignScore { get; set; }
    public int? InitialReadabilityScore { get; set; }
    public int? InitialVisualsScore { get; set; }
    public string? JudgeFeedback { get; set; }
    public string? InitialScreenshotBase64 { get; set; }
    public double? RevisionElapsedSec { get; set; }
    public string? RevisionSkippedReason { get; set; }
    public List<StrategyActivityLogEntry> ActivityLog { get; set; } = new();
}

/// <summary>Flat DTO for persisting a strategy activity log entry to SQLite.</summary>
public record StrategyActivityLogEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Message,
    string? MetadataJson = null);

/// <summary>
/// SQLite-based persistence for agent state recovery, activity logging, and metrics.
/// </summary>
public class AgentStateStore : IDisposable
{
    private SqliteConnection _connection;
    private readonly object _dbLock = new();
    private bool _disposed;

    public AgentStateStore(string dbPath = "agentsquad.db")
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
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
            InitializeDatabase();
        }
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

            CREATE TABLE IF NOT EXISTS active_runs (
                run_id       TEXT PRIMARY KEY,
                mode         TEXT NOT NULL,
                feature_id   TEXT,
                status       TEXT NOT NULL DEFAULT 'NotStarted',
                repo         TEXT NOT NULL,
                base_branch  TEXT NOT NULL DEFAULT 'main',
                target_branch TEXT,
                created_at   DATETIME NOT NULL DEFAULT (datetime('now')),
                started_at   DATETIME,
                completed_at DATETIME,
                artifact_base_path TEXT
            );

            CREATE TABLE IF NOT EXISTS features (
                id                TEXT PRIMARY KEY,
                title             TEXT NOT NULL,
                description       TEXT NOT NULL,
                target_repo       TEXT,
                base_branch       TEXT NOT NULL DEFAULT 'main',
                tech_stack_override TEXT,
                additional_context TEXT,
                acceptance_criteria TEXT,
                status            TEXT NOT NULL DEFAULT 'Draft',
                created_at        DATETIME NOT NULL DEFAULT (datetime('now')),
                started_at        DATETIME,
                completed_at      DATETIME,
                run_id            TEXT
            );

            CREATE TABLE IF NOT EXISTS strategy_tasks (
                run_id               TEXT NOT NULL,
                task_id              TEXT NOT NULL,
                started_at           TEXT NOT NULL,
                completed_at         TEXT,
                winner_strategy_id   TEXT,
                tie_break_reason     TEXT,
                evaluation_elapsed_sec REAL,
                PRIMARY KEY (run_id, task_id)
            );

            CREATE TABLE IF NOT EXISTS strategy_candidates (
                run_id             TEXT NOT NULL,
                task_id            TEXT NOT NULL,
                strategy_id        TEXT NOT NULL,
                state              TEXT NOT NULL,
                started_at         TEXT,
                completed_at       TEXT,
                elapsed_sec        REAL,
                succeeded          INTEGER,
                failure_reason     TEXT,
                tokens_used        INTEGER,
                ac_score           INTEGER,
                design_score       INTEGER,
                readability_score  INTEGER,
                survived           INTEGER,
                judge_skipped_reason TEXT,
                execution_summary_json TEXT,
                screenshot_base64  TEXT,
                initial_ac_score   INTEGER,
                initial_design_score INTEGER,
                initial_readability_score INTEGER,
                initial_visuals_score INTEGER,
                judge_feedback     TEXT,
                initial_screenshot_base64 TEXT,
                revision_elapsed_sec REAL,
                revision_skipped_reason TEXT,
                PRIMARY KEY (run_id, task_id, strategy_id)
            );

            CREATE TABLE IF NOT EXISTS strategy_activity_log (
                run_id      TEXT NOT NULL,
                task_id     TEXT NOT NULL,
                strategy_id TEXT NOT NULL,
                timestamp   TEXT NOT NULL,
                category    TEXT NOT NULL,
                message     TEXT NOT NULL,
                metadata_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_activity_log_candidate
                ON strategy_activity_log (run_id, task_id, strategy_id);
            """;
        cmd.ExecuteNonQuery();

        // Schema migration: add run_id columns to existing tables (safe if column already exists)
        MigrateAddRunIdColumns();

        // Read run start time (set once on first DB creation, survives restarts)
        using var readCmd = _connection.CreateCommand();
        readCmd.CommandText = "SELECT value FROM run_metadata WHERE key = 'run_started_utc'";
        var result = readCmd.ExecuteScalar();
        RunStartedUtc = result is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime() : DateTime.UtcNow;
    }

    /// <summary>
    /// Add run_id columns to tables that predate run-scoped state.
    /// Safe to call on already-migrated DBs (ALTER TABLE fails silently).
    /// </summary>
    private void MigrateAddRunIdColumns()
    {
        string[] migrations =
        [
            "ALTER TABLE workflow_state ADD COLUMN run_id TEXT NOT NULL DEFAULT '_global'",
            "ALTER TABLE gate_approvals ADD COLUMN run_id TEXT NOT NULL DEFAULT '_global'",
            "ALTER TABLE processed_items ADD COLUMN run_id TEXT NOT NULL DEFAULT '_global'",
            "ALTER TABLE strategy_candidates ADD COLUMN visuals_score INTEGER",
            "ALTER TABLE strategy_candidates ADD COLUMN initial_ac_score INTEGER",
            "ALTER TABLE strategy_candidates ADD COLUMN initial_design_score INTEGER",
            "ALTER TABLE strategy_candidates ADD COLUMN initial_readability_score INTEGER",
            "ALTER TABLE strategy_candidates ADD COLUMN initial_visuals_score INTEGER",
            "ALTER TABLE strategy_candidates ADD COLUMN judge_feedback TEXT",
            "ALTER TABLE strategy_candidates ADD COLUMN initial_screenshot_base64 TEXT",
            "ALTER TABLE strategy_candidates ADD COLUMN revision_elapsed_sec REAL",
            "ALTER TABLE strategy_candidates ADD COLUMN revision_skipped_reason TEXT",
            "ALTER TABLE active_runs ADD COLUMN artifact_base_path TEXT",
            "ALTER TABLE active_runs ADD COLUMN run_scope TEXT",
        ];

        foreach (var sql in migrations)
        {
            try
            {
                using var migCmd = _connection.CreateCommand();
                migCmd.CommandText = sql;
                migCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists — expected on already-migrated DBs
            }
        }
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

    /// <summary>Save the current workflow phase and signals to SQLite, scoped by run ID.</summary>
    public async Task SaveWorkflowStateAsync(string phase, IEnumerable<string> signals, string runId = "_global", CancellationToken ct = default)
    {
        var signalsJson = System.Text.Json.JsonSerializer.Serialize(signals);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workflow_state (id, phase, signals_json, run_id, updated_at)
                VALUES (1, @phase, @signals, @runId, datetime('now'))
            ON CONFLICT(id) DO UPDATE SET
                phase = excluded.phase,
                signals_json = excluded.signals_json,
                run_id = excluded.run_id,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@phase", phase);
        cmd.Parameters.AddWithValue("@signals", signalsJson);
        cmd.Parameters.AddWithValue("@runId", runId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load the persisted workflow state, if any.</summary>
    public async Task<WorkflowCheckpoint?> LoadWorkflowStateAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT phase, signals_json, updated_at, COALESCE(run_id, '_global') FROM workflow_state WHERE id = 1;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new WorkflowCheckpoint(
            Phase: reader.GetString(0),
            SignalsJson: reader.GetString(1),
            Timestamp: reader.GetDateTime(2),
            RunId: reader.GetString(3));
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
    public async Task<Dictionary<(int PrNumber, string Reviewer), int>> LoadReworkAttemptsAsync(string agentRole, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT rework_attempts_json FROM agent_task_checkpoint WHERE agent_role = @role;";
        cmd.Parameters.AddWithValue("@role", agentRole);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            return new Dictionary<(int, string), int>();

        // New format: "prNumber|reviewer" → count
        var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>((string)result)
               ?? new Dictionary<string, int>();

        var parsed = new Dictionary<(int PrNumber, string Reviewer), int>();
        foreach (var kvp in raw)
        {
            var parts = kvp.Key.Split('|', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var prNum))
                parsed[(prNum, parts[1])] = kvp.Value;
            else if (int.TryParse(kvp.Key, out var legacyPr))
                // Legacy format: just PR number → count (treat as unknown reviewer)
                parsed[(legacyPr, "Unknown")] = kvp.Value;
        }
        return parsed;
    }

    // ── Processed Items (dedup) ──────────────────────────────────────

    /// <summary>Record that an item has been processed by an agent role, scoped by run ID.</summary>
    public async Task AddProcessedItemAsync(string agentRole, string itemType, string itemId, string runId = "_global", CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO processed_items (agent_role, item_type, item_id, run_id)
            VALUES (@role, @type, @item, @runId);
            """;
        cmd.Parameters.AddWithValue("@role", agentRole);
        cmd.Parameters.AddWithValue("@type", itemType);
        cmd.Parameters.AddWithValue("@item", itemId);
        cmd.Parameters.AddWithValue("@runId", runId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Load all processed item IDs for an agent role and item type, optionally scoped by run ID.</summary>
    public async Task<HashSet<string>> LoadProcessedItemsAsync(string agentRole, string itemType, string? runId = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        if (runId is not null)
        {
            cmd.CommandText = """
                SELECT item_id FROM processed_items
                WHERE agent_role = @role AND item_type = @type AND run_id = @runId;
                """;
            cmd.Parameters.AddWithValue("@runId", runId);
        }
        else
        {
            cmd.CommandText = """
                SELECT item_id FROM processed_items
                WHERE agent_role = @role AND item_type = @type;
                """;
        }
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

    /// <summary>Clear all checkpoints (nuclear reset for fresh runs).</summary>
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

    /// <summary>Clear state associated with a specific run only (leaves other runs' data intact).</summary>
    public async Task ClearRunStateAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM workflow_state WHERE run_id = @runId;
            DELETE FROM processed_items WHERE run_id = @runId;
            DELETE FROM gate_approvals WHERE run_id = @runId;
            """;
        cmd.Parameters.AddWithValue("@runId", runId);
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

    /// <summary>Save a gate approval to SQLite, scoped by run ID.</summary>
    public void SaveGateApproval(string gateId, DateTime approvedAt, string runId = "_global")
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gate_approvals (gate_id, approved_at, run_id)
                VALUES (@id, @at, @runId)
            ON CONFLICT(gate_id) DO UPDATE SET
                approved_at = excluded.approved_at,
                run_id = excluded.run_id;
            """;
        cmd.Parameters.AddWithValue("@id", gateId);
        cmd.Parameters.AddWithValue("@at", approvedAt);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Load all persisted gate approvals, optionally filtered by run ID.</summary>
    public Dictionary<string, DateTime> LoadGateApprovals(string? runId = null)
    {
        var result = new Dictionary<string, DateTime>();
        using var cmd = _connection.CreateCommand();
        if (runId is not null)
        {
            cmd.CommandText = "SELECT gate_id, approved_at FROM gate_approvals WHERE run_id = @runId;";
            cmd.Parameters.AddWithValue("@runId", runId);
        }
        else
        {
            cmd.CommandText = "SELECT gate_id, approved_at FROM gate_approvals;";
        }
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

    // ── Active Runs ────────────────────────────────────────────────────

    /// <summary>Save or update an active run.</summary>
    public async Task SaveActiveRunAsync(ActiveRun run, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO active_runs (run_id, mode, feature_id, status, repo, base_branch, target_branch, created_at, started_at, completed_at, artifact_base_path, run_scope)
                VALUES (@runId, @mode, @featureId, @status, @repo, @baseBranch, @targetBranch, @createdAt, @startedAt, @completedAt, @artifactBasePath, @runScope)
            ON CONFLICT(run_id) DO UPDATE SET
                status = excluded.status,
                started_at = excluded.started_at,
                completed_at = excluded.completed_at,
                artifact_base_path = COALESCE(excluded.artifact_base_path, active_runs.artifact_base_path),
                run_scope = COALESCE(excluded.run_scope, active_runs.run_scope);
            """;
        cmd.Parameters.AddWithValue("@runId", run.RunId);
        cmd.Parameters.AddWithValue("@mode", run.Mode.ToString());
        cmd.Parameters.AddWithValue("@featureId", (object?)run.FeatureId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", run.Status.ToString());
        cmd.Parameters.AddWithValue("@repo", run.Repo);
        cmd.Parameters.AddWithValue("@baseBranch", run.BaseBranch);
        cmd.Parameters.AddWithValue("@targetBranch", (object?)run.TargetBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", run.CreatedAt);
        cmd.Parameters.AddWithValue("@startedAt", (object?)run.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt", (object?)run.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artifactBasePath", (object?)run.ArtifactBasePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@runScope", (object?)run.RunScope ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get the currently active (Running or Paused) run, if any.</summary>
    public async Task<ActiveRun?> GetActiveRunAsync(CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, mode, feature_id, status, repo, base_branch, target_branch, created_at, started_at, completed_at, artifact_base_path, run_scope
            FROM active_runs
            WHERE status IN ('Running', 'Paused', 'NotStarted')
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        return await ReadSingleRunAsync(cmd, ct);
    }

    /// <summary>Get a specific run by ID.</summary>
    public async Task<ActiveRun?> GetRunByIdAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, mode, feature_id, status, repo, base_branch, target_branch, created_at, started_at, completed_at, artifact_base_path, run_scope
            FROM active_runs WHERE run_id = @runId;
            """;
        cmd.Parameters.AddWithValue("@runId", runId);
        return await ReadSingleRunAsync(cmd, ct);
    }

    /// <summary>Get run history (most recent first).</summary>
    public async Task<IReadOnlyList<ActiveRun>> GetRunHistoryAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, mode, feature_id, status, repo, base_branch, target_branch, created_at, started_at, completed_at, artifact_base_path, run_scope
            FROM active_runs
            ORDER BY created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var runs = new List<ActiveRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            runs.Add(MapActiveRun(reader));
        return runs;
    }

    /// <summary>Update only the status (and optional timestamps) of a run.</summary>
    public async Task UpdateRunStatusAsync(string runId, RunStatus status, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = status switch
        {
            RunStatus.Running => """
                UPDATE active_runs SET status = @status, started_at = COALESCE(started_at, datetime('now'))
                WHERE run_id = @runId;
                """,
            RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled => """
                UPDATE active_runs SET status = @status, completed_at = datetime('now')
                WHERE run_id = @runId;
                """,
            _ => "UPDATE active_runs SET status = @status WHERE run_id = @runId;"
        };
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<ActiveRun?> ReadSingleRunAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return MapActiveRun(reader);
    }

    private static ActiveRun MapActiveRun(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        Mode = Enum.Parse<WorkMode>(reader.GetString(1)),
        FeatureId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Status = Enum.Parse<RunStatus>(reader.GetString(3)),
        Repo = reader.GetString(4),
        BaseBranch = reader.GetString(5),
        TargetBranch = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = reader.GetDateTime(7),
        StartedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        CompletedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
        ArtifactBasePath = reader.IsDBNull(10) ? null : reader.GetString(10),
        RunScope = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null
    };

    // ── Features ─────────────────────────────────────────────────────

    /// <summary>Save or update a feature definition.</summary>
    public async Task SaveFeatureAsync(FeatureDefinition feature, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO features (id, title, description, target_repo, base_branch, tech_stack_override,
                                  additional_context, acceptance_criteria, status, created_at, started_at, completed_at, run_id)
                VALUES (@id, @title, @desc, @repo, @branch, @tech, @ctx, @criteria, @status, @created, @started, @completed, @runId)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                description = excluded.description,
                target_repo = excluded.target_repo,
                base_branch = excluded.base_branch,
                tech_stack_override = excluded.tech_stack_override,
                additional_context = excluded.additional_context,
                acceptance_criteria = excluded.acceptance_criteria,
                status = excluded.status,
                started_at = excluded.started_at,
                completed_at = excluded.completed_at,
                run_id = excluded.run_id;
            """;
        cmd.Parameters.AddWithValue("@id", feature.Id);
        cmd.Parameters.AddWithValue("@title", feature.Title);
        cmd.Parameters.AddWithValue("@desc", feature.Description);
        cmd.Parameters.AddWithValue("@repo", (object?)feature.TargetRepo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@branch", feature.BaseBranch);
        cmd.Parameters.AddWithValue("@tech", (object?)feature.TechStackOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ctx", (object?)feature.AdditionalContext ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@criteria", (object?)feature.AcceptanceCriteria ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", feature.Status.ToString());
        cmd.Parameters.AddWithValue("@created", feature.CreatedAt);
        cmd.Parameters.AddWithValue("@started", (object?)feature.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@completed", (object?)feature.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@runId", (object?)feature.RunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Get a feature by ID.</summary>
    public async Task<FeatureDefinition?> GetFeatureAsync(string featureId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, description, target_repo, base_branch, tech_stack_override,
                   additional_context, acceptance_criteria, status, created_at, started_at, completed_at, run_id
            FROM features WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", featureId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return MapFeature(reader);
    }

    /// <summary>List all features, most recent first.</summary>
    public async Task<IReadOnlyList<FeatureDefinition>> ListFeaturesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, description, target_repo, base_branch, tech_stack_override,
                   additional_context, acceptance_criteria, status, created_at, started_at, completed_at, run_id
            FROM features
            ORDER BY created_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var features = new List<FeatureDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            features.Add(MapFeature(reader));
        return features;
    }

    /// <summary>Update feature status and optional timestamps.</summary>
    public async Task UpdateFeatureStatusAsync(string featureId, FeatureStatus status, string? runId = null, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = status switch
        {
            FeatureStatus.Running => """
                UPDATE features SET status = @status, started_at = COALESCE(started_at, datetime('now')), run_id = @runId
                WHERE id = @id;
                """,
            FeatureStatus.Completed or FeatureStatus.Failed or FeatureStatus.Cancelled => """
                UPDATE features SET status = @status, completed_at = datetime('now')
                WHERE id = @id;
                """,
            _ => "UPDATE features SET status = @status WHERE id = @id;"
        };
        cmd.Parameters.AddWithValue("@id", featureId);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@runId", (object?)runId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Delete a feature (only allowed for Draft status).</summary>
    public async Task DeleteFeatureAsync(string featureId, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM features WHERE id = @id AND status = 'Draft';";
        cmd.Parameters.AddWithValue("@id", featureId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FeatureDefinition MapFeature(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Description = reader.GetString(2),
        TargetRepo = reader.IsDBNull(3) ? null : reader.GetString(3),
        BaseBranch = reader.GetString(4),
        TechStackOverride = reader.IsDBNull(5) ? null : reader.GetString(5),
        AdditionalContext = reader.IsDBNull(6) ? null : reader.GetString(6),
        AcceptanceCriteria = reader.IsDBNull(7) ? null : reader.GetString(7),
        Status = Enum.Parse<FeatureStatus>(reader.GetString(8)),
        CreatedAt = reader.GetDateTime(9),
        StartedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
        CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
        RunId = reader.IsDBNull(12) ? null : reader.GetString(12)
    };

    // ── Strategy / Framework result persistence ──────────────────────────

    /// <summary>
    /// Persist a completed strategy task and all its candidates (including screenshots) to SQLite.
    /// Called when a task moves from active → recent in CandidateStateStore.
    /// </summary>
    public void SaveStrategyTask(StrategyTaskRecord task)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var tx = _connection.BeginTransaction();
        try
        {
            using var taskCmd = _connection.CreateCommand();
            taskCmd.Transaction = tx;
            taskCmd.CommandText = """
                INSERT OR REPLACE INTO strategy_tasks
                    (run_id, task_id, started_at, completed_at, winner_strategy_id, tie_break_reason, evaluation_elapsed_sec)
                VALUES
                    ($run_id, $task_id, $started_at, $completed_at, $winner, $tiebreak, $eval_sec)
                """;
            taskCmd.Parameters.AddWithValue("$run_id", task.RunId);
            taskCmd.Parameters.AddWithValue("$task_id", task.TaskId);
            taskCmd.Parameters.AddWithValue("$started_at", task.StartedAt.ToString("o"));
            taskCmd.Parameters.AddWithValue("$completed_at", task.CompletedAt?.ToString("o") ?? (object)DBNull.Value);
            taskCmd.Parameters.AddWithValue("$winner", task.WinnerStrategyId ?? (object)DBNull.Value);
            taskCmd.Parameters.AddWithValue("$tiebreak", task.TieBreakReason ?? (object)DBNull.Value);
            taskCmd.Parameters.AddWithValue("$eval_sec", task.EvaluationElapsedSec ?? (object)DBNull.Value);
            taskCmd.ExecuteNonQuery();

            foreach (var c in task.Candidates)
            {
                using var cCmd = _connection.CreateCommand();
                cCmd.Transaction = tx;
                cCmd.CommandText = """
                    INSERT OR REPLACE INTO strategy_candidates
                        (run_id, task_id, strategy_id, state, started_at, completed_at,
                         elapsed_sec, succeeded, failure_reason, tokens_used,
                         ac_score, design_score, readability_score, visuals_score, survived,
                         judge_skipped_reason, execution_summary_json, screenshot_base64,
                         initial_ac_score, initial_design_score, initial_readability_score,
                         initial_visuals_score, judge_feedback, initial_screenshot_base64,
                         revision_elapsed_sec, revision_skipped_reason)
                    VALUES
                        ($run_id, $task_id, $strategy_id, $state, $started_at, $completed_at,
                         $elapsed_sec, $succeeded, $failure_reason, $tokens_used,
                         $ac_score, $design_score, $readability_score, $visuals_score, $survived,
                         $judge_skipped, $summary_json, $screenshot,
                         $initial_ac_score, $initial_design_score, $initial_readability_score,
                         $initial_visuals_score, $judge_feedback, $initial_screenshot,
                         $revision_elapsed_sec, $revision_skipped_reason)
                    """;
                cCmd.Parameters.AddWithValue("$run_id", task.RunId);
                cCmd.Parameters.AddWithValue("$task_id", task.TaskId);
                cCmd.Parameters.AddWithValue("$strategy_id", c.StrategyId);
                cCmd.Parameters.AddWithValue("$state", c.State);
                cCmd.Parameters.AddWithValue("$started_at", c.StartedAt?.ToString("o") ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$completed_at", c.CompletedAt?.ToString("o") ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$elapsed_sec", c.ElapsedSec ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$succeeded", c.Succeeded.HasValue ? (c.Succeeded.Value ? 1 : 0) : DBNull.Value);
                cCmd.Parameters.AddWithValue("$failure_reason", c.FailureReason ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$tokens_used", c.TokensUsed ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$ac_score", c.AcScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$design_score", c.DesignScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$readability_score", c.ReadabilityScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$visuals_score", c.VisualsScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$survived", c.Survived.HasValue ? (c.Survived.Value ? 1 : 0) : DBNull.Value);
                cCmd.Parameters.AddWithValue("$judge_skipped", c.JudgeSkippedReason ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$summary_json", c.ExecutionSummaryJson ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$screenshot", c.ScreenshotBase64 ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$initial_ac_score", c.InitialAcScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$initial_design_score", c.InitialDesignScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$initial_readability_score", c.InitialReadabilityScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$initial_visuals_score", c.InitialVisualsScore ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$judge_feedback", c.JudgeFeedback ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$initial_screenshot", c.InitialScreenshotBase64 ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$revision_elapsed_sec", c.RevisionElapsedSec ?? (object)DBNull.Value);
                cCmd.Parameters.AddWithValue("$revision_skipped_reason", c.RevisionSkippedReason ?? (object)DBNull.Value);
                cCmd.ExecuteNonQuery();

                // Batch-insert activity log entries for this candidate (capped at 50 most recent)
                foreach (var entry in c.ActivityLog.TakeLast(50))
                {
                    using var aCmd = _connection.CreateCommand();
                    aCmd.Transaction = tx;
                    aCmd.CommandText = """
                        INSERT INTO strategy_activity_log
                            (run_id, task_id, strategy_id, timestamp, category, message, metadata_json)
                        VALUES
                            ($run_id, $task_id, $strategy_id, $ts, $cat, $msg, $meta)
                        """;
                    aCmd.Parameters.AddWithValue("$run_id", task.RunId);
                    aCmd.Parameters.AddWithValue("$task_id", task.TaskId);
                    aCmd.Parameters.AddWithValue("$strategy_id", c.StrategyId);
                    aCmd.Parameters.AddWithValue("$ts", entry.Timestamp.ToString("o"));
                    aCmd.Parameters.AddWithValue("$cat", entry.Category);
                    aCmd.Parameters.AddWithValue("$msg", entry.Message);
                    aCmd.Parameters.AddWithValue("$meta", entry.MetadataJson ?? (object)DBNull.Value);
                    aCmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Load completed strategy tasks from SQLite, most recent first.
    /// Used by CandidateStateStore to hydrate on startup.
    /// </summary>
    public List<StrategyTaskRecord> LoadRecentStrategyTasks(int limit = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tasks = new List<StrategyTaskRecord>();

        using var taskCmd = _connection.CreateCommand();
        taskCmd.CommandText = """
            SELECT run_id, task_id, started_at, completed_at, winner_strategy_id,
                   tie_break_reason, evaluation_elapsed_sec
            FROM strategy_tasks
            ORDER BY completed_at DESC, started_at DESC
            LIMIT $limit
            """;
        taskCmd.Parameters.AddWithValue("$limit", limit);

        using var taskReader = taskCmd.ExecuteReader();
        while (taskReader.Read())
        {
            var runId = taskReader.GetString(0);
            var taskId = taskReader.GetString(1);

            var task = new StrategyTaskRecord
            {
                RunId = runId,
                TaskId = taskId,
                StartedAt = DateTimeOffset.Parse(taskReader.GetString(2)),
                CompletedAt = taskReader.IsDBNull(3) ? null : DateTimeOffset.Parse(taskReader.GetString(3)),
                WinnerStrategyId = taskReader.IsDBNull(4) ? null : taskReader.GetString(4),
                TieBreakReason = taskReader.IsDBNull(5) ? null : taskReader.GetString(5),
                EvaluationElapsedSec = taskReader.IsDBNull(6) ? null : taskReader.GetDouble(6),
                Candidates = new List<StrategyCandidateRecord>(),
            };
            tasks.Add(task);
        }

        // Load candidates for each task
        foreach (var task in tasks)
        {
            using var cCmd = _connection.CreateCommand();
            cCmd.CommandText = """
                SELECT strategy_id, state, started_at, completed_at, elapsed_sec,
                       succeeded, failure_reason, tokens_used,
                       ac_score, design_score, readability_score, visuals_score, survived,
                       judge_skipped_reason, execution_summary_json, screenshot_base64,
                       initial_ac_score, initial_design_score, initial_readability_score,
                       initial_visuals_score, judge_feedback, initial_screenshot_base64,
                       revision_elapsed_sec, revision_skipped_reason
                FROM strategy_candidates
                WHERE run_id = $run_id AND task_id = $task_id
                """;
            cCmd.Parameters.AddWithValue("$run_id", task.RunId);
            cCmd.Parameters.AddWithValue("$task_id", task.TaskId);

            using var cReader = cCmd.ExecuteReader();
            while (cReader.Read())
            {
                task.Candidates.Add(new StrategyCandidateRecord
                {
                    StrategyId = cReader.GetString(0),
                    State = cReader.GetString(1),
                    StartedAt = cReader.IsDBNull(2) ? null : DateTimeOffset.Parse(cReader.GetString(2)),
                    CompletedAt = cReader.IsDBNull(3) ? null : DateTimeOffset.Parse(cReader.GetString(3)),
                    ElapsedSec = cReader.IsDBNull(4) ? null : cReader.GetDouble(4),
                    Succeeded = cReader.IsDBNull(5) ? null : cReader.GetInt32(5) == 1,
                    FailureReason = cReader.IsDBNull(6) ? null : cReader.GetString(6),
                    TokensUsed = cReader.IsDBNull(7) ? null : cReader.GetInt64(7),
                    AcScore = cReader.IsDBNull(8) ? null : cReader.GetInt32(8),
                    DesignScore = cReader.IsDBNull(9) ? null : cReader.GetInt32(9),
                    ReadabilityScore = cReader.IsDBNull(10) ? null : cReader.GetInt32(10),
                    VisualsScore = cReader.IsDBNull(11) ? null : cReader.GetInt32(11),
                    Survived = cReader.IsDBNull(12) ? null : cReader.GetInt32(12) == 1,
                    JudgeSkippedReason = cReader.IsDBNull(13) ? null : cReader.GetString(13),
                    ExecutionSummaryJson = cReader.IsDBNull(14) ? null : cReader.GetString(14),
                    ScreenshotBase64 = cReader.IsDBNull(15) ? null : cReader.GetString(15),
                    InitialAcScore = cReader.IsDBNull(16) ? null : cReader.GetInt32(16),
                    InitialDesignScore = cReader.IsDBNull(17) ? null : cReader.GetInt32(17),
                    InitialReadabilityScore = cReader.IsDBNull(18) ? null : cReader.GetInt32(18),
                    InitialVisualsScore = cReader.IsDBNull(19) ? null : cReader.GetInt32(19),
                    JudgeFeedback = cReader.IsDBNull(20) ? null : cReader.GetString(20),
                    InitialScreenshotBase64 = cReader.IsDBNull(21) ? null : cReader.GetString(21),
                    RevisionElapsedSec = cReader.IsDBNull(22) ? null : cReader.GetDouble(22),
                    RevisionSkippedReason = cReader.IsDBNull(23) ? null : cReader.GetString(23),
                });
            }

            // Load activity logs for each candidate (capped at 50 per candidate)
            foreach (var c in task.Candidates)
            {
                using var aCmd = _connection.CreateCommand();
                aCmd.CommandText = """
                    SELECT timestamp, category, message, metadata_json
                    FROM strategy_activity_log
                    WHERE run_id = $run_id AND task_id = $task_id AND strategy_id = $strategy_id
                    ORDER BY timestamp ASC
                    LIMIT 50
                    """;
                aCmd.Parameters.AddWithValue("$run_id", task.RunId);
                aCmd.Parameters.AddWithValue("$task_id", task.TaskId);
                aCmd.Parameters.AddWithValue("$strategy_id", c.StrategyId);

                using var aReader = aCmd.ExecuteReader();
                while (aReader.Read())
                {
                    c.ActivityLog.Add(new StrategyActivityLogEntry(
                        DateTimeOffset.Parse(aReader.GetString(0)),
                        aReader.GetString(1),
                        aReader.GetString(2),
                        aReader.IsDBNull(3) ? null : aReader.GetString(3)));
                }
            }
        }

        return tasks;
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
