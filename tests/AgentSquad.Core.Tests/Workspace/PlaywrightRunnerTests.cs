using AgentSquad.Core.Configuration;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentSquad.Core.Tests.Workspace;

public class PlaywrightRunnerTests : IDisposable
{
    private readonly PlaywrightRunner _runner;
    private readonly string _tempDir;

    public PlaywrightRunnerTests()
    {
        _runner = new PlaywrightRunner(NullLogger<PlaywrightRunner>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "PlaywrightRunnerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Creates a fake csproj file in the temp directory structure.
    /// </summary>
    private string CreateCsproj(string relativePath, string sdk = "Microsoft.NET.Sdk.Web")
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"<Project Sdk=\"{sdk}\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        return fullPath;
    }

    #region RewriteProjectPathForWorkDir Tests

    [Fact]
    public void RewriteProjectPathForWorkDir_SameWorkDir_ReturnsUnchanged()
    {
        var command = "dotnet run --project Sub\\App.csproj --urls http://localhost:5000";
        var result = PlaywrightRunner.RewriteProjectPathForWorkDir(command, _tempDir, _tempDir);
        Assert.Equal(command, result);
    }

    [Fact]
    public void RewriteProjectPathForWorkDir_SubdirWorkDir_RewritesToRelative()
    {
        // This is the exact bug scenario: workspace = repo root, workDir = project subdir
        var workspace = @"C:\Agents\testengineer\ReportingDashboard";
        var workDir = @"C:\Agents\testengineer\ReportingDashboard\ReportingDashboard.Web";
        var command = "dotnet run --project ReportingDashboard.Web\\ReportingDashboard.Web.csproj --urls http://localhost:5834";

        var result = PlaywrightRunner.RewriteProjectPathForWorkDir(command, workspace, workDir);

        Assert.Equal("dotnet run --project ReportingDashboard.Web.csproj --urls http://localhost:5834", result);
    }

    [Fact]
    public void RewriteProjectPathForWorkDir_ForwardSlashes_RewritesCorrectly()
    {
        var workspace = @"C:\Agents\testengineer\Repo";
        var workDir = @"C:\Agents\testengineer\Repo\src\WebApp";
        var command = "dotnet run --project src/WebApp/WebApp.csproj --urls http://localhost:5000";

        var result = PlaywrightRunner.RewriteProjectPathForWorkDir(command, workspace, workDir);

        Assert.Equal("dotnet run --project WebApp.csproj --urls http://localhost:5000", result);
    }

    [Fact]
    public void RewriteProjectPathForWorkDir_NoProjectFlag_ReturnsUnchanged()
    {
        var command = "dotnet run --urls http://localhost:5000";
        var result = PlaywrightRunner.RewriteProjectPathForWorkDir(command, @"C:\Workspace", @"C:\Workspace\Sub");
        Assert.Equal(command, result);
    }

    [Fact]
    public void RewriteProjectPathForWorkDir_QuotedProjectPath_RewritesCorrectly()
    {
        var workspace = @"C:\Agents\Repo";
        var workDir = @"C:\Agents\Repo\My App";
        var command = "dotnet run --project \"My App\\MyApp.csproj\" --urls http://localhost:5000";

        var result = PlaywrightRunner.RewriteProjectPathForWorkDir(command, workspace, workDir);

        Assert.Equal("dotnet run --project \"MyApp.csproj\" --urls http://localhost:5000", result);
    }

    #endregion

    #region ResolveAppStartCommand Tests

    [Fact]
    public void ResolveAppStartCommand_ConfiguredPathExists_ReturnsOriginalCommand()
    {
        CreateCsproj(@"src\MyApp\MyApp.csproj");
        var config = new WorkspaceConfig { AppStartCommand = @"dotnet run --project src\MyApp\MyApp.csproj" };

        var result = _runner.ResolveAppStartCommand(_tempDir, config);

        Assert.Equal(config.AppStartCommand, result);
    }

    [Fact]
    public void ResolveAppStartCommand_ConfiguredPathMissing_AutoResolves()
    {
        // Config says src/MyApp/MyApp.csproj but actual is MyApp/MyApp.csproj
        CreateCsproj(@"MyApp\MyApp.csproj");
        var config = new WorkspaceConfig { AppStartCommand = @"dotnet run --project src\MyApp\MyApp.csproj" };

        var result = _runner.ResolveAppStartCommand(_tempDir, config);

        Assert.Contains(@"MyApp\MyApp.csproj", result);
        Assert.DoesNotContain(@"src\MyApp", result);
    }

    [Fact]
    public void ResolveAppStartCommand_PrefersWebSdk_OverModels()
    {
        // Simulates the ReportingDashboard structure
        CreateCsproj(@"ReportingDashboard.Models\ReportingDashboard.Models.csproj", sdk: "Microsoft.NET.Sdk");
        CreateCsproj(@"ReportingDashboard.Web\ReportingDashboard.Web.csproj", sdk: "Microsoft.NET.Sdk.Web");
        var config = new WorkspaceConfig { AppStartCommand = @"dotnet run --project src\NonExistent\NonExistent.csproj" };

        var result = _runner.ResolveAppStartCommand(_tempDir, config);

        Assert.Contains("ReportingDashboard.Web", result);
        Assert.DoesNotContain("Models", result);
    }

    #endregion

    #region ResolveAppProjectDirectory Tests

    [Fact]
    public void ResolveAppProjectDirectory_ProjectInSubdir_ReturnsSubdir()
    {
        CreateCsproj(@"ReportingDashboard.Web\ReportingDashboard.Web.csproj");
        var command = @"dotnet run --project ReportingDashboard.Web\ReportingDashboard.Web.csproj";

        var result = _runner.ResolveAppProjectDirectory(_tempDir, command);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(_tempDir, "ReportingDashboard.Web"), result);
    }

