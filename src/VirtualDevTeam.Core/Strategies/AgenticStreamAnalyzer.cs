namespace VirtualDevTeam.Core.Strategies;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Frameworks;

/// <summary>
/// AI-powered session analyzer that replaces hardcoded tool-call-cap heuristics.
/// Subscribes to the JSONL activity stream from agentic CLI sessions and uses
/// deterministic pattern matching (MVP) + optional budget-tier LLM (future) to
/// assess whether the session is productive, stuck, or off-track.
///
/// <para>Architecture:</para>
/// <list type="bullet">
///   <item>Event-triggered, not polling — fires at configurable soft thresholds</item>
///   <item>Advisory — AI suggests actions, deterministic rules enforce kills</item>
///   <item>Two-tier intervention: Tier 1 = stdin nudge, Tier 2 = kill + resume</item>
/// </list>
///
/// <para>The hard tool-call-cap (AgenticConfig.ToolCallCap) is kept as a safety net
/// but should be set to 0 (disabled) when this analyzer is active.</para>
/// </summary>
public sealed class AgenticStreamAnalyzer : IDisposable
{
    private readonly ILogger _logger;
    private readonly IChatCompletionRunner? _llmRunner;
    private readonly ConcurrentQueue<ActivityEvent> _recentEvents = new();
    private readonly int _maxBufferedEvents;
    private readonly int _softCapThreshold;
    private readonly int _analysisIntervalCalls;
    private int _toolCallCount;
    private int _lastAnalysisAt;
    private volatile bool _disposed;
    private volatile string? _lastVerdict;
    private volatile bool _nudgeSent;
    private DateTimeOffset _sessionStart;
    private string _taskId = "";
    private string _taskTitle = "";
    private int _testsPassedDetected; // 0=false, 1=true (int for Interlocked)
    private int _buildPassedDetected; // 0=false, 1=true (int for Interlocked)
    private int _buildFailCount;
    private string? _lastBuildError;
    private int _consecutiveSameErrorCount;
    private int _lastAnalyzerUpdateAt; // tool call count at last analyzer update emission

    /// <summary>Intervention sink for writing to the CLI's stdin.</summary>
    public IAgenticInterventionSink? InterventionSink { get; set; }

    /// <summary>Last AI verdict for dashboard display.</summary>
    public string? LastVerdict => _lastVerdict;

    /// <summary>Whether the analyzer detected tests passing.</summary>
    public bool TestsPassedDetected => Volatile.Read(ref _testsPassedDetected) != 0;

    /// <summary>Returns a coherent snapshot of the analyzer's current state for dashboard display.</summary>
    public AnalyzerStateSnapshot GetStateSnapshot() => new(
        ToolCallCount: Volatile.Read(ref _toolCallCount),
        BuildPassed: Volatile.Read(ref _buildPassedDetected) != 0,
        TestsPassed: Volatile.Read(ref _testsPassedDetected) != 0,
        BuildFailCount: Volatile.Read(ref _buildFailCount),
        AnalyzerVerdict: _lastVerdict,
        NudgeSent: _nudgeSent);

    /// <summary>Immutable snapshot of analyzer state for event emission.</summary>
    public record AnalyzerStateSnapshot(
        int ToolCallCount,
        bool BuildPassed,
        bool TestsPassed,
        int BuildFailCount,
        string? AnalyzerVerdict,
        bool NudgeSent);

    /// <summary>Callback fired when analyzer state changes materially (throttled to every 5 tool calls + on state transitions).</summary>
    public event Action<AnalyzerStateSnapshot>? OnStateChanged;

