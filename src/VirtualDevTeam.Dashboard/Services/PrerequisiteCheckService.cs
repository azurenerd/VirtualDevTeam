using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Frameworks;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Checks and installs development tool prerequisites.
/// UI-agnostic — can be consumed by Blazor, API endpoints, or a future React/TypeScript UI.
/// </summary>
public sealed class PrerequisiteCheckService
{
    private readonly ILogger<PrerequisiteCheckService> _logger;
    private readonly SquadReadinessChecker? _squadChecker;

    public PrerequisiteCheckService(ILogger<PrerequisiteCheckService> logger, SquadReadinessChecker? squadChecker = null)
    {
        _logger = logger;
        _squadChecker = squadChecker;
    }

    /// <summary>Returns the default list of prerequisite tool definitions.</summary>
    public static IReadOnlyList<PrerequisiteTool> GetDefaultTools() =>
    [
        new("GitHub CLI", "gh --version", "Authentication & API access",
            "winget install GitHub.cli", PrereqInstallKind.Command, false,
            "The GitHub CLI (gh) provides authenticated access to the GitHub API. Agents use it to create PRs, manage issues, post comments, and coordinate work through your repository.",
            "Agents cannot interact with GitHub. No PRs, issues, or code reviews will be created."),
        new("GitHub Copilot CLI", "copilot --version", "AI-powered code generation",
            "winget install GitHub.Copilot", PrereqInstallKind.Command, false,
            "The Copilot CLI is the AI backbone — every agent uses it for code generation, analysis, reviews, and multi-turn reasoning. Requires an active Copilot subscription.",
            "No AI capabilities. Agents cannot generate code, perform reviews, or make decisions."),
        new("Agency CLI", "agency --version", "MSFT Entra authentication wrapper",
            "winget install Microsoft.Agency", PrereqInstallKind.Command, true,
            "Agency wraps the Copilot CLI with Microsoft Entra authentication and provides additional MCP servers (WorkIQ, EngHub, Learn). Recommended for Microsoft employees. When not installed, VDT calls copilot directly.",
            "VDT will call copilot directly without Entra auth. Non-Microsoft users don't need this."),
        new("Node.js 18+", "node --version", "MCP servers & frontend preview",
            "https://nodejs.org", PrereqInstallKind.Url, false,
            "Node.js runs MCP (Model Context Protocol) servers that extend agent capabilities — Playwright for browser testing, file system access, and more. Also needed for frontend project previews.",
            "MCP servers won't start. Agents lose browser testing, extended tool access, and frontend preview capabilities."),
        new(".NET 8+ SDK", "dotnet --version", "Build & run the solution",
            "https://dot.net", PrereqInstallKind.Url, false,
            "The .NET SDK (8 or newer) builds and runs the VirtualDevTeam solution itself, and is required for any .NET-based target projects. Agents use it for build verification and test execution.",
            "Cannot build or run VirtualDevTeam. Agents cannot verify builds or run tests for .NET projects."),
        new("PowerShell 7+", "pwsh --version", "Script execution & browser install",
            "winget install Microsoft.PowerShell", PrereqInstallKind.Command, false,
            "PowerShell 7+ (pwsh) is required for reset scripts, Playwright browser installation, and various automation tasks. Windows PowerShell 5.1 (powershell.exe) is not sufficient.",
            "Reset scripts, Playwright browser auto-install, and some automation workflows will fail. Manual workarounds required."),
        new("ffmpeg", "ffmpeg -version", "Video trimming & GIF generation",
            "winget install Gyan.FFmpeg", PrereqInstallKind.Command, true,
            "ffmpeg trims Playwright test recordings and converts them to GIFs for PR previews and the Testing dashboard. Purely cosmetic — agents function without it.",
            "Test recordings won't be trimmed or converted to GIFs. PR previews will use static screenshots instead."),
        new("Azure CLI", "az version", "Azure DevOps integration",
            "winget install Microsoft.AzureCLI", PrereqInstallKind.Command, true,
            "The Azure CLI provides authentication for Azure DevOps projects. Only needed if your target repository is on Azure DevOps (not GitHub).",
            "Cannot use Azure DevOps as a platform. GitHub projects are unaffected."),
        new("Squad CLI", "__squad_check__", "Multi-agent parallel development",
            "__squad_install__", PrereqInstallKind.Command, true,
            "Squad is an optional multi-agent framework that coordinates multiple AI sub-agents (lead developer, frontend, backend, tester) working in parallel. It can produce higher-quality results for complex tasks but uses more premium requests.",
            "The Squad strategy won't be available in the Frameworks page. Single-agent strategies (Copilot CLI, direct generation) still work."),
        new("Playwright Chromium", "__playwright_check__", "Browser testing & screenshots",
            "npx -y playwright install chromium", PrereqInstallKind.Command, false,
            "Playwright's bundled Chromium browser is used by agents to run E2E tests, capture screenshots for PR previews, and validate UI changes. Installed to a local cache (~250 MB), not system-wide.",
            "Agents cannot run browser tests, capture screenshots, or validate UI. PR preview images will be unavailable."),
        new("Windows Long Paths", "__longpath_check__", "Support deep directory paths",
            "__longpath_install__", PrereqInstallKind.Command, true,
            "Windows LongPathsEnabled removes the 260-character path limit. Strategy candidate worktrees can exceed this limit with deep npm/dotnet paths. Requires one-time registry change (needs admin).",
            "Strategy evaluation may fail with 'path too long' errors in deep worktrees. Set HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled=1."),
    ];

