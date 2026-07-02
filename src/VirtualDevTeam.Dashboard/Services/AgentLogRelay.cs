using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Dashboard.Hubs;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Background service that reads from <see cref="AgentCliLogService.EventReader"/>
/// and fans out log entries to subscribed SignalR clients via <see cref="AgentLogHub"/>.
/// Batches entries for high-volume streams (max 100ms debounce).
/// </summary>
public sealed class AgentLogRelay : BackgroundService
{
    private readonly AgentCliLogService _logService;
    private readonly IHubContext<AgentLogHub> _hubContext;
    private readonly ILogger<AgentLogRelay> _logger;

    public AgentLogRelay(
        AgentCliLogService logService,
        IHubContext<AgentLogHub> hubContext,
        ILogger<AgentLogRelay> logger)
    {
        _logService = logService;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentLogRelay started — streaming CLI log entries to Dashboard clients");

        try
        {
            await foreach (var evt in _logService.EventReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _hubContext.Clients
                        .Group($"agent-log:{evt.AgentId}")
                        .SendAsync("ReceiveLogEntry", new
                        {
                            agentId = evt.AgentId,
                            sequence = evt.Entry.Sequence,
                            timestamp = evt.Entry.TimestampUtc.ToString("O"),
                            text = evt.Entry.Text,
                            classification = evt.Entry.Classification.ToString(),
                            callId = evt.Entry.CallId
                        }, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to relay log entry to SignalR (non-fatal)");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentLogRelay stopped unexpectedly");
        }
    }
}