    public AgenticStreamAnalyzer(
        ILogger logger,
        IChatCompletionRunner? llmRunner = null,
        int softCapThreshold = 500,
        int analysisIntervalCalls = 200,
        int maxBufferedEvents = 30)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _llmRunner = llmRunner;
        _softCapThreshold = softCapThreshold;
        _analysisIntervalCalls = analysisIntervalCalls;
        _maxBufferedEvents = maxBufferedEvents;
    }

    /// <summary>Configure the analyzer for a specific task.</summary>
    public void Initialize(string taskId, string taskTitle)
    {
        _taskId = taskId;
        _taskTitle = taskTitle;
        _sessionStart = DateTimeOffset.UtcNow;
        _toolCallCount = 0;
        _lastAnalysisAt = 0;
        _testsPassedDetected = 0;
        _buildPassedDetected = 0;
        _buildFailCount = 0;
        _lastBuildError = null;
        _consecutiveSameErrorCount = 0;
        _nudgeSent = false;
        _lastVerdict = null;
        _lastAnalyzerUpdateAt = 0;
    }

    /// <summary>
    /// Called by <see cref="AnalyzerTeeSink"/> for each activity event from the CLI session.
    /// Buffers events and triggers analysis/intervention at soft thresholds.
    /// </summary>
    public void OnActivityEvent(FrameworkActivityEvent evt)
    {
        if (_disposed) return;

        var message = evt.Message ?? "";
        var recorded = new ActivityEvent(DateTimeOffset.UtcNow, evt.Category, message);
        _recentEvents.Enqueue(recorded);
        while (_recentEvents.Count > _maxBufferedEvents)
            _recentEvents.TryDequeue(out _);

        // ── Deterministic: detect tests-passed pattern ──
        // This is the highest-value detection — when the CLI says "all tests pass"
        // but keeps going, we immediately nudge it to commit and stop.
        if (evt.Category is "assistant" or "complete")
        {
            if (message.Contains("tests pass", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("0 failure", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("no failure", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("all pass", StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.Exchange(ref _testsPassedDetected, 1);
                _logger.LogInformation(
                    "AgenticStreamAnalyzer: detected tests-passed for task {Task}: {Detail}",
                    _taskId, message.Length > 200 ? message[..200] : message);

                // Immediately try to nudge — don't wait for soft cap
                if (!_nudgeSent)
                    _ = Task.Run(() => SendTestsPassedNudgeAsync());

                EmitStateChanged(); // tests-passed is a material state transition
            }

            // Detect build success
            if (message.Contains("build succeeded", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Build succeeded", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _buildPassedDetected, 1);
                EmitStateChanged(); // build-passed is a material state transition
            }

            // Detect repeated build errors (error loop detection)
            if (message.Contains("build failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Build FAILED", StringComparison.Ordinal) ||
                message.Contains("error CS", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _buildFailCount);
                var errorKey = message.Length > 80 ? message[..80] : message;
                if (errorKey == _lastBuildError)
                {
                    _consecutiveSameErrorCount++;
                    if (_consecutiveSameErrorCount >= 3)
                    {
                        _logger.LogWarning(
                            "AgenticStreamAnalyzer: task {Task} has the same build error {Count} times in a row — likely stuck in error loop: {Error}",
                            _taskId, _consecutiveSameErrorCount, errorKey);
                        _lastVerdict = $"error-loop: same build error {_consecutiveSameErrorCount}x";
                        EmitStateChanged(); // error-loop is a material state transition
                    }
                }
                else
                {
                    _lastBuildError = errorKey;
                    _consecutiveSameErrorCount = 1;
                }
            }
        }

        // Track tool calls for soft-cap threshold
        if (evt.Category is "tool" or "intent")
        {
            var count = Interlocked.Increment(ref _toolCallCount);

            // Emit analyzer state update every 5 tool calls
            if ((count - _lastAnalyzerUpdateAt) >= 5)
            {
                _lastAnalyzerUpdateAt = count;
                EmitStateChanged();
            }

            // Report milestone at soft-cap crossing
            if (count == _softCapThreshold)
            {
                _logger.LogInformation(
                    "AgenticStreamAnalyzer: task {Task} crossed soft-cap at {Count} tool calls ({Elapsed})",
                    _taskId, count, DateTimeOffset.UtcNow - _sessionStart);
            }

            // Trigger LLM analysis at soft-cap and then every N calls after
            if (_llmRunner is not null &&
                count >= _softCapThreshold &&
                (count - _lastAnalysisAt) >= _analysisIntervalCalls)
            {
                _lastAnalysisAt = count;
                _ = Task.Run(() => AnalyzeWithLlmAsync(count));
            }
        }
    }

    private void EmitStateChanged()
    {
        try { OnStateChanged?.Invoke(GetStateSnapshot()); }
        catch { /* Never let subscriber errors break the analyzer */ }
    }

    private async Task SendTestsPassedNudgeAsync()
    {
        if (_nudgeSent || InterventionSink is null || _disposed)
            return;

        if (InterventionSink is not { IsAlive: true })
            return;

        _nudgeSent = true;

        var nudgeMessage = "\n\nIMPORTANT: All tests have passed successfully. " +
            "Your integration validation is COMPLETE. " +
            "Commit all your changes now with `git add -A && git commit -m \"Integration fixes\"` and STOP. " +
            "Do NOT continue exploring, auditing, cleaning up, or running additional checks. " +
            "Your work is done.\n";

        _logger.LogInformation(
            "AgenticStreamAnalyzer: tests passed for task {Task} — sending Tier 1 stdin nudge",
            _taskId);

        try
        {
            await InterventionSink.WriteToSessionAsync(nudgeMessage);
            _lastVerdict = "nudge-sent: tests passed, told CLI to commit and stop";

            // Report to dashboard
            _recentEvents.Enqueue(new ActivityEvent(
                DateTimeOffset.UtcNow, "watchdog",
                "🎯 Tests passed — sent stdin nudge to commit and stop"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticStreamAnalyzer: stdin nudge failed for task {Task}", _taskId);
        }
    }

    private async Task AnalyzeWithLlmAsync(int currentToolCount)
    {
        if (_llmRunner is null) return;

        try
        {
            var elapsed = DateTimeOffset.UtcNow - _sessionStart;
            var events = _recentEvents.ToArray();
            var summary = BuildEventSummary(events, currentToolCount, elapsed,
                Volatile.Read(ref _buildPassedDetected) != 0, Volatile.Read(ref _testsPassedDetected) != 0,
                Volatile.Read(ref _buildFailCount),
                _consecutiveSameErrorCount, _lastBuildError);

            var systemPrompt = "You are monitoring an AI coding agent. Respond with exactly one word: " +
                "PRODUCTIVE (making progress), STUCK (repeating same actions), or OFF-TRACK (exploring beyond scope). " +
                "If tests have passed and the agent is still running, respond: NUDGE: <one-sentence instruction>.";

            var userPrompt = $"Task: {_taskId} — {_taskTitle}\n\n{summary}";

            var result = await _llmRunner.InvokeAsync(systemPrompt, userPrompt, "standard", $"stream-analyzer/{_taskId}");

            _lastVerdict = result?.Length > 200 ? result[..200] : result;
            _logger.LogInformation(
                "AgenticStreamAnalyzer LLM verdict for {Task} at {Count} calls ({Elapsed}): {Verdict}",
                _taskId, currentToolCount, elapsed, _lastVerdict);

            // If verdict suggests nudge and we have a sink, try Tier 1
            if (result?.Contains("NUDGE:", StringComparison.OrdinalIgnoreCase) == true &&
                InterventionSink is { IsAlive: true } && !_nudgeSent)
            {
                _nudgeSent = true;
                var nudge = ExtractNudgeMessage(result);
                if (!string.IsNullOrWhiteSpace(nudge))
                {
                    await InterventionSink.WriteToSessionAsync($"\n\n{nudge}\n");
                    _logger.LogInformation(
                        "AgenticStreamAnalyzer: sent LLM-generated nudge to {Task}: {Nudge}",
                        _taskId, nudge.Length > 100 ? nudge[..100] : nudge);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AgenticStreamAnalyzer: LLM analysis failed for task {Task}", _taskId);
        }
    }

    private static string BuildEventSummary(ActivityEvent[] events, int toolCount, TimeSpan elapsed,
        bool buildPassed, bool testsPassed, int buildFailCount, int consecutiveSameError, string? lastBuildError)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Session State");
        sb.AppendLine($"- Tool calls: {toolCount}");
        sb.AppendLine($"- Elapsed: {elapsed.TotalMinutes:F1} minutes");
        sb.AppendLine($"- Build passed: {buildPassed}");
        sb.AppendLine($"- Tests passed: {testsPassed}");
        sb.AppendLine($"- Build failures seen: {buildFailCount}");
        if (consecutiveSameError >= 2)
            sb.AppendLine($"- ⚠️ Same error repeated {consecutiveSameError}x: {lastBuildError}");
        sb.AppendLine();
        sb.AppendLine($"## Last {events.Length} events:");
        foreach (var evt in events.TakeLast(20))
        {
            var detail = evt.Message.Length > 150 ? evt.Message[..150] + "..." : evt.Message;
            sb.AppendLine($"  [{evt.Category}] {detail}");
        }
        return sb.ToString();
    }

    private static string? ExtractNudgeMessage(string verdict)
    {
        var idx = verdict.IndexOf("NUDGE:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return verdict[(idx + 6)..].Trim();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private record ActivityEvent(DateTimeOffset Timestamp, string Category, string Message);
}

/// <summary>
/// Abstraction for writing to a running CLI session's stdin.
/// Implemented by the process manager; consumed by the stream analyzer.
/// </summary>
public interface IAgenticInterventionSink
{
    /// <summary>Write a message to the CLI's stdin. Thread-safe, best-effort.</summary>
    Task WriteToSessionAsync(string message);

    /// <summary>Whether the session process is still alive.</summary>
    bool IsAlive { get; }
}

/// <summary>
/// Wraps a <see cref="System.IO.StreamWriter"/> (Process.StandardInput) as an intervention sink.
/// Handles races with process exit gracefully.
/// </summary>
public sealed class ProcessStdinInterventionSink : IAgenticInterventionSink
{
    private readonly System.Diagnostics.Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger _logger;

    public ProcessStdinInterventionSink(System.Diagnostics.Process process, ILogger logger)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsAlive => !_process.HasExited;

    public async Task WriteToSessionAsync(string message)
    {
        if (_process.HasExited)
        {
            _logger.LogDebug("Stdin write skipped — process already exited");
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            if (_process.HasExited) return;
            await _process.StandardInput.WriteAsync(message);
            await _process.StandardInput.FlushAsync();
            _logger.LogDebug("Stdin intervention written ({Length} chars)", message.Length);
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Stdin write failed — stream closed or process disposed");
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Stdin write failed — IO error");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Tee sink that forwards <see cref="FrameworkActivityEvent"/>s to both the original
/// dashboard sink and the <see cref="AgenticStreamAnalyzer"/>. Drop-in replacement
/// for the activity sink passed to CLI options.
/// </summary>
public sealed class AnalyzerTeeSink : IProgress<FrameworkActivityEvent>
{
    private readonly IProgress<FrameworkActivityEvent>? _original;
    private readonly AgenticStreamAnalyzer _analyzer;

    public AnalyzerTeeSink(IProgress<FrameworkActivityEvent>? original, AgenticStreamAnalyzer analyzer)
    {
        _original = original;
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public void Report(FrameworkActivityEvent value)
    {
        _original?.Report(value);
        _analyzer.OnActivityEvent(value);
    }
}
