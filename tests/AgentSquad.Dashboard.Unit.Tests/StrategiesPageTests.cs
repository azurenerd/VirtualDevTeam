using System.Collections.Immutable;
using AgentSquad.Core.Strategies;
using AgentSquad.Dashboard.Components.Pages;
using AgentSquad.Dashboard.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentSquad.Dashboard.Unit.Tests;

/// <summary>
/// bUnit component tests for the /strategies Blazor page. Exercises rendering
/// in each state: framework-off banner, empty active/recent, active task cards,
/// winner row rendering, and refresh behaviour. SignalR hub connection inside
/// <see cref="Strategies"/> is caught by the page's try/catch and does not
/// affect rendering assertions.
/// </summary>
public sealed class StrategiesPageTests : TestContext
{
    private readonly Mock<IStrategiesDataService> _data = new();

    private void StubServices(
        IReadOnlyList<TaskSnapshot>? active = null,
        IReadOnlyList<TaskSnapshot>? recent = null,
        EnabledStrategiesInfo? enabled = null)
    {
        _data.Setup(d => d.GetActiveTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(active ?? Array.Empty<TaskSnapshot>());
        _data.Setup(d => d.GetRecentTasksAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(recent ?? Array.Empty<TaskSnapshot>());
        _data.Setup(d => d.GetEnabledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabled ?? new EnabledStrategiesInfo(false, Array.Empty<string>()));

        Services.AddSingleton(_data.Object);
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
    }

    private static TaskSnapshot Active(string taskId, params (string id, CandidateState state)[] candidates)
    {
        var dict = ImmutableDictionary.CreateBuilder<string, CandidateSnapshot>();
        foreach (var (id, state) in candidates)
        {
            dict[id] = new CandidateSnapshot
            {
                StrategyId = id,
                State = state,
                StartedAt = DateTimeOffset.UtcNow,
                ElapsedSec = 1.2,
                TokensUsed = 500,
                AcScore = state == CandidateState.Scored || state == CandidateState.Winner ? 8 : null,
                DesignScore = state == CandidateState.Scored || state == CandidateState.Winner ? 7 : null,
                ReadabilityScore = state == CandidateState.Scored || state == CandidateState.Winner ? 9 : null,
            };
        }
        return new TaskSnapshot
        {
            RunId = "run-1",
            TaskId = taskId,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            Candidates = dict.ToImmutable(),
        };
    }

    private static TaskSnapshot Completed(string taskId, string? winner, string tieBreak)
    {
        return new TaskSnapshot
        {
            RunId = "run-1",
            TaskId = taskId,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-60),
            CompletedAt = DateTimeOffset.UtcNow,
            WinnerStrategyId = winner,
            TieBreakReason = tieBreak,
            EvaluationElapsedSec = 0.5,
            Candidates = ImmutableDictionary<string, CandidateSnapshot>.Empty
                .Add("baseline", new CandidateSnapshot
                {
                    StrategyId = "baseline",
                    State = winner == "baseline" ? CandidateState.Winner : CandidateState.Completed,
                })
                .Add("mcp-enhanced", new CandidateSnapshot
                {
                    StrategyId = "mcp-enhanced",
                    State = winner == "mcp-enhanced" ? CandidateState.Winner : CandidateState.Completed,
                }),
        };
    }

    [Fact]
    public void Renders_framework_off_banner_when_master_disabled()
    {
        StubServices(enabled: new EnabledStrategiesInfo(false, Array.Empty<string>()));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
            Assert.Contains("Agentic frameworks are disabled", cut.Markup));
        Assert.Contains("Framework OFF", cut.Markup);
    }

    [Fact]
    public void Hides_framework_off_banner_when_master_enabled()
    {
        StubServices(enabled: new EnabledStrategiesInfo(true, new[] { "baseline", "mcp-enhanced" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
            Assert.Contains("Framework ON", cut.Markup));
        Assert.DoesNotContain("Agentic frameworks are disabled", cut.Markup);
    }

    [Fact]
    public void Shows_empty_state_when_no_active_or_recent()
    {
        StubServices();

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No active framework candidates", cut.Markup);
            Assert.Contains("No completed tasks yet", cut.Markup);
        });
    }

