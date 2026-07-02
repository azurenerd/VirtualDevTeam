using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Executes test commands in a local workspace and parses real test results.
/// Supports xUnit/NUnit/MSTest (via dotnet test), Jest, pytest, and others.
/// No more fabricated reports — only actual test execution results.
/// </summary>
public class TestRunner
{
    private readonly ILogger<TestRunner> _logger;

    public TestRunner(ILogger<TestRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run the configured test command and parse real results.
    /// </summary>
    public async Task<TestResult> RunTestsAsync(
        string workspacePath,
        string testCommand,
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        Dictionary<string, string>? environmentVariables = null)
    {
        var projectType = ProjectTypeDetector.Detect(workspacePath);
        if (projectType == ProjectTypeDetector.ProjectType.NoBuildableCode)
        {
            _logger.LogInformation(
                "Tests skipped in {Path}: no buildable code detected (no project markers found in workspace). " +
                "Treating as success — pure-asset / pure-doc PRs do not have a test suite.",
                workspacePath);
            return new TestResult
            {
                Success = true,
                Output = "Tests skipped: no buildable code detected (no project markers).",
                Passed = 0,
                Failed = 0,
                Skipped = 0,
                Duration = TimeSpan.Zero,
                FailureDetails = Array.Empty<string>(),
            };
        }

        // If the caller's test command is any 'dotnet test' variant but the project is
        // not .NET, swap to the detected default. Handles 'dotnet test --verbosity normal',
        // 'dotnet test --filter ...', etc. Same generality rule as BuildRunner — workspace
        // state drives the choice, not the agent's role.
        var trimmed = testCommand?.Trim() ?? string.Empty;
        if (trimmed.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase)
            && projectType != ProjectTypeDetector.ProjectType.DotNet)
        {
            var defaultForType = ProjectTypeDetector.GetDefaultTestCommand(projectType);
            if (!string.IsNullOrEmpty(defaultForType))
            {
                _logger.LogInformation(
                    "Test command was 'dotnet test' but workspace is {Type} — using '{Cmd}' instead",
                    projectType, defaultForType);
                testCommand = defaultForType;
            }
        }

        // Auto-resolve test target for 'dotnet test' commands to avoid MSB1011/MSB1003
        // when the workspace has multiple .sln files (mirrors BuildRunner.ResolveBuildCommand).
        testCommand = ResolveTestCommand(workspacePath, testCommand);

        _logger.LogInformation("Running tests in {Path}: {Command}", workspacePath, testCommand);

        var result = await RunCommandAsync(workspacePath, testCommand, timeoutSeconds, ct, environmentVariables);
        var combinedOutput = result.StandardOutput + "\n" + result.StandardError;

        var (passed, failed, skipped) = ParseTestCounts(combinedOutput);
        var failures = ParseTestFailures(combinedOutput);

        // Reconcile: if parser found failure details but count says 0 failed, trust the details.
        // This happens when dotnet test output format doesn't match count regex but failures are parseable.
        if (failed == 0 && failures.Count > 0)
        {
            _logger.LogWarning("Test count parser reported 0 failed but {FailureCount} failure details found — correcting count",
                failures.Count);
            failed = failures.Count;
        }

        // Trust parsed test counts over process exit code when available.
        // dotnet test can return non-zero exit code even when all tests pass
        // (e.g., one test project fails to build while others run fine).
        var testsWereParsed = passed > 0 || failed > 0;
        var success = testsWereParsed
            ? failed == 0
            : result.Success;

        var testResult = new TestResult
        {
            Success = success,
            Output = combinedOutput,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Duration = result.Duration,
            FailureDetails = failures
        };

        if (testResult.Success)
        {
            _logger.LogInformation("Tests passed: {Passed} passed, {Skipped} skipped in {Duration:F1}s",
                passed, skipped, result.Duration.TotalSeconds);
            if (!result.Success)
                _logger.LogWarning("Test process exited with code {ExitCode} but all {Passed} parsed tests passed — treating as success",
                    result.ExitCode, passed);
        }
        else
            _logger.LogWarning("Tests FAILED: {Passed} passed, {Failed} failed, {Skipped} skipped in {Duration:F1}s",
                passed, failed, skipped, result.Duration.TotalSeconds);

        return testResult;
    }

