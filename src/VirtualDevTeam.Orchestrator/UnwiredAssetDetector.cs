using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// MissingWorkDetector D2: scan asset directories for files NOT referenced by any source file.
/// Catches the live 2026-05-12 scenario where PR #1508 generated sprite PNGs but no code
/// referenced them (PreloadScene placeholder said <c>// Future: load sprite atlases, audio, etc.</c>
/// and never loaded them).
///
/// <para>
/// Detection logic:
/// <list type="bullet">
///   <item>Walk asset directories: <c>client/public/assets/</c>, <c>wwwroot/</c>, <c>static/</c>,
///         <c>Resources/</c>, <c>assets/</c>, <c>public/</c>, <c>art-pipeline/output/</c>.</item>
///   <item>For each non-trivial asset (size ≥ 1 KB), grep ALL source files for the basename
///         OR the full relative path. Directory-level references (e.g. <c>assets/sprites/{slug}/</c>)
///         count as references for all files in that directory (covers Phaser-style
///         <c>this.load.spritesheet(key, `assets/sprites/${id}/walk.png`)</c> patterns).</item>
///   <item>Unreferenced assets emit a single grouped finding per asset directory.</item>
/// </list>
/// </para>
///
/// <para>
/// Excluded from scans: node_modules, .git, bin, obj, .candidates, strategy-artifacts, dist,
/// build. Excluded extensions: .map, .d.ts, .lock files.
/// </para>
/// </summary>
public sealed class UnwiredAssetDetector : VirtualDevTeam.Core.MissingWork.IMissingWorkDetector
{
    public string DetectorId => "unwired-asset";

    private readonly ILogger<UnwiredAssetDetector> _logger;

    public UnwiredAssetDetector(ILogger<UnwiredAssetDetector> logger)
    {
        _logger = logger;
    }

    private const long MinAssetBytes = 1024;
    private const int MaxFindingsPerTick = 50;
    private const int MaxAssetsScanned = 5000;
    private const int MaxEvidencePerFinding = 8;

