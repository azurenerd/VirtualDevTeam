namespace AgentSquad.Core.Services;

using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;

/// <summary>
/// Produces a team composition proposal based on project docs, available agent catalog,
/// and MCP server capabilities. Used by the PM agent after PMSpec is finalized.
/// </summary>
public class AgentTeamComposer
{
    private readonly SMEAgentDefinitionService _definitionService;
    private readonly McpServerRegistry _mcpRegistry;
    private readonly AgentSquadConfig _config;

    public AgentTeamComposer(
        SMEAgentDefinitionService definitionService,
        McpServerRegistry mcpRegistry,
        Microsoft.Extensions.Options.IOptions<AgentSquadConfig> config)
    {
        _definitionService = definitionService ?? throw new ArgumentNullException(nameof(definitionService));
        _mcpRegistry = mcpRegistry ?? throw new ArgumentNullException(nameof(mcpRegistry));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Builds the context prompt that the PM agent will use to generate a team composition proposal.
    /// Contains: built-in agent catalog, SME templates, MCP server capabilities, project constraints.
    /// </summary>
    public async Task<string> BuildTeamCompositionPromptAsync(
        string projectDescription,
        string? researchContent,
        string? pmSpecContent,
        CancellationToken ct = default)
    {
        var prompt = new System.Text.StringBuilder();

        prompt.AppendLine("# Team Composition Analysis");
        prompt.AppendLine();
        prompt.AppendLine("You are analyzing a software project to determine the optimal team composition.");
        prompt.AppendLine("You must decide which built-in agents to activate and whether any specialist (SME) agents are needed.");
        prompt.AppendLine();

        // Project context
        prompt.AppendLine("## Project Description");
        prompt.AppendLine(Truncate(projectDescription, 3000));
        prompt.AppendLine();

        if (!string.IsNullOrWhiteSpace(researchContent))
        {
            prompt.AppendLine("## Research Findings");
            prompt.AppendLine(Truncate(researchContent, 2000));
            prompt.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(pmSpecContent))
        {
            prompt.AppendLine("## PM Specification (Summary)");
            prompt.AppendLine(Truncate(pmSpecContent, 3000));
            prompt.AppendLine();
        }

        // Built-in agent catalog
        prompt.AppendLine("## Available Built-in Agents");
        prompt.AppendLine("These agents are always available. Indicate which should be activated:");
        prompt.AppendLine();
        prompt.AppendLine(GetBuiltInAgentCatalog());

        // SME templates
        var allDefs = await _definitionService.GetAllAsync();
        var templates = allDefs.Values
            .Where(d => d.CreatedByAgentId is null) // Only pre-configured templates
            .ToList();

        if (templates.Count > 0)
        {
            prompt.AppendLine("## Available SME Agent Templates");
            prompt.AppendLine("Pre-configured specialist agents that can be activated on demand:");
            prompt.AppendLine();
            foreach (var t in templates)
            {
                prompt.AppendLine($"- **{t.RoleName}** (ID: {t.DefinitionId})");
                prompt.AppendLine($"  Capabilities: {string.Join(", ", t.Capabilities)}");
                prompt.AppendLine($"  MCP Servers: {string.Join(", ", t.McpServers)}");
                prompt.AppendLine($"  Mode: {t.WorkflowMode}");
                prompt.AppendLine();
            }
        }

        // MCP server capabilities
        var mcpServers = _mcpRegistry.GetAll();
        if (mcpServers.Any())
        {
            prompt.AppendLine("## Available MCP Servers (Tools)");
            prompt.AppendLine("These tool servers can be assigned to agents:");
            prompt.AppendLine();
            foreach (var (name, server) in mcpServers)
            {
                prompt.AppendLine($"- **{name}**: {server.Description}");
                if (server.ProvidedCapabilities.Count > 0)
                    prompt.AppendLine($"  Capabilities: {string.Join(", ", server.ProvidedCapabilities)}");
            }
            prompt.AppendLine();
        }

        // Custom agents already configured
        var customAgents = _config.Agents?.CustomAgents?.Where(c => c.Enabled).ToList() ?? [];
        if (customAgents.Count > 0)
        {
            prompt.AppendLine("## Pre-configured Custom Agents");
            foreach (var ca in customAgents)
            {
                prompt.AppendLine($"- **{ca.Name}** (Tier: {ca.ModelTier})");
                if (!string.IsNullOrWhiteSpace(ca.RoleDescription))
                    prompt.AppendLine($"  Role: {Truncate(ca.RoleDescription, 200)}");
            }
            prompt.AppendLine();
        }

        // Constraints
        prompt.AppendLine("## Constraints");
        prompt.AppendLine($"- Maximum total SME agents: {_config.SmeAgents.MaxTotalSmeAgents}");
        prompt.AppendLine($"- SME agents enabled: {_config.SmeAgents.Enabled}");
        prompt.AppendLine($"- Allow agent-created definitions: {_config.SmeAgents.AllowAgentCreatedDefinitions}");
        prompt.AppendLine();

        // Output format
        prompt.AppendLine("## Required Output Format");
        prompt.AppendLine("Respond with a JSON object matching this schema:");
        prompt.AppendLine("```json");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"projectSummary\": \"One-line summary of what this project does\",");
        prompt.AppendLine("  \"rationale\": \"2-3 sentences on why this team composition is optimal\",");
        prompt.AppendLine("  \"builtInAgents\": [");
        prompt.AppendLine("    { \"role\": \"Researcher\", \"count\": 1, \"justification\": \"why\", \"roleDescription\": \"Optional: focus areas or persona adjustments for this agent\" },");
        prompt.AppendLine("    { \"role\": \"Architect\", \"count\": 1, \"justification\": \"why\" }");
        prompt.AppendLine("  ],");
        prompt.AppendLine("  \"existingTemplateIds\": [\"template-id-1\"],");
        prompt.AppendLine("  \"newSmeAgents\": [");
        prompt.AppendLine("    {");
        prompt.AppendLine("      \"roleName\": \"Name of the specialist\",");
        prompt.AppendLine("      \"systemPrompt\": \"You are a specialist in...\",");
        prompt.AppendLine("      \"capabilities\": [\"cap1\", \"cap2\"],");
        prompt.AppendLine("      \"mcpServers\": [\"server-name\"],");
        prompt.AppendLine("      \"knowledgeLinks\": [\"https://docs.example.com\"],");
        prompt.AppendLine("      \"modelTier\": \"standard\",");
        prompt.AppendLine("      \"workflowMode\": \"OnDemand\",");
        prompt.AppendLine("      \"justification\": \"Why this specialist is needed\"");
        prompt.AppendLine("    }");
        prompt.AppendLine("  ]");
        prompt.AppendLine("}");
        prompt.AppendLine("```");
        prompt.AppendLine();
        prompt.AppendLine("IMPORTANT: Only propose SME agents when the project genuinely requires specialist expertise ");
        prompt.AppendLine("beyond what the built-in agents provide. The built-in team (PM, Researcher, Architect, Software Engineer, TE) ");
        prompt.AppendLine("handles most standard software development projects well. Only add SME agents for domains like ");
        prompt.AppendLine("security auditing, specialized databases, ML/AI, compliance, etc.");

        return prompt.ToString();
    }

