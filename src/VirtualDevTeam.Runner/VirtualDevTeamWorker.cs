using System.Diagnostics;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Services;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Runner;

public class VirtualDevTeamWorker : BackgroundService
{
    private readonly AgentSpawnManager _spawnManager;
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly AgentStateStore _stateStore;
    private readonly RunCoordinator _runCoordinator;
    private readonly ILogger<VirtualDevTeamWorker> _logger;
    private readonly VirtualDevTeamConfig _config;
    private readonly SMEAgentDefinitionService? _definitionService;
    private readonly List<Task> _agentTasks = new();

    public VirtualDevTeamWorker(
        AgentSpawnManager spawnManager,
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        PullRequestWorkflow prWorkflow,
        AgentStateStore stateStore,
        RunCoordinator runCoordinator,
        ILogger<VirtualDevTeamWorker> logger,
        IOptions<VirtualDevTeamConfig> config,
        SMEAgentDefinitionService? definitionService = null)
    {
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _runCoordinator = runCoordinator ?? throw new ArgumentNullException(nameof(runCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _definitionService = definitionService;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("VirtualDevTeam starting...");
        _logger.LogInformation("Project: {Name}", _config.Project.Name);

        // Record boot time so the standalone dashboard can filter to current-run agents
        _stateStore.RecordBoot();

        Console.WriteLine(@"
   ___                    __  ____                    __
  / _ |___ ____ ___  ____/ / / __/__ ___ _____ ___ __/ /
 / __ / _ `/ -_) _ \/ __/ / _\ \/ _ `/ // / _ `/ _  / 
/_/ |_\_, /\__/_//_/\__/_/ /___/\_, /\_,_/\_,_/\_,_/  
     /___/                        /_/                   
");
        Console.WriteLine($"  Starting VirtualDevTeam for project: {_config.Project.Name}");
        Console.WriteLine($"  GitHub: {_config.Project.GitHubRepo}");
        Console.WriteLine($"  Max additional engineers: {_config.Limits.MaxAdditionalEngineers}");
        Console.WriteLine();

        // Check ffmpeg availability and log warning if missing
        await CheckFfmpegAvailabilityAsync(ct);

        // Try to recover an in-progress run from the database
        var recovery = await _runCoordinator.RecoverAsync(ct);
        switch (recovery)
        {
            case RunCoordinator.RecoveryResult.ResumeImmediately:
            {
                var run = _runCoordinator.ActiveRun!;
                _logger.LogInformation("Recovered {Mode} run {RunId} — resuming agent spawn",
                    run.Mode, run.RunId);

                // Spawn agents for the recovered run
                await _runCoordinator.SpawnAgentsForRunAsync(ct);
                await SpawnCustomAndSmeAgents(ct);
                break;
            }

            case RunCoordinator.RecoveryResult.WaitForResume:
            {
                var run = _runCoordinator.ActiveRun!;
                _logger.LogInformation(
                    "Recovered {Mode} run {RunId} in Paused state — waiting for user to resume via dashboard",
                    run.Mode, run.RunId);
                // Don't spawn agents — user must press "Continue" on the dashboard
                break;
            }

            case RunCoordinator.RecoveryResult.NoRun:
            default:
            {
                // No active run — the Develop wizard is the sole start trigger.
                // The worker just stays alive; RunCoordinator.StartProjectAsync() is called
                // from the dashboard when the user clicks Start in the Develop tab.
                _logger.LogInformation("No active run found — waiting for project start from Develop wizard");
                break;
            }
        }

        // BUG FIX: Do NOT start agent loops here — SpawnAgentAsync already calls
        // agent.StartAsync() in a background task (lines 87-98 of AgentSpawnManager).
        if (recovery == RunCoordinator.RecoveryResult.ResumeImmediately)
            _logger.LogInformation("All agents spawned. Agent loops already started by SpawnAgentAsync.");
        else
            _logger.LogInformation("Worker standing by — agents will be spawned when a project is started via the wizard or API.");

        // Keep alive until cancellation — agents run as background tasks started by SpawnAgentAsync
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VirtualDevTeam shutting down...");
        }
    }

    private async Task SpawnCustomAndSmeAgents(CancellationToken ct)
    {
        // Spawn custom agents from configuration
        foreach (var customAgent in _config.Agents.CustomAgents)
        {
            if (!customAgent.Enabled || string.IsNullOrWhiteSpace(customAgent.Name))
                continue;

            try
            {
                var customIdentity = await _spawnManager.SpawnCustomAgentAsync(
                    customAgent.Name, customAgent.ModelTier, ct);
                if (customIdentity != null)
                    _logger.LogInformation("Custom agent spawned: {Name}", customIdentity.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to spawn custom agent '{Name}'", customAgent.Name);
            }
        }

        // Respawn persisted SME agents from previous runs (Continuous mode only)
        if (_definitionService is not null && _config.SmeAgents.Enabled)
        {
            try
            {
                var allDefs = await _definitionService.GetAllAsync(ct);
                var continuousDefs = allDefs.Values
                    .Where(d => d.WorkflowMode == SmeWorkflowMode.Continuous)
                    .ToList();

                if (continuousDefs.Count > 0)
                {
                    _logger.LogInformation("Found {Count} persisted Continuous SME agent(s) to respawn", continuousDefs.Count);
                    foreach (var def in continuousDefs)
                    {
                        try
                        {
                            var smeIdentity = await _spawnManager.SpawnSmeAgentAsync(def, ct: ct);
                            if (smeIdentity != null)
                                _logger.LogInformation("Respawned SME agent: {Name} ({DefId})", smeIdentity.DisplayName, def.DefinitionId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to respawn SME agent '{RoleName}' ({DefId})", def.RoleName, def.DefinitionId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load persisted SME definitions for respawn");
            }
        }
    }

    private async Task CheckFfmpegAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0)
                    _logger.LogInformation("ffmpeg detected — GIF generation and video trimming enabled");
                else
                    _logger.LogWarning("ffmpeg not available (exit code {ExitCode}) — GIF generation will be skipped. Install: winget install ffmpeg", proc.ExitCode);
            }
        }
        catch
        {
            _logger.LogWarning("ffmpeg not found on PATH — GIF generation will be skipped. Install: winget install ffmpeg");
        }
    }
}
