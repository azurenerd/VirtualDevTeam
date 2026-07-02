using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.Tests;

public sealed class DiagnosticActionExecutorTests : IDisposable
{
    private readonly string _scratchRoot;

    public DiagnosticActionExecutorTests()
    {
        _scratchRoot = Path.Combine(AppContext.BaseDirectory, "diagnostic-action-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
                Directory.Delete(_scratchRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup only
        }
    }

    [Fact]
    public async Task ExecuteAsync_ApplyRecommendation_UpdatesStateAndResolvesNotification()
    {
        var rec = CreateRecommendation(FixTier.Live, "src/VirtualDevTeam.Runner/appsettings.json");
        var store = new FakeRecommendationStore(rec);
        var applicator = new FakeApplicator(new FixApplyResult
        {
            State = FixRecommendationState.Applied,
            Detail = "applied live",
        });
        var notifications = CreateNotificationService();
        await notifications.AddNotificationAsync($"flow-monitor:fix:{rec.Id}", "ctx");
        var sut = CreateExecutor(store, applicator, notifications);

        var result = await sut.ExecuteAsync(new DiagnosticActionRequest
        {
            Kind = DiagnosticActionKind.ApplyRecommendation,
            RecommendationId = rec.Id,
            RepoRoot = _scratchRoot,
        });

        Assert.NotNull(result);
        Assert.Equal(FixRecommendationState.Applied, result.State);
        Assert.Equal(FixTier.Live, result.Tier);
        Assert.False(result.RestartRequired);
        Assert.Equal(new[] { FixRecommendationState.ApprovedForCoding, FixRecommendationState.Applied }, store.StateTransitions);
        Assert.Single(applicator.Calls);
        Assert.True(notifications.GetAll().Single().IsResolved);
    }

    [Fact]
    public async Task ExecuteAsync_ApplyRecommendation_BlockedStagesPlanForNextBoot()
    {
        var rec = CreateRecommendation(FixTier.Blocked, "src/VirtualDevTeam.Runner/VirtualDevTeam.Runner.csproj");
        var store = new FakeRecommendationStore(rec);
        var applicator = new FakeApplicator(new FixApplyResult
        {
            State = FixRecommendationState.Applied,
            Detail = "should not be used",
        });
        var notifications = CreateNotificationService();
        await notifications.AddNotificationAsync($"flow-monitor:fix:{rec.Id}", "ctx");
        var sut = CreateExecutor(store, applicator, notifications);

        var repoRoot = Path.Combine(_scratchRoot, "blocked-repo");
        Directory.CreateDirectory(repoRoot);

        var result = await sut.ExecuteAsync(new DiagnosticActionRequest
        {
            Kind = DiagnosticActionKind.ApplyRecommendation,
            RecommendationId = rec.Id,
            RepoRoot = repoRoot,
        });

        Assert.NotNull(result);
        Assert.Equal(FixRecommendationState.StagedForNextRestart, result.State);
        Assert.Equal(FixTier.Blocked, result.Tier);
        Assert.True(result.RestartRequired);
        Assert.Empty(applicator.Calls);
        Assert.Equal(new[] { FixRecommendationState.ApprovedForCoding, FixRecommendationState.StagedForNextRestart }, store.StateTransitions);

        var stagedDir = Path.Combine(repoRoot, "FixRecommendations", "staged");
        var stagedFile = Assert.Single(Directory.GetFiles(stagedDir, "*.md"));
        var stagedText = await File.ReadAllTextAsync(stagedFile);
        Assert.Contains(rec.PlanMarkdown, stagedText);
        Assert.True(notifications.GetAll().Single().IsResolved);
    }

    [Fact]
    public async Task ExecuteAsync_DismissRecommendation_RejectsAndResolvesNotification()
    {
        var rec = CreateRecommendation(FixTier.DeferredRestart, "src/VirtualDevTeam.Core/Agents/AgentBase.cs");
        var store = new FakeRecommendationStore(rec);
        var applicator = new FakeApplicator(new FixApplyResult
        {
            State = FixRecommendationState.Coded,
            Detail = "unused",
        });
        var notifications = CreateNotificationService();
        await notifications.AddNotificationAsync($"flow-monitor:fix:{rec.Id}", "ctx");
        var sut = CreateExecutor(store, applicator, notifications);

        var result = await sut.ExecuteAsync(new DiagnosticActionRequest
        {
            Kind = DiagnosticActionKind.DismissRecommendation,
            RecommendationId = rec.Id,
        });

        Assert.NotNull(result);
        Assert.Equal(FixRecommendationState.Rejected, result.State);
        Assert.False(result.RestartRequired);
        Assert.Equal(new[] { FixRecommendationState.Rejected }, store.StateTransitions);
        Assert.Empty(applicator.Calls);
        Assert.True(notifications.GetAll().Single().IsResolved);
    }

    [Fact]
    public async Task ExecuteAsync_MissingRecommendation_ReturnsNull()
    {
        var store = new FakeRecommendationStore(null);
        var notifications = CreateNotificationService();
        var sut = CreateExecutor(
            store,
            new FakeApplicator(new FixApplyResult
            {
                State = FixRecommendationState.Applied,
                Detail = "unused",
            }),
            notifications);

        var result = await sut.ExecuteAsync(new DiagnosticActionRequest
        {
            Kind = DiagnosticActionKind.ApplyRecommendation,
            RecommendationId = "missing",
            RepoRoot = _scratchRoot,
        });

        Assert.Null(result);
        Assert.Empty(store.StateTransitions);
    }

    private static DiagnosticActionExecutor CreateExecutor(
        IFixRecommendationStore store,
        IFixRecommendationApplicator applicator,
        GateNotificationService notifications) =>
        new(
            store,
            applicator,
            notifications,
            NullLogger<DiagnosticActionExecutor>.Instance);

    private static GateNotificationService CreateNotificationService()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new GateNotificationService(
            Array.Empty<INotificationChannel>(),
            services,
            Options.Create(new VirtualDevTeamConfig()),
            NullLogger<GateNotificationService>.Instance);
    }

