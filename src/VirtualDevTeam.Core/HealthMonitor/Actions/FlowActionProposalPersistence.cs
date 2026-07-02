using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// SQLite persistence for operator-gated <see cref="ProposedFlowAction"/> records.
/// Uses the same database file as <see cref="AgentStateStore"/> (one file per project)
/// but with its own connection to avoid lock contention.
///
/// <para>
/// Thread safety: all public methods acquire <c>_dbLock</c> before touching SQLite.
/// The interface contract is fulfilled via synchronous-under-lock operations wrapped
/// in <c>Task.FromResult</c> — adequate for MVP throughput (proposals arrive at
/// FlowMonitor cadence, not high-frequency).
/// </para>
/// </summary>
public sealed class FlowActionProposalPersistence : IFlowActionProposalStore, IDisposable
{
    private readonly AgentStateStore _stateStore;
    private readonly ILogger<FlowActionProposalPersistence> _logger;
    private readonly object _dbLock = new();
    private SqliteConnection? _connection;
    private string? _connectedPath;
    private bool _disposed;

    public FlowActionProposalPersistence(
        AgentStateStore stateStore,
        ILogger<FlowActionProposalPersistence> logger)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Connection management ──────────────────────────────────────────────────────

    private SqliteConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_dbLock)
        {
            var currentPath = _stateStore.DatabasePath;
            if (_connection is not null && _connectedPath == currentPath) return _connection;
            _connection?.Dispose();
            _connection = new SqliteConnection($"Data Source={currentPath}");
            _connection.Open();
            _connectedPath = currentPath;
            EnsureSchema(_connection);
            return _connection;
        }
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS proposed_flow_actions (
                id                  TEXT PRIMARY KEY,
                created_at          TEXT NOT NULL,
                finding_id          TEXT NOT NULL,
                title               TEXT NOT NULL,
                rationale           TEXT NOT NULL,
                risk_assessment     TEXT NOT NULL,
                risk_tier           TEXT NOT NULL,
                action_type         TEXT NOT NULL,
                parameters_json     TEXT NOT NULL,
                state               TEXT NOT NULL DEFAULT 'Pending',
                operator_action_at  TEXT,
                operator_rationale  TEXT,
                execution_result    TEXT,
                expires_at          TEXT,
                execution_duration_ms INTEGER,
                execution_log       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_proposed_flow_actions_state   ON proposed_flow_actions(state);
            CREATE INDEX IF NOT EXISTS idx_proposed_flow_actions_finding  ON proposed_flow_actions(finding_id);
        """;
        cmd.ExecuteNonQuery();
    }

    // ── IFlowActionProposalStore ───────────────────────────────────────────────────

    public Task<string> InsertAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO proposed_flow_actions
                    (id, created_at, finding_id, title, rationale, risk_assessment, risk_tier,
                     action_type, parameters_json, state, operator_action_at, operator_rationale,
                     execution_result, expires_at, execution_duration_ms, execution_log)
                VALUES
                    ($id, $created, $finding, $title, $rationale, $risk, $tier,
                     $type, $params, $state, $opAt, $opRationale,
                     $result, $expires, $durationMs, $execLog)
            """;
            cmd.Parameters.AddWithValue("$id",          proposal.Id);
            cmd.Parameters.AddWithValue("$created",     proposal.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$finding",     proposal.FindingId);
            cmd.Parameters.AddWithValue("$title",       proposal.Title);
            cmd.Parameters.AddWithValue("$rationale",   proposal.Rationale);
            cmd.Parameters.AddWithValue("$risk",        proposal.RiskAssessment);
            cmd.Parameters.AddWithValue("$tier",        proposal.RiskTier.ToString());
            cmd.Parameters.AddWithValue("$type",        proposal.Type.ToString());
            cmd.Parameters.AddWithValue("$params",      JsonSerializer.Serialize(proposal.Parameters));
            cmd.Parameters.AddWithValue("$state",       proposal.State.ToString());
            cmd.Parameters.AddWithValue("$opAt",        (object?)proposal.OperatorActionAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$opRationale", (object?)proposal.OperatorRationale ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$result",      (object?)proposal.ExecutionResult ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expires",     (object?)proposal.ExpiresAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$durationMs",  (object?)proposal.ExecutionDurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$execLog",     (object?)proposal.ExecutionLog ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return Task.FromResult(proposal.Id);
    }

    public Task<IReadOnlyList<ProposedFlowAction>> ListPendingAsync(CancellationToken ct)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, created_at, finding_id, title, rationale, risk_assessment, risk_tier,
                       action_type, parameters_json, state, operator_action_at, operator_rationale,
                       execution_result, expires_at, execution_duration_ms, execution_log
                FROM proposed_flow_actions
                WHERE state = 'Pending'
                ORDER BY created_at ASC
                LIMIT 50
            """;
            var results = new List<ProposedFlowAction>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(HydrateRow(reader));
            return Task.FromResult<IReadOnlyList<ProposedFlowAction>>(results);
        }
    }

    public Task<ProposedFlowAction?> GetAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, created_at, finding_id, title, rationale, risk_assessment, risk_tier,
                       action_type, parameters_json, state, operator_action_at, operator_rationale,
                       execution_result, expires_at, execution_duration_ms, execution_log
                FROM proposed_flow_actions
                WHERE id = $id
                LIMIT 1
            """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Task.FromResult<ProposedFlowAction?>(null);
            return Task.FromResult<ProposedFlowAction?>(HydrateRow(reader));
        }
    }

    public Task<bool> UpdateStateAsync(
        string id,
        ProposedFlowActionState newState,
        string? operatorRationale,
        string? executionResult,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE proposed_flow_actions
                SET state              = $state,
                    operator_action_at = CASE WHEN $isOperatorAction = 1 THEN $now ELSE operator_action_at END,
                    operator_rationale = COALESCE($opRationale, operator_rationale),
                    execution_result   = COALESCE($result, execution_result)
                WHERE id = $id
            """;
            var isOperatorAction = newState is ProposedFlowActionState.Approved
                                              or ProposedFlowActionState.Rejected ? 1 : 0;
            cmd.Parameters.AddWithValue("$id",              id);
            cmd.Parameters.AddWithValue("$state",           newState.ToString());
            cmd.Parameters.AddWithValue("$now",             DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$isOperatorAction", isOperatorAction);
            cmd.Parameters.AddWithValue("$opRationale",     (object?)operatorRationale ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$result",          (object?)executionResult ?? DBNull.Value);
            var affected = cmd.ExecuteNonQuery();
            return Task.FromResult(affected > 0);
        }
    }

    public Task<int> MarkExpiredAsync(CancellationToken ct)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE proposed_flow_actions
                SET state = 'Expired'
                WHERE state = 'Pending'
                  AND expires_at IS NOT NULL
                  AND expires_at < $now
            """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return Task.FromResult(cmd.ExecuteNonQuery());
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static ProposedFlowAction HydrateRow(SqliteDataReader r)
    {
        Dictionary<string, object> parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(r.GetString(8))
                         ?? new Dictionary<string, object>();
        }
        catch { parameters = new Dictionary<string, object>(); }

        return new ProposedFlowAction
        {
            Id              = r.GetString(0),
            CreatedAt       = DateTime.Parse(r.GetString(1)).ToUniversalTime(),
            FindingId       = r.GetString(2),
            Title           = r.GetString(3),
            Rationale       = r.GetString(4),
            RiskAssessment  = r.GetString(5),
            RiskTier        = Enum.Parse<FlowActionRiskTier>(r.GetString(6)),
            Type            = Enum.Parse<FlowActionType>(r.GetString(7)),
            Parameters      = parameters,
            State           = Enum.Parse<ProposedFlowActionState>(r.GetString(9)),
            OperatorActionAt = r.IsDBNull(10) ? null : DateTime.Parse(r.GetString(10)).ToUniversalTime(),
            OperatorRationale = r.IsDBNull(11) ? null : r.GetString(11),
            ExecutionResult  = r.IsDBNull(12) ? null : r.GetString(12),
            ExpiresAt        = r.IsDBNull(13) ? null : DateTime.Parse(r.GetString(13)).ToUniversalTime(),
            ExecutionDurationMs = r.IsDBNull(14) ? null : r.GetInt32(14),
            ExecutionLog     = r.IsDBNull(15) ? null : r.GetString(15),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_dbLock)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
