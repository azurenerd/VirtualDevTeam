using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Rung-2 action for <c>agent-claiming-duplicate-task</c> findings: closes the
/// newer duplicate PR and posts a comment explaining the closure. The older PR
/// (lower number, started first) is kept as the canonical implementation.
///
/// This is a destructive action (closes a PR with in-progress work), so it runs
/// at rung 2 (after the agent has been nudged at rung 1 but the duplicate persists).
/// The closed PR's work is not lost — it can be reopened manually if needed.
/// </summary>
public sealed class CloseDuplicatePrAction : IFlowAction
{
    private readonly IPullRequestService _prService;
    private readonly IReviewService _reviewService;
    private readonly ILogger<CloseDuplicatePrAction> _logger;

    public string ActionType => "close-duplicate-pr";
    public int Rung => 2;

    public CloseDuplicatePrAction(
        IPullRequestService prService,
        IReviewService reviewService,
        ILogger<CloseDuplicatePrAction> logger)
    {
        _prService = prService;
        _reviewService = reviewService;
        _logger = logger;
    }

    public bool CanHandle(FlowFinding finding) =>
        string.Equals(finding.DetectorId, "agent-claiming-duplicate-task", StringComparison.OrdinalIgnoreCase);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        // Extract the issue number from TargetResource (format: "issue#1234")
        var issueNumber = ExtractIssueNumber(finding.TargetResource);
        if (issueNumber is null)
        {
            _logger.LogWarning(
                "CloseDuplicatePrAction: could not extract issue number from TargetResource '{Resource}' in finding {Id}",
                finding.TargetResource, finding.Id);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Detail = "Could not extract issue number from finding — manual intervention needed.",
            };
        }

        // Find all open PRs referencing this issue (via branch name pattern t-{issueNumber})
        try
        {
            var openPrs = await _prService.ListOpenAsync(ct);
            var matchingPrs = openPrs
                .Where(p => p.HeadBranch is not null &&
                    p.HeadBranch.Contains($"t-{issueNumber}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Number)
                .ToList();

            if (matchingPrs.Count < 2)
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Skipped,
                    Detail = $"Only {matchingPrs.Count} open PR(s) found for issue #{issueNumber} — no duplicate to close.",
                };
            }

            // Close the NEWER PR (higher number) — the older one started first
            var keepPr = matchingPrs.First().Number;
            var closePr = matchingPrs.Last().Number;

            var comment = $"🔒 **Duplicate task claim detected by FlowMonitor.**\n\n" +
                $"This PR duplicates the work in PR #{keepPr} (which was created first) for issue #{issueNumber}. " +
                $"Closing this PR to prevent merge conflicts and wasted effort.\n\n" +
                $"If this closure was incorrect, reopen this PR manually.";

            await _reviewService.AddCommentAsync(closePr, comment, ct);
            await _prService.CloseAsync(closePr, ct);

            _logger.LogInformation(
                "CloseDuplicatePrAction: closed duplicate PR #{ClosePr} (keeping #{KeepPr}) for issue #{Issue}",
                closePr, keepPr, issueNumber);

            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Detail = $"Closed duplicate PR #{closePr} — keeping PR #{keepPr} for issue #{issueNumber}.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CloseDuplicatePrAction: failed to close duplicate PR for issue #{Issue}",
                issueNumber);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Detail = $"Failed to close duplicate PR for issue #{issueNumber}: {ex.Message}",
            };
        }
    }

    private static int? ExtractIssueNumber(string? targetResource)
    {
        if (string.IsNullOrEmpty(targetResource)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(targetResource, @"#(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }
}