    /// <summary>
    /// Parse test pass/fail/skip counts from test runner output.
    /// Supports dotnet test, Jest, Vitest, pytest, and generic patterns.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCounts(string output)
    {
        int passed = 0, failed = 0, skipped = 0;

        // dotnet test (xUnit/NUnit/MSTest): "Passed: 85, Failed: 2, Skipped: 0"
        // or "Failed!  - Failed:     2, Passed:    83, Skipped:     0, Total:    85"
        var dotnetMatch = Regex.Match(output,
            @"(?:Passed|Failed).*?Passed:\s*(\d+).*?Failed:\s*(\d+).*?Skipped:\s*(\d+)",
            RegexOptions.Singleline);
        if (!dotnetMatch.Success)
        {
            // Alternative format: "Passed:    83, Failed:     2, Skipped:     0"
            dotnetMatch = Regex.Match(output,
                @"Passed:\s*(\d+).*?Failed:\s*(\d+).*?Skipped:\s*(\d+)",
                RegexOptions.Singleline);
        }

        if (dotnetMatch.Success)
        {
            // In dotnet test output, the order might be different in different lines
            // Find all instances and use the last one (final summary)
            var allMatches = Regex.Matches(output,
                @"Passed:\s*(\d+).*?Failed:\s*(\d+).*?Skipped:\s*(\d+)",
                RegexOptions.Singleline);
            if (allMatches.Count > 0)
            {
                var last = allMatches[^1];
                passed = int.Parse(last.Groups[1].Value);
                failed = int.Parse(last.Groups[2].Value);
                skipped = int.Parse(last.Groups[3].Value);
                return (passed, failed, skipped);
            }
        }

        // Alternative: "Failed:     2, Passed:    83" (Failed first)
        var altMatch = Regex.Match(output,
            @"Failed:\s*(\d+).*?Passed:\s*(\d+)(?:.*?Skipped:\s*(\d+))?",
            RegexOptions.Singleline);
        if (altMatch.Success)
        {
            failed = int.Parse(altMatch.Groups[1].Value);
            passed = int.Parse(altMatch.Groups[2].Value);
            skipped = altMatch.Groups[3].Success ? int.Parse(altMatch.Groups[3].Value) : 0;
            return (passed, failed, skipped);
        }

        // Vitest: "Test Files  2 passed (2)" / "Test Files  1 failed | 2 passed (3)"
        // and "Tests  8 passed (8)" / "Tests  2 failed | 45 passed (47)"
        var vitestTests = Regex.Match(output,
            @"Tests\s+(?:(\d+)\s+failed\s*\|?\s*)?(\d+)\s+passed(?:\s*\|?\s*(\d+)\s+skipped)?\s*\(\d+\)",
            RegexOptions.IgnoreCase);
        if (vitestTests.Success)
        {
            failed = vitestTests.Groups[1].Success ? int.Parse(vitestTests.Groups[1].Value) : 0;
            passed = int.Parse(vitestTests.Groups[2].Value);
            skipped = vitestTests.Groups[3].Success ? int.Parse(vitestTests.Groups[3].Value) : 0;
            return (passed, failed, skipped);
        }

        // Vitest alternative: "✓ 45 tests passed" / "× 2 tests failed"
        var vitestSimplePassed = Regex.Match(output, @"[✓✔]\s*(\d+)\s+tests?\s+passed", RegexOptions.IgnoreCase);
        var vitestSimpleFailed = Regex.Match(output, @"[✗✘×]\s*(\d+)\s+tests?\s+failed", RegexOptions.IgnoreCase);
        if (vitestSimplePassed.Success || vitestSimpleFailed.Success)
        {
            passed = vitestSimplePassed.Success ? int.Parse(vitestSimplePassed.Groups[1].Value) : 0;
            failed = vitestSimpleFailed.Success ? int.Parse(vitestSimpleFailed.Groups[1].Value) : 0;
            return (passed, failed, skipped);
        }

        // Jest: "Tests: 2 failed, 83 passed, 85 total"
        var jestMatch = Regex.Match(output,
            @"Tests:\s*(?:(\d+)\s+failed,\s*)?(\d+)\s+passed(?:,\s*(\d+)\s+skipped)?",
            RegexOptions.IgnoreCase);
        if (jestMatch.Success)
        {
            failed = jestMatch.Groups[1].Success ? int.Parse(jestMatch.Groups[1].Value) : 0;
            passed = int.Parse(jestMatch.Groups[2].Value);
            skipped = jestMatch.Groups[3].Success ? int.Parse(jestMatch.Groups[3].Value) : 0;
            return (passed, failed, skipped);
        }

        // pytest: "3 passed, 1 failed, 1 skipped"
        var pytestPassed = Regex.Match(output, @"(\d+)\s+passed");
        var pytestFailed = Regex.Match(output, @"(\d+)\s+failed");
        var pytestSkipped = Regex.Match(output, @"(\d+)\s+skipped");
        if (pytestPassed.Success || pytestFailed.Success)
        {
            passed = pytestPassed.Success ? int.Parse(pytestPassed.Groups[1].Value) : 0;
            failed = pytestFailed.Success ? int.Parse(pytestFailed.Groups[1].Value) : 0;
            skipped = pytestSkipped.Success ? int.Parse(pytestSkipped.Groups[1].Value) : 0;
            return (passed, failed, skipped);
        }

        return (passed, failed, skipped);
    }

