using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Options;

namespace AgentSquad.Runner;

public class AgentSquadWorker : BackgroundService
{
    private readonly AgentSpawnManager _spawnManager;
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ILogger<AgentSquadWorker> _logger;
    private readonly AgentSquadConfig _config;
    private readonly List<Task> _agentTasks = new();

    public AgentSquadWorker(
        AgentSpawnManager spawnManager,
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        PullRequestWorkflow prWorkflow,
        ILogger<AgentSquadWorker> logger,
        IOptions<AgentSquadConfig> config)
    {
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AgentSquad starting...");
        _logger.LogInformation("Project: {Name}", _config.Project.Name);

        Console.WriteLine(@"
   ___                    __  ____                    __
  / _ |___ ____ ___  ____/ / / __/__ ___ _____ ___ __/ /
 / __ / _ `/ -_) _ \/ __/ / _\ \/ _ `/ // / _ `/ _  / 
/_/ |_\_, /\__/_//_/\__/_/ /___/\_, /\_,_/\_,_/\_,_/  
     /___/                        /_/                   
");
        Console.WriteLine($"  Starting AgentSquad for project: {_config.Project.Name}");
        Console.WriteLine($"  GitHub: {_config.Project.GitHubRepo}");
        Console.WriteLine($"  Max additional engineers: {_config.Limits.MaxAdditionalEngineers}");
        Console.WriteLine();

        // Recover workflow state from SQLite checkpoint if available
        var recovered = await _workflow.RecoverAsync(ct);
        if (recovered)
        {
            _logger.LogInformation("Resumed from checkpoint — workflow phase: {Phase}", _workflow.CurrentPhase);
        }

        // Clean up stale .agentsquad task lock files from the repo so they don't
        // confuse fresh runs with phantom "in-progress" tasks from previous sessions
        await _prWorkflow.CleanupStaleTaskFilesAsync(ct);

        // Spawn all core agents
        var roles = new[]
        {
            AgentRole.ProgramManager,
            AgentRole.Researcher,
            AgentRole.Architect,
            AgentRole.PrincipalEngineer,
            AgentRole.TestEngineer
        };

        foreach (var role in roles)
        {
            var identity = await _spawnManager.SpawnAgentAsync(role, ct);
            if (identity == null)
            {
                _logger.LogCritical("Failed to spawn {Role} agent", role);
                if (role == AgentRole.ProgramManager) return;
                continue;
            }
            _logger.LogInformation("{Role} agent spawned: {Name}", role, identity.DisplayName);
        }

        // BUG FIX: Do NOT start agent loops here — SpawnAgentAsync already calls
        // agent.StartAsync() in a background task (lines 87-98 of AgentSpawnManager).
        // Previously, this worker also iterated all agents calling StartAsync(), causing
        // every agent loop to run TWICE in parallel. This caused duplicate GitHub issues,
        // duplicate research kickoffs, and "Reference already exists" branch errors.
        _logger.LogInformation("All core agents spawned. Agent loops already started by SpawnAgentAsync.");

        // Keep alive until cancellation — agents run as background tasks started by SpawnAgentAsync
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AgentSquad shutting down...");
        }
    }
}
