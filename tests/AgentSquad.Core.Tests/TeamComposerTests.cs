using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Tests;

public class TeamComposerTests
{
    private readonly TeamCompositionProposal _validProposal;
    private readonly TestOptionsMonitor _optionsMonitor;
    private readonly McpServerRegistry _mcpRegistry;
    private readonly SMEAgentDefinitionService _definitionService;
    private readonly AgentTeamComposer _composer;

    public TeamComposerTests()
    {
        _optionsMonitor = new TestOptionsMonitor();
        _optionsMonitor.CurrentValue = new AgentSquadConfig
        {
            McpServers = new Dictionary<string, McpServerDefinition>
            {
                { "test-server", new McpServerDefinition { Name = "test-server", Description = "Test Server" } }
            },
            SmeAgents = new SmeAgentsConfig { Enabled = true, MaxTotalSmeAgents = 5, PersistDefinitions = false, Templates = new() },
            Agents = new AgentConfigs()
        };

        var mcpRegistry = new McpServerRegistry(_optionsMonitor, NullLogger<McpServerRegistry>.Instance);
        var securityPolicy = new McpServerSecurityPolicy(mcpRegistry);
        _definitionService = new SMEAgentDefinitionService(_optionsMonitor, securityPolicy, NullLogger<SMEAgentDefinitionService>.Instance);
        _composer = new AgentTeamComposer(_definitionService, mcpRegistry, Options.Create(_optionsMonitor.CurrentValue));
        _mcpRegistry = mcpRegistry;

        _validProposal = new TeamCompositionProposal
        {
            ProjectSummary = "Test Project",
            BuiltInAgents = new List<BuiltInAgentRequest>
            {
                new() { Role = AgentRole.Researcher, Count = 1, Justification = "Research needed" }
            },
            ExistingTemplateIds = new List<string> { "template-1" },
            NewSmeAgents = new List<SMEAgentDefinition>
            {
                new()
                {
                    DefinitionId = "sme-specialist-123",
                    RoleName = "Specialist",
                    SystemPrompt = "You are a specialist.",
                    Capabilities = new List<string> { "expertise" },
                    McpServers = new List<string>(),
                    KnowledgeLinks = new List<string>(),
                    ModelTier = "standard",
                    WorkflowMode = SmeWorkflowMode.OnDemand,
                    CreatedAt = DateTime.UtcNow
                }
            },
            Rationale = "This team is optimal"
        };
    }

    // ===== ParseProposal Tests =====

    [Fact]
    public void ParseProposal_ReturnsNull_ForEmptyInput()
    {
        var result = _composer.ParseProposal("", "agent-pm");
        Assert.Null(result);
    }

    [Fact]
    public void ParseProposal_ReturnsNull_ForInvalidJson()
    {
        var result = _composer.ParseProposal("This is not JSON at all", "agent-pm");
        Assert.Null(result);
    }

