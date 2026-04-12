namespace AgentSquad.Core.Workspace;

/// <summary>
/// Configuration for agent local workspaces — enables real build and test
/// verification before code is committed to GitHub.
/// When RootPath is null/empty, agents fall back to GitHub API-only mode.
/// </summary>
public class WorkspaceConfig
{
    /// <summary>
    /// Root directory for agent workspaces. Each agent gets a subdirectory.
    /// Example: "C:/AgentSquadWorkspaces" → "C:/AgentSquadWorkspaces/PrincipalEngineer/{repo}"
    /// When null or empty, local workspace is disabled and agents use GitHub API-only mode.
    /// </summary>
    public string? RootPath { get; set; } = @"C:\Agents";

    /// <summary>
    /// Command to build the project. Executed from the repo root directory.
    /// Examples: "dotnet build", "npm run build", "cargo build"
    /// </summary>
    public string BuildCommand { get; set; } = "dotnet build";

    /// <summary>
    /// Command to run ALL tests (fallback when tier-specific commands are not configured).
    /// Examples: "dotnet test --no-build --verbosity normal", "npm test", "cargo test"
    /// </summary>
    public string TestCommand { get; set; } = "dotnet test --verbosity normal";

    #region Multi-Tier Test Commands

    /// <summary>
    /// Command to run unit tests only. Uses test category/trait filters.
    /// When null, falls back to <see cref="TestCommand"/>.
    /// </summary>
    public string? UnitTestCommand { get; set; }

    /// <summary>
    /// Command to run integration tests only.
    /// When null, skipped (unit tests cover via fallback TestCommand).
    /// </summary>
    public string? IntegrationTestCommand { get; set; }

    /// <summary>
    /// Command to run UI/E2E tests (Playwright) only.
    /// When null, UI tests use the standard test runner with a Category=UI filter.
    /// </summary>
    public string? UITestCommand { get; set; }

    /// <summary>Timeout for unit tests in seconds.</summary>
    public int UnitTestTimeoutSeconds { get; set; } = 60;

    /// <summary>Timeout for integration tests in seconds.</summary>
    public int IntegrationTestTimeoutSeconds { get; set; } = 180;

    /// <summary>Timeout for UI/E2E tests in seconds.</summary>
    public int UITestTimeoutSeconds { get; set; } = 300;

    #endregion

    #region Playwright / UI Testing

    /// <summary>
    /// Whether UI/E2E tests with Playwright are enabled.
    /// When false, the Test Engineer skips UI test generation even for UI-related PRs.
    /// </summary>
    public bool EnableUITests { get; set; } = true;

    /// <summary>
    /// When true, skip unit and integration test generation/execution — only run UI tests.
    /// Useful for debugging the UI test pipeline in isolation.
    /// </summary>
    public bool UITestsOnly { get; set; }

    /// <summary>
    /// Path to cache Playwright browser binaries. Shared across all agent workspaces
    /// to avoid re-downloading ~150MB per agent. Set via PLAYWRIGHT_BROWSERS_PATH env var.
    /// When null, auto-derived as "{RootPath}/.playwright-browsers".
    /// </summary>
    public string? PlaywrightBrowsersCachePath { get; set; }

    /// <summary>
    /// Whether Playwright runs in headless mode (no visible browser window).
    /// MUST be true for agent execution — set to false only for local debugging.
    /// </summary>
    public bool PlaywrightHeadless { get; set; } = true;

    /// <summary>
    /// Command to start the application under test for UI tests.
    /// Examples: "dotnet run --urls http://localhost:5000", "npm run dev"
    /// When null, auto-derived from tech stack at runtime.
    /// </summary>
    public string? AppStartCommand { get; set; }

