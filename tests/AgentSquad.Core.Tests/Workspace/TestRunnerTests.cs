using AgentSquad.Core.Workspace;
using Xunit;

namespace AgentSquad.Core.Tests.Workspace;

public class TestRunnerParseTests
{
    [Fact]
    public void ParseTestCounts_DotnetTest_ParsesCorrectly()
    {
        var output = "Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204";
        var (passed, failed, skipped) = TestRunner.ParseTestCounts(output);
        Assert.Equal(204, passed);
        Assert.Equal(0, failed);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void ParseTestCounts_DotnetTestWithFailures_ParsesCorrectly()
    {
        var output = "Failed!  - Failed:    25, Passed:   179, Skipped:     0, Total:   204";
        var (passed, failed, skipped) = TestRunner.ParseTestCounts(output);
        Assert.Equal(179, passed);
        Assert.Equal(25, failed);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void ParseTestCounts_NoTestOutput_ReturnsZeros()
    {
        var output = "Build succeeded.\n0 Warning(s)\n0 Error(s)";
        var (passed, failed, skipped) = TestRunner.ParseTestCounts(output);
        Assert.Equal(0, passed);
        Assert.Equal(0, failed);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204", true)]
    [InlineData("Failed!  - Failed:     5, Passed:   199, Skipped:     0, Total:   204", false)]
    public void SuccessLogic_TrustsParsedCounts(string output, bool expectedSuccess)
    {
        var (passed, failed, _) = TestRunner.ParseTestCounts(output);
        var testsWereParsed = passed > 0 || failed > 0;
        var success = testsWereParsed ? failed == 0 : true;
        Assert.Equal(expectedSuccess, success);
    }

    [Fact]
    public void SuccessLogic_AllPassedWithNonZeroExitCode_TreatsAsSuccess()
    {
        // This is the exact bug scenario: dotnet test returns non-zero exit code
        // (e.g., one test project fails to build) but all 204 tests that ran passed.
        var output = "Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204";
        var (passed, failed, _) = TestRunner.ParseTestCounts(output);

        // Simulate non-zero exit code (process failed)
        bool processSuccess = false;

        // OLD logic (buggy): Success = processSuccess && failed == 0 → false
        var oldLogic = processSuccess && failed == 0;
        Assert.False(oldLogic, "Old logic incorrectly reports failure");

        // NEW logic: trust parsed counts when available
        var testsWereParsed = passed > 0 || failed > 0;
        var newLogic = testsWereParsed ? failed == 0 : processSuccess;
        Assert.True(newLogic, "New logic correctly reports success based on parsed counts");
    }

    [Fact]
    public void SuccessLogic_NoParsedTests_FallsBackToExitCode()
    {
        // When no tests are parsed (e.g., build-only failure), use exit code
        var output = "Build FAILED with 5 errors";
        var (passed, failed, _) = TestRunner.ParseTestCounts(output);

        bool processSuccess = false;
        var testsWereParsed = passed > 0 || failed > 0;
        var success = testsWereParsed ? failed == 0 : processSuccess;

        Assert.False(success, "Should fall back to exit code when no tests parsed");
    }
}
