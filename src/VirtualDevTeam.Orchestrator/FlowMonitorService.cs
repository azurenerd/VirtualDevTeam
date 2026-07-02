using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.HealthMonitor.Diagnostics;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Always-on background service that watches the flow of the multi-agent system,
/// detects stuck states via a catalog of <see cref="IFlowDetector"/>s, and applies
/// safe in-process corrective actions via <see cref="IFlowAction"/>s.
///
/// All findings + actions are persisted to SQLite (via <see cref="FlowMonitorPersistence"/>)
/// so the dashboard can render an audit trail. Notifications are emitted via the existing
/// notification bell when an action is taken.
///
/// Hard rules this service obeys:
///   - NEVER restarts processes
///   - NEVER recompiles
///   - NEVER force-merges PRs
///   - NEVER modifies code
///   - NEVER deletes issues / PRs / branches
///   - Rate-limits actions to <see cref="FlowMonitorConfig.MaxActionsPerHour"/>
///   - Deduplicates findings by DedupKey within <see cref="FlowMonitorConfig.DedupWindowMinutes"/>
///
/// If the AI used for "rubber-duck a fix" is unavailable, the service falls back to its
/// vetted action catalog without invoking AI. That catalog only contains low-risk actions
/// (kick a poll, post a comment, refresh a gate) that have well-understood semantics.
/// </summary>
public sealed class FlowMonitorService : BackgroundService
{
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;
    private readonly IReadOnlyList<IFlowDetector> _detectors;
    private readonly IReadOnlyList<IFlowAction> _actions;
    private readonly IReadOnlyList<IFlowDiagnosticEnricher> _enrichers;
    private readonly FlowMonitorPersistence _persistence;
    private readonly GateNotificationService? _notifications;
    private readonly FixRecommendationPlannerService? _recommendationPlanner;
    private readonly IPullRequestService? _pullRequestService;
    private readonly IWorkItemService? _workItemService;
    private readonly IReviewService? _reviewService;
    private readonly FlowMonitorEventBus? _eventBus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FlowMonitorService> _logger;
    private readonly IOptionsMonitor<FlowMonitorConfig> _config;
    private readonly VirtualDevTeamConfig _projectConfig;

    public FlowMonitorService(
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        IEnumerable<IFlowDetector> detectors,
        IEnumerable<IFlowAction> actions,
        FlowMonitorPersistence persistence,
        IOptionsMonitor<FlowMonitorConfig> config,
        IOptions<VirtualDevTeamConfig> projectConfig,
        ILoggerFactory loggerFactory,
        ILogger<FlowMonitorService> logger,
        GateNotificationService? notifications = null,
        FixRecommendationPlannerService? recommendationPlanner = null,
        IPullRequestService? pullRequestService = null,
        IWorkItemService? workItemService = null,
        IReviewService? reviewService = null,
        FlowMonitorEventBus? eventBus = null,
        IEnumerable<IFlowDiagnosticEnricher>? enrichers = null)
    {
        _registry = registry;
        _workflow = workflow;
        _detectors = detectors.ToList();
        _actions = actions.ToList();
        _enrichers = enrichers?.ToList() ?? new List<IFlowDiagnosticEnricher>();
        _persistence = persistence;
        _notifications = notifications;
        _recommendationPlanner = recommendationPlanner;
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _reviewService = reviewService;
        _eventBus = eventBus;
        _config = config;
        _projectConfig = projectConfig.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "FlowMonitor starting with {Detectors} detector(s) and {Actions} action(s). " +
            "Config: enabled={Enabled}, poll={Poll}s, max-actions/hr={Cap}",
            _detectors.Count, _actions.Count,
            _config.CurrentValue.Enabled, _config.CurrentValue.PollIntervalSeconds,
            _config.CurrentValue.MaxActionsPerHour);
        _eventBus?.Lifecycle(
            $"FlowMonitor starting — {_detectors.Count} detector(s), {_actions.Count} action(s)",
            $"enabled={_config.CurrentValue.Enabled}, poll={_config.CurrentValue.PollIntervalSeconds}s, cap={_config.CurrentValue.MaxActionsPerHour}/hr");

