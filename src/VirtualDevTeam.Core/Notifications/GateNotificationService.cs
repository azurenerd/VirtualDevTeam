using System.Text.Json;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Notifications;

/// <summary>
/// Central service that tracks gate notifications, dispatches them to registered channels,
/// and periodically polls for resolution status to keep the dashboard up to date.
/// Runs as a hosted service so it can poll in the background without exhausting rate limits.
/// </summary>
public class GateNotificationService : BackgroundService
{
    private readonly List<GateNotification> _notifications = new();
    private readonly List<INotificationChannel> _channels;
    private readonly IServiceProvider _serviceProvider;
    private readonly VirtualDevTeamConfig _config;
    private readonly IOptionsMonitor<GateNotificationConfig>? _gateConfig;
    private readonly AgentStateStore? _stateStore;
    private readonly IPlatformHostContext? _platformHost;
    private readonly ILogger<GateNotificationService> _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Guard flag so the "project complete" notification fires exactly once per app lifetime.
    /// Reset only on process restart.
    /// </summary>
    private bool _projectCompleteNotified;

    /// <summary>
    /// Poll interval for checking pending gate approvals on GitHub.
    /// NoMessyCodePlan Theme 8: now configurable via <see cref="GateNotificationConfig.PollIntervalSeconds"/>;
    /// falls back to 120s when the config isn't bound (tests, standalone host).
    /// Floored at 10s to avoid thrashing the platform API.
    /// </summary>
    private TimeSpan ResolvedPollInterval =>
        TimeSpan.FromSeconds(Math.Max(10, _gateConfig?.CurrentValue.PollIntervalSeconds ?? 120));

    /// <summary>Raised when a notification is added, read, or resolved.</summary>
    public event Action? OnChange;

    public GateNotificationService(
        IEnumerable<INotificationChannel> channels,
        IServiceProvider serviceProvider,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<GateNotificationService> logger,
        IPlatformHostContext? platformHost = null,
        AgentStateStore? stateStore = null,
        IOptionsMonitor<GateNotificationConfig>? gateConfig = null)
    {
        _channels = channels.ToList();
        _serviceProvider = serviceProvider;
        _config = config.Value;
        _logger = logger;
        _platformHost = platformHost;
        _stateStore = stateStore;
        _gateConfig = gateConfig;
        RestoreFromStore();
    }

    // -- Queries --

    /// <summary>Get all notifications (newest first).</summary>
    public IReadOnlyList<GateNotification> GetAll()
    {
        lock (_lock)
        {
            return _notifications
                .OrderByDescending(n => n.CreatedAt)
                .Select(HydrateReworkState)
                .ToList();
        }
    }

    /// <summary>Get notifications filtered by status.</summary>
    public IReadOnlyList<GateNotification> GetByStatus(NotificationFilter filter)
    {
        lock (_lock)
        {
            IEnumerable<GateNotification> query = filter switch
            {
                NotificationFilter.Open => _notifications.Where(n => !n.IsResolved),
                NotificationFilter.Resolved => _notifications.Where(n => n.IsResolved),
                _ => _notifications,
            };
            return query
                .OrderByDescending(n => n.CreatedAt)
                .Select(HydrateReworkState)
                .ToList();
        }
    }

    /// <summary>
    /// Project the notification with the current rework-in-flight state pulled from
    /// <see cref="IGateCheckService.GetReworkInFlight"/>. Resolved via the service provider
    /// rather than a constructor dep to avoid a circular dependency
    /// (GateCheckService already depends on GateNotificationService for Resolve()).
    /// Returns the notification unchanged when rework isn't in flight or the gate
    /// service isn't registered (e.g., in narrow unit tests).
    /// </summary>
    private GateNotification HydrateReworkState(GateNotification n)
    {
        // Only OPEN gates can have rework in flight — once resolved, the notification
        // is historical and the rework state on it should reflect the moment it was
        // resolved (which is "no rework" by definition: resolution means the agent
        // re-gated successfully or the operator approved). Skip the lookup to avoid
        // showing stale rework state on resolved cards.
        if (n.IsResolved) return n;
        try
        {
            var gateCheck = _serviceProvider.GetService(typeof(IGateCheckService)) as IGateCheckService;
            var rework = gateCheck?.GetReworkInFlight(n.GateId, n.ResourceNumber);
            if (rework is null) return n;
            // Clone with the rework state set so we don't mutate the in-memory original
            // (other concurrent readers may be iterating the list).
            return n with { ReworkState = rework };
        }
        catch
        {
            // Defensive: never let a rework-state lookup failure block notification listing.
            return n;
        }
    }

