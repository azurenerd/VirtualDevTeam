using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Tests;

public class SmeDefinitionGeneratorTests
{
    private readonly TestOptionsMonitor _optionsMonitor;
    private readonly McpServerRegistry _mcpRegistry;
    private readonly SMEAgentDefinitionService _definitionService;
    private readonly SmeDefinitionGenerator _generator;

    public SmeDefinitionGeneratorTests()
    {
        _optionsMonitor = new TestOptionsMonitor();
        _optionsMonitor.CurrentValue = new VirtualDevTeamConfig
        {
            McpServers = new Dictionary<string, McpServerDefinition>
            {
                { "filesystem", new McpServerDefinition { Name = "filesystem", Description = "File system access" } },
                { "github", new McpServerDefinition { Name = "github", Description = "GitHub API access" } }
            },
            SmeAgents = new SmeAgentsConfig { Enabled = true, MaxTotalSmeAgents = 5, PersistDefinitions = false, Templates = new() },
            Agents = new AgentConfigs()
        };

        _mcpRegistry = new McpServerRegistry(_optionsMonitor, NullLogger<McpServerRegistry>.Instance);
        var securityPolicy = new McpServerSecurityPolicy(_mcpRegistry);
        _definitionService = new SMEAgentDefinitionService(_optionsMonitor, securityPolicy, NullLogger<SMEAgentDefinitionService>.Instance);
        _generator = new SmeDefinitionGenerator(_mcpRegistry, _definitionService);
    }

    // ===== ParseDefinition Tests =====

    [Fact]
    public void ParseDefinition_ReturnsNull_ForEmptyInput()
    {
        var result = _generator.ParseDefinition("", "agent-pe");
        Assert.Null(result);
    }

    [Fact]
    public void ParseDefinition_ReturnsNull_ForInvalidJson()
    {
        var result = _generator.ParseDefinition("This is not valid JSON at all", "agent-pe");
        Assert.Null(result);
    }

