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

    /// <summary>Whether Playwright is validated and ready (browsers installed, Chromium launches).</summary>
    public bool IsReady { get; private set; }

    /// <summary>Human-readable reason when IsReady is false.</summary>
    public string? NotReadyReason { get; private set; }

    /// <summary>Last time a successful validation occurred.</summary>
    public DateTime? LastValidatedUtc { get; private set; }

    /// <summary>Event raised when IsReady changes. Dashboard subscribes for live updates.</summary>
    public event Action<bool>? ReadyStateChanged;

    public PlaywrightRunner(ILogger<PlaywrightRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validate that Playwright is operational: browsers exist and Chromium can launch.
    /// Sets IsReady/NotReadyReason. Call at startup and periodically.
    /// </summary>
    public async Task<bool> ValidateAsync(WorkspaceConfig config, string? workspacePath = null, CancellationToken ct = default)
    {
        var previousState = IsReady;
        try
        {
            var browsersPath = config.GetPlaywrightBrowsersPath();

            // Step 1: Check browser binary exists
            if (!IsBrowserExecutablePresent(browsersPath))
            {
                if (workspacePath is not null)
                {
                    _logger.LogInformation("Playwright browsers not found — attempting install to {Path}", browsersPath);
                    await EnsureBrowsersInstalledAsync(config, workspacePath, ct);

                    if (!IsBrowserExecutablePresent(browsersPath))
                    {
                        SetNotReady("Browser install failed — Chromium executable not found after install attempt");
                        return false;
                    }
                }
                else
                {
                    SetNotReady($"Chromium not found at {browsersPath}");
                    return false;
                }
            }

            // Step 2: Smoke test — can Chromium actually launch?
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Timeout = 10000 // 10s — if it doesn't launch in 10s, something is broken
                });
                await browser.CloseAsync();
            }
            finally
            {
                playwright.Dispose();
            }

            // All good
            IsReady = true;
            NotReadyReason = null;
            LastValidatedUtc = DateTime.UtcNow;
            _logger.LogInformation("Playwright validated ✓ — Chromium launches successfully from {Path}", browsersPath);

            if (!previousState)
                ReadyStateChanged?.Invoke(true);

            return true;
        }
        catch (Exception ex)
        {
            SetNotReady($"Chromium launch failed: {ex.Message}");
            return false;
        }
    }

    private void SetNotReady(string reason)
    {
        var wasReady = IsReady;
        IsReady = false;
        NotReadyReason = reason;
        _logger.LogWarning("Playwright NOT ready: {Reason}", reason);

        if (wasReady)
            ReadyStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Derive a unique port for the app under test based on the workspace path.
    /// This prevents port conflicts when multiple agents run apps simultaneously.
    /// Port range: 5100–5899 (800 slots).
    /// </summary>
    internal static int DeriveUniquePort(string workspacePath, int configuredPort = 5100)
    {
        var hash = Math.Abs(workspacePath.GetHashCode());
        return 5100 + (hash % 800);
    }

    /// <summary>
    /// Replace the port in a URL and optionally in an app start command.
    /// </summary>
    private static (string url, string? command) RewritePort(string baseUrl, string? appCommand, int newPort)
    {
        var uri = new Uri(baseUrl);
        var newUrl = $"{uri.Scheme}://localhost:{newPort}";

        string? newCommand = appCommand;
        if (appCommand is not null && uri.Port > 0)
            newCommand = appCommand.Replace($":{uri.Port}", $":{newPort}");

        return (newUrl, newCommand);
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

        // Set env var so all child processes (including dotnet test) find the browsers
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);

        // Check for actual chrome executable — not just the directory
        if (IsBrowserExecutablePresent(browsersPath))
        {
            _logger.LogDebug("Playwright Chromium executable found at {Path}", browsersPath);
            return;
        }

        _logger.LogInformation("Installing Playwright Chromium browsers to {Path}", browsersPath);

        // Strategy 0: Use playwright.ps1 from the Runner's own bin directory
        // This guarantees browser version matches the NuGet package version we're running
        var runnerAssemblyDir = Path.GetDirectoryName(typeof(PlaywrightRunner).Assembly.Location);
        if (runnerAssemblyDir is not null)
        {
            var runnerScript = Path.Combine(runnerAssemblyDir, "playwright.ps1");
            if (File.Exists(runnerScript))
            {
                var dllPath = Path.Combine(runnerAssemblyDir, "Microsoft.Playwright.dll");
                if (File.Exists(dllPath))
                {
                    _logger.LogInformation("Using Runner's playwright.ps1 at {Script}", runnerScript);
                    await RunInstallCommandAsync(
                        "pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{runnerScript}\" install chromium",
                        browsersPath, ct);

                    if (IsBrowserExecutablePresent(browsersPath))
                        return;
                    _logger.LogWarning("Runner playwright.ps1 install did not produce expected browser executable");
                }
            }
        }

        // Strategy 1: Use the node-based Playwright CLI from the built test project
        // This is the most reliable method — the .playwright folder ships with the NuGet package
        var nodeCliPair = FindNodePlaywrightCli(workspacePath);
        if (nodeCliPair is not null)
        {
            _logger.LogInformation("Using node-based Playwright CLI: {Cli}", nodeCliPair.Value.cliJs);
            await RunInstallCommandAsync(
                nodeCliPair.Value.nodeExe,
                $"\"{nodeCliPair.Value.cliJs}\" install chromium",
                browsersPath, ct);
            return;
        }

        // Strategy 2: Try .NET Playwright PowerShell script (from NuGet package)
        var dotnetPlaywrightScript = FindDotNetPlaywrightScript(workspacePath);
        if (dotnetPlaywrightScript is not null)
        {
            await RunInstallCommandAsync(
                "pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{dotnetPlaywrightScript}\" install chromium",
                browsersPath, ct);
            return;
        }

        // Strategy 3: Fallback to npx (Node.js projects)
        await RunInstallCommandAsync(
            OperatingSystem.IsWindows() ? "cmd" : "npx",
            OperatingSystem.IsWindows() ? "/c npx --yes playwright install chromium" : "--yes playwright install chromium",
            browsersPath, ct);

        _logger.LogInformation("Playwright browser installation complete");

        // Verify after install
        if (!IsBrowserExecutablePresent(browsersPath))
            _logger.LogWarning("Playwright install completed but browser executable not found at {Path}", browsersPath);
    }

    /// <summary>
    /// Check if the actual Chromium executable exists (not just the directory).
    /// Playwright stores browsers as: {browsersPath}/chromium-{version}/chrome-win/chrome.exe (Windows)
    /// or {browsersPath}/chromium-{version}/chrome-linux/chrome (Linux)
    /// </summary>
    internal static bool IsBrowserExecutablePresent(string browsersPath)
    {
        if (!Directory.Exists(browsersPath)) return false;

        var chromiumDirs = Directory.GetDirectories(browsersPath, "chromium*", SearchOption.TopDirectoryOnly);
        foreach (var dir in chromiumDirs)
        {
            // Windows: chromium-{ver}/chrome-win/chrome.exe (older) or chrome-win64/chrome.exe (newer)
            var winExe = Path.Combine(dir, "chrome-win", "chrome.exe");
            if (File.Exists(winExe)) return true;
            var winExe64 = Path.Combine(dir, "chrome-win64", "chrome.exe");
            if (File.Exists(winExe64)) return true;

            // Headless shell variant: chromium_headless_shell-{ver}/chrome-headless-shell-win64/headless_shell.exe
            var headlessExe = Path.Combine(dir, "chrome-headless-shell-win64", "headless_shell.exe");
            if (File.Exists(headlessExe)) return true;

            // Linux: chromium-{ver}/chrome-linux/chrome or chrome-linux64/chrome
            var linuxExe = Path.Combine(dir, "chrome-linux", "chrome");
            if (File.Exists(linuxExe)) return true;
            var linuxExe64 = Path.Combine(dir, "chrome-linux64", "chrome");
            if (File.Exists(linuxExe64)) return true;

            // macOS: chromium-{ver}/chrome-mac/Chromium.app
            var macApp = Path.Combine(dir, "chrome-mac", "Chromium.app");
            if (Directory.Exists(macApp)) return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the node.exe and cli.js pair from a built test project's .playwright folder.
    /// </summary>
    private (string nodeExe, string cliJs)? FindNodePlaywrightCli(string workspacePath)
    {
        try
        {
            // Search in bin output directories for the .playwright folder
            var searchPaths = new[] { workspacePath };
            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                var playwrightDirs = Directory.GetDirectories(searchPath, ".playwright", SearchOption.AllDirectories);
                foreach (var pwDir in playwrightDirs)
                {
                    var nodeExe = Path.Combine(pwDir, "node", "win32_x64", "node.exe");
                    if (!OperatingSystem.IsWindows())
                        nodeExe = Path.Combine(pwDir, "node", "linux-x64", "node");
                    var cliJs = Path.Combine(pwDir, "package", "cli.js");

                    if (File.Exists(nodeExe) && File.Exists(cliJs))
                        return (nodeExe, cliJs);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching for node-based Playwright CLI");
        }
        return null;
    }

    /// <summary>
    /// Install browsers matching the test project's Playwright NuGet version.
    /// Searches for playwright.ps1 in the test project's bin output and runs it.
    /// This ensures `dotnet test` uses browsers matching its own Playwright assembly.
    /// </summary>
    private async Task InstallBrowsersFromTestProjectAsync(
        string workspacePath, string browsersPath, CancellationToken ct)
    {
        try
        {
            // First, build the test projects so playwright.ps1 appears in bin/
            // (dotnet test builds implicitly, but we need the script BEFORE running tests)
            var testProjects = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                .Where(f =>
                {
                    try
                    {
                        var content = File.ReadAllText(f);
                        return content.Contains("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                })
                .ToList();

            foreach (var proj in testProjects)
            {
                _logger.LogInformation("Building Playwright test project to generate browser install script: {Project}", proj);
                var buildInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{proj}\" -v q",
                    WorkingDirectory = workspacePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                buildInfo.EnvironmentVariables["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

                using var buildProcess = new Process { StartInfo = buildInfo };
                buildProcess.Start();
                var buildOut = await buildProcess.StandardOutput.ReadToEndAsync(ct);
                var buildErr = await buildProcess.StandardError.ReadToEndAsync(ct);

                using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                buildCts.CancelAfter(TimeSpan.FromMinutes(3));
                try { await buildProcess.WaitForExitAsync(buildCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { buildProcess.Kill(entireProcessTree: true); } catch { }
                    _logger.LogWarning("Test project build timed out for {Project}", proj);
                    continue;
                }

                if (buildProcess.ExitCode != 0)
                {
                    _logger.LogWarning("Test project build failed ({Code}): {Err}", buildProcess.ExitCode,
                        buildErr.Length > 300 ? buildErr[..300] : buildErr);
                }
            }

            // Now find playwright.ps1 from built test project output
            var scripts = Directory.EnumerateFiles(workspacePath, "playwright.ps1", SearchOption.AllDirectories)
                .Where(f => f.Contains("bin", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var script in scripts)
            {
                // Verify Microsoft.Playwright.dll exists alongside it
                var dir = Path.GetDirectoryName(script)!;
                var dll = Path.Combine(dir, "Microsoft.Playwright.dll");
                if (!File.Exists(dll)) continue;

                _logger.LogInformation("Installing browsers from test project script: {Script}", script);
                await RunInstallCommandAsync(
                    "pwsh", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" install chromium",
                    browsersPath, ct);
                return;
            }

            _logger.LogDebug("No playwright.ps1 found in test project bin directories");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install browsers from test project");
        }
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

        // Derive unique port per workspace to prevent conflicts when agents run simultaneously
        var uniquePort = DeriveUniquePort(workspacePath);
        var (baseUrl, rewrittenCommand) = RewritePort(config.AppBaseUrl, config.AppStartCommand, uniquePort);
        _logger.LogInformation("UI tests using unique port {Port} (workspace: {Path})", uniquePort, workspacePath);

        // Temporarily override config command with port-rewritten version
        var originalCommand = config.AppStartCommand;
        if (rewrittenCommand is not null) config.AppStartCommand = rewrittenCommand;

        // Environment variables for headless Playwright
        var envVars = new Dictionary<string, string>
        {
            ["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath,
            ["HEADED"] = config.PlaywrightHeadless ? "0" : "1",
            ["BASE_URL"] = baseUrl,
            ["BROWSER"] = "chromium",
            ["ASPNETCORE_URLS"] = baseUrl,
            // Force Development environment so Kestrel logs "Now listening on:" to stdout.
            // AI-generated apps often hardcode UseUrls()/app.Urls.Add() which overrides
            // ASPNETCORE_URLS — URL detection from stdout is our fallback to discover the
            // actual listening port.
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            // Ensure hosting lifetime logs (including "Now listening on:") are emitted even
            // if the app configures a higher minimum log level.
            ["Logging__Console__LogLevel__Microsoft.Hosting.Lifetime"] = "Information"
        };

        // Video recording: set env vars so test fixtures can configure BrowserNewContextOptions
        var testResultsPath = Path.Combine(workspacePath, config.TestResultsDir);
        Directory.CreateDirectory(testResultsPath);

        if (config.RecordTestVideos)
        {
            envVars["PWVIDEO_DIR"] = Path.Combine(testResultsPath, "videos");
            Directory.CreateDirectory(envVars["PWVIDEO_DIR"]);
        }

        if (config.RecordTestTraces)
        {
            envVars["PWTRACE_DIR"] = Path.Combine(testResultsPath, "traces");
            Directory.CreateDirectory(envVars["PWTRACE_DIR"]);
        }

        // Standard Playwright test output directory
        envVars["PLAYWRIGHT_TEST_RESULTS_DIR"] = testResultsPath;

        Process? appProcess = null;
        try
        {
            // Ensure data files exist so the app doesn't show an error page
            EnsureSampleDataExists(workspacePath);

            // Start app under test if configured
            if (!string.IsNullOrWhiteSpace(config.AppStartCommand))
            {
                var (proc, detectedUrl) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
                appProcess = proc;

                // Use detected URL if the app overrode our configured port
                var effectiveUrl = detectedUrl ?? baseUrl;
                if (detectedUrl is not null && detectedUrl != baseUrl)
                {
                    _logger.LogInformation("App listening on {DetectedUrl} instead of configured {BaseUrl}, using detected URL",
                        detectedUrl, baseUrl);
                    baseUrl = detectedUrl;
                    envVars["BASE_URL"] = baseUrl;
                }

                // Wait for app readiness
                var ready = await WaitForAppReadyAsync(
                    baseUrl, config.AppStartupTimeoutSeconds, ct, appProcess);

                if (!ready)
                {
                    // Fallback: AI-generated apps may hardcode UseUrls()/app.Urls.Add()
                    // which overrides ASPNETCORE_URLS. If URL detection also failed (e.g.,
                    // Production log level suppressed the "Now listening on:" message), try
                    // the original configured port as a last resort.
                    var configuredUrl = config.AppBaseUrl;
                    if (!string.Equals(baseUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Derived port {DerivedUrl} not responding, trying configured base URL {ConfiguredUrl}",
                            baseUrl, configuredUrl);
                        ready = await WaitForAppReadyAsync(configuredUrl, 5, ct);
                        if (ready)
                        {
                            _logger.LogInformation(
                                "App responded on configured URL {ConfiguredUrl} — hardcoded port likely overrides ASPNETCORE_URLS",
                                configuredUrl);
                            baseUrl = configuredUrl;
                            envVars["BASE_URL"] = baseUrl;
                        }
                    }
                }

                if (!ready)
                {
                    // Capture diagnostic info about why the app didn't start
                    var exitInfo = "";
                    if (appProcess is not null)
                    {
                        if (appProcess.HasExited)
                            exitInfo = $" Process exited with code {appProcess.ExitCode}.";
                        else
                            exitInfo = " Process is still running but not responding on expected port.";
                    }

                    _logger.LogWarning("App under test did not become ready at {Url} within {Timeout}s.{ExitInfo} — attempting build first",
                        baseUrl, config.AppStartupTimeoutSeconds, exitInfo);

                    // Kill the failed process and try building before re-starting
                    try { if (appProcess is not null && !appProcess.HasExited) appProcess.Kill(entireProcessTree: true); } catch { }
                    appProcess?.Dispose();
                    appProcess = null;

                    // Build first, then retry app start
                    var buildCommand = config.BuildCommand ?? "dotnet build --verbosity quiet";
                    var (buildExe, buildArgs) = BuildRunner.ParseCommand(buildCommand);
                    var buildPsi = new ProcessStartInfo(buildExe, buildArgs)
                    {
                        WorkingDirectory = workspacePath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var buildProc = Process.Start(buildPsi);
                    if (buildProc is not null)
                    {
                        await buildProc.WaitForExitAsync(ct);
                        if (buildProc.ExitCode == 0)
                        {
                            _logger.LogInformation("Build succeeded, retrying app start for UI tests");
                            var (proc2, detectedUrl2) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
                            appProcess = proc2;
                            var retryUrl = detectedUrl2 ?? baseUrl;
                            if (detectedUrl2 is not null) { baseUrl = detectedUrl2; envVars["BASE_URL"] = baseUrl; }
                            ready = await WaitForAppReadyAsync(retryUrl, config.AppStartupTimeoutSeconds, ct, appProcess);
                        }
                        else
                        {
                            var buildStderr = await buildProc.StandardError.ReadToEndAsync(ct);
                            _logger.LogWarning("Build failed with code {Code} before UI test retry: {Stderr}",
                                buildProc.ExitCode, buildStderr.Length > 1000 ? buildStderr[..1000] : buildStderr);
                        }
                    }

                    if (!ready)
                    {
                        var finalExitInfo = "";
                        if (appProcess is not null && appProcess.HasExited)
                            finalExitInfo = $" Process exited with code {appProcess.ExitCode}.";

                        return new TestResult
                        {
                            Success = false,
                            Output = $"App under test failed to start at {baseUrl}.{finalExitInfo}",
                            Passed = 0, Failed = 0, Skipped = 0,
                            Duration = TimeSpan.Zero,
                            Tier = TestTier.UI,
                            FailureDetails = [$"App did not respond at {baseUrl} within {config.AppStartupTimeoutSeconds}s.{finalExitInfo}"]
                        };
                    }
                }

                _logger.LogInformation("App under test is ready at {Url}", baseUrl);
            }

            // Install browsers matching the test project's Playwright NuGet version.
            // The AI-generated test project may reference a different Playwright version
            // than the Runner, so we must install browsers from the test project's own script.
            await InstallBrowsersFromTestProjectAsync(workspacePath, browsersPath, ct);

            // Run the test command with Playwright environment
            var result = await RunTestCommandAsync(
                workspacePath, testCommand, envVars, timeoutSeconds, ct);

            var combinedOutput = result.StandardOutput + "\n" + result.StandardError;
            var (passed, failed, skipped) = TestRunner.ParseTestCounts(combinedOutput);
            var failures = TestRunner.ParseTestFailures(combinedOutput);

            // Reconcile: if parser found failure details but count says 0 failed, trust the details
            if (failed == 0 && failures.Count > 0)
            {
                _logger.LogWarning("Playwright test count parser reported 0 failed but {FailureCount} failure details found — correcting",
                    failures.Count);
                failed = failures.Count;
            }

            // Collect video, trace, and screenshot artifacts
            var artifacts = CollectTestArtifacts(testResultsPath, config);

            if (artifacts.HasArtifacts)
            {
                _logger.LogInformation(
                    "Collected test artifacts: {Videos} videos, {Traces} traces, {Screenshots} screenshots",
                    artifacts.Videos.Count, artifacts.Traces.Count, artifacts.Screenshots.Count);
            }

            return new TestResult
            {
                Success = result.Success && failed == 0,
                Output = combinedOutput,
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Duration = result.Duration,
                Tier = TestTier.UI,
                FailureDetails = failures,
                Artifacts = artifacts
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

            // Restore original command
            config.AppStartCommand = originalCommand;

            // Restore any patched Program.cs files
            RestoreOriginalPortBindings(workspacePath);
        }
    }

    /// <summary>
    /// Start the application under test in a background process.
    /// Returns the Process handle and any detected listening URL so it can be killed after tests.
    /// The detected URL comes from parsing "Now listening on:" lines in stdout/stderr,
    /// which is needed because AI-generated apps often hardcode UseUrls() which overrides
    /// both --urls and ASPNETCORE_URLS environment variable.
    /// </summary>
    private async Task<(Process Process, string? DetectedUrl)> StartAppUnderTestAsync(
        string workspacePath,
        WorkspaceConfig config,
        Dictionary<string, string> envVars,
        CancellationToken ct)
    {
        // Patch hardcoded port bindings in the target app so it respects ASPNETCORE_URLS.
        // AI-generated apps often have app.Urls.Clear()/app.Urls.Add("http://localhost:5050")
        // which overrides all env vars and CLI args, causing port conflicts with the runner.
        PatchHardcodedPortBindings(workspacePath, envVars);

        var appCommand = ResolveAppStartCommand(workspacePath, config);

        // Resolve the app project directory for WorkingDirectory.
        // Using the workspace root causes relative path issues (e.g., data.json not found)
        // when the app resolves files relative to its CWD.
        var appWorkDir = ResolveAppProjectDirectory(workspacePath, appCommand) ?? workspacePath;

        // If WorkingDirectory changed from workspace root, rewrite the --project path
        // to be relative to the new WorkingDirectory. Otherwise dotnet run fails because
        // the --project path (relative to workspace root) doesn't exist from the project subdir.
        appCommand = RewriteProjectPathForWorkDir(appCommand, workspacePath, appWorkDir);

        var (exe, args) = BuildRunner.ParseCommand(appCommand);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = appWorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in envVars)
            startInfo.EnvironmentVariables[key] = value;

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // Capture output to detect the actual listening URL
        // AI-generated apps often hardcode UseUrls() which overrides our --urls/env var
        var stdoutBuffer = new System.Text.StringBuilder();
        var stderrBuffer = new System.Text.StringBuilder();
        string? detectedUrl = null;
        var urlLock = new object();
        var listeningPattern = new System.Text.RegularExpressions.Regex(
            @"Now listening on:\s*(https?://[^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    lock (stdoutBuffer) stdoutBuffer.AppendLine(line);
                    var match = listeningPattern.Match(line);
                    if (match.Success)
                    {
                        var url = match.Groups[1].Value;
                        lock (urlLock)
                        {
                            if (detectedUrl is null || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                                detectedUrl = url;
                        }
                    }
                }
            }
            catch { /* process exited */ }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
                {
                    lock (stderrBuffer) stderrBuffer.AppendLine(line);
                    var match = listeningPattern.Match(line);
                    if (match.Success)
                    {
                        var url = match.Groups[1].Value;
                        lock (urlLock)
                        {
                            if (detectedUrl is null || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                                detectedUrl = url;
                        }
                    }
                }
            }
            catch { /* process exited */ }
        }, ct);

        _logger.LogInformation("Started app under test: {Command} (PID {Pid})",
            appCommand, process.Id);

        // Poll for URL detection — dotnet run includes compilation so it can take 15-20s
        for (var i = 0; i < 20 && detectedUrl is null && !process.HasExited; i++)
            await Task.Delay(1000, ct);

        if (detectedUrl is not null)
            _logger.LogInformation("Detected app listening URL from process output: {Url}", detectedUrl);
        else
        {
            if (process.HasExited)
            {
                // Wait for reader tasks to finish flushing before reading buffers
                try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(3), ct); }
                catch { /* timeout is fine, best-effort */ }

                string stdout, stderr;
                lock (stdoutBuffer) { stdout = stdoutBuffer.ToString().Trim(); }
                lock (stderrBuffer) { stderr = stderrBuffer.ToString().Trim(); }
                var combinedOutput = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrEmpty(s)));
                _logger.LogWarning("App process exited with code {Code} before becoming ready. Output:\n{Output}",
                    process.ExitCode, combinedOutput);
            }
            else
            {
                _logger.LogDebug("No listening URL detected from process output after 20s");
            }
        }

        return (process, detectedUrl);
    }

    /// <summary>
    /// Patch hardcoded port bindings in the target app's Program.cs so it respects
    /// the ASPNETCORE_URLS environment variable. AI-generated apps frequently contain:
    ///   app.Urls.Clear();
    ///   app.Urls.Add("http://localhost:5050");
    /// which overrides ALL external configuration (env vars, --urls, launchSettings).
    /// This causes port conflicts when the runner is already on that port, and prevents
    /// the PlaywrightRunner from assigning a unique port per workspace.
    ///
    /// The patch replaces hardcoded Urls.Add/Urls.Clear calls with code that reads
    /// ASPNETCORE_URLS, falling back to the original hardcoded value.
    /// A .bak file is saved so the original can be restored after tests.
    /// </summary>
    private void PatchHardcodedPortBindings(string workspacePath, Dictionary<string, string> envVars)
    {
        // Find Program.cs files (exclude test projects)
        var programFiles = Directory.EnumerateFiles(workspacePath, "Program.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var programFile in programFiles)
        {
            try
            {
                var content = File.ReadAllText(programFile);

                // Check if it has hardcoded port bindings
                if (!content.Contains("app.Urls.Add") && !content.Contains("Urls.Add(") &&
                    !content.Contains(".UseUrls("))
                    continue;

                var relPath = Path.GetRelativePath(workspacePath, programFile);

                // Save backup for restoration
                var backupPath = programFile + ".playwright-bak";
                if (!File.Exists(backupPath))
                    File.Copy(programFile, backupPath);

                var patched = content;

                // Comment out app.Urls.Clear() entirely
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)app\.Urls\.Clear\(\);",
                    "$1// [PlaywrightRunner] app.Urls.Clear(); — removed so ASPNETCORE_URLS env var controls the port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Comment out app.Urls.Add("http://...") entirely — let ASPNETCORE_URLS env var control the port.
                // Previous approach of replacing with env var read didn't work reliably
                // because dotnet run may skip recompilation. By commenting out the line,
                // the app has NO programmatic URL override, so ASPNETCORE_URLS takes full effect.
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)app\.Urls\.Add\(""(https?://[^""]+)""\);",
                    "$1// [PlaywrightRunner] app.Urls.Add(\"$2\"); — removed so ASPNETCORE_URLS env var controls the port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Also handle builder.WebHost.UseUrls("...") pattern
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)(.+)\.UseUrls\(""(https?://[^""]+)""\)",
                    "$1// [PlaywrightRunner] $2.UseUrls(\"$3\") — removed so ASPNETCORE_URLS env var controls the port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Handle ConfigureKestrel with ListenLocalhost(port) — common in AI-generated Blazor apps
                // This pattern overrides ASPNETCORE_URLS, so we must comment it out.
                // Matches: builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(5000));
                //          options.ListenLocalhost(5000)  (multi-line ConfigureKestrel blocks)
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)(.+\.ConfigureKestrel\(.+\bListenLocalhost\(\d+\).*);",
                    "$1// [PlaywrightRunner] $2; — removed so ASPNETCORE_URLS env var controls the port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Handle multi-line ConfigureKestrel blocks:
                //   builder.WebHost.ConfigureKestrel(options =>
                //   {
                //       options.ListenLocalhost(5000);
                //   });
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.ListenLocalhost\(\d+\);",
                    "$1// [PlaywrightRunner] removed ListenLocalhost — ASPNETCORE_URLS controls port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Handle Listen(IPAddress.Loopback, port) pattern
                patched = System.Text.RegularExpressions.Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.Listen\(IPAddress\.Loopback,\s*\d+\);",
                    "$1// [PlaywrightRunner] removed Listen(IPAddress.Loopback) — ASPNETCORE_URLS controls port",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                if (patched != content)
                {
                    File.WriteAllText(programFile, patched);
                    _logger.LogInformation(
                        "Patched hardcoded port bindings in {File} to respect ASPNETCORE_URLS",
                        relPath);

                    // Force rebuild: delete bin/ and obj/ directories so dotnet run
                    // cannot skip recompilation. Without this, dotnet run may use the
                    // stale pre-patch build output with hardcoded ports.
                    var projectDir = Path.GetDirectoryName(programFile)!;
                    foreach (var dir in new[] { "bin", "obj" })
                    {
                        var targetDir = Path.Combine(projectDir, dir);
                        if (Directory.Exists(targetDir))
                        {
                            try
                            {
                                Directory.Delete(targetDir, true);
                                _logger.LogInformation("Deleted {Dir} to force rebuild with patched port bindings", targetDir);
                            }
                            catch (Exception dirEx)
                            {
                                _logger.LogDebug(dirEx, "Could not delete {Dir}, rebuild may use stale output", targetDir);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to patch port bindings in {File}", programFile);
            }
        }
    }

    /// <summary>
    /// Restore any Program.cs files that were patched by <see cref="PatchHardcodedPortBindings"/>.
    /// Called in the finally block after UI tests complete.
    /// </summary>
    private void RestoreOriginalPortBindings(string workspacePath)
    {
        try
        {
            var backups = Directory.EnumerateFiles(workspacePath, "*.playwright-bak", SearchOption.AllDirectories);
            foreach (var backup in backups)
            {
                var original = backup[..^".playwright-bak".Length];
                File.Copy(backup, original, overwrite: true);
                File.Delete(backup);
                _logger.LogDebug("Restored original {File}", Path.GetRelativePath(workspacePath, original));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to restore patched files in {Path}", workspacePath);
        }
    }

    /// <summary>
    /// Resolves the app start command, auto-detecting the project path if the configured
    /// --project path doesn't exist in the workspace (e.g., config says src/Foo/Foo.csproj
    /// but the repo has Foo/Foo.csproj at root).
    /// </summary>
    internal string ResolveAppStartCommand(string workspacePath, WorkspaceConfig config)
    {
        var command = config.AppStartCommand!;

        // Extract --project value from command
        var projectMatch = System.Text.RegularExpressions.Regex.Match(
            command, @"--project\s+""?([^""]+\.csproj)""?");
        if (!projectMatch.Success)
            return command;

        var configuredPath = projectMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(workspacePath, configuredPath);

        if (File.Exists(fullPath))
            return command; // configured path works

        // Auto-detect: search for a .csproj with the same filename
        // Filter using relative path to avoid matching "test" in workspace root (e.g., testengineer agent paths)
        var fileName = Path.GetFileName(configuredPath);
        var candidates = Directory.EnumerateFiles(workspacePath, fileName, SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            // Broader search for any web .csproj — rank by web SDK preference
            candidates = RankCsprojCandidates(
                Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                    .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase)));
        }

        if (candidates.Count > 0)
        {
            var preferred = candidates.First();
            var resolvedPath = Path.GetRelativePath(workspacePath, preferred);
            var newCommand = command.Replace(projectMatch.Groups[1].Value, resolvedPath);
            _logger.LogInformation(
                "Auto-resolved app project path: {ConfiguredPath} -> {ResolvedPath}",
                configuredPath, resolvedPath);
            return newCommand;
        }

        _logger.LogWarning("Could not find project file {FileName} in workspace {Path}, using configured command as-is",
            fileName, workspacePath);
        return command;
    }

    /// <summary>
    /// Resolves the app project directory from the start command.
    /// Used to set WorkingDirectory so the app can find relative files (data.json, wwwroot, etc.).
    /// </summary>
    internal string? ResolveAppProjectDirectory(string workspacePath, string appCommand)
    {
        // Extract --project path from the command
        var projectMatch = System.Text.RegularExpressions.Regex.Match(
            appCommand, @"--project\s+""?([^""]+\.csproj)""?");
        if (projectMatch.Success)
        {
            var projectPath = Path.Combine(workspacePath, projectMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(projectPath))
            {
                var dir = Path.GetDirectoryName(projectPath);
                if (dir is not null)
                {
                    _logger.LogDebug("Resolved app working directory from --project: {Dir}", dir);
                    return dir;
                }
            }
        }

        // Fallback: find the main (non-test) web .csproj — rank by web SDK preference
        var candidates = RankCsprojCandidates(
            Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase)));

        if (candidates.Count > 0)
        {
            var preferred = candidates.First();
            var dir = Path.GetDirectoryName(preferred);
            if (dir is not null)
            {
                _logger.LogDebug("Resolved app working directory from csproj search: {Dir}", dir);
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites the --project path in an app command when the WorkingDirectory differs
    /// from the workspace root. The --project path is originally relative to the workspace,
    /// so it must be recalculated relative to the new WorkingDirectory.
    /// </summary>
    internal static string RewriteProjectPathForWorkDir(string appCommand, string workspacePath, string appWorkDir)
    {
        if (string.Equals(appWorkDir, workspacePath, StringComparison.OrdinalIgnoreCase))
            return appCommand;

        var projectMatch = System.Text.RegularExpressions.Regex.Match(
            appCommand, @"--project\s+""?([^""]+\.csproj)""?");
        if (!projectMatch.Success)
            return appCommand;

        var originalRelative = projectMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
        var absoluteProject = Path.GetFullPath(Path.Combine(workspacePath, originalRelative));
        var newRelative = Path.GetRelativePath(appWorkDir, absoluteProject);
        return appCommand.Replace(projectMatch.Groups[1].Value, newRelative);
    }

    /// <summary>
    /// Ranks csproj candidates to prefer runnable web projects over class libraries.
    /// Web projects use Microsoft.NET.Sdk.Web and are the ones we need to `dotnet run`.
    /// </summary>
    internal List<string> RankCsprojCandidates(IEnumerable<string> candidates)
    {
        return candidates
            .Select(f =>
            {
                var score = 0;
                try
                {
                    var content = File.ReadAllText(f);
                    // Strong signal: Web SDK means it's a runnable web app
                    if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                        score += 100;
                    // Medium signal: references typical web packages
                    if (content.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
                        score += 50;
                    // Medium signal: OutputType Exe (not Library)
                    if (content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 40;
                    // Weak signal: project name contains "Web", "App", "Server", "Dashboard"
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("App", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Blazor", StringComparison.OrdinalIgnoreCase))
                        score += 20;
                    // Prefer src/ paths
                    if (Path.GetRelativePath(".", f).StartsWith("src", StringComparison.OrdinalIgnoreCase))
                        score += 10;
                    // Penalize Models/Shared/Common libraries
                    if (name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Shared", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Common", StringComparison.OrdinalIgnoreCase))
                        score -= 30;
                }
                catch { /* can't read file, low priority */ }
                return (File: f, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Select(x => x.File)
            .ToList();
    }

    /// <summary>
    /// Poll the base URL until it returns HTTP 200 or timeout expires.
    /// If a process is provided, bail immediately when it exits (crash/build error).
    /// </summary>
    internal async Task<bool> WaitForAppReadyAsync(
        string baseUrl,
        int timeoutSeconds,
        CancellationToken ct,
        Process? appProcess = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // Fast-fail: if the process already exited, no point polling
            if (appProcess is not null && appProcess.HasExited)
            {
                _logger.LogWarning("App process exited with code {Code} during readiness poll — aborting wait",
                    appProcess.ExitCode);
                return false;
            }

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
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Auto-answer any interactive prompts (e.g., npx "Ok to proceed? (y)")
        try { await process.StandardInput.WriteLineAsync("y"); process.StandardInput.Close(); }
        catch { /* stdin may already be closed */ }

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
    /// <summary>
    /// Render a static HTML file (or raw HTML string) to a PNG screenshot using Playwright.
    /// Does NOT require a running app — loads HTML directly in headless Chromium.
    /// Useful for capturing design reference files as visual embeds.
    /// </summary>
    /// <param name="htmlContent">Raw HTML content to render.</param>
    /// <param name="config">Workspace config for browser path.</param>
    /// <param name="viewportWidth">Viewport width in pixels (default 1920).</param>
    /// <param name="viewportHeight">Viewport height in pixels (default 1080).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PNG bytes, or null if rendering fails.</returns>
    public async Task<byte[]?> CaptureHtmlScreenshotAsync(
        string htmlContent,
        WorkspaceConfig config,
        int viewportWidth = 1920,
        int viewportHeight = 1080,
        CancellationToken ct = default)
    {
        try
        {
            var browsersPath = config.GetPlaywrightBrowsersPath();
            if (!IsBrowserExecutablePresent(browsersPath))
            {
                _logger.LogDebug("Playwright browser executable not found, skipping HTML screenshot");
                return null;
            }
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);

            var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            try
            {
                var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                {
                    Headless = true
                });

                var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
                {
                    ViewportSize = new Microsoft.Playwright.ViewportSize { Width = viewportWidth, Height = viewportHeight }
                });

                var page = await context.NewPageAsync();
                await page.SetContentAsync(htmlContent, new Microsoft.Playwright.PageSetContentOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle,
                    Timeout = 15000
                });

                // Brief render delay for any CSS animations or SVG rendering
                await Task.Delay(1000, ct);

                var screenshotBytes = await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    FullPage = true,
                    Type = Microsoft.Playwright.ScreenshotType.Png
                });

                await browser.DisposeAsync();
                _logger.LogInformation("Captured HTML design screenshot ({Size} bytes, {W}×{H})",
                    screenshotBytes.Length, viewportWidth, viewportHeight);

                return screenshotBytes;
            }
            finally
            {
                playwright.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture HTML design screenshot");
            return null;
        }
    }

    /// <summary>
    /// Capture a screenshot of an HTML file from the workspace.
    /// Reads the file, renders it via Playwright, and returns PNG bytes.
    /// </summary>
    public async Task<byte[]?> CaptureHtmlFileScreenshotAsync(
        string workspacePath,
        string relativeFilePath,
        WorkspaceConfig config,
        CancellationToken ct = default)
    {
        var fullPath = Path.Combine(workspacePath, relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("HTML file not found for screenshot: {Path}", fullPath);
            return null;
        }

        var htmlContent = await File.ReadAllTextAsync(fullPath, ct);
        if (string.IsNullOrWhiteSpace(htmlContent))
            return null;

        return await CaptureHtmlScreenshotAsync(htmlContent, config, ct: ct);
    }

    /// <summary>
    /// Captures the running application's main page as a 1920×1080 PNG screenshot.
    /// </summary>
    public async Task<byte[]?> CaptureAppScreenshotAsync(
        string workspacePath,
        WorkspaceConfig config,
        CancellationToken ct = default)
    {
        Process? appProcess = null;
        string? originalCommand = config.AppStartCommand;
        try
        {
            var browsersPath = config.GetPlaywrightBrowsersPath();
            if (!IsBrowserExecutablePresent(browsersPath))
            {
                _logger.LogDebug("Playwright browser executable not found, skipping screenshot");
                return null;
            }
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);

            // Ensure data files exist so the app doesn't show an error page
            EnsureSampleDataExists(workspacePath);

            // Patch hardcoded port bindings (ConfigureKestrel, UseUrls, etc.)
            // so ASPNETCORE_URLS env var controls the port — same as UI test path
            var envVarsForPatch = new Dictionary<string, string>();
            PatchHardcodedPortBindings(workspacePath, envVarsForPatch);

            // Derive unique port per workspace to prevent conflicts when agents run simultaneously
            var uniquePort = DeriveUniquePort(workspacePath);
            var (portUrl, _) = RewritePort(config.AppBaseUrl, null, uniquePort);
            _logger.LogInformation("Screenshot using unique port {Port} (workspace: {Path})", uniquePort, workspacePath);

            // Derive or use configured app start command
            var appStartCommand = config.AppStartCommand;
            if (string.IsNullOrWhiteSpace(appStartCommand))
            {
                // Auto-detect — prefer web projects (Sdk.Web) over class libraries
                var nonTestProjects = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                    .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase));
                var csproj = RankCsprojCandidates(nonTestProjects).FirstOrDefault();
                if (csproj is not null)
                {
                    _logger.LogInformation("Auto-detected app project: {Project}", Path.GetRelativePath(workspacePath, csproj));
                    appStartCommand = $"dotnet run --project \"{csproj}\" --urls {portUrl}";
                }
                else
                    return null; // Can't start app without a command
            }
            else
            {
                // Use ResolveAppStartCommand then rewrite port
                appStartCommand = ResolveAppStartCommand(workspacePath, config);
                var configuredUri = new Uri(config.AppBaseUrl);
                if (configuredUri.Port > 0)
                    appStartCommand = appStartCommand.Replace($":{configuredUri.Port}", $":{uniquePort}");
            }

            var envVars = new Dictionary<string, string>
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = browsersPath,
                ["ASPNETCORE_URLS"] = portUrl,
                ["DOTNET_ENVIRONMENT"] = "Development"
            };

            var screenshotUrl = portUrl;

            // Override config command with port-rewritten version (restored in finally)
            config.AppStartCommand = appStartCommand;

            var (proc, detectedUrl) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
            appProcess = proc;

            // Use detected URL if the app overrode our configured port
            if (detectedUrl is not null && detectedUrl != screenshotUrl)
            {
                _logger.LogInformation("Screenshot: app listening on {DetectedUrl} instead of configured {BaseUrl}",
                    detectedUrl, screenshotUrl);
                screenshotUrl = detectedUrl;
            }

            var ready = await WaitForAppReadyAsync(screenshotUrl, config.AppStartupTimeoutSeconds, ct, appProcess);

            // Fallback: if unique port didn't respond, try the configured base URL
            if (!ready)
            {
                var configuredUrl = config.AppBaseUrl;
                if (!string.Equals(screenshotUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Screenshot: unique port {UniquePort} not responding, trying configured URL {ConfiguredUrl}",
                        screenshotUrl, configuredUrl);
                    ready = await WaitForAppReadyAsync(configuredUrl, 5, ct, appProcess);
                    if (ready) screenshotUrl = configuredUrl;
                }
            }

            if (!ready)
            {
                _logger.LogWarning("App not ready for screenshot at {Url} — attempting build first", screenshotUrl);
                // Kill the failed process and try building before starting
                try { if (!appProcess.HasExited) appProcess.Kill(entireProcessTree: true); } catch { }
                appProcess.Dispose();
                appProcess = null;

                // Build first using dotnet build, then retry start
                var buildPsi = new ProcessStartInfo("dotnet", "build --verbosity quiet")
                {
                    WorkingDirectory = workspacePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var buildProc = Process.Start(buildPsi);
                if (buildProc is not null)
                {
                    await buildProc.WaitForExitAsync(ct);
                    if (buildProc.ExitCode == 0)
                    {
                        _logger.LogInformation("Build succeeded, retrying app start");
                        var (proc2, detectedUrl2) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
                        appProcess = proc2;
                        if (detectedUrl2 is not null) screenshotUrl = detectedUrl2;
                        ready = await WaitForAppReadyAsync(screenshotUrl, config.AppStartupTimeoutSeconds, ct, appProcess);
                    }
                }

                if (!ready)
                {
                    // Fallback: try the original configured base URL (app may have ignored our unique port)
                    var configuredUrl = config.AppBaseUrl;
                    if (!string.Equals(screenshotUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Screenshot: trying configured URL {ConfiguredUrl} as fallback", configuredUrl);
                        ready = await WaitForAppReadyAsync(configuredUrl, 5, ct, appProcess);
                        if (ready) screenshotUrl = configuredUrl;
                    }
                }

                if (!ready)
                {
                    _logger.LogDebug("App still not ready after build+restart+fallback, giving up on screenshot");
                    return null;
                }
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
                await page.GotoAsync(screenshotUrl, new Microsoft.Playwright.PageGotoOptions
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
                    screenshotBytes.Length, screenshotUrl);

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
            // Restore original command
            config.AppStartCommand = originalCommand;

            // Restore any patched Program.cs files
            RestoreOriginalPortBindings(workspacePath);

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
            "/// Runs headless by default. Captures screenshots on failure,\n" +
            "/// records video and traces when configured via environment variables.\n" +
            "/// </summary>\n" +
            "public class PlaywrightFixture : IAsyncLifetime\n" +
            "{\n" +
            "    public IPlaywright Playwright { get; private set; } = null!;\n" +
            "    public IBrowser Browser { get; private set; } = null!;\n\n" +
            "    public string BaseUrl =>\n" +
            "        Environment.GetEnvironmentVariable(\"BASE_URL\")\n" +
            "            ?? throw new InvalidOperationException(\n" +
            "                \"BASE_URL environment variable not set. \" +\n" +
            "                \"The test runner should set this to the app's URL.\");\n\n" +
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
            "    public async Task<(IPage Page, IBrowserContext Context)> NewPageWithContextAsync(string? testName = null)\n" +
            "    {\n" +
            "        var contextOptions = new BrowserNewContextOptions\n" +
            "        {\n" +
            "            BaseURL = BaseUrl,\n" +
            "            IgnoreHTTPSErrors = true\n" +
            "        };\n\n" +
            "        // Enable video recording if PWVIDEO_DIR is set\n" +
            "        var videoDir = Environment.GetEnvironmentVariable(\"PWVIDEO_DIR\");\n" +
            "        if (!string.IsNullOrEmpty(videoDir))\n" +
            "        {\n" +
            "            Directory.CreateDirectory(videoDir);\n" +
            "            contextOptions.RecordVideoDir = videoDir;\n" +
            "            contextOptions.RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 };\n" +
            "        }\n\n" +
            "        var context = await Browser.NewContextAsync(contextOptions);\n\n" +
            "        // Enable tracing if PWTRACE_DIR is set\n" +
            "        var traceDir = Environment.GetEnvironmentVariable(\"PWTRACE_DIR\");\n" +
            "        if (!string.IsNullOrEmpty(traceDir))\n" +
            "        {\n" +
            "            Directory.CreateDirectory(traceDir);\n" +
            "            await context.Tracing.StartAsync(new TracingStartOptions\n" +
            "            {\n" +
            "                Screenshots = true,\n" +
            "                Snapshots = true,\n" +
            "                Sources = true\n" +
            "            });\n" +
            "        }\n\n" +
            "        return (await context.NewPageAsync(), context);\n" +
            "    }\n\n" +
            "    public async Task<IPage> NewPageAsync()\n" +
            "    {\n" +
            "        var (page, _) = await NewPageWithContextAsync();\n" +
            "        return page;\n" +
            "    }\n\n" +
            "    /// <summary>Stops tracing and saves the trace file. Call in test cleanup.</summary>\n" +
            "    public static async Task StopTracingAsync(IBrowserContext context, string testName)\n" +
            "    {\n" +
            "        var traceDir = Environment.GetEnvironmentVariable(\"PWTRACE_DIR\");\n" +
            "        if (string.IsNullOrEmpty(traceDir)) return;\n" +
            "        var tracePath = Path.Combine(traceDir, $\"{testName}.zip\");\n" +
            "        await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });\n" +
            "    }\n\n" +
            "    public static async Task CaptureScreenshotAsync(IPage page, string testName)\n" +
            "    {\n" +
            "        var resultsDir = Environment.GetEnvironmentVariable(\"PLAYWRIGHT_TEST_RESULTS_DIR\") ?? \"TestResults\";\n" +
            "        var screenshotDir = Path.Combine(resultsDir, \"screenshots\");\n" +
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

    /// <summary>
    /// Collect video, trace, and screenshot artifacts from the test results directory.
    /// Searches recursively for .webm (videos), .zip (traces), and .png (screenshots).
    /// </summary>
    internal static TestArtifacts CollectTestArtifacts(string testResultsPath, WorkspaceConfig config)
    {
        if (!Directory.Exists(testResultsPath))
            return new TestArtifacts();

        var videos = new List<string>();
        var traces = new List<string>();
        var screenshots = new List<string>();

        // Collect videos (.webm files)
        if (config.RecordTestVideos)
        {
            var videoDir = Path.Combine(testResultsPath, "videos");
            if (Directory.Exists(videoDir))
            {
                videos.AddRange(Directory.GetFiles(videoDir, "*.webm", SearchOption.AllDirectories));
            }
        }

        // Collect traces (.zip files in traces dir)
        if (config.RecordTestTraces)
        {
            var traceDir = Path.Combine(testResultsPath, "traces");
            if (Directory.Exists(traceDir))
            {
                traces.AddRange(Directory.GetFiles(traceDir, "*.zip", SearchOption.AllDirectories));
            }
        }

        // Collect screenshots (.png files anywhere in results)
        var screenshotDir = Path.Combine(testResultsPath, "screenshots");
        if (Directory.Exists(screenshotDir))
        {
            screenshots.AddRange(Directory.GetFiles(screenshotDir, "*.png", SearchOption.AllDirectories));
        }

        return new TestArtifacts
        {
            Videos = videos,
            Traces = traces,
            Screenshots = screenshots
        };
    }

    /// <summary>
    /// Ensures that sample data files exist before starting the app for screenshots/tests.
    /// AI-generated apps often gitignore data.json (it may contain sensitive project names),
    /// but include data.template.json or test data. Without data.json the app shows an error page,
    /// producing misleading screenshots. This copies template/sample data when data.json is missing.
    /// </summary>
    private void EnsureSampleDataExists(string workspacePath)
    {
        // Search for the main app project directory (where data.json would live)
        var appDirs = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct()
            .ToList();

        foreach (var appDir in appDirs)
        {
            // Check multiple candidate locations where data.json might be expected:
            // - appDir/data.json (project root)
            // - appDir/wwwroot/data.json (Blazor static files)
            // - appDir/wwwroot/data/data.json (nested data folder)
            var candidatePaths = new[]
            {
                Path.Combine(appDir, "data.json"),
                Path.Combine(appDir, "Data", "data.json"),
                Path.Combine(appDir, "wwwroot", "data.json"),
                Path.Combine(appDir, "wwwroot", "data", "data.json"),
            };

            if (candidatePaths.Any(File.Exists))
                continue; // Already has data somewhere

            // Strategy 1: Copy data.template.json or data.example.json from the workspace
            var templateCandidates = new[]
            {
                Path.Combine(appDir, "data.template.json"),
                Path.Combine(workspacePath, "data.template.json"),
                Path.Combine(appDir, "data.example.json"),
                Path.Combine(workspacePath, "data.example.json"),
                Path.Combine(appDir, "Data", "data.example.json"),
                Path.Combine(appDir, "Data", "data.template.json"),
            };

            var template = templateCandidates.FirstOrDefault(File.Exists);
            if (template is not null)
            {
                // Place in all candidate locations to cover however the app resolves the path
                foreach (var dest in candidatePaths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(template, dest, overwrite: false);
                }
                _logger.LogInformation("Copied {Template} → data.json ({Count} locations) for app preview",
                    Path.GetRelativePath(workspacePath, template), candidatePaths.Length);
                continue;
            }

            // Strategy 2: Copy a valid test data file (prefer "full" variants)
            var testDataFiles = Directory.EnumerateFiles(workspacePath, "valid-full*.json", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(workspacePath, "valid*.json", SearchOption.AllDirectories))
                .Where(f => Path.GetRelativePath(workspacePath, f).Contains("TestData", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (testDataFiles.Count > 0)
            {
                foreach (var dest in candidatePaths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(testDataFiles[0], dest, overwrite: false);
                }
                _logger.LogInformation("Copied test data {Source} → data.json ({Count} locations) for app preview",
                    Path.GetRelativePath(workspacePath, testDataFiles[0]), candidatePaths.Length);
                continue;
            }

            // Strategy 3: No data file found — log a warning but do NOT generate a fallback.
            // A hardcoded fallback schema will almost certainly not match the app's data model,
            // causing misleading "schema validation failed" errors in screenshots.
            // Better to let the app show "data.json not found" (which is at least accurate)
            // than to create a file with the wrong schema that triggers confusing validation errors.
            _logger.LogWarning(
                "No data.json or data.template.json found for app in {AppDir}. " +
                "The app may show a 'file not found' error in screenshots. " +
                "Ensure the engineering task includes creating a sample data.json matching the data model schema.",
                Path.GetRelativePath(workspacePath, appDir));
        }
    }
}