    /// <summary>
    /// Base URL of the application under test. Playwright tests navigate relative to this.
    /// Each agent uses a unique port derived from its agent ID to prevent conflicts.
    /// </summary>
    public string AppBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// Seconds to wait for the app under test to become ready (HTTP 200) before timing out.
    /// </summary>
    public int AppStartupTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Resolved path for Playwright browser cache. Uses explicit config or auto-derives from RootPath.
    /// </summary>
    public string GetPlaywrightBrowsersPath()
    {
        if (!string.IsNullOrWhiteSpace(PlaywrightBrowsersCachePath))
            return PlaywrightBrowsersCachePath;

        // Use standard Playwright browser cache location (where 'playwright install' puts them)
        // but verify the actual executable exists, not just the directory
        var standardPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");
        if (PlaywrightRunner.IsBrowserExecutablePresent(standardPath))
            return standardPath;

        // Fallback to workspace-local path
        return Path.Combine(RootPath ?? @"C:\Agents", ".playwright-browsers");
    }

    #endregion

    /// <summary>
    /// Maximum seconds to wait for a build to complete before killing the process.
    /// </summary>
    public int BuildTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum seconds to wait for tests to complete before killing the process.
    /// Used as fallback when tier-specific timeouts are not set.
    /// </summary>
    public int TestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum attempts to fix build errors by feeding them back to the AI.
    /// After this many failures + a full code regeneration attempt, the step is skipped entirely.
    /// No broken builds are ever committed.
    /// </summary>
    public int MaxBuildRetries { get; set; } = 5;

    /// <summary>
    /// Maximum attempts to fix test failures by feeding them back to the AI.
    /// After this many failures, failing tests are removed with a documented note,
    /// and the buildable/passing code is committed.
    /// </summary>
    public int MaxTestRetries { get; set; } = 3;

    /// <summary>
    /// Maximum number of test methods to generate per tier (unit, integration, UI).
    /// Controls AI test generation scope. Lower values = faster TE cycles.
    /// Set to 0 for no limit (AI decides). Recommended: 3-5 for fast iterations.
    /// </summary>
    public int MaxTestsPerTier { get; set; } = 5;

    /// <summary>
    /// Whether to delete agent workspaces when the project is complete (all issues closed).
    /// </summary>
    public bool CleanupOnProjectComplete { get; set; } = true;

    /// <summary>
    /// Whether to capture a UI screenshot after each successful build+commit and post it
    /// as a PR comment. Requires Playwright browsers installed and AppStartCommand configured.
    /// </summary>
    public bool CaptureScreenshots { get; set; } = true;

    /// <summary>
    /// Seconds to wait for the page to fully render before capturing a screenshot.
    /// Allows JavaScript frameworks (Blazor, React) time to hydrate.
    /// </summary>
    public int ScreenshotRenderDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Whether to capture Playwright video recordings during UI test execution.
    /// Videos are stored in the test-results directory and posted to PR comments.
    /// </summary>
    public bool RecordTestVideos { get; set; } = true;

    /// <summary>
    /// Whether to capture Playwright execution traces during UI test execution.
    /// Traces include screenshots, DOM snapshots, network requests, and console logs.
    /// Can be viewed at https://trace.playwright.dev
    /// </summary>
    public bool RecordTestTraces { get; set; } = true;

    /// <summary>
    /// Directory name (relative to workspace) for Playwright test output artifacts
    /// (videos, traces, screenshots). Cleaned between runs.
    /// </summary>
    public string TestResultsDir { get; set; } = "test-results";

    /// <summary>
    /// Whether local workspace mode is enabled (RootPath is configured).
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>
    /// Test workflow mode: "inline" pushes test commits to the original PR's branch
    /// (pre-merge), "separate-pr" creates a dedicated test PR (post-merge, legacy).
    /// Default is "inline" for a single-PR workflow.
    /// </summary>
    public string TestWorkflow { get; set; } = "inline";

    /// <summary>Whether the TestWorkflow is set to inline mode.</summary>
    public bool IsInlineTestWorkflow =>
        string.Equals(TestWorkflow, "inline", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Optional .gitconfig overrides for cloned repos (e.g., user.name, user.email).
    /// Applied via git config after clone.
    /// </summary>
    public string GitUserName { get; set; } = "AgentSquad";

    /// <summary>
    /// Git email for commits in local workspaces.
    /// </summary>
    public string GitUserEmail { get; set; } = "agentsquad@noreply.github.com";
}
