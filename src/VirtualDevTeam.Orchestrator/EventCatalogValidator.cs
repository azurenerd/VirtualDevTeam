using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// MissingWorkDetector: static-analysis pass that cross-references the
/// <c>## Event Catalog</c> table in <c>Architecture.md</c> against actual
/// emit/subscribe patterns in TS/JS/C# source files.
///
/// <para>
/// Detection rules:
/// <list type="bullet">
///   <item><b>Undeclared emitter</b> — code calls <c>.emit('foo')</c> or
///         <c>.publish('foo')</c> but no <c>foo</c> row exists in the catalog
///         → severity Critical (confidence 0.85).</item>
///   <item><b>Undeclared subscriber</b> — code calls <c>.on('foo')</c> or
///         <c>.subscribe('foo')</c> but no <c>foo</c> row exists in the catalog
///         → severity Important (confidence 0.70).</item>
///   <item><b>Subscriber-without-emitter</b> — catalog declares a subscriber
///         for an event but no matching <c>emit/publish</c> call is found in
///         code → severity Important (confidence 0.65).</item>
///   <item><b>Emitter-without-subscriber</b> — catalog declares an emitter for
///         an event but no matching <c>on/subscribe</c> call is found in code
///         → severity Warning (confidence 0.45).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>ARCH-CONTRACT secondary source:</b> a comment of the form
/// <c>// ARCH-CONTRACT: emits=foo</c> or <c>// ARCH-CONTRACT: subscribes=foo</c>
/// is treated as an additional declaration. When an annotation names an event
/// that lacks a catalog row, a Warning finding is emitted to encourage
/// formalising the catalog (confidence 0.50).
/// </para>
///
/// <para>
/// <b>Graceful degrade:</b> if <c>Architecture.md</c> cannot be found in the
/// workspace root (or common doc sub-paths), or if the file contains no
/// <c>## Event Catalog</c> section, the detector exits immediately with an
/// empty findings list. Most in-flight projects won't have the Event Catalog
/// section yet.
/// </para>
/// </summary>
public sealed class EventCatalogValidator : VirtualDevTeam.Core.MissingWork.IMissingWorkDetector
{
    public string DetectorId => "event-catalog";

    private readonly ILogger<EventCatalogValidator> _logger;

