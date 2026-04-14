using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Manages parallel Copilot CLI sessions ("threads") for the Director CLI tab.
/// Each thread is an independent copilot process that can handle conversations
/// without blocking other threads. This lets the user fire off a question in
/// Thread 1 and immediately switch to Thread 2 for another question.
/// </summary>
public class DirectorCliService : IDisposable
{
    private readonly ILogger<DirectorCliService> _logger;
    private readonly ConcurrentDictionary<string, CliThread> _threads = new();
    private bool _disposed;
    private int _nextThreadId;

    public DirectorCliService(ILogger<DirectorCliService> logger)
    {
        _logger = logger;
    }

    /// <summary>All active thread IDs and their current state.</summary>
    public IReadOnlyList<CliThreadInfo> GetThreads()
    {
        return _threads.Values
            .OrderBy(t => t.CreatedAt)
            .Select(t => new CliThreadInfo
            {
                Id = t.Id,
                Name = t.Name,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                LastActivityAt = t.LastActivityAt,
                IsProcessRunning = t.Process is not null && !t.Process.HasExited
            })
            .ToList();
    }

    /// <summary>Create a new CLI thread with an independent copilot process.</summary>
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
    /// Send a command/prompt to a specific thread. The thread will start
    /// or reuse its copilot process and pipe the prompt via stdin.
    /// Returns immediately — use GetOutput to stream the response.
    /// </summary>
    public async Task SendCommandAsync(
        string threadId,
        string command,
        Action<string> onOutput,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_threads.TryGetValue(threadId, out var thread))
            throw new ArgumentException($"Thread {threadId} not found");

        thread.Status = CliThreadStatus.Busy;
        thread.LastActivityAt = DateTime.UtcNow;

        // Append the user command to the output buffer
        onOutput($"\r\n\x1b[36m❯\x1b[0m {command}\r\n");

