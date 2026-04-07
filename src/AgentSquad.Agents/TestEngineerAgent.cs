using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// Monitors merged PRs and generates real test code (unit, integration, UI tests)
/// for code changes. Only triggers after a PR is reviewed, approved, and merged —
/// ignores non-code artifacts like markdown documentation.
/// </summary>
public class TestEngineerAgent : AgentBase
{
    private const string TestedLabel = "tested";

    /// <summary>
    /// File extensions that are testable code. Everything else (markdown, images,
    /// config, etc.) is ignored when deciding whether a merged PR needs tests.
    /// </summary>
    private static readonly HashSet<string> TestableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rs",
        ".razor", ".blazor", ".vue", ".svelte", ".rb", ".php", ".swift", ".kt"
    };

    private readonly IMessageBus _messageBus;
    private readonly IGitHubService _github;
    private readonly PullRequestWorkflow _prWorkflow;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly HashSet<int> _testedPRs = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<(int PrNumber, string PrTitle, string Feedback, string Reviewer)> _reworkQueue = new();
    private readonly Dictionary<int, int> _reworkAttempts = new();
    private int? _currentTestPrNumber;

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

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<ChangesRequestedMessage>(
            Identity.Id, HandleChangesRequestedAsync));
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Monitoring merged PRs for test coverage");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Priority 1: Process rework feedback on test PRs
                await ProcessReworkAsync(ct);

                // Priority 2: Recover any open test PRs that need review
                await RecoverTestPRsAsync(ct);

                // Priority 3: Scan for new merged PRs to test
                await ScanMergedPRsForTestingAsync(ct);

                // Poll less frequently than other agents
                var pollInterval = TimeSpan.FromSeconds(_config.Limits.GitHubPollIntervalSeconds * 3);
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

    /// <summary>
    /// Scans recently merged PRs and generates tests for any that contain code changes
    /// and haven't been tested yet.
    /// </summary>
    private async Task ScanMergedPRsForTestingAsync(CancellationToken ct)
    {
        var mergedPRs = await _github.GetMergedPullRequestsAsync(ct);

        foreach (var pr in mergedPRs)
        {
            if (ct.IsCancellationRequested)
                break;

            if (_testedPRs.Contains(pr.Number))
                continue;

            // Skip PRs already labeled as tested
            if (pr.Labels.Contains(TestedLabel, StringComparer.OrdinalIgnoreCase))
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Skip PRs created by this agent to avoid circular testing
            if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Get the files changed in this PR to check if it has testable code
            var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
            var codeFiles = changedFiles
                .Where(f => TestableExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (codeFiles.Count == 0)
            {
                // No code files — only docs/config/images. Skip.
                Logger.LogDebug("Skipping PR #{Number} — no testable code files (only docs/config)", pr.Number);
                _testedPRs.Add(pr.Number);
                continue;
            }

            Logger.LogInformation(
                "Found merged PR #{Number} with {Count} testable code files: {Title}",
                pr.Number, codeFiles.Count, pr.Title);
            LogActivity("task", $"🧪 Generating tests for PR #{pr.Number}: {pr.Title} ({codeFiles.Count} code files)");

            try
            {
                await GenerateTestsForMergedPRAsync(pr, codeFiles, ct);
                _testedPRs.Add(pr.Number);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate tests for merged PR #{Number}", pr.Number);
            }
        }
    }

    /// <summary>
    /// Reads the actual source code from the merged PR's files on main,
    /// generates real test code via AI, and creates a test PR with those files.
    /// </summary>
    private async Task GenerateTestsForMergedPRAsync(
        AgentPullRequest pr, List<string> codeFilePaths, CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Reading code from merged PR #{pr.Number}");

        // Read the actual code content from the main branch (files are merged there now)
        var sourceFiles = new Dictionary<string, string>();
        foreach (var filePath in codeFilePaths)
        {
            try
            {
                var content = await _github.GetFileContentAsync(filePath, _config.Project.DefaultBranch, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    sourceFiles[filePath] = content;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not read {Path} from main branch", filePath);
            }
        }

        if (sourceFiles.Count == 0)
        {
            // Files no longer exist on main — mark as tested to avoid re-scanning every cycle
            Logger.LogWarning("Could not read any source files from merged PR #{Number} (files may have been removed)", pr.Number);
            _testedPRs.Add(pr.Number);
            return;
        }

        UpdateStatus(AgentStatus.Working, $"Generating tests for PR #{pr.Number} ({sourceFiles.Count} files)");

        // Generate real test code via AI
        var testOutput = await GenerateTestCodeAsync(pr, sourceFiles, ct);

        if (string.IsNullOrWhiteSpace(testOutput))
        {
            Logger.LogWarning("Empty test output for PR #{Number}", pr.Number);
            return;
        }

        // Parse the AI output into code files
        var testFiles = CodeFileParser.ParseFiles(testOutput);

        if (testFiles.Count == 0)
        {
            Logger.LogWarning("AI generated test content but no parseable files for PR #{Number}", pr.Number);
            return;
        }

        // Create the test PR with real code files
        var testPrNumber = await CreateTestPRWithCodeAsync(pr, testFiles, ct);
        _currentTestPrNumber = testPrNumber;

        Logger.LogInformation(
            "Created test PR #{TestPR} with {Count} test files for merged PR #{SourcePR}",
            testPrNumber, testFiles.Count, pr.Number);
        LogActivity("task", $"✅ Created test PR #{testPrNumber} with {testFiles.Count} test files for PR #{pr.Number}");

        // Apply "tested" label to the source PR so we don't re-process it on restart
        try
        {
            var sourcePrData = await _github.GetPullRequestAsync(pr.Number, ct);
            if (sourcePrData is not null)
            {
                var updatedLabels = sourcePrData.Labels
                    .Append(TestedLabel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not apply tested label to source PR #{Number}", pr.Number);
        }

        // Mark test PR ready-for-review and request PE review
        await _prWorkflow.MarkReadyForReviewAsync(testPrNumber, Identity.DisplayName, ct);

        await _messageBus.PublishAsync(new ReviewRequestMessage
        {
            FromAgentId = Identity.Id,
            ToAgentId = "*",
            MessageType = "ReviewRequest",
            PrNumber = testPrNumber,
            PrTitle = $"{Identity.DisplayName}: Tests for PR #{pr.Number} - {pr.Title}",
            ReviewType = "CodeReview"
        }, ct);

        Logger.LogInformation("Test PR #{TestPR} marked ready-for-review, requested PE review", testPrNumber);
        UpdateStatus(AgentStatus.Idle, $"Test PR #{testPrNumber} awaiting PE review");
    }

    /// <summary>
    /// Uses AI to generate real, runnable test code for the source files in a merged PR.
    /// The prompt includes the actual file contents so the AI writes tests against real code.
    /// </summary>
    private async Task<string> GenerateTestCodeAsync(
        AgentPullRequest pr, Dictionary<string, string> sourceFiles, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var techStack = _config.Project.TechStack;

        var history = new ChatHistory();
        history.AddSystemMessage(
            $"You are an expert test engineer writing tests for a {techStack} project.\n\n" +
            "Your job is to generate REAL, RUNNABLE test code — not documentation or test plans.\n" +
            "Write actual test files that can be compiled and executed.\n\n" +
            "Guidelines:\n" +
            "- Write unit tests for individual functions, methods, and classes\n" +
            "- Write integration tests for component interactions where applicable\n" +
            "- Write UI/rendering tests for frontend components where applicable\n" +
            "- Use the standard testing framework for the tech stack (e.g., xUnit for C#, " +
            "Jest for TypeScript, pytest for Python, bUnit for Blazor components)\n" +
            "- Include proper imports, test class setup, and assertions\n" +
            "- Test happy paths, edge cases, and error conditions\n" +
            "- Use mocks/stubs for external dependencies\n" +
            "- Place test files in a `tests/` directory mirroring the source structure\n\n" +
            "Output each test file using this exact format:\n\n" +
            "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
            "Every file MUST use the FILE: marker format so it can be parsed and committed.");

        // Build source file context
        var sourceContext = new System.Text.StringBuilder();
        sourceContext.AppendLine("## Source Files to Test\n");
        foreach (var (path, content) in sourceFiles)
        {
            var ext = Path.GetExtension(path).TrimStart('.');
            sourceContext.AppendLine($"### {path}");
            sourceContext.AppendLine($"```{ext}");
            // Truncate very large files to avoid token limits
            var truncated = content.Length > 8000 ? content[..8000] + "\n// ... (truncated)" : content;
            sourceContext.AppendLine(truncated);
            sourceContext.AppendLine("```\n");
        }

        history.AddUserMessage(
            $"## Merged PR #{pr.Number}: {pr.Title}\n\n" +
            $"## PR Description\n{pr.Body}\n\n" +
            sourceContext.ToString() +
            $"\nGenerate comprehensive test files for the above source code using {techStack}. " +
            "Focus on testing the actual implementation — functions, classes, components, " +
            "and their behavior. Include edge cases and error handling.");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content?.Trim() ?? "";
    }

    /// <summary>
    /// Creates a test PR with actual test code files committed to a branch.
    /// </summary>
    private async Task<int> CreateTestPRWithCodeAsync(
        AgentPullRequest sourcePR,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        CancellationToken ct)
    {
        var taskSlug = $"{sourcePR.Number}-tests";
        var branchName = await _prWorkflow.CreateTaskBranchAsync(Identity.DisplayName, taskSlug, ct);

        // Commit all test files to the branch
        foreach (var file in testFiles)
        {
            await _github.CreateOrUpdateFileAsync(
                file.Path,
                file.Content,
                $"test: add {Path.GetFileName(file.Path)} for PR #{sourcePR.Number}",
                branchName,
                ct);
        }

        // Create the test PR with file listing
        var fileList = string.Join("\n", testFiles.Select(f => $"- `{f.Path}`"));
        var prTitle = $"{Identity.DisplayName}: Tests for PR #{sourcePR.Number} - {sourcePR.Title}";
        var prBody = $"""
            ## Test Engineering

            **Source PR:** #{sourcePR.Number} (merged)
            **Generated by:** {Identity.DisplayName}
            **Test Files:** {testFiles.Count}

            ### Test Files
            {fileList}

            ### Coverage
            - Unit tests for new/changed functions and classes
            - Integration tests for component interactions
            - Edge case and error handling coverage

            ### How to Run
            Run the test suite with the standard test runner for the project tech stack.
            """;

        var labels = new[] { "tests", PullRequestWorkflow.Labels.InProgress };

        var testPr = await _github.CreatePullRequestAsync(
            prTitle,
            prBody,
            branchName,
            _config.Project.DefaultBranch,
            labels,
            ct);

        Logger.LogInformation(
            "Created test PR #{TestPR} for merged PR #{SourcePR} on branch {Branch}",
            testPr.Number, sourcePR.Number, branchName);

        return testPr.Number;
    }

    #region Rework Loop

    private Task HandleChangesRequestedAsync(ChangesRequestedMessage message, CancellationToken ct)
    {
        if (_currentTestPrNumber != message.PrNumber)
            return Task.CompletedTask;

        Logger.LogInformation("TestEngineer received change request from {Reviewer} on PR #{PrNumber}",
            message.ReviewerAgent, message.PrNumber);

        _reworkQueue.Enqueue((message.PrNumber, message.PrTitle, message.Feedback, message.ReviewerAgent));
        return Task.CompletedTask;
    }

    private async Task ProcessReworkAsync(CancellationToken ct)
    {
        while (_reworkQueue.TryDequeue(out var rework))
        {
            var pr = await _github.GetPullRequestAsync(rework.PrNumber, ct);
            if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                continue;

            var attempts = _reworkAttempts.GetValueOrDefault(rework.PrNumber, 0) + 1;
            _reworkAttempts[rework.PrNumber] = attempts;

            if (attempts > _config.Limits.MaxReworkCycles)
            {
                Logger.LogWarning("TestEngineer reached max rework cycles for PR #{PrNumber}", rework.PrNumber);
                await _github.AddPullRequestCommentAsync(rework.PrNumber,
                    $"⚠️ **{Identity.DisplayName}** has reached the maximum rework cycle limit. " +
                    "Requesting final approval to unblock progress.", ct);
                await _messageBus.PublishAsync(new ReviewRequestMessage
                {
                    FromAgentId = Identity.Id,
                    ToAgentId = "*",
                    MessageType = "ReviewRequest",
                    PrNumber = pr.Number,
                    PrTitle = pr.Title,
                    ReviewType = "FinalApproval"
                }, ct);
                return;
            }

            UpdateStatus(AgentStatus.Working,
                $"Addressing feedback on test PR #{rework.PrNumber} (attempt {attempts}/{_config.Limits.MaxReworkCycles})");
            Logger.LogInformation("TestEngineer reworking PR #{PrNumber} based on feedback from {Reviewer} (attempt {Attempt})",
                rework.PrNumber, rework.Reviewer, attempts);

            try
            {
                var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var techStack = _config.Project.TechStack;

                var history = new ChatHistory();
                history.AddSystemMessage(
                    $"You are an expert test engineer maintaining tests for a {techStack} project.\n" +
                    "A reviewer requested changes on your test PR. Update the test files to address all feedback.\n\n" +
                    "Output each corrected file using this exact format:\n" +
                    "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
                    "Include the COMPLETE content of each changed file.");

                history.AddUserMessage(
                    $"## Test PR #{rework.PrNumber}: {rework.PrTitle}\n\n" +
                    $"## Original PR Description\n{pr.Body}\n\n" +
                    $"## Review Feedback from {rework.Reviewer}\n{rework.Feedback}\n\n" +
                    "Please provide the corrected test files that address all the feedback.");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var updatedContent = response.Content?.Trim() ?? "";

                if (!string.IsNullOrWhiteSpace(updatedContent))
                {
                    var codeFiles = CodeFileParser.ParseFiles(updatedContent);
                    if (codeFiles.Count > 0)
                    {
                        await _prWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, "Address review feedback on tests", ct);
                    }

                    await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);
                    await _messageBus.PublishAsync(new ReviewRequestMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ReviewRequest",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewType = "Rework"
                    }, ct);

                    Logger.LogInformation("TestEngineer submitted rework for PR #{PrNumber}, re-requesting review", pr.Number);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "TestEngineer failed rework on PR #{PrNumber}", rework.PrNumber);
                _reworkQueue.Enqueue(rework);
            }
        }
    }

    /// <summary>
    /// On restart, recover any open test PRs that need review or have unaddressed feedback.
    /// </summary>
    private async Task RecoverTestPRsAsync(CancellationToken ct)
    {
        if (_currentTestPrNumber is not null)
            return; // Already tracking a PR

        try
        {
            var myPRs = await _prWorkflow.GetAgentTasksAsync(Identity.DisplayName, ct);
            foreach (var pr in myPRs)
            {
                if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
                    continue;

                _currentTestPrNumber = pr.Number;

                // Check for unaddressed feedback
                var pendingFeedback = await _prWorkflow.GetPendingChangesRequestedAsync(pr.Number, ct);
                if (pendingFeedback is var (reviewer, feedback))
                {
                    _reworkQueue.Enqueue((pr.Number, pr.Title, feedback, reviewer));
                    Logger.LogInformation("TestEngineer recovered feedback on PR #{PrNumber} from {Reviewer}",
                        pr.Number, reviewer);
                    UpdateStatus(AgentStatus.Working, $"Processing feedback on test PR #{pr.Number}");
                    return;
                }

                // Check if PR needs review
                if (pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                {
                    // Check if PE already approved — maybe we can just wait for merge
                    if (!await _prWorkflow.NeedsReviewFromAsync(pr.Number, "PrincipalEngineer", ct))
                    {
                        UpdateStatus(AgentStatus.Idle, $"Test PR #{pr.Number} reviewed, awaiting merge");
                        return;
                    }

                    // Re-request review
                    await _messageBus.PublishAsync(new ReviewRequestMessage
                    {
                        FromAgentId = Identity.Id,
                        ToAgentId = "*",
                        MessageType = "ReviewRequest",
                        PrNumber = pr.Number,
                        PrTitle = pr.Title,
                        ReviewType = "Recovery"
                    }, ct);

                    Logger.LogInformation("TestEngineer re-requested review for PR #{PrNumber}", pr.Number);
                    UpdateStatus(AgentStatus.Idle, $"Test PR #{pr.Number} awaiting PE review");
                    return;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to recover test PRs");
        }
    }

    #endregion
}
