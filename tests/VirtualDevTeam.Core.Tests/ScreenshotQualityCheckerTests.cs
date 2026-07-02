using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Verifies the blank-screenshot detector flags the 2026-05-11 evidence correctly:
/// the live tower-defense run produced identical 4158-byte PNGs whenever the target
/// backend crashed on startup and the frontend rendered a blank canvas. The detector
/// must catch that pattern without false-positives on legitimate small UIs.
/// </summary>
public sealed class ScreenshotQualityCheckerTests
{
    [Fact]
    public void Check_FlagsBlank_WhenFileBelowThreshold()
    {
        // Synthesize a 4 KB PNG-shaped buffer (just header + filler).
        // Buffer layout: 8 magic + 4 len + 4 "IHDR" + 13 IHDR fields = 29 bytes header.
        var blank = MakePngBuffer(width: 1036, height: 853, payloadBytes: 4158 - 29);

        var result = ScreenshotQualityChecker.Check(blank);

        Assert.True(result.IsLikelyBlank);
        Assert.Equal(4158, result.FileSize);
        Assert.Contains("below blank-canvas threshold", result.Reason);
        Assert.Contains("1036×853", result.Reason);
    }

    [Fact]
    public void Check_PassesThrough_WhenFileLargeEnough()
    {
        // Build a PNG with enough IDAT data to pass both file-size and IDAT ratio checks
        var realistic = MakePngBufferWithIdat(width: 1920, height: 1080, idatPayloadBytes: 50_000);

        var result = ScreenshotQualityChecker.Check(realistic);

        Assert.False(result.IsLikelyBlank);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Check_FlagsNull()
    {
        var result = ScreenshotQualityChecker.Check(null);
        Assert.True(result.IsLikelyBlank);
        Assert.Equal(0, result.FileSize);
    }

    [Fact]
    public void Check_FlagsEmpty()
    {
        var result = ScreenshotQualityChecker.Check(Array.Empty<byte>());
        Assert.True(result.IsLikelyBlank);
    }

    [Fact]
    public void Check_SkipsNonPng()
    {
        // 5 KB of zeros — no PNG signature, so the detector doesn't claim authority.
        var notPng = new byte[5_000];
        var result = ScreenshotQualityChecker.Check(notPng);
        Assert.False(result.IsLikelyBlank);
        Assert.Contains("Not a PNG", result.Reason);
    }

    [Fact]
    public void Check_FlagsBlank_WhenIdatRatioTooLow()
    {
        // High-resolution white image: large file (>15KB) but tiny IDAT relative to pixels.
        // 1780×1080 = 1,922,400 pixels. With only 2000 IDAT bytes → ratio ≈ 0.001
        var whiteHighRes = MakePngBufferWithIdat(width: 1780, height: 1080, idatPayloadBytes: 2000);

        var result = ScreenshotQualityChecker.Check(whiteHighRes);

        Assert.True(result.IsLikelyBlank);
        Assert.Contains("IDAT ratio", result.Reason);
        Assert.Contains("bytes/pixel", result.Reason);
    }

    [Fact]
    public void Check_PassesThrough_WhenIdatRatioHealthy()
    {
        // Same resolution but enough IDAT content to look like real UI
        // 1780×1080 = 1,922,400 pixels. With 20000 IDAT bytes → ratio ≈ 0.01 (above 0.005)
        var realUi = MakePngBufferWithIdat(width: 1780, height: 1080, idatPayloadBytes: 20_000);

        var result = ScreenshotQualityChecker.Check(realUi);

        Assert.False(result.IsLikelyBlank);
    }

    [Fact]
    public void Check_SkipsIdatRatio_ForSmallImages()
    {
        // Small image (200×200 = 40K pixels < 500K threshold) — IDAT ratio check not applied.
        // Even with tiny IDAT, file size > 15KB means it passes.
        var smallWithTinyIdat = MakePngBufferWithIdat(width: 200, height: 200, idatPayloadBytes: 100, paddingBytes: 15_000);

        var result = ScreenshotQualityChecker.Check(smallWithTinyIdat);

        Assert.False(result.IsLikelyBlank);
    }

    /// <summary>
    /// PNG with header + IHDR (containing width/height) + arbitrary trailing payload to hit a
    /// target total size. Not a valid renderable PNG — just enough to exercise the detector.
    /// </summary>
    private static byte[] MakePngBuffer(int width, int height, int payloadBytes)
    {
        // 8-byte signature + 4 length + 4 "IHDR" + 4 width + 4 height + 5 (rest of IHDR fields) + payload
        var buf = new byte[8 + 4 + 4 + 4 + 4 + 5 + payloadBytes];
        // PNG magic
        buf[0] = 0x89; buf[1] = 0x50; buf[2] = 0x4E; buf[3] = 0x47;
        buf[4] = 0x0D; buf[5] = 0x0A; buf[6] = 0x1A; buf[7] = 0x0A;
        // IHDR chunk length (13)
        buf[8] = 0; buf[9] = 0; buf[10] = 0; buf[11] = 13;
        // "IHDR"
        buf[12] = (byte)'I'; buf[13] = (byte)'H'; buf[14] = (byte)'D'; buf[15] = (byte)'R';
        // Width (big-endian)
        buf[16] = (byte)(width >> 24); buf[17] = (byte)(width >> 16); buf[18] = (byte)(width >> 8); buf[19] = (byte)width;
        // Height (big-endian)
        buf[20] = (byte)(height >> 24); buf[21] = (byte)(height >> 16); buf[22] = (byte)(height >> 8); buf[23] = (byte)height;
        // bit depth, color type, compression, filter, interlace
        buf[24] = 8; buf[25] = 2; buf[26] = 0; buf[27] = 0; buf[28] = 0;
        return buf;
    }

    /// <summary>
    /// PNG with proper IHDR + IDAT chunk(s) + IEND. The IDAT data is synthetic (zeros)
    /// but the chunk structure is valid enough for <see cref="ScreenshotQualityChecker"/>
    /// to walk and sum IDAT bytes. File size is padded with tEXt chunks (not IDAT) so the
    /// IDAT ratio remains controlled by <paramref name="idatPayloadBytes"/> alone.
    /// </summary>
    private static byte[] MakePngBufferWithIdat(int width, int height, int idatPayloadBytes, int paddingBytes = 0)
    {
        // Layout: signature(8) + IHDR(25) + IDAT(12+data) + tEXt padding(12+pad) + IEND(12)
        var ihdrLen = 25;  // 4 length + 4 "IHDR" + 13 data + 4 CRC
        var idatLen = 12 + idatPayloadBytes;  // 4 length + 4 "IDAT" + data + 4 CRC
        var iendLen = 12;  // 4 length + 4 "IEND" + 0 data + 4 CRC
        var baseSize = 8 + ihdrLen + idatLen + iendLen;

        // Auto-pad to exceed MinNonBlankFileSizeBytes when the test expects file-size gate to pass
        var neededPad = paddingBytes;
        if (baseSize < 16_000 && idatPayloadBytes > 1000)
            neededPad = Math.Max(neededPad, 16_000 - baseSize);
        if (paddingBytes > 0 && baseSize + 12 + paddingBytes < 16_000)
            neededPad = Math.Max(neededPad, 16_000 - baseSize - 12);

        var paddingChunkLen = neededPad > 0 ? 12 + neededPad : 0;
        var totalSize = baseSize + paddingChunkLen;

        var buf = new byte[totalSize];
        var offset = 0;

        // PNG signature
        buf[0] = 0x89; buf[1] = 0x50; buf[2] = 0x4E; buf[3] = 0x47;
        buf[4] = 0x0D; buf[5] = 0x0A; buf[6] = 0x1A; buf[7] = 0x0A;
        offset = 8;

        // IHDR chunk: length=13, type="IHDR", width, height, depth=8, colorType=2, rest=0, CRC=0
        WriteBigEndian(buf, offset, 13); offset += 4;
        buf[offset] = (byte)'I'; buf[offset + 1] = (byte)'H'; buf[offset + 2] = (byte)'D'; buf[offset + 3] = (byte)'R'; offset += 4;
        WriteBigEndian(buf, offset, width); offset += 4;
        WriteBigEndian(buf, offset, height); offset += 4;
        buf[offset] = 8; buf[offset + 1] = 2; offset += 5; // depth, colorType, compression, filter, interlace
        offset += 4; // CRC (zeros — not validated by checker)

        // IDAT chunk (the actual compressed image data)
        WriteBigEndian(buf, offset, idatPayloadBytes); offset += 4;
        buf[offset] = (byte)'I'; buf[offset + 1] = (byte)'D'; buf[offset + 2] = (byte)'A'; buf[offset + 3] = (byte)'T'; offset += 4;
        offset += idatPayloadBytes; // data (zeros)
        offset += 4; // CRC

        // Padding as tEXt chunk (ancillary — NOT counted as IDAT by the checker)
        if (neededPad > 0)
        {
            WriteBigEndian(buf, offset, neededPad); offset += 4;
            buf[offset] = (byte)'t'; buf[offset + 1] = (byte)'E'; buf[offset + 2] = (byte)'X'; buf[offset + 3] = (byte)'t'; offset += 4;
            offset += neededPad;
            offset += 4; // CRC
        }

        // IEND chunk
        WriteBigEndian(buf, offset, 0); offset += 4;
        buf[offset] = (byte)'I'; buf[offset + 1] = (byte)'E'; buf[offset + 2] = (byte)'N'; buf[offset + 3] = (byte)'D';

        return buf;
    }

    private static void WriteBigEndian(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}
