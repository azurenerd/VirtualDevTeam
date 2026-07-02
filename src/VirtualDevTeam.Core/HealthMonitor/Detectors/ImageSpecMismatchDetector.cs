using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// FlowMonitor detector that verifies image deliverables promised by the PM.
///
/// The PM prompt emits a structured <c>[image-deliverables]</c> YAML manifest at the
/// bottom of <c>PMSpec.md</c>:
/// <code>
/// [image-deliverables]
/// - path: AgentDocs/MyProject/reference-images/style-anchor.png
///   purpose: "Master style reference."
/// - path: AgentDocs/MyProject/reference-images/cannon-tower-concept.png
///   purpose: "Concept reference for the Cannon Tower."
/// </code>
/// Or, when no images are required:
/// <code>
/// [image-deliverables]
/// # No image deliverables required for this project.
/// </code>
///
/// This detector parses the manifest, verifies each declared path exists on the working
/// branch, and that the file is at least 5 KB (rules out empty placeholders).
///
/// Findings:
/// <list type="bullet">
/// <item><description>Missing file → <see cref="FlowFindingSeverity.Critical"/></description></item>
/// <item><description>Trivial size (≤ 5 KB) → <see cref="FlowFindingSeverity.Warning"/></description></item>
/// <item><description>PMSpec missing manifest entirely → <see cref="FlowFindingSeverity.Warning"/></description></item>
/// </list>
///
/// Architecture.md is also probed for symmetry, but no warning is emitted if it lacks the
/// manifest (only PMSpec is required to declare image deliverables today).
///
/// The detector lives in Core (alongside the other Tier-2 detectors) because all its
/// dependencies (<see cref="IRepositoryContentService"/>, <see cref="ProjectFileManager"/>)
/// are Core-resident.
/// </summary>
public sealed class ImageSpecMismatchDetector : IFlowDetector
{
    public string DetectorId => "image-spec-mismatch";

    /// <summary>
    /// Minimum byte size to consider an image file "real" (vs. an empty placeholder).
    /// 5 KB is conservative: even a 32x32 indexed PNG is typically &gt; 500 bytes; the
    /// smallest meaningful concept art for our purposes is well above 5 KB.
    /// </summary>
    public const int MinFileSizeBytes = 5 * 1024;

    /// <summary>Regex matching <c>- path: &lt;value&gt;</c> (value may be quoted).</summary>
    private static readonly Regex PathItemRegex = new(
        @"^-\s*path\s*:\s*(?<path>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> EligiblePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        // PMSpec.md is created by the PM during Research phase (in response to
        // ResearchComplete). By the time we reach Architecture, it must exist.
        // We deliberately exclude Initialization + Research to avoid firing while
        // the PM is still drafting.
        "Architecture",
        "EngineeringPlanning",
        "ParallelDevelopment",
        "Testing",
        "Review",
        "Completion",
    };

    private readonly ILogger<ImageSpecMismatchDetector> _logger;
    private readonly IRepositoryContentService? _repoContent;
    private readonly ProjectFileManager? _projectFiles;

    public ImageSpecMismatchDetector(
        ILogger<ImageSpecMismatchDetector> logger,
        IRepositoryContentService? repoContent = null,
        ProjectFileManager? projectFiles = null)
    {
        _logger = logger;
        _repoContent = repoContent;
        _projectFiles = projectFiles;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        try
        {
            // 1. Phase gate — PMSpec.md is not expected to exist before Architecture phase.
            if (!EligiblePhases.Contains(ctx.CurrentPhase))
                return findings;

            // 2. Need IRepositoryContentService to verify file existence/size. Without it
            //    (e.g., project not opened yet), we can't do anything useful — exit silently.
            if (_repoContent is null)
                return findings;

            var branch = ctx.EffectiveBranch;

            // 3. Read PMSpec.md from the working branch.
            var (pmSpecContent, pmSpecPath) = await ReadDocAsync("PMSpec.md", branch, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(pmSpecContent))
            {
                // PMSpec doesn't exist yet — too early to validate. This should be rare
                // because the phase gate above filters out pre-PMSpec phases, but legacy
                // resets or out-of-band edits could leave us here. Skip silently.
                return findings;
            }

            // 4. Parse the manifest from PMSpec.
            var pmManifest = ParseImageDeliverables(pmSpecContent);

            if (!pmManifest.HasMarker)
            {
                // PMSpec exists but no [image-deliverables] block — legacy PMSpec or
                // older prompt template. Emit Warning (not Critical) so the operator
                // is informed without blocking the flow.
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = pmSpecPath,
                    Summary = "PMSpec missing [image-deliverables] manifest",
                    Rationale =
                        $"{pmSpecPath} on branch '{branch}' has content but does not include a " +
                        "`[image-deliverables]` YAML block. Newer PM prompts emit this block at the " +
                        "bottom of PMSpec.md so the FlowMonitor can verify promised image assets were " +
                        "produced. Older PMSpecs from prior prompt versions may not have it — this is a " +
                        "warning, not a critical block. If the project has no visual assets, add an " +
                        "explicit empty block (`[image-deliverables]` followed by a `# no images...` comment).",
                    DedupKey = $"image-spec-mismatch:{branch}:pmspec-missing-manifest",
                });
            }
            else
            {
                // 5. For each declared path, verify existence and size on the working branch.
                await VerifyManifestAsync(pmManifest.Items, branch, ctx, pmSpecPath, findings, ct)
                    .ConfigureAwait(false);
            }

