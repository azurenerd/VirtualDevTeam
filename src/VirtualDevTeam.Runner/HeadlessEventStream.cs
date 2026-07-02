using System.Text.Json;

namespace VirtualDevTeam.Runner;

/// <summary>
/// Headless output service — streams pipeline events as JSONL to stdout
/// when running in --headless mode. Replaces the Blazor dashboard UI.
/// Each line is a self-contained JSON object with type, timestamp, and data.
/// </summary>
public class HeadlessEventStream : IDisposable
{
    private readonly ILogger<HeadlessEventStream> _logger;
    private readonly TextWriter _output;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
    private bool _disposed;

    public HeadlessEventStream(ILogger<HeadlessEventStream> logger)
    {
        _logger = logger;
        _output = Console.Out;
    }

    /// <summary>Start streaming events to stdout.</summary>
    public void Start()
    {
        EmitEvent("lifecycle", "started", new { message = "VDT headless mode started", pid = Environment.ProcessId });
        _logger.LogInformation("Headless event stream started — writing JSONL to stdout");
    }

    /// <summary>Emit a structured event to stdout as a single JSONL line.</summary>
    public void EmitEvent(string type, string? id, object? data = null)
    {
        if (_disposed) return;
        try
        {
            var evt = new { type, id, timestamp = DateTimeOffset.UtcNow, data };
            var json = JsonSerializer.Serialize(evt, _jsonOpts);
            _output.WriteLine(json);
            _output.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write headless event");
        }
    }

    /// <summary>Emit an agent status change.</summary>
    public void EmitAgentStatus(string agentName, string status, string? reason = null, string? task = null)
    {
        EmitEvent("agent.status", agentName, new { agent = agentName, status, reason, task });
    }

    /// <summary>Emit a PR event.</summary>
    public void EmitPrEvent(string action, int prNumber, string title, string? url = null)
    {
        EmitEvent($"pr.{action}", prNumber.ToString(), new { number = prNumber, title, url });
    }

    /// <summary>Emit a phase transition.</summary>
    public void EmitPhaseEvent(string fromPhase, string toPhase)
    {
        EmitEvent("phase.transition", toPhase, new { from = fromPhase, to = toPhase });
    }

    /// <summary>Emit completion with summary.</summary>
    public void EmitCompletion(bool success, string summary)
    {
        EmitEvent("lifecycle", "completed", new { success, summary });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
