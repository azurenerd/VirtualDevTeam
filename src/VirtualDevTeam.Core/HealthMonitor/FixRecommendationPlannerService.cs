using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Builds <see cref="FixRecommendation"/> documents for Critical FlowMonitor findings
/// that have no vetted in-process action handler. Uses a two-pass Copilot CLI invocation:
/// <list type="number">
///   <item>Pass 1: GitHub /plan-style structured Markdown fix plan (Problem, Root cause,
///         Proposed fix, Risks, Verification).</item>
///   <item>Pass 2: <em>fresh, separate</em> conversation as a skeptical "rubber-duck"
///         reviewer that critiques pass 1, lists top 3 risks, and emits a confidence
///         score (0.0–1.0) as JSON.</item>
/// </list>
/// Pass 2 sees only the plan from pass 1 — never the original finding — so it cannot
/// anchor on the same framing. Empirically reduces hallucination because the adversarial
/// prompt forces critique. The merged Markdown is persisted to SQLite and the
/// <c>/FixRecommendations/</c> folder; the recommendation surfaces on the Approvals page.
/// </summary>
public sealed class FixRecommendationPlannerService
{
    private readonly IChatCompletionRunner _chatRunner;
    private readonly FlowMonitorPersistence _persistence;
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<FixRecommendationPlannerService> _logger;

    /// <summary>Model tier used for both passes. Keep at "premium" — adversarial review needs reasoning.</summary>
    private const string ModelTier = "premium";

    /// <summary>
    /// Captures the JSON object emitted at the end of pass 2: <c>{"confidence":0.85,"top_risks":[...]}</c>.
    /// Tolerant of whitespace and trailing prose by anchoring on the last balanced object in the response.
    /// </summary>
    private static readonly Regex ConfidenceJsonPattern =
        new(@"\{[^{}]*""confidence""\s*:\s*(?<conf>[01]?\.\d+|[01])[^{}]*\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FixRecommendationPlannerService(
        IChatCompletionRunner chatRunner,
        FlowMonitorPersistence persistence,
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<FixRecommendationPlannerService> logger)
    {
        _chatRunner = chatRunner ?? throw new ArgumentNullException(nameof(chatRunner));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Two-pass plan generation for a Critical finding. Returns a fully-populated
    /// <see cref="FixRecommendation"/> in <see cref="FixRecommendationState.Draft"/> state.
    /// The caller is responsible for persisting it (so we don't double-persist when the rework
    /// path also uses this method via <see cref="ReviseAsync"/>).
    /// </summary>
    public async Task<FixRecommendation> GenerateAsync(FlowFinding finding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);

        // Pass 1 — structured plan
        var planBody = await RunPlanPassAsync(finding, ct).ConfigureAwait(false);

        // Pass 2 — adversarial rubber-duck. Brand-new ChatHistory, no carryover.
        var (critiqueBody, confidence) = await RunRubberDuckPassAsync(planBody, ct).ConfigureAwait(false);

        var stitched = StitchMarkdown(planBody, critiqueBody);
        var (filesToChange, needsRestart, estimatedMinutes) = ExtractMetadata(stitched);

        // T1.6: classify the recommendation so the approve endpoint knows whether it can
        // apply live (config/prompt reload), needs a restart (.cs/.razor change), or has to
        // be staged for the next boot (NuGet, schema). We compute this at insertion time so
        // the operator sees the tier badge as soon as the plan lands on the Approvals page.
        var draft = new FixRecommendation
        {
            Id = Guid.NewGuid().ToString("N"),
            FindingId = finding.Id,
            DetectorId = finding.DetectorId,
            Severity = finding.Severity,
            Confidence = confidence,
            NeedsRestart = needsRestart,
            FilesToChange = filesToChange,
            EstimatedMinutes = estimatedMinutes,
            PlanMarkdown = stitched,
            State = FixRecommendationState.PendingReview,
        };
        var classification = FixClassifier.Classify(draft);
        return draft with
        {
            FixTier = classification.Tier,
            AffectedFiles = classification.AffectedFiles,
        };
    }