        try
        {
            // Start a fresh copilot process for each command
            // (copilot CLI is designed for single-prompt execution)
            var psi = new ProcessStartInfo
            {
                FileName = "copilot",
                Arguments = "--no-auto-update --no-custom-instructions --silent --no-color --no-ask-user --allow-all",
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
                onOutput("\r\n\x1b[31mError: Could not start copilot CLI process\x1b[0m\r\n");
                thread.Status = CliThreadStatus.Error;
                return;
            }

            thread.Process = process;

            // Write the command to stdin and close it
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            // Stream stdout in real-time, converting markdown to ANSI per line
            var outputTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    var ansi = MarkdownToAnsi(line);
                    onOutput(ansi + "\r\n");
                    thread.OutputBuffer.AppendLine(ansi);
                }
            }, ct);

            // Also capture stderr
            var errorTask = Task.Run(async () =>
            {
                var buffer = new char[256];
                int bytesRead;
                while ((bytesRead = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                {
                    var chunk = new string(buffer, 0, bytesRead);
                    chunk = chunk.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
                    onOutput($"\x1b[33m{chunk}\x1b[0m"); // Yellow for stderr
                    thread.OutputBuffer.Append(chunk);
                }
            }, ct);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);

            thread.Status = process.ExitCode == 0
                ? CliThreadStatus.Ready
                : CliThreadStatus.Error;

            if (process.ExitCode != 0)
            {
                onOutput($"\r\n\x1b[31m[Process exited with code {process.ExitCode}]\x1b[0m\r\n");
            }

            onOutput("\r\n\x1b[32m✓\x1b[0m Ready\r\n");
        }
        catch (OperationCanceledException)
        {
            onOutput("\r\n\x1b[33m[Cancelled]\x1b[0m\r\n");
            thread.Status = CliThreadStatus.Ready;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing command in thread {ThreadId}", threadId);
            onOutput($"\r\n\x1b[31mError: {ex.Message}\x1b[0m\r\n");
            thread.Status = CliThreadStatus.Error;
        }
        finally
        {
            thread.LastActivityAt = DateTime.UtcNow;
            // Clean up the process
            if (thread.Process is not null)
            {
                try
                {
                    if (!thread.Process.HasExited)
                        thread.Process.Kill(entireProcessTree: true);
                }
                catch { }
                thread.Process.Dispose();
                thread.Process = null;
            }
        }
    }

    /// <summary>Get the full output history for a thread.</summary>
    public string GetThreadOutput(string threadId)
    {
        return _threads.TryGetValue(threadId, out var thread)
            ? thread.OutputBuffer.ToString()
            : string.Empty;
    }

    /// <summary>Clear a thread's output buffer.</summary>
    public void ClearThread(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var thread))
        {
            thread.OutputBuffer.Clear();
            thread.LastActivityAt = DateTime.UtcNow;
        }
    }

    /// <summary>Close and remove a thread.</summary>
    public void CloseThread(string threadId)
    {
        if (_threads.TryRemove(threadId, out var thread))
        {
            if (thread.Process is not null)
            {
                try
                {
                    if (!thread.Process.HasExited)
                        thread.Process.Kill(entireProcessTree: true);
                }
                catch { }
                thread.Process.Dispose();
            }
            _logger.LogInformation("Closed CLI thread {Id}", threadId);
        }
    }

    /// <summary>Cancel the currently running command in a thread.</summary>
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
                try
                {
                    if (!thread.Process.HasExited)
                        thread.Process.Kill(entireProcessTree: true);
                }
                catch { }
                thread.Process.Dispose();
            }
        }
        _threads.Clear();
    }

    private class CliThread
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public CliThreadStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public required StringBuilder OutputBuffer { get; set; }
        public Process? Process { get; set; }
    }

    /// <summary>Convert a single line of markdown to ANSI escape codes for xterm rendering.</summary>
    private static string MarkdownToAnsi(string line)
    {
        // Headers: ### → bold cyan
        if (line.StartsWith("#### "))
            return $"\x1b[1;36m{line[5..]}\x1b[0m";
        if (line.StartsWith("### "))
            return $"\x1b[1;36m{line[4..]}\x1b[0m";
        if (line.StartsWith("## "))
            return $"\x1b[1;96m{line[3..]}\x1b[0m";
        if (line.StartsWith("# "))
            return $"\x1b[1;97m{line[2..]}\x1b[0m";

        // Horizontal rules
        if (line.Trim() is "---" or "***" or "___")
            return "\x1b[90m────────────────────────────────────────\x1b[0m";

        // Bullet lists: - or * → cyan bullet
        if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
        {
            var indent = line.Length - line.TrimStart().Length;
            var content = line.TrimStart()[2..];
            content = ApplyInlineFormatting(content);
            return new string(' ', indent) + $"\x1b[36m•\x1b[0m {content}";
        }

        // Numbered lists: keep but apply inline formatting
        if (line.TrimStart().Length > 0 && char.IsDigit(line.TrimStart()[0]) && line.Contains(". "))
        {
            return ApplyInlineFormatting(line);
        }

        // Code blocks (``` lines) → dim
        if (line.TrimStart().StartsWith("```"))
            return $"\x1b[90m{line}\x1b[0m";

        // Regular lines: apply inline formatting
        return ApplyInlineFormatting(line);
    }

    /// <summary>Apply inline markdown formatting: **bold**, *italic*, `code`.</summary>
    private static string ApplyInlineFormatting(string text)
    {
        // Bold: **text** → bright white bold
        while (text.Contains("**"))
        {
            var start = text.IndexOf("**", StringComparison.Ordinal);
            var end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (end < 0) break;
            var inner = text[(start + 2)..end];
            text = string.Concat(text[..start], "\x1b[1;97m", inner, "\x1b[0m", text[(end + 2)..]);
        }

        // Inline code: `text` → yellow on dark bg
        while (text.Contains('`'))
        {
            var start = text.IndexOf('`');
            var end = text.IndexOf('`', start + 1);
            if (end < 0) break;
            var inner = text[(start + 1)..end];
            text = string.Concat(text[..start], "\x1b[33m", inner, "\x1b[0m", text[(end + 1)..]);
        }

        return text;
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
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivityAt { get; init; }
    public required bool IsProcessRunning { get; init; }
}
