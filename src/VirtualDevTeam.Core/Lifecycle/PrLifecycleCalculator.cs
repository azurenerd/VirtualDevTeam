using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.Lifecycle;

/// <summary>
/// Pure stateless calculator that derives the PR lifecycle from labels, comments, and
/// project configuration. No DI dependencies, no side effects, fully unit-testable.
///
/// Stages are built dynamically from config — not hardcoded matrices. The calculator
/// accounts for: IsInlineTestWorkflow, TestEngineerReviews, IsSinglePr, gate preferences,
/// and peer review agent presence.
/// </summary>
public static class PrLifecycleCalculator
{
    /// <summary>
    /// Compute the full lifecycle for a PR given its current state and project config.
    /// </summary>
    /// <param name="pr">The PR with current labels, dates, and merge state.</param>
    /// <param name="config">Project configuration (workflow mode, gate preferences, etc.).</param>
    /// <param name="comments">PR comments for timestamp/actor extraction. Null = timestamps unavailable.</param>
    /// <param name="hasPeerReviewAgents">Whether SME/worker agents exist for peer review.</param>
    public static PrLifecycle Compute(
        PlatformPullRequest pr,
        VirtualDevTeamConfig config,
        IReadOnlyList<PlatformComment>? comments = null,
        bool hasPeerReviewAgents = false)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(config);

        var stages = BuildStages(config, hasPeerReviewAgents);
        var labels = pr.Labels ?? new List<string>();

