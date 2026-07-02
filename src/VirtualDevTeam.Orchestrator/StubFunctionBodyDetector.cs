using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// MissingWorkDetector C1 (Layer C — Development-time): scans source files for function/method
/// bodies that contain only stub content — comments matching stub keywords, empty braces, or
/// parameters prefixed with <c>_</c> combined with an empty body.
///
/// <para><b>Detection categories:</b></para>
/// <list type="bullet">
///   <item><b>Cat-A</b> — Function body whose only executable content is comments matching
///         <c>\b(stub|placeholder|no.?op|wip|draft|for\s+now|to\s+be\s+wired|integration\s+point|TBD)\b</c>
///         (case-insensitive).</item>
///   <item><b>Cat-D</b> — Function body with zero executable statements (completely empty braces,
///         Python <c>pass</c> only, or Python <c>...</c> ellipsis only).</item>
///   <item><b>Cat-E</b> — Function parameters prefixed with <c>_</c> (the unused-parameter
///         convention) combined with a Cat-A or Cat-D body. Canonical case — the GridGuardians
///         pathfinding stub:
///         <code>export function register(_scene: any): void { /* to be completed when EventBus exists */ }</code>
///         </item>
/// </list>
///
/// <para><b>Confidence thresholds:</b> Cat-E = 0.92, Cat-A = 0.88, Cat-D = 0.85. All exceed the
/// PR-blocking threshold of 0.80 that EngineerAgentBase uses to apply the <c>stub-detected</c>
/// label and remove <c>ready-for-review</c>.</para>
///
/// <para><b>STUB_OK escape hatch:</b> Functions annotated with a <c>STUB_OK</c> comment either
/// in the body or within the 3 lines immediately preceding the function header are exempt.
/// Convention:
/// <code>
/// // STUB_OK: pathfinding placeholder — software-engineer-1 2026-05-13
/// export function register(_scene: any): void {
///   /* to be completed when EventBus exists */
/// }
///
/// # Python / shell variant:
/// # STUB_OK: intentional no-op during init phase — software-engineer-2 2026-05-14
/// def register(scene): pass
/// </code>
/// </para>
///
/// <para><b>Languages supported (best-effort regex, no full parser):</b>
/// TypeScript (.ts, .tsx), JavaScript (.js, .jsx, .mjs, .cjs), C# (.cs), Python (.py), Go (.go).
/// </para>
///
/// <para><b>Known false-positive cases:</b> Legitimate empty C# methods such as
/// <c>IDisposable.Dispose() { }</c> will be flagged at Cat-D confidence. Use the
/// <c>// STUB_OK:</c> annotation to suppress intentional empty methods.</para>
/// </summary>
public sealed class StubFunctionBodyDetector : VirtualDevTeam.Core.MissingWork.IMissingWorkDetector
{
    public string DetectorId => "stub-function-body";

    private readonly ILogger<StubFunctionBodyDetector> _logger;

    public StubFunctionBodyDetector(ILogger<StubFunctionBodyDetector> logger)
    {
        _logger = logger;
    }

