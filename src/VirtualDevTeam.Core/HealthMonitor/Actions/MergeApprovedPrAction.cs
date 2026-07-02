using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// post-run3-merge-bottleneck: safety-net merger for PRs flagged by
/// <c>UnmergedApprovedPrDetector</c>. The detector identifies PRs that have all
/// required approval labels but haven't been merged because the responsible
/// engineer agent is busy. This action picks up the slack — safely.
///
/// <para>
/// **Safety properties:**
/// 1. RE-FETCHES the PR before merging. If the original engineer merged in the
///    meantime, we see <c>IsMerged=true</c> / non-open state and return NoOp.
/// 2. RE-VERIFIES both <c>architect-approved</c> AND <c>pm-approved</c> labels are
///    still present at execution time — handles race where a reviewer rescinded.
/// 3. Verifies <c>tests-added</c> label is present when the workspace is in inline
///    test workflow mode (matches SE's own merge gate at SoftwareEngineerAgent.cs:4745).
///    Falls back to requiring tests-added if config is unavailable (safer default).
/// 4. Skips merge attempt entirely if <c>MergeableState</c> shows conflicts.
/// 5. Catches <see cref="PlatformConflictException"/> with <c>NotMergeable</c> kind —
///    leaves the PR alone for engineer-driven recovery. No rebase/force-push attempts.
/// 6. <see cref="IFlowAction.UndoAsync"/> is intentionally a no-op — once merged, you
///    can't cleanly un-merge. The action is one-way, and the detector won't re-fire
///    because the PR is no longer open.
/// </para>
///
/// <para>
/// **Why this lives as a flow action and not in <c>PullRequestWorkflow</c>:** Engineers
/// own the normal merge path. This action is a catcher's mitt only — invoked by the
/// orchestrator-level FlowMonitor when normal flow is delayed. Keeping it separate
/// makes the safety boundary obvious in code review (only one place merges a PR
/// outside of the engineer agent: this file).
/// </para>
///
/// <para>
/// **Optional dependencies:** if <see cref="IPullRequestService"/> is null (project
/// not opened yet), the action returns <see cref="FlowActionResult.Skipped"/>. The
/// workspace config is optional — when present, the inline-test gate is enforced;
/// when absent, the action conservatively requires tests-added anyway.
/// </para>
/// </summary>
public sealed class MergeApprovedPrAction : IFlowAction
{
    public string ActionType => "merge-approved-pr";

    private const string ArchitectApprovedLabel = "architect-approved";
    private const string PmApprovedLabel = "pm-approved";
    private const string TestsAddedLabel = "tests-added";

    private readonly IPullRequestService? _pullRequestService;
    private readonly IBranchService? _branchService;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;
    private readonly ILogger<MergeApprovedPrAction> _logger;

    public MergeApprovedPrAction(
        ILogger<MergeApprovedPrAction> logger,
        IPullRequestService? pullRequestService = null,
        IBranchService? branchService = null,
        IOptionsMonitor<VirtualDevTeamConfig>? config = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _pullRequestService = pullRequestService;
        _branchService = branchService;
        _config = config;
    }

