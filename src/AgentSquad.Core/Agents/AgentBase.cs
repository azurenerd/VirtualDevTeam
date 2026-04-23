using AgentSquad.Core.AI;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Agents;

public abstract class AgentBase : IAgent, IDisposable
{
    private readonly object _statusLock = new();
    private readonly object _errorLock = new();
    private AgentStatus _status = AgentStatus.Requested;
    private string? _statusReason;
    private AgentDiagnostic? _currentDiagnostic;
    private bool _disposed;
    private readonly List<AgentLogEntry> _recentErrors = new();
    private string? _cachedMemorySummary;

    /// <summary>
    /// The Copilot CLI session ID for this agent. Non-engineer agents use a single
    /// persistent session for their lifetime. Engineer agents override this to
    /// manage per-PR sessions via <see cref="SetCliSession"/>.
    /// </summary>
    private string _cliSessionId = Guid.NewGuid().ToString();

    protected AgentBase(AgentIdentity identity, ILogger<AgentBase> logger, AgentMemoryStore? memoryStore = null, RoleContextProvider? roleContextProvider = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        MemoryStore = memoryStore;
        RoleContext = roleContextProvider;
        LifetimeCts = new CancellationTokenSource();
    }

    public AgentIdentity Identity { get; }

    public AgentStatus Status
    {
        get { lock (_statusLock) { return _status; } }
    }

    public string? StatusReason
    {
        get { lock (_statusLock) { return _statusReason; } }
    }

    /// <summary>Gets recent error/warning log entries for this agent.</summary>
    public IReadOnlyList<AgentLogEntry> RecentErrors
    {
        get { lock (_errorLock) { return _recentErrors.ToList(); } }
    }

    /// <summary>Gets the current self-diagnostic snapshot.</summary>
    public AgentDiagnostic? CurrentDiagnostic
    {
        get { lock (_statusLock) { return _currentDiagnostic; } }
    }

