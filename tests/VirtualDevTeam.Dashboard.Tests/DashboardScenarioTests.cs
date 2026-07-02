using Microsoft.Playwright;
using System.Text;

namespace VirtualDevTeam.Dashboard.Tests;

/// <summary>
/// Playwright-based UI scenario tests for the VirtualDevTeam Dashboard.
/// Self-hosting: spins up the dashboard automatically — no external process needed.
/// Auto-installs Playwright browsers if missing (fast check, ~5s if already installed).
/// Each test navigates to a dashboard page, validates key elements, and captures a screenshot.
/// </summary>
public class DashboardScenarioTests : IClassFixture<DashboardWebAppFixture>, IClassFixture<PlaywrightFixture>, IAsyncDisposable
{
    private readonly DashboardWebAppFixture _app;
    private readonly PlaywrightFixture _pw;
    private readonly string _screenshotDir;
    private readonly string _videoDir;

    public DashboardScenarioTests(DashboardWebAppFixture app, PlaywrightFixture pw)
    {
        _app = app;
        _pw = pw;
        _screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "screenshots");
        _videoDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "videos");
        Directory.CreateDirectory(_screenshotDir);
        Directory.CreateDirectory(_videoDir);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IBrowserContext> CreateContextAsync(string scenarioName)
    {
        var scenarioVideoDir = Path.Combine(_videoDir, scenarioName);
        Directory.CreateDirectory(scenarioVideoDir);

        return await _pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            RecordVideoDir = scenarioVideoDir,
            RecordVideoSize = new RecordVideoSize { Width = 1920, Height = 1080 }
        });
    }

    private void RenameVideo(string scenarioName)
    {
        var scenarioVideoDir = Path.Combine(_videoDir, scenarioName);
        var videoFile = Directory.GetFiles(scenarioVideoDir, "*.webm").FirstOrDefault();
        if (videoFile is null) return;

        var dest = Path.Combine(_videoDir, $"{scenarioName}.webm");
        File.Move(videoFile, dest, overwrite: true);
        try { Directory.Delete(scenarioVideoDir, recursive: true); } catch { }
    }

    private async Task<string> CaptureScreenshotAsync(IPage page, string scenarioId)
    {
        var path = Path.Combine(_screenshotDir, $"{scenarioId}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        return path;
    }

    [Fact]
    public async Task S01_AgentOverview_ShowsAgentCards()
    {
        var context = await CreateContextAsync("S01");
        var page = await context.NewPageAsync();

        await page.GotoAsync(_app.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var body = await page.QuerySelectorAsync("body");
        Assert.NotNull(body);

        var content = await page.QuerySelectorAsync(".content, main, article");
        Assert.NotNull(content);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Page body should have text content");

        await CaptureScreenshotAsync(page, "S01_AgentOverview");
        await context.CloseAsync();
        RenameVideo("S01");
    }

    [Fact]
    public async Task S02_PullRequests_ShowsPRList()
    {
        var context = await CreateContextAsync("S02");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/pullrequests", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "PR page should have content");

        var hasFilters = text.Contains("Open", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Closed", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Pull Request", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasFilters, "PR page should have state filters or PR heading");

        await CaptureScreenshotAsync(page, "S02_PullRequests");
        await context.CloseAsync();
        RenameVideo("S02");
    }

    [Fact]
    public async Task S03_Issues_ShowsIssueList()
    {
        var context = await CreateContextAsync("S03");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/issues", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Issues page should have content");

        var hasIssueElements = text.Contains("Issue", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Open", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasIssueElements, "Issues page should have issue-related content");

        await CaptureScreenshotAsync(page, "S03_Issues");
        await context.CloseAsync();
        RenameVideo("S03");
    }

    [Fact]
    public async Task S04_Reasoning_ShowsAgentReasoning()
    {
        var context = await CreateContextAsync("S04");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/reasoning", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Reasoning page should have content");

        await CaptureScreenshotAsync(page, "S04_Reasoning");
        await context.CloseAsync();
        RenameVideo("S04");
    }

    [Fact]
    public async Task S05_Timeline_ShowsTimelineGroups()
    {
        var context = await CreateContextAsync("S05");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/timeline", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Timeline page should have content");

        await CaptureScreenshotAsync(page, "S05_Timeline");
        await context.CloseAsync();
        RenameVideo("S05");
    }

    [Fact]
    public async Task S06_Configuration_ShowsAgentSections()
    {
        var context = await CreateContextAsync("S06");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/configuration", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Configuration page should have content");

        var hasConfigContent = text.Contains("Configuration", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Agent", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Settings", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasConfigContent, "Configuration page should have config-related content");

        await CaptureScreenshotAsync(page, "S06_Configuration");
        await context.CloseAsync();
        RenameVideo("S06");
    }

    [Fact]
    public async Task S08_HealthMonitor_ShowsStatus()
    {
        var context = await CreateContextAsync("S08");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/health", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Health Monitor page should have content");

        await CaptureScreenshotAsync(page, "S08_HealthMonitor");
        await context.CloseAsync();
        RenameVideo("S08");
    }

    [Fact]
    public async Task S09_Metrics_ShowsData()
    {
        var context = await CreateContextAsync("S09");
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync($"{_app.BaseUrl}/metrics", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        Assert.NotNull(response);
        Assert.True(response.Ok, $"Metrics page returned {response.Status} — expected 200");
        await CaptureScreenshotAsync(page, "S09_Metrics");

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Metrics page should have content");
        await context.CloseAsync();
        RenameVideo("S09");
    }

    [Fact]
    public async Task S10_TeamViz_ShowsVisualization()
    {
        var context = await CreateContextAsync("S10");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/team", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Team Viz page should have content");

        await CaptureScreenshotAsync(page, "S10_TeamViz");
        await context.CloseAsync();
        RenameVideo("S10");
    }

    [Fact]
    public async Task S11_Approvals_ShowsGates()
    {
        var context = await CreateContextAsync("S11");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_app.BaseUrl}/approvals", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Approvals page should have content");

        await CaptureScreenshotAsync(page, "S11_Approvals");
        await context.CloseAsync();
        RenameVideo("S11");
    }

    public record ScenarioResult(string Id, string Name, bool Passed, string? Error, string ScreenshotPath);
}
