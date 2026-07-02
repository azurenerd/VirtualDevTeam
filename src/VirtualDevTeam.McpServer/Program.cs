using System.Text.Json;
using System.Text.Json.Nodes;
using VirtualDevTeam.McpServer;

// Entry point: stdio JSON-RPC MCP server, scoped read-only to a single root directory.
// Usage: VirtualDevTeam.McpServer --root <path>

var rootArg = ParseArg(args, "--root");
if (rootArg is null)
{
    Console.Error.WriteLine("VirtualDevTeam.McpServer: --root <path> is required.");
    return 2;
}

string rootFull;
try
{
    rootFull = Path.GetFullPath(rootArg);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"VirtualDevTeam.McpServer: invalid --root: {ex.Message}");
    return 2;
}
if (!Directory.Exists(rootFull))
{
    Console.Error.WriteLine($"VirtualDevTeam.McpServer: --root does not exist: {rootFull}");
    return 2;
}

var server = new StdioJsonRpcServer(Console.In, Console.Out, new WorkspaceTools(rootFull));
await server.RunAsync(CancellationToken.None);
return 0;

static string? ParseArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.Ordinal))
            return args[i + 1];
    }
    return null;
}
