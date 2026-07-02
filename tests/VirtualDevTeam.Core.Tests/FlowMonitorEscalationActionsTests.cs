using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// T1.2 — behavioral tests for the rung-2 / rung-3 escalation actions.
/// Covers the "this rung handles agent-stuck findings", the platform-resolution
/// path (PR preferred, issue fallback), and the graceful-degradation behavior when
/// platform services are missing.
/// </summary>
public sealed class FlowMonitorEscalationActionsTests
{
    // ---------------------------------------------------------------------
    // PostExplicitAskAction
    // ---------------------------------------------------------------------

    [Fact]
    public async Task PostExplicitAskAction_PostsCommentToOpenPr_WhenFound()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.ListForAgentAsync("Software Engineer 1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformPullRequest>
            {
                new() { Number = 42, Title = "Implement auth", State = "open" },
            });

        var review = new Mock<IReviewService>();

        var action = new PostExplicitAskAction(
            NullLogger<PostExplicitAskAction>.Instance,
            pr.Object,
            workItemService: null,
            reviewService: review.Object);

        var finding = StuckFinding(displayName: "Software Engineer 1");
        var outcome = await action.ExecuteAsync(finding, CancellationToken.None);

        // Rung-2 PR comments are suppressed per Lesson #28 — action succeeds but
        // does NOT post a comment (no agent parses FlowMonitor PR comments).
        Assert.Equal(FlowActionResult.Success, outcome.Result);
        Assert.Equal("pr#42", outcome.Target);
        review.Verify(r => r.AddCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PostExplicitAskAction_FallsBackToIssue_WhenNoOpenPr()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.ListForAgentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlatformPullRequest>());
        var review = new Mock<IReviewService>(); // never invoked

