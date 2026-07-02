using System.Diagnostics;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// <see cref="ICandidatePreviewProducer"/> that builds an "image asset" preview from
/// raster files committed in a candidate's worktree. Highest priority in the chain
/// (<see cref="Priority"/> = 10) so it wins over the Playwright capture (Priority=100)
/// and the diagram producer (Priority=20) when the PR's deliverable is visual content
/// (sprites, art, generated screenshots, reference images) rather than a runnable app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Library choice — Magick.NET-Q8-AnyCPU.</b> Matches the rest of the preview-producer
/// feature (the sibling <see cref="DiagramCandidatePreviewProducer"/> and the shared
/// <see cref="ContactSheetBuilder"/>). The spec invited an alternative (SkiaSharp / pure
/// managed PNG / System.Drawing.Common) but a single image library across the feature
/// is preferable to dragging in a second one. Magick.NET 14.x ships ~30 known CVEs in
/// its bundled native ImageMagick binaries which surface as NU190x warnings on restore —
/// that's accepted as a known cost in this codebase; the alternatives would either ship
/// the same warnings (SkiaSharp transitively pulls a different native lib but doesn't
/// help when the rest of the feature already requires Magick.NET) or sacrifice format
/// coverage (System.Drawing.Common can't decode WEBP).
/// </para>
/// <para>
/// <b>Discovery.</b> The producer walks <see cref="CandidatePreviewContext.CandidateWorktreePath"/>
/// looking for PNG/JPG/JPEG/WEBP/GIF files under any of these conventional roots
/// (recursive within each):
/// <list type="bullet">
///   <item><c>assets/</c></item>
///   <item><c>art/</c></item>
///   <item><c>images/</c></item>
///   <item><c>sprites/</c></item>
///   <item><c>.screenshots/</c></item>
///   <item><c>AgentDocs/&lt;*&gt;/reference-images/</c> (one level under each AgentDocs subfolder)</item>
/// </list>
/// Files smaller than <c>5 KB</c> are skipped (likely placeholder stubs or error
/// thumbnails). Discovery is capped at <c>16</c> items; when truncated, the contact
/// sheet gets a "+N more" overlay in the bottom-right corner of the composed sheet.
/// </para>
/// <para>
/// <b>Output.</b> Writes one file:
/// <c>{ArtifactOutputDir}/framework-{taskId}-{strategyId}-assets.png</c>. For a single
/// image, the source is decoded + re-encoded as PNG (no resizing). For multiple images,
/// a square <c>NxN</c> contact sheet is built where <c>N = ceil(sqrt(count))</c>, each
/// cell is up to 256×256, source images are letterboxed centered on a transparent
/// background to preserve aspect ratio. The same bytes are returned base64-encoded in
/// <see cref="CandidatePreview.ScreenshotBase64"/> for direct dashboard rendering.
/// </para>
/// </remarks>
public sealed class ImageAssetCandidatePreviewProducer : ICandidatePreviewProducer
{
    private readonly ILogger<ImageAssetCandidatePreviewProducer> _logger;

    /// <summary>
    /// Minimum on-disk size for an image to be considered (anything smaller is treated as a
    /// zero-byte stub or git lfs pointer). Real placeholder check is done by
    /// <see cref="CandidateBinaryQualityCheck.HasValidImageSignature"/> in the evaluator;
    /// here we only reject truly empty files.
    /// </summary>
    internal const long MinFileSizeBytes = 1 * 1024;

    /// <summary>Maximum number of images included in a contact sheet; remainder is shown as a "+N more" overlay.</summary>
    internal const int MaxImages = 16;

