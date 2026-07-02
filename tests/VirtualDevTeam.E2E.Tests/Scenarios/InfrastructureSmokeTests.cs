using VirtualDevTeam.E2E.Tests.Infrastructure;

namespace VirtualDevTeam.E2E.Tests.Scenarios;

/// <summary>
/// Smoke tests that validate the E2E test infrastructure works correctly
/// before attempting full workflow scenarios.
/// </summary>
public class InfrastructureSmokeTests
{
    [Fact]
    public void ContentLoader_LoadsAllPrebuiltContent()
    {
        var research = E2EContentLoader.LoadResearch();
        Assert.Contains("Hello World", research);
        Assert.Contains("ASP.NET", research);

        var pmSpec = E2EContentLoader.LoadPMSpec();
        Assert.Contains("Hello World", pmSpec);
        Assert.Contains("Acceptance Criteria", pmSpec);

        var architecture = E2EContentLoader.LoadArchitecture();
        Assert.Contains("Razor Pages", architecture);
        Assert.Contains("Kestrel", architecture);

        var plan = E2EContentLoader.LoadEngineeringPlan();
        Assert.Contains("Hello World", plan);
        Assert.Contains("Task 1", plan);
    }

    [Fact]
    public void ContentLoader_LoadsHelloWorldAppFiles()
    {
        var files = E2EContentLoader.LoadHelloWorldAppFiles();

        Assert.True(files.Count > 0, "Expected at least some app files");
        Assert.True(files.ContainsKey("Program.cs"), "Expected Program.cs");
        Assert.True(files.ContainsKey("HelloWorld.csproj"), "Expected HelloWorld.csproj");
        Assert.True(files.ContainsKey("Pages/Index.cshtml"), "Expected Pages/Index.cshtml");
    }

    [Fact]
    public void ContentLoader_HelloWorldAppPathExists()
    {
        var path = E2EContentLoader.GetHelloWorldAppPath();
        Assert.True(Directory.Exists(path), $"HelloWorldApp directory not found at {path}");
    }

    [Fact]
    public void ScriptedChatService_MatchesSystemPromptPatterns()
    {
        var service = HelloWorldScripts.CreateForAllAgents();

        // Should have scripts loaded
        Assert.NotNull(service);
    }

    [Fact]
    public async Task ScriptedChatService_ReturnsResearchForResearcher()
    {
        var service = HelloWorldScripts.CreateForAllAgents();
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddSystemMessage("You are a senior researcher analyzing technology options.");
        history.AddUserMessage("Research the best approach for a hello world web app.");

        var result = await service.GetChatMessageContentsAsync(history);

        Assert.Single(result);
        Assert.Contains("ASP.NET", result[0].Content);
        Assert.Contains("Hello World", result[0].Content);
    }

    [Fact]
    public async Task ScriptedChatService_ReturnsPMSpecForPM()
    {
        var service = HelloWorldScripts.CreateForAllAgents();
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddSystemMessage("You are a program manager creating product specifications.");
        history.AddUserMessage("Create a spec for the hello world app.");

        var result = await service.GetChatMessageContentsAsync(history);

        Assert.Single(result);
        Assert.Contains("Acceptance Criteria", result[0].Content);
    }

    [Fact]
    public async Task ScriptedChatService_ReturnsOKForUnmatchedPrompt()
    {
        var service = HelloWorldScripts.CreateForAllAgents();
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddSystemMessage("You are an unrecognized agent type.");
        history.AddUserMessage("Do something.");

        var result = await service.GetChatMessageContentsAsync(history);

        Assert.Single(result);
        Assert.Equal("Acknowledged. Proceeding with the task as specified.", result[0].Content);
    }

    [Fact]
    public async Task ScriptedChatService_LogsCalls()
    {
        var service = HelloWorldScripts.CreateForAllAgents();
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddSystemMessage("You are a senior researcher.");
        history.AddUserMessage("Research.");

        await service.GetChatMessageContentsAsync(history);

        Assert.Single(service.CallLog);
        Assert.Contains("senior researcher", service.CallLog[0].SystemPromptSnippet);
    }

    [Fact]
    public void AutoApproveGateService_ApprovesEverything()
    {
        var gateService = new AutoApproveGateCheckService();

        Assert.False(gateService.IsEnabled);
        Assert.False(gateService.RequiresHuman("any-gate"));
        Assert.True(gateService.IsGateApprovedLocally("any-gate"));
        Assert.Null(gateService.GetLocalRejection("any-gate"));
    }

    [Fact]
    public async Task AutoApproveGateService_CheckGateReturnsProceeed()
    {
        var gateService = new AutoApproveGateCheckService();

        var result = await gateService.CheckGateAsync("test-gate", "context");
        Assert.Equal(VirtualDevTeam.Core.Configuration.GateResult.Proceed, result);

        Assert.Contains("test-gate", gateService.CheckedGates);
    }

    [Fact]
    public async Task AutoApproveGateService_AssessReturnsApproved()
    {
        var gateService = new AutoApproveGateCheckService();

        var assessment = await gateService.AssessGateApprovalAsync("gate", 1);
        Assert.Equal(VirtualDevTeam.Core.Configuration.GateDecision.Approved, assessment.Decision);
    }
}
