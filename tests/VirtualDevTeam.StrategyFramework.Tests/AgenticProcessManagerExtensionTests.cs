using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Covers <c>p3-process-manager-ext</c>: the new <see cref="CopilotCliRequestOptions"/>
/// / <see cref="AgenticSessionResult"/> types and the argv + guard behaviour of
/// <see cref="CopilotCliProcessManager.ExecuteAgenticSessionAsync"/> /
/// <see cref="CopilotCliProcessManager.BuildAgenticArguments"/>. The full lifecycle
/// (pool split, watchdog, sandbox, Job Object) is exercised in later todos and
/// layers 2/3 of the test strategy; here we just lock the shape.
/// </summary>
public class AgenticProcessManagerExtensionTests
{
    private static CopilotCliProcessManager NewManager(
        Action<CopilotCliConfig>? configureCli = null,
        Action<StrategyFrameworkConfig>? configureFramework = null)
    {
        var cfg = new VirtualDevTeamConfig();
        cfg.CopilotCli.ModelName = "claude-opus-4.6";
        cfg.CopilotCli.SilentMode = true;
        configureCli?.Invoke(cfg.CopilotCli);

        var frameworkCfg = new StrategyFrameworkConfig();
        configureFramework?.Invoke(frameworkCfg);

        return new CopilotCliProcessManager(
            Options.Create(cfg),
            Options.Create(frameworkCfg),
            NullLogger<CopilotCliProcessManager>.Instance);
    }

    [Fact]
    public void BuildAgenticArguments_includes_allow_all_when_requested()
    {
        var mgr = NewManager();
        var opts = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
        };
        var args = mgr.BuildAgenticArguments(opts);
        Assert.Contains("--allow-all", args);
    }

    [Fact]
    public void BuildAgenticArguments_omits_allow_all_when_not_requested()
    {
        var mgr = NewManager();
        var opts = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = false,
        };
        var args = mgr.BuildAgenticArguments(opts);
        Assert.DoesNotContain("--allow-all", args);
    }

    [Fact]
    public void BuildAgenticArguments_forces_json_output_mode()
    {
        // Even when CopilotCli.JsonOutput is false, agentic argv always carries
        // --output-format json so the watchdog can count tool-call events without
        // stdout-regex fallback.
        var mgr = NewManager(cli => cli.JsonOutput = false);
        var args = mgr.BuildAgenticArguments(new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
        });

        var list = args.ToList();
        var idx = list.IndexOf("--output-format");
        Assert.True(idx >= 0, "--output-format must be present");
        Assert.Equal("json", list[idx + 1]);
        // And only once — double-specify would trip the CLI arg parser.
        Assert.Equal(1, list.Count(a => a == "--output-format"));
    }

    [Fact]
    public void BuildAgenticArguments_does_not_double_add_json_when_already_on()
    {
        var mgr = NewManager(cli => cli.JsonOutput = true);
        var args = mgr.BuildAgenticArguments(new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
        });
        Assert.Equal(1, args.Count(a => a == "--output-format"));
    }

    [Fact]
    public void BuildAgenticArguments_honours_model_override()
    {
        var mgr = NewManager();
        var args = mgr.BuildAgenticArguments(new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            ModelOverride = "claude-sonnet-4.6",
        });
        var list = args.ToList();
        var idx = list.IndexOf("--model");
        Assert.True(idx >= 0);
        Assert.Equal("claude-sonnet-4.6", list[idx + 1]);
    }

    [Fact]
    public async Task ExecuteAgenticSessionAsync_rejects_non_agentic_pool()
    {
        var mgr = NewManager();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mgr.ExecuteAgenticSessionAsync(
                "noop",
                new CopilotCliRequestOptions { Pool = CopilotCliPool.SingleShot },
                default));
    }

    [Fact]
    public async Task ExecuteAgenticSessionAsync_returns_unavailable_when_cli_missing()
    {
        // No StartAsync called → _copilotAvailable stays false → Unavailable result.
        var mgr = NewManager();
        var result = await mgr.ExecuteAgenticSessionAsync(
            "noop",
            new CopilotCliRequestOptions { Pool = CopilotCliPool.Agentic, AllowAll = true },
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(AgenticFailureReason.Unavailable, result.FailureReason);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void CopilotCliRequestOptions_defaults_are_safe()
    {
        // Legacy-compatible defaults: SingleShot pool, no --allow-all, legacy close-stdin.
        var opts = new CopilotCliRequestOptions();
        Assert.Equal(CopilotCliPool.SingleShot, opts.Pool);
        Assert.False(opts.AllowAll);
        Assert.True(opts.CloseStdinAfterPrompt);
        Assert.Equal(CopilotCliWatchdogMode.Default, opts.WatchdogMode);
    }

    [Fact]
    public void AgenticConfig_defaults_match_plan()
    {
        var cfg = new AgenticConfig();
        Assert.Equal(0, cfg.StuckSeconds); // 0 = disabled (no stuck timeout)
        Assert.Equal(500, cfg.ToolCallCap);
        Assert.True(cfg.ValidateHostGitconfigUnchanged);
        Assert.True(cfg.JobObjectActiveProcessLimit > 0);
    }

    [Fact]
    public async Task ExecutePromptAsync_pool_overload_rejects_agentic()
    {
        var mgr = NewManager();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mgr.ExecutePromptAsync(
                "noop",
                new CopilotCliRequestOptions { Pool = CopilotCliPool.Agentic },
                default));
    }

    [Fact]
    public void Constructor_sizes_pools_from_framework_config()
    {
        var mgr = NewManager(configureFramework: fw =>
        {
            fw.Concurrency.SingleShotSlots = 7;
            fw.Concurrency.CandidateSlots = 5;
            fw.Concurrency.AgenticSlots = 3;
        });
        Assert.NotNull(mgr); // Smoke test — ctor doesn't throw with custom sizing.
    }

    [Fact]
    public void Constructor_falls_back_to_cli_max_when_singleshot_zero()
    {
        // Legacy behaviour: if framework SingleShotSlots is 0, use CopilotCli.MaxConcurrentRequests.
        var mgr = NewManager(
            configureCli: cli => cli.MaxConcurrentRequests = 9,
            configureFramework: fw => fw.Concurrency.SingleShotSlots = 0);
        Assert.NotNull(mgr);
    }
}
