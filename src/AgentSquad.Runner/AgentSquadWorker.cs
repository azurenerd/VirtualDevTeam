using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Options;

namespace AgentSquad.Runner;

public class AgentSquadWorker : BackgroundService
{
    private readonly AgentSpawnManager _spawnManager;
    private readonly WorkflowStateMachine _workflow;
    private readonly ILogger<AgentSquadWorker> _logger;
    private readonly AgentSquadConfig _config;

    public AgentSquadWorker(
        AgentSpawnManager spawnManager,
        WorkflowStateMachine workflow,
        ILogger<AgentSquadWorker> logger,
        IOptions<AgentSquadConfig> config)
    {
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
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

        // Phase 1: Spawn the Program Manager
        var pmIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.ProgramManager, ct);
        if (pmIdentity == null)
        {
            _logger.LogCritical("Failed to spawn Program Manager agent");
            return;
        }
        _logger.LogInformation("Program Manager agent spawned: {Name}", pmIdentity.DisplayName);

        // Phase 2: Spawn Researcher
        var researcherIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.Researcher, ct);
        _logger.LogInformation("Researcher agent spawned: {Name}", researcherIdentity?.DisplayName);

        // Phase 3: Spawn Architect
        var architectIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.Architect, ct);
        _logger.LogInformation("Architect agent spawned: {Name}", architectIdentity?.DisplayName);

        // Phase 4: Spawn Principal Engineer
        var principalIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.PrincipalEngineer, ct);
        _logger.LogInformation("Principal Engineer agent spawned: {Name}", principalIdentity?.DisplayName);

        // Phase 5: Spawn Test Engineer
        var testerIdentity = await _spawnManager.SpawnAgentAsync(AgentRole.TestEngineer, ct);
        _logger.LogInformation("Test Engineer agent spawned: {Name}", testerIdentity?.DisplayName);

        _logger.LogInformation("All core agents spawned. PM agent will manage the rest.");

        // Keep alive — the hosted services (agents, health monitor) run independently
        await Task.Delay(Timeout.Infinite, ct);
    }
}
