using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// SQLite persistence for FlowMonitor findings + actions. Uses the same database file
/// as <see cref="AgentStateStore"/> (alongside agent_state, activity_log, etc.) but with
/// its own connection to avoid contention with the existing store's lock.
///
/// All writes are best-effort: we'd rather lose an audit row than crash the monitor loop.
/// </summary>
public sealed class FlowMonitorPersistence : IDisposable, IFixRecommendationStore
{
    private readonly AgentStateStore _stateStore;
    private readonly ILogger<FlowMonitorPersistence> _logger;
    private readonly FlowMonitorEventBus? _eventBus;
    private readonly object _dbLock = new();
    private SqliteConnection? _connection;
    private string? _connectedPath;
    private bool _disposed;

    public FlowMonitorPersistence(AgentStateStore stateStore, ILogger<FlowMonitorPersistence> logger, FlowMonitorEventBus? eventBus = null)
    {
        _stateStore = stateStore;
        _logger = logger;
        _eventBus = eventBus;
        EnsureConnection();
    }

    private SqliteConnection? EnsureConnection()
    {
        // AgentStateStore.Reconfigure swaps DB files between runs — re-open if path changed.
        var currentPath = _stateStore.DatabasePath;
        if (string.IsNullOrEmpty(currentPath)) return null;
        lock (_dbLock)
        {
            if (_connection is not null && _connectedPath == currentPath) return _connection;
            // Bug fix (T0.2): single Dispose handles Close + handle release atomically.
            // Previous try{Close} + try{Dispose} pattern silently leaked file handles
            // when Close threw (e.g., while a transaction was in progress) — Dispose is
            // documented to be safe to call multiple times and handles all cleanup paths.
            try { _connection?.Dispose(); } catch { /* best-effort during reconfigure */ }
            _connection = new SqliteConnection($"Data Source={currentPath}");
            _connection.Open();
            _connectedPath = currentPath;
            InitializeSchema();
            return _connection;
        }
    }

