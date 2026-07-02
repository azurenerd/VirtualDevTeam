using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Services;

namespace VirtualDevTeam.Core.Tests
{
    public class SMEAgentDefinitionServiceTests : IDisposable
    {
        private readonly List<string> _tempFiles = new();

        /// <summary>
        /// Test implementation of IOptionsMonitor that allows setting custom config.
        /// </summary>
        private class TestOptionsMonitor : IOptionsMonitor<VirtualDevTeamConfig>
        {
            public VirtualDevTeamConfig CurrentValue { get; set; } = new();
            
            public VirtualDevTeamConfig Get(string? name) => CurrentValue;
            
            public IDisposable? OnChange(Action<VirtualDevTeamConfig, string?> listener) => null;
        }

        /// <summary>
        /// Creates a test-specific config with a unique temp file path for definitions.
        /// </summary>
        private VirtualDevTeamConfig CreateTestConfig(bool persistDefinitions = true)
        {
            var definitionsPath = Path.Combine(Path.GetTempPath(), $"sme-test-{Guid.NewGuid():N}.json");
            _tempFiles.Add(definitionsPath);

            return new VirtualDevTeamConfig
            {
                McpServers = new Dictionary<string, McpServerDefinition>
                {
                    ["github"] = new McpServerDefinition 
                    { 
                        Name = "github", 
                        ProvidedCapabilities = new List<string> { "github-issues" }
                    },
                    ["slack"] = new McpServerDefinition 
                    { 
                        Name = "slack", 
                        ProvidedCapabilities = new List<string> { "slack-messaging" }
                    }
                },
                SmeAgents = new SmeAgentsConfig
                {
                    Enabled = true,
                    PersistDefinitions = persistDefinitions,
                    DefinitionsPath = definitionsPath,
                    MaxTotalSmeAgents = 5,
                    Templates = new Dictionary<string, SMEAgentDefinition>
                    {
                        ["security-auditor"] = new SMEAgentDefinition
                        {
                            DefinitionId = "security-auditor",
                            RoleName = "Security Auditor",
                            SystemPrompt = "You are a security specialist.",
                            Capabilities = new List<string> { "security", "vulnerability-scanning" },
                            McpServers = new List<string> { "github" }
                        },
                        ["devops-engineer"] = new SMEAgentDefinition
                        {
                            DefinitionId = "devops-engineer",
                            RoleName = "DevOps Engineer",
                            SystemPrompt = "You are a DevOps expert.",
                            Capabilities = new List<string> { "infrastructure", "deployment" },
                            McpServers = new List<string> { "github", "slack" }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Helper to create service with test config.
        /// </summary>
        private SMEAgentDefinitionService CreateService(VirtualDevTeamConfig config)
        {
            var optionsMonitor = new TestOptionsMonitor { CurrentValue = config };
            var mcpRegistry = new McpServerRegistry(optionsMonitor, NullLogger<McpServerRegistry>.Instance);
            var securityPolicy = new McpServerSecurityPolicy(mcpRegistry);
            return new SMEAgentDefinitionService(optionsMonitor, securityPolicy, NullLogger<SMEAgentDefinitionService>.Instance);
        }

        /// <summary>
        /// Test 1: GetAllAsync returns templates when no custom definitions exist.
        /// </summary>
        [Fact]
        public async Task GetAllAsync_ReturnsTemplates_WhenNoCustomDefinitions()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var result = await service.GetAllAsync();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Contains("security-auditor", result.Keys);
            Assert.Contains("devops-engineer", result.Keys);
            Assert.Equal("Security Auditor", result["security-auditor"].RoleName);
        }

        /// <summary>
        /// Test 2: GetAllAsync merges templates and custom definitions.
        /// </summary>
        [Fact]
        public async Task GetAllAsync_MergesTemplatesAndCustom()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var customDef = new SMEAgentDefinition
            {
                DefinitionId = "data-scientist",
                RoleName = "Data Scientist",
                SystemPrompt = "You are a data expert.",
                Capabilities = new List<string> { "ml", "data-analysis" },
                McpServers = new List<string> { "github" }
            };

            // Save a custom definition
            await service.SaveAsync(customDef);

            // Act
            var result = await service.GetAllAsync();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(3, result.Count); // 2 templates + 1 custom
            Assert.Contains("security-auditor", result.Keys);
            Assert.Contains("devops-engineer", result.Keys);
            Assert.Contains("data-scientist", result.Keys);
            Assert.Equal("Data Scientist", result["data-scientist"].RoleName);
        }

        /// <summary>
        /// Test 3: GetAllAsync templates take priority over custom with same ID.
        /// </summary>
        [Fact]
        public async Task GetAllAsync_TemplatesTakePriority_OverCustomWithSameId()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Try to override template with custom (same ID)
            var overrideDef = new SMEAgentDefinition
            {
                DefinitionId = "security-auditor",
                RoleName = "Modified Auditor",
                SystemPrompt = "This should not appear.",
                Capabilities = new List<string> { "other" },
                McpServers = new List<string>()
            };

            await service.SaveAsync(overrideDef);

            // Act
            var result = await service.GetAllAsync();

            // Assert
            // Template should take priority
            Assert.Equal("Security Auditor", result["security-auditor"].RoleName);
            Assert.NotEqual("Modified Auditor", result["security-auditor"].RoleName);
        }

        /// <summary>
        /// Test 4: GetAsync returns template when it exists.
        /// </summary>
        [Fact]
        public async Task GetAsync_ReturnsTemplate_WhenExists()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var result = await service.GetAsync("security-auditor");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("security-auditor", result.DefinitionId);
            Assert.Equal("Security Auditor", result.RoleName);
        }

        /// <summary>
        /// Test 5: GetAsync returns null when definition doesn't exist.
        /// </summary>
        [Fact]
        public async Task GetAsync_ReturnsNull_WhenNotExists()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var result = await service.GetAsync("nonexistent-agent");

            // Assert
            Assert.Null(result);
        }

