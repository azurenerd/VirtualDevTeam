using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace AgentSquad.Agents;

/// <summary>
/// Monitors completed PRs and generates test plans / test PRs for untested changes.
/// </summary>
public class TestEngineerAgent : AgentBase
{
    private const string TestedLabel = "tested";

    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly HashSet<int> _testedPRs = new();

    public TestEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        ModelRegistry modelRegistry,
        IOptions<AgentSquadConfig> config,
        ILogger<AgentBase> logger)
        : base(identity, logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Monitoring PRs for test coverage");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanForUntestedPRsAsync(ct);

                // Poll less frequently than other agents
                var pollInterval = TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds * 2);
                await Task.Delay(pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Test engineer loop error");
                RecordError($"Test loop error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                UpdateStatus(AgentStatus.Error, ex.Message);
                await Task.Delay(5000, ct);
                UpdateStatus(AgentStatus.Idle, "Resuming after error");
            }
        }
    }

    private async Task ScanForUntestedPRsAsync(CancellationToken ct)
    {
        var openPRs = await _github.GetOpenPullRequestsAsync(ct);

        foreach (var pr in openPRs)
        {
            if (ct.IsCancellationRequested)
                break;

            if (_testedPRs.Contains(pr.Number))
                continue;

            if (pr.Labels.Contains(TestedLabel, StringComparer.OrdinalIgnoreCase))
            {
                // Already tested externally; track so we don't re-check
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Skip PRs created by this agent to avoid circular testing
            if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only test PRs that are ready for review
            if (!pr.Labels.Contains(PullRequestWorkflow.Labels.ReadyForReview, StringComparer.OrdinalIgnoreCase))
                continue;

            Logger.LogInformation("Found untested PR #{Number}: {Title}", pr.Number, pr.Title);

            try
            {
                await ProcessUntestedPRAsync(pr, ct);
                _testedPRs.Add(pr.Number);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to process tests for PR #{Number}", pr.Number);
            }
        }
    }

    private async Task ProcessUntestedPRAsync(AgentPullRequest pr, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Generating tests for PR #{pr.Number}");

        var testPlan = await GenerateTestPlanAsync(pr, ct);

        if (string.IsNullOrWhiteSpace(testPlan))
        {
            Logger.LogWarning("Empty test plan generated for PR #{Number}", pr.Number);
            return;
        }

        await CreateTestPRAsync(pr, testPlan, ct);

        // Mark the source PR as tested
        await _github.AddPullRequestCommentAsync(
            pr.Number,
            $"🧪 **{Identity.DisplayName}** has generated a test plan and created a test PR for this change.\n\n"
            + "<details>\n<summary>Test Plan Summary</summary>\n\n"
            + testPlan
            + "\n</details>",
            ct);

        var updatedLabels = pr.Labels
            .Append(TestedLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);

        Logger.LogInformation("Completed test generation for PR #{Number}", pr.Number);

        // Notify via message bus
        await _messageBus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "status-update",
            NewStatus = AgentStatus.Working,
            CurrentTask = $"Tests generated for PR #{pr.Number}",
            Details = $"Test plan created for: {pr.Title}"
        }, ct);
    }

    private async Task<string> GenerateTestPlanAsync(AgentPullRequest pr, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var modelConfig = _modelRegistry.GetModelConfig(Identity.ModelTier);

        var prompt = $"""
            You are an expert test engineer. Given the following pull request, generate a comprehensive test plan
            with concrete test cases that should be written.

            ## Pull Request #{pr.Number}: {pr.Title}

            ### Description
            {pr.Body}

            ### Branch
            {pr.HeadBranch} -> {pr.BaseBranch}

            ---

            Generate a test plan in Markdown that includes:
            1. **Test Strategy** — unit tests, integration tests, or end-to-end tests needed
            2. **Test Cases** — numbered list with:
               - Test name
               - Description of what is being tested
               - Expected behavior / assertions
               - Edge cases to cover
            3. **Test File Locations** — suggested file paths for the test files
            4. **Dependencies / Mocks** — any services or components that need mocking

            Be specific and actionable. Focus on testing the changes described, not the entire codebase.
            """;

        var settings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["max_tokens"] = modelConfig?.MaxTokensPerRequest ?? 4096,
                ["temperature"] = modelConfig?.Temperature ?? 0.3
            }
        };

        var result = await kernel.InvokePromptAsync(prompt, new KernelArguments(settings), cancellationToken: ct);
        return result.GetValue<string>() ?? string.Empty;
    }

    private async Task CreateTestPRAsync(AgentPullRequest sourcePR, string testPlan, CancellationToken ct)
    {
        var taskSlug = $"{sourcePR.Number}-tests";
        var branchName = await _prWorkflow.CreateTaskBranchAsync(Identity.DisplayName, taskSlug, ct);

        // Create the test plan file on the branch
        var testPlanPath = $"docs/test-plans/pr-{sourcePR.Number}-test-plan.md";
        var testPlanContent = $"""
            # Test Plan for PR #{sourcePR.Number}: {sourcePR.Title}

            **Generated by:** {Identity.DisplayName}
            **Source PR:** #{sourcePR.Number}
            **Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

            ---

            {testPlan}
            """;

        await _github.CreateOrUpdateFileAsync(
            testPlanPath,
            testPlanContent,
            $"test: add test plan for PR #{sourcePR.Number}",
            branchName,
            ct);

        // Create the test PR
        var prTitle = $"{Identity.DisplayName}: Tests for PR #{sourcePR.Number} - {sourcePR.Title}";
        var prBody = $"""
            ## Test Engineering

            **Source PR:** #{sourcePR.Number}
            **Generated by:** {Identity.DisplayName}

            ### Summary
            This PR contains a test plan and test scaffolding for the changes introduced in PR #{sourcePR.Number}.

            ### Test Plan
            {testPlan}

            ### Checklist
            - [x] Test plan generated
            - [ ] Test implementations reviewed
            - [ ] All tests passing
            """;

        var labels = new[] { "tests", PullRequestWorkflow.Labels.InProgress };

        await _github.CreatePullRequestAsync(
            prTitle,
            prBody,
            branchName,
            _config.Project.DefaultBranch,
            labels,
            ct);

        Logger.LogInformation(
            "Created test PR for source PR #{SourcePR} on branch {Branch}",
            sourcePR.Number, branchName);
    }
}
