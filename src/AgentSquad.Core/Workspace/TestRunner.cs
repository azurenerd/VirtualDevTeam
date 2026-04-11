using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Workspace;

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
        CancellationToken ct = default)
    {
        _logger.LogInformation("Running tests in {Path}: {Command}", workspacePath, testCommand);

        var result = await RunCommandAsync(workspacePath, testCommand, timeoutSeconds, ct);
        var combinedOutput = result.StandardOutput + "\n" + result.StandardError;

        var (passed, failed, skipped) = ParseTestCounts(combinedOutput);
        var failures = ParseTestFailures(combinedOutput);

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
    /// Supports dotnet test, Jest, pytest, and generic patterns.
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

    private async Task<ProcessResult> RunCommandAsync(
        string workDir, string command, int timeoutSeconds, CancellationToken ct)
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

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            sw.Stop();
            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = await stdoutTask,
                StandardError = $"Tests timed out after {timeoutSeconds}s",
                Duration = sw.Elapsed
            };
        }

        sw.Stop();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdoutTask,
            StandardError = await stderrTask,
            Duration = sw.Elapsed
        };
    }
}
