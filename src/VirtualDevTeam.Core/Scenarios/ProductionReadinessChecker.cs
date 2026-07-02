using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Verdict for a single production-readiness checklist item.
/// </summary>
public enum CheckStatus { Pass, Fail, Skip }

/// <summary>
/// A single checklist item result from <see cref="ProductionReadinessChecker"/>.
/// </summary>
public record ChecklistItem(string Id, string Name, CheckStatus Status, string? Reason);

/// <summary>
/// Aggregated result of the production-readiness check.
/// </summary>
public record ProductionReadinessReport(IReadOnlyList<ChecklistItem> Items)
{
    /// <summary>True when every item is either Pass or Skip — no Fails.</summary>
    public bool AllPassed => Items.All(i => i.Status is CheckStatus.Pass or CheckStatus.Skip);
    public int PassCount => Items.Count(i => i.Status == CheckStatus.Pass);
    public int FailCount => Items.Count(i => i.Status == CheckStatus.Fail);
    public int SkipCount => Items.Count(i => i.Status == CheckStatus.Skip);
}

/// <summary>
/// Deterministic (no-LLM) production-readiness checker for the 15-item checklist
/// evaluated at the Completion gate. Each item returns Pass / Fail / Skip — skip when
/// the prerequisite artifact is absent so partial projects don't block unnecessarily.
/// </summary>
public sealed class ProductionReadinessChecker
{
    // ── Cat-A stub keyword pattern (mirrors StubFunctionBodyDetector exactly) ─
    private static readonly Regex StubCatAPattern = new(
        @"\b(stub|placeholder|no.?op|wip|draft|for\s+now|to\s+be\s+(wired|completed)|integration\s+point|TBD|TODO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── STUB_OK escape hatch ───────────────────────────────────────────────────
    private static readonly Regex StubOkPattern = new(
        @"(?://|#)\s*STUB_OK\s*:",
        RegexOptions.Compiled);

    // ── Merge-conflict markers ─────────────────────────────────────────────────
    private static readonly Regex MergeConflictPattern = new(
        @"^(<{7}|={7}|>{7})\s",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // ── Hardcoded secret heuristics ────────────────────────────────────────────
    // Matches: keyword=<quoted-literal> where the value is ≥6 chars and enclosed in quotes.
    // Env-var references (process.env.X, ${VAR}) aren't quoted in this form, so they're
    // naturally excluded. Template placeholders like 'your-secret-here' are caught but can
    // be accepted — it's better to over-flag than to miss real secrets.
    private static readonly Regex SecretPattern = new(
        @"\b(password|api[_-]?key|apikey|secret|auth[_-]?token)\s*[=:]\s*[""'][^""'\r\n]{6,}[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Debug leak patterns ────────────────────────────────────────────────────
    private static readonly Regex DebugLeakPattern = new(
        @"console\.log\(|debugger;|System\.Diagnostics\.Debug\.Write",
        RegexOptions.Compiled);

    // ── File/dir exclusions (same as StubFunctionBodyDetector) ────────────────
    private static readonly string[] ExcludedDirSegments =
    [
        "/.git/", "/node_modules/", "/.candidates/", "/bin/", "/obj/",
        "/dist/", "/build/", "/.next/", "/.cache/", "/__pycache__/",
        "/vendor/", "/target/", "/strategy-artifacts/",
    ];

    private static readonly string[] SourceExtensions =
    [
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".cs", ".py", ".go",
    ];

    private static readonly string[] TestFilePatterns =
    [
        ".test.ts", ".spec.ts", ".test.js", ".spec.js",
        "Tests.cs", "Test.cs", "_test.go",
    ];

    private static readonly string[] ConfigExtensions = [".json", ".yaml", ".yml", ".env"];

    private const int MaxFilesScanned = 3000;

    private readonly ILogger<ProductionReadinessChecker> _logger;
    private readonly AgentStateStore? _stateStore;

    /// <param name="logger">Structured logger.</param>
    /// <param name="stateStore">Optional — required only for check #14 (FlowFindings). Pass null to Skip that check.</param>
    public ProductionReadinessChecker(
        ILogger<ProductionReadinessChecker> logger,
        AgentStateStore? stateStore = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _stateStore = stateStore;
    }

    /// <summary>
    /// Runs all 15 checklist items and returns the aggregated report.
    /// </summary>
    /// <param name="workspaceRoot">Root of the generated workspace (e.g. <c>.agents/software-engineer-1/&lt;repo&gt;</c>).
    /// When null or non-existent, all file-scanning checks return Skip.</param>
    /// <param name="scenarios">Scenario registry — used for emitter/subscriber validation. May be null.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ProductionReadinessReport> CheckAsync(
        string? workspaceRoot,
        IScenarioRegistry? scenarios,
        CancellationToken ct = default)
    {
        var items = new List<ChecklistItem>();

        bool hasWorkspace = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot);

        // Read Architecture.md and PMSpec.md once — skip if workspace absent.
        string? archContent = hasWorkspace ? TryReadFile(Path.Combine(workspaceRoot!, "Architecture.md")) : null;
        string? pmSpecContent = hasWorkspace ? TryReadFile(Path.Combine(workspaceRoot!, "PMSpec.md")) : null;

        // 1. Build success
        items.Add(CheckBuildSuccess(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 2. Smoke test pass
        items.Add(CheckSmokeTestPass(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 3. No Cat-A stubs
        items.Add(CheckNoCatAStubs(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 4. No missing assets
        items.Add(CheckNoMissingAssets(workspaceRoot, hasWorkspace, pmSpecContent));
        ct.ThrowIfCancellationRequested();

        // 5. All features registered (emitters)
        items.Add(CheckAllFeaturesRegistered(workspaceRoot, hasWorkspace, archContent));
        ct.ThrowIfCancellationRequested();

        // 6. Event wiring valid (subscribers)
        items.Add(CheckEventWiringValid(workspaceRoot, hasWorkspace, archContent));
        ct.ThrowIfCancellationRequested();

        // 7. Test coverage exists
        items.Add(CheckTestCoverageExists(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 8. No compiler warnings
        items.Add(CheckNoCompilerWarnings(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 9. No debug leaks
        items.Add(CheckNoDebugLeaks(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 10. Config completeness
        items.Add(CheckConfigCompleteness(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 11. Screenshots non-empty
        items.Add(CheckScreenshotsNonEmpty(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 12. Smoke spec present
        items.Add(CheckSmokeSpecPresent(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 13. Integration PR clean (no merge-conflict markers)
        items.Add(CheckIntegrationPrClean(workspaceRoot, hasWorkspace));
        ct.ThrowIfCancellationRequested();

        // 14. No unresolved FlowFindings
        items.Add(await CheckNoUnresolvedFlowFindingsAsync(ct));
        ct.ThrowIfCancellationRequested();

        // 15. No security ship-blockers
        items.Add(CheckNoSecurityShipBlockers(workspaceRoot, hasWorkspace));

        var report = new ProductionReadinessReport(items.AsReadOnly());
        _logger.LogInformation(
            "ProductionReadinessChecker complete — Pass:{Pass} Fail:{Fail} Skip:{Skip} AllPassed:{AllPassed}",
            report.PassCount, report.FailCount, report.SkipCount, report.AllPassed);
        return report;
    }

    // ── Check implementations ─────────────────────────────────────────────────

    private ChecklistItem CheckBuildSuccess(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "build-success";
        const string name = "Build success";

        if (!hasWorkspace)
            return Skip(id, name, "No workspace directory");

        var markerPaths = new[]
        {
            Path.Combine(workspaceRoot!, ".build-result.json"),
            Path.Combine(workspaceRoot!, "build-result.json"),
            Path.Combine(workspaceRoot!, ".build-success"),
        };

        foreach (var path in markerPaths)
        {
            if (!File.Exists(path)) continue;
            var content = TryReadFile(path) ?? string.Empty;
            // A .build-result.json with "success": true, or a .build-success sentinel file.
            if (Path.GetExtension(path) == ".json")
            {
                if (content.Contains("\"success\": true", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase))
                    return Pass(id, name, $"Found build result marker: {Path.GetFileName(path)}");
                // If JSON exists but doesn't say success, it's a fail.
                return Fail(id, name, $"Build result marker {Path.GetFileName(path)} does not indicate success");
            }
            // Sentinel file — existence = success.
            return Pass(id, name, $"Found build success sentinel: {Path.GetFileName(path)}");
        }

        // Also check for any *.log file containing "Build succeeded" phrase (dotnet / MSBuild output).
        var logs = Directory.EnumerateFiles(workspaceRoot!, "*.log", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .Take(20);

        foreach (var log in logs)
        {
            var content = TryReadFile(log) ?? string.Empty;
            if (content.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Successfully compiled", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("webpack compiled successfully", StringComparison.OrdinalIgnoreCase))
                return Pass(id, name, $"Build success phrase found in {Path.GetFileName(log)}");
            if (content.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("error TS", StringComparison.Ordinal) ||
                content.Contains("CompilationError", StringComparison.OrdinalIgnoreCase))
                return Fail(id, name, $"Build failure phrase found in {Path.GetFileName(log)}");
        }

        return Skip(id, name, "No build result marker or log found");
    }

    private static ChecklistItem CheckSmokeTestPass(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "smoke-test-pass";
        const string name = "Smoke test pass";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        var smokeFile = FindFile(workspaceRoot!, "playwright-smoke.spec.ts");
        if (smokeFile is null)
            return Skip(id, name, "playwright-smoke.spec.ts not found — check item #12 instead");

        // Look for a test-results JSON for the smoke spec.
        var resultsDir = Path.Combine(workspaceRoot!, "test-results");
        if (!Directory.Exists(resultsDir))
            return Skip(id, name, "Smoke spec found but no test-results directory");

        var resultFiles = Directory.EnumerateFiles(resultsDir, "*.json", SearchOption.AllDirectories)
            .Take(50).ToList();

        foreach (var rf in resultFiles)
        {
            var content = TryReadFile(rf) ?? string.Empty;
            if (!content.Contains("playwright-smoke", StringComparison.OrdinalIgnoreCase)) continue;
            if (content.Contains("\"status\": \"passed\"", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("\"status\":\"passed\"", StringComparison.OrdinalIgnoreCase))
                return Pass(id, name, "Smoke test result: passed");
            if (content.Contains("\"status\": \"failed\"", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("\"status\":\"failed\"", StringComparison.OrdinalIgnoreCase))
                return Fail(id, name, "Smoke test result: failed");
        }

        return Skip(id, name, "Smoke spec found but no matching result JSON");
    }

    private ChecklistItem CheckNoCatAStubs(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "no-cat-a-stubs";
        const string name = "No Cat-A stubs";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        int stubsFound = 0;
        string? firstExample = null;

        foreach (var file in EnumerateSourceFiles(workspaceRoot!))
        {
            if (IsTestFile(file)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Skip lines with STUB_OK
                if (StubOkPattern.IsMatch(line)) continue;
                // Must be inside a comment-like context to be Cat-A stub content
                var trimmed = line.TrimStart();
                bool isCommentLine = trimmed.StartsWith("//", StringComparison.Ordinal) ||
                                     trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                                     trimmed.StartsWith("*", StringComparison.Ordinal) ||
                                     trimmed.StartsWith("#", StringComparison.Ordinal);
                if (!isCommentLine) continue;
                if (!StubCatAPattern.IsMatch(line)) continue;

                // Check if the preceding 3 lines have STUB_OK
                bool exempted = false;
                for (int k = Math.Max(0, i - 3); k < i; k++)
                    if (StubOkPattern.IsMatch(lines[k])) { exempted = true; break; }
                if (exempted) continue;

                stubsFound++;
                if (firstExample is null)
                {
                    var relPath = Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/');
                    firstExample = $"{relPath}:{i + 1} — {line.Trim()}";
                }
                if (stubsFound >= 10) goto doneScanningStubs;
            }
        }

        doneScanningStubs:
        if (stubsFound == 0)
            return Pass(id, name, "No Cat-A stub comments detected");

        return Fail(id, name, $"{stubsFound} Cat-A stub(s) found. First: {firstExample}");
    }

    private static ChecklistItem CheckNoMissingAssets(string? workspaceRoot, bool hasWorkspace, string? pmSpecContent)
    {
        const string id = "no-missing-assets";
        const string name = "No missing assets";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");
        if (string.IsNullOrWhiteSpace(pmSpecContent))
            return Skip(id, name, "PMSpec.md not found");

        // Extract # image-deliverables block.
        var deliverablesMatch = Regex.Match(pmSpecContent,
            @"#\s*image-deliverables\s*\r?\n(.*?)(?=\r?\n#|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (!deliverablesMatch.Success)
            return Skip(id, name, "No # image-deliverables block in PMSpec");

        var block = deliverablesMatch.Groups[1].Value;
        var assetPaths = Regex.Matches(block, @"^\s*[-*]\s*(.+\.(?:png|jpg|jpeg|gif|svg|webp|ico))\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        if (assetPaths.Count == 0)
            return Skip(id, name, "# image-deliverables block found but contains no asset paths");

        var missing = new List<string>();
        foreach (var assetPath in assetPaths)
        {
            var fullPath = Path.IsPathRooted(assetPath)
                ? assetPath
                : Path.Combine(workspaceRoot!, assetPath.TrimStart('/', '\\'));

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                missing.Add(assetPath);
        }

        return missing.Count == 0
            ? Pass(id, name, $"All {assetPaths.Count} declared asset(s) present and non-empty")
            : Fail(id, name, $"{missing.Count}/{assetPaths.Count} asset(s) missing or empty: {string.Join(", ", missing.Take(3))}");
    }

    private static ChecklistItem CheckAllFeaturesRegistered(string? workspaceRoot, bool hasWorkspace, string? archContent)
    {
        const string id = "all-features-registered";
        const string name = "All features registered";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");
        if (string.IsNullOrWhiteSpace(archContent))
            return Skip(id, name, "Architecture.md not found");

        var emitters = ExtractEventCatalogEmitters(archContent);
        if (emitters.Count == 0)
            return Skip(id, name, "No ## Event Catalog section in Architecture.md");

        var missing = new List<string>();
        foreach (var emitter in emitters)
        {
            bool found = SearchFilesForPattern(workspaceRoot!, $"\\.emit(\\s*\\(", emitter);
            if (!found)
                missing.Add(emitter);
        }

        return missing.Count == 0
            ? Pass(id, name, $"All {emitters.Count} declared emitter(s) have .emit( calls")
            : Fail(id, name, $"{missing.Count}/{emitters.Count} emitter(s) have no .emit( call: {string.Join(", ", missing.Take(3))}");
    }

    private static ChecklistItem CheckEventWiringValid(string? workspaceRoot, bool hasWorkspace, string? archContent)
    {
        const string id = "event-wiring-valid";
        const string name = "Event wiring valid";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");
        if (string.IsNullOrWhiteSpace(archContent))
            return Skip(id, name, "Architecture.md not found");

        var subscribers = ExtractEventCatalogSubscribers(archContent);
        if (subscribers.Count == 0)
            return Skip(id, name, "No ## Event Catalog section with subscribers in Architecture.md");

        var unmatched = new List<string>();
        foreach (var sub in subscribers)
        {
            bool found = SearchFilesForPattern(workspaceRoot!, $"\\.emit(\\s*\\(", sub);
            if (!found)
                unmatched.Add(sub);
        }

        return unmatched.Count == 0
            ? Pass(id, name, $"All {subscribers.Count} subscriber(s) have matching emitters")
            : Fail(id, name, $"{unmatched.Count}/{subscribers.Count} subscriber(s) lack a matching emitter: {string.Join(", ", unmatched.Take(3))}");
    }

    private static ChecklistItem CheckTestCoverageExists(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "test-coverage-exists";
        const string name = "Test coverage exists";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        int testFileCount = 0;
        foreach (var file in EnumerateAllFiles(workspaceRoot!))
        {
            if (IsTestFile(file)) testFileCount++;
            if (testFileCount >= 1) break;
        }

        return testFileCount > 0
            ? Pass(id, name, "At least one test file found")
            : Fail(id, name, "No test files (.test.ts, .spec.ts, *Tests.cs, test_*.py, *_test.go) found");
    }

    private ChecklistItem CheckNoCompilerWarnings(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "no-compiler-warnings";
        const string name = "No compiler warnings";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        var logs = Directory.EnumerateFiles(workspaceRoot!, "*.log", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .Take(20);

        foreach (var log in logs)
        {
            var content = TryReadFile(log) ?? string.Empty;
            // Parse "X Warning(s)" from MSBuild output or "warning TS" count.
            var warnMatch = Regex.Match(content,
                @"(\d+)\s+Warning\(s\)",
                RegexOptions.IgnoreCase);
            if (warnMatch.Success && int.TryParse(warnMatch.Groups[1].Value, out int count))
            {
                if (count < 10)
                    return Pass(id, name, $"{count} compiler warning(s) — under threshold");
                return Fail(id, name, $"{count} compiler warning(s) — threshold is 10");
            }

            // Count "warning TS" or "warning CS" lines as a fallback.
            var warnLines = Regex.Matches(content, @"\bwarning\s+(TS|CS)\d+",
                RegexOptions.IgnoreCase).Count;
            if (warnLines > 0)
            {
                if (warnLines < 10)
                    return Pass(id, name, $"{warnLines} inline warning(s) — under threshold");
                return Fail(id, name, $"{warnLines} inline warning(s) — threshold is 10");
            }
        }

        return Skip(id, name, "No build log found");
    }

    private ChecklistItem CheckNoDebugLeaks(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "no-debug-leaks";
        const string name = "No debug leaks";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        int leaksFound = 0;
        string? firstExample = null;

        foreach (var file in EnumerateSourceFiles(workspaceRoot!))
        {
            if (IsTestFile(file)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!DebugLeakPattern.IsMatch(lines[i])) continue;
                leaksFound++;
                if (firstExample is null)
                {
                    var relPath = Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/');
                    firstExample = $"{relPath}:{i + 1}";
                }
                if (leaksFound >= 10) goto doneLeaks;
            }
        }

        doneLeaks:
        return leaksFound == 0
            ? Pass(id, name, "No console.log / debugger / Debug.Write found in source")
            : Fail(id, name, $"{leaksFound} debug statement(s) found. First: {firstExample}");
    }

    private static ChecklistItem CheckConfigCompleteness(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "config-completeness";
        const string name = "Config completeness";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        var issues = new List<string>();
        int filesScanned = 0;

        foreach (var file in EnumerateAllFiles(workspaceRoot!))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!ConfigExtensions.Contains(ext)) continue;
            // Include .env.example files by name check.
            var name2 = Path.GetFileName(file);
            if (ext == "" && !name2.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) continue;

            if (filesScanned++ > 200) break;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"\b(TODO|FIXME)\b", RegexOptions.IgnoreCase))
                {
                    var relPath = Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/');
                    issues.Add($"{relPath}:{i + 1}");
                    if (issues.Count >= 5) goto doneConfig;
                }
            }
        }

        doneConfig:
        return issues.Count == 0
            ? Pass(id, name, "No TODO/FIXME found in config files")
            : Fail(id, name, $"{issues.Count} TODO/FIXME placeholder(s) in config. First: {issues[0]}");
    }

    private static ChecklistItem CheckScreenshotsNonEmpty(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "screenshots-non-empty";
        const string name = "Screenshots non-empty";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        var screenshotDir = Path.Combine(workspaceRoot!, ".screenshots");
        if (!Directory.Exists(screenshotDir))
            return Skip(id, name, "No .screenshots/ directory");

        var pngs = Directory.EnumerateFiles(screenshotDir, "*.png", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).Length > 5 * 1024)
            .Take(1)
            .ToList();

        return pngs.Count > 0
            ? Pass(id, name, $"Found at least 1 PNG > 5KB in .screenshots/")
            : Fail(id, name, "No PNG > 5KB found in .screenshots/ — screenshots may be blank");
    }

    private static ChecklistItem CheckSmokeSpecPresent(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "smoke-spec-present";
        const string name = "Smoke spec present";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        // Primary: playwright-smoke.spec.ts
        var smokeFile = FindFile(workspaceRoot!, "playwright-smoke.spec.ts");
        if (smokeFile is not null)
            return Pass(id, name, $"playwright-smoke.spec.ts found: {Path.GetRelativePath(workspaceRoot!, smokeFile).Replace('\\', '/')}");

        // Fallback: any spec file containing "smoke" in name or content referencing scenarios.
        foreach (var file in EnumerateAllFiles(workspaceRoot!))
        {
            var fn = Path.GetFileName(file).ToLowerInvariant();
            if (!fn.EndsWith(".spec.ts") && !fn.EndsWith(".spec.js")) continue;

            if (fn.Contains("smoke"))
                return Pass(id, name, $"Smoke-named spec found: {Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/')}");

            var content = TryReadFile(file) ?? string.Empty;
            if (content.Contains("scenario", StringComparison.OrdinalIgnoreCase))
                return Pass(id, name, $"Spec referencing scenarios found: {Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/')}");
        }

        return Fail(id, name, "No playwright-smoke.spec.ts or equivalent scenario-referencing spec found");
    }

    private ChecklistItem CheckIntegrationPrClean(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "integration-pr-clean";
        const string name = "Integration PR clean";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        int conflictsFound = 0;
        string? firstExample = null;
        int filesScanned = 0;

        foreach (var file in EnumerateSourceFiles(workspaceRoot!))
        {
            if (filesScanned++ > MaxFilesScanned) break;
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }

            if (!MergeConflictPattern.IsMatch(content)) continue;

            conflictsFound++;
            if (firstExample is null)
                firstExample = Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/');
            if (conflictsFound >= 5) break;
        }

        return conflictsFound == 0
            ? Pass(id, name, "No merge-conflict markers found")
            : Fail(id, name, $"{conflictsFound} file(s) contain merge-conflict markers. First: {firstExample}");
    }

    private async Task<ChecklistItem> CheckNoUnresolvedFlowFindingsAsync(CancellationToken ct)
    {
        const string id = "no-unresolved-flow-findings";
        const string name = "No unresolved FlowFindings";

        if (_stateStore is null)
            return Skip(id, name, "AgentStateStore not provided");

        try
        {
            int criticalCount = await Task.Run(() => QueryCriticalFlowFindings(_stateStore.DatabasePath), ct)
                .ConfigureAwait(false);

            return criticalCount == 0
                ? Pass(id, name, "No unresolved Critical FlowFindings in database")
                : Fail(id, name, $"{criticalCount} unresolved Critical FlowFinding(s) in flow_findings table");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CheckNoUnresolvedFlowFindings: database query failed");
            return Skip(id, name, $"Database query failed: {ex.Message}");
        }
    }

    private ChecklistItem CheckNoSecurityShipBlockers(string? workspaceRoot, bool hasWorkspace)
    {
        const string id = "no-security-ship-blockers";
        const string name = "No security ship-blockers";

        if (!hasWorkspace) return Skip(id, name, "No workspace directory");

        int secretsFound = 0;
        string? firstExample = null;
        int filesScanned = 0;

        foreach (var file in EnumerateSourceFiles(workspaceRoot!))
        {
            if (filesScanned++ > MaxFilesScanned) break;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Skip comment-only lines — the pattern targets value assignments.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;

                if (!SecretPattern.IsMatch(line)) continue;

                secretsFound++;
                if (firstExample is null)
                {
                    var relPath = Path.GetRelativePath(workspaceRoot!, file).Replace('\\', '/');
                    firstExample = $"{relPath}:{i + 1}";
                }
                if (secretsFound >= 5) goto doneSecrets;
            }
        }

        doneSecrets:
        return secretsFound == 0
            ? Pass(id, name, "No hardcoded secret patterns detected")
            : Fail(id, name, $"{secretsFound} potential hardcoded secret(s). First: {firstExample}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ChecklistItem Pass(string id, string name, string? reason = null) =>
        new(id, name, CheckStatus.Pass, reason);

    private static ChecklistItem Fail(string id, string name, string? reason = null) =>
        new(id, name, CheckStatus.Fail, reason);

    private static ChecklistItem Skip(string id, string name, string? reason = null) =>
        new(id, name, CheckStatus.Skip, reason);

    private static string? TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static string? FindFile(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault(f => !IsExcludedPath(f));
        }
        catch { return null; }
    }

    private static bool IsExcludedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var seg in ExcludedDirSegments)
            if (normalized.Contains(seg, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsTestFile(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var pat in TestFilePatterns)
            if (name.EndsWith(pat, StringComparison.OrdinalIgnoreCase)) return true;
        // Python: test_*.py
        if (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Enumerate source code files up to <see cref="MaxFilesScanned"/>.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        int count = 0;
        foreach (var file in SafeEnumerateFiles(root))
        {
            if (count++ >= MaxFilesScanned) yield break;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!SourceExtensions.Contains(ext)) continue;
            if (IsExcludedPath(file)) continue;
            yield return file;
        }
    }

    private static IEnumerable<string> EnumerateAllFiles(string root)
    {
        int count = 0;
        foreach (var file in SafeEnumerateFiles(root))
        {
            if (count++ >= MaxFilesScanned) yield break;
            if (IsExcludedPath(file)) continue;
            yield return file;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
        {
            yield return f;
        }
    }

    private static List<string> ExtractEventCatalogEmitters(string archContent)
    {
        var emitters = new List<string>();
        var catalogSection = ExtractSection(archContent, "Event Catalog");
        if (catalogSection is null) return emitters;

        // Extract emitter names from lines like "| EventName | EmitterModule | ..."
        // or "Emitter: ModuleName" or "- emitter: ModuleName"
        foreach (Match m in Regex.Matches(catalogSection,
            @"(?:^|\|)\s*emitter\s*(?:\|?:?\s*)([A-Za-z_]\w+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name)) emitters.Add(name);
        }

        return emitters;
    }

    private static List<string> ExtractEventCatalogSubscribers(string archContent)
    {
        var subscribers = new List<string>();
        var catalogSection = ExtractSection(archContent, "Event Catalog");
        if (catalogSection is null) return subscribers;

        foreach (Match m in Regex.Matches(catalogSection,
            @"(?:^|\|)\s*subscriber[s]?\s*(?:\|?:?\s*)([A-Za-z_]\w+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name)) subscribers.Add(name);
        }

        return subscribers;
    }

    private static string? ExtractSection(string content, string heading)
    {
        var m = Regex.Match(content,
            $@"##\s*{Regex.Escape(heading)}\s*\r?\n(.*?)(?=\r?\n##|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Returns true if any source file under root contains <paramref name="pattern"/> 
    /// AND also contains <paramref name="context"/> as a nearby identifier.</summary>
    private static bool SearchFilesForPattern(string root, string pattern, string context)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled);
        var contextRegex = new Regex(Regex.Escape(context), RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (var file in EnumerateSourceFiles(root))
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }

            if (!contextRegex.IsMatch(content)) continue;
            if (regex.IsMatch(content)) return true;
        }
        return false;
    }

    private static int QueryCriticalFlowFindings(string dbPath)
    {
        if (!File.Exists(dbPath)) return 0;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // Table may not exist on a fresh DB — use a graceful fallback.
        cmd.CommandText = """
            SELECT COUNT(*) FROM flow_findings
            WHERE severity = 'Critical'
              AND state IN ('Open', 'ActedOn')
            """;

        try
        {
            var result = cmd.ExecuteScalar();
            return result is long l ? (int)l : 0;
        }
        catch (SqliteException)
        {
            // Table doesn't exist yet — no findings.
            return 0;
        }
    }
}
