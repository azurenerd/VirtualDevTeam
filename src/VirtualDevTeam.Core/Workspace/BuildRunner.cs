using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Executes build commands in a local workspace and parses the output for errors.
/// Supports dotnet build, npm run build, and other configurable build tools.
/// </summary>
public class BuildRunner
{
    private readonly ILogger<BuildRunner> _logger;

    public BuildRunner(ILogger<BuildRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run the configured build command in the workspace directory.
    /// Auto-detects .sln file when using 'dotnet build' to avoid MSB1011 errors.
    /// Short-circuits with a successful no-op result when the workspace contains
    /// no buildable code (pure-asset / pure-doc / pure-image PRs) — fix for the
    /// 2026-05-12 build-test-no-dotnet-hardcoding generality rule. The signal comes
    /// from <see cref="ProjectTypeDetector"/> probing files on disk, NOT from the
    /// agent's role.
    /// </summary>
    public async Task<BuildResult> BuildAsync(
        string workspacePath,
        string buildCommand,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var projectType = ProjectTypeDetector.Detect(workspacePath);
        if (projectType == ProjectTypeDetector.ProjectType.NoBuildableCode)
        {
            _logger.LogInformation(
                "Build skipped in {Path}: no buildable code detected (no .sln/.csproj/package.json/pyproject.toml/go.mod/Cargo.toml). " +
                "Workspace contains only assets/docs/binaries — treating as success.",
                workspacePath);
            return new BuildResult
            {
                Success = true,
                Output = "Build skipped: no buildable code detected (no project markers found in workspace).",
                Errors = string.Empty,
                Duration = TimeSpan.Zero,
                ParsedErrors = Array.Empty<string>(),
            };
        }

        // If the caller's build command is any 'dotnet build' variant but the project is NOT a .NET
        // project, swap to the detected default. Avoids the "Artist agent runs dotnet build
        // on a Phaser frontend" failure mode the user flagged 2026-05-12.
        var trimmed = buildCommand?.Trim() ?? string.Empty;
        if (trimmed.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase)
            && projectType != ProjectTypeDetector.ProjectType.DotNet)
        {
            var defaultForType = ProjectTypeDetector.GetDefaultBuildCommand(projectType);
            if (!string.IsNullOrEmpty(defaultForType))
            {
                _logger.LogInformation(
                    "Build command was 'dotnet build' but workspace is {Type} — using '{Cmd}' instead",
                    projectType, defaultForType);
                buildCommand = defaultForType;
            }
        }

        // Auto-resolve build target for bare 'dotnet build' to avoid MSB1011
        var resolvedCommand = ResolveBuildCommand(workspacePath, buildCommand);
        _logger.LogInformation("Running build in {Path}: {Command}", workspacePath, resolvedCommand);

        var result = await RunCommandAsync(workspacePath, resolvedCommand, timeoutSeconds, ct);

        var parsedErrors = ParseBuildErrors(result.StandardOutput + "\n" + result.StandardError);

        var buildResult = new BuildResult
        {
            Success = result.Success,
            Output = result.StandardOutput,
            Errors = result.StandardError,
            Duration = result.Duration,
            ParsedErrors = parsedErrors
        };

        if (buildResult.Success)
            _logger.LogInformation("Build succeeded in {Duration:F1}s", buildResult.Duration.TotalSeconds);
        else
            _logger.LogWarning("Build FAILED with {Count} errors in {Duration:F1}s",
                parsedErrors.Count, buildResult.Duration.TotalSeconds);

        return buildResult;
    }

