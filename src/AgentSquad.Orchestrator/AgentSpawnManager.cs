namespace AgentSquad.Orchestrator;

using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Services;
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
    private readonly IGateCheckService _gateCheck;
    private readonly AgentSquadConfig _config;
    private readonly ILogger<AgentSpawnManager> _logger;
    private readonly SMEAgentDefinitionService? _definitionService;

    private readonly object _lock = new();
    private readonly Dictionary<AgentRole, int> _spawnCounts = new();

    // Pool counter for additional (non-leader) Software Engineer spawns
    private int _spawnedSEs;

    private static readonly HashSet<AgentRole> CoreSingletonRoles = new()
    {
        AgentRole.ProgramManager,
        AgentRole.Researcher,
        AgentRole.Architect,
        AgentRole.TestEngineer
    };

    public AgentSpawnManager(
        AgentRegistry registry,
        IAgentFactory agentFactory,
        IGateCheckService gateCheck,
        IOptions<AgentSquadConfig> config,
        ILogger<AgentSpawnManager> logger,
        SMEAgentDefinitionService? definitionService = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _definitionService = definitionService;
    }

    /// <summary>
    /// Reset all spawn slot counters so agents can be re-spawned from scratch.
    /// Call this after all agents have been unregistered.
    /// </summary>
    public void ResetSlots()
    {
        lock (_lock)
        {
            _spawnCounts.Clear();
            _spawnedSEs = 0;
        }
        _logger.LogInformation("Agent spawn slot counters reset");
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
            var (name, rank) = GenerateAgentNameAndRank(role);
            var modelTier = GetModelTier(role);

            identity = new AgentIdentity
            {
                Id = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                DisplayName = name,
                Role = role,
                ModelTier = modelTier,
                Rank = rank
            };

            // === Gate: AgentTeamComposition — human approves agent spawn ===
            await _gateCheck.WaitForGateAsync(
                GateIds.AgentTeamComposition,
                $"Ready to spawn agent: {identity.DisplayName}",
                ct: ct);

            _logger.LogInformation(
                "Spawning agent '{DisplayName}' ({Role}) with model tier '{ModelTier}', rank {Rank}.",
                identity.DisplayName, role, modelTier, rank);

            var agent = _agentFactory.Create(role, identity);
            await _registry.RegisterAsync(agent, ct);
            await agent.InitializeAsync(ct);

            // Start the agent's main loop as a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await agent.StartAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception loopEx)
                {
                    _logger.LogError(loopEx, "Agent '{AgentId}' loop crashed.", identity.Id);
                }
            }, ct);

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
    /// Spawn a custom agent by configuration name. Returns the agent identity,
    /// or null if an agent with that name is already running.
    /// </summary>
    public async Task<AgentIdentity?> SpawnCustomAgentAsync(
        string customAgentName, string modelTier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customAgentName);

        // Check if already running
        var existing = _registry.GetAllAgents()
            .Where(a => a.Identity.Role == AgentRole.Custom
                     && string.Equals(a.Identity.CustomAgentName, customAgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (existing.Count > 0)
        {
            _logger.LogWarning("Custom agent '{Name}' is already running, skipping spawn.", customAgentName);
            return null;
        }

        var identity = new AgentIdentity
        {
            Id = $"custom-{customAgentName.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            DisplayName = customAgentName,
            Role = AgentRole.Custom,
            ModelTier = modelTier,
            CustomAgentName = customAgentName,
            Rank = 0
        };

        try
        {
            // === Gate: AgentTeamComposition — human approves agent spawn ===
            await _gateCheck.WaitForGateAsync(
                GateIds.AgentTeamComposition,
                $"Ready to spawn custom agent: {identity.DisplayName}",
                ct: ct);

            _logger.LogInformation(
                "Spawning custom agent '{DisplayName}' with model tier '{ModelTier}'.",
                identity.DisplayName, modelTier);

            var agent = _agentFactory.Create(AgentRole.Custom, identity);
            await _registry.RegisterAsync(agent, ct);
            await agent.InitializeAsync(ct);

            // Start the agent's main loop as a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await agent.StartAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception loopEx)
                {
                    _logger.LogError(loopEx, "Custom agent '{AgentId}' loop crashed.", identity.Id);
                }
            }, ct);

            _logger.LogInformation(
                "Custom agent '{AgentId}' ({DisplayName}) spawned and initialized.",
                identity.Id, identity.DisplayName);

            return identity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn custom agent '{Name}'.", customAgentName);

            // Best-effort cleanup: unregister if it was registered
            try { await _registry.UnregisterAsync(identity.Id, ct); }
            catch { /* already logged upstream */ }

            throw;
        }
    }

    /// <summary>
    /// Spawns an SME agent from an <see cref="SMEAgentDefinition"/>.
    /// Enforces MaxInstances per definition and MaxTotalSmeAgents globally.
    /// Subject to human gate approval via <see cref="GateIds.SmeAgentSpawn"/>.
    /// </summary>
    public async Task<AgentIdentity?> SpawnSmeAgentAsync(
        SMEAgentDefinition definition, int? assignToIssue = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Check per-definition instance limit
        var existingCount = _registry.GetAllAgents()
            .Count(a => a.Identity.Role == AgentRole.Custom
                     && a.Identity.CustomAgentName != null
                     && a.Identity.CustomAgentName.StartsWith($"sme:{definition.DefinitionId}", StringComparison.OrdinalIgnoreCase));

        if (existingCount >= definition.MaxInstances)
        {
            _logger.LogWarning("SME agent '{RoleName}' at max instances ({Max})",
                definition.RoleName, definition.MaxInstances);
            return null;
        }

        // Check global SME agent cap
        var smeConfig = _config.SmeAgents;
        var totalSmeCount = _registry.GetAllAgents()
            .Count(a => a.Identity.Role == AgentRole.Custom
                     && a.Identity.CustomAgentName?.StartsWith("sme:", StringComparison.OrdinalIgnoreCase) == true);

        if (totalSmeCount >= smeConfig.MaxTotalSmeAgents)
        {
            _logger.LogWarning("Total SME agent cap reached ({Max}). Cannot spawn '{RoleName}'.",
                smeConfig.MaxTotalSmeAgents, definition.RoleName);
            return null;
        }

        var identity = new AgentIdentity
        {
            Id = $"sme-{definition.DefinitionId}-{Guid.NewGuid():N}"[..Math.Min(48, $"sme-{definition.DefinitionId}-{Guid.NewGuid():N}".Length)],
            DisplayName = definition.RoleName,
            Role = AgentRole.Custom,
            ModelTier = definition.ModelTier,
            CustomAgentName = $"sme:{definition.DefinitionId}",
            Rank = existingCount
        };

        try
        {
            // === Gate: SmeAgentSpawn — human approves SME agent creation ===
            await _gateCheck.WaitForGateAsync(
                GateIds.SmeAgentSpawn,
                $"Ready to spawn SME agent: {definition.RoleName} ({definition.DefinitionId})\n" +
                $"Capabilities: {string.Join(", ", definition.Capabilities)}\n" +
                $"MCP Servers: {string.Join(", ", definition.McpServers)}",
                ct: ct);

            _logger.LogInformation(
                "Spawning SME agent '{RoleName}' (def: {DefId}) with tier '{Tier}'.",
                definition.RoleName, definition.DefinitionId, definition.ModelTier);

            var agent = _agentFactory.CreateSme(identity, definition);
            await _registry.RegisterAsync(agent, ct);
            await agent.InitializeAsync(ct);

            // Start the agent's main loop as a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await agent.StartAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception loopEx)
                {
                    _logger.LogError(loopEx, "SME agent '{AgentId}' loop crashed.", identity.Id);
                }
            }, ct);

            _logger.LogInformation(
                "SME agent '{AgentId}' ({RoleName}) spawned and initialized.",
                identity.Id, definition.RoleName);

            // Persist the definition so the agent can be re-spawned on restart
            if (_definitionService is not null)
            {
                try
                {
                    var saveResult = await _definitionService.SaveAsync(definition, ct);
                    if (saveResult.IsValid)
                        _logger.LogInformation("Persisted SME definition '{DefId}' for restart recovery", definition.DefinitionId);
                    else
                        _logger.LogWarning("Failed to persist SME definition '{DefId}': {Errors}",
                            definition.DefinitionId, string.Join(", ", saveResult.Errors));
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx, "Failed to persist SME definition '{DefId}' — agent is running but won't survive restart",
                        definition.DefinitionId);
                }
            }

            // Optionally assign to a task
            if (assignToIssue.HasValue)
            {
                var messageBus = _registry.GetAgent(identity.Id) is not null
                    ? GetMessageBus()
                    : null;

                // Assignment will be handled by the caller via message bus
                _logger.LogInformation("SME agent '{AgentId}' should be assigned to issue #{Issue}",
                    identity.Id, assignToIssue.Value);
            }

            return identity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn SME agent '{RoleName}' (def: {DefId}).",
                definition.RoleName, definition.DefinitionId);

            try { await _registry.UnregisterAsync(identity.Id, ct); }
            catch { /* best-effort cleanup */ }

            throw;
        }
    }

    // Helper to get message bus from DI - lazy approach to avoid circular dependency
    private Core.Messaging.IMessageBus? GetMessageBus() => null; // Will be wired in Phase 6

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
    /// Gracefully retire an SME agent: stop it, unregister it, decrement counters, and log the retirement.
    /// This is a specialized version of TerminateAgentAsync for SME agents.
    /// </summary>
    public async Task RetireSmeAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agent = _registry.GetAgent(agentId);
        if (agent is null)
        {
            _logger.LogWarning("Attempted to retire unknown SME agent '{AgentId}'.", agentId);
            return;
        }

        var agentName = agent.Identity.DisplayName;
        var customAgentName = agent.Identity.CustomAgentName;

        _logger.LogInformation(
            "Retiring SME agent '{AgentId}' ({DisplayName}, definition: {DefId}).",
            agentId, agentName, customAgentName);

        try
        {
            // Stop the agent gracefully
            await agent.StopAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping SME agent '{AgentId}' during retirement.", agentId);
        }

        // Unregister from the registry
        try
        {
            await _registry.UnregisterAsync(agentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error unregistering SME agent '{AgentId}' during retirement.", agentId);
        }

        // Decrement any tracking counters for the agent's role
        lock (_lock)
        {
            DecrementSpawnCount(agent.Identity.Role);
        }

        _logger.LogInformation(
            "SME agent '{AgentId}' ({DisplayName}) successfully retired.",
            agentId, agentName);
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

    /// <summary>Number of additional Software Engineers currently spawned.</summary>
    public int GetAdditionalEngineersCount()
    {
        lock (_lock) { return _spawnedSEs; }
    }

    /// <summary>Configured maximum additional engineers.</summary>
    public int GetMaxAdditionalEngineers() => _config.Limits.MaxAdditionalEngineers;

    /// <summary>Returns pool capacity remaining for the given engineer role.</summary>
    public int GetRemainingPoolCapacity(AgentRole role)
    {
        var pool = _config.Limits.EngineerPool;
        lock (_lock)
        {
            return role switch
            {
                AgentRole.SoftwareEngineer => pool.SoftwareEngineerPool - _spawnedSEs,
                _ => 0
            };
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private bool CanSpawnInternal(AgentRole role)
    {
        // Custom agents are handled by SpawnCustomAgentAsync with their own guard
        if (role == AgentRole.Custom)
            return true;

        // Core singleton roles (PM, Researcher, Architect, TE) — at most one
        if (CoreSingletonRoles.Contains(role))
        {
            var existing = _registry.GetAgentsByRole(role);
            return existing.Count == 0;
        }

        // SoftwareEngineer: first one is the leader (rank 0).
        // Additional SEs come from the pool.
        if (role == AgentRole.SoftwareEngineer)
        {
            var existingSEs = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer);
            if (existingSEs.Count == 0)
                return true; // First SE (the leader) can always spawn
            return _spawnedSEs < _config.Limits.EngineerPool.SoftwareEngineerPool;
        }

        return false;
    }

    private (string Name, int Rank) GenerateAgentNameAndRank(AgentRole role)
    {
        if (role == AgentRole.SoftwareEngineer)
        {
            var existingSEs = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer);
            if (existingSEs.Count == 0)
                return ("SoftwareEngineer", 0); // Leader
            var rank = existingSEs.Count; // 1-based for additional SEs
            return ($"SoftwareEngineer {rank}", rank);
        }

        return (role.ToString(), 0);
    }

    private string GetModelTier(AgentRole role)
    {
        return role switch
        {
            AgentRole.ProgramManager => _config.Agents.ProgramManager.ModelTier,
            AgentRole.Researcher => _config.Agents.Researcher.ModelTier,
            AgentRole.Architect => _config.Agents.Architect.ModelTier,
            AgentRole.SoftwareEngineer => _config.Agents.SoftwareEngineer.ModelTier,
            AgentRole.TestEngineer => _config.Agents.TestEngineer.ModelTier,
            AgentRole.Custom => "standard", // Custom agents use their own config; this is a fallback
            _ => "standard"
        };
    }

    private void IncrementSpawnCount(AgentRole role)
    {
        if (role == AgentRole.SoftwareEngineer)
        {
            // Only count additional SEs (not the first/leader)
            if (_registry.GetAgentsByRole(AgentRole.SoftwareEngineer).Count > 0)
                _spawnedSEs++;
        }
    }

    private void DecrementSpawnCount(AgentRole role)
    {
        if (role == AgentRole.SoftwareEngineer && _spawnedSEs > 0)
            _spawnedSEs--;
    }
}
