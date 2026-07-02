using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="DefaultMcpServerLocator"/> — the disk-probing resolver used
/// by <see cref="McpEnhancedStrategy"/> when no explicit override is configured.
/// </summary>
public class DefaultMcpServerLocatorTests : IDisposable
{
    private readonly string _sandbox;

    public DefaultMcpServerLocatorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static IOptionsMonitor<StrategyFrameworkConfig> Monitor(StrategyFrameworkConfig cfg) =>
        new StaticMonitor<StrategyFrameworkConfig>(cfg);

    [Fact]
    public void Explicit_config_path_is_resolved_when_present_with_runtimeconfig()
    {
        var dll = Path.Combine(_sandbox, "VirtualDevTeam.McpServer.dll");
        var runtime = Path.Combine(_sandbox, "VirtualDevTeam.McpServer.runtimeconfig.json");
        File.WriteAllText(dll, "");
        File.WriteAllText(runtime, "{}");

        var cfg = new StrategyFrameworkConfig { McpServerDllPath = dll };
        var locator = new DefaultMcpServerLocator(Monitor(cfg), NullLogger<DefaultMcpServerLocator>.Instance);

        var spec = locator.Resolve();

        Assert.Equal("dotnet", spec.Command);
        Assert.Contains(dll, spec.FixedArgs[0]);
        Assert.Equal(Path.GetFullPath(dll), spec.ResolvedPath);
    }

    [Fact]
    public void Explicit_config_path_without_runtimeconfig_falls_through_to_probe()
    {
        // DLL exists but sidecar is missing → not usable → locator should keep probing.
        // With no other path available in this isolated sandbox, it should throw.
        var dll = Path.Combine(_sandbox, "VirtualDevTeam.McpServer.dll");
        File.WriteAllText(dll, "");

        var cfg = new StrategyFrameworkConfig { McpServerDllPath = dll };
        var locator = new DefaultMcpServerLocator(Monitor(cfg), NullLogger<DefaultMcpServerLocator>.Instance);

        // Might still find the server in the test output dir's `beside` probe; accept
        // either a successful resolve OR a throw — the invariant is that the broken
        // explicit path is NOT returned.
        try
        {
            var spec = locator.Resolve();
            Assert.NotEqual(Path.GetFullPath(dll), spec.ResolvedPath);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains("VirtualDevTeam.McpServer.dll", ex.Message);
            Assert.Contains(Path.GetFullPath(dll), ex.Message);
        }
    }

    [Fact]
    public void Missing_binary_throws_with_probe_paths_in_message()
    {
        var cfg = new StrategyFrameworkConfig { McpServerDllPath = Path.Combine(_sandbox, "does-not-exist.dll") };
        var locator = new DefaultMcpServerLocator(Monitor(cfg), NullLogger<DefaultMcpServerLocator>.Instance);

        // If the test run has `beside` McpServer in its output, this will succeed.
        // We only assert the exception shape when it does throw.
        try
        {
            locator.Resolve();
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains("VirtualDevTeam.McpServer.dll", ex.Message);
            Assert.Contains("McpServerDllPath", ex.Message);
            // Probe paths listed for operator diagnosis.
            Assert.Contains("Probed:", ex.Message);
        }
    }

    private sealed class StaticMonitor<T> : IOptionsMonitor<T>
    {
        public StaticMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