    [Fact]
    public void ParseDefinition_ReturnsNull_ForMissingRoleName()
    {
        var json = """
        {
            "systemPrompt": "You are a specialist.",
            "capabilities": ["expertise"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard",
            "workflowMode": "OneShot"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");
        Assert.Null(result);
    }

    [Fact]
    public void ParseDefinition_ParsesValidJson()
    {
        var json = """
        {
            "roleName": "Security Auditor",
            "systemPrompt": "You are a security auditor.",
            "capabilities": ["security-audit", "penetration-testing"],
            "mcpServers": ["filesystem"],
            "knowledgeLinks": ["https://docs.example.com"],
            "modelTier": "premium",
            "workflowMode": "OneShot",
            "justification": "Security review needed"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.Equal("Security Auditor", result.RoleName);
        Assert.Equal("You are a security auditor.", result.SystemPrompt);
        Assert.Equal(2, result.Capabilities.Count);
        Assert.Single(result.McpServers);
        Assert.Equal("filesystem", result.McpServers[0]);
    }

    [Fact]
    public void ParseDefinition_GeneratesDefinitionId_WithPePrefix()
    {
        var json = """
        {
            "roleName": "Database Specialist",
            "systemPrompt": "You are a database specialist.",
            "capabilities": ["database-design"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard",
            "workflowMode": "OneShot"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.NotNull(result.DefinitionId);
        Assert.StartsWith("pe-", result.DefinitionId);
        Assert.True(result.DefinitionId.Length <= 48, "DefinitionId should be truncated to 48 chars");
    }

    [Fact]
    public void ParseDefinition_SetsCreatedByAgentId()
    {
        var json = """
        {
            "roleName": "API Specialist",
            "systemPrompt": "You are an API specialist.",
            "capabilities": ["api-design"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard",
            "workflowMode": "OnDemand"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe-456");

        Assert.NotNull(result);
        Assert.Equal("agent-pe-456", result.CreatedByAgentId);
    }

    [Fact]
    public void ParseDefinition_DefaultsSystemPrompt_WhenNull()
    {
        var json = """
        {
            "roleName": "Infrastructure Engineer",
            "capabilities": ["infrastructure"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard",
            "workflowMode": "OnDemand"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.NotNull(result.SystemPrompt);
        Assert.Contains("Infrastructure Engineer", result.SystemPrompt);
    }

    [Fact]
    public void ParseDefinition_DefaultsToOneShotMode()
    {
        var json = """
        {
            "roleName": "Performance Analyst",
            "systemPrompt": "You are a performance analyst.",
            "capabilities": ["performance-analysis"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.Equal(SmeWorkflowMode.OneShot, result.WorkflowMode);
    }

    [Fact]
    public void ParseDefinition_DefaultsToStandardTier()
    {
        var json = """
        {
            "roleName": "Code Reviewer",
            "systemPrompt": "You are a code reviewer.",
            "capabilities": ["code-review"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "workflowMode": "OnDemand"
        }
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.Equal("standard", result.ModelTier);
    }

    [Fact]
    public void ParseDefinition_ParsesCodeBlockJson()
    {
        var json = """
        ```json
        {
            "roleName": "Documentation Writer",
            "systemPrompt": "You are a documentation writer.",
            "capabilities": ["technical-writing"],
            "mcpServers": [],
            "knowledgeLinks": [],
            "modelTier": "standard",
            "workflowMode": "OneShot"
        }
        ```
        """;

        var result = _generator.ParseDefinition(json, "agent-pe");

        Assert.NotNull(result);
        Assert.Equal("Documentation Writer", result.RoleName);
    }

    // ===== BuildDefinitionGenerationPrompt Tests =====

    [Fact]
    public void BuildDefinitionGenerationPrompt_ContainsTaskDescription()
    {
        var taskDescription = "Analyze database schema optimization";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription);

        Assert.Contains("Generate SME Agent Definition", prompt);
        Assert.Contains(taskDescription, prompt);
        Assert.Contains("## Task Description", prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_IncludesAdditionalContext_WhenProvided()
    {
        var taskDescription = "Create load testing suite";
        var context = "Need to test 10000 concurrent users";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription, context);

        Assert.Contains("## Additional Context", prompt);
        Assert.Contains(context, prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_IncludesMcpServers_WhenAvailable()
    {
        var taskDescription = "Implement file processing";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription);

        Assert.Contains("## Available MCP Servers", prompt);
        Assert.Contains("filesystem", prompt);
        Assert.Contains("github", prompt);
        Assert.Contains("File system access", prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_ContainsJsonOutputFormat()
    {
        var taskDescription = "Test task";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription);

        Assert.Contains("## Required Output Format", prompt);
        Assert.Contains("```json", prompt);
        Assert.Contains("roleName", prompt);
        Assert.Contains("systemPrompt", prompt);
        Assert.Contains("capabilities", prompt);
        Assert.Contains("mcpServers", prompt);
        Assert.Contains("modelTier", prompt);
        Assert.Contains("workflowMode", prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_DoesNotIncludeAdditionalContext_WhenNull()
    {
        var taskDescription = "Test task";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription, null);

        Assert.Contains("## Task Description", prompt);
        // Should not have an Additional Context section
        Assert.DoesNotContain("## Additional Context", prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_DoesNotIncludeAdditionalContext_WhenEmpty()
    {
        var taskDescription = "Test task";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription, "");

        Assert.Contains("## Task Description", prompt);
        // Should not have an Additional Context section
        Assert.DoesNotContain("## Additional Context", prompt);
    }

    [Fact]
    public void BuildDefinitionGenerationPrompt_IncludesGuidelines()
    {
        var taskDescription = "Test task";
        var prompt = _generator.BuildDefinitionGenerationPrompt(taskDescription);

        Assert.Contains("Guidelines", prompt);
        Assert.Contains("OneShot", prompt);
        Assert.Contains("OnDemand", prompt);
    }

    // ===== Helper Classes =====

    private class TestOptionsMonitor : IOptionsMonitor<VirtualDevTeamConfig>
    {
        public VirtualDevTeamConfig CurrentValue { get; set; } = new();

        public VirtualDevTeamConfig Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<VirtualDevTeamConfig, string?> listener) => null;
    }
}
