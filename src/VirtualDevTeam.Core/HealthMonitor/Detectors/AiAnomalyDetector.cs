using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.21 AI Anomaly Detector — last-resort meta-detector that asks an LLM to inspect the
/// current flow snapshot for anomalies that the rule-based detectors didn't catch. Designed
/// per the LangGraph / MetaGPT supervisor-router pattern: AI is *advisory*, not authoritative.
///
/// <para>
/// **Hard rules of engagement** (rubber-duck from research synthesis fm-1, fm-10):
/// </para>
/// <list type="bullet">
///   <item>Only fires when no other detector has produced an Open finding within the last
///         <see cref="_skipWhenRecentFindings"/> window — supervisor must stay simpler than
///         the system it watches (lesson from AutoGen).</item>
///   <item>Hard 2-second LLM timeout. If the model is slow, we skip and try next tick.</item>
///   <item>Cooldown between AI calls (default 1 hour) so a single anomalous state doesn't
///         spam $0.001 calls indefinitely.</item>
///   <item>Severity capped at <see cref="FlowFindingSeverity.Warning"/> — even if the LLM
///         insists something is critical, we surface it as Warning. The human promotes via
///         the dashboard if they agree.</item>
///   <item>Confidence-gated: the LLM must report a self-confidence ≥
///         <see cref="FlowMonitorConfig.ConfidenceThreshold"/>, else no finding emitted.</item>
///   <item>Stateless call, not a loop — the detector does NOT chain follow-up LLM calls.</item>
/// </list>
///
/// <para>
/// **Optional deps:** if <see cref="IChatCompletionRunner"/> is null (e.g. tests, project not
/// opened), the detector is a no-op. If <see cref="FlowMonitorPersistence"/> is null, the
/// "recent findings" skip-check is bypassed and we rely only on the cooldown.
/// </para>
/// </summary>
public sealed class AiAnomalyDetector : IFlowDetector
{
    public string DetectorId => "ai-anomaly";

    private readonly ILogger<AiAnomalyDetector> _logger;
    private readonly AI.IChatCompletionRunner? _chatRunner;
    private readonly FlowMonitorPersistence? _persistence;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _skipWhenRecentFindings;
    private readonly TimeSpan _llmTimeout;
    private readonly double _confidenceThreshold;
    private readonly string _modelTier;

    private DateTimeOffset _lastFiredAt = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public AiAnomalyDetector(
        ILogger<AiAnomalyDetector> logger,
        AI.IChatCompletionRunner? chatRunner = null,
        FlowMonitorPersistence? persistence = null,
        double confidenceThreshold = 0.75,
        TimeSpan? cooldown = null,
        TimeSpan? skipWhenRecentFindings = null,
        TimeSpan? llmTimeout = null,
        string modelTier = "standard")
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _chatRunner = chatRunner;
        _persistence = persistence;
        _confidenceThreshold = Math.Clamp(confidenceThreshold, 0.0, 1.0);
        _cooldown = cooldown ?? TimeSpan.FromHours(1);
        _skipWhenRecentFindings = skipWhenRecentFindings ?? TimeSpan.FromMinutes(10);
        _llmTimeout = llmTimeout ?? TimeSpan.FromSeconds(2);
        _modelTier = string.IsNullOrWhiteSpace(modelTier) ? "standard" : modelTier;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        if (_chatRunner is null) return Array.Empty<FlowFinding>();

        try
        {
            lock (_gate)
            {
                if (ctx.Now - _lastFiredAt < _cooldown) return Array.Empty<FlowFinding>();
            }

            // Skip when rule-based detectors already found something — supervisor stays
            // simpler than the watched system; rule findings deserve attention first.
            if (_persistence is not null)
            {
                var recent = _persistence.GetRecentFindings(20);
                var recentCutoff = ctx.Now - _skipWhenRecentFindings;
                if (recent.Any(f =>
                        f.State == FlowFindingState.Open &&
                        !string.Equals(f.DetectorId, DetectorId, StringComparison.OrdinalIgnoreCase) &&
                        f.DetectedAt >= recentCutoff))
                {
                    return Array.Empty<FlowFinding>();
                }
            }

            var contextSummary = SummarizeContext(ctx);
            // Skip if there is no meaningful context — empty agent list pre-project-open etc.
            if (string.IsNullOrWhiteSpace(contextSummary)) return Array.Empty<FlowFinding>();

            var systemPrompt = BuildSystemPrompt();
            var userPrompt = "Snapshot:\n" + contextSummary + "\n\n" +
                             "Return JSON: {\"anomaly\": <bool>, \"confidence\": <0.0-1.0>, " +
                             "\"summary\": <one sentence>, \"corrective\": <one sentence>}.";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_llmTimeout);

