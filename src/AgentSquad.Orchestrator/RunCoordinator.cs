using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Orchestrator;

/// <summary>
/// Manages the lifecycle of work runs (project or feature).
/// Enforces single active run, handles start/stop/recover, and provides
/// the active <see cref="IWorkflowProfile"/> to agents.
/// </summary>
public class RunCoordinator
{
    private readonly AgentSpawnManager _spawnManager;
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;
    private readonly AgentStateStore _stateStore;
    private readonly IGateCheckService _gateCheck;
    private readonly ProjectFileManager _fileManager;
    private readonly ILogger<RunCoordinator> _logger;
    private readonly AgentSquadConfig _config;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private ActiveRun? _activeRun;
    private IWorkflowProfile? _activeProfile;
    private CancellationTokenSource? _runCts;

    public RunCoordinator(
        AgentSpawnManager spawnManager,
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        AgentStateStore stateStore,
        IGateCheckService gateCheck,
        ProjectFileManager fileManager,
        ILogger<RunCoordinator> logger,
        IOptions<AgentSquadConfig> config)
    {
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>The currently active run, or null if idle.</summary>
    public ActiveRun? ActiveRun
    {
        get { lock (_lock) return _activeRun; }
    }

    /// <summary>The workflow profile for the active run, or null if idle.</summary>
    public IWorkflowProfile? ActiveProfile
    {
        get { lock (_lock) return _activeProfile; }
    }

    /// <summary>Whether a run is currently active (Running or Paused).</summary>
    public bool HasActiveRun
    {
        get { lock (_lock) return _activeRun is { Status: RunStatus.Running or RunStatus.Paused }; }
    }

    /// <summary>
    /// Result of attempting to recover a run on startup.
    /// </summary>
    public enum RecoveryResult
    {
        /// <summary>No saved run found — fresh start.</summary>
        NoRun,
        /// <summary>A paused run was recovered — wait for user to resume.</summary>
        WaitForResume,
        /// <summary>A running run was recovered after crash — resume immediately.</summary>
        ResumeImmediately
    }

    /// <summary>
    /// Try to recover an in-progress run from the database on startup.
    /// Returns the recovery action needed.
    /// </summary>
    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        var savedRun = await _stateStore.GetActiveRunAsync(ct);
        if (savedRun is null)
        {
            _logger.LogInformation("No active run found in database — waiting for start command");
            return RecoveryResult.NoRun;
        }

        // Set run ID on workflow state machine before recovery so it validates against the checkpoint
        _workflow.RunId = savedRun.RunId;

        // Recover workflow state
        var workflowRecovered = await _workflow.RecoverAsync(ct);

        lock (_lock)
        {
            _activeRun = savedRun;
            _activeProfile = CreateProfile(savedRun);
        }

        // Sync the file manager's artifact path with the recovered profile
        _fileManager.ArtifactBasePath = _activeProfile!.ArtifactBasePath;

        _logger.LogInformation(
            "Recovered {Mode} run {RunId} in status {Status} (workflow recovered: {WfRecovered}, docs path: {DocsPath})",
            savedRun.Mode, savedRun.RunId, savedRun.Status, workflowRecovered, _activeProfile.ArtifactBasePath);

        return savedRun.Status == RunStatus.Running
            ? RecoveryResult.ResumeImmediately
            : RecoveryResult.WaitForResume;
    }