    /// <summary>Clears all tracked errors/warnings.</summary>
    public void ClearErrors()
    {
        lock (_errorLock) { _recentErrors.Clear(); }
        ErrorsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<AgentStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? ErrorsChanged;
    public event EventHandler<AgentActivityEventArgs>? ActivityLogged;
    public event EventHandler<DiagnosticChangedEventArgs>? DiagnosticChanged;

    protected ILogger<AgentBase> Logger { get; }
    protected CancellationTokenSource LifetimeCts { get; }

    /// <summary>
    /// Gets the current Copilot CLI session ID. Non-engineer agents keep one session
    /// for their lifetime. Engineer agents switch sessions per PR/Issue.
    /// </summary>
    protected string CliSessionId => _cliSessionId;

    /// <summary>
    /// Sets the active CLI session ID. Use this in engineer agents to switch context
    /// when starting a new PR or resuming rework on an existing one.
    /// </summary>
    protected void SetCliSession(string sessionId)
    {
        _cliSessionId = sessionId;
        AgentCallContext.CurrentSessionId = sessionId;
    }

    /// <summary>Persistent memory store, available to all agents.</summary>
    protected AgentMemoryStore? MemoryStore { get; }

    /// <summary>Provider for custom role context from configuration (role descriptions, knowledge links).</summary>
    protected RoleContextProvider? RoleContext { get; }

    /// <summary>
    /// Builds a system prompt by prepending any custom role context (role description,
    /// knowledge links) from configuration. If no customization is configured, returns
    /// the default prompt unchanged.
    /// </summary>
    protected string BuildSystemPrompt(string defaultPrompt)
    {
        if (RoleContext is null)
            return defaultPrompt;

        var roleCtx = RoleContext.GetRoleSystemContext(Identity.Role, Identity.CustomAgentName);
        if (string.IsNullOrWhiteSpace(roleCtx))
            return defaultPrompt;

        return $"{roleCtx}\n\n{defaultPrompt}";
    }

    /// <summary>
    /// Creates a new ChatHistory with role context automatically injected as the first
    /// system message (if custom role description or knowledge links are configured).
    /// Use this instead of <c>new ChatHistory()</c> to ensure role customization is applied.
    /// </summary>
    protected Microsoft.SemanticKernel.ChatCompletion.ChatHistory CreateChatHistory()
    {
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();

        if (RoleContext is not null)
        {
            var roleCtx = RoleContext.GetRoleSystemContext(Identity.Role, Identity.CustomAgentName);
            if (!string.IsNullOrWhiteSpace(roleCtx))
            {
                history.AddSystemMessage(roleCtx);
            }
        }

        return history;
    }

    /// <summary>
    /// Record a memory entry that persists across AI calls and process restarts.
    /// This builds the agent's long-term context so each AI call can include
    /// relevant history of what the agent has done and learned.
    /// </summary>
    protected async Task RememberAsync(
        MemoryType type,
        string summary,
        string? details = null,
        CancellationToken ct = default)
    {
        if (MemoryStore is null) return;
        try
        {
            await MemoryStore.StoreAsync(Identity.Id, type, summary, details, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to store memory for {AgentId}", Identity.Id);
        }
    }

    /// <summary>
    /// Get formatted memory context for inclusion in AI prompts.
    /// Returns empty string if no memories exist or store is unavailable.
    /// </summary>
    protected async Task<string> GetMemoryContextAsync(int maxEntries = 20, CancellationToken ct = default)
    {
        if (MemoryStore is null) return "";
        try
        {
            return await MemoryStore.GetMemoryContextAsync(Identity.Id, maxEntries, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load memory context for {AgentId}", Identity.Id);
            return "";
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        AgentCallContext.CurrentAgentId = Identity.Id;
        AgentCallContext.CurrentSessionId = _cliSessionId;
        UpdateStatus(AgentStatus.Initializing, "Agent initialization started");
        LogActivity("system", "Agent initialization started");

        // Initialize role customization context (knowledge links, MCP servers)
        if (RoleContext is not null)
        {
            try
            {
                await RoleContext.InitializeForAgentAsync(Identity.Role, Identity.CustomAgentName, ct);
                var mcpServers = RoleContext.GetMcpServers(Identity.Role, Identity.CustomAgentName);
                if (mcpServers.Count > 0)
                {
                    AgentCallContext.McpServers = mcpServers;
                    Logger.LogInformation("Agent {AgentId} configured with {McpCount} MCP servers: {Servers}",
                        Identity.Id, mcpServers.Count, string.Join(", ", mcpServers));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to initialize role context for {AgentId}, continuing with defaults", Identity.Id);
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeCts.Token);
        await OnInitializeAsync(linked.Token);
        UpdateStatus(AgentStatus.Online, "Agent initialized successfully");
        LogActivity("system", "Agent initialized successfully");
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        AgentCallContext.CurrentAgentId = Identity.Id;
        AgentCallContext.CurrentSessionId = _cliSessionId;

        // Re-set MCP servers for this async context (AsyncLocal doesn't flow across tasks)
        if (RoleContext is not null)
        {
            var mcpServers = RoleContext.GetMcpServers(Identity.Role);
            if (mcpServers.Count > 0)
                AgentCallContext.McpServers = mcpServers;
        }

        UpdateStatus(AgentStatus.Working, "Agent starting main loop");
        LogActivity("system", "Agent starting main loop");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeCts.Token);
        await OnStartAsync(linked.Token);
        await RunAgentLoopAsync(linked.Token);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Logger.LogInformation("Agent {AgentId} stopping", Identity.Id);
        LogActivity("system", "Agent stopping");
        await LifetimeCts.CancelAsync();
        await OnStopAsync(ct);
        UpdateStatus(AgentStatus.Offline, "Agent stopped gracefully");
        LogActivity("system", "Agent stopped gracefully");
    }

    public async Task HandleMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeCts.Token);
        Logger.LogDebug("Agent {AgentId} received message {MessageType} from {FromAgent}",
            Identity.Id, message.MessageType, message.FromAgentId);
        await OnMessageReceivedAsync(message, linked.Token);
    }

    protected void UpdateStatus(AgentStatus newStatus, string? reason = null)
    {
        AgentStatus oldStatus;
        string? oldReason;
        lock (_statusLock)
        {
            oldStatus = _status;
            oldReason = _statusReason;
            _status = newStatus;
            _statusReason = reason;
        }

        // Skip logging and events when nothing meaningful changed
        if (oldStatus == newStatus && oldReason == reason)
            return;

        Logger.LogInformation("Agent {AgentId} status changed: {OldStatus} -> {NewStatus} ({Reason})",
            Identity.Id, oldStatus, newStatus, reason ?? "no reason");

        if (oldStatus != newStatus)
        {
            LogActivity("status", $"{oldStatus} → {newStatus}" + (reason is not null ? $": {reason}" : ""));
        }

        StatusChanged?.Invoke(this, new AgentStatusChangedEventArgs
        {
            Agent = Identity,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Reason = reason
        });

        // Refresh diagnostic on every status update to keep it in sync
        RefreshDiagnostic();
    }

    /// <summary>Record an error or warning that will be visible in the dashboard.</summary>
    protected void RecordError(string message, LogLevel level = LogLevel.Error, Exception? exception = null)
    {
        var entry = new AgentLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            ExceptionDetails = exception?.ToString()
        };

        lock (_errorLock)
        {
            _recentErrors.Add(entry);
            // Keep last 50 entries max
            if (_recentErrors.Count > 50)
                _recentErrors.RemoveAt(0);
        }

        ErrorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Log an activity event that will appear in the agent's activity history on the dashboard.
    /// Event types: "task", "status", "system", "message", "github".
    /// </summary>
    protected void LogActivity(string eventType, string details)
    {
        ActivityLogged?.Invoke(this, new AgentActivityEventArgs
        {
            AgentId = Identity.Id,
            EventType = eventType,
            Details = details
        });
    }

    /// <summary>
    /// Refresh the agent's self-diagnostic by evaluating current state against role expectations.
    /// Uses cached memory summary if available (call RefreshDiagnosticWithMemoryAsync periodically
    /// to update the cache). Called from UpdateStatus on every status change — lightweight (no I/O).
    /// </summary>
    protected void RefreshDiagnostic()
    {
        var diagnostic = RoleExpectations.Evaluate(
            Identity.Role, Status, StatusReason, Identity.AssignedPullRequest,
            _cachedMemorySummary);

        AgentDiagnostic? previous;
        lock (_statusLock)
        {
            previous = _currentDiagnostic;
            _currentDiagnostic = diagnostic;
        }

        // Always fire the event so the dashboard snapshot stays in sync.
        // Include whether the diagnostic actually changed for history recording.
        bool changed = previous?.Summary != diagnostic.Summary 
                    || previous?.IsCompliant != diagnostic.IsCompliant;

        DiagnosticChanged?.Invoke(this, new DiagnosticChangedEventArgs
        {
            AgentId = Identity.Id,
            Diagnostic = diagnostic,
            IsChanged = changed
        });

        if (!diagnostic.IsCompliant)
        {
            Logger.LogWarning(
                "Agent {AgentId} self-diagnostic NON-COMPLIANT: {Issue}",
                Identity.Id, diagnostic.ComplianceIssue ?? diagnostic.Summary);
        }
    }

    /// <summary>
    /// Async version that loads agent memory before evaluating diagnostics.
    /// Call this periodically in the agent loop (e.g., once per iteration) to
    /// keep the diagnostic memory-aware. The loaded memory is cached so that
    /// even the sync RefreshDiagnostic() path benefits from it.
    /// </summary>
    protected async Task RefreshDiagnosticWithMemoryAsync(CancellationToken ct = default)
    {
        if (MemoryStore is not null)
        {
            try
            {
                // Load recent memories and format as a compact summary
                var memories = await MemoryStore.GetRecentAsync(Identity.Id, count: 10, ct: ct);
                if (memories.Count > 0)
                {
                    var lines = new List<string>(memories.Count);
                    for (int i = memories.Count - 1; i >= 0; i--)
                    {
                        var m = memories[i];
                        var tag = m.Type switch
                        {
                            Persistence.MemoryType.Action => "DID",
                            Persistence.MemoryType.Decision => "DECIDED",
                            Persistence.MemoryType.Learning => "LEARNED",
                            Persistence.MemoryType.Instruction => "TOLD",
                            _ => "NOTE"
                        };
                        lines.Add($"• [{tag}] {m.Summary}");
                    }
                    _cachedMemorySummary = string.Join('\n', lines);
                }
                else
                {
                    _cachedMemorySummary = null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load memory for diagnostics");
            }
        }

        // Now refresh with the updated cache
        RefreshDiagnostic();
    }

    protected virtual Task OnInitializeAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStopAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnMessageReceivedAsync(AgentMessage message, CancellationToken ct) => Task.CompletedTask;

    protected abstract Task RunAgentLoopAsync(CancellationToken ct);

    public void Dispose()
    {
        if (!_disposed)
        {
            LifetimeCts.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>A log entry recorded by an agent for dashboard display.</summary>
public record AgentLogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";
    public string? ExceptionDetails { get; init; }
}
