using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Per-run experiment record writer. One ndjson file per runId; one JSON object per task.
/// Append-only; safe for concurrent tasks via a per-file lock.
/// </summary>
public class ExperimentTracker
{
    private readonly ILogger<ExperimentTracker> _logger;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly object _lock = new();

    public ExperimentTracker(ILogger<ExperimentTracker> logger, IOptionsMonitor<StrategyFrameworkConfig> cfg)
    { _logger = logger; _cfg = cfg; }

    public string ResolveDirectory()
    {
        var dir = _cfg.CurrentValue.ExperimentDataDirectory;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ResolveFile(string runId)
        => Path.Combine(ResolveDirectory(), $"{SafeRunId(runId)}.ndjson");

    public void Write(ExperimentRecord record)
    {
        var path = ResolveFile(record.RunId);
        var json = JsonSerializer.Serialize(record, ExperimentJson.Options);
        lock (_lock)
        {
            File.AppendAllText(path, json + Environment.NewLine);
        }
        _logger.LogInformation("Wrote experiment record: run={Run} task={Task} winner={Winner}",
            record.RunId, record.TaskId, record.WinnerStrategyId ?? "<none>");
    }

    private static string SafeRunId(string id)
    {
        var s = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(s) ? "run" : s;
    }
}

public static class ExperimentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

/// <summary>One ndjson row per task.</summary>
public record ExperimentRecord
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required IReadOnlyList<CandidateRecord> Candidates { get; init; }
    public string? WinnerStrategyId { get; init; }
    public string? TieBreakReason { get; init; }
    public double EvaluationElapsedSec { get; init; }
    public long TotalTokens { get; init; }
}

public record CandidateRecord
{
    public required string StrategyId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public string? FailedGate { get; init; }
    public double ElapsedSec { get; init; }
    public int PatchSizeBytes { get; init; }
    public long? TokensUsed { get; init; }
    public int? AcceptanceCriteriaScore { get; init; }
    public int? DesignScore { get; init; }
    public int? ReadabilityScore { get; init; }

    // ── Framework metadata (populated for external adapters) ──

    /// <summary>Framework adapter ID — same as StrategyId for built-in, distinct for external.</summary>
    public string? FrameworkId { get; init; }

    /// <summary>Number of sub-agents spawned (e.g., Squad team members).</summary>
    public int? SubAgentsSpawned { get; init; }

    /// <summary>Number of LLM calls made by the framework.</summary>
    public int? LlmCallsMade { get; init; }

    /// <summary>Number of decisions captured from framework output.</summary>
    public int? DecisionsMade { get; init; }

    /// <summary>Whether this candidate came from an external framework vs built-in strategy.</summary>
    public bool IsExternalFramework { get; init; }
}