    /// <summary>
    /// Start a new greenfield project run. Fails if a run is already active.
    /// </summary>
    public async Task<ActiveRun> StartProjectAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_activeRun is { Status: RunStatus.Running or RunStatus.NotStarted or RunStatus.Paused })
                    throw new InvalidOperationException($"Cannot start a project — run {_activeRun.RunId} is already {_activeRun.Status}");
            }

        var run = new ActiveRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            Mode = WorkMode.Project,
            Status = RunStatus.Running,
            Repo = _config.Project.GitHubRepo,
            BaseBranch = _config.Project.DefaultBranch,
            StartedAt = DateTime.UtcNow
        };

        // Run scope: use ParentWorkItemId if set, otherwise first 8 chars of RunId
        var runScope = _config.Project.ParentWorkItemId?.ToString() ?? run.RunId[..8];
        var profile = new ProjectWorkflowProfile(
            _config.Limits.SinglePRMode,
            _config.Project.DocsFolderPath,
            runScope);

        // Clear any stale state from a previous run and set the new run ID
        await _stateStore.ClearAllCheckpointsAsync(ct);
        _workflow.RunId = run.RunId;

        await _stateStore.SaveActiveRunAsync(run, ct);

        lock (_lock)
        {
            _activeRun = run;
            _activeProfile = profile;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // Set the artifact base path so all agent doc reads/writes go to the scoped folder
        _fileManager.ArtifactBasePath = profile.ArtifactBasePath;

        _logger.LogInformation("Started Project run {RunId} for {Repo} (docs path: {DocsPath})",
            run.RunId, run.Repo, profile.ArtifactBasePath);
        return run;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Start a feature run. Loads the feature definition, creates the run, and sets up the profile.
    /// </summary>
    public async Task<ActiveRun> StartFeatureAsync(string featureId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_activeRun is { Status: RunStatus.Running or RunStatus.NotStarted or RunStatus.Paused })
                    throw new InvalidOperationException($"Cannot start a feature — run {_activeRun.RunId} is already {_activeRun.Status}");
            }

            var feature = await _stateStore.GetFeatureAsync(featureId, ct)
                ?? throw new InvalidOperationException($"Feature '{featureId}' not found");

            if (feature.Status is not (FeatureStatus.Draft or FeatureStatus.Queued))
                throw new InvalidOperationException($"Feature '{featureId}' is in status {feature.Status} — only Draft or Queued features can be started");

            var runId = Guid.NewGuid().ToString("N");
            var run = new ActiveRun
            {
                RunId = runId,
                Mode = WorkMode.Feature,
                FeatureId = featureId,
                Status = RunStatus.Running,
                Repo = feature.TargetRepo ?? _config.Project.GitHubRepo,
                BaseBranch = feature.BaseBranch,
                TargetBranch = $"feature/{feature.Title.ToLowerInvariant().Replace(' ', '-').Replace("--", "-").Trim('-')}",
                StartedAt = DateTime.UtcNow
            };

            var profile = new FeatureWorkflowProfile(feature, runId);

            // Clear any stale state from a previous run and set the new run ID
            await _stateStore.ClearAllCheckpointsAsync(ct);
            _workflow.RunId = run.RunId;

            await _stateStore.SaveActiveRunAsync(run, ct);
            await _stateStore.UpdateFeatureStatusAsync(featureId, FeatureStatus.Running, runId, ct);

            lock (_lock)
            {
                _activeRun = run;
                _activeProfile = profile;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            // Feature runs use their own artifact path
            _fileManager.ArtifactBasePath = profile.ArtifactBasePath;

            _logger.LogInformation("Started Feature run {RunId} for feature '{Title}' ({FeatureId}), docs path: {DocsPath}",
                run.RunId, feature.Title, featureId, profile.ArtifactBasePath);
            return run;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Pause the current run. Stops all agents, checkpoints state for later resume.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            ActiveRun? run;
            lock (_lock)
            {
                run = _activeRun;
                if (run is null or { Status: not RunStatus.Running })
                {
                    _logger.LogWarning("StopAsync called but no running run to stop");
                    return;
                }
            }

            _logger.LogInformation("Pausing run {RunId} — stopping all agents...", run.RunId);

            // Checkpoint workflow state FIRST (captures latest phase/signals)
            await _workflow.CheckpointAsync();

            // Stop all registered agents gracefully
            var agents = _registry.GetAllAgents();
            var stopTasks = agents
                .Where(a => a.Status is not (AgentStatus.Offline or AgentStatus.Terminated))
                .Select(async agent =>
                {
                    try
                    {
                        await agent.StopAsync(ct);
                        _logger.LogDebug("Agent '{AgentId}' stopped during pause", agent.Identity.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to stop agent '{AgentId}' during pause", agent.Identity.Id);
                    }
                });

            await Task.WhenAll(stopTasks);

            // Unregister all agents from the registry
            foreach (var agent in agents)
            {
                try
                {
                    await _registry.UnregisterAsync(agent.Identity.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Agent '{AgentId}' already unregistered", agent.Identity.Id);
                }
            }

            // Reset spawn slot counters
            _spawnManager.ResetSlots();

            // Update run status to Paused
            await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Paused, ct);

            lock (_lock)
            {
                _activeRun = run with { Status = RunStatus.Paused };
            }

            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;

            _logger.LogInformation("Run {RunId} paused — {AgentCount} agents stopped", run.RunId, agents.Count);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Resume a paused run. Spawns fresh agents that pick up from the checkpointed workflow state.
    /// </summary>
    public async Task<ActiveRun> ResumeAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            ActiveRun? run;
            lock (_lock)
            {
                run = _activeRun;
                if (run is null or { Status: not RunStatus.Paused })
                    throw new InvalidOperationException(
                        run is null ? "No active run to resume" : $"Run {run.RunId} is {run.Status}, not Paused");
            }

            _logger.LogInformation("Resuming run {RunId}...", run.RunId);

            // Update status to Running
            await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Running, ct);

            lock (_lock)
            {
                _activeRun = run with { Status = RunStatus.Running };
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            _logger.LogInformation("Run {RunId} resumed — ready for agent spawn", run.RunId);
            return _activeRun!;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Mark the current run as completed.
    /// </summary>
    public async Task CompleteRunAsync(CancellationToken ct = default)
    {
        ActiveRun? run;
        lock (_lock) { run = _activeRun; }

        if (run is null) return;

        await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Completed, ct);

        if (run.FeatureId is not null)
            await _stateStore.UpdateFeatureStatusAsync(run.FeatureId, FeatureStatus.Completed, ct: ct);

        lock (_lock)
        {
            _activeRun = run with { Status = RunStatus.Completed, CompletedAt = DateTime.UtcNow };
        }

        _logger.LogInformation("Run {RunId} completed", run.RunId);
    }

    /// <summary>
    /// Mark the current run as failed.
    /// </summary>
    public async Task FailRunAsync(string reason, CancellationToken ct = default)
    {
        ActiveRun? run;
        lock (_lock) { run = _activeRun; }

        if (run is null) return;

        await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Failed, ct);

        if (run.FeatureId is not null)
            await _stateStore.UpdateFeatureStatusAsync(run.FeatureId, FeatureStatus.Failed, ct: ct);

        lock (_lock)
        {
            _activeRun = run with { Status = RunStatus.Failed, CompletedAt = DateTime.UtcNow };
        }

        _logger.LogWarning("Run {RunId} failed: {Reason}", run.RunId, reason);
    }

    /// <summary>
    /// Cancel the current run (e.g., during cleanup/reset). Clears the active run so recovery
    /// won't attempt to resume it. Also cancels the run CTS to signal any waiting agents.
    /// </summary>
    public async Task CancelRunAsync(CancellationToken ct = default)
    {
        ActiveRun? run;
        lock (_lock) { run = _activeRun; }

        if (run is null) return;

        await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Cancelled, ct);

        lock (_lock)
        {
            _activeRun = null;
            _activeProfile = null;
        }

        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;

        _logger.LogWarning("Run {RunId} cancelled via cleanup/reset", run.RunId);
    }

    /// <summary>
    /// Spawn the agents required for the active run's workflow profile.
    /// </summary>
    public async Task SpawnAgentsForRunAsync(CancellationToken ct = default)
    {
        IWorkflowProfile? profile;
        lock (_lock) { profile = _activeProfile; }

        if (profile is null)
            throw new InvalidOperationException("No active profile — call StartProjectAsync or StartFeatureAsync first");

        var roleMap = new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramManager"] = AgentRole.ProgramManager,
            ["Researcher"] = AgentRole.Researcher,
            ["Architect"] = AgentRole.Architect,
            ["SoftwareEngineer"] = AgentRole.SoftwareEngineer,
            ["TestEngineer"] = AgentRole.TestEngineer
        };

        foreach (var roleName in profile.RequiredAgentRoles)
        {
            if (!roleMap.TryGetValue(roleName, out var role))
            {
                _logger.LogWarning("Unknown agent role '{Role}' in workflow profile, skipping", roleName);
                continue;
            }

            var agentConfig = _config.Agents.GetConfigForRole(role);
            if (!agentConfig.Enabled || !agentConfig.AutoSpawn)
            {
                _logger.LogInformation("{Role} agent skipped (Enabled={Enabled}, AutoSpawn={AutoSpawn})",
                    role, agentConfig.Enabled, agentConfig.AutoSpawn);
                continue;
            }

            var identity = await _spawnManager.SpawnAgentAsync(role, ct);
            if (identity is null)
            {
                _logger.LogCritical("Failed to spawn {Role} agent", role);
                if (role == AgentRole.ProgramManager)
                    throw new InvalidOperationException("Cannot start run without ProgramManager agent");
                continue;
            }

            _logger.LogInformation("{Role} agent spawned: {Name}", role, identity.DisplayName);
        }
    }

    private IWorkflowProfile CreateProfile(ActiveRun run)
    {
        if (run.Mode == WorkMode.Feature && run.FeatureId is not null)
        {
            var feature = _stateStore.GetFeatureAsync(run.FeatureId).GetAwaiter().GetResult();
            if (feature is not null)
                return new FeatureWorkflowProfile(feature, run.RunId);

            _logger.LogWarning("Feature {FeatureId} not found for run {RunId}, falling back to project profile",
                run.FeatureId, run.RunId);
        }

        var runScope = _config.Project.ParentWorkItemId?.ToString() ?? run.RunId[..Math.Min(8, run.RunId.Length)];
        return new ProjectWorkflowProfile(
            _config.Limits.SinglePRMode,
            _config.Project.DocsFolderPath,
            runScope);
    }
}
