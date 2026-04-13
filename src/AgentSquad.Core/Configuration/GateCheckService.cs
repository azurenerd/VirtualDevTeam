using System.Collections.Concurrent;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Configuration;

/// <summary>
/// Evaluates human interaction gates at workflow touchpoints.
/// Supports two approval paths:
///   1. GitHub-based: labels/comments on a PR or issue (when resourceNumber is provided)
///   2. Local: in-memory approval via REST API/dashboard (always available, required for
///      workflow-level gates like ResearchCompleteness that have no associated PR)
/// When gates are disabled or set to auto, returns Proceed immediately.
/// </summary>
public class GateCheckService : IGateCheckService
{
    private const string AwaitingHumanLabel = "awaiting-human-review";
    private const string HumanApprovedLabel = "human-approved";
    private const string GateCommentPrefix = "🚦 **Human Review Gate";

    /// <summary>
    /// Tracks gates approved via the local path (REST API / dashboard).
    /// Key = gateId, Value = UTC timestamp of approval. Thread-safe.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _localApprovals = new();

    private readonly HumanInteractionConfig _config;
    private readonly IGitHubService _github;
    private readonly GateNotificationService? _notificationService;
    private readonly ILogger<GateCheckService> _logger;

    public GateCheckService(
        IOptions<AgentSquadConfig> config,
        IGitHubService github,
        ILogger<GateCheckService> logger,
        GateNotificationService? notificationService = null)
    {
        _config = config.Value.HumanInteraction;
        _github = github;
        _logger = logger;
        _notificationService = notificationService;
    }

    public bool IsEnabled => _config.Enabled;

    public bool RequiresHuman(string gateId) => _config.RequiresHuman(gateId);

