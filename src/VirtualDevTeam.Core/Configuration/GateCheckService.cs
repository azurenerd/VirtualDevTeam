using System.Collections.Concurrent;
using System.Text.Json;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Evaluates human interaction gates at workflow touchpoints.
/// Uses AI to assess human comments for approval/rejection intent — no hardcoded keyword matching.
/// </summary>
public class GateCheckService : IGateCheckService
{
    private const string AwaitingHumanLabel = "awaiting-human-review";
    private const string HumanApprovedLabel = "human-approved";
    private const string GateCommentPrefix = "🚦 **Human Review Gate";
    private const string ReworkCommentPrefix = "🔄 **Human Review — Rework Requested**";
    private const string RevisedCommentPrefix = "📝 **Revised**";

    private readonly ConcurrentDictionary<string, DateTime> _localApprovals = new();
    private readonly ConcurrentDictionary<string, GateRejection> _localRejections = new();
    /// <summary>
    /// Cumulative iteration count per (gateId, resourceNumber) key. Survives a
    /// rejection being cleared by the agent re-gating after rework so that the
    /// next rejection on the same key continues numbering (iteration 2, 3, ...).
    /// Only cleared when the gate is fully approved.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _reworkIterationCounts = new();
    private readonly SemaphoreSlim _gateSignal = new(0, int.MaxValue);

    private readonly VirtualDevTeamConfig _config;
    private readonly IPullRequestService _prService;
    private readonly IReviewService _reviewService;
    private readonly IWorkItemService _workItemService;
    private readonly GateNotificationService? _notificationService;
    private readonly IChatCompletionRunner? _chatRunner;
    private readonly AgentStateStore? _stateStore;
    private readonly DevelopSettingsService? _developSettings;
    private readonly ILogger<GateCheckService> _logger;

    // Read from the same singleton instance that RunCoordinator.MergeIntoConfig mutates.
    // Using IOptionsMonitor would create a separate instance that never sees develop-settings.json overrides.
    private HumanInteractionConfig Config => _config.HumanInteraction;

