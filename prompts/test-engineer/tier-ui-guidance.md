---
version: "1.0"
description: "UI/E2E Playwright test tier guidance appended to system prompt"
variables: []
tags:
  - test-engineer
  - tier
  - ui
  - playwright
---
## Test Tier: UI/E2E TESTS (Playwright)
Focus on testing user-facing behavior through browser automation.
Guidelines:
- Use Microsoft.Playwright for browser automation
- Use the Page Object Model pattern: create a page object class for each page/component
- Tests run HEADLESS (no visible browser) — use environment variable HEADED to control
- Base URL comes from environment variable BASE_URL (default: http://localhost:5000)
- Add [Trait("Category", "UI")] and [Collection("Playwright")] attributes
- Place files in tests/{ProjectName}.UITests/ directory
- Test user workflows: navigation, form submission, button clicks, data display
- Include assertions on page content, element visibility, and navigation outcomes
- Capture screenshots on failure using PlaywrightFixture.CaptureScreenshotAsync
- Include a shared PlaywrightFixture base class (use IAsyncLifetime)
- IMPORTANT: Use xUnit ([Fact], [Collection], [Trait]) — do NOT use NUnit ([Test], [SetUpFixture])
- CRITICAL SELECTOR RULES:
  * ONLY use CSS selectors/classes that you can see in the Source Files provided to you.
  * If a selector does not appear in the source code, DO NOT USE IT — it does not exist.
  * Prefer content-based selectors: page.GetByText(), page.GetByRole(), page.Locator("h1")
  * Use page.WaitForLoadStateAsync(LoadState.NetworkIdle) before asserting on elements
  * NEVER invent CSS class names from spec/architecture documents — only use what's in the code
- Set BrowserNewContextOptions.DefaultTimeout to 60000ms — apps may need time for initial load
- Example Playwright test structure:
```csharp
// PlaywrightFixture.cs — shared base class
public class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; }
    public IBrowser Browser { get; private set; }
    public string BaseUrl => Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:5000";
    public async Task InitializeAsync() {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = true });
    }
    public async Task DisposeAsync() { await Browser.CloseAsync(); Playwright.Dispose(); }
    public async Task<IPage> NewPageAsync() {
        var page = await Browser.NewPageAsync();
        page.SetDefaultTimeout(60000);
        return page;
    }
}

// Tests — use xUnit [Fact], inject via IClassFixture
[Collection("Playwright")]
[Trait("Category", "UI")]
public class HomePageTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;
    public HomePageTests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task HomePage_LoadsSuccessfully()
    {
        var page = await _fixture.NewPageAsync();
        await page.GotoAsync(_fixture.BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(page).ToHaveTitleAsync(new Regex(".*"));
    }
}
```
