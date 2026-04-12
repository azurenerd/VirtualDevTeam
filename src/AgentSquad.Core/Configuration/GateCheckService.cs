using AgentSquad.Core.GitHub;
using AgentSquad.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Configuration;

/// <summary>
/// Evaluates human interaction gates at workflow touchpoints.
/// When a gate requires human approval, adds a GitHub label and posts a comment.
/// When gates are disabled or set to auto, returns Proceed immediately.
/// </summary>
public class GateCheckService : IGateCheckService
{
    private const string AwaitingHumanLabel = "awaiting-human-review";
    private const string HumanApprovedLabel = "human-approved";
    private const string GateCommentPrefix = "🚦 **Human Review Gate";

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

        var gateName = GetGateName(gateId);
        _logger.LogInformation("Gate {GateId} ({GateName}) requires human approval: {Context}",
            gateId, gateName, context);

        if (resourceNumber.HasValue)
        {
            try
            {
                // Add awaiting-human-review label to the PR/issue
                var pr = await _github.GetPullRequestAsync(resourceNumber.Value, ct);
                if (pr is not null)
                {
                    var labels = pr.Labels?.ToList() ?? new List<string>();
                    if (!labels.Contains(AwaitingHumanLabel))
                    {
                        labels.Add(AwaitingHumanLabel);
                        await _github.UpdatePullRequestAsync(resourceNumber.Value, labels: labels.ToArray(), ct: ct);
                    }

                    // Post a comment explaining what needs human review
                    var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                        $"This PR is paused at gate `{gateId}` and requires human approval before proceeding.\n\n" +
                        $"**What needs review:** {context}\n\n" +
                        $"**To approve:** Add a comment with `approved` or add the `{HumanApprovedLabel}` label.\n" +
                        $"**To request changes:** Add a comment describing the changes needed.";
                    await _github.AddPullRequestCommentAsync(resourceNumber.Value, comment, ct);
                }
                else
                {
                    // Try as issue
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
            return true; // Gate doesn't require human, always "approved"

        try
        {
            // Check for human-approved label on PR
            var pr = await _github.GetPullRequestAsync(resourceNumber, ct);
            if (pr?.Labels?.Contains(HumanApprovedLabel) == true)
            {
                _logger.LogInformation("Gate {GateId} approved via label on PR #{Number}", gateId, resourceNumber);
                _notificationService?.Resolve(gateId, resourceNumber);
                return true;
            }

            // Check for approval comment
            var comments = await _github.GetPullRequestCommentsAsync(resourceNumber, ct);
            foreach (var comment in comments.Reverse())
            {
                // Skip bot comments
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;

                var body = comment.Body?.Trim().ToLowerInvariant() ?? "";
                if (body.Contains("approved") || body.Contains("lgtm") || body.Contains("ship it"))
                {
                    _logger.LogInformation("Gate {GateId} approved via comment on PR #{Number}", gateId, resourceNumber);
                    _notificationService?.Resolve(gateId, resourceNumber);

                    // Remove awaiting label, add approved label
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

    private static string GetGateName(string gateId)
    {
        foreach (var (_, id, name, _) in GateIds.AllGates)
        {
            if (id == gateId) return name;
        }
        return gateId;
    }
}