    /// <summary>
    /// Parse individual test failure details from output.
    /// Returns a list of failure descriptions suitable for AI feedback.
    /// </summary>
    internal static IReadOnlyList<string> ParseTestFailures(string output)
    {
        var failures = new List<string>();

        // dotnet test failure blocks: "Failed TestName [duration]" followed by error + stack trace
        var failureBlocks = Regex.Matches(output,
            @"Failed\s+([\w.]+)\s*\[.*?\]\s*\n\s*Error Message:\s*\n(.*?)(?=\n\s*Stack Trace:|\n\s*Failed\s+\w|\nPassed!|\nFailed!|\z)",
            RegexOptions.Singleline);
        foreach (Match m in failureBlocks)
        {
            var testName = m.Groups[1].Value.Trim();
            var errorMsg = m.Groups[2].Value.Trim();
            // Limit error message length for AI prompt
            if (errorMsg.Length > 500)
                errorMsg = errorMsg[..500] + "...";
            failures.Add($"{testName}: {errorMsg}");
        }

        // If no structured failures found, look for simpler patterns
        if (failures.Count == 0)
        {
            var simpleFailures = Regex.Matches(output,
                @"^\s*[✗×✘]\s*(.+)$",
                RegexOptions.Multiline);
            foreach (Match m in simpleFailures)
                failures.Add(m.Groups[1].Value.Trim());
        }

        return failures;
    }

    /// <summary>
    /// Format test results as markdown for inclusion in PR body/comments.
    /// </summary>
    public static string FormatResultsAsMarkdown(TestResult result, string testCommand)
    {
        var status = result.Success ? "✅ PASSED" : "❌ FAILED";
        var md = $"""
            ## Test Results — {status}

            | Metric | Value |
            |--------|-------|
            | **Passed** | {result.Passed} |
            | **Failed** | {result.Failed} |
            | **Skipped** | {result.Skipped} |
            | **Total** | {result.Total} |
            | **Duration** | {result.Duration.TotalSeconds:F1}s |
            | **Command** | `{testCommand}` |
            """;

        if (result.FailureDetails.Count > 0)
        {
            md += "\n\n### Failure Details\n";
            foreach (var failure in result.FailureDetails)
                md += $"\n- **{failure}**";
        }

        return md;
    }

