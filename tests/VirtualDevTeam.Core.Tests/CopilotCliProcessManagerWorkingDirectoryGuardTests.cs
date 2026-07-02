using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Regression tests for the workspace-leak hard-guard. The 2026-05-11 GridGuardians.Api
/// incident saw agent-authored target-project source files appear at
/// <c>C:\Git\VirtualDevTeam\src\GridGuardians.Api\</c> because the Copilot CLI inherited
/// the Runner's CWD when no working directory was set. These tests pin the contract:
/// <list type="bullet">
///   <item>Null/empty working dir → rejected.</item>
///   <item>Working dir equals the VDT runner repo root → rejected.</item>
///   <item>Working dir is a VDT source project (e.g. <c>src/VirtualDevTeam.Core</c>) → rejected.</item>
///   <item>Working dir is an agent workspace under <c>.agents/</c> or <c>.candidates/</c> → accepted.</item>
///   <item>Working dir is a genuine target-project workspace (e.g. <c>C:\Work\Compliance</c>) → accepted.</item>
/// </list>
/// </summary>
public class CopilotCliProcessManagerWorkingDirectoryGuardTests
{
    private const string RunnerRoot = @"C:\Git\VirtualDevTeam";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsNullOrEmptyWorkingDirectory(string? workingDir)
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(workingDir, RunnerRoot, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("has no working directory", error);
        Assert.Contains("TestSite", error);
    }

    [Fact]
    public void Validate_RejectsRunnerRepoRoot()
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(RunnerRoot, RunnerRoot, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("targets the VDT runner repo root", error);
        Assert.Contains("GridGuardians.Api leak incident", error);
    }

    [Fact]
    public void Validate_RejectsRunnerRepoRoot_TrailingSlashIgnored()
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(RunnerRoot + @"\", RunnerRoot, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("runner repo root", error);
    }

    [Fact]
    public void Validate_RejectsRunnerRepoRoot_CaseInsensitive()
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(@"c:\git\virtualdevteam", RunnerRoot, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("runner repo root", error);
    }

    [Theory]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Core")]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner")]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Agents")]
    public void Validate_RejectsVdtSourceProjects(string workingDir)
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(workingDir, RunnerRoot, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("VDT source project", error);
    }

    [Theory]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner\.agents\sme-1\Compliance")]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner\.agents\software-engineer-1\repo")]
    [InlineData(@"C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner\.candidates\strat-1\repo")]
    public void Validate_AcceptsAgentWorkspacesUnderRunner(string workingDir)
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(workingDir, RunnerRoot, "TestSite");

        Assert.Null(error);
    }

    [Theory]
    [InlineData(@"C:\Work\Compliance")]
    [InlineData(@"D:\Projects\TargetApp")]
    [InlineData(@"C:\Temp\agent-workspace")]
    public void Validate_AcceptsExternalWorkspaces(string workingDir)
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(workingDir, RunnerRoot, "TestSite");

        Assert.Null(error);
    }

    [Fact]
    public void Validate_AcceptsAnythingWhenRunnerRootUnknown()
    {
        // When the runner root can't be located (e.g. packaged install with no sln nearby),
        // the guard falls back to the null/empty check only — still catches the most common bug.
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(RunnerRoot, runnerRoot: null, "TestSite");

        Assert.Null(error);
    }

    [Fact]
    public void Validate_StillRejectsEmptyWhenRunnerRootUnknown()
    {
        var error = CopilotCliProcessManager.ValidateWorkingDirectoryCore(null, runnerRoot: null, "TestSite");

        Assert.NotNull(error);
        Assert.Contains("has no working directory", error);
    }
}
