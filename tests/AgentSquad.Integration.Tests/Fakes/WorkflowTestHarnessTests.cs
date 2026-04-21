namespace AgentSquad.Integration.Tests.Fakes;

using AgentSquad.Core.Agents;
using AgentSquad.Orchestrator;

public class WorkflowTestHarnessTests : IDisposable
{
    private readonly WorkflowTestHarness _harness = WorkflowTestHarness.Create();

    [Fact]
    public void Create_BuildsServiceProvider()
    {
        Assert.NotNull(_harness.GitHub);
        Assert.NotNull(_harness.MessageBus);
        Assert.NotNull(_harness.Workflow);
        Assert.NotNull(_harness.Registry);
    }

    [Fact]
    public void GitHub_IsInMemoryInstance()
    {
        Assert.IsType<InMemoryGitHubService>(
            _harness.Services.GetService(typeof(AgentSquad.Core.GitHub.IGitHubService)));
    }

    [Fact]
    public async Task GitHub_CanCreateAndQueryIssues()
    {
        var issue = await _harness.GitHub.CreateIssueAsync("Test issue", "body", ["bug"]);
        var found = await _harness.GitHub.GetIssueAsync(issue.Number);
        Assert.NotNull(found);
        Assert.Equal("Test issue", found.Title);
    }

    [Fact]
    public void Workflow_StartsAtInitialization()
    {
        Assert.Equal(ProjectPhase.Initialization, _harness.Workflow.CurrentPhase);
    }

    [Fact]
    public async Task Signal_AdvancesWorkflow()
    {
        // Initialization → Research requires PM Online
        await _harness.RegisterFakeAgentAsync(AgentRole.ProgramManager);
        _harness.Workflow.TryAdvancePhase(out _);
        Assert.Equal(ProjectPhase.Research, _harness.Workflow.CurrentPhase);
    }

    [Fact]
    public async Task AdvanceUntil_ReturnsTrueWhenPredicateMet()
    {
        await _harness.RegisterFakeAgentAsync(AgentRole.ProgramManager);
        _harness.Workflow.TryAdvancePhase(out _);
        var reached = await _harness.AdvanceUntilAsync(
            wf => wf.CurrentPhase == ProjectPhase.Research,
            TimeSpan.FromSeconds(1));
        Assert.True(reached);
    }

    [Fact]
    public async Task AdvanceUntil_ReturnsFalseOnTimeout()
    {
        var reached = await _harness.AdvanceUntilAsync(
            wf => wf.CurrentPhase == ProjectPhase.Completion,
            TimeSpan.FromMilliseconds(100));
        Assert.False(reached);
    }

    [Fact]
    public void Create_WithCustomConfig_AppliesOverrides()
    {
        using var harness = WorkflowTestHarness.Create(cfg =>
        {
            cfg.Project.GitHubRepo = "custom/repo";
        });

        Assert.Equal("custom/repo", harness.GitHub.RepositoryFullName);
    }

    public void Dispose() => _harness.Dispose();
}
