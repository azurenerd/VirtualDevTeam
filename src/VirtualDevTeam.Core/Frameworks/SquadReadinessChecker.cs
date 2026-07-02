using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Frameworks;

/// <summary>
/// Validates that the Squad framework and all its dependencies are installed and usable.
/// Implements <see cref="IFrameworkLifecycle"/> to integrate with the orchestrator's
/// pre-flight readiness checks.
///
/// Dependency chain: Node.js ≥22.5 → npm → gh CLI → gh auth → copilot CLI → squad-cli.
/// </summary>
public sealed class SquadReadinessChecker : IFrameworkLifecycle
{
    private readonly ILogger<SquadReadinessChecker> _logger;
    private readonly string _copilotExecutablePath;
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Minimum required Node.js version for Squad.</summary>
    private static readonly Version MinNodeVersion = new(22, 5, 0);

    public SquadReadinessChecker(ILogger<SquadReadinessChecker> logger, IOptions<VirtualDevTeamConfig>? config = null)
    {
        _logger = logger;
        _copilotExecutablePath = config?.Value.CopilotCli.ExecutablePath ?? "copilot";
    }

    public async Task<FrameworkReadinessResult> CheckReadinessAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var missing = new List<string>();

        // 1. Node.js ≥22.5
        var nodeVersion = await GetCommandVersionAsync("node", "--version", ct);
        if (nodeVersion is null)
        {
            missing.Add("Node.js ≥22.5 (not found)");
        }
        else if (!TryParseNodeVersion(nodeVersion, out var parsed) || parsed < MinNodeVersion)
        {
            missing.Add($"Node.js ≥22.5 (found {nodeVersion})");
        }

        // 2. npm
        if (!await IsCommandAvailableAsync("npm", "--version", ct))
            missing.Add("npm (not found)");

        // 2b. npx (required for MCP servers like WorkIQ)
        if (!await IsCommandAvailableAsync("npx", "--version", ct))
            missing.Add("npx (not found — required for MCP tool servers)");

        // 3. GitHub CLI
        if (!await IsCommandAvailableAsync("gh", "--version", ct))
            missing.Add("GitHub CLI 'gh' (not found)");

        // 4. GitHub auth
        if (!await IsGhAuthenticatedAsync(ct))
            missing.Add("GitHub CLI authentication ('gh auth status' failed)");

        // 5. Copilot CLI — try configured executable path first (matches CopilotCliProcessManager),
        //    then cmd.exe-based check, then direct exe scan.
        var copilotFound = false;

