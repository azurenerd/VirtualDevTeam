namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Resolves executable paths using the Windows registry PATH (Machine + User) instead of
/// the inherited process PATH. Tools installed via winget/npm after the Runner started
/// are invisible to Process.Start() unless we read fresh PATH from the registry.
///
/// Usage: call <see cref="GetFreshPath"/> to get the combined PATH string, or
/// <see cref="ResolveExecutable"/> to find a specific tool's full path.
/// Apply <see cref="ApplyFreshPath"/> to a ProcessStartInfo before Process.Start().
/// </summary>
public static class FreshPathResolver
{
    /// <summary>
    /// Returns the combined Machine + User PATH from the Windows registry,
    /// merged with the current process PATH (for non-registry entries like
    /// conda, nvm, or other session-local additions).
    /// On non-Windows, returns the current process PATH unchanged.
    /// </summary>
    public static string GetFreshPath()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable("PATH") ?? "";

        try
        {
            var machPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";

            // Merge: registry paths first (authoritative), then process-only paths
            var registryDirs = new HashSet<string>(
                $"{machPath};{userPath}".Split(';', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var processDirs = processPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in processDirs)
            {
                registryDirs.Add(dir); // no-op if already present
            }

            return string.Join(';', registryDirs);
        }
        catch
        {
            return Environment.GetEnvironmentVariable("PATH") ?? "";
        }
    }

    /// <summary>
    /// Finds the full path to an executable by searching the fresh PATH.
    /// Returns the full path if found, or null if not found.
    /// On Windows, also checks PATHEXT extensions (.exe, .cmd, .bat).
    /// </summary>
    public static string? ResolveExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // If already a full path, just verify it exists
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        var freshPath = GetFreshPath();
        var extensions = OperatingSystem.IsWindows()
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { "" };

        foreach (var dir in freshPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch { /* skip invalid path entries */ }
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the fresh PATH to a ProcessStartInfo so child processes
    /// can find tools installed after the Runner started.
    /// </summary>
    public static void ApplyFreshPath(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.Environment["PATH"] = GetFreshPath();
    }
}
