using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Frameworks;

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
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Minimum required Node.js version for Squad.</summary>
    private static readonly Version MinNodeVersion = new(22, 5, 0);

    public SquadReadinessChecker(ILogger<SquadReadinessChecker> logger)
    {
        _logger = logger;
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

        // 3. GitHub CLI
        if (!await IsCommandAvailableAsync("gh", "--version", ct))
            missing.Add("GitHub CLI 'gh' (not found)");

        // 4. GitHub auth
        if (!await IsGhAuthenticatedAsync(ct))
            missing.Add("GitHub CLI authentication ('gh auth status' failed)");

        // 5. Copilot CLI
        if (!await IsCommandAvailableAsync("copilot", "--version", ct))
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
            $"Missing {missing.Count} dependency(ies)",
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{command}'");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }
}
