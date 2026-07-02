using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Hosted service that watches <see cref="ReviewConfig.TestEngineerReviews"/> for hot-toggle
/// changes via <see cref="IOptionsMonitor{TOptions}.OnChange"/> and reacts:
/// <list type="bullet">
///   <item><b>false → true</b>: spawns the <see cref="AgentRole.TestEngineer"/> agent (if not present).
///         The newly-spawned TE picks up open <c>architect-approved</c> PRs on its next poll.</item>
///   <item><b>true → false</b>: stops the running TE agent and sweeps open PRs to clear TE-blocking
///         labels so PM/SE can advance any in-flight PR past the TE-tests phase.</item>
/// </list>
///
/// <para>
/// Each transition is recorded as an <see cref="AgentDecision"/> for the Reasoning page so the
/// operator can audit when/why TE participation changed mid-run. Optional dependencies on
/// <see cref="IPullRequestService"/> / <see cref="IDecisionLog"/> mean this handler degrades to a
/// no-op when the project hasn't been opened yet (no platform services bound).
/// </para>
/// </summary>
public sealed class TestEngineerToggleHandler : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly AgentRegistry _registry;
    private readonly AgentSpawnManager _spawnManager;
    private readonly ILogger<TestEngineerToggleHandler> _logger;
    private readonly IServiceProvider _serviceProvider;

    private IDisposable? _onChangeSubscription;
    private bool _lastSeenEnabled;
    private readonly object _lock = new();
    private bool _disposed;

    public TestEngineerToggleHandler(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        AgentRegistry registry,
        AgentSpawnManager spawnManager,
        ILogger<TestEngineerToggleHandler> logger,
        IServiceProvider serviceProvider)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _spawnManager = spawnManager ?? throw new ArgumentNullException(nameof(spawnManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastSeenEnabled = _config.CurrentValue.Review.TestEngineerReviews;
        _onChangeSubscription = _config.OnChange(OnConfigChanged);
        _logger.LogInformation(
            "TestEngineerToggleHandler started (initial Review.TestEngineerReviews={Enabled})",
            _lastSeenEnabled);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _onChangeSubscription?.Dispose();
        _onChangeSubscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onChangeSubscription?.Dispose();
    }

    private void OnConfigChanged(VirtualDevTeamConfig updated, string? _changeName)
    {
        try
        {
            bool newEnabled = updated.Review.TestEngineerReviews;
            bool oldEnabled;
            lock (_lock)
            {
                if (newEnabled == _lastSeenEnabled) return; // unchanged — ignore
                oldEnabled = _lastSeenEnabled;
                _lastSeenEnabled = newEnabled;
            }

            _logger.LogInformation(
                "Review.TestEngineerReviews changed from {Old} to {New} (hot-reloaded)",
                oldEnabled, newEnabled);

            // Run the side-effect work on a background task so the OnChange callback returns quickly.
            _ = Task.Run(() => HandleToggleChangeAsync(oldEnabled, newEnabled, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TestEngineerToggleHandler config-change listener failed");
        }
    }

    private async Task HandleToggleChangeAsync(bool oldEnabled, bool newEnabled, CancellationToken ct)
    {
        try
        {
            if (newEnabled)
            {
                await ApplyTurnedOnAsync(ct);
            }
            else
            {
                await ApplyTurnedOffAsync(ct);
            }

            RecordToggleDecision(oldEnabled, newEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestEngineerToggleHandler failed to apply toggle change ({Old}→{New})",
                oldEnabled, newEnabled);
        }
    }

    private async Task ApplyTurnedOnAsync(CancellationToken ct)
    {
        var existing = _registry.GetAgentsByRole(AgentRole.TestEngineer);
        if (existing.Count > 0)
        {
            _logger.LogInformation("TE toggle ON — TestEngineer already running, no spawn needed");
            return;
        }

        _logger.LogInformation("TE toggle ON — spawning TestEngineer agent");
        var identity = await _spawnManager.SpawnAgentAsync(AgentRole.TestEngineer, ct);
        if (identity is null)
        {
            _logger.LogWarning("TE toggle ON — SpawnAgentAsync returned null (slot exhausted or factory error)");
        }
        else
        {
            _logger.LogInformation("TE toggle ON — TestEngineer '{DisplayName}' spawned", identity.DisplayName);
        }
    }

    private async Task ApplyTurnedOffAsync(CancellationToken ct)
    {
        // 1) Stop any running TE agent. Multiple instances are not expected (singleton role)
        //    but we iterate defensively.
        var teAgents = _registry.GetAgentsByRole(AgentRole.TestEngineer);
        foreach (var te in teAgents)
        {
            try
            {
                _logger.LogInformation("TE toggle OFF — terminating '{AgentId}' ({DisplayName})",
                    te.Identity.Id, te.Identity.DisplayName);
                await _spawnManager.TerminateAgentAsync(te.Identity.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TE toggle OFF — failed to terminate '{AgentId}'", te.Identity.Id);
            }
        }

        // 2) Sweep open PRs that were waiting on TE so they can advance.
        //    PR services are optional — only present after a project has been opened.
        var prService = _serviceProvider.GetService(typeof(IPullRequestService)) as IPullRequestService;
        if (prService is null)
        {
            _logger.LogDebug("TE toggle OFF — no IPullRequestService available, skipping PR sweep");
            return;
        }

        try
        {
            var openPrs = await prService.ListOpenAsync(ct);
            int unblocked = 0;
            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested) break;
                bool architectApproved = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.ArchitectApproved, StringComparer.OrdinalIgnoreCase);
                bool testsAdded = pr.Labels.Contains(
                    PullRequestWorkflow.Labels.TestsAdded, StringComparer.OrdinalIgnoreCase);

                if (!architectApproved || testsAdded) continue;

                // Clear the agent-stuck label if TE escalation set it (TE isn't coming back),
                // so PM / SE can pick the PR up immediately.
                if (pr.Labels.Contains("agent-stuck", StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        await prService.RemoveLabelAsync(pr.Number, "agent-stuck", ct);
                        _logger.LogInformation(
                            "TE toggle OFF — cleared 'agent-stuck' label from PR #{Number} (TE no longer participating)",
                            pr.Number);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "TE toggle OFF — failed to clear 'agent-stuck' label from PR #{Number}", pr.Number);
                    }
                }

                unblocked++;
            }

            _logger.LogInformation(
                "TE toggle OFF — sweep complete: {Count} architect-approved PR(s) bypass TE; PM/SE will advance them on next poll",
                unblocked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TE toggle OFF — PR sweep failed (non-fatal, PM polling will recover)");
        }
    }

    private void RecordToggleDecision(bool oldEnabled, bool newEnabled)
    {
        // Optional — IDecisionLog is registered as singleton in Runner only; standalone
        // dashboard or test harnesses may omit it.
        var decisionLog = _serviceProvider.GetService(typeof(IDecisionLog)) as IDecisionLog;
        if (decisionLog is null)
        {
            _logger.LogDebug("TE toggle decision not recorded — no IDecisionLog registered");
            return;
        }

        try
        {
            decisionLog.Log(new AgentDecision
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = "system:test-engineer-toggle",
                AgentDisplayName = "System (TE Toggle)",
                Phase = "Configuration",
                ImpactLevel = DecisionImpactLevel.M,
                Title = newEnabled
                    ? "Test Engineer enabled mid-run"
                    : "Test Engineer disabled mid-run",
                Rationale = newEnabled
                    ? "Operator flipped Review.TestEngineerReviews ON via the Configuration page. " +
                      "TE agent has been spawned and will pick up open architect-approved PRs."
                    : "Operator flipped Review.TestEngineerReviews OFF via the Configuration page. " +
                      "TE agent has been stopped and any in-flight PR with architect-approved bypasses " +
                      "the tests-added gate so PM / SE can merge directly.",
                Category = "AgentReviewers",
                Status = DecisionStatus.AutoApproved,
                ResolvedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TE toggle decision logging failed (non-fatal)");
        }
    }
}