    /// <summary>
    /// Parses the AI's team composition response into a structured proposal.
    /// Generates DefinitionIds for any new SME agents proposed.
    /// </summary>
    public TeamCompositionProposal? ParseProposal(string aiResponse, string createdByAgentId)
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

            var raw = System.Text.Json.JsonSerializer.Deserialize<RawTeamCompositionProposal>(json, options);
            if (raw is null) return null;

            // Convert raw SME proposals to proper SMEAgentDefinitions with generated IDs
            var newSmeAgents = raw.NewSmeAgents?.Select(sme => new SMEAgentDefinition
            {
                DefinitionId = $"sme-{ToSlug(sme.RoleName)}-{Guid.NewGuid():N}"[..Math.Min(48, $"sme-{ToSlug(sme.RoleName)}-{Guid.NewGuid():N}".Length)],
                RoleName = sme.RoleName,
                SystemPrompt = sme.SystemPrompt ?? $"You are a {sme.RoleName} specialist.",
                McpServers = sme.McpServers ?? [],
                KnowledgeLinks = sme.KnowledgeLinks ?? [],
                Capabilities = sme.Capabilities ?? [],
                ModelTier = sme.ModelTier ?? "standard",
                WorkflowMode = sme.WorkflowMode ?? SmeWorkflowMode.OnDemand,
                CreatedByAgentId = createdByAgentId,
                CreatedAt = DateTime.UtcNow
            }).ToList() ?? [];

