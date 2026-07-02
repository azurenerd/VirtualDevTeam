using System.Buffers.Binary;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Detects screenshot capture failures: blank/uniform canvases produced when the page rendered
/// the wrong scene (Phaser scene-key error, blocked WebGL, white div, etc.), unresolved hydration,
/// or when the target app's backend returned 500 on its own config endpoints so the frontend
/// never bootstrapped. NoMessyCodePlan post-Tier-2 (2026-05-11 tower-defense monitoring run).
///
/// <para>
/// Background: during the live tower-defense run, every <c>menuscene-screenshot.png</c> was an
/// identical 4158 bytes — the canvas rendered as solid white because <c>GridGuardians.Api</c> had
/// crashed on a SQLite UNIQUE constraint and the frontend got 500s on every config endpoint.
/// Playwright still happily captured the blank canvas; agents kept merging PRs because none of
/// the pipeline stages noticed the runtime had failed.
/// </para>
///
/// <para>
/// **Detection strategy** (cheap, no external deps, no PNG library):
/// </para>
/// <list type="number">
///   <item>A PNG is considered <b>suspect</b> if the raw file size is below
///         <see cref="MinNonBlankFileSizeBytes"/>. PNG of a uniform-color canvas compresses
///         aggressively (4 KB for 1036x853 white). A meaningful UI produces ≥30-50 KB even
///         on minimal content.</item>
///   <item>For PNGs above the size threshold but suspiciously low, we sample the IDAT-chunk
///         stream length encoded in the file header. Truly uniform images have IDAT streams
///         orders of magnitude smaller than dimensions would predict.</item>
/// </list>
///
/// <para>
/// We intentionally don't decompress the image pixels — that would pull in System.Drawing /
/// ImageSharp and slow capture by 100ms+. The file-size heuristic is robust enough to flag the
/// "100% solid color canvas" class without false-positives on legitimate sparse UIs.
/// </para>
/// </summary>
public static class ScreenshotQualityChecker
{
    /// <summary>
    /// Empirically-derived floor below which a PNG is almost certainly a uniform / mostly-empty
    /// canvas. Tuned against the 2026-05-11 evidence: blank Phaser canvases at 1036×853 came in at
    /// ~4 KB, while even minimal real-UI captures (sparse menu, single text) clear 25 KB.
    /// </summary>
    public const int MinNonBlankFileSizeBytes = 15_000;

    /// <summary>
    /// Minimum IDAT-bytes-per-pixel ratio for a PNG to be considered non-blank.
    /// Uniform-fill PNGs compress to &lt;0.002 bytes/pixel regardless of resolution;
    /// even minimal real UIs produce &gt;0.01. Threshold of 0.005 gives comfortable margin.
    /// Only applied when the image exceeds <see cref="MinIdatRatioPixelThreshold"/> pixels
    /// to avoid false positives on legitimately tiny thumbnails.
    /// </summary>
    public const double MinIdatBytesPerPixel = 0.005;

    /// <summary>
    /// Pixel count above which the IDAT ratio check kicks in (500K pixels ≈ 700×700).
    /// Below this, the file-size heuristic alone is sufficient.
    /// </summary>
    public const long MinIdatRatioPixelThreshold = 500_000;

    /// <summary>
    /// Inspect a PNG byte buffer and return a verdict on whether it looks blank / failed.
    /// </summary>
    public static ScreenshotQuality Check(byte[]? png)
    {
        if (png is null || png.Length == 0)
        {
            return new ScreenshotQuality(IsLikelyBlank: true, FileSize: 0,
                Reason: "PNG byte buffer is null or empty");
        }

        if (!LooksLikePng(png))
        {
            // Not a PNG — caller's responsibility. Don't flag as blank; they may have written
            // JPEG or some other format. We only adjudicate PNG.
            return new ScreenshotQuality(IsLikelyBlank: false, FileSize: png.Length,
                Reason: "Not a PNG signature — quality check skipped");
        }

        var (width, height) = TryReadDimensions(png);

        if (png.Length < MinNonBlankFileSizeBytes)
        {
            return new ScreenshotQuality(IsLikelyBlank: true, FileSize: png.Length,
                Reason: $"PNG size {png.Length} B is below blank-canvas threshold " +
                        $"({MinNonBlankFileSizeBytes} B) for {width}×{height} — likely uniform fill " +
                        "(blank canvas, failed render, target backend 5xx). See ScreenshotQualityChecker docs.");
        }

        // Second gate: IDAT ratio check for high-resolution images that pass the file-size
        // threshold. A pure-white 1780×1080 PNG produces ~16-20 KB (above the 15 KB floor)
        // but its IDAT compressed data is extremely small relative to pixel count.
        long totalPixels = (long)width * height;
        if (totalPixels >= MinIdatRatioPixelThreshold)
        {
            var totalIdatBytes = SumIdatChunkBytes(png);
            if (totalIdatBytes > 0)
            {
                var ratio = (double)totalIdatBytes / totalPixels;
                if (ratio < MinIdatBytesPerPixel)
                {
                    return new ScreenshotQuality(IsLikelyBlank: true, FileSize: png.Length,
                        Reason: $"PNG IDAT ratio {ratio:F4} bytes/pixel ({totalIdatBytes} IDAT bytes " +
                                $"for {width}×{height} = {totalPixels} pixels) is below threshold " +
                                $"{MinIdatBytesPerPixel} — likely uniform fill (blank canvas, white page).");
                }
            }
        }

        return new ScreenshotQuality(IsLikelyBlank: false, FileSize: png.Length, Reason: null);
    }

    private static bool LooksLikePng(byte[] bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static (int Width, int Height) TryReadDimensions(byte[] bytes)
    {
        // PNG signature 8 bytes, then IHDR chunk: 4-byte length + 4-byte "IHDR" + 4-byte width + 4-byte height
        try
        {
            if (bytes.Length < 24) return (0, 0);
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            return (width, height);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Walk the PNG chunk stream and sum all IDAT chunk data lengths.
    /// Returns 0 if no IDAT chunks are found (e.g., truncated or synthetic test buffers).
    /// </summary>
    private static long SumIdatChunkBytes(byte[] png)
    {
        // PNG structure: 8-byte signature, then repeating chunks:
        //   4-byte data length (big-endian) + 4-byte type + data + 4-byte CRC
        long total = 0;
        int offset = 8; // skip signature
        try
        {
            while (offset + 8 <= png.Length)
            {
                var dataLen = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                if (dataLen < 0) break; // corrupt
                var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                if (type == "IDAT")
                    total += dataLen;
                else if (type == "IEND")
                    break;
                // Advance: 4 (length) + 4 (type) + dataLen + 4 (CRC)
                offset += 12 + dataLen;
            }
        }
        catch
        {
            // Corrupt PNG — return what we have
        }
        return total;
    }
}

/// <summary>Outcome of a screenshot quality check.</summary>
public sealed record ScreenshotQuality(bool IsLikelyBlank, int FileSize, string? Reason);
