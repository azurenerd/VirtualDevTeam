using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.Tests;

public sealed class PullRequestWorkflowPrBodySanitizerTests
{
    [Fact]
    public void SanitizePrBody_StripsExplorationBeforeFirstHeading_PreservingPrefixMetadata()
    {
        var body = "Closes #123\n\n<!-- agent-id: se-1 -->\nI'm exploring the existing codebase first.\nNow let me inspect the src directory.\n## Summary\nReal PR content";

        var sanitized = PullRequestWorkflow.SanitizePrBody(body);

        Assert.Equal("Closes #123\n\n<!-- agent-id: se-1 -->\n## Summary\nReal PR content", sanitized);
    }

    [Fact]
    public void SanitizePrBody_StripsLeadingExplorationLines_WhenNoHeadingExists()
    {
        var body = "Let me try a different approach first.\nNow let me check the csproj files.\nActual PR description without headings.";

        var sanitized = PullRequestWorkflow.SanitizePrBody(body);

        Assert.Equal("Actual PR description without headings.", sanitized);
    }

    [Fact]
    public void SanitizePrBody_LeavesValidBodyWithoutHeadingUntouched()
    {
        var body = "Implements the requested API validation and adds regression coverage.";

        var sanitized = PullRequestWorkflow.SanitizePrBody(body);

        Assert.Equal(body, sanitized);
    }

    [Fact]
    public void SanitizePrBody_HandlesCrLfBodies()
    {
        var body = "Closes #77\r\n\r\nNow let me explore the repository structure:\r\n## Summary\r\nReady to review";

        var sanitized = PullRequestWorkflow.SanitizePrBody(body);

        Assert.Equal("Closes #77\r\n\r\n## Summary\r\nReady to review", sanitized);
    }

    [Fact]
    public void SanitizePrBody_IgnoresHeadingsInsideCodeFences()
    {
        var body = "Let me inspect the output first.\n```text\n# not-a-heading\n```\n## Summary\nActual content";

        var sanitized = PullRequestWorkflow.SanitizePrBody(body);

        Assert.Equal("## Summary\nActual content", sanitized);
    }
}
