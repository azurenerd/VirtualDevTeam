using System.Diagnostics;

namespace AgentSquad.Dashboard.Tests.Helpers;

/// <summary>
/// Converts WebM video files to optimized animated GIFs using FFmpeg two-pass pipeline.
/// Pass 1: Generate optimal color palette from video frames.
/// Pass 2: Encode GIF using the palette for high quality at small file size.
/// </summary>
public static class GifConverter
{
    private static readonly string FfmpegPath = FindFfmpeg();

    /// <summary>
    /// Convert a WebM video to an animated GIF.
    /// </summary>
    /// <param name="webmPath">Path to the source .webm file.</param>
    /// <param name="gifPath">Path for the output .gif file.</param>
    /// <param name="fps">Frames per second (lower = smaller file). Default 3.</param>
    /// <param name="maxWidth">Max width in pixels. Default 960.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if conversion succeeded.</returns>
    public static async Task<bool> ConvertAsync(
        string webmPath,
        string gifPath,
        int fps = 3,
        int maxWidth = 960,
        CancellationToken ct = default)
    {
        if (!File.Exists(webmPath))
            return false;

        var palettePath = Path.Combine(
            Path.GetDirectoryName(gifPath)!,
            $"palette-{Guid.NewGuid():N}.png");

        try
        {
            var filters = $"fps={fps},scale={maxWidth}:-1:flags=lanczos";

            // Pass 1: Generate palette
            var pass1 = await RunFfmpegAsync(
                $"-i \"{webmPath}\" -vf \"{filters},palettegen=stats_mode=diff\" -y \"{palettePath}\"",
                ct);
            if (!pass1)
                return false;

            // Pass 2: Encode GIF with palette
            var pass2 = await RunFfmpegAsync(
                $"-i \"{webmPath}\" -i \"{palettePath}\" -filter_complex \"{filters}[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5\" -y \"{gifPath}\"",
                ct);
            return pass2;
        }
        finally
        {
            try { File.Delete(palettePath); } catch { }
        }
    }

    public static bool IsAvailable => File.Exists(FfmpegPath);

    private static async Task<bool> RunFfmpegAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stderr (FFmpeg writes progress there)
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await stderrTask; // just drain it
        return process.ExitCode == 0;
    }

    private static string FindFfmpeg()
    {
        // Check common locations
        var candidates = new[]
        {
            @"C:\Tools\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };

        foreach (var c in candidates)
            if (File.Exists(c))
                return c;

        // Fall back to PATH
        return "ffmpeg";
    }
}
