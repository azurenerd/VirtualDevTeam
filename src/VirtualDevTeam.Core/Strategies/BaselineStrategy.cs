using System.Diagnostics;
using VirtualDevTeam.Core.Frameworks;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Phase-1 baseline strategy. When an <see cref="IBaselineCodeGenerator"/> is wired,
/// delegates to it for real single-shot SE-equivalent code generation. When no
/// generator is provided (e.g., orchestrator integration tests, lightweight harnesses),
/// falls back to writing a marker file so the rest of the pipeline (patch extraction,
/// gates, tracker) can still be exercised end-to-end.
/// </summary>
public class BaselineStrategy : ICodeGenerationStrategy
{
    public string Id => "baseline";
    private readonly ILogger<BaselineStrategy> _logger;
    private readonly IBaselineCodeGenerator? _generator;

    public BaselineStrategy(ILogger<BaselineStrategy> logger, IBaselineCodeGenerator? generator = null)
    {
        _logger = logger;
        _generator = generator;
    }

    public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sink = invocation.ActivitySink;

        if (_generator is not null)
        {
            try
            {
                sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Starting single-pass code generation"));
                var outcome = await _generator.GenerateAsync(invocation.WorktreePath, invocation.Task, ct, "baseline-strategy", sink, invocation.Revision);
                if (!outcome.Succeeded)
                {
                    sink?.Report(new Frameworks.FrameworkActivityEvent("error",
                        $"Generation failed: {outcome.FailureReason}"));
                    _logger.LogWarning(
                        "BaselineStrategy generator failed for task {TaskId}: {Reason}",
                        invocation.Task.TaskId, outcome.FailureReason);
                }
                else
                {
                    sink?.Report(new Frameworks.FrameworkActivityEvent("complete",
                        $"Generation finished: {outcome.FilesWritten} file(s), {outcome.TokensUsed:N0} tokens"));
                    _logger.LogDebug(
                        "BaselineStrategy wrote {Files} file(s) for task {TaskId}",
                        outcome.FilesWritten, invocation.Task.TaskId);
                }
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = outcome.Succeeded,
                    FailureReason = outcome.FailureReason,
                    Elapsed = sw.Elapsed,
                    TokensUsed = outcome.TokensUsed,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = $"generator-exception: {ex.GetType().Name}: {ex.Message}",
                    Elapsed = sw.Elapsed,
                };
            }
        }

        // No generator wired — preserve the original marker behavior so orchestrator
        // tests and lightweight harnesses still produce a non-empty patch. The SE
        // integration (TryRunStrategyFrameworkAsync) rejects marker-only patches as
        // an extra safety net, so this can never accidentally ship a no-op live PR.
        try
        {
            sink?.Report(new Frameworks.FrameworkActivityEvent("init", "No code generator wired — producing stub marker"));
            var marker = Path.Combine(invocation.WorktreePath, ".strategy-baseline.md");
            var body =
                $"# Baseline candidate (stub — no IBaselineCodeGenerator wired)\n\n" +
                $"Task: {invocation.Task.TaskTitle}\n" +
                $"TaskId: {invocation.Task.TaskId}\n" +
                $"Strategy: {Id}\n" +
                $"Generated-At: {DateTimeOffset.UtcNow:O}\n";
            await File.WriteAllTextAsync(marker, body, ct);

            _logger.LogDebug(
                "BaselineStrategy produced stub marker (no generator wired) at {Marker}", marker);
            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = true,
                Elapsed = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = false,
                FailureReason = $"{ex.GetType().Name}: {ex.Message}",
                Elapsed = sw.Elapsed,
            };
        }
    }
}
