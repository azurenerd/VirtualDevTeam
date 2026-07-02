using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Trims leading blank/loading frames from WebM videos using FFmpeg.
/// Uses pixel-based luminance detection (YAVG) to find when content first renders,
/// then applies frame-accurate trimming. Fully optional — gracefully degrades if FFmpeg
/// is not installed.
/// </summary>
public sealed class VideoTrimmer
{
    private readonly ILogger<VideoTrimmer> _logger;
    private readonly string? _ffmpegPath;

    public VideoTrimmer(ILogger<VideoTrimmer> logger)
    {
        _logger = logger;
        _ffmpegPath = FindFfmpeg();
    }

    /// <summary>True when FFmpeg is available and video trimming is functional.</summary>
    public bool IsAvailable => _ffmpegPath is not null;

    /// <summary>
    /// Trim leading blank/loading frames from a WebM video in-place.
    /// Returns the path to the trimmed video (same path if trimmed, original if skipped).
    /// </summary>
    public async Task<string> TrimVideoAsync(string webmPath, CancellationToken ct = default)
    {
        if (!IsAvailable || !File.Exists(webmPath))
            return webmPath;

        try
        {
            var contentStart = await DetectContentStartAsync(webmPath, ct);
            if (contentStart < 0.1)
            {
                _logger.LogDebug("No leading blank frames detected in {Path}", webmPath);
                return webmPath;
            }

            _logger.LogInformation("Trimming {Seconds:F1}s of leading blank frames from {Path}",
                contentStart, webmPath);

            var trimmedPath = Path.Combine(
                Path.GetDirectoryName(webmPath)!,
                Path.GetFileNameWithoutExtension(webmPath) + "-trimmed.webm");

            // Use trim filter (frame-accurate on decoded frames) instead of -ss
            // which is keyframe-based and may land before content start.
            var ok = await RunFfmpegAsync(
                $"-i \"{webmPath}\" -vf \"trim=start={contentStart:F2},setpts=PTS-STARTPTS\" -y \"{trimmedPath}\"",
                ct);

            if (ok && File.Exists(trimmedPath) && new FileInfo(trimmedPath).Length > 1000)
            {
                File.Delete(webmPath);
                File.Move(trimmedPath, webmPath);
                _logger.LogInformation("Video trimmed successfully: {Path}", webmPath);
                return webmPath;
            }

            // Trim failed or produced tiny file — keep original
            try { if (File.Exists(trimmedPath)) File.Delete(trimmedPath); } catch { }
            _logger.LogWarning("Video trim produced invalid output — keeping original");
            return webmPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video trimming failed for {Path} — keeping original", webmPath);
            return webmPath;
        }
    }

    /// <summary>
    /// Detect when content first renders by comparing per-frame average luminance (YAVG)
    /// against the first frame's baseline. When YAVG changes significantly, content has started.
    /// Samples at 4fps for precision. Works for any color scheme — dark, light, or colorful.
    /// </summary>
    internal async Task<double> DetectContentStartAsync(string webmPath, CancellationToken ct)
    {
        if (_ffmpegPath is null) return 0;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{webmPath}\" -vf \"fps=4,signalstats,metadata=print:key=lavfi.signalstats.YAVG\" -f null -",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        try
        {
            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exitCts.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        // Parse YAVG values — each corresponds to a frame at 4fps (0.25s intervals)
        var matches = Regex.Matches(stderr, @"lavfi\.signalstats\.YAVG=([0-9.]+)");
        if (matches.Count < 2) return 0;

        if (!double.TryParse(matches[0].Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var baseline))
            return 0;

        // Find first frame that differs significantly from baseline
        const double changeThreshold = 15.0;
        for (int i = 1; i < matches.Count; i++)
        {
            if (double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var yavg))
            {
                if (Math.Abs(yavg - baseline) > changeThreshold)
                {
                    // Content detected. Subtract 0.1s buffer so first content frame is included.
                    var contentTime = i * 0.25; // 4fps = 0.25s per frame
                    return Math.Max(0, contentTime - 0.1);
                }
            }
        }

        return 0; // No significant change — don't trim
    }

    private async Task<bool> RunFfmpegAsync(string args, CancellationToken ct)
    {
        if (_ffmpegPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exitCts.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return process.ExitCode == 0;
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\Tools\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        };

        foreach (var c in candidates)
            if (File.Exists(c))
                return c;

        // Search fresh PATH from Windows registry via centralized resolver
        var resolved = VirtualDevTeam.Core.AI.FreshPathResolver.ResolveExecutable("ffmpeg");
        if (resolved is not null)
            return resolved;

        // Try PATH probe
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                    return "ffmpeg";
            }
        }
        catch { }

        return null;
    }
}
