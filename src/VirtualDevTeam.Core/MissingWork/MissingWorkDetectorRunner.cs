using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.MissingWork;

/// <summary>
/// Background service that periodically runs every <see cref="IMissingWorkDetector"/> registered
/// in DI, building a fresh <see cref="MissingWorkContext"/> per tick from current workspace +
/// issue tracker state. Findings are logged for now (Phase 1.2 MVP); the next pieces are
/// persistence + planner + Approvals UI.
///
/// <para>
/// Cadence: every <c>MissingWork:IntervalMinutes</c> (default 10 min). Detectors are run
/// sequentially with a budget of ~5 s each. Total tick time is bounded; the service won't
/// run a tick if the previous tick is still in flight (uses a SemaphoreSlim).
/// </para>
///
/// <para>
/// On each finding, emits structured Information log so operators can see what was caught.
/// When persistence + planner ship in subsequent commits, findings will instead be
/// inserted into the <c>missing_work_findings</c> SQLite table and the planner will be
/// invoked for findings with <c>Confidence ≥ 0.6</c>.
/// </para>
/// </summary>
public sealed class MissingWorkDetectorRunner : BackgroundService
{
    private readonly IEnumerable<IMissingWorkDetector> _detectors;
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly IWorkItemService? _workItems;
    private readonly MissingWorkPersistence? _persistence;
    private readonly IMissingWorkPlanner? _planner;
    private readonly ILogger<MissingWorkDetectorRunner> _logger;
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public MissingWorkDetectorRunner(
        IEnumerable<IMissingWorkDetector> detectors,
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<MissingWorkDetectorRunner> logger,
        IWorkItemService? workItems = null,
        MissingWorkPersistence? persistence = null,
        IMissingWorkPlanner? planner = null)
    {
        _detectors = detectors ?? throw new ArgumentNullException(nameof(detectors));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workItems = workItems;
        _persistence = persistence;
        _planner = planner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var detectorList = _detectors.ToList();
        if (detectorList.Count == 0)
        {
            _logger.LogInformation("MissingWorkDetectorRunner: no detectors registered, exiting");
            return;
        }
        _logger.LogInformation(
            "MissingWorkDetectorRunner starting with {Count} detector(s): {Ids}",
            detectorList.Count, string.Join(", ", detectorList.Select(d => d.DetectorId)));

        // Stagger first tick by 60s so we don't spam findings during runner cold-start
        // (workspace may not have a clone yet, issue tracker fetch may rate-limit).
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOneTickAsync(detectorList, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MissingWorkDetectorRunner tick failed (non-fatal)");
            }
            var cfg = _config.CurrentValue.MissingWork;
            var interval = TimeSpan.FromSeconds(Math.Max(60, cfg.IntervalMinutes * 60));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOneTickAsync(List<IMissingWorkDetector> detectors, CancellationToken ct)
    {
        if (!await _tickLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("MissingWorkDetectorRunner: previous tick still in flight, skipping");
            return;
        }
        try
        {
            var ctx = await BuildContextAsync(ct);
            if (ctx is null)
            {
                _logger.LogDebug("MissingWorkDetectorRunner: skipping tick — no workspace root configured");
                return;
            }

            var sw = Stopwatch.StartNew();
            int totalFindings = 0;
            foreach (var detector in detectors)
            {
                if (ct.IsCancellationRequested) break;
                var dsw = Stopwatch.StartNew();
                IReadOnlyList<MissingWorkFinding> findings;
                var detectorTimeout = TimeSpan.FromSeconds(Math.Max(5, _config.CurrentValue.MissingWork.PerDetectorTimeoutSeconds));
                using var detectorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                detectorCts.CancelAfter(detectorTimeout);
                try { findings = await detector.DetectAsync(ctx, detectorCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "MissingWorkDetectorRunner: {Id} exceeded {Seconds}s timeout — skipping (will retry next tick)",
                        detector.DetectorId, detectorTimeout.TotalSeconds);
                    continue;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "MissingWorkDetectorRunner: {Id} threw (will continue with other detectors)",
                        detector.DetectorId);
                    continue;
                }
                dsw.Stop();
                _logger.LogDebug(
                    "MissingWorkDetectorRunner: {Id} produced {Count} finding(s) in {Ms}ms",
                    detector.DetectorId, findings.Count, dsw.ElapsedMilliseconds);

                foreach (var f in findings)
                {
                    totalFindings++;
                    var evidenceStr = string.Join("; ", f.Evidence.Take(3)
                        .Select(e => $"{e.FilePath}{(e.LineNumber.HasValue ? $":{e.LineNumber}" : "")}"));
                    if (_persistence is not null)
                    {
                        var inserted = _persistence.InsertFinding(f, TimeSpan.FromHours(2));
                        if (!inserted)
                        {
                            _logger.LogDebug(
                                "MissingWork[{Detector}] {Summary} — dedup-suppressed (seen within 2h)",
                                f.DetectorId, f.Summary);
                            continue;
                        }
                        if (_planner is not null)
                        {
                            try { await _planner.PlanProposalAsync(f, ct); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Planner failed for {Id} (non-fatal)", f.Id); }
                        }
                    }
                    _logger.LogInformation(
                        "MissingWork[{Detector}] {Summary} (confidence={Conf:F2}, pattern='{Pattern}'). Evidence: {Evidence}",
                        f.DetectorId, f.Summary, f.Confidence, f.Pattern, evidenceStr);
                }
            }
            sw.Stop();
            if (totalFindings > 0)
            {
                _logger.LogInformation(
                    "MissingWorkDetectorRunner tick complete: {Total} finding(s) across {DetectorCount} detector(s) in {Ms}ms",
                    totalFindings, detectors.Count, sw.ElapsedMilliseconds);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task<MissingWorkContext?> BuildContextAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        var workspaceRoot = cfg.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;

        // The detectors scan a project workspace — typically a single agent's repo clone.
        // For the MVP we pick the first agent subdir that has a real .git clone in it.
        var resolvedRoot = ResolveProjectWorkspaceRoot(workspaceRoot);
        if (resolvedRoot is null) return null;

        IReadOnlyList<IssueRef> openIssues = Array.Empty<IssueRef>();
        IReadOnlyList<IssueRef> recentClosed = Array.Empty<IssueRef>();
        if (_workItems is not null)
        {
            try
            {
                var open = await _workItems.ListOpenAsync(ct);
                openIssues = open.Select(i => new IssueRef(
                    i.Number,
                    i.Title ?? "",
                    (IReadOnlyList<string>)(i.Labels ?? new List<string>()),
                    i.Body ?? "")).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MissingWorkDetectorRunner: failed to fetch open issues (best-effort)");
            }
        }

        return new MissingWorkContext
        {
            WorkspaceRoot = resolvedRoot,
            OpenIssues = openIssues,
            RecentlyClosedIssues = recentClosed,
            Now = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// The configured Workspace.RootPath is a parent dir (e.g. <c>.agents</c>) — each agent
    /// has its own subdirectory under there with a clone of the project repo. For detector
    /// purposes we want a single project clone to scan. Pick the first agent subdir that
    /// has a <c>.git</c> child (or fall back to the root itself if it has <c>.git</c>).
    /// </summary>
    private static string? ResolveProjectWorkspaceRoot(string configuredRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(configuredRoot);
            if (Directory.Exists(Path.Combine(fullRoot, ".git"))) return fullRoot;
            if (!Directory.Exists(fullRoot)) return null;
            foreach (var agentDir in Directory.EnumerateDirectories(fullRoot))
            {
                foreach (var repoDir in Directory.EnumerateDirectories(agentDir))
                {
                    if (Directory.Exists(Path.Combine(repoDir, ".git"))) return repoDir;
                }
            }
            return null;
        }
        catch { return null; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _tickLock.Dispose();
    }
}
