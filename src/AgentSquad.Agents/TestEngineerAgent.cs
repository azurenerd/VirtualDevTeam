using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.Prompts;
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
    /// Result of AI-based testability assessment for a PR.
    /// Replaces the old hardcoded TestableExtensions approach — the AI evaluates
    /// the actual files, acceptance criteria, and issue context to decide what tests are needed.
    /// </summary>
    internal sealed record TestabilityAssessment(
        bool NeedsTests,
        bool NeedsUnitTests,
        bool NeedsIntegrationTests,
        bool NeedsUITests,
        string Rationale,
        IReadOnlyList<string> TestableFiles);

    /// <summary>Classification of a test failure: is the bug in the test or the source?</summary>
    internal enum FailureClassification { TestBug, SourceBug, Ambiguous }

    /// <summary>Details of a source code bug found by a failing test.</summary>
    internal sealed record SourceBugReport(
        string TestName,
        string SourceFile,
        string SourceMethod,
        string Issue,
        string TestOutput);

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
    private readonly IGateCheckService _gateCheck;
    private readonly IPromptTemplateService? _promptService;
    private readonly DecisionGateService? _decisionGate;
    private readonly IAgentTaskTracker _taskTracker;

    private LocalWorkspace? _workspace;
    private bool _pendingWorkspaceCleanup;
    private readonly HashSet<int> _testedPRs = new();
    private readonly HashSet<int> _sessionTestedPRs = new(); // Only PRs actually tested this session (not skipped old ones)
    private readonly Dictionary<int, int> _testFailureAttempts = new(); // Track transient failure retries per PR
    private const int MaxTestFailureRetries = 2;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentQueue<(int PrNumber, string PrTitle, string Feedback, string Reviewer)> _reworkQueue = new();
    private readonly Dictionary<int, int> _reworkAttempts = new();
    private readonly Dictionary<int, string> _prSessionIds = new();
    private readonly AgentStateStore? _stateStore;
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow.AddHours(-4); // Look back 4h to catch PRs from recent runs without massive backlog
    private int? _currentTestPrNumber;

    // Holds the AI testability assessment from the scan phase so GenerateTestCodeAsync can use it
    private TestabilityAssessment? _lastTestabilityAssessment;

    // Tracks PRs where TE found source bugs and is waiting for engineer to fix.
    // Key = PR number, Value = number of source-bug rounds already requested.
    private readonly Dictionary<int, int> _pendingSourceFixPRs = new();
    // Stores the failing test details per PR so TE can re-run after engineer fixes.
    private readonly Dictionary<int, List<SourceBugReport>> _pendingSourceBugDetails = new();
    // Transient: holds source bugs from the last RunTestTierWithRetryAsync call for the caller to consume
    private List<SourceBugReport> _lastClassifiedSourceBugs = new();

    public TestEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        ILogger<AgentBase> logger,
        RoleContextProvider? roleContextProvider = null,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        PlaywrightRunner? playwrightRunner = null,
        TestStrategyAnalyzer? testStrategyAnalyzer = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        AgentStateStore? stateStore = null,
        IPromptTemplateService? promptService = null,
        DecisionGateService? decisionGate = null,
        IAgentTaskTracker? taskTracker = null)
        : base(identity, logger, memoryStore, roleContextProvider)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _prWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        _projectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _buildRunner = buildRunner;
        _testRunner = testRunner;
        _playwrightRunner = playwrightRunner;
        _testStrategyAnalyzer = testStrategyAnalyzer;
        _metrics = metrics;
        _stateStore = stateStore;
        _promptService = promptService;
        _decisionGate = decisionGate;
        _taskTracker = taskTracker!;
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

        // Restore CLI session IDs from database so test rework resumes the same conversation
        if (_stateStore is not null)
        {
            try
            {
                var sessions = await _stateStore.LoadCliSessionsAsync(Identity.Id, ct);
                foreach (var (prNumber, sessionId) in sessions)
                    _prSessionIds[prNumber] = sessionId;

                if (sessions.Count > 0)
                    Logger.LogInformation("TestEngineer restored {Count} CLI session(s) from database", sessions.Count);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "TestEngineer failed to restore CLI sessions from database");
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

            // AI-based testability assessment for legacy mode
            var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);
            
            var assessStepId = _taskTracker.BeginStep(Identity.Id, $"te-pr-{pr.Number}", "Generate test plan",
                $"Assessing testability of merged PR #{pr.Number}", Identity.ModelTier);
            Logger.LogInformation("Assessing testability of merged PR #{Number} ({FileCount} files): {Title}",
                pr.Number, changedFiles.Count, pr.Title);
            var assessment = await AssessTestabilityAsync(pr, changedFiles, ct);
            _taskTracker.RecordLlmCall(assessStepId);

            if (!assessment.NeedsTests)
            {
                Logger.LogInformation("AI assessment: merged PR #{Number} does not need tests — {Rationale}", pr.Number, assessment.Rationale);
                _testedPRs.Add(pr.Number);
                _taskTracker.CompleteStep(assessStepId, AgentTaskStepStatus.Skipped);
                continue;
            }
            _taskTracker.CompleteStep(assessStepId);

            _lastTestabilityAssessment = assessment;
            var codeFiles = assessment.TestableFiles;

            Logger.LogInformation(
                "AI assessment: merged PR #{Number} needs tests ({Count} testable files): {Title}",
                pr.Number, codeFiles.Count, pr.Title);
            LogActivity("task", $"🧪 Generating tests for PR #{pr.Number}: {pr.Title} ({codeFiles.Count} files)");

            try
            {
                var execStepId = _taskTracker.BeginStep(Identity.Id, $"te-pr-{pr.Number}", "Execute tests",
                    $"Generating and running tests for PR #{pr.Number}", Identity.ModelTier);
                await GenerateTestsForMergedPRAsync(pr, codeFiles, ct);
                _testedPRs.Add(pr.Number);
                _sessionTestedPRs.Add(pr.Number);
                _taskTracker.CompleteStep(execStepId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to generate tests for merged PR #{Number}", pr.Number);
                _testedPRs.Add(pr.Number); // Don't retry failed PRs indefinitely
                _sessionTestedPRs.Add(pr.Number); // Count toward coverage even if PR creation failed
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

        // Priority: re-test PRs where we previously found source bugs and the engineer may have pushed fixes
        if (_pendingSourceFixPRs.Count > 0)
        {
            foreach (var pendingPr in openPRs.Where(p => _pendingSourceFixPRs.ContainsKey(p.Number)))
            {
                if (ct.IsCancellationRequested) break;

                // Check if the PR no longer has CHANGES_REQUESTED (engineer addressed feedback)
                // The engineer's HandleReworkAsync re-requests review after fixing, which clears CHANGES_REQUESTED.
                // We detect this by checking if the architect-approved label is still present.
                if (pendingPr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.LogInformation(
                        "TestEngineer: PR #{Number} was re-approved after source bug fix — re-testing",
                        pendingPr.Number);
                    _pendingSourceBugDetails.Remove(pendingPr.Number);
                    // Don't remove from _pendingSourceFixPRs — the round count persists
                    // Inline testing will re-run and either pass or escalate again
                    // Re-fetch changed files and re-test
                    try
                    {
                        var changedFiles = await _github.GetPullRequestChangedFilesAsync(pendingPr.Number, ct);
                        // For re-test after source fix, use all changed files — AI assessment already done
                        await AddInlineTestsToPRAsync(pendingPr, changedFiles, ct);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Re-test failed for source-fix PR #{Number}", pendingPr.Number);
                    }
                    return; // One PR at a time
                }
            }
        }

        // Sort by creation date (oldest first = FIFO) so earlier PRs get tests first
        var candidates = openPRs
            .Where(pr => !_testedPRs.Contains(pr.Number))
            .OrderBy(pr => pr.CreatedAt)
            .ToList();

        foreach (var pr in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Gate: must have 'architect-approved' label (Phase 1 complete — Architect reviewed)
            if (!pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                continue;

            // Gate: PE must have finished all rework. Check that the last PE review comment
            // is APPROVED (not CHANGES_REQUESTED). This prevents TE from starting work while
            // the PE worker is still pushing rework commits.
            try
            {
                var comments = await _github.GetPullRequestCommentsAsync(pr.Number, ct);
                var lastPeReviewComment = comments
                    .Where(c => c.Body.Contains("[SoftwareEngineer]", StringComparison.OrdinalIgnoreCase)
                             && (c.Body.Contains("APPROVED", StringComparison.OrdinalIgnoreCase)
                              || c.Body.Contains("CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase)))
                    .LastOrDefault();

                if (lastPeReviewComment is not null &&
                    lastPeReviewComment.Body.Contains("CHANGES REQUESTED", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogDebug("Skipping PR #{Number} — PE still has outstanding changes requested", pr.Number);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not check PE review status for PR #{Number}", pr.Number);
                // If we can't check, proceed anyway — architect-approved is the primary gate
            }

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

            // AI-based testability assessment — examines files, acceptance criteria, and context
            var changedFiles = await _github.GetPullRequestChangedFilesAsync(pr.Number, ct);

            var inlineAssessStepId = _taskTracker.BeginStep(Identity.Id, $"te-pr-{pr.Number}", "Generate test plan",
                $"Assessing testability of PR #{pr.Number}", Identity.ModelTier);
            Logger.LogInformation("Assessing testability of PR #{Number} ({FileCount} changed files): {Title}",
                pr.Number, changedFiles.Count, pr.Title);
            var assessment = await AssessTestabilityAsync(pr, changedFiles, ct);
            _taskTracker.RecordLlmCall(inlineAssessStepId);

            if (!assessment.NeedsTests)
            {
                Logger.LogInformation("AI assessment: PR #{Number} does not need tests — {Rationale}", pr.Number, assessment.Rationale);
                _testedPRs.Add(pr.Number);
                // Post comment BEFORE label so PM sees test results when it sees the label
                await _github.AddPullRequestCommentAsync(pr.Number,
                    $"✅ **[TestEngineer] No Tests Needed** — {assessment.Rationale}\n\n" +
                    "Marking as tested to proceed with PM review.", ct);
                await ApplyTestsAddedLabelAsync(pr, ct);
                _taskTracker.CompleteStep(inlineAssessStepId, AgentTaskStepStatus.Skipped);
                continue;
            }
            _taskTracker.CompleteStep(inlineAssessStepId);

            // Feed the assessment into the test strategy so GenerateTestCodeAsync knows what tiers to write
            _lastTestabilityAssessment = assessment;
            var codeFiles = assessment.TestableFiles;

            Logger.LogInformation(
                "AI assessment: PR #{Number} needs tests (Unit={Unit}, Integration={Integration}, UI={UI}) — {Count} testable files: {Title}",
                pr.Number, assessment.NeedsUnitTests, assessment.NeedsIntegrationTests, assessment.NeedsUITests,
                codeFiles.Count, pr.Title);
            LogActivity("task",
                $"🧪 Adding inline tests to approved PR #{pr.Number}: {pr.Title} ({codeFiles.Count} files, " +
                $"Unit={assessment.NeedsUnitTests}, Integration={assessment.NeedsIntegrationTests}, UI={assessment.NeedsUITests})");

            try
            {
                var inlineExecStepId = _taskTracker.BeginStep(Identity.Id, $"te-pr-{pr.Number}", "Execute tests",
                    $"Adding inline tests to PR #{pr.Number}", Identity.ModelTier);
                var testingCompleted = await AddInlineTestsToPRAsync(pr, codeFiles, ct);
                if (testingCompleted)
                {
                    _testedPRs.Add(pr.Number);
                    _sessionTestedPRs.Add(pr.Number);
                    _taskTracker.CompleteStep(inlineExecStepId);
                }
                else
                {
                    _taskTracker.SetStepWaiting(inlineExecStepId);
                }
                // else: blocked (e.g., base build failed) — don't add to _testedPRs so TE re-checks after rework
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

                // Allow retries for transient errors (network, auth), but cap to prevent infinite loops
                _testFailureAttempts.TryGetValue(pr.Number, out var attempts);
                _testFailureAttempts[pr.Number] = attempts + 1;
                if (attempts + 1 >= MaxTestFailureRetries)
                {
                    _testedPRs.Add(pr.Number); // Give up after max retries
                    Logger.LogWarning("PR #{Number} failed {Attempts} times — marking as tested to prevent infinite retry",
                        pr.Number, attempts + 1);
                }
                else
                {
                    Logger.LogInformation("PR #{Number} failed (attempt {Attempt}/{Max}) — will retry next cycle",
                        pr.Number, attempts + 1, MaxTestFailureRetries);
                }

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
    /// Uses AI to assess whether a PR's changed files need tests, and what types.
    /// Examines the actual file contents, PR description, linked issue acceptance criteria,
    /// and tech stack context — not hardcoded file extensions.
    /// </summary>
    private async Task<TestabilityAssessment> AssessTestabilityAsync(
        AgentPullRequest pr, IReadOnlyList<string> changedFiles, CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Gather acceptance criteria from linked issue
        string? issueBody = null;
        var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
        if (issueNumber.HasValue)
        {
            try
            {
                var issue = await _github.GetIssueAsync(issueNumber.Value, ct);
                issueBody = issue?.Body;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not fetch linked issue for testability assessment");
            }
        }

        // Build file listing with extensions and brief content hints
        var fileList = new System.Text.StringBuilder();
        foreach (var f in changedFiles)
        {
            fileList.AppendLine($"- {f} (ext: {Path.GetExtension(f)})");
        }

        var prompt = _promptService is not null
            ? await _promptService.RenderAsync("test-engineer/testability-assessment", new Dictionary<string, string>
            {
                ["pr_title"] = pr.Title,
                ["pr_description"] = pr.Body ?? "(no description)",
                ["file_list"] = fileList.ToString(),
                ["issue_body"] = issueBody ?? "(no linked issue)",
                ["tech_stack"] = _config.Project.TechStack
            }, ct)
            : null;

        prompt ??= $"""
            You are a Test Engineer assessing whether a pull request needs automated tests.
            
            ## PR Information
            **Title:** {pr.Title}
            **Description:**
            {pr.Body ?? "(no description)"}
            
            ## Changed Files
            {fileList}
            
            ## Linked Issue / Acceptance Criteria
            {issueBody ?? "(no linked issue)"}
            
            ## Tech Stack
            {_config.Project.TechStack}
            
            ## Your Task
            Analyze the changed files and acceptance criteria. Determine:
            1. **Does this PR need any automated tests?** Consider: are there code files with logic that can be tested? Config-only, documentation-only, or purely static asset PRs typically don't need tests.
            2. **What types of tests?** Unit tests (logic, models, services), Integration tests (API endpoints, data access, middleware), UI/E2E tests (pages, components, user interactions).
            
            Respond in EXACTLY this format (no other text):
            NEEDS_TESTS: true/false
            NEEDS_UNIT: true/false
            NEEDS_INTEGRATION: true/false
            NEEDS_UI: true/false
            TESTABLE_FILES: comma-separated list of files that should have tests written (empty if none)
            RATIONALE: one sentence explaining your assessment
            """;

        try
        {
            var history = CreateChatHistory();
            history.AddUserMessage(prompt);
            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var responseText = string.Join("", response.Select(r => r.Content ?? ""));

            return ParseTestabilityResponse(responseText, changedFiles);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "AI testability assessment failed for PR #{Number}, falling back to all-files", pr.Number);
            // Fallback: assume tests needed for any non-trivial PR
            return new TestabilityAssessment(
                NeedsTests: changedFiles.Count > 0,
                NeedsUnitTests: true,
                NeedsIntegrationTests: false,
                NeedsUITests: false,
                Rationale: "Fallback: AI assessment unavailable, defaulting to unit tests",
                TestableFiles: changedFiles);
        }
    }

    /// <summary>Parses the structured AI response into a TestabilityAssessment.</summary>
    private static TestabilityAssessment ParseTestabilityResponse(string response, IReadOnlyList<string> allFiles)
    {
        bool needsTests = false, needsUnit = false, needsIntegration = false, needsUI = false;
        string rationale = "AI assessment";
        var testableFiles = new List<string>();

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("NEEDS_TESTS:", StringComparison.OrdinalIgnoreCase))
                needsTests = trimmed.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (trimmed.StartsWith("NEEDS_UNIT:", StringComparison.OrdinalIgnoreCase))
                needsUnit = trimmed.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (trimmed.StartsWith("NEEDS_INTEGRATION:", StringComparison.OrdinalIgnoreCase))
                needsIntegration = trimmed.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (trimmed.StartsWith("NEEDS_UI:", StringComparison.OrdinalIgnoreCase))
                needsUI = trimmed.Contains("true", StringComparison.OrdinalIgnoreCase);
            else if (trimmed.StartsWith("TESTABLE_FILES:", StringComparison.OrdinalIgnoreCase))
            {
                var filesPart = trimmed["TESTABLE_FILES:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(filesPart))
                {
                    testableFiles = filesPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(f => allFiles.Any(af => af.EndsWith(f.Trim(), StringComparison.OrdinalIgnoreCase)
                            || f.Trim().Equals(af, StringComparison.OrdinalIgnoreCase)))
                        .Select(f => allFiles.FirstOrDefault(af => af.EndsWith(f.Trim(), StringComparison.OrdinalIgnoreCase)
                            || f.Trim().Equals(af, StringComparison.OrdinalIgnoreCase)) ?? f.Trim())
                        .ToList();
                }
            }
            else if (trimmed.StartsWith("RATIONALE:", StringComparison.OrdinalIgnoreCase))
                rationale = trimmed["RATIONALE:".Length..].Trim();
        }

        // If AI says tests needed but didn't list specific files, use all changed files
        if (needsTests && testableFiles.Count == 0)
            testableFiles = allFiles.ToList();

        return new TestabilityAssessment(needsTests, needsUnit, needsIntegration, needsUI, rationale, testableFiles);
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
    /// <returns>true if testing completed (pass or fail); false if blocked (e.g., base build failed, awaiting rework)</returns>
    private async Task<bool> AddInlineTestsToPRAsync(
        AgentPullRequest pr, IReadOnlyList<string> codeFilePaths, CancellationToken ct)
    {
        _currentTestPrNumber = pr.Number;
        ActivateTestPrSession(pr.Number);

        try
        {
            UpdateStatus(AgentStatus.Working, $"Adding tests to PR #{pr.Number}");

            if (_workspace is not null && _buildRunner is not null && _testRunner is not null)
                return await AddInlineTestsViaWorkspaceAsync(pr, codeFilePaths, ct);
            else
            {
                await AddInlineTestsViaApiAsync(pr, codeFilePaths, ct);
                return true; // API path always completes
            }
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
    /// <returns>true if testing completed; false if blocked by build failure (awaiting rework)</returns>
    private async Task<bool> AddInlineTestsViaWorkspaceAsync(
        AgentPullRequest pr, IReadOnlyList<string> codeFilePaths, CancellationToken ct)
    {
        try
        {
        var wsConfig = _config.Workspace;

        // --- Phase 1: Sync workspace to the PR's branch ---
        LogActivity("testing", "🔍 Checking out PR branch");
        UpdateStatus(AgentStatus.Working, $"Checking out PR #{pr.Number} branch");
        await _workspace!.SyncWithMainAsync(ct);
        await _workspace.CheckoutBranchAsync(pr.HeadBranch, ct);

        // --- Phase 2: Verify the PR's code actually builds ---
        // If the base code doesn't build, there's no point adding tests to it
        LogActivity("testing", "⏳ Verifying PR base code builds");
        UpdateStatus(AgentStatus.Working, $"Verifying PR #{pr.Number} base build");
        var baseBuild = await _buildRunner!.BuildAsync(
            _workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);

        // If root build failed (often MSB1003: no .sln/.csproj at root), try finding the actual project
        if (!baseBuild.Success && baseBuild.Duration.TotalSeconds < 2)
        {
            var slnFiles = Directory.GetFiles(_workspace.RepoPath, "*.sln", SearchOption.AllDirectories);
            var csprojFiles = Directory.GetFiles(_workspace.RepoPath, "*.csproj", SearchOption.AllDirectories);
            string? buildTarget = slnFiles.Length > 0
                ? slnFiles[0]
                : (csprojFiles.Length > 0 ? csprojFiles[0] : null);

            if (buildTarget is not null)
            {
                Logger.LogInformation("Root build failed quickly — retrying with discovered project: {Target}", buildTarget);
                baseBuild = await _buildRunner.BuildAsync(
                    _workspace.RepoPath, $"dotnet build \"{buildTarget}\"", wsConfig.BuildTimeoutSeconds, ct);
            }
        }

        if (!baseBuild.Success)
        {
            var truncatedErrors = baseBuild.Errors.Length > 500
                ? baseBuild.Errors[..500] : baseBuild.Errors;
            Logger.LogWarning(
                "PR #{Number} base code doesn't build — skipping inline tests. Errors: {Errors}",
                pr.Number, truncatedErrors);

            var buildErrorSummary = baseBuild.ParsedErrors.Count > 0
                ? string.Join("\n", baseBuild.ParsedErrors.Take(10))
                : truncatedErrors;

            // Use the standard CHANGES REQUESTED pattern so GetPendingChangesRequestedAsync detects this on restart
            await _prWorkflow.RequestChangesAsync(pr.Number, "TestEngineer",
                "⚠️ Cannot add tests — the PR's code doesn't build.\n\n" +
                $"**Build errors:**\n```\n{buildErrorSummary}\n```\n\n" +
                "Please fix build errors first. The Test Engineer will retry when the PR is updated.", ct);

            // Notify the author engineer via bus so they wake up and fix the build
            await _messageBus.PublishAsync(new ChangesRequestedMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "*",
                MessageType = "ChangesRequested",
                PrNumber = pr.Number,
                PrTitle = pr.Title,
                ReviewerAgent = "TestEngineer",
                Feedback = $"Build failed — cannot add tests until build errors are fixed:\n{buildErrorSummary}"
            }, ct);

            // Return false — PR NOT tested, caller should NOT add to _testedPRs
            return false;
        }

        // --- Phase 3: Read source files locally (faster than API, no rate limit) ---
        LogActivity("testing", "📋 Reading source files from PR");
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
            return true; // Nothing to test — consider handled
        }

        // --- Phase 4: Generate test code via AI ---
        LogActivity("testing", "🤖 Calling AI to generate test code");
        UpdateStatus(AgentStatus.Working, $"Generating tests for PR #{pr.Number} ({sourceFiles.Count} files)");
        var existingTests = await DiscoverExistingTestsAsync(sourceFiles.Keys.ToList(), ct);
        var testOutput = await GenerateTestCodeAsync(pr, sourceFiles, existingTests, ct);

        if (string.IsNullOrWhiteSpace(testOutput))
        {
            Logger.LogWarning("AI returned empty test output for PR #{Number}", pr.Number);
            return true; // AI couldn't generate tests — consider handled
        }

        var testFiles = FilterToTestFilesOnly(CodeFileParser.ParseFiles(testOutput));
        if (testFiles.Count == 0)
        {
            Logger.LogWarning("No parseable test files from AI output for PR #{Number}", pr.Number);
            return true; // No test files parsed — consider handled
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
        LogActivity("testing", "🧪 Building generated test code");
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
            // Still apply tests-added label so PM can proceed with final review
            // (TE has attempted testing — the label signals "TE processing complete")
            await ApplyTestsAddedLabelAsync(pr, ct);
            return true; // Build failed but we reported it — consider handled
        }

        // --- Phase 7: Run tests by tier ---
        LogActivity("testing", "🧪 Running test suites");
        UpdateStatus(AgentStatus.Working, $"Running tests for PR #{pr.Number}");
        var tierResults = new List<TestResult>();

        if (!wsConfig.UITestsOnly)
        {
            // Unit tests (always run unless UITestsOnly)
            var unitResult = await RunTestTierWithRetryAsync(
                TestTier.Unit,
                wsConfig.UnitTestCommand ?? wsConfig.TestCommand,
                wsConfig.UnitTestTimeoutSeconds, wsConfig, ct, pr.Number);
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
                        wsConfig.IntegrationTestTimeoutSeconds, wsConfig, ct, pr.Number);
                    if (intResult is not null)
                        tierResults.Add(intResult);
                }
            }
        }
        else
        {
            Logger.LogInformation("UITestsOnly mode — skipping unit and integration tests for PR #{Number}", pr.Number);
        }

        // UI tests (only if prior tiers passed or UITestsOnly, enabled, and command configured)
        if ((wsConfig.UITestsOnly || tierResults.All(r => r.Success)) && wsConfig.EnableUITests)
        {
            var uiCommand = wsConfig.UITestCommand;
            if (!string.IsNullOrWhiteSpace(uiCommand) && _playwrightRunner is not null)
            {
                if (!_playwrightRunner.IsReady)
                {
                    Logger.LogWarning("TestEngineer: Playwright not ready ({Reason}), skipping UI tests for PR #{Number}",
                        _playwrightRunner.NotReadyReason, pr.Number);
                    tierResults.Add(new TestResult
                    {
                        Success = false,
                        Output = $"Playwright not ready: {_playwrightRunner.NotReadyReason}",
                        Passed = 0, Failed = 0, Skipped = 0,
                        Duration = TimeSpan.Zero,
                        Tier = TestTier.UI,
                        FailureDetails = [$"Playwright not ready: {_playwrightRunner.NotReadyReason}. Health service will retry automatically."]
                    });
                }
                else
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
        }

        // --- Phase 7.5: Escalate source bugs to PR author if found ---
        var sourceBugs = _lastClassifiedSourceBugs;
        _lastClassifiedSourceBugs = new(); // consume
        if (sourceBugs.Count > 0)
        {
            var maxRounds = _config.Limits?.MaxSourceBugRounds ?? 2;
            _pendingSourceFixPRs.TryGetValue(pr.Number, out var roundsSoFar);

            if (roundsSoFar < maxRounds)
            {
                Logger.LogInformation(
                    "TestEngineer: escalating {Count} source bug(s) on PR #{Number} (round {Round}/{Max})",
                    sourceBugs.Count, pr.Number, roundsSoFar + 1, maxRounds);

                _pendingSourceFixPRs[pr.Number] = roundsSoFar + 1;
                _pendingSourceBugDetails[pr.Number] = sourceBugs;

                // Commit & push the passing tests we DO have, then request changes
                if (testFiles.Count > 0)
                {
                    await _workspace.CommitAsync($"test: add passing tests for PR #{pr.Number} (source bugs escalated)", ct);
                    try { await _workspace.PushAsync(pr.HeadBranch, ct); }
                    catch (Exception pushEx)
                    {
                        Logger.LogWarning(pushEx, "Push failed during source bug escalation for PR #{Number}", pr.Number);
                    }
                }

                // === Gate: SourceBugEscalation — human reviews before escalating source bug ===
                await _gateCheck.WaitForGateAsync(
                    GateIds.SourceBugEscalation,
                    $"TE found source bugs in PR #{pr.Number}, requesting engineer fix",
                    pr.Number, ct: ct);

                await RequestSourceBugFixesAsync(pr, sourceBugs, ct);
                // Don't add tests-added label yet — waiting for engineer to fix source bugs
                _testedPRs.Remove(pr.Number); // Allow re-test after engineer fixes
                return false;
            }
            else
            {
                Logger.LogWarning(
                    "TestEngineer: source bug escalation exhausted ({Max} rounds) for PR #{Number} — removing failing tests",
                    maxRounds, pr.Number);
                _pendingSourceFixPRs.Remove(pr.Number);
                _pendingSourceBugDetails.Remove(pr.Number);
                // Fall through — commit whatever passing tests we have
            }
        }

        // --- Phase 8: Pre-push safety check — is the PR still open? ---
        var currentPr = await _github.GetPullRequestAsync(pr.Number, ct);
        if (currentPr is null || !string.Equals(currentPr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("PR #{Number} is no longer open (state: {State}), discarding test work",
                pr.Number, currentPr?.State ?? "null");
            await _workspace.RevertUncommittedChangesAsync(ct);
            return true; // PR closed — consider it handled
        }

        // --- Phase 9: Commit and push to the PR's branch ---
        LogActivity("testing", "📌 Pushing test results to PR branch");
        UpdateStatus(AgentStatus.Working, $"Pushing tests to PR #{pr.Number}");
        await _workspace.CommitAsync($"test: add tests for PR #{pr.Number}", ct);
        var pushed = true;

        try
        {
            await _workspace.PushAsync(pr.HeadBranch, ct);
        }
        catch (Exception pushEx)
        {
            // Push failed — the branch moved since we checked it out (PE pushed new commits).
            // Strategy: rebase our changes on top of the latest remote, then push.
            Logger.LogWarning(pushEx,
                "Push to {Branch} rejected for PR #{Number} — rebasing onto latest remote",
                pr.HeadBranch, pr.Number);

            try
            {
                var rebased = await _workspace.PullRebaseAsync(pr.HeadBranch, ct);
                if (rebased)
                {
                    // Rebase succeeded — our changes applied cleanly on top of PE's latest
                    await _workspace.PushAsync(pr.HeadBranch, ct);
                }
                else
                {
                    // Rebase conflict — nuclear option: nuke clone, re-clone fresh,
                    // re-apply our test files, and push cleanly.
                    Logger.LogWarning("Rebase conflict on PR #{Number} — using fresh clone fallback", pr.Number);
                    await _workspace.RevertUncommittedChangesAsync(ct);
                    pushed = await FreshCloneAndReapplyAsync(pr, testFiles, ct);
                    if (!pushed) return false;
                }
            }
            catch (Exception retryEx)
            {
                Logger.LogWarning(retryEx, "Rebase push also failed for PR #{Number} — trying fresh clone fallback", pr.Number);
                try
                {
                    pushed = await FreshCloneAndReapplyAsync(pr, testFiles, ct);
                    if (!pushed) return false;
                }
                catch (Exception nukeEx)
                {
                    Logger.LogError(nukeEx, "Fresh clone fallback also failed for PR #{Number}", pr.Number);
                    // Post test results even though push failed
                    await PostInlineTestResultsCommentAsync(pr, testFiles, tierResults, ct,
                        pushFailed: true);
                    _testedPRs.Remove(pr.Number);
                    return false;
                }
            }
        }

        // --- Phase 10: Post results FIRST, then apply label ---
        // Post comment before label so PM sees test results when it detects the label
        await PostInlineTestResultsCommentAsync(pr, testFiles, tierResults, ct);
        await ApplyTestsAddedLabelAsync(pr, ct);

        Logger.LogInformation(
            "Successfully added {Count} test files to PR #{Number}: {Title}",
            testFiles.Count, pr.Number, pr.Title);
        LogActivity("task",
            $"✅ Added {testFiles.Count} test files inline to PR #{pr.Number}: {pr.Title}");
        UpdateStatus(AgentStatus.Idle,
            $"Tests added to PR #{pr.Number} — awaiting PE merge");
        return true;

        } // end try
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "TestEngineer: Unexpected error adding tests to PR #{Number} — applying tests-added label to unblock pipeline",
                pr.Number);
            try
            {
                await _github.AddPullRequestCommentAsync(pr.Number,
                    $"❌ **Test Engineer:** Encountered an internal error while adding tests. " +
                    $"Error: {ex.GetType().Name}: {ex.Message}\n\n" +
                    "The PR can still be merged without automated tests.", ct);
                await ApplyTestsAddedLabelAsync(pr, ct);
            }
            catch (Exception labelEx)
            {
                Logger.LogError(labelEx,
                    "TestEngineer: Also failed to apply tests-added label for PR #{Number}", pr.Number);
            }
            return true; // Handled — don't leave PR stuck
        }
    }

    /// <summary>
    /// API-only fallback: commit test files directly via GitHub API when workspace is unavailable.
    /// No local build/test execution — tests are committed untested.
    /// </summary>
    private async Task AddInlineTestsViaApiAsync(
        AgentPullRequest pr, IReadOnlyList<string> codeFilePaths, CancellationToken ct)
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

        // Post comment before label so PM sees test results when it detects the label
        await PostInlineTestResultsCommentAsync(pr, testFiles, tierResults: null, ct);
        await ApplyTestsAddedLabelAsync(pr, ct);

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

            // Include project structure so AI knows real namespaces when fixing
            var projectCtx = "";
            try
            {
                projectCtx = DiscoverProjectStructure() ?? "";
                if (projectCtx.Length > 0)
                    projectCtx = "\n## Project Structure (Use ONLY These Real Namespaces)\n" + projectCtx;
            }
            catch { /* non-critical */ }

            var fixHistory = CreateChatHistory();

            var testFilesContext = string.Join("\n", testFiles.Select(f =>
                $"### {f.Path}\n```\n{(f.Content.Length > 1500 ? f.Content[..1500] + "\n// ... truncated" : f.Content)}\n```"));

            var fixSysMsg = _promptService is not null
                ? await _promptService.RenderAsync("test-engineer/build-fix-inline-system", new Dictionary<string, string>
                {
                    ["project_context"] = projectCtx
                }, ct)
                : null;
            fixSysMsg ??=
                "You are a test engineer fixing build errors in test files. " +
                "The test files were added to an existing PR branch and won't compile. " +
                "Fix ONLY the test code — do NOT modify the source code under test.\n" +
                "COMMON FIX: If errors are 'type or namespace not found', check the project structure below for REAL namespaces. " +
                "Do NOT invent namespaces — use ONLY those that actually exist in the project.\n" +
                "CRITICAL: All output files MUST be under tests/ directories. Do NOT output files under src/. " +
                "If model types exist in the source project, use 'using' directives — do NOT redefine them.\n" +
                "CRITICAL: If errors are 'already contains a definition' (CS0101) or 'multiple top-level statements' (CS8802), " +
                "you are creating duplicate types or entry points. REMOVE the duplicate files entirely — " +
                "use project references to access types from the source project instead of redefining them.\n" +
                "Also ensure the dependency manifest includes all required packages. Output the corrected manifest too.\n\n" +
                "Output ONLY corrected files using:\nFILE: path/to/file.ext\n```language\n<content>\n```";
            fixHistory.AddSystemMessage(fixSysMsg);

            var fixUserMsg = _promptService is not null
                ? await _promptService.RenderAsync("test-engineer/build-fix-inline-user", new Dictionary<string, string>
                {
                    ["attempt_number"] = (attempt + 1).ToString(),
                    ["max_retries"] = wsConfig.MaxBuildRetries.ToString(),
                    ["error_summary"] = errorSummary,
                    ["project_context"] = projectCtx,
                    ["test_files_context"] = testFilesContext
                }, ct)
                : null;
            fixUserMsg ??=
                $"Build attempt {attempt + 1}/{wsConfig.MaxBuildRetries} failed.\n\n" +
                $"## Build Errors\n\n{errorSummary}\n\n" +
                projectCtx +
                $"\n## Test Files\n\n" +
                testFilesContext +
                "\n\nFix ALL build errors. Only modify test files.";
            fixHistory.AddUserMessage(fixUserMsg);

            var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
            var fixedFiles = FilterToTestFilesOnly(CodeFileParser.ParseFiles(fixResponse.Content ?? ""));

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
        // === Gate: TestResults — human reviews test results before proceeding ===
        await _gateCheck.WaitForGateAsync(
            GateIds.TestResults,
            $"Tests completed on PR #{pr.Number}, results ready for human review",
            pr.Number, ct: ct);

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
    /// Nuclear fallback: delete local clone, re-clone fresh, re-apply test files, commit, push.
    /// Used when rebase fails due to conflicts with PE's changes.
    /// </summary>
    private async Task<bool> FreshCloneAndReapplyAsync(
        AgentPullRequest pr,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        CancellationToken ct)
    {
        Logger.LogWarning("PR #{Number}: Nuking clone and re-applying {Count} test files from scratch",
            pr.Number, testFiles.Count);

        await _workspace.NukeAndRecloneAsync(pr.HeadBranch, ct);

        // Re-write all test files onto the fresh checkout
        foreach (var file in testFiles)
            await _workspace.WriteFileAsync(file.Path, file.Content, ct);

        EnsureTestProjectExists(testFiles);
        await AddTestProjectsToSolutionAsync(testFiles, ct);

        await _workspace.CommitAsync($"test: add tests for PR #{pr.Number}", ct);

        try
        {
            await _workspace.PushAsync(pr.HeadBranch, ct);
            Logger.LogInformation("PR #{Number}: Fresh clone push succeeded", pr.Number);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PR #{Number}: Fresh clone push also failed — giving up", pr.Number);
            await _github.AddPullRequestCommentAsync(pr.Number,
                "⚠️ **Test Engineer:** Could not push tests after multiple retries (including fresh clone). " +
                "Will retry on next cycle.", ct);
            _testedPRs.Remove(pr.Number);
            return false;
        }
    }

    /// <summary>
    /// Post a detailed test results comment on the PR.
    /// </summary>
    private async Task PostInlineTestResultsCommentAsync(
        AgentPullRequest pr,
        IReadOnlyList<CodeFileParser.CodeFile> testFiles,
        IReadOnlyList<TestResult>? tierResults,
        CancellationToken ct,
        bool pushFailed = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 🧪 Test Engineer — Inline Tests Added");
        sb.AppendLine();

        if (pushFailed)
        {
            sb.AppendLine("⚠️ **Note:** Test files could not be pushed to this branch (branch diverged). " +
                "Tests will be retried on the next cycle. Results below are from the test run.");
            sb.AppendLine();
        }

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
            sb.AppendLine("⚠️ **WARNING: API-Only Mode — Tests NOT Verified**");
            sb.AppendLine();
            sb.AppendLine("Tests were committed via GitHub API only — they were **NOT built or executed**.");
            sb.AppendLine("No screenshots or test results are available. This typically happens when the");
            sb.AppendLine("test workspace failed to initialize (e.g., git clone timeout).");
            sb.AppendLine("**A reviewer should manually verify these tests compile and pass.**");
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
        bool hasUploadedScreenshots = false;
        if (tierResults is { Count: > 0 })
        {
            hasUploadedScreenshots = await UploadTestArtifactsToPrAsync(pr, tierResults, sb, ct);
        }

        // Fallback: if no screenshots were captured from tests, try a standalone screenshot
        if (!hasUploadedScreenshots && _playwrightRunner is not null && _workspace is not null
            && _config.Workspace.CaptureScreenshots)
        {
            try
            {
                // C3: prune any stale screenshots we previously generated ourselves
                // (standalone app-preview files only — leave any user/test fixture baselines alone)
                // so a stale preview from a prior PR merge cannot silently substitute for this PR's capture.
                try
                {
                    var screenshotDir = System.IO.Path.Combine(_workspace.RepoPath, "test-results", "screenshots");
                    if (System.IO.Directory.Exists(screenshotDir))
                    {
                        foreach (var stale in System.IO.Directory.GetFiles(screenshotDir, "pr-*-app-preview.png"))
                        {
                            var stem = System.IO.Path.GetFileNameWithoutExtension(stale) ?? string.Empty;
                            var m = System.Text.RegularExpressions.Regex.Match(stem, @"^pr-(?<n>\d+)-app-preview$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (m.Success && int.TryParse(m.Groups["n"].Value, out var n) && n != pr.Number)
                            {
                                try { System.IO.File.Delete(stale); Logger.LogDebug("C3: deleted stale screenshot {Path}", stale); }
                                catch (Exception delEx) { Logger.LogDebug(delEx, "C3: could not delete stale screenshot {Path}", stale); }
                            }
                        }
                    }
                }
                catch (Exception pruneEx)
                {
                    Logger.LogDebug(pruneEx, "C3: screenshot prune failed (non-fatal)");
                }

                Logger.LogInformation("No test screenshots found — attempting standalone screenshot capture for PR #{PrNumber}", pr.Number);
                LogActivity("screenshot", $"📸 Attempting standalone screenshot capture for PR #{pr.Number}");
                var screenshotBytes = await _playwrightRunner.CaptureAppScreenshotAsync(
                    _workspace.RepoPath, _config.Workspace, ct);

                if (screenshotBytes is { Length: > 0 })
                {
                    var fileName = $"pr-{pr.Number}-app-preview.png";
                    var repoPath = $"test-results/screenshots/{fileName}";
                    var imageUrl = await _github.CommitBinaryFileAsync(
                        repoPath, screenshotBytes,
                        $"📸 App screenshot for PR #{pr.Number}", pr.HeadBranch, ct);

                    if (imageUrl is not null)
                    {
                        sb.AppendLine("### 📸 App Preview");
                        sb.AppendLine();
                        sb.AppendLine($"![App Preview]({imageUrl})");
                        sb.AppendLine();
                        sb.AppendLine("_Standalone screenshot captured by Test Engineer_");
                        sb.AppendLine();
                        Logger.LogInformation("Posted standalone screenshot for PR #{PrNumber}", pr.Number);
                        LogActivity("screenshot", $"✅ Screenshot captured and uploaded for PR #{pr.Number} ({screenshotBytes.Length} bytes)");

                        // AI-describe the screenshot for dashboard visibility
                        try
                        {
                            var descKernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                            var descChat = descKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                            var img = new PullRequestWorkflow.ScreenshotImage(screenshotBytes, "image/png",
                                $"TE screenshot PR #{pr.Number}", imageUrl);
                            var desc = await PullRequestWorkflow.DescribeScreenshotAsync(img, descChat, ct);
                            LogActivity("screenshot", $"🖼️ TE screenshot content (PR #{pr.Number}): {desc}");
                            Logger.LogInformation("TE screenshot description for PR #{PrNumber}: {Description}",
                                pr.Number, desc);
                        }
                        catch (Exception descEx)
                        {
                            Logger.LogDebug(descEx, "Could not describe screenshot for PR #{PrNumber}", pr.Number);
                        }
                    }
                }
                else
                {
                    // C2: don't silently fall through. Post an explicit warning so PM review
                    // knows there's no actual screenshot (rather than relying on stale imagery).
                    Logger.LogWarning("Standalone screenshot capture returned no data for PR #{PrNumber}", pr.Number);
                    LogActivity("screenshot", $"⚠️ Screenshot capture returned no data for PR #{pr.Number}");
                    sb.AppendLine("### ⚠️ App Preview Unavailable");
                    sb.AppendLine();
                    sb.AppendLine($"Test Engineer attempted to capture a screenshot of the running application for PR #{pr.Number}, " +
                        "but the capture returned no data. This typically means the app failed to start, " +
                        "crashed before rendering, or the preview port was unreachable.");
                    sb.AppendLine();
                    sb.AppendLine("**PM review note:** treat this as evidence the PR does not render. " +
                        "Do not approve on the assumption that prior screenshots represent current state.");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Standalone screenshot capture failed for PR #{PrNumber}", pr.Number);
                LogActivity("screenshot", $"❌ Screenshot capture failed for PR #{pr.Number}: {ex.Message}");
            }
        }

        // === Gate: TestScreenshots — human reviews screenshots before proceeding ===
        await _gateCheck.WaitForGateAsync(
            GateIds.TestScreenshots,
            $"Screenshots captured for PR #{pr.Number}, ready for human review",
            pr.Number, ct: ct);

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
    /// Returns true if at least one screenshot was uploaded.
    /// </summary>
    private async Task<bool> UploadTestArtifactsToPrAsync(
        AgentPullRequest pr,
        IReadOnlyList<TestResult> tierResults,
        System.Text.StringBuilder sb,
        CancellationToken ct)
    {
        // C1: filter out stale app-preview screenshots named for a different PR number.
        // Only targets OUR OWN standalone file pattern pr-<N>-app-preview.png so we don't
        // accidentally drop user/test-fixture baseline files that happen to start with pr-.
        static bool IsStaleOtherPrScreenshot(string path, int currentPr)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var m = System.Text.RegularExpressions.Regex.Match(name,
                @"^pr-(?<n>\d+)-app-preview$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups["n"].Value, out var n) && n != currentPr;
        }

        var screenshots = tierResults
            .SelectMany(r => r.Artifacts.Screenshots)
            .Where(File.Exists)
            .Where(p => !IsStaleOtherPrScreenshot(p, pr.Number))
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
            return false;

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
        return screenshots.Count > 0;
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

                // Only count architect-approved PRs (Phase 1 complete, our responsibility)
                if (!pr.Labels.Contains(PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (pr.Labels.Contains(PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (pr.Labels.Contains(TestedLabel, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title) is { } agent &&
                    agent.Equals(Identity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Count any PR with changed files that hasn't been processed yet
                // (AI assessment determines testability, not file extensions)
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
        else if (untestedCodePRs > 0)
        {
            Logger.LogInformation("Coverage check: {Untested} untested code PRs remaining, {Tested} tested this session",
                untestedCodePRs, _sessionTestedPRs.Count);
        }
        else if (_sessionTestedPRs.Count == 0)
        {
            Logger.LogInformation("Coverage check: no untested PRs but also no PRs tested this session yet");
        }
    }

    /// <summary>
    /// Reads the actual source code from the merged PR's files on main,
    /// generates real test code via AI, and creates a test PR with those files.
    /// </summary>
    private async Task GenerateTestsForMergedPRAsync(
        AgentPullRequest pr, IReadOnlyList<string> codeFilePaths, CancellationToken ct)
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

        // Parse the AI output into code files — filter to test paths only
        var testFiles = FilterToTestFilesOnly(CodeFileParser.ParseFiles(testOutput));

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
                NeedsUITests = sourceFiles.Keys.Any(f =>
                    f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".vue", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".svelte", StringComparison.OrdinalIgnoreCase)),
                Rationale = "Fallback: no strategy analyzer available, UI detected from file extensions"
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

        var useSinglePass = _config.CopilotCli.SinglePassMode;

        // Use AI testability assessment if available (from ScanApprovedPRsForInlineTestingAsync)
        // This overrides both the heuristic TestStrategyAnalyzer and UITestsOnly config
        if (_lastTestabilityAssessment is not null)
        {
            strategy = strategy with
            {
                NeedsUnitTests = _lastTestabilityAssessment.NeedsUnitTests,
                NeedsIntegrationTests = _lastTestabilityAssessment.NeedsIntegrationTests,
                NeedsUITests = _lastTestabilityAssessment.NeedsUITests,
                Rationale = $"AI assessment: {_lastTestabilityAssessment.Rationale}"
            };
            Logger.LogInformation("Using AI testability assessment for PR #{Number}: Unit={Unit}, Integration={Integration}, UI={UI}",
                pr.Number, strategy.NeedsUnitTests, strategy.NeedsIntegrationTests, strategy.NeedsUITests);
            _lastTestabilityAssessment = null; // Consume it
        }
        else if (_config.Workspace.UITestsOnly)
        {
            // Legacy fallback: When UITestsOnly, override strategy to skip unit/integration generation
            strategy = strategy with { NeedsUnitTests = false, NeedsIntegrationTests = false, NeedsUITests = true };
            Logger.LogInformation("UITestsOnly mode — overriding strategy to UI tests only for PR #{Number}", pr.Number);
        }

        if (useSinglePass)
        {
            // Single-pass: combine all test tiers into one AI call
            var tiers = new List<string>();
            if (strategy.NeedsUnitTests) tiers.Add("Unit");
            if (strategy.NeedsIntegrationTests) tiers.Add("Integration");
            if (strategy.NeedsUITests && _config.Workspace.EnableUITests) tiers.Add("UI/E2E (Playwright)");

            UpdateStatus(AgentStatus.Working, $"Generating all tests ({string.Join("+", tiers)}) for PR #{pr.Number}");
            Logger.LogInformation("TE single-pass: generating {Tiers} tests in one prompt for PR #{Number}",
                string.Join(", ", tiers), pr.Number);

            var history = CreateChatHistory();

            // Build tier guidance for template or fallback
            var tierGuidanceBuilder = new System.Text.StringBuilder();
            tierGuidanceBuilder.AppendLine($"Generate ALL of the following test tiers in a single response:\n");
            if (strategy.NeedsUnitTests)
            {
                tierGuidanceBuilder.AppendLine("## Tier 1: UNIT TESTS");
                tierGuidanceBuilder.AppendLine("- Mock ALL external dependencies. Test one behavior per test method.");
                tierGuidanceBuilder.AppendLine("- Add [Trait(\"Category\", \"Unit\")] attribute. Place in tests/{ProjectName}.Tests/Unit/");
                tierGuidanceBuilder.AppendLine("- Cover happy paths, edge cases, null/empty inputs, boundary values, error conditions.\n");
            }
            if (strategy.NeedsIntegrationTests)
            {
                tierGuidanceBuilder.AppendLine("## Tier 2: INTEGRATION TESTS");
                tierGuidanceBuilder.AppendLine("- Test DI wiring, API endpoints, middleware. Use WebApplicationFactory.");
                tierGuidanceBuilder.AppendLine("- Add [Trait(\"Category\", \"Integration\")] attribute. Place in tests/{ProjectName}.Tests/Integration/");
                tierGuidanceBuilder.AppendLine("- Test error handling and validation at API boundaries.\n");
            }
            if (strategy.NeedsUITests && _config.Workspace.EnableUITests)
            {
                tierGuidanceBuilder.AppendLine("## Tier 3: UI/E2E TESTS (Playwright)");
                tierGuidanceBuilder.AppendLine("- Use Microsoft.Playwright for browser automation with Page Object Model.");
                tierGuidanceBuilder.AppendLine("- Base URL from env var BASE_URL (default: http://localhost:5000).");
                tierGuidanceBuilder.AppendLine("- IMPORTANT: Use xUnit ([Fact], [Collection], [Trait]) — do NOT use NUnit.");
                tierGuidanceBuilder.AppendLine("- PlaywrightFixture base class must use IAsyncLifetime, NOT [SetUpFixture].");
                tierGuidanceBuilder.AppendLine("- Add [Trait(\"Category\", \"UI\")] and [Collection(\"Playwright\")] attributes.");
                tierGuidanceBuilder.AppendLine("- Place in tests/{ProjectName}.UITests/. Include PlaywrightFixture class.");
                tierGuidanceBuilder.AppendLine("- CRITICAL SELECTOR RULES:");
                tierGuidanceBuilder.AppendLine("  * ONLY use CSS selectors/classes that appear in the Source Files provided below.");
                tierGuidanceBuilder.AppendLine("  * If you cannot find a selector in the source code, DO NOT USE IT.");
                tierGuidanceBuilder.AppendLine("  * Prefer content-based selectors: page.GetByText(), page.GetByRole(), page.Locator(\"h1\").");
                tierGuidanceBuilder.AppendLine("  * Use page.WaitForLoadStateAsync(LoadState.NetworkIdle) before asserting on elements.");
                tierGuidanceBuilder.AppendLine("  * NEVER invent CSS class names from spec/architecture documents.");
                tierGuidanceBuilder.AppendLine("- Set DefaultTimeout to 60000ms in PlaywrightFixture — apps need time for initial load.\n");
            }

            var blazorGuidanceForSinglePass = IsBlazorProject() ? GetBlazorTestGuidance() : "";

            var singlePassSysMsg = _promptService is not null
                ? await _promptService.RenderAsync("test-engineer/single-pass-system", new Dictionary<string, string>
                {
                    ["tech_stack"] = techStack,
                    ["tier_guidance"] = tierGuidanceBuilder.ToString(),
                    ["blazor_guidance"] = blazorGuidanceForSinglePass,
                    ["memory_context"] = memoryContext ?? ""
                }, ct)
                : null;

            if (singlePassSysMsg is not null)
            {
                history.AddSystemMessage(singlePassSysMsg);
            }
            else
            {
                // Fallback: build combined system prompt inline
                var combinedSystem = new System.Text.StringBuilder();
                combinedSystem.AppendLine($"You are an expert test engineer writing tests for a {techStack} project.");
                combinedSystem.AppendLine("Your job is to generate REAL, RUNNABLE test code — not documentation or test plans.");
                combinedSystem.AppendLine("Write actual test files that can be compiled and executed.\n");
                combinedSystem.AppendLine("CRITICAL RULE — DEPENDENCY MANAGEMENT:");
                combinedSystem.AppendLine("Before using ANY library, package, framework, or external dependency in your code, you MUST:");
                combinedSystem.AppendLine("1. Check the project's existing dependency manifest to see what is already installed");
                combinedSystem.AppendLine("2. If a dependency is NOT already listed, add it to the manifest file");
                combinedSystem.AppendLine("3. ALWAYS output the complete dependency manifest with ALL needed dependencies");
                combinedSystem.AppendLine("Missing dependencies are the #1 cause of build failures. Prevent this by always including the manifest.\n");
                combinedSystem.AppendLine("CRITICAL RULE — DO NOT CREATE DUPLICATE FILES:");
                combinedSystem.AppendLine("- NEVER create model classes, entity classes, DTOs, or data types that already exist in the source project.");
                combinedSystem.AppendLine("- NEVER create a Program.cs, Startup.cs, or application entry point in your test project.");
                combinedSystem.AppendLine("- Use project references or import/using statements to reference types from the source project.");
                combinedSystem.AppendLine("- If you need types from the source project, reference them — do NOT redefine them.\n");
                combinedSystem.AppendLine("CRITICAL RULE — ASSERTIONS MUST MATCH ACTUAL CODE:");
                combinedSystem.AppendLine("- Derive ALL expected values (text, counts, sizes, CSS classes) from the SOURCE CODE provided below.");
                combinedSystem.AppendLine("- Do NOT derive expected values from spec documents, architecture docs, or design references.");
                combinedSystem.AppendLine("- The spec describes intent; the source code is what actually runs. Test what the code DOES, not what the spec SAYS.\n");
                combinedSystem.AppendLine("Output each test file using this exact format:\n");
                combinedSystem.AppendLine("FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n");
                combinedSystem.AppendLine("Every file MUST use the FILE: marker format so it can be parsed and committed.\n");

                if (IsBlazorProject())
                    combinedSystem.AppendLine(GetBlazorTestGuidance());

                combinedSystem.AppendLine(tierGuidanceBuilder.ToString());
                combinedSystem.AppendLine("YOU MUST output .csproj files with all required package references.");

                if (!string.IsNullOrEmpty(memoryContext))
                    combinedSystem.AppendLine($"\n{memoryContext}");

                history.AddSystemMessage(combinedSystem.ToString());
            }

            var userPrompt = new System.Text.StringBuilder();
            userPrompt.AppendLine($"## Merged PR #{pr.Number}: {pr.Title}\n");
            userPrompt.AppendLine($"## PR Description\n{pr.Body}\n");
            userPrompt.AppendLine(businessContext);
            userPrompt.AppendLine(sourceContext.ToString());
            userPrompt.AppendLine(existingTestContext);

            if (strategy.NeedsUITests && _config.Workspace.EnableUITests && strategy.UITestScenarios.Count > 0)
            {
                userPrompt.AppendLine("## UI Test Scenarios to Cover");
                foreach (var scenario in strategy.UITestScenarios)
                    userPrompt.AppendLine($"- {scenario}");
                userPrompt.AppendLine();
            }

            if (strategy.NeedsUITests && _config.Workspace.EnableUITests)
            {
                var designCtx = await ReadDesignReferencesForTestsAsync(ct);
                if (!string.IsNullOrWhiteSpace(designCtx))
                {
                    userPrompt.AppendLine("## Visual Design Reference");
                    userPrompt.AppendLine(designCtx);
                    userPrompt.AppendLine();
                }
            }

            // Scale test count to PR complexity using configurable cap
            var totalSourceLines = sourceFiles.Values.Sum(c => c.Split('\n').Length);
            var fileCount = sourceFiles.Count;
            var maxTests = _config.Workspace.MaxTestsPerTier;

            userPrompt.AppendLine($"## Test Scope Guidance");
            userPrompt.AppendLine($"This PR has {fileCount} source file(s) with ~{totalSourceLines} lines.");
            if (maxTests > 0)
            {
                userPrompt.AppendLine($"Generate up to **{maxTests} test method(s)** per tier. Focus on the highest-value smoke tests that prove the feature works.");
                userPrompt.AppendLine($"Keep it focused — at most {maxTests} test method(s) per tier.\n");
            }
            else
            {
                userPrompt.AppendLine($"Generate comprehensive tests covering key behaviors and edge cases.\n");
            }
            userPrompt.AppendLine($"Generate {string.Join(", ", tiers)} tests for the above code using {techStack}.");
            userPrompt.AppendLine("Include ALL test tiers requested above in your response.");

            history.AddUserMessage(userPrompt.ToString());
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            allOutputs.AppendLine(response.Content?.Trim() ?? "");
        }
        else
        {
            // Multi-pass: separate AI call per tier
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
        var history = CreateChatHistory();
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
                userPrompt.AppendLine("## Visual Design Reference (informational only)");
                userPrompt.AppendLine("The following design files show the intended UI design. " +
                    "Use this for CONTEXT ONLY — do NOT derive expected values, class names, or element counts from this. " +
                    "All assertions must be based on the actual Source Files above, not this design reference.\n");
                userPrompt.AppendLine(designCtx);
                userPrompt.AppendLine();
            }
        }

        userPrompt.AppendLine(GetTierUserSuffix(tier, techStack, _config.Workspace.MaxTestsPerTier));
        history.AddUserMessage(userPrompt.ToString());

        var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return response.Content?.Trim() ?? "";
    }

    /// <summary>
    /// Get the system prompt for a specific test tier with appropriate guidance and examples.
    /// </summary>
    private string GetTierSystemPrompt(TestTier tier, string techStack, string memoryContext)
    {
        // Detect Blazor project and add specific guidance
        var blazorGuidance = IsBlazorProject() ? GetBlazorTestGuidance() : "";

        // Resolve tier-specific guidance from template or hardcoded fallback
        var tierTemplateName = tier switch
        {
            TestTier.Unit => "test-engineer/tier-unit-guidance",
            TestTier.Integration => "test-engineer/tier-integration-guidance",
            TestTier.UI => "test-engineer/tier-ui-guidance",
            _ => null
        };
        var tierGuidance = tierTemplateName is not null && _promptService is not null
            ? _promptService.RenderAsync(tierTemplateName, new Dictionary<string, string>(), default).GetAwaiter().GetResult()
            : null;

        tierGuidance ??= tier switch
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
                "- Include a shared PlaywrightFixture base class (use IAsyncLifetime)\n" +
                "- IMPORTANT: Use xUnit ([Fact], [Collection], [Trait]) — do NOT use NUnit ([Test], [SetUpFixture])\n" +
                "- CRITICAL SELECTOR RULES:\n" +
                "  * ONLY use CSS selectors/classes that you can see in the Source Files provided to you.\n" +
                "  * If a selector does not appear in the source code, DO NOT USE IT — it does not exist.\n" +
                "  * Prefer content-based selectors: page.GetByText(), page.GetByRole(), page.Locator(\"h1\")\n" +
                "  * Use page.WaitForLoadStateAsync(LoadState.NetworkIdle) before asserting on elements\n" +
                "  * NEVER invent CSS class names from spec/architecture documents — only use what's in the code\n" +
                "- Set BrowserNewContextOptions.DefaultTimeout to 60000ms — apps may need time for initial load\n" +
                "- Example Playwright test structure:\n" +
                "```csharp\n" +
                "// PlaywrightFixture.cs — shared base class\n" +
                "public class PlaywrightFixture : IAsyncLifetime\n{\n" +
                "    public IPlaywright Playwright { get; private set; }\n" +
                "    public IBrowser Browser { get; private set; }\n" +
                "    public string BaseUrl => Environment.GetEnvironmentVariable(\"BASE_URL\") ?? \"http://localhost:5000\";\n" +
                "    public async Task InitializeAsync() {\n" +
                "        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();\n" +
                "        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = true });\n" +
                "    }\n" +
                "    public async Task DisposeAsync() { await Browser.CloseAsync(); Playwright.Dispose(); }\n" +
                "    public async Task<IPage> NewPageAsync() {\n" +
                "        var page = await Browser.NewPageAsync();\n" +
                "        page.SetDefaultTimeout(60000);\n" +
                "        return page;\n" +
                "    }\n" +
                "}\n\n" +
                "// Tests — use xUnit [Fact], inject via IClassFixture\n" +
                "[Collection(\"Playwright\")]\n[Trait(\"Category\", \"UI\")]\n" +
                "public class HomePageTests : IClassFixture<PlaywrightFixture>\n{\n" +
                "    private readonly PlaywrightFixture _fixture;\n" +
                "    public HomePageTests(PlaywrightFixture fixture) => _fixture = fixture;\n\n" +
                "    [Fact]\n    public async Task HomePage_LoadsSuccessfully()\n    {\n" +
                "        var page = await _fixture.NewPageAsync();\n" +
                "        await page.GotoAsync(_fixture.BaseUrl);\n" +
                "        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);\n" +
                "        await Assertions.Expect(page).ToHaveTitleAsync(new Regex(\".*\"));\n" +
                "    }\n}\n```\n",

            _ => ""
        };

        // Try rendering the full tier-system-base template
        if (_promptService is not null)
        {
            var rendered = _promptService.RenderAsync("test-engineer/tier-system-base", new Dictionary<string, string>
            {
                ["tech_stack"] = techStack,
                ["blazor_guidance"] = blazorGuidance,
                ["tier_guidance"] = tierGuidance,
                ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : memoryContext
            }, default).GetAwaiter().GetResult();

            if (rendered is not null)
                return rendered;
        }

        // Fallback: build inline
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
            "CRITICAL RULE — DO NOT CREATE DUPLICATE FILES:\n" +
            "- NEVER create model classes, entity classes, DTOs, or data types that already exist in the source project.\n" +
            "- NEVER create a Program.cs, Startup.cs, main.py, index.ts, or any application entry point in your test project.\n" +
            "- Use project references or import statements to reference types from the source project.\n" +
            "- If you need types from the source project, reference them — do NOT redefine them.\n\n" +
            "CRITICAL RULE — ASSERTIONS MUST MATCH ACTUAL CODE:\n" +
            "- Derive ALL expected values (text content, counts, sizes, CSS classes, element names) from the SOURCE CODE provided.\n" +
            "- Do NOT derive expected values from spec documents, architecture docs, or design references.\n" +
            "- The spec describes intent; the source code is what actually runs. Test what the code DOES.\n\n" +
            "Output each test file using this exact format:\n\n" +
            "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
            "Every file MUST use the FILE: marker format so it can be parsed and committed.\n\n";

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

        if (_promptService is not null)
        {
            var rendered = _promptService.RenderAsync("test-engineer/blazor-test-guidance", new Dictionary<string, string>
            {
                ["project_name"] = projectName
            }, default).GetAwaiter().GetResult();

            if (rendered is not null)
                return rendered;
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
    /// <summary>
    /// Filters AI-generated files to only include paths under test directories.
    /// Prevents the AI from overwriting source files (src/) with test helper stubs
    /// that cause CS0101 "namespace already contains a definition" errors.
    /// </summary>
    private IReadOnlyList<CodeFileParser.CodeFile> FilterToTestFilesOnly(
        IReadOnlyList<CodeFileParser.CodeFile> files)
    {
        var filtered = new List<CodeFileParser.CodeFile>();
        foreach (var file in files)
        {
            var normalized = file.Path.Replace('\\', '/');

            // Allow files in test directories
            if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("Test", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(file);
                continue;
            }

            // Reject files that target source directories — AI should not modify source code
            if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/src/", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "Filtered out AI-generated file targeting source directory: {Path}", file.Path);
                continue;
            }

            // Allow root-level config files (.csproj, .sln, etc.) but block entry points
            var fileName = System.IO.Path.GetFileName(normalized);
            if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("index.js", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("main.py", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "Filtered out AI-generated entry-point file that would conflict with source: {Path}", file.Path);
                continue;
            }

            filtered.Add(file);
        }

        if (filtered.Count < files.Count)
        {
            Logger.LogInformation(
                "Filtered {Removed} non-test files from AI output ({Kept} kept)",
                files.Count - filtered.Count, filtered.Count);
        }

        return filtered;
    }

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

    private static string GetTierUserSuffix(TestTier tier, string techStack, int maxTestsPerTier = 5)
    {
        var count = maxTestsPerTier > 0 ? $"up to {maxTestsPerTier}" : "comprehensive";
        return tier switch
        {
            TestTier.Unit =>
                $"Generate {count} unit test method(s) in a single test file for the above source code using {techStack}. " +
                "Focus on the most important behaviors. Keep tests simple and focused.",

            TestTier.Integration =>
                $"Generate {count} integration test method(s) in a single test file for the above source code using {techStack}. " +
                "Test the most critical integration points. Keep tests simple.",

            TestTier.UI =>
                $"Generate {count} Playwright UI test method(s) in a single test file for the above source code using {techStack}. " +
                "Test that the main page loads and renders key content. Use headless mode. " +
                "IMPORTANT: Use xUnit ([Fact], IClassFixture<PlaywrightFixture>), NOT NUnit. " +
                "PlaywrightFixture must implement IAsyncLifetime. Include the PlaywrightFixture class with page.SetDefaultTimeout(60000). " +
                "CRITICAL: ONLY use CSS selectors that appear in the Source Files above. " +
                "Prefer page.GetByText(), page.GetByRole(), or semantic HTML selectors (h1, nav, main). " +
                "Use page.WaitForLoadStateAsync(LoadState.NetworkIdle) before assertions. " +
                "NEVER invent CSS class names — if you cannot find it in the source code, do not use it.",

            _ => $"Generate {count} test method(s) in a single test file for the above source code using {techStack}."
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

        // 4. Scan actual project structure so AI knows real namespaces, files, and organization
        try
        {
            var projectStructure = DiscoverProjectStructure();
            if (!string.IsNullOrWhiteSpace(projectStructure))
            {
                context.AppendLine("## Project Structure (IMPORTANT — Use These Real Namespaces)");
                context.AppendLine("The target project has the following actual files and namespaces.");
                context.AppendLine("You MUST use only namespaces and types that exist below.");
                context.AppendLine("Do NOT invent namespaces like 'ProjectName.Models' or 'ProjectName.Components' unless they appear here.");
                context.AppendLine("CRITICAL: ALL test files MUST be placed under tests/ directories. Do NOT create files under src/.");
                context.AppendLine("If you need model types, use 'using' directives to reference the existing source project — do NOT redefine them.\n");
                context.AppendLine(projectStructure);
                context.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not scan project structure for test context");
        }

        return context.ToString();
    }

    /// <summary>
    /// Scans the workspace to discover real project structure: file tree, namespaces, and .csproj contents.
    /// This prevents the AI from inventing non-existent namespaces and types.
    /// </summary>
    private string? DiscoverProjectStructure()
    {
        if (_workspace?.RepoPath is null) return null;
        var repoPath = _workspace.RepoPath;
        var sb = new System.Text.StringBuilder();

        // 1. Read main .csproj to get project name, target framework, and existing dependencies
        var csprojFiles = Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules"))
            .ToList();

        foreach (var csproj in csprojFiles.Take(5))
        {
            var relPath = Path.GetRelativePath(repoPath, csproj);
            try
            {
                var content = File.ReadAllText(csproj);
                sb.AppendLine($"### {relPath}");
                // Truncate very large csproj files
                var truncated = content.Length > 3000 ? content[..3000] + "\n<!-- truncated -->" : content;
                sb.AppendLine($"```xml\n{truncated}\n```\n");
            }
            catch { /* skip unreadable files */ }
        }

        // 2. List all source files with directory structure (compact tree)
        var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".razor", ".cshtml", ".tsx", ".ts", ".jsx", ".js", ".vue", ".svelte",
            ".py", ".rb", ".rs", ".go", ".java", ".kt", ".swift", ".json", ".css", ".html"
        };
        var allFiles = Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules")
                     && !f.Contains(".git") && !f.Contains(".playwright"))
            .Where(f => sourceExtensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(repoPath, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        if (allFiles.Count > 0)
        {
            sb.AppendLine("### File Tree");
            sb.AppendLine("```");
            foreach (var file in allFiles.Take(100)) // cap to prevent token explosion
                sb.AppendLine(file);
            if (allFiles.Count > 100)
                sb.AppendLine($"... and {allFiles.Count - 100} more files");
            sb.AppendLine("```\n");
        }

        // 3. Extract actual namespace declarations from .cs files
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        var csFiles = Directory.EnumerateFiles(repoPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules"))
            .ToList();

        foreach (var csFile in csFiles.Take(50))
        {
            try
            {
                // Read just first 30 lines to find namespace declaration
                using var reader = new StreamReader(csFile);
                for (var i = 0; i < 30 && !reader.EndOfStream; i++)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    var trimmed = line.Trim();
                    // Match both "namespace X;" (file-scoped) and "namespace X {" (block)
                    if (trimmed.StartsWith("namespace "))
                    {
                        var ns = trimmed.Replace("namespace ", "").TrimEnd(';', ' ', '{');
                        if (!string.IsNullOrWhiteSpace(ns) && !ns.Contains("//"))
                            namespaces.Add(ns);
                        break;
                    }
                }
            }
            catch { /* skip unreadable files */ }
        }

        if (namespaces.Count > 0)
        {
            sb.AppendLine("### Actual Namespaces in Project");
            sb.AppendLine("These are the REAL namespaces. Only use/reference these:");
            sb.AppendLine("```");
            foreach (var ns in namespaces)
                sb.AppendLine(ns);
            sb.AppendLine("```\n");
        }

        // 4. List Razor component names (important for Blazor test references)
        var razorFiles = allFiles.Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)).ToList();
        if (razorFiles.Count > 0)
        {
            sb.AppendLine("### Blazor Components");
            sb.AppendLine("```");
            foreach (var f in razorFiles)
                sb.AppendLine(f);
            sb.AppendLine("```\n");
        }

        var result = sb.ToString();
        if (result.Length > 0)
            Logger.LogInformation("Discovered project structure: {FileCount} files, {NsCount} namespaces, {RazorCount} Razor components",
                allFiles.Count, namespaces.Count, razorFiles.Count);

        return result.Length > 0 ? result : null;
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
            // Common code file extensions for test discovery
            var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rb", ".rs",
                ".razor", ".vue", ".svelte", ".php", ".swift", ".kt", ".scala"
            };

            var testFilePaths = repoTree
                .Where(f =>
                    (f.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                     f.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                     f.Contains("Tests/", StringComparison.OrdinalIgnoreCase)) &&
                    codeExtensions.Contains(Path.GetExtension(f)))
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
                var fixHistory = CreateChatHistory();

                var wsBuildFixSys = _promptService is not null
                    ? await _promptService.RenderAsync("test-engineer/workspace-build-fix-system", new Dictionary<string, string>
                    {
                        ["blazor_guidance"] = IsBlazorProject() ? GetBlazorTestGuidance() : ""
                    }, ct)
                    : null;
                wsBuildFixSys ??=
                    "You are a test engineer fixing build errors in test files. " +
                    "You have full context about the project structure.\n" +
                    "COMMON FIX: If errors are 'type or namespace not found', the dependency manifest is likely missing a " +
                    "package reference. Output a corrected manifest with the missing packages added.\n" +
                    "CRITICAL: If errors are 'already contains a definition' or 'multiple top-level statements', " +
                    "you created duplicate types or entry points that conflict with the source project. " +
                    "DELETE those duplicate files — use project references and import statements instead of redefining types.\n" +
                    (IsBlazorProject() ? GetBlazorTestGuidance() : "") +
                    "Output ONLY corrected files using:\nFILE: path/to/file.ext\n```language\n<content>\n```\n" +
                    "If a dependency manifest is missing or has wrong references, include the corrected one in your output.";
                fixHistory.AddSystemMessage(wsBuildFixSys);

                var testFileList = string.Join("\n", testFiles.Select(f => $"- {f.Path}"));
                var wsBuildFixUser = _promptService is not null
                    ? await _promptService.RenderAsync("test-engineer/workspace-build-fix-user", new Dictionary<string, string>
                    {
                        ["attempt_number"] = (attempt + 1).ToString(),
                        ["max_retries"] = wsConfig.MaxBuildRetries.ToString(),
                        ["error_summary"] = errorSummary,
                        ["test_file_list"] = testFileList
                    }, ct)
                    : null;
                wsBuildFixUser ??=
                    $"Build attempt {attempt + 1}/{wsConfig.MaxBuildRetries} failed.\n\n" +
                    $"## Build Errors\n\n{errorSummary}\n\n" +
                    $"## Test files currently written\n\n" +
                    testFileList + "\n\n" +
                    "Fix ALL errors. If duplicate type definitions exist, remove those files entirely and use project references. " +
                    "If namespace errors occur, check the actual project namespace and fix the import statements.";
                fixHistory.AddUserMessage(wsBuildFixUser);

                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixedFiles = FilterToTestFilesOnly(CodeFileParser.ParseFiles(fixResponse.Content ?? ""));
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
                wsConfig, ct, sourcePR.Number);
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
                    wsConfig, ct, sourcePR.Number);
                if (intResult is not null)
                    tierResults.Add(intResult);
            }

            // Tier 3: UI tests with Playwright (only if earlier tiers passed)
            var allPriorPassed = tierResults.All(r => r.Success);
            if (allPriorPassed && wsConfig.EnableUITests && _playwrightRunner is not null && wsConfig.UITestCommand is not null)
            {
                if (!_playwrightRunner.IsReady)
                {
                    Logger.LogWarning("TestEngineer: Playwright not ready ({Reason}), skipping UI tests",
                        _playwrightRunner.NotReadyReason);
                    tierResults.Add(new TestResult
                    {
                        Success = false,
                        Output = $"Playwright not ready: {_playwrightRunner.NotReadyReason}",
                        Passed = 0, Failed = 0, Skipped = 0,
                        Duration = TimeSpan.Zero,
                        Tier = TestTier.UI,
                        FailureDetails = [$"Playwright not ready: {_playwrightRunner.NotReadyReason}. Health service will retry automatically."]
                    });
                }
                else
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
            try
            {
                await _github.AddPullRequestCommentAsync(sourcePR.Number,
                    $"❌ **Test Build Blocked:** Test files for PR #{sourcePR.Number} could not be made to compile after " +
                    $"{wsConfig.MaxBuildRetries} fix attempts. Tests were not committed.", ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not add build-blocked comment to PR #{Number}", sourcePR.Number);
            }
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
        CancellationToken ct,
        int? prNumber = null)
    {
        UpdateStatus(AgentStatus.Working, prNumber.HasValue
            ? $"Running {tier} tests for PR #{prNumber.Value}"
            : $"Running {tier} tests");

        // Pass PLAYWRIGHT_BROWSERS_PATH so dotnet test child processes find Chromium
        var envVars = new Dictionary<string, string>
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = wsConfig.GetPlaywrightBrowsersPath()
        };

        var testResult = await _testRunner!.RunTestsAsync(
            _workspace!.RepoPath, testCommand, timeoutSeconds, ct, envVars);
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

                var fixHistory = CreateChatHistory();

                var testFixMsg = _promptService is not null
                    ? await _promptService.RenderAsync("test-engineer/test-fix-user", new Dictionary<string, string>
                    {
                        ["tier"] = tier.ToString(),
                        ["failed_count"] = testResult.Failed.ToString(),
                        ["total_count"] = testResult.Total.ToString(),
                        ["failure_summary"] = failureSummary
                    }, ct)
                    : null;
                testFixMsg ??=
                    $"{tier} tests failed ({testResult.Failed} of {testResult.Total}):\n\n{failureSummary}\n\n" +
                    "Fix the test code. Output ONLY corrected files using:\n" +
                    "FILE: path/to/file.ext\n```language\n<content>\n```\n\n" +
                    "Only fix test bugs — don't mask real code bugs.";
                fixHistory.AddUserMessage(testFixMsg);

                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixedFiles = FilterToTestFilesOnly(CodeFileParser.ParseFiles(fixResponse.Content ?? ""));
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
                    _workspace.RepoPath, testCommand, timeoutSeconds, ct, envVars);
                testResult = testResult with { Tier = tier };
            }

            // Last resort: if tests still fail after all retries, classify and handle
            if (!testResult.Success)
            {
                Logger.LogWarning("TestEngineer: {Tier} tests still failing after {Max} retries — classifying failures",
                    tier, wsConfig.MaxTestRetries);

                // Gather source files for classification context
                var sourceFiles = new Dictionary<string, string>();
                try
                {
                    var srcDir = Path.Combine(_workspace.RepoPath, "src");
                    if (Directory.Exists(srcDir))
                    {
                        var localCodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rb", ".rs",
                        ".razor", ".vue", ".svelte", ".php", ".swift", ".kt", ".scala"
                    };
                    foreach (var f in Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => localCodeExtensions.Contains(Path.GetExtension(f)))
                            .Take(10))
                        {
                            sourceFiles[Path.GetRelativePath(_workspace.RepoPath, f)] =
                                await File.ReadAllTextAsync(f, ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not read source files for failure classification");
                }

                var sourceBugs = await ClassifyTestFailuresAsync(testResult, sourceFiles, ct);
                if (sourceBugs.Count > 0)
                {
                    Logger.LogInformation(
                        "TestEngineer: classified {Count} source bug(s) in {Tier} tier",
                        sourceBugs.Count, tier);
                    // Store source bugs — the caller (inline pipeline) handles escalation
                    _lastClassifiedSourceBugs = sourceBugs;
                }

                // Remove failing tests (both test bugs AND source bugs that can't be fixed here)
                testResult = await RemoveFailingTestsForTierAsync(
                    tier, testResult, testCommand, timeoutSeconds, wsConfig, ct);
            }
        }

        return testResult;
    }

    /// <summary>
    /// Uses AI to classify each test failure as a test bug (wrong test code) or a source bug
    /// (real defect in the code under test). Returns a list of source bugs found.
    /// </summary>
    private async Task<List<SourceBugReport>> ClassifyTestFailuresAsync(
        TestResult failingResult,
        Dictionary<string, string> sourceFiles,
        CancellationToken ct)
    {
        var kernel = _modelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var failureSummary = failingResult.FailureDetails.Count > 0
            ? string.Join("\n\n", failingResult.FailureDetails.Take(15))
            : failingResult.Output.Length > 3000 ? failingResult.Output[^3000..] : failingResult.Output;

        var sourceContext = string.Join("\n\n", sourceFiles.Take(5).Select(kv =>
            $"### {kv.Key}\n```\n{(kv.Value.Length > 2000 ? kv.Value[..2000] : kv.Value)}\n```"));

        var history = CreateChatHistory();

        var classifySys = _promptService is not null
            ? await _promptService.RenderAsync("test-engineer/classify-failures-system", new Dictionary<string, string>(), ct)
            : null;
        classifySys ??=
            "You are an expert test engineer classifying test failures. " +
            "For each failing test, determine if the failure is caused by:\n" +
            "- TEST_BUG: The test itself is wrong (bad assertion, missing mock, wrong setup)\n" +
            "- SOURCE_BUG: The source code has a real defect (logic error, null reference, wrong return value)\n" +
            "- AMBIGUOUS: Cannot determine from available information\n\n" +
            "Output ONLY a JSON array with one object per failure:\n" +
            "[{\"test\": \"TestMethodName\", \"classification\": \"SOURCE_BUG\", " +
            "\"sourceFile\": \"path/to/file.cs\", \"sourceMethod\": \"MethodName\", " +
            "\"issue\": \"Brief description of the bug\", \"output\": \"Key error line\"}]\n\n" +
            "Be conservative — only classify as SOURCE_BUG when the test logic is clearly correct " +
            "and the source code clearly has a defect.";
        history.AddSystemMessage(classifySys);

        var classifyUser = _promptService is not null
            ? await _promptService.RenderAsync("test-engineer/classify-failures-user", new Dictionary<string, string>
            {
                ["failure_summary"] = failureSummary,
                ["source_context"] = sourceContext
            }, ct)
            : null;
        classifyUser ??= $"## Failing Tests\n{failureSummary}\n\n## Source Code Under Test\n{sourceContext}";
        history.AddUserMessage(classifyUser);

        try
        {
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var content = response.Content ?? "";

            // Extract JSON array from response
            var jsonStart = content.IndexOf('[');
            var jsonEnd = content.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return [];

            var json = content[jsonStart..(jsonEnd + 1)];
            var items = System.Text.Json.JsonSerializer.Deserialize<List<FailureClassificationItem>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null) return [];

            return items
                .Where(i => string.Equals(i.Classification, "SOURCE_BUG", StringComparison.OrdinalIgnoreCase))
                .Select(i => new SourceBugReport(
                    i.Test ?? "Unknown",
                    i.SourceFile ?? "Unknown",
                    i.SourceMethod ?? "Unknown",
                    i.Issue ?? "Unspecified",
                    i.Output ?? ""))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to classify test failures — treating all as test bugs");
            return [];
        }
    }

    /// <summary>DTO for JSON deserialization of failure classification.</summary>
    private sealed class FailureClassificationItem
    {
        public string? Test { get; set; }
        public string? Classification { get; set; }
        public string? SourceFile { get; set; }
        public string? SourceMethod { get; set; }
        public string? Issue { get; set; }
        public string? Output { get; set; }
    }

    /// <summary>
    /// Posts a structured source-bug report as a PR comment and requests changes
    /// from the PR author so they fix the underlying code defects.
    /// </summary>
    private async Task RequestSourceBugFixesAsync(
        AgentPullRequest pr, List<SourceBugReport> bugs, CancellationToken ct)
    {
        var bugList = string.Join("\n\n", bugs.Select((b, i) =>
            $"### {i + 1}. `{b.TestName}`\n" +
            $"**File:** `{b.SourceFile}` → `{b.SourceMethod}`\n" +
            $"**Issue:** {b.Issue}\n" +
            (string.IsNullOrWhiteSpace(b.TestOutput) ? "" : $"**Test output:** `{b.TestOutput}`")));

        var comment =
            $"🐛 **Test Engineer: Source Code Issues Found**\n\n" +
            $"{EngineerAgentBase.TeSourceBugMarker}\n\n" +
            $"The following tests expose potential bugs in the source code:\n\n" +
            $"{bugList}\n\n" +
            $"---\n" +
            $"Please fix these issues. The Test Engineer will re-run tests after your changes.\n" +
            $"Passing tests have been committed to this PR.";

        await _github.AddPullRequestCommentAsync(pr.Number, comment, ct);

        // Request changes via the PR workflow — this triggers the engineer's rework pipeline
        await _prWorkflow.RequestChangesAsync(
            pr.Number, Identity.DisplayName,
            $"{EngineerAgentBase.TeSourceBugMarker} {bugs.Count} source code bug(s) found by test failures. See PR comments for details.",
            ct);

        Logger.LogInformation(
            "TestEngineer: requested source fixes for {Count} bugs on PR #{Number}",
            bugs.Count, pr.Number);
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

        var removePrompt = _promptService is not null
            ? await _promptService.RenderAsync("test-engineer/remove-failing-tests", new Dictionary<string, string>
            {
                ["tier"] = tier.ToString(),
                ["max_retries"] = wsConfig.MaxTestRetries.ToString(),
                ["failure_summary"] = failureSummary
            }, ct)
            : null;

        removePrompt ??= $"""
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

        var history = CreateChatHistory();
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
            var envVarsForRetest = new Dictionary<string, string>
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = wsConfig.GetPlaywrightBrowsersPath()
            };
            var result = await _testRunner!.RunTestsAsync(
                _workspace.RepoPath, testCommand, timeoutSeconds, ct, envVarsForRetest);
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

                var history = CreateChatHistory();

                var reworkSysMsg = _promptService is not null
                    ? await _promptService.RenderAsync("test-engineer/rework-system", new Dictionary<string, string>
                    {
                        ["tech_stack"] = techStack
                    }, ct)
                    : null;
                reworkSysMsg ??=
                    $"You are an expert test engineer maintaining tests for a {techStack} project.\n" +
                    "A reviewer requested changes on your test PR. Update the test files to address all feedback.\n\n" +
                    "CRITICAL: Your response MUST start with a CHANGES SUMMARY that addresses EACH numbered " +
                    "feedback item from the reviewer using the SAME numbers (1. 2. 3.). For each item, state " +
                    "in one sentence what you changed or why no change was needed.\n\n" +
                    "After the CHANGES SUMMARY, output each corrected file using this exact format:\n" +
                    "FILE: tests/path/to/TestFile.ext\n```language\n<complete file content>\n```\n\n" +
                    "Include the COMPLETE content of each changed file. " +
                    "You MUST include at least one FILE: block — a summary alone is not sufficient.";
                history.AddSystemMessage(reworkSysMsg);

                var currentFilesBlock = string.IsNullOrEmpty(currentFilesContext) ? "" :
                    $"## Current Files on PR Branch\nThese are the files you already wrote. " +
                    "Modify them to address the feedback below:\n" + currentFilesContext + "\n\n";
                var reworkUserMsg = _promptService is not null
                    ? await _promptService.RenderAsync("test-engineer/rework-user", new Dictionary<string, string>
                    {
                        ["pr_number"] = rework.PrNumber.ToString(),
                        ["pr_title"] = rework.PrTitle,
                        ["pr_description"] = pr.Body ?? "",
                        ["current_files_context"] = currentFilesBlock,
                        ["reviewer"] = rework.Reviewer,
                        ["feedback"] = rework.Feedback
                    }, ct)
                    : null;
                reworkUserMsg ??=
                    $"## Test PR #{rework.PrNumber}: {rework.PrTitle}\n\n" +
                    $"## Original PR Description\n{pr.Body}\n\n" +
                    currentFilesBlock +
                    $"## Review Feedback from {rework.Reviewer}\n{rework.Feedback}\n\n" +
                    "REQUIRED: Start your response with CHANGES SUMMARY that addresses each numbered " +
                    "feedback item using the SAME numbers. Example:\n" +
                    "CHANGES SUMMARY\n" +
                    "1. Added missing error handling test as requested\n" +
                    "2. Fixed assertion to check return type\n\n" +
                    "Then output the corrected test files.";
                history.AddUserMessage(reworkUserMsg);

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
                    if (!await _prWorkflow.NeedsReviewFromAsync(pr.Number, "SoftwareEngineer", ct))
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

            // Persist to DB so session survives runner restarts
            if (_stateStore is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _stateStore.SaveCliSessionAsync(Identity.Id, prNumber, sessionId); }
                    catch (Exception ex) { Logger.LogWarning(ex, "Failed to persist CLI session for test PR #{Pr}", prNumber); }
                });
            }
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
