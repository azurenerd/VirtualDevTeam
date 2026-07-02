// NoMessyCodePlan Theme 4d: Program.cs split — FlowMonitor (always-on watchdog) registration.
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// FlowMonitor: always-on background watchdog that detects stuck flow and applies safe
/// in-process corrective actions (no recompile, no restart, no force-merge). See
/// docs/MonitoringLoops.md for the patterns. All detectors are pure logic — escalation
/// rungs are picked by attempt count, not by an LLM (lesson #87). AI participation is
/// confined to the FixRecommendation flow (T1.5) gated behind operator approval.
/// </summary>
public static class RunnerHealthMonitorExtensions
{
    public static IServiceCollection AddRunnerHealthMonitor(this IServiceCollection services, IConfiguration configuration)
    {
        // Config bindings.
        services.Configure<FlowMonitorConfig>(
            configuration.GetSection("VirtualDevTeam:FlowMonitor"));
        // healthmon-false-research-complete: kill-switch + cooldown for the orchestrator
        // HealthMonitor's signal auto-detection heuristic.
        services.Configure<HealthMonitorConfig>(
            configuration.GetSection("VirtualDevTeam:HealthMonitor"));
        // NoMessyCodePlan Theme 8: per-environment poll cadence for the gate notification service.
        services.Configure<GateNotificationConfig>(
            configuration.GetSection("VirtualDevTeam:GateNotification"));

        // Persistence (SQLite — table set distinct from AgentStateStore).
        services.AddSingleton<FlowMonitorPersistence>();
        services.AddSingleton<IFixRecommendationStore>(sp => sp.GetRequiredService<FlowMonitorPersistence>());
        services.AddSingleton<IDiagnosticActionExecutor, DiagnosticActionExecutor>();

        // Proposed-action persistence — stores operator-gated FlowAction proposals generated
        // by the escalation ladder (Rung 3+) when automatic actions aren't safe to apply.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.Actions.FlowActionProposalPersistence>();
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionProposalStore>(
            sp => sp.GetRequiredService<VirtualDevTeam.Core.HealthMonitor.Actions.FlowActionProposalPersistence>());

        // T1.5: planner that produces /plan + rubber-duck FixRecommendations for Critical
        // findings without a vetted action handler. Optional dependency on IChatCompletionRunner —
        // if AI is unavailable, the service still works and the planner just stays idle.
        services.AddSingleton<FixRecommendationPlannerService>();

        // T1.6: Code-fix-without-restart pipeline.
        // LiveFixApplicator handles the Live + DeferredRestart tiers via Copilot CLI agentic mode
        // with --allow-all and a constrained scope prompt. StagedFixApplicator runs at runner
        // startup to apply Blocked-tier fixes (NuGet, .csproj, schema) that can't be applied
        // while the runner is up. HOSTED SERVICE ORDERING: StagedFixApplicator MUST run before
        // FlowMonitorService so the workspace is in a known-good state when normal monitoring
        // resumes. IHostedService starts in registration order — keep StagedFixApplicator before
        // FlowMonitorService below.
        services.AddSingleton<LiveFixApplicator>();
        services.AddSingleton<IFixRecommendationApplicator>(sp => sp.GetRequiredService<LiveFixApplicator>());
        services.AddHostedService<StagedFixApplicator>();

        // ─── Detectors (each registered as IFlowDetector so FlowMonitorService receives them via IEnumerable<IFlowDetector>) ───

        // Tier-1: rule-based, in-process state checks.
        services.AddSingleton<IFlowDetector>(sp =>
        {
            // post-run-stuck-threshold: read threshold from FlowMonitorConfig (default 45m).
            // Captured at construction — IOptionsMonitor.OnChange does NOT live-update the detector's
            // _threshold field. Restart required for changes to take effect.
            var cfg = sp.GetRequiredService<IOptionsMonitor<FlowMonitorConfig>>().CurrentValue;
            var stuckMinutes = Math.Max(1, cfg.StuckThresholdMinutes);
            return new AgentStuckDetector(
                TimeSpan.FromMinutes(stuckMinutes),
                sp.GetRequiredService<ILogger<AgentStuckDetector>>(),
                sp.GetService<AgentCliLogService>(),
                sp.GetService<ActiveLlmCallTracker>());
        });
        services.AddSingleton<IFlowDetector, PhaseCompletionMismatchDetector>();
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.DeadlockFlowDetector>();
        // image-regen-anomaly (imggen Phase 5): catches image reworks that produced no visible
        // change. Compares the perceptual hash of a PNG at the latest commit on a PR against the
        // previous commit's pHash; equal pHashes → the regen was a no-op.
        services.AddSingleton<IFlowDetector>(sp =>
            new VirtualDevTeam.Orchestrator.ImageRegenAnomalyDetector(
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.ImageRegenAnomalyDetector>>(),
                sp.GetService<VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService>(),
                sp.GetService<VirtualDevTeam.Core.DevPlatform.Capabilities.IRepositoryContentService>()));
        // doc-rework-size-anomaly: catches doc reworks where a "surgical edit" balloons into a full
        // rewrite (ratio>2x + |Δ|>2000 chars → Critical; ratio>1.3x + |Δ|>500 chars → Warning).
        services.AddSingleton<IFlowDetector>(sp =>
            new VirtualDevTeam.Orchestrator.DocReworkSizeAnomalyDetector(
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.DocReworkSizeAnomalyDetector>>(),
                sp.GetService<VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService>(),
                sp.GetService<VirtualDevTeam.Core.DevPlatform.Capabilities.IRepositoryContentService>()));
        // post-run-pr-merge-conflict-detector: surfaces stale CONFLICTING PRs (>15m) as Critical
        // → auto-triggers the T1.5 FixRecommendation flow.
        services.AddSingleton<IFlowDetector, StalePullRequestConflictDetector>();
        // agent-disappearance: fires when agents vanish during an active run (0 agents
        // or below minimum threshold). Catches ResetCaches bugs and silent agent crashes.
        services.AddSingleton<IFlowDetector, AgentDisappearanceDetector>();
        // post-run3-merge-bottleneck: PRs with both architect-approved AND pm-approved labels
        // that haven't been merged. Threshold from FlowMonitorConfig.MergeApprovedPrStuckMinutes.
        services.AddSingleton<IFlowDetector>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptionsMonitor<FlowMonitorConfig>>().CurrentValue;
            var stuckMinutes = Math.Max(1, cfg.MergeApprovedPrStuckMinutes);
            var escalationMinutes = Math.Max(1, cfg.PrMergeEscalationMinutes);
            return new UnmergedApprovedPrDetector(
                sp.GetRequiredService<ILogger<UnmergedApprovedPrDetector>>(),
                TimeSpan.FromMinutes(stuckMinutes),
                TimeSpan.FromMinutes(escalationMinutes));
        });
        // Operator-requested: nudge the specific reviewer agent when a PR has been waiting
        // for their approval label beyond 10 min. Pairs with NudgeReviewerAction below.
        services.AddSingleton<IFlowDetector, MissingReviewerDetector>();

        // Tier-2: deeper flow-state checks (2026-05-11 batch). All use the per-tick lazy/cached
        // IPlatformView so they share API calls. Thresholds are conservative (5-30min) to avoid noise.
        services.AddSingleton<IFlowDetector>(sp =>
            new IdleAgentPhaseStuckDetector(
                sp.GetRequiredService<ILogger<IdleAgentPhaseStuckDetector>>(),
                logService: sp.GetService<AgentCliLogService>(),
                llmTracker: sp.GetService<ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowDetector>(sp =>
            new TestEngineerFalseCompletionDetector(
                sp.GetRequiredService<ILogger<TestEngineerFalseCompletionDetector>>(),
                config: sp.GetService<IOptionsMonitor<VirtualDevTeamConfig>>(),
                logService: sp.GetService<AgentCliLogService>(),
                llmTracker: sp.GetService<ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowDetector, LabelTransitionTimeoutDetector>();
        services.AddSingleton<IFlowDetector, ReworkSaturationDetector>();
        services.AddSingleton<IFlowDetector, HandoffGapDetector>();
        services.AddSingleton<IFlowDetector, PhaseAdvancementWatchdog>();
        services.AddSingleton<IFlowDetector>(sp =>
            new StatusReasonStagnationDetector(
                sp.GetRequiredService<ILogger<StatusReasonStagnationDetector>>(),
                logService: sp.GetService<AgentCliLogService>(),
                llmTracker: sp.GetService<ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowDetector, OrphanPrDetector>();
        services.AddSingleton<IFlowDetector, IdleIdleCycleDetector>();
        services.AddSingleton<IFlowDetector>(sp =>
            new EmptyQueueDetector(
                sp.GetRequiredService<ILogger<EmptyQueueDetector>>(),
                logService: sp.GetService<AgentCliLogService>(),
                llmTracker: sp.GetService<ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowDetector>(sp =>
            new PipelineStallDetector(
                sp.GetRequiredService<ILogger<PipelineStallDetector>>(),
                logService: sp.GetService<AgentCliLogService>(),
                llmTracker: sp.GetService<ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowDetector, OfflineAgentAssignedWorkDetector>();
        // imggen-spec-mismatch-detector: parses [image-deliverables] manifest in PMSpec.md
        // (and Architecture.md) and verifies each declared path exists on the working branch
        // with >5KB size. Missing → Critical, trivial size → Warning.
        services.AddSingleton<IFlowDetector, ImageSpecMismatchDetector>();

        // 2026-05-12 batch — three detectors that close visibility gaps from the sprite-loss
        // incident. All three are best-effort log/file scanners with bounded cost.
        // - framework-log-watchdog: catches "No Azure OpenAI credentials" / framework-decline /
        //   worktree-cleanup-partial-failure log lines that previously surfaced only when a
        //   developer manually tailed the runner log
        // - framework-cleanup-race: confirms the cleanup-race fix's auto-commit Layer 1 worked
        //   on every candidate; raises Critical if the FATAL log fires
        // - write-location-mismatch: agent claimed a file write succeeded (status text contains
        //   "✓") but the file is missing or written to an unexpected directory
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.FrameworkLogWatchdogDetector>();
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.FrameworkCleanupRaceDetector>();
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.WriteLocationMismatchDetector>();
        // 2026-05-12: catch agents stuck on externally-merged PRs (live evidence: 3 agents
        // wasting LLM/API on already-merged PRs #1511 + #1508 after operator used gh pr merge).
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.ExternalMergeDesyncDetector>();
        // 2026-05-??: belt-and-suspenders for the issue-reopen root-cause fix in b22011c.
        // Catches any reopen-via-side-effect paths not covered by ResetToPendingAsync guard.
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.ReopenedClosedIssueDetector>();
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.AgentClaimingDuplicateTaskDetector>();
        // Auto-close duplicate PRs when two agents claim the same task (rung 2).
        services.AddSingleton<IFlowAction, VirtualDevTeam.Core.HealthMonitor.Actions.CloseDuplicatePrAction>();
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.ScaffoldingRebuildDetector>();

        // Stuck strategy candidate detector: catches CLI sessions that launched but produce
        // no stdout for 10+ minutes. Uses CandidateStateStore.GetStuckCandidates() + LastActivityAt.
        // Paired with CancelStrategyCandidateAction which cancels via IOrchestrationCancellationService.
        services.AddSingleton<IFlowDetector>(sp =>
            new VirtualDevTeam.Orchestrator.StuckStrategyCandidateDetector(
                sp.GetRequiredService<VirtualDevTeam.Core.Strategies.CandidateStateStore>(),
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.StuckStrategyCandidateDetector>>()));
        services.AddSingleton<IFlowAction, VirtualDevTeam.Orchestrator.CancelStrategyCandidateAction>();

        // Strategy evaluation stuck detector: catches higher-level evaluation-phase stuck states
        // (scoring stuck, media capture stuck, candidate stuck) vs the process-level stuck above.
        // Paired with PromoteStrategyWinnerAction which cancels the task to trigger emergency winner.
        services.AddSingleton<IFlowDetector>(sp =>
            new VirtualDevTeam.Orchestrator.StrategyEvaluationStuckDetector(
                sp.GetRequiredService<VirtualDevTeam.Core.Strategies.CandidateStateStore>(),
                sp.GetRequiredService<IOptionsMonitor<VirtualDevTeam.Core.Configuration.StrategyFrameworkConfig>>(),
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.StrategyEvaluationStuckDetector>>(),
                sp.GetService<VirtualDevTeam.Core.AI.AgentCliLogService>(),
                sp.GetService<VirtualDevTeam.Core.AI.ActiveLlmCallTracker>()));
        services.AddSingleton<IFlowAction>(sp =>
            new VirtualDevTeam.Orchestrator.PromoteStrategyWinnerAction(
                sp.GetRequiredService<VirtualDevTeam.Core.Strategies.IOrchestrationCancellationService>(),
                sp.GetRequiredService<VirtualDevTeam.Core.Strategies.CandidateStateStore>(),
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.PromoteStrategyWinnerAction>>(),
                sp.GetService<VirtualDevTeam.Core.Notifications.GateNotificationService>()));

        // Stale rate-limit detector: catches RateLimitManager stuck in paused state
        // with remaining quota. Auto-clears via ClearStaleRateLimitAction (rung 1).
        services.AddSingleton<IFlowDetector, StaleRateLimitDetector>();
        services.AddSingleton<IFlowAction, VirtualDevTeam.Core.HealthMonitor.Actions.ClearStaleRateLimitAction>();

        // Playwright not-ready detector: fires when Playwright browsers are missing/mismatched
        // during ParallelDevelopment+. All candidate previews silently fail without this alert.
        services.AddSingleton<IFlowDetector, PlaywrightNotReadyDetector>();

        // Squad not-ready detector: fires when Squad is enabled in strategy config but
        // the CLI isn't installed. Every Squad candidate immediately fails the gate.
        services.AddSingleton<IFlowDetector, SquadNotReadyDetector>();

        // Push failure detector: catches repeated git push/rebase failures in worktree workspaces.
        // The PushFailureTracker singleton is written to by WorktreeWorkspace.PushAsync.
        services.AddSingleton<PushFailureTracker>();
        services.AddSingleton<IFlowDetector>(sp =>
            new PushFailureDetector(
                sp.GetRequiredService<PushFailureTracker>(),
                sp.GetRequiredService<ILogger<PushFailureDetector>>()));

        // Diagnostic enrichers: explain WHY an agent is stuck (not just that it IS stuck).
        // Enrichers run after detection, before action selection. They add checklist diagnostics
        // and recommended fix actions to findings.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.Diagnostics.IFlowDiagnosticEnricher,
            VirtualDevTeam.Core.HealthMonitor.Diagnostics.PrLifecycleDiagnosticEnricher>();

        // ─── MissingWorkRecommendation subsystem(azure-core/VirtualDevTeam#6) ───
        // Deterministic detectors that find work-to-be-done implicit in code but absent from
        // the issue tracker. Phase 1 MVP — 3 detectors registered, persistence + planner +
        // Approvals UI in follow-up commits.
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.MissingWorkPersistence>();
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkDetector,
            VirtualDevTeam.Orchestrator.PhantomTaskReferenceDetector>();
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkDetector,
            VirtualDevTeam.Orchestrator.UnwiredAssetDetector>();
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkDetector,
            VirtualDevTeam.Orchestrator.NullStubFutureCommentDetector>();
        // FinalQualityImprovement Phase 2 C1 — PR-blocking stub-function-body detection.
        // Catches Cat-A (stub-comment-only body), Cat-D (empty body), Cat-E (_param + empty)
        // across TS/JS/C#/Python/Go. Honors STUB_OK: <reason> annotations as escape hatch.
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkDetector,
            VirtualDevTeam.Orchestrator.StubFunctionBodyDetector>();
        // FinalQualityImprovement Phase 2 B2 — Architecture.md Event Catalog cross-check.
        // Compares the declared `## Event Catalog` table against codebase .emit()/.on()
        // patterns; raises Critical for undeclared emitters, Important for missing subs.
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkDetector,
            VirtualDevTeam.Orchestrator.EventCatalogValidator>();
        services.AddHostedService<VirtualDevTeam.Core.MissingWork.MissingWorkDetectorRunner>();
        // Phase 1.7+1.8: planner that converts findings above the confidence threshold into
        // proposed issues via Copilot CLI (JSON-output mode). Persisted to proposed_issues
        // table for operator approval on the Approvals page (Phase 1.9 follow-up).
        services.AddSingleton<VirtualDevTeam.Core.MissingWork.IMissingWorkPlanner,
            VirtualDevTeam.Core.MissingWork.MissingWorkPlanner>();
        // T2.21 AI Anomaly — reads confidence threshold from FlowMonitorConfig. REGISTERED LAST
        // so it runs after all rule-based detectors in the foreach loop, letting it observe
        // whether other findings already exist for this tick before paying the AI cost.
        services.AddSingleton<IFlowDetector>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptionsMonitor<FlowMonitorConfig>>().CurrentValue;
            return new AiAnomalyDetector(
                sp.GetRequiredService<ILogger<AiAnomalyDetector>>(),
                sp.GetService<VirtualDevTeam.Core.AI.IChatCompletionRunner>(),
                sp.GetService<FlowMonitorPersistence>(),
                confidenceThreshold: cfg.ConfidenceThreshold);
        });

        // Executor that dispatches operator-approved ProposedFlowActions to the right
        // concrete handler (AddPrLabel, RemovePrLabel, PostPrComment, NudgeAgent).
        // Platform services (IPullRequestService, IReviewService) are optional — the
        // executor degrades gracefully when they are not yet registered (pre-project state).
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionExecutor,
            VirtualDevTeam.Core.HealthMonitor.Actions.SimpleFlowActionExecutor>();

        // ─── Actions (escalation ladder: rung 1 → rung 2 → rung 3) ───
        services.AddSingleton<IFlowAction, KickAgentPollAction>();
        // T1.2 rung-2 — post explicit comment on the target's open PR (preferred) or issue.
        // Optional deps on IPullRequestService/IWorkItemService/IReviewService — degrades to
        // FlowActionResult.Skipped pre-project-open rather than throwing.
        services.AddSingleton<IFlowAction, PostExplicitAskAction>();
        // T1.2 rung-3 — apply `agent-stuck` label + emit a non-auto-resolving human notification
        // (surfaces on the Approvals page).
        services.AddSingleton<IFlowAction, EscalateToHumanAction>();
        // post-run3-merge-bottleneck: safety-net merger for UnmergedApprovedPrDetector above.
        // Re-checks all approval labels + mergeability at execution time before calling MergeAsync.
        services.AddSingleton<IFlowAction, MergeApprovedPrAction>();
        // Merge escalation: notification-only escalation for partially-approved PRs (Tier 2
        // of UnmergedApprovedPrDetector). Does NOT auto-merge — surfaces on Approvals page.
        services.AddSingleton<IFlowAction>(sp =>
            new VirtualDevTeam.Orchestrator.MergeEscalationAction(
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.MergeEscalationAction>>(),
                sp.GetService<VirtualDevTeam.Core.Notifications.GateNotificationService>(),
                sp.GetService<IOptionsMonitor<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>(),
                sp.GetService<IOptionsMonitor<VirtualDevTeam.Core.HealthMonitor.FlowMonitorConfig>>()));
        // Operator-requested reviewer nudge: publishes ReviewNudgeMessage directly to the
        // reviewer agent's bus subscription when a PR is overdue for their approval label.
        // Idempotency: 5-min per-(PR, role) cooldown in NudgeReviewerAction prevents spam.
        services.AddSingleton<IFlowAction, VirtualDevTeam.Core.HealthMonitor.Actions.NudgeReviewerAction>();
        // pr-approval-stuck: Critical finding when a PR has been waiting for a specific
        // review label (architect-approved | tests-added | pm-approved) for >ReviewAutoApprovalMinutes.
        // Complements MissingReviewerDetector (Warning at 10 min) — fires at Critical so
        // AutoApproveReviewAction can add the missing label to unblock the pipeline.
        services.AddSingleton<IFlowDetector>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptionsMonitor<FlowMonitorConfig>>().CurrentValue;
            var thresholdMinutes = Math.Max(1, cfg.ReviewAutoApprovalMinutes);
            return new VirtualDevTeam.Orchestrator.PrApprovalStuckDetector(
                sp.GetRequiredService<ILogger<VirtualDevTeam.Orchestrator.PrApprovalStuckDetector>>(),
                TimeSpan.FromMinutes(thresholdMinutes));
        });

        // FinalQualityImprovement Layer 0 — raises Critical when sidecar scenarios.json drifts
        // from PMSpec.md `# scenarios` YAML block (subscribed to IScenarioRegistry.Changed).
        services.AddSingleton<IFlowDetector, VirtualDevTeam.Orchestrator.ScenariosDriftDetector>();

        // Gate/decision stuck detector + auto-approval action: detects gates/decisions
        // pending longer than AutoApprovalMinutes threshold and auto-approves them.
        // 0 = disabled. Notifications appear on Approvals page with Dismiss button.
        services.AddSingleton<IFlowDetector>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptionsMonitor<FlowMonitorConfig>>().CurrentValue;
            return new GateStuckDetector(
                sp.GetRequiredService<VirtualDevTeam.Core.Agents.Decisions.IDecisionLog>(),
                sp.GetRequiredService<VirtualDevTeam.Core.Notifications.GateNotificationService>(),
                cfg.AutoApprovalMinutes,
                sp.GetRequiredService<ILogger<GateStuckDetector>>());
        });
        services.AddSingleton<IFlowAction, VirtualDevTeam.Core.HealthMonitor.Actions.AutoApproveGateAction>();
        // Auto-approve stuck PR reviews: adds the missing label (architect-approved, tests-added,
        // pm-approved) to a PR that has been waiting for reviewer action beyond the configured
        // ReviewAutoApprovalMinutes threshold. Short-circuited in PickActionForRung like gate-stuck.
        services.AddSingleton<IFlowAction, VirtualDevTeam.Core.HealthMonitor.Actions.AutoApproveReviewAction>();

        // ─── Pipeline Assessment (proactive AI health loop) ───
        services.AddSingleton<PipelineStatusSnapshotService>();
        services.AddSingleton<PipelineAssessmentStore>();
        services.AddSingleton<PipelineAssessmentResultParser>();
        services.AddSingleton<AssessmentGrounder>();
        services.AddHostedService<VirtualDevTeam.Orchestrator.PipelineAssessmentService>();

        // ─── Service + event stream ───
        services.AddHostedService<VirtualDevTeam.Orchestrator.FlowMonitorService>();
        // T1.4: live event stream — bounded channel + DropOldest so the FlowMonitor never blocks
        // on a slow / disconnected dashboard. Drained by FlowMonitorEventRelay → SignalR fan-out.
        services.AddSingleton<FlowMonitorEventBus>();
        services.AddHostedService<VirtualDevTeam.Dashboard.Services.FlowMonitorEventRelay>();
        services.AddHostedService<VirtualDevTeam.Dashboard.Services.AgentLogRelay>();

        // Rate-limit notification: subscribes to RateLimitManager.OnRateLimitStatusChanged
        // and creates dismissable FlowMonitor notifications so operators see throttle events.
        services.AddHostedService<RateLimitNotificationObserver>();

        return services;
    }
}
