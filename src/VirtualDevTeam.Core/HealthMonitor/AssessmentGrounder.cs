using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Post-LLM validation that grounds AI claims against actual snapshot data.
/// Every issue the AI raises must cite specific targets that exist in the snapshot.
/// Unresolvable references are demoted to grounding_passed=false and excluded
/// from FlowFinding creation (but still persisted in the assessment for transparency).
/// </summary>
public sealed class AssessmentGrounder
{
    private readonly ILogger<AssessmentGrounder> _logger;

    public AssessmentGrounder(ILogger<AssessmentGrounder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validate each issue's target references against the snapshot context.
    /// Returns the issues with GroundingPassed set, and the overall pass rate.
    /// </summary>
    public GroundingResult Ground(AssessmentIssue[] issues, PipelineStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (issues.Length == 0)
            return new GroundingResult(Array.Empty<AssessmentIssue>(), 1.0);

        var grounded = new List<AssessmentIssue>(issues.Length);
        var passCount = 0;

        foreach (var issue in issues)
        {
            var passed = ValidateIssue(issue, snapshot);
            grounded.Add(issue with { GroundingPassed = passed });
            if (passed) passCount++;
        }

        var passRate = (double)passCount / issues.Length;
        _logger.LogDebug(
            "AssessmentGrounder: {Passed}/{Total} issues passed grounding ({Rate:P0})",
            passCount, issues.Length, passRate);

        return new GroundingResult(grounded.ToArray(), passRate);
    }

    private bool ValidateIssue(AssessmentIssue issue, PipelineStatusSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(issue.TargetType) || string.IsNullOrWhiteSpace(issue.TargetId))
        {
            // No specific target claimed — can't invalidate, treat as generic observation
            return true;
        }

        var targetType = issue.TargetType.ToLowerInvariant();
        var targetId = issue.TargetId;

        return targetType switch
        {
            "agent" => snapshot.Agents?.Any(a =>
                string.Equals(a.AgentId, targetId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.DisplayName, targetId, StringComparison.OrdinalIgnoreCase)) ?? false,

            "pr" or "pullrequest" => int.TryParse(targetId.TrimStart('#'), out var prNum) &&
                (snapshot.PullRequests?.Any(p => p.Number == prNum) ?? false),

            "task" or "workitem" or "issue" => snapshot.WorkItems?.Any(w =>
                string.Equals(w.TaskId, targetId, StringComparison.OrdinalIgnoreCase) ||
                (int.TryParse(targetId.TrimStart('#'), out var num) && w.Number == num)) ?? false,

            "phase" => !string.IsNullOrWhiteSpace(snapshot.CurrentPhase),

            _ => LogUnknownTargetType(targetType)
        };
    }

    private bool LogUnknownTargetType(string targetType)
    {
        _logger.LogDebug("AssessmentGrounder: unknown target type '{Type}' — treating as valid", targetType);
        return true;
    }
}

/// <summary>Result of grounding validation.</summary>
public sealed record GroundingResult(AssessmentIssue[] Issues, double PassRate);
