using System.Diagnostics;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Phase-3 strategy: instead of running a single-pass LLM generator, it delegates
/// the whole task to a sandboxed agentic <c>copilot --allow-all</c> session running
/// inside the candidate worktree. The CLI does its own planning + tool-calling
/// (read, write, run, git add/commit) until it self-reports done, exhausts the
/// tool-call cap, goes stuck, or hits the wall-clock budget.
///
/// <para>
/// Trust model: process-level containment only. Uses env scrubbing
/// (<see cref="CopilotCliAgenticScope"/>), a Windows Job Object for atomic
/// descendant-kill (<see cref="Win32JobObject"/>), and per-worktree git config
/// isolation. Does NOT protect against network exfil, reads of host-readable
/// files via absolute paths, or human-targeted prompt injection. Ship opt-in;
/// never default.
/// </para>
/// </summary>
public class AgenticDelegationStrategy : ICodeGenerationStrategy
{
    public string Id => "copilot-cli";

    private readonly ILogger<AgenticDelegationStrategy> _logger;
    private readonly CopilotCliProcessManager _processManager;
    private readonly StrategyFrameworkConfig _frameworkConfig;
    private readonly AgenticPromptBuilder _promptBuilder;
    private readonly IChatCompletionRunner? _llmRunner;

    public AgenticDelegationStrategy(
        ILogger<AgenticDelegationStrategy> logger,
        CopilotCliProcessManager processManager,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        AgenticPromptBuilder? promptBuilder = null,
        IChatCompletionRunner? llmRunner = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _frameworkConfig = (frameworkConfig ?? throw new ArgumentNullException(nameof(frameworkConfig))).Value;
        _promptBuilder = promptBuilder ?? new AgenticPromptBuilder();
        _llmRunner = llmRunner;
    }

    public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sink = invocation.ActivitySink;

