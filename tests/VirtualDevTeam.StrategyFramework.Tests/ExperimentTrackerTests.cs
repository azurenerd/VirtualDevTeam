using System.Text.Json;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class ExperimentTrackerTests
{
    private sealed class StaticMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        private readonly T _v;
        public StaticMonitor(T v) { _v = v; }
        public T CurrentValue => _v;
        public T Get(string? name) => _v;
        public IDisposable OnChange(Action<T, string?> listener) => new Null();
        private sealed class Null : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Write_appends_ndjson_record_per_call()
    {
        var temp = Path.Combine(Path.GetTempPath(), "strategy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new StrategyFrameworkConfig { ExperimentDataDirectory = temp };
            var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, new StaticMonitor<StrategyFrameworkConfig>(cfg));

            var record1 = NewRecord("run-1", "task-1", "baseline");
            var record2 = NewRecord("run-1", "task-2", "baseline");
            tracker.Write(record1);
            tracker.Write(record2);

            var file = tracker.ResolveFile("run-1");
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                var doc = JsonDocument.Parse(line);
                Assert.Equal("run-1", doc.RootElement.GetProperty("runId").GetString());
                Assert.Equal("baseline", doc.RootElement.GetProperty("winnerStrategyId").GetString());
            }
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void ResolveFile_sanitizes_run_id()
    {
        var cfg = new StrategyFrameworkConfig { ExperimentDataDirectory = Path.GetTempPath() };
        var tracker = new ExperimentTracker(NullLogger<ExperimentTracker>.Instance, new StaticMonitor<StrategyFrameworkConfig>(cfg));

        var file = tracker.ResolveFile("run/with:slashes*and?chars");
        Assert.DoesNotContain(":", Path.GetFileName(file));
        Assert.DoesNotContain("*", Path.GetFileName(file));
        Assert.EndsWith(".ndjson", file);
    }

    private static ExperimentRecord NewRecord(string runId, string taskId, string winner) => new()
    {
        RunId = runId,
        TaskId = taskId,
        TaskTitle = "t",
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Candidates = new List<CandidateRecord>
        {
            new() { StrategyId = winner, Succeeded = true, ElapsedSec = 1, PatchSizeBytes = 10, TokensUsed = 0 }
        },
        WinnerStrategyId = winner,
        TieBreakReason = "sole-survivor",
        EvaluationElapsedSec = 0.1,
        TotalTokens = 0,
    };
}
