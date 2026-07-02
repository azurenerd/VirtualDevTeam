using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Prompts;

namespace VirtualDevTeam.Core.MissingWork;

/// <summary>
/// Implements <see cref="IMissingWorkPlanner"/> by invoking the Copilot CLI (SingleShot pool,
/// premium model) to produce a structured <see cref="ProposedIssue"/> JSON from a
/// <see cref="MissingWorkFinding"/>. On success the proposal is persisted via
/// <see cref="MissingWorkPersistence.InsertProposedIssue"/>; on any failure the method returns
/// null and logs a warning so the detector runner continues unaffected.
/// </summary>
public sealed class MissingWorkPlanner : IMissingWorkPlanner
{
    private readonly CopilotCliProcessManager _cli;
    private readonly IPromptTemplateService _prompts;
    private readonly MissingWorkPersistence _persistence;
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<MissingWorkPlanner> _logger;

    // Premium-tier model — same tier used by PM + Architect for quality-critical decisions.
    private const string PremiumModel = "claude-opus-4.8";

    public MissingWorkPlanner(
        CopilotCliProcessManager cli,
        IPromptTemplateService prompts,
        MissingWorkPersistence persistence,
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<MissingWorkPlanner> logger)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProposedIssue?> PlanProposalAsync(MissingWorkFinding finding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);
        try
        {
            var threshold = _config.CurrentValue.MissingWork?.PlannerConfidenceThreshold ?? 0.6;
            if (finding.Confidence < threshold)
            {
                _logger.LogDebug(
                    "MissingWorkPlanner: skipping finding {Id} (confidence {Conf:F2} < threshold {Thr:F2})",
                    finding.Id, finding.Confidence, threshold);
                return null;
            }

            var promptVars = new Dictionary<string, string>
            {
                ["detector_id"] = finding.DetectorId,
                ["pattern"] = finding.Pattern,
                ["summary"] = finding.Summary,
                ["confidence"] = finding.Confidence.ToString("F2"),
                ["evidence_block"] = BuildEvidenceBlock(finding.Evidence),
            };

            var prompt = await _prompts.RenderAsync("missing-work/planner", promptVars, ct);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                _logger.LogWarning(
                    "MissingWorkPlanner: template 'missing-work/planner' not found; skipping finding {Id}",
                    finding.Id);
                return null;
            }

            var options = new CopilotCliRequestOptions
            {
                Pool = CopilotCliPool.SingleShot,
                ModelOverride = PremiumModel,
                Timeout = TimeSpan.FromMinutes(2),
            };

            var result = await _cli.ExecutePromptAsync(prompt, options, ct);

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogWarning(
                    "MissingWorkPlanner: CLI returned no usable output for finding {Id} (exit={Exit})",
                    finding.Id, result.ExitCode);
                return null;
            }

            var parsed = ParseProposal(result.Output, finding);
            if (parsed is null)
            {
                _logger.LogWarning(
                    "MissingWorkPlanner: failed to parse JSON proposal for finding {Id}. Output snippet: {Snippet}",
                    finding.Id, result.Output.Length > 200 ? result.Output[..200] : result.Output);
                return null;
            }

            _persistence.InsertProposedIssue(parsed);
            _logger.LogInformation(
                "MissingWorkPlanner: persisted proposed issue {ProposalId} for finding {FindingId} — '{Title}'",
                parsed.Id, finding.Id, parsed.ProposedTitle);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MissingWorkPlanner failed for finding {Id} (non-fatal)", finding.Id);
            return null;
        }
    }

    /// <summary>Formats evidence citations as a numbered Markdown list for the prompt.</summary>
    private static string BuildEvidenceBlock(IReadOnlyList<EvidenceCitation> evidence)
    {
        if (evidence.Count == 0) return "(no evidence citations)";
        var sb = new StringBuilder();
        for (var i = 0; i < evidence.Count; i++)
        {
            var e = evidence[i];
            sb.Append(i + 1).Append(". `").Append(e.FilePath);
            if (e.LineNumber.HasValue) sb.Append(':').Append(e.LineNumber);
            sb.Append('`');
            if (!string.IsNullOrWhiteSpace(e.Kind)) sb.Append(" [").Append(e.Kind).Append(']');
            if (!string.IsNullOrWhiteSpace(e.Snippet)) sb.Append(" — ").Append(e.Snippet);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Extracts the JSON object from the CLI output and constructs a <see cref="ProposedIssue"/>.
    /// The model is instructed to emit a single-line JSON object; we search for the first
    /// balanced <c>{…}</c> block to tolerate any preamble or trailing prose.
    /// </summary>
    private ProposedIssue? ParseProposal(string output, MissingWorkFinding finding)
    {
        // Locate the first '{' and match to its closing '}'.
        var start = output.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var end = -1;
        for (var i = start; i < output.Length; i++)
        {
            if (output[i] == '{') depth++;
            else if (output[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end < 0) return null;

        var json = output[start..(end + 1)];
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return null;

            var title = node["title"]?.GetValue<string>() ?? "";
            var body = node["body"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return null;

            var labels = ParseStringArray(node["labels"]);
            var dependsOn = ParseIntArray(node["depends_on"]);
            var blocks = ParseIntArray(node["blocks"]);

            return new ProposedIssue
            {
                Id = Guid.NewGuid().ToString("N"),
                FindingId = finding.Id,
                DetectorId = finding.DetectorId,
                State = ProposedIssueState.Pending,
                ProposedTitle = title.Length > 80 ? title[..80] : title,
                ProposedBody = body,
                ProposedLabels = labels,
                ProposedDependsOn = dependsOn,
                ProposedBlocks = blocks,
                Confidence = finding.Confidence,
                Evidence = finding.Evidence,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MissingWorkPlanner: JSON parse error for finding {Id}", finding.Id);
            return null;
        }
    }

    private static IReadOnlyList<string> ParseStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return Array.Empty<string>();
        var result = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var s = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
        }
        return result;
    }

    private static IReadOnlyList<int> ParseIntArray(JsonNode? node)
    {
        if (node is not JsonArray arr) return Array.Empty<int>();
        var result = new List<int>(arr.Count);
        foreach (var item in arr)
        {
            try { result.Add(item?.GetValue<int>() ?? 0); } catch { /* skip non-integer */ }
        }
        return result;
    }
}
