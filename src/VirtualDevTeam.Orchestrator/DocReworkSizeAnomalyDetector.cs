using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor detector — doc-rework-size-anomaly.
///
/// <para>
/// Catches a specific class of silent failure observed when document-rework agents
/// (PM revising PMSpec.md, Architect revising Architecture.md) are asked to make a
/// surgical edit following operator feedback but the model returns a result that is
/// dramatically larger or smaller than the previous version. The canonical observed
/// regression: PMSpec.md grew from 64 → 31500 chars (~492× ratio) on a "surgical edit"
/// — almost certainly the model wrote a fresh document instead of reading the current
/// one. The opposite case (full → stub) is equally pathological. Either way, the operator
/// needs to verify the diff before approving a second-cycle rework.
/// </para>
///
/// <para>
/// This is the DOC-REWORK sibling of <see cref="ImageRegenAnomalyDetector"/>. Where the
/// image variant inspects a perceptual hash of the regenerated PNG, this one inspects
/// the textual size delta between the latest commit on a doc PR and the previous one.
/// Both share the same general algorithm: walk doc PRs, get commit history, fetch the
/// file content at the latest two SHAs, compute a delta, emit a finding when the delta
/// crosses a configured threshold.
/// </para>
///
/// <para>
/// <b>Algorithm</b>:
/// <list type="number">
///   <item>Enumerate open PRs via <see cref="IPlatformView.ListOpenPullRequestsAsync"/>.</item>
///   <item>Filter to "doc PRs" by title prefix (PM Spec or Architecture). The reworkable
///         documents are PMSpec.md and Architecture.md.</item>
///   <item>For each candidate PR, fetch its commit history. A rework requires ≥2 commits —
///         if there's only one, the doc was created in a single pass and there's no rework
///         to assess.</item>
///   <item>Fetch the PR's overall changed-files list. Filter to <c>.md</c> files whose
///         basename matches the expected doc for the PR type (PMSpec.md / Architecture.md).
///         These docs may live at the repo root OR under <c>AgentDocs/&lt;scope&gt;/</c>;
///         the basename match works for both layouts.</item>
///   <item>For each candidate doc path, fetch text content at the latest commit SHA and at
///         the second-to-latest commit SHA. Skip if either fetch returns null (file added
///         in only one commit — no prior version to compare against).</item>
///   <item>Compute size delta on the character lengths:
///         <c>ratio = newSize / max(oldSize, 1)</c> and <c>absDelta = |newSize - oldSize|</c>.
///         The ratio is computed in both directions (max(new/old, old/new)) so a "shrink to stub"
///         (e.g. 31500 → 64 chars) flags as severely as a "balloon" (64 → 31500). Critical
///         if <c>ratio &gt; 2.0</c> AND <c>absDelta &gt; 2000</c>; Warning if
///         <c>ratio &gt; 1.3</c> AND <c>absDelta &gt; 500</c> (and not already Critical).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Severity</b>: Critical for the >2× ratio + >2000 char delta case (this is the
/// "stub-to-full-rewrite" regression directly observed in production). Warning for the
/// >1.3× + >500 char case (catches accidental section additions / smaller-but-still-suspect
/// edits). The Critical threshold ensures the operator gets paged via the
/// FixRecommendation flow (T1.5) when no action handler resolves the finding; the Warning
/// threshold provides early signal without alarm fatigue.
/// </para>
///
/// <para>
/// <b>Dedup</b>: stable per <c>(prNumber, filePath)</c>. The FlowMonitor's window-based
/// dedup ensures a single open-but-unresolved finding doesn't refire on every tick. A
/// subsequent rework commit changes the SHAs but the dedup key intentionally does NOT
/// include them — if the same doc is reworked AGAIN and AGAIN produces an anomalous
/// delta, the operator only sees one finding per PR+doc until they act.
/// </para>
///
/// <para>
/// <b>Platform note</b>: the detector uses <see cref="IRepositoryContentService.GetFileContentAsync"/>
/// passing a commit SHA where the API expects a "branch" — this works on the GitHub adapter
/// (Octokit's <c>GetAllContentsByRef</c> accepts any git ref including SHAs). The ADO adapter
/// hardcodes <c>versionType=branch</c> and will silently fail this lookup; on ADO the detector
/// degrades to skipping the comparison rather than raising false findings. Same caveat as the
/// image-regen sibling — see its xml-doc for the long-form discussion.
/// </para>
/// </summary>
public sealed class DocReworkSizeAnomalyDetector : IFlowDetector
{
    public string DetectorId => "doc-rework-size-anomaly";

