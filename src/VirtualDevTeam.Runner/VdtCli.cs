using System.Diagnostics;
using System.Reflection;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Runner;

/// <summary>
/// CLI entry point for VDT distribution.
/// Parses command-line arguments before the ASP.NET host starts.
/// 
/// Commands:
///   vdt start [--port N] [--headless]  — Start the VDT server (default if no command)
///   vdt check-deps [--install]          — Check prerequisites, optionally auto-install
///   vdt version                         — Print version and exit
///   vdt yolo "description"              — Quick start: InPlace on CWD, auto-approve, headless
/// </summary>
public static class VdtCli
{
    /// <summary>
    /// Process CLI arguments. Returns true if the command was handled (caller should exit).
    /// Returns false if the server should start normally.
    /// </summary>
    public static async Task<bool> HandleCliAsync(string[] args)
    {
        if (args.Length == 0)
            return false; // No args = start server (legacy behavior)

        var command = args[0].ToLowerInvariant();

        return command switch
        {
            "version" or "--version" or "-v" => HandleVersion(),
            "check-deps" => await HandleCheckDepsAsync(args.Skip(1).ToArray()),
            "help" or "--help" or "-h" => HandleHelp(),
            "start" => false, // Fall through to normal server startup
            _ => false, // Unknown command = start server (pass args through)
        };
    }

    /// <summary>
    /// Extract CLI flags relevant to server startup (--port, --headless, etc.)
    /// </summary>
    public static CliStartupOptions ParseStartupOptions(string[] args)
    {
        var options = new CliStartupOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var port))
                        options.Port = port;
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--auto-approve":
                    options.AutoApprove = true;
                    break;
                case "--open-browser":
                    options.OpenBrowser = true;
                    break;
                case "--no-open-browser":
                    options.OpenBrowser = false;
                    break;
                case "--project" or "--path" or "-p" when i + 1 < args.Length:
                    options.ProjectPath = args[++i];
                    break;
            }
        }

        return options;
    }

    private static bool HandleVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        Console.WriteLine($"VirtualDevTeam {informational}");
        return true;
    }

    private static bool HandleHelp()
    {
        Console.WriteLine("""
            VirtualDevTeam — AI-powered multi-agent development team
            
            Usage:
              vdt                          Start the server (default)
              vdt start [options]          Start the VDT server
              vdt check-deps [--install]   Check prerequisites
              vdt version                  Print version
              vdt help                     Show this help
            
            Start options:
              --port N                     Server port (default: 5050)
              --headless                   Skip dashboard UI, stream events to stdout
              --auto-approve               Auto-approve all human gates
              --open-browser               Open dashboard in browser on startup
              --no-open-browser            Don't open browser
            
            Examples:
              vdt start --port 8080        Start on port 8080
              vdt check-deps --install     Check and auto-install missing tools
              vdt start --open-browser     Start and open dashboard in browser
            """);
        return true;
    }

    private static async Task<bool> HandleCheckDepsAsync(string[] args)
    {
        var autoInstall = args.Contains("--install");
        Console.WriteLine("🔍 Checking VirtualDevTeam prerequisites...\n");

        var checks = new (string name, string command, string installHint, bool required)[]
        {
            ("Git", "git --version", "winget install Git.Git", true),
            ("GitHub CLI", "gh --version", "winget install GitHub.cli", true),
            ("GitHub Copilot CLI", "copilot --version", "winget install GitHub.Copilot", true),
            ("Node.js", "node --version", "winget install OpenJS.NodeJS", true),
            (".NET SDK", "dotnet --version", "https://dot.net", false),
            ("PowerShell 7", "pwsh --version", "winget install Microsoft.PowerShell", false),
            ("ffmpeg", "ffmpeg -version", "winget install Gyan.FFmpeg", false),
        };

        var allPassed = true;
        foreach (var (name, command, installHint, required) in checks)
        {
            var (found, version) = await CheckToolAsync(command);
            var status = found ? "✅" : (required ? "❌" : "⚠️");
            var versionText = found ? version : (required ? "MISSING" : "optional, not found");
            Console.WriteLine($"  {status} {name,-25} {versionText}");

            if (!found && required)
            {
                allPassed = false;
                if (autoInstall && installHint.StartsWith("winget"))
                {
                    Console.WriteLine($"     ⬇️  Installing: {installHint}");
                    await RunCommandAsync(installHint + " --accept-source-agreements --accept-package-agreements");
                }
                else if (!autoInstall)
                {
                    Console.WriteLine($"     💡 Install: {installHint}");
                }
            }
        }

        // Check GitHub auth
        Console.WriteLine();
        var (ghAuth, ghUser) = await CheckGhAuthAsync();
        if (ghAuth)
            Console.WriteLine($"  ✅ GitHub authenticated as {ghUser}");
        else
        {
            Console.WriteLine("  ❌ GitHub CLI not authenticated");
            Console.WriteLine("     💡 Run: gh auth login");
            allPassed = false;
        }

        // Check Windows LongPaths
        if (OperatingSystem.IsWindows())
        {
            var longPaths = CheckLongPathsEnabled();
            Console.WriteLine($"  {(longPaths ? "✅" : "⚠️")} Windows Long Paths    {(longPaths ? "enabled" : "disabled (may cause issues with deep paths)")}");
        }

        Console.WriteLine();
        Console.WriteLine(allPassed
            ? "✅ All required prerequisites met! Run 'vdt start' to begin."
            : "❌ Some prerequisites are missing. Install them and run 'vdt check-deps' again.");

        return true;
    }

    private static async Task<(bool found, string version)> CheckToolAsync(string command)
    {
        try
        {
            var parts = command.Split(' ', 2);
            var psi = new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "");
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return (false, "");
            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(firstLine, @"[\d]+\.[\d]+[\.\d]*");
            return (true, match.Success ? match.Value : "installed");
        }
        catch { return (false, ""); }
    }

    private static async Task<(bool auth, string? user)> CheckGhAuthAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null);
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return (false, null);
            var match = System.Text.RegularExpressions.Regex.Match(stderr, @"Logged in to github\.com account (\S+)");
            return (true, match.Success ? match.Groups[1].Value : "unknown");
        }
        catch { return (false, null); }
    }

    private static bool CheckLongPathsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int val && val == 1;
        }
        catch { return false; }
    }

    private static async Task RunCommandAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd", $"/c {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null) await proc.WaitForExitAsync();
        }
        catch { Console.WriteLine("     ⚠️  Auto-install failed. Install manually."); }
    }
}

/// <summary>CLI startup options parsed from command-line arguments.</summary>
public class CliStartupOptions
{
    public int Port { get; set; } = 5050;
    public bool Headless { get; set; }
    public bool AutoApprove { get; set; }
    public bool? OpenBrowser { get; set; }
    /// <summary>Local project path. Defaults to CWD if not specified.</summary>
    public string? ProjectPath { get; set; }
}