    /// <summary>Check if a tool is available and return its version.</summary>
    public async Task<PrereqCheckResult> CheckToolAsync(string checkCommand, CancellationToken ct)
    {
        if (checkCommand == "__playwright_check__")
            return CheckPlaywrightChromium();
        if (checkCommand == "__squad_check__")
            return CheckSquadCli();
        if (checkCommand == "__longpath_check__")
            return CheckLongPathsEnabled();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = checkCommand.StartsWith("az ", StringComparison.OrdinalIgnoreCase) ? 30000 : 5000;
            cts.CancelAfter(timeout);
            var psi = CreateFreshPathPsi(checkCommand);
            var proc = Process.Start(psi);
            if (proc is null) return PrereqCheckResult.NotFound;
            using (proc)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await proc.WaitForExitAsync(cts.Token);
                if (proc.ExitCode != 0) return PrereqCheckResult.NotFound;
                var output = await stdoutTask;
                var firstLine = output.Split('\n').FirstOrDefault()?.Trim();
                var version = Regex.Match(firstLine ?? "", @"[\d]+\.[\d]+[\.\d]*").Value;
                return new PrereqCheckResult(true, string.IsNullOrEmpty(version) ? "installed" : version);
            }
        }
        catch
        {
            return PrereqCheckResult.NotFound;
        }
    }

    /// <summary>Check GitHub CLI authentication status.</summary>
    public async Task<(bool authenticated, string? username)> CheckGitHubAuthAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            var psi = CreateFreshPathPsi("gh auth status");
            var proc = Process.Start(psi);
            if (proc is null) return (false, null);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var combined = stderr + stdout;
            if (proc.ExitCode == 0 || combined.Contains("Logged in"))
            {
                var match = Regex.Match(combined, @"Logged in to [^ ]+ as ([^\s(]+)");
                return (true, match.Success ? match.Groups[1].Value : "user");
            }
        }
        catch { }
        return (false, null);
    }

    /// <summary>Check Azure CLI authentication status.</summary>
    public async Task<bool> CheckAzureAuthAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            var psi = CreateFreshPathPsi("az account show");
            var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Install a tool and return the result.</summary>
    public async Task<PrereqInstallResult> InstallToolAsync(PrerequisiteTool tool, CancellationToken ct)
    {
        if (tool.InstallCommand == "__squad_install__")
            return await InstallSquadAsync(ct);
        if (tool.InstallCommand == "__longpath_install__")
            return InstallLongPaths();

        Process? proc = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            var fullCommand = tool.InstallCommand.StartsWith("winget", StringComparison.OrdinalIgnoreCase)
                ? tool.InstallCommand + " --accept-source-agreements --accept-package-agreements"
                : tool.InstallCommand;

            var psi = CreateFreshPathPsi(fullCommand);
            proc = Process.Start(psi);
            if (proc is null)
                return new PrereqInstallResult(false, "Failed to start installer");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode == 0)
            {
                var check = await CheckToolAsync(tool.CheckCommand, cts.Token);
                if (check.Found)
                    return new PrereqInstallResult(true, $"Installed {check.Version ?? "successfully"}", check.Version);
                else
                    return new PrereqInstallResult(true, "Installed — will be available after Runner restart");
            }

            var errorSummary = (stderr + stdout).Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .FirstOrDefault() ?? "Unknown error";
            return new PrereqInstallResult(false, errorSummary.Length > 80 ? errorSummary[..80] + "…" : errorSummary);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(entireProcessTree: true); } catch { }
            return new PrereqInstallResult(false, "Install timed out (5 min limit)");
        }
        catch (Exception ex)
        {
            try { proc?.Kill(entireProcessTree: true); } catch { }
            var msg = ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message;
            return new PrereqInstallResult(false, msg);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private async Task<PrereqInstallResult> InstallSquadAsync(CancellationToken ct)
    {
        if (_squadChecker is null)
            return new PrereqInstallResult(false, "SquadReadinessChecker not available");

        try
        {
            var result = await _squadChecker.EnsureInstalledAsync(ct);
            var msg = result.Message?.Length > 80 ? result.Message[..80] + "…" : (result.Message ?? (result.Succeeded ? "Installed" : "Failed"));
            return new PrereqInstallResult(result.Succeeded, msg);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message;
            return new PrereqInstallResult(false, msg);
        }
    }

    private static PrereqCheckResult CheckPlaywrightChromium()
    {
        var browsersPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        if (!Directory.Exists(browsersPath)) return PrereqCheckResult.NotFound;
        var chromiumDirs = Directory.GetDirectories(browsersPath, "chromium-*")
            .OrderByDescending(d => d).ToArray();
        if (chromiumDirs.Length == 0) return PrereqCheckResult.NotFound;

        var chromeExe = Directory.GetFiles(chromiumDirs[0], "chrome.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (chromeExe is not null)
        {
            try
            {
                var ver = FileVersionInfo.GetVersionInfo(chromeExe).ProductVersion;
                if (!string.IsNullOrEmpty(ver)) return new PrereqCheckResult(true, $"Chromium {ver}");
            }
            catch { }
        }

        var dirName = Path.GetFileName(chromiumDirs[0]);
        return new PrereqCheckResult(true, dirName?.Replace("chromium-", "rev ") ?? "installed");
    }

    private static PrereqCheckResult CheckSquadCli()
    {
        // Primary: check via npm global list
        try
        {
            var psi = CreateFreshPathPsi("npm list -g @bradygaster/squad-cli --depth=0");
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                using (proc)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0)
                    {
                        var match = Regex.Match(output, @"squad-cli@([\d.]+)");
                        if (match.Success) return new PrereqCheckResult(true, match.Groups[1].Value);
                    }
                }
            }
        }
        catch { }

        // Fallback: check for squad binary on PATH
        var freshPath = GetFreshPath();
        foreach (var dir in freshPath.Split(';'))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, "squad.cmd")) ||
                    File.Exists(Path.Combine(dir, "squad")) ||
                    File.Exists(Path.Combine(dir, "squad.exe")))
                    return new PrereqCheckResult(true, "installed");
            }
            catch { }
        }
        return PrereqCheckResult.NotFound;
    }

    /// <summary>Reads fresh PATH from the Windows registry (not the Runner's stale inherited PATH).</summary>
    internal static string GetFreshPath()
    {
        var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        return $"{machinePath};{userPath}";
    }

    /// <summary>Check if Windows LongPathsEnabled registry key is set.</summary>
    private static PrereqCheckResult CheckLongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return new PrereqCheckResult(true, "n/a (not Windows)");

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem");
            var value = key?.GetValue("LongPathsEnabled");
            if (value is int intVal && intVal == 1)
                return new PrereqCheckResult(true, "enabled");
            return PrereqCheckResult.NotFound;
        }
        catch
        {
            return PrereqCheckResult.NotFound;
        }
    }

    /// <summary>Enable Windows LongPathsEnabled via registry (requires admin elevation).</summary>
    private static PrereqInstallResult InstallLongPaths()
    {
        if (!OperatingSystem.IsWindows())
            return new PrereqInstallResult(true, "Not Windows — long paths supported natively");

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem", writable: true);
            if (key is null)
                return new PrereqInstallResult(false,
                    "Cannot open registry key. Run as Administrator, or set manually:\n" +
                    "reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem /v LongPathsEnabled /t REG_DWORD /d 1 /f");

            key.SetValue("LongPathsEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return new PrereqInstallResult(true, "LongPathsEnabled set to 1 — restart may be needed for full effect", "enabled");
        }
        catch (UnauthorizedAccessException)
        {
            return new PrereqInstallResult(false,
                "Administrator access required. Run this command in an elevated PowerShell:\n" +
                "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem' -Name 'LongPathsEnabled' -Value 1");
        }
        catch (Exception ex)
        {
            return new PrereqInstallResult(false, $"Registry update failed: {ex.Message}");
        }
    }

    /// <summary>Creates a ProcessStartInfo that uses cmd /c with fresh PATH from the registry.</summary>
    internal static ProcessStartInfo CreateFreshPathPsi(string command)
    {
        var psi = new ProcessStartInfo("cmd", $"/c {command}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PATH"] = GetFreshPath();
        return psi;
    }
}

/// <summary>Result of checking whether a prerequisite tool is installed.</summary>
public sealed record PrereqCheckResult(bool Found, string? Version = null)
{
    public static readonly PrereqCheckResult NotFound = new(false);
}

/// <summary>Result of installing a prerequisite tool.</summary>
public sealed record PrereqInstallResult(bool Succeeded, string Message, string? Version = null);

/// <summary>How a prerequisite tool is installed.</summary>
public enum PrereqInstallKind { Command, Url, Manual }

/// <summary>Definition of a prerequisite tool with check/install metadata.</summary>
public sealed record PrerequisiteTool(
    string Name,
    string CheckCommand,
    string Purpose,
    string InstallCommand,
    PrereqInstallKind InstallKind,
    bool IsOptional,
    string DetailedDescription,
    string ImpactWithout)
{
    public bool CanAutoInstall => InstallKind == PrereqInstallKind.Command;
}
