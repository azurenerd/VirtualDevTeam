using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Decides, for a single task, which strategies should actually run.
/// Given the configured <see cref="StrategyFrameworkConfig.EnabledStrategies"/>,
/// <see cref="StrategyFrameworkConfig.SamplingPolicy"/>, and
/// <see cref="StrategyFrameworkConfig.Budget"/>, returns the filtered list plus a
/// human-readable reason so the experiment tracker can record why.
///
/// Policy syntax (in <see cref="StrategyFrameworkConfig.SamplingPolicy"/>):
/// <list type="bullet">
///   <item><c>always</c> (default): run every enabled strategy.</item>
///   <item><c>disabled</c>: run only <c>baseline</c>. Multi-strategy completely off.</item>
///   <item><c>complexity-above:N</c>: only run non-baseline strategies if the task
///     <see cref="TaskContext.Complexity"/> is strictly greater than N.</item>
///   <item><c>every-n:N</c>: deterministically pick multi-strategy on every Nth task
///     (hash of taskId mod N == 0). Useful for low-cost sampling over a backlog.</item>
///   <item><c>random-pct:P</c>: randomly pick multi-strategy P% of the time (0..100).</item>
/// </list>
/// When budget is exhausted for a run, non-baseline strategies are dropped
/// regardless of sampling policy (baseline always survives so the SE agent still
/// gets an answer).
/// </summary>
public sealed class StrategySamplingPolicy
{
    private readonly ILogger<StrategySamplingPolicy> _logger;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly RunBudgetTracker _budget;

    public StrategySamplingPolicy(
        ILogger<StrategySamplingPolicy> logger,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        RunBudgetTracker budget)
    {
        _logger = logger;
        _cfg = cfg;
        _budget = budget;
    }

    public SamplingDecision Decide(TaskContext task, IReadOnlyList<string> enabled)
    {
        var cfg = _cfg.CurrentValue;
        var policy = (cfg.SamplingPolicy ?? "always").Trim();

        // Budget check is authoritative — it overrides sampling.
        if (_budget.IsExhausted(task.RunId))
        {
            var reduced = enabled.Where(IsAlwaysOn).ToList();
            return new SamplingDecision(reduced, $"budget-exhausted (kept: {string.Join(",", reduced)})");
        }

        if (enabled.Count <= 1) return new SamplingDecision(enabled, "single-strategy");

        if (policy.Equals("always", StringComparison.OrdinalIgnoreCase))
            return new SamplingDecision(enabled, "policy=always");

        if (policy.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            var reduced = enabled.Where(IsAlwaysOn).ToList();
            return new SamplingDecision(reduced, "policy=disabled (baseline-only)");
        }

        if (TryParsePrefixed(policy, "complexity-above:", out var threshold))
        {
            if (task.Complexity > threshold)
                return new SamplingDecision(enabled, $"complexity {task.Complexity} > {threshold}");

            var reduced = enabled.Where(IsAlwaysOn).ToList();
            return new SamplingDecision(reduced, $"complexity {task.Complexity} <= {threshold} (baseline-only)");
        }

        if (TryParsePrefixed(policy, "every-n:", out var n) && n > 0)
        {
            var hash = StableHash(task.TaskId);
            if (hash % n == 0)
                return new SamplingDecision(enabled, $"every-n:{n} hit (taskId hash)");

            var reduced = enabled.Where(IsAlwaysOn).ToList();
            return new SamplingDecision(reduced, $"every-n:{n} miss");
        }

        if (TryParsePrefixed(policy, "random-pct:", out var pct) && pct is > 0 and <= 100)
        {
            // Deterministic per (runId,taskId) so the same task is treated the
            // same way on retry. Hash-derived roll, NOT Random().
            var roll = (int)(StableHash(task.RunId + "|" + task.TaskId) % 100);
            if (roll < pct)
                return new SamplingDecision(enabled, $"random-pct:{pct} hit (roll={roll})");

            var reduced = enabled.Where(IsAlwaysOn).ToList();
            return new SamplingDecision(reduced, $"random-pct:{pct} miss (roll={roll})");
        }

        _logger.LogWarning("Unknown SamplingPolicy '{Policy}'; treating as always", policy);
        return new SamplingDecision(enabled, $"policy={policy} (unknown, defaulting to always)");
    }

    /// <summary>Baseline is the safety net — it always runs regardless of policy.</summary>
    private static bool IsAlwaysOn(string strategyId)
        => string.Equals(strategyId, "baseline", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePrefixed(string value, string prefix, out int parsed)
    {
        parsed = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(value.Substring(prefix.Length), out parsed);
    }

    /// <summary>FNV-1a 64-bit. Stable across runs, doesn't depend on string.GetHashCode randomization.</summary>
    private static long StableHash(string s)
    {
        const long offset = unchecked((long)14695981039346656037UL);
        const long prime = 1099511628211L;
        var hash = offset;
        foreach (var c in s)
        {
            hash ^= c;
            unchecked { hash *= prime; }
        }
        return hash & 0x7FFF_FFFF_FFFF_FFFFL;
    }
}

public sealed record SamplingDecision(IReadOnlyList<string> SelectedStrategies, string Reason);
