using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class StrategyFrameworkConfigTests
{
    [Fact]
    public void Defaults_match_user_locked_decisions()
    {
        var cfg = new StrategyFrameworkConfig();

        // Phase 1: framework defaults to OFF until baseline-contract (p1-baseline-contract)
        // replaces the marker-file stub with real single-shot SE generation. This prevents
        // the live SE pipeline from accidentally shipping no-op marker PRs when a runner
        // omits the StrategyFramework section from appsettings.
        Assert.False(cfg.Enabled);
        Assert.Equal("always", cfg.SamplingPolicy);
        Assert.Equal("full-review", cfg.PostWinnerFlow);
    }

    [Fact]
    public void Default_enabled_strategies_is_empty_to_force_explicit_opt_in()
    {
        var cfg = new StrategyFrameworkConfig();

        // Empty default prevents .NET IConfiguration.Bind from APPENDING
        // config file entries to the default list (which produced duplicates
        // like baseline,mcp-enhanced,baseline,agentic-delegation at runtime).
        // Every deployment must explicitly list EnabledStrategies in config.
        Assert.Empty(cfg.EnabledStrategies);
    }

    [Fact]
    public void Global_cap_is_below_sum_of_pools_to_prevent_overload()
    {
        var cfg = new StrategyFrameworkConfig();
        var sumOfPools = cfg.Concurrency.SingleShotSlots
                         + cfg.Concurrency.CandidateSlots
                         + cfg.Concurrency.AgenticSlots;

        Assert.True(cfg.Concurrency.GlobalMaxConcurrentProcesses <= sumOfPools,
            "GlobalMaxConcurrentProcesses must cap total concurrent copilot processes below raw pool sum.");
    }

    [Fact]
    public void Reserved_evaluator_path_is_under_tests_dir()
    {
        var cfg = new StrategyFrameworkConfig();
        Assert.StartsWith("tests/", cfg.Evaluator.ReservedPathPrefix);
    }
}