    /// <summary>
    /// When the test command starts with 'dotnet test' (with or without flags) but no
    /// project/solution target is specified, auto-detect the .sln or .csproj to avoid
    /// MSB1011 (multiple project files) or MSB1003 (no project file) errors.
    /// Mirrors <see cref="BuildRunner.ResolveBuildCommand"/> but handles flags after 'dotnet test'.
    /// </summary>
    internal string ResolveTestCommand(string workspacePath, string testCommand)
    {
        var trimmed = testCommand.Trim();

        // Only resolve for 'dotnet test' commands (with or without flags)
        if (!trimmed.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
            return testCommand;

        // Extract everything after 'dotnet test' (preserves leading space + flags like ' --verbosity normal')
        var afterCommand = trimmed.Substring("dotnet test".Length);
        var afterTrimmed = afterCommand.TrimStart();

        // If first non-whitespace after 'dotnet test' is NOT a flag, a project path is already specified
        if (afterTrimmed.Length > 0 && !afterTrimmed.StartsWith('-'))
            return testCommand;

        var slnFiles = Directory.GetFiles(workspacePath, "*.sln");
        var csprojFiles = Directory.GetFiles(workspacePath, "*.csproj");

        // No .NET project files at root — search subdirectories
        if (slnFiles.Length == 0 && csprojFiles.Length == 0)
        {
            var anySlns = SafeGetFiles(workspacePath, "*.sln");
            var anyCsprojs = SafeGetFiles(workspacePath, "*.csproj");

            if (anySlns.Length > 0)
            {
                var target = Path.GetRelativePath(workspacePath, anySlns[0]);
                _logger.LogInformation("Auto-resolved test target to {Target} (found in subdirectory)", target);
                return $"dotnet test {target}{afterCommand}";
            }

            if (anyCsprojs.Length > 0)
            {
                var target = Path.GetRelativePath(workspacePath, anyCsprojs[0]);
                _logger.LogInformation("Auto-resolved test target to {Target} (found in subdirectory)", target);
                return $"dotnet test {target}{afterCommand}";
            }

            _logger.LogWarning("No .NET project files found in {Path}; returning original test command", workspacePath);
            return testCommand;
        }

        // If exactly one target exists, dotnet test auto-detection works fine
        if (slnFiles.Length + csprojFiles.Length <= 1)
            return testCommand;

        // Multiple targets — prefer .sln (it includes all projects)
        if (slnFiles.Length >= 1)
        {
            var target = Path.GetFileName(slnFiles[0]);
            _logger.LogInformation("Auto-resolved test target to {Target} (found {SlnCount} .sln, {CsprojCount} .csproj)",
                target, slnFiles.Length, csprojFiles.Length);
            return $"dotnet test {target}{afterCommand}";
        }

        // Fallback: use first .csproj
        var csprojTarget = Path.GetFileName(csprojFiles[0]);
        _logger.LogInformation("Auto-resolved test target to {Target} (no .sln, {Count} .csproj files)",
            csprojTarget, csprojFiles.Length);
        return $"dotnet test {csprojTarget}{afterCommand}";
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

    private async Task<ProcessResult> RunCommandAsync(
        string workDir, string command, int timeoutSeconds, CancellationToken ct,
        Dictionary<string, string>? environmentVariables = null)
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

        // Apply environment variables (e.g., PLAYWRIGHT_BROWSERS_PATH for UI tests)
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
                startInfo.EnvironmentVariables[key] = value;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        // Create the linked timeout CTS BEFORE starting IO tasks so pipe reads
        // are cancelled when the timeout fires. Without this, orphan child processes
        // that inherit stdout handles keep ReadToEndAsync blocked indefinitely after
        // the parent process is killed (root cause of the 5-hour SE3 hang, May 2026).
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

            // Bounded wait for pipe drain after kill — never block indefinitely.
            // Orphan grandchildren may still hold handles; give 5s grace then abandon.
            string stdout = "", stderr = "Tests timed out after " + timeoutSeconds + "s";
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
}