    // Stub keyword regex — applied to comment content inside a function body.
    // Includes TODO and "to be completed" since these are the most common stub markers in the VDT fleet.
    private static readonly Regex StubCommentPattern = new(
        @"\b(stub|placeholder|no.?op|wip|draft|for\s+now|to\s+be\s+(wired|completed)|integration\s+point|TBD|TODO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // STUB_OK annotation: // STUB_OK: reason — agent-id date (or # for Python/YAML/shell)
    private static readonly Regex StubOkPattern = new(
        @"(?://|#)\s*STUB_OK\s*:",
        RegexOptions.Compiled);

    // Underscore-prefixed parameters (unused-param convention in TS/JS/Python/Go).
    // Uses simple word-boundary match: any identifier starting with _ in the params string.
    private static readonly Regex UnderscoreParamPattern = new(
        @"\b_\w+",
        RegexOptions.Compiled);

    // ── TypeScript / JavaScript ────────────────────────────────────────────────

    // Named function declaration: (export)? (async)? function name<T>(params)
    private static readonly Regex TsJsNamedFunction = new(
        @"(?:^|\s)(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(?<name>\w+)\s*(?:<[^>]*>)?\s*\((?<params>[^)]{0,400})\)",
        RegexOptions.Compiled);

    // Class / object method with optional modifiers (must be indented to avoid matching top-level constructs)
    private static readonly Regex TsJsMethodHeader = new(
        @"^\s+(?:(?:public|private|protected|static|override|abstract|async|readonly)\s+)*(?<name>(?!(?:if|for|while|switch|catch|return|throw|new|await|typeof|void|class|import|export|const|let|var)\b)\w+)\s*(?:<[^>]*>)?\s*\((?<params>[^)]{0,400})\)\s*(?::\s*[\w\s<>\[\]|?,]+?)?\s*\{",
        RegexOptions.Compiled);

    // ── C# ────────────────────────────────────────────────────────────────────

    // C# method header: modifiers+ return-type name(params)
    // Excludes class/struct/interface/record/namespace declarations.
    private static readonly Regex CSharpMethodHeader = new(
        @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|abstract|async|new|partial|extern)\s+)+(?!(?:class|struct|interface|enum|namespace|record)\b)(?:(?:Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|void|bool|string|int|uint|long|ulong|double|float|decimal|byte|sbyte|char|short|ushort|object|dynamic|IEnumerable(?:<[^>]+>)?|IAsyncEnumerable(?:<[^>]+>)?|\w+(?:<[^>]+>)?(?:\[\])?)\s+)(?<name>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\((?<params>[^)]{0,400})\)",
        RegexOptions.Compiled);

    // ── Python ────────────────────────────────────────────────────────────────

    private static readonly Regex PythonDefHeader = new(
        @"^(?<indent>[ \t]*)def\s+(?<name>\w+)\s*\((?<params>[^)]{0,400})\)\s*(?:->[^:]+)?\s*:",
        RegexOptions.Compiled);

    // ── Go ────────────────────────────────────────────────────────────────────

    private static readonly Regex GoFuncHeader = new(
        @"^func\s+(?:\([^)]+\)\s+)?(?<name>\w+)\s*\((?<params>[^)]{0,400})\)",
        RegexOptions.Compiled);

    // ── File / dir filters ────────────────────────────────────────────────────

    private static readonly string[] SupportedExtensions = new[]
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".cs", ".py", ".go",
    };

    private static readonly string[] ExcludedDirSegments = new[]
    {
        "/.git/", "/node_modules/", "/.candidates/", "/bin/", "/obj/",
        "/dist/", "/build/", "/.next/", "/.cache/", "/__pycache__/",
        "/vendor/", "/target/", "/strategy-artifacts/",
    };

    private const int MaxFilesScanned = 3000;
    private const int MaxEvidencePerFinding = 8;

    /// <summary>
    /// Cap body extraction at this many lines to avoid runaway scanning on minified/generated files.
    /// </summary>
    private const int MaxBodyScanLines = 120;

    public Task<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>> DetectAsync(
        VirtualDevTeam.Core.MissingWork.MissingWorkContext ctx, CancellationToken ct)
    {
        var findings = new List<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>();
        try
        {
            if (!Directory.Exists(ctx.WorkspaceRoot))
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);

            var byKey = new Dictionary<string, (double Confidence, string CategoryLabel, string FuncName, List<VirtualDevTeam.Core.MissingWork.EvidenceCitation> Evidence)>();

            int filesScanned = 0;
            foreach (var file in EnumerateCodeFiles(ctx.WorkspaceRoot, ct))
            {
                if (ct.IsCancellationRequested) break;
                if (filesScanned++ > MaxFilesScanned) break;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var relPath = Path.GetRelativePath(ctx.WorkspaceRoot, file).Replace('\\', '/');

                IEnumerable<StubMatch> stubs = ext switch
                {
                    ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => FindTsJsStubs(lines),
                    ".cs" => FindCSharpStubs(lines),
                    ".py" => FindPythonStubs(lines),
                    ".go" => FindGoStubs(lines),
                    _ => Array.Empty<StubMatch>(),
                };

                foreach (var stub in stubs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (stub.HasStubOk) continue;

                    var (confidence, categoryLabel) = stub.Category switch
                    {
                        StubCategory.CatE => (0.92, "Cat-E"),
                        StubCategory.CatA => (0.88, "Cat-A"),
                        StubCategory.CatD => (0.85, "Cat-D"),
                        _ => (0.0, "None"),
                    };
                    if (confidence < 0.80) continue;

                    var dedupKey = $"stub-body:{stub.Category}:{HashShort(stub.FunctionName + ":" + relPath)}";

                    if (!byKey.TryGetValue(dedupKey, out var bucket))
                    {
                        bucket = (confidence, categoryLabel, stub.FunctionName,
                            new List<VirtualDevTeam.Core.MissingWork.EvidenceCitation>());
                        byKey[dedupKey] = bucket;
                    }

                    if (bucket.Evidence.Count < MaxEvidencePerFinding)
                    {
                        var snippet = stub.SignatureSnippet;
                        bucket.Evidence.Add(new VirtualDevTeam.Core.MissingWork.EvidenceCitation
                        {
                            FilePath = relPath,
                            LineNumber = stub.HeaderLine + 1,
                            Snippet = snippet.Length <= 200 ? snippet : snippet[..200] + "…",
                            Kind = categoryLabel.ToLowerInvariant(),
                        });
                    }
                }
            }

            foreach (var (_, bucket) in byKey)
            {
                if (bucket.Evidence.Count == 0) continue;
                findings.Add(new VirtualDevTeam.Core.MissingWork.MissingWorkFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectorId = DetectorId,
                    Pattern = bucket.FuncName,
                    Summary = $"'{bucket.FuncName}' appears to be a stub function ({bucket.CategoryLabel}) in {bucket.Evidence.Count} location(s) — implement body or add // STUB_OK: annotation",
                    Evidence = bucket.Evidence,
                    Confidence = bucket.Confidence,
                    DedupKey = $"stub-body:{bucket.CategoryLabel}:{HashShort(bucket.FuncName)}",
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StubFunctionBodyDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
    }

    // ── TypeScript / JavaScript ────────────────────────────────────────────────

    internal static IEnumerable<StubMatch> FindTsJsStubs(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Try named function declaration first, then method header
            var m = TsJsNamedFunction.Match(line);
            if (!m.Success)
                m = TsJsMethodHeader.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var paramsStr = m.Groups["params"].Value;
            if (string.IsNullOrEmpty(name)) continue;

            var (bodyLines, endLine) = ExtractBraceBody(lines, i);
            if (endLine < 0) continue;

            bool stubOk = HasStubOkAnnotation(lines, i, bodyLines);
            var category = ClassifyBraceBody(bodyLines, paramsStr);
            if (category == StubCategory.None) continue;
            if (stubOk) continue; // STUB_OK annotation suppresses this finding

            yield return new StubMatch(name, i, stubOk, category, line.Trim());
        }
    }

    // ── C# ────────────────────────────────────────────────────────────────────

    internal static IEnumerable<StubMatch> FindCSharpStubs(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = CSharpMethodHeader.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var paramsStr = m.Groups["params"].Value;

            // Skip abstract / extern declarations (no body — signature ends with `;`)
            if (line.TrimEnd().EndsWith(';')) continue;
            // Skip any line that explicitly declares abstract (no body)
            if (line.Contains(" abstract ", StringComparison.Ordinal) ||
                line.Contains("\tabstract ", StringComparison.Ordinal)) continue;

            var (bodyLines, endLine) = ExtractBraceBody(lines, i);
            if (endLine < 0) continue;

            bool stubOk = HasStubOkAnnotation(lines, i, bodyLines);
            var category = ClassifyBraceBody(bodyLines, paramsStr);
            if (category == StubCategory.None) continue;
            if (stubOk) continue; // STUB_OK annotation suppresses this finding

            yield return new StubMatch(name, i, stubOk, category, line.Trim());
        }
    }

    // ── Python ────────────────────────────────────────────────────────────────

    internal static IEnumerable<StubMatch> FindPythonStubs(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = PythonDefHeader.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var paramsStr = m.Groups["params"].Value;
            var defIndent = m.Groups["indent"].Value.Length;

            // Handle inline body: `def f(): pass` or `def f(): ...`
            var afterColon = line[(m.Index + m.Length)..].Trim();
            List<string> bodyLines;
            if (afterColon.Length > 0)
            {
                bodyLines = new List<string> { afterColon };
            }
            else
            {
                // Multi-line indented body
                bodyLines = new List<string>();
                for (int j = i + 1; j < lines.Length && j < i + MaxBodyScanLines; j++)
                {
                    var bl = lines[j];
                    if (string.IsNullOrWhiteSpace(bl)) { bodyLines.Add(bl); continue; }
                    var blIndent = bl.Length - bl.TrimStart().Length;
                    if (blIndent <= defIndent) break; // back to same or outer indent
                    bodyLines.Add(bl);
                }
            }

            bool stubOk = HasStubOkAnnotation(lines, i, bodyLines);
            var category = ClassifyPythonBody(bodyLines, paramsStr);
            if (category == StubCategory.None) continue;
            if (stubOk) continue; // STUB_OK annotation suppresses this finding

            yield return new StubMatch(name, i, stubOk, category, line.Trim());
        }
    }

    // ── Go ────────────────────────────────────────────────────────────────────

    internal static IEnumerable<StubMatch> FindGoStubs(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = GoFuncHeader.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var paramsStr = m.Groups["params"].Value;

            var (bodyLines, endLine) = ExtractBraceBody(lines, i);
            if (endLine < 0) continue;

            bool stubOk = HasStubOkAnnotation(lines, i, bodyLines);
            var category = ClassifyBraceBody(bodyLines, paramsStr);
            if (category == StubCategory.None) continue;
            if (stubOk) continue; // STUB_OK annotation suppresses this finding

            yield return new StubMatch(name, i, stubOk, category, line.Trim());
        }
    }

    // ── Body extraction ───────────────────────────────────────────────────────

    /// <summary>
    /// Extracts inner body lines between the first <c>{</c> and the matching <c>}</c>.
    /// The line that contains the opening brace (the function signature line) is NOT included
    /// in the returned body — only lines AFTER it are collected.
    /// Returns <c>endLine = -1</c> if no complete body was found within <see cref="MaxBodyScanLines"/>.
    /// Single-line string literals are tracked to avoid counting braces inside strings.
    /// </summary>
    internal static (List<string> BodyLines, int EndLine) ExtractBraceBody(string[] lines, int fromLine)
    {
        int depth = 0;
        bool bodyStarted = false;
        int openBraceLineIndex = -1; // the line index that contains the first opening brace
        var bodyLines = new List<string>();
        int cap = Math.Min(lines.Length, fromLine + MaxBodyScanLines);

        for (int i = fromLine; i < cap; i++)
        {
            var line = lines[i];
            bool inString = false;
            char stringChar = '\0';

            for (int ci = 0; ci < line.Length; ci++)
            {
                char c = line[ci];
                // Minimal single-line string tracking to avoid false brace counts.
                if (!inString && (c == '"' || c == '\'') && (ci == 0 || line[ci - 1] != '\\'))
                {
                    inString = true;
                    stringChar = c;
                }
                else if (inString && c == stringChar && (ci == 0 || line[ci - 1] != '\\'))
                {
                    inString = false;
                }
                else if (!inString)
                {
                    if (c == '{')
                    {
                        depth++;
                        if (!bodyStarted)
                        {
                            bodyStarted = true;
                            openBraceLineIndex = i; // record which line opened the body
                        }
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (bodyStarted && depth == 0) return (bodyLines, i);
                    }
                }
            }

            // Collect inner body lines: only lines strictly AFTER the opening-brace line.
            if (bodyStarted && depth > 0 && i != openBraceLineIndex)
                bodyLines.Add(line);
        }

        return (bodyLines, -1);
    }

    // ── Body classification ───────────────────────────────────────────────────

    internal static StubCategory ClassifyBraceBody(List<string> bodyLines, string paramsStr)
    {
        var nonEmpty = bodyLines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        bool hasUnderscoreParam = HasUnderscoreParams(paramsStr);

        // Cat-D or Cat-E: completely empty body
        if (nonEmpty.Count == 0)
            return hasUnderscoreParam ? StubCategory.CatE : StubCategory.CatD;

        // All non-empty lines must be comment lines for any stub classification
        if (!nonEmpty.All(IsCommentLine)) return StubCategory.None;

        // Cat-E: underscore params + any comment-only body, regardless of comment content.
        // Canonical case: `_scene: any` + `/* to be completed when EventBus exists */`
        if (hasUnderscoreParam)
            return StubCategory.CatE;

        // Cat-A: comment-only body with at least one stub-pattern comment (no underscore params)
        if (nonEmpty.Any(l => StubCommentPattern.IsMatch(l)))
            return StubCategory.CatA;

        return StubCategory.None;
    }

    internal static StubCategory ClassifyPythonBody(List<string> bodyLines, string paramsStr)
    {
        var nonEmpty = bodyLines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        bool hasUnderscoreParam = HasUnderscoreParams(paramsStr);

        if (nonEmpty.Count == 0)
            return hasUnderscoreParam ? StubCategory.CatE : StubCategory.CatD;

        // All lines must be `pass`, `...` (ellipsis), or comment lines.
        if (!nonEmpty.All(l => l is "pass" or "..." || IsCommentLine(l)))
            return StubCategory.None;

        bool hasStubComment = nonEmpty.Any(l => IsCommentLine(l) && StubCommentPattern.IsMatch(l));

        if (hasStubComment)
            return hasUnderscoreParam ? StubCategory.CatE : StubCategory.CatA;

        // Only pass/... (and possibly non-stub comments) → Cat-D
        if (nonEmpty.Any(l => l is "pass" or "..."))
            return hasUnderscoreParam ? StubCategory.CatE : StubCategory.CatD;

        return StubCategory.None;
    }

    private static bool HasUnderscoreParams(string paramsStr)
        => UnderscoreParamPattern.IsMatch(paramsStr);

    private static bool IsCommentLine(string trimmedLine)
        => trimmedLine.StartsWith("//") || trimmedLine.StartsWith("*")
        || trimmedLine.StartsWith("/*") || trimmedLine.StartsWith("*/")
        || trimmedLine.StartsWith("#");

    private static bool HasStubOkAnnotation(string[] lines, int headerLine, List<string> bodyLines)
    {
        // Check 3 preceding lines
        for (int i = Math.Max(0, headerLine - 3); i < headerLine; i++)
            if (StubOkPattern.IsMatch(lines[i])) return true;

        // Check the function body
        foreach (var bl in bodyLines)
            if (StubOkPattern.IsMatch(bl)) return true;

        return false;
    }

    // ── File enumeration ──────────────────────────────────────────────────────

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
                if (Array.IndexOf(SupportedExtensions, ext) >= 0)
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

    // ── Internal model ────────────────────────────────────────────────────────

    internal enum StubCategory { None, CatA, CatD, CatE }

    internal sealed record StubMatch(
        string FunctionName,
        int HeaderLine,
        bool HasStubOk,
        StubCategory Category,
        string SignatureSnippet);
}
