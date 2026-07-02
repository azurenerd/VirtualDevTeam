using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests.Workspace;

public class AggregateTestResultTests
{
    [Fact]
    public void AllPassed_WhenAllTiersPass_ReturnsTrue()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 5, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(1), Tier = TestTier.Unit },
                new TestResult { Success = true, Passed = 3, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(2), Tier = TestTier.Integration },
            ]
        };

        Assert.True(aggregate.AllPassed);
    }

    [Fact]
    public void AllPassed_WhenAnyTierFails_ReturnsFalse()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 5, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(1), Tier = TestTier.Unit },
                new TestResult { Success = false, Passed = 2, Failed = 1, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(3), Tier = TestTier.Integration },
            ]
        };

        Assert.False(aggregate.AllPassed);
    }

    [Fact]
    public void Totals_AggregateAcrossTiers()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 10, Failed = 0, Skipped = 1, Output = "", Duration = TimeSpan.FromSeconds(2), Tier = TestTier.Unit },
                new TestResult { Success = true, Passed = 5, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(5), Tier = TestTier.Integration },
                new TestResult { Success = false, Passed = 2, Failed = 1, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(10), Tier = TestTier.UI },
            ]
        };

        Assert.Equal(17, aggregate.TotalPassed);
        Assert.Equal(1, aggregate.TotalFailed);
        Assert.Equal(1, aggregate.TotalSkipped);
        Assert.Equal(19, aggregate.TotalTests);
        Assert.Equal(17, aggregate.TotalDuration.TotalSeconds);
    }

    [Fact]
    public void FormatAsMarkdown_IncludesPerTierBreakdown()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 5, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(1), Tier = TestTier.Unit },
                new TestResult { Success = false, Passed = 2, Failed = 1, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(3), Tier = TestTier.Integration, FailureDetails = ["AuthService.Login_ThrowsOnInvalidCredentials"] },
            ]
        };

        var markdown = aggregate.FormatAsMarkdown();

        Assert.Contains("FAILURES DETECTED", markdown);
        Assert.Contains("Unit Tests", markdown);
        Assert.Contains("Integration Tests", markdown);
        Assert.Contains("5 passed", markdown);
        Assert.Contains("AuthService.Login_ThrowsOnInvalidCredentials", markdown);
        Assert.Contains("Total:", markdown);
    }

    [Fact]
    public void FormatAsMarkdown_AllPassed_ShowsSuccess()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 10, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(2), Tier = TestTier.Unit },
            ]
        };

        var markdown = aggregate.FormatAsMarkdown();

        Assert.Contains("ALL PASSED", markdown);
    }

    [Fact]
    public void FormatAsMarkdown_NoTier_ShowsGeneral()
    {
        var aggregate = new AggregateTestResult
        {
            TierResults =
            [
                new TestResult { Success = true, Passed = 3, Failed = 0, Skipped = 0, Output = "", Duration = TimeSpan.FromSeconds(1), Tier = null },
            ]
        };

        var markdown = aggregate.FormatAsMarkdown();

        Assert.Contains("General Tests", markdown);
    }
}
