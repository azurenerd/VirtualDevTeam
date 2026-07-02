using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor detector — image-regen-anomaly.
///
/// <para>
/// Catches a specific class of silent failure observed when image-generation agents (PM,
/// Artist SME, Engineers) are asked to "rework" a generated PNG following operator feedback
/// but the model returns a result that is visually identical to the previous version. The
/// rework was a no-op — the operator's feedback did not actually change anything.
/// </para>
///
/// <para>
/// This is the IMAGE-REGEN sibling of the planned doc rework-size-anomaly detector. Where the
/// doc variant inspects a textual size delta on each rework cycle, this one inspects a
/// PERCEPTUAL hash of the image's pixel content. Identical pHashes between the latest commit
/// touching a PNG and the previous one → the regenerated image looks the same as the previous
/// one at thumbnail resolution. The operator can then decide whether to escalate, abandon, or
/// retry with stronger guidance.
/// </para>
///
/// <para>
/// <b>Algorithm</b>:
/// <list type="number">
///   <item>Enumerate open PRs via <see cref="IPlatformView.ListOpenPullRequestsAsync"/>.</item>
///   <item>Cap the per-tick scan at <see cref="MaxPrsPerTick"/> PRs (most-recently-updated first)
///         so cost is bounded on long-running runs.</item>
///   <item>For each candidate PR, fetch its commit history. A rework requires ≥2 commits — if
///         there's only one, the PR hasn't been reworked yet and there's nothing to compare.</item>
///   <item>Fetch the PR's overall changed-files list. Filter to <c>.png</c> files in art-ish
///         paths (or anywhere when the PR title indicates an Artist task).</item>
///   <item>For each candidate PNG, download bytes at the latest commit SHA and at the
///         second-to-latest commit SHA, skipping anything below <see cref="MinSizeBytes"/>
///         (likely stubs or error placeholders).</item>
///   <item>Compute a 64-bit perceptual hash of each (see <see cref="ComputePerceptualHash"/>).
///         Hamming distance == 0 → identical pHash → emit Warning finding.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Severity</b>: Warning, not Critical. A no-op image regen is advisory information for the
/// operator — they may have asked for a subtle change the model missed (real anomaly) or the
/// previous output was already correct and the rework cycle was unnecessary (false alarm). We
/// leave the escalation decision to the operator. Action-handler wiring is intentionally
/// deferred to a separate todo (<c>imggen-action-handlers</c>) so this detector ships first.
/// </para>
///
/// <para>
/// <b>Dedup</b>: stable per <c>(prNumber, filePath)</c>. The FlowMonitor's window-based dedup
/// ensures a single open-but-unresolved finding doesn't refire on every tick. A subsequent
/// rework commit changes <see cref="latest.Sha"/> but the dedup key intentionally does NOT
/// include the SHA — if the same path is reworked AGAIN and AGAIN produces identical pHash,
/// the operator only sees one finding per PR+path until they act.
/// </para>
///
/// <para>
/// <b>Platform note</b>: the detector uses <see cref="IRepositoryContentService.GetFileBytesAsync"/>
/// passing a commit SHA where the API expects a "branch" — this works on the GitHub adapter
/// (Octokit's <c>GetAllContentsByRef</c> accepts any git ref including SHAs). The ADO adapter
/// hardcodes <c>versionType=branch</c> and will silently fail this lookup; on ADO the detector
/// degrades to skipping the comparison rather than raising false findings. A future
/// improvement (out of scope for the MVP) is to extend the abstraction with an explicit
/// <c>GetFileBytesAtCommitAsync</c>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImageRegenAnomalyDetector : IFlowDetector
{
    public string DetectorId => "image-regen-anomaly";

    /// <summary>Minimum PNG size (bytes) before we bother decoding + hashing. Tiny PNGs are
    /// almost always stubs or error placeholders — comparing them yields noise.</summary>
    internal const long MinSizeBytes = 5 * 1024;

    /// <summary>Cap on PRs scanned per tick. Bounded API + pHash cost — at most this many
    /// commit-list fetches and 2× file-bytes fetches per tick.</summary>
    internal const int MaxPrsPerTick = 5;

    /// <summary>Path prefixes that mark a file as "art / asset content" eligible for scanning
    /// regardless of PR title. The detector also accepts any PNG when the PR title looks like
    /// an Artist task (see <see cref="LooksLikeArtPr"/>).</summary>
    private static readonly string[] ArtPathPrefixes =
    {
        "assets/",
        "art/",
        "images/",
        "sprites/",
        ".screenshots/",
    };

    /// <summary>Substring marker for PM/Artist reference-image content nested under AgentDocs.
    /// Matched as a path-segment to avoid catching unrelated AgentDocs files.</summary>
    private const string AgentDocsReferenceImagesMarker = "/reference-images/";

    private readonly ILogger<ImageRegenAnomalyDetector> _logger;
    private readonly IPullRequestService? _prService;
    private readonly IRepositoryContentService? _contentService;

    public ImageRegenAnomalyDetector(
        ILogger<ImageRegenAnomalyDetector> logger,
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
                .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                .Take(MaxPrsPerTick)
                .ToList();

            foreach (var pr in prsToScan)
            {
                if (ct.IsCancellationRequested) break;
                await ScanPullRequestAsync(pr, findings, ctx.Now, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageRegenAnomalyDetector tick failed (non-fatal)");
        }

        return findings;
    }

    private async Task ScanPullRequestAsync(
        PullRequestView pr, List<FlowFinding> findings, DateTimeOffset now, CancellationToken ct)
    {
        // Rework requires ≥2 commits. One commit = original; nothing has been reworked yet.
        IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformCommitInfo> commits;
        try
        {
            commits = await _prService!.GetCommitsWithDatesAsync(pr.Number, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageRegenAnomalyDetector: GetCommitsWithDatesAsync failed for PR #{Pr} (skipping)", pr.Number);
            return;
        }
        if (commits is null || commits.Count < 2) return;

        // Order ascending by commit time; we use the last two.
        var sorted = commits.OrderBy(c => c.CommittedAt).ToList();
        var latest = sorted[^1];
        var previous = sorted[^2];
        if (string.IsNullOrEmpty(latest.Sha) || string.IsNullOrEmpty(previous.Sha)) return;
        if (string.Equals(latest.Sha, previous.Sha, StringComparison.OrdinalIgnoreCase)) return;

        // Fetch PR-wide diff to find changed PNG paths. Per-commit diff isn't exposed by the
        // current capability surface, but the PR-wide changed-files set is a superset of "files
        // touched in any commit" — good enough for this MVP heuristic.
        IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformFileDiff> diffs;
        try
        {
            diffs = await _prService.GetFileDiffsAsync(pr.Number, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageRegenAnomalyDetector: GetFileDiffsAsync failed for PR #{Pr} (skipping)", pr.Number);
            return;
        }
        if (diffs is null || diffs.Count == 0) return;

        var prIsArtTitle = LooksLikeArtPr(pr.Title);
        var candidatePaths = diffs
            .Where(d => !string.IsNullOrEmpty(d.FileName))
            .Where(d => d.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Where(d => prIsArtTitle || IsArtPath(d.FileName))
            .Select(d => d.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidatePaths.Count == 0) return;

        foreach (var path in candidatePaths)
        {
            if (ct.IsCancellationRequested) break;

            byte[]? latestBytes;
            byte[]? previousBytes;
            try
            {
                latestBytes = await _contentService!.GetFileBytesAsync(path, latest.Sha, ct).ConfigureAwait(false);
                if (latestBytes is null || latestBytes.LongLength < MinSizeBytes) continue;

                previousBytes = await _contentService.GetFileBytesAsync(path, previous.Sha, ct).ConfigureAwait(false);
                if (previousBytes is null || previousBytes.LongLength < MinSizeBytes) continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ImageRegenAnomalyDetector: failed to fetch bytes for {Path} on PR #{Pr} (skipping)", path, pr.Number);
                continue;
            }

            var latestHash = ComputePerceptualHash(latestBytes);
            var previousHash = ComputePerceptualHash(previousBytes);
            if (latestHash is null || previousHash is null) continue;

            if (!latestHash.Value.Equals(previousHash.Value)) continue;

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = now,
                DetectorId = DetectorId,
                Severity = FlowFindingSeverity.Warning,
                TargetResource = $"pr#{pr.Number}",
                TargetDisplayName = pr.AssignedAgent,
                Summary = $"Image regen produced no change: PR #{pr.Number} {path}",
                Rationale =
                    $"PR #{pr.Number}: {path} was regenerated in commit {ShortSha(latest.Sha)} but its " +
                    $"perceptual hash matches the previous version ({ShortSha(previous.Sha)}). The rework " +
                    "was a no-op — the operator's feedback was not visibly applied to the image. " +
                    "Evidence: " +
                    $"prNumber={pr.Number}, " +
                    $"filePath={path}, " +
                    $"previousSha={previous.Sha}, " +
                    $"latestSha={latest.Sha}, " +
                    $"pHash=0x{latestHash.Value.StructureBits:X16} mean(R,G,B)=({latestHash.Value.MeanR},{latestHash.Value.MeanG},{latestHash.Value.MeanB}).",
                DedupKey = $"image-regen-anomaly:{pr.Number}:{path}",
            });
        }
    }

    /// <summary>
    /// PR title heuristic — PR is for an Artist SME or otherwise visual asset work.
    /// </summary>
    internal static bool LooksLikeArtPr(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return title.Contains("Artist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the file path looks like art/asset content the detector should
    /// scan. Accepts the well-known art-folder prefixes and any path that contains a
    /// <c>/reference-images/</c> segment (PM-produced style anchors under AgentDocs).
    /// </summary>
    internal static bool IsArtPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace('\\', '/').TrimStart('/');
        foreach (var prefix in ArtPathPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (normalized.Contains(AgentDocsReferenceImagesMarker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ShortSha(string sha) =>
        string.IsNullOrEmpty(sha) ? "(unknown)" : (sha.Length <= 8 ? sha : sha[..8]);

    // ----------------------------------------------------------------------------------
    // Perceptual hash
    // ----------------------------------------------------------------------------------
    //
    // The hash is a (StructureBits, MeanR, MeanG, MeanB) record struct. Two images are
    // considered visually equivalent iff ALL four components match.
    //
    //   1. Decode bytes → Bitmap (using System.Drawing.Common — Windows-supported, no
    //      exotic image-processing library required).
    //   2. Resize to 8 × 8 using high-quality bicubic interpolation. This downsamples
    //      out compression noise, JPEG-ringing, and metadata-only re-encodings while
    //      preserving the dominant visual structure.
    //   3. Convert each of the 64 pixels to grayscale luminance using Rec.601 weights
    //      (Y = 0.299·R + 0.587·G + 0.114·B). This is the same formula NTSC TVs used,
    //      and it's a stable, deterministic mapping. Also accumulate per-channel sums
    //      for the mean-color signature.
    //   4. Compute the mean luminance of the 64 pixels and per-channel mean R/G/B.
    //   5. Build a 64-bit StructureBits hash: bit i (row-major, top-left = MSB) is 1 iff
    //      pixel i's luminance is ≥ mean. This makes the structural component robust to
    //      global brightness shifts — only the relative pattern matters.
    //   6. Quantize mean R/G/B to bytes — gives a coarse 24-bit color signature.
    //   7. The full PerceptualHash is (StructureBits, MeanR, MeanG, MeanB). Record-struct
    //      equality requires all four to match.
    //
    // Why color AS WELL as structure: pure grayscale-threshold pHash is invariant to
    // global color shifts — a solid-red 32×32 and a solid-blue 32×32 produce identical
    // structural patterns. For an image-regen detector we want "visually identical"
    // semantics, so color recolors are NOT acceptable as no-op reworks. Adding the
    // 24-bit mean-color signature catches that case while staying robust to the
    // compression / re-encoding noise that motivates pHash in the first place
    // (re-encoded PNGs of the same content still produce identical mean R/G/B and
    // identical 8×8 luminance threshold).
    //
    // Returns null if the bytes don't decode as a valid image. Exceptions are
    // intentionally swallowed — detectors must never throw.
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// 64-bit luminance-threshold pattern + 24-bit mean-color signature. Two images are
    /// considered visually equivalent iff all four components match. Record-struct
    /// equality is auto-generated and is what the detector compares against.
    /// </summary>
    internal readonly record struct PerceptualHash(ulong StructureBits, byte MeanR, byte MeanG, byte MeanB);

    internal static PerceptualHash? ComputePerceptualHash(byte[] imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            using var src = new Bitmap(ms);
            using var dst = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, 8, 8));
            }

            var lumas = new double[64];
            double sumLuma = 0;
            int sumR = 0, sumG = 0, sumB = 0;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var c = dst.GetPixel(x, y);
                    var luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
                    lumas[(y * 8) + x] = luma;
                    sumLuma += luma;
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                }
            }
            var meanLuma = sumLuma / 64.0;

            ulong hash = 0;
            for (var i = 0; i < 64; i++)
            {
                if (lumas[i] >= meanLuma)
                {
                    hash |= 1UL << (63 - i);
                }
            }
            return new PerceptualHash(
                hash,
                (byte)(sumR / 64),
                (byte)(sumG / 64),
                (byte)(sumB / 64));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
