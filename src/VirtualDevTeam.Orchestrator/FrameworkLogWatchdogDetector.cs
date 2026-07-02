using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Watches Strategy Framework / agentic CLI session logs for silent failure modes
/// that would otherwise only surface hours later when the operator wonders why
/// a candidate produced no real output. Targets the failure classes we burned
/// the entire 2026-05-12 day debugging:
///
/// 1. <b>Empty Azure OpenAI image-gen credentials</b>: agent shell echoes
///    <c>ENDPOINT: '' </c> or <c>KEY length: 0</c> (or "No Azure OpenAI credentials"
///    in the agentic reasoning) — means env-var injection failed and the candidate
///    will produce zero PNGs while reporting "success" (because per AC #6 it
///    correctly leaves files ABSENT instead of fabricating).
///
/// 2. <b>Framework-decline diagnostic</b>: <c>Strategy framework declined for PR…</c>
///    log line emitted by SpecialistEngineerAgent when the framework opts out of a
///    rework — operator should know which guard fired.
///
/// 3. <b>Worktree-cleanup partial failure</b>: <c>Directory delete failed</c> or
///    <c>git worktree remove failed after 6 attempts</c> — the cleanup race that
///    destroyed Squad's PNGs. Should be near-extinct after the 2026-05-12 fix
///    but the detector keeps watching to confirm regression-free.
///
/// Implementation: scans the most-recent Runner stdout log file in <c>Logs/</c>
/// once per tick, tail-only (last 1000 lines) to bound cost. Pattern matches
/// against compiled regexes; emits one finding per matched signal per tick with
/// a stable dedup key so a single sustained failure doesn't spam.
///
/// Cost: a single 50-100KB file read + regex pass per tick (~10ms). Bounded.
/// </summary>
public sealed class FrameworkLogWatchdogDetector : IFlowDetector
{
    public string DetectorId => "framework-log-watchdog";

    private static readonly System.Text.RegularExpressions.Regex EmptyImageEndpoint = new(
        @"ENDPOINT:\s*['""]\s*['""]|No Azure OpenAI (?:image )?(?:credentials|environment variables)|KEY length:\s*0",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex FrameworkDeclined = new(
        @"Strategy framework declined for PR (?<pr>\d+)\b.*reason[:=]\s*(?<reason>[^|\r\n]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex WorktreeCleanupFailed = new(
        @"git worktree remove failed after \d+ attempts|Directory delete failed for .*\\.candidates\\",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Cap on log lines scanned per tick. Latest-first via tail-read.</summary>
    internal const int TailLineCount = 1000;

    private readonly ILogger<FrameworkLogWatchdogDetector> _logger;
    private readonly Func<string?> _logPathProvider;
    private long _lastReadOffset; // resumes scanning after the last seen position to avoid re-scanning
    private string? _lastLogPath;

    public FrameworkLogWatchdogDetector(ILogger<FrameworkLogWatchdogDetector> logger)
        : this(logger, ResolveLatestRunnerLogPath) { }

    /// <summary>Constructor for tests — inject a custom log-path provider.</summary>
    internal FrameworkLogWatchdogDetector(ILogger<FrameworkLogWatchdogDetector> logger, Func<string?> logPathProvider)
    {
        _logger = logger;
        _logPathProvider = logPathProvider;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var logPath = _logPathProvider();
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            // Reset offset when the log file rotates (different path = new runner session).
            if (!string.Equals(logPath, _lastLogPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastReadOffset = 0;
                _lastLogPath = logPath;
            }

            var fi = new FileInfo(logPath);
            if (fi.Length < _lastReadOffset)
                _lastReadOffset = 0; // truncated/rotated — restart from beginning

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_lastReadOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            int scanned = 0;
            while ((line = reader.ReadLine()) is not null && scanned < TailLineCount * 5)
            {
                scanned++;
                if (string.IsNullOrEmpty(line)) continue;
                ScanLine(line, ctx.Now, findings);
            }
            _lastReadOffset = fs.Position;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FrameworkLogWatchdogDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private static void ScanLine(string line, DateTimeOffset now, List<FlowFinding> findings)
    {
        if (EmptyImageEndpoint.IsMatch(line))
        {
            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = now,
                DetectorId = "framework-log-watchdog",
                Severity = FlowFindingSeverity.Critical,
                Summary = "Agentic candidate session reports MISSING Azure OpenAI image-gen credentials",
                Rationale =
                    "An agentic CLI / Squad session echoed an empty AZURE_OPENAI_IMAGE_ENDPOINT / KEY length 0 / " +
                    "\"No Azure OpenAI credentials available\" message. This means env-var injection into the child " +
                    "process failed (DI factory regression, stale config, wizard not run) and the candidate is " +
                    "going to produce ZERO real images while honoring the no-fabrication rule. Verify " +
                    "CopilotCliProcessManager + SquadFrameworkAdapter both receive IAzureImageAuthProvider via DI " +
                    "and that develop-settings.json has a populated AzureOpenAIImage block.",
                DedupKey = "framework-log:image-creds-empty",
            });
            return;
        }

        var declined = FrameworkDeclined.Match(line);
        if (declined.Success)
        {
            var pr = declined.Groups["pr"].Value;
            var reason = declined.Groups["reason"].Value.Trim();
            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = now,
                DetectorId = "framework-log-watchdog",
                Severity = FlowFindingSeverity.Warning,
                TargetResource = pr,
                Summary = $"Strategy framework DECLINED to handle rework on PR #{pr}",
                Rationale =
                    $"SpecialistEngineerAgent's framework-routing path opted out for this PR with reason " +
                    $"\"{reason}\". The agent fell back to the legacy rework path. If this happens repeatedly, " +
                    "the framework's CanHandle / preconditions logic likely needs an exception for this kind of " +
                    "rework (or the prompt needs to be updated to make the framework eligible).",
                DedupKey = $"framework-log:declined:{pr}",
            });
            return;
        }

        if (WorktreeCleanupFailed.IsMatch(line))
        {
            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = now,
                DetectorId = "framework-log-watchdog",
                Severity = FlowFindingSeverity.Warning,
                Summary = "Worktree cleanup partially failed — possible orphan worktree",
                Rationale =
                    "GitWorktreeManager.RemoveWorktreeQuietAsync exhausted retries on git worktree remove or " +
                    "Directory.Delete. After the 2026-05-12 cleanup-race fix this should be rare. Investigate " +
                    "whether descendant processes (MCP, Python sprite-gen, browser drivers) are leaking past " +
                    "their parent's exit. Recovery: confirm the candidate's commit is reachable via " +
                    "refs/candidates/{taskId}/{strategyId} (Layer 3 of the cleanup-race fix).",
                DedupKey = "framework-log:worktree-cleanup-failed",
            });
        }
    }

    /// <summary>Resolves the path to the most-recent Runner stdout log under <c>Logs/</c>.</summary>
    private static string? ResolveLatestRunnerLogPath()
    {
        try
        {
            // Look for Logs/ relative to the CWD or a few likely candidates. The runner
            // writes to ../../Logs/ from src/VirtualDevTeam.Runner/bin/Debug/net8.0/.
            var candidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "Logs"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Logs"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Logs"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Logs"),
            };
            foreach (var candidate in candidates)
            {
                var resolved = Path.GetFullPath(candidate);
                if (!Directory.Exists(resolved)) continue;
                var latest = new DirectoryInfo(resolved)
                    .EnumerateFiles("runner-*-stdout.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest is not null) return latest.FullName;
            }
        }
        catch { /* best-effort */ }
        return null;
    }
}
