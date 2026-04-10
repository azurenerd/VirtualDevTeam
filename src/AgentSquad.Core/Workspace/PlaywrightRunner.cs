using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Workspace;

/// <summary>
/// Manages Playwright browser installation and UI test execution in agent workspaces.
/// Runs headless only — never takes over the screen. Handles app-under-test lifecycle
/// (start, readiness poll, test execution, shutdown).
/// </summary>
public class PlaywrightRunner
{
    private readonly ILogger<PlaywrightRunner> _logger;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public PlaywrightRunner(ILogger<PlaywrightRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensure Playwright browsers are installed in the shared cache directory.
    /// Installs Chromium only (smallest, ~80MB). Idempotent — no-op if already present.
    /// </summary>
    public async Task EnsureBrowsersInstalledAsync(
        WorkspaceConfig config,
        string workspacePath,
        CancellationToken ct = default)
    {
        var browsersPath = config.GetPlaywrightBrowsersPath();
        Directory.CreateDirectory(browsersPath);

        // Check if chromium directory already exists (quick skip)
        if (Directory.Exists(browsersPath) &&
            Directory.GetDirectories(browsersPath, "chromium*", SearchOption.TopDirectoryOnly).Length > 0)
        {
            _logger.LogDebug("Playwright Chromium already installed at {Path}", browsersPath);
            return;
        }

        _logger.LogInformation("Installing Playwright Chromium browsers to {Path}", browsersPath);

        // Try .NET Playwright PowerShell script first (from NuGet package)
        var dotnetPlaywrightScript = FindDotNetPlaywrightScript(workspacePath);
        if (dotnetPlaywrightScript is not null)
        {
            await RunInstallCommandAsync(
                "pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{dotnetPlaywrightScript}\" install chromium",
                browsersPath, ct);
        }
        else
        {
            // Fallback to npx (Node.js projects)
            await RunInstallCommandAsync(
                OperatingSystem.IsWindows() ? "cmd" : "npx",
                OperatingSystem.IsWindows() ? "/c npx playwright install chromium" : "playwright install chromium",
                browsersPath, ct);
        }

        _logger.LogInformation("Playwright browser installation complete");
    }

    /// <summary>
    /// Run UI tests with Playwright. Handles the full lifecycle:
    /// 1. Set environment variables for headless mode and browser path
    /// 2. Start the app under test in background (if configured)
    /// 3. Wait for app readiness (HTTP 200)
    /// 4. Run the test command
    /// 5. Kill the app process
    /// </summary>
    public async Task<TestResult> RunUITestsAsync(
        string workspacePath,
        WorkspaceConfig config,
        string testCommand,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        var browsersPath = config.GetPlaywrightBrowsersPath();
        var baseUrl = config.AppBaseUrl;

        // Environment variables for headless Playwright
        var envVars = new Dictionary<string, string>
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath,
            ["HEADED"] = config.PlaywrightHeadless ? "0" : "1",
            ["BASE_URL"] = baseUrl,
            ["BROWSER"] = "chromium"
        };

        Process? appProcess = null;
        try
        {
            // Start app under test if configured
            if (!string.IsNullOrWhiteSpace(config.AppStartCommand))
            {
                appProcess = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);

                // Wait for app readiness
                var ready = await WaitForAppReadyAsync(
                    baseUrl, config.AppStartupTimeoutSeconds, ct);

                if (!ready)
                {
                    _logger.LogWarning("App under test did not become ready at {Url} within {Timeout}s",
                        baseUrl, config.AppStartupTimeoutSeconds);

                    return new TestResult
                    {
                        Success = false,
                        Output = $"App under test failed to start at {baseUrl}",
                        Passed = 0, Failed = 0, Skipped = 0,
                        Duration = TimeSpan.Zero,
                        Tier = TestTier.UI,
                        FailureDetails = [$"App did not respond at {baseUrl} within {config.AppStartupTimeoutSeconds}s"]
                    };
                }

                _logger.LogInformation("App under test is ready at {Url}", baseUrl);
            }

            // Run the test command with Playwright environment
            var result = await RunTestCommandAsync(
                workspacePath, testCommand, envVars, timeoutSeconds, ct);

            var combinedOutput = result.StandardOutput + "\n" + result.StandardError;
            var (passed, failed, skipped) = TestRunner.ParseTestCounts(combinedOutput);
            var failures = TestRunner.ParseTestFailures(combinedOutput);

            return new TestResult
            {
                Success = result.Success && failed == 0,
                Output = combinedOutput,
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Duration = result.Duration,
                Tier = TestTier.UI,
                FailureDetails = failures
            };
        }
        finally
        {
            // Always kill the app process
            if (appProcess is not null)
            {
                try
                {
                    if (!appProcess.HasExited)
                    {
                        appProcess.Kill(entireProcessTree: true);
                        _logger.LogDebug("Killed app under test (PID {Pid})", appProcess.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to kill app under test process");
                }
                finally
                {
                    appProcess.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Start the application under test in a background process.
    /// Returns the Process handle so it can be killed after tests.
    /// </summary>
    private async Task<Process> StartAppUnderTestAsync(
        string workspacePath,
        WorkspaceConfig config,
        Dictionary<string, string> envVars,
        CancellationToken ct)
    {
        var (exe, args) = BuildRunner.ParseCommand(config.AppStartCommand!);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in envVars)
            startInfo.EnvironmentVariables[key] = value;

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // Consume output in background to prevent deadlocks
        _ = Task.Run(() => process.StandardOutput.ReadToEndAsync(ct), ct);
        _ = Task.Run(() => process.StandardError.ReadToEndAsync(ct), ct);

        _logger.LogInformation("Started app under test: {Command} (PID {Pid})",
            config.AppStartCommand, process.Id);

        // Give it a moment to initialize
        await Task.Delay(2000, ct);

        return process;
    }

    /// <summary>
    /// Poll the base URL until it returns HTTP 200 or timeout expires.
    /// </summary>
    internal async Task<bool> WaitForAppReadyAsync(
        string baseUrl,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.GetAsync(baseUrl, ct);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // App not ready yet — keep polling
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }

    /// <summary>
    /// Run a test command with Playwright-specific environment variables.
    /// </summary>
    private async Task<ProcessResult> RunTestCommandAsync(
        string workDir,
        string command,
        Dictionary<string, string> envVars,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var (exe, args) = BuildRunner.ParseCommand(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in envVars)
            startInfo.EnvironmentVariables[key] = value;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            sw.Stop();
            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = await stdoutTask,
                StandardError = $"UI tests timed out after {timeoutSeconds}s",
                Duration = sw.Elapsed
            };
        }

        sw.Stop();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdoutTask,
            StandardError = await stderrTask,
            Duration = sw.Elapsed
        };
    }

    private async Task RunInstallCommandAsync(
        string exe, string args, string browsersPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("Playwright browser install timed out");
            return;
        }

        if (process.ExitCode != 0)
            _logger.LogWarning("Playwright install exited with code {Code}: {Stderr}",
                process.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);
        else
            _logger.LogInformation("Playwright install succeeded: {Output}",
                stdout.Length > 200 ? stdout[..200] : stdout);
    }

