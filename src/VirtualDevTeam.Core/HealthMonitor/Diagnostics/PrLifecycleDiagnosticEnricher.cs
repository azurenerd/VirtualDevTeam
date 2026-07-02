using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Core.HealthMonitor.Diagnostics;

/// <summary>
/// Diagnostic enricher for PR lifecycle stuck states. Checks the specific conditions
/// each reviewer agent requires before acting:
/// <list type="bullet">
///   <item>PM requires: tests-added label AND TE completion comment</item>
///   <item>TE requires: architect-approved label AND no tests-added</item>
///   <item>Architect requires: ready-for-review label AND no architect-approved</item>
/// </list>
/// Produces a checklist of passed/failed checks and a recommended fix action.
/// </summary>
public sealed class PrLifecycleDiagnosticEnricher : IFlowDiagnosticEnricher
{
    private readonly ILogger<PrLifecycleDiagnosticEnricher> _logger;

    public PrLifecycleDiagnosticEnricher(ILogger<PrLifecycleDiagnosticEnricher> logger)
    {
        _logger = logger;
    }

    public bool CanEnrich(string detectorId) =>
        detectorId is "idle-agent-phase-stuck" or "agent-stuck";

    public async Task<FlowFinding> EnrichAsync(FlowFinding finding, DetectorContext ctx, CancellationToken ct)
    {
        if (finding.TargetResource is null || !finding.TargetResource.StartsWith("pr#"))
            return finding;

        if (!int.TryParse(finding.TargetResource.Replace("pr#", ""), out var prNumber))
            return finding;

        var role = finding.TargetDisplayName ?? finding.TargetAgentId ?? "";
        var diagnostics = new List<FlowDiagnostic>();
        string? fixId = null;
        string? fixDesc = null;

        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct);
            var pr = prs.FirstOrDefault(p => p.Number == prNumber);
            if (pr is null)
            {
                diagnostics.Add(new FlowDiagnostic("PR exists", false, $"PR #{prNumber} not found in open PRs"));
                return finding with { Diagnostics = diagnostics };
            }

            bool has(string label) => pr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

            if (IsPmRole(role))
            {
                diagnostics.Add(new FlowDiagnostic(
                    "architect-approved label", has("architect-approved"),
                    has("architect-approved") ? "Present" : "Missing — Architect has not reviewed yet"));

                diagnostics.Add(new FlowDiagnostic(
                    "tests-added label", has("tests-added"),
                    has("tests-added") ? "Present" : "Missing — TE has not added tests yet"));

                // Defense-in-depth: PM also requires a TE completion comment.
                // FlowMonitor context doesn't have full comment access — report honestly
                // as "not verified" rather than falsely claiming "missing."
                if (has("tests-added"))
                {
                    diagnostics.Add(new FlowDiagnostic(
                        "TE completion comment", true,
                        "Not verified in FlowMonitor context — tests-added label present, " +
                        "PM will check for TE comment during its own review scan"));
                }
                else
                {
                    fixId = $"nudge-agent:TestEngineer:pr#{prNumber}";
                    fixDesc = $"Nudge Test Engineer to test PR #{prNumber} — " +
                              "tests-added label is missing, blocking PM review";
                }

                if (!has("architect-approved"))
                {
                    fixId = $"nudge-agent:Architect:pr#{prNumber}";
                    fixDesc = $"Nudge Architect to review PR #{prNumber} — " +
                              "architect-approved label is missing";
                }
            }
            else if (IsTeRole(role))
            {
                diagnostics.Add(new FlowDiagnostic(
                    "architect-approved label", has("architect-approved"),
                    has("architect-approved") ? "Present — TE can proceed" : "Missing — TE waiting for Architect"));

                diagnostics.Add(new FlowDiagnostic(
                    "tests-added label", !has("tests-added"),
                    has("tests-added") ? "Already present — TE may have been bypassed" : "Not yet applied — TE should assess"));

                if (!has("architect-approved"))
                {
                    fixId = $"nudge-agent:Architect:pr#{prNumber}";
                    fixDesc = $"Nudge Architect to review PR #{prNumber} first — TE is waiting";
                }
            }
            else if (IsArchitectRole(role))
            {
                diagnostics.Add(new FlowDiagnostic(
                    "ready-for-review label", has("ready-for-review"),
                    has("ready-for-review") ? "Present — Architect can review" : "Missing — SE has not marked ready"));

                if (!has("ready-for-review"))
                {
                    fixId = $"nudge-agent:SoftwareEngineer:pr#{prNumber}";
                    fixDesc = $"Nudge SE to mark PR #{prNumber} ready-for-review";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PrLifecycleDiagnosticEnricher failed for finding {Id}", finding.Id);
            diagnostics.Add(new FlowDiagnostic("Enrichment", false, $"Failed: {ex.Message}"));
        }

        return finding with
        {
            Diagnostics = diagnostics,
            RecommendedFixId = fixId,
            RecommendedFixDescription = fixDesc,
        };
    }

    private static bool IsPmRole(string role) =>
        role.Contains("ProgramManager", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("Program Manager", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("PM", StringComparison.OrdinalIgnoreCase);

    private static bool IsTeRole(string role) =>
        role.Contains("TestEngineer", StringComparison.OrdinalIgnoreCase) ||
        role.Contains("Test Engineer", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchitectRole(string role) =>
        role.Contains("Architect", StringComparison.OrdinalIgnoreCase);
}
