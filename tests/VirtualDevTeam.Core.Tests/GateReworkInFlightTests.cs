using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Pins the rework-in-flight persistence + projection contract added for the
/// 2026-05-12 <c>approvals-page-rework-state-persistence</c> todo. The Approvals
/// page used to render rework state from an in-memory Razor field that was lost
/// every time the operator navigated away. These tests verify the new flow:
/// <list type="number">
///   <item>RejectGate -> GetReworkInFlight returns a populated <see cref="ReworkInFlightState"/>.</item>
///   <item>IterationCount survives an agent re-gate (CheckGateAsync clears the rejection but keeps the count for the next rejection).</item>
///   <item>ApproveGate clears both the rejection AND the cumulative counter.</item>
///   <item>GateNotificationService projects the rework state onto each open notification card via the /api/notifications payload.</item>
/// </list>
/// </summary>
public sealed class GateReworkInFlightTests
{
    private const string GateId = GateIds.PRReviewApproval;
    private const int PrNumber = 1448;

    private static GateCheckService CreateGate(GateNotificationService? notification = null)
    {
        var config = Options.Create(new VirtualDevTeamConfig
        {
            HumanInteraction = new HumanInteractionConfig
            {
                Enabled = true,
                Gates = new Dictionary<string, GateConfig>
                {
                    [GateId] = new GateConfig { RequiresHuman = true }
                }
            }
        });
        return new GateCheckService(
            config: config,
            prService: new Mock<IPullRequestService>().Object,
            reviewService: new Mock<IReviewService>().Object,
            workItemService: new Mock<IWorkItemService>().Object,
            logger: NullLogger<GateCheckService>.Instance,
            notificationService: notification);
    }