    /// <summary>Count of unread, unresolved notifications (drives badge number).</summary>
    public int UnreadCount
    {
        get { lock (_lock) { return _notifications.Count(n => !n.IsRead && !n.IsResolved); } }
    }

    /// <summary>Count of open (unresolved) notifications.</summary>
    public int OpenCount
    {
        get { lock (_lock) { return _notifications.Count(n => !n.IsResolved); } }
    }

    /// <summary>Count of resolved notifications.</summary>
    public int ResolvedCount
    {
        get { lock (_lock) { return _notifications.Count(n => n.IsResolved); } }
    }

    // -- Commands --

    /// <summary>
    /// Add a new gate notification and dispatch to all enabled channels.
    /// Called by GateCheckService when a gate requires human approval.
    /// </summary>
    public async Task AddNotificationAsync(
        string gateId, string context, int? resourceNumber = null, CancellationToken ct = default,
        IReadOnlyList<GateArtifact>? artifacts = null)
    {
        var gateName = GetGateName(gateId);
        var githubUrl = BuildGitHubUrl(resourceNumber);
        var resourceType = resourceNumber.HasValue ? "PR" : null;

        var notification = new GateNotification
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            GateId = gateId,
            GateName = gateName,
            Context = context,
            ResourceNumber = resourceNumber,
            ResourceType = resourceType,
            GitHubUrl = githubUrl,
            Artifacts = artifacts,
        };

        lock (_lock)
        {
            // Check for existing unresolved notification for the same gate + resource
            var existing = _notifications.FirstOrDefault(n =>
                n.GateId == gateId && n.ResourceNumber == resourceNumber && !n.IsResolved);
            if (existing is not null)
            {
                // Resolve the old notification — agent re-gated after rework
                existing.IsResolved = true;
                existing.ResolvedAt = DateTime.UtcNow;
                _stateStore?.UpdateGateNotification(existing.Id, existing.IsRead, existing.IsResolved, existing.ResolvedAt);
                notification = notification with { IsReworked = true };
                _logger.LogInformation("Resolved previous notification {OldId} for {GateId} #{Resource} — agent re-submitted after rework",
                    existing.Id, gateId, resourceNumber);
            }

            _notifications.Add(notification);
        }

        // Persist to SQLite
        var artifactsJson = notification.Artifacts is { Count: > 0 }
            ? JsonSerializer.Serialize(notification.Artifacts)
            : null;
        _stateStore?.SaveGateNotification(notification.Id, notification.GateId, notification.GateName,
            notification.Context, notification.ResourceNumber, notification.ResourceType, notification.GitHubUrl,
            notification.IsReworked, artifactsJson);

        _logger.LogInformation("Gate notification added: {GateName} (#{Resource})",
            gateName, resourceNumber);

        // Dispatch to all enabled channels
        foreach (var channel in _channels.Where(c => c.IsEnabled))
        {
            try
            {
                await channel.SendAsync(notification, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification via {Channel}", channel.ChannelName);
            }
        }

        OnChange?.Invoke();
    }

    /// <summary>
    /// Add an informational FlowMonitor notification. Appears on Approvals page with
    /// a Dismiss button (not Approve/Reject). The action is already taken — this is
    /// for operator awareness and audit trail.
    /// Created as pre-resolved so they don't inflate the "pending" badge count.
    /// </summary>
    public void AddFlowMonitorNotification(string gateId, string gateName, string context)
    {
        var now = DateTime.UtcNow;
        var notification = new GateNotification
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            GateId = gateId,
            GateName = gateName,
            Context = context,
            IsFlowMonitorAction = true,
            // Pre-resolved: these are audit-trail entries, not decisions requiring human action.
            // They show in the history section of Approvals but don't increment the badge count.
            IsResolved = true,
            ResolvedAt = now,
        };