    public bool CanHandle(FlowFinding finding) =>
        string.Equals(finding.DetectorId, "unmerged-approved-pr", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(finding.TargetResource);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (_pullRequestService is null)
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Target = finding.TargetResource,
                Detail = "IPullRequestService not bound (project not opened)",
            };
        }

        if (!TryParsePrNumber(finding.TargetResource, out var prNumber))
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Target = finding.TargetResource,
                Detail = $"Cannot parse PR number from target '{finding.TargetResource}'",
            };
        }

        try
        {
            // SAFETY GATE 1: re-fetch PR. If merged/closed since detection, we're done.
            var pr = await _pullRequestService.GetAsync(prNumber, ct).ConfigureAwait(false);
            if (pr is null)
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = "PR not found on platform (deleted?)",
                };
            }
            if (pr.IsMerged)
            {
                _logger.LogInformation(
                    "FlowMonitor merge-approved-pr: PR #{Number} already merged — no-op (race with engineer agent)",
                    prNumber);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = "PR already merged (race with engineer agent)",
                };
            }
            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = $"PR in non-open state: {pr.State}",
                };
            }

            // SAFETY GATE 2: re-verify dual-reviewer labels at execution time.
            var hasArchitect = pr.Labels.Contains(ArchitectApprovedLabel, StringComparer.OrdinalIgnoreCase);
            var hasPm = pr.Labels.Contains(PmApprovedLabel, StringComparer.OrdinalIgnoreCase);
            if (!hasArchitect || !hasPm)
            {
                _logger.LogInformation(
                    "FlowMonitor merge-approved-pr: PR #{Number} no longer has required labels (architect={HasArchitect}, pm={HasPm}) — skipping",
                    prNumber, hasArchitect, hasPm);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Skipped,
                    Target = $"pr#{prNumber}",
                    Detail = $"Required approval labels missing: architect={hasArchitect}, pm={hasPm}",
                };
            }

            // SAFETY GATE 2.5: never merge a security-blocked PR.
            // The SecurityAuditor must re-review and clear the label before FlowMonitor
            // can act as the safety-net merger. Unlike conflict states, security findings
            // require human inspection — do not skip silently, log a warning.
            if (pr.Labels.Contains("security-blocked", StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "FlowMonitor merge-approved-pr: PR #{Number} has security-blocked label — " +
                    "merge refused. SecurityAuditor findings must be resolved first.",
                    prNumber);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Skipped,
                    Target = $"pr#{prNumber}",
                    Detail = "security-blocked label present — SecurityAuditor findings must be resolved before FlowMonitor can merge",
                };
            }

            // SAFETY GATE 3: inline-test-workflow check — require tests-added when active.
            // Default to requiring it if config isn't injected (safer-by-default).
            // disable-te-toggle: when Review.TestEngineerReviews is OFF, the TE never applies the
            // tests-added label, so requiring it would deadlock the merge.
            var cfg = _config?.CurrentValue;
            var requireTests = (cfg?.Workspace.IsInlineTestWorkflow ?? true)
                && (cfg?.Review.TestEngineerReviews ?? true);
            if (requireTests && !pr.Labels.Contains(TestsAddedLabel, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "FlowMonitor merge-approved-pr: PR #{Number} fully approved but missing tests-added (inline test workflow active) — waiting for TE",
                    prNumber);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Skipped,
                    Target = $"pr#{prNumber}",
                    Detail = "Inline test workflow active and tests-added label is missing — waiting for TE",
                };
            }

            // SAFETY GATE 4: mergeability — bail early on known-bad states.
            // Tightened per rubber-duck review: also reject "unstable" (failing non-required
            // checks) and "unknown" when the PR has agent-stuck label — extra caution on the
            // bypass path where we're overriding a human escalation signal.
            var state = pr.MergeableState ?? string.Empty;
            var hasAgentStuck = pr.Labels.Contains("agent-stuck", StringComparer.OrdinalIgnoreCase);
            if (string.Equals(state, "dirty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "blocked", StringComparison.OrdinalIgnoreCase) ||
                (hasAgentStuck && string.Equals(state, "unstable", StringComparison.OrdinalIgnoreCase)) ||
                (hasAgentStuck && string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation(
                    "FlowMonitor merge-approved-pr: PR #{Number} has MergeableState={State} — leaving for engineer rebase",
                    prNumber, state);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Skipped,
                    Target = $"pr#{prNumber}",
                    Detail = $"MergeableState={state} requires engineer-driven recovery",
                };
            }

            // ALL GATES PASSED — attempt merge.
            var commitMessage = "Merged by FlowMonitor safety-net after dual approval " +
                                "(architect + PM) — engineer agent was busy or restarted. " +
                                $"Original finding: {finding.Id}";
            try
            {
                await _pullRequestService.MergeAsync(prNumber, commitMessage, ct).ConfigureAwait(false);
            }
            catch (PlatformConflictException ex) when (ex.Kind == PlatformConflictKind.NotMergeable)
            {
                // Race guard: another worker may have just merged. Re-fetch to disambiguate.
                try
                {
                    var refetch = await _pullRequestService.GetAsync(prNumber, ct).ConfigureAwait(false);
                    if (refetch?.IsMerged == true)
                    {
                        _logger.LogInformation(
                            "FlowMonitor merge-approved-pr: PR #{Number} merged by another worker — no-op",
                            prNumber);
                        return new FlowActionOutcome
                        {
                            Result = FlowActionResult.NoOp,
                            Target = $"pr#{prNumber}",
                            Detail = "PR merged by another worker between gate-check and merge call",
                        };
                    }
                }
                catch (Exception fetchEx)
                {
                    _logger.LogDebug(fetchEx, "Re-fetch after NotMergeable failed for PR #{Number}", prNumber);
                }

                _logger.LogWarning(
                    "FlowMonitor merge-approved-pr: PR #{Number} not mergeable — leaving for engineer-driven recovery",
                    prNumber);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Failed,
                    Target = $"pr#{prNumber}",
                    Detail = "Merge call returned NotMergeable — engineer-driven rebase needed",
                };
            }

            _logger.LogInformation(
                "FlowMonitor merge-approved-pr: PR #{Number} merged successfully (safety-net)",
                prNumber);

            // Best-effort: strip agent-stuck label post-merge so it doesn't pollute audit views.
            if (hasAgentStuck)
            {
                try
                {
                    await _pullRequestService.RemoveLabelAsync(prNumber, "agent-stuck", ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "FlowMonitor merge-approved-pr: stripped agent-stuck label from merged PR #{Number}", prNumber);
                }
                catch (Exception labelEx)
                {
                    _logger.LogDebug(labelEx,
                        "FlowMonitor merge-approved-pr: agent-stuck label cleanup failed for PR #{Number} (non-fatal)",
                        prNumber);
                }
            }

            // Best-effort branch cleanup — don't fail the action on cleanup error.
            if (_branchService is not null && !string.IsNullOrEmpty(pr.HeadBranch))
            {
                try
                {
                    await _branchService.DeleteAsync(pr.HeadBranch, ct).ConfigureAwait(false);
                }
                catch (Exception delEx)
                {
                    _logger.LogDebug(delEx,
                        "FlowMonitor merge-approved-pr: branch cleanup failed for {Branch} (non-fatal)",
                        pr.HeadBranch);
                }
            }

            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Target = $"pr#{prNumber}",
                Detail = $"Merged PR #{prNumber} after dual approval (architect + PM)" +
                         (requireTests ? " + tests-added" : ""),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MergeApprovedPrAction failed for PR target {Target}", finding.TargetResource);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetResource,
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // Parses "pr#1234" → 1234. Returns false for any other shape so the action
    // refuses to act on findings it doesn't understand.
    private static bool TryParsePrNumber(string? targetResource, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(targetResource)) return false;
        const string prefix = "pr#";
        if (!targetResource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(targetResource.AsSpan(prefix.Length), out number) && number > 0;
    }
}
