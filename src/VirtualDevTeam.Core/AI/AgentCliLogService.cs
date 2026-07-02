using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Singleton service that captures and buffers CLI output per agent for the live log viewer.
/// Thread-safe ring buffer per agent with bounded event bus for SignalR push.
/// </summary>
public sealed class AgentCliLogService : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentLogBuffer> _buffers = new();
    private readonly Channel<AgentCliLogEvent> _eventChannel;
    private readonly ILogger<AgentCliLogService> _logger;
    private bool _disposed;

    private const int MaxLinesPerAgent = 500;
    private const int MaxBytesPerAgent = 512 * 1024; // 512KB
    private const int MaxLineLength = 4096;
    private const int EventChannelCapacity = 500;

    public AgentCliLogService(ILogger<AgentCliLogService> logger)
    {
        _logger = logger;
        _eventChannel = Channel.CreateBounded<AgentCliLogEvent>(
            new BoundedChannelOptions(EventChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Append a classified log entry for an agent. Called from CopilotCliProcessManager's
    /// stdout/stderr readers (tee point). Thread-safe.
    /// </summary>
    public void Append(string agentId, string text, LogLineClassification classification,
        string? callId = null, string? toolName = null, bool? toolSuccess = null, string? toolOutput = null)
    {
        if (_disposed || string.IsNullOrEmpty(agentId)) return;

        // Truncate very long lines
        var displayText = text.Length > MaxLineLength ? text[..MaxLineLength] + "…" : text;

        var buffer = _buffers.GetOrAdd(agentId, _ => new AgentLogBuffer(MaxLinesPerAgent, MaxBytesPerAgent));
        var entry = buffer.Append(displayText, classification, callId, toolName, toolSuccess, toolOutput);

        // Non-blocking publish to event channel
        _eventChannel.Writer.TryWrite(new AgentCliLogEvent(agentId, entry));
    }

    /// <summary>
    /// Insert a call boundary marker when a new CLI process starts for an agent.
    /// </summary>
    public void MarkCallBoundary(string agentId, CallBoundaryInfo info)
    {
        if (_disposed || string.IsNullOrEmpty(agentId)) return;

        var label = FormatBoundaryLabel(info);
        Append(agentId, label, LogLineClassification.CallBoundary, info.CallId);
    }

    /// <summary>
    /// Get recent log entries for an agent, optionally filtered by verbosity and after a sequence number.
    /// Returns an immutable snapshot.
    /// </summary>
    public IReadOnlyList<AgentCliLogEntry> GetRecent(string agentId, LogVerbosity verbosity = LogVerbosity.High, long afterSequence = -1)
    {
        if (!_buffers.TryGetValue(agentId, out var buffer))
            return Array.Empty<AgentCliLogEntry>();

        return buffer.GetSnapshot(verbosity, afterSequence);
    }

    /// <summary>
    /// Get the latest sequence number for an agent (for reconnection support).
    /// </summary>
    public long GetLatestSequence(string agentId)
    {
        if (!_buffers.TryGetValue(agentId, out var buffer))
            return -1;
        return buffer.LatestSequence;
    }

    /// <summary>
    /// Get the UTC timestamp of the most recent log entry for an agent.
    /// Returns null if no entries exist. Used by FlowMonitor to check log activity.
    /// </summary>
    public DateTime? GetLatestEntryTimestamp(string agentId)
    {
        if (!_buffers.TryGetValue(agentId, out var buffer))
            return null;
        return buffer.LatestTimestamp;
    }

    /// <summary>
    /// Channel reader for the event bus. SignalR relay subscribes to this.
    /// </summary>
    public ChannelReader<AgentCliLogEvent> EventReader => _eventChannel.Reader;

    /// <summary>
    /// Clear log buffer for an agent.
    /// </summary>
    public void Clear(string agentId)
    {
        if (_buffers.TryGetValue(agentId, out var buffer))
            buffer.Clear();
    }

    private static string FormatBoundaryLabel(CallBoundaryInfo info)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(info.Model))
            parts.Add(info.Model);
        if (!string.IsNullOrEmpty(info.PromptPreview))
            parts.Add(info.PromptPreview.Length > 80 ? info.PromptPreview[..80] + "…" : info.PromptPreview);
        if (!string.IsNullOrEmpty(info.WorkingDirectory))
        {
            var dir = info.WorkingDirectory;
            // Show just the last 2 path segments
            var segments = dir.Replace('/', '\\').Split('\\');
            if (segments.Length > 2)
                dir = string.Join('\\', segments[^2..]);
            parts.Add(dir);
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "New CLI call";
    }

    public void Dispose()
    {
        _disposed = true;
        _eventChannel.Writer.TryComplete();
    }
}

/// <summary>
/// Event emitted when a new log entry is appended. Used by the SignalR relay.
/// </summary>
public sealed record AgentCliLogEvent(string AgentId, AgentCliLogEntry Entry);

/// <summary>
/// Thread-safe ring buffer of log entries for a single agent.
/// Bounded by both line count and total byte size.
/// </summary>
internal sealed class AgentLogBuffer
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly LinkedList<AgentCliLogEntry> _entries = new();
    private readonly object _lock = new();
    private long _sequence;
    private int _totalBytes;

    public AgentLogBuffer(int maxLines, int maxBytes)
    {
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    public long LatestSequence
    {
        get { lock (_lock) return _sequence; }
    }

    public DateTime? LatestTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _entries.Last?.Value?.TimestampUtc;
            }
        }
    }

    public AgentCliLogEntry Append(string text, LogLineClassification classification, string? callId,
        string? toolName = null, bool? toolSuccess = null, string? toolOutput = null)
    {
        lock (_lock)
        {
            // Merge consecutive Assistant deltas with the same callId into one entry.
            // This prevents word-per-line fragmentation from JSONL assistant.message_delta events.
            if (classification == LogLineClassification.Assistant
                && callId is not null
                && _entries.Last?.Value is { } last
                && last.Classification == LogLineClassification.Assistant
                && last.CallId == callId
                && last.Text.Length + text.Length < MaxMergedEntryLength)
            {
                var oldBytes = last.Text.Length * 2;
                var merged = last with { Text = last.Text + text };
                _entries.Last!.Value = merged;
                _totalBytes += text.Length * 2;

                // Publish the merged entry under the SAME sequence so the UI can update in-place
                return merged;
            }

            var entry = new AgentCliLogEntry(
                Sequence: ++_sequence,
                TimestampUtc: DateTime.UtcNow,
                Text: text,
                Classification: classification,
                CallId: callId,
                ToolName: toolName,
                ToolSuccess: toolSuccess,
                ToolOutput: toolOutput);

            var entryBytes = text.Length * 2; // rough UTF-16 estimate
            _entries.AddLast(entry);
            _totalBytes += entryBytes;

            // Evict oldest entries if over limits
            while (_entries.Count > _maxLines || _totalBytes > _maxBytes)
            {
                if (_entries.First is null) break;
                var removed = _entries.First.Value;
                _entries.RemoveFirst();
                _totalBytes -= removed.Text.Length * 2;
            }

            return entry;
        }
    }

    private const int MaxMergedEntryLength = 8192;

    public IReadOnlyList<AgentCliLogEntry> GetSnapshot(LogVerbosity verbosity, long afterSequence)
    {
        lock (_lock)
        {
            var result = new List<AgentCliLogEntry>();
            foreach (var entry in _entries)
            {
                if (entry.Sequence <= afterSequence)
                    continue;
                if (CliLineClassifier.IsVisibleAtVerbosity(entry.Classification, verbosity))
                    result.Add(entry);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _totalBytes = 0;
        }
    }
}