        lock (_lock)
        {
            _notifications.Add(notification);
        }

        _stateStore?.SaveGateNotification(notification.Id, notification.GateId, notification.GateName,
            notification.Context, null, null, null, false, null);
        // Immediately mark resolved in the store too
        _stateStore?.UpdateGateNotification(notification.Id, notification.IsRead, notification.IsResolved, notification.ResolvedAt);

        _logger.LogInformation("FlowMonitor notification added (pre-resolved): {GateName}", gateName);
        OnChange?.Invoke();
    }

    /// <summary>Mark a notification as read (user clicked on it).</summary>
    public void MarkAsRead(string notificationId)
    {
        lock (_lock)
        {
            var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification is not null)
            {
                notification.IsRead = true;
                _stateStore?.UpdateGateNotification(notification.Id, notification.IsRead, notification.IsResolved, notification.ResolvedAt);
                _logger.LogDebug("Notification {Id} marked as read", notificationId);
            }
        }
        OnChange?.Invoke();
    }

    /// <summary>Mark all notifications as read.</summary>
    public void MarkAllAsRead()
    {
        lock (_lock)
        {
            foreach (var n in _notifications.Where(n => !n.IsRead))
            {
                n.IsRead = true;
                _stateStore?.UpdateGateNotification(n.Id, n.IsRead, n.IsResolved, n.ResolvedAt);
            }
        }
        OnChange?.Invoke();
    }

    /// <summary>Mark a gate notification as resolved (gate was approved).</summary>
    public void Resolve(string gateId, int? resourceNumber = null)
    {
        bool changed = false;
        lock (_lock)
        {
            var matches = _notifications.Where(n =>
                n.GateId == gateId &&
                n.ResourceNumber == resourceNumber &&
                !n.IsResolved).ToList();

            foreach (var n in matches)
            {
                n.IsResolved = true;
                n.ResolvedAt = DateTime.UtcNow;
                _stateStore?.UpdateGateNotification(n.Id, n.IsRead, n.IsResolved, n.ResolvedAt);
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Gate {GateId} #{Resource} resolved", gateId, resourceNumber);
            OnChange?.Invoke();
        }
    }

    /// <summary>Clear all resolved notifications older than the specified age.</summary>
    public void PurgeResolved(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        lock (_lock)
        {
            _notifications.RemoveAll(n => n.IsResolved && n.ResolvedAt < cutoff);
        }
        _stateStore?.PurgeResolvedNotifications(cutoff);
        OnChange?.Invoke();
    }

    /// <summary>
    /// Dismiss a single notification by its <see cref="GateNotification.Id"/>. Idempotent —
    /// no-op if already resolved or unknown. Used for the operator's "Dismiss" button on
    /// info-only entries (e.g. FlowMonitor audit-trail cards) that need no real decision.
    /// </summary>
    public void Dismiss(string notificationId)
    {
        if (string.IsNullOrEmpty(notificationId)) return;
        bool changed = false;
        lock (_lock)
        {
            var match = _notifications.FirstOrDefault(n => n.Id == notificationId && !n.IsResolved);
            if (match is not null)
            {
                match.IsResolved = true;
                match.ResolvedAt = DateTime.UtcNow;
                _stateStore?.UpdateGateNotification(match.Id, match.IsRead, match.IsResolved, match.ResolvedAt);
                changed = true;
            }
        }
        if (changed)
        {
            _logger.LogInformation("Notification {Id} dismissed by operator", notificationId);
            OnChange?.Invoke();
        }
    }

    /// <summary>
    /// Bulk-dismiss every open FlowMonitor info-only notification (any <c>flow-monitor:*</c>
    /// gate ID EXCEPT <c>flow-monitor:fix:*</c>, which is a real decision gate). Used by the
    /// "Dismiss all FlowMonitor entries" header action to clear out the audit-trail pile-up
    /// the 2026-05-11 operator session encountered. Returns the count actually dismissed.
    /// </summary>
    public int DismissAllFlowMonitorInfo()
    {
        var dismissedIds = new List<string>();
        lock (_lock)
        {
            var matches = _notifications
                .Where(n => !n.IsResolved
                    && n.GateId.StartsWith("flow-monitor:", StringComparison.OrdinalIgnoreCase)
                    && !n.GateId.StartsWith("flow-monitor:fix:", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var now = DateTime.UtcNow;
            foreach (var n in matches)
            {
                n.IsResolved = true;
                n.ResolvedAt = now;
                _stateStore?.UpdateGateNotification(n.Id, n.IsRead, n.IsResolved, n.ResolvedAt);
                dismissedIds.Add(n.Id);
            }
        }
        if (dismissedIds.Count > 0)
        {
            _logger.LogInformation("Bulk-dismissed {Count} FlowMonitor info notification(s)", dismissedIds.Count);
            OnChange?.Invoke();
        }
        return dismissedIds.Count;
    }

    /// <summary>
    /// Auto-dismiss FlowMonitor info-only notifications older than the given TTL.
    /// Runs periodically from <see cref="ExecuteAsync"/> so audit-trail entries don't pile up
    /// indefinitely. FlowMonitor "fix" gates (real decisions) are NEVER auto-dismissed.
    /// </summary>
    public int AutoDismissStaleFlowMonitorInfo(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var dismissedIds = new List<string>();
        lock (_lock)
        {
            var matches = _notifications
                .Where(n => !n.IsResolved
                    && n.CreatedAt < cutoff
                    && n.GateId.StartsWith("flow-monitor:", StringComparison.OrdinalIgnoreCase)
                    && !n.GateId.StartsWith("flow-monitor:fix:", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var now = DateTime.UtcNow;
            foreach (var n in matches)
            {
                n.IsResolved = true;
                n.ResolvedAt = now;
                _stateStore?.UpdateGateNotification(n.Id, n.IsRead, n.IsResolved, n.ResolvedAt);
                dismissedIds.Add(n.Id);
            }
        }
        if (dismissedIds.Count > 0)
        {
            _logger.LogInformation("Auto-dismissed {Count} FlowMonitor info notification(s) older than {Ttl}",
                dismissedIds.Count, olderThan);
            OnChange?.Invoke();
        }
        return dismissedIds.Count;
    }

    // -- Background polling --

    /// <summary>
    /// Periodically checks GitHub for approval status on open gate notifications.
    /// Only polls notifications that have a resource number (PR/issue).
    /// Skips polling entirely when there are no open notifications.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Gate notification poller starting (interval: {Interval}s)", ResolvedPollInterval.TotalSeconds);

        // Short initial delay to let the system start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollPendingGatesAsync(stoppingToken);
                await CheckProjectCompleteAsync(stoppingToken);
                // Auto-dismiss FlowMonitor audit-trail entries older than 24h so the
                // Approvals "Open" list doesn't fill with stale post-hoc notifications.
                AutoDismissStaleFlowMonitorInfo(TimeSpan.FromHours(24));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during gate notification poll cycle");
            }

            await Task.Delay(ResolvedPollInterval, stoppingToken);
        }
    }

    private async Task PollPendingGatesAsync(CancellationToken ct)
    {
        // Snapshot open notifications that have a pollable resource
        List<GateNotification> pendingWithResource;
        lock (_lock)
        {
            pendingWithResource = _notifications
                .Where(n => !n.IsResolved && n.ResourceNumber.HasValue)
                .ToList();
        }

        if (pendingWithResource.Count == 0)
            return;

        // Resolve lazily to break circular dependency with GateCheckService
        var gateCheck = _serviceProvider.GetRequiredService<IGateCheckService>();

        _logger.LogDebug("Polling {Count} pending gate notification(s) for approval", pendingWithResource.Count);

        foreach (var notification in pendingWithResource)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var approved = await gateCheck.IsGateApprovedAsync(
                    notification.GateId, notification.ResourceNumber!.Value, ct);

                if (approved)
                {
                    _logger.LogInformation("Gate {GateId} #{Resource} approved (detected by poller)",
                        notification.GateId, notification.ResourceNumber);
                    Resolve(notification.GateId, notification.ResourceNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll gate status for {GateId} #{Resource}",
                    notification.GateId, notification.ResourceNumber);
            }

            // Small delay between checks to spread out API calls
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    // -- Project completion detection --

    /// <summary>
    /// Checks whether the project is fully complete (zero open PRs and zero open issues).
    /// Fires a single "Project Complete" notification when detected, then never checks again.
    /// Only starts checking after at least one notification has been created (meaning agents
    /// have been active), to avoid false positives during startup.
    /// </summary>
    private async Task CheckProjectCompleteAsync(CancellationToken ct)
    {
        // Already notified — nothing to do for the rest of this process lifetime
        if (_projectCompleteNotified)
            return;

        // Don't check until agents have actually been active (at least one notification exists)
        bool hasAnyNotifications;
        lock (_lock)
        {
            hasAnyNotifications = _notifications.Count > 0;
        }
        if (!hasAnyNotifications)
            return;

        // Don't check if there are still unresolved REAL gate notifications
        // (exclude FlowMonitor info-only notifications — they shouldn't block
        // project completion detection, and they'll be auto-dismissed below)
        int realOpenCount;
        lock (_lock)
        {
            realOpenCount = _notifications.Count(n => !n.IsResolved
                && !n.GateId.StartsWith("flow-monitor:", StringComparison.OrdinalIgnoreCase));
        }
        if (realOpenCount > 0)
            return;

        try
        {
            // Use platform capability interfaces (not IGitHubService) to avoid
            // GitHub API calls in Local mode. These route to SQLite in Local mode.
            var prService = _serviceProvider.GetService<IPullRequestService>();
            var wiService = _serviceProvider.GetService<IWorkItemService>();

            if (prService is null || wiService is null)
                return; // Platform services not registered yet

            var openPRs = await prService.ListOpenAsync(ct);
            if (openPRs.Any())
                return; // Still have open PRs

            // Small delay to spread API calls
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            var openIssues = await wiService.ListByLabelAsync("engineering-task", state: "open", ct);
            if (openIssues.Any())
                return; // Still have open issues

            // Guard: don't fire during early phases where 0 PRs + 0 issues is the normal
            // starting state (before engineering tasks are created). Require at least one
            // merged PR to prove engineering work actually happened and completed.
            var mergedPRs = await prService.ListMergedAsync(ct);
            var engineeringPRs = mergedPRs.Count(pr =>
                pr.Title?.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase) == true ||
                pr.Title?.Contains("Software Engineer", StringComparison.OrdinalIgnoreCase) == true ||
                pr.Title?.Contains("Frontend Engineer", StringComparison.OrdinalIgnoreCase) == true ||
                PullRequestWorkflow.Labels.IsFinalIntegrationPr(pr.Labels, pr.Title, pr.HeadBranch));
            if (engineeringPRs == 0)
                return; // No engineering PRs merged yet — too early to declare complete

            // All clear — project is done. Set flag BEFORE creating the notification
            // to guarantee we never fire twice even if AddNotificationAsync throws.
            _projectCompleteNotified = true;
            _stateStore?.SetRunMetadata("project_complete_notified", "true");

            _logger.LogInformation("🎉 Project complete — no open PRs or issues remain");

            // Auto-dismiss all FlowMonitor info notifications now that the project is done.
            // These are audit-trail entries from earlier phases that are no longer actionable.
            DismissAllFlowMonitorInfo();

            var notification = new GateNotification
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                GateId = "project-complete",
                GateName = "Project Complete",
                Context = "All PRs merged and all issues closed — the project is finished!",
                ResourceType = "Project",
                GitHubUrl = BuildRepositoryUrl(),
                IsResolved = true,
                ResolvedAt = DateTime.UtcNow,
            };

            lock (_lock) { _notifications.Add(notification); }

            // Dispatch to channels (email/teams/slack)
            foreach (var channel in _channels.Where(c => c.IsEnabled))
            {
                try { await channel.SendAsync(notification, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send completion notification via {Channel}",
                        channel.ChannelName);
                }
            }

            OnChange?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking project completion status");
        }
    }

    // -- Restore from SQLite --

    private void RestoreFromStore()
    {
        if (_stateStore is null) return;
        try
        {
            // Restore notifications
            var saved = _stateStore.LoadGateNotifications();
            foreach (var n in saved)
            {
                IReadOnlyList<GateArtifact>? artifacts = null;
                if (!string.IsNullOrEmpty(n.ArtifactsJson))
                {
                    try { artifacts = JsonSerializer.Deserialize<List<GateArtifact>>(n.ArtifactsJson); }
                    catch { /* corrupted JSON — skip artifacts */ }
                }

                _notifications.Add(new GateNotification
                {
                    Id = n.Id,
                    GateId = n.GateId,
                    GateName = n.GateName,
                    Context = n.Context,
                    ResourceNumber = n.ResourceNumber,
                    ResourceType = n.ResourceType,
                    GitHubUrl = n.GitHubUrl,
                    CreatedAt = n.CreatedAt,
                    IsRead = n.IsRead,
                    IsResolved = n.IsResolved,
                    ResolvedAt = n.ResolvedAt,
                    IsReworked = n.IsReworked,
                    Artifacts = artifacts,
                });
            }

            if (saved.Count > 0)
                _logger.LogInformation("Restored {Count} gate notification(s) from SQLite", saved.Count);

            // Restore project-complete flag
            var flag = _stateStore.GetRunMetadata("project_complete_notified");
            if (flag == "true")
            {
                _projectCompleteNotified = true;
                _logger.LogInformation("Restored project-complete flag from SQLite (will not re-notify)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore gate notifications from SQLite");
        }
    }

    // -- Helpers --

    private string? BuildGitHubUrl(int? resourceNumber)
    {
        if (!resourceNumber.HasValue)
            return null;

        if (_platformHost is not null)
            return _platformHost.GetPullRequestWebUrl(resourceNumber.Value);

        // Fallback for when platform host is not available
        if (string.IsNullOrEmpty(_config.Project.GitHubRepo))
            return null;

        return $"https://github.com/{_config.Project.GitHubRepo}/pull/{resourceNumber.Value}";
    }

    private string? BuildRepositoryUrl()
    {
        if (_platformHost is not null)
        {
            // Derive repo URL from a PR URL by stripping the PR-specific suffix
            var prUrl = _platformHost.GetPullRequestWebUrl(1);
            var idx = prUrl.LastIndexOf("/pull/", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return prUrl[..idx];
            idx = prUrl.LastIndexOf("/pullrequest/", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return prUrl[..idx];
            return prUrl;
        }

        if (!string.IsNullOrEmpty(_config.Project.GitHubRepo))
            return $"https://github.com/{_config.Project.GitHubRepo}";

        return null;
    }

    private static string GetGateName(string gateId)
    {
        foreach (var (_, id, name, _) in GateIds.AllGates)
        {
            if (id == gateId) return name;
        }

        // FlowMonitor gates — return human-readable names instead of raw IDs with GUIDs
        if (gateId.StartsWith("flow-monitor:escalate:", StringComparison.OrdinalIgnoreCase))
            return "⚠️ Agent Needs Attention";
        if (gateId.StartsWith("flow-monitor:auto-approve:gate:", StringComparison.OrdinalIgnoreCase))
            return "🤖 FlowMonitor Auto-Approved Gate";
        if (gateId.StartsWith("flow-monitor:auto-approve:decision:", StringComparison.OrdinalIgnoreCase))
            return "🤖 FlowMonitor Auto-Approved Decision";
        if (gateId.StartsWith("flow-monitor:auto-approve:", StringComparison.OrdinalIgnoreCase))
            return "🤖 FlowMonitor Auto-Approved";
        if (gateId.StartsWith("flow-monitor:fix:", StringComparison.OrdinalIgnoreCase))
            return "🔧 Fix Recommendation";
        if (gateId.StartsWith("flow-monitor:nudge-reviewer", StringComparison.OrdinalIgnoreCase))
            return "📋 Review Reminder Sent";
        if (gateId.StartsWith("flow-monitor:", StringComparison.OrdinalIgnoreCase))
            return "🔧 FlowMonitor Action";

        return gateId;
    }
}

/// <summary>Filter options for the notification popup.</summary>
public enum NotificationFilter
{
    /// <summary>Show only open (unresolved) notifications.</summary>
    Open,
    /// <summary>Show only resolved notifications.</summary>
    Resolved,
    /// <summary>Show all notifications.</summary>
    All,
}