    /// <summary>Cap on PRs scanned per tick. Bounded API + content-fetch cost — at most this
    /// many commit-list fetches and 2× content fetches per tick.</summary>
    internal const int MaxPrsPerTick = 5;

    /// <summary>Critical ratio threshold. A ratio above this paired with an absolute delta
    /// above <see cref="CriticalAbsDelta"/> indicates a near-certain "stub-to-rewrite" or
    /// "rewrite-to-stub" event. Both bounds must be exceeded so trivially-sized docs don't
    /// false-positive (a 1-char doc growing to 3 chars is 3× ratio but only 2 chars delta).</summary>
    internal const double CriticalRatio = 2.0;

    /// <summary>Critical absolute character delta threshold. Paired with <see cref="CriticalRatio"/>.</summary>
    internal const int CriticalAbsDelta = 2000;

    /// <summary>Warning ratio threshold. Above this paired with <see cref="WarningAbsDelta"/>
    /// indicates a suspicious-but-not-catastrophic edit (e.g., accidental section addition).</summary>
    internal const double WarningRatio = 1.3;

    /// <summary>Warning absolute character delta threshold. Paired with <see cref="WarningRatio"/>.</summary>
    internal const int WarningAbsDelta = 500;

    /// <summary>PR title prefix marking a PMSpec doc PR. PMs publish these via
    /// <c>OpenDocumentPRAsync(agentName: "ProgramManager", prTitle: "PM Specification for ...")</c>
    /// → <c>"ProgramManager: PM Specification for ..."</c>. The "PM Spec" prefix is sufficient
    /// because "PM Specification" starts with it and we only check StartsWith on the title body.</summary>
    private const string PmSpecAgentPrefix = "ProgramManager:";
    private const string PmSpecTitleMarker = "PM Spec";

    /// <summary>Document basename for PM Spec PRs.</summary>
    private const string PmSpecDocBasename = "PMSpec.md";

    /// <summary>PR title prefix marking an Architecture doc PR. Architects publish these via
    /// <c>OpenDocumentPRAsync(agentName: "Architect", prTitle: "Architecture design for ...")</c>
    /// → <c>"Architect: Architecture design for ..."</c>.</summary>
    private const string ArchitectAgentPrefix = "Architect:";
    private const string ArchitectureTitleMarker = "Architecture";

    /// <summary>Document basename for Architecture PRs.</summary>
    private const string ArchitectureDocBasename = "Architecture.md";

    private readonly ILogger<DocReworkSizeAnomalyDetector> _logger;
    private readonly IPullRequestService? _prService;
    private readonly IRepositoryContentService? _contentService;