    public EventCatalogValidator(ILogger<EventCatalogValidator> logger)
    {
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Regex — emit/publish patterns (TS/JS + C#, single or double quotes)
    // Captures the event-name string literal in group 1.
    // -------------------------------------------------------------------------
    private static readonly Regex EmitPattern = new(
        @"(?:eventBus\.|bus\.|\bthis\.)?(?:emit|publish)\s*\(\s*['""]([^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubscribePattern = new(
        @"(?:eventBus\.|bus\.|\bthis\.)?(?:\bon\b|subscribe)\s*\(\s*['""]([^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ARCH-CONTRACT secondary-source annotations.
    private static readonly Regex ArchContractEmit = new(
        @"//\s*ARCH-CONTRACT:\s*emits=(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ArchContractSubscribe = new(
        @"//\s*ARCH-CONTRACT:\s*subscribes=(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Architecture.md lookup: separator in markdown tables.
    private static readonly Regex TableSeparatorRow = new(
        @"^\s*\|[\s\-:]+\|", RegexOptions.Compiled);

    private static readonly string[] CodeFileExtensions = new[]
    {
        ".ts", ".tsx", ".js", ".jsx", ".cs",
    };

    private static readonly string[] ExcludedDirSegments = new[]
    {
        "/.git/", "/node_modules/", "/.candidates/", "/bin/", "/obj/",
        "/dist/", "/build/", "/.next/", "/.cache/", "/__pycache__/",
        "/vendor/", "/target/", "/strategy-artifacts/",
    };

    // -------------------------------------------------------------------------
    // Public IMissingWorkDetector entry point
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>> DetectAsync(
        VirtualDevTeam.Core.MissingWork.MissingWorkContext ctx, CancellationToken ct)
    {
        var findings = new List<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>();
        try
        {
            if (!Directory.Exists(ctx.WorkspaceRoot))
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);

            var archFile = FindArchitectureFile(ctx.WorkspaceRoot);
            if (archFile is null)
            {
                _logger.LogDebug(
                    "EventCatalogValidator: Architecture.md not found in {Root} — skipping",
                    ctx.WorkspaceRoot);
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
            }

            string archContent;
            try { archContent = File.ReadAllText(archFile); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventCatalogValidator: could not read {File}", archFile);
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
            }

            var catalog = ParseEventCatalog(archContent);
            if (catalog is null)
            {
                _logger.LogDebug(
                    "EventCatalogValidator: no ## Event Catalog section found in {File} — skipping",
                    archFile);
                return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
            }

            _logger.LogDebug(
                "EventCatalogValidator: loaded {Count} catalog entries from {File}",
                catalog.Count, archFile);

            // Build lookup dictionaries (event name → catalog entry, normalized).
            var catalogByName = catalog.ToDictionary(e => e.EventName, StringComparer.OrdinalIgnoreCase);

            // Collect all code-level emit/subscribe references.
            var codeEmits = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
            var codeSubscribes = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
            var archContractEmits = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
            var archContractSubscribes = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in EnumerateCodeFiles(ctx.WorkspaceRoot, ct))
            {
                if (ct.IsCancellationRequested) break;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                var relPath = Path.GetRelativePath(ctx.WorkspaceRoot, file).Replace('\\', '/');
                ScanFileForEventRefs(lines, relPath, codeEmits, codeSubscribes,
                    archContractEmits, archContractSubscribes);
            }

            // Merge ARCH-CONTRACT annotations into code collections as additional declared evidence.
            foreach (var (name, refs) in archContractEmits)
                MergeRefs(codeEmits, name, refs);
            foreach (var (name, refs) in archContractSubscribes)
                MergeRefs(codeSubscribes, name, refs);

            // Rule 1: undeclared emitter — code emits event not in catalog.
            foreach (var (eventName, refs) in codeEmits)
            {
                if (catalogByName.ContainsKey(eventName)) continue;

                // If the ref came ONLY from ARCH-CONTRACT annotations, emit a Warning
                // that the annotation precedes catalog, not a Critical.
                bool annotationOnly = refs.All(r => r.IsAnnotation);
                if (annotationOnly)
                {
                    EmitFinding(findings,
                        "undeclared-annotation-emit", eventName,
                        $"ARCH-CONTRACT annotation declares emitter for '{eventName}' but no Event Catalog row found — add a catalog entry",
                        0.50, refs.Take(MaxEvidence).ToList());
                }
                else
                {
                    EmitFinding(findings,
                        "undeclared-emit", eventName,
                        $"Event '{eventName}' is emitted in code (.emit/.publish) but has no entry in the Architecture.md Event Catalog",
                        0.85, refs.Take(MaxEvidence).ToList());
                }
            }

            // Rule 2: undeclared subscriber — code subscribes to event not in catalog.
            foreach (var (eventName, refs) in codeSubscribes)
            {
                if (catalogByName.ContainsKey(eventName)) continue;
                bool annotationOnly = refs.All(r => r.IsAnnotation);
                if (annotationOnly)
                {
                    EmitFinding(findings,
                        "undeclared-annotation-subscribe", eventName,
                        $"ARCH-CONTRACT annotation declares subscriber for '{eventName}' but no Event Catalog row found — add a catalog entry",
                        0.50, refs.Take(MaxEvidence).ToList());
                }
                else
                {
                    EmitFinding(findings,
                        "undeclared-subscribe", eventName,
                        $"Event '{eventName}' is subscribed to in code (.on/.subscribe) but has no entry in the Architecture.md Event Catalog",
                        0.70, refs.Take(MaxEvidence).ToList());
                }
            }

            // Rule 3: subscriber-without-emitter — catalog has subscriber but no code emitter.
            foreach (var entry in catalog.Where(e => e.Subscribers.Count > 0))
            {
                if (codeEmits.ContainsKey(entry.EventName)) continue;
                EmitFinding(findings,
                    "subscriber-no-emitter", entry.EventName,
                    $"Event '{entry.EventName}' has declared subscribers in the catalog but no emitter (.emit/.publish) was found in code (could be a dangling subscription or external emitter)",
                    0.65, new List<CodeEventRef>());
            }

            // Rule 4: emitter-without-subscriber — catalog has emitter but no code subscriber.
            foreach (var entry in catalog.Where(e => e.Emitters.Count > 0))
            {
                if (codeSubscribes.ContainsKey(entry.EventName)) continue;
                EmitFinding(findings,
                    "emitter-no-subscriber", entry.EventName,
                    $"Event '{entry.EventName}' has a declared emitter in the catalog but no subscriber (.on/.subscribe) was found in code (could be telemetry / external consumer)",
                    0.45, new List<CodeEventRef>());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EventCatalogValidator tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<VirtualDevTeam.Core.MissingWork.MissingWorkFinding>>(findings);
    }

    // -------------------------------------------------------------------------
    // Internal helpers (internal for testability)
    // -------------------------------------------------------------------------

    private const int MaxEvidence = 8;

    /// <summary>
    /// Finds Architecture.md in common locations within the workspace.
    /// Returns the absolute path, or null if not found.
    /// </summary>
    internal static string? FindArchitectureFile(string workspaceRoot)
    {
        // 1. Repo root (most common for the VDT pipeline)
        var rootCandidate = Path.Combine(workspaceRoot, "Architecture.md");
        if (File.Exists(rootCandidate)) return rootCandidate;

        // 2. AgentDocs/{id}/Architecture.md — single-level wildcard
        var agentDocs = Path.Combine(workspaceRoot, "AgentDocs");
        if (Directory.Exists(agentDocs))
        {
            foreach (var sub in Directory.EnumerateDirectories(agentDocs))
            {
                var candidate = Path.Combine(sub, "Architecture.md");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3. docs/ sub-folder
        var docsCandidate = Path.Combine(workspaceRoot, "docs", "Architecture.md");
        if (File.Exists(docsCandidate)) return docsCandidate;

        return null;
    }

    /// <summary>
    /// Parses the <c>## Event Catalog</c> table from <paramref name="markdown"/>.
    /// Returns <c>null</c> if the section does not exist — the caller should no-op in that case.
    /// Returns an empty list if the section exists but contains no data rows.
    /// </summary>
    /// <remarks>
    /// Expected table structure (columns are positional):
    /// <code>
    /// ## Event Catalog
    /// | Event | Emitter(s) | Subscriber(s) |
    /// |-------|------------|---------------|
    /// | user:login | auth-service | notification, analytics |
    /// </code>
    /// Column 0 = event name. Column 1 = emitters (comma-separated). Column 2 = subscribers (comma-separated).
    /// </remarks>
    internal static List<CatalogEntry>? ParseEventCatalog(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var entries = new List<CatalogEntry>();
        var lines = markdown.Split('\n');
        bool inSection = false;
        bool pastHeader = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (!inSection)
            {
                // Detect "## Event Catalog" heading (any level 2 heading containing these words)
                if (Regex.IsMatch(line, @"^##\s+Event\s+Catalog", RegexOptions.IgnoreCase))
                    inSection = true;
                continue;
            }

            // Stop at the next heading of the same or higher level
            if (Regex.IsMatch(line, @"^##\s", RegexOptions.IgnoreCase) && !line.TrimStart().StartsWith("### "))
                break;

            var trimmed = line.Trim();

            // Must be a table row
            if (!trimmed.StartsWith("|")) continue;

            // Skip separator rows (|---|---|)
            if (TableSeparatorRow.IsMatch(trimmed))
            {
                pastHeader = true;
                continue;
            }

            // Skip header row (before the separator)
            if (!pastHeader) continue;

            var cells = SplitTableRow(trimmed);
            if (cells.Count < 1) continue;

            var eventName = cells[0].Trim();
            if (string.IsNullOrWhiteSpace(eventName) || eventName.StartsWith("_")) continue;

            var emitters = cells.Count > 1
                ? SplitCellList(cells[1])
                : new List<string>();
            var subscribers = cells.Count > 2
                ? SplitCellList(cells[2])
                : new List<string>();

            entries.Add(new CatalogEntry(eventName, emitters, subscribers));
        }

        // Return null if the section was never entered (not found), else return entries (possibly empty).
        return inSection ? entries : null;
    }

    /// <summary>
    /// Scans a single file's lines for emit/subscribe patterns and ARCH-CONTRACT annotations,
    /// appending found references to the provided dictionaries.
    /// </summary>
    internal static void ScanFileForEventRefs(
        string[] lines,
        string relPath,
        Dictionary<string, List<CodeEventRef>> emits,
        Dictionary<string, List<CodeEventRef>> subscribes,
        Dictionary<string, List<CodeEventRef>> annotationEmits,
        Dictionary<string, List<CodeEventRef>> annotationSubscribes)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Code-level emit patterns
            foreach (Match m in EmitPattern.Matches(line))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    AddRef(emits, name, new CodeEventRef(name, relPath, i + 1, TrimSnippet(line), false));
            }

            // Code-level subscribe patterns
            foreach (Match m in SubscribePattern.Matches(line))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    AddRef(subscribes, name, new CodeEventRef(name, relPath, i + 1, TrimSnippet(line), false));
            }

            // ARCH-CONTRACT: emits=eventName
            foreach (Match m in ArchContractEmit.Matches(line))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    AddRef(annotationEmits, name, new CodeEventRef(name, relPath, i + 1, TrimSnippet(line), true));
            }

            // ARCH-CONTRACT: subscribes=eventName
            foreach (Match m in ArchContractSubscribe.Matches(line))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    AddRef(annotationSubscribes, name, new CodeEventRef(name, relPath, i + 1, TrimSnippet(line), true));
            }
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static void EmitFinding(
        List<VirtualDevTeam.Core.MissingWork.MissingWorkFinding> findings,
        string rule,
        string eventName,
        string summary,
        double confidence,
        List<CodeEventRef> refs)
    {
        var evidence = refs.Select(r => new VirtualDevTeam.Core.MissingWork.EvidenceCitation
        {
            FilePath = r.FilePath,
            LineNumber = r.LineNumber,
            Snippet = r.Snippet,
            Kind = r.IsAnnotation ? "arch-contract" : "code-ref",
        }).ToList();

        findings.Add(new VirtualDevTeam.Core.MissingWork.MissingWorkFinding
        {
            Id = Guid.NewGuid().ToString("N"),
            DetectorId = "event-catalog",
            Pattern = eventName,
            Summary = summary,
            Evidence = evidence,
            Confidence = confidence,
            DedupKey = $"event-catalog:{rule}:{HashShort(eventName)}",
        });
    }

    private static void AddRef(
        Dictionary<string, List<CodeEventRef>> dict,
        string key,
        CodeEventRef @ref)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<CodeEventRef>();
            dict[key] = list;
        }
        if (list.Count < MaxEvidence)
            list.Add(@ref);
    }

    private static void MergeRefs(
        Dictionary<string, List<CodeEventRef>> target,
        string key,
        List<CodeEventRef> refs)
    {
        foreach (var r in refs)
            AddRef(target, key, r);
    }

    private static List<string> SplitTableRow(string row)
    {
        // Strip leading/trailing pipes and split on remaining |
        var inner = row.Trim('|').Trim();
        return inner.Split('|').Select(c => c.Trim()).ToList();
    }

    private static List<string> SplitCellList(string cell)
    {
        return cell.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "-" && s != "—" && s != "N/A")
            .ToList();
    }

    private static string TrimSnippet(string line)
    {
        var t = line.Trim();
        return t.Length <= 200 ? t : t[..200] + "…";
    }

    private static string HashShort(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
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
                var normalized = sub.Replace('\\', '/') + "/";
                bool excluded = false;
                foreach (var seg in ExcludedDirSegments)
                {
                    if (normalized.Contains(seg, StringComparison.OrdinalIgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (!excluded) stack.Push(sub);
            }
        }
    }
}

// -------------------------------------------------------------------------
// Supporting types (internal to the validator)
// -------------------------------------------------------------------------

/// <summary>An entry parsed from the ## Event Catalog table in Architecture.md.</summary>
internal sealed record CatalogEntry(string EventName, List<string> Emitters, List<string> Subscribers);

/// <summary>A code-level emit or subscribe reference found by the scanner.</summary>
internal sealed record CodeEventRef(
    string EventName,
    string FilePath,
    int LineNumber,
    string Snippet,
    bool IsAnnotation);