    private void InitializeSchema()
    {
        if (_connection is null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS flow_findings (
                id              TEXT PRIMARY KEY,
                detected_at     DATETIME NOT NULL,
                detector_id     TEXT NOT NULL,
                severity        TEXT NOT NULL,
                target_agent_id TEXT,
                target_resource TEXT,
                summary         TEXT NOT NULL,
                rationale       TEXT NOT NULL,
                state           TEXT NOT NULL,
                dedup_key       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_flow_findings_detected_at ON flow_findings(detected_at DESC);
            CREATE INDEX IF NOT EXISTS idx_flow_findings_dedup ON flow_findings(dedup_key);
            -- Bug fix (T0.3): the dedup query filters `state IN ('Open','ActedOn')` AND
            -- `dedup_key = ?` AND `detected_at > cutoff`. Without a covering index, this
            -- becomes a full scan after ~10K rows. The composite (state, dedup_key,
            -- detected_at DESC) index lets SQLite serve dedup checks from the index alone.
            CREATE INDEX IF NOT EXISTS idx_flow_findings_state_dedup ON flow_findings(state, dedup_key, detected_at DESC);

            CREATE TABLE IF NOT EXISTS flow_actions (
                id            TEXT PRIMARY KEY,
                finding_id    TEXT NOT NULL,
                action_type   TEXT NOT NULL,
                target        TEXT,
                initiated_at  DATETIME NOT NULL,
                completed_at  DATETIME,
                result        TEXT NOT NULL,
                detail        TEXT,
                FOREIGN KEY (finding_id) REFERENCES flow_findings(id)
            );
            CREATE INDEX IF NOT EXISTS idx_flow_actions_initiated_at ON flow_actions(initiated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_flow_actions_finding ON flow_actions(finding_id);

            CREATE TABLE IF NOT EXISTS flow_monitor_ticks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                tick_at     DATETIME NOT NULL DEFAULT (datetime('now')),
                detectors   TEXT NOT NULL,
                findings    INTEGER NOT NULL DEFAULT 0,
                actions     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_flow_ticks_at ON flow_monitor_ticks(tick_at DESC);

            -- T1.5: FixRecommendation pipeline storage. Each row is a two-pass Copilot
            -- /plan + rubber-duck output paired with the Critical finding that motivated
            -- it. Operator decisions (approve/rework/reject) mutate state in place.
            CREATE TABLE IF NOT EXISTS fix_recommendations (
                id                TEXT PRIMARY KEY,
                finding_id        TEXT NOT NULL,
                created_at        DATETIME NOT NULL DEFAULT (datetime('now')),
                updated_at        DATETIME NOT NULL DEFAULT (datetime('now')),
                detector_id       TEXT NOT NULL,
                severity          TEXT NOT NULL,
                confidence        REAL NOT NULL DEFAULT 0.0,
                needs_restart     INTEGER NOT NULL DEFAULT 0,
                files_to_change   TEXT,
                estimated_minutes INTEGER,
                plan_markdown     TEXT NOT NULL,
                plan_file_path    TEXT,
                state             TEXT NOT NULL DEFAULT 'Draft',
                operator_feedback TEXT,
                rework_count      INTEGER NOT NULL DEFAULT 0,
                resolved_at       DATETIME,
                pr_number         INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_fix_recs_finding ON fix_recommendations(finding_id);
            CREATE INDEX IF NOT EXISTS idx_fix_recs_state ON fix_recommendations(state);
            """;
        cmd.ExecuteNonQuery();

        // T1.2: per-action escalation rung counter. Older rows default to 1 (rung 1 ≡ bus
        // nudge, the only behavior pre-T1.2) so rung-based routing on legacy data keeps
        // working — re-detection just steps up from rung 2 instead of rung 1.
        TryAddColumn("flow_actions", "attempt_count", "INTEGER NOT NULL DEFAULT 1");

        // T1.2: optional human-friendly target name (e.g. "Software Engineer 1") so escalation
        // actions can look up PRs/issues by display name without reaching across project layers
        // for an AgentRegistry. Detectors that don't know a display name leave it null and
        // actions degrade gracefully (return Skipped).
        TryAddColumn("flow_findings", "target_display_name", "TEXT");

        // T1.6: tier classification + affected-files allowlist for FixRecommendations.
        // Drives the approve endpoint's apply path: Live → in-place CLI edit + auto-reload,
        // DeferredRestart → CLI edit + restart prompt, Blocked → save to staged/ for next boot.
        // Older rows leave both columns null and lazy-classify on read in MapRecommendation.
        TryAddColumn("fix_recommendations", "fix_tier", "TEXT");
        TryAddColumn("fix_recommendations", "affected_files", "TEXT");

        // post-run2-undo-on-expired: track when a finding has had its prior actions undone
        // (e.g., agent-stuck label removed). Without this column, the UndoSweep would re-run
        // UndoAsync on the same Expired finding every tick. UndoAsync IS idempotent (refetches
        // labels and no-ops if already gone) but each invocation costs an API call — better to
        // mark and skip.
        TryAddColumn("flow_findings", "undone_at", "TEXT");

        // Diagnostic enrichment: JSON blob of diagnostic checks + recommended fix.
        // Populated by IFlowDiagnosticEnricher after detection.
        TryAddColumn("flow_findings", "diagnostics_json", "TEXT");
        TryAddColumn("flow_findings", "recommended_fix_id", "TEXT");
        TryAddColumn("flow_findings", "recommended_fix_desc", "TEXT");
    }

    /// <summary>
    /// Lightweight column migration. <c>ALTER TABLE … ADD COLUMN</c> is the recommended
    /// SQLite migration pattern (no transaction, idempotent guard via try/catch — duplicate
    /// column errors are expected on subsequent boots). We never drop columns; the SQLite
    /// file lives across upgrades, so additive migrations are safe.
    /// </summary>
    private void TryAddColumn(string table, string column, string sqlType)
    {
        if (_connection is null) return;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType};";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("FlowMonitorPersistence: added column {Column} to {Table}", column, table);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — expected on every boot after the first. No-op.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlowMonitorPersistence: failed to add column {Column} to {Table}", column, table);
        }
    }

    /// <summary>
    /// Insert a finding, suppressing if an open finding with the same DedupKey was inserted
    /// within the dedup window (caller-supplied). Returns true if inserted, false if deduped.
    /// </summary>
    public bool InsertFinding(FlowFinding finding, TimeSpan dedupWindow)
    {
        var conn = EnsureConnection();
        if (conn is null) return false;
        try
        {
            lock (_dbLock)
            {
                if (!string.IsNullOrEmpty(finding.DedupKey))
                {
                    using var dedupCmd = conn.CreateCommand();
                    dedupCmd.CommandText =
                        "SELECT COUNT(*) FROM flow_findings WHERE dedup_key = $key " +
                        "AND detected_at > $cutoff AND state IN ('Open','ActedOn')";
                    dedupCmd.Parameters.AddWithValue("$key", finding.DedupKey);
                    dedupCmd.Parameters.AddWithValue("$cutoff",
                        DateTime.UtcNow.Subtract(dedupWindow).ToString("o"));
                    var existing = Convert.ToInt32(dedupCmd.ExecuteScalar() ?? 0);
                    if (existing > 0) return false;
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO flow_findings
                      (id, detected_at, detector_id, severity, target_agent_id, target_resource,
                       summary, rationale, state, dedup_key, target_display_name,
                       diagnostics_json, recommended_fix_id, recommended_fix_desc)
                    VALUES
                      ($id, $detectedAt, $detectorId, $severity, $targetAgentId, $targetResource,
                       $summary, $rationale, $state, $dedupKey, $targetDisplayName,
                       $diagnosticsJson, $recommendedFixId, $recommendedFixDesc);
                    """;
                cmd.Parameters.AddWithValue("$id", finding.Id);
                cmd.Parameters.AddWithValue("$detectedAt", finding.DetectedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$detectorId", finding.DetectorId);
                cmd.Parameters.AddWithValue("$severity", finding.Severity.ToString());
                cmd.Parameters.AddWithValue("$targetAgentId", (object?)finding.TargetAgentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$targetResource", (object?)finding.TargetResource ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$summary", finding.Summary);
                cmd.Parameters.AddWithValue("$rationale", finding.Rationale);
                cmd.Parameters.AddWithValue("$state", finding.State.ToString());
                cmd.Parameters.AddWithValue("$dedupKey", (object?)finding.DedupKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$targetDisplayName", (object?)finding.TargetDisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$diagnosticsJson",
                    finding.Diagnostics.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(finding.Diagnostics)
                        : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$recommendedFixId", (object?)finding.RecommendedFixId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$recommendedFixDesc", (object?)finding.RecommendedFixDescription ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertFinding failed for {Detector}/{Target}", finding.DetectorId, finding.TargetResource);
            return false;
        }
    }

    public void UpdateFindingState(string findingId, FlowFindingState newState)
    {
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE flow_findings SET state = $state WHERE id = $id";
                cmd.Parameters.AddWithValue("$state", newState.ToString());
                cmd.Parameters.AddWithValue("$id", findingId);
                cmd.ExecuteNonQuery();
            }
            _eventBus?.StatusChange(findingId, $"Finding state → {newState}", findingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateFindingState failed for {Id}", findingId);
        }
    }

    public void InsertAction(FlowAction action)
    {
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO flow_actions
                      (id, finding_id, action_type, target, initiated_at, completed_at, result, detail, attempt_count)
                    VALUES
                      ($id, $findingId, $actionType, $target, $initiatedAt, $completedAt, $result, $detail, $attemptCount);
                    """;
                cmd.Parameters.AddWithValue("$id", action.Id);
                cmd.Parameters.AddWithValue("$findingId", action.FindingId);
                cmd.Parameters.AddWithValue("$actionType", action.ActionType);
                cmd.Parameters.AddWithValue("$target", (object?)action.Target ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$initiatedAt", action.InitiatedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$completedAt",
                    action.CompletedAt.HasValue ? action.CompletedAt.Value.UtcDateTime.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$result", action.Result.ToString());
                cmd.Parameters.AddWithValue("$detail", (object?)action.Detail ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$attemptCount", action.AttemptCount);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertAction failed for {Type}/{Target}", action.ActionType, action.Target);
        }
    }

    /// <summary>Record one tick of the monitor loop for observability of liveness.</summary>
    public void RecordTick(IReadOnlyList<string> detectorsRun, int findingsCreated, int actionsTaken)
    {
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO flow_monitor_ticks (detectors, findings, actions) " +
                    "VALUES ($d, $f, $a)";
                cmd.Parameters.AddWithValue("$d", string.Join(",", detectorsRun));
                cmd.Parameters.AddWithValue("$f", findingsCreated);
                cmd.Parameters.AddWithValue("$a", actionsTaken);
                cmd.ExecuteNonQuery();
            }
        }
        catch { /* tick logging is purely diagnostic — never crash on it */ }
    }

    public IReadOnlyList<FlowFinding> GetRecentFindings(int limit = 50)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowFinding>();
        var list = new List<FlowFinding>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, detected_at, detector_id, severity, target_agent_id, target_resource, " +
                    "summary, rationale, state, dedup_key, target_display_name, " +
                    "diagnostics_json, recommended_fix_id, recommended_fix_desc " +
                    "FROM flow_findings ORDER BY detected_at DESC LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(ReadFinding(reader));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecentFindings failed");
        }
        return list;
    }

    /// <summary>
    /// Returns active (Open or ActedOn) findings sorted by severity (Critical first) then age.
    /// Used by the Flow Monitor dashboard to show current issues requiring attention.
    /// </summary>
    public IReadOnlyList<FlowFinding> GetActiveFindings(int limit = 50)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowFinding>();
        var list = new List<FlowFinding>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, detected_at, detector_id, severity, target_agent_id, target_resource, " +
                    "summary, rationale, state, dedup_key, target_display_name, " +
                    "diagnostics_json, recommended_fix_id, recommended_fix_desc " +
                    "FROM flow_findings " +
                    "WHERE state IN ('Open', 'ActedOn') " +
                    "ORDER BY CASE severity WHEN 'Critical' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, " +
                    "detected_at ASC " +
                    "LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(ReadFinding(reader));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActiveFindings failed");
        }
        return list;
    }

    /// <summary>
    /// post-run2-undo-on-expired: returns Expired findings within the time window that have
    /// NOT yet been swept for action-undo (i.e., undone_at is null). Used by FlowMonitor's
    /// per-tick undo sweep — when a finding's condition has cleared but its prior actions
    /// (e.g., applied labels) still linger because the Expired transition skipped UndoAsync.
    /// </summary>
    public IReadOnlyList<FlowFinding> GetExpiredFindingsForUndoSweep(DateTimeOffset since)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowFinding>();
        var list = new List<FlowFinding>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, detected_at, detector_id, severity, target_agent_id, target_resource, " +
                    "summary, rationale, state, dedup_key, target_display_name, " +
                    "diagnostics_json, recommended_fix_id, recommended_fix_desc " +
                    "FROM flow_findings " +
                    "WHERE state = 'Expired' AND detected_at >= $since AND undone_at IS NULL " +
                    "ORDER BY detected_at ASC";
                cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(ReadFinding(reader));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetExpiredFindingsForUndoSweep failed");
        }
        return list;
    }

    /// <summary>
    /// post-run2-undo-on-expired: mark a finding as having had its actions undone. Once set,
    /// the UndoSweep skips it on subsequent ticks.
    /// </summary>
    public void MarkFindingUndone(string findingId)
    {
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE flow_findings SET undone_at = $now WHERE id = $id";
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$id", findingId);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MarkFindingUndone failed for {Id}", findingId);
        }
    }

    /// <summary>
    /// T1.3: Findings currently in <see cref="FlowFindingState.ActedOn"/> within the
    /// supplied time window. Used by the verification loop to re-run detectors and
    /// confirm the underlying condition has cleared.
    /// </summary>
    public IReadOnlyList<FlowFinding> GetActedOnFindingsSince(DateTimeOffset since)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowFinding>();
        var list = new List<FlowFinding>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, detected_at, detector_id, severity, target_agent_id, target_resource, " +
                    "summary, rationale, state, dedup_key, target_display_name, " +
                    "diagnostics_json, recommended_fix_id, recommended_fix_desc " +
                    "FROM flow_findings " +
                    "WHERE state = 'ActedOn' AND detected_at >= $since " +
                    "ORDER BY detected_at ASC";
                cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(ReadFinding(reader));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActedOnFindingsSince failed");
        }
        return list;
    }

    /// <summary>
    /// T1.3: bump severity on an existing finding (Info → Warning → Critical) when the
    /// originating condition persists after a corrective action. Best-effort.
    /// </summary>
    public void UpdateFindingSeverity(string findingId, FlowFindingSeverity newSeverity)
    {
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE flow_findings SET severity = $severity WHERE id = $id";
                cmd.Parameters.AddWithValue("$severity", newSeverity.ToString());
                cmd.Parameters.AddWithValue("$id", findingId);
                cmd.ExecuteNonQuery();
            }
            _eventBus?.StatusChange(findingId, $"Finding severity → {newSeverity}", findingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateFindingSeverity failed for {Id}", findingId);
        }
    }

    /// <summary>
    /// T1.2: count prior actions on findings sharing the same dedup_key inside the
    /// supplied window. Used by the routing layer to pick the next escalation rung —
    /// 0 prior actions → rung 1 (bus nudge), 1 → rung 2 (explicit ask), 2+ → rung 3
    /// (escalate to human). Joins via finding_id since flow_actions doesn't carry the
    /// dedup_key directly. Verification rows (action_type='verify-acted-on') are
    /// excluded — they're observability, not escalation budget.
    /// </summary>
    public int GetAttemptCount(string dedupKey, TimeSpan window)
    {
        if (string.IsNullOrEmpty(dedupKey)) return 0;
        var conn = EnsureConnection();
        if (conn is null) return 0;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM flow_actions a " +
                    "JOIN flow_findings f ON a.finding_id = f.id " +
                    "WHERE f.dedup_key = $key " +
                    "  AND a.initiated_at >= $cutoff " +
                    "  AND a.action_type != 'verify-acted-on'";
                cmd.Parameters.AddWithValue("$key", dedupKey);
                cmd.Parameters.AddWithValue("$cutoff",
                    DateTime.UtcNow.Subtract(window).ToString("o"));
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAttemptCount failed for {Key}", dedupKey);
            return 0;
        }
    }

    /// <summary>
    /// Get the most recent action time for a specific finding and action type.
    /// Used by verification throttling to avoid spamming verify-acted-on rows.
    /// </summary>
    public DateTimeOffset? GetLastActionTime(string findingId, string actionType)
    {
        var conn = EnsureConnection();
        if (conn is null) return null;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT MAX(initiated_at) FROM flow_actions " +
                    "WHERE finding_id = $findingId AND action_type = $actionType";
                cmd.Parameters.AddWithValue("$findingId", findingId);
                cmd.Parameters.AddWithValue("$actionType", actionType);
                var result = cmd.ExecuteScalar();
                if (result is null || result is DBNull) return null;
                return DateTimeOffset.Parse((string)result, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<FlowAction> GetRecentActions(int limit = 50)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowAction>();
        var list = new List<FlowAction>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, finding_id, action_type, target, initiated_at, completed_at, result, detail, " +
                    "COALESCE(attempt_count, 1) AS attempt_count " +
                    "FROM flow_actions ORDER BY initiated_at DESC LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new FlowAction
                    {
                        Id = reader.GetString(0),
                        FindingId = reader.GetString(1),
                        ActionType = reader.GetString(2),
                        Target = reader.IsDBNull(3) ? null : reader.GetString(3),
                        InitiatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                        CompletedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                        Result = Enum.Parse<FlowActionResult>(reader.GetString(6)),
                        Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
                        AttemptCount = reader.IsDBNull(8) ? 1 : reader.GetInt32(8),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecentActions failed");
        }
        return list;
    }

    /// <summary>
    /// post-run-stuck-label-cleanup: returns all SUCCEEDED actions taken on a given finding,
    /// excluding internal verification rows ("verify-acted-on"). Used by the verification loop
    /// to find prior actions that may have applied side-effects (e.g., labels) so they can be
    /// undone when the finding is marked Resolved.
    /// </summary>
    public IReadOnlyList<FlowAction> GetActionsForFinding(string findingId)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FlowAction>();
        var list = new List<FlowAction>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, finding_id, action_type, target, initiated_at, completed_at, result, detail, " +
                    "COALESCE(attempt_count, 1) AS attempt_count " +
                    "FROM flow_actions " +
                    "WHERE finding_id = $fid " +
                    "  AND action_type != 'verify-acted-on' " +
                    "  AND result = 'Success' " +
                    "ORDER BY initiated_at ASC";
                cmd.Parameters.AddWithValue("$fid", findingId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new FlowAction
                    {
                        Id = reader.GetString(0),
                        FindingId = reader.GetString(1),
                        ActionType = reader.GetString(2),
                        Target = reader.IsDBNull(3) ? null : reader.GetString(3),
                        InitiatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                        CompletedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                        Result = Enum.Parse<FlowActionResult>(reader.GetString(6)),
                        Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
                        AttemptCount = reader.IsDBNull(8) ? 1 : reader.GetInt32(8),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetActionsForFinding failed for {Id}", findingId);
        }
        return list;
    }

    /// <summary>Returns the timestamp of the most recent tick, or null if no ticks recorded.</summary>
    public DateTimeOffset? GetLastTick()
    {
        var conn = EnsureConnection();
        if (conn is null) return null;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT tick_at FROM flow_monitor_ticks ORDER BY tick_at DESC LIMIT 1";
                var result = cmd.ExecuteScalar();
                if (result is null || result == DBNull.Value) return null;
                return DateTimeOffset.Parse(result.ToString()!);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Counts corrective actions (excludes internal <c>verify-acted-on</c> checks) since the
    /// given timestamp. Used by the rate limiter — verification checks must NOT consume the
    /// action budget or real corrective actions (kick-agent-poll, escalate-to-human) are starved.
    /// </summary>
    public int CountActionsSince(DateTimeOffset since)
    {
        var conn = EnsureConnection();
        if (conn is null) return 0;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM flow_actions WHERE initiated_at >= $since AND action_type != 'verify-acted-on'";
                cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("o"));
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }
        catch { return 0; }
    }

    /// <summary>
    /// Prune findings/actions/ticks older than the retention window. Without this the
    /// tables grow unbounded — production observation shows ~500MB DB after 90 days of
    /// continuous operation. Findings + actions are JOINed via finding_id so we delete
    /// actions before findings to satisfy the FK constraint.
    /// Best-effort: failures are logged but never crash the monitor loop.
    /// </summary>
    public int PruneOldRecords(TimeSpan retentionWindow)
    {
        var conn = EnsureConnection();
        if (conn is null) return 0;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(retentionWindow).UtcDateTime.ToString("o");
            lock (_dbLock)
            {
                using var tx = conn.BeginTransaction();
                int total = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM flow_actions WHERE initiated_at < $cutoff";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    total += cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM flow_findings WHERE detected_at < $cutoff";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    total += cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM flow_monitor_ticks WHERE tick_at < $cutoff";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    total += cmd.ExecuteNonQuery();
                }
                tx.Commit();
                if (total > 0)
                    _logger.LogInformation("FlowMonitor pruned {Count} records older than {Cutoff}", total, cutoff);
                return total;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PruneOldRecords failed (non-fatal)");
            return 0;
        }
    }

    // ---------------------------------------------------------------------
    // T1.5: FixRecommendation CRUD
    // ---------------------------------------------------------------------

    /// <summary>
    /// Insert a new fix recommendation. Returns the row's id (echoes <see cref="FixRecommendation.Id"/>
    /// when supplied, or a freshly-minted GUID when blank). Best-effort: returns empty string on failure.
    /// </summary>
    public string InsertRecommendation(FixRecommendation r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var conn = EnsureConnection();
        if (conn is null) return string.Empty;

        var id = string.IsNullOrEmpty(r.Id) ? Guid.NewGuid().ToString("N") : r.Id;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO fix_recommendations
                      (id, finding_id, created_at, updated_at, detector_id, severity,
                       confidence, needs_restart, files_to_change, estimated_minutes,
                       plan_markdown, plan_file_path, state, operator_feedback,
                       rework_count, resolved_at, pr_number, fix_tier, affected_files)
                    VALUES
                      ($id, $findingId, $createdAt, $updatedAt, $detectorId, $severity,
                       $confidence, $needsRestart, $files, $minutes,
                       $plan, $planFile, $state, $feedback,
                       $reworkCount, $resolvedAt, $prNumber, $fixTier, $affectedFiles);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$findingId", r.FindingId);
                cmd.Parameters.AddWithValue("$createdAt", r.CreatedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$updatedAt", r.UpdatedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$detectorId", r.DetectorId);
                cmd.Parameters.AddWithValue("$severity", r.Severity.ToString());
                cmd.Parameters.AddWithValue("$confidence", r.Confidence);
                cmd.Parameters.AddWithValue("$needsRestart", r.NeedsRestart ? 1 : 0);
                cmd.Parameters.AddWithValue("$files", (object?)r.FilesToChange ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$minutes", (object?)r.EstimatedMinutes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$plan", r.PlanMarkdown);
                cmd.Parameters.AddWithValue("$planFile", (object?)r.PlanFilePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$state", r.State.ToString());
                cmd.Parameters.AddWithValue("$feedback", (object?)r.OperatorFeedback ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reworkCount", r.ReworkCount);
                cmd.Parameters.AddWithValue("$resolvedAt",
                    r.ResolvedAt.HasValue ? r.ResolvedAt.Value.UtcDateTime.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$prNumber", (object?)r.PrNumber ?? DBNull.Value);
                // T1.6: tier + affected files. AffectedFiles is stored as a JSON array string
                // so the same column is portable across in-memory deserialisation.
                cmd.Parameters.AddWithValue("$fixTier", r.FixTier.HasValue ? r.FixTier.Value.ToString() : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$affectedFiles",
                    r.AffectedFiles is { Count: > 0 }
                        ? System.Text.Json.JsonSerializer.Serialize(r.AffectedFiles)
                        : (object)DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertRecommendation failed for finding {FindingId}", r.FindingId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Transition a recommendation to a new state, optionally recording operator feedback
    /// (used when transitioning to a rework or rejection). Bumps <c>updated_at</c> and, for
    /// terminal states (<see cref="FixRecommendationState.Complete"/>, <see cref="FixRecommendationState.Rejected"/>,
    /// <see cref="FixRecommendationState.Pruned"/>), also stamps <c>resolved_at</c>.
    /// Best-effort: failures are logged but never thrown.
    /// </summary>
    public void UpdateRecommendationState(string id, FixRecommendationState newState, string? feedback = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                var nowIso = DateTime.UtcNow.ToString("o");
                var isTerminal = newState is FixRecommendationState.Complete
                    or FixRecommendationState.Rejected
                    or FixRecommendationState.Pruned;
                cmd.CommandText = """
                    UPDATE fix_recommendations
                       SET state = $state,
                           updated_at = $updatedAt,
                           operator_feedback = COALESCE($feedback, operator_feedback),
                           resolved_at = CASE WHEN $isTerminal = 1 THEN $resolvedAt ELSE resolved_at END
                     WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$state", newState.ToString());
                cmd.Parameters.AddWithValue("$updatedAt", nowIso);
                cmd.Parameters.AddWithValue("$feedback", (object?)feedback ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$isTerminal", isTerminal ? 1 : 0);
                cmd.Parameters.AddWithValue("$resolvedAt", nowIso);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdateRecommendationState failed for {Id}", id);
        }
    }

    /// <summary>
    /// Update the <c>rework_count</c> + feedback after a successful rework pass. Also stamps
    /// <c>updated_at</c>. The new plan content is inserted via a fresh <see cref="InsertRecommendation"/>
    /// (we keep history rather than mutating in place, so the operator can compare versions).
    /// </summary>
    public void IncrementReworkCount(string id, string? feedback)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var conn = EnsureConnection();
        if (conn is null) return;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE fix_recommendations
                       SET rework_count = rework_count + 1,
                           operator_feedback = COALESCE($feedback, operator_feedback),
                           updated_at = $updatedAt
                     WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$feedback", (object?)feedback ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IncrementReworkCount failed for {Id}", id);
        }
    }

    /// <summary>Get the most recent fix recommendations across all findings, newest first.</summary>
    public IReadOnlyList<FixRecommendation> GetRecentRecommendations(int limit = 50)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FixRecommendation>();
        var list = new List<FixRecommendation>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, finding_id, created_at, updated_at, detector_id, severity, " +
                    "confidence, needs_restart, files_to_change, estimated_minutes, " +
                    "plan_markdown, plan_file_path, state, operator_feedback, " +
                    "rework_count, resolved_at, pr_number, fix_tier, affected_files " +
                    "FROM fix_recommendations ORDER BY created_at DESC LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(MapRecommendation(reader));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecentRecommendations failed");
        }
        return list;
    }

    /// <summary>Look up a single recommendation by id, or null if not found.</summary>
    public FixRecommendation? GetRecommendation(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var conn = EnsureConnection();
        if (conn is null) return null;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, finding_id, created_at, updated_at, detector_id, severity, " +
                    "confidence, needs_restart, files_to_change, estimated_minutes, " +
                    "plan_markdown, plan_file_path, state, operator_feedback, " +
                    "rework_count, resolved_at, pr_number, fix_tier, affected_files " +
                    "FROM fix_recommendations WHERE id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read()) return MapRecommendation(reader);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecommendation failed for {Id}", id);
        }
        return null;
    }

    /// <summary>All recommendations attached to a finding (oldest first — i.e. v1 then -rework-v2 etc.).</summary>
    public IReadOnlyList<FixRecommendation> GetRecommendationsForFinding(string findingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(findingId);
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<FixRecommendation>();
        var list = new List<FixRecommendation>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, finding_id, created_at, updated_at, detector_id, severity, " +
                    "confidence, needs_restart, files_to_change, estimated_minutes, " +
                    "plan_markdown, plan_file_path, state, operator_feedback, " +
                    "rework_count, resolved_at, pr_number, fix_tier, affected_files " +
                    "FROM fix_recommendations WHERE finding_id = $f ORDER BY created_at ASC";
                cmd.Parameters.AddWithValue("$f", findingId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(MapRecommendation(reader));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecommendationsForFinding failed for {FindingId}", findingId);
        }
        return list;
    }

    private static FixRecommendation MapRecommendation(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        // T1.6: tier and affected_files were added in a later migration so legacy rows have
        // them as NULL. Lazily classify those rows on read so the dashboard never has to
        // special-case "unknown tier" in the UI.
        FixTier? tier = null;
        IReadOnlyList<string>? affectedFiles = null;

        if (!r.IsDBNull(17))
        {
            if (Enum.TryParse<FixTier>(r.GetString(17), ignoreCase: true, out var parsed))
                tier = parsed;
        }
        if (!r.IsDBNull(18))
        {
            try
            {
                var raw = r.GetString(18);
                affectedFiles = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw);
            }
            catch (System.Text.Json.JsonException)
            {
                // Treat malformed JSON as "no files known" rather than throwing — the row is
                // still useful, just opaque to the classifier until refreshed.
                affectedFiles = null;
            }
        }

        var planMarkdown = r.GetString(10);
        var filesToChange = r.IsDBNull(8) ? null : r.GetString(8);

        // Lazy classify: if the DB row predates T1.6 (tier null), compute it on the fly so
        // the dashboard always has tier metadata. We don't write it back here — the next
        // explicit insert/update path will persist the freshly-computed value.
        if (tier is null)
        {
            var classification = FixClassifier.ClassifyFiles(
                affectedFiles ?? FixClassifier.ExtractFiles(planMarkdown, filesToChange));
            tier = classification.Tier;
            affectedFiles ??= classification.AffectedFiles;
        }

        return new FixRecommendation
        {
            Id = r.GetString(0),
            FindingId = r.GetString(1),
            CreatedAt = DateTimeOffset.Parse(r.GetString(2)),
            UpdatedAt = DateTimeOffset.Parse(r.GetString(3)),
            DetectorId = r.GetString(4),
            Severity = Enum.Parse<FlowFindingSeverity>(r.GetString(5)),
            Confidence = r.GetDouble(6),
            NeedsRestart = r.GetInt32(7) != 0,
            FilesToChange = filesToChange,
            EstimatedMinutes = r.IsDBNull(9) ? null : r.GetInt32(9),
            PlanMarkdown = planMarkdown,
            PlanFilePath = r.IsDBNull(11) ? null : r.GetString(11),
            State = Enum.Parse<FixRecommendationState>(r.GetString(12)),
            OperatorFeedback = r.IsDBNull(13) ? null : r.GetString(13),
            ReworkCount = r.GetInt32(14),
            ResolvedAt = r.IsDBNull(15) ? null : DateTimeOffset.Parse(r.GetString(15)),
            PrNumber = r.IsDBNull(16) ? null : r.GetInt32(16),
            FixTier = tier,
            AffectedFiles = affectedFiles,
        };
    }

    /// <summary>
    /// Shared reader for the standard 14-column finding SELECT used by all query methods.
    /// Column order: id(0), detected_at(1), detector_id(2), severity(3), target_agent_id(4),
    /// target_resource(5), summary(6), rationale(7), state(8), dedup_key(9),
    /// target_display_name(10), diagnostics_json(11), recommended_fix_id(12),
    /// recommended_fix_desc(13).
    /// </summary>
    private static FlowFinding ReadFinding(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        List<FlowDiagnostic> diagnostics = new();
        if (!reader.IsDBNull(11))
        {
            try
            {
                var json = reader.GetString(11);
                diagnostics = System.Text.Json.JsonSerializer.Deserialize<List<FlowDiagnostic>>(json) ?? new();
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed diagnostics JSON — degrade gracefully with empty list
            }
        }

        return new FlowFinding
        {
            Id = reader.GetString(0),
            DetectedAt = DateTimeOffset.Parse(reader.GetString(1)),
            DetectorId = reader.GetString(2),
            Severity = Enum.Parse<FlowFindingSeverity>(reader.GetString(3)),
            TargetAgentId = reader.IsDBNull(4) ? null : reader.GetString(4),
            TargetResource = reader.IsDBNull(5) ? null : reader.GetString(5),
            Summary = reader.GetString(6),
            Rationale = reader.GetString(7),
            State = Enum.Parse<FlowFindingState>(reader.GetString(8)),
            DedupKey = reader.IsDBNull(9) ? null : reader.GetString(9),
            TargetDisplayName = reader.IsDBNull(10) ? null : reader.GetString(10),
            Diagnostics = diagnostics,
            RecommendedFixId = reader.IsDBNull(12) ? null : reader.GetString(12),
            RecommendedFixDescription = reader.IsDBNull(13) ? null : reader.GetString(13),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _connection?.Close(); } catch { }
        try { _connection?.Dispose(); } catch { }
    }
}