    /// <summary>Size (square) of each cell in the contact sheet, in pixels.</summary>
    internal const int CellSize = 256;

    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
    };

    /// <summary>
    /// Directories excluded from the whole-worktree scan — build artefacts, tool caches,
    /// framework internals, and fixture/test-data/sample directories that hold input
    /// artifacts rather than candidate deliverables. See <see cref="PreviewDiscoveryFilters"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> _excludedDirNames =
        PreviewDiscoveryFilters.ExcludedDirectoryNames;

    public ImageAssetCandidatePreviewProducer(ILogger<ImageAssetCandidatePreviewProducer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public string Id => "image-assets";

    /// <inheritdoc />
    public async Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.CandidateWorktreePath) ||
            !Directory.Exists(context.CandidateWorktreePath))
        {
            _logger.LogDebug(
                "Candidate worktree path missing or empty for task {TaskId}/{Strategy} — skipping image-asset producer.",
                context.TaskId, context.StrategyId);
            return null;
        }

        List<string> discovered;
        try
        {
            discovered = await Task.Run(
                () => DiscoverImages(context.CandidateWorktreePath, ct).ToList(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Image discovery failed in worktree {Path} for task {TaskId}/{Strategy}.",
                context.CandidateWorktreePath, context.TaskId, context.StrategyId);
            return null;
        }

        if (discovered.Count == 0)
        {
            _logger.LogDebug(
                "No qualifying images (>= {Min} bytes) in worktree {Path} for task {TaskId}/{Strategy}.",
                MinFileSizeBytes, context.CandidateWorktreePath, context.TaskId, context.StrategyId);
            return null;
        }

        var totalCount = discovered.Count;
        var capped = totalCount > MaxImages;
        var selected = capped ? discovered.Take(MaxImages).ToList() : discovered;
        var extraCount = capped ? totalCount - MaxImages : 0;

        Directory.CreateDirectory(context.ArtifactOutputDir);
        var outputPath = Path.Combine(
            context.ArtifactOutputDir,
            $"framework-{context.TaskId}-{context.StrategyId}-assets.png");

        byte[] pngBytes;
        try
        {
            pngBytes = selected.Count == 1
                ? BuildSingleImage(selected[0])
                : BuildContactSheet(selected, extraCount);

            await File.WriteAllBytesAsync(outputPath, pngBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to build image-asset preview for task {TaskId}/{Strategy}.",
                context.TaskId, context.StrategyId);
            return null;
        }

        _logger.LogInformation(
            "Image-asset preview produced for task {TaskId}/{Strategy}: {Count} image(s) ({Truncated}), output {Output}.",
            context.TaskId, context.StrategyId, selected.Count,
            capped ? $"truncated from {totalCount}" : "all included",
            outputPath);

        return new CandidatePreview
        {
            SourceProducerId = Id,
            ScreenshotBase64 = Convert.ToBase64String(pngBytes),
            Source = CandidatePreviewSource.ImageAssets,
            IncludedAssetPaths = selected,
        };
    }

    // ── discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the entire candidate worktree, skipping build-artefact and tool-cache
    /// directories, and collects every image file that has a recognised extension and is
    /// at least <see cref="MinFileSizeBytes"/> on disk. Results are returned
    /// largest-first so the most significant assets appear at the front of the contact
    /// sheet; a stable secondary sort by path preserves determinism when sizes match.
    ///
    /// <para>
    /// 2026-05-13 fix (strategy-image-producer-counts-stale-committed-files): the prior
    /// implementation indiscriminately returned every image in the worktree, including
    /// images committed by upstream merged PRs that pre-existed the candidate's work.
    /// Now we attempt a best-effort git filter: query the candidate worktree for files
    /// changed in the candidate's commits via `git diff --name-only HEAD~1..HEAD` (the
    /// canonical case where the candidate applies a single patch on top of the baseline)
    /// and restrict discovery to that set. If the git query fails for any reason, fall
    /// back to scanning everything (matches the prior behaviour as a safety net).
    /// </para>
    /// </summary>
    private static IEnumerable<string> DiscoverImages(string worktree, CancellationToken ct)
    {
        var results = new List<(string path, long size)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Best-effort: get the set of files the CANDIDATE'S COMMITS changed. This
        // excludes pre-existing committed assets (e.g. art shipped by earlier merged PRs)
        // that would otherwise be mis-attributed to the candidate.
        var changedFiles = GetCandidateChangedFiles(worktree, ct);

        void Walk(string dir)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_imageExtensions.Contains(Path.GetExtension(file))) continue;
                    if (!seen.Add(file)) continue;

                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists || info.Length < MinFileSizeBytes) continue;
                        // Skip files NOT changed by the candidate's commits (when filter available).
                        if (changedFiles is not null
                            && changedFiles.Count > 0
                            && !changedFiles.Contains(Path.GetFullPath(file)))
                            continue;
                        results.Add((file, info.Length));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (_excludedDirNames.Contains(Path.GetFileName(sub))) continue;
                    Walk(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        Walk(worktree);

        // Largest first so the highest-quality assets lead the contact sheet.
        return results
            .OrderByDescending(x => x.size)
            .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.path);
    }

    /// <summary>
    /// Best-effort: return the set of files (full paths) changed by the candidate's
    /// most recent commit. Uses `git diff --name-only HEAD~1..HEAD` which matches the
    /// canonical strategy-framework pattern where the candidate applies ONE patch on
    /// top of the baseline. If the git query fails (no .git dir, no parent commit,
    /// timeout, etc.), returns null so the caller falls back to the unfiltered scan.
    /// </summary>
    private static HashSet<string>? GetCandidateChangedFiles(string worktree, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(worktree, ".git")) &&
                !File.Exists(Path.Combine(worktree, ".git"))) // .git can be a worktree file
                return null;

            var psi = new ProcessStartInfo("git", "diff --name-only HEAD~1..HEAD")
            {
                WorkingDirectory = worktree,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* best-effort */ }
                return null;
            }
            if (p.ExitCode != 0) return null;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var rel = line.Trim();
                if (string.IsNullOrEmpty(rel)) continue;
                try
                {
                    var full = Path.GetFullPath(Path.Combine(worktree, rel));
                    set.Add(full);
                }
                catch { /* skip malformed path */ }
            }
            return set;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    private static byte[] BuildSingleImage(string path)
    {
        using var img = new MagickImage(path);
        return img.ToByteArray(MagickFormat.Png);
    }

    private static byte[] BuildContactSheet(List<string> paths, int extraCount)
    {
        var grid = (int)Math.Ceiling(Math.Sqrt(paths.Count));
        if (grid < 1) grid = 1;
        var canvasSize = grid * CellSize;

        using var sheet = new MagickImage(MagickColors.Transparent, (uint)canvasSize, (uint)canvasSize);

        for (int i = 0; i < paths.Count; i++)
        {
            var col = i % grid;
            var row = i / grid;
            var cellX = col * CellSize;
            var cellY = row * CellSize;

            try
            {
                using var img = new MagickImage(paths[i]);
                // Resize to fit inside CellSize×CellSize preserving aspect ratio (Greater=true only shrinks).
                img.Resize(new MagickGeometry((uint)CellSize, (uint)CellSize) { Greater = true });

                // Letterbox to exactly CellSize×CellSize with transparent fill so the cell is centered.
                img.BackgroundColor = MagickColors.Transparent;
                img.Extent(
                    new MagickGeometry((uint)CellSize, (uint)CellSize),
                    Gravity.Center,
                    MagickColors.Transparent);

                sheet.Composite(img, cellX, cellY, CompositeOperator.Over);
            }
            catch
            {
                // Skip individual decode failures; leave the cell transparent.
            }
        }

        if (extraCount > 0)
        {
            DrawMoreOverlay(sheet, canvasSize, extraCount);
        }

        return sheet.ToByteArray(MagickFormat.Png);
    }

    private static void DrawMoreOverlay(MagickImage sheet, int canvasSize, int extraCount)
    {
        var text = $"+{extraCount} more";
        const double fontSize = 28;
        const int padX = 14;
        const int padY = 10;
        // Rough width estimate (Magick.NET doesn't expose cheap text metrics on every backend);
        // a fixed 16 px-per-char-at-28pt approximation is good enough for a small badge.
        int estTextWidth = (int)(text.Length * fontSize * 0.55);
        int boxW = estTextWidth + 2 * padX;
        int boxH = (int)(fontSize + 2 * padY);
        int boxX = canvasSize - boxW - 12;
        int boxY = canvasSize - boxH - 12;

        new Drawables()
            .FillColor(new MagickColor((byte)0, (byte)0, (byte)0, (byte)180))
            .Rectangle(boxX, boxY, boxX + boxW, boxY + boxH)
            .FillColor(MagickColors.White)
            .StrokeColor(MagickColors.Transparent)
            .FontPointSize(fontSize)
            .TextAlignment(TextAlignment.Left)
            .Text(boxX + padX, boxY + boxH - padY - 2, text)
            .Draw(sheet);
    }
}
