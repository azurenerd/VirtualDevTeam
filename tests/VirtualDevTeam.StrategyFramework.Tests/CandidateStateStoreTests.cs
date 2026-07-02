using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using Xunit;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class CandidateStateStoreTests
{
    [Fact]
    public void Started_event_creates_task_and_running_candidate()
    {
        var store = new CandidateStateStore();
        var at = DateTimeOffset.UtcNow;

        store.RecordStarted(new CandidateStartedEvent("run1", "task1", "baseline", at));

        var active = store.GetActiveTasks();
        Assert.Single(active);
        var t = active[0];
        Assert.Equal("run1", t.RunId);
        Assert.Equal("task1", t.TaskId);
        Assert.True(t.Candidates.ContainsKey("baseline"));
        Assert.Equal(CandidateState.Running, t.Candidates["baseline"].State);
        Assert.Equal(at, t.Candidates["baseline"].StartedAt);
    }

    [Fact]
    public void Second_strategy_started_merges_into_existing_task()
    {
        var store = new CandidateStateStore();
        var at = DateTimeOffset.UtcNow;

        store.RecordStarted(new CandidateStartedEvent("run1", "task1", "baseline", at));
        store.RecordStarted(new CandidateStartedEvent("run1", "task1", "mcp-enhanced", at.AddMilliseconds(10)));

        var active = store.GetActiveTasks();
        Assert.Single(active);
        Assert.Equal(2, active[0].Candidates.Count);
        Assert.Contains("baseline", active[0].Candidates.Keys);
        Assert.Contains("mcp-enhanced", active[0].Candidates.Keys);
    }

    [Fact]
    public void Completed_event_updates_candidate_state()
    {
        var store = new CandidateStateStore();
        var at = DateTimeOffset.UtcNow;
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", at));

        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.5, 42));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal(CandidateState.Completed, c.State);
        Assert.True(c.Succeeded);
        Assert.Equal(1.5, c.ElapsedSec);
        Assert.Equal(42L, c.TokensUsed);
    }

    [Fact]
    public void Scored_event_updates_scores_and_state()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));

        store.RecordScored(new CandidateScoredEvent("r", "t", "baseline", 8, 7, 9));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal(CandidateState.Scored, c.State);
        Assert.Equal(8, c.AcScore);
        Assert.Equal(7, c.DesignScore);
        Assert.Equal(9, c.ReadabilityScore);
    }

    [Fact]
    public void Winner_event_moves_task_from_active_to_recent()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordStarted(new CandidateStartedEvent("r", "t", "mcp-enhanced", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "mcp-enhanced", true, null, 1.1, 12));
        store.RecordScored(new CandidateScoredEvent("r", "t", "baseline", 8, 7, 9));
        store.RecordScored(new CandidateScoredEvent("r", "t", "mcp-enhanced", 9, 8, 9));

        store.RecordWinner(new WinnerSelectedEvent("r", "t", "mcp-enhanced", "higher-total-score", 0.4));

        Assert.Empty(store.GetActiveTasks());
        var recent = store.GetRecentTasks();
        Assert.Single(recent);
        Assert.Equal("mcp-enhanced", recent[0].WinnerStrategyId);
        Assert.Equal(CandidateState.Winner, recent[0].Candidates["mcp-enhanced"].State);
        Assert.Equal("higher-total-score", recent[0].TieBreakReason);
    }

    [Fact]
    public void OnChange_fires_for_each_mutation()
    {
        var store = new CandidateStateStore();
        var count = 0;
        store.OnChange += _ => Interlocked.Increment(ref count);

        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordScored(new CandidateScoredEvent("r", "t", "baseline", 8, 7, 9));
        store.RecordWinner(new WinnerSelectedEvent("r", "t", "baseline", "only-survivor", 0.1));

        Assert.Equal(4, count);
    }

    [Fact]
    public void Recent_ring_respects_capacity()
    {
        var store = new CandidateStateStore(recentCapacity: 3);

        for (var i = 0; i < 5; i++)
        {
            var taskId = $"t{i}";
            store.RecordStarted(new CandidateStartedEvent("r", taskId, "baseline", DateTimeOffset.UtcNow));
            store.RecordWinner(new WinnerSelectedEvent("r", taskId, "baseline", "solo", 0.1));
        }

        var recent = store.GetRecentTasks();
        Assert.Equal(3, recent.Count);
        Assert.Equal("t4", recent[0].TaskId);
        Assert.Equal("t3", recent[1].TaskId);
        Assert.Equal("t2", recent[2].TaskId);
    }

    [Fact]
    public void ArchiveTaskIfActive_moves_task_without_winner()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", false, "gate-failed", 1.0, 5));

        store.ArchiveTaskIfActive("r", "t", "all-candidates-failed");

        Assert.Empty(store.GetActiveTasks());
        var recent = store.GetRecentTasks();
        Assert.Single(recent);
        Assert.Null(recent[0].WinnerStrategyId);
        Assert.Equal("all-candidates-failed", recent[0].TieBreakReason);
    }

    [Fact]
    public void Late_events_for_unknown_task_are_silently_ignored()
    {
        var store = new CandidateStateStore();

        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordScored(new CandidateScoredEvent("r", "t", "baseline", 8, 7, 9));
        store.RecordWinner(new WinnerSelectedEvent("r", "t", "baseline", "orphan", 0.0));

        Assert.Empty(store.GetActiveTasks());
        Assert.Empty(store.GetRecentTasks());
    }

    // ---- InitialScored tests ---------------------------------------------------

    [Fact]
    public void InitialScored_event_stores_all_initial_scores_and_feedback()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordEvaluated(new CandidateEvaluatedEvent("r", "t", "baseline", true, null, null, "screenshot-data", null));

        store.RecordInitialScored(new CandidateInitialScoredEvent(
            "r", "t", "baseline",
            AcScore: 7, DesignScore: 8, ReadabilityScore: 9, VisualsScore: 6,
            Feedback: "Overall solid work",
            ScreenshotBase64: "ss-initial",
            AcFeedback: "AC needs minor fix", DesignFeedback: "Clean design",
            ReadabilityFeedback: "Clear code", VisualsFeedback: "Layout spacing off"));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal(CandidateState.InitialScored, c.State);
        Assert.Equal(7, c.InitialAcScore);
        Assert.Equal(8, c.InitialDesignScore);
        Assert.Equal(9, c.InitialReadabilityScore);
        Assert.Equal(6, c.InitialVisualsScore);
        Assert.Equal("Overall solid work", c.JudgeFeedback);
        Assert.Equal("AC needs minor fix", c.InitialAcFeedback);
        Assert.Equal("Clean design", c.InitialDesignFeedback);
        Assert.Equal("Clear code", c.InitialReadabilityFeedback);
        Assert.Equal("Layout spacing off", c.InitialVisualsFeedback);
        Assert.Equal("ss-initial", c.InitialScreenshotBase64);
    }

    [Fact]
    public void InitialScored_with_null_feedback_stores_nulls_gracefully()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));

        store.RecordInitialScored(new CandidateInitialScoredEvent(
            "r", "t", "baseline",
            AcScore: 5, DesignScore: 6, ReadabilityScore: 7, VisualsScore: null,
            Feedback: null, ScreenshotBase64: null));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal(CandidateState.InitialScored, c.State);
        Assert.Equal(5, c.InitialAcScore);
        Assert.Equal(6, c.InitialDesignScore);
        Assert.Equal(7, c.InitialReadabilityScore);
        Assert.Null(c.InitialVisualsScore);
        Assert.Null(c.JudgeFeedback);
        Assert.Null(c.InitialAcFeedback);
    }

    [Fact]
    public void InitialScored_preserves_screenshot_from_evaluated_event()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordEvaluated(new CandidateEvaluatedEvent("r", "t", "baseline", true, null, null, "eval-screenshot", null));

        // InitialScored with null screenshot should preserve the one from Evaluated
        store.RecordInitialScored(new CandidateInitialScoredEvent(
            "r", "t", "baseline",
            AcScore: 8, DesignScore: 8, ReadabilityScore: 8, VisualsScore: null,
            Feedback: "Good", ScreenshotBase64: null));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal("eval-screenshot", c.InitialScreenshotBase64);
    }

    [Fact]
    public void InitialScored_then_revision_then_scored_preserves_initial_feedback()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));

        store.RecordInitialScored(new CandidateInitialScoredEvent(
            "r", "t", "baseline",
            AcScore: 5, DesignScore: 6, ReadabilityScore: 7, VisualsScore: null,
            Feedback: "Initial feedback",
            ScreenshotBase64: "initial-ss",
            AcFeedback: "AC was weak", DesignFeedback: "Design was OK"));

        store.RecordRevisionStarted(new CandidateRevisionStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));

        // After revision, candidate gets final scored — this should NOT wipe initial feedback
        store.RecordScored(new CandidateScoredEvent(
            "r", "t", "baseline",
            AcScore: 8, DesignScore: 9, ReadabilityScore: 8,
            Feedback: "Much improved after revision",
            AcFeedback: "AC now meets criteria", DesignFeedback: "Design is clean"));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];

        // Final scores
        Assert.Equal(CandidateState.Scored, c.State);
        Assert.Equal(8, c.AcScore);
        Assert.Equal(9, c.DesignScore);
        Assert.Equal(8, c.ReadabilityScore);
        Assert.Equal("Much improved after revision", c.JudgeFeedback);

        // Initial scores preserved
        Assert.Equal(5, c.InitialAcScore);
        Assert.Equal(6, c.InitialDesignScore);
        Assert.Equal(7, c.InitialReadabilityScore);

        // Initial feedback preserved (RecordScored should not overwrite Initial* fields)
        Assert.Equal("AC was weak", c.InitialAcFeedback);
        Assert.Equal("Design was OK", c.InitialDesignFeedback);
    }

    [Fact]
    public void Scored_event_preserves_existing_screenshot()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));
        store.RecordEvaluated(new CandidateEvaluatedEvent("r", "t", "baseline", true, null, null, "original-ss", null));

        // RecordScored with null screenshot should preserve the existing one
        store.RecordScored(new CandidateScoredEvent("r", "t", "baseline", 8, 7, 9, ScreenshotBase64: null));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal("original-ss", c.ScreenshotBase64);
    }

    [Fact]
    public void Scored_event_stores_per_dimension_feedback()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("r", "t", "baseline", true, null, 1.0, 10));

        store.RecordScored(new CandidateScoredEvent(
            "r", "t", "baseline", 8, 7, 9,
            Feedback: "Good overall",
            AcFeedback: "Meets all AC", DesignFeedback: "Solid structure",
            ReadabilityFeedback: "Very clear"));

        var c = store.GetActiveTasks()[0].Candidates["baseline"];
        Assert.Equal("Good overall", c.JudgeFeedback);
        Assert.Equal("Meets all AC", c.AcFeedback);
        Assert.Equal("Solid structure", c.DesignFeedback);
        Assert.Equal("Very clear", c.ReadabilityFeedback);
    }
}
