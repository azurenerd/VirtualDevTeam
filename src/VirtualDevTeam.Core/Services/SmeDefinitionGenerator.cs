namespace VirtualDevTeam.Core.Services;

using VirtualDevTeam.Core.Configuration;

/// <summary>
/// AI-powered generator that creates SME agent definitions from a task description
/// and the available MCP server catalog. Used by the PE for reactive SME spawning.
/// </summary>
public class SmeDefinitionGenerator
{
    private readonly McpServerRegistry _mcpRegistry;
    private readonly SMEAgentDefinitionService _definitionService;

    public SmeDefinitionGenerator(
        McpServerRegistry mcpRegistry,
        SMEAgentDefinitionService definitionService)
    {
        _mcpRegistry = mcpRegistry ?? throw new ArgumentNullException(nameof(mcpRegistry));
        _definitionService = definitionService ?? throw new ArgumentNullException(nameof(definitionService));
    }

    /// <summary>
    /// Builds a prompt for the AI to generate an SME agent definition from a task description.
    /// </summary>
    public string BuildDefinitionGenerationPrompt(string taskDescription, string? additionalContext = null)
    {
        var prompt = new System.Text.StringBuilder();

        prompt.AppendLine("# Generate SME Agent Definition");
        prompt.AppendLine();
        prompt.AppendLine("Based on the following task that requires specialist expertise, generate an SME agent definition.");
        prompt.AppendLine();
        prompt.AppendLine("## Task Description");
        prompt.AppendLine(taskDescription);
        prompt.AppendLine();

        if (!string.IsNullOrWhiteSpace(additionalContext))
        {
            prompt.AppendLine("## Additional Context");
            prompt.AppendLine(additionalContext);
            prompt.AppendLine();
        }

        // Available MCP servers
        var mcpServers = _mcpRegistry.GetAll();
        if (mcpServers.Any())
        {
            prompt.AppendLine("## Available MCP Servers");
            prompt.AppendLine("Select any that would help this specialist:");
            foreach (var (name, server) in mcpServers)
            {
                prompt.AppendLine($"- **{name}**: {server.Description}");
                if (server.ProvidedCapabilities.Count > 0)
                    prompt.AppendLine($"  Capabilities: {string.Join(", ", server.ProvidedCapabilities)}");
            }
            prompt.AppendLine();
        }

        prompt.AppendLine("## Required Output Format");
        prompt.AppendLine("Respond with a JSON object:");
        prompt.AppendLine("```json");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"roleName\": \"Descriptive specialist name\",");
        prompt.AppendLine("  \"systemPrompt\": \"You are a specialist in... Your job is to...\",");
        prompt.AppendLine("  \"capabilities\": [\"capability1\", \"capability2\"],");
        prompt.AppendLine("  \"mcpServers\": [\"server-name\"],");
        prompt.AppendLine("  \"knowledgeLinks\": [\"https://relevant-docs.example.com\"],");
        prompt.AppendLine("  \"modelTier\": \"standard\",");
        prompt.AppendLine("  \"workflowMode\": \"OneShot\",");
        prompt.AppendLine("  \"justification\": \"Why this specialist is needed for this task\"");
        prompt.AppendLine("}");
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("Guidelines:");
        prompt.AppendLine("- Make the systemPrompt detailed and specific to the task domain");
        prompt.AppendLine("- Only include MCP servers that are genuinely useful");
        prompt.AppendLine("- Use OneShot mode for single-task specialists, OnDemand for reusable ones");
        prompt.AppendLine("- Prefer 'standard' model tier unless the task requires premium reasoning");

        return prompt.ToString();
    }

    /// <summary>
    /// Parses the AI's response into an SME agent definition.
    /// </summary>
    public SMEAgentDefinition? ParseDefinition(string aiResponse, string createdByAgentId)
    {
        try
        {
            var json = ExtractJson(aiResponse);
            if (json is null) return null;

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            var raw = System.Text.Json.JsonSerializer.Deserialize<RawSmeDefinition>(json, options);
            if (raw is null || string.IsNullOrWhiteSpace(raw.RoleName)) return null;

            var slug = raw.RoleName.ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

            return new SMEAgentDefinition
            {
                DefinitionId = $"pe-{slug}-{Guid.NewGuid():N}"[..Math.Min(48, $"pe-{slug}-{Guid.NewGuid():N}".Length)],
                RoleName = raw.RoleName,
                SystemPrompt = raw.SystemPrompt ?? $"You are a {raw.RoleName} specialist.",
                McpServers = raw.McpServers ?? [],
                KnowledgeLinks = raw.KnowledgeLinks ?? [],
                Capabilities = raw.Capabilities ?? [],
                ModelTier = raw.ModelTier ?? "standard",
                WorkflowMode = raw.WorkflowMode ?? SmeWorkflowMode.OneShot,
                CreatedByAgentId = createdByAgentId,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if an existing template matches the task capabilities.
    /// Returns the best match or null.
    /// </summary>
    public async Task<SMEAgentDefinition?> FindMatchingTemplateAsync(
        IReadOnlyList<string> requiredCapabilities, CancellationToken ct = default)
    {
        if (requiredCapabilities.Count == 0) return null;

        var matches = await _definitionService.FindByCapabilitiesAsync(requiredCapabilities, ct);
        return matches.FirstOrDefault();
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start = text.IndexOf('\n', start) + 1;
            var end = text.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
                return text[start..end].Trim();
        }

        start = text.IndexOf('{');
        if (start >= 0)
        {
            var end = text.LastIndexOf('}');
            if (end > start)
                return text[start..(end + 1)];
        }

        return null;
    }
}

internal sealed class RawSmeDefinition
{
    public string? RoleName { get; set; }
    public string? SystemPrompt { get; set; }
    public List<string>? Capabilities { get; set; }
    public List<string>? McpServers { get; set; }
    public List<string>? KnowledgeLinks { get; set; }
    public string? ModelTier { get; set; }
    public SmeWorkflowMode? WorkflowMode { get; set; }
    public string? Justification { get; set; }
}
