using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor action that cancels a stuck strategy orchestration to trigger emergency winner
/// selection. Handles findings from <see cref="StrategyEvaluationStuckDetector"/> (dedup key
/// prefix <c>strategy-stuck:</c>).
///
/// <para>
/// Before cancelling, posts a rich notification with candidate data (strategy types, scores,
/// build status) so the operator can see what's happening. The cancellation then triggers
/// <see cref="StrategyOrchestrator"/>'s catch block, which calls
/// <see cref="CandidateEvaluator.SelectEmergencyWinner"/> to pick the best available
/// candidate from partial results — reusing the Phase 2 emergency winner path.
/// </para>
/// </summary>
public sealed class PromoteStrategyWinnerAction : IFlowAction
{
    public string ActionType => "promote-strategy-winner";

    private readonly IOrchestrationCancellationService _cancellation;
    private readonly CandidateStateStore _stateStore;
    private readonly GateNotificationService? _notifications;
    private readonly ILogger<PromoteStrategyWinnerAction> _logger;

    public PromoteStrategyWinnerAction(
        IOrchestrationCancellationService cancellation,
        CandidateStateStore stateStore,
        ILogger<PromoteStrategyWinnerAction> logger,
        GateNotificationService? notifications = null)
    {
        _cancellation = cancellation;
        _stateStore = stateStore;
        _logger = logger;
        _notifications = notifications;
    }

    public bool CanHandle(FlowFinding finding)
        => finding.DedupKey.StartsWith("strategy-stuck:", StringComparison.Ordinal);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        // Parse taskId from dedup key: "strategy-stuck:{taskId}:{condition}"
        var parts = finding.DedupKey.Split(':');
        if (parts.Length < 3)
        {
            _logger.LogWarning("PromoteStrategyWinnerAction: unexpected dedup key format '{Key}'", finding.DedupKey);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetResource,
                Detail = $"Could not parse taskId from dedup key: {finding.DedupKey}",
            };
        }

        var taskId = parts[1];
        var condition = parts[2];

        // Look up the task from active tasks
        var activeTasks = _stateStore.GetActiveTasks();
        var matchingTask = activeTasks.FirstOrDefault(t =>
            string.Equals(t.TaskId, taskId, StringComparison.Ordinal));

        if (matchingTask is null)
        {
            _logger.LogInformation(
                "PromoteStrategyWinnerAction: task '{TaskId}' no longer active (may have resolved itself)",
                taskId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.NoOp,
                Target = $"strategy-task:{taskId}",
                Detail = "Task no longer active — may have resolved between detection and action.",
            };
        }

        // If a winner was already selected, skip
        if (matchingTask.WinnerStrategyId is not null)
        {
            _logger.LogInformation(
                "PromoteStrategyWinnerAction: task '{TaskId}' already has winner '{Winner}', skipping",
                taskId, matchingTask.WinnerStrategyId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.NoOp,
                Target = $"strategy-task:{taskId}",
                Detail = $"Winner '{matchingTask.WinnerStrategyId}' already selected.",
            };
        }

        // Build rich notification content with candidate data before cancelling
        var candidateSummary = BuildCandidateSummary(matchingTask);
        var notificationContext =
            $"🚨 **Strategy evaluation stuck** ({condition}) for task `{Truncate(taskId, 60)}`\n\n" +
            $"{candidateSummary}\n\n" +
            $"**Action:** Cancelling orchestration to trigger emergency winner selection — " +
            $"the best available candidate will be promoted automatically.\n\n" +
            $"✅ **Approve** to accept the emergency winner | " +
            $"🔄 **Request Rework** to retry with feedback";

        if (_notifications is not null)
        {
            try
            {
                await _notifications.AddNotificationAsync(
                    "strategy-emergency-promotion",
                    notificationContext,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PromoteStrategyWinnerAction: failed to post notification for task '{TaskId}' (non-fatal)",
                    taskId);
            }
        }

        var runId = matchingTask.RunId;
        _logger.LogWarning(
            "PromoteStrategyWinnerAction: cancelling orchestration for run '{RunId}' task '{TaskId}' " +
            "(condition: {Condition}) via emergency promotion to trigger winner selection",
            runId, taskId, condition);

        var cancelled = _cancellation.RequestEmergencyPromotion(runId, taskId);

        return new FlowActionOutcome
        {
            Result = cancelled ? FlowActionResult.Success : FlowActionResult.NoOp,
            Target = $"strategy-task:{taskId}",
            Detail = cancelled
                ? $"Cancelled orchestration for {condition}; emergency winner selection will engage.\n{candidateSummary}"
                : $"Cancellation request returned false — orchestration may have already completed.",
        };
    }

    public Task UndoAsync(FlowFinding finding, CancellationToken ct)
        => Task.CompletedTask;

    private static string BuildCandidateSummary(TaskSnapshot task)
    {
        if (task.Candidates.IsEmpty)
            return "No candidates available.";

        var lines = new List<string> { $"**Candidates ({task.Candidates.Count}):**" };
        foreach (var (strategyId, candidate) in task.Candidates)
        {
            var friendlyName = strategyId switch
            {
                "copilot-cli" => "Copilot CLI",
                "squad" => "Squad",
                _ => strategyId
            };

            var stateIcon = candidate.State.ToString() switch
            {
                "Evaluated" => "✅",
                "Running" => "🔄",
                "Failed" => "❌",
                "Cancelled" => "⛔",
                _ => "⬜"
            };

            var scoreInfo = candidate.AcScore.HasValue
                ? $"Scores: AC={candidate.AcScore:F0}, Design={candidate.DesignScore:F0}, Readability={candidate.ReadabilityScore:F0}"
                : "not scored";
            var visualInfo = candidate.VisualsScore.HasValue
                ? $", Visual={candidate.VisualsScore:F0}"
                : "";
            var elapsed = candidate.CompletedAt.HasValue && candidate.ProcessStartedAt.HasValue
                ? $" in {(candidate.CompletedAt.Value - candidate.ProcessStartedAt.Value).TotalMinutes:F0} min"
                : "";

            lines.Add($"  {stateIcon} **{friendlyName}**: {candidate.State}{elapsed} — {scoreInfo}{visualInfo}");
        }

        return string.Join("\n", lines);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
