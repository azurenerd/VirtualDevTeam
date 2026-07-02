using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.Checkpoints;

/// <summary>
/// Subscribes to <see cref="PullRequestWorkflow.OnPRMerged"/> and fires
/// checkpoint captures as fire-and-forget tasks. Failures are logged
/// and swallowed — checkpoint capture must never block the pipeline.
/// Partial captures are cleaned up on failure.
/// </summary>
public sealed class CheckpointAutoTrigger : IHostedService, IDisposable
{
    private readonly IPipelineCheckpointService _checkpoints;
    private readonly PullRequestWorkflow _workflow;
    private readonly CheckpointConfig _config;
    private readonly ILogger<CheckpointAutoTrigger> _logger;
    private bool _disposed;

    public CheckpointAutoTrigger(
        IPipelineCheckpointService checkpoints,
        PullRequestWorkflow workflow,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<CheckpointAutoTrigger> logger)
    {
        _checkpoints = checkpoints;
        _workflow = workflow;
        _config = config.Value.Checkpoints;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Enabled && _config.AutoCapture.OnPRMerge)
        {
            _workflow.OnPRMerged += HandlePRMerged;
            _logger.LogInformation("CheckpointAutoTrigger: subscribed to OnPRMerged");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _workflow.OnPRMerged -= HandlePRMerged;
        return Task.CompletedTask;
    }

    private void HandlePRMerged(int prNumber, string? prTitle)
    {
        // Fire-and-forget — never block the merge path
        _ = Task.Run(async () =>
        {
            var name = $"AfterMerge_PR{prNumber}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
            try
            {
                _logger.LogInformation(
                    "Auto-checkpoint: capturing '{Name}' after PR #{PR} merge ({Title})",
                    name, prNumber, prTitle);

                var result = await _checkpoints.CaptureAsync(name, CheckpointTrigger.AfterPRMerge);

                if (result.Succeeded)
                {
                    _logger.LogInformation(
                        "Auto-checkpoint '{Name}' captured in {Elapsed:F1}s ({Size}MB)",
                        name, result.Elapsed.TotalSeconds,
                        (result.Info?.DiskSizeBytes ?? 0) / (1024 * 1024));
                }
                else
                {
                    _logger.LogWarning("Auto-checkpoint '{Name}' failed: {Error}", name, result.Error);
                    // Clean up partial capture
                    await _checkpoints.DeleteAsync(name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-checkpoint '{Name}' threw — cleaning up", name);
                try { await _checkpoints.DeleteAsync(name); }
                catch { /* best effort cleanup */ }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workflow.OnPRMerged -= HandlePRMerged;
    }
}
