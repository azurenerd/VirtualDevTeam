using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
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
    private readonly ProjectFileManager _projectFiles;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentSquadConfig _config;

    private readonly HashSet<int> _testedPRs = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<(int PrNumber, string PrTitle, string Feedback, string Reviewer)> _reworkQueue = new();
    private readonly Dictionary<int, int> _reworkAttempts = new();
    private readonly Dictionary<int, string> _prSessionIds = new();
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow;
    private int? _currentTestPrNumber;

    public TestEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        ILogger<AgentBase> logger)
        : base(identity, logger, memoryStore)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
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

                await RefreshDiagnosticWithMemoryAsync(ct);

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

            // Skip PRs merged before this session started — they're from previous runs
            if (pr.MergedAt.HasValue && pr.MergedAt.Value < _sessionStartUtc)
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

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
        // Create a CLI session for this test PR (or resume existing)
        ActivateTestPrSession(pr.Number);

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
        await RememberAsync(MemoryType.Action,
            $"Created test PR #{testPrNumber} with {testFiles.Count} test files for merged PR #{pr.Number}: {pr.Title}",
            ct: ct);

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

        // Sync branch with main before marking ready — ensures PR is merge-clean
        await SyncBranchWithMainAsync(testPrNumber, ct);

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
    /// Gathers full business context (linked issue, PMSpec, Architecture) so tests validate
    /// acceptance criteria and business goals — not just structural code coverage.
    /// </summary>
    private async Task<string> GenerateTestCodeAsync(
        AgentPullRequest pr, Dictionary<string, string> sourceFiles, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var techStack = _config.Project.TechStack;

        // Gather business context: linked issue, PMSpec, Architecture
        var businessContext = await GatherBusinessContextAsync(pr, ct);

        var history = new ChatHistory();
        var memoryContext = await GetMemoryContextAsync(ct: ct);
        history.AddSystemMessage(
            $"You are an expert test engineer writing tests for a {techStack} project.\n\n" +
            "Your job is to generate REAL, RUNNABLE test code — not documentation or test plans.\n" +
            "Write actual test files that can be compiled and executed.\n\n" +
            "You will be given:\n" +
            "- The business requirements (user story and acceptance criteria) this code must satisfy\n" +
            "- The PM specification and architecture for broader project context\n" +
            "- The actual source code files to test\n\n" +
            "Guidelines:\n" +
            "- Write acceptance tests that verify the user story and acceptance criteria are met\n" +
            "- Write unit tests for individual functions, methods, and classes\n" +
            "- Write integration tests for component interactions where applicable\n" +
            "- Write UI/rendering tests for frontend components where applicable\n" +
            "- Use the standard testing framework for the tech stack (e.g., xUnit for C#, " +
            "Jest for TypeScript, pytest for Python, bUnit for Blazor components)\n" +
            "- Include proper imports, test class setup, and assertions\n" +
            "- Test happy paths, edge cases, and error conditions\n" +
            "- Use mocks/stubs for external dependencies\n" +
            "- Place test files in a `tests/` directory mirroring the source structure\n" +
            "- Prioritize testing business behavior over implementation details\n\n" +
            "Output each test file using this exact format:\n\n" +
            "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
            "Every file MUST use the FILE: marker format so it can be parsed and committed." +
            (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}"));

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
            businessContext +
            sourceContext.ToString() +
            $"\nGenerate comprehensive test files for the above source code using {techStack}. " +
            "Write tests that verify both the acceptance criteria from the user story AND " +
            "the technical implementation. Ensure the business goals are testable and tested. " +
            "Include edge cases and error handling.");

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content?.Trim() ?? "";
    }

    /// <summary>
    /// Gathers business context from the linked issue, PMSpec.md, and Architecture.md
    /// so the AI can write tests that validate acceptance criteria — not just code structure.
    /// </summary>
    private async Task<string> GatherBusinessContextAsync(AgentPullRequest pr, CancellationToken ct)
    {
        var context = new System.Text.StringBuilder();

        // 1. Parse linked issue from PR body ("Closes #NNN")
        var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
        if (issueNumber.HasValue)
        {
            try
            {
                var issue = await _github.GetIssueAsync(issueNumber.Value, ct);
                if (issue is not null)
                {
                    context.AppendLine("## Linked Issue (User Story & Acceptance Criteria)");
                    context.AppendLine($"**Issue #{issue.Number}:** {issue.Title}\n");
                    context.AppendLine(issue.Body);
                    context.AppendLine();
                    Logger.LogDebug("Loaded linked issue #{Number} for test context", issueNumber.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not fetch linked issue #{Number}", issueNumber.Value);
            }
        }

        // 2. Read PMSpec.md for business requirements
        try
        {
            var pmSpec = await _projectFiles.GetPMSpecAsync(ct);
            if (!string.IsNullOrWhiteSpace(pmSpec))
            {
                // Truncate to keep token budget reasonable
                var truncated = pmSpec.Length > 6000
                    ? pmSpec[..6000] + "\n\n<!-- truncated -->"
                    : pmSpec;
                context.AppendLine("## PM Specification (Business Requirements)");
                context.AppendLine(truncated);
                context.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read PMSpec.md for test context");
        }

        // 3. Read Architecture.md for technical patterns and constraints
        try
        {
            var archDoc = await _projectFiles.GetArchitectureDocAsync(ct);
            if (!string.IsNullOrWhiteSpace(archDoc))
            {
                var truncated = archDoc.Length > 4000
                    ? archDoc[..4000] + "\n\n<!-- truncated -->"
                    : archDoc;
                context.AppendLine("## Architecture Document (Technical Patterns)");
                context.AppendLine(truncated);
                context.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read Architecture.md for test context");
        }

        return context.ToString();
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

            // Resume the CLI session used to create these tests
            ActivateTestPrSession(rework.PrNumber);

            try
            {
                var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var techStack = _config.Project.TechStack;

                var history = new ChatHistory();
                history.AddSystemMessage(
                    $"You are an expert test engineer maintaining tests for a {techStack} project.\n" +
                    "A reviewer requested changes on your test PR. Update the test files to address all feedback.\n\n" +
                    "CRITICAL: Your response MUST start with a CHANGES SUMMARY that addresses EACH numbered " +
                    "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
                    "in one sentence what you changed or why no change was needed.\n\n" +
                    "After the CHANGES SUMMARY, output each corrected file using this exact format:\n" +
                    "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
                    "Include the COMPLETE content of each changed file.");

                history.AddUserMessage(
                    $"## Test PR #{rework.PrNumber}: {rework.PrTitle}\n\n" +
                    $"## Original PR Description\n{pr.Body}\n\n" +
                    $"## Review Feedback from {rework.Reviewer}\n{rework.Feedback}\n\n" +
                    "REQUIRED: Start your response with CHANGES SUMMARY that addresses each numbered " +
                    "feedback item using the SAME numbers. Example:\n" +
                    "CHANGES SUMMARY\n" +
                    "1. Added missing error handling test as requested\n" +
                    "2. Fixed assertion to check return type\n\n" +
                    "Then output the corrected test files.");

                var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                var updatedContent = response.Content?.Trim() ?? "";

                if (!string.IsNullOrWhiteSpace(updatedContent))
                {
                    var changesSummary = PullRequestWorkflow.ExtractChangesSummary(updatedContent);

                    var codeFiles = CodeFileParser.ParseFiles(updatedContent);
                    if (codeFiles.Count > 0)
                    {
                        await _prWorkflow.CommitCodeFilesToPRAsync(
                            pr.Number, codeFiles, "Address review feedback on tests", ct);
                    }

                    var commentBody = $"**[{Identity.DisplayName}] Rework** — Addressed feedback from {rework.Reviewer}.\n\n";
                    if (!string.IsNullOrWhiteSpace(changesSummary))
                        commentBody += changesSummary;
                    else
                    {
                        var filesSummary = codeFiles.Count > 0
                            ? string.Join(", ", codeFiles.Select(f => $"`{f.Path}`"))
                            : "test files";
                        commentBody += $"**Files updated:** {filesSummary}";
                    }
                    await _github.AddPullRequestCommentAsync(pr.Number, commentBody, ct);

                    // Sync branch with main before marking ready — ensures PR is merge-clean
                    await SyncBranchWithMainAsync(pr.Number, ct);

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
                    await RememberAsync(MemoryType.Action,
                        $"Addressed review feedback on test PR #{pr.Number} from {rework.Reviewer}",
                        TruncateForMemory(rework.Feedback), ct);
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

    private static string TruncateForMemory(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLength) return text;
        var cut = text[..maxLength];
        var lastPeriod = cut.LastIndexOf('.');
        return lastPeriod > maxLength / 2 ? cut[..(lastPeriod + 1)] : cut + "…";
    }

    /// <summary>
    /// Sync a PR branch with the latest main to avoid merge conflicts.
    /// Non-fatal: logs result but does not throw.
    /// </summary>
    private async Task SyncBranchWithMainAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var synced = await _github.UpdatePullRequestBranchAsync(prNumber, ct);
            if (synced)
                Logger.LogInformation("TestEngineer synced PR #{PrNumber} branch with main", prNumber);
            else
                Logger.LogWarning("TestEngineer PR #{PrNumber} branch sync failed — possible conflict", prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "TestEngineer failed to sync PR #{PrNumber} branch", prNumber);
        }
    }

    /// <summary>
    /// Gets or creates a CLI session for a specific test PR, providing conversational
    /// continuity when doing rework on tests.
    /// </summary>
    private void ActivateTestPrSession(int prNumber)
    {
        if (!_prSessionIds.TryGetValue(prNumber, out var sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
            _prSessionIds[prNumber] = sessionId;
        }
        SetCliSession(sessionId);
    }

    #endregion
}