    public DocReworkSizeAnomalyDetector(
        ILogger<DocReworkSizeAnomalyDetector> logger,
        IPullRequestService? prService = null,
        IRepositoryContentService? contentService = null)
    {
        _logger = logger;
        _prService = prService;
        _contentService = contentService;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        // Pre-project-open: platform services aren't bound yet. Nothing to do.
        if (_prService is null || _contentService is null) return findings;

        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            if (prs.Count == 0) return findings;

            var prsToScan = prs
                .Where(p => ClassifyDocPr(p.Title) is not null)
                .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                .Take(MaxPrsPerTick)
                .ToList();

            foreach (var pr in prsToScan)
            {
                if (ct.IsCancellationRequested) break;
                var docBasename = ClassifyDocPr(pr.Title);
                if (docBasename is null) continue;
                await ScanPullRequestAsync(pr, docBasename, findings, ctx.Now, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DocReworkSizeAnomalyDetector tick failed (non-fatal)");
        }

        return findings;
    }

    private async Task ScanPullRequestAsync(
        PullRequestView pr,
        string docBasename,
        List<FlowFinding> findings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Rework requires ≥2 commits. One commit = original; nothing has been reworked yet.
        IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformCommitInfo> commits;
        try
        {
            commits = await _prService!.GetCommitsWithDatesAsync(pr.Number, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DocReworkSizeAnomalyDetector: GetCommitsWithDatesAsync failed for PR #{Pr} (skipping)", pr.Number);
            return;
        }
        if (commits is null || commits.Count < 2) return;

        // Order ascending by commit time; we use the last two.
        var sorted = commits.OrderBy(c => c.CommittedAt).ToList();
        var latest = sorted[^1];
        var previous = sorted[^2];
        if (string.IsNullOrEmpty(latest.Sha) || string.IsNullOrEmpty(previous.Sha)) return;
        if (string.Equals(latest.Sha, previous.Sha, StringComparison.OrdinalIgnoreCase)) return;

        // Fetch PR-wide diff to find changed doc paths. Per-commit diff isn't exposed by the
        // current capability surface, but the PR-wide changed-files set is a superset of "files
        // touched in any commit" — good enough for this MVP heuristic.
        IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformFileDiff> diffs;
        try
        {
            diffs = await _prService.GetFileDiffsAsync(pr.Number, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DocReworkSizeAnomalyDetector: GetFileDiffsAsync failed for PR #{Pr} (skipping)", pr.Number);
            return;
        }
        if (diffs is null || diffs.Count == 0) return;

        var candidatePaths = diffs
            .Where(d => !string.IsNullOrEmpty(d.FileName))
            .Where(d => MatchesDocBasename(d.FileName, docBasename))
            .Select(d => d.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidatePaths.Count == 0) return;

        foreach (var path in candidatePaths)
        {
            if (ct.IsCancellationRequested) break;

            string? latestContent;
            string? previousContent;
            try
            {
                latestContent = await _contentService!.GetFileContentAsync(path, latest.Sha, ct).ConfigureAwait(false);
                if (latestContent is null) continue;

                previousContent = await _contentService.GetFileContentAsync(path, previous.Sha, ct).ConfigureAwait(false);
                // Null previous = file was added in only one commit — no prior version to compare.
                // Per the spec: "Skip if only 1 commit touches the file (no rework)."
                if (previousContent is null) continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DocReworkSizeAnomalyDetector: failed to fetch content for {Path} on PR #{Pr} (skipping)", path, pr.Number);
                continue;
            }

            var newSize = latestContent.Length;
            var oldSize = previousContent.Length;
            var assessment = AssessSizeDelta(oldSize, newSize);
            if (assessment is null) continue;

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = now,
                DetectorId = DetectorId,
                Severity = assessment.Value.Severity,
                TargetResource = $"pr#{pr.Number}",
                TargetDisplayName = pr.AssignedAgent,
                Summary = BuildSummary(pr.Number, path, oldSize, newSize, assessment.Value),
                Rationale = BuildRationale(pr.Number, path, latest.Sha, previous.Sha, oldSize, newSize, assessment.Value),
                DedupKey = $"doc-rework-size-anomaly:{pr.Number}:{path}",
            });
        }
    }

    /// <summary>
    /// Classify a PR title as a PM Spec doc, an Architecture doc, or neither.
    /// Returns the expected document basename for matching files in the diff, or null
    /// if this PR isn't a doc-rework candidate.
    /// </summary>
    internal static string? ClassifyDocPr(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        if (title.StartsWith(PmSpecAgentPrefix, StringComparison.OrdinalIgnoreCase)
            && title.Contains(PmSpecTitleMarker, StringComparison.OrdinalIgnoreCase))
        {
            return PmSpecDocBasename;
        }

        if (title.StartsWith(ArchitectAgentPrefix, StringComparison.OrdinalIgnoreCase)
            && title.Contains(ArchitectureTitleMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ArchitectureDocBasename;
        }

        return null;
    }

    /// <summary>
    /// True when the file path's basename matches the expected doc basename. Accepts both
    /// the repo-root layout (<c>PMSpec.md</c>) and the AgentDocs layout
    /// (<c>AgentDocs/&lt;scope&gt;/PMSpec.md</c>). Rejects same-suffix files like
    /// <c>OldPMSpec.md</c> via path-segment boundary matching.
    /// </summary>
    internal static bool MatchesDocBasename(string filePath, string expectedBasename)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var normalized = filePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        var basename = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        return string.Equals(basename, expectedBasename, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inspect old vs new char counts and return the matching threshold tier or null
    /// when neither threshold is exceeded. Ratio is computed bidirectionally so a
    /// "shrink-to-stub" event flags as severely as a "balloon-to-rewrite" event:
    /// e.g. 31500→64 chars (492× shrink) and 64→31500 chars (492× growth) both
    /// produce the same Critical finding.
    /// </summary>
    internal static SizeAssessment? AssessSizeDelta(int oldSize, int newSize)
    {
        var absDelta = Math.Abs(newSize - oldSize);
        // Symmetric ratio: max(new/old, old/new). Floor denominators at 1 so a 0→N
        // change still produces a finite, large ratio rather than divide-by-zero.
        var growth = (double)newSize / Math.Max(oldSize, 1);
        var shrink = (double)oldSize / Math.Max(newSize, 1);
        var ratio = Math.Max(growth, shrink);

        if (ratio > CriticalRatio && absDelta > CriticalAbsDelta)
        {
            return new SizeAssessment(FlowFindingSeverity.Critical, ratio, absDelta);
        }
        if (ratio > WarningRatio && absDelta > WarningAbsDelta)
        {
            return new SizeAssessment(FlowFindingSeverity.Warning, ratio, absDelta);
        }
        return null;
    }

    internal readonly record struct SizeAssessment(FlowFindingSeverity Severity, double Ratio, int AbsDelta);

    private static string BuildSummary(int prNumber, string path, int oldSize, int newSize, SizeAssessment a)
    {
        var direction = newSize >= oldSize ? "grew" : "shrank";
        return $"Doc rework size anomaly: PR #{prNumber} {path} {direction} from {oldSize} → {newSize} chars ({a.Ratio:F1}× ratio, |Δ|={a.AbsDelta})";
    }

    private static string BuildRationale(
        int prNumber, string path, string latestSha, string previousSha,
        int oldSize, int newSize, SizeAssessment a)
    {
        var direction = newSize >= oldSize ? "grew" : "shrank";
        var verdict = a.Severity == FlowFindingSeverity.Critical
            ? "near-certain indicator that the rework either read a stub instead of the real document, or " +
              "the LLM ignored the surgical-edit prompt and produced a full rewrite. Verify the diff before " +
              "approving the rework"
            : "suspicious size delta — likely an over-aggressive section addition or an unintended structural " +
              "rewrite. Inspect the diff before approving";

        return
            $"PR #{prNumber}: {path} was reworked in commit {ShortSha(latestSha)} but the file size {direction} " +
            $"by {a.AbsDelta} characters ({oldSize} → {newSize}, ratio {a.Ratio:F2}× vs previous commit " +
            $"{ShortSha(previousSha)}). Above the {(a.Severity == FlowFindingSeverity.Critical ? "Critical" : "Warning")} " +
            $"threshold ({(a.Severity == FlowFindingSeverity.Critical ? $"ratio>{CriticalRatio} AND |Δ|>{CriticalAbsDelta}" : $"ratio>{WarningRatio} AND |Δ|>{WarningAbsDelta}")}). " +
            $"This is a {verdict}. " +
            "Suggested fix: verify ReviseDocumentAsync is reading current branch content, not a local stub. " +
            "Evidence: " +
            $"prNumber={prNumber}, " +
            $"filePath={path}, " +
            $"previousSha={previousSha}, " +
            $"latestSha={latestSha}, " +
            $"oldSize={oldSize}, newSize={newSize}, ratio={a.Ratio:F2}, absDelta={a.AbsDelta}.";
    }

    private static string ShortSha(string sha) =>
        string.IsNullOrEmpty(sha) ? "(unknown)" : (sha.Length <= 8 ? sha : sha[..8]);
}
