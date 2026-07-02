using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.E2E.Tests.Infrastructure;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.E2E.Tests.Scenarios;

/// <summary>
/// Scenario 1: Single-PR, Single-Issue E2E workflow test.
/// 
/// Tests the complete workflow from Initialization through Completion with:
/// - Pre-built content (no real LLM calls for content generation)
/// - InMemoryGitHubService (no real GitHub API)
/// - AutoApproveGateCheckService (no human gates)
/// - Single PR mode (one PR for all work)
/// - Copilot CLI framework only
/// </summary>
public class SinglePrSingleIssueTests : IDisposable
{
    private readonly E2ETestHarness _harness;

    public SinglePrSingleIssueTests()
    {
        _harness = E2ETestHarness.Create(config =>
        {
            config.Limits.SinglePRMode = true;
            config.Limits.SingleIssueMode = true;
            config.Limits.MaxAdditionalEngineers = 0;
            config.Limits.GitHubPollIntervalSeconds = 1;
        });
    }

    [Fact]
    public async Task CanStartProjectRun()
    {
        var run = await _harness.Coordinator.StartProjectAsync();

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(WorkMode.Project, run.Mode);
        Assert.Equal("test-owner/hello-world", run.Repo);
    }

    [Fact]
    public async Task CanSpawnAgentsForRun()
    {
        await _harness.Coordinator.StartProjectAsync();
        await _harness.Coordinator.SpawnAgentsForRunAsync();

        var agents = _harness.Registry.GetAllAgents();
        Assert.NotEmpty(agents);

        var roles = agents.Select(a => a.Identity.Role).Distinct().ToList();
        Assert.Contains(AgentRole.ProgramManager, roles);
        Assert.Contains(AgentRole.Researcher, roles);
    }

    [Fact]
    public async Task WorkflowAdvancesFromInitializationToResearch()
    {
        Assert.Equal(ProjectPhase.Initialization, _harness.Workflow.CurrentPhase);

        // StartFullRunAsync includes HealthMonitor which drives phase transitions
        await _harness.StartFullRunAsync();

        // Give agents a moment to initialize and come online
        await Task.Delay(500);

        // The PM coming online should trigger Initialization → Research
        var advanced = await Helpers.PhaseWaiter.WaitForPhaseAsync(
            _harness.Workflow,
            ProjectPhase.Research,
            TimeSpan.FromSeconds(10));

        Assert.True(advanced, "Workflow should advance to Research phase when PM is online");
    }

    /// <summary>
    /// Test that the workflow advances through Research phase when agents
    /// produce the right signals via HealthMonitor auto-detection.
    /// </summary>
    [Fact]
    public async Task WorkflowAdvancesThroughResearchPhase()
    {
        await _harness.StartFullRunAsync();

        // Wait for Research phase to complete → Architecture phase
        // HealthMonitor auto-detects signals from agent status reasons
        var reachedArchitecture = await Helpers.PhaseWaiter.WaitForPhaseAsync(
            _harness.Workflow,
            ProjectPhase.Architecture,
            TimeSpan.FromSeconds(60));

        Assert.True(reachedArchitecture,
            $"Workflow should advance past Research. Current phase: {_harness.Workflow.CurrentPhase}");
    }

    /// <summary>
    /// Diagnostic: run the workflow for up to 90 seconds and dump agent states + LLM calls.
    /// This helps identify where the workflow gets stuck and what scripts are missing.
    /// </summary>
    [Fact]
    public async Task DiagnosticRun_DumpAgentStatesAndLLMCalls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var run = await _harness.StartFullRunAsync(cts.Token);
        Assert.Equal(RunStatus.Running, run.Status);

        var lastPhase = _harness.Workflow.CurrentPhase;
        var maxPhaseReached = lastPhase;
        var phaseLog = new List<string> { $"[0s] Phase: {lastPhase}" };
        var startTime = DateTime.UtcNow;

