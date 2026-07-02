using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects agents that have been in the Working state continuously for longer than
/// the configured threshold without any status-reason update. Indicates a hung agent
/// (e.g., AI call wedged, network stall) where the existing pipeline won't recover.
///
/// Before flagging, checks for recent log output and active LLM calls — an agent
/// that is actively producing logs or waiting for an AI response is working, not stuck.
/// </summary>
public sealed class AgentStuckDetector : IFlowDetector
{
    public string DetectorId => "agent-stuck";

    private readonly TimeSpan _threshold;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private readonly ILogger<AgentStuckDetector> _logger;

    /// <summary>Max age of the most recent log entry for the agent to be considered "active".</summary>
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(10);

    public AgentStuckDetector(
        TimeSpan threshold,
        ILogger<AgentStuckDetector> logger,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _threshold = threshold;
        _logger = logger;
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            foreach (var agent in ctx.Agents)
            {
                if (!string.Equals(agent.Status, "Working", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (agent.StatusChangedAt is null) continue;
                var stuckFor = ctx.Now - agent.StatusChangedAt.Value;
                if (stuckFor < _threshold) continue;

                var reason = agent.StatusReason ?? "";
                var multiplier = GetActivityMultiplier(reason);
                var effectiveThreshold = _threshold * multiplier;
                if (stuckFor < effectiveThreshold) continue;

                // Check for recent log activity — if the agent is producing output, it's working
                var hasRecentLogs = false;
                DateTime? lastLogAt = null;
                if (_logService is not null)
                {
                    lastLogAt = _logService.GetLatestEntryTimestamp(agent.Id);
                    if (lastLogAt.HasValue)
                    {
                        var logAge = DateTime.UtcNow - lastLogAt.Value;
                        hasRecentLogs = logAge < LogActivityWindow;
                    }
                }

                // Check if there's an active LLM call in-flight
                var activeLlmCall = _llmTracker?.GetActiveCall(agent.Id);
                var hasActiveLlmCall = activeLlmCall is not null;

                // If agent has recent logs OR an active LLM call, it's not stuck — skip
                if (hasRecentLogs || hasActiveLlmCall)
                {
                    _logger.LogDebug(
                        "Agent {Agent} has been working for {Duration} (past threshold {Threshold}) " +
                        "but is still active — RecentLogs: {HasLogs} (last {LogAge} ago), ActiveLLM: {HasLlm}{LlmContext}",
                        agent.DisplayName, FormatDuration(stuckFor), FormatDuration(effectiveThreshold),
                        hasRecentLogs, lastLogAt.HasValue ? FormatDuration(DateTime.UtcNow - lastLogAt.Value) : "N/A",
                        hasActiveLlmCall, activeLlmCall?.Context is not null ? $" ({activeLlmCall.Context})" : "");
                    continue;
                }

                // Build informative rationale including log/LLM status
                var logStatus = lastLogAt.HasValue
                    ? $"Last log entry was {FormatDuration(DateTime.UtcNow - lastLogAt.Value)} ago."
                    : "No log entries found for this agent.";

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = stuckFor > _threshold * 2 ? FlowFindingSeverity.Critical : FlowFindingSeverity.Warning,
                    TargetAgentId = agent.Id,
                    TargetResource = agent.Id,
                    TargetDisplayName = agent.DisplayName,
                    Summary = $"Agent {agent.DisplayName} has been Working for {FormatDuration(stuckFor)}",
                    Rationale = $"Status reason: \"{agent.StatusReason ?? "(none)"}\". " +
                                $"Threshold for stuck-detection is {FormatDuration(_threshold)} " +
                                $"(×{multiplier:0.#} for activity type = {FormatDuration(effectiveThreshold)}). " +
                                $"{logStatus} No active LLM call detected. " +
                                "This suggests a wedged AI call, a hung network operation, " +
                                "or a missing terminate-condition in the agent's loop.",
                    DedupKey = $"agent-stuck:{agent.Id}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentStuckDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    /// <summary>
    /// Per-reason multipliers instead of a blanket 3x for all long-running activities.
    /// Strategy/framework sessions are genuinely long (3x), build fix/rework is moderate (2x),
    /// integration PRs and other activities use the base threshold (1x).
    /// </summary>
    internal static double GetActivityMultiplier(string statusReason)
    {
        if (string.IsNullOrEmpty(statusReason)) return 1.0;

        // Strategy framework sessions can run 30-45min legitimately
        if (statusReason.Contains("Strategy", StringComparison.OrdinalIgnoreCase)
            || statusReason.Contains("Framework", StringComparison.OrdinalIgnoreCase))
            return 3.0;

        // Build fix, rework, self-assessment, CLI edit — moderate extensions
        if (statusReason.Contains("self-assessment", StringComparison.OrdinalIgnoreCase)
            || statusReason.Contains("Rework", StringComparison.OrdinalIgnoreCase)
            || statusReason.Contains("CLI edit", StringComparison.OrdinalIgnoreCase)
            || statusReason.Contains("build fix", StringComparison.OrdinalIgnoreCase))
            return 2.0;

        // Integration PR — complex merge but shouldn't take forever
        if (statusReason.Contains("integration PR", StringComparison.OrdinalIgnoreCase))
            return 1.5;

        return 1.0;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{ts.TotalHours:0.0}h";
    }
}