    /// <summary>
    /// Find the Playwright PowerShell install script from .NET NuGet packages.
    /// Searches bin/Debug and bin/Release directories for the playwright.ps1 script.
    /// </summary>
    internal static string? FindDotNetPlaywrightScript(string workspacePath)
    {
        var searchPaths = new[]
        {
            Path.Combine(workspacePath, "bin", "Debug"),
            Path.Combine(workspacePath, "bin", "Release"),
            workspacePath
        };

        foreach (var basePath in searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            try
            {
                var scripts = Directory.GetFiles(basePath, "playwright.ps1", SearchOption.AllDirectories);
                if (scripts.Length > 0)
                    return scripts[0];
            }
            catch
            {
                // Directory enumeration failed — skip
            }
        }

        return null;
    }

    /// <summary>
    /// Generate the .NET test project scaffold for Playwright UI tests if it doesn't exist.
    /// Returns the .csproj content and base test fixture class.
    /// </summary>

    /// <summary>
    /// Capture a full-page screenshot of the web application for PR visual progress.
    /// Starts the app, waits for readiness, navigates to the base URL, takes screenshot, stops app.
    /// Returns the PNG bytes, or null if capture fails.
    /// </summary>
    public async Task<byte[]?> CaptureAppScreenshotAsync(
        string workspacePath,
        WorkspaceConfig config,
        CancellationToken ct = default)
    {
        Process? appProcess = null;
        try
        {
            var browsersPath = config.GetPlaywrightBrowsersPath();
            if (!Directory.Exists(browsersPath) ||
                Directory.GetDirectories(browsersPath, "chromium*", SearchOption.TopDirectoryOnly).Length == 0)
            {
                _logger.LogDebug("Playwright browsers not installed, skipping screenshot");
                return null;
            }

            // Derive or use configured app start command
            var appStartCommand = config.AppStartCommand;
            if (string.IsNullOrWhiteSpace(appStartCommand))
            {
                // Try to auto-detect — look for a .csproj in the workspace
                var csproj = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (csproj is not null)
                    appStartCommand = $"dotnet run --project \"{csproj}\" --urls {config.AppBaseUrl}";
                else
                    return null; // Can't start app without a command
            }

            var envVars = new Dictionary<string, string>
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath,
                ["ASPNETCORE_URLS"] = config.AppBaseUrl,
                ["DOTNET_ENVIRONMENT"] = "Development"
            };

            appProcess = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
            var ready = await WaitForAppReadyAsync(config.AppBaseUrl, config.AppStartupTimeoutSeconds, ct);

            if (!ready)
            {
                _logger.LogDebug("App not ready for screenshot at {Url}", config.AppBaseUrl);
                return null;
            }

            // Use Playwright to take a full-page screenshot
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                {
                    Headless = true
                });