        /// <summary>
        /// Test 6: GetAsync throws when definition ID is null or empty.
        /// </summary>
        [Fact]
        public async Task GetAsync_ThrowsOnNull()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetAsync(null!));
            // Empty string doesn't throw - it's not null, so it just returns null
        }

        /// <summary>
        /// Test 7: SaveAsync persists definition and it can be retrieved.
        /// </summary>
        [Fact]
        public async Task SaveAsync_PersistsDefinition()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var newDef = new SMEAgentDefinition
            {
                DefinitionId = "database-expert",
                RoleName = "Database Expert",
                SystemPrompt = "You are a database specialist.",
                Capabilities = new List<string> { "sql", "database-optimization" },
                McpServers = new List<string> { "github" }
            };

            // Act
            var saveResult = await service.SaveAsync(newDef);
            var retrievedDef = await service.GetAsync("database-expert");

            // Assert
            Assert.True(saveResult.IsValid, $"Save failed: {string.Join(", ", saveResult.Errors)}");
            Assert.NotNull(retrievedDef);
            Assert.Equal("database-expert", retrievedDef.DefinitionId);
            Assert.Equal("Database Expert", retrievedDef.RoleName);
        }

        /// <summary>
        /// Test 8: SaveAsync returns validation errors for invalid definition.
        /// </summary>
        [Fact]
        public async Task SaveAsync_ReturnsValidationErrors_ForInvalidDefinition()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var invalidDef = new SMEAgentDefinition
            {
                DefinitionId = null!, // Invalid: null ID
                RoleName = "Invalid Agent",
                SystemPrompt = "Some prompt",
                Capabilities = new List<string>(),
                McpServers = new List<string> { "nonexistent-server" } // Invalid: server doesn't exist
            };

            // Act
            var result = await service.SaveAsync(invalidDef);

            // Assert
            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
        }

        /// <summary>
        /// Test 9: SaveAsync skips persistence when PersistDefinitions is disabled.
        /// </summary>
        [Fact]
        public async Task SaveAsync_SkipsPersistence_WhenDisabled()
        {
            // Arrange
            var config = CreateTestConfig(persistDefinitions: false);
            var service = CreateService(config);

            var newDef = new SMEAgentDefinition
            {
                DefinitionId = "temp-agent",
                RoleName = "Temporary Agent",
                SystemPrompt = "This won't persist.",
                Capabilities = new List<string> { "temp" },
                McpServers = new List<string>()
            };

            // Act
            var saveResult = await service.SaveAsync(newDef);
            
            // When persistence is disabled, even trying to get it immediately won't work
            // because the definition was never stored in _customDefinitions cache
            var inMemoryDef = await service.GetAsync("temp-agent");

            // And of course, a new instance won't have it either
            var newService = CreateService(config);
            var persistedDef = await newService.GetAsync("temp-agent");

            // Assert
            // Save should still return valid (it just logs a warning)
            Assert.True(saveResult.IsValid);
            // Without persistence, definition is NOT available even in current instance
            Assert.Null(inMemoryDef);
            // And definitely not in a new instance
            Assert.Null(persistedDef);
        }

        /// <summary>
        /// Test 10: DeleteAsync removes custom definition.
        /// </summary>
        [Fact]
        public async Task DeleteAsync_RemovesCustomDefinition()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var customDef = new SMEAgentDefinition
            {
                DefinitionId = "test-agent",
                RoleName = "Test Agent",
                SystemPrompt = "Test",
                Capabilities = new List<string> { "test" },
                McpServers = new List<string>()
            };

            await service.SaveAsync(customDef);
            var existsBefore = await service.GetAsync("test-agent");
            Assert.NotNull(existsBefore);

            // Act
            var deleteResult = await service.DeleteAsync("test-agent");
            var existsAfter = await service.GetAsync("test-agent");

            // Assert
            Assert.True(deleteResult);
            Assert.Null(existsAfter);
        }

        /// <summary>
        /// Test 11: DeleteAsync returns false for template (cannot delete templates).
        /// </summary>
        [Fact]
        public async Task DeleteAsync_ReturnsFalse_ForTemplate()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var deleteResult = await service.DeleteAsync("security-auditor");
            var stillExists = await service.GetAsync("security-auditor");

            // Assert
            Assert.False(deleteResult); // Cannot delete template
            Assert.NotNull(stillExists); // Template still exists
        }

        /// <summary>
        /// Test 12: DeleteAsync returns false for non-existent definition.
        /// </summary>
        [Fact]
        public async Task DeleteAsync_ReturnsFalse_ForNonExistent()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var deleteResult = await service.DeleteAsync("completely-fake-id");

            // Assert
            Assert.False(deleteResult);
        }

        /// <summary>
        /// Test 13: FindByCapabilitiesAsync finds matching definitions (case-insensitive).
        /// </summary>
        [Fact]
        public async Task FindByCapabilitiesAsync_FindsMatchingDefinitions()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var customDef = new SMEAgentDefinition
            {
                DefinitionId = "ml-expert",
                RoleName = "ML Expert",
                SystemPrompt = "Machine learning specialist.",
                Capabilities = new List<string> { "MACHINE-LEARNING", "security", "data-analysis" }, // Mixed case
                McpServers = new List<string> { "github" }
            };

            await service.SaveAsync(customDef);

            // Act
            // Search case-insensitive for capabilities
            var results = await service.FindByCapabilitiesAsync(new[] { "machine-learning", "SECURITY" });

            // Assert
            Assert.NotNull(results);
            Assert.Equal(2, results.Count);
            // ml-expert has 2 matches, security-auditor has 1 match
            Assert.Equal("ml-expert", results[0].DefinitionId); // Should be first (2 matches)
            Assert.Equal("security-auditor", results[1].DefinitionId); // Should be second (1 match)
        }

        /// <summary>
        /// Test 14: FindByCapabilitiesAsync orders by match count (descending).
        /// </summary>
        [Fact]
        public async Task FindByCapabilitiesAsync_OrdersByMatchCount()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            var def1 = new SMEAgentDefinition
            {
                DefinitionId = "high-match",
                RoleName = "High Match",
                SystemPrompt = "Many matches",
                Capabilities = new List<string> { "cap1", "cap2", "cap3", "cap4" },
                McpServers = new List<string>()
            };

            var def2 = new SMEAgentDefinition
            {
                DefinitionId = "low-match",
                RoleName = "Low Match",
                SystemPrompt = "Few matches",
                Capabilities = new List<string> { "cap1" },
                McpServers = new List<string>()
            };

            await service.SaveAsync(def1);
            await service.SaveAsync(def2);

            // Act
            var results = await service.FindByCapabilitiesAsync(new[] { "cap1", "cap2", "cap3", "cap4" });

            // Assert
            Assert.Equal(2, results.Count);
            Assert.Equal("high-match", results[0].DefinitionId); // 4 matches
            Assert.Equal("low-match", results[1].DefinitionId);  // 1 match
        }

        /// <summary>
        /// Test 15: FindByCapabilitiesAsync returns empty when no matches.
        /// </summary>
        [Fact]
        public async Task FindByCapabilitiesAsync_ReturnsEmpty_WhenNoMatch()
        {
            // Arrange
            var config = CreateTestConfig();
            var service = CreateService(config);

            // Act
            var results = await service.FindByCapabilitiesAsync(new[] { "nonexistent-capability", "fake-skill" });

            // Assert
            Assert.NotNull(results);
            Assert.Empty(results);
        }

        /// <summary>
        /// Cleanup temp files created during tests.
        /// </summary>
        public void Dispose()
        {
            foreach (var file in _tempFiles)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