    [Fact]
    public void RejectGate_PopulatesReworkInFlightWithFeedbackAndIteration1()
    {
        var gate = CreateGate();

        gate.RejectGate(GateId, "Remove the Docker references — we're not shipping containers", PrNumber);

        var inFlight = gate.GetReworkInFlight(GateId, PrNumber);

        Assert.NotNull(inFlight);
        Assert.Equal(GateId, inFlight!.GateId);
        Assert.Equal(PrNumber, inFlight.ResourceNumber);
        Assert.Equal(1, inFlight.IterationCount);
        Assert.Equal("Remove the Docker references — we're not shipping containers", inFlight.Feedback);
        Assert.True(inFlight.RequestedAt <= DateTime.UtcNow);
        Assert.True(inFlight.RequestedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void GetReworkInFlight_UnknownGate_ReturnsNull()
    {
        var gate = CreateGate();

        Assert.Null(gate.GetReworkInFlight(GateId, PrNumber));
        Assert.Null(gate.GetReworkInFlight("nonexistent-gate", 42));
    }

    [Fact]
    public void RejectGate_TwiceWithoutClear_LatestFeedbackWinsAndIterationIncrements()
    {
        var gate = CreateGate();

        gate.RejectGate(GateId, "First pass: rename the variable", PrNumber);
        gate.RejectGate(GateId, "Actually — also remove the unused import", PrNumber);

        var inFlight = gate.GetReworkInFlight(GateId, PrNumber);
        Assert.NotNull(inFlight);
        Assert.Equal(2, inFlight!.IterationCount);
        Assert.Equal("Actually — also remove the unused import", inFlight.Feedback);
    }

    [Fact]
    public async Task CheckGateAsync_ClearsRejection_ButPreservesCumulativeIterationForNextCycle()
    {
        // Scenario: human rejects (iteration 1), agent re-gates after rework
        // (CheckGateAsync clears _localRejections), human rejects again (iteration 2).
        var gate = CreateGate();
        gate.RejectGate(GateId, "Fix the typo", PrNumber);
        Assert.Equal(1, gate.GetReworkInFlight(GateId, PrNumber)!.IterationCount);

        // Agent re-gates — clears the in-flight rejection.
        await gate.CheckGateAsync(GateId, "PR ready for re-review after rework", PrNumber);
        Assert.Null(gate.GetReworkInFlight(GateId, PrNumber));

        // Operator rejects again on iteration 2.
        gate.RejectGate(GateId, "Still missing the changelog entry", PrNumber);

        var inFlight = gate.GetReworkInFlight(GateId, PrNumber);
        Assert.NotNull(inFlight);
        Assert.Equal(2, inFlight!.IterationCount);
        Assert.Equal("Still missing the changelog entry", inFlight.Feedback);
    }

    [Fact]
    public void ApproveGate_ClearsBothRejectionAndCumulativeCounter()
    {
        var gate = CreateGate();
        gate.RejectGate(GateId, "Nope", PrNumber);
        gate.RejectGate(GateId, "Still nope", PrNumber);
        Assert.Equal(2, gate.GetReworkInFlight(GateId, PrNumber)!.IterationCount);

        gate.ApproveGate(GateId, PrNumber);

        // In-flight cleared.
        Assert.Null(gate.GetReworkInFlight(GateId, PrNumber));

        // And the cumulative counter resets — a *new* rejection on the same key
        // (e.g., after a later re-activation of the gate) starts at iteration 1 again.
        gate.RejectGate(GateId, "Operator changed their mind on a follow-up review", PrNumber);
        Assert.Equal(1, gate.GetReworkInFlight(GateId, PrNumber)!.IterationCount);
    }

    [Fact]
    public void GetAllReworkInFlight_ReturnsEveryPendingRejection()
    {
        var gate = CreateGate();
        gate.RejectGate(GateId, "PR 1 needs work", resourceNumber: 1001);
        gate.RejectGate(GateId, "PR 2 needs work", resourceNumber: 1002);
        gate.RejectGate(GateIds.PMSpecification, "Spec change", resourceNumber: 1003);

        var all = gate.GetAllReworkInFlight();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, s => s.ResourceNumber == 1001 && s.GateId == GateId);
        Assert.Contains(all, s => s.ResourceNumber == 1002 && s.GateId == GateId);
        Assert.Contains(all, s => s.ResourceNumber == 1003 && s.GateId == GateIds.PMSpecification);
    }

    [Fact]
    public async Task NotificationService_HydratesReworkState_OnOpenCardsViaApprovalsPayload()
    {
        // This is the contract the /api/approvals (notifications) endpoint relies on:
        // every open GateNotification returned by GetByStatus comes pre-hydrated with
        // the current ReworkInFlightState from GateCheckService. The Approvals page
        // renders from that field instead of an in-memory Razor flag, so the spinner +
        // iteration count survive navigation away from /approvals.
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new VirtualDevTeamConfig
        {
            HumanInteraction = new HumanInteractionConfig
            {
                Enabled = true,
                Gates = new Dictionary<string, GateConfig>
                {
                    [GateId] = new GateConfig { RequiresHuman = true }
                }
            }
        }));
        services.AddSingleton(new Mock<IPullRequestService>().Object);
        services.AddSingleton(new Mock<IReviewService>().Object);
        services.AddSingleton(new Mock<IWorkItemService>().Object);
        services.AddSingleton<GateNotificationService>(sp => new GateNotificationService(
            channels: Array.Empty<INotificationChannel>(),
            serviceProvider: sp,
            config: sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>(),
            logger: NullLogger<GateNotificationService>.Instance));
        services.AddSingleton<IGateCheckService>(sp => new GateCheckService(
            config: sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>(),
            prService: sp.GetRequiredService<IPullRequestService>(),
            reviewService: sp.GetRequiredService<IReviewService>(),
            workItemService: sp.GetRequiredService<IWorkItemService>(),
            logger: NullLogger<GateCheckService>.Instance,
            notificationService: sp.GetRequiredService<GateNotificationService>()));
        var sp = services.BuildServiceProvider();
        var notifications = sp.GetRequiredService<GateNotificationService>();
        var gate = (GateCheckService)sp.GetRequiredService<IGateCheckService>();

        // Open an unresolved notification for the gate (as the agent would when it hits the gate).
        await notifications.AddNotificationAsync(GateId, "Review the PR", resourceNumber: PrNumber);

        // Initially no rework requested -> ReworkState is null on the card.
        var beforeReject = notifications.GetByStatus(NotificationFilter.Open).Single();
        Assert.Null(beforeReject.ReworkState);

        // Operator clicks "Request Rework" with feedback.
        gate.RejectGate(GateId, "Reword the executive summary", PrNumber);

        // Re-poll the notifications endpoint payload -> rework state is now visible.
        var afterReject = notifications.GetByStatus(NotificationFilter.Open).Single();
        Assert.NotNull(afterReject.ReworkState);
        Assert.Equal(1, afterReject.ReworkState!.IterationCount);
        Assert.Equal("Reword the executive summary", afterReject.ReworkState.Feedback);
        Assert.Equal(PrNumber, afterReject.ReworkState.ResourceNumber);

        // Agent re-gates after rework — new notification + old marked IsReworked=true; in-flight clears.
        await notifications.AddNotificationAsync(GateId, "Reworked PR ready for re-review", resourceNumber: PrNumber);
        await gate.CheckGateAsync(GateId, "agent re-gating after rework", PrNumber);

        // The new open card no longer reports rework-in-flight.
        var afterAgentReGate = notifications
            .GetByStatus(NotificationFilter.Open)
            .Single(n => n.GateId == GateId && n.ResourceNumber == PrNumber);
        Assert.Null(afterAgentReGate.ReworkState);
        Assert.True(afterAgentReGate.IsReworked);
    }
}