            string raw;
            try
            {
                raw = await _chatRunner.InvokeAsync(systemPrompt, userPrompt, _modelTier,
                    agentId: "flow-monitor-ai", cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("AI anomaly detector LLM call timed out at {Timeout}s — skipping tick",
                    _llmTimeout.TotalSeconds);
                return Array.Empty<FlowFinding>();
            }

            if (!TryParseVerdict(raw, out var verdict)) return Array.Empty<FlowFinding>();
            if (!verdict.Anomaly) return Array.Empty<FlowFinding>();
            if (verdict.Confidence < _confidenceThreshold) return Array.Empty<FlowFinding>();

            lock (_gate) { _lastFiredAt = ctx.Now; }

            return new[]
            {
                new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    // Capped at Warning — operator promotes via dashboard if they agree.
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = "ai-advisor",
                    Summary = $"AI advisor flagged anomaly (confidence={verdict.Confidence:0.00}): {Truncate(verdict.Summary, 120)}",
                    Rationale = "AI anomaly detector ran because no rule-based detector found Open findings " +
                                $"within the last {_skipWhenRecentFindings.TotalMinutes:0}m. " +
                                $"Corrective suggestion: {verdict.Corrective}\n\n" +
                                "Treat as a hint, not a directive — promote to Critical only if you agree after " +
                                "inspecting the dashboard.",
                    DedupKey = $"ai-anomaly:{HashSummary(verdict.Summary)}",
                }
            };
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiAnomalyDetector tick failed (non-fatal)");
            return Array.Empty<FlowFinding>();
        }
    }

    private static string BuildSystemPrompt() =>
        "You are a flow-anomaly advisor for a multi-agent system. Inspect the snapshot and answer in JSON only. " +
        "An ANOMALY is a state that looks unusual or wrong for a healthy multi-agent run — e.g. all agents idle " +
        "during an active phase, contradictory work-item labels, mismatched signal/phase pairs. Do NOT flag " +
        "ordinary slow progress. Do NOT flag scenarios that a rule-based detector would already catch (an idle " +
        "agent during a phase change, a stale PR, a known deadlock). Confidence MUST be < 0.5 unless you can " +
        "point to a specific contradiction in the snapshot. JSON format: " +
        "{\"anomaly\":<bool>,\"confidence\":<0.0-1.0>,\"summary\":<string>,\"corrective\":<string>}. " +
        "Output ONLY the JSON object, no commentary.";

    private static string SummarizeContext(DetectorContext ctx)
    {
        if (ctx.Agents.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"phase={ctx.CurrentPhase}");
        sb.AppendLine($"signals={(ctx.WorkflowSignals.Count == 0 ? "(none)" : string.Join(",", ctx.WorkflowSignals.Take(12)))}");
        sb.AppendLine("agents:");
        foreach (var a in ctx.Agents.Take(20))
        {
            var idleSec = a.StatusChangedAt is null ? -1 : (int)(ctx.Now - a.StatusChangedAt.Value).TotalSeconds;
            sb.AppendLine($"  - {a.DisplayName} role={a.Role} status={a.Status} since={idleSec}s reason=\"{Truncate(a.StatusReason ?? string.Empty, 50)}\"");
        }
        return sb.ToString();
    }

    private static bool TryParseVerdict(string raw, out AiVerdict verdict)
    {
        verdict = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // Extract first JSON object — strip Markdown fences if present.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        var json = raw.Substring(start, end - start + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var anomaly = root.TryGetProperty("anomaly", out var an) && an.GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var cn) ? cn.GetDouble() : 0.0;
            var summary = root.TryGetProperty("summary", out var su) ? (su.GetString() ?? string.Empty) : string.Empty;
            var corrective = root.TryGetProperty("corrective", out var co) ? (co.GetString() ?? string.Empty) : string.Empty;
            verdict = new AiVerdict(anomaly, Math.Clamp(confidence, 0.0, 1.0), summary, corrective);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string HashSummary(string s)
    {
        if (string.IsNullOrEmpty(s)) return "empty";
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 6);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private readonly record struct AiVerdict(bool Anomaly, double Confidence, string Summary, string Corrective);
}
