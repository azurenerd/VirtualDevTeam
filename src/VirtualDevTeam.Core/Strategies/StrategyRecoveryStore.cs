using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Persists and retrieves strategy recovery checkpoints so that completed-but-unjudged
/// candidates can be resumed after a runner restart without re-executing from scratch.
///
/// Checkpoints are written at two milestones in <see cref="StrategyOrchestrator.RunCandidatesAsync"/>:
/// <list type="number">
///   <item><c>ExecutionDone</c> — all candidates finished running, patches extracted</item>
///   <item><c>WinnerSelected</c> — evaluation complete, winner picked but not yet applied by the SE</item>
/// </list>
///
/// On recovery, the orchestrator reads the checkpoint and resumes from the appropriate phase
/// instead of re-running all candidates from scratch.
/// </summary>
public sealed class StrategyRecoveryStore
{
    private readonly string _dbPath;
    private readonly ILogger<StrategyRecoveryStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public StrategyRecoveryStore(ILogger<StrategyRecoveryStore> logger, string? dbPath = null)
    {
        _logger = logger;
        _dbPath = dbPath ?? Path.Combine(Directory.GetCurrentDirectory(), "virtualdevteam_strategy_recovery.db");
        EnsureTable();
    }

    private void EnsureTable()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS strategy_checkpoints (
                    task_id TEXT NOT NULL,
                    run_id TEXT NOT NULL,
                    base_sha TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    winner_strategy_id TEXT,
                    candidates_json TEXT NOT NULL,
                    task_context_json TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY (task_id, run_id)
                )
            """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to create table (non-fatal)");
        }
    }

    /// <summary>
    /// Saves a checkpoint after all candidates have completed execution.
    /// </summary>
    public void SaveExecutionDone(
        string taskId,
        string runId,
        string baseSha,
        TaskContextSnapshot taskContext,
        IReadOnlyList<CandidateCheckpoint> candidates)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO strategy_checkpoints
                    (task_id, run_id, base_sha, phase, winner_strategy_id, candidates_json, task_context_json, created_at)
                VALUES
                    ($taskId, $runId, $baseSha, 'ExecutionDone', NULL, $candidates, $taskContext, datetime('now'))
            """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$baseSha", baseSha);
            cmd.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(candidates, JsonOpts));
            cmd.Parameters.AddWithValue("$taskContext", JsonSerializer.Serialize(taskContext, JsonOpts));
            cmd.ExecuteNonQuery();
            _logger.LogInformation(
                "Strategy recovery checkpoint saved: {Phase} for task {TaskId} run {RunId} ({Count} candidates)",
                "ExecutionDone", taskId, runId, candidates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to save ExecutionDone checkpoint (non-fatal)");
        }
    }

    /// <summary>
    /// Updates the checkpoint after evaluation completes with the winner ID.
    /// </summary>
    public void SaveWinnerSelected(string taskId, string runId, string winnerStrategyId)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE strategy_checkpoints
                SET phase = 'WinnerSelected', winner_strategy_id = $winner
                WHERE task_id = $taskId AND run_id = $runId
            """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$winner", winnerStrategyId);
            cmd.ExecuteNonQuery();
            _logger.LogInformation(
                "Strategy recovery checkpoint updated: WinnerSelected={Winner} for task {TaskId}",
                winnerStrategyId, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to save WinnerSelected checkpoint (non-fatal)");
        }
    }

    /// <summary>
    /// Marks the checkpoint as applied (winner patch committed to PR) so it's no longer recoverable.
    /// </summary>
    public void MarkApplied(string taskId, string runId)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM strategy_checkpoints WHERE task_id = $taskId AND run_id = $runId";
            cmd.Parameters.AddWithValue("$taskId", taskId);
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to mark applied (non-fatal)");
        }
    }

    /// <summary>
    /// Retrieves a recovery checkpoint for the given task, if one exists.
    /// Returns null if no checkpoint or if the checkpoint is stale (different baseSha).
    /// </summary>
    public RecoveryCheckpoint? GetCheckpoint(string taskId, string currentBaseSha)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, base_sha, phase, winner_strategy_id, candidates_json, task_context_json, created_at
                FROM strategy_checkpoints
                WHERE task_id = $taskId
                ORDER BY created_at DESC
                LIMIT 1
            """;
            cmd.Parameters.AddWithValue("$taskId", taskId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var baseSha = reader.GetString(1);
            if (!string.Equals(baseSha, currentBaseSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Strategy recovery checkpoint for task {TaskId} has stale baseSha ({Stored} vs {Current}) — discarding",
                    taskId, baseSha, currentBaseSha);
                // Clean up stale checkpoint
                MarkApplied(taskId, reader.GetString(0));
                return null;
            }

            return new RecoveryCheckpoint
            {
                TaskId = taskId,
                RunId = reader.GetString(0),
                BaseSha = baseSha,
                Phase = reader.GetString(2),
                WinnerStrategyId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Candidates = JsonSerializer.Deserialize<List<CandidateCheckpoint>>(reader.GetString(4), JsonOpts)
                    ?? new List<CandidateCheckpoint>(),
                TaskContext = JsonSerializer.Deserialize<TaskContextSnapshot>(reader.GetString(5), JsonOpts),
                CreatedAt = reader.GetString(6),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to read checkpoint for task {TaskId} (non-fatal)", taskId);
            return null;
        }
    }

    /// <summary>Removes all checkpoints (e.g., during reset).</summary>
    public void Clear()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM strategy_checkpoints";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyRecoveryStore: failed to clear (non-fatal)");
        }
    }
}

/// <summary>What phase the orchestration reached before the runner stopped.</summary>
public record RecoveryCheckpoint
{
    public required string TaskId { get; init; }
    public required string RunId { get; init; }
    public required string BaseSha { get; init; }
    /// <summary>"ExecutionDone" or "WinnerSelected"</summary>
    public required string Phase { get; init; }
    public string? WinnerStrategyId { get; init; }
    public required IReadOnlyList<CandidateCheckpoint> Candidates { get; init; }
    public TaskContextSnapshot? TaskContext { get; init; }
    public string? CreatedAt { get; init; }
}

/// <summary>Persisted candidate state — enough to reconstruct StrategyExecutionResult for evaluation.</summary>
public record CandidateCheckpoint
{
    public required string StrategyId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public bool NoOpAcknowledged { get; init; }
    public double ElapsedSeconds { get; init; }
    public long? TokensUsed { get; init; }
    /// <summary>The unified diff patch against BaseSha. Persisted at checkpoint time so we don't depend on worktree survival.</summary>
    public required string Patch { get; init; }
    /// <summary>Media capture pipeline steps — persisted so the timeline renders on finished strategy cards after restart.</summary>
    public IReadOnlyList<MediaCapture.MediaCaptureStep>? MediaCaptureSteps { get; init; }
}

/// <summary>Minimal task context snapshot for recovery — enough to reconstruct TaskContext.</summary>
public record TaskContextSnapshot
{
    public required string TaskId { get; init; }
    public required string RunId { get; init; }
    public required string TaskTitle { get; init; }
    public string TaskDescription { get; init; } = "";
    public required string BaseSha { get; init; }
    public string? AgentRepoPath { get; init; }
}