        // Configurable startup delay (T0.5) — gives other hosted services time to initialize
        // before we start probing them. Floor 0 so tests can opt out entirely.
        var startupDelay = Math.Max(0, _config.CurrentValue.StartupDelaySeconds);
        if (startupDelay > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(startupDelay), ct); }
            catch (OperationCanceledException) { return; }
        }

        var lastPruneAt = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_config.CurrentValue.Enabled)
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                // Daily retention prune (T0.9) — runs at most once per 24h regardless of config.
                // Best-effort: failures don't crash the loop.
                var retentionDays = _config.CurrentValue.RetentionDays;
                if (retentionDays > 0 && (DateTimeOffset.UtcNow - lastPruneAt).TotalHours >= 24)
                {
                    _persistence.PruneOldRecords(TimeSpan.FromDays(retentionDays));
                    lastPruneAt = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlowMonitor tick crashed (non-fatal — will retry)");
                _eventBus?.Error("service", $"Tick crashed: {ex.GetType().Name}: {ex.Message}", ex.ToString());
            }

            var pollSeconds = Math.Max(10, _config.CurrentValue.PollIntervalSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("FlowMonitor stopping.");
        _eventBus?.Lifecycle("FlowMonitor stopping");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        // Build the DetectorContext snapshot once per tick — detectors all see the same view.
        var ctx = BuildContext(now);

        var findingsCreated = 0;
        var actionsTaken = 0;
        var detectorsRun = new List<string>();

        _eventBus?.Lifecycle(
            $"Tick start — phase={ctx.CurrentPhase}, agents={ctx.Agents.Count}",
            null);

        // T1.3: verify findings we acted on previously, BEFORE running detectors. Two
        // outcomes per acted-on finding: condition cleared → mark Resolved + emit a
        // ✅ event; condition persists → bump severity AND mark the existing finding
        // Expired so the dedup window doesn't suppress the fresh detection. The fresh
        // detection then flows through the regular ladder routing below — which will
        // pick the next rung because GetAttemptCount sees the prior actions on the
        // same dedup_key.
        try
        {
            await VerifyActedOnFindingsAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlowMonitor: VerifyActedOnFindingsAsync failed (non-fatal)");
            _eventBus?.Error("verify", $"Verification pass failed: {ex.GetType().Name}: {ex.Message}", ex.ToString());
        }

        // post-run2-undo-on-expired: sweep recently-Expired findings whose underlying condition
        // has cleared but whose prior side-effect actions (e.g., agent-stuck label from Rung 3)
        // still linger. The Verify path above only handles ActedOn → Resolved transitions; an
        // Open → ActedOn → Verified-Failed → Expired flow leaves the label dangling because the
        // Expired finding is never re-verified. This sweep fills that gap.
        try
        {
            await RunUndoSweepAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlowMonitor: RunUndoSweepAsync failed (non-fatal)");
        }

        foreach (var detector in _detectors)
        {
            // Per-detector enable gate via config (keys: PhaseCompletionMismatch, AgentStuck, etc.)
            if (!cfg.IsDetectorEnabled(detector.DetectorId))
            {
                _logger.LogTrace("FlowMonitor: detector {Id} disabled by config — skipping", detector.DetectorId);
                continue;
            }

            detectorsRun.Add(detector.DetectorId);
            _eventBus?.Detector(detector.DetectorId, "running");
            IReadOnlyList<FlowFinding> findings;
            try { findings = await detector.DetectAsync(ctx, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FlowMonitor: detector {Id} threw (non-fatal — wrapped here, but detector should never throw)", detector.DetectorId);
                _eventBus?.Error(detector.DetectorId, $"Detector threw: {ex.GetType().Name}: {ex.Message}", ex.ToString());
                continue;
            }

            _eventBus?.Detector(detector.DetectorId,
                $"completed — {findings.Count} finding(s)");

            foreach (var rawFinding in findings)
            {
                // Diagnostic enrichment: run enrichers to explain WHY the agent is stuck
                var finding = rawFinding;
                foreach (var enricher in _enrichers)
                {
                    if (!enricher.CanEnrich(finding.DetectorId)) continue;
                    try
                    {
                        finding = await enricher.EnrichAsync(finding, ctx, ct);
                    }
                    catch (Exception enrichEx)
                    {
                        _logger.LogDebug(enrichEx, "FlowMonitor: enricher failed for finding {Id}", finding.Id);
                    }
                    break; // One enricher per finding
                }

                var dedupWindow = TimeSpan.FromMinutes(Math.Max(1, cfg.DedupWindowMinutes));
                var inserted = _persistence.InsertFinding(finding, dedupWindow);
                if (!inserted)
                {
                    _logger.LogTrace("FlowMonitor: finding {Detector}/{DedupKey} suppressed by dedup window",
                        finding.DetectorId, finding.DedupKey);
                    _eventBus?.Finding(finding,
                        $"[{finding.Severity}] {finding.Summary} (dedup-suppressed)",
                        suppressed: true);
                    continue;
                }
                findingsCreated++;
                _eventBus?.Finding(finding, $"[{finding.Severity}] {finding.Summary}");

                // Rate-limit actions to MaxActionsPerHour — but exempt pipeline-unblocking
                // actions (gate-stuck, pr-approval-stuck) which are idempotent label writes
                // that must always fire to prevent indefinite pipeline stalls.
                var isPipelineUnblock = finding.DedupKey?.StartsWith(FlowMonitorConstants.GateStuckPrefix, StringComparison.Ordinal) == true
                    || finding.DedupKey?.StartsWith(FlowMonitorConstants.PrApprovalStuckPrefix + ":", StringComparison.OrdinalIgnoreCase) == true;
                if (!isPipelineUnblock)
                {
                    var since = now.AddHours(-1);
                    var actionsLastHour = _persistence.CountActionsSince(since);
                    if (actionsLastHour >= cfg.MaxActionsPerHour)
                    {
                        _logger.LogInformation(
                            "FlowMonitor: action rate limit reached ({Count}/{Cap} in last hour) — finding {Id} logged but no action taken",
                            actionsLastHour, cfg.MaxActionsPerHour, finding.Id);
                        _eventBus?.Info(finding.DetectorId,
                            $"Rate-limit hit ({actionsLastHour}/{cfg.MaxActionsPerHour}/hr) — no action taken on {finding.Id}");
                        continue;
                    }
                }

                // T1.2: pick action by escalation rung based on prior-action count for this
                // dedup_key. Falls back to the legacy "first CanHandle" if the preferred
                // rung-action isn't registered (e.g. tests, or future detector types
                // without rung-2/-3 implementations yet).
                var priorAttempts = string.IsNullOrEmpty(finding.DedupKey)
                    ? 0
                    : _persistence.GetAttemptCount(finding.DedupKey, TimeSpan.FromHours(4));
                var attemptCount = priorAttempts + 1; // 1-based: this attempt is the next rung
                var actionRunner = PickActionForRung(finding, priorAttempts);
                if (actionRunner is null)
                {
                    _logger.LogDebug(
                        "FlowMonitor: no action handler for finding {Id} (detector={Detector}) — finding logged for human review",
                        finding.Id, finding.DetectorId);
                    _eventBus?.Info(finding.DetectorId,
                        $"No action handler for finding {finding.Id} — logged for human review");

                    // T1.5: For Critical findings with no action handler, generate a /plan +
                    // rubber-duck FixRecommendation so the operator has a concrete starting point.
                    // Wrapped to never crash the tick loop — if the planner is unregistered or
                    // the LLM is down, we just fall through to "logged for human review".
                    if (finding.Severity == FlowFindingSeverity.Critical && _recommendationPlanner is not null)
                    {
                        _ = TryGenerateFixRecommendationAsync(finding, ct);
                    }
                    continue;
                }

                if (!cfg.IsActionEnabled(actionRunner.ActionType))
                {
                    var skipped = new FlowAction
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FindingId = finding.Id,
                        ActionType = actionRunner.ActionType,
                        Target = finding.TargetAgentId ?? finding.TargetResource,
                        InitiatedAt = now,
                        CompletedAt = now,
                        Result = FlowActionResult.Skipped,
                        Detail = "Action disabled by FlowMonitor config",
                        AttemptCount = attemptCount,
                    };
                    _persistence.InsertAction(skipped);
                    _eventBus?.ActionCompleted(skipped, $"{actionRunner.ActionType} skipped (disabled)");
                    continue;
                }

                var initiated = DateTimeOffset.UtcNow;
                _eventBus?.ActionStarted(
                    actionRunner.ActionType,
                    finding.Id,
                    finding.TargetAgentId ?? finding.TargetResource,
                    $"rung {attemptCount}: {actionRunner.ActionType} → {finding.TargetAgentId ?? finding.TargetResource ?? "(none)"}");
                FlowActionOutcome outcome;
                try { outcome = await actionRunner.ExecuteAsync(finding, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    outcome = new FlowActionOutcome
                    {
                        Result = FlowActionResult.Failed,
                        Target = finding.TargetAgentId ?? finding.TargetResource,
                        Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
                    };
                }

                var actionRecord = new FlowAction
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FindingId = finding.Id,
                    ActionType = actionRunner.ActionType,
                    Target = outcome.Target,
                    InitiatedAt = initiated,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = outcome.Result,
                    Detail = outcome.Detail,
                    AttemptCount = attemptCount,
                };
                _persistence.InsertAction(actionRecord);
                _persistence.UpdateFindingState(finding.Id, FlowFindingState.ActedOn);
                actionsTaken++;

                _logger.LogInformation(
                    "FlowMonitor: rung {Rung} action {ActionType} on {Target} → {Result} ({Detail})",
                    attemptCount, actionRunner.ActionType, outcome.Target ?? "(none)", outcome.Result, outcome.Detail);
                _eventBus?.ActionCompleted(actionRecord);

                EmitNotification(finding, actionRecord);
            }
        }

        _persistence.RecordTick(detectorsRun, findingsCreated, actionsTaken);
        _eventBus?.Lifecycle(
            $"Tick complete — detectors={detectorsRun.Count}, findings={findingsCreated}, actions={actionsTaken}");
    }

    /// <summary>
    /// T1.2: pick the action that matches the next escalation rung for this finding.
    /// 2-rung ladder (simplified May 2026 — rung-2 PR comments were never read by agents):
    ///   Rung 1 (priorAttempts=0) → bus nudge ("kick-agent-poll")
    ///   Rung ≥2 (priorAttempts ≥1) → human escalation with diagnostic context ("escalate-to-human")
    /// The old rung-2 "post-explicit-ask" (PR comment posting) is removed from the escalation
    /// sequence — research confirmed no agent parses FlowMonitor comments.
    /// The action also has to <see cref="IFlowAction.CanHandle"/>
    /// the finding — otherwise we fall back to the first CanHandle action so a missing
    /// rung doesn't break the chain (graceful-degradation principle).
    /// </summary>
    internal IFlowAction? PickActionForRung(FlowFinding finding, int priorAttempts)
    {
        // Gate-stuck findings always route to auto-approve — no escalation ladder.
        // The auto-approval IS the intended resolution, not a nudge or human escalation.
        if (finding.DedupKey?.StartsWith(FlowMonitorConstants.GateStuckPrefix) == true)
        {
            return _actions.FirstOrDefault(a =>
                string.Equals(a.ActionType, "auto-approve-gate", StringComparison.OrdinalIgnoreCase)
                && a.CanHandle(finding));
        }

        // PR-approval-stuck findings route to auto-approve-review — same pattern as gates.
        // The 10-min MissingReviewerDetector nudge gives the reviewer a chance first;
        // at 15 min we add the missing label directly to unblock the pipeline.
        if (finding.DedupKey?.StartsWith(FlowMonitorConstants.PrApprovalStuckPrefix + ":") == true)
        {
            return _actions.FirstOrDefault(a =>
                string.Equals(a.ActionType, "auto-approve-review", StringComparison.OrdinalIgnoreCase)
                && a.CanHandle(finding));
        }

        var preferredType = priorAttempts switch
        {
            <= 0 => "kick-agent-poll",
            _ => "escalate-to-human",  // Skip rung-2 (PR comments) — agents don't read them
        };

        var preferred = _actions.FirstOrDefault(a =>
            string.Equals(a.ActionType, preferredType, StringComparison.OrdinalIgnoreCase)
            && a.CanHandle(finding));
        if (preferred is not null) return preferred;

        // Fallback: first registered action that can handle this finding type. Keeps
        // legacy behavior (single-action registration) working until all rungs ship.
        return _actions.FirstOrDefault(a => a.CanHandle(finding));
    }

    /// <summary>
    /// T1.3: Re-run detectors for findings still in <see cref="FlowFindingState.ActedOn"/>
    /// state inside the past hour and confirm the fix took effect.
    ///
    /// Detectors don't currently support a target filter, so we re-run the full detector
    /// pass and inspect the returned set for a finding matching the same target_agent_id
    /// or target_resource. If none → mark Resolved. If a match comes back → bump severity
    /// (Info → Warning → Critical) and mark the original finding Expired so the regular
    /// detector loop's dedup window doesn't suppress the new emission. Each verification
    /// is itself logged as a flow_action row (ActionType="verify-acted-on") so the
    /// dashboard timeline shows the "checked & cleared" / "checked & still present" trail.
    ///
    /// Hour cap on what we verify keeps work bounded — old ActedOn rows that never got
    /// resolved age out via the dedup window naturally.
    /// </summary>
    private async Task VerifyActedOnFindingsAsync(DetectorContext ctx, CancellationToken ct)
    {
        var sinceCutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var actedOn = _persistence.GetActedOnFindingsSince(sinceCutoff);
        if (actedOn.Count == 0) return;

        // Cache detector output per detector-id within a single verification pass — same
        // detector firing for two distinct findings only runs once.
        var perDetectorResults = new Dictionary<string, IReadOnlyList<FlowFinding>>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in actedOn)
        {
            ct.ThrowIfCancellationRequested();

            // Fast-path: if the finding targets a PR that's been merged or closed,
            // resolve it immediately without re-running the detector. This prevents
            // stale findings from persisting for up to 5 minutes on the throttle cycle.
            if (finding.TargetResource?.StartsWith("pr#", StringComparison.OrdinalIgnoreCase) == true &&
                _pullRequestService is not null)
            {
                var prNumStr = finding.TargetResource.Substring(3);
                if (int.TryParse(prNumStr, out var prNum))
                {
                    try
                    {
                        var pr = await _pullRequestService.GetAsync(prNum, ct).ConfigureAwait(false);
                        if (pr is null || pr.IsMerged || pr.State != "open")
                        {
                            _logger.LogInformation(
                                "FlowMonitor: auto-resolving finding {Id} — target PR #{Number} is {State}",
                                finding.Id, prNum, pr?.IsMerged == true ? "merged" : (pr?.State ?? "gone"));
                            _persistence.UpdateFindingState(finding.Id, FlowFindingState.Resolved);
                            _eventBus?.ActionCompleted(new FlowAction
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                FindingId = finding.Id,
                                ActionType = "verify-acted-on",
                                Target = finding.TargetResource,
                                InitiatedAt = DateTimeOffset.UtcNow,
                                CompletedAt = DateTimeOffset.UtcNow,
                                Result = FlowActionResult.Success,
                                Detail = $"PR #{prNum} is {(pr?.IsMerged == true ? "merged" : "closed")} — auto-resolved",
                            }, $"✅ Resolved: {finding.Summary} (PR no longer open)");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "FlowMonitor: PR-state check failed for finding {Id} (non-fatal)", finding.Id);
                    }
                }
            }

            var detector = _detectors.FirstOrDefault(d =>
                string.Equals(d.DetectorId, finding.DetectorId, StringComparison.OrdinalIgnoreCase));
            if (detector is null)
            {
                // Detector no longer registered (possible after a refactor) — leave as-is
                // and let the dedup window naturally age it out.
                _logger.LogDebug(
                    "FlowMonitor: cannot verify finding {Id} — detector {Detector} not registered",
                    finding.Id, finding.DetectorId);
                continue;
            }

            // Throttle verification for max-rung findings: only re-verify every 5 minutes
            // to avoid spamming verify-acted-on rows (which count against the global action budget).
            const int MaxEscalationRungs = 3;
            var priorAttempts = string.IsNullOrEmpty(finding.DedupKey)
                ? 0
                : _persistence.GetAttemptCount(finding.DedupKey, TimeSpan.FromHours(4));
            if (priorAttempts >= MaxEscalationRungs)
            {
                var lastVerify = _persistence.GetLastActionTime(finding.Id, "verify-acted-on");
                if (lastVerify.HasValue && (DateTimeOffset.UtcNow - lastVerify.Value).TotalMinutes < 5)
                {
                    _logger.LogTrace(
                        "FlowMonitor: skipping verification for max-rung finding {Id} (last verified {Ago}m ago)",
                        finding.Id, (int)(DateTimeOffset.UtcNow - lastVerify.Value).TotalMinutes);
                    continue;
                }
            }

            IReadOnlyList<FlowFinding> latest;
            if (!perDetectorResults.TryGetValue(detector.DetectorId, out latest!))
            {
                try { latest = await detector.DetectAsync(ctx, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "FlowMonitor: verification re-run of detector {Id} threw (non-fatal)",
                        detector.DetectorId);
                    continue;
                }
                perDetectorResults[detector.DetectorId] = latest;
            }

            var stillPresent = latest.Any(f => MatchesTarget(f, finding));
            var verifyAction = new FlowAction
            {
                Id = Guid.NewGuid().ToString("N"),
                FindingId = finding.Id,
                ActionType = "verify-acted-on",
                Target = finding.TargetAgentId ?? finding.TargetResource,
                InitiatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Result = stillPresent ? FlowActionResult.Failed : FlowActionResult.Success,
                Detail = stillPresent
                    ? $"Detector {detector.DetectorId} still reports the condition for {finding.TargetAgentId ?? finding.TargetResource ?? "(none)"}"
                    : $"Detector {detector.DetectorId} no longer reports the condition for {finding.TargetAgentId ?? finding.TargetResource ?? "(none)"}",
                AttemptCount = 0, // verification doesn't consume a rung
            };
            _persistence.InsertAction(verifyAction);
            _eventBus?.ActionCompleted(verifyAction,
                stillPresent
                    ? $"⏳ Verification: {finding.Summary} — condition persists"
                    : $"✅ Resolved: {finding.Summary}");

            if (!stillPresent)
            {
                _persistence.UpdateFindingState(finding.Id, FlowFindingState.Resolved);
                _logger.LogInformation(
                    "FlowMonitor: finding {Id} ({Detector}/{Target}) marked Resolved — condition cleared",
                    finding.Id, finding.DetectorId, finding.TargetAgentId ?? finding.TargetResource ?? "(none)");

                // post-run-stuck-label-cleanup: invoke UndoAsync on each successful prior action
                // for this finding so any platform-state side effects (e.g., agent-stuck label
                // applied by Rung 3) get reversed. Most actions have no-op default; only
                // EscalateToHumanAction overrides today. Failures are swallowed — the finding is
                // already Resolved either way.
                await UndoPriorActionsAsync(finding, ct).ConfigureAwait(false);
            }
            else
            {
                var bumped = BumpSeverity(finding.Severity);
                if (bumped != finding.Severity)
                {
                    _persistence.UpdateFindingSeverity(finding.Id, bumped);
                    _logger.LogInformation(
                        "FlowMonitor: finding {Id} severity bumped {From} → {To} (condition persists after rung action)",
                        finding.Id, finding.Severity, bumped);
                    _eventBus?.Info(finding.DetectorId,
                        $"Severity bump on {finding.Id}: {finding.Severity} → {bumped} (post-action condition still present)");
                }

                // Circuit-breaker: if the escalation ladder is exhausted (max rung = 3,
                // i.e., priorAttempts >= 3), keep the finding in ActedOn state so the
                // dedup window continues to suppress re-detection. Without this, the
                // Expired→re-detect→re-escalate cycle creates a new Approvals card every
                // 30s tick indefinitely.
                if (priorAttempts >= MaxEscalationRungs)
                {
                    // Leave as ActedOn — dedup stays active, verification continues to
                    // check for resolution, but no new findings/actions are emitted.
                    _logger.LogDebug(
                        "FlowMonitor: finding {Id} at max rung ({Attempts} attempts) — keeping ActedOn to prevent escalation spam",
                        finding.Id, priorAttempts);
                }
                else
                {
                    // Expire the existing finding so the regular detector loop's dedup window
                    // doesn't suppress the fresh re-detection. The new finding will pick up
                    // the next rung via GetAttemptCount(dedup_key).
                    _persistence.UpdateFindingState(finding.Id, FlowFindingState.Expired);
                }
            }
        }
    }

    private static bool MatchesTarget(FlowFinding candidate, FlowFinding original)
    {
        // Match on the strongest available identifier. Detectors emit a fresh GUID Id each
        // tick so we can't compare ids; use TargetAgentId or TargetResource (whichever is
        // set on both sides) — if neither matches, the dedup_key is the last resort.
        if (!string.IsNullOrEmpty(original.TargetAgentId)
            && string.Equals(candidate.TargetAgentId, original.TargetAgentId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(original.TargetResource)
            && string.Equals(candidate.TargetResource, original.TargetResource, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(original.DedupKey)
            && string.Equals(candidate.DedupKey, original.DedupKey, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// post-run-stuck-label-cleanup: invoke UndoAsync on each successful action that was taken
    /// against the now-Resolved finding. Most actions have a no-op default; only side-effect-
    /// applying actions (currently EscalateToHumanAction) override. Failures are swallowed —
    /// the finding is already Resolved either way.
    /// </summary>
    private async Task UndoPriorActionsAsync(FlowFinding finding, CancellationToken ct)
    {
        try
        {
            var prior = _persistence.GetActionsForFinding(finding.Id);
            foreach (var priorAction in prior)
            {
                var actionRunner = _actions.FirstOrDefault(a =>
                    string.Equals(a.ActionType, priorAction.ActionType, StringComparison.OrdinalIgnoreCase));
                if (actionRunner is null) continue;
                try
                {
                    await actionRunner.UndoAsync(finding, priorAction, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "UndoAsync threw for action {ActionType} on finding {FindingId} — non-fatal",
                        priorAction.ActionType, finding.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UndoPriorActionsAsync top-level failure for finding {Id}", finding.Id);
        }
    }

    /// <summary>
    /// post-run2-undo-on-expired: per-tick sweep for Expired findings whose condition has
    /// cleared since the verification path expired them. Re-runs each finding's originating
    /// detector once (results cached per detector for the sweep). If the detector no longer
    /// reports the condition for the same target, calls UndoAsync on prior side-effect-bearing
    /// actions (e.g., remove agent-stuck label) and marks the finding as undone in SQLite so
    /// future ticks don't re-process it.
    ///
    /// Cost: bounded by `flow_findings` rows in state=Expired with undone_at IS NULL within
    /// last 1h. Detector re-runs share the per-tick DetectorContext, so platform views stay
    /// cached. UndoAsync itself is idempotent (refetches labels, no-op if already gone).
    /// </summary>
    private async Task RunUndoSweepAsync(DetectorContext ctx, CancellationToken ct)
    {
        var sinceCutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var expired = _persistence.GetExpiredFindingsForUndoSweep(sinceCutoff);
        if (expired.Count == 0) return;

        var perDetectorResults = new Dictionary<string, IReadOnlyList<FlowFinding>>(StringComparer.OrdinalIgnoreCase);

        foreach (var finding in expired)
        {
            ct.ThrowIfCancellationRequested();
            var detector = _detectors.FirstOrDefault(d =>
                string.Equals(d.DetectorId, finding.DetectorId, StringComparison.OrdinalIgnoreCase));
            if (detector is null)
            {
                // Detector unregistered (refactor) — mark undone so we stop sweeping it; can't
                // verify cleared status, but leaving the row to re-process forever is worse.
                _persistence.MarkFindingUndone(finding.Id);
                continue;
            }

            IReadOnlyList<FlowFinding> latest;
            if (!perDetectorResults.TryGetValue(detector.DetectorId, out latest!))
            {
                try { latest = await detector.DetectAsync(ctx, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "FlowMonitor: undo-sweep re-run of detector {Id} threw — skipping this finding",
                        detector.DetectorId);
                    continue;
                }
                perDetectorResults[detector.DetectorId] = latest;
            }

            var stillPresent = latest.Any(f => MatchesTarget(f, finding));
            if (stillPresent) continue; // condition still here; defer until it clears or window expires

            // Condition has cleared since the finding was Expired. Run UndoAsync on each prior
            // action so side-effects (labels, etc.) get reversed. Bus messages and PR comments
            // have no-op UndoAsync, so they're untouched.
            await UndoPriorActionsAsync(finding, ct).ConfigureAwait(false);
            _persistence.MarkFindingUndone(finding.Id);

            _logger.LogInformation(
                "FlowMonitor: undo-sweep cleared side-effects for Expired finding {Id} ({Detector}/{Target})",
                finding.Id, finding.DetectorId, finding.TargetAgentId ?? finding.TargetResource ?? "(none)");
            _eventBus?.Info(finding.DetectorId,
                $"🧹 Undo-sweep: side-effects cleared for {finding.Summary}");
        }
    }

    private static FlowFindingSeverity BumpSeverity(FlowFindingSeverity current) => current switch
    {
        FlowFindingSeverity.Info => FlowFindingSeverity.Warning,
        FlowFindingSeverity.Warning => FlowFindingSeverity.Critical,
        _ => FlowFindingSeverity.Critical, // already at the top, stays there
    };

    private DetectorContext BuildContext(DateTimeOffset now)
    {
        var agents = _registry.GetAllAgents();
        var agentViews = agents.Select(a => new AgentStateView
        {
            Id = a.Identity.Id,
            DisplayName = a.Identity.DisplayName,
            Role = a.Identity.Role.ToString(),
            Status = a.Status.ToString(),
            StatusReason = a.StatusReason,
            // a.LastStatusChangedAt isn't a public surface — use registry's tracker if available.
            // Falls back to null when unknown; detectors handle null gracefully.
            StatusChangedAt = TryGetLastStatusChange(a.Identity.Id),
            Capabilities = (a.Identity.Capabilities as IReadOnlyList<string>) ?? Array.Empty<string>(),
            CurrentPrNumber = a.CurrentPrNumber,
        }).ToList();

        // T1.1: build a fresh per-tick lazy/cached platform view. If platform services aren't
        // available (e.g., before a project is opened), use NullPlatformView so detectors that
        // call ctx.Platform.* still get safe empty results without null-checks.
        IPlatformView platformView =
            _pullRequestService is null && _workItemService is null && _reviewService is null
                ? NullPlatformView.Instance
                : new PerTickPlatformView(
                    _pullRequestService, _workItemService, _reviewService,
                    _loggerFactory.CreateLogger<PerTickPlatformView>());

        return new DetectorContext
        {
            Now = now,
            Agents = agentViews,
            CurrentPhase = _workflow.CurrentPhase.ToString(),
            // T1.1: real signals (was Array.Empty). Lets detectors compare phase vs raised gates.
            WorkflowSignals = _workflow.GetSignals(),
            EffectiveBranch = _projectConfig.Project.DefaultBranch,
            Platform = platformView,
        };
    }

    /// <summary>
    /// AgentRegistry doesn't currently expose per-agent last-status-change timestamps in a
    /// stable API. The HealthMonitor service (Orchestrator/HealthMonitor.cs) tracks them
    /// internally via a ConcurrentDictionary; we replicate the simplest version here using
    /// our own tracker that subscribes to AgentStatusChanged.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _statusChangedAt = new();
    private int _statusChangedAtPruneCounter;

    /// <summary>Wire status-change tracking on first start. Idempotent.</summary>
    private bool _statusTrackerWired;
    private readonly object _wireLock = new();
    private DateTimeOffset? TryGetLastStatusChange(string agentId)
    {
        if (!_statusTrackerWired)
        {
            lock (_wireLock)
            {
                if (!_statusTrackerWired)
                {
                    _registry.AgentStatusChanged += (_, e) =>
                    {
                        _statusChangedAt[e.Agent.Id] = DateTimeOffset.UtcNow;
                    };
                    foreach (var agent in _registry.GetAllAgents())
                        _statusChangedAt[agent.Identity.Id] = DateTimeOffset.UtcNow;
                    _statusTrackerWired = true;
                }
            }
        }
        // Prune dead agents every 100th call (T0.6) — without this, dynamically-spawned SME
        // agents accumulate forever (~3MB after 12h soak test). O(N) but rare so amortized free.
        if (System.Threading.Interlocked.Increment(ref _statusChangedAtPruneCounter) % 100 == 0)
        {
            try
            {
                var liveIds = new System.Collections.Generic.HashSet<string>(
                    _registry.GetAllAgents().Select(a => a.Identity.Id));
                foreach (var key in _statusChangedAt.Keys)
                {
                    if (!liveIds.Contains(key))
                        _statusChangedAt.TryRemove(key, out _);
                }
            }
            catch { /* tracker prune is best-effort */ }
        }
        return _statusChangedAt.TryGetValue(agentId, out var t) ? t : null;
    }

    private void EmitNotification(FlowFinding finding, FlowAction action)
    {
        if (_notifications is null) return;
        if (action.Result is not (FlowActionResult.Success or FlowActionResult.Failed)) return;

        // Actions that create their own audit notifications (via AddFlowMonitorNotification
        // which is pre-resolved) don't need a second notification from here. Without this
        // guard, each auto-approve created TWO notifications — one pre-resolved from the
        // action, one from here that raced between AddNotificationAsync and Resolve, leaving
        // orphan unresolved entries that inflated the Open count.
        if (action.ActionType.StartsWith("auto-approve", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var targetLabel = action.Target ?? "pipeline";
            var context = $"Automatically handled: {finding.Summary}\n\n" +
                          $"**Action taken:** {HumanizeActionType(action.ActionType)} on {targetLabel}. " +
                          $"**Result:** {action.Result}" +
                          (string.IsNullOrEmpty(action.Detail) ? "" : $"\n\n{action.Detail}");

            // Use AddFlowMonitorNotification (pre-resolved, IsFlowMonitorAction=true)
            // instead of the two-step AddNotificationAsync→Resolve that had race conditions
            // under concurrent Task.Run. Pre-resolved means these never inflate Open count.
            _notifications.AddFlowMonitorNotification(
                $"flow-monitor:{action.ActionType}",
                $"🔧 FlowMonitor: {HumanizeActionType(action.ActionType)}",
                context);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FlowMonitor notification emit failed (non-fatal)");
        }
    }

    /// <summary>
    /// T1.5: When a Critical finding has no in-process action handler, kick off a two-pass
    /// /plan + rubber-duck Copilot generation so the operator has a concrete fix proposal
    /// in the Approvals page. Runs as a fire-and-forget Task so the planner's LLM latency
    /// (10–60s) never blocks the tick loop. All errors are caught and logged — the tick
    /// loop keeps running even if the planner is broken.
    ///
    /// If the resulting recommendation has confidence ≥ <see cref="FlowMonitorConfig.ConfidenceThreshold"/>,
    /// emit a non-auto-resolving gate notification so the bell icon picks it up. Below the
    /// threshold, persist silently — the operator can still see it in the Approvals page
    /// without being interrupted by a low-quality plan.
    /// </summary>
    private Task TryGenerateFixRecommendationAsync(FlowFinding finding, CancellationToken ct)
    {
        if (_recommendationPlanner is null) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "FlowMonitor: generating FixRecommendation for {Severity} finding {Id} ({Detector})",
                    finding.Severity, finding.Id, finding.DetectorId);

                var draft = await _recommendationPlanner.GenerateAsync(finding, ct).ConfigureAwait(false);

                // Persist to disk first (best-effort; even if disk write fails, DB record stays).
                var repoRoot = Directory.GetCurrentDirectory();
                var path = await _recommendationPlanner
                    .SaveToFixRecommendationsFolderAsync(draft, repoRoot, ct)
                    .ConfigureAwait(false);
                if (path is not null)
                    draft = draft with { PlanFilePath = path };

                var insertedId = _persistence.InsertRecommendation(draft);
                if (string.IsNullOrEmpty(insertedId))
                {
                    _logger.LogWarning(
                        "FlowMonitor: FixRecommendation for finding {Id} could not be persisted",
                        finding.Id);
                    return;
                }

                _logger.LogInformation(
                    "FlowMonitor: FixRecommendation {RecId} persisted (confidence={Confidence:0.00}, files={Files}, restart={Restart})",
                    insertedId, draft.Confidence, draft.FilesToChange ?? "(unknown)", draft.NeedsRestart);

                // Notify the operator only when the plan has cleared the confidence bar — keeps
                // the Approvals page focused on actionable items.
                var threshold = _config.CurrentValue.ConfidenceThreshold;
                if (_notifications is not null && draft.Confidence >= threshold)
                {
                    var summary = string.IsNullOrEmpty(draft.FilesToChange)
                        ? finding.Summary
                        : $"{finding.Summary} (files: {draft.FilesToChange})";
                    var context =
                        $"🔧 **Fix recommendation** ({draft.Confidence:0%} confidence) for finding `{finding.DetectorId}`. " +
                        $"{summary}" +
                        (draft.EstimatedMinutes.HasValue ? $" · est. {draft.EstimatedMinutes}m" : string.Empty) +
                        (draft.NeedsRestart ? " · runner restart needed" : string.Empty);

                    try
                    {
                        await _notifications.AddNotificationAsync(
                            gateId: $"flow-monitor:fix:{insertedId}",
                            context: context,
                            resourceNumber: null).ConfigureAwait(false);
                        // DELIBERATE: do NOT auto-resolve. This is a real human decision — the
                        // Approvals page surfaces approve/rework/reject buttons.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "FlowMonitor: fix-rec notification emit failed (non-fatal)");
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "FlowMonitor: FixRecommendation {RecId} below confidence threshold ({Conf:0.00} < {Threshold:0.00}) — persisted without notification",
                        insertedId, draft.Confidence, threshold);
                }
            }
            catch (OperationCanceledException) { /* runner is shutting down */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FlowMonitor: TryGenerateFixRecommendationAsync failed for finding {Id}", finding.Id);
            }
        }, ct);
    }

    /// <summary>
    /// Converts an internal action type slug into plain English for notifications.
    /// </summary>
    private static string HumanizeActionType(string actionType) => actionType switch
    {
        "kick-agent-poll" => "Sent a wake-up nudge",
        "post-explicit-ask" => "Posted a question",
        "escalate-to-human" => "Flagged for human review",
        "auto-approve-gate" => "Auto-approved a gate",
        "auto-approve-decision" => "Auto-approved a decision",
        "nudge-reviewer" => "Sent a review reminder",
        "merge-approved-pr" => "Merged an approved PR",
        _ => actionType.Replace("-", " "),
    };
}
