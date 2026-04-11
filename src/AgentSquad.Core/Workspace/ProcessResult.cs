namespace AgentSquad.Core.Workspace;

/// <summary>
/// Test tier classification for multi-tier test execution.
/// </summary>
public enum TestTier
{
    /// <summary>Unit tests — fast, isolated, mock dependencies.</summary>
    Unit,
    /// <summary>Integration tests — real dependencies, DI container, API calls.</summary>
    Integration,
    /// <summary>UI/E2E tests — Playwright browser automation, user workflows.</summary>
    UI
}

/// <summary>
/// Result of running an external process (git, build, test).
/// </summary>
public record ProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Result of a build operation with parsed error details.
/// </summary>
public record BuildResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public required string Errors { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Individual build errors extracted from compiler output.
    /// Each entry is a single error message (e.g., "error CS1002: ; expected at File.cs:42").
    /// </summary>
    public IReadOnlyList<string> ParsedErrors { get; init; } = [];
}

/// <summary>
/// Result of a test execution with parsed pass/fail/skip counts.
/// </summary>
public record TestResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Which test tier this result belongs to. Null for legacy undifferentiated runs.
    /// </summary>
    public TestTier? Tier { get; init; }

    /// <summary>
    /// Details of individual test failures for AI feedback.
    /// Each entry describes one failing test with the error message.
    /// </summary>
    public IReadOnlyList<string> FailureDetails { get; init; } = [];

    /// <summary>
    /// Paths to test artifacts (videos, traces, screenshots) produced during this run.
    /// Grouped by type for structured access.
    /// </summary>
    public TestArtifacts Artifacts { get; init; } = new();

    public int Total => Passed + Failed + Skipped;
}

/// <summary>
/// Collected Playwright test artifacts from a UI test run.
/// </summary>
public record TestArtifacts
{
    /// <summary>Paths to .webm video recordings of test execution.</summary>
    public IReadOnlyList<string> Videos { get; init; } = [];

    /// <summary>Paths to .zip Playwright trace files (viewable at trace.playwright.dev).</summary>
    public IReadOnlyList<string> Traces { get; init; } = [];

    /// <summary>Paths to .png screenshot files captured during tests.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>Whether any artifacts were collected.</summary>
    public bool HasArtifacts => Videos.Count > 0 || Traces.Count > 0 || Screenshots.Count > 0;
}

/// <summary>
/// Aggregated test results across multiple test tiers.
/// </summary>
public record AggregateTestResult
{
    public required IReadOnlyList<TestResult> TierResults { get; init; }

    public bool AllPassed => TierResults.All(r => r.Success);
    public int TotalPassed => TierResults.Sum(r => r.Passed);
    public int TotalFailed => TierResults.Sum(r => r.Failed);
    public int TotalSkipped => TierResults.Sum(r => r.Skipped);
    public int TotalTests => TierResults.Sum(r => r.Total);
    public TimeSpan TotalDuration => TimeSpan.FromTicks(TierResults.Sum(r => r.Duration.Ticks));

    /// <summary>
    /// Format multi-tier results as markdown for PR body.
    /// </summary>
    public string FormatAsMarkdown()
    {
        var status = AllPassed ? "✅ ALL PASSED" : "❌ FAILURES DETECTED";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Test Results — {status}");
        sb.AppendLine();

        foreach (var result in TierResults)
        {
            var tierName = result.Tier?.ToString() ?? "General";
            var tierStatus = result.Success ? "✅" : "❌";
            sb.AppendLine($"### {tierName} Tests {tierStatus} — {result.Passed} passed, {result.Failed} failed ({result.Duration.TotalSeconds:F1}s)");

            if (result.FailureDetails.Count > 0)
            {
                sb.AppendLine();
                foreach (var failure in result.FailureDetails.Take(5))
                    sb.AppendLine($"- **{failure}**");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"**Total:** {TotalPassed} passed, {TotalFailed} failed, {TotalSkipped} skipped in {TotalDuration.TotalSeconds:F1}s");
        return sb.ToString();
    }
}
