using VirtualDevTeam.Core.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.Core.Tests.Workspace;

public class TestStrategyAnalyzerTests
{
    private readonly TestStrategyAnalyzer _analyzer;

    public TestStrategyAnalyzerTests()
    {
        _analyzer = new TestStrategyAnalyzer(NullLogger<TestStrategyAnalyzer>.Instance);
    }

    [Fact]
    public void Analyze_CSharpFiles_ReturnsUnitTestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Services/Calculator.cs"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsUnitTests);
        Assert.False(result.NeedsUITests);
    }

    [Fact]
    public void Analyze_RazorFiles_ReturnsUITestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Pages/Dashboard.razor"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8 with Blazor Server");

        Assert.True(result.NeedsUnitTests);
        Assert.True(result.NeedsUITests);
        Assert.NotEmpty(result.UITestScenarios);
    }

    [Fact]
    public void Analyze_TsxFiles_ReturnsUITestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/components/UserForm.tsx"],
            prBody: null,
            issueBody: null,
            techStack: "TypeScript React");

        Assert.True(result.NeedsUITests);
    }

    [Fact]
    public void Analyze_ControllerFile_ReturnsIntegrationTestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Controllers/UsersController.cs"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsUnitTests);
        Assert.True(result.NeedsIntegrationTests);
    }

    [Fact]
    public void Analyze_ServiceFile_ReturnsIntegrationTestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Services/PaymentService.cs"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsIntegrationTests);
    }

    [Fact]
    public void Analyze_RepositoryFile_ReturnsIntegrationTestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Data/UserRepository.cs"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsIntegrationTests);
    }

    [Fact]
    public void Analyze_UIKeywordsInIssueBody_ReturnsUITestsNeeded()
    {
        var result = _analyzer.Analyze(
            ["src/Models/ViewModel.cs"],
            prBody: null,
            issueBody: "The user can see a dashboard with charts and the navigation menu should be visible",
            techStack: "C# .NET 8");

        Assert.True(result.NeedsUITests);
    }

    [Fact]
    public void Analyze_IntegrationKeywordsInPRBody_ReturnsIntegrationTests()
    {
        var result = _analyzer.Analyze(
            ["src/Helpers/DataMapper.cs"],
            prBody: "This PR adds a new REST API endpoint for user authentication",
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsIntegrationTests);
    }

    [Fact]
    public void Analyze_NoCodeFiles_ReturnsNoTests()
    {
        var result = _analyzer.Analyze(
            ["docs/README.md", "config/settings.json"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.False(result.NeedsUnitTests);
        Assert.False(result.NeedsIntegrationTests);
        Assert.False(result.NeedsUITests);
    }

    [Fact]
    public void Analyze_MixedFiles_ReturnsAllTiers()
    {
        var result = _analyzer.Analyze(
            [
                "src/Services/AuthService.cs",
                "src/Pages/Login.razor",
                "src/Models/User.cs"
            ],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8 Blazor");

        Assert.True(result.NeedsUnitTests);
        Assert.True(result.NeedsIntegrationTests);
        Assert.True(result.NeedsUITests);
    }

    [Fact]
    public void Analyze_ReturnsRationale()
    {
        var result = _analyzer.Analyze(
            ["src/Pages/Home.razor"],
            prBody: null,
            issueBody: null,
            techStack: "Blazor");

        Assert.NotEmpty(result.Rationale);
        Assert.Contains("UI file detected", result.Rationale);
    }

    [Fact]
    public void Analyze_RequiredTiers_ReturnsCorrectEnumValues()
    {
        var result = _analyzer.Analyze(
            ["src/Pages/Home.razor", "src/Services/DataService.cs"],
            prBody: null,
            issueBody: null,
            techStack: "Blazor");

        var tiers = result.RequiredTiers.ToList();
        Assert.Contains(TestTier.Unit, tiers);
        Assert.Contains(TestTier.Integration, tiers);
        Assert.Contains(TestTier.UI, tiers);
    }

    [Fact]
    public void ExtractAcceptanceCriteria_ParsesChecklistItems()
    {
        var issueBody = """
            ## Description
            Build a login page.

            ## Acceptance Criteria
            - [ ] User can enter username and password
            - [ ] User clicks login button
            - [x] Error displayed for invalid credentials
            
            ## Notes
            Some other notes here.
            """;

        var criteria = TestStrategyAnalyzer.ExtractAcceptanceCriteria(issueBody);

        Assert.Equal(3, criteria.Count);
        Assert.Contains(criteria, c => c.Contains("username"));
        Assert.Contains(criteria, c => c.Contains("login button"));
        Assert.Contains(criteria, c => c.Contains("Error displayed"));
    }

    [Fact]
    public void ExtractAcceptanceCriteria_ParsesNumberedItems()
    {
        var issueBody = """
            ## Acceptance Criteria
            1. Should display a list of users
            2. Should support pagination
            """;

        var criteria = TestStrategyAnalyzer.ExtractAcceptanceCriteria(issueBody);

        Assert.Equal(2, criteria.Count);
    }

    [Fact]
    public void Analyze_AcceptanceCriteriaWithUIKeywords_AddsUIScenarios()
    {
        var result = _analyzer.Analyze(
            ["src/Pages/Settings.razor"],
            prBody: null,
            issueBody: """
                ## Acceptance Criteria
                - [ ] User can see settings page with form inputs
                - [ ] User clicks save button and changes persist
                """,
            techStack: "Blazor");

        Assert.True(result.NeedsUITests);
        Assert.True(result.UITestScenarios.Count >= 1);
    }

    [Theory]
    [InlineData(".vue")]
    [InlineData(".svelte")]
    [InlineData(".jsx")]
    [InlineData(".cshtml")]
    public void Analyze_AllUIExtensions_TriggerUITests(string extension)
    {
        var result = _analyzer.Analyze(
            [$"src/Components/Widget{extension}"],
            prBody: null,
            issueBody: null,
            techStack: "Web App");

        Assert.True(result.NeedsUITests);
    }

    [Theory]
    [InlineData("Gateway")]
    [InlineData("Middleware")]
    [InlineData("Hub")]
    [InlineData("Client")]
    public void Analyze_AllIntegrationPatterns_TriggerIntegrationTests(string pattern)
    {
        var result = _analyzer.Analyze(
            [$"src/Infrastructure/{pattern}Manager.cs"],
            prBody: null,
            issueBody: null,
            techStack: "C# .NET 8");

        Assert.True(result.NeedsIntegrationTests);
    }
}
