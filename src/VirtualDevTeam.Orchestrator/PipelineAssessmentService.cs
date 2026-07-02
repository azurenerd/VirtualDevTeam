using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.HealthMonitor;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Proactive AI assessment loop — periodically gathers a full pipeline snapshot, sends
/// it to an LLM, and creates advisory FlowFindings for detected anomalies.
///
/// Key design principles (see docs/plans/StrategyStuckEscalationPlan.md):
/// - PROACTIVE POLLER, not event-triggered — simulates a human checking the dashboard
/// - Advisory only — findings capped at Warning severity (lesson #21)
/// - No auto-action — findings surface to the operator, never mutate state
/// - Hot-reloadable prompt via prompts/flow-monitor/pipeline-assessment.md
/// - Budget-aware context — tiered timeline span inclusion with hard cap
/// </summary>
public sealed class PipelineAssessmentService : BackgroundService
{
    private const string DetectorId = "pipeline-assessment";
    private const string WatchdogAgentId = "pipeline-watchdog";
    private const string PromptTemplatePath = "flow-monitor/pipeline-assessment";

    private readonly PipelineStatusSnapshotService _snapshotService;
    private readonly PipelineAssessmentStore _store;
    private readonly PipelineAssessmentResultParser _parser;
    private readonly AssessmentGrounder _grounder;
    private readonly IChatCompletionRunner _chatRunner;
    private readonly FlowMonitorPersistence _flowPersistence;
    private readonly FlowMonitorEventBus? _eventBus;
    private readonly Core.Prompts.IPromptTemplateService? _promptService;
    private readonly IOptionsMonitor<FlowMonitorConfig> _config;
    private readonly ILogger<PipelineAssessmentService> _logger;

    // Adaptive cadence state
    private int _consecutiveHealthy;
    private int _consecutiveUnhealthy;
    private int _assessmentsToday;
    private DateTimeOffset _dayStart = DateTimeOffset.UtcNow.Date;
    private DateTimeOffset? _lastPhaseChange;
    private string? _lastKnownPhase;

    // On-demand trigger
    private readonly SemaphoreSlim _runNowSignal = new(0, 1);
    private string? _runNowFocusQuery;

    public PipelineAssessmentService(
        PipelineStatusSnapshotService snapshotService,
        PipelineAssessmentStore store,
        PipelineAssessmentResultParser parser,
        AssessmentGrounder grounder,
        IChatCompletionRunner chatRunner,
        FlowMonitorPersistence flowPersistence,
        IOptionsMonitor<FlowMonitorConfig> config,
        ILogger<PipelineAssessmentService> logger,
        FlowMonitorEventBus? eventBus = null,
        Core.Prompts.IPromptTemplateService? promptService = null)
    {
        _snapshotService = snapshotService;
        _store = store;
        _parser = parser;
        _grounder = grounder;
        _chatRunner = chatRunner;
        _flowPersistence = flowPersistence;
        _eventBus = eventBus;
        _promptService = promptService;
        _config = config;
        _logger = logger;
    }

    /// <summary>Trigger an immediate assessment, optionally with a focus query.</summary>
    public void RunNowAsync(string? focusQuery = null)
    {
        _runNowFocusQuery = focusQuery;
        // Release the semaphore if it hasn't been released already
        if (_runNowSignal.CurrentCount == 0)
        {
            try { _runNowSignal.Release(); }
            catch (SemaphoreFullException) { /* already signaled */ }
        }
    }

    /// <summary>Latest completed assessment (for UI binding).</summary>
    public PipelineAssessment? LatestAssessment => _store.GetLatestAssessment();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue.Assessment;
        _logger.LogInformation(
            "PipelineAssessmentService starting — enabled={Enabled}, interval={Interval}s, tier={Tier}",
            cfg.Enabled, cfg.IntervalSeconds, cfg.ModelTier);

        // Startup delay — let other services initialize
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            cfg = _config.CurrentValue.Assessment;