        bool has(string label) => labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        // Derive stage statuses from labels
        var isMerged = pr.MergedAt.HasValue;
        var isClosed = !isMerged && string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase);
        var hasReadyForReview = has(PullRequestWorkflow.Labels.ReadyForReview);
        var hasArchitectApproved = has(PullRequestWorkflow.Labels.ArchitectApproved);
        var hasTestsAdded = has(PullRequestWorkflow.Labels.TestsAdded);
        var hasPmApproved = has(PullRequestWorkflow.Labels.PmApproved);
        var hasInProgress = has(PullRequestWorkflow.Labels.InProgress);
        var hasSecurityBlocked = has(PullRequestWorkflow.Labels.SecurityBlocked);
        var hasSecurityAdvisory = has(PullRequestWorkflow.Labels.SecurityAdvisory);
        var hasSecurityEscalated = has("security-escalated");
        var hasSecurityAuditComment = comments?.Any(c =>
            c.Body.Contains("[SecurityAuditor]", StringComparison.OrdinalIgnoreCase)) ?? false;

        // Apply statuses to each stage
        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            if (stage.Status == StageStatus.Skipped) continue; // Already marked skipped by BuildStages

            stages[i] = stage.Id switch
            {
                StageIds.Development => stage with
                {
                    Status = hasReadyForReview || hasArchitectApproved || isMerged
                        ? StageStatus.Complete
                        : hasInProgress ? StageStatus.InProgress : StageStatus.NotStarted,
                    CompletedAt = hasReadyForReview || hasArchitectApproved || isMerged
                        ? FindCommentTimestamp(comments, "has marked this PR as ready for review") ?? pr.CreatedAt
                        : null,
                    Actor = hasReadyForReview || hasArchitectApproved || isMerged
                        ? ExtractAuthorRole(pr.Title) : null,
                },

                StageIds.ArchitectReview => stage with
                {
                    Status = hasArchitectApproved || isMerged
                        ? StageStatus.Complete
                        : hasReadyForReview ? StageStatus.InProgress : StageStatus.NotStarted,
                    CompletedAt = hasArchitectApproved || isMerged
                        ? FindCommentTimestamp(comments, "[Architect]", "APPROVED") : null,
                    Actor = hasArchitectApproved ? "Architect" : null,
                },

                StageIds.PeerReview => ComputePeerReview(stage, comments, hasArchitectApproved, hasTestsAdded, isMerged),

                StageIds.Testing => stage with
                {
                    Status = hasTestsAdded || isMerged
                        ? StageStatus.Complete
                        : hasArchitectApproved ? StageStatus.InProgress : StageStatus.NotStarted,
                    CompletedAt = hasTestsAdded || isMerged
                        ? FindCommentTimestamp(comments, "Test Engineer", "Test Results")
                          ?? FindCommentTimestamp(comments, "[TestEngineer]") : null,
                    Actor = hasTestsAdded ? "Test Engineer" : null,
                },

                // Security Audit stage: visible only when there's evidence of audit activity.
                // - No audit comment → Skipped (not a security-sensitive PR for this run).
                // - security-blocked label → InProgress (blocked, needs rework).
                // - security-escalated label → InProgress (waiting for human decision).
                // - security-advisory-open label → Complete with advisory (approved but findings tracked).
                // - Audit comment present, no blocking label → Complete.
                StageIds.SecurityAudit => hasSecurityAuditComment || hasSecurityBlocked || hasSecurityEscalated
                    ? stage with
                    {
                        Status = hasSecurityBlocked || hasSecurityEscalated
                            ? StageStatus.InProgress
                            : StageStatus.Complete,
                        CompletedAt = !hasSecurityBlocked && !hasSecurityEscalated
                            ? FindCommentTimestamp(comments, "[SecurityAuditor]") : null,
                        Actor = hasSecurityAuditComment ? "Security Auditor" : null,
                        SkipReason = null,
                    }
                    : stage with
                    {
                        Status = StageStatus.Skipped,
                        SkipReason = "No security triggers found — audit not applicable",
                    },

                StageIds.PmReview => stage with
                {
                    Status = hasPmApproved || isMerged
                        ? StageStatus.Complete
                        : hasTestsAdded || (!config.Review.TestEngineerReviews && hasArchitectApproved)
                            ? StageStatus.InProgress : StageStatus.NotStarted,
                    CompletedAt = hasPmApproved || isMerged
                        ? FindCommentTimestamp(comments, "PM", "APPROVED")
                          ?? FindCommentTimestamp(comments, "ProgramManager") : null,
                    Actor = hasPmApproved ? "PM" : null,
                },

                StageIds.Merge => stage with
                {
                    Status = isMerged
                        ? StageStatus.Complete
                        : isClosed ? StageStatus.Skipped
                        : hasPmApproved ? StageStatus.InProgress : StageStatus.NotStarted,
                    CompletedAt = pr.MergedAt,
                    Actor = isMerged ? "Software Engineer" : null,
                    SkipReason = isClosed ? "PR was closed without merging" : null,
                },

                _ => stage
            };
        }

        // Determine what's blocking and who should act next
        var currentStage = stages.FirstOrDefault(s => s.Status == StageStatus.InProgress);
        string? nextActor = currentStage?.Id switch
        {
            StageIds.Development => "Software Engineer",
            StageIds.ArchitectReview => "Architect",
            StageIds.PeerReview => "Software Engineer (peer)",
            StageIds.Testing => "Test Engineer",
            StageIds.SecurityAudit => "Security Auditor",
            StageIds.PmReview => "Program Manager",
            StageIds.Merge => "Software Engineer",
            _ => null
        };

        var missing = new List<string>();
        if (currentStage is not null)
        {
            switch (currentStage.Id)
            {
                case StageIds.ArchitectReview:
                    missing.Add("architect-approved label");
                    break;
                case StageIds.Testing:
                    missing.Add("tests-added label");
                    missing.Add("TE completion comment");
                    break;
                case StageIds.SecurityAudit:
                    if (hasSecurityBlocked)
                        missing.Add("security findings resolved + security-blocked label removed");
                    if (hasSecurityEscalated)
                        missing.Add("human security review decision");
                    break;
                case StageIds.PmReview:
                    missing.Add("pm-approved label");
                    break;
                case StageIds.Merge:
                    missing.Add("PR merge");
                    break;
            }
        }

        return new PrLifecycle
        {
            PrNumber = pr.Number,
            Stages = stages,
            NextRequiredActor = nextActor,
            MissingRequirements = missing,
            ComputedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Build the applicable stage list based on project configuration.
    /// Stages are skipped (not omitted) when their gate/agent is disabled — this keeps
    /// the timeline visually consistent while showing what was skipped and why.
    /// </summary>
    private static List<PrLifecycleStage> BuildStages(VirtualDevTeamConfig config, bool hasPeerReviewAgents)
    {
        var stages = new List<PrLifecycleStage>();
        int order = 0;

        // Development is always present
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.Development, Name = "Development", Icon = "🔨", Order = order++
        });

        // Architect Review — always included (the reviewer agent handles the decision)
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.ArchitectReview, Name = "Architect Review", Icon = "🏗️", Order = order++
        });

        // Peer Review — skip in SinglePR mode or when no peer agents exist
        var peerReviewApplicable = !config.Limits.IsSinglePr && hasPeerReviewAgents;
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.PeerReview, Name = "Peer Review", Icon = "👥", Order = order++,
            Status = peerReviewApplicable ? StageStatus.NotStarted : StageStatus.Skipped,
            SkipReason = !peerReviewApplicable
                ? (config.Limits.IsSinglePr ? "SinglePR mode — no peer agents" : "No peer review agents available")
                : null
        });

        // Testing — skip if TE is disabled
        var teEnabled = config.Review.TestEngineerReviews;
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.Testing, Name = "Testing", Icon = "🧪", Order = order++,
            Status = teEnabled ? StageStatus.NotStarted : StageStatus.Skipped,
            SkipReason = teEnabled ? null : "TestEngineerReviews disabled in config"
        });

        // Security Audit — skip if SecurityAuditor agent is disabled in config.
        // When enabled, the stage is shown only when there is evidence of security
        // audit activity (labels or comments). Otherwise it renders as Skipped to
        // keep the timeline clean for non-security-sensitive PRs.
        var secAuditEnabled = config.Agents.SecurityAuditor.Enabled;
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.SecurityAudit, Name = "Security Audit", Icon = "🔒", Order = order++,
            Status = secAuditEnabled ? StageStatus.NotStarted : StageStatus.Skipped,
            SkipReason = secAuditEnabled ? null : "SecurityAuditor disabled in config"
        });

        // PM Review is always present (final human gate)
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.PmReview, Name = "PM Review", Icon = "📋", Order = order++
        });

        // Merge is always present
        stages.Add(new PrLifecycleStage
        {
            Id = StageIds.Merge, Name = "Merge", Icon = "✅", Order = order++
        });

        return stages;
    }

    private static PrLifecycleStage ComputePeerReview(
        PrLifecycleStage stage,
        IReadOnlyList<PlatformComment>? comments,
        bool hasArchitectApproved,
        bool hasTestsAdded,
        bool isMerged)
    {
        if (stage.Status == StageStatus.Skipped) return stage;

        // Look for SE/SME peer review comments — match [SoftwareEngineer] and
        // numbered variants like [SoftwareEngineer 1], [SoftwareEngineer 2]
        var peerApproval = comments?.FirstOrDefault(c =>
            IsSoftwareEngineerComment(c.Body) &&
            c.Body.Contains("APPROVED", StringComparison.OrdinalIgnoreCase));

        if (peerApproval is not null)
        {
            return stage with
            {
                Status = StageStatus.Complete,
                CompletedAt = peerApproval.CreatedAt,
                Actor = "Software Engineer (peer)"
            };
        }

        // If later stages are complete but no peer review happened, mark as skipped
        if (hasTestsAdded || isMerged)
        {
            return stage with
            {
                Status = StageStatus.Skipped,
                SkipReason = "No peer review comment found — later stages proceeded"
            };
        }

        // Peer review is applicable but hasn't happened yet
        return stage with
        {
            Status = hasArchitectApproved ? StageStatus.InProgress : StageStatus.NotStarted
        };
    }

    /// <summary>Find the first comment matching all keywords and return its timestamp.</summary>
    private static DateTimeOffset? FindCommentTimestamp(
        IReadOnlyList<PlatformComment>? comments, params string[] keywords)
    {
        if (comments is null || keywords.Length == 0) return null;

        var match = comments.FirstOrDefault(c =>
            keywords.All(kw => c.Body.Contains(kw, StringComparison.OrdinalIgnoreCase)));

        return match?.CreatedAt;
    }

    /// <summary>Extract author role from PR title (e.g., "SoftwareEngineer: Task" → "SoftwareEngineer").</summary>
    private static string? ExtractAuthorRole(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var colonIdx = title.IndexOf(':');
        return colonIdx > 0 ? title[..colonIdx].Trim() : null;
    }

    /// <summary>
    /// Checks if a comment body contains a SoftwareEngineer marker, including
    /// numbered variants like [SoftwareEngineer 1], [SoftwareEngineer 2].
    /// </summary>
    internal static bool IsSoftwareEngineerComment(string body) =>
        body.Contains("[SoftwareEngineer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a PR is the Final Integration / T-FINAL PR.
    /// Prefers the <c>final-integration</c> label; falls back to title/branch heuristics.
    /// </summary>
    public static bool IsFinalIntegrationPr(IEnumerable<string>? labels, string? title, string? headBranch) =>
        PullRequestWorkflow.Labels.IsFinalIntegrationPr(labels, title, headBranch);

    /// <summary>
    /// Title-only overload for callers that don't have label data.
    /// Prefer the (labels, title, headBranch) overload when labels are available.
    /// </summary>
    public static bool IsFinalIntegrationPr(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        (title.Contains("Final Integration", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("T-FINAL", StringComparison.OrdinalIgnoreCase));
}
