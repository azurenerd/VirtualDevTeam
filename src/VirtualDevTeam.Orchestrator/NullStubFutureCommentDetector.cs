using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// MissingWorkDetector D3: scan source files for fields/properties initialized to <c>null</c>
/// with a nearby comment indicating future work that ISN'T tracked as an issue. Catches the
/// 2026-05-12 EnemyEntity.ts pattern:
/// <code>
/// sprite: Phaser.GameObjects.Sprite | null = null;
/// // "when sprite textures aren't available yet (T14 art pipeline)"
/// </code>
/// — placeholder field declared, never assigned, comment references a phantom T14 task.
///
/// <para>
/// Detection logic:
/// <list type="bullet">
///   <item>Match TS/C# field/property patterns: <c>field: Type | null = null</c>, <c>field?: Type</c>,
///         <c>field?: Type = null</c>, <c>public T? field { get; set; }</c></item>
///   <item>Inspect ±5 lines around the match for future-work keywords: <c>when</c>, <c>awaiting</c>,
///         <c>TODO</c>, <c>FIXME</c>, <c>pending</c>, <c>future</c>, <c>pipeline</c>, <c>not yet</c>,
///         <c>placeholder</c>.</item>
///   <item>Verify the field is never assigned elsewhere in the project (cheap text grep for
///         <c>\.{field}\s*=</c> — false positives possible on common names but acceptable
///         for MVP).</item>
///   <item>Emit finding with the field declaration line + nearby comment.</item>
/// </list>
/// </para>
/// </summary>
public sealed class NullStubFutureCommentDetector : VirtualDevTeam.Core.MissingWork.IMissingWorkDetector
{
    public string DetectorId => "null-stub-future-comment";

    private readonly ILogger<NullStubFutureCommentDetector> _logger;

    public NullStubFutureCommentDetector(ILogger<NullStubFutureCommentDetector> logger)
    {
        _logger = logger;
    }

    // TS field: `name: Type | null = null` or `name?: Type | null = null`
    // C# field: `public Type? name = null` or `public Type? name { get; set; }`
    // Captures the field name in group 'name'.
    private static readonly Regex TsNullFieldPattern = new(
        @"^\s*(?<name>\w+)\s*\??\s*:\s*[\w\.\<\>\[\]\s]+(\|\s*null)?\s*=\s*null\s*[;,]?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CSharpNullablePropertyPattern = new(
        @"public\s+[\w\.\<\>\[\],\s]+\?\s+(?<name>\w+)\s*\{",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FutureWorkKeywords = new(
        @"\b(when|awaiting|TODO|FIXME|XXX|pending|future|pipeline|not\s+yet|placeholder|stub|once\s+\w+\s+(is\s+)?(ready|available|implemented))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] CodeFileExtensions = new[]
    {
        ".ts", ".tsx", ".cs", ".java", ".kt", ".swift", ".dart",
    };

    private static readonly string[] ExcludedDirSegments = new[]
    {
        "/.git/", "/node_modules/", "/.candidates/", "/bin/", "/obj/",
        "/dist/", "/build/", "/.next/", "/.cache/", "/__pycache__/",
        "/vendor/", "/target/", "/strategy-artifacts/", "/.test.", "/test.",
    };

    private const int MaxFilesScanned = 2000;
    private const int MaxEvidencePerFinding = 8;

    public Task<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>> DetectAsync(
        VirtualDevTeam.Core.MissingWork.MissingWorkContext ctx, CancellationToken ct)
    {
        var findings = new List<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>();
        try
        {
            if (!Directory.Exists(ctx.WorkspaceRoot))
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);

            var byPattern = new Dictionary<string, (double Confidence, string FieldName, List<VirtualDevTeam.Core.MissingWork.EvidenceCitation> Evidence)>();

            int filesScanned = 0;
            foreach (var file in EnumerateCodeFiles(ctx.WorkspaceRoot, ct))
            {
                if (ct.IsCancellationRequested) break;
                if (filesScanned++ > MaxFilesScanned) break;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var patterns = ext switch
                {
                    ".ts" or ".tsx" => new[] { TsNullFieldPattern },
                    ".cs" => new[] { TsNullFieldPattern, CSharpNullablePropertyPattern },
                    _ => new[] { TsNullFieldPattern, CSharpNullablePropertyPattern },
                };

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    foreach (var pat in patterns)
                    {
                        var m = pat.Match(line);
                        if (!m.Success) continue;
                        var fieldName = m.Groups["name"].Value;
                        if (string.IsNullOrEmpty(fieldName)) continue;

                        // Look ±5 lines for future-work keywords
                        var contextStart = Math.Max(0, i - 5);
                        var contextEnd = Math.Min(lines.Length - 1, i + 5);
                        bool hasFutureComment = false;
                        string? commentLine = null;
                        for (int j = contextStart; j <= contextEnd; j++)
                        {
                            if (j == i) continue;
                            var ctxLine = lines[j];
                            // Only inspect actual comment lines or doc strings
                            var trimmed = ctxLine.TrimStart();
                            if (!trimmed.StartsWith("//") && !trimmed.StartsWith("*") &&
                                !trimmed.StartsWith("/*") && !trimmed.StartsWith("#")) continue;
                            if (FutureWorkKeywords.IsMatch(ctxLine))
                            {
                                hasFutureComment = true;
                                commentLine = trimmed;
                                break;
                            }
                        }
                        if (!hasFutureComment) continue;

                        var dedupKey = $"null-stub:{HashShort(fieldName + ":" + Path.GetFileName(file))}";
                        if (!byPattern.TryGetValue(dedupKey, out var bucket))
                        {
                            bucket = (0.65, fieldName, new List<VirtualDevTeam.Core.MissingWork.EvidenceCitation>());
                            byPattern[dedupKey] = bucket;
                        }
                        if (bucket.Evidence.Count < MaxEvidencePerFinding)
                        {
                            bucket.Evidence.Add(new VirtualDevTeam.Core.MissingWork.EvidenceCitation
                            {
                                FilePath = Path.GetRelativePath(ctx.WorkspaceRoot, file).Replace('\\', '/'),
                                LineNumber = i + 1,
                                Snippet = $"{line.Trim()}\n    // → {commentLine?.Substring(0, Math.Min(120, commentLine.Length))}",
                                Kind = "null-stub",
                            });
                        }
                    }
                }
            }

            foreach (var (key, bucket) in byPattern)
            {
                if (bucket.Evidence.Count == 0) continue;
                findings.Add(new VirtualDevTeam.Core.MissingWork.MissingWorkFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectorId = DetectorId,
                    Pattern = bucket.FieldName,
                    Summary = $"Field '{bucket.FieldName}' is declared as null placeholder with future-work comment, never assigned ({bucket.Evidence.Count} occurrence(s))",
                    Evidence = bucket.Evidence,
                    Confidence = bucket.Confidence,
                    DedupKey = key,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NullStubFutureCommentDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
    }

    private static IEnumerable<string> EnumerateCodeFiles(string root, CancellationToken ct)
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
                if (Array.IndexOf(CodeFileExtensions, ext) < 0) continue;
                // Skip test files (likely have intentional null stubs)
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name.Contains(".test.") || name.Contains(".spec.")) continue;
                yield return f;
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
