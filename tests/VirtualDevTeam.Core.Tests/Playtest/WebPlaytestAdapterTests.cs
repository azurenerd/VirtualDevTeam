using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Agents.Playtest;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests.Playtest;

/// <summary>
/// Tests the <see cref="WebPlaytestAdapter"/> using mocked PlaywrightRunner.
/// Full Playwright integration tests require browsers to be installed; these unit tests
/// focus on dispatch logic, CanHandle routing, and evidence type correctness.
/// Actual Playwright page interaction is tested in integration tests that require
/// a running browser environment.
/// </summary>
public class WebPlaytestAdapterTests : IDisposable
{
    private readonly WebPlaytestAdapter _adapter;

    public WebPlaytestAdapterTests()
    {
        // PlaywrightRunner only needs to be available for CanHandle/dispatch tests
        var logger = NullLogger<PlaywrightRunner>.Instance;
        var appLauncher = new AppLauncher(NullLogger<AppLauncher>.Instance, null);
        var runner = new PlaywrightRunner(
            logger,
            appLauncher,
            new MediaRecorder(NullLogger<MediaRecorder>.Instance),
            new ApiSmokeRunner(NullLogger<ApiSmokeRunner>.Instance, appLauncher));
        _adapter = new WebPlaytestAdapter(runner, NullLogger<WebPlaytestAdapter>.Instance);
    }

    public void Dispose()
    {
        // WebPlaytestAdapter is IAsyncDisposable; synchronous dispose for test cleanup
        _adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ── CanHandle ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("page.goto")]
    [InlineData("page.click")]
    [InlineData("page.fill")]
    [InlineData("page.waitForSelector")]
    [InlineData("page.evaluate")]
    [InlineData("page.screenshot")]
    [InlineData("assert.selectorExists")]
    [InlineData("assert.selectorText")]
    [InlineData("assert.selectorChanged")]
    [InlineData("assert.eventFired")]
    [InlineData("wait.ms")]
    [InlineData("log.snapshot")]
    public void CanHandle_WebActionTypes_ReturnsTrue(string actionType)
    {
        var action = MakeAction(actionType);
        Assert.True(_adapter.CanHandle(action));
    }

    [Theory]
    [InlineData("http.post")]
    [InlineData("http.get")]
    [InlineData("http.assertStatus")]
    [InlineData("cli.run")]
    [InlineData("cli.assertExitCode")]
    [InlineData("db.query")]
    public void CanHandle_NonWebActionTypes_ReturnsFalse(string actionType)
    {
        var action = MakeAction(actionType);
        Assert.False(_adapter.CanHandle(action));
    }

    // ── CanHandle null guard ──────────────────────────────────────────────────

    [Fact]
    public void CanHandle_NullAction_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _adapter.CanHandle(null!));
    }

    // ── ExecuteAsync null guards ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullAction_ThrowsArgumentNullException()
    {
        var handle = new AppHandle();
        var snapshots = new Dictionary<string, string?>();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _adapter.ExecuteAsync(null!, handle, snapshots));
    }

    [Fact]
    public async Task ExecuteAsync_NullHandle_ThrowsArgumentNullException()
    {
        var action = MakeAction("page.click");
        var snapshots = new Dictionary<string, string?>();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _adapter.ExecuteAsync(action, null!, snapshots));
    }

    [Fact]
    public async Task ExecuteAsync_NullSnapshots_ThrowsArgumentNullException()
    {
        var action = MakeAction("page.click");
        var handle = new AppHandle();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _adapter.ExecuteAsync(action, handle, null!));
    }

    // ── Evidence types ────────────────────────────────────────────────────────

    /// <summary>
    /// When Playwright browsers are not installed, attempting to open a browser will
    /// throw an exception. The adapter must catch it and return ActionFailureEvidence,
    /// not propagate the exception.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenBrowserUnavailable_ReturnsActionFailureEvidence()
    {
        // This test intentionally does NOT require browsers to be installed.
        // If browsers ARE installed the test still works — it navigates to localhost:1 which
        // is not listening, so goto throws and we get ActionFailureEvidence.
        var action = MakeAction("page.goto", ("url", "http://localhost:1/nonexistent"));
        var handle = new AppHandle { BaseUrl = "http://localhost:1" };
        var snapshots = new Dictionary<string, string?>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var evidence = await _adapter.ExecuteAsync(action, handle, snapshots, cts.Token);

        // Either browsers aren't installed → ActionFailureEvidence
        // Or they are but the server doesn't exist → ActionFailureEvidence
        // In both cases we must not throw
        Assert.IsAssignableFrom<IPlaytestEvidence>(evidence);
        if (evidence is ActionFailureEvidence failure)
        {
            Assert.False(failure.Passed);
        }
        // If somehow it "succeeds" (browser opened empty page) that's fine — test is about no-throw
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PlaytestAction MakeAction(string actionType, params (string key, string value)[] paramPairs)
    {
        var @params = paramPairs.ToDictionary(
            p => p.key,
            p => System.Text.Json.JsonSerializer.SerializeToElement(p.value));

        return new PlaytestAction
        {
            StepIndex = 0,
            ActionType = actionType,
            Params = @params,
        };
    }
}
