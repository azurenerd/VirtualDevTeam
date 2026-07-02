using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Agents;

/// <summary>
/// In-process singleton registry that prevents multiple engineer agents from claiming
/// the same work item simultaneously. Uses <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// as an atomic lock — the first agent to call <see cref="TryClaim"/> wins; all others
/// see the task as already taken and skip it.
///
/// The registry is scoped to the lifetime of the Runner process. On restart, it starts
/// empty — the existing <c>AgentClaimingDuplicateTaskDetector</c> FlowMonitor detector
/// provides the safety net for cross-restart duplicates.
///
/// DI registration: <c>services.AddSingleton&lt;ClaimedTaskRegistry&gt;()</c>
/// </summary>
public sealed class ClaimedTaskRegistry
{
    private readonly ConcurrentDictionary<int, string> _claims = new();
    private readonly ConcurrentDictionary<int, StrategyClaim> _strategyClaims = new();
    private readonly ILogger<ClaimedTaskRegistry> _logger;

    public ClaimedTaskRegistry(ILogger<ClaimedTaskRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempt to claim a work item. Returns true if this agent won the claim;
    /// false if another agent already claimed it.
    /// </summary>
    public bool TryClaim(int issueNumber, string agentId)
    {
        if (_claims.TryAdd(issueNumber, agentId))
        {
            _logger.LogInformation(
                "Task #{IssueNumber} claimed by {AgentId}",
                issueNumber, agentId);
            return true;
        }

        var holder = _claims.GetValueOrDefault(issueNumber, "unknown");
        if (string.Equals(holder, agentId, StringComparison.Ordinal))
            return true; // same agent re-claiming (idempotent)

        _logger.LogInformation(
            "Task #{IssueNumber} already claimed by {Holder} — {Requester} skipping",
            issueNumber, holder, agentId);
        return false;
    }

    /// <summary>
    /// Check if a work item is already claimed by any agent.
    /// </summary>
    public bool IsClaimed(int issueNumber) => _claims.ContainsKey(issueNumber);

    /// <summary>
    /// Get the agent ID that holds the claim, or null if unclaimed.
    /// </summary>
    public string? GetClaimHolder(int issueNumber) =>
        _claims.TryGetValue(issueNumber, out var holder) ? holder : null;

    /// <summary>
    /// Release a claim (e.g., when the agent abandons the task or the PR is closed).
    /// </summary>
    public void Release(int issueNumber)
    {
        if (_claims.TryRemove(issueNumber, out var holder))
        {
            _logger.LogDebug("Task #{IssueNumber} claim released (was held by {Holder})",
                issueNumber, holder);
        }
    }

    /// <summary>
    /// Record a claim from a bus message (another agent claimed it).
    /// </summary>
    public void RecordClaim(int issueNumber, string agentId)
    {
        _claims.TryAdd(issueNumber, agentId);
    }

    // ─── Strategy evaluation claims ───

    /// <summary>
    /// Attempt to claim strategy evaluation for a work item. Returns true if this agent
    /// won the claim; false if another agent is already evaluating strategies for it.
    /// Claims auto-expire after <paramref name="ttl"/> (default 60 min) to prevent
    /// orphaned claims from crashed agents blocking all future evaluations.
    /// </summary>
    public bool TryClaimStrategy(int issueNumber, string agentId, TimeSpan? ttl = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = ttl ?? TimeSpan.FromMinutes(60);

        // Check for expired claim and remove it first
        if (_strategyClaims.TryGetValue(issueNumber, out var existing))
        {
            if (string.Equals(existing.AgentId, agentId, StringComparison.Ordinal))
            {
                // Same agent re-claiming (idempotent) — refresh timestamp
                _strategyClaims[issueNumber] = new StrategyClaim(agentId, now);
                return true;
            }

            if ((now - existing.ClaimedAt) > expiry)
            {
                // Expired claim — remove and allow new claim
                _logger.LogInformation(
                    "Strategy claim for task #{IssueNumber} by {OldAgent} expired ({Age:0}m > {Ttl:0}m) — releasing for {NewAgent}",
                    issueNumber, existing.AgentId, (now - existing.ClaimedAt).TotalMinutes, expiry.TotalMinutes, agentId);
                _strategyClaims.TryRemove(issueNumber, out _);
            }
            else
            {
                _logger.LogInformation(
                    "Strategy evaluation for task #{IssueNumber} already claimed by {Holder} — {Requester} skipping",
                    issueNumber, existing.AgentId, agentId);
                return false;
            }
        }

        if (_strategyClaims.TryAdd(issueNumber, new StrategyClaim(agentId, now)))
        {
            _logger.LogInformation(
                "Strategy evaluation for task #{IssueNumber} claimed by {AgentId}",
                issueNumber, agentId);
            return true;
        }

        // Lost the race — another agent claimed between our check and TryAdd
        return false;
    }

    /// <summary>
    /// Release a strategy evaluation claim.
    /// </summary>
    public void ReleaseStrategy(int issueNumber)
    {
        if (_strategyClaims.TryRemove(issueNumber, out var claim))
        {
            _logger.LogDebug("Strategy claim for task #{IssueNumber} released (was held by {Holder})",
                issueNumber, claim.AgentId);
        }
    }

    /// <summary>
    /// Check if strategy evaluation is claimed for a work item.
    /// </summary>
    public bool IsStrategyClaimed(int issueNumber) => _strategyClaims.ContainsKey(issueNumber);

    private sealed record StrategyClaim(string AgentId, DateTimeOffset ClaimedAt);
}