            return new TeamCompositionProposal
            {
                ProjectSummary = raw.ProjectSummary ?? "Unknown project",
                BuiltInAgents = raw.BuiltInAgents ?? [],
                ExistingTemplateIds = raw.ExistingTemplateIds ?? [],
                NewSmeAgents = newSmeAgents,
                Rationale = raw.Rationale ?? "No rationale provided"
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Generates a TeamComposition.md document from an approved proposal.
    /// </summary>
    public string GenerateTeamCompositionDoc(TeamCompositionProposal proposal)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Team Composition");
        sb.AppendLine();
        sb.AppendLine($"**Project:** {proposal.ProjectSummary}");
        sb.AppendLine();
        sb.AppendLine("## Rationale");
        sb.AppendLine(proposal.Rationale);
        sb.AppendLine();

        sb.AppendLine("## Built-in Agents");
        sb.AppendLine("| Role | Count | Justification |");
        sb.AppendLine("|------|-------|---------------|");
        foreach (var agent in proposal.BuiltInAgents)
        {
            sb.AppendLine($"| {agent.Role} | {agent.Count} | {agent.Justification} |");
        }
        sb.AppendLine();

        if (proposal.ExistingTemplateIds.Count > 0)
        {
            sb.AppendLine("## Activated SME Templates");
            foreach (var id in proposal.ExistingTemplateIds)
                sb.AppendLine($"- {id}");
            sb.AppendLine();
        }

        if (proposal.NewSmeAgents.Count > 0)
        {
            sb.AppendLine("## New SME Agents");
            foreach (var sme in proposal.NewSmeAgents)
            {
                sb.AppendLine($"### {sme.RoleName}");
                sb.AppendLine($"- **Tier:** {sme.ModelTier}");
                sb.AppendLine($"- **Mode:** {sme.WorkflowMode}");
                sb.AppendLine($"- **Capabilities:** {string.Join(", ", sme.Capabilities)}");
                if (sme.McpServers.Count > 0)
                    sb.AppendLine($"- **MCP Servers:** {string.Join(", ", sme.McpServers)}");
                sb.AppendLine();
            }
        }

        sb.AppendLine($"---");
        sb.AppendLine($"_Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC_");
        return sb.ToString();
    }

    private static string GetBuiltInAgentCatalog()
    {
        return """
        | Role | Capabilities | Model Tier |
        |------|-------------|------------|
        | ProgramManager | Project coordination, PMSpec creation, user story decomposition, PR reviews, stakeholder communication | premium |
        | Researcher | Technical research, technology evaluation, competitive analysis, feasibility assessment | standard |
        | Architect | System design, architecture documents, technology decisions, API design, data modeling | premium |
        | SoftwareEngineer | Engineering plan creation, task decomposition, feature implementation, code review, technical leadership, quality standards | premium |
        | TestEngineer | Test strategy, test implementation, E2E testing, quality assurance, test automation | standard |
        """;
    }

    private static string? ExtractJson(string text)
    {
        // Try to find JSON in code blocks
        var start = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start = text.IndexOf('\n', start) + 1;
            var end = text.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
                return text[start..end].Trim();
        }

        // Try to find bare JSON object
        start = text.IndexOf('{');
        if (start >= 0)
        {
            var end = text.LastIndexOf('}');
            if (end > start)
                return text[start..(end + 1)];
        }

        return null;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "\n... [truncated]";
    }

    private static string ToSlug(string name)
    {
        return name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');
    }
}

/// <summary>
/// Raw JSON DTO for parsing AI responses before converting to domain types.
/// </summary>
internal sealed class RawTeamCompositionProposal
{
    public string? ProjectSummary { get; set; }
    public List<BuiltInAgentRequest>? BuiltInAgents { get; set; }
    public List<string>? ExistingTemplateIds { get; set; }
    public List<RawSmeAgentProposal>? NewSmeAgents { get; set; }
    public string? Rationale { get; set; }
}

internal sealed class RawSmeAgentProposal
{
    public required string RoleName { get; set; }
    public string? SystemPrompt { get; set; }
    public List<string>? Capabilities { get; set; }
    public List<string>? McpServers { get; set; }
    public List<string>? KnowledgeLinks { get; set; }
    public string? ModelTier { get; set; }
    public SmeWorkflowMode? WorkflowMode { get; set; }
    public string? Justification { get; set; }
}