        // First: try the configured ExecutablePath directly (most reliable — same as CopilotCliProcessManager)
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_commandTimeout);
            var psi = new ProcessStartInfo
            {
                FileName = _copilotExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(cts.Token);
                var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogInformation("Copilot CLI found via configured path '{Path}': {Version}",
                        _copilotExecutablePath, output.Trim());
                    copilotFound = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copilot CLI check via configured path '{Path}' failed", _copilotExecutablePath);
        }

        // Second: try cmd.exe shell resolution
        if (!copilotFound)
        {
            try
            {
                var (copilotExit, copilotOut) = await RunCommandAsync("copilot", "--version", _commandTimeout, ct);
                _logger.LogInformation("Copilot CLI check via cmd: exit={ExitCode}, output='{Output}'", copilotExit, copilotOut?.Trim());
                copilotFound = copilotExit == 0 && !string.IsNullOrWhiteSpace(copilotOut);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Copilot CLI check via cmd.exe failed, trying direct exe");
            }
        }

        // Third: scan WinGet packages directory
        if (!copilotFound)
        {
            copilotFound = await TryCopilotDirectAsync(ct);
        }

        if (!copilotFound)
            missing.Add("Copilot CLI 'copilot' (not found)");

        // 6. Squad CLI
        var squadInstalled = await IsCommandAvailableAsync("squad", "--version", ct);
        if (!squadInstalled)
        {
            // Check if available via npx
            var npxAvailable = await IsCommandAvailableAsync("npx", "@bradygaster/squad-cli --version", ct);
            if (!npxAvailable)
                missing.Add("Squad CLI '@bradygaster/squad-cli' (not installed globally or via npx)");
        }

        if (missing.Count == 0)
        {
            _logger.LogInformation("Squad readiness check passed — all dependencies available");
            return new FrameworkReadinessResult(
                FrameworkReadiness.Ready,
                "All Squad dependencies are available",
                Array.Empty<string>());
        }

        // Determine severity: if only squad-cli is missing, it's installable
        var onlySquadMissing = missing.Count == 1 &&
            missing[0].Contains("Squad CLI", StringComparison.OrdinalIgnoreCase);

        var status = onlySquadMissing
            ? FrameworkReadiness.InstallRequired
            : FrameworkReadiness.MissingDependency;

        _logger.LogWarning("Squad readiness check: {Status} — missing: {Missing}",
            status, string.Join(", ", missing));

        return new FrameworkReadinessResult(status,
            $"Missing {missing.Count} dependency(ies): {string.Join(", ", missing)}",
            missing.AsReadOnly());
    }

    public async Task<FrameworkInstallResult> EnsureInstalledAsync(CancellationToken ct)
    {
        _logger.LogInformation("Attempting to install Squad CLI globally via npm...");

        try
        {
            var (exitCode, output) = await RunCommandAsync(
                "npm", "install -g @bradygaster/squad-cli",
                TimeSpan.FromMinutes(2), ct);

            if (exitCode == 0)
            {
                // Verify installation
                var available = await IsCommandAvailableAsync("squad", "--version", ct);
                if (available)
                {
                    _logger.LogInformation("Squad CLI installed successfully");
                    return new FrameworkInstallResult(true, "Squad CLI installed successfully");
                }

                _logger.LogWarning("npm install succeeded but 'squad --version' still fails");
                return new FrameworkInstallResult(false,
                    "npm install reported success but Squad CLI is not available on PATH. " +
                    "Try: npm install -g @bradygaster/squad-cli");
            }

            _logger.LogError("Squad CLI installation failed (exit {ExitCode}): {Output}",
                exitCode, output);
            return new FrameworkInstallResult(false,
                $"Installation failed (exit {exitCode}). Try manually: npm install -g @bradygaster/squad-cli\n{output}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Exception during Squad CLI installation");
            return new FrameworkInstallResult(false,
                $"Installation error: {ex.Message}. Try manually: npm install -g @bradygaster/squad-cli");
        }
    }

    // ── Helpers ──

    private async Task<string?> GetCommandVersionAsync(string command, string args, CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await RunCommandAsync(command, args, _commandTimeout, ct);
            return exitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> IsCommandAvailableAsync(string command, string args, CancellationToken ct)
    {
        var version = await GetCommandVersionAsync(command, args, ct);
        return version is not null;
    }

    private async Task<bool> IsGhAuthenticatedAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, _) = await RunCommandAsync("gh", "auth status", _commandTimeout, ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseNodeVersion(string versionString, out Version version)
    {
        // Node outputs "v22.16.0" — strip the leading 'v'
        var cleaned = versionString.TrimStart('v', 'V').Trim();
        return Version.TryParse(cleaned, out version!);
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string args, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // On Windows, many tools (npm, npx, gh) are .cmd/.bat shims that
        // ProcessStartInfo cannot find when UseShellExecute=false.
        // Route through cmd.exe to resolve them from PATH correctly.
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : command,
            Arguments = isWindows ? $"/c {command} {args}" : args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // BUG FIX: psi.Environment is populated from the current process, but when the
        // Runner is spawned from VS Code or a service the inherited PATH may be empty/truncated.
        // Read PATH directly from the system environment as a robust fallback, then augment
        // with common tool locations for npm/node.
        if (isWindows)
        {
            // Use the process-level PATH first, fall back to machine+user PATH if empty
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
                var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                currentPath = string.IsNullOrWhiteSpace(machinePath)
                    ? userPath
                    : $"{machinePath};{userPath}";
            }

            var appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            var programFilesNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
            var localAppDataPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            var programFilesGitHubCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI");

            // Scan WinGet packages directory for copilot CLI (copilot.exe may be
            // at the package root OR in a subdirectory)
            var wingetPackagesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            var wingetCopilotDirs = new List<string>();
            if (Directory.Exists(wingetPackagesDir))
            {
                try
                {
                    foreach (var pkgDir in Directory.GetDirectories(wingetPackagesDir, "GitHub.Copilot*"))
                    {
                        // Check the package root itself
                        if (File.Exists(Path.Combine(pkgDir, "copilot.exe")))
                            wingetCopilotDirs.Add(pkgDir);
                        // Also check subdirectories
                        foreach (var sub in Directory.GetDirectories(pkgDir, "*", SearchOption.AllDirectories))
                        {
                            if (File.Exists(Path.Combine(sub, "copilot.exe")))
                                wingetCopilotDirs.Add(sub);
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            var candidatePaths = new[] { appDataNpm, programFilesNode, localAppDataPrograms, programFilesGitHubCli }
                .Concat(wingetCopilotDirs);
            var extraPaths = candidatePaths
                .Where(dir => Directory.Exists(dir) && !currentPath.Contains(dir, StringComparison.OrdinalIgnoreCase));

            var augmented = string.Join(';', extraPaths.Append(currentPath));
            psi.Environment["PATH"] = augmented;

            // Diagnostic: log augmented PATH segments containing "copilot" (case-insensitive)
            var copilotSegments = augmented.Split(';').Where(s => s.Contains("copilot", StringComparison.OrdinalIgnoreCase));
            if (copilotSegments.Any())
            {
                System.Diagnostics.Debug.WriteLine($"[SquadReadiness] PATH has copilot segments: {string.Join("; ", copilotSegments)}");
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{command}'");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }

    /// <summary>
    /// Attempt to run copilot directly (not through cmd.exe) to avoid shell PATH issues.
    /// Searches the WinGet packages directory and the system PATH for copilot.exe.
    /// </summary>
    private async Task<bool> TryCopilotDirectAsync(CancellationToken ct)
    {
        // Build a list of candidate paths for copilot.exe
        var candidates = new List<string> { "copilot" }; // Will resolve via PATH

        if (OperatingSystem.IsWindows())
        {
            var wingetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetDir))
            {
                try
                {
                    foreach (var pkgDir in Directory.GetDirectories(wingetDir, "GitHub.Copilot*"))
                    {
                        var exe = Path.Combine(pkgDir, "copilot.exe");
                        if (File.Exists(exe))
                            candidates.Insert(0, exe); // prefer full path
                        foreach (var sub in Directory.GetDirectories(pkgDir, "*", SearchOption.AllDirectories))
                        {
                            exe = Path.Combine(sub, "copilot.exe");
                            if (File.Exists(exe))
                                candidates.Insert(0, exe);
                        }
                    }
                }
                catch { /* best-effort */ }
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_commandTimeout);

                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is null) continue;

                var exited = process.WaitForExit((int)_commandTimeout.TotalMilliseconds);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    continue;
                }

                if (process.ExitCode == 0)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    _logger.LogInformation("Copilot CLI found via direct exe at '{Path}': {Version}",
                        candidate, output);
                    return true;
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        _logger.LogWarning("Copilot CLI not found via any direct exe path");
        return false;
    }
}
