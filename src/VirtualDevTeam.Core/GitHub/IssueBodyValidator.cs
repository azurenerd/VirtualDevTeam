using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Validates issue body quality before posting to GitHub.
/// Catches garbled LLM output, empty descriptions, and truncated content.
/// </summary>
public static partial class IssueBodyValidator
{
    /// <summary>
    /// Validates an issue body and returns a cleaned version.
    /// Returns null if the body is unrecoverably bad.
    /// </summary>
    public static string? ValidateAndClean(string? body, string title, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            logger?.LogWarning("Issue body is empty for '{Title}'", title);
            return null;
        }

        var cleaned = body.Trim();

        // Check minimum length — a valid issue body should have at least some substance
        if (cleaned.Length < 30)
        {
            logger?.LogWarning("Issue body too short ({Length} chars) for '{Title}': {Body}",
                cleaned.Length, title, cleaned);
            return null;
        }

        // Detect garbled output: high ratio of non-ASCII or control characters
        var nonPrintable = cleaned.Count(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');
        if (nonPrintable > cleaned.Length * 0.05)
        {
            logger?.LogWarning("Issue body has too many control characters ({Count}/{Total}) for '{Title}'",
                nonPrintable, cleaned.Length, title);
            return null;
        }

        // Detect truncated JSON or markdown — unbalanced braces/brackets at end
        if (cleaned.EndsWith('{') || cleaned.EndsWith('[') || cleaned.EndsWith("```"))
        {
            logger?.LogWarning("Issue body appears truncated for '{Title}'", title);
            // Try to salvage by trimming the truncated part
            cleaned = cleaned.TrimEnd('{', '[');
            if (cleaned.EndsWith("```"))
                cleaned = cleaned[..^3];
            cleaned = cleaned.TrimEnd();
        }

        // Detect repeated content (LLM stuck in a loop)
        if (HasExcessiveRepetition(cleaned))
        {
            logger?.LogWarning("Issue body has excessive repetition for '{Title}'", title);
            cleaned = DeduplicateRepeatedBlocks(cleaned);
        }

        // Strip ANSI escape codes that sometimes leak from CLI output
        cleaned = AnsiEscapePattern().Replace(cleaned, "");

        // Final length check after cleaning
        if (cleaned.Length < 20)
        {
            logger?.LogWarning("Issue body too short after cleaning ({Length} chars) for '{Title}'",
                cleaned.Length, title);
            return null;
        }

        return cleaned;
    }

    /// <summary>
    /// Quick check: does the body look like valid issue content?
    /// </summary>
    public static bool IsValid(string? body) =>
        !string.IsNullOrWhiteSpace(body) && body.Trim().Length >= 30;

    private static bool HasExcessiveRepetition(string text)
    {
        // Split into lines and check if any line repeats more than 5 times consecutively
        var lines = text.Split('\n');
        if (lines.Length < 10) return false;

        int repeatCount = 1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 10 &&
                lines[i].Trim().Equals(lines[i - 1].Trim(), StringComparison.Ordinal))
            {
                repeatCount++;
                if (repeatCount > 5) return true;
            }
            else
            {
                repeatCount = 1;
            }
        }
        return false;
    }

    private static string DeduplicateRepeatedBlocks(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        string? lastLine = null;
        int repeatCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 10 && trimmed.Equals(lastLine, StringComparison.Ordinal))
            {
                repeatCount++;
                if (repeatCount <= 2) // Keep at most 2 repetitions
                    result.Add(line);
            }
            else
            {
                repeatCount = 0;
                result.Add(line);
                lastLine = trimmed;
            }
        }

        return string.Join('\n', result);
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiEscapePattern();
}
