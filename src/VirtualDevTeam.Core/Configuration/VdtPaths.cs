namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Resolves cross-platform paths for VDT's user data (SQLite, prompts, logs, settings).
/// <list type="bullet">
///   <item>Windows: <c>%LOCALAPPDATA%\VirtualDevTeam\</c></item>
///   <item>macOS: <c>~/Library/Application Support/VirtualDevTeam/</c></item>
///   <item>Linux: <c>~/.local/share/VirtualDevTeam/</c></item>
/// </list>
/// When running from source (development mode), paths resolve relative to the Runner directory.
/// When running as installed exe, paths use the platform user-data convention.
/// </summary>
public static class VdtPaths
{
    private const string AppName = "VirtualDevTeam";

    /// <summary>
    /// Root directory for all VDT user data.
    /// In development mode (running from source), returns null to signal "use legacy paths."
    /// In installed mode, returns the platform-specific user-data directory.
    /// </summary>
    public static string GetUserDataRoot()
    {
        // Check if we're running as an installed exe (not from source)
        // Heuristic: if the exe is under %LOCALAPPDATA%\VDT or /usr/local/bin,
        // it's installed. If it's under a git repo with .sln files, it's dev mode.
        if (IsDevMode())
            return Path.Combine(AppContext.BaseDirectory); // Legacy: beside the exe

        return GetPlatformUserDataRoot();
    }

    /// <summary>
    /// Platform-specific user data root, regardless of dev/installed mode.
    /// Always returns the "proper" location for user data.
    /// </summary>
    public static string GetPlatformUserDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA%\VirtualDevTeam
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, AppName);
        }

        if (OperatingSystem.IsMacOS())
        {
            // ~/Library/Application Support/VirtualDevTeam
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName);
        }

        // Linux and others: ~/.local/share/VirtualDevTeam
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            return Path.Combine(xdgDataHome, AppName);

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".local", "share", AppName);
    }

    /// <summary>Path to the prompts directory (editable user copies).</summary>
    public static string GetPromptsDir() => Path.Combine(GetUserDataRoot(), "prompts");

    /// <summary>Path to the SQLite database directory.</summary>
    public static string GetDatabaseDir() => Path.Combine(GetUserDataRoot(), "data");

    /// <summary>Path to the logs directory.</summary>
    public static string GetLogsDir() => Path.Combine(GetUserDataRoot(), "logs");

    /// <summary>Path to the develop-settings.json file.</summary>
    public static string GetSettingsDir() => Path.Combine(GetUserDataRoot(), "settings");

    /// <summary>
    /// Ensure all user data directories exist. Call once at startup.
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(GetPromptsDir());
        Directory.CreateDirectory(GetDatabaseDir());
        Directory.CreateDirectory(GetLogsDir());
        Directory.CreateDirectory(GetSettingsDir());
    }

    /// <summary>
    /// Detect if we're running from source (development mode).
    /// Heuristic: check for .sln files near the exe or the presence of a src/ directory.
    /// </summary>
    private static bool IsDevMode()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;

            // If running from bin/Debug/net8.0, we're in dev mode
            if (baseDir.Contains(Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase)
                || baseDir.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase))
                return true;

            // Walk up 3 levels looking for .sln (typical: Runner/bin/Debug/net8.0)
            var dir = baseDir;
            for (var i = 0; i < 5; i++)
            {
                dir = Path.GetDirectoryName(dir);
                if (dir is null) break;
                if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
                    return true;
            }
        }
        catch { /* best effort */ }

        return false;
    }
}
