using System.Collections.Concurrent;
using System.Text.Json;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Core.Configuration;

/// <summary>
/// Evaluates human interaction gates at workflow touchpoints.
/// Uses AI to assess human comments for approval/rejection intent — no hardcoded keyword matching.
/// </summary>
public class GateCheckService : IGateCheckService
{
    private const string AwaitingHumanLabel = "awaiting-human-review";
    private const string HumanApprovedLabel = "human-approved";
    private const string GateCommentPrefix = "🚦 **Human Review Gate";

    private readonly ConcurrentDictionary<string, DateTime> _localApprovals = new();

    private readonly HumanInteractionConfig _config;
    private readonly IGitHubService _github;
    private readonly GateNotificationService? _notificationService;
    private readonly ModelRegistry? _modelRegistry;
    private readonly ILogger<GateCheckService> _logger;

    public GateCheckService(
        IOptions<AgentSquadConfig> config,
        IGitHubService github,
        ILogger<GateCheckService> logger,
        GateNotificationService? notificationService = null,
        ModelRegistry? modelRegistry = null)
    {
        _config = config.Value.HumanInteraction;
        _github = github;
        _logger = logger;
        _notificationService = notificationService;
        _modelRegistry = modelRegistry;
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

                    // Only post the gate comment once (avoid duplicates on restart)
                    var existingComments = await _github.GetPullRequestCommentsAsync(resourceNumber.Value, ct);
                    var hasGateComment = existingComments.Any(c => c.Body?.Contains(GateCommentPrefix) == true
                        && c.Body.Contains(gateId));
                    if (!hasGateComment)
                    {
                        var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                            $"This PR is paused at gate `{gateId}` and requires human approval before proceeding.\n\n" +
                            $"**What needs review:** {context}\n\n" +
                            $"**To approve:** Add a comment with `approved` or add the `{HumanApprovedLabel}` label.\n" +
                            $"**To request changes:** Add a comment describing the changes needed.";
                        await _github.AddPullRequestCommentAsync(resourceNumber.Value, comment, ct);
                    }
                }
                else
                {
                    var existingComments = await _github.GetIssueCommentsAsync(resourceNumber.Value, ct);
                    var hasGateComment = existingComments.Any(c => c.Body?.Contains(GateCommentPrefix) == true
                        && c.Body.Contains(gateId));
                    if (!hasGateComment)
                    {
                        var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                            $"This issue is paused at gate `{gateId}` and requires human approval.\n\n" +
                            $"**What needs review:** {context}\n\n" +
                            $"**To approve:** Add a comment with `approved`.";
                        await _github.AddIssueCommentAsync(resourceNumber.Value, comment, ct);
                    }
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

    public async Task<GateCommentAssessment> AssessGateApprovalAsync(
        string gateId, int resourceNumber, CancellationToken ct = default)
    {
        if (!_config.RequiresHuman(gateId))
            return new GateCommentAssessment(GateDecision.Approved);

        if (_localApprovals.ContainsKey(gateId))
        {
            _logger.LogInformation("Gate {GateId} approved locally (while polling PR #{Number})", gateId, resourceNumber);
            _notificationService?.Resolve(gateId, resourceNumber);
            return new GateCommentAssessment(GateDecision.Approved);
        }

        try
        {
            var pr = await _github.GetPullRequestAsync(resourceNumber, ct);
            if (pr?.Labels?.Contains(HumanApprovedLabel) == true)
            {
                _logger.LogInformation("Gate {GateId} approved via label on PR #{Number}", gateId, resourceNumber);
                _notificationService?.Resolve(gateId, resourceNumber);
                return new GateCommentAssessment(GateDecision.Approved);
            }

            var comments = await _github.GetPullRequestCommentsAsync(resourceNumber, ct);

            // Find the most recent non-bot human comment (skip gate notification comments)
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;
                var body = comment.Body?.Trim() ?? "";
                if (string.IsNullOrEmpty(body)) continue;

                // Use AI to assess the comment intent
                var assessment = await AssessCommentWithAIAsync(body, ct);

                if (assessment.Decision == GateDecision.Approved)
                {
                    _logger.LogInformation("Gate {GateId} approved via AI assessment on PR #{Number}", gateId, resourceNumber);
                    _notificationService?.Resolve(gateId, resourceNumber);

                    if (pr is not null)
                    {
                        var labels = pr.Labels?.ToList() ?? new List<string>();
                        labels.Remove(AwaitingHumanLabel);
                        if (!labels.Contains(HumanApprovedLabel))
                            labels.Add(HumanApprovedLabel);
                        await _github.UpdatePullRequestAsync(resourceNumber, labels: labels.ToArray(), ct: ct);
                    }

                    return assessment;
                }

                if (assessment.Decision == GateDecision.Rejected)
                {
                    _logger.LogInformation(
                        "Gate {GateId} REJECTED via AI assessment on PR #{Number}: {Feedback}",
                        gateId, resourceNumber, assessment.Feedback ?? "(no feedback)");
                    return assessment;
                }

                // If Pending/unclear, check next comment
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking gate approval for {GateId} on #{Number}",
                gateId, resourceNumber);
        }

        return new GateCommentAssessment(GateDecision.Pending);
    }