    public GateCheckService(
        IOptions<VirtualDevTeamConfig> config,
        IPullRequestService prService,
        IReviewService reviewService,
        IWorkItemService workItemService,
        ILogger<GateCheckService> logger,
        GateNotificationService? notificationService = null,
        IChatCompletionRunner? chatRunner = null,
        AgentStateStore? stateStore = null,
        DevelopSettingsService? developSettings = null)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _prService = prService;
        _reviewService = reviewService;
        _workItemService = workItemService;
        _logger = logger;
        _notificationService = notificationService;
        _chatRunner = chatRunner;
        _stateStore = stateStore;
        _developSettings = developSettings;
        RestoreApprovals();
    }

    /// <summary>Build a dictionary key scoped to a specific resource when provided.</summary>
    private static string MakeLocalKey(string gateId, int? resourceNumber) =>
        resourceNumber.HasValue ? $"{gateId}:{resourceNumber}" : gateId;

    /// <summary>
    /// Check for a local approval. When resourceNumber is provided, only the
    /// resource-scoped key is checked — no fallback to the global key.
    /// This prevents approvals for one PR from leaking to another.
    /// </summary>
    private bool TryGetLocalApproval(string gateId, int? resourceNumber)
    {
        var key = MakeLocalKey(gateId, resourceNumber);
        return _localApprovals.ContainsKey(key);
    }

    /// <summary>
    /// Check for a local rejection. When resourceNumber is provided, only the
    /// resource-scoped key is checked — no fallback to the global key.
    /// </summary>
    private GateRejection? TryGetLocalRejection(string gateId, int? resourceNumber)
    {
        var key = MakeLocalKey(gateId, resourceNumber);
        return _localRejections.TryGetValue(key, out var rejection) ? rejection : null;
    }

    public bool IsEnabled => !AreAllGatesDisabled();

    /// <summary>
    /// Returns <c>true</c> when every gate is unconditionally disabled — i.e. the
    /// wizard's master switch in <c>develop-settings.json</c> says
    /// <c>gatePreferences.enabled = false</c> OR the appsettings.json
    /// <c>HumanInteraction.Enabled</c> is <c>false</c>. When this is <c>true</c>,
    /// individual per-gate <c>RequiresHuman</c> flags are ignored and every gate
    /// auto-passes. Develop-settings is consulted FIRST; appsettings is only used
    /// as a fallback when develop-settings is unavailable or doesn't define
    /// <c>gatePreferences</c>.
    /// </summary>
    public bool AreAllGatesDisabled()
    {
        // Source 1 (preferred): wizard config — develop-settings.json. If the wizard
        // explicitly disabled the master switch, no per-gate flag can override it.
        var wizardPrefs = _developSettings?.Current?.GatePreferences;
        if (wizardPrefs is { Enabled: false })
            return true;

        // Source 2 (fallback): appsettings.json — HumanInteraction.Enabled.
        if (!Config.Enabled)
            return true;

        return false;
    }

    /// <inheritdoc/>
    public void UpdateGates(HumanInteractionConfig updatedGates)
    {
        ArgumentNullException.ThrowIfNull(updatedGates);

        _config.HumanInteraction.Enabled = updatedGates.Enabled;

        foreach (var (gateId, gateConfig) in updatedGates.Gates)
        {
            if (_config.HumanInteraction.Gates.TryGetValue(gateId, out var existing))
            {
                existing.RequiresHuman = gateConfig.RequiresHuman;
            }
            else
            {
                _config.HumanInteraction.Gates[gateId] = new GateConfig { RequiresHuman = gateConfig.RequiresHuman };
            }
        }

        _logger.LogInformation("Gate configuration hot-reloaded: Enabled={Enabled}, {Count} gates updated",
            updatedGates.Enabled, updatedGates.Gates.Count);
    }

    public bool RequiresHuman(string gateId)
    {
        // Defensive: master switch off short-circuits every per-gate flag.
        if (AreAllGatesDisabled())
        {
            _logger.LogDebug(
                "Gate {GateId} resolved auto-pass: master switch is OFF (wizard.enabled={WizardEnabled}, appsettings.enabled={AppsettingsEnabled})",
                gateId,
                _developSettings?.Current?.GatePreferences?.Enabled,
                Config.Enabled);
            return false;
        }

        // Per-gate enablement: prefer develop-settings's per-gate map over appsettings.json.
        var wizardGates = _developSettings?.Current?.GatePreferences?.Gates;
        if (wizardGates is not null && wizardGates.TryGetValue(gateId, out var wizardRequiresHuman))
        {
            _logger.LogDebug(
                "Gate {GateId} resolution: source=wizard, requiresHuman={RequiresHuman}",
                gateId, wizardRequiresHuman);
            return wizardRequiresHuman;
        }

        var requires = Config.RequiresHuman(gateId);
        _logger.LogDebug(
            "Gate {GateId} resolution: source=appsettings, requiresHuman={RequiresHuman}",
            gateId, requires);
        return requires;
    }

    public async Task<GateResult> CheckGateAsync(
        string gateId, string context, int? resourceNumber = null, CancellationToken ct = default)
    {
        // Master switch (wizard or appsettings) — short-circuit BEFORE per-gate evaluation
        // so that disabling all gates from the wizard cannot be undone by a stale
        // per-gate flag in appsettings.json.
        if (AreAllGatesDisabled())
        {
            _logger.LogInformation(
                "Gate {GateId} auto-passed: all gates disabled by master switch (wizard.enabled={WizardEnabled}, appsettings.enabled={AppsettingsEnabled})",
                gateId,
                _developSettings?.Current?.GatePreferences?.Enabled,
                Config.Enabled);
            return GateResult.Proceed;
        }

        if (!RequiresHuman(gateId))
        {
            _logger.LogDebug("Gate {GateId} does not require human approval, proceeding", gateId);
            return GateResult.Proceed;
        }

        _logger.LogInformation(
            "Gate {GateId} requires human review (resource={Resource}) — entering wait flow",
            gateId, resourceNumber?.ToString() ?? "global");

        // Already approved locally (e.g., pre-approved via dashboard before gate was hit)
        if (TryGetLocalApproval(gateId, resourceNumber))
        {
            _logger.LogInformation("Gate {GateId} already approved locally (resource: {Resource}), proceeding",
                gateId, resourceNumber?.ToString() ?? "global");
            return GateResult.Proceed;
        }

        // Clear any previous rejection for this gate+resource (agent re-gating after rework)
        var rejectKey = MakeLocalKey(gateId, resourceNumber);
        if (_localRejections.TryRemove(rejectKey, out _))
        {
            _logger.LogInformation("Cleared previous rejection for gate {GateId} (resource: {Resource}) — agent re-gating after rework",
                gateId, resourceNumber?.ToString() ?? "global");
            _stateStore?.DeleteGateRejection(rejectKey);
        }

        var gateName = GetGateName(gateId);
        _logger.LogInformation(
            "Gate {GateId} ({GateName}) requires human approval: {Context} (resource: {Resource})",
            gateId, gateName, context, resourceNumber?.ToString() ?? "none — use dashboard/API to approve");

        if (resourceNumber.HasValue)
        {
            try
            {
                var pr = await _prService.GetAsync(resourceNumber.Value, ct);
                if (pr is not null)
                {
                    var labels = pr.Labels?.ToList() ?? new List<string>();
                    if (!labels.Contains(AwaitingHumanLabel))
                    {
                        labels.Add(AwaitingHumanLabel);
                        await _prService.UpdateAsync(resourceNumber.Value, labels: labels, ct: ct);
                    }

                    // Only post the gate comment once (avoid duplicates on restart)
                    var existingComments = await _reviewService.GetCommentsAsync(resourceNumber.Value, ct);
                    var hasGateComment = existingComments.Any(c => c.Body?.Contains(GateCommentPrefix) == true
                        && c.Body.Contains(gateId));
                    if (!hasGateComment)
                    {
                        var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                            $"This PR is paused at gate `{gateId}` and requires human approval before proceeding.\n\n" +
                            $"**What needs review:** {context}\n\n" +
                            $"**To approve:** Add a comment with `approved` or add the `{HumanApprovedLabel}` label.\n" +
                            $"**To request changes:** Add a comment describing the changes needed.";
                        await _reviewService.AddCommentAsync(resourceNumber.Value, comment, ct);
                    }
                }
                else
                {
                    // Resource is a work item / issue (not a PR)
                    var existingComments = await _workItemService.GetCommentsAsync(resourceNumber.Value, ct);
                    var hasGateComment = existingComments.Any(c => c.Body?.Contains(GateCommentPrefix) == true
                        && c.Body.Contains(gateId));
                    if (!hasGateComment)
                    {
                        var comment = $"{GateCommentPrefix}: {gateName}**\n\n" +
                            $"This item is paused at gate `{gateId}` and requires human approval.\n\n" +
                            $"**What needs review:** {context}\n\n" +
                            $"**To approve:** Add a comment with `approved`.";
                        await _workItemService.AddCommentAsync(resourceNumber.Value, comment, ct);
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
        if (!RequiresHuman(gateId))
            return new GateCommentAssessment(GateDecision.Approved);

        // Check local approval (resource-scoped, then global fallback)
        if (TryGetLocalApproval(gateId, resourceNumber))
        {
            _logger.LogInformation("Gate {GateId} approved locally (while polling PR #{Number})", gateId, resourceNumber);
            _notificationService?.Resolve(gateId, resourceNumber);

            // Update PR labels to reflect dashboard approval
            try
            {
                var pr = await _prService.GetAsync(resourceNumber, ct);
                if (pr is not null)
                {
                    var labels = pr.Labels?.ToList() ?? new List<string>();
                    labels.Remove(AwaitingHumanLabel);
                    if (!labels.Contains(HumanApprovedLabel))
                        labels.Add(HumanApprovedLabel);
                    await _prService.UpdateAsync(resourceNumber, labels: labels, ct: ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update labels after local approval for gate {GateId} on #{Number}", gateId, resourceNumber);
            }

            return new GateCommentAssessment(GateDecision.Approved);
        }

        // Check local rejection (resource-scoped, then global fallback)
        var rejection = TryGetLocalRejection(gateId, resourceNumber);
        if (rejection is not null)
        {
            _logger.LogInformation("Gate {GateId} rejected locally (while polling PR #{Number}): {Feedback}",
                gateId, resourceNumber, rejection.Feedback ?? "(no feedback)");
            return new GateCommentAssessment(GateDecision.Rejected, rejection.Feedback);
        }

        try
        {
            var pr = await _prService.GetAsync(resourceNumber, ct);
            if (pr?.Labels?.Contains(HumanApprovedLabel) == true)
            {
                _logger.LogInformation("Gate {GateId} approved via label on PR #{Number}", gateId, resourceNumber);
                _notificationService?.Resolve(gateId, resourceNumber);
                return new GateCommentAssessment(GateDecision.Approved);
            }

            var comments = await _reviewService.GetCommentsAsync(resourceNumber, ct);

            // Find the most recent non-bot human comment (skip gate notification comments
            // and bot-posted rework/revision comments to prevent dual-signal duplication)
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;
                if (comment.Body?.Contains(ReworkCommentPrefix) == true) continue;
                if (comment.Body?.Contains(RevisedCommentPrefix) == true) continue;
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
                        await _prService.UpdateAsync(resourceNumber, labels: labels, ct: ct);
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

    public void ApproveGate(string gateId, int? resourceNumber = null)
    {
        var key = MakeLocalKey(gateId, resourceNumber);
        if (_localApprovals.TryAdd(key, DateTime.UtcNow))
        {
            _logger.LogInformation("Gate {GateId} approved locally via dashboard/API (resource: {Resource})",
                gateId, resourceNumber?.ToString() ?? "global");
            _stateStore?.SaveGateApproval(key, DateTime.UtcNow);
            _notificationService?.Resolve(gateId, resourceNumber);

            // Clear any pending rejection for the same key (approval overrides rejection)
            if (_localRejections.TryRemove(key, out _))
                _stateStore?.DeleteGateRejection(key);

            // Approval ends the rework loop for this gate+resource — reset cumulative
            // iteration counter so a future rejection on a fresh activation starts from 1.
            _reworkIterationCounts.TryRemove(key, out _);

            SignalWaiters();
        }
        else
        {
            _logger.LogDebug("Gate {GateId} was already approved locally (key: {Key})", gateId, key);
        }
    }

    public void RejectGate(string gateId, string? feedback = null, int? resourceNumber = null)
    {
        var key = MakeLocalKey(gateId, resourceNumber);

        // Cumulative iteration: ++ from whichever source has the most-recent value.
        // _localRejections has it when the operator is double-rejecting before the agent
        // could re-gate; _reworkIterationCounts has it when the agent already re-gated
        // after a previous rejection and the operator is now starting iteration N+1.
        var previousCount =
            _localRejections.TryGetValue(key, out var existingRejection)
                ? existingRejection.IterationCount
                : _reworkIterationCounts.GetValueOrDefault(key, 0);
        var nextIteration = previousCount + 1;
        _reworkIterationCounts[key] = nextIteration;

        var rejection = new GateRejection(gateId, feedback, resourceNumber, DateTime.UtcNow, nextIteration);
        _localRejections[key] = rejection; // Overwrite if already rejected (latest feedback wins)
        _logger.LogInformation(
            "Gate {GateId} rejected locally via dashboard (resource: {Resource}, iteration: {Iteration}): {Feedback}",
            gateId, resourceNumber?.ToString() ?? "global", nextIteration, feedback ?? "(no feedback)");
        _stateStore?.SaveGateRejection(key, feedback, DateTime.UtcNow, nextIteration);

        // Clear any pending approval for the same key (rejection overrides approval)
        if (_localApprovals.TryRemove(key, out _))
            _stateStore?.DeleteGateApproval(key);

        SignalWaiters();

        // Post rejection feedback as a comment on the associated PR/work item
        if (resourceNumber.HasValue && !string.IsNullOrWhiteSpace(feedback))
        {
            _ = PostRejectionCommentAsync(gateId, resourceNumber.Value, feedback);
        }
    }

    /// <summary>
    /// Best-effort: post rejection feedback as a PR/work item comment so agents can see it.
    /// Fire-and-forget — dashboard should not block on this.
    /// </summary>
    private async Task PostRejectionCommentAsync(string gateId, int resourceNumber, string feedback)
    {
        try
        {
            var gateName = GetGateName(gateId);
            var comment = $"🔄 **Human Review — Rework Requested** ({gateName})\n\n{feedback}";

            // Try PR first; fall back to work item
            var pr = await _prService.GetAsync(resourceNumber);
            if (pr is not null)
                await _reviewService.AddCommentAsync(resourceNumber, comment);
            else
                await _workItemService.AddCommentAsync(resourceNumber, comment);

            _logger.LogInformation("Posted rejection feedback to PR/Item #{ResourceNumber} for gate {GateId}",
                resourceNumber, gateId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post rejection comment to #{ResourceNumber} (best-effort)",
                resourceNumber);
        }
    }

    public GateRejection? GetLocalRejection(string gateId, int? resourceNumber = null) =>
        TryGetLocalRejection(gateId, resourceNumber);

    /// <inheritdoc/>
    public ReworkInFlightState? GetReworkInFlight(string gateId, int? resourceNumber = null)
    {
        var rejection = TryGetLocalRejection(gateId, resourceNumber);
        if (rejection is null) return null;
        return new ReworkInFlightState(
            GateId: rejection.GateId,
            ResourceNumber: rejection.ResourceNumber,
            RequestedAt: rejection.RejectedAt,
            Feedback: rejection.Feedback,
            IterationCount: rejection.IterationCount);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ReworkInFlightState> GetAllReworkInFlight()
    {
        // Snapshot to avoid enumeration-mutation races. _localRejections is a
        // ConcurrentDictionary but we still want a stable list for the caller.
        var snapshot = _localRejections.Values.ToArray();
        var result = new List<ReworkInFlightState>(snapshot.Length);
        foreach (var r in snapshot)
        {
            result.Add(new ReworkInFlightState(
                GateId: r.GateId,
                ResourceNumber: r.ResourceNumber,
                RequestedAt: r.RejectedAt,
                Feedback: r.Feedback,
                IterationCount: r.IterationCount));
        }
        return result;
    }

    public bool IsGateApprovedLocally(string gateId, int? resourceNumber = null) =>
        TryGetLocalApproval(gateId, resourceNumber);

    public async Task<GateStatus> GetGateStatusAsync(
        string gateId, int resourceNumber, CancellationToken ct = default)
    {
        if (!RequiresHuman(gateId))
            return GateStatus.NotActivated;

        if (TryGetLocalApproval(gateId, resourceNumber))
            return GateStatus.Approved;

        try
        {
            var pr = await _prService.GetAsync(resourceNumber, ct);
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
            var comments = await _reviewService.GetCommentsAsync(resourceNumber, ct);
            foreach (var comment in comments.Reverse())
            {
                if (comment.Body?.Contains(GateCommentPrefix) == true) continue;
                if (comment.Body?.Contains(ReworkCommentPrefix) == true) continue;
                if (comment.Body?.Contains(RevisedCommentPrefix) == true) continue;
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
            if (RequiresHuman(id) && !TryGetLocalApproval(id, null))
            {
                pending.Add(new PendingGateInfo(id, name, description));
            }
        }
        return pending;
    }

    /// <summary>Get all locally-approved gates with timestamps.</summary>
    public IReadOnlyDictionary<string, DateTime> GetApprovedGates() =>
        new Dictionary<string, DateTime>(_localApprovals);

    /// <summary>
    /// Signal all agents waiting on gate polls to wake up immediately.
    /// Called after ApproveGate/RejectGate to eliminate polling delay.
    /// </summary>
    private void SignalWaiters()
    {
        // Release enough permits for all potential waiters.
        // CurrentCount resets naturally as waiters consume permits.
        try { _gateSignal.Release(100); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Wait for a gate signal or timeout. Used by WaitForGateAsync to respond
    /// instantly to dashboard approvals instead of blind polling.
    /// </summary>
    public async Task WaitForSignalAsync(int timeoutSeconds, CancellationToken ct = default)
    {
        // Wait for either a signal (instant) or timeout (fallback to poll GitHub)
        await _gateSignal.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
    }

    private static string GetGateName(string gateId)
    {
        foreach (var (_, id, name, _) in GateIds.AllGates)
        {
            if (id == gateId) return name;
        }
        return gateId;
    }

    private void RestoreApprovals()
    {
        if (_stateStore is null) return;
        try
        {
            var savedApprovals = _stateStore.LoadGateApprovals();
            foreach (var (gateId, approvedAt) in savedApprovals)
            {
                _localApprovals.TryAdd(gateId, approvedAt);
            }
            if (savedApprovals.Count > 0)
                _logger.LogInformation("Restored {Count} gate approval(s) from SQLite", savedApprovals.Count);

            var savedRejections = _stateStore.LoadGateRejections();
            foreach (var (key, feedback, rejectedAt, iterationCount) in savedRejections)
            {
                // Parse key to extract gateId and resourceNumber
                var parts = key.Split(':', 2);
                var gateId = parts[0];
                int? resourceNumber = parts.Length > 1 && int.TryParse(parts[1], out var rn) ? rn : null;
                // Restored rows may pre-date the iteration_count column (defaulted to 0
                // by the migration). Floor at 1 so the dashboard always shows a sensible
                // "Rework iteration N" badge.
                var iter = iterationCount > 0 ? iterationCount : 1;
                _localRejections.TryAdd(key, new GateRejection(gateId, feedback, resourceNumber, rejectedAt, iter));
                _reworkIterationCounts[key] = iter;
            }
            if (savedRejections.Count > 0)
                _logger.LogInformation("Restored {Count} gate rejection(s) from SQLite", savedRejections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore gate approvals/rejections from SQLite");
        }
    }

    /// <summary>
    /// Use AI to assess whether a human comment indicates approval, rejection, or is unrelated.
    /// Falls back to simple heuristics if AI is unavailable.
    /// </summary>
    private async Task<GateCommentAssessment> AssessCommentWithAIAsync(string commentBody, CancellationToken ct)
    {
        if (_chatRunner is null)
        {
            // Fallback: simple heuristics when AI is not available (e.g., standalone dashboard)
            return AssessCommentWithHeuristics(commentBody);
        }

        try
        {
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

            var responseText = (await _chatRunner.InvokeAsync(new ChatCompletionRequest
            {
                History = history,
                ModelTier = "budget"
            }, ct)).Trim();

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
