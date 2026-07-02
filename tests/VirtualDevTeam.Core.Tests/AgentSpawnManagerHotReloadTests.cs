using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Verifies that <see cref="AgentSpawnManager"/> reads the SE pool cap from
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> at every call, so an operator
/// can raise <c>EngineerPool.SoftwareEngineerPool</c> on the Configuration page mid-run
/// and the next spawn-eligibility check picks up the new value WITHOUT recreating the
/// AgentSpawnManager instance (no runner restart required).
/// </summary>
public sealed class AgentSpawnManagerHotReloadTests
{
    [Fact]
    public void GetRemainingPoolCapacity_ReflectsInitialConfig()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(softwareEngineerPool: 3));
        var manager = BuildManager(monitor);

        Assert.Equal(3, manager.GetRemainingPoolCapacity(AgentRole.SoftwareEngineer));
    }

    [Fact]
    public void GetRemainingPoolCapacity_ReflectsHotReloadedConfig_WithoutRebuildingManager()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(softwareEngineerPool: 3));
        var manager = BuildManager(monitor);

        // Sanity baseline
        Assert.Equal(3, manager.GetRemainingPoolCapacity(AgentRole.SoftwareEngineer));

        // Simulate the operator raising the pool from 3 → 6 on the Configuration page.
        // The Save flow rewrites appsettings.json; the file watcher fires
        // IOptionsMonitor.OnChange. We emulate that by mutating CurrentValue and
        // raising OnChange manually.
        monitor.Set(MakeConfig(softwareEngineerPool: 6));

        // Same AgentSpawnManager instance — no restart, no DI rebuild.
        Assert.Equal(6, manager.GetRemainingPoolCapacity(AgentRole.SoftwareEngineer));
    }

    [Fact]
    public void GetMaxAdditionalEngineers_ReflectsHotReloadedConfig()
    {
        var monitor = new MutableOptionsMonitor<VirtualDevTeamConfig>(MakeConfig(softwareEngineerPool: 2));
        var manager = BuildManager(monitor);

        Assert.Equal(2, manager.GetMaxAdditionalEngineers());

        monitor.Set(MakeConfig(softwareEngineerPool: 8));

        Assert.Equal(8, manager.GetMaxAdditionalEngineers());
    }

    // ── Test helpers ────────────────────────────────────────────────

    private static VirtualDevTeamConfig MakeConfig(int softwareEngineerPool) =>
        new()
        {
            Limits = new LimitsConfig
            {
                EngineerPool = new EngineerPoolConfig
                {
                    SoftwareEngineerPool = softwareEngineerPool
                }
            }
        };

    private static AgentSpawnManager BuildManager(IOptionsMonitor<VirtualDevTeamConfig> monitor)
    {
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var factory = new Mock<IAgentFactory>().Object;
        var gateCheck = new Mock<IGateCheckService>().Object;

        return new AgentSpawnManager(
            registry,
            factory,
            gateCheck,
            monitor,
            NullLogger<AgentSpawnManager>.Instance);
    }

    /// <summary>
    /// Hand-rolled <see cref="IOptionsMonitor{TOptions}"/> test fake whose
    /// <see cref="Set"/> method updates <see cref="CurrentValue"/> and fires the
    /// OnChange callbacks — mirrors what the real options system does when
    /// <c>appsettings.json</c> is rewritten on disk.
    /// </summary>
    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _current;
        private readonly List<Action<T, string?>> _listeners = new();

        public MutableOptionsMonitor(T initial)
        {
            _current = initial;
        }

        public T CurrentValue => _current;

        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_listeners) _listeners.Add(listener);
            return new Subscription(this, listener);
        }

        public void Set(T value)
        {
            _current = value;
            Action<T, string?>[] snapshot;
            lock (_listeners) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(value, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly MutableOptionsMonitor<T> _owner;
            private readonly Action<T, string?> _listener;

            public Subscription(MutableOptionsMonitor<T> owner, Action<T, string?> listener)
            {
                _owner = owner;
                _listener = listener;
            }

            public void Dispose()
            {
                lock (_owner._listeners) _owner._listeners.Remove(_listener);
            }
        }
    }
}
