using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Manages the lifecycle of an app-under-test: process start, port management,
/// companion frontend detection, dependency restoration, and sample data seeding.
/// Extracted from PlaywrightRunner to separate app-launching concerns from
/// browser/screenshot/media concerns.
/// </summary>
public sealed class AppLauncher
{
    private readonly ILogger<AppLauncher> _logger;
    private readonly CopilotCliProcessManager? _cliProcessManager;
    private readonly RunnerProcessJob? _runnerJob;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public AppLauncher(
        ILogger<AppLauncher> logger,
        CopilotCliProcessManager? cliProcessManager = null,
        RunnerProcessJob? runnerJob = null)
    {
        _logger = logger;
        _cliProcessManager = cliProcessManager;
        _runnerJob = runnerJob;
    }

    /// <summary>
    /// Derive a unique port for the app under test based on the workspace path.
    /// This prevents port conflicts when multiple agents run apps simultaneously.
    /// Port range: 5100–5899 (800 slots).
    /// </summary>
    public static int DeriveUniquePort(string workspacePath, int configuredPort = 5100)
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
    /// Unified app launch pipeline used by BOTH RunUITestsAsync and CaptureAppScreenshotAsync.
    /// Handles: port derivation → pre-flight validation → patching → start → post-launch verification →
    /// fallback to detected/configured URL → build-and-retry → comprehensive diagnostics on failure.
    /// Returns null if the app could not be started and verified.
    /// </summary>
    public async Task<AppLaunchResult?> LaunchVerifiedAppAsync(
        string workspacePath,
        WorkspaceConfig config,
        Dictionary<string, string> envVars,
        CancellationToken ct)
    {
        var diagnosticNotes = new List<string>();
        var patchedFiles = new List<string>();

        // ── Step 1: Derive unique port ──
        var uniquePort = DeriveUniquePort(workspacePath);
        var (baseUrl, rewrittenCommand) = RewritePort(config.AppBaseUrl, config.AppStartCommand, uniquePort);
        _logger.LogInformation("LaunchVerified: using port {Port} for workspace {Path}", uniquePort, workspacePath);

        // ── Step 2: Advisory port check ──
        if (!IsPortAvailable(uniquePort))
        {
            _logger.LogWarning("Port {Port} appears occupied — will proceed but may need fallback", uniquePort);
            diagnosticNotes.Add($"Port {uniquePort} was occupied at pre-check");
        }

        // Override env vars with correct port
        envVars["ASPNETCORE_URLS"] = baseUrl;
        envVars["BASE_URL"] = baseUrl;

        // Override config command with port-rewritten version
        var originalCommand = config.AppStartCommand;
        if (rewrittenCommand is not null) config.AppStartCommand = rewrittenCommand;

        // ── Step 3: Pre-flight patching (all override vectors) ──
        PatchHardcodedPortBindings(workspacePath, envVars);
        var neutralizedLaunchSettings = NeutralizeLaunchSettings(workspacePath);
        patchedFiles.AddRange(neutralizedLaunchSettings.Select(f => $"launchSettings: {f}"));
        var patchedAppSettings = PatchAppSettingsKestrelEndpoints(workspacePath, uniquePort);
        patchedFiles.AddRange(patchedAppSettings.Select(f => $"appsettings: {f}"));
        var patchedProxyConfigs = RewriteFrontendProxyTargets(workspacePath, baseUrl);
        patchedFiles.AddRange(patchedProxyConfigs.Select(f => $"proxy-rewrite: {f}"));

        // ── Step 4: Start app and detect URL ──
        var (proc, detectedUrl) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);

        var effectiveUrl = baseUrl;
        if (detectedUrl is not null && detectedUrl != baseUrl)
        {
            _logger.LogInformation("App listening on {DetectedUrl} instead of configured {BaseUrl}", detectedUrl, baseUrl);
            effectiveUrl = detectedUrl;
            envVars["BASE_URL"] = effectiveUrl;
            diagnosticNotes.Add($"URL detection override: {detectedUrl}");
        }

        // ── Step 5: Post-start verification ──
        var ready = await WaitForAppReadyAsync(effectiveUrl, config.AppStartupTimeoutSeconds, ct, proc);

        // Fallback 1: try configured base URL (app may have hardcoded a port we didn't catch)
        if (!ready)
        {
            var configuredUrl = config.AppBaseUrl;
            if (!string.Equals(effectiveUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Port {Port} not responding, trying configured URL {ConfiguredUrl}",
                    uniquePort, configuredUrl);
                ready = await WaitForAppReadyAsync(configuredUrl, 5, ct, proc);
                if (ready)
                {
                    effectiveUrl = configuredUrl;
                    envVars["BASE_URL"] = effectiveUrl;
                    diagnosticNotes.Add($"Fallback to configured URL: {configuredUrl}");
                }
            }
        }

