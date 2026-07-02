using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Lifecycle;

/// <summary>
/// States in the engineering task lifecycle. Tasks progress through these states
/// as agents claim, implement, test, review, and merge work.
/// </summary>
public enum TaskState
{
    Pending,
    Claimed,
    InProgress,
    PROpen,
    TestsAdded,
    UnderReview,
    Approved,
    Merging,
    Merged,
    Done,
    Blocked,
    Failed,
}

/// <summary>
/// Result of a state transition attempt.
/// </summary>
public enum TransitionStatus
{
    /// <summary>Transition succeeded.</summary>
    Succeeded,
    /// <summary>Transition is not legal from the current state.</summary>
    IllegalTransition,
    /// <summary>Current state did not match expected (concurrent modification).</summary>
    Conflict,
    /// <summary>Task is already in the requested state (idempotent no-op).</summary>
    NoOp,
}

/// <summary>
/// Rich result from <see cref="TaskStateMachine.TryTransitionAsync"/>.
/// </summary>
public record TaskTransitionResult(
    TransitionStatus Status,
    TaskState PreviousState,
    TaskState CurrentState,
    string? Message = null);

/// <summary>
/// Identity of a task being tracked. Uses run scope + issue number for uniqueness.
/// </summary>
public record TaskIdentity(string RunScope, int IssueNumber);

/// <summary>
/// A single transition in the task lifecycle log.
/// </summary>
public record TaskStateTransition(
    string RunScope,
    int IssueNumber,
    TaskState FromState,
    TaskState ToState,
    string AgentId,
    string? AgentRole,
    string? Reason,
    DateTime Timestamp);

/// <summary>
/// Raised when a task transitions to a new state. Consumers should treat this
/// as a wake-up signal and read current state from the store for truth.
/// </summary>
public record TaskStateChangedEvent(
    TaskIdentity Task,
    TaskState FromState,
    TaskState ToState,
    string AgentId,
    int? PrNumber);

/// <summary>
/// Centralized state machine for engineering task lifecycle.
/// Provides compare-and-set atomic transitions, SQLite persistence,
/// and events for downstream consumers.
///
/// This is the single source of truth for task state. Labels and comments
/// are projections of this state, not the reverse.
///
/// Thread-safe: all transitions use SQLite compare-and-set (UPDATE WHERE current_state = expected).
/// </summary>
public class TaskStateMachine
{
    private readonly AgentStateStore _store;
    private readonly ILogger<TaskStateMachine> _logger;
    private bool _initialized;
    private readonly object _initLock = new();

    /// <summary>
    /// Raised after a successful state transition. Published outside the DB lock
    /// so consumers cannot block persistence. Treat as notification, not durability.
    /// </summary>
    public event Action<TaskStateChangedEvent>? OnStateChanged;

    // Legal transitions: from → set of allowed to-states
    private static readonly Dictionary<TaskState, HashSet<TaskState>> s_transitions = new()
    {
        [TaskState.Pending] = [TaskState.Claimed, TaskState.Blocked, TaskState.InProgress],
        [TaskState.Claimed] = [TaskState.InProgress, TaskState.Pending],
        [TaskState.InProgress] = [TaskState.PROpen, TaskState.Blocked, TaskState.Failed, TaskState.Pending],
        [TaskState.PROpen] = [TaskState.TestsAdded, TaskState.UnderReview, TaskState.Blocked, TaskState.InProgress],
        [TaskState.TestsAdded] = [TaskState.UnderReview, TaskState.PROpen],
        [TaskState.UnderReview] = [TaskState.Approved, TaskState.PROpen],
        [TaskState.Approved] = [TaskState.Merging, TaskState.PROpen],
        [TaskState.Merging] = [TaskState.Merged, TaskState.PROpen],
        [TaskState.Merged] = [TaskState.Done],
        [TaskState.Done] = [],
        [TaskState.Blocked] = [TaskState.Pending, TaskState.InProgress, TaskState.Claimed],
        [TaskState.Failed] = [TaskState.Pending],
    };