        // Run for up to 90s or until Completion, whichever comes first
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
            $"=== E2E DIAGNOSTIC RUN ===",
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
            string.Join("\n", issues.Select(i => $"  #{i.Number}: {i.Title} [{i.State}]")),
            $"",
            $"GitHub PRs: {prs.Count}",
            string.Join("\n", prs.Select(p => $"  #{p.Number}: {p.Title} [{p.State}] Merged={p.IsMerged} Labels=[{string.Join(",", p.Labels)}]")),
            $"",
            $"PR #8 Comments: {(prs.FirstOrDefault(p => p.Number == 8)?.Comments?.Count ?? 0)}",
            string.Join("\n", (prs.FirstOrDefault(p => p.Number == 8)?.Comments ?? new List<VirtualDevTeam.Core.GitHub.Models.IssueComment>())
                .Select(c => $"  [{c.CreatedAt:HH:mm:ss}] {c.Body[..Math.Min(120, c.Body.Length)]}")),
            $"",
            $"In-Memory File Tree:",
            _harness.GitHub.DumpFiles(),
            $"",
            $"Log Errors ({_harness.LogSink.Errors.Count}):",
            string.Join("\n", _harness.LogSink.Errors.Take(20)),
            $"",
            $"Log Warnings ({_harness.LogSink.Warnings.Count}):",
            string.Join("\n", _harness.LogSink.Warnings.Take(20)),
            $"",
            $"Key Info Logs (LLM/PMSpec/Engineering/loop):",
            string.Join("\n", _harness.LogSink.Entries
                .Where(e => e.StartsWith("[Information]") && (
                    e.Contains("LLM", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("PMSpec", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("Quick", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("engineering plan", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("loop", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("ChatCompletion", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("kernel", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("user story", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("task", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("mono", StringComparison.OrdinalIgnoreCase)))
                .Take(30)),
            $"",
            $"Review/Merge Logs:",
            string.Join("\n", _harness.LogSink.Entries
                .Where(e => (
                    e.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("merge", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("pm-approved", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("architect-approved", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("ReviewEngineer", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("approvals", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("verdict", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("ready-for-review", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("ReviewPullRequests", StringComparison.OrdinalIgnoreCase)) &&
                    !e.Contains("PromptTemplate") &&
                    !e.Contains("Loop exited"))
                .Take(50)),
        });

        // Write diagnostics to temp file for examination
        var diagFile = Path.Combine(Path.GetTempPath(), "e2e_diagnostic.txt");
        File.WriteAllText(diagFile, diagnostics);

        // This test always passes — it's for diagnostics
        // The assert message contains the full diagnostic output
        Assert.True(maxPhaseReached >= ProjectPhase.Research,
            $"Should at least reach Research.\n\n{diagnostics}");
    }

    /// <summary>
    /// Full E2E: drive the workflow from start to completion.
    /// Verifies all phases visited, all docs created, PRs merged, issues closed.
    /// </summary>
    [Fact]
    public async Task FullWorkflow_SinglePR_CompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var run = await _harness.StartFullRunAsync(cts.Token);
        Assert.Equal(RunStatus.Running, run.Status);

        var completed = await Helpers.PhaseWaiter.WaitForPhaseAsync(
            _harness.Workflow,
            ProjectPhase.Completion,
            TimeSpan.FromMinutes(2),
            cts.Token);

        Assert.True(completed,
            $"Workflow should reach Completion. Current phase: {_harness.Workflow.CurrentPhase}");

        // Verify all doc PRs were created and merged
        var prs = await _harness.GitHub.GetAllPullRequestsAsync();
        Assert.True(prs.Count >= 3, $"Should have at least 3 PRs (Research, PMSpec, Architecture). Got {prs.Count}");
        Assert.True(prs.Any(p => p.IsMerged && p.Title.Contains("Research", StringComparison.OrdinalIgnoreCase)),
            "Research PR should be merged");
        Assert.True(prs.Any(p => p.IsMerged && p.Title.Contains("PM Specification", StringComparison.OrdinalIgnoreCase)),
            "PMSpec PR should be merged");
        Assert.True(prs.Any(p => p.IsMerged && p.Title.Contains("Architecture", StringComparison.OrdinalIgnoreCase)),
            "Architecture PR should be merged");

        // Verify implementation PR was created and merged
        var implPrs = prs.Where(p =>
            p.Title.Contains("Implement", StringComparison.OrdinalIgnoreCase) ||
            p.Title.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(implPrs);
        Assert.True(implPrs.All(p => p.IsMerged),
            "All implementation PRs should be merged");

        // Verify key documents exist in main branch
        var allFiles = _harness.GitHub.DumpFiles();
        Assert.Contains("Research.md", allFiles);
        Assert.Contains("PMSpec.md", allFiles);
        Assert.Contains("Architecture.md", allFiles);

        // Verify no unrecoverable errors
        Assert.Empty(_harness.LogSink.Errors);

        // Verify LLM was called for key activities
        Assert.True(_harness.ChatService.CallLog.Count >= 8,
            $"Should have at least 8 LLM calls (3 docs + plan + impl + reviews). Got {_harness.ChatService.CallLog.Count}");
    }

    public void Dispose()
    {
        _harness.Dispose();
    }
}
