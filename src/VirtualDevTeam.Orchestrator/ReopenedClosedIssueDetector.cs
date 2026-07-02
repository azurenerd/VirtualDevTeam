using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Belt-and-suspenders defense for the issue-reopen pattern fixed in commit b22011c
/// (ResetToPendingAsync now refuses to reopen closed issues unless allowReopen:true).
///
/// <para>
/// Strategy: maintain a local cache of every issue number we have ever seen on the
/// <em>open</em> list. When an issue disappears from the open list we record its number
/// and the time we last saw it closed. If the same issue number re-appears on the open
/// list in a later tick we know it transitioned Closed→Open and we fire a Critical finding.
/// </para>
///
/// <para>
/// First tick: populates the "currently open" set as the baseline; no findings.
/// Second+ tick: any issue that was in <c>_lastSeenClosedAt</c> (previously seen absent
/// from open list) but is now present on the open list → reopen detected.
/// </para>
///
/// <para>
/// Dedup key includes the approximate close timestamp so a legitimate reopen for a new
/// sprint of work (where months have elapsed) doesn't stay silenced by the old dedup window.
/// </para>
/// </summary>
public sealed class ReopenedClosedIssueDetector : IFlowDetector
{
    public string DetectorId => "reopened-closed-issue";

    private readonly ILogger<ReopenedClosedIssueDetector> _logger;

    /// <summary>Issue numbers observed as open on the most-recent tick.</summary>
    private HashSet<int> _previouslyOpen = new();

    /// <summary>
    /// Issue numbers that have DISAPPEARED from the open list (i.e. closed), keyed to
    /// the UTC date (hour precision) when we first noticed them gone.
    /// </summary>
    private readonly Dictionary<int, DateTimeOffset> _lastSeenClosedAt = new();

    /// <summary>Whether we have completed at least one full population tick.</summary>
    private bool _initialized;

    private readonly object _lock = new();

    public ReopenedClosedIssueDetector(ILogger<ReopenedClosedIssueDetector> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var openItems = await ctx.Platform.ListOpenWorkItemsAsync(ct).ConfigureAwait(false);
            var currentlyOpenNumbers = new HashSet<int>(openItems.Select(w => w.Number));

            lock (_lock)
            {
                if (!_initialized)
                {
                    // First tick: just populate the baseline, no findings.
                    _previouslyOpen = currentlyOpenNumbers;
                    _initialized = true;
                    _logger.LogDebug(
                        "ReopenedClosedIssueDetector: baseline populated with {Count} open issues",
                        _previouslyOpen.Count);
                    return findings;
                }

                // Issues that were open last tick but absent now → record as closed.
                foreach (var number in _previouslyOpen)
                {
                    if (!currentlyOpenNumbers.Contains(number))
                        _lastSeenClosedAt.TryAdd(number, ctx.Now);
                }

                // Issues that were closed (absent) before but are open now → reopen detected.
                // 2026-05-12 rubber-duck fix: iterate over a SNAPSHOT (.ToList()) to avoid
                // InvalidOperationException ("Collection was modified") when calling
                // _lastSeenClosedAt.Remove(number) inside the loop body. Without the snapshot,
                // the first reopen fired correctly but subsequent reopens in the same tick
                // (or even the _previouslyOpen update on line 115) would throw and be silently
                // swallowed by the outer catch, leaving _previouslyOpen stale and re-firing
                // the same finding every tick.
                var numbersToRemove = new List<int>();
                foreach (var (number, closedAt) in _lastSeenClosedAt.ToList())
                {
                    if (!currentlyOpenNumbers.Contains(number)) continue;

                    // Issue re-appeared on the open list.
                    var closedDateTag = closedAt.ToString("yyyyMMddHH");
                    var workItem = openItems.FirstOrDefault(w => w.Number == number);
                    var title = workItem?.Title ?? $"#{number}";
                    var assignedAgent = workItem?.AssignedAgent ?? "unknown agent";

                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetResource = $"issue#{number}",
                        Summary = $"Issue #{number} was closed then re-opened (potential reopen-via-side-effect)",
                        Rationale =
                            $"Issue #{number} ('{Truncate(title, 80)}', assigned: {assignedAgent}) was observed " +
                            $"absent from the open-issues list around {closedAt:u} — indicating it was closed. " +
                            "It is now present on the open list again, which means something re-opened it. " +
                            "Root cause fix (b22011c) prevents ResetToPendingAsync from reopening closed issues " +
                            "without explicit allowReopen:true, but a side-effect path may still exist. " +
                            "Investigate which agent or API call triggered the reopen and verify the fix covers it.",
                        DedupKey = $"reopened-issue:{number}:{closedDateTag}",
                    });

                    numbersToRemove.Add(number);
                }

                // Second pass: remove fired entries AFTER iteration completes.
                foreach (var n in numbersToRemove)
                    _lastSeenClosedAt.Remove(n);

                _previouslyOpen = currentlyOpenNumbers;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReopenedClosedIssueDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
