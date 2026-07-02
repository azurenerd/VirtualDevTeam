using System.Text.Json.Nodes;
using VirtualDevTeam.Core.Mcp;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class McpConfigWriterTests : IDisposable
{
    private readonly string _tmpRoot;

    public McpConfigWriterTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "mcp-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpRoot)) Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Fact]
    public void BuildConfig_produces_expected_shape()
    {
        var cfg = McpConfigWriter.BuildConfig(
            serverName: "workspace-reader",
            command: "dotnet",
            args: new[] { "C:\\bin\\VirtualDevTeam.McpServer.dll", "--root", "C:\\wt" });

        var server = cfg["mcpServers"]!["workspace-reader"]!.AsObject();
        Assert.Equal("dotnet", server["command"]!.GetValue<string>());
        var argList = server["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "C:\\bin\\VirtualDevTeam.McpServer.dll", "--root", "C:\\wt" }, argList);
    }

    [Fact]
    public void BuildConfig_rejects_empty_server_name()
    {
        Assert.Throws<ArgumentException>(() =>
            McpConfigWriter.BuildConfig("", "dotnet", new[] { "x" }));
    }

    [Fact]
    public void WriteScopedConfig_writes_outside_worktree_succeeds()
    {
        var worktree = Path.Combine(_tmpRoot, "candidate-a");
        var cfgDir = Path.Combine(_tmpRoot, "runtime", "candidate-a");
        Directory.CreateDirectory(worktree);
        var outputPath = Path.Combine(cfgDir, "mcp.json");

        var written = McpConfigWriter.WriteScopedConfig(
            outputConfigPath: outputPath,
            serverName: "workspace-reader",
            command: "dotnet",
            fixedArgs: new[] { "/bin/server.dll" },
            candidateWorktreeRoot: worktree);

        Assert.True(File.Exists(written));
        var json = JsonNode.Parse(File.ReadAllText(written))!.AsObject();
        var args = json["mcpServers"]!["workspace-reader"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal("/bin/server.dll", args[0]);
        Assert.Equal("--root", args[1]);
        Assert.Equal(Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar), args[2]);
    }

    [Fact]
    public void WriteScopedConfig_rejects_output_inside_worktree()
    {
        var worktree = Path.Combine(_tmpRoot, "candidate-a");
        Directory.CreateDirectory(worktree);
        var bad = Path.Combine(worktree, ".candidate-mcp", "mcp.json");

        Assert.Throws<InvalidOperationException>(() =>
            McpConfigWriter.WriteScopedConfig(
                outputConfigPath: bad,
                serverName: "workspace-reader",
                command: "dotnet",
                fixedArgs: Array.Empty<string>(),
                candidateWorktreeRoot: worktree));
    }

    [Fact]
    public void WriteScopedConfig_rejects_output_inside_forbidden_root()
    {
        var worktree = Path.Combine(_tmpRoot, "candidate-a");
        var otherCandidate = Path.Combine(_tmpRoot, "candidate-b");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(otherCandidate);
        var bad = Path.Combine(otherCandidate, "mcp.json");

        Assert.Throws<InvalidOperationException>(() =>
            McpConfigWriter.WriteScopedConfig(
                outputConfigPath: bad,
                serverName: "workspace-reader",
                command: "dotnet",
                fixedArgs: Array.Empty<string>(),
                candidateWorktreeRoot: worktree,
                forbiddenRoots: new[] { otherCandidate }));
    }

    [Fact]
    public void WriteScopedConfig_overwrites_existing_atomically()
    {
        var worktree = Path.Combine(_tmpRoot, "candidate-a");
        Directory.CreateDirectory(worktree);
        var outputPath = Path.Combine(_tmpRoot, "runtime", "mcp.json");

        McpConfigWriter.WriteScopedConfig(outputPath, "workspace-reader", "dotnet",
            new[] { "v1.dll" }, worktree);
        McpConfigWriter.WriteScopedConfig(outputPath, "workspace-reader", "dotnet",
            new[] { "v2.dll" }, worktree);

        var json = JsonNode.Parse(File.ReadAllText(outputPath))!.AsObject();
        var firstArg = json["mcpServers"]!["workspace-reader"]!["args"]!.AsArray()[0]!.GetValue<string>();
        Assert.Equal("v2.dll", firstArg);

        // No stray temp files left behind.
        var stray = Directory.GetFiles(Path.GetDirectoryName(outputPath)!, "mcp.json.tmp-*");
        Assert.Empty(stray);
    }

    [Fact]
    public async Task WriteScopedConfig_concurrent_candidates_do_not_cross_contaminate()
    {
        // p2-test-mcp-race: launch 8 concurrent writes against 8 distinct candidates.
        // Each must end with its own --root value; no candidate's config may reference
        // another's worktree, and all output paths must be independent.
        const int N = 8;
        var tasks = new Task<(string cfgPath, string worktree)>[N];
        for (int i = 0; i < N; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(() =>
            {
                var worktree = Path.Combine(_tmpRoot, "candidates", $"c{idx:D2}");
                Directory.CreateDirectory(worktree);
                var outPath = Path.Combine(_tmpRoot, "runtime", $"c{idx:D2}", "mcp.json");
                var w = McpConfigWriter.WriteScopedConfig(outPath, "workspace-reader",
                    "dotnet", new[] { "server.dll" }, worktree);
                return (w, worktree);
            });
        }
        var results = await Task.WhenAll(tasks);

        foreach (var (cfgPath, worktree) in results)
        {
            var json = JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
            var args = json["mcpServers"]!["workspace-reader"]!["args"]!.AsArray()
                .Select(a => a!.GetValue<string>()).ToArray();
            Assert.Equal("--root", args[1]);
            var expected = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(expected, args[2]);
        }
        // All config paths unique.
        Assert.Equal(N, results.Select(r => r.cfgPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
