using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Pre-flight gate that determines whether a task is likely to produce meaningful
/// screenshots. Avoids wasting 30–120s on app startup + Playwright capture for
/// pure backend / library / config tasks that have no visual output.
/// </summary>
public static class MediaCaptureGate
{
    private static readonly HashSet<string> UiFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".razor", ".cshtml", ".tsx", ".jsx", ".css", ".scss", ".less",
        ".html", ".htm", ".vue", ".svelte", ".xaml"
    };

    private static readonly Regex UiKeywordPattern = new(
        @"\b(dashboard|page|component|ui|frontend|view|form|button|table|chart|layout|" +
        @"sidebar|navbar|nav\s?bar|modal|dialog|toast|card|panel|widget|menu|tab|" +
        @"header|footer|screenshot|visual|render|display|screen|blazor|react|angular|vue)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApiDocsPattern = new(
        @"\b(swagger|openapi|api[-\s]?docs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VisualVerificationPattern = new(
        @"##\s*Visual\s+Verification",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns <c>true</c> if the task looks like it has visual output worth screenshotting.
    /// Defaults to <c>true</c> when <paramref name="taskDescription"/> is null/empty (cautious — try to capture).
    /// </summary>
    /// <param name="taskDescription">Issue body or PR description.</param>
    /// <param name="changedFiles">Optional list of file paths changed in the PR/workspace.</param>
    public static bool ShouldCapture(string? taskDescription, IReadOnlyList<string>? changedFiles = null)
    {
        // Default to true if no task description (cautious — try to capture)
        if (string.IsNullOrWhiteSpace(taskDescription) && (changedFiles is null || changedFiles.Count == 0))
            return true;

        // 1. Visual Verification section in task description
        if (!string.IsNullOrWhiteSpace(taskDescription) && VisualVerificationPattern.IsMatch(taskDescription))
            return true;

        // 2. UI-related file extensions in changed files
        if (changedFiles is not null)
        {
            foreach (var file in changedFiles)
            {
                var ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext) && UiFileExtensions.Contains(ext))
                    return true;
            }
        }

        // 3. UI keywords in task description
        if (!string.IsNullOrWhiteSpace(taskDescription) && UiKeywordPattern.IsMatch(taskDescription))
            return true;

        // 4. Swagger/API docs (these have visual output too)
        if (!string.IsNullOrWhiteSpace(taskDescription) && ApiDocsPattern.IsMatch(taskDescription))
            return true;

        // No signals found — pure backend/library/config task
        return false;
    }
}
