using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests.Workspace;

public class WorkspaceModeTests
{
    [Fact]
    public void WorkspaceConfig_DefaultsToCloneMode()
    {
        var config = new WorkspaceConfig();
        Assert.Equal(WorkspaceMode.Clone, config.WorkspaceMode);
        Assert.False(config.IsWorktreeMode);
        Assert.False(config.IsInPlaceMode);
    }

    [Fact]
    public void WorkspaceConfig_WorktreeMode_IsWorktreeTrue()
    {
        var config = new WorkspaceConfig { WorkspaceMode = WorkspaceMode.Worktree };
        Assert.True(config.IsWorktreeMode);
        Assert.False(config.IsInPlaceMode);
    }

    [Fact]
    public void WorkspaceConfig_InPlaceMode_BothPropertiesCorrect()
    {
        var config = new WorkspaceConfig { WorkspaceMode = WorkspaceMode.InPlace };
        Assert.True(config.IsWorktreeMode);
        Assert.True(config.IsInPlaceMode);
    }

    [Fact]
    public void WorkspaceConfig_SparseCheckoutPaths_DefaultsToEmpty()
    {
        var config = new WorkspaceConfig();
        Assert.NotNull(config.SparseCheckoutPaths);
        Assert.Empty(config.SparseCheckoutPaths);
    }

    [Fact]
    public void WorkspaceConfig_RequireCleanHostTree_DefaultsToTrue()
    {
        var config = new WorkspaceConfig();
        Assert.True(config.RequireCleanHostTree);
    }

    [Fact]
    public void SharedCloneManager_IsVdtWorktree_ReturnsFalse_WhenNoMarkerFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdt-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.False(SharedCloneManager.IsVdtWorktree(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SharedCloneManager_IsVdtWorktree_ReturnsTrue_WhenMarkerFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdt-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, SharedCloneManager.WorktreeMarkerFileName), "test-agent");
        try
        {
            Assert.True(SharedCloneManager.IsVdtWorktree(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IAgentWorkspace_LocalWorkspace_ImplementsInterface()
    {
        // Verify LocalWorkspace implements IAgentWorkspace at compile time
        IAgentWorkspace? workspace = null;
        var config = new WorkspaceConfig { RootPath = Path.GetTempPath() };

        // This would fail at compile time if LocalWorkspace doesn't implement the interface
        workspace = new LocalWorkspace(config, "test-agent", "https://github.com/test/repo.git", "main",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.NotNull(workspace);
        Assert.Equal(WorkspaceMode.Clone, workspace.Mode);
    }

    [Fact]
    public void GitWorktreeManager_ResolveGitCommonDir_ReturnsFullPathForNonGitDir()
    {
        // For a non-git directory, ResolveGitCommonDir should return the path itself
        var tempDir = Path.Combine(Path.GetTempPath(), "vdt-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = VirtualDevTeam.Core.Strategies.GitWorktreeManager.ResolveGitCommonDir(tempDir);
            Assert.Equal(Path.GetFullPath(tempDir), result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
