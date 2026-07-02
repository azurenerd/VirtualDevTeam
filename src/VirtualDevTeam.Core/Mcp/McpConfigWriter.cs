using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtualDevTeam.Core.Mcp;

/// <summary>
/// Writes a per-candidate MCP configuration file that the copilot CLI can load via
/// <c>--mcp-config &lt;path&gt;</c>. The output path MUST live outside the candidate's
/// git worktree so that <c>git add -A</c> during patch extraction does not pull the
/// control-plane file into the candidate's diff.
/// </summary>
/// <remarks>
/// The writer is invocation-agnostic: callers supply the exact command/args that
/// should be used to spawn the server. In production this is typically
/// <c>dotnet &lt;path-to-VirtualDevTeam.McpServer.dll&gt;</c>; tests can spawn a fake binary.
///
/// File writes are atomic (write-to-temp then replace) to avoid partial-file reads
/// by a concurrent reader.
/// </remarks>
public static class McpConfigWriter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Build the MCP config JSON document for a single worktree-reader server entry.
    /// Pure function — no I/O. Exposed for testing.
    /// </summary>
    public static JsonObject BuildConfig(
        string serverName,
        string command,
        IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(args);

        var argsArray = new JsonArray();
        foreach (var a in args) argsArray.Add(a);

        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [serverName] = new JsonObject
                {
                    ["command"] = command,
                    ["args"] = argsArray,
                },
            },
        };
    }

    /// <summary>
    /// Atomically write an MCP config file for a single candidate. The candidate's
    /// <paramref name="candidateWorktreeRoot"/> is injected as the <c>--root</c> arg
    /// of the server invocation (appended after <paramref name="fixedArgs"/>).
    /// </summary>
    /// <param name="outputConfigPath">Absolute path where <c>mcp.json</c> will be written. MUST be outside any candidate worktree.</param>
    /// <returns>The absolute path of the written file.</returns>
    public static string WriteScopedConfig(
        string outputConfigPath,
        string serverName,
        string command,
        IReadOnlyList<string> fixedArgs,
        string candidateWorktreeRoot,
        IEnumerable<string>? forbiddenRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateWorktreeRoot);

        var outputFull = Path.GetFullPath(outputConfigPath);
        var worktreeFull = Path.GetFullPath(candidateWorktreeRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Config file must NOT live inside the candidate worktree — it would get picked
        // up by `git add -A` during patch extraction and contaminate the diff.
        if (IsWithin(outputFull, worktreeFull))
        {
            throw new InvalidOperationException(
                $"MCP config path must not live inside the candidate worktree. " +
                $"output='{outputFull}' worktree='{worktreeFull}'");
        }

        if (forbiddenRoots is not null)
        {
            foreach (var f in forbiddenRoots)
            {
                if (string.IsNullOrEmpty(f)) continue;
                var fFull = Path.GetFullPath(f).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (IsWithin(outputFull, fFull))
                {
                    throw new InvalidOperationException(
                        $"MCP config path must not live inside forbidden root '{fFull}'. output='{outputFull}'");
                }
            }
        }

        var args = new List<string>(fixedArgs ?? Array.Empty<string>())
        {
            "--root",
            worktreeFull,
        };

        var config = BuildConfig(serverName, command, args);
        var json = config.ToJsonString(s_json);

        var dir = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: temp file in same dir, then replace.
        var temp = outputFull + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(outputFull))
                File.Replace(temp, outputFull, destinationBackupFileName: null);
            else
                File.Move(temp, outputFull);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        return outputFull;
    }

    private static bool IsWithin(string candidatePath, string rootFull)
    {
        if (string.IsNullOrEmpty(rootFull)) return false;
        var rootWithSep = rootFull + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidatePath, rootFull, StringComparison.OrdinalIgnoreCase);
    }
}