            if (!cfg.Enabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunAssessmentAsync(cfg, focusQuery: null, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipelineAssessmentService tick failed (non-fatal)");
                _eventBus?.Error(DetectorId, $"Assessment failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Wait for interval or run-now signal
            var interval = ComputeInterval(cfg);
            try
            {
                // Wait for either the interval to elapse or a RunNow signal
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = Task.Delay(interval, cts.Token);
                var signalTask = _runNowSignal.WaitAsync(cts.Token);
                var completed = await Task.WhenAny(delayTask, signalTask);

                if (completed == signalTask)
                {
                    // RunNow was triggered — run immediately with optional focus
                    var focus = _runNowFocusQuery;
                    _runNowFocusQuery = null;
                    try
                    {
                        await RunAssessmentAsync(cfg, focus, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RunNow assessment failed");
                    }
                }

                // Cancel whichever task didn't complete
                await cts.CancelAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        _logger.LogInformation("PipelineAssessmentService stopping.");
    }

    private async Task RunAssessmentAsync(AssessmentConfig cfg, string? focusQuery, CancellationToken ct)
    {
        // Daily cap check
        ResetDailyCounterIfNeeded();
        if (_assessmentsToday >= cfg.MaxAssessmentsPerDay)
        {
            _logger.LogDebug("PipelineAssessment: daily cap ({Cap}) reached — skipping", cfg.MaxAssessmentsPerDay);
            return;
        }

        var kind = focusQuery is not null ? "on_demand" : "periodic";

        // Gather snapshot
        var snapshot = await _snapshotService.GetSnapshotAsync(ct);

        // Phase gate: skip before ParallelDevelopment (no engineering work to monitor)
        if (!IsMonitorablePhase(snapshot.CurrentPhase))
        {
            _logger.LogDebug("PipelineAssessment: phase '{Phase}' — skipping (pre-engineering)", snapshot.CurrentPhase);
            return;
        }

        // Phase-transition grace period
        if (_lastKnownPhase != snapshot.CurrentPhase)
        {
            _lastPhaseChange = DateTimeOffset.UtcNow;
            _lastKnownPhase = snapshot.CurrentPhase;
        }
        if (_lastPhaseChange.HasValue &&
            (DateTimeOffset.UtcNow - _lastPhaseChange.Value).TotalSeconds < cfg.PhaseTransitionGraceSeconds)
        {
            _logger.LogDebug("PipelineAssessment: phase transition grace period — skipping");
            return;
        }

        // Build context string (budget-managed)
        var contextString = snapshot.ToContextString(cfg.ContextBudgetChars);

        // Load prompt
        var systemPrompt = await LoadPromptAsync(ct);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            _logger.LogWarning("PipelineAssessment: prompt template missing or empty — skipping");
            return;
        }

        // Include last 3 assessment summaries for trend detection
        var recentAssessments = _store.GetRecentAssessments(3);
        var trendContext = BuildTrendContext(recentAssessments);

        var userPrompt = BuildUserPrompt(contextString, trendContext, focusQuery);

        // Call LLM with timeout
        string rawResponse;
        using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        llmCts.CancelAfter(TimeSpan.FromSeconds(cfg.LlmTimeoutSeconds));

        try
        {
            rawResponse = await _chatRunner.InvokeAsync(
                systemPrompt, userPrompt, cfg.ModelTier, WatchdogAgentId, llmCts.Token);
        }
        catch (OperationCanceledException) when (llmCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("PipelineAssessment: LLM call timed out after {Timeout}s", cfg.LlmTimeoutSeconds);
            return;
        }

        // Parse
        var parseResult = _parser.Parse(rawResponse);
        if (!parseResult.IsSuccess)
        {
            _logger.LogWarning("PipelineAssessment: failed to parse LLM response — {Error}", parseResult.Error);
            // Still persist for transparency
            _store.InsertAssessment(new PipelineAssessment
            {
                Id = Guid.NewGuid().ToString("N"),
                AssessedAt = DateTimeOffset.UtcNow,
                Kind = kind,
                HealthScore = -1,
                Status = "inconclusive",
                Summary = $"Parse failed: {parseResult.Error}",
                RawResponse = rawResponse,
                ContextJson = contextString,
                ParseStatus = parseResult.Status,
            });
            _assessmentsToday++;
            return;
        }

        var result = parseResult.Value!;

        // Ground issues against snapshot
        var groundingResult = _grounder.Ground(result.Issues ?? Array.Empty<AssessmentIssue>(), snapshot);

        // Compute delta from previous assessment
        var previous = _store.GetLatestAssessment();
        var delta = ComputeDelta(previous, result, groundingResult);

        // Derive status from health score
        var status = result.HealthScore >= 7 ? "healthy"
            : result.HealthScore >= 4 ? "warning" : "critical";

        // Persist
        var assessment = new PipelineAssessment
        {
            Id = Guid.NewGuid().ToString("N"),
            AssessedAt = DateTimeOffset.UtcNow,
            Kind = kind,
            HealthScore = result.HealthScore,
            Status = result.Status ?? status,
            Summary = result.Summary ?? "",
            IssuesJson = JsonSerializer.Serialize(groundingResult.Issues),
            RecommendationsJson = result.Recommendations is { Length: > 0 }
                ? JsonSerializer.Serialize(result.Recommendations) : null,
            ForwardLook = result.ForwardLook,
            GroundingPassRate = groundingResult.PassRate,
            RawResponse = rawResponse,
            ContextJson = contextString,
            ModelTier = cfg.ModelTier,
            ParseStatus = parseResult.Status,
            DeltaJson = delta is not null ? JsonSerializer.Serialize(delta) : null,
        };
        _store.InsertAssessment(assessment);
        _assessmentsToday++;

        _logger.LogInformation(
            "PipelineAssessment: score={Score}/10, issues={Issues} ({Grounded} grounded), delta={New}↑ {Resolved}↓",
            result.HealthScore, groundingResult.Issues.Length,
            groundingResult.Issues.Count(i => i.GroundingPassed == true),
            delta?.NewIssues ?? 0, delta?.ResolvedIssues ?? 0);

        // Publish to event bus
        _eventBus?.Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Lifecycle,
            Source = DetectorId,
            Message = $"Assessment complete: {result.HealthScore}/10 — {groundingResult.Issues.Length} issue(s)",
            Detail = assessment.Summary,
        });

        // Create FlowFindings for high-confidence grounded issues (Warning-capped per lesson #21)
        if (cfg.CreateFindingsOnIssues)
        {
            CreateFlowFindings(groundingResult.Issues, cfg, snapshot);
        }

        // Update adaptive cadence state
        UpdateCadenceState(result.HealthScore);
    }

    private async Task<string?> LoadPromptAsync(CancellationToken ct)
    {
        if (_promptService is not null)
        {
            var rendered = await _promptService.RenderAsync(
                PromptTemplatePath,
                new Dictionary<string, string>(),
                ct);
            if (!string.IsNullOrWhiteSpace(rendered)) return rendered;
        }

        // Hardcoded fallback if prompt service or template is unavailable
        return """
            You are a pipeline health monitor. Analyze the pipeline status data below and provide a JSON assessment.
            Rate overall health 1-10, identify any anomalies or stuck processes, and provide recommendations.
            Output must be valid JSON with: healthScore (int 1-10), summary (string), issues (array), trajectoryPrediction (string).
            Each issue: { dedupKey, category, targetType, targetId, description, severity (info/warning), confidence (0.0-1.0), recommendation, evidence }
            """;
    }

    private static string BuildUserPrompt(string context, string trend, string? focusQuery)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== PIPELINE STATUS DATA ===");
        sb.AppendLine(context);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(trend))
        {
            sb.AppendLine("=== RECENT ASSESSMENT TREND ===");
            sb.AppendLine(trend);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(focusQuery))
        {
            sb.AppendLine("=== OPERATOR FOCUS QUERY ===");
            sb.AppendLine(focusQuery);
            sb.AppendLine();
        }

        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine("Analyze the pipeline status above and provide your assessment as JSON.");
        return sb.ToString();
    }

    private static string BuildTrendContext(IReadOnlyList<PipelineAssessment> recent)
    {
        if (recent.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        foreach (var a in recent)
        {
            sb.AppendLine($"- [{a.AssessedAt:HH:mm:ss}] Score: {a.HealthScore}/10 — {a.Summary}");
        }
        return sb.ToString();
    }

    private void CreateFlowFindings(AssessmentIssue[] issues, AssessmentConfig cfg, PipelineStatusSnapshot snapshot)
    {
        foreach (var issue in issues.Where(i => i.GroundingPassed == true && i.Confidence >= cfg.ConfidenceThreshold))
        {
            // Warning-capped severity (lesson #21 — no LLM in FlowMonitor control flow)
            var severity = issue.Severity?.ToLowerInvariant() switch
            {
                "critical" => FlowFindingSeverity.Warning, // cap to Warning
                "warning" => FlowFindingSeverity.Warning,
                _ => FlowFindingSeverity.Info,
            };

            var evidenceStr = issue.Evidence is { Length: > 0 }
                ? string.Join("; ", issue.Evidence)
                : "No evidence cited";

            var finding = new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = DateTimeOffset.UtcNow,
                DetectorId = DetectorId,
                Severity = severity,
                TargetAgentId = issue.TargetType?.Equals("agent", StringComparison.OrdinalIgnoreCase) == true
                    ? issue.TargetId : null,
                TargetResource = issue.TargetId,
                Summary = issue.Description ?? "AI-detected anomaly",
                Rationale = $"[AI Assessment] {evidenceStr}\nRecommendation: {issue.RecommendedAction ?? "N/A"}",
                DedupKey = $"pipeline-assessment:{issue.DedupKey ?? issue.TargetId ?? "unknown"}",
            };

            var dedupWindow = TimeSpan.FromMinutes(15);
            var inserted = _flowPersistence.InsertFinding(finding, dedupWindow);
            if (inserted)
            {
                _eventBus?.Finding(finding, $"AI assessment: {issue.Description}");
                _logger.LogInformation(
                    "PipelineAssessment: created finding for {Target} — {Description}",
                    issue.TargetId, issue.Description);
            }
        }
    }

    private TimeSpan ComputeInterval(AssessmentConfig cfg)
    {
        // If daily cap is approaching, slow down
        if (_assessmentsToday >= cfg.MaxAssessmentsPerDay * 0.9)
            return TimeSpan.FromMinutes(30);

        var baseInterval = cfg.IntervalSeconds;
        var latest = _store.GetLatestAssessment();
        if (latest is null) return TimeSpan.FromSeconds(baseInterval);

        // Adaptive cadence: healthy = full interval, warning = half, critical = quarter
        double multiplier;
        if (latest.HealthScore >= 7)
        {
            multiplier = 1.0;
        }
        else if (latest.HealthScore >= 4)
        {
            // Require 2 consecutive unhealthy before speeding up
            multiplier = _consecutiveUnhealthy >= 2 ? 0.5 : 1.0;
        }
        else
        {
            multiplier = _consecutiveUnhealthy >= 2 ? 0.25 : 0.5;
        }

        var seconds = Math.Max(cfg.MinIntervalSeconds, (int)(baseInterval * multiplier));
        seconds = Math.Min(seconds, cfg.MaxIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private void UpdateCadenceState(int healthScore)
    {
        if (healthScore >= 7)
        {
            _consecutiveHealthy++;
            _consecutiveUnhealthy = 0;
        }
        else
        {
            _consecutiveUnhealthy++;
            _consecutiveHealthy = 0;
        }
    }

    private void ResetDailyCounterIfNeeded()
    {
        var today = DateTimeOffset.UtcNow.Date;
        if (today > _dayStart)
        {
            _dayStart = today;
            _assessmentsToday = 0;
        }
    }

    private static bool IsMonitorablePhase(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return false;
        return phase.Contains("Development", StringComparison.OrdinalIgnoreCase) ||
               phase.Contains("Testing", StringComparison.OrdinalIgnoreCase) ||
               phase.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
               phase.Contains("Completion", StringComparison.OrdinalIgnoreCase);
    }

    private static AssessmentDelta? ComputeDelta(PipelineAssessment? previous, AssessmentResult current, GroundingResult grounding)
    {
        if (previous is null) return null;

        var prevIssues = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(previous.IssuesJson))
        {
            try
            {
                var prevParsed = JsonSerializer.Deserialize<AssessmentIssue[]>(previous.IssuesJson);
                if (prevParsed is not null)
                {
                    foreach (var i in prevParsed.Where(i => !string.IsNullOrWhiteSpace(i.DedupKey)))
                        prevIssues.Add(i.DedupKey!);
                }
            }
            catch { /* ignore deserialization failures */ }
        }

        var currentKeys = grounding.Issues
            .Where(i => !string.IsNullOrWhiteSpace(i.DedupKey))
            .Select(i => i.DedupKey!)
            .ToHashSet();

        return new AssessmentDelta
        {
            NewIssues = currentKeys.Except(prevIssues).Count(),
            ResolvedIssues = prevIssues.Except(currentKeys).Count(),
            PersistingIssues = currentKeys.Intersect(prevIssues).Count(),
            ScoreChange = current.HealthScore - previous.HealthScore,
        };
    }
}

/// <summary>Delta between consecutive assessments for trend detection.</summary>
public sealed record AssessmentDelta
{
    public int NewIssues { get; init; }
    public int ResolvedIssues { get; init; }
    public int PersistingIssues { get; init; }
    public int ScoreChange { get; init; }
}
