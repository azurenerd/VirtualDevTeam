using System.Text;
using System.Text.Json;
using AgentSquad.Core.Frameworks;
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
        IProgress<FrameworkActivityEvent>? activitySink = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(logBuffer);
        ArgumentNullException.ThrowIfNull(killSource);

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
                    return;
                }

                if (line is null)
                    return; // EOF

                logBuffer.AppendLine(line);

                // Any non-empty activity resets the stuck window. Reset BEFORE any
                // further work on the line so a slow tool-call counter cannot
                // accidentally trip the stuck detector.
                stuckCts.CancelAfter(stuckWindow);

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
            _logger.LogDebug(ex, "AgenticOutputMonitor reader failed");
        }
    }

    /// <summary>
    /// Parse a stdout line into a human-readable activity event and report it.
    /// JSONL tool-call lines are parsed for type/name; plain text lines are
    /// reported as-is. Raw JSON blobs are never sent to the UI.
    /// </summary>
    private static void ReportActivity(IProgress<FrameworkActivityEvent> sink, string line)
    {
        // Try to parse JSONL for tool-call events
        if (line.Length < 64_000 && line.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                {
                    var type = typeProp.GetString() ?? "";

                    // Tool-call events: extract tool name, args, and result for readable display
                    if (type.Contains("tool", StringComparison.OrdinalIgnoreCase))
                    {
                        var toolName = TryGetToolName(root);
                        var toolArgs = TryGetToolArgs(root);
                        var toolResult = TryGetToolResult(root);

                        var msg = !string.IsNullOrEmpty(toolName) ? $"Tool: {toolName}" : $"Tool event: {type}";
                        if (!string.IsNullOrEmpty(toolArgs))
                            msg += $" ({toolArgs})";
                        if (!string.IsNullOrEmpty(toolResult))
                            msg += $" → {toolResult}";

                        sink.Report(new FrameworkActivityEvent("tool-call", msg));
                        return;
                    }

                    // Assistant message events: show a summary
                    if (type.Contains("assistant", StringComparison.OrdinalIgnoreCase) ||
                        type.Contains("message", StringComparison.OrdinalIgnoreCase))
                    {
                        var content = TryGetContent(root);
                        if (!string.IsNullOrEmpty(content))
                        {
                            var summary = content.Length > 120 ? content[..120] + "…" : content;
                            sink.Report(new FrameworkActivityEvent("thinking", summary));
                            return;
                        }
                    }

                    // Other JSON events — skip noise (don't send raw JSON to UI)
                    return;
                }
            }
            catch (JsonException) { /* not valid JSON, fall through to plain text */ }
        }

        // Plain text output — report as stdout (truncate very long lines)
        var trimmed = line.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200] + "…";
        sink.Report(new FrameworkActivityEvent("stdout", trimmed));
    }

    /// <summary>Extract tool name from JSONL tool-call events.</summary>
    private static string? TryGetToolName(JsonElement root)
    {
        // Try common shapes: {"name": "..."}, {"tool": {"name": "..."}}, {"function": {"name": "..."}}
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            return n.GetString();
        if (root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.Object &&
            t.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String)
            return tn.GetString();
        if (root.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object &&
            f.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String)
            return fn.GetString();
        return null;
    }

    /// <summary>Extract a short human-readable summary of tool arguments.</summary>
    private static string? TryGetToolArgs(JsonElement root)
    {
        // Check common argument locations
        JsonElement args = default;
        if (root.TryGetProperty("arguments", out var a))
            args = a;
        else if (root.TryGetProperty("input", out var inp))
            args = inp;
        else if (root.TryGetProperty("tool_input", out var ti))
            args = ti;
        else if (root.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object &&
                 fn.TryGetProperty("arguments", out var fa))
            args = fa;
        else if (root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.Object &&
                 t.TryGetProperty("input", out var tInp))
            args = tInp;
        else
            return null;

        // For string arguments (already serialized JSON string), extract key hints
        if (args.ValueKind == JsonValueKind.String)
        {
            var raw = args.GetString() ?? "";
            return SummarizeArgs(raw);
        }

        // For object arguments, extract key fields
        if (args.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var prop in args.EnumerateObject())
            {
                if (parts.Count >= 3) break; // limit to 3 key args
                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => "..."
                };
                if (val.Length > 60) val = val[..60] + "…";
                parts.Add($"{prop.Name}={val}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        return null;
    }

    /// <summary>Summarize a serialized JSON argument string into key hints.</summary>
    private static string SummarizeArgs(string raw)
    {
        if (raw.Length < 3) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (parts.Count >= 3) break;
                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => "..."
                };
                if (val.Length > 60) val = val[..60] + "…";
                parts.Add($"{prop.Name}={val}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : raw.Length > 80 ? raw[..80] + "…" : raw;
        }
        catch
        {
            return raw.Length > 80 ? raw[..80] + "…" : raw;
        }
    }

    /// <summary>Extract tool result/output summary.</summary>
    private static string? TryGetToolResult(JsonElement root)
    {
        JsonElement result = default;
        if (root.TryGetProperty("result", out var r))
            result = r;
        else if (root.TryGetProperty("output", out var o))
            result = o;
        else if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var text = c.GetString() ?? "";
            if (text.Length > 100) text = text[..100] + "…";
            return text;
        }
        else
            return null;

        if (result.ValueKind == JsonValueKind.String)
        {
            var text = result.GetString() ?? "";
            return text.Length > 100 ? text[..100] + "…" : text;
        }

        return null;
    }

    /// <summary>Extract content/text from assistant message events.</summary>
    private static string? TryGetContent(JsonElement root)
    {
        if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString();
        if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
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
