using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// T1.6: Tier classification for a <see cref="FixRecommendation"/>. The tier decides how
/// the approve endpoint applies the fix:
/// <list type="bullet">
///   <item><see cref="Live"/> — file watchers / IOptionsMonitor pick up the change without
///         restart. Examples: prompt .md edits, appsettings.json keys, develop-settings.json,
///         SME definition JSONs, FlowMonitor config toggles.</item>
///   <item><see cref="DeferredRestart"/> — Copilot writes the file but the runner has to
///         restart for it to take effect. Examples: any .cs / .razor change.</item>
///   <item><see cref="Blocked"/> — cannot apply live at all; staged for the next runner
///         boot. Examples: NuGet package add, .csproj edits, package-lock.json, DB schema
///         migrations.</item>
/// </list>
/// </summary>
public enum FixTier
{
    /// <summary>No restart needed — config / prompt reload via existing watchers.</summary>
    Live,
    /// <summary>Source file change; runner restart required to activate.</summary>
    DeferredRestart,
    /// <summary>Cannot apply while runner is up; staged for next startup.</summary>
    Blocked,
}

/// <summary>
/// Result of classifying a <see cref="FixRecommendation"/>: which tier the fix lands in,
/// the list of files extracted from the plan, and a one-line rationale the operator can read
/// on the dashboard ("why is this Deferred?").
/// </summary>
public sealed record FixClassification
{
    public required FixTier Tier { get; init; }
    public required IReadOnlyList<string> AffectedFiles { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
/// Pure-function classifier: given a fix recommendation's plan markdown, decide whether the
/// fix can be applied live, requires a restart, or must be staged. Heuristics only — no AI.
///
/// Classification rules (first matching rule wins, evaluated in this order):
/// <list type="number">
///   <item>If any file matches a Blocked pattern (.csproj, package*.json, *.sql, sql migration
///         folder paths) → <see cref="FixTier.Blocked"/>.</item>
///   <item>If any file is a .cs or .razor source file (case-insensitive; .razor.cs counts as
///         DeferredRestart, NOT Live) → <see cref="FixTier.DeferredRestart"/>.</item>
///   <item>If every file is on the live-safe allowlist (prompts/**/*.md, appsettings.json,
///         develop-settings.json, prompts/sme-templates/*.json) → <see cref="FixTier.Live"/>.</item>
///   <item>Empty file list, mixed unclassifiable paths, or anything else → default to
///         <see cref="FixTier.DeferredRestart"/> for safety. The author can override by being
///         explicit in the plan.</item>
/// </list>
///
/// File path extraction is forgiving: parses inline backticks (`src/Foo.cs`), bullet lists
/// under "## Files to modify" / "Files to change:" headings, code-fence file path comments
/// (`// path/to/file.cs`), and bare relative paths. Duplicates and noise are filtered.
/// </summary>
public static class FixClassifier
{
    // Blocked patterns — these files cannot be applied while the runner is running because
    // they require either a NuGet restore (.csproj, package.json) or a DB migration apply.
    private static readonly Regex BlockedPattern = new(
        @"\.csproj\b|\bpackage(-lock)?\.json\b|\bpackages\.config\b|\.sql\b|[\\/]migrations?[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Source files: .cs and .razor (and Razor's code-behind .razor.cs handled by the .cs branch).
    // The "deferred-restart" tier exists because the runner has to recompile and reload its
    // own assembly to pick these up — Razor runtime compilation is not enabled in the project.
    private static readonly Regex DeferredPattern = new(
        @"\.cs(?:html)?\b|\.razor\b|\.vb\b|\.fs\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Live-safe paths. Each must match the WHOLE relative path (not just a substring) so that
    // a hostile path like "src/foo/prompts/Program.cs" doesn't pass through.
    private static readonly Regex[] LiveSafePatterns = new[]
    {
        // Prompt templates — PromptFileWatcher invalidates the cache automatically.
        new Regex(@"^prompts[\\/].+\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // SME templates — SMEAgentDefinitionService re-reads JSON on demand.
        new Regex(@"^prompts[\\/]sme-templates[\\/].+\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // appsettings.json + variants — bound via IOptionsMonitor, reload-on-change is on.
        new Regex(@"^(?:src[\\/][^\\/]+[\\/])?appsettings(?:\.[A-Za-z0-9_-]+)?\.json$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // develop-settings.json — RunCoordinator picks up next reconfigure cycle.
        new Regex(@"^(?:src[\\/][^\\/]+[\\/])?develop-settings\.json$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Classify a recommendation. Combines the structured <c>FilesToChange</c> field (set by
    /// the planner from a "Files to change:" line) with a deeper plan-markdown scan so we
    /// catch files mentioned only in the body. Returns a non-null result in all cases.
    /// </summary>
    public static FixClassification Classify(FixRecommendation rec)
    {
        ArgumentNullException.ThrowIfNull(rec);
        var files = ExtractFiles(rec.PlanMarkdown, rec.FilesToChange);
        return ClassifyFiles(files);
    }

    /// <summary>
    /// Tier-only classification from a pre-computed file list. Exposed for the lazy
    /// classifier path (when reading older rows whose <c>fix_tier</c> column is null) and
    /// for direct testing.
    /// </summary>
    public static FixClassification ClassifyFiles(IReadOnlyList<string> affectedFiles)
    {
        ArgumentNullException.ThrowIfNull(affectedFiles);

        // Empty file list = Blocked-default. We refuse to apply when the planner couldn't
        // identify *any* file: better to stage for next restart than to let a CLI session
        // touch arbitrary code with no scope.
        if (affectedFiles.Count == 0)
        {
            return new FixClassification
            {
                Tier = FixTier.Blocked,
                AffectedFiles = Array.Empty<string>(),
                Rationale = "No files identified in plan — cannot scope a live fix.",
            };
        }

        // Rule 1: any blocked pattern wins outright.
        var blocked = affectedFiles.Where(f => BlockedPattern.IsMatch(f)).ToArray();
        if (blocked.Length > 0)
        {
            return new FixClassification
            {
                Tier = FixTier.Blocked,
                AffectedFiles = affectedFiles,
                Rationale =
                    $"Touches dependency / migration file(s) ({string.Join(", ", blocked.Take(3))}" +
                    (blocked.Length > 3 ? $", +{blocked.Length - 3} more" : "") +
                    ") — must be staged for next startup.",
            };
        }

        // Rule 2: any C#/Razor file → restart needed. We check this BEFORE the live check
        // so that mixed paths (a .cs plus a prompt edit) still classify as DeferredRestart.
        var deferred = affectedFiles.Where(f => DeferredPattern.IsMatch(f)).ToArray();
        if (deferred.Length > 0)
        {
            return new FixClassification
            {
                Tier = FixTier.DeferredRestart,
                AffectedFiles = affectedFiles,
                Rationale =
                    $"Touches source file(s) ({string.Join(", ", deferred.Take(3))}" +
                    (deferred.Length > 3 ? $", +{deferred.Length - 3} more" : "") +
                    ") — runner restart required to activate.",
            };
        }

        // Rule 3: only proceeds if EVERY file is on the live-safe allowlist.
        if (affectedFiles.All(IsLiveSafe))
        {
            return new FixClassification
            {
                Tier = FixTier.Live,
                AffectedFiles = affectedFiles,
                Rationale = "All files are config/prompt-only — live reload via existing watchers.",
            };
        }

        // Rule 4: fallback. Some files don't match any pattern (could be an unclassifiable
        // text file). Default to DeferredRestart so the human always sees the change before
        // it goes live; restart-after-CLI is the safer side of the fence.
        return new FixClassification
        {
            Tier = FixTier.DeferredRestart,
            AffectedFiles = affectedFiles,
            Rationale = "Unclassified file types — defaulting to restart for safety.",
        };
    }

    /// <summary>
    /// Extract a deduped list of relative file paths from a plan. Combines the planner's
    /// <c>FilesToChange</c> structured line with a deeper markdown scan that catches inline
    /// code-fenced paths (e.g. `src/Foo.cs`) and bullet lists under "## Files to modify".
    /// </summary>
    public static IReadOnlyList<string> ExtractFiles(string planMarkdown, string? filesToChange)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source 1: the planner's structured "Files to change: a, b, c" line, persisted into
        // the recommendation's FilesToChange property. Most authoritative when present.
        if (!string.IsNullOrWhiteSpace(filesToChange))
        {
            foreach (var raw in filesToChange.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = NormalizePath(raw);
                if (IsPlausiblePath(path)) found.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(planMarkdown))
        {
            // Source 2: bullet items under a "Files to (modify|change|edit|update)" heading.
            // Greedy-match the section, then split on newlines and strip bullet glyphs.
            var sectionPattern = new Regex(
                @"(?im)^#{2,4}\s*Files\s+to\s+(?:modify|change|edit|update|add|create)\s*[:\-]?\s*$(?<body>(?:.|\n)*?)(?=^#{2,4}\s|\z)",
                RegexOptions.Compiled);
            foreach (Match m in sectionPattern.Matches(planMarkdown))
            {
                var body = m.Groups["body"].Value;
                foreach (var line in body.Split('\n'))
                {
                    var stripped = Regex.Replace(line, @"^[\s\-\*\+\d\.\)]+", "").Trim();
                    foreach (var token in ExtractPathTokens(stripped))
                    {
                        var path = NormalizePath(token);
                        if (IsPlausiblePath(path)) found.Add(path);
                    }
                }
            }

            // Source 3: any inline backtick-quoted token that looks like a file path with an
            // extension we recognise. This catches "see `src/Foo.cs` for the bug" prose.
            var inlinePattern = new Regex(
                @"`(?<p>[A-Za-z0-9_\-./\\]{2,200}\.(?:cs|csproj|razor|md|json|sql|js|ts|html|css|fs|vb|sh|ps1|yml|yaml|xml|config))`",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (Match m in inlinePattern.Matches(planMarkdown))
            {
                var path = NormalizePath(m.Groups["p"].Value);
                if (IsPlausiblePath(path)) found.Add(path);
            }
        }

        return found.ToArray();
    }

    private static IEnumerable<string> ExtractPathTokens(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;

        // Strip backticks and emphasis so "**`src/Foo.cs`**" becomes "src/Foo.cs".
        var cleaned = line.Replace("`", "").Replace("**", "").Replace("*", "").Trim();

        // Stop at the first " - " or " — " (em dash) — common bullet-then-comment separator.
        var dashIdx = cleaned.IndexOfAny(new[] { '—', '–' });
        if (dashIdx > 0) cleaned = cleaned.Substring(0, dashIdx).Trim();
        var commentIdx = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (commentIdx > 0) cleaned = cleaned.Substring(0, commentIdx).Trim();

        // Split on whitespace, comma, semicolon — but only emit tokens that look path-like.
        foreach (var token in cleaned.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return token;
        }
    }

    private static string NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim().Trim('`', '"', '\'', '*', '(', ')', '[', ']', '<', '>', '.');
        // Convert backslashes to forward slashes for matching consistency. The CLI itself
        // is path-separator agnostic on Windows.
        trimmed = trimmed.Replace('\\', '/');
        // Drop a leading "./".
        if (trimmed.StartsWith("./", StringComparison.Ordinal)) trimmed = trimmed.Substring(2);
        return trimmed;
    }

    private static bool IsPlausiblePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length < 2 || path.Length > 400) return false;
        // Must contain a directory separator OR an extension dot — single-word "Researcher"
        // shouldn't classify as a path.
        if (!path.Contains('/') && !path.Contains('.')) return false;
        // Reject obvious prose tokens (URLs, anchors).
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("#", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsLiveSafe(string path)
    {
        // Match against the WHOLE path with explicit anchors so partial matches don't sneak
        // through. Path is already forward-slash-normalised.
        foreach (var pattern in LiveSafePatterns)
        {
            if (pattern.IsMatch(path)) return true;
        }
        return false;
    }
}
