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
                    baseUrl, config.AppStartupTimeoutSeconds, ct);

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
                        ready = await WaitForAppReadyAsync(configuredUrl, 15, ct);
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
        var appCommand = ResolveAppStartCommand(workspacePath, config);
        var (exe, args) = BuildRunner.ParseCommand(appCommand);

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

        // Capture output to detect the actual listening URL
        // AI-generated apps often hardcode UseUrls() which overrides our --urls/env var
        var outputBuffer = new System.Text.StringBuilder();
        string? detectedUrl = null;
        var listeningPattern = new System.Text.RegularExpressions.Regex(
            @"Now listening on:\s*(https?://[^\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    outputBuffer.AppendLine(line);
                    var match = listeningPattern.Match(line);
                    if (match.Success && detectedUrl is null)
                    {
                        // Prefer http over https for local testing
                        var url = match.Groups[1].Value;
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || detectedUrl is null)
                            detectedUrl = url;
                    }
                }
            }
            catch { /* process exited */ }
        }, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
                {
                    outputBuffer.AppendLine(line);
                    var match = listeningPattern.Match(line);
                    if (match.Success && detectedUrl is null)
                    {
                        var url = match.Groups[1].Value;
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || detectedUrl is null)
                            detectedUrl = url;
                    }
                }
            }
            catch { /* process exited */ }
        }, ct);

        _logger.LogInformation("Started app under test: {Command} (PID {Pid})",
            appCommand, process.Id);

        // Poll for URL detection — dotnet run includes compilation so it can take 30s+
        for (var i = 0; i < 60 && detectedUrl is null && !process.HasExited; i++)
            await Task.Delay(1000, ct);

        if (detectedUrl is not null)
            _logger.LogInformation("Detected app listening URL from process output: {Url}", detectedUrl);
        else
            _logger.LogDebug("No listening URL detected from process output after 60s");

        return (process, detectedUrl);
    }

    /// <summary>
    /// Resolves the app start command, auto-detecting the project path if the configured
    /// --project path doesn't exist in the workspace (e.g., config says src/Foo/Foo.csproj
    /// but the repo has Foo/Foo.csproj at root).
    /// </summary>
    private string ResolveAppStartCommand(string workspacePath, WorkspaceConfig config)
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
            // Broader search for any web .csproj
            candidates = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (candidates.Count > 0)
        {
            // Prefer src/ paths over root-level paths (PE typically puts app code in src/)
            var preferred = candidates
                .OrderByDescending(f => Path.GetRelativePath(workspacePath, f)
                    .StartsWith("src", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .First();
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

            // Derive unique port per workspace to prevent conflicts when agents run simultaneously
            var uniquePort = DeriveUniquePort(workspacePath);
            var (portUrl, _) = RewritePort(config.AppBaseUrl, null, uniquePort);
            _logger.LogInformation("Screenshot using unique port {Port} (workspace: {Path})", uniquePort, workspacePath);

            // Derive or use configured app start command
            var appStartCommand = config.AppStartCommand;
            if (string.IsNullOrWhiteSpace(appStartCommand))
            {
                // Auto-detect — prefer src/ paths over root-level, exclude test projects
                var csproj = Directory.EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
                    .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetRelativePath(workspacePath, f)
                        .StartsWith("src", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault();
                if (csproj is not null)
                    appStartCommand = $"dotnet run --project \"{csproj}\" --urls {portUrl}";
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

            var ready = await WaitForAppReadyAsync(screenshotUrl, config.AppStartupTimeoutSeconds, ct);

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
                        ready = await WaitForAppReadyAsync(screenshotUrl, config.AppStartupTimeoutSeconds, ct);
                    }
                }

                if (!ready)
                {
                    _logger.LogDebug("App still not ready after build+restart, giving up on screenshot");
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
            var dataJsonPath = Path.Combine(appDir, "data.json");
            if (File.Exists(dataJsonPath))
                continue; // Already has data

            // Strategy 1: Copy data.template.json from anywhere in the workspace
            var templateCandidates = new[]
            {
                Path.Combine(appDir, "data.template.json"),
                Path.Combine(workspacePath, "data.template.json"),
            };

            var template = templateCandidates.FirstOrDefault(File.Exists);
            if (template is not null)
            {
                File.Copy(template, dataJsonPath);
                _logger.LogInformation("Copied {Template} → {DataJson} for app preview",
                    Path.GetRelativePath(workspacePath, template),
                    Path.GetRelativePath(workspacePath, dataJsonPath));
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
                File.Copy(testDataFiles[0], dataJsonPath);
                _logger.LogInformation("Copied test data {Source} → {DataJson} for app preview",
                    Path.GetRelativePath(workspacePath, testDataFiles[0]),
                    Path.GetRelativePath(workspacePath, dataJsonPath));
            }
        }
    }
}
