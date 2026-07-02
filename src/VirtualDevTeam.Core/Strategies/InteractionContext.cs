using System.Text;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Aggregates runtime behavior signals from multiple capture sources into a single
/// context object. Fed to judges, reviewers, and the dashboard so they see what
/// happened when the candidate's code actually ran — not just what the code looks like.
/// </summary>
public sealed record InteractionContext
{
    /// <summary>JavaScript console errors captured during page load/interaction.</summary>
    public IReadOnlyList<string> ConsoleErrors { get; init; } = Array.Empty<string>();

    /// <summary>Failed network requests (HTTP errors, timeouts, connection refused).</summary>
    public IReadOnlyList<string> FailedRequests { get; init; } = Array.Empty<string>();

    /// <summary>API smoke probe results (endpoint, status code, body snippet for failures).</summary>
    public IReadOnlyList<ApiProbeSnapshot> ApiProbes { get; init; } = Array.Empty<ApiProbeSnapshot>();

    /// <summary>Total network requests observed during page interaction.</summary>
    public int NetworkRequestCount { get; init; }

    /// <summary>Detected page type: SPA, SSR, Static, ApiOnly, Unknown.</summary>
    public string? PageType { get; init; }

    /// <summary>Whether the app started and served HTTP responses.</summary>
    public bool AppStartedSuccessfully { get; init; }

    /// <summary>If the app failed to start, the error message.</summary>
    public string? AppStartupError { get; init; }

    /// <summary>Build errors/warnings (first 5 lines).</summary>
    public IReadOnlyList<string> BuildErrors { get; init; } = Array.Empty<string>();

    /// <summary>Test failure details (first 5 failures).</summary>
    public IReadOnlyList<string> TestFailures { get; init; } = Array.Empty<string>();

    /// <summary>Test summary: X passed, Y failed, Z skipped.</summary>
    public string? TestSummary { get; init; }

    /// <summary>
    /// True if any runtime error was detected (console errors, failed requests,
    /// app startup failure, test failures). Quick check for penalty decisions.
    /// </summary>
    public bool HasErrors =>
        ConsoleErrors.Count > 0
        || FailedRequests.Count > 0
        || !string.IsNullOrEmpty(AppStartupError)
        || TestFailures.Count > 0
        || ApiProbes.Any(p => p.StatusCode >= 500);

    /// <summary>
    /// Produces a concise text summary suitable for injecting into LLM prompts.
    /// Capped at ~2000 characters to avoid bloating context.
    /// </summary>
    public string ToPromptSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Runtime Behavior");

        if (!string.IsNullOrEmpty(AppStartupError))
            sb.AppendLine($"- ❌ App failed to start: {Truncate(AppStartupError, 200)}");
        else if (AppStartedSuccessfully)
            sb.AppendLine($"- ✅ App started successfully");

        if (!string.IsNullOrEmpty(PageType))
            sb.AppendLine($"- Page type: {PageType}");

        if (ConsoleErrors.Count > 0)
        {
            sb.AppendLine($"- {ConsoleErrors.Count} console error(s):");
            foreach (var err in ConsoleErrors.Take(5))
                sb.AppendLine($"  • {Truncate(err, 200)}");
        }

        if (FailedRequests.Count > 0)
        {
            sb.AppendLine($"- {FailedRequests.Count} failed network request(s):");
            foreach (var req in FailedRequests.Take(5))
                sb.AppendLine($"  • {Truncate(req, 200)}");
        }

        if (ApiProbes.Count > 0)
        {
            var failed = ApiProbes.Where(p => p.StatusCode >= 400).ToList();
            var passed = ApiProbes.Count - failed.Count;
            sb.AppendLine($"- API smoke test: {passed}/{ApiProbes.Count} probes passed");
            foreach (var probe in failed.Take(5))
                sb.AppendLine($"  • {probe.Method} {probe.Url} → {probe.StatusCode}{(probe.BodySnippet is not null ? $": {Truncate(probe.BodySnippet, 100)}" : "")}");
        }

        if (BuildErrors.Count > 0)
        {
            sb.AppendLine($"- Build errors:");
            foreach (var err in BuildErrors.Take(5))
                sb.AppendLine($"  • {Truncate(err, 150)}");
        }

        if (!string.IsNullOrEmpty(TestSummary))
            sb.AppendLine($"- Tests: {TestSummary}");

        if (TestFailures.Count > 0)
        {
            sb.AppendLine($"- Test failures:");
            foreach (var fail in TestFailures.Take(5))
                sb.AppendLine($"  • {Truncate(fail, 150)}");
        }

        if (!HasErrors && ConsoleErrors.Count == 0 && FailedRequests.Count == 0)
            sb.AppendLine("- No runtime errors detected");

        // Hard cap
        var result = sb.ToString();
        return result.Length > 2000 ? result[..1997] + "..." : result;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    /// <summary>
    /// Build an InteractionContext from the various capture sources.
    /// All parameters are optional — pass whatever is available.
    /// </summary>
    public static InteractionContext Build(
        PageAnalysis? pageAnalysis = null,
        bool appStarted = false,
        string? appStartupError = null,
        IReadOnlyList<string>? buildErrors = null,
        IReadOnlyList<string>? testFailures = null,
        string? testSummary = null,
        IReadOnlyList<ApiProbeSnapshot>? apiProbes = null)
    {
        return new InteractionContext
        {
            ConsoleErrors = pageAnalysis?.ConsoleErrors ?? Array.Empty<string>(),
            FailedRequests = pageAnalysis?.FailedRequests ?? Array.Empty<string>(),
            NetworkRequestCount = pageAnalysis?.NetworkRequestCount ?? 0,
            PageType = pageAnalysis?.PageType,
            AppStartedSuccessfully = appStarted,
            AppStartupError = appStartupError,
            BuildErrors = buildErrors ?? Array.Empty<string>(),
            TestFailures = testFailures ?? Array.Empty<string>(),
            TestSummary = testSummary,
            ApiProbes = apiProbes ?? Array.Empty<ApiProbeSnapshot>(),
        };
    }
}

/// <summary>Snapshot of an API smoke test probe result.</summary>
public sealed record ApiProbeSnapshot
{
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public int StatusCode { get; init; }
    public string? BodySnippet { get; init; }
}