        // Build sandbox scope. Worktree must already exist — strategy orchestrator
        // owns worktree creation; we just materialize the sandbox dirs inside it.
        CopilotCliAgenticScope scope;
        try
        {
            scope = CopilotCliAgenticScope.Prepare(invocation.WorktreePath);
            sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Sandbox scope prepared"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticDelegationStrategy sandbox prepare failed for task {Task}", invocation.Task.TaskId);
            return Fail(sw, $"sandbox-prepare: {ex.GetType().Name}: {ex.Message}");
        }

        var prompt = _promptBuilder.Build(invocation);
        sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Prompt built, launching agentic session"));

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            CloseStdinAfterPrompt = true,
            WatchdogMode = CopilotCliWatchdogMode.Agentic,
            WorkingDirectory = invocation.WorktreePath,
            EnvironmentOverrides = scope.EnvironmentOverrides,
            Timeout = invocation.Timeout,
            ActivitySink = sink,
            ToolCallCapOverride = invocation.Task.ToolCallCapOverride,
            ForceNoWrapper = invocation.ForceNoWrapper,
        };

        // Live artifact watcher — emits FrameworkActivityEvent for each new PNG/JSON/etc
        // the session writes to the worktree. Lets the operator watch image-gen tasks
        // land assets in real time on the Frameworks dashboard (2026-05-12 ask).
        await using var artifactWatcher = new Frameworks.CandidateArtifactWatcher(_logger);
        artifactWatcher.Start(invocation.WorktreePath, sink, ct);

        // Snapshot host state BEFORE launching the CLI. The validator compares
        // post-session state against this to catch sandbox escapes that the
        // in-process containment missed (e.g. a ~/.gitconfig write that slipped
        // past the GIT_CONFIG_GLOBAL redirect).
        var validator = new SandboxPostRunValidator(_logger);
        var snapshot = SandboxPostRunValidator.TakeSnapshot(invocation.WorktreePath);

        try
        {
            var result = await _processManager.ExecuteAgenticSessionAsync(prompt, options, ct);

            sink?.Report(new Frameworks.FrameworkActivityEvent("complete",
                $"Session finished: {(result.Succeeded ? "succeeded" : "failed")} " +
                $"(tool-calls: {result.ToolCallCount}, wall: {result.WallClock.TotalSeconds:F1}s)"));

            if (!result.Succeeded)
            {
                var reason = result.FailureReason switch
                {
                    AgenticFailureReason.StuckNoOutput => "stuck-no-output",
                    AgenticFailureReason.ToolCallCap => "tool-call-cap",
                    AgenticFailureReason.Timeout => "timeout",
                    AgenticFailureReason.Canceled => "canceled",
                    AgenticFailureReason.ExitNonzero => $"exit-nonzero: {result.ExitCode}",
                    AgenticFailureReason.LaunchFailed => $"launch-failed: {result.ErrorMessage}",
                    AgenticFailureReason.Unavailable => "cli-unavailable",
                    _ => result.ErrorMessage ?? "unknown-agentic-failure",
                };
                sink?.Report(new Frameworks.FrameworkActivityEvent("error", $"Failed: {reason}"));
                _logger.LogWarning(
                    "AgenticDelegationStrategy failed for task {Task}: {Reason} (tool-calls: {ToolCalls}, wall: {Wall}s)",
                    invocation.Task.TaskId, reason, result.ToolCallCount, result.WallClock.TotalSeconds);
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = reason,
                    Elapsed = sw.Elapsed,
                    Log = new[] { TruncateLog(result.LogBuffer) },
                };
            }

            // Post-run sandbox validation. Any violation demotes the candidate to
            // failed — better to drop a suspect patch than ship an escaped one.
            sink?.Report(new Frameworks.FrameworkActivityEvent("sandbox", "Running post-execution sandbox validation"));
            var violations = validator.Validate(
                invocation.WorktreePath, snapshot, scope.SandboxGitconfigPath);
            if (violations.Count > 0)
            {
                var codes = string.Join(",", violations.Select(v => v.Code));
                sink?.Report(new Frameworks.FrameworkActivityEvent("error", $"Sandbox violation: {codes}"));
                _logger.LogError(
                    "AgenticDelegationStrategy sandbox violations for task {Task}: {Codes}",
                    invocation.Task.TaskId, codes);
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = $"sandbox-violation: {codes}",
                    Elapsed = sw.Elapsed,
                    Log = new[] { TruncateLog(result.LogBuffer) },
                };
            }

            sink?.Report(new Frameworks.FrameworkActivityEvent("sandbox", "Sandbox validation passed ✓"));

            // Post-execution diff-size guard: detect suspiciously large diffs that may
            // indicate a runaway tool-call loop rewrote the entire worktree. Threshold
            // is generous — legitimate tasks rarely touch 200+ files or 50K+ lines.
            // Uses baseSha to correctly count changes even when the CLI committed mid-run
            // (git diff --stat HEAD returns nothing when everything is already committed).
            var diffStats = CountWorktreeChanges(invocation.WorktreePath, invocation.BaseSha);
            if (diffStats.FileCount > 200 || diffStats.LineCount > 50_000)
            {
                var reason = $"diff-too-large: {diffStats.FileCount} files, {diffStats.LineCount} lines changed";
                sink?.Report(new Frameworks.FrameworkActivityEvent("error", reason));
                _logger.LogWarning(
                    "AgenticDelegationStrategy diff guard tripped for task {Task}: {Reason}",
                    invocation.Task.TaskId, reason);
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = reason,
                    Elapsed = sw.Elapsed,
                    Log = new[] { TruncateLog(result.LogBuffer) },
                };
            }

            // Diagnostic: warn about zero changes — common failure mode where CLI exits 0
            // but produced no actual file modifications (network errors, auth failures,
            // tool execution silently failed). Surface prominently so it's clear in the UI.
            // Special case: if the agent's last assistant message indicates "task already
            // complete" / "clean working tree" — the worktree's BaseSha already contains
            // the work (e.g. a previous merged PR). That's a legitimate no-op, not a
            // failure; surface it distinctly so the orchestrator doesn't burn retries.
            var noOpAcknowledged = false;
            if (diffStats.FileCount == 0)
            {
                noOpAcknowledged = LogIndicatesTaskAlreadyComplete(result.LogBuffer);

                // T-FINAL: 0 changes with successful exit = clean integration (build/test passed).
                // This is the expected happy path — not a failure requiring retry.
                if (!noOpAcknowledged && invocation.Task.TaskId.Equals("T-FINAL", StringComparison.OrdinalIgnoreCase))
                {
                    noOpAcknowledged = true;
                    sink?.Report(new Frameworks.FrameworkActivityEvent("complete",
                        $"✅ T-FINAL validation complete: CLI ran {result.ToolCallCount} tool calls in " +
                        $"{result.WallClock.TotalSeconds:F0}s with no file changes. " +
                        $"Build + tests passed — integration is clean."));
                    _logger.LogInformation(
                        "T-FINAL integration validation passed with no changes needed " +
                        "(tool-calls: {ToolCalls}, wall: {Wall}s)",
                        result.ToolCallCount, result.WallClock.TotalSeconds);
                }
                else if (noOpAcknowledged)
                {
                    sink?.Report(new Frameworks.FrameworkActivityEvent("complete",
                        $"🪶 Agent inspected the worktree and reported the task is already complete " +
                        $"(no changes needed). This is a legitimate no-op — the work was done by a " +
                        $"prior merged PR or earlier wave. Not retrying."));
                    _logger.LogInformation(
                        "AgenticDelegationStrategy task {Task} is a legitimate no-op " +
                        "(agent self-reported task already complete)",
                        invocation.Task.TaskId);
                }
                else
                {
                    sink?.Report(new Frameworks.FrameworkActivityEvent("warning",
                        $"⚠️ CLI session completed successfully ({result.ToolCallCount} tool calls, " +
                        $"{result.WallClock.TotalSeconds:F0}s) but produced NO file changes. " +
                        $"This often indicates a transient failure (network, auth, or tool error that CLI swallowed). " +
                        $"The orchestrator will retry automatically."));
                    _logger.LogWarning(
                        "AgenticDelegationStrategy produced ZERO changes for task {Task} despite reporting success " +
                        "(tool-calls: {ToolCalls}, wall: {Wall}s) — likely transient CLI failure",
                        invocation.Task.TaskId, result.ToolCallCount, result.WallClock.TotalSeconds);
                }
            }

            _logger.LogInformation(
                "AgenticDelegationStrategy succeeded for task {Task} (tool-calls: {ToolCalls}, wall: {Wall}s)",
                invocation.Task.TaskId, result.ToolCallCount, result.WallClock.TotalSeconds);

            // Parse token usage from the CLI session log. The agentic copilot-cli prints
            // each event as a JSONL line; the final `{"type":"result", ..., "usage":{...}}`
            // line carries premiumRequests + totalApiDurationMs (the CLI does NOT expose
            // raw token counts). Estimate tokens at ~30k per premium request — the typical
            // per-session cost for Claude Opus / Sonnet on this codebase, matching what the
            // Squad framework reports via its "Tokens ↑ Xk" stdout summary.
            //
            // Fallback chain:
            //   1. JSONL result event with usage.premiumRequests → estimate as N×30000
            //   2. Squad-style "Tokens ↑ Xk · ↓ Yk" line in log buffer (older CLI shapes)
            //   3. null (dashboard shows "unknown")
            long? tokensUsed = null;
            int? premiumRequests = null;
            try
            {
                var (pr, tokens) = ParseAgenticUsage(result.LogBuffer ?? "");
                premiumRequests = pr;
                tokensUsed = tokens;
                if (tokensUsed.HasValue)
                {
                    _logger.LogInformation(
                        "Agentic CLI usage for task {Task}: premiumRequests={PR} tokens(est)={Tokens}",
                        invocation.Task.TaskId, pr ?? 0, tokensUsed.Value);
                }
            }
            catch (Exception parseEx)
            {
                _logger.LogDebug(parseEx, "Failed to parse agentic CLI usage from session log");
            }

            // Diagnostic: persist agentic session log for forensic analysis. Agentic
            // can report "succeeded" but still produce an empty patch (the CLI ran
            // its tool calls but wrote nothing to the worktree); without the log we
            // can't see WHY. File is best-effort — don't fail the strategy on IO error.
            TryPersistAgenticLog(invocation, result);

            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = true,
                Elapsed = sw.Elapsed,
                TokensUsed = tokensUsed,
                Log = new[] { TruncateLog(result.LogBuffer) },
                NoOpAcknowledged = noOpAcknowledged,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticDelegationStrategy threw for task {Task}", invocation.Task.TaskId);
            return Fail(sw, $"strategy-exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private StrategyExecutionResult Fail(Stopwatch sw, string reason) => new()
    {
        StrategyId = Id,
        Succeeded = false,
        FailureReason = reason,
        Elapsed = sw.Elapsed,
    };

    /// <summary>
    /// Lightweight surgical revision: runs a focused CLI session that makes targeted edits
    /// based on judge feedback. Much faster than full re-execution (~30s-2min vs 7-19min).
    /// Reuses the same sandbox/validation pipeline as <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task<StrategyExecutionResult> ExecuteRevisionAsync(
        Frameworks.FrameworkRevisionInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sink = invocation.ActivitySink;

        CopilotCliAgenticScope scope;
        try
        {
            scope = CopilotCliAgenticScope.Prepare(invocation.WorktreePath);
            sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Revision sandbox scope prepared"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Revision sandbox prepare failed for task {Task}", invocation.TaskId);
            return Fail(sw, $"revision-sandbox-prepare: {ex.GetType().Name}: {ex.Message}");
        }

        var prompt = _promptBuilder.BuildRevisionPrompt(invocation);
        sink?.Report(new Frameworks.FrameworkActivityEvent("revision", "Surgical revision prompt built, launching focused session"));

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            CloseStdinAfterPrompt = true,
            WatchdogMode = CopilotCliWatchdogMode.Agentic,
            WorkingDirectory = invocation.WorktreePath,
            EnvironmentOverrides = scope.EnvironmentOverrides,
            Timeout = invocation.Timeout,
            ActivitySink = sink,
        };

        var validator = new SandboxPostRunValidator(_logger);
        var snapshot = SandboxPostRunValidator.TakeSnapshot(invocation.WorktreePath);

        try
        {
            var result = await _processManager.ExecuteAgenticSessionAsync(prompt, options, ct);

            sink?.Report(new Frameworks.FrameworkActivityEvent("revision",
                $"Revision finished: {(result.Succeeded ? "succeeded" : "failed")} " +
                $"(tool-calls: {result.ToolCallCount}, wall: {result.WallClock.TotalSeconds:F1}s)"));

            if (!result.Succeeded)
            {
                var reason = result.FailureReason switch
                {
                    AgenticFailureReason.StuckNoOutput => "revision-stuck-no-output",
                    AgenticFailureReason.ToolCallCap => "revision-tool-call-cap",
                    AgenticFailureReason.Timeout => "revision-timeout",
                    AgenticFailureReason.Canceled => "revision-canceled",
                    AgenticFailureReason.ExitNonzero => $"revision-exit-nonzero: {result.ExitCode}",
                    AgenticFailureReason.LaunchFailed => $"revision-launch-failed: {result.ErrorMessage}",
                    _ => result.ErrorMessage ?? "revision-unknown-failure",
                };
                sink?.Report(new Frameworks.FrameworkActivityEvent("error", $"Revision failed: {reason}"));
                return Fail(sw, reason);
            }

            // Post-run sandbox validation (same as full execution)
            sink?.Report(new Frameworks.FrameworkActivityEvent("sandbox", "Running post-revision sandbox validation"));
            var violations = validator.Validate(
                invocation.WorktreePath, snapshot, scope.SandboxGitconfigPath);
            if (violations.Count > 0)
            {
                var codes = string.Join(",", violations.Select(v => v.Code));
                sink?.Report(new Frameworks.FrameworkActivityEvent("error", $"Revision sandbox violation: {codes}"));
                return Fail(sw, $"revision-sandbox-violation: {codes}");
            }

            sink?.Report(new Frameworks.FrameworkActivityEvent("sandbox", "Revision sandbox validation passed ✓"));

            _logger.LogInformation(
                "Surgical revision succeeded for task {Task} (tool-calls: {ToolCalls}, wall: {Wall}s)",
                invocation.TaskId, result.ToolCallCount, result.WallClock.TotalSeconds);

            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = true,
                Elapsed = sw.Elapsed,
                Log = new[] { TruncateLog(result.LogBuffer) },
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surgical revision threw for task {Task}", invocation.TaskId);
            return Fail(sw, $"revision-exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string TruncateLog(string log)
    {
        const int MaxLogChars = 8 * 1024;
        return log.Length <= MaxLogChars ? log : log[..MaxLogChars] + "\n… [truncated]";
    }

    /// <summary>
    /// Extract usage metrics from an agentic CLI session log.
    /// The session log is JSONL; the final <c>{"type":"result", ..., "usage":{"premiumRequests":N, "totalApiDurationMs":M}}</c>
    /// line carries premium-request count (the CLI's billing unit) and total API
    /// duration. The CLI does NOT expose raw token counts, so we estimate as
    /// <c>premiumRequests × {TokensPerPremiumRequest}</c>. This is a rough average
    /// matching what Squad's stdout summary shows on the same hardware/model.
    ///
    /// Falls back to scanning for a Squad-style "Tokens ↑ Xk · ↓ Yk" line
    /// in case the CLI surfaces that format in a future version.
    /// </summary>
    /// <returns>(premiumRequests, estimatedTokens). Either may be null if absent.</returns>
    internal static (int? PremiumRequests, long? EstimatedTokens) ParseAgenticUsage(string logBuffer)
    {
        if (string.IsNullOrWhiteSpace(logBuffer))
            return (null, null);

        // Conservative middle-of-the-road estimate. Claude Opus typical session: 25-50K tokens
        // per premium request. We pick 30K to err slightly under so we don't over-report cost.
        const long TokensPerPremiumRequest = 30_000;

        // Pass 1: scan the most recent {"type":"result"} JSONL line for usage.
        var lines = logBuffer.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            if (!line.Contains("\"type\":\"result\"", StringComparison.Ordinal)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("usage", out var usage)) break;
                if (!usage.TryGetProperty("premiumRequests", out var pr)) break;
                var premiumRequests = pr.GetInt32();
                if (premiumRequests <= 0) return (premiumRequests, null);
                return (premiumRequests, premiumRequests * TokensPerPremiumRequest);
            }
            catch (System.Text.Json.JsonException)
            {
                // malformed JSON on this line — ignore and try older shapes
                break;
            }
        }

        // Pass 2: Squad-style "Tokens ↑ Xk · ↓ Yk" summary (older CLI shapes / future-proof)
        var metrics = Frameworks.SquadStdoutParser.ParseMetrics(logBuffer);
        if (metrics.TotalTokens.HasValue)
            return (null, metrics.TotalTokens);

        return (null, null);
    }

    /// <summary>
    /// Heuristic: scan the agentic session log for the agent's final assistant message
    /// indicating that no work was needed (e.g. files from a prior merged PR were already
    /// present at <c>BaseSha</c>). Returns true only when the agent's own self-reported
    /// completion language is present — never inferred from absence.
    /// </summary>
    /// <remarks>
    /// Pattern source: real CLI outputs observed in <c>experiment-data/*.log</c> when the
    /// agent inspects the worktree, finds the implementation already in place, and exits
    /// without making changes. Examples: "Clean working tree — everything is already
    /// committed", "task is complete", "the task is already done".
    /// </remarks>
    internal static bool LogIndicatesTaskAlreadyComplete(string logBuffer)
    {
        if (string.IsNullOrWhiteSpace(logBuffer)) return false;

        // Only check the tail of the log — the agent's FINAL message is what matters.
        // Earlier discussion of "the task" doesn't count as a self-reported done state.
        const int TailWindow = 4 * 1024;
        var tail = logBuffer.Length <= TailWindow
            ? logBuffer
            : logBuffer[(logBuffer.Length - TailWindow)..];

        // Phrase combinations that strongly indicate self-reported "no work needed".
        // Each entry is a pair: both substrings must appear in the tail (case-insensitive).
        // Two-substring AND-matching prevents false positives from phrases like
        // "make sure the task is complete by …" appearing mid-prompt.
        var signals = new (string A, string B)[]
        {
            ("clean working tree",      "already"),
            ("task is complete",        "already"),
            ("task is already complete", ""),
            ("nothing to do",           "task"),
            ("everything is already",   ""),
            ("no changes needed",       "task"),
            ("work is already",         ""),
        };

        foreach (var (a, b) in signals)
        {
            if (tail.Contains(a, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(b) || tail.Contains(b, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts files and total line-delta in the worktree via <c>git diff --stat</c>.
    /// Best-effort: returns zeros on any error (don't fail the strategy on stats).
    /// </summary>
    private static (int FileCount, int LineCount) CountWorktreeChanges(string worktreePath, string? baseSha)
    {
        try
        {
            // Stage any untracked files so git diff sees them. The agentic CLI often
            // commits mid-run (git add + commit), making git diff --stat HEAD return
            // nothing. By diffing against baseSha we capture ALL changes since worktree
            // creation, regardless of whether they were committed or left untracked.
            var addPsi = new System.Diagnostics.ProcessStartInfo("git", "add -A")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var addProc = System.Diagnostics.Process.Start(addPsi);
            addProc?.WaitForExit(10_000);

            // Use baseSha if available (captures committed + staged changes); fall back
            // to HEAD (only sees staged but uncommitted) for callers without baseSha.
            var diffTarget = !string.IsNullOrWhiteSpace(baseSha) ? baseSha : "HEAD";
            // Exclude framework scaffolding paths (same as ExtractPatchAsync) so that
            // .sandbox/, .copilot/, bin/, obj/, etc. don't trip the diff-size guard.
            // Use :(exclude,glob)**/ for build directories to match at any nesting depth.
            var buildDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "bin", "obj", "node_modules", "TestResults", "test-results", ".vs" };
            var excludes = string.Join(" ", GitWorktreeManager.FrameworkExcludePaths
                .Select(p =>
                {
                    var trimmed = p.TrimEnd('/');
                    return buildDirs.Contains(trimmed)
                        ? $"\":(exclude,glob)**/{trimmed}/**\""
                        : $"\":!{trimmed}\"";
                }));
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"diff --stat {diffTarget} -- . {excludes}")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return (0, 0);
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);

            // Last line of git diff --stat looks like: " 42 files changed, 1500 insertions(+), 300 deletions(-)"
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return (0, 0);

            var summary = lines[^1];
            var fileCount = 0;
            var lineCount = 0;

            // Parse "N file(s) changed"
            var fileMatch = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+)\s+file");
            if (fileMatch.Success) fileCount = int.Parse(fileMatch.Groups[1].Value);

            // Parse insertions and deletions
            var insertMatch = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+)\s+insertion");
            var deleteMatch = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+)\s+deletion");
            if (insertMatch.Success) lineCount += int.Parse(insertMatch.Groups[1].Value);
            if (deleteMatch.Success) lineCount += int.Parse(deleteMatch.Groups[1].Value);

            return (fileCount, lineCount);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void TryPersistAgenticLog(StrategyInvocation invocation, AgenticSessionResult result)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "experiment-data");
            Directory.CreateDirectory(dir);
            var safeTask = new string((invocation.Task.TaskId ?? "unknown")
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var fileName = $"{stamp}-{safeTask}-agentic.log";
            var fullPath = Path.Combine(dir, fileName);
            var header = $"# Agentic session log\n" +
                         $"# stamp: {stamp}\n" +
                         $"# task: {invocation.Task.TaskId} — {invocation.Task.TaskTitle}\n" +
                         $"# worktree: {invocation.WorktreePath}\n" +
                         $"# succeeded: {result.Succeeded}\n" +
                         $"# tool-calls: {result.ToolCallCount}\n" +
                         $"# wall: {result.WallClock.TotalSeconds:F1}s\n" +
                         $"# exit: {result.ExitCode}\n" +
                         $"# ----- stdout/stderr begin -----\n";
            File.WriteAllText(fullPath, header + (result.LogBuffer ?? ""));
            _logger.LogInformation("Persisted agentic session log: {Path}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist agentic session log for task {Task}", invocation.Task.TaskId);
        }
    }
}

