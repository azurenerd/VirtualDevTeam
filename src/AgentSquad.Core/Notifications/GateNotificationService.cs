using AgentSquad.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Notifications;

/// <summary>
/// Central service that tracks gate notifications, dispatches them to registered channels,
/// and periodically polls GitHub for resolution status to keep the dashboard up to date.
/// Runs as a hosted service so it can poll in the background without exhausting rate limits.
/// </summary>
public class GateNotificationService : BackgroundService
{
    private readonly List<GateNotification> _notifications = new();
    private readonly List<INotificationChannel> _channels;
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentSquadConfig _config;
    private readonly ILogger<GateNotificationService> _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Poll interval for checking pending gate approvals on GitHub.
    /// Deliberately conservative (60s) to avoid exhausting rate limits.
    /// With N pending gates, this costs N API calls per minute.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Raised when a notification is added, read, or resolved.</summary>
    public event Action? OnChange;

    public GateNotificationService(
        IEnumerable<INotificationChannel> channels,
        IServiceProvider serviceProvider,
        IOptions<AgentSquadConfig> config,
        ILogger<GateNotificationService> logger)
    {
        _channels = channels.ToList();
        _serviceProvider = serviceProvider;
        _config = config.Value;
        _logger = logger;
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
                n.IsRead = true;
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

    // -- Helpers --

    private string? BuildGitHubUrl(int? resourceNumber)
    {
        if (!resourceNumber.HasValue || string.IsNullOrEmpty(_config.Project.GitHubRepo))
            return null;

        var repo = _config.Project.GitHubRepo;
        return $"https://github.com/{repo}/pull/{resourceNumber.Value}";
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
