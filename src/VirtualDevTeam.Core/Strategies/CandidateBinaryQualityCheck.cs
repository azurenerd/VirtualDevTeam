using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Inspects a candidate's worktree for binary deliverables (PNGs, JPGs, MP4s) and
/// returns a quality grade so the <see cref="CandidateEvaluator"/> can demote
/// candidates that produced obvious fake/stub assets in favor of candidates that
/// produced real generative outputs.
///
/// <para>
/// The 2026-05-12 sprite-loss incident exposed this gap: a copilot-cli candidate
/// produced 4 Pillow-primitive stub PNGs (58-87 KB each — too small to be real
/// gpt-image-1 outputs which average 400 KB-1.6 MB), exited cleanly, was marked
/// sole-survivor and won the judge round. The squad sibling produced REAL 1.4MB
/// gpt-image content but exited NON-ZERO and was discarded. The judge couldn't
/// tell the difference because the patch text looked similar.
/// </para>
///
/// <para>
/// Heuristic scoring (0..100):
/// - Each PNG/JPG ≥ 100KB AND with a valid image signature counts as "real" (most
///   gpt-image outputs at 1024x1024 are 400 KB-2 MB; downscaled 256x256 outputs
///   are typically 50-100 KB but originate from 1024x1024 master frames so a
///   real candidate has BOTH sizes present).
/// - Each PNG/JPG &lt; 30KB or with no valid image signature counts as "fake" (Pillow
///   primitives at 256x256 are usually 1-15 KB; System.Drawing rectangles are
///   200 bytes-3 KB).
/// - Score = 100 * (real - fake) / max(1, total). Negative scores cap at 0.
/// - Candidates with no binary deliverables get null (no opinion) and don't
///   affect the ranking.
/// - PNG validation checks the 8-byte magic header; JPEG validation checks the
///   FF D8 FF SOI marker.
/// </para>
/// </summary>
public static class CandidateBinaryQualityCheck
{
    /// <summary>Threshold under which a PNG is considered a likely fake.
    /// Determined empirically: real gpt-image-1 outputs at 1024x1024 are 400 KB-2 MB;
    /// downscaled 256x256 PNGs from real masters average 50-100 KB. Pillow primitives
    /// and System.Drawing fakes are always &lt; 15 KB.</summary>
    public const long FakeSizeBytesUpperBound = 30 * 1024;

    /// <summary>Threshold above which a PNG is considered a likely real generation.
    /// Real master frames are 400 KB-2 MB; even small icons from the API exceed 100 KB
    /// when generated at 1024x1024. Downscaled outputs that are smaller don't get
    /// classified as "real" but they don't get classified as "fake" either — they're
    /// neutral and don't affect the score.</summary>
    public const long RealSizeBytesLowerBound = 100 * 1024;

    /// <summary>Image extensions we consider for the heuristic.</summary>
    private static readonly HashSet<string> _imageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    /// <summary>
    /// Scans the worktree under <paramref name="worktreePath"/> for image deliverables.
    /// Returns null when the candidate produced no images (no opinion to register);
    /// otherwise returns a 0-100 quality score plus the (real, fake, total) breakdown.
    /// </summary>
    public static BinaryQualityResult? Inspect(string worktreePath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
            return null;

        int real = 0, fake = 0, neutral = 0;
        long bytesScanned = 0;
        var details = new List<string>();
        try
        {
            // Bound enumeration cost: cap at 5000 entries (a candidate with more deliverables
            // than that is likely a build-output dump, not a deliverable manifest).
            var enumerated = 0;
            foreach (var path in Directory.EnumerateFiles(worktreePath, "*.*", SearchOption.AllDirectories))
            {
                if (++enumerated > 5000) break;
                if (!_imageExts.Contains(Path.GetExtension(path))) continue;
                // Skip framework scaffolding directories
                if (path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\.candidates\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) continue;

                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }
                if (!fi.Exists) continue;

                bytesScanned += fi.Length;
                var classification = Classify(fi);
                switch (classification)
                {
                    case Classification.Real: real++; break;
                    case Classification.Fake: fake++; break;
                    default: neutral++; break;
                }
                if (details.Count < 8)
                    details.Add($"{Path.GetFileName(path)}={fi.Length / 1024.0:F0}KB({classification})");
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "CandidateBinaryQualityCheck: enumeration failed for {Path}", worktreePath);
            return null;
        }

        var total = real + fake + neutral;
        if (total == 0) return null;

        // Score = 100 * (real - fake) / max(1, total). Negative scores cap at 0.
        var raw = 100.0 * (real - fake) / Math.Max(1, total);
        var score = (int)Math.Clamp(raw, 0, 100);

        return new BinaryQualityResult(score, real, fake, neutral, total, bytesScanned, details);
    }

    private enum Classification { Real, Fake, Neutral }

    private static Classification Classify(FileInfo fi)
    {
        // Quick reject for byte-tiny stubs (typically Pillow placeholder PNGs <30KB).
        // Tiny BUT valid PNGs (legit pixel-art icons) are kept as Neutral so they don't
        // tank a candidate that intentionally produces small assets.
        if (fi.Length < FakeSizeBytesUpperBound)
        {
            return HasValidImageSignature(fi.FullName) ? Classification.Neutral : Classification.Fake;
        }
        if (fi.Length >= RealSizeBytesLowerBound && HasValidImageSignature(fi.FullName))
            return Classification.Real;
        // Mid-range size with valid signature — neutral. Could be downscaled real output.
        return Classification.Neutral;
    }

    private static bool HasValidImageSignature(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => HasPngSignature(path),
            ".jpg" or ".jpeg" => HasJpgSignature(path),
            _ => false,
        };
    }

    private static bool HasJpgSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // JPEG starts with FF D8 FF (SOI marker + APP0/APP1/etc); minimum 3 bytes for SOI.
            // We also check for the trailer FF D9 by seeking to end-2, but for the heuristic
            // we only need the start marker — broken-trailer JPEGs still render visually fine
            // and are useful evidence of a real generation (PIL/Drawing emits truncated files
            // only on hard errors which already produce non-zero exit codes elsewhere).
            Span<byte> head = stackalloc byte[3];
            var read = fs.Read(head);
            return read == 3
                && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        }
        catch { return false; }
    }

    private static bool HasPngSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            // Full 8-byte PNG magic: 89 50 4E 47 0D 0A 1A 0A
            Span<byte> head = stackalloc byte[8];
            var read = fs.Read(head);
            return read == 8
                && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A;
        }
        catch { return false; }
    }
}

/// <summary>
/// Quality assessment of a candidate's binary deliverables. Lower scores indicate
/// likely-fake outputs (Pillow stubs, System.Drawing primitives); higher scores
/// indicate real generative content from gpt-image-* or similar APIs.
/// </summary>
public sealed record BinaryQualityResult(
    int Score,
    int RealCount,
    int FakeCount,
    int NeutralCount,
    int TotalCount,
    long TotalBytes,
    IReadOnlyList<string> SampleDetails)
{
    /// <summary>
    /// Returns a one-line operator-friendly summary suitable for logs / dashboard.
    /// </summary>
    public override string ToString() =>
        $"binary-quality={Score}/100 (real={RealCount} fake={FakeCount} neutral={NeutralCount} " +
        $"total={TotalCount} bytes={TotalBytes / 1024}KB; sample: {string.Join(", ", SampleDetails)})";
}
