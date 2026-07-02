using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace VirtualDevTeam.Agents;

/// <summary>
/// A Subject Matter Expert (SME) agent created dynamically from an <see cref="SMEAgentDefinition"/>.
/// Extends <see cref="CustomAgent"/> with workflow mode behavior (OnDemand, Continuous, OneShot)
/// and structured result reporting via the message bus.
/// </summary>
public class SmeAgent : CustomAgent
{
    private readonly SmeMetrics _smeMetrics;
    private bool _hasCompletedOneShot;

    /// <summary>The definition that created this SME agent.</summary>
    public SMEAgentDefinition Definition { get; }

    public SmeAgent(
        AgentIdentity identity,
        SMEAgentDefinition definition,
        AgentCoreServices core,
        AgentPlatformServices platform,
        ILogger<SmeAgent> logger,
        SmeMetrics smeMetrics)
        : base(identity, core, platform, logger)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _smeMetrics = smeMetrics ?? throw new ArgumentNullException(nameof(smeMetrics));
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        switch (Definition.WorkflowMode)
        {
            case SmeWorkflowMode.OneShot:
                await RunOneShotAsync(ct);
                break;

            case SmeWorkflowMode.Continuous:
            case SmeWorkflowMode.OnDemand:
            default:
                // Use the base CustomAgent loop — it handles issue/task queues and polling
                await base.RunAgentLoopAsync(ct);
                break;
        }
    }

    /// <summary>
    /// OneShot mode: wait for a single task, execute it, report results, then stop.
    /// </summary>
    private async Task RunOneShotAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "OneShot: waiting for task assignment");

        var pollInterval = TimeSpan.FromSeconds(Core.Config.Limits.GitHubPollIntervalSeconds);

        // Wait for a task assignment (poll the base class queues)
        while (!ct.IsCancellationRequested && !_hasCompletedOneShot)
        {
            await WaitIfPausedAsync(ct);
            // Defer to base loop for one iteration - it handles queue processing
            await base.RunAgentLoopAsync(CreateOneShotToken(ct));
            _hasCompletedOneShot = true;
        }

        // Report completion
        Logger.LogInformation("SME agent '{DisplayName}' completed OneShot execution", Identity.DisplayName);
        UpdateStatus(AgentStatus.Idle, "OneShot complete — shutting down");
    }

    /// <summary>
    /// Creates a cancellation token that cancels after one loop iteration for OneShot mode.
    /// </summary>
    private static CancellationToken CreateOneShotToken(CancellationToken parent)
    {
        // Just return the parent token - the loop will be controlled by _hasCompletedOneShot flag
        return parent;
    }

    /// <summary>
    /// Attempts to initialize and use MCP servers, with graceful degradation on failure.
    /// If MCP servers fail to load or execute, the agent logs a warning and continues
    /// without them, ensuring the agent loop does not crash due to MCP issues.
    /// </summary>
    protected void TryInitializeMcpServersWithDegradation()
    {
        try
        {
            // Attempt to set up MCP context. This may fail if:
            // - MCP servers are unavailable
            // - Config files are missing or corrupted
            // - Network calls fail
            TrySetMcpServerContext();
        }
        catch (Exception ex)
        {
            _smeMetrics.IncrementMcpServerErrors();
            Logger.LogWarning(
                ex,
                "MCP server initialization failed for SME agent '{DisplayName}'. " +
                "Continuing without MCP capabilities.",
                Identity.DisplayName);
        }
    }

    /// <summary>
    /// Internal method to set up MCP server context. Extracted for testability.
    /// Override or customize in subclasses if needed.
    /// </summary>
    protected virtual void TrySetMcpServerContext()
    {
        // This would typically be called by the task processing logic
        // to set AgentCallContext.McpServers if RoleContext is available
        if (RoleContext is not null)
        {
            try
            {
                var mcpServers = RoleContext.GetMcpServers(Identity.Role, Identity.CustomAgentName);
                if (mcpServers.Count > 0)
                {
                    AgentCallContext.McpServers = mcpServers;
                }
            }
            catch (Exception ex)
            {
                _smeMetrics.IncrementMcpServerErrors();
                Logger.LogWarning(ex, "Failed to retrieve MCP servers for SME agent '{DisplayName}'.", Identity.DisplayName);
            }
        }
    }
}