    public async Task<GateResult> CheckGateAsync(
        string gateId, string context, int? resourceNumber = null, CancellationToken ct = default)
    {
        if (!_config.RequiresHuman(gateId))
        {
            _logger.LogDebug("Gate {GateId} does not require human approval, proceeding", gateId);
            return GateResult.Proceed;
        }

        // Already approved locally (e.g., pre-approved via dashboard before gate was hit)
        if (_localApprovals.ContainsKey(gateId))
        {
            _logger.LogInformation("Gate {GateId} already approved locally, proceeding", gateId);
            return GateResult.Proceed;
        }

        var gateName = GetGateName(gateId);
        _logger.LogInformation(
            "Gate {GateId} ({GateName}) requires human approval: {Context} (resource: {Resource})",
            gateId, gateName, context, resourceNumber?.ToString() ?? "none — use dashboard/API to approve");

        if (resourceNumber.HasValue)
        {
            try
            {
                var pr = await _github.GetPullRequestAsync(resourceNumber.Value, ct);
                if (pr is not null)
                {
                    var labels = pr.Labels?.ToList() ?? new List<string>();
                    if (!labels.Contains(AwaitingHumanLabel))
                    {
                        labels.Add(AwaitingHumanLabel);
                        await _github.UpdatePullRequestAsync(resourceNumber.Value, labels: labels.ToArray(), ct: ct);
                    }

                    var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                        $"This PR is paused at gate `{gateId}` and requires human approval before proceeding.\n\n" +
                        $"**What needs review:** {context}\n\n" +
                        $"**To approve:** Add a comment with `approved` or add the `{HumanApprovedLabel}` label.\n" +
                        $"**To request changes:** Add a comment describing the changes needed.";
                    await _github.AddPullRequestCommentAsync(resourceNumber.Value, comment, ct);
                }
                else
                {
                    var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                        $"This issue is paused at gate `{gateId}` and requires human approval.\n\n" +
                        $"**What needs review:** {context}\n\n" +
                        $"**To approve:** Add a comment with `approved`.";
                    await _github.AddIssueCommentAsync(resourceNumber.Value, comment, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add gate notification for {GateId} on #{Number}",
                    gateId, resourceNumber.Value);
            }
        }

        // Notify via dashboard + any enabled channels
        if (_notificationService is not null)
        {
            try
            {
                await _notificationService.AddNotificationAsync(gateId, context, resourceNumber, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch gate notification for {GateId}", gateId);
            }
        }

        return GateResult.WaitingForHuman;
    }

    public async Task<bool> IsGateApprovedAsync(
        string gateId, int resourceNumber, CancellationToken ct = default)
    {
        if (!_config.RequiresHuman(gateId))
            return true;

        // Always check local approvals first (fast, no API call)
        if (_localApprovals.ContainsKey(gateId))
        {
            _logger.LogInformation("Gate {GateId} approved locally (while polling PR #{Number})", gateId, resourceNumber);
            _notificationService?.Resolve(gateId, resourceNumber);
            return true;
        }

        try
        {
            var pr = await _github.GetPullRequestAsync(resourceNumber, ct);
            if (pr?.Labels?.Contains(HumanApprovedLabel) == true)
            {
                _logger.LogInformation("Gate {GateId} approved via label on PR #{Number}", gateId, resourceNumber);
                _notificationService?.Resolve(gateId, resourceNumber);
                return true;
            }

            var comments = await _github.GetPullRequestCommentsAsync(resourceNumber, ct);
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;

                var body = comment.Body?.Trim().ToLowerInvariant() ?? "";
                if (body.Contains("approved") || body.Contains("lgtm") || body.Contains("ship it"))
                {
                    _logger.LogInformation("Gate {GateId} approved via comment on PR #{Number}", gateId, resourceNumber);
                    _notificationService?.Resolve(gateId, resourceNumber);

                    if (pr is not null)
                    {
                        var labels = pr.Labels?.ToList() ?? new List<string>();
                        labels.Remove(AwaitingHumanLabel);
                        if (!labels.Contains(HumanApprovedLabel))
                            labels.Add(HumanApprovedLabel);
                        await _github.UpdatePullRequestAsync(resourceNumber, labels: labels.ToArray(), ct: ct);
                    }

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking gate approval for {GateId} on #{Number}",
                gateId, resourceNumber);
        }

        return false;
    }

    public void ApproveGate(string gateId)
    {
        if (_localApprovals.TryAdd(gateId, DateTime.UtcNow))
        {
            _logger.LogInformation("Gate {GateId} approved locally via dashboard/API", gateId);
            _notificationService?.Resolve(gateId);
        }
        else
        {
            _logger.LogDebug("Gate {GateId} was already approved locally", gateId);
        }
    }

    public bool IsGateApprovedLocally(string gateId) => _localApprovals.ContainsKey(gateId);

    public async Task<GateStatus> GetGateStatusAsync(
        string gateId, int resourceNumber, CancellationToken ct = default)
    {
        if (!_config.RequiresHuman(gateId))
            return GateStatus.Approved; // gate not active → treat as approved

        if (_localApprovals.ContainsKey(gateId))
            return GateStatus.Approved;

        try
        {
            var pr = await _github.GetPullRequestAsync(resourceNumber, ct);
            if (pr is null)
                return GateStatus.NotActivated;

            // PR already merged → gate was approved in a prior run
            if (pr.IsMerged)
                return GateStatus.Approved;

            var labels = pr.Labels?.ToList() ?? new List<string>();

            if (labels.Contains(HumanApprovedLabel))
                return GateStatus.Approved;

            if (labels.Contains(AwaitingHumanLabel))
                return GateStatus.AwaitingApproval;

            // Check comments for approval
            var comments = await _github.GetPullRequestCommentsAsync(resourceNumber, ct);
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;
                var body = comment.Body?.Trim().ToLowerInvariant() ?? "";
                if (body.Contains("approved") || body.Contains("lgtm") || body.Contains("ship it"))
                    return GateStatus.Approved;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking gate status for {GateId} on #{Number}", gateId, resourceNumber);
        }

        return GateStatus.NotActivated;
    }

    /// <summary>Get all pending (non-approved) gates that require human approval.</summary>
    public IReadOnlyList<PendingGateInfo> GetPendingGates()
    {
        var pending = new List<PendingGateInfo>();
        foreach (var (_, id, name, description) in GateIds.AllGates)
        {
            if (_config.RequiresHuman(id) && !_localApprovals.ContainsKey(id))
            {
                pending.Add(new PendingGateInfo(id, name, description));
            }
        }
        return pending;
    }

    /// <summary>Get all locally-approved gates with timestamps.</summary>
    public IReadOnlyDictionary<string, DateTime> GetApprovedGates() =>
        new Dictionary<string, DateTime>(_localApprovals);

    private static string GetGateName(string gateId)
    {
        foreach (var (_, id, name, _) in GateIds.AllGates)
        {
            if (id == gateId) return name;
        }
        return gateId;
    }
}

/// <summary>Info about a gate that is configured for human approval but not yet approved.</summary>
public record PendingGateInfo(string GateId, string GateName, string Description);
