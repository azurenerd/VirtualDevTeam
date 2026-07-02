using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.DevPlatform.Config;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Initializes <see cref="LocalPlatformContext"/> eagerly at startup so that
/// Blazor pages never trigger the sync-over-async <c>EnsureInitializedSync</c>
/// path (which deadlocks on the Blazor synchronization context).
/// Only runs when <c>DevPlatformType.Local</c> is configured.
/// </summary>
public sealed class LocalPlatformInitializer : IHostedService
{
    private readonly LocalPlatformContext _ctx;
    private readonly IOptions<DevPlatformConfig> _config;
    private readonly ILogger<LocalPlatformInitializer> _logger;

    public LocalPlatformInitializer(
        LocalPlatformContext ctx,
        IOptions<DevPlatformConfig> config,
        ILogger<LocalPlatformInitializer> logger)
    {
        _ctx = ctx;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Value.Platform != DevPlatformType.Local)
            return Task.CompletedTask;

        try
        {
            // Force initialization now (on the startup thread, not a Blazor circuit)
            using var conn = _ctx.CreateConnection();
            _logger.LogInformation("LocalPlatformInitializer: context initialized eagerly at startup");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalPlatformInitializer: failed to initialize — will retry on first use");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
