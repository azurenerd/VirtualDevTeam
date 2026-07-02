using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.GitHub;

/// <summary>
/// Maps absolute file line numbers to GitHub diff positions.
/// GitHub's PR Review API uses "position" (1-based offset within the diff,
/// NOT counting the @@ hunk header) to place inline comments.
/// </summary>
public static partial class DiffPositionMapper
{
    /// <summary>
    /// Maps a new-file (right-side) line number to a diff position.
    /// Returns null if the line is not present in the diff.
    /// </summary>
    /// <param name="patch">The unified diff patch string (from GitHub's PullRequestFile.Patch).</param>
    /// <param name="newFileLine">The 1-based line number in the new version of the file.</param>
    public static int? MapLineToPosition(string? patch, int newFileLine)
    {
        if (string.IsNullOrEmpty(patch) || newFileLine < 1)
            return null;

        var lines = patch.Split('\n');
        var position = 0; // 1-based offset within the diff (incremented for every line including @@ headers)
        var currentNewLine = 0; // tracks the current new-file line number

        foreach (var line in lines)
        {
            if (line.StartsWith("@@"))
            {
                position++; // @@ header counts as a position
                var match = HunkHeaderRegex().Match(line);
                if (match.Success)
                {
                    currentNewLine = int.Parse(match.Groups[1].Value) - 1; // will be incremented on next non-deletion line
                }
                continue;
            }

            if (position == 0)
                continue; // skip lines before first hunk header (shouldn't happen with GitHub patches)

            position++;

            if (line.StartsWith('-'))
            {
                // Deletion — doesn't advance new-file line counter
            }
            else if (line.StartsWith('+'))
            {
                currentNewLine++;
                if (currentNewLine == newFileLine)
                    return position;
            }
            else
            {
                // Context line — advances both old and new line counters
                currentNewLine++;
                if (currentNewLine == newFileLine)
                    return position;
            }
        }

        return null; // line not found in the diff
    }

    /// <summary>
    /// Gets all new-file line numbers present in the diff (lines that can receive inline comments).
    /// </summary>
    public static IReadOnlyList<int> GetCommentableLines(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
            return [];

        var result = new List<int>();
        var lines = patch.Split('\n');
        var currentNewLine = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("@@"))
            {
                var match = HunkHeaderRegex().Match(line);
                if (match.Success)
                    currentNewLine = int.Parse(match.Groups[1].Value) - 1;
                continue;
            }

            if (line.StartsWith('-'))
            {
                // deletion — no new line
            }
            else if (line.StartsWith('+'))
            {
                currentNewLine++;
                result.Add(currentNewLine);
            }
            else
            {
                currentNewLine++;
                result.Add(currentNewLine);
            }
        }

        return result;
    }

    // Matches +newStart in hunk headers like @@ -10,5 +20,8 @@
    [GeneratedRegex(@"\+(\d+)")]
    private static partial Regex HunkHeaderRegex();
}