    private static readonly string[] AssetDirCandidates = new[]
    {
        "client/public/assets",
        "client/src/assets",
        "wwwroot/assets",
        "wwwroot",
        "static",
        "Resources",
        "assets",
        "public/assets",
        "art-pipeline/output",
    };

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico",
        ".mp3", ".wav", ".ogg", ".m4a", ".flac",
        ".mp4", ".webm", ".mov",
        ".glb", ".gltf", ".obj", ".fbx",
        ".ttf", ".otf", ".woff", ".woff2",
        ".pdf",
    };

    private static readonly string[] SourceFileExtensions = new[]
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs",
        ".java", ".kt", ".swift", ".cpp", ".cc", ".h", ".hpp",
        ".rb", ".php", ".vue", ".svelte", ".razor", ".cshtml", ".vb",
        ".html", ".css", ".scss", ".sass", ".less", ".json", ".yaml", ".yml", ".xml",
    };

    private static readonly string[] ExcludedDirSegments = new[]
    {
        "/.git/", "/node_modules/", "/.candidates/", "/bin/", "/obj/",
        "/dist/", "/build/", "/.next/", "/.cache/", "/__pycache__/",
        "/vendor/", "/target/", "/strategy-artifacts/",
    };

    public Task<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>> DetectAsync(
        VirtualDevTeam.Core.MissingWork.MissingWorkContext ctx, CancellationToken ct)
    {
        var findings = new List<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>();
        try
        {
            if (!Directory.Exists(ctx.WorkspaceRoot))
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);

            // Step 1: pre-load source file CONTENT into one big in-memory blob for grep
            // (deduped + truncated per file to keep memory bounded). Cheap O(total source size).
            var sourceBlob = LoadSourceBlob(ctx.WorkspaceRoot, ct);

            // Step 2: enumerate asset directories, find unreferenced assets, group by dir.
            int assetsScanned = 0;
            foreach (var relDirCandidate in AssetDirCandidates)
            {
                if (ct.IsCancellationRequested) break;
                var dir = Path.Combine(ctx.WorkspaceRoot, relDirCandidate);
                if (!Directory.Exists(dir)) continue;

                var unreferencedByDir = new Dictionary<string, List<VirtualDevTeam.Core.MissingWork.EvidenceCitation>>();
                foreach (var assetFile in EnumerateAssetFiles(dir, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    if (assetsScanned++ > MaxAssetsScanned) break;
                    if (findings.Count + unreferencedByDir.Count > MaxFindingsPerTick) break;

                    long size;
                    try { size = new FileInfo(assetFile).Length; }
                    catch { continue; }
                    if (size < MinAssetBytes) continue;

                    var relAssetPath = Path.GetRelativePath(ctx.WorkspaceRoot, assetFile).Replace('\\', '/');
                    var assetBasename = Path.GetFileName(assetFile);
                    var assetParentDir = Path.GetDirectoryName(relAssetPath)?.Replace('\\', '/') ?? "";

                    // Check 1: basename appears in any source file
                    if (sourceBlob.Contains(assetBasename, StringComparison.OrdinalIgnoreCase)) continue;
                    // Check 2: full relative path appears
                    if (sourceBlob.Contains(relAssetPath, StringComparison.OrdinalIgnoreCase)) continue;
                    // Check 3: parent directory referenced (covers dynamic loaders like
                    // this.load.spritesheet(key, `assets/sprites/${id}/walk.png`))
                    if (!string.IsNullOrEmpty(assetParentDir) &&
                        sourceBlob.Contains(assetParentDir, StringComparison.OrdinalIgnoreCase)) continue;

                    // Unreferenced — group by parent dir for cleaner findings
                    var groupKey = assetParentDir;
                    if (!unreferencedByDir.TryGetValue(groupKey, out var list))
                    {
                        list = new List<VirtualDevTeam.Core.MissingWork.EvidenceCitation>();
                        unreferencedByDir[groupKey] = list;
                    }
                    if (list.Count < MaxEvidencePerFinding)
                    {
                        list.Add(new VirtualDevTeam.Core.MissingWork.EvidenceCitation
                        {
                            FilePath = relAssetPath,
                            LineNumber = null,
                            Snippet = $"{size / 1024} KB",
                            Kind = "asset-file",
                        });
                    }
                }

                foreach (var (groupDir, evidence) in unreferencedByDir)
                {
                    if (evidence.Count == 0) continue;
                    var dedupKey = $"unwired-asset:{HashShort(groupDir)}";
                    findings.Add(new VirtualDevTeam.Core.MissingWork.MissingWorkFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectorId = DetectorId,
                        Pattern = groupDir,
                        Summary = $"{evidence.Count} asset file(s) in '{groupDir}' are not referenced by any source code",
                        Evidence = evidence,
                        Confidence = evidence.Count >= 3 ? 0.85 : 0.65,
                        DedupKey = dedupKey,
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UnwiredAssetDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
    }

    private static string LoadSourceBlob(string root, CancellationToken ct)
    {
        var sb = new StringBuilder(1024 * 1024);
        var stack = new Stack<string>();
        stack.Push(root);
        int filesRead = 0;
        const int MaxSourceFiles = 5000;
        const long MaxBytesPerFile = 256 * 1024;
        while (stack.Count > 0 && filesRead < MaxSourceFiles)
        {
            if (ct.IsCancellationRequested) break;
            var dir = stack.Pop();
            string[] files, subdirs;
            try { files = Directory.GetFiles(dir); subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                if (filesRead >= MaxSourceFiles) break;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(SourceFileExtensions, ext) < 0) continue;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > MaxBytesPerFile) continue;
                    sb.AppendLine(File.ReadAllText(f));
                    filesRead++;
                }
                catch { continue; }
            }
            foreach (var sub in subdirs)
            {
                var normalized = sub.Replace('\\', '/') + "/";
                bool excluded = false;
                foreach (var seg in ExcludedDirSegments)
                {
                    if (normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)) { excluded = true; break; }
                }
                if (!excluded) stack.Push(sub);
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateAssetFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            var dir = stack.Pop();
            string[] files, subdirs;
            try { files = Directory.GetFiles(dir); subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (AssetExtensions.Contains(ext)) yield return f;
            }
            foreach (var sub in subdirs)
            {
                var normalized = sub.Replace('\\', '/') + "/";
                bool excluded = false;
                foreach (var seg in ExcludedDirSegments)
                {
                    if (normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)) { excluded = true; break; }
                }
                if (!excluded) stack.Push(sub);
            }
        }
    }

    private static string HashShort(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
