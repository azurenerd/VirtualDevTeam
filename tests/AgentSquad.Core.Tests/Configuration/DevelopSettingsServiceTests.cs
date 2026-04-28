using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Tests.Configuration;

public class DevelopSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public DevelopSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agentsquad-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "develop-settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private DevelopSettingsService CreateService(AgentSquadConfig? config = null)
    {
        var logger = NullLogger<DevelopSettingsService>.Instance;
        var options = config is not null ? Options.Create(config) : null;
        return new DevelopSettingsService(logger, options, _filePath);
    }

    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileDoesNotExist()
    {
        using var svc = CreateService();
        var settings = await svc.LoadAsync();

        Assert.NotNull(settings);
        Assert.Equal("AzureDevOps", settings.Platform);
        Assert.True(string.IsNullOrEmpty(settings.Description));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        using var svc = CreateService();
        var original = new DevelopSettings
        {
            Platform = "AzureDevOps",
            Description = "Test project",
            TechStack = ".NET 8",
            AzureDevOps = new AdoRepoSettings
            {
                Organization = "myorg",
                Project = "myproj",
                Repository = "myrepo"
            }
        };

        await svc.SaveAsync(original);
        var loaded = await svc.LoadAsync();

        Assert.Equal("AzureDevOps", loaded.Platform);
        Assert.Equal("Test project", loaded.Description);
        Assert.Equal(".NET 8", loaded.TechStack);
        Assert.Equal("myorg", loaded.AzureDevOps.Organization);
    }

    [Fact]
    public async Task LoadAsync_SeedsFromExistingConfig_WhenFileDoesNotExist()
    {
        var config = new AgentSquadConfig();
        config.DevPlatform.Platform = DevPlatformType.AzureDevOps;
        config.DevPlatform.AzureDevOps = new AzureDevOpsConfig
        {
            Organization = "contoso",
            Project = "widgets",
            Repository = "widgets-repo"
        };
        config.Project.Description = "Widget builder";

        using var svc = CreateService(config);
        var settings = await svc.LoadAsync();

        Assert.Equal("AzureDevOps", settings.Platform);
        Assert.Equal("contoso", settings.AzureDevOps.Organization);
        Assert.Equal("Widget builder", settings.Description);
        Assert.True(File.Exists(_filePath));
    }

    [Fact]
    public void MergeIntoConfig_OverlaysProjectFields()
    {
        var config = new AgentSquadConfig();
        var settings = new DevelopSettings
        {
            Platform = "AzureDevOps",
            Description = "New description",
            TechStack = "React + Node",
            ParentWorkItemId = 42,
            AzureDevOps = new AdoRepoSettings
            {
                Organization = "org",
                Project = "proj",
                Repository = "repo",
                DefaultBranch = "develop"
            }
        };

        using var svc = CreateService();
        svc.MergeIntoConfig(config, settings);

        Assert.Equal("New description", config.Project.Description);
        Assert.Equal("React + Node", config.Project.TechStack);
        Assert.Equal(42, config.Project.ParentWorkItemId);
        Assert.Equal(DevPlatformType.AzureDevOps, config.DevPlatform.Platform);
        Assert.Equal("org", config.DevPlatform.AzureDevOps!.Organization);
    }

    [Fact]
    public void MergeIntoConfig_DoesNotOverwriteEmptyFields()
    {
        var config = new AgentSquadConfig();
        config.Project.Description = "Original";
        config.Project.TechStack = "Python";

        var settings = new DevelopSettings
        {
            Platform = "GitHub",
            Description = "",
            TechStack = "Go"
        };

        using var svc = CreateService();
        svc.MergeIntoConfig(config, settings);

        Assert.Equal("Original", config.Project.Description);
        Assert.Equal("Go", config.Project.TechStack);
    }

    [Fact]
    public void CreateFromExistingConfig_MapsAllFields()
    {
        var config = new AgentSquadConfig();
        config.DevPlatform.Platform = DevPlatformType.GitHub;
        config.DevPlatform.AuthMethod = DevPlatformAuthMethod.Pat;
        config.Project.GitHubRepo = "owner/repo";
        config.Project.DefaultBranch = "main";
        config.Project.Description = "My project";
        config.Project.TechStack = "Rust";
        config.Project.ParentWorkItemId = 99;

        using var svc = CreateService();
        var settings = svc.CreateFromExistingConfig(config);

        Assert.Equal("GitHub", settings.Platform);
        Assert.Equal("Pat", settings.AuthMethod);
        Assert.Equal("owner/repo", settings.GitHub.Repo);
        Assert.Equal("main", settings.GitHub.DefaultBranch);
        Assert.Equal("My project", settings.Description);
        Assert.Equal("Rust", settings.TechStack);
        Assert.Equal(99, settings.ParentWorkItemId);
    }
}
