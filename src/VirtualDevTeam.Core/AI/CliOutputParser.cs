using System.Text.Json;
using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Parses raw copilot CLI output into clean AI response text.
/// Supports both text mode (--silent) and JSON mode (--output-format json).
/// Handles ANSI escape codes, CLI chrome, progress spinners, and response boundary detection.
/// </summary>
public static partial class CliOutputParser
{
    // Matches ANSI escape sequences: ESC[ ... final_byte
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B\(B", RegexOptions.Compiled)]
    private static partial Regex AnsiEscapeRegex();

    // Matches common CLI spinners and progress indicators
    [GeneratedRegex(@"^[\s]*[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏⣾⣽⣻⢿⡿⣟⣯⣷|/\-\\][\s]*$", RegexOptions.Multiline)]
    private static partial Regex SpinnerLineRegex();

    // Matches carriage-return-based line overwrites (progress bars, spinners)
    [GeneratedRegex(@"\r[^\n]")]
    private static partial Regex CarriageReturnOverwriteRegex();

    /// <summary>
    /// Strips ANSI escape codes from raw terminal output.
    /// </summary>
    public static string StripAnsiCodes(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        return AnsiEscapeRegex().Replace(raw, "");
    }

    /// <summary>
    /// Removes CLI chrome: banners, session info, prompt markers, and spinner lines.
    /// </summary>
    public static string RemoveCliChrome(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var lines = text.Split('\n');
        var cleanLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            // Skip known CLI chrome patterns
            if (IsCliChromeLine(trimmed))
                continue;

            cleanLines.Add(trimmed);
        }

        return string.Join('\n', cleanLines).Trim();
    }

    /// <summary>
    /// Full parsing pipeline: strip ANSI → remove carriage-return overwrites → remove chrome → trim.
    /// </summary>
    public static string Parse(string rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
            return string.Empty;

        var result = rawOutput;

        // 1. Handle carriage-return overwrites (keep only final overwrite per line)
        result = ResolveCarriageReturns(result);

        // 2. Strip ANSI escape codes
        result = StripAnsiCodes(result);

        // 3. Remove spinner lines
        result = SpinnerLineRegex().Replace(result, "");

        // 4. Remove CLI chrome
        result = RemoveCliChrome(result);

        // 5. Collapse excessive blank lines
        result = CollapseBlankLines(result);

        return result.Trim();
    }

    /// <summary>
    /// Resolves carriage-return overwrites, keeping only the final content per line.
    /// This handles progress bars and spinners that overwrite the same line.
    /// </summary>
    internal static string ResolveCarriageReturns(string text)
    {
        if (!text.Contains('\r'))
            return text;

        var lines = text.Split('\n');
        var resolved = new List<string>();

        foreach (var line in lines)
        {
            if (line.Contains('\r'))
            {
                // Split by \r and take the last non-empty segment
                var segments = line.Split('\r');
                var last = segments.LastOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
                resolved.Add(last);
            }
            else
            {
                resolved.Add(line);
            }
        }

        return string.Join('\n', resolved);
    }

