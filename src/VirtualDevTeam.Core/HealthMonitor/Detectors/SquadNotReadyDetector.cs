using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when the Squad framework is enabled in strategy config but the
/// readiness check fails (missing Squad CLI, Node.js, or other dependencies).
/// Every Squad candidate will immediately fail with "framework-not-ready" gate
/// failure, wasting a candidate slot. This detector alerts the operator so they
/// can either install the missing dependency or disable Squad.
/// </summary>
public sealed class SquadNotReadyDetector : IFlowDetector
{
    public string DetectorId => "squad-not-ready";

    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<SquadNotReadyDetector> _logger;

    /// <summary>Phases where strategy framework runs candidates.</summary>
    private static readonly HashSet<string> RelevantPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ParallelDevelopment", "Testing", "Review"
    };

    /// <summary>Cache last check result to avoid running readiness check every 30s tick.</summary>
    private DateTime _lastCheckUtc;
    private bool _lastCheckPassed = true;
    private string? _lastCheckMessage;

    /// <summary>Re-check interval — don't spam the readiness checker every tick.</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);

    public SquadNotReadyDetector(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<SquadNotReadyDetector> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        try
        {
            if (!RelevantPhases.Contains(ctx.CurrentPhase))
                return findings;

            var cfg = _config.CurrentValue.StrategyFramework;
            if (!cfg.Enabled)
                return findings;

            // Check if squad is in the enabled strategies list
            var squadEnabled = cfg.EnabledStrategies
                .Any(s => s.Equals("squad", StringComparison.OrdinalIgnoreCase));
            if (!squadEnabled)
                return findings;

            // Throttle: only re-check every 5 minutes
            if ((ctx.Now.UtcDateTime - _lastCheckUtc) < RecheckInterval)
            {
                if (_lastCheckPassed)
                    return findings;

                // Re-emit cached finding
                findings.Add(BuildFinding(ctx, _lastCheckMessage ?? "Squad CLI not ready"));
                return findings;
            }

            // Run a lightweight check — just verify 'squad --version' works
            _lastCheckUtc = ctx.Now.UtcDateTime;
            var available = await IsSquadAvailableAsync(ct);

            if (available)
            {
                _lastCheckPassed = true;
                _lastCheckMessage = null;
                return findings;
            }

            _lastCheckPassed = false;
            _lastCheckMessage = "Squad CLI '@bradygaster/squad-cli' not found on PATH — " +
                "install with 'npm install -g @bradygaster/squad-cli' or disable Squad in Configuration";

            _logger.LogDebug("SquadNotReadyDetector: Squad enabled but not available");
            findings.Add(BuildFinding(ctx, _lastCheckMessage));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SquadNotReadyDetector: check failed (non-fatal)");
        }

        return findings;
    }

    private FlowFinding BuildFinding(DetectorContext ctx, string message)
    {
        return new FlowFinding
        {
            Id = Guid.NewGuid().ToString("N"),
            DetectedAt = ctx.Now,
            DetectorId = DetectorId,
            Severity = FlowFindingSeverity.Warning,
            Summary = "Squad framework enabled but CLI not installed — all Squad candidates will fail",
            Rationale = $"{message}. " +
                "Every task will waste time spawning a Squad candidate that immediately fails " +
                "the 'framework-not-ready' gate. Either install Squad CLI or disable it on the " +
                "Configuration page under Frameworks → Active Frameworks.",
            DedupKey = "squad-not-ready",
            State = FlowFindingState.Open,
        };
    }

    private static async Task<bool> IsSquadAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "squad",
                Arguments = OperatingSystem.IsWindows() ? "/c squad --version" : "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;

            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