                var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
                {
                    ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1920, Height = 1080 },
                    IgnoreHTTPSErrors = true
                });

                var page = await context.NewPageAsync();
                await page.GotoAsync(config.AppBaseUrl, new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                // Wait for render (Blazor hydration, JS frameworks)
                await Task.Delay(config.ScreenshotRenderDelaySeconds * 1000, ct);

                var screenshotBytes = await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    FullPage = true,
                    Type = Microsoft.Playwright.ScreenshotType.Png
                });

                await browser.DisposeAsync();
                _logger.LogInformation("Captured UI screenshot ({Size} bytes) from {Url}",
                    screenshotBytes.Length, config.AppBaseUrl);

                return screenshotBytes;
            }
            finally
            {
                playwright.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture UI screenshot");
            return null;
        }
        finally
        {
            if (appProcess is not null)
            {
                try
                {
                    if (!appProcess.HasExited)
                        appProcess.Kill(entireProcessTree: true);
                }
                catch { }
                finally { appProcess.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Generate the .NET test project scaffold for Playwright UI tests if it doesn't exist.
    /// Returns the .csproj content and base test fixture class.
    /// </summary>
    public static IReadOnlyList<(string Path, string Content)> GeneratePlaywrightTestScaffold(
        string projectName,
        string testProjectDir)
    {
        var files = new List<(string Path, string Content)>();

        var csprojPath = Path.Combine(testProjectDir, $"{projectName}.UITests.csproj");
        files.Add((csprojPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
                <PackageReference Include="Microsoft.Playwright" Version="1.*" />
                <PackageReference Include="xunit" Version="2.*" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
              </ItemGroup>
            </Project>
            """));

        var fixtureDir = Path.Combine(testProjectDir, "Infrastructure");
        var fixtureContent =
            $"using Microsoft.Playwright;\n" +
            $"using Xunit;\n\n" +
            $"namespace {projectName}.UITests.Infrastructure;\n\n" +
            "/// <summary>\n" +
            "/// Shared Playwright fixture that manages browser lifecycle.\n" +
            "/// Runs headless by default. Captures screenshots on test failure.\n" +
            "/// </summary>\n" +
            "public class PlaywrightFixture : IAsyncLifetime\n" +
            "{\n" +
            "    public IPlaywright Playwright { get; private set; } = null!;\n" +
            "    public IBrowser Browser { get; private set; } = null!;\n\n" +
            "    public string BaseUrl =>\n" +
            "        Environment.GetEnvironmentVariable(\"BASE_URL\") ?? \"http://localhost:5000\";\n\n" +
            "    public async Task InitializeAsync()\n" +
            "    {\n" +
            "        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();\n" +
            "        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions\n" +
            "        {\n" +
            "            Headless = Environment.GetEnvironmentVariable(\"HEADED\") != \"1\"\n" +
            "        });\n" +
            "    }\n\n" +
            "    public async Task DisposeAsync()\n" +
            "    {\n" +
            "        await Browser.DisposeAsync();\n" +
            "        Playwright.Dispose();\n" +
            "    }\n\n" +
            "    public async Task<IPage> NewPageAsync()\n" +
            "    {\n" +
            "        var context = await Browser.NewContextAsync(new BrowserNewContextOptions\n" +
            "        {\n" +
            "            BaseURL = BaseUrl,\n" +
            "            IgnoreHTTPSErrors = true\n" +
            "        });\n" +
            "        return await context.NewPageAsync();\n" +
            "    }\n\n" +
            "    public static async Task CaptureScreenshotAsync(IPage page, string testName)\n" +
            "    {\n" +
            "        var screenshotDir = Path.Combine(\"TestResults\", \"screenshots\");\n" +
            "        Directory.CreateDirectory(screenshotDir);\n" +
            "        var path = Path.Combine(screenshotDir, $\"{testName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png\");\n" +
            "        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });\n" +
            "    }\n" +
            "}\n\n" +
            "[CollectionDefinition(\"Playwright\")]\n" +
            $"public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture> {{ }}\n";

        files.Add((Path.Combine(fixtureDir, "PlaywrightFixture.cs"), fixtureContent));

        return files;
    }
}