    /// <summary>
    /// Collapses runs of 3+ blank lines into 2.
    /// </summary>
    internal static string CollapseBlankLines(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        int consecutiveBlanks = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlanks++;
                if (consecutiveBlanks <= 2)
                    result.Add(line);
            }
            else
            {
                consecutiveBlanks = 0;
                result.Add(line);
            }
        }

        return string.Join('\n', result);
    }

    private static bool IsCliChromeLine(string line)
    {
        var trimmed = line.Trim();

        // Empty lines pass through (handled by CollapseBlankLines)
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        // ghcs banner/version lines
        if (trimmed.StartsWith("GitHub Copilot", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("Powered by", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("Version", StringComparison.OrdinalIgnoreCase))
            return true;

        // Session markers
        if (trimmed.StartsWith("Session ID:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Prompt markers (the > or $ at the beginning of input lines)
        if (trimmed.StartsWith("> ") || trimmed == ">")
            return true;
        if (trimmed.StartsWith("$ ") || trimmed == "$")
            return true;

        // Tool usage/status lines
        if (trimmed.StartsWith("Using tool:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.StartsWith("Running:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Separator lines (all dashes, equals, etc.)
        if (trimmed.Length > 3 && trimmed.All(c => c == '-' || c == '=' || c == '─' || c == '━'))
            return true;

        return false;
    }

    /// <summary>
    /// Parses JSONL output from <c>--output-format json</c> mode.
    /// Extracts the response content from <c>assistant.message</c> events.
    /// When the CLI emits multiple <c>assistant.message</c> events in a single response
    /// (for example, text before and after a tool call), the contents are joined with
    /// <c>\n\n</c> so Markdown block boundaries (paragraphs, tables, headers) survive.
    /// </summary>
    /// <returns>
    /// The extracted assistant content, or <see cref="string.Empty"/> if the output is valid
    /// JSONL (has typed events) but contains no assistant messages — preventing the caller
    /// from falling back to text-mode parsing on raw JSONL. Returns <c>null</c> only when
    /// the output is not JSONL at all (no lines with a JSON "type" property).
    /// </returns>
    public static string? ParseJsonOutput(string jsonlOutput)
    {
        if (string.IsNullOrWhiteSpace(jsonlOutput))
            return null;

        var assistantContents = new List<string>();
        bool foundAnyJsonlEvent = false;

        // Use StringReader instead of Split to avoid allocating a massive string array
        // on large JSONL output (observed 61 MB of MCP session events in production).
        using var reader = new StringReader(jsonlOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    continue;

                foundAnyJsonlEvent = true;
                var type = typeElement.GetString();

                // The definitive response is in assistant.message (non-ephemeral, has full content).
                // Skip ephemeral streaming-delta events so we only capture finalized messages.
                if (type != "assistant.message")
                    continue;
                if (root.TryGetProperty("ephemeral", out var eph) && eph.ValueKind == JsonValueKind.True)
                    continue;
                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.String)
                    continue;

                var text = contentElement.GetString();
                if (!string.IsNullOrEmpty(text))
                    assistantContents.Add(text);
            }
            catch (JsonException)
            {
                // Not valid JSON — skip this line
            }
        }

        if (assistantContents.Count == 0)
        {
            // Distinguish "valid JSONL but no assistant content" from "not JSONL at all".
            // Returning "" prevents the caller's ?? fallback from passing raw JSONL
            // through the text-mode parser (which caused a 61 MB PMSpec.md of raw MCP events).
            return foundAnyJsonlEvent ? string.Empty : null;
        }

        // Join with blank-line separator so each message starts a fresh Markdown block.
        return string.Join("\n\n", assistantContents).Trim();
    }

    /// <summary>
    /// Parses JSONL output and extracts usage statistics from the <c>result</c> event.
    /// </summary>
    public static CopilotCliUsage? ParseJsonUsage(string jsonlOutput)
    {
        if (string.IsNullOrWhiteSpace(jsonlOutput))
            return null;

        var lines = jsonlOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    continue;

                if (typeElement.GetString() != "result")
                    continue;

                var usage = new CopilotCliUsage();

                if (root.TryGetProperty("sessionId", out var sid))
                    usage.SessionId = sid.GetString();

                if (root.TryGetProperty("exitCode", out var ec))
                    usage.ExitCode = ec.GetInt32();

                if (root.TryGetProperty("usage", out var usageData))
                {
                    if (usageData.TryGetProperty("premiumRequests", out var pr))
                        usage.PremiumRequests = pr.GetInt32();
                    if (usageData.TryGetProperty("totalApiDurationMs", out var ad))
                        usage.TotalApiDurationMs = ad.GetInt64();
                    if (usageData.TryGetProperty("sessionDurationMs", out var sd))
                        usage.SessionDurationMs = sd.GetInt64();
                }

                return usage;
            }
            catch (JsonException)
            {
                // Skip malformed lines
            }
            catch (InvalidOperationException)
            {
                // Skip lines with unexpected element types (e.g., Array where Object expected)
            }
        }

        return null;
    }
}

/// <summary>Usage statistics extracted from the copilot CLI result event.</summary>
public class CopilotCliUsage
{
    public string? SessionId { get; set; }
    public int ExitCode { get; set; }
    public int PremiumRequests { get; set; }
    public long TotalApiDurationMs { get; set; }
    public long SessionDurationMs { get; set; }
}
