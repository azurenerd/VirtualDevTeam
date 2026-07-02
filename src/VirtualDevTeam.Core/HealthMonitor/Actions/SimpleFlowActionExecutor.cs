using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Messaging;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Concrete <see cref="IFlowActionExecutor"/> for operator-approved <see cref="ProposedFlowAction"/>s.
/// Dispatches by <see cref="FlowActionType"/>:
/// <list type="bullet">
///   <item><see cref="FlowActionType.AddPrLabel"/>    — atomically adds a label via <see cref="IPullRequestService"/>.</item>
///   <item><see cref="FlowActionType.RemovePrLabel"/> — atomically removes a label.</item>
///   <item><see cref="FlowActionType.PostPrComment"/> — posts a comment via <see cref="IReviewService"/>.</item>
///   <item><see cref="FlowActionType.NudgeAgent"/>    — publishes a <c>FlowMonitorNudge</c> bus message.</item>
///   <item>All other types                            — returns an unsupported-kind message (no-op).</item>
/// </list>
/// Platform services (<see cref="IPullRequestService"/>, <see cref="IReviewService"/>) are
/// optional — if not registered, the relevant action kinds return a graceful degradation message
/// instead of throwing.
/// </summary>
public sealed class SimpleFlowActionExecutor : IFlowActionExecutor
{
    private readonly IMessageBus _bus;
    private readonly IPullRequestService? _pr;
    private readonly IReviewService? _review;
    private readonly ILogger<SimpleFlowActionExecutor> _logger;

    public SimpleFlowActionExecutor(
        IMessageBus bus,
        ILogger<SimpleFlowActionExecutor> logger,
        IPullRequestService? pr = null,
        IReviewService? review = null)
    {
        _bus    = bus    ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pr     = pr;
        _review = review;
    }

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Type switch
        {
            FlowActionType.AddPrLabel    => AddPrLabelAsync(proposal, ct),
            FlowActionType.RemovePrLabel => RemovePrLabelAsync(proposal, ct),
            FlowActionType.PostPrComment => PostPrCommentAsync(proposal, ct),
            FlowActionType.NudgeAgent    => NudgeAgentAsync(proposal, ct),
            // 2026-05-12 rubber-duck fix: previously returned a string from the _ arm.
            // The approve endpoint treats any non-exception return as success and writes
            // ProposedFlowActionState.Executed — so an operator approving a RetryAgentStep
            // would see "Executed" on the dashboard while the action did nothing. Throw
            // NotSupportedException instead; the endpoint's catch path stores Failed +
            // returns 500 Problem with the exception message in the body.
            _ => ThrowUnsupported(proposal.Type),
        };
    }

    private Task<string> ThrowUnsupported(FlowActionType type)
    {
        _logger.LogWarning(
            "SimpleFlowActionExecutor: action type {Type} is not yet implemented — " +
            "throwing NotSupportedException so the proposal is recorded as Failed instead of Executed",
            type);
        throw new NotSupportedException(
            $"Action type '{type}' is not supported by SimpleFlowActionExecutor — no execution path registered. " +
            "Supported types: AddPrLabel, RemovePrLabel, PostPrComment, NudgeAgent. " +
            "Operator should reject (or re-route to a different executor) rather than approve.");
    }

    // ── Concrete handlers ──────────────────────────────────────────────────────────

    private async Task<string> AddPrLabelAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        if (_pr is null) return "IPullRequestService not available — cannot add label.";
        var prNumber  = GetInt(proposal.Parameters, "prNumber");
        var labelName = GetString(proposal.Parameters, "labelName");
        if (prNumber is null || labelName is null)
            return "Missing required parameters: prNumber, labelName.";
        await _pr.AddLabelAsync(prNumber.Value, labelName, ct);
        _logger.LogInformation("SimpleFlowActionExecutor: added label '{Label}' to PR #{Pr}", labelName, prNumber);
        return $"Added label '{labelName}' to PR #{prNumber}.";
    }

    private async Task<string> RemovePrLabelAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        if (_pr is null) return "IPullRequestService not available — cannot remove label.";
        var prNumber  = GetInt(proposal.Parameters, "prNumber");
        var labelName = GetString(proposal.Parameters, "labelName");
        if (prNumber is null || labelName is null)
            return "Missing required parameters: prNumber, labelName.";
        await _pr.RemoveLabelAsync(prNumber.Value, labelName, ct);
        _logger.LogInformation("SimpleFlowActionExecutor: removed label '{Label}' from PR #{Pr}", labelName, prNumber);
        return $"Removed label '{labelName}' from PR #{prNumber}.";
    }

    private async Task<string> PostPrCommentAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        if (_review is null) return "IReviewService not available — cannot post comment.";
        var prNumber = GetInt(proposal.Parameters, "prNumber");
        var body     = GetString(proposal.Parameters, "body");
        if (prNumber is null || body is null)
            return "Missing required parameters: prNumber, body.";
        await _review.AddCommentAsync(prNumber.Value, body, ct);
        _logger.LogInformation("SimpleFlowActionExecutor: posted comment to PR #{Pr}", prNumber);
        return $"Posted comment to PR #{prNumber}.";
    }

    private async Task<string> NudgeAgentAsync(ProposedFlowAction proposal, CancellationToken ct)
    {
        var agentId = GetString(proposal.Parameters, "agentId");
        var reason  = GetString(proposal.Parameters, "reason") ?? proposal.Rationale;
        if (agentId is null)
            return "Missing required parameter: agentId.";
        await _bus.PublishAsync(new FlowMonitorNudgeMessage
        {
            FromAgentId = "flow-monitor",
            ToAgentId   = agentId,
            MessageType = "FlowMonitorNudge",
            Reason      = $"Operator-approved nudge: {reason}",
        }, ct);
        _logger.LogInformation("SimpleFlowActionExecutor: nudged agent {AgentId}", agentId);
        return $"Published FlowMonitorNudge to agent '{agentId}'.";
    }

    // ── Parameter helpers — handle both raw values and JsonElement (from SQLite JSON round-trip) ──

    private static string? GetString(Dictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return val?.ToString();
    }

    private static int? GetInt(Dictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var val)) return null;
        if (val is JsonElement je)
        {
            if (je.TryGetInt32(out var n)) return n;
            return int.TryParse(je.GetString(), out var s) ? s : null;
        }
        if (val is int i) return i;
        return int.TryParse(val?.ToString(), out var r) ? r : null;
    }
}
