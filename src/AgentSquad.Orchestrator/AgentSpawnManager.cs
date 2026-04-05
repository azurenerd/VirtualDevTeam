namespace AgentSquad.Orchestrator;

using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Dynamically spawns and manages agent lifecycle at runtime.
/// Uses <see cref="IAgentFactory"/> to create agent instances and
/// <see cref="AgentRegistry"/> to track them.
/// </summary>
public class AgentSpawnManager
{
    private readonly AgentRegistry _registry;
    private readonly IAgentFactory _agentFactory;
    private readonly AgentSquadConfig _config;
    private readonly ILogger<AgentSpawnManager> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<AgentRole, int> _spawnCounts = new();
    private int _additionalEngineers;

    private static readonly HashSet<AgentRole> CoreRoles = new()
    {
        AgentRole.ProgramManager,
        AgentRole.Researcher,
        AgentRole.Architect,
        AgentRole.PrincipalEngineer,
        AgentRole.TestEngineer
    };

    public AgentSpawnManager(
        AgentRegistry registry,
        IAgentFactory agentFactory,
        IOptions<AgentSquadConfig> config,
        ILogger<AgentSpawnManager> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Spawn a new agent by role. Returns the agent identity, or null if the
    /// spawn limit for that role has been reached.
    /// </summary>
    public async Task<AgentIdentity?> SpawnAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!CanSpawnInternal(role))
            {
                _logger.LogWarning("Cannot spawn {Role}: limit reached.", role);
                return null;
            }

            // Reserve the slot inside the lock so concurrent callers don't over-allocate
            IncrementSpawnCount(role);
        }

        AgentIdentity? identity = null;

        try
        {
            var name = GenerateAgentName(role);
            var modelTier = GetModelTier(role);

            identity = new AgentIdentity
            {
                Id = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                DisplayName = name,
                Role = role,
                ModelTier = modelTier
            };

            _logger.LogInformation(
                "Spawning agent '{DisplayName}' ({Role}) with model tier '{ModelTier}'.",
                identity.DisplayName, role, modelTier);

            var agent = _agentFactory.Create(role, identity);
            await _registry.RegisterAsync(agent, ct);
            await agent.InitializeAsync(ct);

            _logger.LogInformation(
                "Agent '{AgentId}' ({DisplayName}) spawned and initialized.",
                identity.Id, identity.DisplayName);

            return identity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn agent for role {Role}.", role);

            // Roll back the slot reservation
            lock (_lock)
            {
                DecrementSpawnCount(role);
            }

            // Best-effort cleanup: unregister if it was registered
            if (identity is not null)
            {
                try { await _registry.UnregisterAsync(identity.Id, ct); }
                catch { /* already logged upstream */ }
            }

            throw;
        }
    }

    /// <summary>
    /// Returns true if a new agent of the given role may be spawned
    /// without exceeding configured limits.
    /// </summary>
    public bool CanSpawn(AgentRole role)
    {
        lock (_lock)
        {
            return CanSpawnInternal(role);
        }
    }

    /// <summary>
    /// Stop and unregister an agent, freeing its slot.
    /// </summary>
    public async Task TerminateAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agent = _registry.GetAgent(agentId);
        if (agent is null)
        {
            _logger.LogWarning("Attempted to terminate unknown agent '{AgentId}'.", agentId);
            return;
        }

        var role = agent.Identity.Role;

        _logger.LogInformation("Terminating agent '{AgentId}' ({Role}).", agentId, role);

        await _registry.UnregisterAsync(agentId, ct);

        lock (_lock)
        {
            DecrementSpawnCount(role);
        }

        _logger.LogInformation("Agent '{AgentId}' terminated.", agentId);
    }

    /// <summary>
    /// Pause an agent by sending a control message.
    /// </summary>
    public async Task PauseAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agent = _registry.GetAgent(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");

        if (agent.Status == AgentStatus.Paused)
        {
            _logger.LogDebug("Agent '{AgentId}' is already paused.", agentId);
            return;
        }

        _logger.LogInformation("Pausing agent '{AgentId}'.", agentId);

        await agent.HandleMessageAsync(new AgentMessage
        {
            FromAgentId = "system",
            ToAgentId = agentId,
            MessageType = "control.pause"
        }, ct);
    }

    /// <summary>
    /// Resume a paused agent by sending a control message.
    /// </summary>
    public async Task ResumeAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agent = _registry.GetAgent(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");

        if (agent.Status != AgentStatus.Paused)
        {
            _logger.LogDebug("Agent '{AgentId}' is not paused (status: {Status}).", agentId, agent.Status);
            return;
        }

        _logger.LogInformation("Resuming agent '{AgentId}'.", agentId);

        await agent.HandleMessageAsync(new AgentMessage
        {
            FromAgentId = "system",
            ToAgentId = agentId,
            MessageType = "control.resume"
        }, ct);
    }

    /// <summary>Number of additional engineers (Senior + Junior) currently spawned.</summary>
    public int GetAdditionalEngineersCount()
    {
        lock (_lock) { return _additionalEngineers; }
    }

    /// <summary>Configured maximum additional engineers.</summary>
    public int GetMaxAdditionalEngineers() => _config.Limits.MaxAdditionalEngineers;

    // ── Private helpers ──────────────────────────────────────────────

    private bool CanSpawnInternal(AgentRole role)
    {
        if (CoreRoles.Contains(role))
        {
            // Core roles are singletons — at most one instance each
            var existing = _registry.GetAgentsByRole(role);
            return existing.Count == 0;
        }

        // Engineer roles share the MaxAdditionalEngineers pool
        return _additionalEngineers < _config.Limits.MaxAdditionalEngineers;
    }

    private string GenerateAgentName(AgentRole role)
    {
        var count = _spawnCounts.GetValueOrDefault(role, 0) + 1;
        _spawnCounts[role] = count;

        return role switch
        {
            AgentRole.SeniorEngineer => $"Senior Engineer {count}",
            AgentRole.JuniorEngineer => $"Junior Engineer {count}",
            _ => role.ToString()
        };
    }

    private string GetModelTier(AgentRole role)
    {
        return role switch
        {
            AgentRole.ProgramManager => _config.Agents.ProgramManager.ModelTier,
            AgentRole.Researcher => _config.Agents.Researcher.ModelTier,
            AgentRole.Architect => _config.Agents.Architect.ModelTier,
            AgentRole.PrincipalEngineer => _config.Agents.PrincipalEngineer.ModelTier,
            AgentRole.TestEngineer => _config.Agents.TestEngineer.ModelTier,
            AgentRole.SeniorEngineer => _config.Agents.SeniorEngineerTemplate.ModelTier,
            AgentRole.JuniorEngineer => _config.Agents.JuniorEngineerTemplate.ModelTier,
            _ => "standard"
        };
    }

    private void IncrementSpawnCount(AgentRole role)
    {
        if (!CoreRoles.Contains(role))
            _additionalEngineers++;
    }

    private void DecrementSpawnCount(AgentRole role)
    {
        if (!CoreRoles.Contains(role) && _additionalEngineers > 0)
            _additionalEngineers--;
    }
}