/// <summary>
/// Builds the task prompt handed to the agentic copilot session. Extracted so
/// tests can assert prompt shape without needing a real process. Default
/// implementation emits a structured markdown block with acceptance criteria,
/// commit-message convention, and "do not push / do not touch files outside
/// this worktree" safety reminders.
/// </summary>
public class AgenticPromptBuilder
{
    /// <summary>Shared constraint block used by both full execution and revision prompts.</summary>
    private static string BuildConstraints(string worktreePath) =>
        $"""
        ## Working directory
        You are running inside the git worktree at `{worktreePath}`.

        ## Constraints
        - Modify files ONLY inside this worktree. Do NOT touch any file outside the working directory.
        - Do NOT run `git push` or any network-mutating operation. Commits are expected and fine.
        - Keep commits focused and scoped. Prefer one logical commit per concern.
        - Write a concise commit message: `fix(scope): description` with what/why.
        - If a test suite exists, try to keep it green.
        - Stop when the fixes are complete or when you cannot make forward progress.
        """;

    public virtual string Build(StrategyInvocation invocation)
    {
        var t = invocation.Task;
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(t.ExistingProjectContext))
        {
            sb.AppendLine("> ⚠️ **WARNING: This is an EXISTING project with working code. You are EXTENDING it, not creating it from scratch.**");
            sb.AppendLine("> Do NOT scaffold new project files (Program.cs, .csproj, package.json, tsconfig) if they already exist.");
            sb.AppendLine("> Read existing code FIRST, then make surgical additions that fit the existing architecture.");
            sb.AppendLine();
        }
        sb.AppendLine($"# Task {t.TaskId}: {t.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(t.TaskDescription);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(t.ExistingProjectContext))
        {
            sb.AppendLine("## Existing Project Context");
            sb.AppendLine("This task is part of an EXISTING project. The following summary describes the project's current state, structure, patterns, and conventions. **You MUST align your implementation with these existing patterns.**");
            sb.AppendLine();
            sb.AppendLine(t.ExistingProjectContext);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(t.PmSpec))
        {
            sb.AppendLine("## Product Spec (context)");
            sb.AppendLine(t.PmSpec);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(t.Architecture))
        {
            sb.AppendLine("## Architecture (context)");
            sb.AppendLine(t.Architecture);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(t.DesignContext))
        {
            sb.AppendLine("## UI / Design context");
            sb.AppendLine(t.DesignContext);
            sb.AppendLine();
        }
        sb.AppendLine("## Working directory");
        sb.AppendLine($"You are running inside the git worktree at `{invocation.WorktreePath}`.");
        sb.AppendLine($"Branch: `{t.PrBranch}` (based on `{t.BaseSha}`).");
        sb.AppendLine();
        sb.AppendLine("## Constraints");
        sb.AppendLine("- Modify files ONLY inside this worktree. Do NOT touch any file outside the working directory.");
        sb.AppendLine("- Do NOT run `git push` or any network-mutating operation. Commits are expected and fine.");
        sb.AppendLine("- Keep commits focused and scoped to this task. Prefer one logical commit per concern.");
        sb.AppendLine("- Write a concise commit message: `{type}({scope}): {title}` body with what/why.");
        sb.AppendLine("- If a test suite exists, try to keep it green. If you add new tests, ensure they pass.");
        sb.AppendLine("- Do NOT impose artificial file-size limits or budgets (e.g., 100KB). Files can be as large as needed.");
        sb.AppendLine("- **Parallelize where possible**: run independent commands simultaneously (e.g., backend build + frontend install, multiple test suites). Don't wait for one to finish before starting the next.");
        sb.AppendLine("- Stop when the acceptance criteria are met or when you cannot make forward progress.");
        sb.AppendLine();
        if (invocation.Revision is { } rev)
        {
            sb.AppendLine("## REVISION ROUND — Targeted Fix");
            sb.AppendLine();
            sb.AppendLine("This is a REVISION attempt. Your initial code already exists in the working directory.");
            sb.AppendLine("Do NOT regenerate everything from scratch. Make TARGETED fixes based on the feedback below.");
            sb.AppendLine();
            sb.AppendLine("### Initial Judge Scores (0-10):");
            foreach (var (axis, score) in rev.InitialScores)
                sb.AppendLine($"- {axis}: {score}/10");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(rev.JudgeFeedback))
            {
                sb.AppendLine("### Judge Feedback (what to fix):");
                sb.AppendLine(rev.JudgeFeedback);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(rev.RubberDuckFeedback))
            {
                sb.AppendLine("### Independent Critique (second opinion):");
                sb.AppendLine(rev.RubberDuckFeedback);
                sb.AppendLine();
            }
            sb.AppendLine("### Instructions:");
            sb.AppendLine("- Read the existing files in the working directory first");
            sb.AppendLine("- Fix ONLY the specific issues mentioned in the feedback");
            sb.AppendLine("- Do not refactor or rewrite code that wasn't flagged");
            sb.AppendLine("- Focus on raising the lowest-scoring axes");
            sb.AppendLine();
        }

