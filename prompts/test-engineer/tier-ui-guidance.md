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

## Quiet-page assertions are mandatory

Every UI test must assert that the page produced no UNEXPECTED runtime noise — not just that the expected DOM is visible. A page that renders the right markup while throwing 500s or unhandled JS errors in the background is broken, even if the visible assertions read green.

Wire two passive collectors at the start of each test and check them before the test ends:

```csharp
var consoleErrors = new List<string>();
var failedResponses = new List<string>();
page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add(m.Text); };
page.Response += (_, r) => { if ((int)r.Status >= 500) failedResponses.Add($"{(int)r.Status} {r.Url}"); };
// ... drive the page, perform interactions, run your DOM assertions ...
Assert.Empty(consoleErrors);     // surfaces silent JS errors
Assert.Empty(failedResponses);   // surfaces silent 5xx responses
```

When asserting visible content, prefer accessible queries (`GetByRoleAsync`, `GetByLabelAsync`, `GetByTextAsync`) over raw CSS selectors. Accessibility regressions then show up as test failures rather than passing silently.

## MANDATORY: Navigation Smoke Test

Every UI test suite MUST include a navigation smoke test that validates ALL navigation links
in the application render real pages. This is the single most important UI test — it catches
broken routes, missing pages, and 404s that pass HTTP status checks in SPAs.

**CRITICAL: Use DOM assertions, NOT HTTP status codes.** Single Page Applications (SPAs like
Blazor, React, Vue) return HTTP 200 for ALL routes including broken ones. The test MUST:

1. Load the app root
2. Find all navigation links (sidebar, top nav, hamburger menu)
3. Click each link
4. Assert the resulting page has meaningful content (not just the shell):
   - Page body does NOT contain "404", "Not Found", "Page not found"
   - No error boundary visible (e.g., no `blazor-error-ui`, no React error overlay)
   - At least one route-specific element exists (heading, data container, form)
5. Skip links to external domains or links with dynamic route parameters (`{`, `:`)

Example implementation:
```csharp
[Fact]
public async Task AllNavLinks_LoadRealPages()
{
    var page = await _fixture.NewPageAsync();
    await page.GotoAsync(_fixture.BaseUrl);
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

    // Collect all nav links
    var navLinks = await page.Locator("nav a[href]").AllAsync();

    foreach (var link in navLinks)
    {
        var href = await link.GetAttributeAsync("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("http") || href.Contains("{"))
            continue;

        await link.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // DOM assertions — NOT HTTP status
        var bodyText = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("404", bodyText);
        Assert.DoesNotContain("Not Found", bodyText);

        // Verify meaningful content rendered (not just app shell)
        var mainContent = page.Locator("main, [role='main'], .page-content, .content");
        await Assertions.Expect(mainContent.First).ToBeVisibleAsync();

        // Navigate back for next link
        await page.GotoAsync(_fixture.BaseUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
```

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