    /// <summary>
    /// Third Copilot pass: revise an existing recommendation using operator feedback.
    /// Inserts a brand-new recommendation row (history preserved) referencing the same finding.
    /// Increments the rework count on the prior recommendation.
    /// </summary>
    public async Task<FixRecommendation?> ReviseAsync(string existingId, string operatorFeedback, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(existingId);
        ArgumentException.ThrowIfNullOrEmpty(operatorFeedback);

        var existing = _persistence.GetRecommendation(existingId);
        if (existing is null)
        {
            _logger.LogWarning("ReviseAsync: recommendation {Id} not found", existingId);
            return null;
        }

        var systemPrompt =
            "You are a senior engineer revising a previously-proposed fix plan based on operator feedback. " +
            "Preserve the original Markdown structure (## Problem, ## Root cause analysis, ## Proposed fix, " +
            "## Risks/alternatives considered, ## Verification steps), but incorporate the operator's guidance " +
            "as a constraint. Be specific about what changed from the previous draft. Output Markdown only.";

        var userPrompt =
            $"# Previous plan (rework round {existing.ReworkCount + 1})\n\n" +
            existing.PlanMarkdown +
            "\n\n# Operator feedback (apply as a hard constraint)\n\n" +
            operatorFeedback +
            "\n\nRevise the plan above. Keep what's still valid; replace what conflicts with the feedback.";

        var revised = await SafeInvokeAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(revised))
        {
            _logger.LogWarning("ReviseAsync: empty response from LLM for {Id}", existingId);
            return null;
        }

        // Run rubber-duck again on the revised plan to refresh the confidence score.
        var (critique, confidence) = await RunRubberDuckPassAsync(revised, ct).ConfigureAwait(false);
        var stitched = StitchMarkdown(revised, critique);
        var (filesToChange, needsRestart, estimatedMinutes) = ExtractMetadata(stitched);

        // Bump the prior recommendation's rework counter for audit, then insert a new row.
        _persistence.IncrementReworkCount(existingId, operatorFeedback);

