using AgentSquad.Core.Strategies;
using AgentSquad.Core.Strategies.Contracts;
using Xunit;

namespace AgentSquad.StrategyFramework.Tests;

public class VisualJudgeTests
{
    [Fact]
    public async Task NullVisualJudge_ReturnsEmptyScores()
    {
        var judge = new NullVisualJudge();
        var input = new VisualJudgeInput
        {
            TaskId = "t1",
            TaskTitle = "Test",
            TaskDescription = "Build a test app",
            CandidateScreenshots = new Dictionary<string, byte[]>
            {
                ["baseline"] = new byte[] { 1, 2, 3 },
            },
        };

        var result = await judge.ScoreAsync(input, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public void VisualScore_ClampsToRange()
    {
        var score = new VisualScore { Score = 5, Reasoning = "looks ok" };
        Assert.Equal(5, score.Score);
        Assert.Equal("looks ok", score.Reasoning);
    }

    [Fact]
    public void VisualJudgeResult_EmptyScoresProperty()
    {
        var result = new VisualJudgeResult { Scores = new Dictionary<string, VisualScore>() };
        Assert.Empty(result.Scores);
        Assert.Null(result.Error);
        Assert.Equal(0, result.TokensUsed);
        Assert.True(result.IsFallback);
    }

    [Fact]
    public void CandidateScore_VisualsScore_NullByDefault()
    {
        var score = new CandidateScore();
        Assert.Null(score.VisualsScore);
    }

    [Fact]
    public void CandidateScore_VisualsScore_IncludesInEquality()
    {
        var a = new CandidateScore { AcceptanceCriteriaScore = 8, DesignScore = 7, ReadabilityScore = 6, VisualsScore = 9 };
        var b = new CandidateScore { AcceptanceCriteriaScore = 8, DesignScore = 7, ReadabilityScore = 6, VisualsScore = 5 };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ScoreSummary_Total_IncludesVisuals_WhenPresent()
    {
        var summary = new ScoreSummary
        {
            AcceptanceCriteria = 8,
            Design = 7,
            Readability = 6,
            Visuals = 9,
        };
        Assert.Equal(30, summary.Total);
        Assert.Equal(40, summary.MaxScore);
    }

    [Fact]
    public void ScoreSummary_Total_ExcludesVisuals_WhenNull()
    {
        var summary = new ScoreSummary
        {
            AcceptanceCriteria = 8,
            Design = 7,
            Readability = 6,
            Visuals = null,
        };
        Assert.Equal(21, summary.Total);
        Assert.Equal(30, summary.MaxScore);
    }

    [Fact]
    public void ScoreSummary_Total_AddsZeroVisuals_WhenZero()
    {
        var summary = new ScoreSummary
        {
            AcceptanceCriteria = 8,
            Design = 7,
            Readability = 6,
            Visuals = 0,
        };
        Assert.Equal(21, summary.Total);
        Assert.Equal(40, summary.MaxScore);
    }

    [Fact]
    public void CandidateSnapshot_VisualsScore_PropagatesInWith()
    {
        var original = new CandidateSnapshot
        {
            StrategyId = "baseline",
            State = CandidateState.Scored,
            AcScore = 8,
            DesignScore = 7,
            ReadabilityScore = 6,
        };

        var updated = original with { VisualsScore = 9 };
        Assert.Equal(9, updated.VisualsScore);
        Assert.Null(original.VisualsScore);
    }

    [Fact]
    public void CandidateScoredEvent_VisualsScore_DefaultsToNull()
    {
        var e = new CandidateScoredEvent("run1", "t1", "baseline", 8, 7, 6);
        Assert.Null(e.VisualsScore);
    }

    [Fact]
    public void CandidateScoredEvent_VisualsScore_CanBeSet()
    {
        var e = new CandidateScoredEvent("run1", "t1", "baseline", 8, 7, 6, 9);
        Assert.Equal(9, e.VisualsScore);
    }

    [Fact]
    public void RecordScored_PropagatesVisualsScore()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("run1", "t1", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("run1", "t1", "baseline", true, null, 5.0, null));

        store.RecordScored(new CandidateScoredEvent("run1", "t1", "baseline", 8, 7, 6, 9));

        var tasks = store.GetActiveTasks();
        Assert.Single(tasks);
        var c = tasks[0].Candidates["baseline"];
        Assert.Equal(CandidateState.Scored, c.State);
        Assert.Equal(8, c.AcScore);
        Assert.Equal(7, c.DesignScore);
        Assert.Equal(6, c.ReadabilityScore);
        Assert.Equal(9, c.VisualsScore);
    }

    [Fact]
    public void RecordScored_NullVisualsScore_RemainsNull()
    {
        var store = new CandidateStateStore();
        store.RecordStarted(new CandidateStartedEvent("run1", "t1", "baseline", DateTimeOffset.UtcNow));
        store.RecordCompleted(new CandidateCompletedEvent("run1", "t1", "baseline", true, null, 5.0, null));

        store.RecordScored(new CandidateScoredEvent("run1", "t1", "baseline", 8, 7, 6));

        var tasks = store.GetActiveTasks();
        var c = tasks[0].Candidates["baseline"];
        Assert.Null(c.VisualsScore);
    }
}
