using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Comprehensive unit tests for McpServerRegistry.
/// </summary>
public class McpServerRegistryTests : IDisposable
{
    private readonly TestOptionsMonitor _optionsMonitor;
    private readonly McpServerRegistry _registry;

    public McpServerRegistryTests()
    {
        _optionsMonitor = new TestOptionsMonitor();
        _registry = new McpServerRegistry(_optionsMonitor, NullLogger<McpServerRegistry>.Instance);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    #region GetAll Tests

    [Fact]
    public void GetAll_ReturnsConfiguredServers()
    {
        // Arrange
        var server1 = new McpServerDefinition { Name = "server1" };
        var server2 = new McpServerDefinition { Name = "server2" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "server1", server1 },
            { "server2", server2 }
        };

        // Act
        var result = _registry.GetAll();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains("server1", result.Keys);
        Assert.Contains("server2", result.Keys);
        Assert.Same(server1, result["server1"]);
        Assert.Same(server2, result["server2"]);
    }

    #endregion

    #region Get Tests

    [Fact]
    public void Get_ReturnsServer_WhenExists()
    {
        // Arrange
        var serverDef = new McpServerDefinition { Name = "testserver" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "testserver", serverDef }
        };

        // Act
        var result = _registry.Get("testserver");

        // Assert
        Assert.NotNull(result);
        Assert.Same(serverDef, result);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotExists()
    {
        // Arrange
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>();

        // Act
        var result = _registry.Get("nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Get_ThrowsOnNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _registry.Get(null!));
    }

    #endregion

    #region Contains Tests

    [Fact]
    public void Contains_ReturnsTrue_WhenServerExists()
    {
        // Arrange
        var serverDef = new McpServerDefinition { Name = "existingserver" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "existingserver", serverDef }
        };

        // Act
        var result = _registry.Contains("existingserver");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void Contains_ReturnsFalse_WhenServerNotExists()
    {
        // Arrange
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>();

        // Act
        var result = _registry.Contains("nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Contains_ThrowsOnNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _registry.Contains(null!));
    }

    #endregion

    #region FindByCapability Tests

    [Fact]
    public void FindByCapability_ReturnsMatchingServers_CaseInsensitive()
    {
        // Arrange
        var server1 = new McpServerDefinition
        {
            Name = "server1",
            ProvidedCapabilities = new List<string> { "FileAccess", "Database" }
        };
        var server2 = new McpServerDefinition
        {
            Name = "server2",
            ProvidedCapabilities = new List<string> { "API", "FileAccess" }
        };
        var server3 = new McpServerDefinition
        {
            Name = "server3",
            ProvidedCapabilities = new List<string> { "Compute" }
        };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "server1", server1 },
            { "server2", server2 },
            { "server3", server3 }
        };

        // Act - Test case-insensitive matching
        var result1 = _registry.FindByCapability("FILEACCESS");
        var result2 = _registry.FindByCapability("fileaccess");
        var result3 = _registry.FindByCapability("FileAccess");

        // Assert
        Assert.Equal(2, result1.Count);
        Assert.Contains("server1", result1);
        Assert.Contains("server2", result1);

        Assert.Equal(2, result2.Count);
        Assert.Contains("server1", result2);
        Assert.Contains("server2", result2);

        Assert.Equal(2, result3.Count);
        Assert.Contains("server1", result3);
        Assert.Contains("server2", result3);
    }

    [Fact]
    public void FindByCapability_ReturnsEmpty_WhenNoMatch()
    {
        // Arrange
        var server1 = new McpServerDefinition
        {
            Name = "server1",
            ProvidedCapabilities = new List<string> { "FileAccess" }
        };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "server1", server1 }
        };

        // Act
        var result = _registry.FindByCapability("NonExistentCapability");

        // Assert
        Assert.Empty(result);
    }

    #endregion

    #region GetServerNames Tests

    [Fact]
    public void GetServerNames_ReturnsAllNames()
    {
        // Arrange
        var server1 = new McpServerDefinition { Name = "server1" };
        var server2 = new McpServerDefinition { Name = "server2" };
        var server3 = new McpServerDefinition { Name = "server3" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "server1", server1 },
            { "server2", server2 },
            { "server3", server3 }
        };

        // Act
        var result = _registry.GetServerNames();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Contains("server1", result);
        Assert.Contains("server2", result);
        Assert.Contains("server3", result);
    }

    #endregion
}

/// <summary>
/// Comprehensive unit tests for McpServerSecurityPolicy.
/// </summary>
public class McpServerSecurityPolicyTests : IDisposable
{
    private readonly TestOptionsMonitor _optionsMonitor;
    private readonly McpServerRegistry _registry;
    private readonly McpServerSecurityPolicy _policy;

