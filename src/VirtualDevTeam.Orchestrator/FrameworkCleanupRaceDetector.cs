using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Confirms the cleanup-race fix is doing its job. Triggers when:
///
/// 1. <b>The Critical "cleanup blocked, uncommitted work" log fires</b> — Layer 1
///    of the cleanup-race fix (auto-commit before patch extraction in
///    <c>GitWorktreeManager.ExtractPatchAsync</c>) somehow could not commit
///    a candidate's working-tree changes, which means the candidate's work is
///    in imminent danger of loss when the worktree is torn down. This is the
///    smoking-gun signal we want operators alerted to immediately.
///
/// 2. <b>A successful framework run produced ZERO files in its eval candidate</b>
///    — strong indication that Layer 1 was bypassed and the patch came out empty.
///
/// Operator action: investigate before next run; the framework-log-watchdog will
/// also emit a finding for the underlying log line.
/// </summary>
public sealed class FrameworkCleanupRaceDetector : IFlowDetector
{
    public string DetectorId => "framework-cleanup-race";

    private static readonly System.Text.RegularExpressions.Regex CleanupBlockedLog = new(
        @"cleanup blocked.*uncommitted work in worktree|EnsureWorkCommittedAsync.*FATAL|pre-patch auto-commit failed",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly ILogger<FrameworkCleanupRaceDetector> _logger;
    private readonly Func<string?> _logPathProvider;
    private long _lastReadOffset;
    private string? _lastLogPath;

    public FrameworkCleanupRaceDetector(ILogger<FrameworkCleanupRaceDetector> logger)
        : this(logger, FrameworkLogWatchdogDetector_LogPath) { }

    internal FrameworkCleanupRaceDetector(ILogger<FrameworkCleanupRaceDetector> logger, Func<string?> logPathProvider)
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

            if (!string.Equals(logPath, _lastLogPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastReadOffset = 0;
                _lastLogPath = logPath;
            }
            var fi = new FileInfo(logPath);
            if (fi.Length < _lastReadOffset)
                _lastReadOffset = 0;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_lastReadOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            // Cap scan at 5000 lines/tick to honor the IFlowDetector ≤2s contract.
            // Without this a multi-GB log on first tick after restart blocks the FlowMonitor.
            // Same cap as FrameworkLogWatchdogDetector (TailLineCount * 5).
            const int MaxLinesPerTick = 5000;
            int scanned = 0;
            while (scanned++ < MaxLinesPerTick && (line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (CleanupBlockedLog.IsMatch(line))
                {
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        Summary = "Framework cleanup-race fix Layer 1 (auto-commit) failed for a candidate",
                        Rationale =
                            "GitWorktreeManager.ExtractPatchAsync's pre-patch auto-commit failed for a candidate " +
                            "worktree. This means the candidate's work is in imminent danger of loss when the " +
                            "worktree is torn down. Investigate: was the worktree's git config corrupted? Are " +
                            "hooks fighting back despite --no-verify? Manually preserve the candidate's HEAD via " +
                            "git update-ref refs/candidates/{taskId}/{strategyId} HEAD before next runner restart.",
                        DedupKey = "cleanup-race:auto-commit-failed",
                    });
                }
            }
            _lastReadOffset = fs.Position;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FrameworkCleanupRaceDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    /// <summary>Reuses the same log-path resolution as <see cref="FrameworkLogWatchdogDetector"/>.</summary>
    private static string? FrameworkLogWatchdogDetector_LogPath()
    {
        try
        {
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
