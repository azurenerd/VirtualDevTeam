using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Notifications;

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
    private readonly AgentSquadConfig _config;
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
    /// Deliberately conservative (60s) to avoid exhausting rate limits.
    /// With N pending gates, this costs N API calls per minute.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(120);

    /// <summary>Raised when a notification is added, read, or resolved.</summary>
    public event Action? OnChange;

    public GateNotificationService(
        IEnumerable<INotificationChannel> channels,
        IServiceProvider serviceProvider,
        IOptions<AgentSquadConfig> config,
        ILogger<GateNotificationService> logger,
        IPlatformHostContext? platformHost = null,
        AgentStateStore? stateStore = null)
    {
        _channels = channels.ToList();
        _serviceProvider = serviceProvider;
        _config = config.Value;
        _logger = logger;
        _platformHost = platformHost;
        _stateStore = stateStore;
        RestoreFromStore();
    }

    // -- Queries --

    /// <summary>Get all notifications (newest first).</summary>
    public IReadOnlyList<GateNotification> GetAll()
    {
        lock (_lock) { return _notifications.OrderByDescending(n => n.CreatedAt).ToList(); }
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
            return query.OrderByDescending(n => n.CreatedAt).ToList();
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
        string gateId, string context, int? resourceNumber = null, CancellationToken ct = default)
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
        };

        lock (_lock)
        {
            // Avoid duplicate notifications for the same gate + resource
            var existing = _notifications.FirstOrDefault(n =>
                n.GateId == gateId && n.ResourceNumber == resourceNumber && !n.IsResolved);
            if (existing is not null)
            {
                _logger.LogDebug("Gate notification already exists for {GateId} #{Resource}",
                    gateId, resourceNumber);
                return;
            }

            _notifications.Add(notification);
        }

        // Persist to SQLite
        _stateStore?.SaveGateNotification(notification.Id, notification.GateId, notification.GateName,
            notification.Context, notification.ResourceNumber, notification.ResourceType, notification.GitHubUrl);

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

    // -- Background polling --

    /// <summary>
    /// Periodically checks GitHub for approval status on open gate notifications.
    /// Only polls notifications that have a resource number (PR/issue).
    /// Skips polling entirely when there are no open notifications.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Gate notification poller starting (interval: {Interval}s)", PollInterval.TotalSeconds);

        // Short initial delay to let the system start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollPendingGatesAsync(stoppingToken);
                await CheckProjectCompleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during gate notification poll cycle");
            }

            await Task.Delay(PollInterval, stoppingToken);
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

        // Don't check if there are still unresolved gate notifications
        if (OpenCount > 0)
            return;

        try
        {
            var github = _serviceProvider.GetRequiredService<IGitHubService>();

            var openPRs = await github.GetOpenPullRequestsAsync(ct);
            if (openPRs.Count > 0)
                return; // Still have open PRs

            // Small delay to spread API calls
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            var openIssues = await github.GetOpenIssuesAsync(ct);
            if (openIssues.Count > 0)
                return; // Still have open issues

            // All clear — project is done. Set flag BEFORE creating the notification
            // to guarantee we never fire twice even if AddNotificationAsync throws.
            _projectCompleteNotified = true;
            _stateStore?.SetRunMetadata("project_complete_notified", "true");

            _logger.LogInformation("🎉 Project complete — no open PRs or issues remain");

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