    public McpServerSecurityPolicyTests()
    {
        _optionsMonitor = new TestOptionsMonitor();
        _registry = new McpServerRegistry(_optionsMonitor, NullLogger<McpServerRegistry>.Instance);
        _policy = new McpServerSecurityPolicy(_registry);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    #region IsServerAllowed Tests

    [Theory]
    [InlineData("shell")]
    [InlineData("exec")]
    [InlineData("terminal")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("bash")]
    public void IsServerAllowed_ReturnsFalse_ForBlockedServers(string blockedServer)
    {
        // Act
        var result = _policy.IsServerAllowed(blockedServer);

        // Assert
        Assert.False(result, $"Server '{blockedServer}' should be blocked");
    }

    [Fact]
    public void IsServerAllowed_ReturnsFalse_ForUnregistered()
    {
        // Arrange
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>();

        // Act
        var result = _policy.IsServerAllowed("unregistered");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsServerAllowed_ReturnsTrue_ForRegisteredNonBlocked()
    {
        // Arrange
        var serverDef = new McpServerDefinition { Name = "legitimateserver" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "legitimateserver", serverDef }
        };

        // Act
        var result = _policy.IsServerAllowed("legitimateserver");

        // Assert
        Assert.True(result);
    }

    #endregion

    #region ValidateDefinition Tests - Valid Definition

    [Fact]
    public void ValidateDefinition_ReturnsValid_ForGoodDefinition()
    {
        // Arrange
        var serverDef = new McpServerDefinition { Name = "goodserver" };
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>
        {
            { "goodserver", serverDef }
        };

        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            McpServers = new List<string> { "goodserver" },
            KnowledgeLinks = new List<string> { "https://example.com" },
            ModelTier = "standard",
            MaxInstances = 1
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    #endregion

    #region ValidateDefinition Tests - DefinitionId

    [Fact]
    public void ValidateDefinition_RejectsEmptyDefinitionId()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant."
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("DefinitionId"));
    }

    #endregion

    #region ValidateDefinition Tests - RoleName

    [Fact]
    public void ValidateDefinition_RejectsEmptyRoleName()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "",
            SystemPrompt = "You are a helpful assistant."
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("RoleName"));
    }

    #endregion

    #region ValidateDefinition Tests - SystemPrompt

    [Fact]
    public void ValidateDefinition_RejectsEmptySystemPrompt()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = ""
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("SystemPrompt"));
    }

    [Fact]
    public void ValidateDefinition_RejectsLongSystemPrompt()
    {
        // Arrange
        var longPrompt = new string('a', 5001);
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = longPrompt
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("SystemPrompt") && e.Contains("5000"));
    }

    #endregion

    #region ValidateDefinition Tests - MCP Servers

    [Fact]
    public void ValidateDefinition_RejectsBlockedMcpServer()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            McpServers = new List<string> { "shell" }
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("shell") && e.Contains("blocked"));
    }

    [Fact]
    public void ValidateDefinition_RejectsUnregisteredMcpServer()
    {
        // Arrange
        _optionsMonitor.CurrentValue.McpServers = new Dictionary<string, McpServerDefinition>();
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            McpServers = new List<string> { "unregistered" }
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("unregistered") && e.Contains("not in the registry"));
    }

    #endregion

    #region ValidateDefinition Tests - Knowledge Links

    [Fact]
    public void ValidateDefinition_RejectsHttpKnowledgeLink()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            KnowledgeLinks = new List<string> { "http://example.com" }
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("http://example.com") && e.Contains("HTTPS"));
    }

    [Fact]
    public void ValidateDefinition_RejectsLocalhostKnowledgeLink()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            KnowledgeLinks = new List<string> { "https://localhost:8080" }
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("localhost"));
    }

    [Theory]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://172.16.0.1")]
    public void ValidateDefinition_RejectsPrivateNetworkLinks(string privateLink)
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            KnowledgeLinks = new List<string> { privateLink }
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains(privateLink) && e.Contains("private network"));
    }

    #endregion

    #region ValidateDefinition Tests - MaxInstances

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateDefinition_RejectsInvalidMaxInstances_TooLow(int maxInstances)
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            MaxInstances = maxInstances
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("MaxInstances") && e.Contains("1") && e.Contains("10"));
    }

    [Fact]
    public void ValidateDefinition_RejectsInvalidMaxInstances_TooHigh()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            MaxInstances = 11
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("MaxInstances") && e.Contains("1") && e.Contains("10"));
    }

    #endregion

    #region ValidateDefinition Tests - ModelTier

    [Theory]
    [InlineData("invalid")]
    [InlineData("gpu")]
    [InlineData("")]
    public void ValidateDefinition_RejectsInvalidModelTier(string modelTier)
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            ModelTier = modelTier
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("ModelTier"));
    }

    [Theory]
    [InlineData("premium")]
    [InlineData("standard")]
    [InlineData("budget")]
    [InlineData("local")]
    public void ValidateDefinition_AcceptsValidModelTier(string modelTier)
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "agent123",
            RoleName = "TestAgent",
            SystemPrompt = "You are a helpful assistant.",
            ModelTier = modelTier,
            MaxInstances = 1
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        // Should have no model tier errors (may have other errors, but not model tier)
        Assert.DoesNotContain(result.Errors, e => e.Contains("ModelTier"));
    }

    #endregion

    #region ValidateDefinition Tests - Multiple Errors

    [Fact]
    public void ValidateDefinition_CollectsMultipleErrors()
    {
        // Arrange
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "",  // Empty - error
            RoleName = "",      // Empty - error
            SystemPrompt = "",  // Empty - error
            McpServers = new List<string> { "shell", "unregistered" },
            KnowledgeLinks = new List<string> { "http://example.com" },
            MaxInstances = 15,  // Too high - error
            ModelTier = "invalid"  // Invalid - error
        };

        // Act
        var result = _policy.ValidateDefinition(definition);

        // Assert
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.True(result.Errors.Count >= 5, $"Expected at least 5 errors, got {result.Errors.Count}");
    }

    #endregion
}

/// <summary>
/// Test helper for IOptionsMonitor<VirtualDevTeamConfig>.
/// Since Core.Tests doesn't have Moq, we provide a simple implementation.
/// </summary>
internal class TestOptionsMonitor : IOptionsMonitor<VirtualDevTeamConfig>
{
    public VirtualDevTeamConfig CurrentValue { get; set; } = new();

    public VirtualDevTeamConfig Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<VirtualDevTeamConfig, string?> listener) => null;
}
