using System.Diagnostics;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// <see cref="ICandidatePreviewProducer"/> that renders diagram source files committed in a
/// candidate's worktree (Mermaid <c>.mmd</c>, PlantUML <c>.puml</c>/<c>.plantuml</c>, raw
/// <c>.svg</c>, and Draw.io <c>.drawio</c>) into a single contact-sheet PNG suitable for
/// surfacing in the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Sits at <see cref="Priority"/> = 20 — between <c>ImageAssetCandidatePreviewProducer</c>
/// (Priority=10, wins when raw image assets ship in the PR) and
/// <see cref="PlaywrightCandidatePreviewProducer"/> (Priority=100, last-resort fallback).
/// </para>
/// <para>
/// Rendering strategy per extension (chosen for minimal external dependencies):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>.mmd</c>: spawn <c>mmdc</c> via <c>npx --yes @mermaid-js/mermaid-cli</c>.
///       Requires Node.js on PATH. If Node is absent or rendering fails, a placeholder
///       card is emitted instead so the producer still returns something useful.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>.svg</c>: rasterized in-process via Magick.NET (already a Core dep). No
///       external tools required.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>.puml</c>/<c>.plantuml</c>: emits a placeholder card (gray panel with filename
///       and a "PlantUML render skipped — install plantuml.jar to enable" note). Full
///       rendering would require Java + plantuml.jar which is too heavy for the default
///       runner footprint. TODO: optional <c>plantuml.jar</c> hookup if PLANTUML_JAR env
///       variable is set.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>.drawio</c>: placeholder card. <c>.drawio</c> files require the Electron-based
///       drawio CLI or VS Code extension to render; out of scope for the runner.
///     </description>
///   </item>
/// </list>
/// <para>
/// Returns <c>null</c> when the worktree contains zero matching diagram files. Always
/// returns a populated <see cref="CandidatePreview.IncludedAssetPaths"/> listing the
/// diagram source files that contributed cards to the sheet (including ones that were
/// rendered as placeholders due to missing tooling — they still count as "diagrams in
/// the PR").
/// </para>
/// </remarks>
public sealed class DiagramCandidatePreviewProducer : ICandidatePreviewProducer
{
    private static readonly string[] DiagramExtensions =
        { ".mmd", ".puml", ".plantuml", ".svg", ".drawio" };

    /// <summary>Hard cap on diagrams included in a single contact sheet — keeps the sheet readable and bounded.</summary>
    private const int MaxDiagramsPerSheet = 16;

    /// <summary>Timeout per mermaid render. mmdc has to spin up puppeteer/chromium on first run, so we allow a generous window.</summary>
    private static readonly TimeSpan MermaidRenderTimeout = TimeSpan.FromSeconds(90);

    private readonly ILogger<DiagramCandidatePreviewProducer> _logger;