        // ── Step 6: Self-healing — kill, build, re-patch, restart ──
        if (!ready)
        {
            _logger.LogWarning("App not ready — attempting build+restart recovery");
            diagnosticNotes.Add("Triggered build+restart recovery");

            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();

            // Build first
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
                // Read stdout + stderr concurrently to avoid pipe deadlock (Lesson #44).
                // The 4KB pipe buffer fills quickly when the build has many errors,
                // blocking the child process and causing WaitForExitAsync to hang forever.
                using var buildTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                buildTimeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
                var buildStdoutTask = buildProc.StandardOutput.ReadToEndAsync(buildTimeoutCts.Token);
                var buildStderrTask = buildProc.StandardError.ReadToEndAsync(buildTimeoutCts.Token);

                try
                {
                    await buildProc.WaitForExitAsync(buildTimeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Recovery build timed out after 3 minutes — killing");
                    try { if (!buildProc.HasExited) buildProc.Kill(entireProcessTree: true); } catch { }
                }

                // Drain pipes with a short timeout after process exit/kill
                var buildStdout = "";
                var buildStderr = "";
                try
                {
                    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    drainCts.CancelAfter(TimeSpan.FromSeconds(5));
                    buildStdout = await buildStdoutTask.WaitAsync(drainCts.Token);
                    buildStderr = await buildStderrTask.WaitAsync(drainCts.Token);
                }
                catch { /* best-effort drain */ }

                if (buildProc.HasExited && buildProc.ExitCode == 0)
                {
                    _logger.LogInformation("Recovery build succeeded, retrying app start");
                    var (proc2, detectedUrl2) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
                    proc = proc2;
                    if (detectedUrl2 is not null)
                    {
                        effectiveUrl = detectedUrl2;
                        envVars["BASE_URL"] = effectiveUrl;
                    }
                    ready = await WaitForAppReadyAsync(effectiveUrl, config.AppStartupTimeoutSeconds, ct, proc);

                    // One more fallback after rebuild
                    if (!ready)
                    {
                        var configuredUrl = config.AppBaseUrl;
                        if (!string.Equals(effectiveUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            ready = await WaitForAppReadyAsync(configuredUrl, 5, ct, proc);
                            if (ready)
                            {
                                effectiveUrl = configuredUrl;
                                envVars["BASE_URL"] = effectiveUrl;
                                diagnosticNotes.Add("Fallback to configured URL after rebuild");
                            }
                        }
                    }
                }
                else
                {
                    var exitCode = buildProc.HasExited ? buildProc.ExitCode : -1;
                    _logger.LogWarning("Recovery build failed with code {Code}: {Stderr}",
                        exitCode, buildStderr.Length > 1000 ? buildStderr[..1000] : buildStderr);
                    diagnosticNotes.Add($"Build failed with exit code {exitCode}");

                    // Still need a valid process reference for cleanup
                    var (proc3, _) = await StartAppUnderTestAsync(workspacePath, config, envVars, ct);
                    proc = proc3;
                }
            }
        }

        // ── Step 7: Final verdict ──
        if (!ready)
        {
            LogPortDiagnostics(effectiveUrl, uniquePort, proc, workspacePath, envVars);

            // Clean up the failed process
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();

            // Restore config
            config.AppStartCommand = originalCommand;
            return null;
        }

        // Extract port from the effective URL for the result
        var effectivePort = uniquePort;
        try { effectivePort = new Uri(effectiveUrl).Port; } catch { }

        _logger.LogInformation("App verified and ready at {Url} (port {Port})", effectiveUrl, effectivePort);

        // ── Companion frontend detection (split API+frontend architectures) ──
        // If main app is a backend API and a frontend project exists, start it too
        // so all screenshot/test paths navigate to the frontend URL (actual HTML).
        Process? companionProcess = null;
        string? companionBrowserUrl = null;
        var companionResult = TryDetectCompanionFrontend(workspacePath, config.AppStartCommand);
        if (companionResult is not null)
        {
            _logger.LogInformation(
                "Detected companion frontend: {Command} at {Url} (backend at {BackendUrl})",
                companionResult.Value.Command, companionResult.Value.Url, effectiveUrl);
            try
            {
                // Set API base URL env vars so frontend proxy connects to the rewritten backend port
                var companionEnvVars = new Dictionary<string, string>(envVars)
                {
                    ["VITE_API_BASE_URL"] = effectiveUrl,
                    ["REACT_APP_API_BASE_URL"] = effectiveUrl,
                    ["NEXT_PUBLIC_API_BASE_URL"] = effectiveUrl,
                    ["API_BASE_URL"] = effectiveUrl,
                };

                companionProcess = await StartCompanionProcessAsync(
                    workspacePath, companionResult.Value.Command, companionResult.Value.WorkDir, companionEnvVars, ct);
                if (companionProcess is not null)
                {
                    var frontendReady = await WaitForAppReadyAsync(companionResult.Value.Url, 30, ct, companionProcess);
                    if (frontendReady)
                    {
                        _logger.LogInformation("Companion frontend ready at {Url}", companionResult.Value.Url);
                        companionBrowserUrl = companionResult.Value.Url;
                    }
                    else
                    {
                        _logger.LogWarning("Companion frontend didn't respond at {Url} — using backend URL for screenshots", companionResult.Value.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start companion frontend — using backend URL for screenshots");
            }
        }

        return new AppLaunchResult
        {
            Process = proc,
            VerifiedUrl = effectiveUrl,
            Port = effectivePort,
            DetectedUrl = detectedUrl,
            UsedFallback = effectiveUrl != baseUrl,
            PatchedFiles = patchedFiles,
            DiagnosticNotes = diagnosticNotes,
            CompanionProcess = companionProcess,
            CompanionBrowserUrl = companionBrowserUrl,
        };
    }

    /// <summary>
    /// Connect to an already-running external dev server instead of launching one.
    /// Used in InPlace mode when <see cref="ServiceDefinition.UseExistingDevServer"/> is true.
    /// Performs a health check and returns a synthetic AppLaunchResult with no managed process.
    /// </summary>
    public async Task<AppLaunchResult?> ConnectToExternalServerAsync(
        Configuration.ServiceDefinition service, CancellationToken ct)
    {
        var healthUrl = service.HealthUrl;
        var port = service.Port ?? 0;
        var baseUrl = $"http://localhost:{port}";

        if (healthUrl is not null)
        {
            _logger.LogInformation(
                "Connecting to external dev server for {Service} at {Url} (health: {Health})",
                service.Name, baseUrl, healthUrl);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var resp = await _httpClient.GetAsync(healthUrl, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("External dev server {Service} is healthy at {Url}",
                            service.Name, healthUrl);
                        // Return a synthetic result with no managed process
                        return CreateExternalServerResult(baseUrl, port);
                    }
                    _logger.LogWarning("Health check for {Service} returned {Status}, retrying...",
                        service.Name, (int)resp.StatusCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Health check for {Service} failed: {Error}, retrying...",
                        service.Name, ex.Message);
                }
                await Task.Delay(2000, ct);
            }

            _logger.LogWarning("External dev server {Service} health check failed after 3 attempts", service.Name);
            return null;
        }

        // No health URL — try connecting directly to the port
        try
        {
            var resp = await _httpClient.GetAsync(baseUrl, ct);
            _logger.LogInformation("External dev server {Service} responded at {Url} (status: {Status})",
                service.Name, baseUrl, (int)resp.StatusCode);
            return CreateExternalServerResult(baseUrl, port);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Cannot connect to external dev server {Service} at {Url}: {Error}",
                service.Name, baseUrl, ex.Message);
            return null;
        }
    }

    private static AppLaunchResult CreateExternalServerResult(string url, int port)
    {
        // Create a dummy process that represents "no managed process"
        // The Process field is required, so we use the current process as a sentinel
        return new AppLaunchResult
        {
            Process = Process.GetCurrentProcess(), // sentinel — not managed by VDT
            VerifiedUrl = url,
            Port = port,
            DiagnosticNotes = ["External dev server — not managed by VDT"],
        };
    }

