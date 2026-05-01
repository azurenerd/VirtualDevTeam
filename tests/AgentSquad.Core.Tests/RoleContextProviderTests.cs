using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSquad.Core.Tests;

public class RoleContextProviderTests
{
    private readonly RoleContextProvider _provider;
    private readonly AgentSquadConfig _config;

    public RoleContextProviderTests()
    {
        _config = new AgentSquadConfig
        {
            Agents = new AgentConfigs
            {
                ProgramManager = new AgentConfig { RoleDescription = "PM default role" },
                SoftwareEngineer = new AgentConfig { RoleDescription = "SE default role" },
                Researcher = new AgentConfig { RoleDescription = "Researcher default" },
                Architect = new AgentConfig { RoleDescription = "Architect default" },
                TestEngineer = new AgentConfig { RoleDescription = "TE default" }
            }
        };

        var monitor = new TestOptionsMonitor<AgentSquadConfig>(_config);
        _provider = new RoleContextProvider(monitor, NullLogger<RoleContextProvider>.Instance);
    }

    // ── GetCacheKey tests ──

    [Fact]
    public void GetCacheKey_BuiltInRole_NoCustomName_ReturnsRoleName()
    {
        var key = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, null);
        Assert.Equal("SoftwareEngineer", key);
    }

    [Fact]
    public void GetCacheKey_BuiltInRole_WithCustomName_ReturnsQualifiedKey()
    {
        // SME agents have Role=SoftwareEngineer but custom display name
        var key = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, "Game Engine Engineer 1");
        Assert.Equal("SoftwareEngineer:Game Engine Engineer 1", key);
    }

    [Fact]
    public void GetCacheKey_CustomRole_WithName_ReturnsQualifiedKey()
    {
        var key = RoleContextProvider.GetCacheKey(AgentRole.Custom, "Security Analyst");
        Assert.Equal("Custom:Security Analyst", key);
    }

    [Fact]
    public void GetCacheKey_WithBlankCustomName_ReturnsRoleOnly()
    {
        var key = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, "   ");
        Assert.Equal("SoftwareEngineer", key);
    }

    [Fact]
    public void GetCacheKey_SMEsGetDistinctKeys()
    {
        // Two different SME agents should have distinct cache keys
        var key1 = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, "Frontend Engineer 1");
        var key2 = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, "Backend Engineer 1");
        var keyBase = RoleContextProvider.GetCacheKey(AgentRole.SoftwareEngineer, null);

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, keyBase);
        Assert.NotEqual(key2, keyBase);
    }

    // ── Override CRUD tests ──

    [Fact]
    public void SetRoleDescriptionOverride_SimpleRole_TakesEffectImmediately()
    {
        _provider.SetRoleDescriptionOverride(AgentRole.ProgramManager, "New PM role");

        var context = _provider.GetRoleSystemContext(AgentRole.ProgramManager);
        Assert.Contains("New PM role", context);
        Assert.DoesNotContain("PM default role", context);
    }

    [Fact]
    public void SetRoleDescriptionOverride_WithCustomName_IsolatesFromBase()
    {
        // Override for SME should not affect base SE
        _provider.SetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "Game Engine specialist", "Game Engine Engineer 1");

        var smeContext = _provider.GetRoleSystemContext(AgentRole.SoftwareEngineer, "Game Engine Engineer 1");
        var baseContext = _provider.GetRoleSystemContext(AgentRole.SoftwareEngineer, null);

        Assert.Contains("Game Engine specialist", smeContext);
        Assert.Contains("SE default role", baseContext);
        Assert.DoesNotContain("Game Engine specialist", baseContext);
    }

    [Fact]
    public void TryGetRoleDescriptionOverride_NoOverride_ReturnsFalse()
    {
        var result = _provider.TryGetRoleDescriptionOverride(AgentRole.Researcher, null, out var text);
        Assert.False(result);
        Assert.Null(text);
    }

    [Fact]
    public void TryGetRoleDescriptionOverride_WithOverride_ReturnsTrueAndText()
    {
        _provider.SetRoleDescriptionOverride(AgentRole.Architect, "Custom architect role");

        var result = _provider.TryGetRoleDescriptionOverride(AgentRole.Architect, null, out var text);
        Assert.True(result);
        Assert.Equal("Custom architect role", text);
    }

    [Fact]
    public void ClearRoleDescriptionOverride_RemovesOverride()
    {
        _provider.SetRoleDescriptionOverride(AgentRole.TestEngineer, "Override TE role");
        Assert.True(_provider.TryGetRoleDescriptionOverride(AgentRole.TestEngineer, null, out _));

        var cleared = _provider.ClearRoleDescriptionOverride(AgentRole.TestEngineer, null);
        Assert.True(cleared);

        Assert.False(_provider.TryGetRoleDescriptionOverride(AgentRole.TestEngineer, null, out _));
    }

    [Fact]
    public void ClearRoleDescriptionOverride_NoExistingOverride_ReturnsFalse()
    {
        var cleared = _provider.ClearRoleDescriptionOverride(AgentRole.Researcher, null);
        Assert.False(cleared);
    }

    [Fact]
    public void GetConfiguredRoleDescription_ReturnsConfigValue()
    {
        var desc = _provider.GetConfiguredRoleDescription(AgentRole.ProgramManager);
        Assert.Equal("PM default role", desc);
    }

    [Fact]
    public void GetConfiguredRoleDescription_UnknownRole_ReturnsNullOrEmpty()
    {
        var desc = _provider.GetConfiguredRoleDescription(AgentRole.Custom, "NonExistent");
        Assert.True(string.IsNullOrEmpty(desc));
    }

    // ── Backward compatibility ──

    [Fact]
    public void SetRoleDescriptionOverride_LegacySignature_StillWorks()
    {
        // The PM calls the (role, description) overload — must still work
        _provider.SetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "PM-assigned SE role");

        var context = _provider.GetRoleSystemContext(AgentRole.SoftwareEngineer);
        Assert.Contains("PM-assigned SE role", context);
    }

    // ── ClearAllOverrides ──

    [Fact]
    public void ClearAllOverrides_RemovesAllInMemoryOverrides()
    {
        // Set overrides for multiple agents
        _provider.SetRoleDescriptionOverride(AgentRole.ProgramManager, "Custom PM");
        _provider.SetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "Custom SE", "sme:game-engine");
        _provider.SetRoleDescriptionOverride(AgentRole.Architect, "Custom Architect");

        // All should exist
        Assert.True(_provider.TryGetRoleDescriptionOverride(AgentRole.ProgramManager, null, out _));
        Assert.True(_provider.TryGetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "sme:game-engine", out _));
        Assert.True(_provider.TryGetRoleDescriptionOverride(AgentRole.Architect, null, out _));

        // Clear all
        _provider.ClearAllOverrides();

        // None should remain
        Assert.False(_provider.TryGetRoleDescriptionOverride(AgentRole.ProgramManager, null, out _));
        Assert.False(_provider.TryGetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "sme:game-engine", out _));
        Assert.False(_provider.TryGetRoleDescriptionOverride(AgentRole.Architect, null, out _));

        // Config defaults should still be accessible
        var context = _provider.GetRoleSystemContext(AgentRole.ProgramManager);
        Assert.Contains("PM default role", context);
    }

    // ── SME key alignment ──

    [Fact]
    public void SME_Override_WithDefinitionId_MatchesAgentReadPath()
    {
        // SME agents use Identity.CustomAgentName = "sme:{definitionId}" for reads
        // The UI/API must save with the same key for overrides to reach the agent
        var smeCustomName = "sme:game-engine";

        _provider.SetRoleDescriptionOverride(AgentRole.SoftwareEngineer, "Game engine specialist focus", smeCustomName);

        // This simulates what AgentBase.BuildSystemPrompt does:
        // RoleContext.GetRoleSystemContext(Identity.Role, Identity.CustomAgentName)
        var context = _provider.GetRoleSystemContext(AgentRole.SoftwareEngineer, smeCustomName);
        Assert.Contains("Game engine specialist focus", context);
    }

    // ── Helper ──
    private class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
