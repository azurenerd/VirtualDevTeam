using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.Pipeline;

/// <summary>
/// Deterministic, label-driven resolver that builds a <see cref="PrMergeFlowSnapshot"/>
/// for a single PR from its current platform state.  No LLM in the control path.
/// </summary>
public sealed class PrMergeFlowResolver : IPrMergeFlowSource
{
    private readonly IPullRequestService _pr;
    private readonly ILogger<PrMergeFlowResolver> _logger;

    public PrMergeFlowResolver(IPullRequestService pr, ILogger<PrMergeFlowResolver> logger)
    {
        _pr = pr;
        _logger = logger;
    }

    public async Task<PrMergeFlowSnapshot?> GetSnapshotAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var pr = await _pr.GetAsync(prNumber, ct);
            if (pr is null) return null;

            var labelSet = new HashSet<string>(pr.Labels, StringComparer.OrdinalIgnoreCase);
            var isMerged = pr.IsMerged;
            var isClosed = !isMerged && pr.State == "closed";
            var now = DateTime.UtcNow;

            var hasReadyForReview      = labelSet.Contains("ready-for-review");
            var hasArchitectApproved   = labelSet.Contains("architect-approved");
            var hasTestsAdded          = labelSet.Contains("tests-added");
            var hasPmApproved          = labelSet.Contains("pm-approved");
            var hasHumanApproved       = labelSet.Contains("human-approved");
            var hasAwaitingHumanReview = labelSet.Contains("awaiting-human-review");

            var steps = new List<PrMergeFlowStep>
            {
                // ─── branch-created ─────────────────────────────────────────────────────
                BuildStep("branch-created", "Branch Created", "🌿",
                    state: PrMergeFlowStepState.Done,
                    startedAt:   pr.CreatedAt,
                    completedAt: pr.CreatedAt),

                // ─── implementation ─────────────────────────────────────────────────────
                BuildStep("implementation", "Implementation", "⚙️",
                    state: isMerged || isClosed || hasReadyForReview
                        ? PrMergeFlowStepState.Done
                        : PrMergeFlowStepState.InProgress,
                    startedAt:   pr.CreatedAt,
                    completedAt: hasReadyForReview || isMerged || isClosed ? pr.UpdatedAt : null),

                // ─── self-assessment ────────────────────────────────────────────────────
                BuildStep("self-assessment", "Self-Assessment", "🔍",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged || hasArchitectApproved ? PrMergeFlowStepState.Done
                         : hasReadyForReview ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending),

                // ─── architect-review ───────────────────────────────────────────────────
                BuildStep("architect-review", "Architect Review", "🏛️",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged || hasArchitectApproved ? PrMergeFlowStepState.Done
                         : hasReadyForReview ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: hasArchitectApproved || isMerged ? pr.UpdatedAt : null),

                // ─── te-inline-tests ────────────────────────────────────────────────────
                BuildStep("te-inline-tests", "Test Engineer", "🧪",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged || hasTestsAdded ? PrMergeFlowStepState.Done
                         : hasArchitectApproved ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: hasTestsAdded || isMerged ? pr.UpdatedAt : null),

                // ─── pm-review ──────────────────────────────────────────────────────────
                BuildStep("pm-review", "PM Review", "📋",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged || hasPmApproved ? PrMergeFlowStepState.Done
                         : hasTestsAdded ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: hasPmApproved || isMerged ? pr.UpdatedAt : null),

                // ─── se-peer-review ─────────────────────────────────────────────────────
                // Not used in the default agent pipeline — kept as NotApplicable unless a
                // future config flag enables it per project.
                BuildStep("se-peer-review", "SE Peer Review", "👁️",
                    state: PrMergeFlowStepState.NotApplicable),

                // ─── security-audit ─────────────────────────────────────────────────────
                BuildStep("security-audit", "Security Audit", "🔒",
                    state: PrMergeFlowStepState.NotApplicable),

                // ─── human-gate ─────────────────────────────────────────────────────────
                BuildStep("human-gate", "Human Gate", "🚦",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged || hasHumanApproved ? PrMergeFlowStepState.Done
                         : hasAwaitingHumanReview || hasPmApproved ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: hasHumanApproved || isMerged ? pr.UpdatedAt : null),

                // ─── mergeable-ci ───────────────────────────────────────────────────────
                BuildStep("mergeable-ci", "CI / Mergeable", "🤖",
                    state: isClosed ? PrMergeFlowStepState.Skipped
                         : isMerged ? PrMergeFlowStepState.Done
                         : hasPmApproved ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: isMerged ? pr.MergedAt : null),

                // ─── merge ──────────────────────────────────────────────────────────────
                BuildStep("merge", "Final Merge", "🚀",
                    state: isMerged ? PrMergeFlowStepState.Done
                         : isClosed ? PrMergeFlowStepState.Failed
                         : hasPmApproved ? PrMergeFlowStepState.InProgress
                         : PrMergeFlowStepState.Pending,
                    completedAt: isMerged ? pr.MergedAt : null,
                    failedAt:    isClosed ? pr.UpdatedAt : null),
            };

            var applicable = steps
                .Where(s => s.State != PrMergeFlowStepState.NotApplicable)
                .ToList();

            var completedCount = applicable.Count(s => s.State == PrMergeFlowStepState.Done);
            var currentStep = applicable.FirstOrDefault(s => s.State == PrMergeFlowStepState.InProgress)
                           ?? applicable.FirstOrDefault(s => s.State == PrMergeFlowStepState.Pending);

            var stuckSeconds = currentStep?.StartedAt is { } start
                ? (int)(now - start).TotalSeconds
                : 0;

            return new PrMergeFlowSnapshot
            {
                PrNumber    = prNumber,
                ComputedAt  = now,
                Steps       = steps,
                Summary     = new PrMergeFlowSummary
                {
                    CurrentStepId        = currentStep?.Id,
                    CompletedCount       = completedCount,
                    TotalApplicableCount = applicable.Count,
                    StuckSeconds         = stuckSeconds,
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrMergeFlowResolver failed for PR #{Number}", prNumber);
            return null;
        }
    }

    private static PrMergeFlowStep BuildStep(
        string id,
        string title,
        string icon,
        PrMergeFlowStepState state,
        DateTime? startedAt   = null,
        DateTime? completedAt = null,
        DateTime? failedAt    = null) =>
        new()
        {
            Id          = id,
            Title       = title,
            Icon        = icon,
            State       = state,
            Lane        = 0,
            StartedAt   = startedAt,
            CompletedAt = completedAt,
            FailedAt    = failedAt,
        };
}