            // 6. Symmetric check on Architecture.md — IF it has the manifest, validate the
            //    paths too. No "missing manifest" warning for Architecture (PM is the only
            //    role required to declare image deliverables today).
            var (archContent, archPath) = await ReadDocAsync("Architecture.md", branch, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(archContent))
            {
                var archManifest = ParseImageDeliverables(archContent);
                if (archManifest.HasMarker && archManifest.Items.Count > 0)
                {
                    await VerifyManifestAsync(archManifest.Items, branch, ctx, archPath, findings, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageSpecMismatchDetector tick failed (non-fatal)");
        }

        return findings;
    }

    /// <summary>
    /// Read a doc by name through <see cref="ProjectFileManager"/> when available (handles
    /// the artifact-scoped path + root fallback), or fall back to raw repo reads.
    /// Returns the content and the resolved path used for any subsequent error reporting.
    /// </summary>
    private async Task<(string? Content, string Path)> ReadDocAsync(
        string fileName, string branch, CancellationToken ct)
    {
        if (_projectFiles is not null)
        {
            var resolvedPath = _projectFiles.ResolvePath(fileName);
            // ProjectFileManager wraps the placeholder when content is null — for PMSpec
            // we want the actual content (or null), so we go through GetFileAsync which
            // returns null on miss. Use the scoped path the manager would have used.
            var content = await _projectFiles.GetFileAsync(resolvedPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
                return (content, resolvedPath);

            // Fall back to repo-root (legacy pre-scope projects).
            var rootContent = await _repoContent!.GetFileContentAsync(fileName, branch, ct).ConfigureAwait(false);
            return (rootContent, fileName);
        }

        var direct = await _repoContent!.GetFileContentAsync(fileName, branch, ct).ConfigureAwait(false);
        return (direct, fileName);
    }

    /// <summary>
    /// Verify each declared image path exists on the working branch with byte size &gt; threshold.
    /// Adds Critical (missing) or Warning (trivial size) findings to <paramref name="findings"/>.
    /// </summary>
    private async Task VerifyManifestAsync(
        IReadOnlyList<string> declaredPaths,
        string branch,
        DetectorContext ctx,
        string sourceDocPath,
        List<FlowFinding> findings,
        CancellationToken ct)
    {
        foreach (var path in declaredPaths)
        {
            ct.ThrowIfCancellationRequested();

            byte[]? bytes;
            try
            {
                bytes = await _repoContent!.GetFileBytesAsync(path, branch, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "ImageSpecMismatchDetector: GetFileBytesAsync failed for {Path} on {Branch} — treating as missing",
                    path, branch);
                bytes = null;
            }

            if (bytes is null)
            {
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    TargetResource = path,
                    Summary = "Declared image deliverable missing",
                    Rationale =
                        $"{sourceDocPath} promised the image deliverable `{path}`, but the file is not " +
                        $"present on branch '{branch}'. Either the agent failed to generate/commit the " +
                        "image, the path in the manifest is incorrect, or the commit was reverted. " +
                        "Operator should regenerate the image OR correct the manifest in the source doc.",
                    DedupKey = $"image-spec-mismatch:{branch}:{path}",
                });
                continue;
            }

            if (bytes.Length <= MinFileSizeBytes)
            {
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = path,
                    Summary = "Declared image deliverable is trivially small",
                    Rationale =
                        $"{sourceDocPath} promised the image deliverable `{path}`, and the file exists on " +
                        $"branch '{branch}' but is only {bytes.Length} bytes (threshold: {MinFileSizeBytes}). " +
                        "This is almost certainly an empty placeholder, a failed download, or a corrupt " +
                        "render — not a usable image. Operator should regenerate the asset.",
                    DedupKey = $"image-spec-mismatch:{branch}:{path}-size",
                });
            }
        }
    }

    /// <summary>
    /// Parse the <c>[image-deliverables]</c> block from a doc.
    /// <para>
    /// Returns <see cref="ManifestParse.HasMarker"/>=false when no block is present.
    /// Returns <see cref="ManifestParse.HasMarker"/>=true with empty Items when the block
    /// exists but contains only YAML comments (the "empty manifest" sentinel).
    /// </para>
    /// </summary>
    internal static ManifestParse ParseImageDeliverables(string content)
    {
        var items = new List<string>();
        var hasMarker = false;
        var inBlock = false;

        var lines = content.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (!inBlock)
            {
                // Marker line: exactly "[image-deliverables]" possibly with surrounding whitespace.
                if (string.Equals(trimmed.TrimEnd(), "[image-deliverables]", StringComparison.OrdinalIgnoreCase))
                {
                    hasMarker = true;
                    inBlock = true;
                }
                continue;
            }

            // Inside block. Determine if this line:
            //   - is a list item ("- path: ..."): record path, keep going
            //   - is a YAML comment ("# ..."): skip
            //   - is blank: skip
            //   - is an indented continuation (e.g., "  purpose: ..."): skip
            //   - is a markdown heading or a new section marker: end the block

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith('#'))
            {
                // YAML comments inside the block start with `#`. We can't perfectly disambiguate
                // a markdown heading from a YAML comment, but the PM prompt requires the
                // [image-deliverables] block to be the LAST section of PMSpec.md, so anything
                // after the marker is part of the YAML payload. Treat `#` as a comment.
                continue;
            }

            // A list item.
            var match = PathItemRegex.Match(trimmed);
            if (match.Success)
            {
                var path = match.Groups["path"].Value.Trim();
                // Strip surrounding quotes (single or double) if the value was quoted.
                if (path.Length >= 2 &&
                    ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
                {
                    path = path[1..^1];
                }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    items.Add(path);
                }
                continue;
            }

            // Indented line (likely a YAML mapping continuation like "  purpose: ..."): skip.
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            // Otherwise we've fallen out of the block (e.g., a new top-level marker).
            break;
        }

        return new ManifestParse(hasMarker, items);
    }

    internal readonly record struct ManifestParse(bool HasMarker, IReadOnlyList<string> Items);
}
