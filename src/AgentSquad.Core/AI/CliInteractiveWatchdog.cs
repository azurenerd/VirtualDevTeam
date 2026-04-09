using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.AI;

/// <summary>
/// Monitors a copilot CLI process's output for interactive prompts and auto-responds.
/// Handles edge cases where --allow-all doesn't cover everything: y/n confirmations,
/// selection menus, "press enter" prompts, and credential requests (fail-fast).
/// </summary>
public sealed partial class CliInteractiveWatchdog
{
    private readonly ILogger _logger;
    private readonly bool _autoApprove;

    public CliInteractiveWatchdog(ILogger logger, bool autoApprove = true)
    {
        _logger = logger;
        _autoApprove = autoApprove;
    }

    /// <summary>
    /// Checks a line of output for interactive prompt patterns and returns the auto-response,
    /// or null if no prompt was detected.
    /// </summary>
    public WatchdogAction? DetectPrompt(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();

        // Credential prompts — NEVER auto-fill, fail immediately
        if (IsCredentialPrompt(trimmed))
        {
            return new WatchdogAction
            {
                Type = WatchdogActionType.FailFast,
                Reason = $"Credential prompt detected: {trimmed}",
                Response = null
            };
        }

        // Auth/permission failures — fail fast, but only for short CLI error lines
        // (not AI response content that might discuss auth concepts like "unauthorized access")
        if (trimmed.Length < 200 && IsAuthFailure(trimmed))
        {
            return new WatchdogAction
            {
                Type = WatchdogActionType.FailFast,
                Reason = $"Authentication failure: {trimmed}",
                Response = null
            };
        }

        if (!_autoApprove)
            return null;

        // Yes/No prompts
        if (IsYesNoPrompt(trimmed))
        {
            _logger.LogInformation("Auto-approving y/n prompt: {Prompt}", trimmed);
            return new WatchdogAction
            {
                Type = WatchdogActionType.Respond,
                Reason = "Yes/No prompt",
                Response = "y"
            };
        }

        // Continue/Proceed prompts
        if (IsContinuePrompt(trimmed))
        {
            _logger.LogInformation("Auto-approving continue prompt: {Prompt}", trimmed);
            return new WatchdogAction
            {
                Type = WatchdogActionType.Respond,
                Reason = "Continue prompt",
                Response = "y"
            };
        }

        // Press Enter / Press any key
        if (IsPressEnterPrompt(trimmed))
        {
            _logger.LogInformation("Auto-pressing enter for prompt: {Prompt}", trimmed);
            return new WatchdogAction
            {
                Type = WatchdogActionType.Respond,
                Reason = "Press enter prompt",
                Response = ""
            };
        }

        // Numbered selection (1, 2, 3...) — pick first/default
        if (IsSelectionPrompt(trimmed))
        {
            _logger.LogInformation("Auto-selecting default (1) for selection: {Prompt}", trimmed);
            return new WatchdogAction
            {
                Type = WatchdogActionType.Respond,
                Reason = "Selection prompt",
                Response = "1"
            };
        }

        // Arrow-key selection with markers (▸, ❯, >) — accept default with Enter
        if (IsArrowSelectionPrompt(trimmed))
        {
            _logger.LogInformation("Auto-accepting default selection: {Prompt}", trimmed);
            return new WatchdogAction
            {
                Type = WatchdogActionType.Respond,
                Reason = "Arrow selection prompt",
                Response = ""
            };
        }

        return null;
    }

    // [y/N], [Y/n], (yes/no), y/n
    [GeneratedRegex(@"\[y/n\]|\(yes/no\)|\by/n\b", RegexOptions.IgnoreCase)]
    private static partial Regex YesNoPatternRegex();

    private static bool IsYesNoPrompt(string line) =>
        YesNoPatternRegex().IsMatch(line);

    [GeneratedRegex(@"\b(continue|proceed|confirm)\b.*\?", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuePatternRegex();

    private static bool IsContinuePrompt(string line) =>
        ContinuePatternRegex().IsMatch(line);

    [GeneratedRegex(@"press (enter|any key|return)", RegexOptions.IgnoreCase)]
    private static partial Regex PressEnterPatternRegex();

    private static bool IsPressEnterPrompt(string line) =>
        PressEnterPatternRegex().IsMatch(line);

    [GeneratedRegex(@"(password|token|api.?key|secret|credential)s?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPatternRegex();

    private static bool IsCredentialPrompt(string line) =>
        CredentialPatternRegex().IsMatch(line);

    [GeneratedRegex(@"(^error:.*permission denied|^error:.*unauthorized|^(401|403)\s|(?<!no\s)authentication (failed|required|error)|^not (logged in|authenticated)$|^access denied$)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthFailurePatternRegex();

    private static bool IsAuthFailure(string line) =>
        AuthFailurePatternRegex().IsMatch(line);

    [GeneratedRegex(@"^\s*(select|choose|pick)\b.*:", RegexOptions.IgnoreCase)]
    private static partial Regex SelectionPatternRegex();

    private static bool IsSelectionPrompt(string line) =>
        SelectionPatternRegex().IsMatch(line);

    // Arrow selection indicators: ▸, ❯, ›, or > at start of line
    private static bool IsArrowSelectionPrompt(string line) =>
        line.TrimStart().StartsWith('▸') ||
        line.TrimStart().StartsWith('❯') ||
        line.TrimStart().StartsWith('›');
}

public class WatchdogAction
{
    public required WatchdogActionType Type { get; init; }
    public required string Reason { get; init; }
    /// <summary>The text to send to stdin. Null for FailFast actions.</summary>
    public string? Response { get; init; }
}

public enum WatchdogActionType
{
    /// <summary>Send a response to stdin and continue.</summary>
    Respond,
    /// <summary>Kill the process and throw an exception.</summary>
    FailFast
}