    [Fact]
    public void ParseProposal_ParsesCodeBlockJson()
    {
        var json = """
        ```json
        {
            "projectSummary": "Test Project",
            "rationale": "Rationale",
            "builtInAgents": [
                { "role": "Researcher", "count": 1, "justification": "Research" }
            ],
            "existingTemplateIds": [],
            "newSmeAgents": []
        }
        ```
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal("Test Project", result.ProjectSummary);
        Assert.Single(result.BuiltInAgents);
    }

    [Fact]
    public void ParseProposal_ParsesBareJson()
    {
        var json = """
        {
            "projectSummary": "Bare JSON Test",
            "rationale": "This works",
            "builtInAgents": [],
            "existingTemplateIds": [],
            "newSmeAgents": []
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal("Bare JSON Test", result.ProjectSummary);
    }

    [Fact]
    public void ParseProposal_SetsProjectSummary()
    {
        var json = """
        {
            "projectSummary": "My Project Summary",
            "rationale": "Good team",
            "builtInAgents": [],
            "existingTemplateIds": [],
            "newSmeAgents": []
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal("My Project Summary", result.ProjectSummary);
    }

    [Fact]
    public void ParseProposal_ParsesBuiltInAgents()
    {
        var json = """
        {
            "projectSummary": "Test",
            "rationale": "Rationale",
            "builtInAgents": [
                { "role": "Researcher", "count": 2, "justification": "Need research" },
                { "role": "Architect", "count": 1, "justification": "Design needed" }
            ],
            "existingTemplateIds": [],
            "newSmeAgents": []
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal(2, result.BuiltInAgents.Count);
        Assert.Equal(AgentRole.Researcher, result.BuiltInAgents[0].Role);
        Assert.Equal(2, result.BuiltInAgents[0].Count);
        Assert.Equal("Need research", result.BuiltInAgents[0].Justification);
    }

    [Fact]
    public void ParseProposal_ParsesExistingTemplateIds()
    {
        var json = """
        {
            "projectSummary": "Test",
            "rationale": "Rationale",
            "builtInAgents": [],
            "existingTemplateIds": ["template-1", "template-2"],
            "newSmeAgents": []
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal(2, result.ExistingTemplateIds.Count);
        Assert.Contains("template-1", result.ExistingTemplateIds);
        Assert.Contains("template-2", result.ExistingTemplateIds);
    }

    [Fact]
    public void ParseProposal_GeneratesDefinitionIds_ForNewSmeAgents()
    {
        var json = """
        {
            "projectSummary": "Test",
            "rationale": "Rationale",
            "builtInAgents": [],
            "existingTemplateIds": [],
            "newSmeAgents": [
                {
                    "roleName": "Security Auditor",
                    "systemPrompt": "You are a security auditor.",
                    "capabilities": ["security-audit"],
                    "mcpServers": [],
                    "knowledgeLinks": [],
                    "modelTier": "standard",
                    "workflowMode": "OnDemand",
                    "justification": "Security review needed"
                }
            ]
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Single(result.NewSmeAgents);
        var sme = result.NewSmeAgents[0];
        Assert.NotNull(sme.DefinitionId);
        Assert.StartsWith("sme-", sme.DefinitionId);
        Assert.True(sme.DefinitionId.Length <= 48, "DefinitionId should be truncated to 48 chars");
    }

    [Fact]
    public void ParseProposal_SetsCreatedByAgentId_OnNewSmeAgents()
    {
        var json = """
        {
            "projectSummary": "Test",
            "rationale": "Rationale",
            "builtInAgents": [],
            "existingTemplateIds": [],
            "newSmeAgents": [
                {
                    "roleName": "Specialist",
                    "systemPrompt": "Specialist prompt",
                    "capabilities": ["specialized"],
                    "mcpServers": [],
                    "knowledgeLinks": [],
                    "modelTier": "standard",
                    "workflowMode": "OnDemand"
                }
            ]
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm-123");

        Assert.NotNull(result);
        Assert.Single(result.NewSmeAgents);
        Assert.Equal("agent-pm-123", result.NewSmeAgents[0].CreatedByAgentId);
    }

    [Fact]
    public void ParseProposal_HandlesNullOptionalFields()
    {
        var json = """
        {
            "projectSummary": "Test Project",
            "rationale": "Good team"
        }
        """;

        var result = _composer.ParseProposal(json, "agent-pm");

        Assert.NotNull(result);
        Assert.Equal("Test Project", result.ProjectSummary);
        Assert.Empty(result.BuiltInAgents);
        Assert.Empty(result.ExistingTemplateIds);
        Assert.Empty(result.NewSmeAgents);
    }

    // ===== GenerateTeamCompositionDoc Tests =====

    [Fact]
    public void GenerateTeamCompositionDoc_IncludesProjectSummary()
    {
        var doc = _composer.GenerateTeamCompositionDoc(_validProposal);

        Assert.Contains("Test Project", doc);
        Assert.Contains("**Project:**", doc);
    }

    [Fact]
    public void GenerateTeamCompositionDoc_IncludesBuiltInAgentTable()
    {
        var doc = _composer.GenerateTeamCompositionDoc(_validProposal);

        Assert.Contains("## Built-in Agents", doc);
        Assert.Contains("| Role | Count | Justification |", doc);
        Assert.Contains("Research needed", doc);
    }

    [Fact]
    public void GenerateTeamCompositionDoc_IncludesSmeTemplates()
    {
        var proposal = new TeamCompositionProposal
        {
            ProjectSummary = "Test",
            BuiltInAgents = new List<BuiltInAgentRequest>(),
            ExistingTemplateIds = new List<string> { "template-1", "template-2" },
            NewSmeAgents = new List<SMEAgentDefinition>(),
            Rationale = "Rationale"
        };

        var doc = _composer.GenerateTeamCompositionDoc(proposal);

        Assert.Contains("## Activated SME Templates", doc);
        Assert.Contains("template-1", doc);
        Assert.Contains("template-2", doc);
    }

    [Fact]
    public void GenerateTeamCompositionDoc_IncludesNewSmeAgents()
    {
        var proposal = new TeamCompositionProposal
        {
            ProjectSummary = "Test",
            BuiltInAgents = new List<BuiltInAgentRequest>(),
            ExistingTemplateIds = new List<string>(),
            NewSmeAgents = new List<SMEAgentDefinition>
            {
                new()
                {
                    DefinitionId = "sme-specialist-123",
                    RoleName = "Security Auditor",
                    SystemPrompt = "You are a security auditor.",
                    Capabilities = new List<string> { "security-audit", "penetration-testing" },
                    McpServers = new List<string> { "test-server" },
                    KnowledgeLinks = new List<string>(),
                    ModelTier = "premium",
                    WorkflowMode = SmeWorkflowMode.OnDemand,
                    CreatedAt = DateTime.UtcNow
                }
            },
            Rationale = "Rationale"
        };

        var doc = _composer.GenerateTeamCompositionDoc(proposal);

        Assert.Contains("## Specialist Engineers & SME Agents", doc);
        Assert.Contains("### Security Auditor", doc);
        Assert.Contains("security-audit", doc);
        Assert.Contains("test-server", doc);
        Assert.Contains("premium", doc);
    }

    // ===== Helper Classes =====

    private class TestOptionsMonitor : IOptionsMonitor<AgentSquadConfig>
    {
        public AgentSquadConfig CurrentValue { get; set; } = new();

        public AgentSquadConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AgentSquadConfig, string?> listener) => null;
    }
}