    [Fact]
    public void ResolveAppProjectDirectory_NoProjectFlag_FallsBackToCsprojSearch()
    {
        CreateCsproj(@"WebApp\WebApp.csproj");
        var command = "dotnet run --urls http://localhost:5000";

        var result = _runner.ResolveAppProjectDirectory(_tempDir, command);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(_tempDir, "WebApp"), result);
    }

    #endregion

    #region RankCsprojCandidates Tests

    [Fact]
    public void RankCsprojCandidates_PrefersWebSdk()
    {
        var modelsPath = CreateCsproj(@"Models\Models.csproj", sdk: "Microsoft.NET.Sdk");
        var webPath = CreateCsproj(@"Web\Web.csproj", sdk: "Microsoft.NET.Sdk.Web");

        var result = _runner.RankCsprojCandidates(new[] { modelsPath, webPath });

        Assert.Equal(webPath, result.First());
    }

    [Fact]
    public void RankCsprojCandidates_EmptyInput_ReturnsEmpty()
    {
        var result = _runner.RankCsprojCandidates(Enumerable.Empty<string>());
        Assert.Empty(result);
    }

    #endregion

    #region End-to-End Path Resolution Tests

    [Fact]
    public void EndToEnd_SubdirProject_CommandPathMatchesWorkDir()
    {
        // Reproduce the exact TE bug scenario:
        // 1. Workspace has ReportingDashboard.Web\ReportingDashboard.Web.csproj
        // 2. Config --project path doesn't exist, auto-resolves to ReportingDashboard.Web\...
        // 3. WorkDir resolves to ReportingDashboard.Web\
        // 4. Command --project path must be relative to WorkDir, not workspace root
        CreateCsproj(@"ReportingDashboard.Web\ReportingDashboard.Web.csproj");
        CreateCsproj(@"ReportingDashboard.Models\ReportingDashboard.Models.csproj", sdk: "Microsoft.NET.Sdk");

        var config = new WorkspaceConfig
        {
            AppStartCommand = @"dotnet run --project src\ReportingDashboard\ReportingDashboard.csproj --urls http://localhost:5834"
        };

        // Step 1: Resolve command (auto-detect since configured path doesn't exist)
        var appCommand = _runner.ResolveAppStartCommand(_tempDir, config);
        Assert.Contains("ReportingDashboard.Web", appCommand);

        // Step 2: Resolve working directory
        var appWorkDir = _runner.ResolveAppProjectDirectory(_tempDir, appCommand) ?? _tempDir;
        Assert.Equal(Path.Combine(_tempDir, "ReportingDashboard.Web"), appWorkDir);

        // Step 3: Rewrite --project path for new WorkingDirectory
        var finalCommand = PlaywrightRunner.RewriteProjectPathForWorkDir(appCommand, _tempDir, appWorkDir);

        // Step 4: Verify the --project path exists relative to the WorkingDirectory
        var projectMatch = System.Text.RegularExpressions.Regex.Match(
            finalCommand, @"--project\s+""?([^""]+\.csproj)""?");
        Assert.True(projectMatch.Success, "Command should contain --project flag");

        var projectRelativePath = projectMatch.Groups[1].Value;
        var projectFullPath = Path.Combine(appWorkDir, projectRelativePath);
        Assert.True(File.Exists(projectFullPath),
            $"Project file should exist at {projectFullPath} (relative: {projectRelativePath}, workDir: {appWorkDir})");
    }

    [Fact]
    public void EndToEnd_RootProject_CommandPathUnchanged()
    {
        // When the csproj is at the workspace root, no rewrite needed
        CreateCsproj("MyApp.csproj");
        var config = new WorkspaceConfig
        {
            AppStartCommand = "dotnet run --project MyApp.csproj --urls http://localhost:5000"
        };

        var appCommand = _runner.ResolveAppStartCommand(_tempDir, config);
        var appWorkDir = _runner.ResolveAppProjectDirectory(_tempDir, appCommand) ?? _tempDir;

        // WorkDir should be workspace root (csproj is there)
        Assert.Equal(_tempDir, appWorkDir);

        var finalCommand = PlaywrightRunner.RewriteProjectPathForWorkDir(appCommand, _tempDir, appWorkDir);
        Assert.Equal(appCommand, finalCommand); // No change needed
    }

    #endregion
}
