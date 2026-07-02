using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Mcp;
using VirtualDevTeam.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Default implementation of <see cref="IChatCompletionRunner"/>.
/// Resolves a kernel from <see cref="ModelRegistry"/>, sets the ambient
/// <see cref="AgentCallContext"/>, invokes chat completion, and extracts
/// the text response — all in one call.
/// </summary>
/// <remarks>
/// When MCP servers with <see cref="McpServerDefinition.AllowedTools"/> are configured,
/// automatically injects the invocation context so every LLM call can use them.
/// Strategy overrides (via <see cref="AgentCallContext.PushInvocationContext"/>) are
/// merged — the strategy's tools are added alongside the global MCP tools.
///
/// Also installs a per-agent scratch working directory under the configured
/// <see cref="WorkspaceConfig.RootPath"/> when no caller has already supplied one.
/// The <see cref="CopilotCliProcessManager"/> refuses to spawn without an explicit
/// CWD (source-tree-leak guard), so agents that don't own a worktree (Researcher,
/// Architect, PM, etc.) would otherwise crash on every LLM call.
/// </remarks>
public sealed class ChatCompletionRunner : IChatCompletionRunner
{
    private readonly ModelRegistry _modelRegistry;
    private readonly McpServerRegistry _mcpRegistry;
    private readonly ILogger<ChatCompletionRunner> _logger;
    private readonly IOptions<VirtualDevTeamConfig>? _config;

    // Cached config JSON + tool list (rebuilt when registry changes).
    private string? _cachedMcpConfigJson;
    private IReadOnlyList<string>? _cachedAllowedTools;
    private int _cachedServerCount = -1;

    public ChatCompletionRunner(
        ModelRegistry modelRegistry,
        McpServerRegistry mcpRegistry,
        ILogger<ChatCompletionRunner> logger,
        IOptions<VirtualDevTeamConfig>? config = null)
    {
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _mcpRegistry = mcpRegistry ?? throw new ArgumentNullException(nameof(mcpRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kernel = _modelRegistry.GetKernel(request.ModelTier, request.AgentId);
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var previousAgentId = AgentCallContext.CurrentAgentId;
        IDisposable? cwdScope = null;
        IDisposable? mcpScope = null;
        try
        {
            if (request.AgentId is not null)
            {
                AgentCallContext.CurrentAgentId = request.AgentId;
            }

            // Ensure every agent LLM call has a safe scratch CWD if none was
            // already pushed by a strategy / engineer worktree. The Copilot CLI
            // process manager refuses to spawn without one (source-tree-leak guard).
            cwdScope = EnsureAgentScratchCwd(request.AgentId);

            // Auto-inject MCP context when global servers are configured and no
            // strategy has already pushed a context (merge if one exists).
            mcpScope = TryPushMcpContext();

            var response = await chatService.GetChatMessageContentsAsync(request.History, cancellationToken: ct);
            return response.FirstOrDefault()?.Content ?? "";
        }
        finally
        {
            mcpScope?.Dispose();
            cwdScope?.Dispose();
            AgentCallContext.CurrentAgentId = previousAgentId;
        }
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(
        string systemPrompt,
        string userPrompt,
        string modelTier,
        string? agentId = null,
        CancellationToken ct = default)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        return await InvokeAsync(new ChatCompletionRequest
        {
            History = history,
            ModelTier = modelTier,
            AgentId = agentId
        }, ct);
    }

    /// <summary>
    /// Ensures the ambient invocation context has an <see cref="CopilotCliInvocationContext.OverrideWorkingDirectory"/>
    /// set so that <see cref="CopilotCliProcessManager"/>'s spawn-time safety guard passes.
    /// </summary>
    /// <remarks>
    /// Returns null when no push is necessary:
    /// <list type="bullet">
    ///   <item>An outer caller (engineer worktree, strategy framework) has already supplied a CWD — preserve it.</item>
    ///   <item>The runner was constructed without an <see cref="IOptions{TOptions}"/> (test path) — let the guard surface the error if it fires.</item>
    ///   <item>The configured <see cref="WorkspaceConfig.RootPath"/> is blank — same.</item>
    /// </list>
    /// Otherwise pushes a context whose CWD is a per-agent scratch directory under
    /// <c>{workspaceRoot}/.scratch/{sanitizedAgentId}/</c>. The directory is created on
    /// demand and is reused across calls for the same agent.
    /// </remarks>
    private IDisposable? EnsureAgentScratchCwd(string? agentId)
    {
        var existing = AgentCallContext.CurrentInvocationContext;
        if (!string.IsNullOrEmpty(existing?.OverrideWorkingDirectory))
            return null;

        var workspaceRoot = _config?.Value.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        string subdir = string.IsNullOrEmpty(agentId)
            ? "anonymous"
            : SanitizeForPath(agentId);
        var scratchDir = Path.Combine(workspaceRoot, ".scratch", subdir);

        try
        {
            Directory.CreateDirectory(scratchDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create agent scratch directory '{ScratchDir}'. CLI safety guard may reject the call.",
                scratchDir);
            return null;
        }

        var ctx = new CopilotCliInvocationContext(
            AdditionalMcpConfigJson: existing?.AdditionalMcpConfigJson,
            AllowedMcpTools: existing?.AllowedMcpTools,
            OverrideWorkingDirectory: scratchDir,
            AllowFileEdits: existing?.AllowFileEdits ?? false,
            Attachments: existing?.Attachments);

        return AgentCallContext.PushInvocationContext(ctx);
    }

    private static string SanitizeForPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Pushes an MCP invocation context if global MCP servers with AllowedTools exist.
    /// Merges with any existing context from a strategy that already pushed one.
    /// Returns a disposable scope (null if nothing was pushed).
    /// </summary>
    private IDisposable? TryPushMcpContext()
    {
        var (configJson, allowedTools) = GetMcpConfig();
        if (configJson is null || allowedTools is null || allowedTools.Count == 0)
            return null;

        var existing = AgentCallContext.CurrentInvocationContext;
        if (existing is not null)
        {
            // Strategy already pushed a context — merge our global tools alongside theirs.
            var mergedTools = MergeTools(existing.AllowedMcpTools, allowedTools);
            var mergedConfig = MergeConfigJson(existing.AdditionalMcpConfigJson, configJson);
            var merged = new CopilotCliInvocationContext(
                AdditionalMcpConfigJson: mergedConfig,
                AllowedMcpTools: mergedTools,
                OverrideWorkingDirectory: existing.OverrideWorkingDirectory,
                AllowFileEdits: existing.AllowFileEdits,
                Attachments: existing.Attachments);
            return AgentCallContext.PushInvocationContext(merged);
        }

        var ctx = new CopilotCliInvocationContext(
            AdditionalMcpConfigJson: configJson,
            AllowedMcpTools: allowedTools);
        return AgentCallContext.PushInvocationContext(ctx);
    }

    /// <summary>
    /// Builds (and caches) the inline MCP config JSON and allowed tool list
    /// from all configured servers that have AllowedTools entries.
    /// </summary>
    private (string? ConfigJson, IReadOnlyList<string>? AllowedTools) GetMcpConfig()
    {
        var allServers = _mcpRegistry.GetAll();
        var serversWithTools = allServers.Values
            .Where(s => s.AllowedTools.Count > 0 && !string.IsNullOrEmpty(s.Command))
            .ToList();

        if (serversWithTools.Count == 0)
            return (null, null);

        // Simple cache: rebuild only when server count changes.
        if (_cachedServerCount == serversWithTools.Count && _cachedMcpConfigJson is not null)
            return (_cachedMcpConfigJson, _cachedAllowedTools);

        // Build inline JSON: {"mcpServers":{"workiq":{"command":"npx","args":[...]},...}}
        var mcpServersNode = new System.Text.Json.Nodes.JsonObject();
        var toolList = new List<string>();

        foreach (var server in serversWithTools)
        {
            var argsArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var arg in server.Args) argsArray.Add(arg);

            var serverNode = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = server.Command,
                ["args"] = argsArray,
            };

            if (server.Env.Count > 0)
            {
                var envNode = new System.Text.Json.Nodes.JsonObject();
                foreach (var (key, value) in server.Env)
                    envNode[key] = value;
                serverNode["env"] = envNode;
            }

            mcpServersNode[server.Name] = serverNode;

            // Grant the server name as --allow-tool (server-level grant)
            toolList.Add(server.Name);
        }

        var configRoot = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = mcpServersNode,
        };

