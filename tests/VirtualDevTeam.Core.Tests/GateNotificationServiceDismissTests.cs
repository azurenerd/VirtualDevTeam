using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Pins the new Dismiss / DismissAllFlowMonitorInfo / AutoDismissStaleFlowMonitorInfo
/// methods added to <see cref="GateNotificationService"/> for the 2026-05-11
/// approvals-dismiss-flow-monitor-notifications todo. The previous Approvals page
/// had no way to clear FlowMonitor audit-trail entries; they piled up indefinitely.
/// </summary>
public class GateNotificationServiceDismissTests
{
    private static GateNotificationService CreateService()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var config = Options.Create(new VirtualDevTeamConfig());
        return new GateNotificationService(
            channels: Array.Empty<INotificationChannel>(),
            serviceProvider: sp,
            config: config,
            logger: NullLogger<GateNotificationService>.Instance);
    }

    [Fact]
    public async Task Dismiss_KnownNotificationId_MarksResolved()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:escalate:abc123", "Idle agent escalation");

        var target = svc.GetByStatus(NotificationFilter.Open).Single();
        svc.Dismiss(target.Id);

        Assert.Empty(svc.GetByStatus(NotificationFilter.Open));
        Assert.Single(svc.GetByStatus(NotificationFilter.Resolved));
    }

    [Fact]
    public async Task Dismiss_AlreadyResolved_Idempotent()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:escalate:abc123", "Idle agent escalation");
        var target = svc.GetByStatus(NotificationFilter.Open).Single();
        svc.Dismiss(target.Id);

        // Second dismiss should be a no-op (no exception, no extra change event).
        svc.Dismiss(target.Id);

        Assert.Single(svc.GetByStatus(NotificationFilter.Resolved));
    }

    [Fact]
    public void Dismiss_UnknownId_NoOp()
    {
        var svc = CreateService();
        svc.Dismiss("nonexistent-id");
        Assert.Empty(svc.GetByStatus(NotificationFilter.Open));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Dismiss_NullOrEmptyId_NoOp(string? id)
    {
        var svc = CreateService();
        svc.Dismiss(id!);
        Assert.Empty(svc.GetByStatus(NotificationFilter.Open));
    }

    [Fact]
    public async Task DismissAllFlowMonitorInfo_DismissesEveryFlowMonitorEntry_LeavesOthersOpen()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:escalate:a", "Escalation A");
        await svc.AddNotificationAsync("flow-monitor:escalate:b", "Escalation B");
        await svc.AddNotificationAsync("flow-monitor:escalate:c", "Escalation C");
        await svc.AddNotificationAsync("PRReviewApproval", "Real human gate", resourceNumber: 42);

        var dismissed = svc.DismissAllFlowMonitorInfo();

        Assert.Equal(3, dismissed);
        var remainingOpen = svc.GetByStatus(NotificationFilter.Open);
        Assert.Single(remainingOpen);
        Assert.Equal("PRReviewApproval", remainingOpen[0].GateId);
    }

    [Fact]
    public async Task DismissAllFlowMonitorInfo_PreservesFlowMonitorFixGates()
    {
        // flow-monitor:fix:* is a REAL decision gate (FixRecommendation rubber-duck plan
        // approve/rework). It must NOT be dismissed by the bulk action.
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:escalate:audit", "Audit-only entry");
        await svc.AddNotificationAsync("flow-monitor:fix:rec-7", "Real fix recommendation");

        var dismissed = svc.DismissAllFlowMonitorInfo();

        Assert.Equal(1, dismissed);
        var remainingOpen = svc.GetByStatus(NotificationFilter.Open);
        Assert.Single(remainingOpen);
        Assert.Equal("flow-monitor:fix:rec-7", remainingOpen[0].GateId);
    }

    [Fact]
    public void DismissAllFlowMonitorInfo_NothingToDismiss_ReturnsZero()
    {
        var svc = CreateService();
        var dismissed = svc.DismissAllFlowMonitorInfo();
        Assert.Equal(0, dismissed);
    }

    [Fact]
    public async Task AutoDismissStaleFlowMonitorInfo_DismissesOnlyOldEntries()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:escalate:fresh", "Fresh entry");
        await svc.AddNotificationAsync("flow-monitor:escalate:old", "Old entry");

        // Force one of the entries to look old by overwriting its CreatedAt in-place.
        // (CreatedAt is init-only on the record but the list stores the same reference
        // — we have to use reflection via the publicly-visible list snapshot.)
        var open = svc.GetByStatus(NotificationFilter.Open);
        var oldEntry = open.First(n => n.Context == "Old entry");
        typeof(GateNotification).GetProperty(nameof(GateNotification.CreatedAt))!
            .SetValue(oldEntry, DateTime.UtcNow.AddHours(-48));

        var dismissed = svc.AutoDismissStaleFlowMonitorInfo(TimeSpan.FromHours(24));

        Assert.Equal(1, dismissed);
        var remaining = svc.GetByStatus(NotificationFilter.Open);
        Assert.Single(remaining);
        Assert.Equal("Fresh entry", remaining[0].Context);
    }

    [Fact]
    public async Task AutoDismissStaleFlowMonitorInfo_NeverDismissesFixGates()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("flow-monitor:fix:rec-old", "Ancient fix recommendation");
        var fixEntry = svc.GetByStatus(NotificationFilter.Open).Single();
        typeof(GateNotification).GetProperty(nameof(GateNotification.CreatedAt))!
            .SetValue(fixEntry, DateTime.UtcNow.AddDays(-30));

        var dismissed = svc.AutoDismissStaleFlowMonitorInfo(TimeSpan.FromHours(24));

        Assert.Equal(0, dismissed);
        Assert.Single(svc.GetByStatus(NotificationFilter.Open));
    }

    [Fact]
    public async Task AutoDismissStaleFlowMonitorInfo_NeverDismissesNonFlowMonitorGates()
    {
        var svc = CreateService();
        await svc.AddNotificationAsync("PRReviewApproval", "Real gate", resourceNumber: 42);
        var realEntry = svc.GetByStatus(NotificationFilter.Open).Single();
        typeof(GateNotification).GetProperty(nameof(GateNotification.CreatedAt))!
            .SetValue(realEntry, DateTime.UtcNow.AddDays(-30));

        var dismissed = svc.AutoDismissStaleFlowMonitorInfo(TimeSpan.FromHours(24));

        Assert.Equal(0, dismissed);
        Assert.Single(svc.GetByStatus(NotificationFilter.Open));
    }
}