    public async Task<bool> IsGateApprovedAsync(
        string gateId, int resourceNumber, CancellationToken ct = default)
    {
        var assessment = await AssessGateApprovalAsync(gateId, resourceNumber, ct);
        return assessment.Decision == GateDecision.Approved;
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
            return GateStatus.Approved;

        if (_localApprovals.ContainsKey(gateId))
            return GateStatus.Approved;

        try
        {
            var pr = await _github.GetPullRequestAsync(resourceNumber, ct);
            if (pr is null)
                return GateStatus.NotActivated;

            if (pr.IsMerged)
                return GateStatus.Approved;

            var labels = pr.Labels?.ToList() ?? new List<string>();

            if (labels.Contains(HumanApprovedLabel))
                return GateStatus.Approved;

            if (labels.Contains(AwaitingHumanLabel))
                return GateStatus.AwaitingApproval;

            // Check comments using AI assessment
            var comments = await _github.GetPullRequestCommentsAsync(resourceNumber, ct);
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;
                var body = comment.Body?.Trim() ?? "";
                if (string.IsNullOrEmpty(body)) continue;

                var assessment = await AssessCommentWithAIAsync(body, ct);
                if (assessment.Decision == GateDecision.Approved)
                    return GateStatus.Approved;
                // Rejected = still awaiting (agent needs to revise)
                if (assessment.Decision == GateDecision.Rejected)
                    return GateStatus.AwaitingApproval;
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

    /// <summary>
    /// Use AI to assess whether a human comment indicates approval, rejection, or is unrelated.
    /// Falls back to simple heuristics if AI is unavailable.
    /// </summary>
    private async Task<GateCommentAssessment> AssessCommentWithAIAsync(string commentBody, CancellationToken ct)
    {
        if (_modelRegistry is null)
        {
            // Fallback: simple heuristics when AI is not available (e.g., standalone dashboard)
            return AssessCommentWithHeuristics(commentBody);
        }

        try
        {
            var kernel = _modelRegistry.GetKernel("budget");
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(
                """
                You are a gate-approval classifier. A human left a comment on a pull request that is paused for review.
                Determine whether the comment is:
                1. APPROVED — the human is satisfied and wants to proceed (e.g., "approved", "lgtm", "ship it", "looks good")
                2. REJECTED — the human is NOT satisfied and wants changes (e.g., "not approved", "needs work", "please fix X", "change Y to Z", or any comment providing critical feedback/instructions for revision)
                3. UNCLEAR — the comment is unrelated, a question, or doesn't express a clear approval/rejection

                IMPORTANT: "Not approved" or "I don't approve" means REJECTED, not APPROVED.
                Any comment that provides specific guidance on what to change is REJECTED with that guidance as feedback.

                Respond with ONLY a JSON object (no markdown, no code fences):
                {"decision": "approved|rejected|unclear", "feedback": "extracted feedback if rejected, null otherwise"}
                """);
            history.AddUserMessage($"Comment:\n{commentBody}");

            var response = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var responseText = response.FirstOrDefault()?.Content?.Trim() ?? "";

            // Strip markdown code fences if present
            if (responseText.StartsWith("```"))
            {
                var lines = responseText.Split('\n');
                responseText = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            // Parse JSON response
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            var decision = root.GetProperty("decision").GetString()?.ToLowerInvariant();
            var feedback = root.TryGetProperty("feedback", out var fb) ? fb.GetString() : null;

            _logger.LogDebug("AI gate assessment: decision={Decision}, feedback={Feedback}", decision, feedback);

            return decision switch
            {
                "approved" => new GateCommentAssessment(GateDecision.Approved),
                "rejected" => new GateCommentAssessment(GateDecision.Rejected, feedback ?? commentBody),
                _ => new GateCommentAssessment(GateDecision.Pending),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI gate assessment failed, falling back to heuristics");
            return AssessCommentWithHeuristics(commentBody);
        }
    }

    /// <summary>Fallback heuristic assessment when AI is unavailable.</summary>
    private static GateCommentAssessment AssessCommentWithHeuristics(string commentBody)
    {
        var lower = commentBody.Trim().ToLowerInvariant();

        // Check rejection patterns FIRST (before approval, since "not approved" contains "approved")
        if (lower.Contains("not approved") || lower.Contains("don't approve") || lower.Contains("do not approve")
            || lower.Contains("rejected") || lower.Contains("needs work") || lower.Contains("changes requested")
            || lower.Contains("please fix") || lower.Contains("please change") || lower.Contains("not ready")
            || lower.StartsWith("no,") || lower.StartsWith("no.") || lower == "no")
        {
            return new GateCommentAssessment(GateDecision.Rejected, commentBody);
        }

        // Then check approval patterns
        if (lower.Contains("approved") || lower.Contains("lgtm") || lower.Contains("ship it")
            || lower.Contains("looks good") || lower == "yes" || lower.StartsWith("yes,") || lower.StartsWith("yes."))
        {
            return new GateCommentAssessment(GateDecision.Approved);
        }

        return new GateCommentAssessment(GateDecision.Pending);
    }
}

/// <summary>Info about a gate that is configured for human approval but not yet approved.</summary>
public record PendingGateInfo(string GateId, string GateName, string Description);
