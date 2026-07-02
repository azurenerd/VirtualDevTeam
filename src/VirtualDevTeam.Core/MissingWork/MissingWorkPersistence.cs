using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.MissingWork;

/// <summary>
/// SQLite persistence for MissingWork findings + proposed-issue lifecycle. Uses the
/// same database file as <see cref="AgentStateStore"/> and <see cref="VirtualDevTeam.Core.HealthMonitor.FlowMonitorPersistence"/>
/// (one file per project) but with its own connection to avoid lock contention.
///
/// <para>
/// Schema:
/// <list type="bullet">
///   <item><c>missing_work_findings</c> — every finding emitted by detectors. Dedup by
///         <c>dedup_key</c> within a configurable suppression window.</item>
///   <item><c>proposed_issues</c> — planner output paired with each finding. Operator
///         decisions (approve/edit/reject) mutate state in place.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MissingWorkPersistence : IDisposable
{
    private readonly AgentStateStore _stateStore;
    private readonly ILogger<MissingWorkPersistence> _logger;
    private readonly object _dbLock = new();
    private SqliteConnection? _connection;
    private string? _connectedPath;
    private bool _disposed;

    public MissingWorkPersistence(AgentStateStore stateStore, ILogger<MissingWorkPersistence> logger)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            CREATE TABLE IF NOT EXISTS missing_work_findings (
                id              TEXT PRIMARY KEY,
                detected_at     DATETIME NOT NULL,
                detector_id     TEXT NOT NULL,
                pattern         TEXT NOT NULL,
                summary         TEXT NOT NULL,
                confidence      REAL NOT NULL,
                dedup_key       TEXT NOT NULL,
                evidence_json   TEXT NOT NULL,
                state           TEXT NOT NULL DEFAULT 'Open',
                resolved_at     DATETIME,
                resolution      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_mw_findings_dedup ON missing_work_findings(dedup_key);
            CREATE INDEX IF NOT EXISTS idx_mw_findings_detected_at ON missing_work_findings(detected_at DESC);
            CREATE INDEX IF NOT EXISTS idx_mw_findings_state ON missing_work_findings(state);

            CREATE TABLE IF NOT EXISTS proposed_issues (
                id                      TEXT PRIMARY KEY,
                finding_id              TEXT NOT NULL,
                detector_id             TEXT NOT NULL,
                state                   TEXT NOT NULL,
                proposed_title          TEXT NOT NULL,
                proposed_body           TEXT NOT NULL,
                proposed_labels         TEXT NOT NULL,
                proposed_depends_on     TEXT,
                proposed_blocks         TEXT,
                confidence              REAL NOT NULL,
                evidence_json           TEXT NOT NULL,
                created_at              DATETIME NOT NULL,
                operator_action_at      DATETIME,
                operator_action         TEXT,
                operator_rationale      TEXT,
                final_title             TEXT,
                final_body              TEXT,
                final_labels            TEXT,
                created_issue_number    INTEGER,
                FOREIGN KEY (finding_id) REFERENCES missing_work_findings(id)
            );
            CREATE INDEX IF NOT EXISTS idx_proposed_issues_state ON proposed_issues(state);
            CREATE INDEX IF NOT EXISTS idx_proposed_issues_finding ON proposed_issues(finding_id);
        """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert a new finding if no Open finding with the same dedup_key exists within the
    /// suppression window. Returns true if inserted (caller may then invoke the planner),
    /// false if suppressed.
    /// </summary>
    public bool InsertFinding(MissingWorkFinding finding, TimeSpan dedupWindow)
    {
        ArgumentNullException.ThrowIfNull(finding);
        lock (_dbLock)
        {
            var conn = GetConnection();
            var cutoff = DateTime.UtcNow - dedupWindow;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = """
                    SELECT 1 FROM missing_work_findings
                    WHERE dedup_key = $key AND state = 'Open' AND detected_at > $cutoff
                    LIMIT 1
                """;
                probe.Parameters.AddWithValue("$key", finding.DedupKey);
                probe.Parameters.AddWithValue("$cutoff", cutoff.ToString("o"));
                using var reader = probe.ExecuteReader();
                if (reader.Read()) return false;
            }
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO missing_work_findings
                    (id, detected_at, detector_id, pattern, summary, confidence, dedup_key, evidence_json, state)
                VALUES
                    ($id, $detected, $detector, $pattern, $summary, $conf, $dedup, $evidence, 'Open')
            """;
            insert.Parameters.AddWithValue("$id", finding.Id);
            insert.Parameters.AddWithValue("$detected", finding.DetectedAt.ToString("o"));
            insert.Parameters.AddWithValue("$detector", finding.DetectorId);
            insert.Parameters.AddWithValue("$pattern", finding.Pattern);
            insert.Parameters.AddWithValue("$summary", finding.Summary);
            insert.Parameters.AddWithValue("$conf", finding.Confidence);
            insert.Parameters.AddWithValue("$dedup", finding.DedupKey);
            insert.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(finding.Evidence));
            insert.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>List findings in Open state, newest first, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<MissingWorkFinding> ListOpenFindings(int limit = 100)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, detected_at, detector_id, pattern, summary, confidence, dedup_key, evidence_json
                FROM missing_work_findings
                WHERE state = 'Open'
                ORDER BY detected_at DESC
                LIMIT $limit
            """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<MissingWorkFinding>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                IReadOnlyList<EvidenceCitation> evidence;
                try
                {
                    evidence = JsonSerializer.Deserialize<List<EvidenceCitation>>(reader.GetString(7))
                        ?? new List<EvidenceCitation>();
                }
                catch { evidence = new List<EvidenceCitation>(); }

                results.Add(new MissingWorkFinding
                {
                    Id = reader.GetString(0),
                    DetectedAt = DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                    DetectorId = reader.GetString(2),
                    Pattern = reader.GetString(3),
                    Summary = reader.GetString(4),
                    Confidence = reader.GetDouble(5),
                    DedupKey = reader.GetString(6),
                    Evidence = evidence,
                });
            }
            return results;
        }
    }

    /// <summary>Mark a finding as resolved (typically after the linked proposed-issue is created or rejected).</summary>
    public bool MarkResolved(string findingId, string resolution)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE missing_work_findings
                SET state = 'Resolved', resolved_at = $at, resolution = $res
                WHERE id = $id AND state = 'Open'
            """;
            cmd.Parameters.AddWithValue("$id", findingId);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$res", resolution);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Insert a proposed issue paired with an existing finding.</summary>
    public void InsertProposedIssue(ProposedIssue proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO proposed_issues
                    (id, finding_id, detector_id, state, proposed_title, proposed_body, proposed_labels,
                     proposed_depends_on, proposed_blocks, confidence, evidence_json, created_at)
                VALUES
                    ($id, $finding, $detector, $state, $title, $body, $labels,
                     $depends, $blocks, $conf, $evidence, $created)
            """;
            cmd.Parameters.AddWithValue("$id", proposal.Id);
            cmd.Parameters.AddWithValue("$finding", proposal.FindingId);
            cmd.Parameters.AddWithValue("$detector", proposal.DetectorId);
            cmd.Parameters.AddWithValue("$state", proposal.State.ToString());
            cmd.Parameters.AddWithValue("$title", proposal.ProposedTitle);
            cmd.Parameters.AddWithValue("$body", proposal.ProposedBody);
            cmd.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(proposal.ProposedLabels));
            cmd.Parameters.AddWithValue("$depends", JsonSerializer.Serialize(proposal.ProposedDependsOn));
            cmd.Parameters.AddWithValue("$blocks", JsonSerializer.Serialize(proposal.ProposedBlocks));
            cmd.Parameters.AddWithValue("$conf", proposal.Confidence);
            cmd.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(proposal.Evidence));
            cmd.Parameters.AddWithValue("$created", proposal.CreatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>List pending proposed issues for the Approvals page.</summary>
    public IReadOnlyList<ProposedIssue> ListPendingProposals(int limit = 50)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, finding_id, detector_id, state, proposed_title, proposed_body, proposed_labels,
                       confidence, evidence_json, created_at
                FROM proposed_issues
                WHERE state = 'Pending'
                ORDER BY created_at DESC
                LIMIT $limit
            """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<ProposedIssue>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                IReadOnlyList<string> labels;
                IReadOnlyList<EvidenceCitation> evidence;
                try { labels = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? new List<string>(); }
                catch { labels = new List<string>(); }
                try { evidence = JsonSerializer.Deserialize<List<EvidenceCitation>>(reader.GetString(8)) ?? new List<EvidenceCitation>(); }
                catch { evidence = new List<EvidenceCitation>(); }

                results.Add(new ProposedIssue
                {
                    Id = reader.GetString(0),
                    FindingId = reader.GetString(1),
                    DetectorId = reader.GetString(2),
                    State = Enum.Parse<ProposedIssueState>(reader.GetString(3)),
                    ProposedTitle = reader.GetString(4),
                    ProposedBody = reader.GetString(5),
                    ProposedLabels = labels,
                    Confidence = reader.GetDouble(7),
                    Evidence = evidence,
                    CreatedAt = DateTime.Parse(reader.GetString(9)).ToUniversalTime(),
                });
            }
            return results;
        }
    }

    /// <summary>Update a proposal's state after operator action.</summary>
    public bool UpdateProposalState(
        string proposalId,
        ProposedIssueState newState,
        OperatorAction? action,
        string? rationale,
        string? finalTitle,
        string? finalBody,
        IReadOnlyList<string>? finalLabels,
        int? createdIssueNumber)
    {
        lock (_dbLock)
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE proposed_issues
                SET state = $state,
                    operator_action_at = $at,
                    operator_action = $action,
                    operator_rationale = $rationale,
                    final_title = $ftitle,
                    final_body = $fbody,
                    final_labels = $flabels,
                    created_issue_number = $issueNum
                WHERE id = $id
            """;
            cmd.Parameters.AddWithValue("$id", proposalId);
            cmd.Parameters.AddWithValue("$state", newState.ToString());
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$action", (object?)action?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rationale", (object?)rationale ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ftitle", (object?)finalTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fbody", (object?)finalBody ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$flabels",
                finalLabels is null ? (object)DBNull.Value : JsonSerializer.Serialize(finalLabels));
            cmd.Parameters.AddWithValue("$issueNum", (object?)createdIssueNumber ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
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
