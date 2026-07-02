using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Agents.Playtest;

namespace VirtualDevTeam.Core.Tests.Playtest;

/// <summary>
/// Tests the <see cref="CliPlaytestAdapter"/> using real process execution.
/// Uses <c>dotnet --version</c> as the cross-platform portable command.
/// </summary>
public class CliPlaytestAdapterTests
{
    private readonly CliPlaytestAdapter _adapter;
    private readonly AppHandle _handle = new() { TargetType = AppTargetType.Cli };

    public CliPlaytestAdapterTests()
    {
        _adapter = new CliPlaytestAdapter(NullLogger<CliPlaytestAdapter>.Instance);
    }

    // ── CanHandle ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanHandle_CliRunAction_ReturnsTrue()
    {
        var action = MakeAction("cli.run");
        Assert.True(_adapter.CanHandle(action));
    }

    [Fact]
    public void CanHandle_PageClickAction_ReturnsFalse()
    {
        var action = MakeAction("page.click");
        Assert.False(_adapter.CanHandle(action));
    }

    [Fact]
    public void CanHandle_HttpPostAction_ReturnsFalse()
    {
        var action = MakeAction("http.post");
        Assert.False(_adapter.CanHandle(action));
    }

    // ── cli.run ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CliRun_DotnetVersion_ExitCodeZero()
    {
        var action = MakeCliRunAction("dotnet", ["--version"]);
        var snapshots = new Dictionary<string, string?>();

        var evidence = await _adapter.ExecuteAsync(action, _handle, snapshots);

        var run = Assert.IsType<CliRunEvidence>(evidence);
        Assert.True(run.Passed);
        Assert.Equal(0, run.ExitCode);
        Assert.NotEmpty(run.Stdout);
        // dotnet --version prints something like "8.0.xxx"
        Assert.Matches(@"\d+\.\d+", run.Stdout);
    }

    [Fact]
    public async Task ExecuteAsync_CliRun_NonZeroExitCode_CapturedInEvidence()
    {
        // `dotnet non-existent-command` exits non-zero
        var action = MakeCliRunAction("dotnet", ["this-command-does-not-exist-xyz"]);
        var snapshots = new Dictionary<string, string?>();

        var evidence = await _adapter.ExecuteAsync(action, _handle, snapshots);

        // CliRunEvidence.Passed is always true (execution didn't throw)
        // The exit code is non-zero
        var run = Assert.IsType<CliRunEvidence>(evidence);
        Assert.NotEqual(0, run.ExitCode);
    }

    // ── cli.assertExitCode ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AssertExitCode_AfterRun_PassesWhenMatch()
    {
        var snapshots = new Dictionary<string, string?>();

        // Run dotnet --version (exit 0)
        await _adapter.ExecuteAsync(MakeCliRunAction("dotnet", ["--version"]), _handle, snapshots);

        var assertAction = MakeAction("cli.assertExitCode", ("expected", "0"));
        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        var exitEvidence = Assert.IsType<ProcessExitCodeEvidence>(evidence);
        Assert.True(exitEvidence.Passed);
        Assert.Equal(0, exitEvidence.ExitCode);
        Assert.Equal(0, exitEvidence.Expected);
    }

    [Fact]
    public async Task ExecuteAsync_AssertExitCode_BeforeRun_ReturnsInconclusive()
    {
        var snapshots = new Dictionary<string, string?>();
        // Note: no cli.run before this assert
        var assertAction = MakeAction("cli.assertExitCode", ("expected", "0"));

        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        var inconclusive = Assert.IsType<InconclusiveEvidence>(evidence);
        Assert.True(inconclusive.IsInconclusive);
    }

    // ── cli.assertStdout ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AssertStdout_MatchingPattern_Passes()
    {
        var snapshots = new Dictionary<string, string?>();

        // dotnet --version outputs a version string like "8.0.123"
        await _adapter.ExecuteAsync(MakeCliRunAction("dotnet", ["--version"]), _handle, snapshots);

        var assertAction = MakeAction("cli.assertStdout", ("regexPattern", @"\d+\.\d+"));
        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        var stdoutEvidence = Assert.IsType<StdoutPatternEvidence>(evidence);
        Assert.True(stdoutEvidence.Passed);
    }

    [Fact]
    public async Task ExecuteAsync_AssertStdout_NonMatchingPattern_Fails()
    {
        var snapshots = new Dictionary<string, string?>();

        await _adapter.ExecuteAsync(MakeCliRunAction("dotnet", ["--version"]), _handle, snapshots);

        var assertAction = MakeAction("cli.assertStdout", ("regexPattern", "this-will-never-match-xyz-12345"));
        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        var stdoutEvidence = Assert.IsType<StdoutPatternEvidence>(evidence);
        Assert.False(stdoutEvidence.Passed);
        Assert.NotNull(stdoutEvidence.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_AssertStdout_BeforeRun_ReturnsInconclusive()
    {
        var snapshots = new Dictionary<string, string?>();
        var assertAction = MakeAction("cli.assertStdout", ("regexPattern", ".*"));

        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        Assert.IsType<InconclusiveEvidence>(evidence);
    }

    // ── cli.assertStderr ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AssertStderr_BeforeRun_ReturnsInconclusive()
    {
        var snapshots = new Dictionary<string, string?>();
        var assertAction = MakeAction("cli.assertStderr", ("regexPattern", ".*"));

        var evidence = await _adapter.ExecuteAsync(assertAction, _handle, snapshots);

        Assert.IsType<InconclusiveEvidence>(evidence);
    }

    // ── Unknown action ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnknownCliVerb_ReturnsInconclusive()
    {
        var snapshots = new Dictionary<string, string?>();
        var action = MakeAction("cli.unknownVerb");

        var evidence = await _adapter.ExecuteAsync(action, _handle, snapshots);

        var inc = Assert.IsType<InconclusiveEvidence>(evidence);
        Assert.True(inc.IsInconclusive);
    }

    // ── Duration captured ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CliRun_Duration_IsPositive()
    {
        var action = MakeCliRunAction("dotnet", ["--version"]);
        var snapshots = new Dictionary<string, string?>();

        var evidence = await _adapter.ExecuteAsync(action, _handle, snapshots);

        var run = Assert.IsType<CliRunEvidence>(evidence);
        Assert.True(run.Duration > TimeSpan.Zero);
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

    private static PlaytestAction MakeCliRunAction(string binary, string[] args)
    {
        var argsJson = "[" + string.Join(",", args.Select(a => $"\"{a}\"")) + "]";
        return new PlaytestAction
        {
            StepIndex = 0,
            ActionType = "cli.run",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["binary"] = System.Text.Json.JsonDocument.Parse($"\"{binary}\"").RootElement,
                ["args"] = System.Text.Json.JsonDocument.Parse(argsJson).RootElement,
            },
        };
    }
}