    /// <summary>
    /// Resolves the app start command, auto-detecting the project path if the configured
    /// --project path doesn't exist in the workspace (e.g., config says src/Foo/Foo.csproj
    /// but the repo has Foo/Foo.csproj at root).
    /// </summary>
    public string ResolveAppStartCommand(string workspacePath, WorkspaceConfig config)
    {
        var command = config.AppStartCommand!;

        // Extract --project value from command
        var projectMatch = Regex.Match(
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
        var candidates = SafeEnumerateFiles(workspacePath, fileName)
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            // Broader search for any web .csproj — rank by web SDK preference
            candidates = RankCsprojCandidates(
                SafeEnumerateFiles(workspacePath, "*.csproj")
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
    public string? ResolveAppProjectDirectory(string workspacePath, string appCommand)
    {
        // Extract --project path from the command
        var projectMatch = Regex.Match(
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
            SafeEnumerateFiles(workspacePath, "*.csproj")
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
    public static string RewriteProjectPathForWorkDir(string appCommand, string workspacePath, string appWorkDir)
    {
        if (string.Equals(appWorkDir, workspacePath, StringComparison.OrdinalIgnoreCase))
            return appCommand;

        var projectMatch = Regex.Match(
            appCommand, @"--project\s+""?([^""]+\.csproj)""?");
        if (!projectMatch.Success)
            return appCommand;

        var originalRelative = projectMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
        var absoluteProject = Path.GetFullPath(Path.Combine(workspacePath, originalRelative));
        var newRelative = Path.GetRelativePath(appWorkDir, absoluteProject);
        return appCommand.Replace(projectMatch.Groups[1].Value, newRelative);
    }

    /// <summary>
    /// Returns true if the .csproj content uses a web-capable SDK
    /// (ASP.NET Core or Blazor WebAssembly).
    /// </summary>
    public static bool IsWebSdkProject(string csprojContent) =>
        csprojContent.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
        csprojContent.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ranks csproj candidates to prefer runnable web projects over class libraries.
    /// Web projects use Microsoft.NET.Sdk.Web or Microsoft.NET.Sdk.BlazorWebAssembly.
    /// </summary>
    public List<string> RankCsprojCandidates(IEnumerable<string> candidates)
    {
        return candidates
            .Select(f =>
            {
                var score = 0;
                try
                {
                    var content = File.ReadAllText(f);
                    // Strong signal: Web SDK means it's a runnable web app
                    if (IsWebSdkProject(content))
                        score += 100;
                    // Medium signal: references typical web packages
                    if (content.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
                        score += 50;
                    // Medium signal: OutputType Exe (not Library)
                    if (content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 40;
                    // Strong penalty: Library projects are NOT directly runnable (e.g., Razor Class Libraries)
                    if (content.Contains("<OutputType>Library</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score -= 150;
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
                    // Strong penalty: test projects are NOT runnable web servers even if they use Web SDK
                    if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                        f.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        f.Contains(Path.DirectorySeparatorChar + "test" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        score -= 200;
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
    public async Task<bool> WaitForAppReadyAsync(
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
                // Accept ANY HTTP response — the app is running. Don't require 200.
                // Real apps often return 302 (redirect to login), 401, or 404 at /
                // and are still fully healthy. The goal is "app is listening and responding."
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
    /// Advisory check: is the port free? Returns true if available, false if occupied.
    /// This is TOCTOU (port could be taken between check and use), so treat as advisory only.
    /// The real source of truth is post-start verification.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

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

        // Always inject --no-launch-profile for dotnet run commands to prevent
        // launchSettings.json from overriding --urls and ASPNETCORE_URLS.
        // Launch profiles take precedence over both env vars and CLI args,
        // causing the app to listen on its default port (e.g., 5000) instead
        // of our unique per-agent port.
        if (appCommand.Contains("dotnet run", StringComparison.OrdinalIgnoreCase) &&
            !appCommand.Contains("--no-launch-profile", StringComparison.OrdinalIgnoreCase))
        {
            appCommand = appCommand.Replace("dotnet run", "dotnet run --no-launch-profile");
            _logger.LogInformation("Injected --no-launch-profile into app start command to prevent port override");
        }

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
        _runnerJob?.Assign(process);

        // Capture output to detect the actual listening URL
        // AI-generated apps often hardcode UseUrls() which overrides our --urls/env var
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        string? detectedUrl = null;
        var urlLock = new object();
        var listeningPattern = new Regex(
            @"Now listening on:\s*(https?://[^\s]+)", RegexOptions.IgnoreCase);

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
    /// the ASPNETCORE_URLS environment variable.
    /// </summary>
    private void PatchHardcodedPortBindings(string workspacePath, Dictionary<string, string> envVars)
    {
        // Stack-agnostic gate: only run .NET-style Program.cs patches against actual .NET workspaces.
        // Non-.NET projects with a coincidentally-named Program.cs would otherwise be parsed and
        // potentially modified. A workspace without .sln/.csproj/.fsproj/.vbproj never gets patched.
        if (!IsDotnetWorkspace(workspacePath))
        {
            _logger.LogDebug("PatchHardcodedPortBindings: workspace is not a .NET project — skipping Program.cs patches");
            return;
        }

        // Find Program.cs files (exclude test projects)
        var programFiles = SafeEnumerateFiles(workspacePath, "Program.cs")
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var programFile in programFiles)
        {
            try
            {
                var content = File.ReadAllText(programFile);

                // Check if it has hardcoded port bindings
                if (!content.Contains("app.Urls.Add") && !content.Contains("Urls.Add(") &&
                    !content.Contains(".UseUrls(") && !content.Contains("ConfigureKestrel") &&
                    !content.Contains("ListenLocalhost") && !content.Contains("Listen(IPAddress") &&
                    !content.Contains("ListenAnyIP") && !content.Contains("app.Run(\"http"))
                    continue;

                var relPath = Path.GetRelativePath(workspacePath, programFile);

                // Save backup for restoration
                var backupPath = programFile + ".playwright-bak";
                if (!File.Exists(backupPath))
                    File.Copy(programFile, backupPath);

                var patched = content;

                // Comment out app.Urls.Clear() entirely
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)app\.Urls\.Clear\(\);",
                    "$1// [PlaywrightRunner] app.Urls.Clear(); — removed so ASPNETCORE_URLS env var controls the port",
                    RegexOptions.Multiline);

                // Comment out app.Urls.Add("http://...") entirely — let ASPNETCORE_URLS env var control the port.
                // Previous approach of replacing with env var read didn't work reliably
                // because dotnet run may skip recompilation. By commenting out the line,
                // the app has NO programmatic URL override, so ASPNETCORE_URLS takes full effect.
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)app\.Urls\.Add\(""(https?://[^""]+)""\);",
                    "$1// [PlaywrightRunner] app.Urls.Add(\"$2\"); — removed so ASPNETCORE_URLS env var controls the port",
                    RegexOptions.Multiline);

                // Also handle builder.WebHost.UseUrls("...") pattern
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)(.+)\.UseUrls\(""(https?://[^""]+)""\)",
                    "$1// [PlaywrightRunner] $2.UseUrls(\"$3\") — removed so ASPNETCORE_URLS env var controls the port",
                    RegexOptions.Multiline);

                // Handle ConfigureKestrel with ListenLocalhost(port) — common in AI-generated Blazor apps
                // This pattern overrides ASPNETCORE_URLS, so we must comment it out.
                // Matches: builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(5000));
                //          options.ListenLocalhost(5000)  (multi-line ConfigureKestrel blocks)
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)(.+\.ConfigureKestrel\(.+\bListenLocalhost\(\d+\).*);",
                    "$1// [PlaywrightRunner] $2; — removed so ASPNETCORE_URLS env var controls the port",
                    RegexOptions.Multiline);

                // Handle multi-line ConfigureKestrel blocks:
                //   builder.WebHost.ConfigureKestrel(options =>
                //   {
                //       options.ListenLocalhost(5000);
                //   });
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.ListenLocalhost\(\d+\);",
                    "$1// [PlaywrightRunner] removed ListenLocalhost — ASPNETCORE_URLS controls port",
                    RegexOptions.Multiline);

                // Handle Listen(IPAddress.Loopback, port) pattern
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.Listen\(IPAddress\.Loopback,\s*\d+\);",
                    "$1// [PlaywrightRunner] removed Listen(IPAddress.Loopback) — ASPNETCORE_URLS controls port",
                    RegexOptions.Multiline);

                // Handle Listen(IPAddress.Any, port) and ListenAnyIP(port) patterns
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.Listen\(IPAddress\.Any,\s*\d+\);",
                    "$1// [PlaywrightRunner] removed Listen(IPAddress.Any) — ASPNETCORE_URLS controls port",
                    RegexOptions.Multiline);
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)\w+\.ListenAnyIP\(\d+\);",
                    "$1// [PlaywrightRunner] removed ListenAnyIP — ASPNETCORE_URLS controls port",
                    RegexOptions.Multiline);

                // Handle app.Run("http://...") — overrides everything when URL is passed directly
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)app\.Run\(""(https?://[^""]+)""\);",
                    "$1// [PlaywrightRunner] app.Run(\"$2\"); — removed so ASPNETCORE_URLS controls port\n$1app.Run();",
                    RegexOptions.Multiline);

                // Handle WebApplication.Urls property assignment patterns
                patched = Regex.Replace(
                    patched,
                    @"^(\s*)(?:app|builder)\.Configuration\[""(?:urls|server\.urls)""\]\s*=\s*""[^""]*"";",
                    "$1// [PlaywrightRunner] removed config URL override — ASPNETCORE_URLS controls port",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);

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
    public void RestoreOriginalPortBindings(string workspacePath)
    {
        try
        {
            var backups = SafeEnumerateFiles(workspacePath, "*.playwright-bak");
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
    /// Best-effort detection of a .NET workspace. Returns true if the workspace contains
    /// any .sln, .csproj, .fsproj, or .vbproj file.
    /// </summary>
    private bool IsDotnetWorkspace(string workspacePath)
    {
        try
        {
            if (!Directory.Exists(workspacePath)) return false;
            foreach (var ext in new[] { "*.sln", "*.csproj", "*.fsproj", "*.vbproj" })
            {
                if (SafeEnumerateFiles(workspacePath, ext).Any())
                    return true;
            }
            return false;
        }
        catch
        {
            // Treat detection failure as "not .NET" — safer to skip patches than to apply them blindly
            return false;
        }
    }

    /// <summary>
    /// Enumerates files recursively, skipping directories that are known to cause
    /// UnauthorizedAccessException (.sandbox, .git, node_modules, etc.).
    /// Same pattern as BuildRunner.SafeGetFiles.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sandbox", ".git", "node_modules", ".candidates", ".candidates-eval", "bin", "obj"
        };

        var dirs = new Stack<string>();
        dirs.Push(root);

        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var f in files)
                yield return f;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!skipDirs.Contains(name))
                        dirs.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// Neutralize launchSettings.json files in the workspace to prevent them from
    /// overriding ASPNETCORE_URLS. Belt-and-suspenders with --no-launch-profile.
    /// Backs up files as *.playwright-bak for restoration.
    /// </summary>
    private List<string> NeutralizeLaunchSettings(string workspacePath)
    {
        var neutralized = new List<string>();
        // Stack-agnostic gate: launchSettings.json is .NET-only. Skip for non-.NET workspaces.
        if (!IsDotnetWorkspace(workspacePath))
        {
            _logger.LogDebug("NeutralizeLaunchSettings: workspace is not a .NET project — skipping");
            return neutralized;
        }
        try
        {
            var launchSettingsFiles = Directory.EnumerateFiles(
                workspacePath, "launchSettings.json", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(workspacePath, f)
                    .Contains("test", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in launchSettingsFiles)
            {
                var backupPath = file + ".playwright-bak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(file, backupPath);
                    File.Delete(file);
                    var relPath = Path.GetRelativePath(workspacePath, file);
                    _logger.LogInformation(
                        "Neutralized {File} (backed up) to prevent port override", relPath);
                    neutralized.Add(relPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error neutralizing launchSettings in {Path}", workspacePath);
        }
        return neutralized;
    }

    /// <summary>
    /// Detect and neutralize Kestrel endpoint configuration in appsettings*.json files.
    /// Only removes narrowly-scoped localhost endpoint bindings, not all config.
    /// </summary>
    private List<string> PatchAppSettingsKestrelEndpoints(string workspacePath, int targetPort)
    {
        var patched = new List<string>();
        // Stack-agnostic gate: appsettings*.json is .NET-specific. Skip for non-.NET workspaces.
        if (!IsDotnetWorkspace(workspacePath))
        {
            _logger.LogDebug("PatchAppSettingsKestrelEndpoints: workspace is not a .NET project — skipping");
            return patched;
        }
        try
        {
            var appSettingsFiles = SafeEnumerateFiles(workspacePath, "appsettings*.json")
                .Where(f => !Path.GetRelativePath(workspacePath, f)
                    .Contains("test", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in appSettingsFiles)
            {
                var content = File.ReadAllText(file);

                // Check for Kestrel endpoint configuration with hardcoded URLs
                if (!content.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) ||
                    !content.Contains("Endpoints", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relPath = Path.GetRelativePath(workspacePath, file);

                // Look for "Url": "http://localhost:XXXX" patterns inside Kestrel config
                var urlPattern = new Regex(
                    @"""Url""\s*:\s*""(https?://(?:localhost|\*|0\.0\.0\.0|127\.0\.0\.1):\d+)""");
                if (!urlPattern.IsMatch(content))
                {
                    _logger.LogInformation(
                        "Detected Kestrel endpoints config in {File} but no hardcoded URLs — leaving as-is", relPath);
                    continue;
                }

                // Backup
                var backupPath = file + ".playwright-bak";
                if (!File.Exists(backupPath))
                    File.Copy(file, backupPath);

                // Replace hardcoded localhost URLs with our target port
                var replaced = urlPattern.Replace(content, $@"""Url"": ""http://localhost:{targetPort}""");

                if (replaced != content)
                {
                    File.WriteAllText(file, replaced);
                    _logger.LogInformation(
                        "Patched Kestrel endpoint URL in {File} to use port {Port}", relPath, targetPort);
                    patched.Add(relPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error patching appsettings Kestrel endpoints in {Path}", workspacePath);
        }
        return patched;
    }

    /// <summary>
    /// Rewrite frontend proxy targets to point to the actual backend URL (dynamic port).
    /// Without this, frontend proxies stay hardcoded to the developer's original port
    /// (e.g., localhost:5000) while the backend runs on a different port.
    /// 
    /// Two-layer approach:
    /// 1. CLI-driven: ask an AI agent which files contain proxy config (handles any framework)
    /// 2. Fallback: scan known config file patterns (deterministic safety net)
    /// 
    /// The actual rewrite is always a deterministic regex (localhost:NNNN → backendUrl),
    /// per lesson #21: the watchdog must be more reliable than the system it watches.
    /// Backs up files as *.playwright-bak for restoration by <see cref="RestoreOriginalPortBindings"/>.
    /// </summary>
    private List<string> RewriteFrontendProxyTargets(string workspacePath, string backendUrl)
    {
        var patched = new List<string>();
        var proxyPattern = new System.Text.RegularExpressions.Regex(
            @"(['""](https?://localhost:\d+)['""])",
            System.Text.RegularExpressions.RegexOptions.None);

        try
        {
            // Layer 1: Ask CLI which files contain proxy/API URL config
            var cliDetectedFiles = TryDetectProxyConfigFilesViaCli(workspacePath);

            // Layer 2: Known config file patterns (safety net)
            var knownPatterns = new[]
            {
                "vite.config.*", "next.config.*", "vue.config.*",
                "webpack.config.*", "proxy.conf.*", "setupProxy.*",
            };

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add CLI-detected files first (highest confidence)
            foreach (var f in cliDetectedFiles)
            {
                var fullPath = Path.IsPathRooted(f) ? f : Path.Combine(workspacePath, f);
                if (File.Exists(fullPath)) candidates.Add(fullPath);
            }

            // Add known-pattern files as fallback
            foreach (var pattern in knownPatterns)
            {
                foreach (var file in SafeEnumerateFiles(workspacePath, pattern))
                    candidates.Add(file);
            }
            // CRA "proxy" field in package.json
            foreach (var file in SafeEnumerateFiles(workspacePath, "package.json"))
                candidates.Add(file);

            foreach (var file in candidates)
            {
                var relPath = Path.GetRelativePath(workspacePath, file);
                if (relPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (relPath.Contains(".playwright-bak", StringComparison.OrdinalIgnoreCase)) continue;

                var content = File.ReadAllText(file);

                // For package.json, only rewrite if it has a "proxy" field (CRA pattern)
                if (Path.GetFileName(file).Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    && !content.Contains("\"proxy\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!proxyPattern.IsMatch(content)) continue;

                var backup = file + ".playwright-bak";
                if (!File.Exists(backup))
                    File.Copy(file, backup);

                var rewritten = proxyPattern.Replace(content, m =>
                {
                    var quote = m.Value[0];
                    _logger.LogInformation(
                        "Rewriting proxy target in {File}: {Old} → {New}",
                        relPath, m.Groups[2].Value, backendUrl);
                    return $"{quote}{backendUrl}{quote}";
                });

                File.WriteAllText(file, rewritten);
                patched.Add(relPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error rewriting frontend proxy targets in {Path}", workspacePath);
        }
        return patched;
    }

    /// <summary>
    /// Ask the Copilot CLI which files in the workspace contain API proxy/backend URL
    /// configuration. Returns relative file paths. Empty list on failure — the deterministic
    /// fallback handles it.
    /// </summary>
    private List<string> TryDetectProxyConfigFilesViaCli(string workspacePath)
    {
        if (_cliProcessManager is null || !_cliProcessManager.IsAvailable)
            return new List<string>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var prompt = @"List the file paths (relative to the current directory) that configure where the frontend dev server proxies API requests to the backend.

Rules:
- Return ONLY file paths, one per line, nothing else
- Include files like vite.config.ts, next.config.js, proxy.conf.json, setupProxy.js, package.json (if it has a ""proxy"" field), webpack.config.js, vue.config.js, or any framework-specific proxy config
- Only include files that actually exist and contain a localhost URL for the backend
- If no proxy config exists, respond with exactly: NONE";

            var options = new CopilotCliRequestOptions
            {
                Pool = CopilotCliPool.SingleShot,
                CloseStdinAfterPrompt = true,
                WorkingDirectory = workspacePath,
            };

            var result = _cliProcessManager.ExecutePromptAsync(prompt, options, cts.Token)
                .GetAwaiter().GetResult();
            if (!result.IsSuccess) return new List<string>();

            var output = result.Output?.Trim();
            if (string.IsNullOrWhiteSpace(output) || output.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return new List<string>();

            if (output.Contains("\"type\":", StringComparison.Ordinal))
                output = CliOutputParser.ParseJsonOutput(output)?.Trim();
            if (string.IsNullOrWhiteSpace(output)) return new List<string>();

            // Parse file paths — reject lines that look like prose (reuse blocklist logic)
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Trim('-', '*', ' '))
                .Where(l => l.Length > 0 && l.Length < 200
                    && !l.StartsWith('#') && !l.StartsWith("```")
                    && (l.Contains('.') || l.Contains('/') || l.Contains('\\')))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI proxy config detection failed — using file-pattern fallback");
            return new List<string>();
        }
    }

    /// <summary>
    private void LogPortDiagnostics(string expectedUrl, int expectedPort, Process? appProcess,
        string workspacePath, Dictionary<string, string> envVars)
    {
        _logger.LogError("PORT DIAGNOSTIC: App failed to respond at {Url}", expectedUrl);
        _logger.LogError("PORT DIAGNOSTIC: Expected port={Port}, ASPNETCORE_URLS={AspUrl}",
            expectedPort, envVars.GetValueOrDefault("ASPNETCORE_URLS", "(not set)"));

        // Check what Program.cs contains
        try
        {
            var programFiles = SafeEnumerateFiles(workspacePath, "Program.cs")
                .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase));
            foreach (var pf in programFiles)
            {
                var content = File.ReadAllText(pf);
                var relPath = Path.GetRelativePath(workspacePath, pf);
                var portPatterns = new[] { "UseUrls", "Urls.Add", "Urls.Clear", "ListenLocalhost",
                    "Listen(IPAddress", "ListenAnyIP", "ConfigureKestrel", ".Run(\"http" };
                var found = portPatterns.Where(p => content.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
                if (found.Count > 0)
                    _logger.LogError("PORT DIAGNOSTIC: {File} still contains port patterns: {Patterns}",
                        relPath, string.Join(", ", found));
                else
                    _logger.LogInformation("PORT DIAGNOSTIC: {File} is clean of port override patterns", relPath);
            }
        }
        catch { /* best effort */ }

        // Check if launchSettings.json exists
        try
        {
            var launchFiles = SafeEnumerateFiles(workspacePath, "launchSettings.json");
            foreach (var lf in launchFiles)
            {
                var relPath = Path.GetRelativePath(workspacePath, lf);
                _logger.LogError("PORT DIAGNOSTIC: launchSettings.json still exists at {File}", relPath);
            }
        }
        catch { /* best effort */ }

        // Check process state
        if (appProcess is not null)
        {
            if (appProcess.HasExited)
                _logger.LogError("PORT DIAGNOSTIC: Process exited with code {Code}", appProcess.ExitCode);
            else
                _logger.LogError("PORT DIAGNOSTIC: Process PID {Pid} still running but not responding", appProcess.Id);
        }
    }

    public string? DetectAppStartCommand(string workspacePath)
    {
        // Prefer prompt-driven detection via Copilot CLI (handles any technology)
        if (_cliProcessManager is not null && _cliProcessManager.IsAvailable)
        {
            try
            {
                var detected = DetectAppStartCommandViaCli(workspacePath).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    _logger.LogInformation("Interaction: CLI detected AppStartCommand: {Command}", detected);
                    return detected;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI-based app start detection failed — falling back to file heuristics");
            }
        }

        // Fallback: file-based heuristics when CLI is unavailable
        return DetectAppStartCommandFallback(workspacePath);
    }

    private async Task<string?> DetectAppStartCommandViaCli(string workspacePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prompt = @"Look at this project and determine the SINGLE command needed to start the web application server.
IMPORTANT: Only look at files in the current working directory and its subdirectories. Do NOT reference projects from parent directories.

Rules:
- Return ONLY the shell command, nothing else (no explanation, no markdown)
- The command must start a web server that listens on HTTP
- PRIORITIZE the actual application project (e.g., src/MyApp.Api/, src/MyApp.Web/) over test projects
- NEVER select a test project (paths containing /tests/, /test/, names ending in .Tests.csproj or .Test.csproj) — test projects use Microsoft.NET.Sdk.Web for WebApplicationFactory but are NOT runnable web servers
- If there is a .sln file, read it to find the main application project (not the test projects)
- If it's a .NET project, use: dotnet run --project ""<path-to-csproj>"" (path MUST be relative to current directory)
- Blazor WebAssembly projects (Sdk=""Microsoft.NET.Sdk.BlazorWebAssembly"") are web apps — treat them the same as Microsoft.NET.Sdk.Web
- If it's a Node.js project, use the appropriate npm script (dev, start, serve)
- If it's Python, use the appropriate framework command (flask run, uvicorn, gunicorn, etc.)
- If there's no web app to start, respond with exactly: NONE

Example responses:
dotnet run --project ""src/MyApp/MyApp.csproj""
npm run dev
python -m flask run";

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.SingleShot,
            CloseStdinAfterPrompt = true,
            WorkingDirectory = workspacePath,
        };

        var result = await _cliProcessManager!.ExecutePromptAsync(prompt, options, cts.Token);
        if (!result.IsSuccess) return null;

        var output = result.Output?.Trim();
        if (string.IsNullOrWhiteSpace(output))
            return null;

        // When JsonOutput is enabled, ExecutePromptAsync returns raw JSONL —
        // parse it to extract the actual assistant message content.
        if (output.Contains("\"type\":", StringComparison.Ordinal))
        {
            output = CliOutputParser.ParseJsonOutput(output)?.Trim();
            if (string.IsNullOrWhiteSpace(output))
                return null;
        }

        if (output.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        // Clean up: take just the first non-empty line (CLI may add noise)
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !l.StartsWith('#') && !l.StartsWith("```")
                && !l.StartsWith('{') && l.Length > 3);

        // Final guard: reject anything that looks like JSON or garbage
        if (firstLine is not null && (firstLine.StartsWith('{') || firstLine.StartsWith('[')))
        {
            _logger.LogWarning("DetectAppStartCommand: rejected JSON-like output: {Output}", firstLine[..Math.Min(80, firstLine.Length)]);
            return null;
        }

        // Command-shape validation: the CLI sometimes returns English prose instead of a
        // shell command (e.g., "The current working directory is empty — there are no project
        // files."). Reject responses that don't start with a known command prefix.
        if (firstLine is not null && !LooksLikeShellCommand(firstLine))
        {
            _logger.LogWarning(
                "DetectAppStartCommand: rejected non-command CLI response: {Output}",
                firstLine[..Math.Min(100, firstLine.Length)]);
            return null;
        }

        // Validate: if command references a --project path, ensure it's inside the workspace
        if (firstLine is not null)
        {
            var projectIdx = firstLine.IndexOf("--project", StringComparison.OrdinalIgnoreCase);
            if (projectIdx >= 0)
            {
                var afterProject = firstLine[(projectIdx + "--project".Length)..].Trim().Trim('"', '\'');
                var projectPath = Path.IsPathRooted(afterProject)
                    ? afterProject
                    : Path.Combine(workspacePath, afterProject);
                var fullProject = Path.GetFullPath(projectPath);
                var fullWorkspace = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (!fullProject.StartsWith(fullWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("DetectAppStartCommand: rejected CLI command referencing project outside workspace: {Command}", firstLine);
                    return null;
                }
                if (!File.Exists(fullProject))
                {
                    _logger.LogWarning("DetectAppStartCommand: rejected CLI command referencing non-existent project: {Project}", fullProject);
                    return null;
                }
            }
        }

        return firstLine;
    }

    /// <summary>
    /// Rejects CLI responses that are obviously English prose rather than shell commands.
    /// Uses a blocklist approach (reject known non-command patterns) rather than an allowlist
    /// of framework prefixes — new frameworks work automatically without code changes.
    /// </summary>
    private static bool LooksLikeShellCommand(string line)
    {
        // A shell command's first token is an executable name — short, no spaces,
        // no sentence-like capitalization. English prose starts with articles,
        // pronouns, or other sentence starters that are never executables.
        var firstSpace = line.IndexOf(' ');
        var firstToken = firstSpace > 0 ? line[..firstSpace] : line;

        // Reject if first token is a common English sentence starter
        ReadOnlySpan<string> englishStarters =
        [
            "the", "a", "an", "this", "that", "these", "those",
            "i", "it", "its", "there", "here", "no", "not",
            "yes", "sorry", "unfortunately", "however", "note",
            "please", "cannot", "could", "would", "should",
            "based", "after", "before", "since", "because",
            "looking", "checking", "found", "appears",
        ];
        foreach (var starter in englishStarters)
        {
            if (firstToken.Equals(starter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Reject if line contains sentence-ending punctuation mid-stream (commands don't)
        if (line.Contains(". ") || line.EndsWith('.') || line.Contains("? "))
            return false;

        // Reject if first token is unreasonably long (executables are short)
        if (firstToken.Length > 40)
            return false;

        // Reject if first token contains characters that never appear in executables
        if (firstToken.Contains(',') || firstToken.Contains('!') || firstToken.Contains('?'))
            return false;

        return true;
    }

    /// <summary>
    /// Detects a companion frontend project when the main start command is a backend API.
    /// Split architectures (e.g., .NET API + Vite/React) need both processes for screenshots.
    /// Returns null if no companion frontend is detected.
    /// </summary>
    private (string Command, string Url, string WorkDir)? TryDetectCompanionFrontend(
        string workspacePath, string? mainCommand)
    {
        // Only check when the main command is a backend (.NET) project
        if (mainCommand is null || !mainCommand.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            return null;

        // Search for package.json files with a "dev" script (Vite, Next.js, etc.)
        foreach (var pkgJson in SafeEnumerateFiles(workspacePath, "package.json"))
        {
            // Skip node_modules and .sandbox directories — but use RELATIVE path so we don't
            // accidentally skip files when the workspace itself is under .candidates (strategy worktrees).
            var pkgDir = Path.GetDirectoryName(pkgJson)!;
            var relativeDir = Path.GetRelativePath(workspacePath, pkgDir);
            if (relativeDir.Contains("node_modules", StringComparison.OrdinalIgnoreCase) ||
                relativeDir.Contains(".sandbox", StringComparison.OrdinalIgnoreCase) ||
                relativeDir.StartsWith(".candidates", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var content = File.ReadAllText(pkgJson);
                // Check for dev/start scripts that indicate a frontend dev server
                if (!content.Contains("\"dev\"", StringComparison.OrdinalIgnoreCase) &&
                    !content.Contains("\"start\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check for index.html or src/ with .tsx/.jsx/.vue files (confirms it's a frontend)
                var hasIndexHtml = File.Exists(Path.Combine(pkgDir, "index.html"));
                var hasSrcDir = Directory.Exists(Path.Combine(pkgDir, "src"));
                if (!hasIndexHtml && !hasSrcDir)
                    continue;

                // Determine the start command and port
                string command;
                int port;

                if (content.Contains("\"vite\"", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("\"dev\": \"vite", StringComparison.OrdinalIgnoreCase))
                {
                    port = DeriveUniquePort(pkgDir + "-frontend");
                    command = $"npx vite --port {port} --host";
                }
                else if (content.Contains("\"next", StringComparison.OrdinalIgnoreCase))
                {
                    port = DeriveUniquePort(pkgDir + "-frontend");
                    command = $"npx next dev --port {port}";
                }
                else
                {
                    port = DeriveUniquePort(pkgDir + "-frontend");
                    // Generic: try npm run dev with PORT env var
                    command = "npm run dev";
                }

                var url = $"http://localhost:{port}";

                _logger.LogInformation(
                    "Detected companion frontend at {Dir}: {Command} → {Url}",
                    pkgDir, command, url);

                return (command, url, pkgDir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse {PkgJson} for companion detection", pkgJson);
            }
        }

        return null;
    }

    /// <summary>
    /// Start a companion process (e.g., frontend dev server) alongside the main app.
    /// </summary>
    private async Task<Process?> StartCompanionProcessAsync(
        string workspacePath, string command, string workDir,
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        // Install dependencies if node_modules doesn't exist
        if (!Directory.Exists(Path.Combine(workDir, "node_modules")))
        {
            _logger.LogInformation("Installing frontend dependencies in {Dir}", workDir);
            var (installExe, installArgs) = BuildRunner.ParseCommand("npm install --prefer-offline");
            var npmInstall = new ProcessStartInfo(installExe, installArgs)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Resolve npm via FreshPathResolver so runtime-installed tools are found
            AI.FreshPathResolver.ApplyFreshPath(npmInstall);

            var installProc = Process.Start(npmInstall);
            if (installProc is not null)
            {
                // Drain pipes to prevent deadlock (Lesson #44)
                using var installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                installCts.CancelAfter(TimeSpan.FromSeconds(90));
                var stdoutTask = installProc.StandardOutput.ReadToEndAsync(installCts.Token);
                var stderrTask = installProc.StandardError.ReadToEndAsync(installCts.Token);
                try
                {
                    await installProc.WaitForExitAsync(installCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("npm install timed out in {Dir} — killing", workDir);
                    try { installProc.Kill(entireProcessTree: true); } catch { }
                }
                // Bounded drain after exit/kill
                try { await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }

                if (installProc.HasExited && installProc.ExitCode != 0)
                {
                    _logger.LogWarning("npm install failed in {Dir} — companion frontend may not work", workDir);
                }
            }
        }

        var (exe, args) = BuildRunner.ParseCommand(command);
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Copy relevant env vars
        foreach (var kv in envVars)
            psi.Environment[kv.Key] = kv.Value;

        var proc = Process.Start(psi);
        if (proc is null)
        {
            _logger.LogWarning("Failed to start companion frontend process");
            return null;
        }

        // Drain stdout/stderr so the process doesn't block
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _logger.LogInformation("Companion frontend started: PID={Pid}, command={Command}", proc.Id, command);
        return proc;
    }

    private string? DetectAppStartCommandFallback(string workspacePath)
    {
        // Look for web projects with launchSettings.json (use SafeEnumerateFiles to skip .sandbox etc.)
        var launchSettings = SafeEnumerateFiles(workspacePath, "launchSettings.json").FirstOrDefault();
        if (launchSettings != null)
        {
            var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(launchSettings)));
            if (projectDir != null)
            {
                var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
                if (csproj != null)
                {
                    // Verify project is runnable (not a Library like Razor Class Libraries)
                    try
                    {
                        var projContent = File.ReadAllText(csproj);
                        if (projContent.Contains("<OutputType>Library</OutputType>", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation(
                                "Interaction: skipping launchSettings project {Csproj} — OutputType is Library", csproj);
                            // Fall through to Web SDK ranking below
                        }
                        else
                        {
                            _logger.LogInformation("Interaction: fallback detected via launchSettings: {Command}", csproj);
                            return $"dotnet run --project \"{csproj}\" --urls http://localhost:5100";
                        }
                    }
                    catch
                    {
                        _logger.LogInformation("Interaction: fallback detected via launchSettings: {Command}", csproj);
                        return $"dotnet run --project \"{csproj}\" --urls http://localhost:5100";
                    }
                }
            }
        }

        // .NET Web SDK fallback — collect all candidates, prefer non-test projects
        var webCandidates = new List<(string Path, bool IsTest, bool IsLibrary)>();
        foreach (var csproj in SafeEnumerateFiles(workspacePath, "*.csproj"))
        {
            try
            {
                // Skip test projects — they often use Microsoft.NET.Sdk.Web for WebApplicationFactory
                // but are NOT runnable web servers
                var name = Path.GetFileNameWithoutExtension(csproj);
                var dirName = Path.GetDirectoryName(csproj) ?? "";
                var isTest = name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("tests", StringComparison.OrdinalIgnoreCase)
                    || dirName.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || dirName.Contains(Path.DirectorySeparatorChar + "test" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || dirName.EndsWith(Path.DirectorySeparatorChar + "tests", StringComparison.OrdinalIgnoreCase)
                    || dirName.EndsWith(Path.DirectorySeparatorChar + "test", StringComparison.OrdinalIgnoreCase);

                var content = File.ReadAllText(csproj);
                if (IsWebSdkProject(content))
                {
                    var isLibrary = content.Contains("<OutputType>Library</OutputType>", StringComparison.OrdinalIgnoreCase);
                    webCandidates.Add((csproj, isTest, isLibrary));
                }
            }
            catch { }
        }

        // Prefer non-test, non-library projects; libraries last (Razor Class Libraries aren't runnable)
        var bestCandidate = webCandidates
            .OrderBy(c => c.IsLibrary ? 2 : c.IsTest ? 1 : 0) // runnable non-test first, then test, then library
            .FirstOrDefault();
        if (bestCandidate.Path is not null)
        {
            _logger.LogInformation("Interaction: fallback detected Web SDK project: {Csproj} (isTest={IsTest})",
                bestCandidate.Path, bestCandidate.IsTest);
            if (bestCandidate.IsTest)
                _logger.LogWarning("Only test project found with Web SDK — app startup may fail");
            return $"dotnet run --project \"{bestCandidate.Path}\" --urls http://localhost:5100";
        }

        // Node.js fallback
        if (File.Exists(Path.Combine(workspacePath, "package.json")))
        {
            _logger.LogInformation("Interaction: fallback detected Node.js project");
            return "npm run dev";
        }

        return null;
    }

    public void EnsureSampleDataExists(string workspacePath)
    {
        // Quick check: are there any data/template files that might need setup?
        // Skip the expensive CLI call when the workspace has no data files at all.
        var dataExtensions = new[] { "*.json", "*.csv", "*.sqlite", "*.db", "*.yaml", "*.yml", "*.xml" };
        var templatePatterns = new[] { "*.template.*", "*.example.*", "*.sample.*" };
        var hasDataFiles = false;

        foreach (var ext in dataExtensions)
        {
            // Only check common data directories, not the entire tree (node_modules etc.)
            var files = Directory.GetFiles(workspacePath, ext, SearchOption.TopDirectoryOnly);
            if (files.Length > 0) { hasDataFiles = true; break; }

            // Check src/ and data/ subdirectories (1 level)
            foreach (var subdir in new[] { "src", "data", "Data", "TestData", "testdata", "seed", "Seed" })
            {
                var subdirPath = Path.Combine(workspacePath, subdir);
                if (Directory.Exists(subdirPath))
                {
                    files = Directory.GetFiles(subdirPath, ext, SearchOption.AllDirectories);
                    if (files.Length > 0) { hasDataFiles = true; break; }
                }
            }
            if (hasDataFiles) break;
        }

        if (!hasDataFiles)
        {
            foreach (var pattern in templatePatterns)
            {
                try
                {
                    var templates = SafeEnumerateFiles(workspacePath, pattern).ToArray();
                    if (templates.Length > 0) { hasDataFiles = true; break; }
                }
                catch { /* glob pattern may not be supported on all platforms */ }
            }
        }

        if (!hasDataFiles)
        {
            _logger.LogDebug("EnsureSampleDataExists: no data/template files found in {Path} — skipping CLI call", workspacePath);
            return;
        }

        // Prefer prompt-driven approach — let CLI figure out what data the app needs
        if (_cliProcessManager is not null && _cliProcessManager.IsAvailable)
        {
            try
            {
                EnsureSampleDataViaCliAsync(workspacePath).GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI-based sample data setup failed — falling back to file heuristics");
            }
        }

        // Fallback: heuristic-based data file copying
        EnsureSampleDataFallback(workspacePath);
    }

    private async Task EnsureSampleDataViaCliAsync(string workspacePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var prompt = @"Look at this project and ensure sample/seed data files are in place so the app can start without ""file not found"" errors.

Steps:
1. Check if the app reads any data files at startup (JSON, SQLite, CSV, YAML, etc.)
2. If template files exist (*.template.*, *.example.*, *.sample.*), copy them to their expected names
3. If test data files exist in a TestData folder, copy the most complete one to where the app expects it
4. If data files are already in place, do nothing

Rules:
- Do NOT modify source code
- Do NOT generate fake data — only copy existing templates/examples
- If no data files are needed, just say 'No sample data needed' and exit";

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            CloseStdinAfterPrompt = true,
            WorkingDirectory = workspacePath,
            WatchdogMode = CopilotCliWatchdogMode.Agentic,
        };

        _logger.LogInformation("Asking CLI to ensure sample data exists in {Path}", workspacePath);
        var result = await _cliProcessManager!.ExecuteAgenticSessionAsync(prompt, options, cts.Token);

        if (result.Succeeded)
            _logger.LogDebug("CLI sample data setup completed");
        else
            _logger.LogDebug("CLI sample data setup reported failure — continuing anyway");
    }

    private void EnsureSampleDataFallback(string workspacePath)
    {
        // Search for the main app project directory (where data.json would live)
        var appDirs = SafeEnumerateFiles(workspacePath, "*.csproj")
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

            // Find which candidate paths are missing — fill them even if some already exist.
            // The app may read from Data/data.json while the SE committed data.json to project root.
            var missingPaths = candidatePaths.Where(p => !File.Exists(p)).ToArray();
            if (missingPaths.Length == 0)
                continue; // All candidate paths already have data.json

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

            // Also use an existing data.json from any candidate path as a source
            var existingDataJson = candidatePaths.FirstOrDefault(File.Exists);
            var template = templateCandidates.FirstOrDefault(File.Exists) ?? existingDataJson;
            if (template is not null)
            {
                foreach (var dest in missingPaths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(template, dest, overwrite: false);
                }
                _logger.LogInformation("Copied {Template} → data.json ({Count} missing locations) for app preview",
                    Path.GetRelativePath(workspacePath, template), missingPaths.Length);
                continue;
            }

            // Strategy 2: Copy a valid test data file (prefer "full" variants)
            var testDataFiles = SafeEnumerateFiles(workspacePath, "valid-full*.json")
                .Concat(SafeEnumerateFiles(workspacePath, "valid*.json"))
                .Where(f => Path.GetRelativePath(workspacePath, f).Contains("TestData", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (testDataFiles.Count > 0)
            {
                foreach (var dest in missingPaths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(testDataFiles[0], dest, overwrite: false);
                }
                _logger.LogInformation("Copied test data {Source} → data.json ({Count} missing locations) for app preview",
                    Path.GetRelativePath(workspacePath, testDataFiles[0]), missingPaths.Length);
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

    public async Task RestoreDependenciesAsync(string workspacePath, CancellationToken ct)
    {
        if (_cliProcessManager is null || !_cliProcessManager.IsAvailable)
        {
            _logger.LogDebug("Screenshot: CLI not available for dependency restore — skipping");
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));

            var prompt = @"Look at this project workspace and run ALL necessary dependency restore/install commands so the app is ready to build and run with full styling. This includes things like:
- Package restore (dotnet restore, npm ci --prefer-offline, pip install, cargo fetch, etc.)
- Client-side library restore (libman restore, bower install, etc.)
- Any build steps needed to generate CSS/assets (npm run build:css, sass compile, etc.)

IMPORTANT: Use cache-friendly flags to speed up installs:
- npm/npx: use 'npm ci --prefer-offline' (or 'npm install --prefer-offline') to use the local cache
- pip: use 'pip install --prefer-binary' to avoid source builds
- dotnet: 'dotnet restore' already uses the global NuGet cache
- yarn: use 'yarn install --offline' if yarn.lock exists
- pnpm: use 'pnpm install --prefer-offline'

Do NOT modify any source code. Only run restore/install/build-assets commands.
If there's nothing to restore, just say 'No dependencies to restore' and exit.";

            var options = new CopilotCliRequestOptions
            {
                Pool = CopilotCliPool.Agentic,
                AllowAll = true,
                CloseStdinAfterPrompt = true,
                WorkingDirectory = workspacePath,
                WatchdogMode = CopilotCliWatchdogMode.Agentic,
            };

            _logger.LogInformation("Screenshot: asking CLI to restore dependencies in {Path}", workspacePath);
            var result = await _cliProcessManager.ExecuteAgenticSessionAsync(prompt, options, cts.Token);

            if (result.Succeeded)                _logger.LogInformation("Screenshot: dependency restore completed successfully");
            else
                _logger.LogDebug("Screenshot: dependency restore session reported failure — continuing anyway");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Screenshot: dependency restore timed out — continuing without full restore");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Screenshot: dependency restore failed — continuing without it");
        }
    }

    /// <summary>
    /// Attempts to run the project to generate static output (HTML files).
    /// Detects project type by common file markers (any language/framework) and runs
    /// the appropriate build command. No hardcoded SDK checks — adapts to .NET, Node.js,
    /// Python, Ruby, Go, or any other project type.
    /// </summary>
    public async Task TryRunProjectGeneratorAsync(string workspacePath, CancellationToken ct)
    {
        // Detect project type and build command by file presence (language-agnostic)
        var (command, args, description) = DetectBuildCommand(workspacePath);
        if (command is null)
        {
            _logger.LogDebug("No recognizable project type found for static generation in {Path}", workspacePath);
            return;
        }

        _logger.LogInformation("Running project generator ({Description}): {Command} {Args}", description, command, args);
        try
        {
            var psi = new ProcessStartInfo(command, args ?? "")
            {
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                _logger.LogDebug("Generator ({Description}) exited with code {Code}", description, proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to run project generator ({Description}) — continuing with static HTML search", description);
        }
    }

    /// <summary>
    /// Detects the project type and returns the appropriate build/run command.
    /// Prefers CLI prompt-driven detection (handles any language/framework);
    /// falls back to file-based heuristics when CLI is unavailable.
    /// Returns (command, args, description) or (null, null, null) if not recognized.
    /// </summary>
    public (string? Command, string? Args, string? Description) DetectBuildCommand(string workspacePath)
    {
        // Prefer prompt-driven detection via CLI
        if (_cliProcessManager is not null && _cliProcessManager.IsAvailable)
        {
            try
            {
                var detected = DetectBuildCommandViaCli(workspacePath).GetAwaiter().GetResult();
                if (detected is not null)
                {
                    _logger.LogInformation("DetectBuild: CLI detected: {Cmd} {Args}", detected.Value.Command, detected.Value.Args);
                    return detected.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI-based build command detection failed — falling back to file heuristics");
            }
        }

        return DetectBuildCommandFallback(workspacePath);
    }

    private async Task<(string Command, string? Args, string? Description)?> DetectBuildCommandViaCli(string workspacePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prompt = @"Look at this project and determine what command will BUILD it (compile/generate output).
This is NOT for starting a dev server — it's for producing static output or compiled artifacts.

Rules:
- Return ONLY the command on one line, nothing else
- Examples: ""dotnet run --project src/App/App.csproj"", ""npm run build"", ""hugo"", ""make""
- If there's nothing to build (already static HTML), respond with: NONE

Return the single build command:";

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.SingleShot,
            CloseStdinAfterPrompt = true,
            WorkingDirectory = workspacePath,
        };

        var result = await _cliProcessManager!.ExecutePromptAsync(prompt, options, cts.Token);
        if (!result.IsSuccess) return null;

        var output = result.Output?.Trim();
        if (string.IsNullOrWhiteSpace(output))
            return null;

        // When JsonOutput is enabled, ExecutePromptAsync returns raw JSONL
        if (output.Contains("\"type\":", StringComparison.Ordinal))
        {
            output = CliOutputParser.ParseJsonOutput(output)?.Trim();
            if (string.IsNullOrWhiteSpace(output))
                return null;
        }

        if (output.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !l.StartsWith('#') && !l.StartsWith("```")
                && !l.StartsWith('{') && l.Length > 2);

        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        // Split into command and args
        var parts = firstLine.Split(' ', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : null, $"CLI-detected ({parts[0]})");
    }

    private (string? Command, string? Args, string? Description) DetectBuildCommandFallback(string workspacePath)
    {
        // .NET projects (csproj/fsproj) — run the non-test project
        var dotnetProjects = SafeEnumerateFiles(workspacePath, "*.csproj")
            .Concat(SafeEnumerateFiles(workspacePath, "*.fsproj"))
            .Where(f => !Path.GetRelativePath(workspacePath, f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dotnetProjects.Count > 0)
        {
            var best = RankCsprojCandidates(dotnetProjects).FirstOrDefault() ?? dotnetProjects[0];
            return ("dotnet", $"run --project \"{best}\"", ".NET project");
        }

        // Node.js — prefer "build" script, fallback to "generate" or "export"
        var packageJson = Path.Combine(workspacePath, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                var json = File.ReadAllText(packageJson);
                // Check for build scripts in priority order
                foreach (var script in new[] { "build", "generate", "export", "build:static" })
                {
                    if (json.Contains($"\"{script}\"", StringComparison.OrdinalIgnoreCase))
                        return ("npm", $"run {script}", $"Node.js ({script})");
                }
                // No build script but has package.json — try npm run build anyway
                return ("npm", "run build", "Node.js (build)");
            }
            catch { /* ignore parse errors */ }
        }

        // Python — look for setup.py, build.py, or manage.py (Django)
        if (File.Exists(Path.Combine(workspacePath, "manage.py")))
            return ("python", "manage.py collectstatic --noinput", "Django collectstatic");
        if (File.Exists(Path.Combine(workspacePath, "build.py")))
            return ("python", "build.py", "Python build script");

        // Ruby — Jekyll or similar
        if (File.Exists(Path.Combine(workspacePath, "Gemfile")) &&
            File.Exists(Path.Combine(workspacePath, "_config.yml")))
            return ("bundle", "exec jekyll build", "Jekyll");

        // Hugo
        if (File.Exists(Path.Combine(workspacePath, "config.toml")) &&
            Directory.Exists(Path.Combine(workspacePath, "content")))
            return ("hugo", "", "Hugo");

        // Makefile
        if (File.Exists(Path.Combine(workspacePath, "Makefile")))
            return ("make", "", "Makefile");

        return (null, null, null);
    }
}
