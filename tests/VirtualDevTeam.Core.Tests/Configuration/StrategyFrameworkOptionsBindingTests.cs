using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Tests.Configuration;

public class StrategyFrameworkOptionsBindingTests
{
    [Fact]
    public void ParentAndChildBindingsFromOverlappingSections_BothBindWithoutInterference()
    {
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();

        services.Configure<VirtualDevTeamConfig>(configuration.GetSection("VirtualDevTeam"));
        services.Configure<StrategyFrameworkConfig>(configuration.GetSection("VirtualDevTeam:StrategyFramework"));

        using var sp = services.BuildServiceProvider();
        var parent = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value.StrategyFramework;
        var child = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>().Value;

        Assert.True(parent.Enabled);
        Assert.Equal(["copilot-cli", "squad"], parent.EnabledStrategies);
        Assert.True(child.Enabled);
        Assert.Equal(["copilot-cli", "squad"], child.EnabledStrategies);
    }

    [Fact]
    public void ParentPostConfigure_DoesNotOverrideSeparateChildOptions()
    {
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();

        services.Configure<VirtualDevTeamConfig>(configuration.GetSection("VirtualDevTeam"));
        services.PostConfigure<VirtualDevTeamConfig>(cfg =>
        {
            cfg.StrategyFramework.Enabled = false;
            cfg.StrategyFramework.EnabledStrategies.Clear();
        });
        services.Configure<StrategyFrameworkConfig>(configuration.GetSection("VirtualDevTeam:StrategyFramework"));

        using var sp = services.BuildServiceProvider();
        var parent = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value.StrategyFramework;
        var child = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>().Value;

        Assert.False(parent.Enabled);
        Assert.Empty(parent.EnabledStrategies);
        Assert.True(child.Enabled);
        Assert.Equal(["copilot-cli", "squad"], child.EnabledStrategies);
    }

    [Fact]
    public void BindConfiguration_ResolvesFromDIAtResolutionTime()
    {
        // Reproduces the production fix: BindConfiguration(sectionPath) resolves
        // IConfiguration from DI at resolution time, avoiding stale section capture.
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.Configure<VirtualDevTeamConfig>(configuration.GetSection("VirtualDevTeam"));
        services.AddOptions<StrategyFrameworkConfig>()
            .BindConfiguration("VirtualDevTeam:StrategyFramework");

        using var sp = services.BuildServiceProvider();
        var parent = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value.StrategyFramework;
        var child = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>().Value;

        Assert.True(child.Enabled);
        Assert.Equal(["copilot-cli", "squad"], child.EnabledStrategies);
        Assert.True(parent.Enabled);
        Assert.Equal(["copilot-cli", "squad"], parent.EnabledStrategies);
    }

    [Fact]
    public void BindConfiguration_BindsNestedComplexTypes()
    {
        // Verifies complex nested types (TimeoutsConfig, ConcurrencyConfig, etc.)
        // bind correctly through BindConfiguration.
        var data = new Dictionary<string, string?>
        {
            ["VirtualDevTeam:StrategyFramework:Enabled"] = "true",
            ["VirtualDevTeam:StrategyFramework:EnabledStrategies:0"] = "copilot-cli",
            ["VirtualDevTeam:StrategyFramework:Concurrency:GlobalMaxConcurrentProcesses"] = "8",
            ["VirtualDevTeam:StrategyFramework:Timeouts:AgenticSeconds"] = "300",
            ["VirtualDevTeam:StrategyFramework:Evaluator:UseCliNativeJudge"] = "false",
            ["VirtualDevTeam:StrategyFramework:RevisionRound:Enabled"] = "true",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<StrategyFrameworkConfig>()
            .BindConfiguration("VirtualDevTeam:StrategyFramework");

        using var sp = services.BuildServiceProvider();
        var cfg = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>().Value;

        Assert.True(cfg.Enabled);
        Assert.Equal(["copilot-cli"], cfg.EnabledStrategies);
        Assert.Equal(8, cfg.Concurrency.GlobalMaxConcurrentProcesses);
        Assert.Equal(300, cfg.Timeouts.AgenticSeconds);
        Assert.False(cfg.Evaluator.UseCliNativeJudge);
        Assert.True(cfg.RevisionRound.Enabled);
    }

    private static IConfiguration CreateConfiguration()
    {
        var data = new Dictionary<string, string?>
        {
            ["VirtualDevTeam:StrategyFramework:Enabled"] = "true",
            ["VirtualDevTeam:StrategyFramework:EnabledStrategies:0"] = "copilot-cli",
            ["VirtualDevTeam:StrategyFramework:EnabledStrategies:1"] = "squad",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }
}
