using System.Text.Json;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Classifies CLI output lines for the Agent Log Viewer.
/// JSONL-first: parses event type from JSON when available, falls back to content patterns.
/// </summary>
public static class CliLineClassifier
{
    /// <summary>
    /// Extended classification result with optional tool metadata.
    /// </summary>
    public record struct ClassifyResult(
        LogLineClassification Classification,
        string DisplayText,
        string? ToolName = null,
        bool? ToolSuccess = null,
        string? ToolOutput = null);

    /// <summary>
    /// Classify a single stdout line. Handles both JSONL and text modes.
    /// Returns the classification and a display-friendly text extraction.
    /// </summary>
    public static (LogLineClassification Classification, string DisplayText) Classify(string rawLine)
    {
        var result = ClassifyFull(rawLine);
        return (result.Classification, result.DisplayText);
    }

    /// <summary>
    /// Classify with full tool metadata preserved.
    /// </summary>
    public static ClassifyResult ClassifyFull(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return new(LogLineClassification.Raw, string.Empty);

        // Strip ANSI escape codes first — CLI output often has ANSI prefixes
        // even with --no-color, which breaks JSONL detection (StartsWith('{'))
        var trimmed = CliOutputParser.StripAnsiCodes(rawLine).Trim();

        // JSONL mode: line starts with '{' — parse event type
        if (trimmed.StartsWith('{'))
            return ClassifyJsonlFull(trimmed);

        // Text mode fallback: content pattern matching
        var (classification, text) = ClassifyText(trimmed);
        return new(classification, text);
    }

    /// <summary>
    /// Classify a stderr line — always Error classification.
    /// </summary>
    public static (LogLineClassification Classification, string DisplayText) ClassifyStderr(string rawLine)
    {
        var text = CliOutputParser.StripAnsiCodes(rawLine).Trim();
        return (LogLineClassification.Error, text);
    }

    private static (LogLineClassification Classification, string DisplayText) ClassifyJsonl(string json)
    {
        var full = ClassifyJsonlFull(json);
        return (full.Classification, full.DisplayText);
    }

    private static ClassifyResult ClassifyJsonlFull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return new(LogLineClassification.Raw, TruncateForDisplay(json));

            var eventType = typeElement.GetString() ?? "";

