using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.E2E.Tests.Infrastructure;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.E2E.Tests.Scenarios;

/// <summary>
/// Scenario 2: Split-PR, Split-Issue E2E workflow test.
/// 
/// Tests the complete workflow with multiple engineering tasks creating
/// separate PRs and issues, versus the single-PR monolithic mode.
/// Uses split engineering plan with 3 tasks (T1 Foundation + T2 Home + T3 Privacy).
/// </summary>
public class SplitPrSplitIssueTests : IDisposable
{
    private readonly E2ETestHarness _harness;

    public SplitPrSplitIssueTests()
    {
        _harness = E2ETestHarness.Create(
            config =>
            {
                config.Limits.SinglePRMode = false;
                config.Limits.SingleIssueMode = false;
                config.Limits.MaxAdditionalEngineers = 0;
                config.Limits.GitHubPollIntervalSeconds = 1;
            },
            HelloWorldScripts.CreateForSplitPR());
    }

    [Fact]
    public async Task CanStartAndSpawnAgents()
    {
        var run = await _harness.Coordinator.StartProjectAsync();
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Running, run.Status);

        await _harness.Coordinator.SpawnAgentsForRunAsync();
        var agents = _harness.Registry.GetAllAgents();
        Assert.NotEmpty(agents);

        var roles = agents.Select(a => a.Identity.Role).Distinct().ToList();
        Assert.Contains(AgentRole.ProgramManager, roles);
        Assert.Contains(AgentRole.Researcher, roles);
    }

    /// <summary>
    /// Diagnostic run for split-PR mode: drives the workflow and dumps full state.
    /// With multiple tasks, the SE should create separate PRs for each.
    /// </summary>
    [Fact]
    public async Task DiagnosticRun_SplitPR_DumpState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var run = await _harness.StartFullRunAsync(cts.Token);
        Assert.Equal(RunStatus.Running, run.Status);

        var lastPhase = _harness.Workflow.CurrentPhase;
        var maxPhaseReached = lastPhase;
        var phaseLog = new List<string> { $"[0s] Phase: {lastPhase}" };
        var startTime = DateTime.UtcNow;

        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(2000, cts.Token).ContinueWith(_ => { });
            if (cts.Token.IsCancellationRequested) break;

            var currentPhase = _harness.Workflow.CurrentPhase;
            if (currentPhase != lastPhase)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                phaseLog.Add($"[{elapsed:F0}s] Phase: {lastPhase} → {currentPhase}");
                lastPhase = currentPhase;
                if (currentPhase > maxPhaseReached)
                    maxPhaseReached = currentPhase;
            }

            if (currentPhase == ProjectPhase.Completion)
                break;
        }

        // Dump diagnostics
        var agents = _harness.Registry.GetAllAgents();
        var agentStates = agents.Select(a =>
            $"  {a.Identity.DisplayName} ({a.Identity.Role}): Status={a.Status}, Reason={a.StatusReason ?? "(null)"}")
            .ToList();

        var llmCalls = _harness.ChatService.CallLog;
        var callSummary = llmCalls.Select((c, i) =>
            $"  [{i}] Prompt: {c.SystemPromptSnippet[..Math.Min(80, c.SystemPromptSnippet.Length)]}... → Response: {c.Response[..Math.Min(60, c.Response.Length)]}...")
            .ToList();

        var issues = await _harness.GitHub.GetAllIssuesAsync();
        var prs = await _harness.GitHub.GetAllPullRequestsAsync();

        var diagnostics = string.Join("\n", new[]
        {
            $"=== SPLIT-PR E2E DIAGNOSTIC RUN ===",
            $"Max phase reached: {maxPhaseReached}",
            $"Phase transitions:",
            string.Join("\n", phaseLog),
            $"",
            $"Agent states ({agents.Count} agents):",
            string.Join("\n", agentStates),
            $"",
            $"LLM calls ({llmCalls.Count} total):",
            string.Join("\n", callSummary),
            $"",
            $"GitHub Issues: {issues.Count}",
            string.Join("\n", issues.Select(i => $"  #{i.Number}: {i.Title} [{i.State}] Labels=[{string.Join(",", i.Labels)}]")),
            $"",
            $"GitHub PRs: {prs.Count}",
            string.Join("\n", prs.Select(p => $"  #{p.Number}: {p.Title} [{p.State}] Merged={p.IsMerged} Labels=[{string.Join(",", p.Labels)}]")),
            $"",
            $"In-Memory File Tree:",
            _harness.GitHub.DumpFiles(),
            $"",
            $"Log Errors ({_harness.LogSink.Errors.Count}):",
            string.Join("\n", _harness.LogSink.Errors.Take(20)),
            $"",
            $"Review/Merge Logs:",
            string.Join("\n", _harness.LogSink.Entries
                .Where(e => (
                    e.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("merge", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("TASK|", StringComparison.Ordinal) ||
                    e.Contains("engineering task", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("parsed", StringComparison.OrdinalIgnoreCase)) &&
                    !e.Contains("PromptTemplate") &&
                    !e.Contains("Loop exited"))
                .Take(50)),
        });

        var diagFile = Path.Combine(Path.GetTempPath(), "e2e_split_pr_diagnostic.txt");
        File.WriteAllText(diagFile, diagnostics);

        Assert.True(maxPhaseReached >= ProjectPhase.Research,
            $"Should at least reach Research.\n\n{diagnostics}");
    }

    /// <summary>
    /// Full E2E: split-PR workflow from start to completion.
    /// Verifies docs are created, enhancement issues exist, and at least one
    /// implementation PR is merged. The signal helper may advance phases before
    /// all tasks complete (expected in test mode with instant LLM responses).
    /// </summary>
    [Fact]
    public async Task FullWorkflow_SplitPR_CompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var run = await _harness.StartFullRunAsync(cts.Token);
        Assert.Equal(RunStatus.Running, run.Status);

        var completed = await Helpers.PhaseWaiter.WaitForPhaseAsync(
            _harness.Workflow,
            ProjectPhase.Completion,
            TimeSpan.FromMinutes(3),
            cts.Token);

        Assert.True(completed,
            $"Workflow should reach Completion. Current phase: {_harness.Workflow.CurrentPhase}");

        // Verify doc PRs created and merged
        var prs = await _harness.GitHub.GetAllPullRequestsAsync();
        Assert.True(prs.Any(p => p.IsMerged && p.Title.Contains("Research", StringComparison.OrdinalIgnoreCase)),
            "Research PR should be merged");
        Assert.True(prs.Any(p => p.IsMerged && p.Title.Contains("Architecture", StringComparison.OrdinalIgnoreCase)),
            "Architecture PR should be merged");

        // In split mode, the PM should have created multiple enhancement issues
        var issues = await _harness.GitHub.GetAllIssuesAsync();
        var enhancements = issues.Where(i =>
            i.Labels.Contains("enhancement", StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(enhancements.Count >= 2,
            $"Split mode should create at least 2 enhancement issues. Got {enhancements.Count}");

        // At least one implementation PR should be merged
        var implPrs = prs.Where(p =>
            p.IsMerged && (
            p.Title.Contains("Implement", StringComparison.OrdinalIgnoreCase) ||
            p.Title.Contains("Foundation", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(implPrs.Count >= 1,
            $"Should have at least 1 merged implementation PR. Got {implPrs.Count}");

        // Verify key documents exist
        var allFiles = _harness.GitHub.DumpFiles();
        Assert.Contains("Research.md", allFiles);
        Assert.Contains("Architecture.md", allFiles);

        // Verify no errors
        Assert.Empty(_harness.LogSink.Errors);
    }

    public void Dispose()
    {
        _harness.Dispose();
    }
}
