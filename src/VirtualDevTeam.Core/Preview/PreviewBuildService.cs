using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Preview;

/// <summary>
/// Orchestrates cloning/updating a working branch, building, and running
/// the project locally for human preview. Manages process lifecycle and
/// streams output via events.
/// </summary>
public sealed class PreviewBuildService : IDisposable
{
    private readonly ILogger<PreviewBuildService> _logger;
    private readonly VirtualDevTeamConfig _config;
    private readonly CopilotCliProcessManager? _cliProcessManager;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process? _runningProcess;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Regex to redact tokens from output (GitHub PAT, ADO PAT patterns)
    private static readonly Regex TokenRedactor = new(
        @"(https?://)([^@:]+)([:@])[^@/]+(@)",
        RegexOptions.Compiled);

    public PreviewState State { get; private set; } = PreviewState.Idle;
    public string? ErrorMessage { get; private set; }
    public string? AppUrl { get; private set; }
    public int ActualPort { get; private set; }
    public int? RunningProcessId => _runningProcess?.Id;

    /// <summary>Raised when new output lines are available (already token-redacted).</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when state changes.</summary>
    public event Action<PreviewState>? StateChanged;

    public PreviewBuildService(
        ILogger<PreviewBuildService> logger,
        IOptions<VirtualDevTeamConfig> config,
        CopilotCliProcessManager? cliProcessManager = null,
        string? settingsPath = null)
    {
        _logger = logger;
        _config = config.Value;
        _cliProcessManager = cliProcessManager;
        _settingsPath = settingsPath
            ?? Path.Combine(Directory.GetCurrentDirectory(), "preview-settings.json");
    }

