using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Metrics;

/// <summary>
/// Centralized metrics recording for build and test events across all agents.
/// Provides a clean API that agents call at key decision points.
/// Data is persisted to SQLite via AgentStateStore and queried by the dashboard.
/// </summary>
public class BuildTestMetrics
{
    private readonly AgentStateStore _store;

    // ── Metric Name Constants ───────────────────────────────────────
    // Build metrics
    public const string BuildAttempts = "build.attempts";
    public const string BuildSuccesses = "build.successes";
    public const string BuildFailures = "build.failures";
    public const string BuildFixAttempts = "build.fix_attempts";
    public const string BuildRegenerations = "build.regenerations";
    public const string BuildRegenerationSuccesses = "build.regeneration_successes";
    public const string BuildBlockedCommits = "build.blocked_commits";

    // Test metrics
    public const string TestRunsTotal = "test.runs_total";
    public const string TestRunsPassed = "test.runs_passed";
    public const string TestRunsFailed = "test.runs_failed";
    public const string TestFixAttempts = "test.fix_attempts";
    public const string TestMaxRetriesReached = "test.max_retries_reached";
    public const string TestsRemoved = "tests.removed";
    public const string TestRemovalPasses = "tests.removal_passes";

    // Rework metrics
    public const string ReworkRequested = "rework.requested";
    public const string ReworkCompleted = "rework.completed";
    public const string ReworkBuildBlocked = "rework.build_blocked";

    // Commit metrics
    public const string CommitsSuccessful = "commits.successful";
    public const string CommitsBlocked = "commits.blocked";
    public const string ApiOnlyCommits = "commits.api_only_no_build_validation";

    public BuildTestMetrics(AgentStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    // ── Build Events ────────────────────────────────────────────────

    /// <summary>Record a build attempt (initial or retry).</summary>
    public Task RecordBuildAttemptAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildAttempts, 1, ct);

    /// <summary>Record a successful build.</summary>
    public Task RecordBuildSuccessAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildSuccesses, 1, ct);

    /// <summary>Record a build failure (after all retries exhausted for one pass).</summary>
    public Task RecordBuildFailureAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildFailures, 1, ct);

    /// <summary>Record an AI fix attempt for build errors.</summary>
    public Task RecordBuildFixAttemptAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildFixAttempts, 1, ct);

    /// <summary>Record a full code regeneration attempt after build fix loop failed.</summary>
    public Task RecordBuildRegenerationAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildRegenerations, 1, ct);

    /// <summary>Record that a code regeneration succeeded.</summary>
    public Task RecordBuildRegenerationSuccessAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildRegenerationSuccesses, 1, ct);

    /// <summary>Record that a commit was blocked because the build could not be fixed.</summary>
    public Task RecordBuildBlockedCommitAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, BuildBlockedCommits, 1, ct);

    // ── Test Events ─────────────────────────────────────────────────

    /// <summary>Record a test run (total count increment).</summary>
    public Task RecordTestRunAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, TestRunsTotal, 1, ct);

    /// <summary>Record a test run with pass/fail tracking.</summary>
    public Task RecordTestRunAsync(string agentId, bool passed, CancellationToken ct = default)
    {
        var tasks = new List<Task>
        {
            _store.RecordMetricAsync(agentId, TestRunsTotal, 1, ct),
            _store.RecordMetricAsync(agentId, passed ? TestRunsPassed : TestRunsFailed, 1, ct)
        };
        return Task.WhenAll(tasks);
    }

    /// <summary>Record an AI fix attempt for test failures.</summary>
    public Task RecordTestFixAttemptAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, TestFixAttempts, 1, ct);

    /// <summary>Record that max test fix retries were reached for a step.</summary>
    public Task RecordTestMaxRetriesReachedAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, TestMaxRetriesReached, 1, ct);

    /// <summary>Record that unfixable tests were removed (count = number of tests removed).</summary>
    public Task RecordTestsRemovedAsync(string agentId, int count, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, TestsRemoved, count, ct);

    /// <summary>Record a test removal pass (AI asked to remove failing tests).</summary>
    public Task RecordTestRemovalPassAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, TestRemovalPasses, 1, ct);

    // ── Rework Events ───────────────────────────────────────────────

    /// <summary>Record that a rework was requested by a reviewer.</summary>
    public Task RecordReworkRequestedAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, ReworkRequested, 1, ct);

    /// <summary>Record that a rework was successfully completed and committed.</summary>
    public Task RecordReworkCompletedAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, ReworkCompleted, 1, ct);

    /// <summary>Record that a rework was blocked by build errors.</summary>
    public Task RecordReworkBuildBlockedAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, ReworkBuildBlocked, 1, ct);

    // ── Commit Events ───────────────────────────────────────────────

    /// <summary>Record a successful commit (code passed build + test gates).</summary>
    public Task RecordSuccessfulCommitAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, CommitsSuccessful, 1, ct);

    /// <summary>Record a commit that was blocked by the build gate.</summary>
    public Task RecordBlockedCommitAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, CommitsBlocked, 1, ct);

    /// <summary>Record a commit via API-only mode (no local build/test validation).</summary>
    public Task RecordApiOnlyCommitAsync(string agentId, CancellationToken ct = default)
        => _store.RecordMetricAsync(agentId, ApiOnlyCommits, 1, ct);

    // ── Query Helpers ───────────────────────────────────────────────

    /// <summary>Get all aggregate metrics since a given time.</summary>
    public Task<Dictionary<string, double>> GetAggregatesAsync(
        DateTime since, CancellationToken ct = default)
        => _store.GetAggregateMetricsAsync(since, ct);

    /// <summary>Get per-agent breakdown for a specific metric.</summary>
    public Task<Dictionary<string, double>> GetByAgentAsync(
        string metricName, DateTime since, CancellationToken ct = default)
        => _store.GetMetricsByAgentAsync(metricName, since, ct);
}