    public DiagramCandidatePreviewProducer(ILogger<DiagramCandidatePreviewProducer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public int Priority => 20;

    /// <inheritdoc />
    public string Id => "diagrams";

    /// <inheritdoc />
    public async Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.CandidateWorktreePath) ||
            !Directory.Exists(context.CandidateWorktreePath))
        {
            _logger.LogDebug(
                "Worktree path '{Path}' does not exist — diagram producer declining.",
                context.CandidateWorktreePath);
            return null;
        }

        var diagrams = FindDiagrams(context.CandidateWorktreePath);
        if (diagrams.Count == 0)
        {
            _logger.LogDebug(
                "No diagram source files found under '{Path}' — diagram producer declining.",
                context.CandidateWorktreePath);
            return null;
        }

        if (diagrams.Count > MaxDiagramsPerSheet)
        {
            _logger.LogInformation(
                "Diagram producer found {Total} diagrams; capping contact sheet at {Cap}.",
                diagrams.Count, MaxDiagramsPerSheet);
            diagrams = diagrams.Take(MaxDiagramsPerSheet).ToList();
        }

        Directory.CreateDirectory(context.ArtifactOutputDir);

        var cards = new List<(byte[] ImageBytes, string Caption)>(diagrams.Count);
        var includedPaths = new List<string>(diagrams.Count);

        foreach (var path in diagrams)
        {
            ct.ThrowIfCancellationRequested();

            byte[]? rendered = await RenderDiagramAsync(path, context.ArtifactOutputDir, ct).ConfigureAwait(false);
            // Producer-level invariant: every detected diagram source produces a card
            // (real render or placeholder). Failing-to-render is logged inside
            // RenderDiagramAsync and falls through to a placeholder.
            rendered ??= BuildPlaceholderCard(Path.GetFileName(path), "render failed");

            cards.Add((rendered, Path.GetFileName(path)));
            includedPaths.Add(path);
        }

        if (cards.Count == 0)
        {
            // Defensive — should be unreachable given the placeholder fallback above.
            return null;
        }

        byte[] sheetPng;
        try
        {
            sheetPng = ContactSheetBuilder.Build(cards);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ContactSheetBuilder failed for diagram preview ({Count} cards) on task {TaskId}/{Strategy}.",
                cards.Count, context.TaskId, context.StrategyId);
            return null;
        }

        return new CandidatePreview
        {
            SourceProducerId = Id,
            ScreenshotBase64 = Convert.ToBase64String(sheetPng),
            Source = CandidatePreviewSource.Diagrams,
            IncludedAssetPaths = includedPaths,
        };
    }

    /// <summary>
    /// Walks the worktree recursively and returns diagram source files in a stable order
    /// (relative-path sort) so contact sheets are deterministic across runs.
    /// Skips build/tool directories (<c>.git</c>, <c>node_modules</c>, <c>bin</c>, <c>obj</c>)
    /// and fixture/test-data/sample directories (see <see cref="PreviewDiscoveryFilters"/>),
    /// so a user-provided diagram copied into a fixtures folder is not mis-surfaced as a
    /// candidate-generated deliverable.
    /// </summary>
    internal static List<string> FindDiagrams(string root)
    {
        var results = new List<string>();
        var rootFull = Path.GetFullPath(root);

        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var sub in subDirs)
            {
                if (PreviewDiscoveryFilters.IsExcludedDirectory(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (DiagramExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(f);
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private async Task<byte[]?> RenderDiagramAsync(string sourcePath, string artifactDir, CancellationToken ct)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mmd" => await RenderMermaidAsync(sourcePath, artifactDir, ct).ConfigureAwait(false),
                ".svg" => RenderSvg(sourcePath),
                ".puml" or ".plantuml" => BuildPlaceholderCard(
                    Path.GetFileName(sourcePath),
                    "PlantUML render skipped — install plantuml.jar to enable"),
                ".drawio" => BuildPlaceholderCard(
                    Path.GetFileName(sourcePath),
                    "Draw.io render skipped — not supported server-side"),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render diagram '{Path}' — will use placeholder card.", sourcePath);
            return null;
        }
    }

    private async Task<byte[]?> RenderMermaidAsync(string sourcePath, string artifactDir, CancellationToken ct)
    {
        var outputPath = Path.Combine(
            artifactDir,
            $"mermaid-{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}.png");

        // mmdc args: input, output, transparent background, white text on dark for readability
        var args = $"--yes @mermaid-js/mermaid-cli -i \"{sourcePath}\" -o \"{outputPath}\" -b transparent";

        var (fileName, fullArgs) = OperatingSystem.IsWindows()
            ? ("cmd", $"/c npx {args}")
            : ("npx", args);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = fullArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Could not start mmdc (Node/npx not on PATH?) — emitting placeholder for '{File}'.",
                Path.GetFileName(sourcePath));
            return BuildPlaceholderCard(
                Path.GetFileName(sourcePath),
                "Mermaid render skipped — Node.js not detected");
        }

        if (proc is null)
        {
            return BuildPlaceholderCard(
                Path.GetFileName(sourcePath),
                "Mermaid render skipped — process start returned null");
        }

        using (proc)
        {
            using var timeoutCts = new CancellationTokenSource(MermaidRenderTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _logger.LogWarning(
                    "mmdc timed out rendering '{File}' after {Seconds}s — emitting placeholder.",
                    Path.GetFileName(sourcePath), MermaidRenderTimeout.TotalSeconds);
                return BuildPlaceholderCard(
                    Path.GetFileName(sourcePath),
                    "Mermaid render timed out");
            }

            if (proc.ExitCode != 0)
            {
                var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "mmdc exited {ExitCode} rendering '{File}': {Err}",
                    proc.ExitCode, Path.GetFileName(sourcePath), Truncate(err, 400));
                return BuildPlaceholderCard(
                    Path.GetFileName(sourcePath),
                    "Mermaid render failed");
            }
        }

        if (!File.Exists(outputPath))
        {
            return BuildPlaceholderCard(
                Path.GetFileName(sourcePath),
                "Mermaid output missing");
        }

        return await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rasterizes an <c>.svg</c> file to PNG using Magick.NET. Magick.NET-Q8-AnyCPU ships
    /// with an internal SVG reader sufficient for the common authoring tools that emit
    /// SVG (mermaid offline export, Excalidraw, etc.); complex SVGs needing librsvg may
    /// fall back to the rendering failure path.
    /// </summary>
    private static byte[] RenderSvg(string sourcePath)
    {
        var readSettings = new MagickReadSettings
        {
            BackgroundColor = MagickColors.Transparent,
            Format = MagickFormat.Svg,
        };

        using var img = new MagickImage();
        img.Read(sourcePath, readSettings);
        img.Format = MagickFormat.Png;
        return img.ToByteArray();
    }

    /// <summary>
    /// Builds a 600×400 placeholder PNG with the source filename and a "render skipped"
    /// reason message. Used for diagram formats we don't render in-process (PlantUML,
    /// Draw.io) and as a fallback for render failures.
    /// </summary>
    internal static byte[] BuildPlaceholderCard(string fileName, string reason)
    {
        const int w = 600;
        const int h = 400;

        using var img = new MagickImage(new MagickColor("#2c3e50"), w, h);

        new Drawables()
            .FillColor(new MagickColor("#34495e"))
            .Rectangle(20, 20, w - 20, h - 20)
            .Draw(img);

        new Drawables()
            .FillColor(new MagickColor("#ecf0f1"))
            .FontPointSize(28)
            .TextAlignment(TextAlignment.Center)
            .Text(w / 2.0, h / 2.0 - 20, fileName)
            .Draw(img);

        new Drawables()
            .FillColor(new MagickColor("#bdc3c7"))
            .FontPointSize(16)
            .TextAlignment(TextAlignment.Center)
            .Text(w / 2.0, h / 2.0 + 30, reason)
            .Draw(img);

        return img.ToByteArray(MagickFormat.Png);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? string.Empty) : s.Substring(0, max);
}
