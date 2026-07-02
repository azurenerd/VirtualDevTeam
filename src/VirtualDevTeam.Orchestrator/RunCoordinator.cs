using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.DevPlatform.Providers.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Orchestrator;

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
    private readonly AgentMemoryStore _memoryStore;
    private readonly IGateCheckService _gateCheck;
    private readonly ProjectFileManager _fileManager;
    private readonly RunBranchProvider _branchProvider;
    private readonly IBranchService _branchService;
    private readonly DevelopSettingsService _developSettingsService;
    private readonly IGitHubService _gitHubService;
    private readonly IPullRequestService? _prService;
    private readonly IWorkItemService? _wiService;
    private readonly ConflictResolver _conflictResolver;
    private readonly CandidateStateStore _candidateStateStore;
    private readonly IDevPlatformAuthProvider _authProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AgentUsageTracker? _usageTracker;
    private readonly RoleContextProvider? _roleContextProvider;
    private readonly LocalPlatformContext? _localPlatformCtx;
    private readonly ILogger<RunCoordinator> _logger;
    private readonly VirtualDevTeamConfig _config;

    // Lazily created when wizard selects GhCli but DI resolved a different provider at startup
    private GhCliAuthProvider? _ghCliProviderFallback;

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
        AgentMemoryStore memoryStore,
        IGateCheckService gateCheck,
        ProjectFileManager fileManager,
        RunBranchProvider branchProvider,
        IBranchService branchService,
        DevelopSettingsService developSettingsService,
        IGitHubService gitHubService,
        ConflictResolver conflictResolver,
        CandidateStateStore candidateStateStore,
        IDevPlatformAuthProvider authProvider,
        ILoggerFactory loggerFactory,
        ILogger<RunCoordinator> logger,
        IOptions<VirtualDevTeamConfig> config,
        RoleContextProvider? roleContextProvider = null,
        AgentUsageTracker? usageTracker = null,
        IPullRequestService? prService = null,
        IWorkItemService? wiService = null,
        LocalPlatformContext? localPlatformCtx = null)
    {
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
        _branchProvider = branchProvider ?? throw new ArgumentNullException(nameof(branchProvider));
        _branchService = branchService ?? throw new ArgumentNullException(nameof(branchService));
        _developSettingsService = developSettingsService ?? throw new ArgumentNullException(nameof(developSettingsService));
        _gitHubService = gitHubService ?? throw new ArgumentNullException(nameof(gitHubService));
        _prService = prService;
        _wiService = wiService;
        _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
        _candidateStateStore = candidateStateStore ?? throw new ArgumentNullException(nameof(candidateStateStore));
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _roleContextProvider = roleContextProvider;
        _usageTracker = usageTracker;
        _localPlatformCtx = localPlatformCtx;
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
        // Load wizard settings and reconfigure services BEFORE checking for active runs.
        // The develop-settings.json may point to a different repo (and therefore a different
        // SQLite database) than appsettings.json defaults. Without this, recovery would
        // check the wrong database and miss the active run.
        var developSettings = await _developSettingsService.LoadAsync(ct);
        _developSettingsService.MergeIntoConfig(_config, developSettings);
        await ReconfigureServicesForRepoAsync(ct);

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

        // Use the persisted artifact path if available; otherwise fall back to profile reconstruction
        var artifactPath = savedRun.ArtifactBasePath ?? _activeProfile!.ArtifactBasePath;
        _fileManager.ArtifactBasePath = artifactPath;

        // Restore the effective branch and run scope from persisted run state
        _branchProvider.SetForRun(savedRun.TargetBranch, savedRun.RunScope);

        // Detect minimal-reset: if the run was active but all platform work is gone
        // (0 open issues + 0 open PRs), the operator likely ran a reset script.
        // Advance _runStartedUtc so ListMergedAsync filters out old merged PRs from
        // the prior run — otherwise the SE recovery sees them and short-circuits to
        // "engineering complete" without creating new tasks.
        try
        {
            // Use platform capability interfaces when available to avoid
            // GitHub API calls in Local mode. Falls back to IGitHubService
            // only when capability interfaces aren't registered.
            int openIssueCount, openPrCount;
            if (_prService is not null && _wiService is not null)
            {
                var openIssues = await _wiService.ListOpenAsync(ct);
                var openPrs = await _prService.ListOpenAsync(ct);
                openIssueCount = openIssues.Count;
                openPrCount = openPrs.Count;
            }
            else
            {
                // Fallback should never be used in non-GitHub modes — it would call
                // GitHub API against the wrong platform. Log and treat as clean-slate.
                _logger.LogWarning(
                    "RunCoordinator recovery: capability interfaces not registered, " +
                    "skipping clean-slate detection (treating as 0 open)");
                openIssueCount = 0;
                openPrCount = 0;
            }

            if (openIssueCount == 0 && openPrCount == 0 && savedRun.Status == RunStatus.Running)
            {
                _logger.LogInformation(
                    "Recovery detected clean-slate (0 open issues, 0 open PRs on an active run) — " +
                    "advancing _runStartedUtc to filter out old merged PRs from prior run");
                _stateStore.ResetRunStartedUtc();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clean-slate detection failed — proceeding with existing _runStartedUtc");
        }

        _logger.LogInformation(
            "Recovered {Mode} run {RunId} in status {Status} (workflow recovered: {WfRecovered}, docs path: {DocsPath}, branch: {Branch})",
            savedRun.Mode, savedRun.RunId, savedRun.Status, workflowRecovered,
            _activeProfile.ArtifactBasePath, _branchProvider.EffectiveBranch);

        return savedRun.Status == RunStatus.Running
            ? RecoveryResult.ResumeImmediately
            : RecoveryResult.WaitForResume;
    }

    /// <summary>
    /// Start a new greenfield project run. Auto-cancels any paused or (if forceRestart) running run.
    /// When forceRestart is true, stops agents and cancels the active run before starting fresh.
    /// </summary>
    public async Task<ActiveRun> StartProjectAsync(CancellationToken ct = default, bool forceRestart = false)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_activeRun is { Status: RunStatus.Running or RunStatus.NotStarted } && !forceRestart)
                    throw new InvalidOperationException($"Cannot start a project — run {_activeRun.RunId} is already {_activeRun.Status}. Stop it first.");
            }

            // Auto-cancel any active run (Running/NotStarted with forceRestart, or Paused always)
            if (_activeRun is { Status: RunStatus.Running or RunStatus.NotStarted or RunStatus.Paused })
            {
                await CancelActiveRunInternalAsync(_activeRun, ct);
            }

        // Load wizard settings and merge into runtime config before reading any config values.
        // The Develop wizard saves to develop-settings.json but doesn't mutate the in-memory config.
        var developSettings = await _developSettingsService.LoadAsync(ct);
        _developSettingsService.MergeIntoConfig(_config, developSettings);
        _logger.LogInformation("Merged develop settings into config (WorkingBranch={Branch})",
            _config.Project.WorkingBranch ?? "(none)");

        // Reconfigure singleton services to target the (possibly changed) repository.
        // This ensures GitHubService, stores, and resolvers use the correct repo/token
        // even if the user changed the target via the wizard since last run.
        await ReconfigureServicesForRepoAsync(ct);

        // Late-bind LocalPlatformContext if UseLocalDevMode was set in develop-settings.json
        // but LocalPlatformInitializer ran too early (before config was merged).
        if (developSettings?.UseLocalDevMode == true && _localPlatformCtx != null)
        {
            try
            {
                _localPlatformCtx.CreateConnection()?.Dispose();
                _logger.LogInformation("LocalPlatformContext initialized via late-binding in StartProjectAsync");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to late-initialize LocalPlatformContext");
            }
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

        // Working branch: if configured, create from default branch and set as target
        var workingBranch = string.IsNullOrWhiteSpace(_config.Project.WorkingBranch)
            ? null : _config.Project.WorkingBranch;
        if (workingBranch is not null)
        {
            if (!await _branchService.ExistsAsync(workingBranch, ct))
            {
                await _branchService.CreateAsync(workingBranch, _config.Project.DefaultBranch, ct);
                _logger.LogInformation("Created working branch {Branch} from {DefaultBranch}",
                    workingBranch, _config.Project.DefaultBranch);
            }
            run = run with { TargetBranch = workingBranch };
        }

        // Run scope: always use first 8 chars of RunId for branch naming uniqueness
        var runScope = run.RunId[..8];

        // Artifact scope: stable identifier for doc paths (survives mini-resets)
        // Priority: ParentWorkItemId (ADO) > working branch (GitHub) > random RunId fragment
        var artifactScope = _config.Project.ParentWorkItemId?.ToString()
            ?? (workingBranch is not null ? NormalizeBranchForPath(workingBranch) : null)
            ?? runScope;
        var profile = new ProjectWorkflowProfile(
            _config.Limits.PrMode,
            _config.Project.DocsFolderPath,
            artifactScope);

        // Persist the artifact path and run scope with the run so recovery is deterministic
        run = run with { ArtifactBasePath = profile.ArtifactBasePath, RunScope = runScope };

        // Clear any stale state from a previous run and set the new run ID
        await _stateStore.ClearAllCheckpointsAsync(ct);
        _roleContextProvider?.ClearAllOverrides();
        _stateStore.ResetRunStartedUtc(); // Advance run scope so queries don't see prior project's items
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

        // Set the effective branch and run scope for branch naming
        _branchProvider.SetForRun(run.TargetBranch, run.RunScope);

        _logger.LogInformation("Started Project run {RunId} for {Repo} (docs path: {DocsPath}, branch: {Branch}, runScope: {RunScope})",
            run.RunId, run.Repo, profile.ArtifactBasePath, _branchProvider.EffectiveBranch, run.RunScope);
        return run;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Reconfigure singleton services to target the current repo from config.
    /// Called at the start of each run so that services pick up any repo/token changes
    /// made via the wizard since the process started.
    /// </summary>
    private async Task ReconfigureServicesForRepoAsync(CancellationToken ct = default)
    {
        var repoValue = _config.Project.GitHubRepo ?? "";
        if (string.IsNullOrWhiteSpace(repoValue) || !repoValue.Contains('/'))
        {
            _logger.LogWarning("Cannot reconfigure — GitHubRepo '{Repo}' is not in owner/repo format", repoValue);
            return;
        }

        var repoParts = repoValue.Split('/', 2);
        var owner = repoParts[0];
        var repo = repoParts[1];
        var token = _config.Project.GitHubToken ?? "";

        // For GhCli auth, ALWAYS resolve the token from gh CLI — any existing token in config
        // may be a stale PAT from appsettings.json that doesn't have access to the target repo.
        if (_config.DevPlatform.AuthMethod == DevPlatformAuthMethod.GhCli)
        {
            _logger.LogInformation("Resolving GitHub token from gh CLI for GhCli auth method");

            // The DI-injected _authProvider may be a PatAuthProvider if the user
            // selected GhCli in the wizard AFTER app startup (DI resolves at startup).
            // Create a GhCliAuthProvider on demand if the injected one is the wrong type.
            IDevPlatformAuthProvider effectiveProvider = _authProvider is GhCliAuthProvider
                ? _authProvider
                : (_ghCliProviderFallback ??= new GhCliAuthProvider(
                    _loggerFactory.CreateLogger<GhCliAuthProvider>()));

            token = await effectiveProvider.GetTokenAsync(ct);
            _config.Project.GitHubToken = token; // Populate for downstream consumers (clone URLs, etc.)
        }

        // Detect whether repo, token, or DB path changed
        var repoChanged = _gitHubService.RepositoryFullName != repoValue;
        var tokenChanged = _gitHubService is GitHubService gs && gs.HasTokenChanged(token);
        var newDbPath = BuildDbPath(repoValue, _config.Project.WorkingBranch);
        var currentDbPath = _stateStore.DatabasePath;
        var dbChanged = !string.Equals(currentDbPath, newDbPath, StringComparison.OrdinalIgnoreCase);

        if (!repoChanged && !tokenChanged && !dbChanged)
        {
            _logger.LogDebug("Repository, auth, and DB path unchanged ({Repo}), skipping service reconfiguration", repoValue);
            return;
        }

        _logger.LogInformation("Reconfiguring services — repo={RepoChanged}, auth={AuthChanged}, db={DbChanged} → {Owner}/{Repo}",
            repoChanged, tokenChanged, dbChanged, owner, repo);

        // 1. Reconfigure GitHub API services (only if repo or token changed)
        if (repoChanged || tokenChanged)
        {
            if (_gitHubService is GitHubService concreteGitHub)
                concreteGitHub.ReconfigureRepository(owner, repo, token);
            _conflictResolver.Reconfigure(owner, repo, token);
        }

        // 2. Reconfigure SQLite stores when the effective DB path changes (repo or branch)
        if (dbChanged)
        {
            _stateStore.Reconfigure(newDbPath);
            _memoryStore.Reconfigure(newDbPath);

            // Reload usage tracker so the cost badge shows the correct totals from the
            // new DB immediately after restart — without this the badge stays at $0.00.
            _usageTracker?.Reload();

            // 3. Reset CandidateStateStore (clears in-memory state, re-hydrates from new DB)
            _candidateStateStore.Reset();

            _logger.LogInformation("All services reconfigured for {Owner}/{Repo} (db: {DbPath})",
                owner, repo, newDbPath);
        }
        else if (repoChanged || tokenChanged)
        {
            _logger.LogInformation("Auth reconfigured for {Owner}/{Repo} (db unchanged)", owner, repo);
        }
    }

    /// <summary>
    /// Build a unique database filename scoped to repo and optionally the working branch.
    /// Includes a short hash to avoid collisions from underscore-ambiguous slugs.
    /// </summary>
    private static string BuildDbPath(string repoFullName, string? workingBranch)
    {
        var repoSlug = repoFullName.Replace('/', '_');

        if (string.IsNullOrWhiteSpace(workingBranch))
            return $"virtualdevteam_{repoSlug}.db";

        // Sanitize branch name for filesystem safety
        var branchSlug = workingBranch
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(':', '-')
            .Replace(' ', '-')
            .Trim('-');

        // Short hash of the full combo to avoid slug collisions
        // (e.g., "a_b" + branch "c" vs "a" + branch "b_c")
        var hashInput = $"{repoFullName}|{workingBranch}";
        var hashBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(hashInput));
        var shortHash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();

        return $"virtualdevteam_{repoSlug}_{branchSlug}_{shortHash}.db";
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
                if (_activeRun is { Status: RunStatus.Running or RunStatus.NotStarted })
                    throw new InvalidOperationException($"Cannot start a feature — run {_activeRun.RunId} is already {_activeRun.Status}. Stop it first.");
            }

            // Auto-cancel any paused run so the user can start a fresh feature
            if (_activeRun is { Status: RunStatus.Paused })
            {
                _logger.LogInformation("Auto-cancelling paused run {RunId} to start new feature", _activeRun.RunId);
                await _stateStore.UpdateRunStatusAsync(_activeRun.RunId, RunStatus.Cancelled, ct);
                lock (_lock)
                {
                    _activeRun = null;
                    _activeProfile = null;
                }
                _branchProvider.Reset();
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = null;
            }

            var feature = await _stateStore.GetFeatureAsync(featureId, ct)
                ?? throw new InvalidOperationException($"Feature '{featureId}' not found");

            if (feature.Status is not (FeatureStatus.Draft or FeatureStatus.Queued))
                throw new InvalidOperationException($"Feature '{featureId}' is in status {feature.Status} — only Draft or Queued features can be started");

            // Reconfigure services if feature targets a different repo
            var featureRepo = feature.TargetRepo ?? _config.Project.GitHubRepo;
            if (!string.IsNullOrWhiteSpace(featureRepo) && featureRepo != _gitHubService.RepositoryFullName)
            {
                var parts = featureRepo.Split('/', 2);
                if (parts.Length == 2)
                {
                    _config.Project.GitHubRepo = featureRepo;
                    await ReconfigureServicesForRepoAsync(ct);
                }
            }

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

            // Persist the artifact path and run scope so recovery is deterministic
            var runScope = runId[..8];
            run = run with { ArtifactBasePath = profile.ArtifactBasePath, RunScope = runScope };

            // Clear any stale state from a previous run and set the new run ID
            await _stateStore.ClearAllCheckpointsAsync(ct);
            _roleContextProvider?.ClearAllOverrides();
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

            // Set the effective branch and run scope for branch naming
            _branchProvider.SetForRun(run.TargetBranch, run.RunScope);

            _logger.LogInformation("Started Feature run {RunId} for feature '{Title}' ({FeatureId}), docs path: {DocsPath}, branch: {Branch}, runScope: {RunScope}",
                run.RunId, feature.Title, featureId, profile.ArtifactBasePath, _branchProvider.EffectiveBranch, run.RunScope);
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

        _branchProvider.Reset();
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

        _branchProvider.Reset();
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

        _branchProvider.Reset();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;

        _logger.LogWarning("Run {RunId} cancelled via cleanup/reset", run.RunId);
    }

    /// <summary>
    /// Internal helper to cancel an active run, optionally stopping agents.
    /// Must be called while holding <see cref="_lifecycleLock"/>. Does NOT acquire it.
    /// </summary>
    private async Task CancelActiveRunInternalAsync(ActiveRun run, CancellationToken ct)
    {
        _logger.LogInformation("Force-cancelling {Status} run {RunId} to start new project",
            run.Status, run.RunId);

        // Stop agents if the run is actively running
        if (run.Status is RunStatus.Running or RunStatus.NotStarted)
        {
            var agents = _registry.GetAllAgents();
            var stopTasks = agents
                .Where(a => a.Status is not (AgentStatus.Offline or AgentStatus.Terminated))
                .Select(async agent =>
                {
                    try
                    {
                        await agent.StopAsync(ct);
                        _logger.LogDebug("Agent '{AgentId}' stopped during force-cancel", agent.Identity.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to stop agent '{AgentId}' during force-cancel", agent.Identity.Id);
                    }
                });

            await Task.WhenAll(stopTasks);

            foreach (var agent in agents)
            {
                try { await _registry.UnregisterAsync(agent.Identity.Id, ct); }
                catch { /* already unregistered */ }
            }

            _spawnManager.ResetSlots();
        }

        // Cancel the feature if this is a feature run
        if (run.FeatureId is not null)
        {
            try { await _stateStore.UpdateFeatureStatusAsync(run.FeatureId, FeatureStatus.Cancelled, ct: ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to update feature {FeatureId} status to Cancelled", run.FeatureId); }
        }

        await _stateStore.UpdateRunStatusAsync(run.RunId, RunStatus.Cancelled, ct);
        lock (_lock)
        {
            _activeRun = null;
            _activeProfile = null;
        }
        _branchProvider.Reset();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;

        _logger.LogInformation("Run {RunId} force-cancelled — ready for new project", run.RunId);
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
            ["TestEngineer"] = AgentRole.TestEngineer,
            ["SecurityAuditor"] = AgentRole.SecurityAuditor
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

        // Artifact scope: stable identifier for doc paths (survives mini-resets)
        // Priority: ParentWorkItemId (ADO) > working branch (GitHub) > random RunId fragment
        var normalizedBranch = run.TargetBranch is not null ? NormalizeBranchForPath(run.TargetBranch) : null;
        var runScope = _config.Project.ParentWorkItemId?.ToString()
            ?? normalizedBranch
            ?? run.RunId[..Math.Min(8, run.RunId.Length)];
        return new ProjectWorkflowProfile(
            _config.Limits.PrMode,
            _config.Project.DocsFolderPath,
            runScope);
    }

    /// <summary>
    /// Normalizes a branch name for use as a path segment (replaces '/' with '-').
    /// e.g., "feature/auth" → "feature-auth", "testbranch" → "testbranch"
    /// </summary>
    private static string NormalizeBranchForPath(string branch) =>
        branch.Replace('/', '-').Replace('\\', '-');
}
