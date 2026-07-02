using VirtualDevTeam.Core.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.Core.Tests;

public class CliInteractiveWatchdogTests
{
    private readonly CliInteractiveWatchdog _watchdog = new(NullLogger.Instance, autoApprove: true);
    private readonly CliInteractiveWatchdog _noAutoApprove = new(NullLogger.Instance, autoApprove: false);

    [Theory]
    [InlineData("Do you want to continue? [y/N]")]
    [InlineData("Proceed? [Y/n]")]
    [InlineData("Are you sure? (yes/no)")]
    [InlineData("Overwrite file? y/n")]
    public void DetectsYesNoPrompts(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.Respond, action!.Type);
        Assert.Equal("y", action.Response);
    }

    [Theory]
    [InlineData("Do you want to continue?")]
    [InlineData("Proceed with changes?")]
    [InlineData("Confirm deployment?")]
    public void DetectsContinuePrompts(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.Respond, action!.Type);
        Assert.Equal("y", action.Response);
    }

    [Theory]
    [InlineData("Press Enter to continue")]
    [InlineData("Press any key to proceed")]
    [InlineData("Press return to confirm")]
    public void DetectsPressEnterPrompts(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.Respond, action!.Type);
        Assert.Equal("", action.Response);
    }

    [Theory]
    [InlineData("Select an option:")]
    [InlineData("Choose a framework:")]
    [InlineData("Pick a language:")]
    public void DetectsSelectionPrompts(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.Respond, action!.Type);
        Assert.Equal("1", action.Response);
    }

    [Theory]
    [InlineData("password:")]
    [InlineData("Enter your API key:")]
    [InlineData("token:")]
    [InlineData("Enter credentials:")]
    [InlineData("GitHub token:")]
    public void DetectsCredentialPrompts_FailsFast(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.FailFast, action!.Type);
        Assert.Null(action.Response);
    }

    [Theory]
    [InlineData("error: permission denied")]
    [InlineData("error: unauthorized")]
    [InlineData("Authentication failed")]
    [InlineData("Not logged in")]
    [InlineData("Not authenticated")]
    [InlineData("401 Unauthorized")]
    [InlineData("access denied")]
    public void DetectsAuthFailures_FailsFast(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.FailFast, action!.Type);
    }

    [Theory]
    [InlineData("Handle unauthorized access by returning a 401 status code")]
    [InlineData("The API should reject requests without permission and return forbidden")]
    [InlineData("Users who are not authenticated should be redirected to the login page")]
    public void DoesNotFalsePositiveOnAuthWords_InContent(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.Null(action);
    }

    [Theory]
    [InlineData("Here is your code output")]
    [InlineData("The function returns 42")]
    [InlineData("")]
    [InlineData("   ")]
    public void IgnoresNonPromptOutput(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.Null(action);
    }

    [Fact]
    public void NoAutoApprove_IgnoresYesNoPrompts()
    {
        var action = _noAutoApprove.DetectPrompt("Continue? [y/N]");
        Assert.Null(action);
    }

    [Fact]
    public void NoAutoApprove_StillDetectsCredentialPrompts()
    {
        var action = _noAutoApprove.DetectPrompt("password:");
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.FailFast, action!.Type);
    }

    [Theory]
    [InlineData("▸ Option A (default)")]
    [InlineData("❯ First choice")]
    [InlineData("› Select this")]
    public void DetectsArrowSelectionPrompts(string line)
    {
        var action = _watchdog.DetectPrompt(line);
        Assert.NotNull(action);
        Assert.Equal(WatchdogActionType.Respond, action!.Type);
        Assert.Equal("", action.Response);
    }
}