    private static FixRecommendation CreateRecommendation(FixTier tier, params string[] affectedFiles) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        FindingId = "finding-1",
        DetectorId = "detector-1",
        Severity = FlowFindingSeverity.Critical,
        Confidence = 0.92,
        NeedsRestart = tier != FixTier.Live,
        FilesToChange = string.Join(", ", affectedFiles),
        FixTier = tier,
        AffectedFiles = affectedFiles,
        PlanMarkdown = "## Plan\n- Apply the fix safely.",
        State = FixRecommendationState.PendingReview,
    };

    private sealed class FakeRecommendationStore : IFixRecommendationStore
    {
        private FixRecommendation? _recommendation;

        public FakeRecommendationStore(FixRecommendation? recommendation)
        {
            _recommendation = recommendation;
        }

        public List<FixRecommendationState> StateTransitions { get; } = new();

        public FixRecommendation? GetRecommendation(string id) => _recommendation?.Id == id ? _recommendation : null;

        public void UpdateRecommendationState(string id, FixRecommendationState newState, string? feedback = null)
        {
            if (_recommendation?.Id != id)
                return;

            StateTransitions.Add(newState);
            _recommendation = _recommendation with
            {
                State = newState,
                OperatorFeedback = feedback ?? _recommendation.OperatorFeedback,
            };
        }
    }

    private sealed class FakeApplicator : IFixRecommendationApplicator
    {
        private readonly FixApplyResult _result;

        public FakeApplicator(FixApplyResult result)
        {
            _result = result;
        }

        public List<(string RecommendationId, FixTier Tier, string RepoRoot)> Calls { get; } = new();

        public Task<FixApplyResult> ApplyAsync(FixRecommendation rec, FixTier tier, string repoRoot, CancellationToken ct)
        {
            Calls.Add((rec.Id, tier, repoRoot));
            return Task.FromResult(_result);
        }
    }
}
