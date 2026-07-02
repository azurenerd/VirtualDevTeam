using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// MissingWorkDetector D1: scan source files for comments referencing FUTURE work tokens
/// (e.g. <c>// Future: load X</c>, <c>// TODO(handle Y)</c>, <c>(T14 art pipeline)</c>) that
/// have NO matching open or recently-closed issue. The Game Engine Engineer's
/// <c>EnemyEntity.ts</c> comment <c>"when sprite textures aren't available yet (T14 art pipeline)"</c>
/// would have triggered this — there was no T14 issue tracking it.
///
/// <para>
/// Detection patterns (case-insensitive):
/// <list type="bullet">
///   <item><c>// TODO(\w+)</c> / <c>// FIXME</c> / <c>// XXX</c> — generic deferred-work markers</item>
///   <item><c>// Future:</c> / <c>// Pending:</c> / <c>// NotYet:</c> — future-work comments</item>
///   <item><c>(T-?\d+ ...)</c> / <c>TASK-\d+</c> — task ID references</item>
///   <item><c>when \w+ is available</c> / <c>awaiting (the )?\w+ (pipeline|task|implementation)</c> — natural-lang gap markers</item>
/// </list>
/// </para>
///
/// <para>
/// Cross-reference: every matched token is checked against the open + recently-closed
/// issue title set. Tokens with no match emit a finding. Confidence scales with the
/// pattern type (explicit task IDs are higher confidence than generic TODOs).
/// </para>
/// </summary>
public sealed class PhantomTaskReferenceDetector : VirtualDevTeam.Core.MissingWork.IMissingWorkDetector
{
    public string DetectorId => "phantom-task-reference";

    private readonly ILogger<PhantomTaskReferenceDetector> _logger;

    public PhantomTaskReferenceDetector(ILogger<PhantomTaskReferenceDetector> logger)
    {
        _logger = logger;
    }

    // Patterns. Each entry: regex + confidence + descriptive kind.
    private static readonly (Regex Pattern, double Confidence, string Kind)[] Patterns = new[]
    {
        // Explicit task IDs — highest confidence. Matches T14, T-1481, TASK-42.
        (new Regex(@"\b(?<token>(T-?\d+|TASK-\d+))\b\s+(?<context>[^\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), 0.85, "task-id"),

        // Future:/Pending:/NotYet: — high confidence
        (new Regex(@"//\s*(Future|Pending|NotYet|Later)\s*:\s*(?<token>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), 0.7, "future-comment"),

        // Natural language: "when X is available", "awaiting Y pipeline"
        (new Regex(@"\b(when|awaiting)\s+(?<token>[\w\s\-]+?)\s+(is\s+)?(available|pipeline|task|implementation|ready)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), 0.55, "natural-lang"),

        // Generic TODO/FIXME/XXX — lowest confidence (might be intentional micro-tasks)
        (new Regex(@"//\s*(?<token>(TODO|FIXME|XXX))\b(\([^\)]+\))?\s*:?\s*(?<context>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), 0.4, "todo-marker"),
    };

    private static readonly string[] CodeFileExtensions = new[]
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs",
        ".java", ".kt", ".swift", ".cpp", ".cc", ".h", ".hpp",
        ".rb", ".php", ".scala", ".dart", ".vue", ".svelte",
        ".razor", ".cshtml", ".vb",
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

            var allIssueText = string.Join(" || ",
                ctx.OpenIssues.Select(i => $"{i.Title} {i.Body}")
                    .Concat(ctx.RecentlyClosedIssues.Select(i => $"{i.Title} {i.Body}"))).ToLowerInvariant();

            // Group matches by dedup key so a token that appears in 50 places becomes
            // ONE finding with 50 evidence citations rather than 50 noisy findings.
            var byKey = new Dictionary<string, (double Confidence, string Kind, string Pattern, List<VirtualDevTeam.Core.MissingWork.EvidenceCitation> Evidence)>();

            foreach (var file in EnumerateCodeFiles(ctx.WorkspaceRoot, ct))
            {
                if (ct.IsCancellationRequested) break;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    foreach (var (pattern, confidence, kind) in Patterns)
                    {
                        var matches = pattern.Matches(line);
                        foreach (Match m in matches)
                        {
                            var rawToken = m.Groups["token"].Value.Trim();
                            var token = kind == "natural-lang" ? NormalizeNaturalLangToken(rawToken) : rawToken;
                            if (string.IsNullOrEmpty(token)) continue;

                            // Cross-reference: skip tokens that appear in any open or recent issue title.
                            // This is a coarse check but keeps the detector deterministic + cheap.
                            if (allIssueText.Contains(token.ToLowerInvariant(), StringComparison.Ordinal))
                                continue;

                            var dedupKey = $"phantom-task:{kind}:{HashShort(token)}";
                            if (!byKey.TryGetValue(dedupKey, out var bucket))
                            {
                                bucket = (confidence, kind, token, new List<VirtualDevTeam.Core.MissingWork.EvidenceCitation>());
                                byKey[dedupKey] = bucket;
                            }
                            if (bucket.Evidence.Count < 10) // cap evidence per finding
                            {
                                bucket.Evidence.Add(new VirtualDevTeam.Core.MissingWork.EvidenceCitation
                                {
                                    FilePath = Path.GetRelativePath(ctx.WorkspaceRoot, file).Replace('\\', '/'),
                                    LineNumber = i + 1,
                                    Snippet = line.Length <= 200 ? line.TrimEnd() : line[..200].TrimEnd() + "…",
                                    Kind = kind,
                                });
                            }
                        }
                    }
                }
            }

            foreach (var (key, bucket) in byKey)
            {
                if (bucket.Evidence.Count == 0) continue;
                findings.Add(new VirtualDevTeam.Core.MissingWork.MissingWorkFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectorId = DetectorId,
                    Pattern = bucket.Pattern,
                    Summary = $"'{bucket.Pattern}' referenced in {bucket.Evidence.Count} location(s) but no tracking issue found",
                    Evidence = bucket.Evidence,
                    Confidence = bucket.Confidence,
                    DedupKey = key,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PhantomTaskReferenceDetector tick failed (non-fatal)");
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
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch { continue; }

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(CodeFileExtensions, ext) >= 0)
                    yield return f;
            }
            foreach (var sub in subdirs)
            {
                var normalizedSub = sub.Replace('\\', '/') + "/";
                bool excluded = false;
                foreach (var seg in ExcludedDirSegments)
                {
                    if (normalizedSub.Contains(seg, StringComparison.OrdinalIgnoreCase)) { excluded = true; break; }
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

    private static readonly HashSet<string> _stopwords = new(StringComparer.OrdinalIgnoreCase)
    { "the", "a", "an", "is", "are", "be", "to", "of", "for", "with", "and", "or" };

    private static string NormalizeNaturalLangToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var parts = raw.Split(new[] { ' ', '\t', '\n', '\r', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (!_stopwords.Contains(p)) return p.ToLowerInvariant();
        }
        return raw.ToLowerInvariant();
    }
}
