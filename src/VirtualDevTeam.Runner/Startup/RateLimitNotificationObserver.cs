using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Hosted service that subscribes to <see cref="RateLimitManager.OnRateLimitStatusChanged"/>
/// and creates dismissable FlowMonitor notifications on the Approvals page so operators
/// can see when the pipeline is blocked by API rate limiting.
/// </summary>
public sealed class RateLimitNotificationObserver : IHostedService
{
    private readonly RateLimitManager _rateLimitManager;
    private readonly GateNotificationService _notificationService;
    private readonly ILogger<RateLimitNotificationObserver> _logger;

    public RateLimitNotificationObserver(
        RateLimitManager rateLimitManager,
        GateNotificationService notificationService,
        ILogger<RateLimitNotificationObserver> logger)
    {
        _rateLimitManager = rateLimitManager;
        _notificationService = notificationService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rateLimitManager.OnRateLimitStatusChanged += OnStatusChanged;
        _logger.LogInformation("RateLimitNotificationObserver started — subscribed to RateLimitManager events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rateLimitManager.OnRateLimitStatusChanged -= OnStatusChanged;
        return Task.CompletedTask;
    }

    private void OnStatusChanged(RateLimitStatus status)
    {
        if (status.IsLimited)
        {
            _notificationService.AddFlowMonitorNotification(
                "flow-monitor:rate-limit-pause",
                "⏳ Pipeline Rate Limited",
                $"GitHub API rate limit exhausted ({status.Remaining} remaining). " +
                $"All API calls paused until {status.ResetAtUtc:HH:mm:ss} UTC " +
                $"({status.PauseDuration.TotalMinutes:F0} min). " +
                $"Agents blocked: all platform-dependent work.");

            _logger.LogWarning(
                "Rate limit notification created: {Remaining} remaining, paused for {Duration:F0} min until {ResetAt:HH:mm:ss} UTC",
                status.Remaining, status.PauseDuration.TotalMinutes, status.ResetAtUtc);
        }
        else
        {
            _logger.LogInformation("Rate limit pause ended — API calls resumed");
        }
    }
}
