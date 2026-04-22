using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Strategies.Contracts;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Decorating <see cref="IStrategyEventSink"/> that translates strategy lifecycle events
/// into <see cref="IAgentTaskTracker"/> steps, giving the dashboard real-time visibility
/// into which strategies are running/completed/winning for each SE task.
///
/// Usage: call <see cref="RegisterTask"/> before <see cref="StrategyOrchestrator.RunCandidatesAsync"/>
/// to associate the (runId, taskId) with the owning agent. Call <see cref="UnregisterTask"/> after.
/// The bridge automatically creates a container step ("Multi-strategy code generation") and
/// child steps for each strategy candidate.
/// </summary>
public sealed class StrategyTaskStepBridge : IStrategyEventSink
{
    private readonly IStrategyEventSink _inner;
    private readonly IAgentTaskTracker _tracker;
    private readonly ILogger<StrategyTaskStepBridge> _logger;

    /// <summary>Maps (runId, taskId) → task registration context.</summary>
    private readonly ConcurrentDictionary<(string RunId, string TaskId), TaskRegistration> _registrations = new();

    public StrategyTaskStepBridge(
        IStrategyEventSink inner,
        IAgentTaskTracker tracker,
        ILogger<StrategyTaskStepBridge> logger,
        IOptions<StrategyFrameworkConfig>? config = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (config?.Value.DisplayNames is { Count: > 0 } names)
            SetDisplayNames(names);
    }

    /// <summary>
    /// Register a task so that subsequent strategy events for this (runId, taskId) create
    /// task-tracker steps under the given agent.
    /// </summary>
    public string RegisterTask(string runId, string taskId, string agentId, int strategyCount)
    {
        var containerStepId = _tracker.BeginContainerStep(agentId, taskId,
            "Multi-strategy code generation",
            $"Running {strategyCount} strategies in parallel");

        var reg = new TaskRegistration(agentId, taskId, containerStepId);
        _registrations[(runId, taskId)] = reg;
        return containerStepId;
    }

    /// <summary>Unregister after orchestration completes. Completes or fails the container step.</summary>
    public void UnregisterTask(string runId, string taskId, bool succeeded, string? winnerStrategy = null)
    {
        if (!_registrations.TryRemove((runId, taskId), out var reg)) return;

        if (succeeded && winnerStrategy is not null)
        {
            _tracker.CompleteStep(reg.ContainerStepId);
            // Update the container description with the winner
            // (the step's Description field is mutable)
            if (_tracker is AgentTaskTracker concreteTracker)
            {
                var step = concreteTracker.GetStepById(reg.ContainerStepId);
                if (step is not null)
                    step.Description = $"Winner: {FormatStrategyName(winnerStrategy)}";
            }
        }
        else
        {
            _tracker.FailStep(reg.ContainerStepId, "No winning strategy — falling back to legacy path");
        }
    }

