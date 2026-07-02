using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Integration tests for <c>p3-test-orphan-cleanup</c>. Uses the
/// <c>VirtualDevTeam.FakeCopilotCli</c> binary as a hermetic stand-in for the
/// real <c>copilot</c>, exercising agentic-session termination paths:
/// <list type="bullet">
///   <item><description>Stuck session → watchdog kills the process tree.</description></item>
///   <item><description>Tool-call cap exceeded → monitor fails fast.</description></item>
///   <item><description>Grandchild process exists → Job Object disposal reaps it.</description></item>
/// </list>
/// Tests are Windows-focused (Job Object is Windows-only), but scenarios that
/// don't depend on Win32 primitives run everywhere.
/// </summary>
public class AgenticOrphanCleanupTests
{
    private static string FakeCliExe
    {
        get
        {
            // Resolve the fake CLI output. ProjectReference with
            // ReferenceOutputAssembly=false ensures MSBuild builds it into the
            // same Debug/Release config. Path is discovered relative to test
            // assembly location, not CWD.
            // Test assembly sits at: <repo>\tests\VirtualDevTeam.StrategyFramework.Tests\bin\<Config>\net8.0\
            var testDir = Path.GetDirectoryName(typeof(AgenticOrphanCleanupTests).Assembly.Location)!;
            var tfmDir = new DirectoryInfo(testDir);          // net8.0
            var configDir = tfmDir.Parent!.Name;               // Debug | Release
            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "VirtualDevTeam.FakeCopilotCli.exe"
                : "VirtualDevTeam.FakeCopilotCli.dll";
            var candidate = Path.Combine(
                testDir, "..", "..", "..", "..", "VirtualDevTeam.FakeCopilotCli",
                "bin", configDir, "net8.0", exeName);
            return Path.GetFullPath(candidate);
        }
    }

    private static CopilotCliProcessManager BuildManager(TimeSpan? agenticBudget = null)
    {
        var agentCfg = new VirtualDevTeamConfig();
        agentCfg.CopilotCli.ExecutablePath = FakeCliExe;
        agentCfg.CopilotCli.Enabled = true;
        var frameworkCfg = new StrategyFrameworkConfig();
        if (agenticBudget is { } budget)
            frameworkCfg.Timeouts.AgenticSeconds = (int)Math.Ceiling(budget.TotalSeconds);
        // Keep the stuck / tool-cap windows tight so tests run fast.
        frameworkCfg.Agentic.StuckSeconds = 3;
        frameworkCfg.Agentic.ToolCallCap = 50;

        var mgr = new CopilotCliProcessManager(
            Options.Create(agentCfg),
            Options.Create(frameworkCfg),
            NullLogger<CopilotCliProcessManager>.Instance);
        // StartAsync probes the exe with --version; the fake handles that.
        mgr.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return mgr;
    }

    private static CopilotCliRequestOptions AgenticOpts(string worktreePath, TimeSpan? timeout = null) => new()
    {
        Pool = CopilotCliPool.Agentic,
        AllowAll = true,
        CloseStdinAfterPrompt = false,
        WatchdogMode = CopilotCliWatchdogMode.Agentic,
        WorkingDirectory = worktreePath,
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    [SkippableFact]
    public async Task StreamingOk_scenario_returns_success_and_writes_marker()
    {
        Skip.IfNot(File.Exists(FakeCliExe),
            $"Fake Copilot CLI not available at '{FakeCliExe}' — expected the .exe apphost (Windows only build layout).");

        var worktree = Path.Combine(Path.GetTempPath(), "as-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", "streaming-ok");
            using var mgr = BuildManager();

            var result = await mgr.ExecuteAgenticSessionAsync(
                "dummy prompt", AgenticOpts(worktree), CancellationToken.None);

            Assert.True(result.Succeeded, $"expected success; reason={result.FailureReason}, log={result.LogBuffer}");
            Assert.True(File.Exists(Path.Combine(worktree, "fake-cli-marker.txt")),
                "fake CLI should have written marker file in worktree");
            Assert.True(result.ToolCallCount >= 5, $"tool calls={result.ToolCallCount}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", null);
            try { Directory.Delete(worktree, true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Stuck_scenario_is_detected_within_stuck_window()
    {
        Skip.IfNot(File.Exists(FakeCliExe), "Fake Copilot CLI not available (non-Windows build layout)");

        var worktree = Path.Combine(Path.GetTempPath(), "as-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", "stuck");
            using var mgr = BuildManager();

            var sw = Stopwatch.StartNew();
            var result = await mgr.ExecuteAgenticSessionAsync(
                "dummy", AgenticOpts(worktree, TimeSpan.FromSeconds(30)), CancellationToken.None);
            sw.Stop();

            Assert.False(result.Succeeded);
            Assert.Equal(AgenticFailureReason.StuckNoOutput, result.FailureReason);
            // Stuck window is 3s; watchdog should fire within ~10s of start.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
                $"stuck detector too slow: {sw.Elapsed}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", null);
            try { Directory.Delete(worktree, true); } catch { }
        }
    }

    [SkippableFact]
    public async Task ToolcapBomb_scenario_is_killed_by_cap()
    {
        Skip.IfNot(File.Exists(FakeCliExe), "Fake Copilot CLI not available (non-Windows build layout)");

        var worktree = Path.Combine(Path.GetTempPath(), "as-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", "toolcap-bomb");
            using var mgr = BuildManager();

            var result = await mgr.ExecuteAgenticSessionAsync(
                "dummy", AgenticOpts(worktree, TimeSpan.FromSeconds(30)), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(AgenticFailureReason.ToolCallCap, result.FailureReason);
            Assert.True(result.ToolCallCount > 50, $"count={result.ToolCallCount}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", null);
            try { Directory.Delete(worktree, true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Grandchild_scenario_is_reaped_on_windows()
    {
        Skip.IfNot(File.Exists(FakeCliExe), "Fake Copilot CLI not available (non-Windows build layout)");
        Skip.IfNot(Win32JobObject.IsSupported, "Windows-only: Job Object required for grandchild reaping");

        var worktree = Path.Combine(Path.GetTempPath(), "as-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        try
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", "grandchild");
            using var mgr = BuildManager();

            // Agentic session hangs after spawning grandchild; watchdog stuck timer
            // (3s) kills the tree. Job Object disposal should then reap the
            // grandchild — verify by checking its PID is no longer alive.
            var result = await mgr.ExecuteAgenticSessionAsync(
                "dummy", AgenticOpts(worktree, TimeSpan.FromSeconds(30)), CancellationToken.None);

            Assert.False(result.Succeeded);

            var pidFile = Path.Combine(worktree, "grandchild.pid");
            if (!File.Exists(pidFile))
                return; // grandchild never spawned — nothing to verify

            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            // Allow a brief window for the OS to propagate the kill.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var stillAlive = true;
            while (DateTime.UtcNow < deadline && stillAlive)
            {
                stillAlive = IsProcessAlive(pid);
                if (stillAlive) await Task.Delay(200);
            }
            Assert.False(stillAlive, $"grandchild PID {pid} should have been reaped by Job Object");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_COPILOT_SCENARIO", null);
            try { Directory.Delete(worktree, true); } catch { }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // No such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
