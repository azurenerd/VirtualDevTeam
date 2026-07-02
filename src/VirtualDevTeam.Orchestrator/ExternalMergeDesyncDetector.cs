using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Detects agents whose <see cref="AgentStateView.CurrentPrNumber"/> points at a PR that
/// has been CLOSED or MERGED externally (e.g. via <c>gh pr merge</c> from the operator or
/// another agent), but the agent is still actively working on it.
///
/// <para>
/// Live evidence 2026-05-12 21:49: 3 agents wasting LLM/API calls on already-merged PRs
/// because the runtime never received a state-change event for the external merges:
/// SE1 reworking PR #1511 (merged 10+ min earlier), TestEngineer generating tests for the
/// same PR, Artist processing PR #1508 (merged 15+ min earlier).
/// </para>
///
/// <para>
/// Detection strategy: fetch the open-PR list once (cached for the tick via
/// <see cref="IPlatformView"/>), build a hash-set of open PR numbers, then for every
/// <c>Working</c> agent that has a non-null <c>CurrentPrNumber</c>, check whether the PR
/// appears in that set. Absence → the PR was merged/closed without the agent's knowledge.
/// </para>
///
/// <para>
/// Resolution is NOT auto-applied — the agent may be mid-write and losing that work is
/// worse than the cost of a single extra status poll. The finding is routed through the
/// standard escalation ladder (rung 1 nudge → rung 2 PR comment → rung 3 human escalation)
/// so the operator can approve via the FlowMonitor Approvals page.
/// </para>
/// </summary>
public sealed class ExternalMergeDesyncDetector : IFlowDetector
{
    public string DetectorId => "external-merge-desync";

    private readonly ILogger<ExternalMergeDesyncDetector> _logger;

    public ExternalMergeDesyncDetector(ILogger<ExternalMergeDesyncDetector> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var openPrs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            if (openPrs.Count == 0 && !ctx.Agents.Any(a =>
                    a.Status == "Working" && a.CurrentPrNumber is not null and not 0))
            {
                // Nothing to check — either no open PRs exist at all or no Working agent
                // holds a PR reference. Both cases are innocuous; skip the loop.
                return findings;
            }

            var openSet = new HashSet<int>(openPrs.Select(p => p.Number));

            foreach (var agent in ctx.Agents)
            {
                if (ct.IsCancellationRequested) break;

                if (agent.Status != "Working") continue;
                if (agent.CurrentPrNumber is null or 0) continue;

                var prNumber = agent.CurrentPrNumber.Value;
                if (openSet.Contains(prNumber)) continue;

                // PR is absent from the open-PR list — merged or closed externally.
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    TargetAgentId = agent.Id,
                    TargetDisplayName = agent.DisplayName,
                    TargetResource = $"pr#{prNumber}",
                    Summary = $"Agent {agent.DisplayName} still Working on PR #{prNumber} but the PR is no longer open",
                    Rationale =
                        $"Agent {agent.DisplayName} ({agent.Id}) has CurrentPrNumber={prNumber} but PR #{prNumber} " +
                        "does not appear in the platform's open-PR list — it was likely merged or closed outside " +
                        "the agent's own logic (e.g. 'gh pr merge' from operator, GitHub UI merge, or a sibling " +
                        "agent). No state-change event reached this runtime so the agent keeps spending LLM calls " +
                        "and API quota on the now-dead PR. " +
                        "Resolution: clear the agent's CurrentPrNumber so it can pick up its next eligible task. " +
                        "NOT auto-applied — the agent may be mid-write; an operator decision via the Approvals " +
                        "page is safer than an automatic interrupt.",
                    DedupKey = $"external-merge-desync:{agent.Id}:{prNumber}",
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExternalMergeDesyncDetector tick failed (non-fatal)");
        }
        return findings;
    }
}
