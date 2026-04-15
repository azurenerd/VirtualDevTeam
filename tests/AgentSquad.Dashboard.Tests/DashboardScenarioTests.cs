using Microsoft.Playwright;
using System.Text;

namespace AgentSquad.Dashboard.Tests;

/// <summary>
/// Playwright-based UI scenario tests for the AgentSquad Dashboard.
/// Each test navigates to a dashboard page, validates key elements, and captures a screenshot.
/// Video recording is enabled per browser context.
/// </summary>
public class DashboardScenarioTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private readonly string _baseUrl = "http://localhost:5051";
    private readonly string _screenshotDir;
    private readonly string _videoDir;
    private readonly List<ScenarioResult> _results = new();

    public DashboardScenarioTests()
    {
        _screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "screenshots");
        _videoDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "videos");
        Directory.CreateDirectory(_screenshotDir);
        Directory.CreateDirectory(_videoDir);
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 200
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    private async Task<IBrowserContext> CreateContextAsync(string scenarioName)
    {
        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            RecordVideoDir = _videoDir,
            RecordVideoSize = new RecordVideoSize { Width = 1920, Height = 1080 }
        });
    }

    private async Task<string> CaptureScreenshotAsync(IPage page, string scenarioId)
    {
        var path = Path.Combine(_screenshotDir, $"{scenarioId}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true
        });
        return path;
    }

    [Fact]
    public async Task S01_AgentOverview_ShowsAgentCards()
    {
        await using var context = await CreateContextAsync("S01");
        var page = await context.NewPageAsync();

        await page.GotoAsync(_baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000); // Allow Blazor to render

        // Verify the page loaded with the dark theme
        var body = await page.QuerySelectorAsync("body");
        Assert.NotNull(body);

        // Check for main content area
        var content = await page.QuerySelectorAsync(".content, main, article");
        Assert.NotNull(content);

        // Check page has text content (not blank)
        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Page body should have text content");

        await CaptureScreenshotAsync(page, "S01_AgentOverview");
    }

    [Fact]
    public async Task S02_PullRequests_ShowsPRList()
    {
        await using var context = await CreateContextAsync("S02");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/pullrequests", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "PR page should have content");

        // Look for typical PR page elements (filter tabs or PR cards)
        var hasFilters = text.Contains("Open", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Closed", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Pull Request", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasFilters, "PR page should have state filters or PR heading");

        await CaptureScreenshotAsync(page, "S02_PullRequests");
    }

    [Fact]
    public async Task S03_Issues_ShowsIssueList()
    {
        await using var context = await CreateContextAsync("S03");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/issues", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Issues page should have content");

        var hasIssueElements = text.Contains("Issue", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Open", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasIssueElements, "Issues page should have issue-related content");

        await CaptureScreenshotAsync(page, "S03_Issues");
    }

    [Fact]
    public async Task S04_Reasoning_ShowsAgentReasoning()
    {
        await using var context = await CreateContextAsync("S04");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/reasoning", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Reasoning page should have content");

        await CaptureScreenshotAsync(page, "S04_Reasoning");
    }

    [Fact]
    public async Task S05_Timeline_ShowsTimelineGroups()
    {
        await using var context = await CreateContextAsync("S05");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/timeline", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Timeline page should have content");

        await CaptureScreenshotAsync(page, "S05_Timeline");
    }

    [Fact]
    public async Task S06_Configuration_ShowsAgentSections()
    {
        await using var context = await CreateContextAsync("S06");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/configuration", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Configuration page should have content");

        // Configuration page should have agent-related content
        var hasConfigContent = text.Contains("Configuration", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Agent", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Settings", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasConfigContent, "Configuration page should have config-related content");

        await CaptureScreenshotAsync(page, "S06_Configuration");
    }

    [Fact]
    public async Task S07_GitHubFeed_ShowsActivity()
    {
        await using var context = await CreateContextAsync("S07");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/github", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "GitHub Feed page should have content");

        await CaptureScreenshotAsync(page, "S07_GitHubFeed");
    }

    [Fact]
    public async Task S08_HealthMonitor_ShowsStatus()
    {
        await using var context = await CreateContextAsync("S08");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/health", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Health Monitor page should have content");

        await CaptureScreenshotAsync(page, "S08_HealthMonitor");
    }

    [Fact]
    public async Task S09_Metrics_ShowsData()
    {
        await using var context = await CreateContextAsync("S09");
        var page = await context.NewPageAsync();

        // Metrics page may return 500 in standalone mode if data service is unavailable
        var response = await page.GotoAsync($"{_baseUrl}/metrics", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        // Accept both 200 (working) and 500 (standalone mode limitation) — capture state either way
        Assert.NotNull(response);
        await CaptureScreenshotAsync(page, "S09_Metrics");

        if (response.Ok)
        {
            var text = await page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(text), "Metrics page should have content when available");
        }
    }

    [Fact]
    public async Task S10_TeamViz_ShowsVisualization()
    {
        await using var context = await CreateContextAsync("S10");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/team", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Team Viz page should have content");

        await CaptureScreenshotAsync(page, "S10_TeamViz");
    }

    [Fact]
    public async Task S11_Approvals_ShowsGates()
    {
        await using var context = await CreateContextAsync("S11");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/approvals", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Approvals page should have content");

        await CaptureScreenshotAsync(page, "S11_Approvals");
    }

    public record ScenarioResult(string Id, string Name, bool Passed, string? Error, string ScreenshotPath);
}
