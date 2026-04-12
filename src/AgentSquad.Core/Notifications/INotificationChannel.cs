namespace AgentSquad.Core.Notifications;

/// <summary>
/// Abstraction for a notification delivery channel.
/// Implementations can deliver notifications via different mediums (dashboard, email, Teams, etc.).
/// </summary>
public interface INotificationChannel
{
    /// <summary>Human-readable name of this channel (e.g., "Dashboard", "Email", "Teams").</summary>
    string ChannelName { get; }

    /// <summary>Whether this channel is currently enabled and configured.</summary>
    bool IsEnabled { get; }

    /// <summary>Send a notification through this channel.</summary>
    Task SendAsync(GateNotification notification, CancellationToken ct = default);

    /// <summary>Send a batch of notifications (e.g., on startup recovery).</summary>
    Task SendBatchAsync(IEnumerable<GateNotification> notifications, CancellationToken ct = default)
    {
        // Default implementation sends one at a time
        return Task.WhenAll(notifications.Select(n => SendAsync(n, ct)));
    }
}

/// <summary>
/// Stub for future email notification delivery.
/// </summary>
public class EmailNotificationChannel : INotificationChannel
{
    public string ChannelName => "Email";
    public bool IsEnabled => false; // Not yet implemented

    public Task SendAsync(GateNotification notification, CancellationToken ct = default)
    {
        // TODO: Implement SMTP/SendGrid email delivery
        // Config path: AgentSquad.Notifications.Email.Recipients
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stub for future Microsoft Teams notification delivery.
/// </summary>
public class TeamsNotificationChannel : INotificationChannel
{
    public string ChannelName => "Teams";
    public bool IsEnabled => false; // Not yet implemented

    public Task SendAsync(GateNotification notification, CancellationToken ct = default)
    {
        // TODO: Implement Teams webhook / Adaptive Card delivery
        // Config path: AgentSquad.Notifications.Teams.WebhookUrl
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stub for future Slack notification delivery.
/// </summary>
public class SlackNotificationChannel : INotificationChannel
{
    public string ChannelName => "Slack";
    public bool IsEnabled => false; // Not yet implemented

    public Task SendAsync(GateNotification notification, CancellationToken ct = default)
    {
        // TODO: Implement Slack webhook delivery
        // Config path: AgentSquad.Notifications.Slack.WebhookUrl
        return Task.CompletedTask;
    }
}
