using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// SQLite persistence for pipeline AI assessments, operator feedback, and prompt history.
/// Uses the same database file as <see cref="AgentStateStore"/> (via <see cref="FlowMonitorPersistence"/>
/// pattern) but with its own connection to avoid contention.
///
/// All writes are best-effort: we'd rather lose an assessment row than crash the service loop.
/// </summary>
public sealed class PipelineAssessmentStore : IDisposable
{
    private readonly AgentStateStore _stateStore;
    private readonly ILogger<PipelineAssessmentStore> _logger;
    private readonly object _dbLock = new();
    private SqliteConnection? _connection;
    private string? _connectedPath;
    private bool _disposed;

    public PipelineAssessmentStore(AgentStateStore stateStore, ILogger<PipelineAssessmentStore> logger)
    {
        _stateStore = stateStore;
        _logger = logger;
        EnsureConnection();
    }

    private SqliteConnection? EnsureConnection()
    {
        var currentPath = _stateStore.DatabasePath;
        if (string.IsNullOrEmpty(currentPath)) return null;
        lock (_dbLock)
        {
            if (_connection is not null && _connectedPath == currentPath) return _connection;
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
            CREATE TABLE IF NOT EXISTS pipeline_assessments (
                id                  TEXT PRIMARY KEY,
                assessed_at         DATETIME NOT NULL,
                kind                TEXT NOT NULL DEFAULT 'periodic',
                health_score        INTEGER NOT NULL,
                status              TEXT NOT NULL,
                summary             TEXT NOT NULL,
                issues_json         TEXT,
                recommendations_json TEXT,
                forward_look        TEXT,
                delta_json          TEXT,
                context_json        TEXT,
                raw_response        TEXT,
                model_tier          TEXT,
                prompt_hash         TEXT,
                token_count         INTEGER,
                grounding_pass_rate REAL,
                parse_status        TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_pa_assessed_at ON pipeline_assessments(assessed_at DESC);
            CREATE INDEX IF NOT EXISTS idx_pa_kind ON pipeline_assessments(kind);

            CREATE TABLE IF NOT EXISTS assessment_feedback (
                id              TEXT PRIMARY KEY,
                assessment_id   TEXT NOT NULL,
                issue_dedup_key TEXT NOT NULL,
                verdict         TEXT NOT NULL,
                operator_note   TEXT,
                created_at      DATETIME NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_af_assessment ON assessment_feedback(assessment_id);

            CREATE TABLE IF NOT EXISTS prompt_history (
                id          TEXT PRIMARY KEY,
                saved_at    DATETIME NOT NULL,
                prompt_text TEXT NOT NULL,
                prompt_hash TEXT NOT NULL,
                change_reason TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_ph_saved_at ON prompt_history(saved_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Assessments ──────────────────────────────────────────────────

    public bool InsertAssessment(PipelineAssessment assessment)
    {
        var conn = EnsureConnection();
        if (conn is null) return false;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO pipeline_assessments
                      (id, assessed_at, kind, health_score, status, summary,
                       issues_json, recommendations_json, forward_look, delta_json,
                       context_json, raw_response, model_tier, prompt_hash,
                       token_count, grounding_pass_rate, parse_status)
                    VALUES
                      ($id, $assessedAt, $kind, $healthScore, $status, $summary,
                       $issuesJson, $recommendationsJson, $forwardLook, $deltaJson,
                       $contextJson, $rawResponse, $modelTier, $promptHash,
                       $tokenCount, $groundingPassRate, $parseStatus);
                    """;
                cmd.Parameters.AddWithValue("$id", assessment.Id);
                cmd.Parameters.AddWithValue("$assessedAt", assessment.AssessedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$kind", assessment.Kind);
                cmd.Parameters.AddWithValue("$healthScore", assessment.HealthScore);
                cmd.Parameters.AddWithValue("$status", assessment.Status);
                cmd.Parameters.AddWithValue("$summary", assessment.Summary);
                cmd.Parameters.AddWithValue("$issuesJson", (object?)assessment.IssuesJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$recommendationsJson", (object?)assessment.RecommendationsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$forwardLook", (object?)assessment.ForwardLook ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$deltaJson", (object?)assessment.DeltaJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$contextJson", (object?)assessment.ContextJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rawResponse", (object?)assessment.RawResponse ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$modelTier", (object?)assessment.ModelTier ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$promptHash", (object?)assessment.PromptHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tokenCount", (object?)assessment.TokenCount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$groundingPassRate", (object?)assessment.GroundingPassRate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$parseStatus", (object?)assessment.ParseStatus ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertAssessment failed for {Id}", assessment.Id);
            return false;
        }
    }

    public PipelineAssessment? GetLatestAssessment(string kind = "periodic")
    {
        var conn = EnsureConnection();
        if (conn is null) return null;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, assessed_at, kind, health_score, status, summary,
                           issues_json, recommendations_json, forward_look, delta_json,
                           context_json, raw_response, model_tier, prompt_hash,
                           token_count, grounding_pass_rate, parse_status
                    FROM pipeline_assessments
                    WHERE kind = $kind
                    ORDER BY assessed_at DESC LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$kind", kind);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                return MapAssessment(reader);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetLatestAssessment failed");
            return null;
        }
    }

    public IReadOnlyList<PipelineAssessment> GetRecentAssessments(int limit = 10, string? kind = null)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<PipelineAssessment>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                var where = kind is not null ? "WHERE kind = $kind" : "";
                cmd.CommandText = $"""
                    SELECT id, assessed_at, kind, health_score, status, summary,
                           issues_json, recommendations_json, forward_look, delta_json,
                           context_json, raw_response, model_tier, prompt_hash,
                           token_count, grounding_pass_rate, parse_status
                    FROM pipeline_assessments
                    {where}
                    ORDER BY assessed_at DESC LIMIT $limit;
                    """;
                if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                var results = new List<PipelineAssessment>();
                while (reader.Read())
                    results.Add(MapAssessment(reader));
                return results;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecentAssessments failed");
            return Array.Empty<PipelineAssessment>();
        }
    }

    public int GetAssessmentCountToday()
    {
        var conn = EnsureConnection();
        if (conn is null) return 0;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT COUNT(*) FROM pipeline_assessments
                    WHERE assessed_at > $today;
                    """;
                cmd.Parameters.AddWithValue("$today", DateTime.UtcNow.Date.ToString("o"));
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAssessmentCountToday failed");
            return 0;
        }
    }

    // ── Feedback ─────────────────────────────────────────────────────

    public bool InsertFeedback(AssessmentFeedback feedback)
    {
        var conn = EnsureConnection();
        if (conn is null) return false;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO assessment_feedback
                      (id, assessment_id, issue_dedup_key, verdict, operator_note, created_at)
                    VALUES ($id, $assessmentId, $issueDedupKey, $verdict, $operatorNote, $createdAt);
                    """;
                cmd.Parameters.AddWithValue("$id", feedback.Id);
                cmd.Parameters.AddWithValue("$assessmentId", feedback.AssessmentId);
                cmd.Parameters.AddWithValue("$issueDedupKey", feedback.IssueDedupKey);
                cmd.Parameters.AddWithValue("$verdict", feedback.Verdict);
                cmd.Parameters.AddWithValue("$operatorNote", (object?)feedback.OperatorNote ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$createdAt", feedback.CreatedAt.UtcDateTime.ToString("o"));
                cmd.ExecuteNonQuery();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertFeedback failed for assessment {Id}", feedback.AssessmentId);
            return false;
        }
    }

    // ── Prompt History ───────────────────────────────────────────────

    public bool InsertPromptVersion(PromptHistoryEntry entry)
    {
        var conn = EnsureConnection();
        if (conn is null) return false;
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO prompt_history
                      (id, saved_at, prompt_text, prompt_hash, change_reason)
                    VALUES ($id, $savedAt, $promptText, $promptHash, $changeReason);
                    """;
                cmd.Parameters.AddWithValue("$id", entry.Id);
                cmd.Parameters.AddWithValue("$savedAt", entry.SavedAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$promptText", entry.PromptText);
                cmd.Parameters.AddWithValue("$promptHash", entry.PromptHash);
                cmd.Parameters.AddWithValue("$changeReason", (object?)entry.ChangeReason ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertPromptVersion failed");
            return false;
        }
    }

    public IReadOnlyList<PromptHistoryEntry> GetPromptHistory(int limit = 20)
    {
        var conn = EnsureConnection();
        if (conn is null) return Array.Empty<PromptHistoryEntry>();
        try
        {
            lock (_dbLock)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, saved_at, prompt_text, prompt_hash, change_reason
                    FROM prompt_history
                    ORDER BY saved_at DESC LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                var results = new List<PromptHistoryEntry>();
                while (reader.Read())
                {
                    results.Add(new PromptHistoryEntry
                    {
                        Id = reader.GetString(0),
                        SavedAt = DateTimeOffset.Parse(reader.GetString(1)),
                        PromptText = reader.GetString(2),
                        PromptHash = reader.GetString(3),
                        ChangeReason = reader.IsDBNull(4) ? null : reader.GetString(4),
                    });
                }
                return results;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetPromptHistory failed");
            return Array.Empty<PromptHistoryEntry>();
        }
    }

    // ── Retention ────────────────────────────────────────────────────

    public int PruneOlderThan(TimeSpan retention)
    {
        var conn = EnsureConnection();
        if (conn is null) return 0;
        var cutoff = DateTime.UtcNow.Subtract(retention).ToString("o");
        var total = 0;
        try
        {
            lock (_dbLock)
            {
                foreach (var (table, column) in new[]
                {
                    ("pipeline_assessments", "assessed_at"),
                    ("assessment_feedback", "created_at"),
                    ("prompt_history", "saved_at"),
                })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff;";
                    cmd.Parameters.AddWithValue("$cutoff", cutoff);
                    total += cmd.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PruneOlderThan failed");
        }
        return total;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PipelineAssessment MapAssessment(SqliteDataReader reader)
    {
        return new PipelineAssessment
        {
            Id = reader.GetString(0),
            AssessedAt = DateTimeOffset.Parse(reader.GetString(1)),
            Kind = reader.GetString(2),
            HealthScore = reader.GetInt32(3),
            Status = reader.GetString(4),
            Summary = reader.GetString(5),
            IssuesJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            RecommendationsJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            ForwardLook = reader.IsDBNull(8) ? null : reader.GetString(8),
            DeltaJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            ContextJson = reader.IsDBNull(10) ? null : reader.GetString(10),
            RawResponse = reader.IsDBNull(11) ? null : reader.GetString(11),
            ModelTier = reader.IsDBNull(12) ? null : reader.GetString(12),
            PromptHash = reader.IsDBNull(13) ? null : reader.GetString(13),
            TokenCount = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            GroundingPassRate = reader.IsDBNull(15) ? null : reader.GetDouble(15),
            ParseStatus = reader.IsDBNull(16) ? null : reader.GetString(16),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_dbLock)
        {
            try { _connection?.Dispose(); } catch { /* best-effort */ }
            _connection = null;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────

/// <summary>Persisted pipeline assessment record.</summary>
public sealed record PipelineAssessment
{
    public required string Id { get; init; }
    public required DateTimeOffset AssessedAt { get; init; }
    /// <summary>"periodic" or "on_demand"</summary>
    public required string Kind { get; init; }
    /// <summary>1-10 health score from the AI.</summary>
    public required int HealthScore { get; init; }
    /// <summary>"healthy", "warning", "critical", or "inconclusive"</summary>
    public required string Status { get; init; }
    public required string Summary { get; init; }
    public string? IssuesJson { get; init; }
    public string? RecommendationsJson { get; init; }
    public string? ForwardLook { get; init; }
    public string? DeltaJson { get; init; }
    /// <summary>Full snapshot sent to the LLM — "what the AI saw" transparency.</summary>
    public string? ContextJson { get; init; }
    /// <summary>Full LLM response for debugging.</summary>
    public string? RawResponse { get; init; }
    public string? ModelTier { get; init; }
    /// <summary>SHA256 prefix of the prompt template used.</summary>
    public string? PromptHash { get; init; }
    public int? TokenCount { get; init; }
    /// <summary>Percentage of AI issues that passed grounding (0.0-1.0).</summary>
    public double? GroundingPassRate { get; init; }
    /// <summary>"success", "partial", "failed", "inconclusive"</summary>
    public string? ParseStatus { get; init; }
}

/// <summary>Operator feedback on a specific issue within an assessment.</summary>
public sealed record AssessmentFeedback
{
    public required string Id { get; init; }
    public required string AssessmentId { get; init; }
    public required string IssueDedupKey { get; init; }
    /// <summary>"accurate", "false_alarm", "already_known", "wrong_target"</summary>
    public required string Verdict { get; init; }
    public string? OperatorNote { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Versioned prompt history entry for diff/rollback.</summary>
public sealed record PromptHistoryEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset SavedAt { get; init; }
    public required string PromptText { get; init; }
    /// <summary>SHA256 hash of prompt text for dedup.</summary>
    public required string PromptHash { get; init; }
    public string? ChangeReason { get; init; }
}
