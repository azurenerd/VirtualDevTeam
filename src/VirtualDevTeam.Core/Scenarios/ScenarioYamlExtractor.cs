using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Extracts and parses <see cref="Scenario"/> objects from a PMSpec.md document or a raw
/// YAML string, following the same <c># scenarios</c> marker convention used by the
/// <c>[image-deliverables]</c> pattern in PMSpec.
/// </summary>
/// <remarks>
/// <para>
/// The PM agent embeds a <c># scenarios</c> YAML block inside <c>PMSpec.md</c> under a
/// <c>## Scenarios</c> section. The marker line <c># scenarios</c> (case-insensitive) separates
/// the human-readable narrative from the machine-readable YAML list. The YAML may optionally
/// be wrapped in a ` ```yaml ``` ` code fence.
/// </para>
/// <para>
/// This extractor is a hand-rolled parser tuned for the specific structure the PM agent emits.
/// It does NOT require YamlDotNet or any external package — only <c>System.Text.RegularExpressions</c>.
/// </para>
/// </remarks>
public static class ScenarioYamlExtractor
{
    private const string ScenariosMarker = "# scenarios";

    // Matches the start of a code fence: ```yaml or ``` (optional trailing whitespace).
    private static readonly Regex CodeFenceOpenRegex = new(@"^```(yaml)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeFenceCloseRegex = new(@"^```\s*$", RegexOptions.Compiled);

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extract and parse scenarios from a full <c>PMSpec.md</c> document.
    /// Returns an empty list when no <c># scenarios</c> marker is present — does NOT throw.
    /// </summary>
    /// <param name="pmSpecContent">Full text content of PMSpec.md.</param>
    /// <param name="logger">Optional logger; used to emit a warning when multiple markers are found.</param>
    public static IReadOnlyList<Scenario> ExtractFromPmSpecMarkdown(
        string pmSpecContent,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pmSpecContent);

        var lines = pmSpecContent.Replace("\r\n", "\n").Split('\n');

        var markerIndices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), ScenariosMarker, StringComparison.OrdinalIgnoreCase))
                markerIndices.Add(i);
        }

        if (markerIndices.Count == 0)
            return Array.Empty<Scenario>();

        if (markerIndices.Count > 1)
            logger?.LogWarning(
                "Found {Count} '# scenarios' markers in PMSpec; using the first occurrence",
                markerIndices.Count);

        var bodyLines = ExtractBodyLines(lines, markerIndices[0] + 1);
        if (bodyLines.Count == 0)
            return Array.Empty<Scenario>();

        return ParseYamlLines(bodyLines, logger);
    }

    /// <summary>
    /// Parse scenarios from a raw YAML string containing a list of scenario objects.
    /// </summary>
    /// <param name="yaml">Raw YAML text (the body after the <c># scenarios</c> marker, without the marker itself).</param>
    /// <param name="logger">Optional logger for parse warnings.</param>
    public static IReadOnlyList<Scenario> ExtractFromYamlString(
        string yaml,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        // The LLM prompt asks for a full YAML doc with project_archetype:, user_voice:,
        // and scenarios: keys. The parser expects flat top-level list items (- id: S01).
        // Strip everything before the scenarios: block and dedent by 2 spaces.
        var scenariosIdx = lines.FindIndex(l =>
            l.TrimStart().StartsWith("scenarios:", StringComparison.OrdinalIgnoreCase));
        if (scenariosIdx >= 0)
        {
            lines = lines.Skip(scenariosIdx + 1).ToList();
            // Dedent: remove leading 2 spaces from each line so "  - id:" becomes "- id:"
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length >= 2 && lines[i][0] == ' ' && lines[i][1] == ' ')
                    lines[i] = lines[i][2..];
            }
            logger?.LogDebug("ScenarioYamlExtractor: stripped preamble, {Count} lines after 'scenarios:' key", lines.Count);
        }

        // Also strip markdown code fences that the LLM may wrap around the YAML
        if (lines.Count > 0 && CodeFenceOpenRegex.IsMatch(lines[0].Trim()))
            lines.RemoveAt(0);
        if (lines.Count > 0 && CodeFenceCloseRegex.IsMatch(lines[^1].Trim()))
            lines.RemoveAt(lines.Count - 1);

        return ParseYamlLines(lines, logger);
    }

    // -------------------------------------------------------------------------
    // Block extraction
    // -------------------------------------------------------------------------

    private static List<string> ExtractBodyLines(string[] allLines, int startIndex)
    {
        var body = new List<string>();
        var inCodeFence = false;
        var seenCodeFenceOpen = false;

        for (var i = startIndex; i < allLines.Length; i++)
        {
            var line = allLines[i];
            var trimmed = line.Trim();

            // Opening code fence: ```yaml or ```
            if (!seenCodeFenceOpen && CodeFenceOpenRegex.IsMatch(trimmed))
            {
                inCodeFence = true;
                seenCodeFenceOpen = true;
                continue; // skip the fence marker itself
            }

            // Closing code fence
            if (inCodeFence && CodeFenceCloseRegex.IsMatch(trimmed))
                break; // end of YAML body

            // When NOT inside a code fence, stop at section boundaries
            if (!inCodeFence)
            {
                // A new Markdown heading (H2 or deeper) terminates the YAML block.
                if (trimmed.StartsWith("## ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("### ", StringComparison.Ordinal))
                    break;

                // Another bracketed-tag section marker (e.g., [image-deliverables]).
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    break;
            }

            body.Add(line);
        }

        return body;
    }

    // -------------------------------------------------------------------------
    // YAML line-by-line parser
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse a list of YAML lines into <see cref="Scenario"/> objects.
    /// Assumes standard 2-space indentation as produced by the PM agent.
    /// Logs a warning if tabs or non-2-space indentation is detected.
    /// </summary>
    private static IReadOnlyList<Scenario> ParseYamlLines(List<string> lines, ILogger? logger)
    {
        // Indentation sanity check: detect tabs or non-2-space indentation early
        // so problems are visible in logs instead of silently dropping scenarios.
        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] == '\t')
            {
                logger?.LogWarning(
                    "Scenario YAML contains tab indentation which is not supported by this parser. " +
                    "Scenarios may be silently skipped. Convert tabs to 2-space indentation. " +
                    "Line: {Line}", line.TrimEnd());
                break;
            }
            var trimmed = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith('#'))
            {
                var indent = line.Length - trimmed.Length;
                if (indent > 0 && indent % 2 != 0)
                {
                    logger?.LogWarning(
                        "Scenario YAML has odd indentation ({Indent} spaces) — expected multiples of 2. " +
                        "Scenarios may be silently skipped or partially parsed. Line: {Line}",
                        indent, line.TrimEnd());
                    break;
                }
            }
        }

        var result = new List<Scenario>();

        // Mutable builder fields
        string? id = null, title = null, actor = null, trigger = null, evidenceUrl = null;
        var journeyKind = JourneyKind.UiInteraction;
        var priority = ScenarioPriority.Important;
        var status = ScenarioStatus.Proposed;
        var verificationStatus = VerificationStatus.NotYetVerified;
        var infrastructure = false;
        var interactiveValidationSafe = true;
        var preconditions = new List<string>();
        var steps = new List<string>();
        var expectedTerminalState = new List<string>();
        var observationSurfaces = new List<ObservationSurface>();
        var subsystemsInvolved = new List<string>();
        var implementingTasks = new List<string>();

        // Current observation surface being built
        string? surfaceKind = null;
        var surfaceFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSurface = false;
        var inScenario = false;
        string? currentListProp = null;
        var inObservationSurfaces = false;

        void FlushSurface()
        {
            if (!inSurface || surfaceKind is null) return;
            observationSurfaces.Add(new ObservationSurface
            {
                Kind = surfaceKind,
                Fields = new Dictionary<string, string>(surfaceFields, StringComparer.OrdinalIgnoreCase),
            });
            surfaceKind = null;
            surfaceFields.Clear();
            inSurface = false;
        }

        void FlushScenario()
        {
            if (!inScenario) return;
            FlushSurface();

            if (id is null || title is null || actor is null || trigger is null)
            {
                logger?.LogWarning(
                    "Skipping incomplete scenario (id={Id}, title={Title}): required fields missing",
                    id, title);
            }
            else
            {
                result.Add(new Scenario
                {
                    Id = id,
                    Title = title,
                    JourneyKind = journeyKind,
                    Actor = actor,
                    Trigger = trigger,
                    Preconditions = preconditions.AsReadOnly(),
                    Steps = steps.AsReadOnly(),
                    ExpectedTerminalState = expectedTerminalState.AsReadOnly(),
                    ObservationSurfaces = observationSurfaces.AsReadOnly(),
                    SubsystemsInvolved = subsystemsInvolved.AsReadOnly(),
                    Priority = priority,
                    Status = status,
                    ImplementingTasks = implementingTasks.AsReadOnly(),
                    VerificationStatus = verificationStatus,
                    VerificationEvidenceUrl = evidenceUrl,
                    Infrastructure = infrastructure,
                    InteractiveValidationSafe = interactiveValidationSafe,
                });
            }

            // Reset builder state
            id = title = actor = trigger = evidenceUrl = null;
            journeyKind = JourneyKind.UiInteraction;
            priority = ScenarioPriority.Important;
            status = ScenarioStatus.Proposed;
            verificationStatus = VerificationStatus.NotYetVerified;
            infrastructure = false;
            interactiveValidationSafe = true;
            preconditions = [];
            steps = [];
            expectedTerminalState = [];
            observationSurfaces = [];
            subsystemsInvolved = [];
            implementingTasks = [];
            currentListProp = null;
            inObservationSurfaces = false;
            inScenario = false;
        }

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimStart();

            // Skip blanks and YAML comments (# comment lines that are NOT the scenarios marker)
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var indent = rawLine.Length - trimmed.Length;

            // ---- indent == 0 : new top-level list item = new scenario ----
            if (indent == 0 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushScenario();
                inScenario = true;

                var rest = trimmed[2..].TrimStart();
                if (!string.IsNullOrEmpty(rest))
                    ApplyProperty(rest, ref id, ref title, ref journeyKind, ref actor, ref trigger,
                        ref priority, ref status, ref verificationStatus, ref infrastructure,
                        ref interactiveValidationSafe, ref evidenceUrl,
                        ref currentListProp, ref inObservationSurfaces,
                        preconditions, steps, expectedTerminalState, subsystemsInvolved, implementingTasks);
                continue;
            }

            if (!inScenario) continue;

            // ---- indent == 2 : scenario property ----
            if (indent == 2)
            {
                FlushSurface();
                inObservationSurfaces = false;
                currentListProp = null;

                var (key, value) = SplitKeyValue(trimmed);

                if (string.IsNullOrEmpty(value))
                {
                    // List property header (e.g., "preconditions:", "observation_surfaces:")
                    currentListProp = key;
                    inObservationSurfaces = key == "observation_surfaces";
                }
                else
                {
                    ApplyScalarProperty(key, value,
                        ref id, ref title, ref journeyKind, ref actor, ref trigger,
                        ref priority, ref status, ref verificationStatus, ref infrastructure,
                        ref interactiveValidationSafe, ref evidenceUrl);
                }
                continue;
            }

            // ---- indent == 4 : list item within a scenario property ----
            if (indent == 4)
            {
                if (inObservationSurfaces && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    // New observation surface
                    FlushSurface();
                    inSurface = true;

                    var rest = trimmed[2..].TrimStart();
                    if (!string.IsNullOrEmpty(rest))
                    {
                        var (k, v) = SplitKeyValue(rest);
                        if (k == "kind") surfaceKind = Unquote(v);
                        else if (!string.IsNullOrEmpty(v)) surfaceFields[k] = Unquote(v);
                    }
                }
                else if (!inObservationSurfaces && currentListProp is not null
                    && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    // String list item
                    var item = Unquote(trimmed[2..].TrimStart());
                    AppendToList(currentListProp, item, preconditions, steps,
                        expectedTerminalState, subsystemsInvolved, implementingTasks);
                }
                continue;
            }

            // ---- indent == 6 : observation surface field ----
            if (indent == 6 && inSurface)
            {
                var (k, v) = SplitKeyValue(trimmed);
                if (k == "kind") surfaceKind = Unquote(v);
                else if (!string.IsNullOrEmpty(v)) surfaceFields[k] = Unquote(v);
            }
        }

        FlushScenario();
        return result.AsReadOnly();
    }

    // -------------------------------------------------------------------------
    // Property application helpers
    // -------------------------------------------------------------------------

    private static void ApplyProperty(
        string line,
        ref string? id, ref string? title, ref JourneyKind journeyKind,
        ref string? actor, ref string? trigger,
        ref ScenarioPriority priority, ref ScenarioStatus status,
        ref VerificationStatus verificationStatus, ref bool infrastructure,
        ref bool interactiveValidationSafe,
        ref string? evidenceUrl,
        ref string? currentListProp, ref bool inObservationSurfaces,
        List<string> preconditions, List<string> steps,
        List<string> expectedTerminalState, List<string> subsystemsInvolved,
        List<string> implementingTasks)
    {
        var (key, value) = SplitKeyValue(line);
        if (string.IsNullOrEmpty(value))
        {
            currentListProp = key;
            inObservationSurfaces = key == "observation_surfaces";
        }
        else
        {
            ApplyScalarProperty(key, value,
                ref id, ref title, ref journeyKind, ref actor, ref trigger,
                ref priority, ref status, ref verificationStatus, ref infrastructure,
                ref interactiveValidationSafe, ref evidenceUrl);
        }
    }

    private static void ApplyScalarProperty(
        string key, string value,
        ref string? id, ref string? title, ref JourneyKind journeyKind,
        ref string? actor, ref string? trigger,
        ref ScenarioPriority priority, ref ScenarioStatus status,
        ref VerificationStatus verificationStatus, ref bool infrastructure,
        ref bool interactiveValidationSafe,
        ref string? evidenceUrl)
    {
        var unquoted = Unquote(value);

        switch (key)
        {
            case "id":
                id = unquoted;
                break;
            case "title":
                title = unquoted;
                break;
            case "journey_kind":
                journeyKind = ParseJourneyKind(unquoted);
                break;
            case "actor":
                actor = unquoted;
                break;
            case "trigger":
                trigger = unquoted;
                break;
            case "priority":
                priority = ParsePriority(unquoted);
                break;
            case "status":
                status = ParseStatus(unquoted);
                break;
            case "verification_status":
                verificationStatus = ParseVerificationStatus(unquoted);
                break;
            case "verification_evidence_url":
                evidenceUrl = IsNullLiteral(unquoted) ? null : unquoted;
                break;
            case "infrastructure":
                infrastructure = ParseBool(unquoted);
                break;
            case "interactive_validation_safe":
                interactiveValidationSafe = ParseBool(unquoted);
                break;
        }
    }

    private static void AppendToList(
        string prop, string item,
        List<string> preconditions, List<string> steps,
        List<string> expectedTerminalState, List<string> subsystemsInvolved,
        List<string> implementingTasks)
    {
        switch (prop)
        {
            case "preconditions": preconditions.Add(item); break;
            case "steps": steps.Add(item); break;
            case "expected_terminal_state": expectedTerminalState.Add(item); break;
            case "subsystems_involved": subsystemsInvolved.Add(item); break;
            case "implementing_tasks": implementingTasks.Add(item); break;
        }
    }

    // -------------------------------------------------------------------------
    // Enum parsers — throw ScenarioParseException for unknown values
    // -------------------------------------------------------------------------

    internal static JourneyKind ParseJourneyKind(string value)
    {
        return NormalizeEnum(value) switch
        {
            "ui_interaction" => JourneyKind.UiInteraction,
            "api_call" => JourneyKind.ApiCall,
            "scheduled_job" => JourneyKind.ScheduledJob,
            "event_arrival" => JourneyKind.EventArrival,
            "webhook" => JourneyKind.Webhook,
            "message_consume" => JourneyKind.MessageConsume,
            "cli_invocation" => JourneyKind.CliInvocation,
            "system_initiated" => JourneyKind.SystemInitiated,
            "data_pipeline" => JourneyKind.DataPipeline,
            _ => throw new ScenarioParseException(
                $"Unknown journey_kind value '{value}'. " +
                "Allowed: ui_interaction, api_call, scheduled_job, event_arrival, webhook, " +
                "message_consume, cli_invocation, system_initiated, data_pipeline."),
        };
    }

    internal static ScenarioPriority ParsePriority(string value)
    {
        return NormalizeEnum(value) switch
        {
            "critical" => ScenarioPriority.Critical,
            "important" => ScenarioPriority.Important,
            "nice_to_have" => ScenarioPriority.NiceToHave,
            _ => throw new ScenarioParseException(
                $"Unknown priority value '{value}'. Allowed: critical, important, nice-to-have."),
        };
    }

    internal static ScenarioStatus ParseStatus(string value)
    {
        return NormalizeEnum(value) switch
        {
            "proposed" => ScenarioStatus.Proposed,
            "approved" => ScenarioStatus.Approved,
            "edited" => ScenarioStatus.Edited,
            "rejected" => ScenarioStatus.Rejected,
            _ => throw new ScenarioParseException(
                $"Unknown status value '{value}'. Allowed: proposed, approved, edited, rejected."),
        };
    }

    internal static VerificationStatus ParseVerificationStatus(string value)
    {
        return NormalizeEnum(value) switch
        {
            "not_yet_verified" => VerificationStatus.NotYetVerified,
            "verified" => VerificationStatus.Verified,
            "broken" => VerificationStatus.Broken,
            "inconclusive" => VerificationStatus.Inconclusive,
            "inferred_pass" => VerificationStatus.InferredPass,
            "inferred_fail" => VerificationStatus.InferredFail,
            _ => throw new ScenarioParseException(
                $"Unknown verification_status value '{value}'. " +
                "Allowed: not_yet_verified, verified, broken, inconclusive, inferred_pass, inferred_fail."),
        };
    }

    // -------------------------------------------------------------------------
    // Low-level helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Split a YAML key: value line into (key, value). The key is lowercased; hyphens replaced
    /// with underscores. The value may be empty (list-property header with no inline value).
    /// </summary>
    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0)
            return (line.Trim().ToLowerInvariant().Replace('-', '_'), string.Empty);

        var key = line[..colonIdx].Trim().ToLowerInvariant().Replace('-', '_');
        var value = line[(colonIdx + 1)..].Trim();
        return (key, value);
    }

    /// <summary>
    /// Strip surrounding double or single quotes from a YAML scalar value.
    /// Strips surrounding single or double quotes from a YAML value and unescapes
    /// backslash-escaped characters (\", \\) so round-tripping through
    /// <see cref="ScenarioYamlSerializer"/> is lossless.
    /// Returns the value unchanged when not quoted.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') ||
                (value[0] == '\'' && value[^1] == '\''))
            {
                var inner = value[1..^1];
                // Unescape backslash sequences (\" → ", \\ → \) for double-quoted strings
                if (value[0] == '"')
                    inner = inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return inner;
            }
        }
        return value;
    }

    private static bool IsNullLiteral(string value) =>
        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "~", StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(value);

    private static bool ParseBool(string value) =>
        value.ToLowerInvariant() is "true" or "yes" or "1";

    /// <summary>
    /// Normalize an enum value string: lowercase, replace hyphens with underscores.
    /// </summary>
    private static string NormalizeEnum(string value) =>
        value.ToLowerInvariant().Replace('-', '_');
}

/// <summary>
/// Thrown when <see cref="ScenarioYamlExtractor"/> encounters a YAML value it cannot
/// unambiguously map to a typed field (e.g., an unknown enum discriminant).
/// </summary>
public sealed class ScenarioParseException : Exception
{
    /// <inheritdoc/>
    public ScenarioParseException(string message) : base(message) { }

    /// <inheritdoc/>
    public ScenarioParseException(string message, Exception inner) : base(message, inner) { }
}
