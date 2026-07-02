using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtualDevTeam.McpServer;

/// <summary>
/// Minimal stdio JSON-RPC loop implementing the subset of MCP the copilot CLI exercises:
/// initialize, notifications/initialized, tools/list, tools/call. Newline-delimited JSON
/// (one message per line), as per the MCP stdio transport. Shuts down cleanly when stdin
/// closes.
/// </summary>
internal sealed class StdioJsonRpcServer
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly WorkspaceTools _tools;
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public StdioJsonRpcServer(TextReader stdin, TextWriter stdout, WorkspaceTools tools)
    {
        _in = stdin;
        _out = stdout;
        _tools = tools;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = await _in.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? req;
            try
            {
                req = JsonNode.Parse(line);
            }
            catch
            {
                WriteRaw(BuildError(null, -32700, "Parse error"));
                continue;
            }
            if (req is not JsonObject obj) continue;

            var id = obj["id"];
            var method = obj["method"]?.GetValue<string>();
            var @params = obj["params"] as JsonObject;

            try
            {
                switch (method)
                {
                    case "initialize":
                        WriteResult(id, BuildInitializeResult());
                        break;
                    case "notifications/initialized":
                        // notification — no response
                        break;
                    case "tools/list":
                        WriteResult(id, BuildToolsList());
                        break;
                    case "tools/call":
                        WriteResult(id, HandleToolCall(@params));
                        break;
                    case "ping":
                        WriteResult(id, new JsonObject());
                        break;
                    default:
                        if (id is not null)
                            WriteRaw(BuildError(id, -32601, $"Method not found: {method}"));
                        break;
                }
            }
            catch (McpToolException ex)
            {
                WriteRaw(BuildError(id, ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                WriteRaw(BuildError(id, -32603, $"Internal error: {ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    private static JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "virtualdevteam-workspace-reader",
            ["version"] = "0.1.0",
        },
    };

    private static JsonObject BuildToolsList()
    {
        var tools = new JsonArray
        {
            ToolDescriptor("read_file",
                "Read the contents of a file within the scoped workspace root.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path inside the workspace root." },
                    },
                    ["required"] = new JsonArray { "path" },
                }),
            ToolDescriptor("list_directory",
                "List entries in a directory within the scoped workspace root.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path (empty for root)." },
                    },
                }),
            ToolDescriptor("search_code",
                "Search file contents with a regex pattern across the workspace root. Skips binary files. Results are capped.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["pattern"] = new JsonObject { ["type"] = "string" },
                        ["glob"] = new JsonObject { ["type"] = "string", ["description"] = "Optional filename glob filter (e.g. *.cs)." },
                    },
                    ["required"] = new JsonArray { "pattern" },
                }),
        };
        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject ToolDescriptor(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    private JsonObject HandleToolCall(JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>() ?? throw new McpToolException(-32602, "Missing tool name");
        var args = @params?["arguments"] as JsonObject ?? new JsonObject();

        string text = name switch
        {
            "read_file" => _tools.ReadFile(args["path"]?.GetValue<string>() ?? ""),
            "list_directory" => _tools.ListDirectory(args["path"]?.GetValue<string>() ?? ""),
            "search_code" => _tools.SearchCode(
                args["pattern"]?.GetValue<string>() ?? "",
                args["glob"]?.GetValue<string>()),
            _ => throw new McpToolException(-32601, $"Unknown tool: {name}"),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
        };
    }

    private void WriteResult(JsonNode? id, JsonNode result)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        WriteRaw(env);
    }

    private static JsonObject BuildError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private void WriteRaw(JsonObject envelope)
    {
        var text = envelope.ToJsonString(s_json);
        lock (_writeLock)
        {
            _out.WriteLine(text);
            _out.Flush();
        }
    }
}

internal sealed class McpToolException : Exception
{
    public int Code { get; }
    public McpToolException(int code, string message) : base(message) { Code = code; }
}
