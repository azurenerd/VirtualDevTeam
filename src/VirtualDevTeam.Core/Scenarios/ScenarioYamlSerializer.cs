using System.Text;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Serializes <see cref="Scenario"/> lists to YAML in the format produced by the PM agent and
/// consumed by <see cref="ScenarioYamlExtractor"/>. Output is round-trippable: parsing the
/// produced YAML with <see cref="ScenarioYamlExtractor.ExtractFromYamlString"/> returns
/// equivalent scenario objects.
/// </summary>
/// <remarks>
/// Hand-rolled; no YamlDotNet dependency. Produces 2-space-indented YAML that matches the
/// parser's expected structure: indent-0 list items for scenarios, indent-2 scalar properties,
/// indent-4 list items, indent-6 observation-surface fields.
/// </remarks>
public static class ScenarioYamlSerializer
{
    /// <summary>
    /// Serialize a list of scenarios to YAML.
    /// Returns an empty string when the list is empty.
    /// </summary>
    public static string Serialize(IReadOnlyList<Scenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        if (scenarios.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var scenario in scenarios)
            AppendScenario(sb, scenario);
        return sb.ToString().TrimEnd();
    }

    private static void AppendScenario(StringBuilder sb, Scenario s)
    {
        sb.Append("- id: ").AppendLine(YamlScalar(s.Id));
        sb.Append("  title: ").AppendLine(YamlScalar(s.Title));
        sb.Append("  journey_kind: ").AppendLine(JourneyKindValue(s.JourneyKind));
        sb.Append("  actor: ").AppendLine(YamlScalar(s.Actor));
        sb.Append("  trigger: ").AppendLine(YamlScalar(s.Trigger));
        sb.Append("  priority: ").AppendLine(PriorityValue(s.Priority));
        sb.Append("  status: ").AppendLine(StatusValue(s.Status));
        sb.Append("  verification_status: ").AppendLine(VerificationStatusValue(s.VerificationStatus));
        sb.Append("  infrastructure: ").AppendLine(s.Infrastructure ? "true" : "false");
        sb.Append("  interactive_validation_safe: ").AppendLine(s.InteractiveValidationSafe ? "true" : "false");

        if (s.Preconditions.Count > 0)
        {
            sb.AppendLine("  preconditions:");
            foreach (var item in s.Preconditions)
                sb.Append("    - ").AppendLine(YamlScalar(item));
        }

        if (s.Steps.Count > 0)
        {
            sb.AppendLine("  steps:");
            foreach (var item in s.Steps)
                sb.Append("    - ").AppendLine(YamlScalar(item));
        }

        if (s.ExpectedTerminalState.Count > 0)
        {
            sb.AppendLine("  expected_terminal_state:");
            foreach (var item in s.ExpectedTerminalState)
                sb.Append("    - ").AppendLine(YamlScalar(item));
        }

        if (s.ObservationSurfaces.Count > 0)
        {
            sb.AppendLine("  observation_surfaces:");
            foreach (var surface in s.ObservationSurfaces)
            {
                sb.Append("    - kind: ").AppendLine(YamlScalar(surface.Kind));
                foreach (var (k, v) in surface.Fields)
                    sb.Append("      ").Append(k).Append(": ").AppendLine(YamlScalar(v));
            }
        }

        if (s.SubsystemsInvolved.Count > 0)
        {
            sb.AppendLine("  subsystems_involved:");
            foreach (var item in s.SubsystemsInvolved)
                sb.Append("    - ").AppendLine(YamlScalar(item));
        }

        if (s.ImplementingTasks.Count > 0)
        {
            sb.AppendLine("  implementing_tasks:");
            foreach (var item in s.ImplementingTasks)
                sb.Append("    - ").AppendLine(YamlScalar(item));
        }

        if (s.VerificationEvidenceUrl is not null)
            sb.Append("  verification_evidence_url: ").AppendLine(s.VerificationEvidenceUrl);

        sb.AppendLine();
    }

    /// <summary>
    /// Wraps a scalar value in double-quotes when it contains characters that would otherwise
    /// be misinterpreted by a YAML parser (colon-space, hash, square brackets, etc.).
    /// Simple values without ambiguous characters are emitted unquoted.
    /// </summary>
    internal static string YamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        var needsQuoting =
            value[0] is '"' or '\'' or '{' or '[' or '&' or '*' or '?' or '|' or '<' or '>' or '=' or '!' or '%' or '@' or '`' ||
            value.Contains(": ", StringComparison.Ordinal) ||
            value.Contains(" #", StringComparison.Ordinal) ||
            value.Contains('\n') ||
            value.Contains('\\');

        if (!needsQuoting)
            return value;

        return '"' + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    }

    private static string JourneyKindValue(JourneyKind v) => v switch
    {
        JourneyKind.UiInteraction   => "ui_interaction",
        JourneyKind.ApiCall         => "api_call",
        JourneyKind.ScheduledJob    => "scheduled_job",
        JourneyKind.EventArrival    => "event_arrival",
        JourneyKind.Webhook         => "webhook",
        JourneyKind.MessageConsume  => "message_consume",
        JourneyKind.CliInvocation   => "cli_invocation",
        JourneyKind.SystemInitiated => "system_initiated",
        JourneyKind.DataPipeline    => "data_pipeline",
        _                           => v.ToString().ToLowerInvariant(),
    };

    private static string PriorityValue(ScenarioPriority v) => v switch
    {
        ScenarioPriority.Critical    => "critical",
        ScenarioPriority.Important   => "important",
        ScenarioPriority.NiceToHave  => "nice_to_have",
        _                            => v.ToString().ToLowerInvariant(),
    };

    private static string StatusValue(ScenarioStatus v) => v switch
    {
        ScenarioStatus.Proposed  => "proposed",
        ScenarioStatus.Approved  => "approved",
        ScenarioStatus.Edited    => "edited",
        ScenarioStatus.Rejected  => "rejected",
        _                        => v.ToString().ToLowerInvariant(),
    };

    private static string VerificationStatusValue(VerificationStatus v) => v switch
    {
        VerificationStatus.NotYetVerified => "not_yet_verified",
        VerificationStatus.Verified       => "verified",
        VerificationStatus.Broken         => "broken",
        VerificationStatus.Inconclusive   => "inconclusive",
        VerificationStatus.InferredPass    => "inferred_pass",
        VerificationStatus.InferredFail    => "inferred_fail",
        _                                 => v.ToString().ToLowerInvariant(),
    };
}