    public TaskStateMachine(AgentStateStore store, ILogger<TaskStateMachine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Ensure task_state and task_state_log tables exist.
    /// Called lazily on first use to avoid schema issues at startup.
    /// </summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _store.ExecuteNonQuery("""
                CREATE TABLE IF NOT EXISTS task_state (
                    run_scope    TEXT NOT NULL,
                    issue_number INTEGER NOT NULL,
                    current_state TEXT NOT NULL DEFAULT 'Pending',
                    last_agent_id TEXT,
                    last_agent_role TEXT,
                    pr_number    INTEGER,
                    updated_at   DATETIME NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY (run_scope, issue_number)
                );

                CREATE TABLE IF NOT EXISTS task_state_log (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_scope    TEXT NOT NULL,
                    issue_number INTEGER NOT NULL,
                    from_state   TEXT NOT NULL,
                    to_state     TEXT NOT NULL,
                    agent_id     TEXT NOT NULL,
                    agent_role   TEXT,
                    reason       TEXT,
                    timestamp    DATETIME NOT NULL DEFAULT (datetime('now'))
                );

                CREATE INDEX IF NOT EXISTS idx_task_state_log_task
                    ON task_state_log (run_scope, issue_number, timestamp DESC);
            """);
            _initialized = true;
        }
    }

    /// <summary>
    /// Attempt to transition a task to a new state using compare-and-set semantics.
    /// </summary>
    /// <param name="task">Task identity (run scope + issue number).</param>
    /// <param name="newState">Desired target state.</param>
    /// <param name="agentId">ID of the agent requesting the transition.</param>
    /// <param name="agentRole">Role of the agent (for audit).</param>
    /// <param name="reason">Optional human-readable reason.</param>
    /// <param name="prNumber">Optional PR number to associate with this task.</param>
    public Task<TaskTransitionResult> TryTransitionAsync(
        TaskIdentity task,
        TaskState newState,
        string agentId,
        string? agentRole = null,
        string? reason = null,
        int? prNumber = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureInitialized();

        var currentState = GetState(task);

        // Idempotent: same-state → NoOp
        if (currentState == newState)
        {
            return Task.FromResult(new TaskTransitionResult(
                TransitionStatus.NoOp, currentState, currentState,
                $"Task is already in {newState}"));
        }

        // Validate transition legality
        if (!s_transitions.TryGetValue(currentState, out var allowed) || !allowed.Contains(newState))
        {
            _logger.LogWarning(
                "Illegal task transition: {RunScope}/#{Issue} {From} → {To} by {Agent} ({Reason})",
                task.RunScope, task.IssueNumber, currentState, newState, agentId, reason);
            return Task.FromResult(new TaskTransitionResult(
                TransitionStatus.IllegalTransition, currentState, currentState,
                $"Cannot transition from {currentState} to {newState}"));
        }

        // Compare-and-set: only update if current_state matches what we read
        var rowsAffected = _store.ExecuteNonQuery(
            """
            UPDATE task_state
            SET current_state = @to, last_agent_id = @agent, last_agent_role = @role,
                pr_number = COALESCE(@pr, pr_number), updated_at = datetime('now')
            WHERE run_scope = @scope AND issue_number = @issue AND current_state = @from
            """,
            ("@to", newState.ToString()),
            ("@agent", agentId),
            ("@role", (object?)agentRole ?? DBNull.Value),
            ("@pr", prNumber.HasValue ? prNumber.Value : DBNull.Value),
            ("@scope", task.RunScope),
            ("@issue", task.IssueNumber),
            ("@from", currentState.ToString()));

        if (rowsAffected == 0)
        {
            // Row didn't exist or state changed concurrently
            // Check if task exists at all
            var actualState = GetState(task);
            if (actualState == TaskState.Pending && currentState == TaskState.Pending)
            {
                // Task doesn't exist yet — insert it at the target state
                _store.ExecuteNonQuery(
                    """
                    INSERT OR IGNORE INTO task_state (run_scope, issue_number, current_state, last_agent_id, last_agent_role, pr_number)
                    VALUES (@scope, @issue, @to, @agent, @role, @pr)
                    """,
                    ("@scope", task.RunScope),
                    ("@issue", task.IssueNumber),
                    ("@to", newState.ToString()),
                    ("@agent", agentId),
                    ("@role", (object?)agentRole ?? DBNull.Value),
                    ("@pr", prNumber.HasValue ? prNumber.Value : DBNull.Value));

                LogTransition(task, TaskState.Pending, newState, agentId, agentRole, reason);
                NotifyStateChanged(task, TaskState.Pending, newState, agentId, prNumber);

                _logger.LogInformation(
                    "Task {RunScope}/#{Issue}: {From} → {To} by {Agent} (initial)",
                    task.RunScope, task.IssueNumber, TaskState.Pending, newState, agentId);

                return Task.FromResult(new TaskTransitionResult(
                    TransitionStatus.Succeeded, TaskState.Pending, newState));
            }

            _logger.LogWarning(
                "Task transition conflict: {RunScope}/#{Issue} expected {Expected} but found {Actual}",
                task.RunScope, task.IssueNumber, currentState, actualState);
            return Task.FromResult(new TaskTransitionResult(
                TransitionStatus.Conflict, currentState, actualState,
                $"Expected {currentState} but task is now {actualState}"));
        }

        // Success — log the transition
        LogTransition(task, currentState, newState, agentId, agentRole, reason);

        _logger.LogInformation(
            "Task {RunScope}/#{Issue}: {From} → {To} by {Agent} ({Reason})",
            task.RunScope, task.IssueNumber, currentState, newState, agentId, reason ?? "");

        // Notify AFTER commit (per rubber-duck advice: commit first, publish second)
        NotifyStateChanged(task, currentState, newState, agentId, prNumber);

        return Task.FromResult(new TaskTransitionResult(
            TransitionStatus.Succeeded, currentState, newState));
    }

    /// <summary>
    /// Get the current state of a task. Returns <see cref="TaskState.Pending"/> if unknown.
    /// </summary>
    public TaskState GetState(TaskIdentity task)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureInitialized();

        var result = _store.ExecuteScalar(
            "SELECT current_state FROM task_state WHERE run_scope = @scope AND issue_number = @issue",
            ("@scope", task.RunScope),
            ("@issue", task.IssueNumber));

        if (result is string stateStr && Enum.TryParse<TaskState>(stateStr, out var state))
            return state;

        return TaskState.Pending;
    }

    /// <summary>
    /// Get all task states for a given run scope.
    /// </summary>
    public IReadOnlyList<(int IssueNumber, TaskState State, string? AgentId, int? PrNumber)> GetAllStates(string runScope)
    {
        EnsureInitialized();
        var results = new List<(int, TaskState, string?, int?)>();

        _store.ExecuteReader(
            "SELECT issue_number, current_state, last_agent_id, pr_number FROM task_state WHERE run_scope = @scope",
            reader =>
            {
                var issueNum = reader.GetInt32(0);
                var stateStr = reader.GetString(1);
                var agentId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var prNum = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);

                if (Enum.TryParse<TaskState>(stateStr, out var state))
                    results.Add((issueNum, state, agentId, prNum));
            },
            ("@scope", runScope));

        return results;
    }

    /// <summary>
    /// Get full transition history for a task, most recent first.
    /// </summary>
    public IReadOnlyList<TaskStateTransition> GetHistory(TaskIdentity task, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureInitialized();

        var results = new List<TaskStateTransition>();

        _store.ExecuteReader(
            """
            SELECT from_state, to_state, agent_id, agent_role, reason, timestamp
            FROM task_state_log
            WHERE run_scope = @scope AND issue_number = @issue
            ORDER BY timestamp DESC
            LIMIT @limit
            """,
            reader =>
            {
                var fromStr = reader.GetString(0);
                var toStr = reader.GetString(1);
                if (Enum.TryParse<TaskState>(fromStr, out var from) &&
                    Enum.TryParse<TaskState>(toStr, out var to))
                {
                    results.Add(new TaskStateTransition(
                        task.RunScope,
                        task.IssueNumber,
                        from,
                        to,
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetDateTime(5)));
                }
            },
            ("@scope", task.RunScope),
            ("@issue", task.IssueNumber),
            ("@limit", limit));

        return results;
    }

    /// <summary>
    /// Register a task in the state machine without transitioning (for tasks that
    /// already exist before the state machine was introduced).
    /// </summary>
    public void RegisterIfAbsent(TaskIdentity task, TaskState initialState = TaskState.Pending, int? prNumber = null)
    {
        EnsureInitialized();
        _store.ExecuteNonQuery(
            """
            INSERT OR IGNORE INTO task_state (run_scope, issue_number, current_state, pr_number)
            VALUES (@scope, @issue, @state, @pr)
            """,
            ("@scope", task.RunScope),
            ("@issue", task.IssueNumber),
            ("@state", initialState.ToString()),
            ("@pr", prNumber.HasValue ? prNumber.Value : DBNull.Value));
    }

    private void LogTransition(
        TaskIdentity task, TaskState from, TaskState to,
        string agentId, string? agentRole, string? reason)
    {
        _store.ExecuteNonQuery(
            """
            INSERT INTO task_state_log (run_scope, issue_number, from_state, to_state, agent_id, agent_role, reason)
            VALUES (@scope, @issue, @from, @to, @agent, @role, @reason)
            """,
            ("@scope", task.RunScope),
            ("@issue", task.IssueNumber),
            ("@from", from.ToString()),
            ("@to", to.ToString()),
            ("@agent", agentId),
            ("@role", (object?)agentRole ?? DBNull.Value),
            ("@reason", (object?)reason ?? DBNull.Value));
    }

    private void NotifyStateChanged(
        TaskIdentity task, TaskState from, TaskState to,
        string agentId, int? prNumber)
    {
        try
        {
            OnStateChanged?.Invoke(new TaskStateChangedEvent(task, from, to, agentId, prNumber));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in TaskStateChanged handler for {RunScope}/#{Issue}",
                task.RunScope, task.IssueNumber);
        }
    }
}
