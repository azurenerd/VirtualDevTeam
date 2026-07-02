using Microsoft.Playwright;

namespace VirtualDevTeam.Dashboard.Tests;

/// <summary>
/// Ensures Playwright browsers are installed before any test runs.
/// Uses a fast check — if Chromium binary exists, skip install. If not,
/// run the install with a 30-second timeout. Fails fast instead of hanging 90+ seconds.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Step 1: Ensure browser binaries are installed (fast check + install if needed)
        await EnsureBrowsersInstalledAsync();

        // Step 2: Create Playwright instance and launch Chromium
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Timeout = 10_000 // 10s — if Chromium doesn't launch in 10s, fail fast
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }

    private static async Task EnsureBrowsersInstalledAsync()
    {
        // Check if Chromium is already available by attempting to locate it
        try
        {
            var pw = await Microsoft.Playwright.Playwright.CreateAsync();
            try
            {
                var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Timeout = 5_000
                });
                await browser.CloseAsync();
                pw.Dispose();
                return; // Browsers already installed and working
            }
            catch
            {
                pw.Dispose();
            }
        }
        catch
        {
            // Playwright.CreateAsync failed — browsers definitely not installed
        }

        // Browsers not available — run install with timeout
        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser install failed with exit code {exitCode}. " +
                "Run 'pwsh bin/Debug/net8.0/playwright.ps1 install chromium' manually.");
    }
}
