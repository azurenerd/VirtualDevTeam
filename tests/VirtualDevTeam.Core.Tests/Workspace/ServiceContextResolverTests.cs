using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests.Workspace;

public class ServiceContextResolverTests
{
    private static ServiceContextResolver CreateResolver(VirtualDevTeamConfig? config = null)
    {
        config ??= new VirtualDevTeamConfig();
        var options = new OptionsWrapper<VirtualDevTeamConfig>(config);
        var monitor = Microsoft.Extensions.Options.Options.Create(config);
        // OptionsMonitor isn't easy to mock — use a test wrapper
        return new ServiceContextResolver(
            new TestOptionsMonitor(config),
            NullLogger<ServiceContextResolver>.Instance);
    }

    [Fact]
    public void GetBuildSpec_NoService_UsesGlobalConfig()
    {
        var config = new VirtualDevTeamConfig
        {
            Workspace = new WorkspaceConfig { BuildCommand = "dotnet build", BuildTimeoutSeconds = 120 }
        };
        var resolver = CreateResolver(config);

        var spec = resolver.GetBuildSpec("/workspace");

        Assert.Equal("dotnet build", spec.Command);
        Assert.Equal("/workspace", spec.WorkingDirectory);
        Assert.Equal(120, spec.TimeoutSeconds);
    }

    [Fact]
    public void GetBuildSpec_WithService_UsesServiceCommand()
    {
        var config = new VirtualDevTeamConfig
        {
            Workspace = new WorkspaceConfig { BuildCommand = "dotnet build" },
            LargeProject = new LargeProjectConfig
            {
                Enabled = true,
                Services = [new ServiceDefinition { Name = "auth", Path = "src/auth", BuildCommand = "dotnet build Auth.csproj" }]
            }
        };
        var resolver = CreateResolver(config);
        var service = config.LargeProject.Services[0];

        var spec = resolver.GetBuildSpec("/workspace", service);

        Assert.Equal("dotnet build Auth.csproj", spec.Command);
        Assert.Contains("src/auth", spec.WorkingDirectory);
    }

    [Fact]
    public void FindService_CaseInsensitive()
    {
        var config = new VirtualDevTeamConfig
        {
            LargeProject = new LargeProjectConfig
            {
                Enabled = true,
                Services = [new ServiceDefinition { Name = "Auth-API", Path = "src/auth" }]
            }
        };
        var resolver = CreateResolver(config);

        var service = resolver.FindService("auth-api");
        Assert.NotNull(service);
        Assert.Equal("Auth-API", service!.Name);
    }

    [Fact]
    public void FindServiceForPath_LongestPrefixMatch()
    {
        var config = new VirtualDevTeamConfig
        {
            LargeProject = new LargeProjectConfig
            {
                Enabled = true,
                Services =
                [
                    new ServiceDefinition { Name = "src", Path = "src" },
                    new ServiceDefinition { Name = "auth", Path = "src/services/auth" }
                ]
            }
        };
        var resolver = CreateResolver(config);

        var service = resolver.FindServiceForPath("src/services/auth/Controllers/AuthController.cs");
        Assert.NotNull(service);
        Assert.Equal("auth", service!.Name);
    }

    [Fact]
    public void FindServiceForPath_NoMatch_ReturnsNull()
    {
        var config = new VirtualDevTeamConfig
        {
            LargeProject = new LargeProjectConfig
            {
                Enabled = true,
                Services = [new ServiceDefinition { Name = "auth", Path = "src/auth" }]
            }
        };
        var resolver = CreateResolver(config);

        var service = resolver.FindServiceForPath("tests/unit/SomeTest.cs");
        Assert.Null(service);
    }

    [Fact]
    public void GetServices_WhenDisabled_ReturnsEmpty()
    {
        var config = new VirtualDevTeamConfig
        {
            LargeProject = new LargeProjectConfig { Enabled = false }
        };
        var resolver = CreateResolver(config);

        Assert.Empty(resolver.GetServices());
    }

    [Fact]
    public void ServiceDefinition_EffectiveDisplayName_FallsBackToName()
    {
        var svc = new ServiceDefinition { Name = "auth-api", Path = "src/auth" };
        Assert.Equal("auth-api", svc.EffectiveDisplayName);

        var svc2 = new ServiceDefinition { Name = "auth-api", Path = "src/auth", DisplayName = "Auth API" };
        Assert.Equal("Auth API", svc2.EffectiveDisplayName);
    }

    /// <summary>Test adapter for IOptionsMonitor — returns the same config on every call.</summary>
    private class TestOptionsMonitor : IOptionsMonitor<VirtualDevTeamConfig>
    {
        public TestOptionsMonitor(VirtualDevTeamConfig value) => CurrentValue = value;
        public VirtualDevTeamConfig CurrentValue { get; }
        public VirtualDevTeamConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<VirtualDevTeamConfig, string?> listener) => null;
    }
}