        sb.AppendLine("## Methodology (CRITICAL — follow this order)");
        sb.AppendLine("**BEFORE writing any code**, you MUST explore the existing project:");
        sb.AppendLine("1. **Read the project structure** — list the top-level directory, understand the layout");
        sb.AppendLine("2. **Read existing related files** — find files related to your task (grep for relevant keywords, read imports, understand patterns already in use)");
        sb.AppendLine("3. **Understand build/test setup** — check package.json scripts, .csproj files, Makefile, etc. to know how to build and test");
        sb.AppendLine("4. **Match existing patterns** — use the same naming conventions, file organization, import style, error handling, and architectural patterns as the existing code");
        sb.AppendLine("5. **Then implement** — write code that fits naturally into the existing project, not code that looks like a standalone example");
        sb.AppendLine("6. **Build and test** — run the project's build command and verify your changes compile and tests pass before finishing");
        sb.AppendLine();
        sb.AppendLine("Common mistakes to avoid:");
        sb.AppendLine("- Do NOT generate boilerplate project scaffolding (Program.cs, package.json, tsconfig.json) if these files already exist — read and extend them");
        sb.AppendLine("- Do NOT use different frameworks/libraries than what the project already uses (e.g., don't add Express if the project uses Fastify)");
        sb.AppendLine("- Do NOT create files in the wrong directory — check where similar files live first");
        sb.AppendLine("- Do NOT ignore existing type definitions, interfaces, or shared utilities — import and reuse them");
        sb.AppendLine();