    /// <summary>
    /// Parse build output for individual error messages.
    /// Supports dotnet/MSBuild, npm/Node, and generic error patterns.
    /// </summary>
    internal static IReadOnlyList<string> ParseBuildErrors(string output)
    {
        var errors = new List<string>();

        // dotnet/MSBuild errors: "File.cs(42,10): error CS1002: ; expected"
        var dotnetErrors = Regex.Matches(output,
            @"^.*?(?:error\s+CS\d+|error\s+MSB\d+|error\s+NU\d+):.*$",
            RegexOptions.Multiline);
        foreach (Match m in dotnetErrors)
            errors.Add(m.Value.Trim());

        // Generic "error:" pattern (catches most build tools)
        if (errors.Count == 0)
        {
            var genericErrors = Regex.Matches(output,
                @"^.*\berror\b.*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match m in genericErrors)
            {
                var line = m.Value.Trim();
                // Skip noise lines
                if (line.Contains("0 Error(s)", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("error(s)", StringComparison.OrdinalIgnoreCase) && line.Contains("warning(s)", StringComparison.OrdinalIgnoreCase)) continue;
                errors.Add(line);
            }
        }

        return errors;
    }

    private async Task<ProcessResult> RunCommandAsync(
        string workDir, string command, int timeoutSeconds, CancellationToken ct)
    {
        // Split command into executable and arguments
        var (exe, args) = ParseCommand(command);

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

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        // Create the linked timeout CTS BEFORE starting IO tasks so pipe reads
        // are cancelled when the timeout fires. Same fix as TestRunner — prevents
        // indefinite hangs when orphan grandchildren hold stdout handles open.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            sw.Stop();

            // Bounded wait for pipe drain after kill
            string stdout = "", stderr = "Build timed out after " + timeoutSeconds + "s";
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { stdout = await stdoutTask.WaitAsync(drainCts.Token); } catch { }

            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = stdout,
                StandardError = stderr,
                Duration = sw.Elapsed
            };
        }

        sw.Stop();

        // Normal exit — give pipes a bounded grace period to flush
        string normalStdout = "", normalStderr = "";
        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { normalStdout = await stdoutTask.WaitAsync(flushCts.Token); } catch { }
        try { normalStderr = await stderrTask.WaitAsync(flushCts.Token); } catch { }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = normalStdout,
            StandardError = normalStderr,
            Duration = sw.Elapsed
        };
    }

    internal static (string Exe, string Args) ParseCommand(string command)
    {
        command = command.Trim();

        // Handle "dotnet build --foo", "npm run build", etc.
        var parts = command.Split(' ', 2);
        var exe = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        // On Windows, if the command is "npm" or "npx", use cmd /c
        if (OperatingSystem.IsWindows() &&
            exe is "npm" or "npx" or "yarn" or "pnpm")
        {
            args = $"/c {command}";
            exe = "cmd";
        }

        return (exe, args);
    }

    /// <summary>
    /// When the build command is bare 'dotnet build' (no project/sln target specified),
    /// auto-detect the .sln or .csproj to avoid MSB1011 "multiple project files" errors.
    /// Priority: single .sln > single .csproj > first .sln alphabetically.
    /// </summary>
    internal string ResolveBuildCommand(string workspacePath, string buildCommand)
    {
        // Only resolve for bare 'dotnet build' (no target already specified)
        var trimmed = buildCommand.Trim();
        if (!trimmed.Equals("dotnet build", StringComparison.OrdinalIgnoreCase))
            return buildCommand;

        var slnFiles = Directory.GetFiles(workspacePath, "*.sln");
        var csprojFiles = Directory.GetFiles(workspacePath, "*.csproj");

        // No .NET project files at all — check for Node.js project
        if (slnFiles.Length == 0 && csprojFiles.Length == 0)
        {
            // Search subdirectories too — use safe enumeration to skip
            // inaccessible dirs (e.g., .sandbox from Copilot CLI strategy eval)
            var anySlns = SafeGetFiles(workspacePath, "*.sln");
            var anyCsprojs = SafeGetFiles(workspacePath, "*.csproj");

            if (anySlns.Length > 0)
            {
                var target = Path.GetRelativePath(workspacePath, anySlns[0]);
                _logger.LogInformation("Auto-resolved build target to {Target} (found in subdirectory)", target);
                return $"dotnet build {target}";
            }

            if (anyCsprojs.Length > 0)
            {
                var target = Path.GetRelativePath(workspacePath, anyCsprojs[0]);
                _logger.LogInformation("Auto-resolved build target to {Target} (found in subdirectory)", target);
                return $"dotnet build {target}";
            }

            // No .NET files anywhere — fall back to multi-stack manifest detection.
            // Order matches typical likelihood for AI-generated projects; first match wins.
            var packageJson = Path.Combine(workspacePath, "package.json");
            if (File.Exists(packageJson))
            {
                _logger.LogInformation("No .NET project found; detected package.json — switching to 'npm run build'");
                return "npm run build";
            }
            // Python: pyproject.toml > setup.py > requirements.txt. Most modern projects use
            // pyproject + a build backend (poetry, hatch, setuptools). For installable libraries
            // 'python -m build' is conventional; for apps, 'pip install -r requirements.txt'
            // serves the same "ensure deps + check imports compile" purpose.
            if (File.Exists(Path.Combine(workspacePath, "pyproject.toml")))
            {
                _logger.LogInformation("No .NET project found; detected pyproject.toml — switching to 'python -m build'");
                return "python -m build";
            }
            if (File.Exists(Path.Combine(workspacePath, "setup.py")) ||
                File.Exists(Path.Combine(workspacePath, "requirements.txt")))
            {
                _logger.LogInformation("No .NET project found; detected setup.py/requirements.txt — switching to 'pip install -e .' (compile/import check)");
                return "pip install -e .";
            }
            // Go
            if (File.Exists(Path.Combine(workspacePath, "go.mod")))
            {
                _logger.LogInformation("No .NET project found; detected go.mod — switching to 'go build ./...'");
                return "go build ./...";
            }
            // Rust
            if (File.Exists(Path.Combine(workspacePath, "Cargo.toml")))
            {
                _logger.LogInformation("No .NET project found; detected Cargo.toml — switching to 'cargo build'");
                return "cargo build";
            }
            // Java/JVM (Maven preferred over Gradle when both somehow present)
            if (File.Exists(Path.Combine(workspacePath, "pom.xml")))
            {
                _logger.LogInformation("No .NET project found; detected pom.xml — switching to 'mvn -B compile'");
                return "mvn -B compile";
            }
            if (File.Exists(Path.Combine(workspacePath, "build.gradle")) ||
                File.Exists(Path.Combine(workspacePath, "build.gradle.kts")))
            {
                _logger.LogInformation("No .NET project found; detected build.gradle — switching to 'gradle build'");
                return "gradle build";
            }
            // Ruby
            if (File.Exists(Path.Combine(workspacePath, "Gemfile")))
            {
                _logger.LogInformation("No .NET project found; detected Gemfile — switching to 'bundle install'");
                return "bundle install";
            }
            // PHP
            if (File.Exists(Path.Combine(workspacePath, "composer.json")))
            {
                _logger.LogInformation("No .NET project found; detected composer.json — switching to 'composer install'");
                return "composer install";
            }

            _logger.LogWarning("No recognized build manifest found in {Path}; returning original command (likely will fail)", workspacePath);
            return buildCommand;
        }

        // If exactly one target exists, dotnet build works fine as-is
        if (slnFiles.Length + csprojFiles.Length <= 1)
            return buildCommand;

        // Prefer .sln file (it includes all projects)
        if (slnFiles.Length >= 1)
        {
            var target = Path.GetFileName(slnFiles[0]);
            _logger.LogInformation("Auto-resolved build target to {Target} (found {SlnCount} .sln, {CsprojCount} .csproj)",
                target, slnFiles.Length, csprojFiles.Length);
            return $"dotnet build {target}";
        }

        // Fallback: use first .csproj
        if (csprojFiles.Length >= 1)
        {
            var target = Path.GetFileName(csprojFiles[0]);
            _logger.LogInformation("Auto-resolved build target to {Target} (no .sln, {Count} .csproj files)",
                target, csprojFiles.Length);
            return $"dotnet build {target}";
        }

        return buildCommand;
    }

    /// <summary>
    /// Recursively search for files, skipping directories that are inaccessible
    /// or known to be irrelevant (e.g., .sandbox, .git, node_modules).
    /// </summary>
    private static string[] SafeGetFiles(string rootPath, string pattern)
    {
        var results = new List<string>();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sandbox", ".git", "node_modules", "bin", "obj",
            ".candidates", ".candidates-eval"
        };

        try
        {
            results.AddRange(Directory.GetFiles(rootPath, pattern));
        }
        catch (UnauthorizedAccessException) { }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath))
            {
                var dirName = Path.GetFileName(dir);
                if (skipDirs.Contains(dirName))
                    continue;

                results.AddRange(SafeGetFiles(dir, pattern));
            }
        }
        catch (UnauthorizedAccessException) { }

        return results.ToArray();
    }
}
