using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// Extracts structured DECISION blocks from AI response text.
/// Agents are instructed to output decisions in a structured format that this parser extracts.
/// </summary>
public static class DecisionBlockParser
{
    /// <summary>
    /// A decision extracted from an AI response.
    /// </summary>
    public record ParsedDecision(
        string Title,
        string? SourceQuestion,
        string Choice,
        string Rationale,
        DecisionImpactLevel Impact);

    // Matches "DECISION:" followed by a title, then optional QUESTION/CHOICE/RATIONALE/IMPACT fields
    private static readonly Regex DecisionHeaderRegex = new(
        @"^DECISION:\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex FieldRegex = new(
        @"^(QUESTION|CHOICE|RATIONALE|IMPACT|ALTERNATIVES|RISK):\s*(.+?)(?=\n(?:DECISION|QUESTION|CHOICE|RATIONALE|IMPACT|ALTERNATIVES|RISK):|$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Extracts all DECISION blocks from an AI response.
    /// Format expected:
    /// DECISION: [title]
    /// QUESTION: [optional — original wizard question]
    /// CHOICE: [what was decided]
    /// RATIONALE: [why]
    /// IMPACT: [XS|S|M|L|XL]
    /// </summary>
    public static List<ParsedDecision> ExtractDecisions(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return new List<ParsedDecision>();

        var decisions = new List<ParsedDecision>();
        var headerMatches = DecisionHeaderRegex.Matches(aiResponse);

        for (int i = 0; i < headerMatches.Count; i++)
        {
            var headerMatch = headerMatches[i];
            var title = headerMatch.Groups[1].Value.Trim();

            // Get the text between this header and the next (or end of string)
            var startIdx = headerMatch.Index + headerMatch.Length;
            var endIdx = i + 1 < headerMatches.Count
                ? headerMatches[i + 1].Index
                : aiResponse.Length;
            var blockText = aiResponse[startIdx..endIdx];

            // Extract fields from the block
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fieldMatches = FieldRegex.Matches(blockText);
            foreach (Match fm in fieldMatches)
            {
                var fieldName = fm.Groups[1].Value.Trim();
                var fieldValue = fm.Groups[2].Value.Trim();
                fields[fieldName] = fieldValue;
            }

            // Also try simple line-by-line extraction for robustness
            if (fields.Count == 0)
            {
                foreach (var line in blockText.Split('\n'))
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    var key = line[..colonIdx].Trim();
                    var val = line[(colonIdx + 1)..].Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        key is "QUESTION" or "CHOICE" or "RATIONALE" or "IMPACT" or "ALTERNATIVES" or "RISK")
                    {
                        fields.TryAdd(key, val);
                    }
                }
            }

            // Must have at least CHOICE to be a valid decision
            if (!fields.TryGetValue("CHOICE", out var choice) || string.IsNullOrWhiteSpace(choice))
                continue;

            fields.TryGetValue("QUESTION", out var question);
            fields.TryGetValue("RATIONALE", out var rationale);
            fields.TryGetValue("IMPACT", out var impactStr);

            var impact = ParseImpactLevel(impactStr);

            decisions.Add(new ParsedDecision(
                Title: title,
                SourceQuestion: string.IsNullOrWhiteSpace(question) ? null : question,
                Choice: choice,
                Rationale: rationale ?? "",
                Impact: impact));
        }

        return decisions;
    }

    /// <summary>
    /// Strips DECISION blocks from document content so they don't appear in committed files.
    /// Returns the cleaned content.
    /// </summary>
    public static string StripDecisionBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var headerMatches = DecisionHeaderRegex.Matches(content);
        if (headerMatches.Count == 0)
            return content;

        // Remove each decision block (from DECISION: to next non-decision-field line or next DECISION:)
        var result = content;
        // Process in reverse order to maintain index stability
        for (int i = headerMatches.Count - 1; i >= 0; i--)
        {
            var headerMatch = headerMatches[i];
            var startIdx = headerMatch.Index;

            // Find end of block: next line that isn't a decision field or empty
            var endIdx = i + 1 < headerMatches.Count
                ? headerMatches[i + 1].Index
                : FindBlockEnd(result, startIdx + headerMatch.Length);

            // Include preceding newline if present
            if (startIdx > 0 && result[startIdx - 1] == '\n')
                startIdx--;

            // Clamp removal length to avoid overshooting string bounds
            var removeCount = Math.Min(endIdx - startIdx, result.Length - startIdx);
            result = result.Remove(startIdx, removeCount);
        }

        return result.TrimEnd() + "\n";
    }

    private static int FindBlockEnd(string text, int startAfterHeader)
    {
        var lines = text[startAfterHeader..].Split('\n');
        var consumed = startAfterHeader;

        foreach (var line in lines)
        {
            consumed += line.Length + 1; // +1 for newline
            var trimmed = line.TrimStart();

            // Decision field lines
            if (trimmed.StartsWith("QUESTION:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CHOICE:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("RATIONALE:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("IMPACT:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ALTERNATIVES:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("RISK:", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            // Non-decision content found — block ends before this line
            consumed -= line.Length + 1;
            break;
        }

        // Cap to text length — the last line's +1 may overshoot if there's no trailing newline
        return Math.Min(consumed, text.Length);
    }

    private static DecisionImpactLevel ParseImpactLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return DecisionImpactLevel.M; // default to Medium if not specified

        return level.Trim().ToUpperInvariant() switch
        {
            "XS" or "EXTRA SMALL" or "EXTRASMALL" => DecisionImpactLevel.XS,
            "S" or "SMALL" => DecisionImpactLevel.S,
            "M" or "MEDIUM" or "MED" => DecisionImpactLevel.M,
            "L" or "LARGE" => DecisionImpactLevel.L,
            "XL" or "EXTRA LARGE" or "EXTRALARGE" => DecisionImpactLevel.XL,
            _ => DecisionImpactLevel.M,
        };
    }

    /// <summary>
    /// A contract-change decision extracted from pipe-delimited DECISION blocks in plan output.
    /// </summary>
    public record ContractChangeDecision(string Impact, string Title, string Rationale, string Files);

    /// <summary>
    /// Parses pipe-delimited DECISION|impact|title|rationale|files lines from plan content.
    /// Used for contract-change decisions emitted during task planning.
    /// </summary>
    public static List<ContractChangeDecision> ParsePipeDelimited(string content)
    {
        var results = new List<ContractChangeDecision>();
        if (string.IsNullOrWhiteSpace(content)) return results;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("DECISION|", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 5) continue;

            var impact = parts[1].Trim();
            var title = parts[2].Trim();
            var rationale = parts[3].Trim();
            var files = parts[4].Trim();

            if (!string.IsNullOrEmpty(impact) && !string.IsNullOrEmpty(title))
                results.Add(new ContractChangeDecision(impact, title, rationale, files));
        }

        return results;
    }
}
