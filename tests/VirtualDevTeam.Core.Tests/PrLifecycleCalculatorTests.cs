using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.Lifecycle;

namespace VirtualDevTeam.Core.Tests;

public sealed class PrLifecycleCalculatorTests
{
    private static VirtualDevTeamConfig DefaultConfig() => new()
    {
        Review = new ReviewConfig { TestEngineerReviews = true },
    };

    private static PlatformPullRequest MakePr(int number, List<string>? labels = null,
        DateTime? mergedAt = null, string state = "open") => new()
    {
        Number = number,
        Title = $"SoftwareEngineer: Task {number}",
        Labels = labels ?? new List<string>(),
        State = state,
        CreatedAt = DateTime.UtcNow.AddHours(-2),
        MergedAt = mergedAt,
        HeadBranch = "agent/se/task",
        BaseBranch = "main",
    };

    [Fact]
    public void InProgress_OnlyInProgressLabel()
    {
        var pr = MakePr(1, new List<string> { "in-progress" });
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.Development).Status);
        Assert.Equal(StageStatus.NotStarted, result.Stages.First(s => s.Id == StageIds.ArchitectReview).Status);
        Assert.Equal("Software Engineer", result.NextRequiredActor);
    }

    [Fact]
    public void ReadyForReview_DevDone_ArchitectPending()
    {
        var pr = MakePr(1, new List<string> { "ready-for-review" });
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.Equal(StageStatus.Complete, result.Stages.First(s => s.Id == StageIds.Development).Status);
        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.ArchitectReview).Status);
        Assert.Equal("Architect", result.NextRequiredActor);
    }

    [Fact]
    public void ArchitectApproved_TestingPending()
    {
        var pr = MakePr(1, new List<string> { "architect-approved" });
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.Equal(StageStatus.Complete, result.Stages.First(s => s.Id == StageIds.ArchitectReview).Status);
        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.Testing).Status);
        Assert.Equal("Test Engineer", result.NextRequiredActor);
    }

    [Fact]
    public void TestsAdded_PmPending()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "tests-added" });
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.Equal(StageStatus.Complete, result.Stages.First(s => s.Id == StageIds.Testing).Status);
        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.PmReview).Status);
        Assert.Equal("Program Manager", result.NextRequiredActor);
    }

    [Fact]
    public void PmApproved_MergePending()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "tests-added", "pm-approved" });
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.Equal(StageStatus.Complete, result.Stages.First(s => s.Id == StageIds.PmReview).Status);
        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.Merge).Status);
        // SecurityAudit and PeerReview may be Skipped for non-security PRs with no peer agents — exclude both.
        Assert.True(result.Stages
            .Where(s => s.Id != StageIds.Merge && s.Id != StageIds.PeerReview && s.Id != StageIds.SecurityAudit)
            .All(s => s.Status == StageStatus.Complete));
        // SecurityAudit should be Skipped (no audit activity on this PR)
        Assert.Equal(StageStatus.Skipped, result.Stages.First(s => s.Id == StageIds.SecurityAudit).Status);
    }

    [Fact]
    public void Merged_AllComplete()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "tests-added", "pm-approved" },
            mergedAt: DateTime.UtcNow);
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        Assert.True(result.IsMerged);
        Assert.True(result.IsReadyForMerge);
        Assert.Equal(StageStatus.Complete, result.Stages.First(s => s.Id == StageIds.Merge).Status);
    }

    [Fact]
    public void Closed_NotMerged_MergeSkipped()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "pm-approved" },
            state: "closed");
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());

        var merge = result.Stages.First(s => s.Id == StageIds.Merge);
        Assert.Equal(StageStatus.Skipped, merge.Status);
        Assert.Contains("closed without merging", merge.SkipReason);
    }

    [Fact]
    public void TeDisabled_TestingSkipped()
    {
        var config = DefaultConfig();
        config.Review.TestEngineerReviews = false;
        var pr = MakePr(1, new List<string> { "architect-approved" });

        var result = PrLifecycleCalculator.Compute(pr, config);

        var testing = result.Stages.First(s => s.Id == StageIds.Testing);
        Assert.Equal(StageStatus.Skipped, testing.Status);
        Assert.Contains("TestEngineerReviews disabled", testing.SkipReason);
        // PM should be next since testing is skipped
        Assert.Equal(StageStatus.InProgress, result.Stages.First(s => s.Id == StageIds.PmReview).Status);
    }

    [Fact]
    public void SinglePr_PeerReviewSkipped()
    {
        var config = DefaultConfig();
        config.Limits = new LimitsConfig { PrMode = PrDeliveryMode.SinglePR };
        var pr = MakePr(1, new List<string> { "architect-approved" });

        var result = PrLifecycleCalculator.Compute(pr, config);

        var peer = result.Stages.First(s => s.Id == StageIds.PeerReview);
        Assert.Equal(StageStatus.Skipped, peer.Status);
        Assert.Contains("SinglePR", peer.SkipReason);
    }

    [Fact]
    public void PeerReview_DetectedFromComments()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "tests-added", "pm-approved" },
            mergedAt: DateTime.UtcNow);
        var comments = new List<PlatformComment>
        {
            new() { Body = "[SoftwareEngineer 2] Inline Review — APPROVED\n\nGreat code!", CreatedAt = DateTime.UtcNow.AddHours(-1) },
        };

        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig(), comments, hasPeerReviewAgents: true);

        var peer = result.Stages.First(s => s.Id == StageIds.PeerReview);
        Assert.Equal(StageStatus.Complete, peer.Status);
    }

    [Fact]
    public void PeerReview_SkippedWhenNoCommentAndLaterStagesComplete()
    {
        var pr = MakePr(1, new List<string> { "architect-approved", "tests-added", "pm-approved" },
            mergedAt: DateTime.UtcNow);
        // No peer review comment, but hasPeerReviewAgents = true
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig(), hasPeerReviewAgents: true);

        var peer = result.Stages.First(s => s.Id == StageIds.PeerReview);
        Assert.Equal(StageStatus.Skipped, peer.Status);
        Assert.Contains("No peer review comment", peer.SkipReason);
    }

    [Fact]
    public void IsFinalIntegrationPr_DetectsVariants()
    {
        Assert.True(PrLifecycleCalculator.IsFinalIntegrationPr("SoftwareEngineer: Final Integration"));
        Assert.True(PrLifecycleCalculator.IsFinalIntegrationPr("T-FINAL task"));
        Assert.True(PrLifecycleCalculator.IsFinalIntegrationPr("final integration & validation"));
        Assert.False(PrLifecycleCalculator.IsFinalIntegrationPr("SoftwareEngineer: Implement auth"));
        Assert.False(PrLifecycleCalculator.IsFinalIntegrationPr(null));
        Assert.False(PrLifecycleCalculator.IsFinalIntegrationPr(""));
    }

    [Fact]
    public void IsSoftwareEngineerComment_MatchesNumberedAgents()
    {
        Assert.True(PrLifecycleCalculator.IsSoftwareEngineerComment("[SoftwareEngineer] APPROVED"));
        Assert.True(PrLifecycleCalculator.IsSoftwareEngineerComment("[SoftwareEngineer 1] Inline Review — APPROVED"));
        Assert.True(PrLifecycleCalculator.IsSoftwareEngineerComment("[SoftwareEngineer 2] APPROVED"));
        Assert.False(PrLifecycleCalculator.IsSoftwareEngineerComment("[Architect] APPROVED"));
        Assert.False(PrLifecycleCalculator.IsSoftwareEngineerComment("[TestEngineer] No Tests Needed"));
    }

    [Fact]
    public void NullLabels_DoesNotThrow()
    {
        var pr = new PlatformPullRequest
        {
            Number = 1,
            Title = "Test",
            Labels = null!,
            State = "open",
            CreatedAt = DateTime.UtcNow,
            HeadBranch = "test",
            BaseBranch = "main",
        };
        var result = PrLifecycleCalculator.Compute(pr, DefaultConfig());
        Assert.NotNull(result);
        Assert.True(result.Stages.Count >= 4);
    }
}
