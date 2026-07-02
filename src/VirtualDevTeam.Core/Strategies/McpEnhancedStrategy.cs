using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Mcp;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Phase-2 strategy: same single-pass generator as <see cref="BaselineStrategy"/>,
/// but scoped so the copilot CLI can invoke the workspace-reader MCP server to
/// inspect the candidate's worktree before generating code. The delta versus
/// baseline is NOT a different prompt or a different model — it's a per-invocation
/// context (<see cref="CopilotCliInvocationContext"/>) that:
/// <list type="bullet">
///   <item><description>Passes an inline <c>--additional-mcp-config</c> naming the scoped workspace-reader server.</description></item>
///   <item><description>Grants <c>--allow-tool=workspace-reader</c> so the CLI can actually call it.</description></item>
///   <item><description>Overrides the process CWD to the candidate worktree so relative paths resolve consistently with the baseline write path.</description></item>
/// </list>
/// The scope is installed ONLY for the duration of <see cref="IBaselineCodeGenerator.GenerateAsync"/>
/// and torn down before the orchestrator extracts the patch. That guarantees the
/// CLI args and prompt wording stay baseline-shaped outside this strategy.
/// </summary>
public class McpEnhancedStrategy : ICodeGenerationStrategy
{
    public string Id => "mcp-enhanced";

    private const string McpServerName = "workspace-reader";

    private readonly ILogger<McpEnhancedStrategy> _logger;
    private readonly IBaselineCodeGenerator? _generator;
    private readonly IMcpServerLocator _locator;

    public McpEnhancedStrategy(
        ILogger<McpEnhancedStrategy> logger,
        IMcpServerLocator locator,
        IBaselineCodeGenerator? generator = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _generator = generator;
    }

    public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_generator is null)
        {
            // Unlike BaselineStrategy we do NOT write a stub marker file here. A marker
            // from mcp-enhanced would either (a) be indistinguishable in the winner-apply
            // path — risking shipping a stub — or (b) pollute experiment records with
            // pretend-success entries. Fail loudly instead; orchestrator unit tests that
            // need this strategy wired can inject a fake generator.
            _logger.LogWarning(
                "McpEnhancedStrategy has no IBaselineCodeGenerator wired; returning failure for task {Task}",
                invocation.Task.TaskId);
            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = false,
                FailureReason = "no-generator: mcp-enhanced requires IBaselineCodeGenerator to be registered",
                Elapsed = sw.Elapsed,
            };
        }

        // Resolve the MCP server binary. Failure here is treated as a hard failure —
        // we refuse to run a no-tools baseline under the mcp-enhanced banner because
        // that would mislabel the experiment record.
        McpServerLaunchSpec spec;
        try
        {
            spec = _locator.Resolve();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpEnhancedStrategy could not locate MCP server for task {Task}", invocation.Task.TaskId);
            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = false,
                FailureReason = $"mcp-server-not-found: {ex.Message}",
                Elapsed = sw.Elapsed,
            };
        }

        // Build inline MCP config. The server is spawned once per CLI call and is
        // scoped read-only to this candidate's worktree via its own --root arg.
        string configJson;
        try
        {
            var args = new List<string>(spec.FixedArgs) { "--root", invocation.WorktreePath };
            configJson = McpConfigWriter
                .BuildConfig(McpServerName, spec.Command, args)
                .ToJsonString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpEnhancedStrategy could not build MCP config for task {Task}", invocation.Task.TaskId);
            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = false,
                FailureReason = $"mcp-config-build: {ex.GetType().Name}: {ex.Message}",
                Elapsed = sw.Elapsed,
            };
        }

        // Install the per-invocation scope. AsyncLocal flow guarantees the args
        // builder and prompt flattener observe a consistent view for the duration
        // of the generator call. Dispose happens before patch extraction runs.
        var ctx = new CopilotCliInvocationContext(
            AdditionalMcpConfigJson: configJson,
            AllowedMcpTools: new[] { McpServerName },
            OverrideWorkingDirectory: invocation.WorktreePath);

        try
        {
            using var _ = AgentCallContext.PushInvocationContext(ctx);

            var outcome = await _generator.GenerateAsync(
                invocation.WorktreePath, invocation.Task, ct, strategyTag: "mcp-enhanced-strategy",
                revision: invocation.Revision);

            if (!outcome.Succeeded)
            {
                _logger.LogWarning(
                    "McpEnhancedStrategy generator failed for task {Task}: {Reason}",
                    invocation.Task.TaskId, outcome.FailureReason);
            }
            else
            {
                _logger.LogDebug(
                    "McpEnhancedStrategy wrote {Files} file(s) for task {Task} (server at {Dll})",
                    outcome.FilesWritten, invocation.Task.TaskId, spec.ResolvedPath);
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
}