        sb.AppendLine("## Begin");
        sb.AppendLine("Implement this task autonomously. You have --allow-all — use tools freely, but respect the constraints and methodology above.");
        sb.AppendLine();
        sb.AppendLine("## Self-Monitoring");
        sb.AppendLine("- **Report progress frequently** — after every few files created or modified, output a brief status line (e.g., 'Created 5/12 files, moving to tests').");
        sb.AppendLine("- If a tool call fails or hangs with no response for over a minute, **skip it** and continue with other work.");
        sb.AppendLine("- If you cannot reach an MCP server, fall back to **direct file operations** (read_file, write_file, bash commands) instead of waiting.");
        sb.AppendLine("- If you find yourself repeating the **same action 3+ times** with the same error, stop, reassess, and try a completely different approach.");
        sb.AppendLine("- If a `npm install`, `dotnet restore`, or similar package command hangs, skip it — scaffold the code files and let the build gate validate later.");
        sb.AppendLine("- If you cannot make meaningful forward progress after 3 different attempts at a sub-task, **commit what you have** and move on to the next part.");
        sb.AppendLine("- Do NOT wait silently — always produce output explaining what you're doing and why.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a lightweight revision-only prompt. Contains ONLY feedback, scores, and file hints.
    /// No task description, PM spec, or architecture — avoids encouraging from-scratch regeneration.
    /// </summary>
    public virtual string BuildRevisionPrompt(Frameworks.FrameworkRevisionInvocation invocation)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Surgical Revision — Targeted Fixes Only");
        sb.AppendLine();
        sb.AppendLine($"**Task:** {invocation.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine(BuildConstraints(invocation.WorktreePath));
        sb.AppendLine();

        // Scores
        sb.AppendLine("## Current Scores (0-10):");
        foreach (var (axis, score) in invocation.InitialScores)
            sb.AppendLine($"- **{axis}**: {score}/10");
        sb.AppendLine();

        // Judge feedback
        if (!string.IsNullOrWhiteSpace(invocation.JudgeFeedback))
        {
            sb.AppendLine("## Judge Feedback (primary — fix these issues):");
            sb.AppendLine(invocation.JudgeFeedback);
            sb.AppendLine();
        }

        // Per-axis feedback (only for low-scoring axes)
        var axisFeedback = new List<(string axis, string? feedback)>
        {
            ("Acceptance Criteria", invocation.AcFeedback),
            ("Design", invocation.DesignFeedback),
            ("Readability", invocation.ReadabilityFeedback),
            ("Visuals", invocation.VisualsFeedback),
        };
        var hasAxisFeedback = axisFeedback.Any(f => !string.IsNullOrWhiteSpace(f.feedback));
        if (hasAxisFeedback)
        {
            sb.AppendLine("## Per-Axis Feedback:");
            foreach (var (axis, feedback) in axisFeedback)
            {
                if (!string.IsNullOrWhiteSpace(feedback))
                    sb.AppendLine($"### {axis}\n{feedback}\n");
            }
        }

        // Rubber-duck critique
        if (!string.IsNullOrWhiteSpace(invocation.RubberDuckFeedback))
        {
            sb.AppendLine("## Independent Critique (second opinion):");
            sb.AppendLine(invocation.RubberDuckFeedback);
            sb.AppendLine();
        }

        // File hints
        if (invocation.OriginalFiles.Count > 0)
        {
            sb.AppendLine("## Files from initial implementation (focus here):");
            foreach (var file in invocation.OriginalFiles.Take(20))
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }

        // Instructions
        sb.AppendLine("## Instructions");
        sb.AppendLine("- Read the existing files listed above");
        sb.AppendLine("- Make ONLY targeted fixes for the specific issues in the feedback");
        sb.AppendLine("- Do NOT rewrite, restructure, or regenerate files from scratch");
        sb.AppendLine("- Do NOT add new features or files unless explicitly requested in feedback");
        sb.AppendLine("- Focus on raising the lowest-scoring axes");
        sb.AppendLine("- Commit your changes when done");
        sb.AppendLine();
        sb.AppendLine("## Begin");
        sb.AppendLine("Make the surgical edits now. Read files → apply fixes → commit.");
        return sb.ToString();
    }
}
