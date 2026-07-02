using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VirtualDevTeam.Core.Frameworks;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.AI;

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
///   <item>When an <see cref="IProgress{FrameworkActivityEvent}"/> activity sink
///   is provided, reports non-blank stdout lines and parsed JSONL tool-call events
///   to the dashboard for real-time visibility.</item>
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

    /// <summary>True once at least one non-null stdout line has been received. False means the
    /// session never produced any output — likely a startup hang (MCP init, auth, wrapper issue).</summary>
    public bool HasReceivedAnyOutput { get; private set; }

    /// <summary>
    /// Drive the stdout stream to EOF (or to a detected violation). On violation
    /// the monitor triggers <paramref name="killSource"/>; the outer lifecycle is
    /// responsible for killing the process tree when that source is cancelled.
    /// The method returns without throwing — callers read <see cref="FailureReason"/>
    /// to discover whether a violation occurred.
    /// </summary>
    /// <param name="stdout">Stdout stream to monitor.</param>
    /// <param name="logBuffer">Buffer accumulating full stdout log.</param>
    /// <param name="killSource">Cancellation source to trigger process kill.</param>
    /// <param name="ct">External cancellation token.</param>
    /// <param name="activitySink">Optional sink for real-time activity streaming to the dashboard.</param>
    public async Task RunAsync(
        StreamReader stdout,
        StringBuilder logBuffer,
        CancellationTokenSource killSource,
        CancellationToken ct,
        IProgress<FrameworkActivityEvent>? activitySink = null,
        Action<string>? onLine = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(logBuffer);
        ArgumentNullException.ThrowIfNull(killSource);

        // Stuck detection is optional — when StuckSeconds <= 0 it is disabled and
        // only the caller's ct (wall-clock timeout) can stop the monitor.
        var stuckEnabled = _config.StuckSeconds > 0;
        _logger.LogInformation(
            "AgenticOutputMonitor started: StuckSeconds={StuckSeconds}, enabled={Enabled}, jsonMode={JsonMode}",
            _config.StuckSeconds, stuckEnabled, _jsonMode);
        using var stuckCts = new CancellationTokenSource();
        if (stuckEnabled)
        {
            var stuckWindow = TimeSpan.FromSeconds(_config.StuckSeconds);
            stuckCts.CancelAfter(stuckWindow);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stuckCts.Token);

        var firstLineReceived = false;
        var startupSw = Stopwatch.StartNew();

        // CRITICAL: Windows anonymous pipes do NOT reliably cancel ReadLineAsync when
        // the CancellationToken fires — the read blocks until data arrives or the pipe
        // closes. If the hung process holds the pipe open, the OCE catch below never
        // executes and the kill chain never starts.
        // Fix: sibling watchdog task that drives killSource directly when stuck fires.
        Task stuckWatchdogTask = Task.CompletedTask;
        if (stuckEnabled)
        {
            stuckWatchdogTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stuckCts.Token);
                }
                catch (OperationCanceledException) when (stuckCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    FailureReason = AgenticFailureReason.StuckNoOutput;
                    _logger.LogWarning(
                        "Stuck watchdog: no meaningful stdout for {Seconds}s — forcing kill via killSource{StartupNote}",
                        _config.StuckSeconds,
                        firstLineReceived ? "" : " (session NEVER produced any output)");
                    SafeCancel(killSource);
                }
            });
        }

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(linked.Token);
                }
                catch (OperationCanceledException) when (stuckEnabled && stuckCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    FailureReason = AgenticFailureReason.StuckNoOutput;
                    _logger.LogWarning(
                        "Copilot CLI session stuck: no stdout for {Seconds}s (stuck window){StartupNote}",
                        _config.StuckSeconds,
                        firstLineReceived ? "" : " — session NEVER produced any output (likely hung during MCP/auth init)");
                    SafeCancel(killSource);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (line is null)
                    return; // EOF

                logBuffer.AppendLine(line);

                // Agent log viewer: tee each line to the log service
                try { onLine?.Invoke(line); } catch { /* best-effort, never block stdout reading */ }
                // Log startup liveness on first output line
                if (!firstLineReceived)
                {
                    firstLineReceived = true;
                    HasReceivedAnyOutput = true;
                    _logger.LogInformation(
                        "Copilot CLI agentic session startup: first stdout after {Elapsed:F1}s",
                        startupSw.Elapsed.TotalSeconds);
                }

                // Only reset the stuck window on MEANINGFUL output (non-blank lines).
                // Empty lines from the wrapper/CLI (heartbeats, progress indicators,
                // blank newlines) must NOT reset the timer — they indicate the process
                // is alive but not making progress. Without this guard, periodic blank
                // lines prevent stuck detection from ever firing.
                if (stuckEnabled && !string.IsNullOrWhiteSpace(line))
                    stuckCts.CancelAfter(TimeSpan.FromSeconds(_config.StuckSeconds));

                // Report activity to dashboard sink (skip blank/whitespace lines)
                if (activitySink is not null && !string.IsNullOrWhiteSpace(line))
                {
                    ReportActivity(activitySink, line);
                }

                if (!_jsonMode)
                    continue;

                if (!IsLikelyToolCallLine(line))
                    continue;

                var newCount = Interlocked.Increment(ref _toolCallCount);
                // Tool-call cap: 0 = disabled (AI monitor handles session lifecycle).
                // When enabled (> 0), acts as a hard safety net.
                if (_config.ToolCallCap > 0 && newCount > _config.ToolCallCap)
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
            _logger.LogDebug(ex, "AgenticOutputMonitor reader failed");
        }
    }

    /// <summary>
    /// Parse a stdout line into a high-level activity event matching the Copilot CLI
    /// terminal UX: assistant explanations (magenta ●) and tool intention summaries
    /// (green ●). Low-level tool execution events are suppressed — they add noise
    /// without value at the dashboard level.
    /// </summary>
    private static void ReportActivity(IProgress<FrameworkActivityEvent> sink, string line)
    {
        // Strip ANSI escape codes — even with --no-color, some can leak through
        // and break JSON parsing (e.g., "\x1b[0m{...}" won't StartsWith('{'))
        var cleanLine = CliOutputParser.StripAnsiCodes(line);

        if (cleanLine.Length < 64_000 && cleanLine.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(cleanLine);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    goto plainText;

                var type = typeProp.GetString() ?? "";

                // Skip ephemeral events (session setup, streaming deltas)
                if (root.TryGetProperty("ephemeral", out var eph) && eph.ValueKind == JsonValueKind.True)
                    return;

                // Skip session management noise (mcp_server_status, tools_updated, etc.)
                if (type.StartsWith("session.", StringComparison.OrdinalIgnoreCase))
                    return;

                // Skip streaming deltas (reasoning_delta, message_delta, content_delta)
                if (type.Contains("_delta", StringComparison.Ordinal) ||
                    type.Contains(".delta", StringComparison.Ordinal))
                    return;

                // Report user messages as "prompt sent" (helps users see the session started)
                if (type.StartsWith("user.", StringComparison.OrdinalIgnoreCase))
                {
                    sink.Report(new FrameworkActivityEvent("intent", "Prompt sent — AI is analyzing…"));
                    return;
                }

                // Skip raw tool execution events — we surface tool activity via the
                // higher-level intentionSummary on assistant.message instead.
                if (type.Contains("tool", StringComparison.OrdinalIgnoreCase))
                    return;

                // Get the data envelope (copilot CLI nests everything here)
                root.TryGetProperty("data", out var data);

                // ── Assistant message: the primary source of visible activity ──
                // Maps to the magenta ● (text) and green ● (tool intents) in the CLI.
                if (type == "assistant.message" && data.ValueKind == JsonValueKind.Object)
                {
                    bool reportedAnything = false;

                    // Green ● — tool intention summaries (what the agent plans to do)
                    if (data.TryGetProperty("toolRequests", out var reqs) &&
                        reqs.ValueKind == JsonValueKind.Array && reqs.GetArrayLength() > 0)
                    {
                        foreach (var req in reqs.EnumerateArray())
                        {
                            var intention = req.TryGetProperty("intentionSummary", out var isp) &&
                                            isp.ValueKind == JsonValueKind.String
                                ? isp.GetString() : null;
                            var reqName = req.TryGetProperty("name", out var rn) &&
                                          rn.ValueKind == JsonValueKind.String
                                ? rn.GetString() : null;

                            if (!string.IsNullOrEmpty(intention))
                            {
                                if (intention!.Length > 160) intention = intention[..160] + "…";
                                sink.Report(new FrameworkActivityEvent("intent", intention));
                                reportedAnything = true;
                            }
                            else if (!string.IsNullOrEmpty(reqName))
                            {
                                sink.Report(new FrameworkActivityEvent("intent", $"Using {reqName}"));
                                reportedAnything = true;
                            }
                        }
                    }

                    // Magenta ● — assistant's explanation / status text
                    var content = TryGetContent(data);
                    if (!string.IsNullOrEmpty(content))
                    {
                        var summary = content!.Length > 200 ? content[..200] + "…" : content;
                        sink.Report(new FrameworkActivityEvent("assistant", summary));
                        reportedAnything = true;
                    }

                    // If the assistant turn had neither tools nor text, surface a "thinking" event
                    if (!reportedAnything)
                    {
                        sink.Report(new FrameworkActivityEvent("assistant", "AI is thinking…"));
                    }
                    return;
                }

                // ── Assistant reasoning (full, non-delta) ──
                if (type == "assistant.reasoning" && data.ValueKind == JsonValueKind.Object)
                {
                    var content = data.TryGetProperty("content", out var rc) &&
                                  rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
                    if (!string.IsNullOrEmpty(content))
                    {
                        var summary = content!.Length > 200 ? content[..200] + "…" : content;
                        sink.Report(new FrameworkActivityEvent("reasoning", summary));
                    }
                    return;
                }

                // Other assistant events (turn_start, turn_end) — skip
                if (type.StartsWith("assistant.", StringComparison.OrdinalIgnoreCase))
                    return;

                // Any other structured JSON — skip noise
                return;
            }
            catch (JsonException) { /* not valid JSON, fall through to plain text */ }
        }

    plainText:
        var trimmed = cleanLine.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200] + "…";
        sink.Report(new FrameworkActivityEvent("stdout", trimmed));
    }

    /// <summary>Extract content/text from assistant message data.</summary>
    private static string? TryGetContent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString();
        if (data.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString();
        return null;
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
        if (line.Length > 64_000) return false;
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