        _cachedMcpConfigJson = configRoot.ToJsonString();
        _cachedAllowedTools = toolList;
        _cachedServerCount = serversWithTools.Count;

        _logger.LogDebug("Built global MCP config for {Count} server(s): {Names}",
            serversWithTools.Count, string.Join(", ", toolList));

        return (_cachedMcpConfigJson, _cachedAllowedTools);
    }

    private static IReadOnlyList<string> MergeTools(
        IReadOnlyList<string>? existing,
        IReadOnlyList<string> global)
    {
        if (existing is null || existing.Count == 0)
            return global;

        var merged = new HashSet<string>(existing, StringComparer.Ordinal);
        foreach (var tool in global)
            merged.Add(tool);
        return merged.ToList();
    }

    private static string MergeConfigJson(string? existingJson, string globalJson)
    {
        if (string.IsNullOrEmpty(existingJson))
            return globalJson;

        // Parse both and merge mcpServers objects (global entries added if not already present)
        try
        {
            var existingNode = System.Text.Json.Nodes.JsonNode.Parse(existingJson)?.AsObject();
            var globalNode = System.Text.Json.Nodes.JsonNode.Parse(globalJson)?.AsObject();
            if (existingNode is null) return globalJson;
            if (globalNode is null) return existingJson;

            var existingServers = existingNode["mcpServers"]?.AsObject();
            var globalServers = globalNode["mcpServers"]?.AsObject();
            if (existingServers is null || globalServers is null)
                return existingJson;

            // Add global servers that aren't already defined by the strategy
            foreach (var (name, value) in globalServers)
            {
                if (!existingServers.ContainsKey(name) && value is not null)
                {
                    existingServers[name] = value.DeepClone();
                }
            }

            return existingNode.ToJsonString();
        }
        catch
        {
            // If merge fails, strategy's config takes priority
            return existingJson;
        }
    }
}
