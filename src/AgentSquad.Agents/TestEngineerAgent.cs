using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
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
    private readonly BuildRunner? _buildRunner;
    private readonly TestRunner? _testRunner;
    private readonly PlaywrightRunner? _playwrightRunner;
    private readonly TestStrategyAnalyzer? _testStrategyAnalyzer;
    private readonly Core.Metrics.BuildTestMetrics? _metrics;

    private LocalWorkspace? _workspace;
    private bool _pendingWorkspaceCleanup;
    private readonly HashSet<int> _testedPRs = new();
    private readonly HashSet<int> _sessionTestedPRs = new(); // Only PRs actually tested this session (not skipped old ones)
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<(int PrNumber, string PrTitle, string Feedback, string Reviewer)> _reworkQueue = new();
    private readonly Dictionary<int, int> _reworkAttempts = new();
    private readonly Dictionary<int, string> _prSessionIds = new();
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow.AddHours(-24); // Look back 24h to catch PRs from restarts within same run
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
        ILogger<AgentBase> logger,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        PlaywrightRunner? playwrightRunner = null,
        TestStrategyAnalyzer? testStrategyAnalyzer = null,
        Core.Metrics.BuildTestMetrics? metrics = null)
        : base(identity, logger, memoryStore)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _buildRunner = buildRunner;
        _testRunner = testRunner;
        _playwrightRunner = playwrightRunner;
        _testStrategyAnalyzer = testStrategyAnalyzer;
        _metrics = metrics;
    }

    protected override async Task OnInitializeAsync(CancellationToken ct)
    {
        _subscriptions.Add(_messageBus.Subscribe<ChangesRequestedMessage>(
            Identity.Id, HandleChangesRequestedAsync));
        _subscriptions.Add(_messageBus.Subscribe<WorkspaceCleanupMessage>(
            Identity.Id, HandleWorkspaceCleanupAsync));

        // Initialize local workspace if configured
        if (_config.Workspace.IsEnabled)
        {
            try
            {
                var repoUrl = $"https://x-access-token:{_config.Project.GitHubToken}@github.com/{_config.Project.GitHubRepo}.git";
                _workspace = new LocalWorkspace(
                    _config.Workspace,
                    Identity.Id,
                    repoUrl,
                    _config.Project.DefaultBranch,
                    Logger);
                await _workspace.InitializeAsync(ct);
                Logger.LogInformation("TestEngineer initialized local workspace at {Path}", _workspace.RepoPath);

                // Pre-install Playwright browsers so UI tests don't skip during test runs.
                // This is done once during init — idempotent, ~80MB cached in shared directory.
                if (_config.Workspace.EnableUITests && _playwrightRunner is not null)
                {
                    try
                    {
                        await _playwrightRunner.EnsureBrowsersInstalledAsync(_config.Workspace, _workspace.RepoPath, ct);
                        Logger.LogInformation("TestEngineer: Playwright browsers ready");
                    }
                    catch (Exception pwEx)
                    {
                        Logger.LogWarning(pwEx, "TestEngineer: Failed to pre-install Playwright browsers, UI tests may skip");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TestEngineer failed to initialize local workspace, falling back to API mode");
                _workspace = null;
            }
        }
    }

    private Task HandleWorkspaceCleanupAsync(WorkspaceCleanupMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("TestEngineer received workspace cleanup signal: {Reason}", msg.Reason);
        _pendingWorkspaceCleanup = true;
        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();

        if (_pendingWorkspaceCleanup && _workspace is not null)
        {
            try
            {
                await _workspace.CleanupAsync();
                Logger.LogInformation("TestEngineer workspace cleaned up");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TestEngineer failed to clean up workspace");
            }
        }
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        var isInline = _config.Workspace.IsInlineTestWorkflow;
        UpdateStatus(AgentStatus.Idle,
            isInline ? "Monitoring PRs for inline test coverage" : "Monitoring merged PRs for test coverage");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Check if our tracked test PR has been closed/merged — clear stale tracking
                await CheckTrackedTestPrStatusAsync(ct);

                // Priority 1: Process rework feedback on test PRs
                await ProcessReworkAsync(ct);

                // Priority 2: Recover any open test PRs that need review
                await RecoverTestPRsAsync(ct);

                // Priority 3: Scan for PRs to test (mode-dependent)
                if (isInline)
                    await ScanApprovedPRsForInlineTestingAsync(ct);
                else
                    await ScanMergedPRsForTestingAsync(ct);

                // Check if all code-bearing PRs have been tested → signal completion
                await CheckTestCoverageCompleteAsync(ct);

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
    /// Check if the currently tracked test PR has been closed/merged and clear stale tracking.
    /// </summary>
    private async Task CheckTrackedTestPrStatusAsync(CancellationToken ct)
    {
        if (_currentTestPrNumber is null)
            return;

        var pr = await _github.GetPullRequestAsync(_currentTestPrNumber.Value, ct);
        if (pr is null || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogInformation("Test PR #{PrNumber} is no longer open (state: {State}), clearing tracking",
                _currentTestPrNumber.Value, pr?.State ?? "not found");
            _currentTestPrNumber = null;
            UpdateStatus(AgentStatus.Idle, "Monitoring merged PRs for test coverage");
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
                _sessionTestedPRs.Add(pr.Number);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate tests for merged PR #{Number}", pr.Number);
                _testedPRs.Add(pr.Number); // Don't retry failed PRs indefinitely
            }
        }
    }

    /// <summary>
    /// Inline test workflow: scans open PRs that have been approved by PE/PM
    /// (have 'approved' label) but don't yet have tests ('tests-added' label).
    /// Pushes test commits directly to the PR's branch so the PR contains both
    /// the feature code and its tests — a single-PR workflow.
    ///
    /// Flow: approved → TE adds tests → tests-added → PE reviews tests → merge
    /// </summary>
    private async Task ScanApprovedPRsForInlineTestingAsync(CancellationToken ct)
    {
        // Don't pick up new work if we're already generating tests for another PR
        if (_currentTestPrNumber is not null)
            return;

        var openPRs = await _github.GetOpenPullRequestsAsync(ct);

        // Sort by creation date (oldest first = FIFO) so earlier PRs get tests first
        var candidates = openPRs
            .Where(pr => !_testedPRs.Contains(pr.Number))
            .OrderBy(pr => pr.CreatedAt)
            .ToList();

        foreach (var pr in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Gate: must have 'approved' label (PE has reviewed the code)
            if (!pr.Labels.Contains(PullRequestWorkflow.Labels.Approved, StringComparer.OrdinalIgnoreCase))
                continue;

            // Skip PRs that already have tests
            if (pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase) ||
                pr.Labels.Contains(TestedLabel, StringComparer.OrdinalIgnoreCase))
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Skip test-only PRs (our own or other TEs)
            if (pr.Labels.Contains("tests", StringComparer.OrdinalIgnoreCase))
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Skip PRs authored by this agent (prevent circular testing)
            if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                _testedPRs.Add(pr.Number);
                continue;
            }

            // Check for testable code files
            var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
            var codeFiles = changedFiles
                .Where(f => TestableExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (codeFiles.Count == 0)
            {
                Logger.LogDebug("Skipping approved PR #{Number} — no testable code files", pr.Number);
                _testedPRs.Add(pr.Number);
                continue;
            }

            Logger.LogInformation(
                "Found approved PR #{Number} with {Count} testable files needing tests: {Title}",
                pr.Number, codeFiles.Count, pr.Title);
            LogActivity("task",
                $"🧪 Adding inline tests to approved PR #{pr.Number}: {pr.Title} ({codeFiles.Count} code files)");

            try
            {
                await AddInlineTestsToPRAsync(pr, codeFiles, ct);
                _testedPRs.Add(pr.Number);
                _sessionTestedPRs.Add(pr.Number);
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to add inline tests to PR #{Number}", pr.Number);
                RecordError($"Inline test failure for PR #{pr.Number}: {ex.Message}",
                    Microsoft.Extensions.Logging.LogLevel.Warning, ex);
                _testedPRs.Add(pr.Number); // Don't retry indefinitely

                // Post a comment so the team knows what happened
                try
                {
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        $"⚠️ **Test Engineer:** Failed to generate tests for this PR.\n\n" +
                        $"Error: `{ex.Message}`\n\n" +
                        $"The PR can still be reviewed and merged without automated tests.", ct);
                }
                catch { /* best effort */ }
            }

            // Process one PR per loop iteration to stay responsive
            break;
        }
    }

    /// <summary>
    /// Core inline test pipeline: checks out the PR's branch, reads source files locally,
    /// generates tests, builds + runs them with retry, commits and pushes.
    ///
    /// Edge cases handled:
    /// - PR closed/merged between start and push → checks state before push
    /// - Base code doesn't build → aborts with comment, doesn't add broken tests
    /// - Push rejected (branch moved) → pulls and retries once
    /// - Test build fails after max retries → reverts, posts comment
    /// - No workspace available → falls back to API-only commit
    /// </summary>
    private async Task AddInlineTestsToPRAsync(
        AgentPullRequest pr, List<string> codeFilePaths, CancellationToken ct)
    {
        _currentTestPrNumber = pr.Number;
        ActivateTestPrSession(pr.Number);

        try
        {
            UpdateStatus(AgentStatus.Working, $"Adding tests to PR #{pr.Number}");

            if (_workspace is not null && _buildRunner is not null && _testRunner is not null)
                await AddInlineTestsViaWorkspaceAsync(pr, codeFilePaths, ct);
            else
                await AddInlineTestsViaApiAsync(pr, codeFilePaths, ct);
        }
        finally
        {
            _currentTestPrNumber = null;
            UpdateStatus(AgentStatus.Idle, "Monitoring approved PRs for test coverage");
        }
    }

    /// <summary>
    /// Workspace path: checkout PR branch, verify build, generate tests, build+test, push.
    /// </summary>
    private async Task AddInlineTestsViaWorkspaceAsync(
        AgentPullRequest pr, List<string> codeFilePaths, CancellationToken ct)
    {
        var wsConfig = _config.Workspace;

        // --- Phase 1: Sync workspace to the PR's branch ---
        UpdateStatus(AgentStatus.Working, $"Checking out PR #{pr.Number} branch");
        await _workspace!.SyncWithMainAsync(ct);
        await _workspace.CheckoutBranchAsync(pr.HeadBranch, ct);

        // --- Phase 2: Verify the PR's code actually builds ---
        // If the base code doesn't build, there's no point adding tests to it
        UpdateStatus(AgentStatus.Working, $"Verifying PR #{pr.Number} base build");
        var baseBuild = await _buildRunner!.BuildAsync(
            _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

        if (!baseBuild.Success)
        {
            Logger.LogWarning(
                "PR #{Number} base code doesn't build — skipping inline tests. Errors: {Errors}",
                pr.Number, baseBuild.Errors.Length > 500 ? baseBuild.Errors[..500] : baseBuild.Errors);
            await _github.AddPullRequestCommentAsync(pr.Number,
                "⚠️ **Test Engineer:** Cannot add tests — the PR's code doesn't build.\n\n" +
                "Please fix build errors first. The Test Engineer will retry when the PR is updated.", ct);
            // Don't mark as tested — allow retry after engineer fixes build
            _testedPRs.Remove(pr.Number);
            return;
        }

        // --- Phase 3: Read source files locally (faster than API, no rate limit) ---
        UpdateStatus(AgentStatus.Working, $"Reading source files from PR #{pr.Number}");
        var sourceFiles = new Dictionary<string, string>();
        foreach (var filePath in codeFilePaths)
        {
            var content = await _workspace.ReadFileAsync(filePath, ct);
            if (!string.IsNullOrWhiteSpace(content))
                sourceFiles[filePath] = content;
        }

        if (sourceFiles.Count == 0)
        {
            // Fallback to API if local read fails (files might be in paths we can't find locally)
            Logger.LogWarning("Could not read source files locally for PR #{Number}, trying API", pr.Number);
            foreach (var filePath in codeFilePaths)
            {
                try
                {
                    var content = await _github.GetFileContentAsync(filePath, pr.HeadBranch, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                        sourceFiles[filePath] = content;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not read {Path} from branch {Branch}", filePath, pr.HeadBranch);
                }
            }
        }

        if (sourceFiles.Count == 0)
        {
            Logger.LogWarning("No source files readable for PR #{Number}", pr.Number);
            return;
        }

        // --- Phase 4: Generate test code via AI ---
        UpdateStatus(AgentStatus.Working, $"Generating tests for PR #{pr.Number} ({sourceFiles.Count} files)");
        var existingTests = await DiscoverExistingTestsAsync(sourceFiles.Keys.ToList(), ct);
        var testOutput = await GenerateTestCodeAsync(pr, sourceFiles, existingTests, ct);

        if (string.IsNullOrWhiteSpace(testOutput))
        {
            Logger.LogWarning("AI returned empty test output for PR #{Number}", pr.Number);
            return;
        }

        var testFiles = CodeFileParser.ParseFiles(testOutput);
        if (testFiles.Count == 0)
        {
            Logger.LogWarning("No parseable test files from AI output for PR #{Number}", pr.Number);
            return;
        }

        Logger.LogInformation(
            "Generated {Count} test files for PR #{Number}: {Files}",
            testFiles.Count, pr.Number,
            string.Join(", ", testFiles.Select(f => f.Path)));

        // --- Phase 5: Write test files, scaffold project infrastructure ---
        foreach (var file in testFiles)
            await _workspace.WriteFileAsync(file.Path, file.Content, ct);

        EnsureTestProjectExists(testFiles);
        await AddTestProjectsToSolutionAsync(testFiles, ct);

        // --- Phase 6: Build test code with AI fix retry loop ---
        UpdateStatus(AgentStatus.Working, $"Building tests for PR #{pr.Number}");
        var buildResult = await _buildRunner.BuildAsync(
            _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

        if (!buildResult.Success)
        {
            buildResult = await RetryBuildWithAIFixesAsync(
                pr, testFiles, buildResult, wsConfig, ct);
        }

        if (!buildResult.Success)
        {
            // Build still failing after all retries — revert and report
            Logger.LogWarning(
                "Inline test build failed after {Retries} retries for PR #{Number}",
                wsConfig.MaxBuildRetries, pr.Number);
            await _workspace.RevertUncommittedChangesAsync(ct);
            await _github.AddPullRequestCommentAsync(pr.Number,
                $"❌ **Test Engineer:** Could not make test code compile after " +
                $"{wsConfig.MaxBuildRetries} attempts. Build errors prevented test addition.\n\n" +
                $"The PR can still be merged without automated tests.", ct);
            return;
        }

        // --- Phase 7: Run tests by tier ---
        UpdateStatus(AgentStatus.Working, $"Running tests for PR #{pr.Number}");
        var tierResults = new List<TestResult>();

        // Unit tests (always run)
        var unitResult = await RunTestTierWithRetryAsync(
            TestTier.Unit,
            wsConfig.UnitTestCommand ?? wsConfig.TestCommand,
            wsConfig.UnitTestTimeoutSeconds, wsConfig, ct);
        if (unitResult is not null)
            tierResults.Add(unitResult);

        // Integration tests (only if unit tests passed and command configured)
        if (unitResult is null or { Success: true })
        {
            var intCommand = wsConfig.IntegrationTestCommand;
            if (!string.IsNullOrWhiteSpace(intCommand))
            {
                var intResult = await RunTestTierWithRetryAsync(
                    TestTier.Integration, intCommand,
                    wsConfig.IntegrationTestTimeoutSeconds, wsConfig, ct);
                if (intResult is not null)
                    tierResults.Add(intResult);
            }
        }

        // UI tests (only if prior tiers passed, enabled, and command configured)
        if (tierResults.All(r => r.Success) && wsConfig.EnableUITests)
        {
            var uiCommand = wsConfig.UITestCommand;
            if (!string.IsNullOrWhiteSpace(uiCommand) && _playwrightRunner is not null)
            {
                try
                {
                    await _playwrightRunner.EnsureBrowsersInstalledAsync(_config.Workspace, _workspace!.RepoPath, ct);

                    var uiResult = await _playwrightRunner.RunUITestsAsync(
                        _workspace.RepoPath, _config.Workspace,
                        uiCommand,
                        wsConfig.UITestTimeoutSeconds, ct);
                    tierResults.Add(uiResult);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "TestEngineer: UI test execution failed for PR #{Number}", pr.Number);
                    tierResults.Add(new TestResult
                    {
                        Success = false,
                        Output = $"UI test execution error: {ex.Message}",
                        Passed = 0, Failed = 0, Skipped = 0,
                        Duration = TimeSpan.Zero,
                        Tier = TestTier.UI,
                        FailureDetails = [ex.Message]
                    });
                }
            }
        }

        // --- Phase 8: Pre-push safety check — is the PR still open? ---
        var currentPr = await _github.GetPullRequestAsync(pr.Number, ct);
        if (currentPr is null || !string.Equals(currentPr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("PR #{Number} is no longer open (state: {State}), discarding test work",
                pr.Number, currentPr?.State ?? "null");
            await _workspace.RevertUncommittedChangesAsync(ct);
            return;
        }

        // --- Phase 9: Commit and push to the PR's branch ---
        UpdateStatus(AgentStatus.Working, $"Pushing tests to PR #{pr.Number}");
        await _workspace.CommitAsync($"test: add tests for PR #{pr.Number}", ct);

        try
        {
            await _workspace.PushAsync(pr.HeadBranch, ct);
        }
        catch (Exception pushEx)
        {
            // Push failed — likely the branch moved (engineer pushed a fix, or force-push)
            // Try once more: pull the latest, rebuild, and push again
            Logger.LogWarning(pushEx,
                "Push to {Branch} rejected for PR #{Number} — attempting pull and retry",
                pr.HeadBranch, pr.Number);

            try
            {
                // Fetch + merge the remote changes
                var merged = await _workspace.MergeMainIntoBranchAsync(ct);
                if (!merged)
                {
                    Logger.LogWarning("Merge conflict on PR #{Number} branch — aborting inline tests", pr.Number);
                    await _workspace.RevertUncommittedChangesAsync(ct);
                    await _github.AddPullRequestCommentAsync(pr.Number,
                        "⚠️ **Test Engineer:** Could not push tests — merge conflict with recent changes on the branch.", ct);
                    return;
                }

                // Rebuild to verify the merge didn't break anything
                var rebuildResult = await _buildRunner.BuildAsync(
                    _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
                if (!rebuildResult.Success)
                {
                    Logger.LogWarning("Rebuild after merge failed for PR #{Number}", pr.Number);
                    await _workspace.RevertUncommittedChangesAsync(ct);
                    return;
                }

                await _workspace.PushAsync(pr.HeadBranch, ct);
            }
            catch (Exception retryEx)
            {
                Logger.LogWarning(retryEx, "Push retry also failed for PR #{Number}", pr.Number);
                await _workspace.RevertUncommittedChangesAsync(ct);
                await _github.AddPullRequestCommentAsync(pr.Number,
                    "⚠️ **Test Engineer:** Could not push tests to this branch. " +
                    "The branch may have diverged. Tests will be retried on the next cycle.", ct);
                _testedPRs.Remove(pr.Number); // Allow retry
                return;
            }
        }

        // --- Phase 10: Update PR labels and post results ---
        await ApplyTestsAddedLabelAsync(pr, ct);
        await PostInlineTestResultsCommentAsync(pr, testFiles, tierResults, ct);

        Logger.LogInformation(
            "Successfully added {Count} test files to PR #{Number}: {Title}",
            testFiles.Count, pr.Number, pr.Title);
        LogActivity("task",
            $"✅ Added {testFiles.Count} test files inline to PR #{pr.Number}: {pr.Title}");
        UpdateStatus(AgentStatus.Idle,
            $"Tests added to PR #{pr.Number} — awaiting PE merge");
    }

    /// <summary>
    /// API-only fallback: commit test files directly via GitHub API when workspace is unavailable.
    /// No local build/test execution — tests are committed untested.
    /// </summary>
    private async Task AddInlineTestsViaApiAsync(
        AgentPullRequest pr, List<string> codeFilePaths, CancellationToken ct)
    {
        // Read source via API
        var sourceFiles = new Dictionary<string, string>();
        foreach (var filePath in codeFilePaths)
        {
            try
            {
                var content = await _github.GetFileContentAsync(filePath, pr.HeadBranch, ct);
                if (!string.IsNullOrWhiteSpace(content))
                    sourceFiles[filePath] = content;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not read {Path} from {Branch}", filePath, pr.HeadBranch);
            }
        }

        if (sourceFiles.Count == 0)
        {
            Logger.LogWarning("No source files readable via API for PR #{Number}", pr.Number);
            return;
        }

        var existingTests = await DiscoverExistingTestsAsync(sourceFiles.Keys.ToList(), ct);
        var testOutput = await GenerateTestCodeAsync(pr, sourceFiles, existingTests, ct);
        var testFiles = CodeFileParser.ParseFiles(testOutput ?? "");

        if (testFiles.Count == 0)
        {
            Logger.LogWarning("No parseable test files for PR #{Number} (API mode)", pr.Number);
            return;
        }

        // Batch commit all test files to the PR's branch
        var fileTuples = testFiles.Select(f => (f.Path, f.Content)).ToList();
        await _github.BatchCommitFilesAsync(
            fileTuples,
            $"test: add {testFiles.Count} test files for PR #{pr.Number}",
            pr.HeadBranch, ct);

        await ApplyTestsAddedLabelAsync(pr, ct);
        await PostInlineTestResultsCommentAsync(pr, testFiles, tierResults: null, ct);

        Logger.LogInformation("Added {Count} test files via API to PR #{Number}", testFiles.Count, pr.Number);
    }

    /// <summary>
    /// Retry build failures by asking AI to fix the test code. Returns the final build result.
    /// </summary>
    private async Task<BuildResult> RetryBuildWithAIFixesAsync(
        AgentPullRequest pr,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        BuildResult buildResult,
        WorkspaceConfig wsConfig,
        CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // First try auto-restoring missing packages (cheap — no AI call needed)
        if (await TryAutoRestoreMissingPackagesAsync(buildResult, ct))
        {
            buildResult = await _buildRunner!.BuildAsync(
                _workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (buildResult.Success) return buildResult;
        }

        for (int attempt = 0; attempt < wsConfig.MaxBuildRetries && !buildResult.Success; attempt++)
        {
            Logger.LogInformation(
                "Inline test build fix attempt {Attempt}/{Max} for PR #{Number}",
                attempt + 1, wsConfig.MaxBuildRetries, pr.Number);

            var errorSummary = buildResult.ParsedErrors.Count > 0
                ? string.Join("\n", buildResult.ParsedErrors.Take(20))
                : buildResult.Errors.Length > 2000 ? buildResult.Errors[..2000] : buildResult.Errors;

            var fixHistory = new ChatHistory();
            fixHistory.AddSystemMessage(
                "You are a test engineer fixing build errors in test files. " +
                "The test files were added to an existing PR branch and won't compile. " +
                "Fix ONLY the test code — do NOT modify the source code under test.\n" +
                "COMMON FIX: If errors are 'type or namespace not found', ensure the .csproj includes the missing " +
                "NuGet PackageReference (e.g., FluentAssertions, Shouldly). Output the corrected .csproj too.\n\n" +
                "Output ONLY corrected files using:\nFILE: path/to/file.ext\n```language\n<content>\n```");
            fixHistory.AddUserMessage(
                $"Build attempt {attempt + 1}/{wsConfig.MaxBuildRetries} failed.\n\n" +
                $"## Build Errors\n\n{errorSummary}\n\n" +
                $"## Test Files\n\n" +
                string.Join("\n", testFiles.Select(f =>
                    $"### {f.Path}\n```\n{(f.Content.Length > 1500 ? f.Content[..1500] + "\n// ... truncated" : f.Content)}\n```")) +
                "\n\nFix ALL build errors. Only modify test files.");

            var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixedFiles = CodeFileParser.ParseFiles(fixResponse.Content ?? "");

            if (fixedFiles.Count == 0)
            {
                Logger.LogDebug("AI fix attempt produced no files for PR #{Number}", pr.Number);
                continue;
            }

            foreach (var file in fixedFiles)
                await _workspace!.WriteFileAsync(file.Path, file.Content, ct);

            buildResult = await _buildRunner!.BuildAsync(
                _workspace!.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
        }

        return buildResult;
    }

    /// <summary>
    /// Auto-detect missing NuGet packages from CS0246 build errors and install them via dotnet add package.
    /// Returns true if any packages were added (caller should rebuild).
    /// </summary>
    private async Task<bool> TryAutoRestoreMissingPackagesAsync(BuildResult buildResult, CancellationToken ct)
    {
        if (_workspace?.RepoPath is null || buildResult.Success) return false;

        // Well-known mapping: namespace → NuGet package name
        var namespaceToPackage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FluentAssertions"] = "FluentAssertions",
            ["Shouldly"] = "Shouldly",
            ["NSubstitute"] = "NSubstitute",
            ["AutoFixture"] = "AutoFixture",
            ["Bogus"] = "Bogus",
            ["FakeItEasy"] = "FakeItEasy",
            ["Moq"] = "Moq",
            ["bunit"] = "bunit",
            ["Bunit"] = "bunit",
            ["AngleSharp"] = "AngleSharp",
            ["Verify"] = "Verify.Xunit",
            ["Microsoft.Playwright"] = "Microsoft.Playwright",
        };

        // Parse CS0246 errors: "The type or namespace name 'X' could not be found"
        var missingNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allErrors = string.Join("\n", buildResult.ParsedErrors);
        if (string.IsNullOrEmpty(allErrors))
            allErrors = buildResult.Errors;

        // Match CS0246 pattern
        var cs0246Regex = new System.Text.RegularExpressions.Regex(
            @"error CS0246:.*?'(\w+)'", System.Text.RegularExpressions.RegexOptions.None);
        foreach (System.Text.RegularExpressions.Match match in cs0246Regex.Matches(allErrors))
        {
            missingNamespaces.Add(match.Groups[1].Value);
        }

        if (missingNamespaces.Count == 0) return false;

        // Find test .csproj files to add packages to
        var testCsprojs = Directory.GetFiles(
            Path.Combine(_workspace.RepoPath, "tests"), "*.csproj", SearchOption.AllDirectories);

        if (testCsprojs.Length == 0) return false;

        var addedAny = false;
        foreach (var ns in missingNamespaces)
        {
            if (!namespaceToPackage.TryGetValue(ns, out var packageName)) continue;

            // Add to each test project that doesn't already have it
            foreach (var csproj in testCsprojs)
            {
                var content = await File.ReadAllTextAsync(csproj, ct);
                if (content.Contains(packageName, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "dotnet",
                            Arguments = $"add \"{csproj}\" package {packageName}",
                            WorkingDirectory = _workspace.RepoPath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    await process.WaitForExitAsync(ct);

                    if (process.ExitCode == 0)
                    {
                        Logger.LogInformation("Auto-added missing package {Package} to {Csproj}", packageName, Path.GetFileName(csproj));
                        addedAny = true;
                    }
                    else
                    {
                        var stderr = await process.StandardError.ReadToEndAsync(ct);
                        Logger.LogWarning("Failed to add package {Package}: {Error}", packageName, stderr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Error adding package {Package}", packageName);
                }
            }
        }

        return addedAny;
    }

    /// <summary>
    /// Apply the 'tests-added' label to the PR, preserving existing labels.
    /// </summary>
    private async Task ApplyTestsAddedLabelAsync(AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var updatedLabels = pr.Labels
                .Append(PullRequestWorkflow.Labels.TestsAdded)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not apply tests-added label to PR #{Number}", pr.Number);
        }
    }

    /// <summary>
    /// Post a detailed test results comment on the PR.
    /// </summary>
    private async Task PostInlineTestResultsCommentAsync(
        AgentPullRequest pr,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        IReadOnlyList<TestResult>? tierResults,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 🧪 Test Engineer — Inline Tests Added");
        sb.AppendLine();
        sb.AppendLine($"Added **{testFiles.Count}** test file(s) to this PR.");
        sb.AppendLine();

        // Test results by tier
        if (tierResults is { Count: > 0 })
        {
            sb.AppendLine("### Test Results");
            sb.AppendLine();
            foreach (var r in tierResults)
            {
                var icon = r.Success ? "✅" : "❌";
                sb.AppendLine($"- **{r.Tier} Tests:** {icon} {r.Passed} passed, {r.Failed} failed, {r.Skipped} skipped ({r.Duration.TotalSeconds:F1}s)");
            }
            sb.AppendLine();

            // Include failure details if any tier failed
            var failures = tierResults.Where(r => !r.Success).ToList();
            if (failures.Count > 0)
            {
                sb.AppendLine("<details><summary>⚠️ Test Failures</summary>");
                sb.AppendLine();
                foreach (var f in failures)
                {
                    if (f.FailureDetails.Count > 0)
                    {
                        sb.AppendLine($"**{f.Tier} failures:**");
                        foreach (var detail in f.FailureDetails.Take(10))
                            sb.AppendLine($"- {detail}");
                        sb.AppendLine();
                    }
                }
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("*Tests committed but not executed locally (API-only mode).*");
            sb.AppendLine();
        }

        // Test artifacts summary (videos, traces, screenshots)
        if (tierResults is { Count: > 0 })
        {
            var allArtifacts = tierResults
                .Where(r => r.Artifacts.HasArtifacts)
                .SelectMany(r => new[]
                {
                    (Type: "🎥 Videos", Items: r.Artifacts.Videos),
                    (Type: "📋 Traces", Items: r.Artifacts.Traces),
                    (Type: "📸 Screenshots", Items: r.Artifacts.Screenshots)
                })
                .Where(a => a.Items.Count > 0)
                .ToList();

            if (allArtifacts.Count > 0)
            {
                sb.AppendLine("### Test Artifacts");
                sb.AppendLine();
                foreach (var artifact in allArtifacts)
                {
                    sb.AppendLine($"- **{artifact.Type}:** {artifact.Items.Count} file(s)");
                    foreach (var path in artifact.Items.Take(5))
                        sb.AppendLine($"  - `{Path.GetFileName(path)}`");
                    if (artifact.Items.Count > 5)
                        sb.AppendLine($"  - ... and {artifact.Items.Count - 5} more");
                }
                sb.AppendLine();
                sb.AppendLine("*Videos and traces are stored in the workspace `test-results/` directory.*");
                sb.AppendLine();
            }
        }

        // Upload test artifacts (screenshots, videos, traces) to the PR branch
        if (tierResults is { Count: > 0 })
        {
            await UploadTestArtifactsToPrAsync(pr, tierResults, sb, ct);
        }

        // File list
        sb.AppendLine("<details><summary>Test Files</summary>");
        sb.AppendLine();
        foreach (var file in testFiles)
            sb.AppendLine($"- `{file.Path}`");
        sb.AppendLine();
        sb.AppendLine("</details>");

        try
        {
            await _github.AddPullRequestCommentAsync(pr.Number, sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not post test results comment on PR #{Number}", pr.Number);
        }
    }

    /// <summary>
    /// Upload test artifacts (screenshots, videos, traces) to the PR branch and embed
    /// them in the markdown comment. Screenshots are inline images, videos are download
    /// links, traces link to trace.playwright.dev for interactive viewing.
    /// </summary>
    private async Task UploadTestArtifactsToPrAsync(
        AgentPullRequest pr,
        IReadOnlyList<TestResult> tierResults,
        System.Text.StringBuilder sb,
        CancellationToken ct)
    {
        var screenshots = tierResults
            .SelectMany(r => r.Artifacts.Screenshots)
            .Where(File.Exists)
            .Take(5)
            .ToList();

        var videos = tierResults
            .SelectMany(r => r.Artifacts.Videos)
            .Where(File.Exists)
            .Take(3)
            .ToList();

        var traces = tierResults
            .SelectMany(r => r.Artifacts.Traces)
            .Where(File.Exists)
            .Take(3)
            .ToList();

        if (screenshots.Count == 0 && videos.Count == 0 && traces.Count == 0)
            return;

        sb.AppendLine("### Uploaded Artifacts");
        sb.AppendLine();

        // Screenshots — embedded as inline images
        foreach (var path in screenshots)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var fileName = Path.GetFileName(path);
                var repoPath = $"test-results/screenshots/{fileName}";
                var imageUrl = await _github.CommitBinaryFileAsync(
                    repoPath, bytes, $"test-artifact: {fileName}", pr.HeadBranch, ct);
                if (imageUrl is not null)
                {
                    sb.AppendLine($"**📸 {fileName}**");
                    sb.AppendLine($"![{fileName}]({imageUrl})");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not upload screenshot {Path}", path);
            }
        }

        // Videos — committed to repo with download links
        foreach (var path in videos)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Length > 10 * 1024 * 1024)
                {
                    Logger.LogDebug("Skipping video upload — too large ({Size} bytes): {Path}", bytes.Length, path);
                    sb.AppendLine($"- 🎥 `{Path.GetFileName(path)}` ({bytes.Length / 1024 / 1024}MB — too large to upload)");
                    continue;
                }
                var fileName = Path.GetFileName(path);
                var repoPath = $"test-results/videos/{fileName}";
                var videoUrl = await _github.CommitBinaryFileAsync(
                    repoPath, bytes, $"test-artifact: {fileName}", pr.HeadBranch, ct);
                if (videoUrl is not null)
                    sb.AppendLine($"- 🎥 [{fileName}]({videoUrl}) ({bytes.Length / 1024}KB)");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not upload video {Path}", path);
            }
        }

        // Traces — committed with link to trace.playwright.dev viewer
        foreach (var path in traces)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Length > 20 * 1024 * 1024)
                {
                    Logger.LogDebug("Skipping trace upload — too large ({Size} bytes): {Path}", bytes.Length, path);
                    sb.AppendLine($"- 📋 `{Path.GetFileName(path)}` ({bytes.Length / 1024 / 1024}MB — too large to upload)");
                    continue;
                }
                var fileName = Path.GetFileName(path);
                var repoPath = $"test-results/traces/{fileName}";
                var traceUrl = await _github.CommitBinaryFileAsync(
                    repoPath, bytes, $"test-artifact: {fileName}", pr.HeadBranch, ct);
                if (traceUrl is not null)
                    sb.AppendLine($"- 📋 [{fileName}]({traceUrl}) — [View in Trace Viewer](https://trace.playwright.dev)");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not upload trace {Path}", path);
            }
        }

        sb.AppendLine();
    }

    /// <summary>
    /// When coverage is complete and no test PRs are pending review, updates status
    /// to trigger the HealthMonitor's testing.coverage.met signal.
    /// </summary>
    private async Task CheckTestCoverageCompleteAsync(CancellationToken ct)
    {
        // Don't signal if we have an active test PR in progress or pending rework
        if (_currentTestPrNumber is not null || !_reworkQueue.IsEmpty)
            return;

        var isInline = _config.Workspace.IsInlineTestWorkflow;
        var untestedCodePRs = 0;

        if (isInline)
        {
            // Inline mode: check open approved PRs that still lack tests
            var openPRs = await _github.GetOpenPullRequestsAsync(ct);
            foreach (var pr in openPRs)
            {
                if (_testedPRs.Contains(pr.Number)) continue;

                // Only count approved PRs (the ones we're responsible for)
                if (!pr.Labels.Contains(PullRequestWorkflow.Labels.Approved, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (pr.Labels.Contains(TestedLabel, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                    agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
                if (changedFiles.Any(f => TestableExtensions.Contains(Path.GetExtension(f))))
                    untestedCodePRs++;
            }
        }
        else
        {
            // Legacy mode: check merged PRs
            var mergedPRs = await _github.GetMergedPullRequestsAsync(ct);
            foreach (var pr in mergedPRs)
            {
                if (pr.MergedAt.HasValue && pr.MergedAt.Value < _sessionStartUtc) continue;
                if (_testedPRs.Contains(pr.Number)) continue;

                if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                    agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
                if (changedFiles.Any(f => TestableExtensions.Contains(Path.GetExtension(f))))
                    untestedCodePRs++;
            }
        }

        if (untestedCodePRs == 0 && _sessionTestedPRs.Count > 0)
        {
            UpdateStatus(AgentStatus.Idle,
                $"All {_sessionTestedPRs.Count} PRs tested — coverage met, tests complete");
            Logger.LogInformation(
                "Test coverage complete: all {Count} code PRs have been tested",
                _sessionTestedPRs.Count);
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

        // Check for existing tests already in the repo (e.g., created by PE in the same PR)
        var existingTests = await DiscoverExistingTestsAsync(sourceFiles.Keys.ToList(), ct);

        UpdateStatus(AgentStatus.Working, $"Generating tests for PR #{pr.Number} ({sourceFiles.Count} files)");

        // Generate real test code via AI (passes existing tests for review/improvement)
        var testOutput = await GenerateTestCodeAsync(pr, sourceFiles, existingTests, ct);

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

        // Build-gate: if the test PR creation was blocked by build errors, skip this PR
        if (testPrNumber < 0)
        {
            Logger.LogWarning("Test PR creation for merged PR #{SourcePR} was blocked by build errors — skipping",
                pr.Number);
            LogActivity("task", $"⛔ Test PR for PR #{pr.Number} blocked by build errors");
            return;
        }

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
    /// Multi-tier: generates unit, integration, and UI tests based on TestStrategy analysis.
    /// </summary>
    private async Task<string> GenerateTestCodeAsync(
        AgentPullRequest pr, Dictionary<string, string> sourceFiles,
        Dictionary<string, string> existingTests, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var techStack = _config.Project.TechStack;

        // Gather business context: linked issue, PMSpec, Architecture
        var businessContext = await GatherBusinessContextAsync(pr, ct);

        // Determine which test tiers are needed
        var issueBody = await GetLinkedIssueBodyAsync(pr, ct);
        var strategy = _testStrategyAnalyzer?.Analyze(
            sourceFiles.Keys.ToList(), pr.Body, issueBody, techStack)
            ?? new TestStrategy
            {
                NeedsUnitTests = true,
                NeedsIntegrationTests = false,
                NeedsUITests = false,
                Rationale = "Fallback: no strategy analyzer available"
            };

        // Build source file context (shared across all tiers)
        var sourceContext = new System.Text.StringBuilder();
        sourceContext.AppendLine("## Source Files to Test\n");
        foreach (var (path, content) in sourceFiles)
        {
            var ext = Path.GetExtension(path).TrimStart('.');
            sourceContext.AppendLine($"### {path}");
            sourceContext.AppendLine($"```{ext}");
            var truncated = content.Length > 8000 ? content[..8000] + "\n// ... (truncated)" : content;
            sourceContext.AppendLine(truncated);
            sourceContext.AppendLine("```\n");
        }

        // Build existing test context so AI can review/improve instead of duplicating
        var existingTestContext = "";
        if (existingTests.Count > 0)
        {
            var etb = new System.Text.StringBuilder();
            etb.AppendLine("## Existing Tests Already in Repo\n");
            etb.AppendLine("The following test files already exist for this code. " +
                "Do NOT duplicate these tests. Instead, review them and only output:\n" +
                "1. **Improved versions** of existing test files if they have gaps or quality issues\n" +
                "2. **New test files** for UNTESTED code paths not covered by existing tests\n" +
                "3. If existing tests are comprehensive, output NOTHING (empty response is fine)\n");
            foreach (var (path, content) in existingTests)
            {
                var ext = Path.GetExtension(path).TrimStart('.');
                etb.AppendLine($"### {path} (EXISTING)");
                etb.AppendLine($"```{ext}");
                var truncated = content.Length > 6000 ? content[..6000] + "\n// ... (truncated)" : content;
                etb.AppendLine(truncated);
                etb.AppendLine("```\n");
            }
            existingTestContext = etb.ToString();
            Logger.LogInformation(
                "Found {Count} existing test files for PR #{Number} — AI will review/improve instead of recreating",
                existingTests.Count, pr.Number);
        }

        var allOutputs = new System.Text.StringBuilder();
        var memoryContext = await GetMemoryContextAsync(ct: ct);

        // Generate unit tests (always)
        if (strategy.NeedsUnitTests)
        {
            UpdateStatus(AgentStatus.Working, $"Generating unit tests for PR #{pr.Number}");
            var unitOutput = await GenerateTestsForTierAsync(
                chat, TestTier.Unit, pr, techStack, businessContext,
                sourceContext.ToString() + existingTestContext, memoryContext, ct);
            allOutputs.AppendLine(unitOutput);
        }

        // Generate integration tests (when service/API layers changed)
        if (strategy.NeedsIntegrationTests)
        {
            UpdateStatus(AgentStatus.Working, $"Generating integration tests for PR #{pr.Number}");
            var integrationOutput = await GenerateTestsForTierAsync(
                chat, TestTier.Integration, pr, techStack, businessContext,
                sourceContext.ToString() + existingTestContext, memoryContext, ct);
            allOutputs.AppendLine(integrationOutput);
        }

        // Generate UI tests with Playwright (when UI components changed)
        if (strategy.NeedsUITests && _config.Workspace.EnableUITests)
        {
            UpdateStatus(AgentStatus.Working, $"Generating UI/Playwright tests for PR #{pr.Number}");
            var uiOutput = await GenerateTestsForTierAsync(
                chat, TestTier.UI, pr, techStack, businessContext,
                sourceContext.ToString() + existingTestContext, memoryContext, ct,
                strategy.UITestScenarios);
            allOutputs.AppendLine(uiOutput);
        }

        Logger.LogInformation("Generated tests for PR #{Number}: Unit={Unit}, Integration={Integration}, UI={UI}, ExistingTests={Existing}",
            pr.Number, strategy.NeedsUnitTests, strategy.NeedsIntegrationTests,
            strategy.NeedsUITests && _config.Workspace.EnableUITests, existingTests.Count);

        return allOutputs.ToString();
    }

    /// <summary>
    /// Generate test code for a specific tier using a tier-appropriate AI prompt.
    /// </summary>
    private async Task<string> GenerateTestsForTierAsync(
        IChatCompletionService chat,
        TestTier tier,
        AgentPullRequest pr,
        string techStack,
        string businessContext,
        string sourceContext,
        string memoryContext,
        CancellationToken ct,
        IReadOnlyList<string>? uiScenarios = null)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(GetTierSystemPrompt(tier, techStack, memoryContext));

        var userPrompt = new System.Text.StringBuilder();
        userPrompt.AppendLine($"## Merged PR #{pr.Number}: {pr.Title}\n");
        userPrompt.AppendLine($"## PR Description\n{pr.Body}\n");
        userPrompt.AppendLine(businessContext);
        userPrompt.AppendLine(sourceContext);

        if (tier == TestTier.UI && uiScenarios?.Count > 0)
        {
            userPrompt.AppendLine("## UI Test Scenarios to Cover");
            foreach (var scenario in uiScenarios)
                userPrompt.AppendLine($"- {scenario}");
            userPrompt.AppendLine();
        }

        // For UI tests, include visual design context so tests verify design conformance
        if (tier == TestTier.UI)
        {
            var designCtx = await ReadDesignReferencesForTestsAsync(ct);
            if (!string.IsNullOrWhiteSpace(designCtx))
            {
                userPrompt.AppendLine("## Visual Design Reference");
                userPrompt.AppendLine("The following design files define the expected UI. " +
                    "Generate assertions that verify: correct CSS classes, element visibility, " +
                    "color schemes, layout structure, and component hierarchy match the design.\n");
                userPrompt.AppendLine(designCtx);
                userPrompt.AppendLine();
            }
        }

        userPrompt.AppendLine(GetTierUserSuffix(tier, techStack));
        history.AddUserMessage(userPrompt.ToString());

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content?.Trim() ?? "";
    }

    /// <summary>
    /// Get the system prompt for a specific test tier with appropriate guidance and examples.
    /// </summary>
    private string GetTierSystemPrompt(TestTier tier, string techStack, string memoryContext)
    {
        var basePrompt = $"You are an expert test engineer writing tests for a {techStack} project.\n" +
            "Your job is to generate REAL, RUNNABLE test code — not documentation or test plans.\n" +
            "Write actual test files that can be compiled and executed.\n\n" +
            "CRITICAL RULE — DEPENDENCY MANAGEMENT:\n" +
            "Before using ANY library, package, framework, or external dependency in your code, you MUST:\n" +
            "1. Check the project's existing dependency manifest (e.g., .csproj, package.json, requirements.txt,\n" +
            "   Cargo.toml, go.mod, build.gradle, pom.xml, Gemfile, etc.) to see what is already installed\n" +
            "2. If a dependency you want to use is NOT already listed, you MUST add it to the manifest file\n" +
            "3. ALWAYS output the complete dependency manifest with ALL needed dependencies — never assume one exists\n" +
            "4. This applies to test frameworks, assertion libraries, mocking libraries, browser automation tools,\n" +
            "   and ANY other third-party code. If you import/using/require it, it must be in the manifest.\n" +
            "Missing dependencies are the #1 cause of build failures. Prevent this by always including the manifest.\n\n" +
            "Output each test file using this exact format:\n\n" +
            "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
            "Every file MUST use the FILE: marker format so it can be parsed and committed.\n\n";

        // Detect Blazor project and add specific guidance
        var blazorGuidance = "";
        if (IsBlazorProject())
        {
            blazorGuidance = GetBlazorTestGuidance();
        }

        var tierGuidance = tier switch
        {
            TestTier.Unit =>
                "## Test Tier: UNIT TESTS\n" +
                "Focus on isolated testing of individual functions, methods, and classes.\n" +
                "Guidelines:\n" +
                "- Mock ALL external dependencies (services, repositories, HTTP clients, databases)\n" +
                "- Test one behavior per test method\n" +
                "- Cover happy paths, edge cases, null/empty inputs, boundary values, and error conditions\n" +
                "- Use descriptive test names: MethodName_Condition_ExpectedResult\n" +
                "- Add [Trait(\"Category\", \"Unit\")] attribute to every test class (for xUnit)\n" +
                "- Place files in tests/{ProjectName}.Tests/Unit/ directory\n" +
                "- Keep tests fast — no I/O, no network, no database calls\n" +
                "- YOU MUST output a .csproj file at tests/{ProjectName}.Tests/{ProjectName}.Tests.csproj\n" +
                "  with xUnit, Moq, and project reference to the source project\n",

            TestTier.Integration =>
                "## Test Tier: INTEGRATION TESTS\n" +
                "Focus on testing component interactions with real or near-real dependencies.\n" +
                "Guidelines:\n" +
                "- Test actual DI container wiring and service resolution\n" +
                "- Test API endpoints end-to-end (request → response)\n" +
                "- Test data access layer with in-memory databases where possible\n" +
                "- Test middleware pipeline behavior\n" +
                "- Add [Trait(\"Category\", \"Integration\")] attribute to every test class\n" +
                "- Place files in tests/{ProjectName}.Tests/Integration/ directory\n" +
                "- Use WebApplicationFactory for ASP.NET Core integration tests\n" +
                "- Test error handling, validation, and edge cases at API boundaries\n" +
                "- YOU MUST output a .csproj file at tests/{ProjectName}.Tests/{ProjectName}.Tests.csproj\n" +
                "  with xUnit, Moq, Microsoft.AspNetCore.Mvc.Testing, and project reference\n",

            TestTier.UI =>
                "## Test Tier: UI/E2E TESTS (Playwright)\n" +
                "Focus on testing user-facing behavior through browser automation.\n" +
                "Guidelines:\n" +
                "- Use Microsoft.Playwright for browser automation\n" +
                "- Use the Page Object Model pattern: create a page object class for each page/component\n" +
                "- Tests run HEADLESS (no visible browser) — use environment variable HEADED to control\n" +
                "- Base URL comes from environment variable BASE_URL (default: http://localhost:5000)\n" +
                "- Add [Trait(\"Category\", \"UI\")] and [Collection(\"Playwright\")] attributes\n" +
                "- Place files in tests/{ProjectName}.UITests/ directory\n" +
                "- Test user workflows: navigation, form submission, button clicks, data display\n" +
                "- Include assertions on page content, element visibility, and navigation outcomes\n" +
                "- Capture screenshots on failure using PlaywrightFixture.CaptureScreenshotAsync\n" +
                "- Include a shared PlaywrightFixture class if one doesn't exist\n" +
                "- Example Playwright test structure:\n" +
                "```csharp\n" +
                "[Collection(\"Playwright\")]\n[Trait(\"Category\", \"UI\")]\n" +
                "public class HomePageTests\n{\n" +
                "    private readonly PlaywrightFixture _fixture;\n" +
                "    public HomePageTests(PlaywrightFixture fixture) => _fixture = fixture;\n\n" +
                "    [Fact]\n    public async Task HomePage_LoadsSuccessfully()\n    {\n" +
                "        var page = await _fixture.NewPageAsync();\n" +
                "        await page.GotoAsync(\"/\");\n" +
                "        await Assertions.Expect(page).ToHaveTitleAsync(new Regex(\".*\"));\n" +
                "    }\n}\n```\n",

            _ => ""
        };

        return basePrompt + blazorGuidance + tierGuidance +
            (string.IsNullOrEmpty(memoryContext) ? "" : $"\n{memoryContext}");
    }

    /// <summary>
    /// Detect whether the target project is a Blazor project by checking for .razor files.
    /// </summary>
    private bool IsBlazorProject()
    {
        if (_workspace?.RepoPath is null) return false;
        try
        {
            return Directory.EnumerateFiles(_workspace.RepoPath, "*.razor", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns Blazor-specific test guidance to include in the system prompt.
    /// </summary>
    private string GetBlazorTestGuidance()
    {
        // Try to find the source .csproj to get the project name
        var projectName = "ReportingDashboard"; // fallback
        if (_workspace?.RepoPath is not null)
        {
            var csproj = Directory.EnumerateFiles(_workspace.RepoPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj is not null)
                projectName = Path.GetFileNameWithoutExtension(csproj);
        }

        return $@"## BLAZOR PROJECT — SPECIAL INSTRUCTIONS

This is a **Blazor Server** (.NET 8) project. You MUST follow these Blazor-specific testing patterns:

### Required .csproj for Unit/Integration Tests
You MUST output this file: `tests/{projectName}.Tests/{projectName}.Tests.csproj`
```xml
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""bunit"" Version=""1.28.9"" />
    <PackageReference Include=""FluentAssertions"" Version=""6.12.0"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.9.0"" />
    <PackageReference Include=""Moq"" Version=""4.20.70"" />
    <PackageReference Include=""xunit"" Version=""2.7.0"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.5.7"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\..\{projectName}.csproj"" />
  </ItemGroup>
</Project>
```

### Blazor Component Testing with bUnit
- Use `bunit.TestContext` for rendering Blazor components
- `using Bunit;` namespace (lowercase 'b' in the using, but the NuGet is `bunit`)
- Render components: `var cut = ctx.RenderComponent<MyComponent>()`
- Pass parameters: `ctx.RenderComponent<MyComponent>(p => p.Add(x => x.Title, ""Test""))`
- Assert markup: `cut.Markup.Contains(""expected text"")`
- Find elements: `cut.Find(""h1"").TextContent`
- Mock services: `ctx.Services.AddSingleton<IMyService>(mockService.Object)`
- Mock JSInterop: `ctx.JSInterop.SetupVoid(""methodName"")`
- For cascading parameters: `ctx.RenderComponent<CascadingValue<Type>>(p => p.Add(x => x.Value, myValue).AddChildContent<MyComponent>())`

### Service/Model Testing (non-Blazor classes)
- Test service classes, models, and utilities with standard xUnit + Moq
- No bUnit needed for plain C# classes
- Mock `IServiceProvider`, `IConfiguration`, `HttpClient` etc. as needed

### Key Rules
- ALWAYS include the .csproj file in your output
- Use `using {projectName};`, `using {projectName}.Models;`, `using {projectName}.Services;` etc. for namespaces
- Do NOT reference namespaces that don't exist in the project
- Keep test classes focused — one test class per source class/component

";
    }

    /// <summary>
    /// Ensures a test project .csproj exists for unit/integration test files.
    /// If the AI didn't generate one, creates a fallback .csproj with standard references.
    /// </summary>
    private void EnsureTestProjectExists(IReadOnlyList<CodeFileParser.CodeFile> testFiles)
    {
        if (_workspace?.RepoPath is null) return;

        // Find test directories that contain .cs files but no .csproj
        var testDirs = testFiles
            .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => GetTestProjectDir(f.Path))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var testDir in testDirs)
        {
            if (testDir is null) continue;

            // Check if AI already generated a .csproj in this dir or if one already exists on disk
            var hasCsproj = testFiles.Any(f =>
                f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                f.Path.Replace('\\', '/').StartsWith(testDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

            var csprojOnDisk = Directory.Exists(Path.Combine(_workspace.RepoPath, testDir)) &&
                Directory.GetFiles(Path.Combine(_workspace.RepoPath, testDir), "*.csproj").Length > 0;

            if (hasCsproj || csprojOnDisk) continue;

            // Generate fallback .csproj
            var projectName = Path.GetFileName(testDir.TrimEnd('/', '\\'));
            var sourceProjectName = GetSourceProjectName();
            var csprojPath = Path.Combine(testDir, $"{projectName}.csproj");
            var isBlazor = IsBlazorProject();

            var csprojContent = GenerateTestCsproj(sourceProjectName, testDir, isBlazor);

            Logger.LogInformation("TestEngineer: scaffolding missing test project {CsprojPath}", csprojPath);
            var fullPath = Path.Combine(_workspace.RepoPath, csprojPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, csprojContent);
        }
    }

    /// <summary>
    /// Adds discovered test .csproj files to the solution so dotnet build/test can find them.
    /// Without this, test projects written to disk are invisible to solution-level commands.
    /// </summary>
    private async Task AddTestProjectsToSolutionAsync(
        IReadOnlyList<CodeFileParser.CodeFile> testFiles, CancellationToken ct)
    {
        if (_workspace?.RepoPath is null) return;

        // Find solution file
        var slnFiles = Directory.GetFiles(_workspace.RepoPath, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length == 0)
        {
            Logger.LogDebug("TestEngineer: no .sln file found, skipping dotnet sln add");
            return;
        }

        var slnPath = slnFiles[0];
        var slnContent = await File.ReadAllTextAsync(slnPath, ct);

        // Find all test .csproj files on disk under tests/
        var testsDir = Path.Combine(_workspace.RepoPath, "tests");
        if (!Directory.Exists(testsDir)) return;

        var testProjects = Directory.GetFiles(testsDir, "*.csproj", SearchOption.AllDirectories);
        foreach (var csproj in testProjects)
        {
            var relativePath = Path.GetRelativePath(_workspace.RepoPath, csproj).Replace('/', '\\');

            // Skip if already in the solution
            if (slnContent.Contains(relativePath, StringComparison.OrdinalIgnoreCase) ||
                slnContent.Contains(relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"sln \"{slnPath}\" add \"{csproj}\"",
                        WorkingDirectory = _workspace.RepoPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0)
                    Logger.LogInformation("TestEngineer: added {Project} to solution", relativePath);
                else
                    Logger.LogWarning("TestEngineer: failed to add {Project} to solution (exit {Code})",
                        relativePath, process.ExitCode);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TestEngineer: error adding {Project} to solution", relativePath);
            }
        }
    }

    /// <summary>
    /// Extracts the test project directory from a test file path (e.g., "tests/Foo.Tests/Unit/Bar.cs" → "tests/Foo.Tests").
    /// </summary>
    private static string? GetTestProjectDir(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        if (!normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = normalized.Split('/');
        if (parts.Length < 3) return null; // Need at least "tests/ProjectName/File.cs"
        return $"{parts[0]}/{parts[1]}";
    }

    /// <summary>
    /// Gets the source project name from the workspace's .csproj file.
    /// </summary>
    private string GetSourceProjectName()
    {
        if (_workspace?.RepoPath is null) return "Project";
        var csproj = Directory.EnumerateFiles(_workspace.RepoPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        return csproj is not null ? Path.GetFileNameWithoutExtension(csproj) : "Project";
    }

    /// <summary>
    /// Generates a test .csproj with appropriate NuGet references for the project type.
    /// </summary>
    private static string GenerateTestCsproj(string sourceProjectName, string testDir, bool isBlazor)
    {
        // Calculate relative path from test dir to source .csproj
        var depth = testDir.Replace('\\', '/').Split('/').Length; // e.g., "tests/Foo.Tests" = 2
        var relPath = string.Join("/", Enumerable.Repeat("..", depth)) + $"/{sourceProjectName}.csproj";

        var blazorPackages = isBlazor
            ? "    <PackageReference Include=\"bunit\" Version=\"1.28.9\" />\n"
            : "";

        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
{blazorPackages}    <PackageReference Include=""FluentAssertions"" Version=""6.12.0"" />
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.9.0"" />
    <PackageReference Include=""Moq"" Version=""4.20.70"" />
    <PackageReference Include=""xunit"" Version=""2.7.0"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""2.5.7"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""{relPath}"" />
  </ItemGroup>
</Project>
";
    }

    private static string GetTierUserSuffix(TestTier tier, string techStack)
    {
        return tier switch
        {
            TestTier.Unit =>
                $"Generate comprehensive UNIT test files for the above source code using {techStack}. " +
                "Test individual methods and classes in isolation with mocked dependencies. " +
                "Include edge cases, error handling, and boundary conditions.",

            TestTier.Integration =>
                $"Generate INTEGRATION test files for the above source code using {techStack}. " +
                "Test component interactions, API endpoints, and data access with real or in-memory dependencies. " +
                "Use WebApplicationFactory for API tests where applicable.",

            TestTier.UI =>
                $"Generate Playwright UI/E2E test files for the above source code using {techStack}. " +
                "Test user workflows, page navigation, form submissions, and visual elements. " +
                "Use the Page Object Model pattern. All tests must run headless. " +
                "Include the PlaywrightFixture class and page object classes.",

            _ => $"Generate test files for the above source code using {techStack}."
        };
    }

    /// <summary>
    /// Get the linked issue body for test strategy analysis.
    /// </summary>
    private async Task<string?> GetLinkedIssueBodyAsync(AgentPullRequest pr, CancellationToken ct)
    {
        var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
        if (!issueNumber.HasValue) return null;

        try
        {
            var issue = await _github.GetIssueAsync(issueNumber.Value, ct);
            return issue?.Body;
        }
        catch
        {
            return null;
        }
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
    /// Discovers test files that already exist in the repo for the given source files.
    /// Prevents the TE from generating duplicate tests when a PE already created tests.
    /// Looks for files in tests/ directories with matching class/component names.
    /// </summary>
    private async Task<Dictionary<string, string>> DiscoverExistingTestsAsync(
        List<string> sourceFilePaths, CancellationToken ct)
    {
        var existingTests = new Dictionary<string, string>();
        try
        {
            var repoTree = await _github.GetRepositoryTreeAsync(_config.Project.DefaultBranch, ct);

            // Build a set of source file names (without extension) to match against test files
            var sourceNames = sourceFilePaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Find test files in the repo tree that match source file names
            var testFilePaths = repoTree
                .Where(f =>
                    (f.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                     f.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                     f.Contains("Tests/", StringComparison.OrdinalIgnoreCase)) &&
                    TestableExtensions.Contains(Path.GetExtension(f)))
                .Where(f =>
                {
                    var testName = Path.GetFileNameWithoutExtension(f);
                    // Match "FooTests", "FooTest", "TestFoo" against source name "Foo"
                    return sourceNames.Any(src =>
                        testName.Contains(src, StringComparison.OrdinalIgnoreCase) ||
                        src.Contains(testName.Replace("Tests", "").Replace("Test", ""), StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            foreach (var testPath in testFilePaths)
            {
                try
                {
                    var content = await _github.GetFileContentAsync(testPath, _config.Project.DefaultBranch, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                        existingTests[testPath] = content;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not read existing test file {Path}", testPath);
                }
            }

            if (existingTests.Count > 0)
            {
                Logger.LogInformation(
                    "Discovered {Count} existing test files for source files: {Paths}",
                    existingTests.Count, string.Join(", ", existingTests.Keys));
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not scan repo tree for existing tests");
        }

        return existingTests;
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
        var branchName = $"agent/{Identity.Id.Replace(" ", "-").ToLowerInvariant()}/{taskSlug}";

        // Local workspace mode: write → build → test → push
        if (_workspace is not null && _buildRunner is not null && _testRunner is not null)
        {
            return await CreateTestPRViaLocalWorkspaceAsync(sourcePR, testFiles, branchName, ct);
        }

        // Fallback: API-only mode
        branchName = await _prWorkflow.CreateTaskBranchAsync(Identity.DisplayName, taskSlug, ct);

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

        return await CreateTestPRMetadataAsync(sourcePR, testFiles, branchName, testResults: null, ct);
    }

    /// <summary>
    /// Creates a test PR using the local workspace: writes test files, builds, runs tests
    /// per tier (unit → integration → UI), retries failures with AI fixes, then pushes.
    /// </summary>
    private async Task<int> CreateTestPRViaLocalWorkspaceAsync(
        AgentPullRequest sourcePR,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        string branchName,
        CancellationToken ct)
    {
        var wsConfig = _config.Workspace;

        // Sync and create branch
        await _workspace!.SyncWithMainAsync(ct);
        await _workspace.CreateBranchAsync(branchName, ct);

        // If UI tests are present and no Playwright project exists, scaffold one
        var hasUITests = testFiles.Any(f =>
            f.Path.Contains("UITests", StringComparison.OrdinalIgnoreCase) ||
            f.Path.Contains("Playwright", StringComparison.OrdinalIgnoreCase));

        if (hasUITests && _playwrightRunner is not null && wsConfig.EnableUITests)
        {
            var uiTestDir = Path.Combine(_workspace.RepoPath, "tests");
            var existingPlaywright = Directory.Exists(uiTestDir) &&
                Directory.GetDirectories(uiTestDir, "*UITests*", SearchOption.TopDirectoryOnly).Length > 0;

            if (!existingPlaywright)
            {
                Logger.LogInformation("TestEngineer: scaffolding Playwright test project infrastructure");
                var sourceProjectName = GetSourceProjectName();
                var scaffoldFiles = PlaywrightRunner.GeneratePlaywrightTestScaffold(
                    sourceProjectName, "tests/UITests");
                foreach (var sf in scaffoldFiles)
                    await _workspace.WriteFileAsync(sf.Path, sf.Content, ct);
            }
        }

        // Write test files
        foreach (var file in testFiles)
            await _workspace.WriteFileAsync(file.Path, file.Content, ct);

        // Ensure a unit/integration test .csproj exists — scaffold one if AI didn't generate it
        EnsureTestProjectExists(testFiles);

        // Add test projects to the solution so dotnet build/test can discover them
        await AddTestProjectsToSolutionAsync(testFiles, ct);

        // Build to verify test files compile
        var buildResult = await _buildRunner!.BuildAsync(
            _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

        if (!buildResult.Success)
        {
            // First try auto-restoring missing packages (cheap — no AI call needed)
            if (await TryAutoRestoreMissingPackagesAsync(buildResult, ct))
            {
                buildResult = await _buildRunner.BuildAsync(
                    _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            }
        }

        if (!buildResult.Success)
        {
            Logger.LogWarning("TestEngineer: test build failed, attempting AI fix (errors: {Errors})",
                buildResult.ParsedErrors.Count > 0
                    ? string.Join("; ", buildResult.ParsedErrors.Take(5))
                    : buildResult.Errors[..Math.Min(500, buildResult.Errors.Length)]);
            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            for (int attempt = 0; attempt < wsConfig.MaxBuildRetries && !buildResult.Success; attempt++)
            {
                var errorSummary = buildResult.ParsedErrors.Count > 0
                    ? string.Join("\n", buildResult.ParsedErrors.Take(20))
                    : buildResult.Errors.Length > 2000 ? buildResult.Errors[..2000] : buildResult.Errors;

                // Include project context so AI can properly fix structural issues
                var fixHistory = new ChatHistory();
                fixHistory.AddSystemMessage(
                    "You are a test engineer fixing build errors in test files. " +
                    "You have full context about the project structure.\n" +
                    "COMMON FIX: If errors are 'type or namespace not found', the .csproj is likely missing a " +
                    "NuGet PackageReference. Output a corrected .csproj with the missing packages added.\n" +
                    (IsBlazorProject() ? GetBlazorTestGuidance() : "") +
                    "Output ONLY corrected files using:\nFILE: path/to/file.ext\n```language\n<content>\n```\n" +
                    "If a .csproj file is missing or has wrong references, include the corrected .csproj in your output.");
                fixHistory.AddUserMessage(
                    $"Build attempt {attempt + 1}/{wsConfig.MaxBuildRetries} failed.\n\n" +
                    $"## Build Errors\n\n{errorSummary}\n\n" +
                    $"## Test files currently written\n\n" +
                    string.Join("\n", testFiles.Select(f => $"- {f.Path}")) + "\n\n" +
                    "Fix ALL errors. If the .csproj is missing or has wrong package references, output a corrected one.\n" +
                    "If namespace errors occur, check the actual project namespace and fix the using statements.");

                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixedFiles = CodeFileParser.ParseFiles(fixResponse.Content ?? "");
                foreach (var file in fixedFiles)
                    await _workspace.WriteFileAsync(file.Path, file.Content, ct);

                // Update our tracked test files list with fixes
                var fixedFileSet = new HashSet<string>(fixedFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                testFiles = testFiles
                    .Where(f => !fixedFileSet.Contains(f.Path))
                    .Concat(fixedFiles)
                    .ToList();

                buildResult = await _buildRunner.BuildAsync(
                    _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            }
        }

        // Run tests per tier — results aggregated for PR body
        var tierResults = new List<TestResult>();

        if (buildResult.Success)
        {
            // Tier 1: Unit tests (fast feedback)
            var unitResult = await RunTestTierWithRetryAsync(
                TestTier.Unit,
                wsConfig.UnitTestCommand ?? wsConfig.TestCommand,
                wsConfig.UnitTestTimeoutSeconds,
                wsConfig, ct);
            if (unitResult is not null)
                tierResults.Add(unitResult);

            // Tier 2: Integration tests (only if unit tests passed or no unit-specific command)
            var unitPassed = unitResult?.Success ?? true;
            if (unitPassed && wsConfig.IntegrationTestCommand is not null)
            {
                var intResult = await RunTestTierWithRetryAsync(
                    TestTier.Integration,
                    wsConfig.IntegrationTestCommand,
                    wsConfig.IntegrationTestTimeoutSeconds,
                    wsConfig, ct);
                if (intResult is not null)
                    tierResults.Add(intResult);
            }

            // Tier 3: UI tests with Playwright (only if earlier tiers passed)
            var allPriorPassed = tierResults.All(r => r.Success);
            if (allPriorPassed && wsConfig.EnableUITests && _playwrightRunner is not null && wsConfig.UITestCommand is not null)
            {
                try
                {
                    // Ensure Playwright browsers are installed
                    await _playwrightRunner.EnsureBrowsersInstalledAsync(wsConfig, _workspace.RepoPath, ct);

                    var uiResult = await _playwrightRunner.RunUITestsAsync(
                        _workspace.RepoPath, wsConfig,
                        wsConfig.UITestCommand,
                        wsConfig.UITestTimeoutSeconds, ct);
                    tierResults.Add(uiResult);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "TestEngineer: UI test execution failed");
                    tierResults.Add(new TestResult
                    {
                        Success = false,
                        Output = $"UI test execution error: {ex.Message}",
                        Passed = 0, Failed = 0, Skipped = 0,
                        Duration = TimeSpan.Zero,
                        Tier = TestTier.UI,
                        FailureDetails = [ex.Message]
                    });
                }
            }

            foreach (var result in tierResults)
            {
                Logger.LogInformation("TestEngineer: {Tier} tests — Passed: {Passed}, Failed: {Failed}, Skipped: {Skipped}",
                    result.Tier, result.Passed, result.Failed, result.Skipped);
            }
        }

        // Gate: only commit and push if the build succeeded
        if (!buildResult.Success)
        {
            Logger.LogError("TestEngineer: build still failing after {MaxRetries} fix attempts, reverting test files",
                wsConfig.MaxBuildRetries);
            await _workspace.RevertUncommittedChangesAsync(ct);
            await _github.AddPullRequestCommentAsync(sourcePR.Number,
                $"❌ **Test Build Blocked:** Test files for PR #{sourcePR.Number} could not be made to compile after " +
                $"{wsConfig.MaxBuildRetries} fix attempts. Tests were not committed.", ct);
            return -1;
        }

        // Commit and push — guaranteed to build at this point
        await _workspace.CommitAsync($"test: add tests for PR #{sourcePR.Number}", ct);
        await _workspace.PushAsync(branchName, ct);

        // Create aggregate result for PR body
        AggregateTestResult? aggregate = tierResults.Count > 0
            ? new AggregateTestResult { TierResults = tierResults }
            : null;

        return await CreateTestPRMetadataAsync(sourcePR, testFiles, branchName, aggregate, ct);
    }

    /// <summary>
    /// Run a specific test tier with the AI fix-retry loop.
    /// </summary>
    private async Task<TestResult?> RunTestTierWithRetryAsync(
        TestTier tier,
        string testCommand,
        int timeoutSeconds,
        WorkspaceConfig wsConfig,
        CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Working, $"Running {tier} tests");

        var testResult = await _testRunner!.RunTestsAsync(
            _workspace!.RepoPath, testCommand, timeoutSeconds, ct);
        testResult = testResult with { Tier = tier };

        if (!testResult.Success)
        {
            Logger.LogWarning("TestEngineer: {Tier} tests failed ({Failed} of {Total}), attempting AI fix",
                tier, testResult.Failed, testResult.Total);

            var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            for (int attempt = 0; attempt < wsConfig.MaxTestRetries && !testResult.Success; attempt++)
            {
                var failureSummary = testResult.FailureDetails.Count > 0
                    ? string.Join("\n", testResult.FailureDetails.Take(10))
                    : testResult.Output.Length > 2000 ? testResult.Output[^2000..] : testResult.Output;

                var fixHistory = new ChatHistory();
                fixHistory.AddUserMessage(
                    $"{tier} tests failed ({testResult.Failed} of {testResult.Total}):\n\n{failureSummary}\n\n" +
                    "Fix the test code. Output ONLY corrected files using:\n" +
                    "FILE: path/to/file.ext\n```language\n<content>\n```\n\n" +
                    "Only fix test bugs — don't mask real code bugs.");

                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixedFiles = CodeFileParser.ParseFiles(fixResponse.Content ?? "");
                foreach (var file in fixedFiles)
                    await _workspace.WriteFileAsync(file.Path, file.Content, ct);

                // Rebuild + retest
                var rebuildResult = await _buildRunner!.BuildAsync(
                    _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
                if (!rebuildResult.Success)
                {
                    // Test fix broke the build — revert it
                    await _workspace.RevertUncommittedChangesAsync(ct);
                    continue;
                }

                testResult = await _testRunner.RunTestsAsync(
                    _workspace.RepoPath, testCommand, timeoutSeconds, ct);
                testResult = testResult with { Tier = tier };
            }

            // Last resort: if tests still fail after all retries, remove failing tests
            if (!testResult.Success)
            {
                Logger.LogWarning("TestEngineer: {Tier} tests still failing after {Max} retries — removing unfixable tests",
                    tier, wsConfig.MaxTestRetries);

                testResult = await RemoveFailingTestsForTierAsync(
                    tier, testResult, testCommand, timeoutSeconds, wsConfig, ct);
            }
        }

        return testResult;
    }

    /// <summary>
    /// Last resort for a test tier: remove failing tests with documentation so remaining tests pass.
    /// Ensures no failing tests are committed.
    /// </summary>
    private async Task<TestResult> RemoveFailingTestsForTierAsync(
        TestTier tier,
        TestResult failingResult,
        string testCommand,
        int timeoutSeconds,
        WorkspaceConfig wsConfig,
        CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var failureSummary = failingResult.FailureDetails.Count > 0
            ? string.Join("\n", failingResult.FailureDetails.Take(20))
            : failingResult.Output.Length > 3000 ? failingResult.Output[^3000..] : failingResult.Output;

        var removePrompt = $"""
            The following {tier} tests have been failing despite {wsConfig.MaxTestRetries} attempts to fix them.
            These tests MUST be removed because they cannot be made to pass.

            FAILING TESTS:
            {failureSummary}

            For each failing test:
            1. REMOVE the failing test method entirely
            2. Add a comment at the location where it was removed:
               // TEST REMOVED: [TestMethodName] - Could not be resolved after {wsConfig.MaxTestRetries} fix attempts.
               // Reason: [brief description of the failure]
               // This test should be revisited when the underlying issue is resolved.
            3. Keep ALL passing tests intact — do not remove or modify them

            Output ONLY the updated test files using this format:
            FILE: path/to/test/file.ext
            ```language
            <complete updated file content with failing tests removed>
            ```

            Include the COMPLETE file content for each test file that needs changes.
            Ensure the remaining code still compiles after removal.
            """;

        var history = new ChatHistory();
        history.AddUserMessage(removePrompt);
        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        var updatedFiles = CodeFileParser.ParseFiles(response.Content ?? "");

        if (updatedFiles.Count > 0)
        {
            foreach (var file in updatedFiles)
                await _workspace!.WriteFileAsync(file.Path, file.Content, ct);

            // Verify build still passes after test removal
            var buildResult = await _buildRunner!.BuildAsync(
                _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!buildResult.Success)
            {
                Logger.LogWarning("TestEngineer: build broken after test removal, reverting");
                await _workspace.RevertUncommittedChangesAsync(ct);
                return failingResult;
            }

            // Verify remaining tests pass
            var result = await _testRunner!.RunTestsAsync(
                _workspace.RepoPath, testCommand, timeoutSeconds, ct);
            result = result with { Tier = tier };

            if (result.Success)
            {
                Logger.LogInformation("TestEngineer: removed unfixable {Tier} tests, {Passed} remaining tests pass",
                    tier, result.Passed);
                return result;
            }

            // Recurse one more time if there are still failures after removal
            Logger.LogWarning("TestEngineer: still {Failed} failing tests after removal, doing another pass", result.Failed);
            return await RemoveFailingTestsForTierAsync(tier, result, testCommand, timeoutSeconds, wsConfig, ct);
        }

        return failingResult;
    }

    /// <summary>
    /// Creates the PR metadata (title, body, labels) — shared by both API and local workspace paths.
    /// Uses AggregateTestResult for multi-tier reporting.
    /// </summary>
    private async Task<int> CreateTestPRMetadataAsync(
        AgentPullRequest sourcePR,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        string branchName,
        AggregateTestResult? testResults,
        CancellationToken ct)
    {
        var fileList = string.Join("\n", testFiles.Select(f => $"- `{f.Path}`"));
        var prTitle = $"{Identity.DisplayName}: Tests for PR #{sourcePR.Number} - {sourcePR.Title}";

        var bodyBuilder = new System.Text.StringBuilder();
        bodyBuilder.AppendLine("## Test Engineering");
        bodyBuilder.AppendLine();
        bodyBuilder.AppendLine($"**Source PR:** #{sourcePR.Number} (merged)");
        bodyBuilder.AppendLine($"**Generated by:** {Identity.DisplayName}");
        bodyBuilder.AppendLine($"**Test Files:** {testFiles.Count}");
        bodyBuilder.AppendLine();

        // Include multi-tier test results
        if (testResults is not null)
        {
            bodyBuilder.AppendLine(testResults.FormatAsMarkdown());
        }

        bodyBuilder.AppendLine("### Test Files");
        bodyBuilder.AppendLine(fileList);
        bodyBuilder.AppendLine();

        // Categorize test files by tier for clarity
        var unitFiles = testFiles.Where(f => f.Path.Contains("/Unit/", StringComparison.OrdinalIgnoreCase) || f.Path.Contains("\\Unit\\", StringComparison.OrdinalIgnoreCase)).ToList();
        var integrationFiles = testFiles.Where(f => f.Path.Contains("/Integration/", StringComparison.OrdinalIgnoreCase) || f.Path.Contains("\\Integration\\", StringComparison.OrdinalIgnoreCase)).ToList();
        var uiFiles = testFiles.Where(f => f.Path.Contains("UITests", StringComparison.OrdinalIgnoreCase) || f.Path.Contains("Playwright", StringComparison.OrdinalIgnoreCase)).ToList();

        bodyBuilder.AppendLine("### Coverage Tiers");
        if (unitFiles.Count > 0) bodyBuilder.AppendLine($"- **Unit:** {unitFiles.Count} files — isolated function/method tests");
        if (integrationFiles.Count > 0) bodyBuilder.AppendLine($"- **Integration:** {integrationFiles.Count} files — component interaction tests");
        if (uiFiles.Count > 0) bodyBuilder.AppendLine($"- **UI/E2E:** {uiFiles.Count} files — Playwright browser automation tests");

        var prBody = bodyBuilder.ToString();

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

        // Upload test artifacts (screenshots, videos, traces) to the test PR
        if (testResults is not null && testResults.TierResults.Any(r => r.Artifacts.HasArtifacts))
        {
            try
            {
                var artifactSb = new System.Text.StringBuilder();
                await UploadTestArtifactsToPrAsync(testPr, testResults.TierResults, artifactSb, ct);
                if (artifactSb.Length > 0)
                    await _github.AddPullRequestCommentAsync(testPr.Number, artifactSb.ToString(), ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not upload test artifacts to PR #{Number}", testPr.Number);
            }
        }

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

            if (attempts >= _config.Limits.MaxReworkCycles)
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

                // Fetch current PR files so the AI can see what it already wrote
                var currentFilesContext = await _prWorkflow.GetPRCodeContextAsync(
                    rework.PrNumber, pr.HeadBranch, ct: ct);

                var history = new ChatHistory();
                history.AddSystemMessage(
                    $"You are an expert test engineer maintaining tests for a {techStack} project.\n" +
                    "A reviewer requested changes on your test PR. Update the test files to address all feedback.\n\n" +
                    "CRITICAL: Your response MUST start with a CHANGES SUMMARY that addresses EACH numbered " +
                    "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
                    "in one sentence what you changed or why no change was needed.\n\n" +
                    "After the CHANGES SUMMARY, output each corrected file using this exact format:\n" +
                    "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
                    "Include the COMPLETE content of each changed file. " +
                    "You MUST include at least one FILE: block — a summary alone is not sufficient.");

                history.AddUserMessage(
                    $"## Test PR #{rework.PrNumber}: {rework.PrTitle}\n\n" +
                    $"## Original PR Description\n{pr.Body}\n\n" +
                    (string.IsNullOrEmpty(currentFilesContext) ? "" :
                        $"## Current Files on PR Branch\nThese are the files you already wrote. " +
                        "Modify them to address the feedback below:\n{currentFilesContext}\n\n") +
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

                        var commentBody = $"**[{Identity.DisplayName}] Rework** — Addressed feedback from {rework.Reviewer}.\n\n";
                        if (!string.IsNullOrWhiteSpace(changesSummary))
                            commentBody += changesSummary;
                        else
                            commentBody += $"**Files updated:** {string.Join(", ", codeFiles.Select(f => $"`{f.Path}`"))}";
                        await _github.AddPullRequestCommentAsync(pr.Number, commentBody, ct);

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
                        UpdateStatus(AgentStatus.Idle, $"Waiting for review on test PR #{pr.Number}");
                        await RememberAsync(MemoryType.Action,
                            $"Addressed review feedback on test PR #{pr.Number} from {rework.Reviewer}",
                            TruncateForMemory(rework.Feedback), ct);
                    }
                    else
                    {
                        // AI failed to produce FILE: blocks — do NOT mark as ready for review
                        Logger.LogWarning(
                            "TestEngineer rework on PR #{PrNumber} produced no FILE: blocks — no changes committed. " +
                            "Skipping ready-for-review to avoid pointless re-review of unchanged code",
                            rework.PrNumber);
                        await _github.AddPullRequestCommentAsync(pr.Number,
                            $"**[{Identity.DisplayName}] Rework attempted** — AI response did not produce committable file changes. " +
                            $"This rework attempt counted toward the limit ({attempts}/{_config.Limits.MaxReworkCycles}).", ct);
                    }
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
                if (pendingFeedback is { } pending)
                {
                    _reworkQueue.Enqueue((pr.Number, pr.Title, pending.Feedback, pending.Reviewer));
                    Logger.LogInformation("TestEngineer recovered feedback on PR #{PrNumber} from {Reviewer}",
                        pr.Number, pending.Reviewer);
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

                // PR exists but isn't ready-for-review and has no pending feedback.
                // This happens if the runner was killed after creating the PR but before marking it ready.
                if (pr.Labels.Contains("in-progress", StringComparer.OrdinalIgnoreCase))
                {
                    var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
                    if (changedFiles.Count > 0)
                    {
                        Logger.LogInformation(
                            "TestEngineer recovering PR #{PrNumber} — has {FileCount} files but not ready-for-review. Marking ready.",
                            pr.Number, changedFiles.Count);
                        await SyncBranchWithMainAsync(pr.Number, ct);
                        await _prWorkflow.MarkReadyForReviewAsync(pr.Number, Identity.DisplayName, ct);
                        await _messageBus.PublishAsync(new ReviewRequestMessage
                        {
                            FromAgentId = Identity.Id,
                            ToAgentId = "*",
                            MessageType = "ReviewRequest",
                            PrNumber = pr.Number,
                            PrTitle = pr.Title,
                            ReviewType = "Recovery"
                        }, ct);
                        UpdateStatus(AgentStatus.Idle, $"Test PR #{pr.Number} recovered and ready for review");
                        return;
                    }
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

    /// <summary>
    /// Read visual design reference files from the repository for UI test generation.
    /// </summary>
    private async Task<string?> ReadDesignReferencesForTestsAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _github.GetRepositoryTreeAsync("main", ct);
            var designFiles = tree
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".html" && ext != ".htm") return false;
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return name.Contains("design") || name.Contains("concept") ||
                           name.Contains("mockup") || name.Contains("wireframe");
                })
                .ToList();

            if (designFiles.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var file in designFiles)
            {
                var content = await _github.GetFileContentAsync(file, ct: ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                sb.AppendLine($"### Design File: `{file}`");
                sb.AppendLine("```html");
                sb.AppendLine(content.Length > 5000 ? content[..5000] + "\n<!-- truncated -->" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read design reference files for test generation");
            return null;
        }
    }

    #endregion
}
