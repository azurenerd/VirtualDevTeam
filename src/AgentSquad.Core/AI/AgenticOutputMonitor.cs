using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Core.AI;

/// <summary>
/// Stdout monitor for agentic CLI sessions. Runs alongside the copilot process
/// and:
/// <list type="bullet">
///   <item>Accumulates every line into the supplied <see cref="StringBuilder"/>
///   log buffer (so the caller always has the full tail available on failure).</item>
///   <item>Detects "stuck" sessions via a renewable stuck-timer: if no stdout
///   activity occurs within <see cref="AgenticConfig.StuckSeconds"/>, the
///   monitor cancels <paramref name="killSource"/>, which the caller observes
///   and kills the process tree.</item>
///   <item>When JSON output mode is active, counts tool-call events (any JSONL
///   line whose <c>type</c> property contains <c>"tool"</c>) and cancels
///   <paramref name="killSource"/> once <see cref="AgenticConfig.ToolCallCap"/>
///   is exceeded. When JSON mode is disabled, tool-call enforcement is off
///   (no stdout-regex fallback) but the stuck detector still applies.</item>
/// </list>
/// This is a SIBLING of <see cref="CliInteractiveWatchdog"/>, not an extension:
/// the legacy regex-based monitor handles interactive prompts on ordinary
/// single-shot calls; this class handles the very different lifecycle of a
/// long-running <c>--allow-all</c> agentic session.
/// </summary>
public sealed class AgenticOutputMonitor
{
    private readonly AgenticConfig _config;
    private readonly ILogger _logger;
    private readonly bool _jsonMode;
    private int _toolCallCount;

    public AgenticOutputMonitor(AgenticConfig config, ILogger logger, bool jsonMode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _jsonMode = jsonMode;
    }

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);

    /// <summary>Set by <see cref="RunAsync"/> when a cap/stuck violation triggers a kill. Null on clean EOF.</summary>
    public AgenticFailureReason? FailureReason { get; private set; }

    /// <summary>
    /// Drive the stdout stream to EOF (or to a detected violation). On violation
    /// the monitor triggers <paramref name="killSource"/>; the outer lifecycle is
    /// responsible for killing the process tree when that source is cancelled.
    /// The method returns without throwing — callers read <see cref="FailureReason"/>
    /// to discover whether a violation occurred.
    /// </summary>
    public async Task RunAsync(
        StreamReader stdout,
        StringBuilder logBuffer,
        CancellationTokenSource killSource,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(logBuffer);
        ArgumentNullException.ThrowIfNull(killSource);

        // The stuck timer is a CancellationTokenSource whose CancelAfter is reset on
        // every line read. When ReadLineAsync throws due to stuckCts cancellation,
        // we flip the failure reason to StuckNoOutput.
        using var stuckCts = new CancellationTokenSource();
        var stuckWindow = TimeSpan.FromSeconds(Math.Max(1, _config.StuckSeconds));
        stuckCts.CancelAfter(stuckWindow);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stuckCts.Token);

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(linked.Token);
                }
                catch (OperationCanceledException) when (stuckCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    FailureReason = AgenticFailureReason.StuckNoOutput;
                    _logger.LogWarning(
                        "Copilot CLI session stuck: no stdout for {Seconds}s (stuck window)",
                        stuckWindow.TotalSeconds);
                    SafeCancel(killSource);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Outer cancel — caller is already tearing down. Exit quietly.
                    return;
                }

                if (line is null)
                    return; // EOF

                logBuffer.AppendLine(line);

                // Any non-empty activity resets the stuck window. Reset BEFORE any
                // further work on the line so a slow tool-call counter cannot
                // accidentally trip the stuck detector.
                stuckCts.CancelAfter(stuckWindow);

                if (!_jsonMode)
                    continue;

                if (!IsLikelyToolCallLine(line))
                    continue;

                var newCount = Interlocked.Increment(ref _toolCallCount);
                if (newCount > _config.ToolCallCap)
                {
                    FailureReason = AgenticFailureReason.ToolCallCap;
                    _logger.LogWarning(
                        "Copilot CLI session exceeded tool-call cap: {Count} > {Cap}",
                        newCount, _config.ToolCallCap);
                    SafeCancel(killSource);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Don't let a reader exception bubble — the outer lifecycle will
            // observe process exit and classify. Just record and bail.
            _logger.LogDebug(ex, "AgenticOutputMonitor reader failed");
        }
    }

    /// <summary>
    /// Cheap pre-filter + strict JSON parse. The pre-filter rejects lines that
    /// can't possibly be tool-call events (no <c>type</c> property, no token
    /// <c>tool</c>), which keeps the JSON parser off the hot path during the
    /// typical case of streaming plain-text assistant output.
    /// </summary>
    private static bool IsLikelyToolCallLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Length > 64_000) return false; // guard against pathological payloads
        if (!line.Contains("\"type\"", StringComparison.Ordinal)) return false;
        if (line.IndexOf("tool", StringComparison.OrdinalIgnoreCase) < 0) return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("type", out var t)) return false;
            if (t.ValueKind != JsonValueKind.String) return false;
            var type = t.GetString();
            return !string.IsNullOrEmpty(type) &&
                   type.Contains("tool", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
