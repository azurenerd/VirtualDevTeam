using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Converts WebM video files to optimized animated GIFs using FFmpeg two-pass pipeline.
/// Uses pixel-based detection to find the first non-white frame (when content actually renders)
/// and trims leading blank/loading frames automatically.
/// </summary>
public static class GifConverter
{
    private static readonly string FfmpegPath = FindFfmpeg();

    /// <summary>
    /// Convert a WebM video to an animated GIF, auto-trimming leading white frames
    /// by detecting when actual content first renders (pixel-based, not time-based).
    /// </summary>
    public static async Task<bool> ConvertAsync(
        string webmPath,
        string gifPath,
        int fps = 4,
        int maxWidth = 1280,
        double trimStartSeconds = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(webmPath))
            return false;

        // Detect when content first appears using pixel analysis
        var contentStart = await DetectContentStartAsync(webmPath, ct);

        var palettePath = Path.Combine(
            Path.GetDirectoryName(gifPath)!,
            $"palette-{Guid.NewGuid():N}.png");

        try
        {
            // Use trim filter (frame-accurate) instead of -ss (keyframe-based),
            // because WebM files typically have only one keyframe at frame 0.
            var trimFilter = contentStart > 0.1
                ? $"trim=start={contentStart:F2},setpts=PTS-STARTPTS,"
                : "";
            var filters = $"{trimFilter}fps={fps},scale={maxWidth}:-1:flags=lanczos";

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

    /// <summary>Whether FFmpeg is available on this machine.</summary>
    public static bool IsAvailable => !string.IsNullOrEmpty(FfmpegPath) && (FfmpegPath == "ffmpeg" || File.Exists(FfmpegPath)) && ProbeAvailability();

    /// <summary>
    /// Detect when content first renders by comparing per-frame average luminance (YAVG)
    /// against the first frame's baseline. When YAVG changes significantly from the initial
    /// value, content has started rendering. Works for any color scheme — dark, light, or colorful.
    /// Samples at 4fps for precision, returns the timestamp of the first changed frame.
    /// </summary>
    private static async Task<double> DetectContentStartAsync(string webmPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = $"-i \"{webmPath}\" -vf \"fps=4,signalstats,metadata=print:key=lavfi.signalstats.YAVG\" -f null -",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); } catch { }

        var matches = Regex.Matches(stderr, @"lavfi\.signalstats\.YAVG=([0-9.]+)");
        if (matches.Count < 2) return 0;

        if (!double.TryParse(matches[0].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseline))
            return 0;

        const double changeThreshold = 15.0;
        for (int i = 1; i < matches.Count; i++)
        {
            if (double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var yavg))
            {
                if (Math.Abs(yavg - baseline) > changeThreshold)
                {
                    var contentTime = i * 0.25; // 4fps = 0.25s per frame
                    return Math.Max(0, contentTime - 0.1);
                }
            }
        }

        return 0; // No change detected — don't trim
    }

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

        await stderrTask;
        return process.ExitCode == 0;
    }

    private static bool ProbeAvailability()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\Tools\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };

        foreach (var c in candidates)
            if (File.Exists(c))
                return c;

        // Search fresh PATH from Windows registry via centralized resolver
        var resolved = VirtualDevTeam.Core.AI.FreshPathResolver.ResolveExecutable("ffmpeg");
        if (resolved is not null)
            return resolved;

        // Try PATH — will be validated by ProbeAvailability()
        return "ffmpeg";
    }
}