        var workItem = new Mock<IWorkItemService>();
        workItem.Setup(w => w.ListForAgentAsync("Software Engineer 1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformWorkItem>
            {
                new() { Number = 99, Title = "engineering-task: auth", State = "open" },
            });

        var action = new PostExplicitAskAction(
            NullLogger<PostExplicitAskAction>.Instance,
            pr.Object, workItem.Object, review.Object);

        var outcome = await action.ExecuteAsync(StuckFinding(), CancellationToken.None);

        // Rung-2 issue comments are also suppressed per Lesson #28/#43.
        // Action returns Success with the issue target, but doesn't post a comment.
        Assert.Equal(FlowActionResult.Success, outcome.Result);
        Assert.Equal("issue#99", outcome.Target);
        workItem.Verify(w => w.AddCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        review.Verify(r => r.AddCommentAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PostExplicitAskAction_NoPlatformServices_ReturnsSkipped()
    {
        var action = new PostExplicitAskAction(NullLogger<PostExplicitAskAction>.Instance);

        var outcome = await action.ExecuteAsync(StuckFinding(), CancellationToken.None);

        Assert.Equal(FlowActionResult.Skipped, outcome.Result);
    }

    [Fact]
    public void PostExplicitAskAction_CanHandle_AgentStuckAndPhaseCompletion()
    {
        var action = new PostExplicitAskAction(NullLogger<PostExplicitAskAction>.Instance);

        Assert.True(action.CanHandle(StuckFinding()));
        Assert.True(action.CanHandle(StuckFinding(detectorId: "phase-completion-mismatch")));
        Assert.False(action.CanHandle(StuckFinding(detectorId: "deadlock")));
        Assert.False(action.CanHandle(StuckFinding(targetAgentId: null)));
    }

    /// <summary>
    /// imggen-action-handlers: image-spec-mismatch and image-regen-anomaly must land in
    /// both rung-2 and rung-3 allowlists so their findings progress through the escalation
    /// ladder. Image findings carry the actionable target in TargetResource ("pr#N") rather
    /// than TargetAgentId, so CanHandle must accept either form.
    /// </summary>
    [Fact]
    public void Actions_AcceptImageDetectorFindings_ViaPrTargetResource()
    {
        var post = new PostExplicitAskAction(NullLogger<PostExplicitAskAction>.Instance);
        var escalate = new EscalateToHumanAction(NullLogger<EscalateToHumanAction>.Instance);

        var imageRegen = ImageFinding(detectorId: "image-regen-anomaly", targetResource: "pr#42");
        var imageSpec = ImageFinding(detectorId: "image-spec-mismatch", targetResource: "pr#7");

        Assert.True(post.CanHandle(imageRegen));
        Assert.True(post.CanHandle(imageSpec));
        Assert.True(escalate.CanHandle(imageRegen));
        Assert.True(escalate.CanHandle(imageSpec));

        // Sanity: a non-PR TargetResource (path) without an agent id should still NOT match
        // because there's no actionable target. The rung ladder escalates anyway via NoOp
        // attempts at the agent-targeted path, but the gate here is about *handle-ability*.
        var pathOnly = ImageFinding(detectorId: "image-spec-mismatch", targetResource: "art/foo.png");
        Assert.False(post.CanHandle(pathOnly));
        Assert.False(escalate.CanHandle(pathOnly));

        // Sanity: unrelated detector ids stay rejected even with a pr#N TargetResource —
        // the allowlist is the gate, not the target shape.
        var foreign = ImageFinding(detectorId: "deadlock", targetResource: "pr#42");
        Assert.False(post.CanHandle(foreign));
        Assert.False(escalate.CanHandle(foreign));
    }

    /// <summary>
    /// imggen-action-handlers: per-detector label routing — image-spec-mismatch maps to
    /// `art-missing`, image-regen-anomaly maps to `art-regen-noop`, and every other
    /// detector keeps the original `agent-stuck` label for backward compatibility.
    /// </summary>
    [Fact]
    public void EscalateToHumanAction_LabelForFinding_RoutesByDetectorId()
    {
        Assert.Equal(IssueWorkflow.Labels.ArtMissing,
            EscalateToHumanAction.LabelForFinding(ImageFinding(detectorId: "image-spec-mismatch")));
        Assert.Equal(IssueWorkflow.Labels.ArtRegenNoop,
            EscalateToHumanAction.LabelForFinding(ImageFinding(detectorId: "image-regen-anomaly")));
        Assert.Equal(IssueWorkflow.Labels.AgentStuck,
            EscalateToHumanAction.LabelForFinding(StuckFinding()));
        Assert.Equal(IssueWorkflow.Labels.AgentStuck,
            EscalateToHumanAction.LabelForFinding(StuckFinding(detectorId: "phase-completion-mismatch")));
    }

    // ---------------------------------------------------------------------
    // EscalateToHumanAction
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EscalateToHumanAction_AppliesAgentStuckLabelOnOpenPr()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.ListForAgentAsync("Software Engineer 2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformPullRequest>
            {
                new()
                {
                    Number = 7, Title = "Foo", State = "open",
                    Labels = new List<string> { "in-progress" },
                },
            });
        IReadOnlyList<string>? capturedLabels = null;
        pr.Setup(p => p.UpdateAsync(7, null, null,
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, string?, string?, IReadOnlyList<string>?, CancellationToken>(
                (_, _, _, labels, _) => capturedLabels = labels)
            .Returns(Task.CompletedTask);

        var action = new EscalateToHumanAction(
            NullLogger<EscalateToHumanAction>.Instance,
            pr.Object, workItemService: null, notifications: null);

        var outcome = await action.ExecuteAsync(
            StuckFinding(displayName: "Software Engineer 2"), CancellationToken.None);

        Assert.Equal(FlowActionResult.Success, outcome.Result);
        Assert.Equal("pr#7", outcome.Target);
        Assert.NotNull(capturedLabels);
        Assert.Contains("in-progress", capturedLabels);
        Assert.Contains("agent-stuck", capturedLabels);
    }

    [Fact]
    public async Task EscalateToHumanAction_NoOpenWork_ReturnsNoOpWhenNotificationsAlsoMissing()
    {
        var pr = new Mock<IPullRequestService>();
        pr.Setup(p => p.ListForAgentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlatformPullRequest>());
        var workItem = new Mock<IWorkItemService>();
        workItem.Setup(w => w.ListForAgentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlatformWorkItem>());

        var action = new EscalateToHumanAction(
            NullLogger<EscalateToHumanAction>.Instance,
            pr.Object, workItem.Object, notifications: null);

        var outcome = await action.ExecuteAsync(StuckFinding(), CancellationToken.None);

        Assert.Equal(FlowActionResult.NoOp, outcome.Result);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static FlowFinding StuckFinding(
        string detectorId = "agent-stuck",
        string? displayName = "Software Engineer 1",
        string? targetAgentId = "se-1") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DetectedAt = DateTimeOffset.UtcNow,
        DetectorId = detectorId,
        Severity = FlowFindingSeverity.Warning,
        TargetAgentId = targetAgentId,
        TargetResource = targetAgentId,
        TargetDisplayName = displayName,
        Summary = "Agent Software Engineer 1 has been Working for 45m",
        Rationale = "test rationale",
        State = FlowFindingState.Open,
        DedupKey = $"{detectorId}:{targetAgentId}",
    };

    /// <summary>
    /// imggen-action-handlers: image-detector findings carry the actionable target in
    /// <c>TargetResource</c> (e.g. <c>pr#42</c>) and have no <c>TargetAgentId</c>. Used by
    /// <see cref="Actions_AcceptImageDetectorFindings_ViaPrTargetResource"/>.
    /// </summary>
    private static FlowFinding ImageFinding(
        string detectorId,
        string? targetResource = "pr#42",
        string? displayName = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DetectedAt = DateTimeOffset.UtcNow,
        DetectorId = detectorId,
        Severity = FlowFindingSeverity.Warning,
        TargetAgentId = null,
        TargetResource = targetResource,
        TargetDisplayName = displayName,
        Summary = $"Image detector {detectorId} fired",
        Rationale = "test rationale",
        State = FlowFindingState.Open,
        DedupKey = $"{detectorId}:{targetResource}",
    };
}