    public async Task<PreviewSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_settingsPath))
            return new PreviewSettings();

        var json = await File.ReadAllTextAsync(_settingsPath, ct);
        return JsonSerializer.Deserialize<PreviewSettings>(json, JsonOpts) ?? new PreviewSettings();
    }

    public async Task SaveSettingsAsync(PreviewSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        var tmp = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, _settingsPath, overwrite: true);
    }

    /// <summary>
    /// Clone (or update) the working branch, build, and run the project.
    /// Streams output via <see cref="OutputReceived"/>.
    /// </summary>
    public async Task StartAsync(PreviewSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (State == PreviewState.Running || State == PreviewState.Cloning || State == PreviewState.Building)
        {
            _logger.LogWarning("Preview is already in state {State}, ignoring start request", State);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ClonePath))
            throw new InvalidOperationException("Clone path must be specified.");

        ErrorMessage = null;
        AppUrl = null;

        try
        {
            // === Stage 1: Clone or Update ===
            SetState(PreviewState.Cloning);
            await CloneOrUpdateAsync(settings, ct);

            // === Stage 2: Detect build/run commands ===
            // Try AI detection first (Copilot CLI analyzes the project), fall back to static patterns.
            // Note: _config.Workspace.BuildCommand describes the VDT agent workspace,
            // NOT the preview target project. Only use user's explicit override or auto-detect.
            string buildCmd, runCmd;
            if (!string.IsNullOrWhiteSpace(settings.BuildCommandOverride))
            {
                buildCmd = settings.BuildCommandOverride;
                runCmd = ResolveRunCommand(settings, 0); // port resolved later
            }
            else
            {
                var aiResult = await AiDetectProjectCommandsAsync(settings.ClonePath, ct);
                if (aiResult is not null)
                {
                    buildCmd = aiResult.Value.buildCmd;
                    runCmd = aiResult.Value.runCmd;
                    Emit($"🤖 AI detected build: {buildCmd}");
                    Emit($"🤖 AI detected run: {runCmd}");
                }
                else
                {
                    buildCmd = DetectBuildCommand(settings.ClonePath);
                    runCmd = ""; // resolved below with port
                }
            }

            // === Stage 3: Build ===
            SetState(PreviewState.Building);
            Emit($"▶ Building: {buildCmd}");
            var buildResult = await RunCommandAsync(buildCmd, settings.ClonePath, timeoutSeconds: 180, ct);
            if (buildResult != 0)
            {
                SetState(PreviewState.Failed);
                ErrorMessage = "Build failed. Check output above for errors.";
                Emit($"❌ Build failed (exit code {buildResult})");
                return;
            }
            Emit("✅ Build succeeded");

            // === Stage 4: Run ===
            SetState(PreviewState.Running);
            ActualPort = ResolvePort(settings.Port);
            if (string.IsNullOrWhiteSpace(runCmd))
                runCmd = ResolveRunCommand(settings, ActualPort);
            else
                runCmd = runCmd.Replace("{port}", ActualPort.ToString());
            Emit($"▶ Starting app on port {ActualPort}: {runCmd}");
            await StartAppProcessAsync(runCmd, settings.ClonePath, ActualPort, ct);
        }
        catch (OperationCanceledException)
        {
            SetState(PreviewState.Stopped);
            Emit("⏹ Preview cancelled.");
        }
        catch (Exception ex)
        {
            SetState(PreviewState.Failed);
            ErrorMessage = ex.Message;
            Emit($"❌ Error: {ex.Message}");
            _logger.LogError(ex, "Preview build/run failed");
        }
    }

    /// <summary>Stop the running preview process.</summary>
    public void Stop()
    {
        if (_runningProcess is { HasExited: false } proc)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                Emit("⏹ Preview process stopped.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill preview process");
            }
        }

        _runningProcess = null;
        AppUrl = null;
        SetState(PreviewState.Stopped);
    }

    /// <summary>Get current status snapshot for the API.</summary>
    public async Task<PreviewStatus> GetStatusAsync(PreviewSettings? settings, CancellationToken ct = default)
    {
        string? branch = null, sha = null, message = null;
        DateTime? lastUpdated = null;
        var clonePath = settings?.ClonePath ?? "";

        if (!string.IsNullOrWhiteSpace(clonePath) && Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            try
            {
                branch = await GetGitOutputAsync("rev-parse --abbrev-ref HEAD", clonePath, ct);
                sha = await GetGitOutputAsync("rev-parse --short HEAD", clonePath, ct);
                message = await GetGitOutputAsync("log -1 --format=%s", clonePath, ct);
                var dateStr = await GetGitOutputAsync("log -1 --format=%aI", clonePath, ct);
                if (DateTime.TryParse(dateStr, out var dt))
                    lastUpdated = dt.ToUniversalTime();
            }
            catch { /* git info is best-effort */ }
        }

        return new PreviewStatus
        {
            State = State,
            ErrorMessage = ErrorMessage,
            AppUrl = AppUrl,
            ProcessId = RunningProcessId,
            BranchName = branch,
            HeadCommitSha = sha,
            HeadCommitMessage = message,
            LastUpdatedUtc = lastUpdated,
            ActualPort = ActualPort
        };
    }

    #region Private Helpers

    /// <summary>
    /// Use Copilot CLI to intelligently analyze the project structure and determine
    /// the correct build and run commands. Returns null if CLI is unavailable or fails.
    /// Falls back to static pattern matching on failure.
    /// </summary>
    private async Task<(string buildCmd, string runCmd)?> AiDetectProjectCommandsAsync(
        string projectPath, CancellationToken ct)
    {
        if (_cliProcessManager is null)
            return null;

        try
        {
            // Build a file listing for the AI to analyze (max 200 files to keep prompt small)
            var files = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".git") && !f.Contains("node_modules") && !f.Contains("bin") && !f.Contains("obj"))
                .Select(f => Path.GetRelativePath(projectPath, f))
                .Take(200)
                .ToList();

            // Read key project descriptor files for richer context
            var descriptorContent = new StringBuilder();
            var descriptors = new[] { "*.sln", "*.csproj", "package.json", "Cargo.toml", "go.mod", "Makefile", "docker-compose.yml", "Dockerfile" };
            foreach (var pattern in descriptors)
            {
                foreach (var file in Directory.GetFiles(projectPath, pattern, SearchOption.AllDirectories).Take(5))
                {
                    var relPath = Path.GetRelativePath(projectPath, file);
                    // Skip large files — just need the first ~50 lines for project metadata
                    var lines = File.ReadLines(file).Take(50);
                    descriptorContent.AppendLine($"--- {relPath} ---");
                    descriptorContent.AppendLine(string.Join('\n', lines));
                    descriptorContent.AppendLine();
                }
            }

            var portPlaceholder = "{port}";
            var prompt = $"""
                Analyze this project structure and determine the correct build and run commands.
                The project is cloned at: {projectPath}

                Files in the project:
                {string.Join('\n', files)}

                Key project files:
                {descriptorContent}

                Respond with EXACTLY two lines, no explanation, no markdown:
                BUILD: <the shell command to build this project>
                RUN: <the shell command to run this project, use {portPlaceholder} as placeholder for the port number>

                Rules:
                - For .NET projects with .sln in a subdirectory, use: dotnet build "relative/path/to/file.sln"
                - For .NET projects without .sln, use: dotnet build "relative/path/to/file.csproj"
                - For .NET run commands, find the web project (.csproj with Microsoft.NET.Sdk.Web) and use: dotnet run --project "relative/path" --urls http://localhost:{portPlaceholder}
                - For Node.js, use npm commands
                - Commands must work when run from the project root: {projectPath}
                - Use relative paths from the project root
                """;

            Emit("🤖 Asking AI to analyze project structure...");

            // Use fast model for this quick analysis (60s timeout — CLI startup can be slow)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            CopilotCliResult result;
            try
            {
                result = await _cliProcessManager.ExecutePromptAsync(
                    prompt, modelOverride: "claude-haiku-4.5", ct: cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our internal timeout fired, not the caller's cancellation — graceful fallback
                _logger.LogDebug("AI project detection timed out after 60s — falling back to static detection");
                Emit("⚠️ AI detection timed out — using static detection");
                return null;
            }

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogDebug("AI project detection failed: {Error}", result.Error ?? "empty response");
                Emit("⚠️ AI detection failed — using static detection");
                return null;
            }

            // Parse the BUILD: and RUN: lines from response
            var output = result.Output;
            string? buildCmd = null, runCmd = null;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("BUILD:", StringComparison.OrdinalIgnoreCase))
                    buildCmd = trimmed["BUILD:".Length..].Trim();
                else if (trimmed.StartsWith("RUN:", StringComparison.OrdinalIgnoreCase))
                    runCmd = trimmed["RUN:".Length..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(buildCmd) && !string.IsNullOrWhiteSpace(runCmd))
            {
                _logger.LogInformation("AI detected build command: {BuildCmd}, run command: {RunCmd}", buildCmd, runCmd);
                return (buildCmd, runCmd);
            }

            _logger.LogDebug("AI response did not contain valid BUILD:/RUN: lines: {Output}", output);
            Emit("⚠️ AI response unparseable — using static detection");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AI project detection failed — falling back to static detection");
            Emit("⚠️ AI detection unavailable — using static detection");
            return null;
        }
    }

    private void SetState(PreviewState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void Emit(string line)
    {
        var redacted = RedactTokens(line);
        OutputReceived?.Invoke(redacted);
    }

    private static string RedactTokens(string input)
    {
        // Redact PATs in URLs: https://token@github.com → https://***@github.com
        return TokenRedactor.Replace(input, "$1***$4");
    }

    private async Task CloneOrUpdateAsync(PreviewSettings settings, CancellationToken ct)
    {
        var clonePath = settings.ClonePath;
        var branch = _config.Project.WorkingBranch ?? _config.Project.DefaultBranch;

        // Resolve the best clone source: GitHub/ADO remote (preferred) or local bare repo (fallback).
        var cloneUrl = ResolvePreviewCloneUrl(branch);
        var isRemote = cloneUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        Emit($"📡 Preview source: {(isRemote ? "remote" : "local")} ({cloneUrl[..Math.Min(80, cloneUrl.Length)]}{(cloneUrl.Length > 80 ? "..." : "")})");

        if (Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            Emit($"📂 Repository already exists at {clonePath}");

            // If the origin remote changed (e.g., bare repo → GitHub after final submission),
            // update it so fetch/pull go to the right place.
            try
            {
                var currentOrigin = await GetGitOutputAsync("remote get-url origin", clonePath, ct) ?? "";
                if (!string.Equals(currentOrigin.Trim(), cloneUrl.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Emit($"🔄 Updating origin remote to {(isRemote ? "GitHub" : "local")}...");
                    await RunCommandAsync($"git -C \"{clonePath}\" remote set-url origin \"{cloneUrl}\"", clonePath, 10, ct);
                }
            }
            catch { /* best-effort remote update */ }

            Emit($"🔄 Pulling latest from {branch}...");

            await RunCommandAsync($"git -C \"{clonePath}\" fetch origin", clonePath, 60, ct);
            await RunCommandAsync($"git -C \"{clonePath}\" checkout {branch}", clonePath, 30, ct);
            var pullResult = await RunCommandAsync(
                $"git -C \"{clonePath}\" pull origin {branch}", clonePath, 120, ct);

            if (pullResult != 0)
            {
                Emit("⚠️ Pull had conflicts or errors. Attempting hard reset to remote...");
                await RunCommandAsync(
                    $"git -C \"{clonePath}\" reset --hard origin/{branch}", clonePath, 30, ct);
            }

            Emit("✅ Repository updated");
        }
        else
        {
            // Create parent directory if needed
            var parent = Path.GetDirectoryName(clonePath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            Emit($"📥 Cloning {branch} to {clonePath}...");
            var result = await RunCommandAsync(
                $"git clone --branch {branch} --single-branch \"{cloneUrl}\" \"{clonePath}\"",
                parent ?? Directory.GetCurrentDirectory(), 300, ct);

            if (result != 0)
                throw new InvalidOperationException("Git clone failed. Check output for details.");

            Emit("✅ Clone complete");
        }
    }

    /// <summary>
    /// Resolve the best clone URL for preview builds.
    /// Always prefer the real GitHub/ADO remote when configured — that's where the
    /// final submitted code lives. Only fall back to the local bare repo when no
    /// remote is available (rare edge case).
    /// </summary>
    private string ResolvePreviewCloneUrl(string branch)
    {
        // Non-local mode: always use the configured remote
        if (_config.DevPlatform.Platform != DevPlatformType.Local)
            return _config.GetGitCloneUrl();

        // Local mode: prefer the real GitHub/ADO remote if configured.
        // After final submission, the code is on the remote — that's the authoritative source.
        // The bare repo is only useful mid-run before final submission pushes to remote.
        if (!string.IsNullOrWhiteSpace(_config.Project.GitHubRepo))
        {
            // Check if the remote actually has the branch (final submission may have pushed it)
            try
            {
                var psi = new ProcessStartInfo("git",
                    $"ls-remote --heads https://github.com/{_config.Project.GitHubRepo}.git {branch}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd() ?? "";
                proc?.WaitForExit(10000);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogInformation("Preview: using GitHub remote (branch {Branch} exists on remote)", branch);
                    return $"https://github.com/{_config.Project.GitHubRepo}.git";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Preview: could not check GitHub remote for branch — trying bare repo");
            }
        }

        // Fallback: try local bare repo (mid-run, before final submission)
        var bareRepoUrl = _config.GetGitCloneUrl();
        if (Directory.Exists(bareRepoUrl))
        {
            _logger.LogInformation("Preview: using local bare repo (branch not yet on remote)");
            return bareRepoUrl;
        }

        // Last resort: construct GitHub URL even if branch check failed
        if (!string.IsNullOrWhiteSpace(_config.Project.GitHubRepo))
        {
            _logger.LogInformation("Preview: using GitHub remote (bare repo not found)");
            return $"https://github.com/{_config.Project.GitHubRepo}.git";
        }

        _logger.LogWarning("Preview: no remote configured and bare repo not found");
        return bareRepoUrl;
    }

    private async Task<int> RunCommandAsync(string command, string workingDir, int timeoutSeconds, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) Emit(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            proc.Kill(entireProcessTree: true);
            Emit($"⏱ Command timed out after {timeoutSeconds}s");
            return -1;
        }
    }

    private async Task StartAppProcessAsync(string command, string workingDir, int port, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Force the app to listen on our port via environment variable.
        // ASPNETCORE_URLS takes precedence over --urls, appsettings.json Kestrel config,
        // and UseUrls() calls — ensuring the app uses the port we expect.
        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        psi.Environment["URLS"] = $"http://localhost:{port}"; // fallback for non-ASP.NET apps

        var proc = new Process { StartInfo = psi };
        var detectedPort = 0;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Emit(e.Data);
            // Detect actual listening port from ASP.NET Core output
            if (detectedPort == 0 && e.Data.Contains("Now listening on: http"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"localhost:(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed != port)
                {
                    detectedPort = parsed;
                    AppUrl = $"http://localhost:{parsed}";
                    ActualPort = parsed;
                    Emit($"🔄 Detected actual port: {parsed} (updating from configured {port})");
                }
            }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _runningProcess = proc;

        // Wait for app to be ready (poll HTTP)
        // Don't set AppUrl yet — wait for port confirmation from stdout or HTTP probe
        var candidateUrl = $"http://localhost:{port}";
        Emit($"⏳ Waiting for app to start (probing port {port})...");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (proc.HasExited)
            {
                SetState(PreviewState.Failed);
                ErrorMessage = $"App process exited with code {proc.ExitCode}";
                Emit($"❌ App exited immediately (code {proc.ExitCode})");
                return;
            }

            // If stdout detected a different port, use that instead
            if (detectedPort != 0)
            {
                candidateUrl = $"http://localhost:{detectedPort}";
            }

            try
            {
                var resp = await httpClient.GetAsync(candidateUrl, ct);
                if ((int)resp.StatusCode < 500)
                {
                    AppUrl = candidateUrl;
                    ActualPort = detectedPort != 0 ? detectedPort : port;
                    Emit($"✅ App is running at {AppUrl}");
                    return;
                }
            }
            catch { /* not ready yet */ }

            await Task.Delay(1000, ct);
        }

        // Even if not responding to HTTP, the process is running — set URL from best known port
        if (!proc.HasExited)
        {
            AppUrl = detectedPort != 0 ? $"http://localhost:{detectedPort}" : candidateUrl;
            Emit($"⚠️ App process is running but not responding to HTTP at {AppUrl}. It may use a different port or be a non-web project.");
        }
    }

    private string ResolveRunCommand(PreviewSettings settings, int port)
    {
        if (!string.IsNullOrWhiteSpace(settings.RunCommandOverride))
            return settings.RunCommandOverride.Replace("{port}", port.ToString());

        // Note: _config.Workspace.AppStartCommand describes VDT agent workspace,
        // NOT the preview target project. Only use user's explicit override or auto-detect.
        return DetectRunCommand(settings.ClonePath, port);
    }

    private int ResolvePort(int preferred)
    {
        if (preferred > 0 && IsPortFree(preferred))
            return preferred;

        // Find a free port in the 5100-5199 range
        for (int p = 5100; p < 5200; p++)
        {
            if (IsPortFree(p)) return p;
        }

        // Fallback: OS-assigned
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Auto-detect build command from project files (searches subdirectories).</summary>
    private static string DetectBuildCommand(string projectPath)
    {
        // Check root first
        if (Directory.GetFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
            return "dotnet build";
        if (Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            return "dotnet build";
        if (File.Exists(Path.Combine(projectPath, "package.json")))
            return "npm install && npm run build";
        if (File.Exists(Path.Combine(projectPath, "requirements.txt")))
            return "pip install -r requirements.txt";
        if (File.Exists(Path.Combine(projectPath, "Cargo.toml")))
            return "cargo build";
        if (File.Exists(Path.Combine(projectPath, "go.mod")))
            return "go build ./...";

        // Search subdirectories for .sln or .csproj (agent-built repos often lack root-level project files)
        var slnFiles = Directory.GetFiles(projectPath, "*.sln", SearchOption.AllDirectories);
        if (slnFiles.Length > 0)
        {
            var relPath = Path.GetRelativePath(projectPath, slnFiles[0]);
            return $"dotnet build \"{relPath}\"";
        }

        var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
        if (csprojFiles.Length > 0)
        {
            // Prefer src/ projects over test projects
            var srcProj = csprojFiles.FirstOrDefault(f => f.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                          ?? csprojFiles[0];
            var relPath = Path.GetRelativePath(projectPath, srcProj);
            return $"dotnet build \"{relPath}\"";
        }

        // Check subdirectories for Node.js projects
        var packageJsonFiles = Directory.GetFiles(projectPath, "package.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains("node_modules"))
            .ToArray();
        if (packageJsonFiles.Length > 0)
        {
            var dir = Path.GetDirectoryName(packageJsonFiles[0])!;
            var relDir = Path.GetRelativePath(projectPath, dir);
            if (relDir == ".")
                return "npm install && npm run build";
            return $"cd \"{relDir}\" && npm install && npm run build";
        }

        return "echo No build system detected";
    }

    /// <summary>Auto-detect run command from project files (searches subdirectories).</summary>
    private static string DetectRunCommand(string projectPath, int port)
    {
        // .NET projects — search all directories for .sln or .csproj
        var slnFiles = Directory.GetFiles(projectPath, "*.sln", SearchOption.AllDirectories);
        if (slnFiles.Length > 0)
        {
            // Find a web project (has launchSettings.json or is a Blazor/ASP.NET project)
            var webProjects = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
                .Where(f => {
                    try
                    {
                        var content = File.ReadAllText(f);
                        return content.Contains("Microsoft.NET.Sdk.Web") ||
                               content.Contains("Microsoft.NET.Sdk.BlazorWebAssembly");
                    }
                    catch { return false; }
                })
                // Prefer non-test projects: test projects use Sdk.Web for WebApplicationFactory
                // but exit immediately when run — they're not real web apps
                .OrderBy(f => IsTestProjectPath(f) ? 1 : 0)
                .ToList();

            if (webProjects.Count > 0)
            {
                var projDir = Path.GetDirectoryName(webProjects[0])!;
                var relPath = Path.GetRelativePath(projectPath, projDir);
                return $"dotnet run --project \"{relPath}\" --urls http://localhost:{port}";
            }

            // Console app — run from .sln directory
            var slnDir = Path.GetDirectoryName(slnFiles[0])!;
            var slnRel = Path.GetRelativePath(projectPath, slnDir);
            if (slnRel == ".")
                return "dotnet run";
            return $"dotnet run --project \"{slnRel}\"";
        }

        // No .sln — search for .csproj files
        var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
        if (csprojFiles.Length > 0)
        {
            // Prefer web projects (excluding test projects that use Sdk.Web for WebApplicationFactory)
            var webProj = csprojFiles
                .OrderBy(f => IsTestProjectPath(f) ? 1 : 0)
                .FirstOrDefault(f => {
                try
                {
                    var content = File.ReadAllText(f);
                    return content.Contains("Microsoft.NET.Sdk.Web") ||
                           content.Contains("Microsoft.NET.Sdk.BlazorWebAssembly");
                }
                catch { return false; }
            });

            if (webProj is not null)
            {
                var projDir = Path.GetDirectoryName(webProj)!;
                var relPath = Path.GetRelativePath(projectPath, projDir);
                return $"dotnet run --project \"{relPath}\" --urls http://localhost:{port}";
            }

            // Non-web .csproj — prefer src/ over tests/
            var srcProj = csprojFiles.FirstOrDefault(f => f.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                          ?? csprojFiles[0];
            var srcDir = Path.GetDirectoryName(srcProj)!;
            var srcRel = Path.GetRelativePath(projectPath, srcDir);
            return $"dotnet run --project \"{srcRel}\" --urls http://localhost:{port}";
        }

        // Node.js projects — check root then subdirectories
        var packageJsonFiles = new[] { Path.Combine(projectPath, "package.json") }
            .Concat(Directory.GetFiles(projectPath, "package.json", SearchOption.AllDirectories)
                .Where(f => !f.Contains("node_modules")))
            .Where(File.Exists)
            .Distinct()
            .ToArray();

        if (packageJsonFiles.Length > 0)
        {
            var pkgPath = packageJsonFiles[0];
            var pkgJson = File.ReadAllText(pkgPath);
            var pkgDir = Path.GetDirectoryName(pkgPath)!;
            var pkgRel = Path.GetRelativePath(projectPath, pkgDir);
            var cdPrefix = pkgRel == "." ? "" : $"cd \"{pkgRel}\" && ";

            if (pkgJson.Contains("\"dev\""))
                return $"{cdPrefix}npx cross-env PORT={port} npm run dev";
            if (pkgJson.Contains("\"start\""))
                return $"{cdPrefix}npx cross-env PORT={port} npm start";
        }

        // Python
        if (File.Exists(Path.Combine(projectPath, "manage.py")))
            return $"python manage.py runserver 0.0.0.0:{port}";
        if (File.Exists(Path.Combine(projectPath, "app.py")) || File.Exists(Path.Combine(projectPath, "main.py")))
            return $"python -m uvicorn main:app --port {port}";

        return $"echo No run command detected for port {port}";
    }

    /// <summary>
    /// Checks if a .csproj path looks like a test project.
    /// Test projects using Microsoft.NET.Sdk.Web (for WebApplicationFactory) are not real web apps
    /// and exit immediately when run via `dotnet run`.
    /// </summary>
    private static bool IsTestProjectPath(string csprojPath)
    {
        var normalized = csprojPath.Replace('\\', '/');
        return normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".Test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Test.csproj", StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(csprojPath).Equals("tests.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> GetGitOutputAsync(string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return output?.Trim();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _lock.Dispose();
    }
}
