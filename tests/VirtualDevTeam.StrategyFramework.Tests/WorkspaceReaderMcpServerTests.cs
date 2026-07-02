using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Spawns <c>VirtualDevTeam.McpServer</c> as a subprocess and drives it over stdio with
/// newline-delimited JSON-RPC messages. Verifies the minimum MCP subset (initialize /
/// tools/list / tools/call) plus path-safety enforcement and clean stdin-close exit.
/// </summary>
public class WorkspaceReaderMcpServerTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _serverDllPath;

    public WorkspaceReaderMcpServerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "mcp-srv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "README.md"), "# hello\ntoken-A\n");
        File.WriteAllText(Path.Combine(_workspace, "src", "app.cs"), "class App {}\ntoken-B\n");

        _serverDllPath = LocateServerDll();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static string LocateServerDll()
    {
        // Test assembly lives at .../VirtualDevTeam.StrategyFramework.Tests/bin/Debug/net8.0/.
        // The server DLL sits next to its own bin output. Walk up to repo root, then down.
        var testAsm = Path.GetDirectoryName(typeof(WorkspaceReaderMcpServerTests).Assembly.Location)!;
        var cursor = new DirectoryInfo(testAsm);
        while (cursor != null && !File.Exists(Path.Combine(cursor.FullName, "VirtualDevTeam.sln")))
            cursor = cursor.Parent;
        Assert.NotNull(cursor);

        var dll = Path.Combine(cursor!.FullName, "src", "VirtualDevTeam.McpServer", "bin",
            IsRelease() ? "Release" : "Debug", "net8.0", "VirtualDevTeam.McpServer.dll");
        Assert.True(File.Exists(dll), $"Server DLL not found at {dll}");
        return dll;
    }

    private static bool IsRelease()
    {
#if RELEASE
        return true;
#else
        return false;
#endif
    }

    private Process StartServer()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{_serverDllPath}\" --root \"{_workspace}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi)!;
        Assert.NotNull(p);
        return p;
    }

    private static void Send(Process p, int id, string method, JsonObject? @params = null)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) env["params"] = @params;
        p.StandardInput.WriteLine(env.ToJsonString());
        p.StandardInput.Flush();
    }

    private static void SendNotification(Process p, string method, JsonObject? @params = null)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null) env["params"] = @params;
        p.StandardInput.WriteLine(env.ToJsonString());
        p.StandardInput.Flush();
    }

    private static JsonObject ReadMessage(Process p, int timeoutMs = 10_000)
    {
        var task = p.StandardOutput.ReadLineAsync();
        if (!task.Wait(timeoutMs)) throw new TimeoutException("No response from MCP server");
        var line = task.Result ?? throw new InvalidOperationException("Server closed stdout");
        return JsonNode.Parse(line)!.AsObject();
    }

    [Fact]
    public async Task Initialize_returns_protocol_handshake()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" });
            var resp = ReadMessage(p);

            Assert.Equal(1, resp["id"]!.GetValue<int>());
            var result = resp["result"]!.AsObject();
            Assert.False(string.IsNullOrEmpty(result["protocolVersion"]!.GetValue<string>()));
            Assert.NotNull(result["capabilities"]!.AsObject()["tools"]);
            Assert.Equal("virtualdevteam-workspace-reader",
                result["serverInfo"]!["name"]!.GetValue<string>());
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task ToolsList_returns_three_read_only_tools()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            SendNotification(p, "notifications/initialized");
            Send(p, 2, "tools/list");
            var resp = ReadMessage(p);

            var tools = resp["result"]!["tools"]!.AsArray();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "list_directory", "read_file", "search_code" }, names);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task ReadFile_returns_scoped_content()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            Send(p, 2, "tools/call", new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["path"] = "README.md" },
            });
            var resp = ReadMessage(p);

            var text = resp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Contains("# hello", text);
            Assert.Contains("token-A", text);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("\\\\server\\share\\secret.txt")]
    public async Task ReadFile_rejects_path_escape_attempts(string badPath)
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            Send(p, 2, "tools/call", new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["path"] = badPath },
            });
            var resp = ReadMessage(p);

            Assert.NotNull(resp["error"]);
            Assert.Null(resp["result"]);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task SearchCode_finds_matches_across_files()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            Send(p, 2, "tools/call", new JsonObject
            {
                ["name"] = "search_code",
                ["arguments"] = new JsonObject { ["pattern"] = "token-" },
            });
            var resp = ReadMessage(p);
            var text = resp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Contains("token-A", text);
            Assert.Contains("token-B", text);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task ListDirectory_returns_entries_under_root()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            Send(p, 2, "tools/call", new JsonObject
            {
                ["name"] = "list_directory",
                ["arguments"] = new JsonObject { ["path"] = "" },
            });
            var resp = ReadMessage(p);
            var text = resp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Contains("FILE README.md", text);
            Assert.Contains("DIR  src", text);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task UnknownTool_returns_error()
    {
        using var p = StartServer();
        try
        {
            Send(p, 1, "initialize");
            ReadMessage(p);
            Send(p, 2, "tools/call", new JsonObject
            {
                ["name"] = "shell_execute",
                ["arguments"] = new JsonObject { ["cmd"] = "rm -rf /" },
            });
            var resp = ReadMessage(p);
            Assert.NotNull(resp["error"]);
        }
        finally
        {
            p.StandardInput.Close();
            await WaitForExit(p);
        }
    }

    [Fact]
    public async Task Server_exits_cleanly_when_stdin_closes()
    {
        using var p = StartServer();
        Send(p, 1, "initialize");
        ReadMessage(p);

        p.StandardInput.Close();
        var exited = p.WaitForExit(10_000);
        Assert.True(exited, "Server did not exit within 10s of stdin close");
        Assert.Equal(0, p.ExitCode);
        await Task.CompletedTask;
    }

    private static async Task WaitForExit(Process p)
    {
        try
        {
            if (!p.WaitForExit(5_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { }
        await Task.CompletedTask;
    }
}
