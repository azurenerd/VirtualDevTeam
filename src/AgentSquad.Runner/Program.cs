using AgentSquad.Core.Configuration;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;
using AgentSquad.Agents;
using AgentSquad.Runner;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
builder.Services.Configure<AgentSquadConfig>(
    builder.Configuration.GetSection("AgentSquad"));
builder.Services.Configure<LimitsConfig>(
    builder.Configuration.GetSection("AgentSquad:Limits"));

// Core services
builder.Services.AddInProcessMessageBus();
builder.Services.AddSemanticKernelModels();
builder.Services.AddGitHubIntegration();

// Persistence
builder.Services.AddSingleton<AgentStateStore>();
builder.Services.AddSingleton<ProjectFileManager>(sp =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSquadConfig>>().Value;
    return new ProjectFileManager(
        sp.GetRequiredService<IGitHubService>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProjectFileManager>>(),
        config.Project.DefaultBranch);
});

// GitHub workflows
builder.Services.AddSingleton<PullRequestWorkflow>(sp =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSquadConfig>>().Value;
    return new PullRequestWorkflow(
        sp.GetRequiredService<IGitHubService>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PullRequestWorkflow>>(),
        config.Project.DefaultBranch);
});
builder.Services.AddSingleton<IssueWorkflow>();
builder.Services.AddSingleton<ConflictResolver>();

// Orchestrator (registry, health monitor, deadlock detector, spawn manager, workflow)
builder.Services.AddOrchestrator();

// Agent factory
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();

// Worker service that starts the core agents and kicks off the workflow
builder.Services.AddHostedService<AgentSquadWorker>();

var host = builder.Build();
host.Run();
