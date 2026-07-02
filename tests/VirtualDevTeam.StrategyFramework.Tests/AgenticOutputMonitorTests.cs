using System.Text;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Unit tests for <see cref="AgenticOutputMonitor"/> (<c>p3-agentic-watchdog</c>).
/// Covers: clean EOF, stuck-detector firing, tool-call cap firing, JSON-off
/// disables tool counting but keeps stuck detection.
/// </summary>
public class AgenticOutputMonitorTests
{
    private static StreamReader StreamOf(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private static AgenticConfig Cfg(int stuckSec = 30, int toolCap = 500) =>
        new() { StuckSeconds = stuckSec, ToolCallCap = toolCap };

    [Fact]
    public async Task Run_returns_cleanly_on_eof_without_violation()
    {
        var monitor = new AgenticOutputMonitor(Cfg(), NullLogger.Instance, jsonMode: true);
        var buf = new StringBuilder();
        using var killSource = new CancellationTokenSource();

        await monitor.RunAsync(
            StreamOf("plain assistant output line 1\nline 2\n"),
            buf, killSource, CancellationToken.None);

        Assert.Null(monitor.FailureReason);
        Assert.Equal(0, monitor.ToolCallCount);
        Assert.False(killSource.IsCancellationRequested);
        Assert.Contains("line 1", buf.ToString());
        Assert.Contains("line 2", buf.ToString());
    }

    [Fact]
    public async Task Json_mode_counts_tool_events_and_fires_cap()
    {
        // Emit 3 tool events against a cap of 2 → monitor cancels after the 3rd.
        var payload = new StringBuilder()
            .AppendLine("{\"type\":\"assistant.message\",\"data\":{\"content\":\"hi\"}}")
            .AppendLine("{\"type\":\"tool.execution_start\",\"name\":\"read\"}")
            .AppendLine("{\"type\":\"tool.execution_complete\",\"name\":\"read\"}")
            .AppendLine("{\"type\":\"tool.execution_start\",\"name\":\"write\"}")
            .AppendLine("{\"type\":\"assistant.message\",\"data\":{\"content\":\"done\"}}")
            .ToString();

        var monitor = new AgenticOutputMonitor(Cfg(toolCap: 2), NullLogger.Instance, jsonMode: true);
        var buf = new StringBuilder();
        using var killSource = new CancellationTokenSource();

        await monitor.RunAsync(StreamOf(payload), buf, killSource, CancellationToken.None);

        Assert.Equal(AgenticFailureReason.ToolCallCap, monitor.FailureReason);
        Assert.True(monitor.ToolCallCount > 2, $"count={monitor.ToolCallCount}");
        Assert.True(killSource.IsCancellationRequested);
    }

    [Fact]
    public async Task Non_json_mode_ignores_tool_lines_for_cap()
    {
        var payload = string.Concat(Enumerable.Repeat(
            "{\"type\":\"tool.execution_start\",\"name\":\"read\"}\n", 10));
        var monitor = new AgenticOutputMonitor(Cfg(toolCap: 1), NullLogger.Instance, jsonMode: false);
        var buf = new StringBuilder();
        using var killSource = new CancellationTokenSource();

        await monitor.RunAsync(StreamOf(payload), buf, killSource, CancellationToken.None);

        Assert.Null(monitor.FailureReason);
        Assert.Equal(0, monitor.ToolCallCount);
        Assert.False(killSource.IsCancellationRequested);
    }

    [Fact]
    public async Task Stuck_detector_fires_when_stream_stalls()
    {
        // Build a stream that yields one line, then hangs forever. We use a
        // SlowStream wrapper that blocks reads after the first line until ct.
        var slowReader = new StreamReader(new SlowStream(
            initial: "first line\n",
            stallAfter: TimeSpan.Zero,
            hangForever: true));

        var monitor = new AgenticOutputMonitor(Cfg(stuckSec: 1), NullLogger.Instance, jsonMode: true);
        var buf = new StringBuilder();
        using var killSource = new CancellationTokenSource();

        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await monitor.RunAsync(slowReader, buf, killSource, runCts.Token);

        Assert.Equal(AgenticFailureReason.StuckNoOutput, monitor.FailureReason);
        Assert.True(killSource.IsCancellationRequested);
        Assert.Contains("first line", buf.ToString());
    }

    [Fact]
    public async Task Outer_cancel_exits_quietly_without_setting_failure()
    {
        // Outer cancellation (process already being torn down) shouldn't flag
        // FailureReason — the caller will classify via exit code / timeout.
        var slowReader = new StreamReader(new SlowStream("line\n", TimeSpan.Zero, hangForever: true));
        var monitor = new AgenticOutputMonitor(Cfg(stuckSec: 600), NullLogger.Instance, jsonMode: true);
        var buf = new StringBuilder();
        using var killSource = new CancellationTokenSource();

        using var outer = new CancellationTokenSource();
        var task = monitor.RunAsync(slowReader, buf, killSource, outer.Token);
        await Task.Delay(100);
        outer.Cancel();
        await task;

        Assert.Null(monitor.FailureReason);
        Assert.False(killSource.IsCancellationRequested);
    }

    /// <summary>
    /// A stream that emits <paramref name="initial"/> bytes, then stalls forever
    /// (reads block on a TaskCompletionSource that is never completed). Used to
    /// drive the stuck detector deterministically.
    /// </summary>
    private sealed class SlowStream : Stream
    {
        private readonly byte[] _bytes;
        private int _pos;
        private readonly bool _hangForever;
        private readonly TimeSpan _stallAfter;

        public SlowStream(string initial, TimeSpan stallAfter, bool hangForever)
        {
            _bytes = Encoding.UTF8.GetBytes(initial);
            _stallAfter = stallAfter;
            _hangForever = hangForever;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < _bytes.Length)
            {
                var remaining = _bytes.Length - _pos;
                var n = Math.Min(count, remaining);
                Array.Copy(_bytes, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            if (_hangForever)
            {
                // Block indefinitely on a semaphore that nobody will ever release.
                var sem = new SemaphoreSlim(0, 1);
                sem.Wait();
                return 0;
            }
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _bytes.Length)
            {
                var remaining = _bytes.Length - _pos;
                var n = Math.Min(buffer.Length, remaining);
                _bytes.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                if (_stallAfter > TimeSpan.Zero)
                    await Task.Delay(_stallAfter, ct);
                return n;
            }
            if (_hangForever)
            {
                // Block until cancelled.
                await Task.Delay(Timeout.Infinite, ct);
            }
            return 0;
        }
    }
}
