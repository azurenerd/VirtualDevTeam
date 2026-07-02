using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Manages parallel Copilot CLI sessions ("threads") for the Director CLI tab.
/// Implements IHostedService to pre-warm a session on startup so the first
/// Director command skips MCP server connection (uses --resume).
/// </summary>
public class DirectorCliService : IHostedService, IDisposable
{
    private readonly ILogger<DirectorCliService> _logger;
    private readonly RunnerProcessJob? _runnerJob;
    private readonly ConcurrentDictionary<string, CliThread> _threads = new();
    private bool _disposed;
    private int _nextThreadId;

    public DirectorCliService(ILogger<DirectorCliService> logger, RunnerProcessJob? runnerJob = null)
    {
        _logger = logger;
        _runnerJob = runnerJob;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Pre-warm disabled temporarily for wizard debugging
        // _ = Task.Run(() => PreWarmSessionAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private async Task PreWarmSessionAsync(CancellationToken ct)
    {
        try
        {
            // Wait a bit for the runner to finish starting up
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var threadId = CreateThread("Main");
            _logger.LogInformation("Pre-warming Director CLI session in thread {ThreadId}...", threadId);

            await SendCommandAsync(threadId, "respond with just the word 'ready'", _ => { }, ct);

            if (_threads.TryGetValue(threadId, out var thread) && !string.IsNullOrEmpty(thread.SessionId))
            {
                // Clear event history — pre-warm output shouldn't be shown to user
                thread.EventHistory.Clear();
                thread.EventHistoryBytes = 0;

                _logger.LogInformation(
                    "Director CLI session pre-warmed: sessionId={SessionId}, model={Model}",
                    thread.SessionId, thread.Model ?? "unknown");
            }
            else
            {
                _logger.LogWarning("Director CLI pre-warm completed but no sessionId captured");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Director CLI pre-warm failed (non-fatal)");
        }
    }

    public IReadOnlyList<CliThreadInfo> GetThreads()
    {
        return _threads.Values
            .OrderBy(t => t.CreatedAt)
            .Select(t => new CliThreadInfo
            {
                Id = t.Id,
                Name = t.Name,
                Status = t.Status,
                Model = t.Model,
                CreatedAt = t.CreatedAt,
                LastActivityAt = t.LastActivityAt,
                IsProcessRunning = t.Process is not null && !t.Process.HasExited,
                CommandCount = t.CommandCount,
                TotalTokens = t.TotalTokens
            })
            .ToList();
    }

    public string CreateThread(string? name = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = $"thread-{Interlocked.Increment(ref _nextThreadId)}";
        var thread = new CliThread
        {
            Id = id,
            Name = name ?? $"Thread {_nextThreadId}",
            Status = CliThreadStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            OutputBuffer = new StringBuilder()
        };

        _threads[id] = thread;
        _logger.LogInformation("Created CLI thread {Id}: {Name}", id, thread.Name);
        return id;
    }

    /// <summary>
    /// Send a command/prompt to a specific thread. Uses a Channel to decouple
    /// CLI stdout reading from UI dispatch, with 30ms batching of text deltas
    /// to reduce JSInterop overhead. Surfaces MCP connection progress for UX.
    /// </summary>
    public async Task SendCommandAsync(
        string threadId,
        string command,
        Action<string> onEvent,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_threads.TryGetValue(threadId, out var thread))
            throw new ArgumentException($"Thread {threadId} not found");

        thread.Status = CliThreadStatus.Busy;
        thread.LastActivityAt = DateTime.UtcNow;
        thread.CommandCount++;

        // Record user command as a history event
        thread.RecordEvent(MakeEvent("user_command", command));

        // Wrap onEvent to also record for session persistence
        void emit(string evt) { thread.RecordEvent(evt); onEvent(evt); }

        var eventChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        try
        {
            // Build args â€” reuse session on subsequent commands to skip MCP reconnection
            var args = "--output-format json --no-auto-update --silent --no-color --no-ask-user --allow-all";
            if (!string.IsNullOrEmpty(thread.SessionId))
                args += $" --resume={thread.SessionId}";

            var psi = new ProcessStartInfo
            {
                FileName = "copilot",
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                emit(MakeEvent("error", "Could not start copilot CLI process"));
                thread.Status = CliThreadStatus.Error;
                return;
            }

            _runnerJob?.Assign(process);
            thread.Process = process;

            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            // Producer: reads stdout continuously, never blocked by UI
            var producerTask = Task.Run(async () =>
            {
                var seenTurnStart = false;
                var toolCallNames = new Dictionary<string, string>(); // toolCallId â†’ toolName
                var writer = eventChannel.Writer;
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed)) continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(trimmed);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("type", out var typeEl)) continue;
                            var type = typeEl.GetString() ?? "";

                            var isEphemeral = root.TryGetProperty("ephemeral", out var eph) &&
                                              eph.ValueKind == JsonValueKind.True;

                            if (isEphemeral)
                            {
                                if (type == "session.mcp_server_status_changed" &&
                                    root.TryGetProperty("data", out var mcpData))
                                {
                                    var serverName = "";
                                    var status = "";
                                    if (mcpData.TryGetProperty("serverName", out var sn))
                                        serverName = sn.GetString() ?? "";
                                    if (mcpData.TryGetProperty("status", out var st))
                                        status = st.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(serverName))
                                        await writer.WriteAsync(
                                            MakeEvent("mcp_status", serverName + ":" + status), ct);
                                }
                                if (type == "session.tools_updated" &&
                                    root.TryGetProperty("data", out var toolsData) &&
                                    toolsData.TryGetProperty("model", out var modelEl))
                                {
                                    thread.Model = modelEl.GetString();
                                    await writer.WriteAsync(
                                        MakeEvent("model", thread.Model ?? ""), ct);
                                }
                                continue;
                            }

                            switch (type)
                            {
                                case "assistant.turn_start":
                                    if (!seenTurnStart)
                                    {
                                        seenTurnStart = true;
                                        await writer.WriteAsync(MakeEvent("thinking_start", ""), ct);
                                    }
                                    break;

                                case "assistant.message.delta":
                                    if (root.TryGetProperty("data", out var deltaData) &&
                                        deltaData.TryGetProperty("content", out var deltaContent))
                                    {
                                        var chunk = deltaContent.GetString() ?? "";
                                        if (!string.IsNullOrEmpty(chunk))
                                            await writer.WriteAsync(
                                                MakeEvent("text_delta", chunk), ct);
                                    }
                                    break;

                                case "assistant.message":
                                    // Extract full content â€” CLI may not emit deltas
                                    if (root.TryGetProperty("data", out var msgData) &&
                                        msgData.TryGetProperty("content", out var msgContent))
                                    {
                                        var fullText = msgContent.GetString() ?? "";
                                        if (!string.IsNullOrEmpty(fullText))
                                            await writer.WriteAsync(
                                                MakeEvent("text_delta", fullText), ct);
                                    }
                                    await writer.WriteAsync(MakeEvent("text_done", ""), ct);
                                    break;

                                case "tool.execution_start":
                                    if (root.TryGetProperty("data", out var toolStartData))
                                    {
                                        var toolName = "";
                                        var toolArgs = "";
                                        var toolCallId = "";
                                        if (toolStartData.TryGetProperty("toolName", out var tnEl))
                                            toolName = tnEl.GetString() ?? "";
                                        else if (toolStartData.TryGetProperty("name", out var nameEl))
                                            toolName = nameEl.GetString() ?? "";
                                        if (toolStartData.TryGetProperty("toolCallId", out var tcIdEl))
                                            toolCallId = tcIdEl.GetString() ?? "";
                                        if (!string.IsNullOrEmpty(toolCallId) && !string.IsNullOrEmpty(toolName))
                                            toolCallNames[toolCallId] = toolName;
                                        if (toolStartData.TryGetProperty("arguments", out var argsEl))
                                            toolArgs = argsEl.ValueKind == JsonValueKind.String
                                                ? argsEl.GetString() ?? ""
                                                : argsEl.GetRawText();
                                        await writer.WriteAsync(
                                            MakeToolEvent("tool_start", toolName, toolArgs, null, null), ct);
                                    }
                                    break;

                                case "tool.execution_complete":
                                    if (root.TryGetProperty("data", out var toolEndData))
                                    {
                                        var toolName = "";
                                        var success = true;
                                        var output = "";

                                        // Look up toolName via toolCallId (complete events lack toolName)
                                        if (toolEndData.TryGetProperty("toolCallId", out var tcIdEl2))
                                        {
                                            var tcId = tcIdEl2.GetString() ?? "";
                                            toolCallNames.TryGetValue(tcId, out toolName!);
                                            toolName ??= "";
                                        }
                                        if (string.IsNullOrEmpty(toolName))
                                        {
                                            if (toolEndData.TryGetProperty("toolName", out var tnEl2))
                                                toolName = tnEl2.GetString() ?? "";
                                        }

                                        if (toolEndData.TryGetProperty("success", out var successEl))
                                            success = successEl.GetBoolean();

                                        // Extract result.content (structured) or raw string
                                        if (toolEndData.TryGetProperty("result", out var resultEl))
                                        {
                                            if (resultEl.ValueKind == JsonValueKind.Object)
                                            {
                                                if (resultEl.TryGetProperty("content", out var rcEl))
                                                    output = rcEl.GetString() ?? "";
                                                else if (resultEl.TryGetProperty("detailedContent", out var rdcEl))
                                                    output = rdcEl.GetString() ?? "";
                                                else
                                                    output = resultEl.GetRawText();
                                            }
                                            else if (resultEl.ValueKind == JsonValueKind.String)
                                            {
                                                output = resultEl.GetString() ?? "";
                                            }
                                            else
                                            {
                                                output = resultEl.GetRawText();
                                            }
                                        }

                                        await writer.WriteAsync(
                                            MakeToolEvent("tool_complete", toolName, null, success, output), ct);
                                    }
                                    break;

                                case "result":
                                {
                                    var premiumReqs = 0;
                                    long sessionDurationMs = 0;
                                    if (root.TryGetProperty("usage", out var usageData))
                                    {
                                        if (usageData.TryGetProperty("premiumRequests", out var pr))
                                            premiumReqs = pr.GetInt32();
                                        if (usageData.TryGetProperty("sessionDurationMs", out var sd))
                                            sessionDurationMs = sd.GetInt64();
                                    }
                                    // Capture sessionId for --resume on subsequent commands
                                    if (root.TryGetProperty("sessionId", out var sidEl))
                                    {
                                        var sid = sidEl.GetString();
                                        if (!string.IsNullOrEmpty(sid))
                                            thread.SessionId = sid;
                                    }
                                    thread.TotalTokens += premiumReqs;
                                    await writer.WriteAsync(
                                        MakeResultEvent(premiumReqs, sessionDurationMs), ct);
                                    break;
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            await writer.WriteAsync(MakeEvent("text_delta", line + "\n"), ct);
                        }
                    }
                }
                finally
                {
                    writer.TryComplete();
                }
            }, ct);

            var errorTask = Task.Run(async () =>
            {
                var buffer = new char[256];
                int bytesRead;
                while ((bytesRead = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                    _logger.LogDebug("CLI stderr: {Chunk}", new string(buffer, 0, bytesRead));
            }, ct);

            // Consumer: reads from channel, batches text_deltas for ~30ms
            var textBatch = new StringBuilder();
            var lastFlush = DateTime.UtcNow;

            await foreach (var evt in eventChannel.Reader.ReadAllAsync(ct))
            {
                if (evt.Contains("\"text_delta\""))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(evt);
                        var content = doc.RootElement.GetProperty("content").GetString() ?? "";
                        textBatch.Append(content);
                    }
                    catch { textBatch.Append(evt); }

                    var elapsed = (DateTime.UtcNow - lastFlush).TotalMilliseconds;
                    if (elapsed >= 30 || textBatch.Length > 4096)
                    {
                        emit(MakeEvent("text_delta", textBatch.ToString()));
                        textBatch.Clear();
                        lastFlush = DateTime.UtcNow;
                    }
                    continue;
                }

                // Flush pending text before any non-text event
                if (textBatch.Length > 0)
                {
                    emit(MakeEvent("text_delta", textBatch.ToString()));
                    textBatch.Clear();
                    lastFlush = DateTime.UtcNow;
                }

                emit(evt);
            }

            if (textBatch.Length > 0)
                emit(MakeEvent("text_delta", textBatch.ToString()));

            await Task.WhenAll(producerTask, errorTask);
            await process.WaitForExitAsync(ct);

            thread.Status = process.ExitCode == 0
                ? CliThreadStatus.Ready
                : CliThreadStatus.Error;

            if (process.ExitCode != 0)
                emit(MakeEvent("error", $"Process exited with code {process.ExitCode}"));

            emit(MakeEvent("command_done", ""));
        }
        catch (OperationCanceledException)
        {
            emit(MakeEvent("cancelled", ""));
            thread.Status = CliThreadStatus.Ready;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing command in thread {ThreadId}", threadId);
            emit(MakeEvent("error", ex.Message));
            thread.Status = CliThreadStatus.Error;
        }
        finally
        {
            thread.LastActivityAt = DateTime.UtcNow;
            if (thread.Process is not null)
            {
                try { if (!thread.Process.HasExited) thread.Process.Kill(entireProcessTree: true); }
                catch { }
                thread.Process.Dispose();
                thread.Process = null;
            }
        }
    }

    public string GetThreadOutput(string threadId) =>
        _threads.TryGetValue(threadId, out var thread) ? thread.OutputBuffer.ToString() : string.Empty;

    /// <summary>Get recorded event history for session replay on page revisit.</summary>
    public IReadOnlyList<string> GetThreadHistory(string threadId) =>
        _threads.TryGetValue(threadId, out var thread) ? thread.EventHistory : [];

    public void ClearThread(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var thread))
        {
            thread.OutputBuffer.Clear();
            thread.EventHistory.Clear();
            thread.EventHistoryBytes = 0;
            thread.LastActivityAt = DateTime.UtcNow;
        }
    }

    public void CloseThread(string threadId)
    {
        if (_threads.TryRemove(threadId, out var thread))
        {
            if (thread.Process is not null)
            {
                try { if (!thread.Process.HasExited) thread.Process.Kill(entireProcessTree: true); } catch { }
                thread.Process.Dispose();
            }
            _logger.LogInformation("Closed CLI thread {Id}", threadId);
        }
    }

    public void CancelCommand(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var thread) && thread.Process is not null)
        {
            try
            {
                if (!thread.Process.HasExited)
                {
                    thread.Process.Kill(entireProcessTree: true);
                    _logger.LogInformation("Cancelled command in thread {Id}", threadId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to kill process in thread {Id}", threadId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var thread in _threads.Values)
        {
            if (thread.Process is not null)
            {
                try { if (!thread.Process.HasExited) thread.Process.Kill(entireProcessTree: true); } catch { }
                thread.Process.Dispose();
            }
        }
        _threads.Clear();
    }

    // --- Event JSON helpers ---

    private static string MakeEvent(string type, string content) =>
        JsonSerializer.Serialize(new { type, content });

    private static string MakeToolEvent(string type, string name, string? args, bool? success, string? output) =>
        JsonSerializer.Serialize(new { type, name, args, success, output });

    private static string MakeResultEvent(int premiumRequests, long sessionDurationMs) =>
        JsonSerializer.Serialize(new { type = "result", premiumRequests, sessionDurationMs });

    private class CliThread
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public CliThreadStatus Status { get; set; }
        public string? Model { get; set; }
        public string? SessionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public required StringBuilder OutputBuffer { get; set; }
        public Process? Process { get; set; }
        public int CommandCount { get; set; }
        public int TotalTokens { get; set; }

        // Event history for session persistence across page navigations
        public List<string> EventHistory { get; } = new();
        public long EventHistoryBytes { get; set; }
        private const long MaxHistoryBytes = 2 * 1024 * 1024; // 2 MB

        public void RecordEvent(string eventJson)
        {
            EventHistory.Add(eventJson);
            EventHistoryBytes += eventJson.Length;

            // Trim oldest events when over limit — remove in bulk for efficiency
            if (EventHistoryBytes > MaxHistoryBytes && EventHistory.Count > 10)
            {
                var removeCount = EventHistory.Count / 4; // drop oldest 25%
                long removedBytes = 0;
                for (var i = 0; i < removeCount; i++)
                    removedBytes += EventHistory[i].Length;
                EventHistory.RemoveRange(0, removeCount);
                EventHistoryBytes -= removedBytes;
                if (EventHistoryBytes < 0) EventHistoryBytes = 0;
            }
        }
    }
}

public enum CliThreadStatus
{
    Ready,
    Busy,
    Error
}

public record CliThreadInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CliThreadStatus Status { get; init; }
    public string? Model { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivityAt { get; init; }
    public required bool IsProcessRunning { get; init; }
    public int CommandCount { get; init; }
    public int TotalTokens { get; init; }
}

