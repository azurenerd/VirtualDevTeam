using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Workspace;
using Xunit;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for CLI edit mode infrastructure: git status parsing, invocation context preservation.
/// </summary>
public class CliEditModeTests
{
    #region ParseStatusPorcelain

    [Fact]
    public void ParseStatusPorcelain_Modified_ReturnsFilePath()
    {
        var result = LocalWorkspace.ParseStatusPorcelain(" M src/MyFile.cs\n");
        Assert.Single(result);
        Assert.Equal("src/MyFile.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_Added_ReturnsFilePath()
    {
        var result = LocalWorkspace.ParseStatusPorcelain("A  src/NewFile.cs\n");
        Assert.Single(result);
        Assert.Equal("src/NewFile.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_Deleted_ReturnsFilePath()
    {
        var result = LocalWorkspace.ParseStatusPorcelain(" D src/OldFile.cs\n");
        Assert.Single(result);
        Assert.Equal("src/OldFile.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_Untracked_ReturnsFilePath()
    {
        var result = LocalWorkspace.ParseStatusPorcelain("?? src/UntrackedFile.cs\n");
        Assert.Single(result);
        Assert.Equal("src/UntrackedFile.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_Renamed_ReturnsNewPath()
    {
        // Git porcelain v1 format for renames: "R  old_name -> new_name"
        var result = LocalWorkspace.ParseStatusPorcelain("R  src/OldName.cs -> src/NewName.cs\n");
        Assert.Single(result);
        Assert.Equal("src/NewName.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_MultipleFiles_ReturnsAll()
    {
        var output = " M src/A.cs\n M src/B.cs\nA  src/C.cs\n?? src/D.cs\n";
        var result = LocalWorkspace.ParseStatusPorcelain(output);
        Assert.Equal(4, result.Count);
        Assert.Contains("src/A.cs", result);
        Assert.Contains("src/B.cs", result);
        Assert.Contains("src/C.cs", result);
        Assert.Contains("src/D.cs", result);
    }

    [Fact]
    public void ParseStatusPorcelain_EmptyOutput_ReturnsEmptyList()
    {
        var result = LocalWorkspace.ParseStatusPorcelain("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseStatusPorcelain_NullOutput_ReturnsEmptyList()
    {
        var result = LocalWorkspace.ParseStatusPorcelain(null);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseStatusPorcelain_QuotedPaths_HandlesCorrectly()
    {
        // Git quotes paths with special characters
        var result = LocalWorkspace.ParseStatusPorcelain(" M \"src/path with spaces/File.cs\"\n");
        Assert.Single(result);
        Assert.Equal("src/path with spaces/File.cs", result[0]);
    }

    [Fact]
    public void ParseStatusPorcelain_WindowsPaths_NormalizesToForwardSlash()
    {
        var result = LocalWorkspace.ParseStatusPorcelain(" M src\\SubDir\\File.cs\n");
        Assert.Single(result);
        Assert.Equal("src/SubDir/File.cs", result[0]);
    }

    #endregion

    #region CopilotCliInvocationContext

    [Fact]
    public void CopilotCliInvocationContext_AllowFileEdits_SetsAllowToolUsage()
    {
        var ctx = new CopilotCliInvocationContext(AllowFileEdits: true);
        Assert.True(ctx.AllowToolUsage);
    }

    [Fact]
    public void CopilotCliInvocationContext_NoFileEditsNoTools_DisallowsToolUsage()
    {
        var ctx = new CopilotCliInvocationContext(AllowFileEdits: false);
        Assert.False(ctx.AllowToolUsage);
    }

    [Fact]
    public void CopilotCliInvocationContext_McpToolsWithoutFileEdits_AllowsToolUsage()
    {
        var ctx = new CopilotCliInvocationContext(
            AllowFileEdits: false,
            AllowedMcpTools: new[] { "tool1" });
        Assert.True(ctx.AllowToolUsage);
    }

    [Fact]
    public void PushInvocationContext_PreservesAllowFileEdits()
    {
        // Verify that pushing a context with AllowFileEdits preserves it
        var originalCtx = new CopilotCliInvocationContext(AllowFileEdits: true, OverrideWorkingDirectory: "/tmp/workspace");
        using var scope = AgentCallContext.PushInvocationContext(originalCtx);

        var current = AgentCallContext.CurrentInvocationContext;
        Assert.NotNull(current);
        Assert.True(current.AllowFileEdits);
        Assert.Equal("/tmp/workspace", current.OverrideWorkingDirectory);
    }

    [Fact]
    public void PushInvocationContext_CleansUpOnDispose()
    {
        var ctx = new CopilotCliInvocationContext(AllowFileEdits: true);
        var scope = AgentCallContext.PushInvocationContext(ctx);
        Assert.NotNull(AgentCallContext.CurrentInvocationContext);

        scope.Dispose();
        Assert.Null(AgentCallContext.CurrentInvocationContext);
    }

    #endregion
}