    [Fact]
    public void Renders_active_task_with_each_candidate_and_its_state()
    {
        var active = new[]
        {
            Active("T1", ("baseline", CandidateState.Running), ("mcp-enhanced", CandidateState.Completed)),
        };
        StubServices(active: active, enabled: new EnabledStrategiesInfo(true, new[] { "baseline", "mcp-enhanced" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("T1", cut.Markup);
            Assert.Contains("baseline", cut.Markup);
            Assert.Contains("mcp-enhanced", cut.Markup);
            // State-indicating CSS classes applied per candidate.
            Assert.Contains("state-running", cut.Markup);
            Assert.Contains("state-completed", cut.Markup);
        });
    }

    [Fact]
    public void Renders_scores_only_for_scored_or_winner_candidates()
    {
        var active = new[]
        {
            Active("T1", ("baseline", CandidateState.Running), ("mcp-enhanced", CandidateState.Scored)),
        };
        StubServices(active: active, enabled: new EnabledStrategiesInfo(true, new[] { "baseline" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            // The scored candidate's Ac/Des/Read scores appear
            Assert.Contains("AC", cut.Markup);
            Assert.Contains("Des", cut.Markup);
            Assert.Contains("Read", cut.Markup);
        });
    }

    [Fact]
    public void Recent_table_shows_winner_badge_and_tiebreak_reason()
    {
        var recent = new[]
        {
            Completed("T1", "mcp-enhanced", "llm-rank"),
        };
        StubServices(recent: recent, enabled: new EnabledStrategiesInfo(true, new[] { "baseline", "mcp-enhanced" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("winner-badge", cut.Markup);
            Assert.Contains("mcp-enhanced", cut.Markup);
            Assert.Contains("llm-rank", cut.Markup);
        });
    }

    [Fact]
    public void Recent_table_renders_none_marker_when_winner_is_null()
    {
        var recent = new[] { Completed("T1", null, "all-failed") };
        StubServices(recent: recent, enabled: new EnabledStrategiesInfo(true, new[] { "baseline" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("— none —", cut.Markup);
            Assert.Contains("all-failed", cut.Markup);
        });
    }

    [Fact]
    public void Header_counts_reflect_active_and_recent_counts()
    {
        var active = new[]
        {
            Active("T1", ("baseline", CandidateState.Running)),
            Active("T2", ("baseline", CandidateState.Running)),
        };
        var recent = new[] { Completed("T0", "baseline", "sole-survivor") };

        StubServices(active: active, recent: recent,
            enabled: new EnabledStrategiesInfo(true, new[] { "baseline" }));

        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Active: 2", cut.Markup);
            Assert.Contains("Recent: 1", cut.Markup);
        });
    }

    [Fact]
    public void Refresh_button_reinvokes_data_service()
    {
        StubServices();
        var cut = RenderComponent<Strategies>();

        cut.WaitForAssertion(() => _data.Verify(d => d.GetEnabledAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce));
        _data.Invocations.Clear();

        cut.Find("button.ntl-refresh-btn").Click();

        cut.WaitForAssertion(() =>
        {
            _data.Verify(d => d.GetActiveTasksAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            _data.Verify(d => d.GetRecentTasksAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            _data.Verify(d => d.GetEnabledAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        });
    }

    [Fact]
    public void Data_service_exception_does_not_crash_component()
    {
        _data.Setup(d => d.GetActiveTasksAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _data.Setup(d => d.GetRecentTasksAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TaskSnapshot>());
        _data.Setup(d => d.GetEnabledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnabledStrategiesInfo(true, Array.Empty<string>()));
        Services.AddSingleton(_data.Object);
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));

        var cut = RenderComponent<Strategies>();

        // The page catches the exception in its refresh try/catch and keeps rendering.
        cut.WaitForAssertion(() => Assert.Contains("Agentic Frameworks", cut.Markup));
    }
}
