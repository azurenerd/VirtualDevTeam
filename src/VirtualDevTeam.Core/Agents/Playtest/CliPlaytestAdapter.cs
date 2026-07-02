using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Handles <c>cli_invocation</c> scenarios by spawning child processes via
/// <see cref="Process.Start"/> and capturing <c>stdout</c>, <c>stderr</c>, and exit code.
/// </summary>
/// <remarks>
/// <para>
/// Supported action categories: <c>cli.*</c>.
/// </para>
/// <para>
/// The adapter stores the result of the most recent <c>cli.run</c> action so subsequent
/// <c>cli.assertExitCode</c> / <c>cli.assertStdout</c> / <c>cli.assertStderr</c> actions
/// can reference it without re-running the process.
/// </para>
/// <para>
/// Process timeout defaults to 60 seconds. Override via <c>params.timeoutSeconds</c> in
/// the <c>cli.run</c> action.
/// </para>
/// </remarks>
public sealed class CliPlaytestAdapter : IPlaytestAdapter
{
    private readonly ILogger<CliPlaytestAdapter> _logger;

    // State within one scenario execution
    private CliRunEvidence? _lastRun;

    private static readonly HashSet<string> _handledCategories =
        new(StringComparer.OrdinalIgnoreCase) { "cli" };

    public CliPlaytestAdapter(ILogger<CliPlaytestAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanHandle(PlaytestAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _handledCategories.Contains(action.ActionCategory);
    }

    /// <inheritdoc/>
    public async Task<IPlaytestEvidence> ExecuteAsync(
        PlaytestAction action,
        AppHandle handle,
        Dictionary<string, string?> snapshots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(snapshots);

        try
        {
            return action.ActionVerb.ToLowerInvariant() switch
            {
                "run" => await RunProcessAsync(action, handle, ct),
                "assertexitcode" => AssertExitCode(action),
                "assertstdout" => AssertPattern(action, isStdout: true),
                "assertstderr" => AssertPattern(action, isStdout: false),
                _ => new InconclusiveEvidence("cli", $"CliPlaytestAdapter: unrecognised cli action '{action.ActionType}'"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CliPlaytestAdapter: action {ActionType} failed", action.ActionType);
            return new ActionFailureEvidence(action.SurfaceVerified ?? "cli", ex.Message);
        }
    }

    // ─── cli.run ─────────────────────────────────────────────────────────────

    private async Task<IPlaytestEvidence> RunProcessAsync(
        PlaytestAction action, AppHandle handle, CancellationToken ct)
    {
        // Resolve binary — prefer explicit param, then AppHandle.CliBinaryPath
        var binary = action.GetParam("binary")
                     ?? (handle.CliBinaryPath is not null
                         ? handle.CliBinaryPath
                         : throw new InvalidOperationException(
                             "cli.run requires either params.binary or AppHandle.CliBinaryPath"));

        // Parse args array or fall back to args string
        var args = new List<string>();
        if (action.Params.TryGetValue("args", out var argsEl)
            && argsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in argsEl.EnumerateArray())
                args.Add(el.GetString() ?? el.GetRawText());
        }
        else if (action.GetParam("args") is string argsStr)
        {
            args.AddRange(argsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var stdinData = action.GetParam("stdinData");
        var timeoutSeconds = action.GetIntParam("timeoutSeconds", 60);

        var workDir = handle.WorkspacePath ?? Directory.GetCurrentDirectory();

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = string.Join(' ', args.Select(EscapeArg)),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogDebug("CliPlaytestAdapter: running {Binary} {Args}", binary, startInfo.Arguments);

        using var process = new Process { StartInfo = startInfo };
        var sw = Stopwatch.StartNew();
        process.Start();

        if (stdinData is not null)
        {
            await process.StandardInput.WriteAsync(stdinData);
            process.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ActionFailureEvidence("cli_run", $"Process '{binary}' timed out after {timeoutSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        _lastRun = new CliRunEvidence(
            binary,
            args,
            process.ExitCode,
            stdout,
            stderr,
            sw.Elapsed);

        _logger.LogDebug(
            "CliPlaytestAdapter: {Binary} exited with code {Code} in {Ms}ms",
            binary, process.ExitCode, sw.ElapsedMilliseconds);

        return _lastRun;
    }

    // ─── cli.assertExitCode ──────────────────────────────────────────────────

    private IPlaytestEvidence AssertExitCode(PlaytestAction action)
    {
        if (_lastRun is null)
            return new InconclusiveEvidence("process_exit_code",
                "cli.assertExitCode called before cli.run");

        var expected = action.GetIntParam("expected", 0);
        return new ProcessExitCodeEvidence(_lastRun.ExitCode, expected);
    }

    // ─── cli.assertStdout / cli.assertStderr ────────────────────────────────

    private IPlaytestEvidence AssertPattern(PlaytestAction action, bool isStdout)
    {
        if (_lastRun is null)
        {
            var kind = isStdout ? "stdout_pattern" : "stderr_pattern";
            return new InconclusiveEvidence(kind, $"cli.assert{(isStdout ? "Stdout" : "Stderr")} called before cli.run");
        }

        var pattern = action.GetParam("regexPattern") ?? action.GetParam("pattern") ?? ".*";
        var text = isStdout ? _lastRun.Stdout : _lastRun.Stderr;
        var matched = Regex.IsMatch(text, pattern, RegexOptions.Multiline);

        return isStdout
            ? new StdoutPatternEvidence(pattern, text, matched)
            : (IPlaytestEvidence)new StderrPatternEvidence(pattern, text, matched);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string EscapeArg(string arg)
    {
        // Minimal escaping — quote args containing spaces
        if (!arg.Contains(' ') && !arg.Contains('"')) return arg;
        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }
}