    public async Task EmitAsync(string eventName, object payload, CancellationToken ct)
    {
        // Always forward to the inner sink (state store + SignalR broadcaster)
        await _inner.EmitAsync(eventName, payload, ct);

        // Then translate to task-tracker steps
        try
        {
            switch (eventName)
            {
                case StrategyEvents.CandidateStarted when payload is CandidateStartedEvent e:
                    OnCandidateStarted(e);
                    break;
                case StrategyEvents.CandidateCompleted when payload is CandidateCompletedEvent e:
                    OnCandidateCompleted(e);
                    break;
                case StrategyEvents.CandidateEvaluated when payload is CandidateEvaluatedEvent e:
                    OnCandidateEvaluated(e);
                    break;
                case StrategyEvents.CandidateScored when payload is CandidateScoredEvent e:
                    OnCandidateScored(e);
                    break;
                case StrategyEvents.CandidateDetail:
                    // Detail events are informational for the store; no task-step side effect needed.
                    break;
                case StrategyEvents.WinnerSelected when payload is WinnerSelectedEvent e:
                    OnWinnerSelected(e);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Strategy→TaskStep bridge error for event {Event}", eventName);
        }
    }

    private void OnCandidateStarted(CandidateStartedEvent e)
    {
        if (!_registrations.TryGetValue((e.RunId, e.TaskId), out var reg)) return;

        var stepId = _tracker.BeginChildStep(reg.AgentId, reg.TaskId, reg.ContainerStepId,
            $"🧪 {FormatStrategyName(e.StrategyId)}",
            $"Running {e.StrategyId} code generation strategy");

        reg.CandidateStepIds[e.StrategyId] = stepId;
    }

    private void OnCandidateCompleted(CandidateCompletedEvent e)
    {
        if (!_registrations.TryGetValue((e.RunId, e.TaskId), out var reg)) return;
        if (!reg.CandidateStepIds.TryGetValue(e.StrategyId, out var stepId)) return;

        if (e.Succeeded)
        {
            _tracker.RecordSubStep(stepId,
                $"Completed in {e.ElapsedSec:F1}s{(e.TokensUsed.HasValue ? $" — {e.TokensUsed.Value:N0} tokens" : "")}",
                TimeSpan.FromSeconds(e.ElapsedSec));
            // Don't complete yet — wait for evaluation/scoring/winner selection
        }
        else
        {
            _tracker.FailStep(stepId, e.FailureReason ?? "Strategy failed");
        }
    }

    private void OnCandidateEvaluated(CandidateEvaluatedEvent e)
    {
        if (!_registrations.TryGetValue((e.RunId, e.TaskId), out var reg)) return;
        if (!reg.CandidateStepIds.TryGetValue(e.StrategyId, out var stepId)) return;

        if (e.Survived)
        {
            var detail = e.JudgeSkippedReason is not null
                ? $"Passed build gates — judge skipped ({e.JudgeSkippedReason})"
                : "Passed build gates — awaiting judge scoring";
            _tracker.RecordSubStep(stepId, detail);
        }
        else
        {
            _tracker.RecordSubStep(stepId, $"Failed gate: {e.FailedGate} — {e.FailureDetail}");
            _tracker.FailStep(stepId, $"Gate failed: {e.FailedGate}");
        }
    }

    private void OnCandidateScored(CandidateScoredEvent e)
    {
        if (!_registrations.TryGetValue((e.RunId, e.TaskId), out var reg)) return;
        if (!reg.CandidateStepIds.TryGetValue(e.StrategyId, out var stepId)) return;

        _tracker.RecordSubStep(stepId,
            $"Scored: AC={e.AcScore}/10, Design={e.DesignScore}/10, Readability={e.ReadabilityScore}/10");
    }

    private void OnWinnerSelected(WinnerSelectedEvent e)
    {
        if (!_registrations.TryGetValue((e.RunId, e.TaskId), out var reg)) return;

        // Mark the winner step as completed with a special description
        if (reg.CandidateStepIds.TryGetValue(e.StrategyId, out var winnerStepId))
        {
            _tracker.RecordSubStep(winnerStepId, "🏆 Selected as winner!");
            _tracker.CompleteStep(winnerStepId);
        }

        // Complete all non-winner strategies that are still in-progress
        foreach (var (strategyId, stepId) in reg.CandidateStepIds)
        {
            if (strategyId != e.StrategyId)
            {
                _tracker.CompleteStep(stepId, AgentTaskStepStatus.Skipped);
            }
        }
    }

    private static readonly Dictionary<string, string> BuiltInDisplayNames = new(StringComparer.Ordinal)
    {
        ["baseline"] = "Baseline",
        ["mcp-enhanced"] = "MCP-Enhanced",
        ["copilot-cli"] = "GitHub Copilot CLI",
        ["agentic-delegation"] = "GitHub Copilot CLI",  // backward compat alias
        ["squad"] = "Squad",
    };

    /// <summary>
    /// Optional config-driven display names. Set via <see cref="SetDisplayNames"/> at startup.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? _configDisplayNames;

    /// <summary>Inject per-strategy display names from configuration.</summary>
    public static void SetDisplayNames(IReadOnlyDictionary<string, string>? names) => _configDisplayNames = names;

    internal static string FormatStrategyName(string strategyId)
    {
        if (_configDisplayNames is not null && _configDisplayNames.TryGetValue(strategyId, out var configName))
            return configName;
        if (BuiltInDisplayNames.TryGetValue(strategyId, out var builtIn))
            return builtIn;
        return strategyId;
    }

    /// <summary>Holds per-task registration state for the bridge.</summary>
    private sealed class TaskRegistration
    {
        public string AgentId { get; }
        public string TaskId { get; }
        public string ContainerStepId { get; }
        public ConcurrentDictionary<string, string> CandidateStepIds { get; } = new();

        public TaskRegistration(string agentId, string taskId, string containerStepId)
        {
            AgentId = agentId;
            TaskId = taskId;
            ContainerStepId = containerStepId;
        }
    }
}
