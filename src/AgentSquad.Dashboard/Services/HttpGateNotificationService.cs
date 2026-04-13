using System.Net.Http.Json;
using AgentSquad.Core.Notifications;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// HTTP-based proxy for GateNotificationService in standalone dashboard mode.
/// Polls the Runner's REST API for notification data instead of using in-process state.
/// </summary>
public sealed class HttpGateNotificationService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpGateNotificationService> _logger;
    private Timer? _pollTimer;

    private IReadOnlyList<GateNotification> _allNotifications = [];
    private int _unreadCount;
    private int _openCount;
    private int _resolvedCount;

    public event Action? OnChange;

    public HttpGateNotificationService(HttpClient http, ILogger<HttpGateNotificationService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public void Start()
    {
        _pollTimer = new Timer(async _ => await PollAsync(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    public int UnreadCount => _unreadCount;
    public int OpenCount => _openCount;
    public int ResolvedCount => _resolvedCount;

    public IReadOnlyList<GateNotification> GetByStatus(NotificationFilter filter) => filter switch
    {
        NotificationFilter.Open => _allNotifications.Where(n => !n.IsResolved).ToList(),
        NotificationFilter.Resolved => _allNotifications.Where(n => n.IsResolved).ToList(),
        _ => _allNotifications
    };

    public void MarkAsRead(string notificationId)
    {
        _ = _http.PostAsync($"/api/notifications/{notificationId}/read", null);
    }

    public void MarkAllAsRead()
    {
        _ = _http.PostAsync("/api/notifications/read-all", null);
    }

    private async Task PollAsync()
    {
        try
        {
            var notifications = await _http.GetFromJsonAsync<List<GateNotification>>("/api/notifications");
            var counts = await _http.GetFromJsonAsync<NotificationCounts>("/api/notifications/counts");

            if (notifications is not null)
                _allNotifications = notifications;
            if (counts is not null)
            {
                _unreadCount = counts.Unread;
                _openCount = counts.Open;
                _resolvedCount = counts.Resolved;
            }

            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to poll Runner notifications API");
        }
    }

    public void Dispose() => _pollTimer?.Dispose();

    private sealed record NotificationCounts(int Unread, int Open, int Resolved);
}