            return eventType switch
            {
                "assistant.message" or "assistant.message.completed" =>
                    new(LogLineClassification.Assistant, ExtractAssistantContent(root)),

                "assistant.message_delta" =>
                    new(LogLineClassification.Assistant, ExtractDeltaContent(root)),

                "assistant.reasoning" =>
                    new(LogLineClassification.System, ExtractContent(root, "Reasoning...")),

                "tool.execution_start" =>
                    ExtractToolStartFull(root),

                "tool.execution_complete" =>
                    ExtractToolCompleteFull(root),

                "result" =>
                    new(LogLineClassification.Lifecycle, ExtractResultSummary(root)),

                "session.start" or "session.resume" =>
                    new(LogLineClassification.System, $"Session {eventType.Split('.')[1]}"),

                "error" =>
                    new(LogLineClassification.Error, ExtractContent(root, "Error")),

                _ => new(LogLineClassification.Raw, $"[{eventType}]")
            };
        }
        catch
        {
            return new(LogLineClassification.Raw, TruncateForDisplay(json));
        }
    }

    private static (LogLineClassification Classification, string DisplayText) ClassifyText(string text)
    {
        // Strip ANSI for pattern matching
        var clean = CliOutputParser.StripAnsiCodes(text);

        // Tool activity
        if (clean.StartsWith("Using tool:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Running:", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("⚡"))
            return (LogLineClassification.ToolStart, clean);

        // Tool completion
        if (clean.StartsWith("✓") || clean.StartsWith("✗") ||
            clean.StartsWith("Done:", StringComparison.OrdinalIgnoreCase))
            return (LogLineClassification.ToolComplete, clean);

        // Errors
        if (clean.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Error:", StringComparison.Ordinal) ||
            clean.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            return (LogLineClassification.Error, clean);

        // CLI chrome — skip for display at low verbosity
        if (IsCliChrome(clean))
            return (LogLineClassification.Raw, clean);

        // System/lifecycle
        if (clean.StartsWith("Session ID:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Model:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Tip:", StringComparison.OrdinalIgnoreCase))
            return (LogLineClassification.System, clean);

        // In text mode, longer lines are general output — classify as Raw so
        // the AI Only filter actually works (previously everything > 10 chars
        // became Assistant, making AI Only and Activity show the same content)
        return (LogLineClassification.Raw, clean);
    }

    private static bool IsCliChrome(string text)
    {
        if (text.Length == 0) return true;
        // Separator lines
        if (text.All(c => c is '─' or '═' or '-' or '=' or ' ')) return true;
        // Prompt markers
        if (text.StartsWith('>') || text.StartsWith('$')) return true;
        // Banner/version lines
        if (text.StartsWith("GitHub Copilot", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("copilot v", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ExtractAssistantContent(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("content", out var content))
            {
                var text = content.GetString() ?? "";
                return TruncateForDisplay(text);
            }
        }
        return "(assistant message)";
    }

    private static string ExtractDeltaContent(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("deltaContent", out var delta))
                return delta.GetString() ?? "";
        }
        return "";
    }

    private static string ExtractToolStart(JsonElement root)
    {
        var result = ExtractToolStartFull(root);
        return result.DisplayText;
    }

    private static ClassifyResult ExtractToolStartFull(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            var toolName = "";
            if (data.TryGetProperty("toolName", out var tn))
                toolName = tn.GetString() ?? "";
            else if (data.TryGetProperty("name", out var n))
                toolName = n.GetString() ?? "";

            var summary = "";
            if (data.TryGetProperty("intentionSummary", out var intent))
                summary = intent.GetString() ?? "";

            if (!string.IsNullOrEmpty(summary))
                return new(LogLineClassification.ToolStart, $"⚙️ {toolName}: {TruncateForDisplay(summary, 120)}", ToolName: toolName);
            return new(LogLineClassification.ToolStart, $"⚙️ {toolName}", ToolName: toolName);
        }
        return new(LogLineClassification.ToolStart, "⚙️ (tool)");
    }

    private static string ExtractToolComplete(JsonElement root)
    {
        var result = ExtractToolCompleteFull(root);
        return result.DisplayText;
    }

    private static ClassifyResult ExtractToolCompleteFull(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            var toolName = "";
            if (data.TryGetProperty("toolName", out var tn))
                toolName = tn.GetString() ?? "";

            var success = true;
            if (data.TryGetProperty("success", out var s))
                success = s.GetBoolean();

            // Preserve tool output for collapsible display
            string? toolOutput = null;
            if (data.TryGetProperty("result", out var result))
                toolOutput = result.GetString();
            else if (data.TryGetProperty("output", out var output))
                toolOutput = output.GetString();

            var icon = success ? "✓" : "✗";
            return new(LogLineClassification.ToolComplete, $"{icon} {toolName}",
                ToolName: toolName, ToolSuccess: success,
                ToolOutput: toolOutput is not null ? TruncateForDisplay(toolOutput) : null);
        }
        return new(LogLineClassification.ToolComplete, "✓ (tool complete)");
    }

    private static string ExtractResultSummary(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("usage", out var usage))
            {
                var input = usage.TryGetProperty("inputTokens", out var it) ? it.GetInt32() : 0;
                var output = usage.TryGetProperty("outputTokens", out var ot) ? ot.GetInt32() : 0;
                return $"📊 Result: {input + output} tokens ({input} in / {output} out)";
            }
        }
        return "📊 Result";
    }

    private static string ExtractContent(JsonElement root, string fallback)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("content", out var content))
                return TruncateForDisplay(content.GetString() ?? fallback);
            if (data.TryGetProperty("message", out var message))
                return TruncateForDisplay(message.GetString() ?? fallback);
        }
        return fallback;
    }

    private static string TruncateForDisplay(string text, int maxLength = 4096)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "…";
    }

    /// <summary>
    /// Returns true if this classification should be shown at the given verbosity level.
    /// </summary>
    public static bool IsVisibleAtVerbosity(LogLineClassification classification, LogVerbosity verbosity)
    {
        return verbosity switch
        {
            LogVerbosity.Low => classification is LogLineClassification.Assistant or LogLineClassification.CallBoundary,
            LogVerbosity.Medium => classification is not LogLineClassification.Raw,
            LogVerbosity.High => true,
            _ => true
        };
    }
}

/// <summary>
/// Verbosity levels for the agent log viewer.
/// </summary>
public enum LogVerbosity
{
    /// <summary>AI responses only (🟣).</summary>
    Low,
    /// <summary>AI + tool activity + system (⚙️).</summary>
    Medium,
    /// <summary>Everything including raw CLI output (📋).</summary>
    High
}
