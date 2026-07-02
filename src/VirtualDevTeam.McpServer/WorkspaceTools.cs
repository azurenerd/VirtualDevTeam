using System.Text;
using System.Text.RegularExpressions;

namespace VirtualDevTeam.McpServer;

/// <summary>
/// Read-only tool implementations scoped to a single workspace root. Every path input
/// is normalized, rejected if rooted/UNC/device/control-char-bearing, combined with the
/// root, then canonicalized and prefix-checked. Ancestor reparse points fail closed.
/// </summary>
internal sealed class WorkspaceTools
{
    private const int MaxFileBytes = 1_048_576;             // 1 MB per file
    private const int MaxSearchFiles = 10_000;
    private const int MaxSearchMatches = 500;
    private const int MaxSearchSnippetChars = 200;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _rootFull;
    private readonly string _rootWithSep;

    public WorkspaceTools(string rootFull)
    {
        _rootFull = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootWithSep = _rootFull + Path.DirectorySeparatorChar;
    }

    public string ReadFile(string relative)
    {
        var full = Resolve(relative, allowEmpty: false);
        if (!File.Exists(full))
            throw new McpToolException(-32001, $"Not found: {relative}");
        var info = new FileInfo(full);
        if (info.Length > MaxFileBytes)
            throw new McpToolException(-32002, $"File exceeds {MaxFileBytes} bytes: {relative}");
        try
        {
            return File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            throw new McpToolException(-32003, $"Read error: {ex.Message}");
        }
    }

    public string ListDirectory(string relative)
    {
        var full = Resolve(relative, allowEmpty: true);
        if (!Directory.Exists(full))
            throw new McpToolException(-32001, $"Not found: {relative}");

        var sb = new StringBuilder();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (IsReparsePoint(dir)) continue;
                sb.Append("DIR  ").AppendLine(Path.GetFileName(dir));
            }
            foreach (var file in Directory.EnumerateFiles(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("FILE ").AppendLine(Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            throw new McpToolException(-32003, $"List error: {ex.Message}");
        }
        return sb.ToString();
    }

    public string SearchCode(string pattern, string? glob)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new McpToolException(-32602, "pattern is required");

        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new McpToolException(-32602, $"Invalid regex: {ex.Message}");
        }

        var globPattern = string.IsNullOrEmpty(glob) ? "*" : glob;
        var sb = new StringBuilder();
        int filesSeen = 0, matches = 0;

        foreach (var file in SafeEnumerateFiles(_rootFull, globPattern))
        {
            if (filesSeen++ >= MaxSearchFiles) break;
            if (matches >= MaxSearchMatches) break;

            FileInfo info;
            try { info = new FileInfo(file); } catch { continue; }
            if (info.Length > MaxFileBytes) continue;

            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            if (LooksBinary(content)) continue;

            int lineNo = 1;
            foreach (var line in content.Split('\n'))
            {
                if (matches >= MaxSearchMatches) break;
                bool hit;
                try
                {
                    hit = re.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new McpToolException(-32004, "Regex timed out; refine pattern");
                }
                if (hit)
                {
                    var rel = Path.GetRelativePath(_rootFull, file).Replace('\\', '/');
                    var snippet = line.Length > MaxSearchSnippetChars
                        ? line[..MaxSearchSnippetChars] + "…"
                        : line;
                    sb.Append(rel).Append(':').Append(lineNo).Append(": ")
                      .Append(snippet.TrimEnd('\r')).Append('\n');
                    matches++;
                }
                lineNo++;
            }
        }
        if (matches == 0) return "(no matches)\n";
        return sb.ToString();
    }

    private IEnumerable<string> SafeEnumerateFiles(string dir, string globPattern)
    {
        // Manual recursion so we can skip reparse-point directories. Directory.EnumerateFiles
        // with SearchOption.AllDirectories would traverse junctions/symlinks.
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current, globPattern); }
            catch { continue; }
            foreach (var f in files) yield return f;

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(current); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (IsReparsePoint(sub)) continue;
                // Skip common heavy/irrelevant dirs.
                var name = Path.GetFileName(sub);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }
        }
    }

    private string Resolve(string relative, bool allowEmpty)
    {
        if (relative is null) throw new McpToolException(-32602, "path is required");
        var trimmed = relative.Replace('\\', '/').Trim();
        if (trimmed.Length == 0)
        {
            if (allowEmpty) return _rootFull;
            throw new McpToolException(-32602, "path must not be empty");
        }

        // Reject control chars.
        foreach (var c in trimmed)
        {
            if (c < 0x20 || c == 0x7F)
                throw new McpToolException(-32602, "path contains control characters");
        }

        // Reject rooted/UNC/device paths BEFORE Path.Combine (Combine would silently reroot).
        if (Path.IsPathRooted(trimmed))
            throw new McpToolException(-32603, "absolute paths are not permitted");
        if (trimmed.StartsWith("//") || trimmed.StartsWith(@"\\"))
            throw new McpToolException(-32603, "UNC/device paths are not permitted");
        if (trimmed.Contains(":"))
            throw new McpToolException(-32603, "drive-qualified paths are not permitted");

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_rootFull, trimmed));
        }
        catch (Exception ex)
        {
            throw new McpToolException(-32603, $"cannot resolve path: {ex.Message}");
        }

        var fullNorm = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullNorm.Equals(_rootFull, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpToolException(-32603, "path escapes workspace root");
        }

        // Reparse-point walk: any ancestor between root and full that is a reparse point
        // could redirect reads outside root even if the lexical check passes.
        if (HasReparsePointAncestor(full))
            throw new McpToolException(-32603, "path traverses a reparse point");

        return full;
    }

    private bool HasReparsePointAncestor(string fullPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(parent))
            {
                if (string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), _rootFull,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                if (IsReparsePoint(parent)) return true;
                parent = Path.GetDirectoryName(parent);
            }
            return true; // escaped root without hitting it — treat as unsafe
        }
        catch
        {
            return true;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksBinary(string content)
    {
        int probe = Math.Min(content.Length, 4096);
        int controls = 0;
        for (int i = 0; i < probe; i++)
        {
            char c = content[i];
            if (c == '\0') return true;
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') controls++;
        }
        return controls > probe / 32;
    }
}