        var revisedDraft = new FixRecommendation
        {
            Id = Guid.NewGuid().ToString("N"),
            FindingId = existing.FindingId,
            DetectorId = existing.DetectorId,
            Severity = existing.Severity,
            Confidence = confidence,
            NeedsRestart = needsRestart,
            FilesToChange = filesToChange,
            EstimatedMinutes = estimatedMinutes,
            PlanMarkdown = stitched,
            OperatorFeedback = operatorFeedback,
            ReworkCount = existing.ReworkCount + 1,
            State = FixRecommendationState.PendingReview,
        };
        // T1.6: re-classify the revised plan — operator feedback can flip a fix between
        // tiers (e.g. "use a config setting instead of a code change" turns Deferred → Live).
        var revisedClassification = FixClassifier.Classify(revisedDraft);
        return revisedDraft with
        {
            FixTier = revisedClassification.Tier,
            AffectedFiles = revisedClassification.AffectedFiles,
        };
    }

    /// <summary>
    /// Persist the recommendation Markdown to a file under <c>/FixRecommendations/</c> in the
    /// runner repo root. The file name is <c>yyyyMMdd-HHmmss-{finding-id-short}.md</c> for an
    /// initial plan, or <c>...-rework-v{N}.md</c> for reworks (never overwrites existing files).
    /// Returns the absolute path that was written, or null on failure (write errors are logged
    /// but never thrown — flow continues even if disk is read-only).
    /// </summary>
    public async Task<string?> SaveToFixRecommendationsFolderAsync(
        FixRecommendation r, string repoRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);

        try
        {
            var dir = Path.Combine(repoRoot, "FixRecommendations");
            Directory.CreateDirectory(dir);

            var stamp = r.CreatedAt.ToString("yyyyMMdd-HHmmss");
            var shortId = r.FindingId.Length > 8 ? r.FindingId[..8] : r.FindingId;
            var suffix = r.ReworkCount > 0 ? $"-rework-v{r.ReworkCount + 1}" : string.Empty;
            var fileName = $"{stamp}-{shortId}{suffix}.md";
            var fullPath = Path.Combine(dir, fileName);

            // Never overwrite — if a same-name file exists (highly unlikely with second-precision
            // timestamps), append a -dup{N} suffix to keep history.
            if (File.Exists(fullPath))
            {
                for (var i = 2; i < 100; i++)
                {
                    var attempt = Path.Combine(dir, $"{stamp}-{shortId}{suffix}-dup{i}.md");
                    if (!File.Exists(attempt)) { fullPath = attempt; break; }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"id: {r.Id}");
            sb.AppendLine($"finding_id: {r.FindingId}");
            sb.AppendLine($"detector_id: {r.DetectorId}");
            sb.AppendLine($"severity: {r.Severity}");
            sb.AppendLine($"confidence: {r.Confidence:0.00}");
            sb.AppendLine($"needs_restart: {(r.NeedsRestart ? "true" : "false")}");
            if (r.FixTier.HasValue)
                sb.AppendLine($"fix_tier: {r.FixTier.Value}");
            if (r.AffectedFiles is { Count: > 0 })
                sb.AppendLine($"affected_files: [{string.Join(", ", r.AffectedFiles.Select(f => $"\"{f}\""))}]");
            if (!string.IsNullOrEmpty(r.FilesToChange))
                sb.AppendLine($"files_to_change: {r.FilesToChange}");
            if (r.EstimatedMinutes.HasValue)
                sb.AppendLine($"estimated_minutes: {r.EstimatedMinutes.Value}");
            sb.AppendLine($"created_at: {r.CreatedAt:o}");
            sb.AppendLine($"rework_count: {r.ReworkCount}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(r.PlanMarkdown);

            await File.WriteAllTextAsync(fullPath, sb.ToString(), ct).ConfigureAwait(false);
            _logger.LogInformation("Wrote fix recommendation to {Path}", fullPath);
            return fullPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveToFixRecommendationsFolderAsync failed for {Id}", r.Id);
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private async Task<string> RunPlanPassAsync(FlowFinding finding, CancellationToken ct)
    {
        var systemPrompt =
            "You are a senior engineer. Use GitHub Copilot /plan conventions to produce a structured " +
            "fix plan with sections: ## Problem ## Root cause analysis ## Proposed fix " +
            "## Risks/alternatives considered ## Verification steps. Output Markdown only — no preamble, " +
            "no code-fence wrapping the whole document. " +
            "If the proposed fix touches C# (.cs) source files or NuGet dependencies, mention 'needs restart' " +
            "in the Verification steps so downstream tooling can detect it. " +
            "If you list files in the Proposed fix section, prefix them with `Files to change:` on a single " +
            "line so they can be parsed. " +
            "If you can estimate effort, include a single line `Estimated effort: N minutes`.";

        var userPrompt =
            $"# Flow finding\n\n" +
            $"- Detector: `{finding.DetectorId}`\n" +
            $"- Severity: **{finding.Severity}**\n" +
            $"- Detected at: {finding.DetectedAt:o}\n" +
            (string.IsNullOrEmpty(finding.TargetAgentId)
                ? string.Empty
                : $"- Target agent: `{finding.TargetAgentId}`\n") +
            (string.IsNullOrEmpty(finding.TargetResource)
                ? string.Empty
                : $"- Target resource: `{finding.TargetResource}`\n") +
            $"\n## Summary\n{finding.Summary}\n\n" +
            $"## Rationale\n{finding.Rationale}\n\n" +
            "Produce the structured fix plan now.";

        var response = await SafeInvokeAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false);
        return response ?? string.Empty;
    }

    private async Task<(string CritiqueBody, double Confidence)> RunRubberDuckPassAsync(
        string planBody, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planBody))
            return ("(No plan available to critique — pass 1 produced empty output.)", 0.0);

        var systemPrompt =
            "You are a skeptical rubber-duck reviewer. The user will paste a fix plan. Challenge every " +
            "assumption in the plan. List the top 3 risks. Be terse — operators read this in a small " +
            "card. End your response with a single JSON object on its own line containing your confidence " +
            "score in the plan's correctness: " +
            "{\"confidence\": 0.00, \"top_risks\": [\"...\", \"...\", \"...\"]}. " +
            "Confidence must be a decimal between 0.0 and 1.0. Do NOT echo the plan back. " +
            "Output Markdown for the critique body, then the JSON line at the end.";

        var userPrompt =
            "Please critique this fix plan:\n\n" + planBody;

        var response = await SafeInvokeAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response))
            return ("(Rubber-duck pass returned no output — confidence defaulted to 0.5.)", 0.5);

        var confidence = ParseConfidence(response);
        return (response, confidence);
    }

    private async Task<string?> SafeInvokeAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage(systemPrompt);
            history.AddUserMessage(userPrompt);

            return await _chatRunner.InvokeAsync(new ChatCompletionRequest
            {
                History = history,
                ModelTier = ModelTier,
                AgentId = "flow-monitor:planner",
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FixRecommendationPlanner: LLM invocation failed");
            return null;
        }
    }

    private static string StitchMarkdown(string planBody, string critiqueBody)
    {
        var sb = new StringBuilder();
        sb.Append(planBody.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Rubber-duck critique");
        sb.AppendLine();
        sb.Append(critiqueBody.TrimEnd());
        return sb.ToString();
    }

    /// <summary>
    /// Try to extract a confidence value from the rubber-duck pass output. Tolerates the LLM
    /// wrapping the JSON in code fences, leading prose, or trailing whitespace. Returns 0.5
    /// if no valid confidence value is found — a deliberate "I don't know" default that won't
    /// trigger the auto-notify threshold (0.75) on its own.
    /// </summary>
    internal static double ParseConfidence(string passTwoOutput)
    {
        if (string.IsNullOrWhiteSpace(passTwoOutput)) return 0.5;

        // Try strict JSON parse first by hunting for the last '{...}' block (LLMs sometimes
        // emit multiple JSON-like fragments — the final one is the canonical answer).
        var lastOpen = passTwoOutput.LastIndexOf('{');
        if (lastOpen >= 0)
        {
            var lastClose = passTwoOutput.IndexOf('}', lastOpen);
            if (lastClose > lastOpen)
            {
                var candidate = passTwoOutput.Substring(lastOpen, lastClose - lastOpen + 1);
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    if (doc.RootElement.TryGetProperty("confidence", out var confEl))
                    {
                        if (confEl.ValueKind == JsonValueKind.Number && confEl.TryGetDouble(out var v))
                            return Clamp01(v);
                        if (confEl.ValueKind == JsonValueKind.String &&
                            double.TryParse(confEl.GetString(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var sv))
                            return Clamp01(sv);
                    }
                }
                catch (JsonException)
                {
                    // Fall through to regex below
                }
            }
        }

        // Regex fallback for misshapen JSON like {confidence: 0.8} (missing quotes) or
        // pretty-printed multi-line objects that don't survive the simple last-brace scan.
        var match = ConfidenceJsonPattern.Match(passTwoOutput);
        if (match.Success && double.TryParse(match.Groups["conf"].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var rv))
        {
            return Clamp01(rv);
        }

        return 0.5;
    }

    private static double Clamp01(double v) => v switch
    {
        < 0.0 => 0.0,
        > 1.0 => 1.0,
        _ => v,
    };

    /// <summary>
    /// Heuristic extraction of files / restart-needed / minutes from the planner output.
    /// Keeps things simple — these are best-effort metadata used to enrich the dashboard card,
    /// not authoritative claims. Missing values are returned as null/false.
    /// </summary>
    private static (string? Files, bool NeedsRestart, int? Minutes) ExtractMetadata(string md)
    {
        if (string.IsNullOrEmpty(md)) return (null, false, null);

        // Files: a line like "Files to change: src/Foo.cs, src/Bar.cs"
        string? files = null;
        var filesMatch = Regex.Match(md, @"(?im)^\s*(?:-\s*)?\*{0,2}Files to change\*{0,2}\s*:?\s*(?<list>.+)$");
        if (filesMatch.Success)
        {
            var raw = filesMatch.Groups["list"].Value.Trim();
            // Strip Markdown emphasis around items
            raw = raw.Replace("`", "").Trim();
            if (raw.Length > 0 && raw.Length <= 1000) files = raw;
        }

        // Needs-restart heuristic: any mention of "needs restart" or any .cs file in the file list.
        var needsRestart = Regex.IsMatch(md, @"(?i)\bneeds\s+restart\b") ||
                           (files is not null && Regex.IsMatch(files, @"(?i)\.cs\b|\bnuget\b|\.csproj\b"));

        // Minutes: "Estimated effort: 30 minutes" or "Estimated effort: ~30 min"
        int? minutes = null;
        var minutesMatch = Regex.Match(md, @"(?i)Estimated\s+effort\s*:?\s*~?\s*(?<n>\d{1,4})\s*(?:min|minute)");
        if (minutesMatch.Success && int.TryParse(minutesMatch.Groups["n"].Value, out var m))
            minutes = m;

        return (files, needsRestart, minutes);
    }
}
