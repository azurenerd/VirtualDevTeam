using VirtualDevTeam.Core.Agents.Decisions;

namespace VirtualDevTeam.Core.Tests;

public class DecisionBlockParserPipeDelimitedTests
{
    [Fact]
    public void ParsePipeDelimited_EmptyContent_ReturnsEmptyList()
    {
        var result = DecisionBlockParser.ParsePipeDelimited("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePipeDelimited_NullContent_ReturnsEmptyList()
    {
        var result = DecisionBlockParser.ParsePipeDelimited(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePipeDelimited_SingleDecision_ParsesCorrectly()
    {
        var content = "DECISION|M|Rename IUserService to IAccountService|Better reflects domain terminology|src/IUserService.cs, src/IAccountService.cs";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Single(result);
        Assert.Equal("M", result[0].Impact);
        Assert.Equal("Rename IUserService to IAccountService", result[0].Title);
        Assert.Equal("Better reflects domain terminology", result[0].Rationale);
        Assert.Equal("src/IUserService.cs, src/IAccountService.cs", result[0].Files);
    }

    [Fact]
    public void ParsePipeDelimited_MultipleDecisions_ParsesAll()
    {
        var content = """
            Some plan text here
            DECISION|S|Rename field userId to accountId|Consistent naming|Models/User.cs
            More plan text
            DECISION|L|Change IRepository<T> return type from List to IAsyncEnumerable|Streaming support|Repositories/IRepository.cs, Services/UserService.cs
            Final text
            """;

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("S", result[0].Impact);
        Assert.Equal("Rename field userId to accountId", result[0].Title);
        Assert.Equal("L", result[1].Impact);
        Assert.Equal("Change IRepository<T> return type from List to IAsyncEnumerable", result[1].Title);
    }

    [Fact]
    public void ParsePipeDelimited_CaseInsensitivePrefix()
    {
        var content = "decision|XL|Redesign auth flow|Security requirements changed|Auth/IAuthProvider.cs";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Single(result);
        Assert.Equal("XL", result[0].Impact);
    }

    [Fact]
    public void ParsePipeDelimited_InsufficientParts_Skipped()
    {
        var content = "DECISION|M|Only three parts";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Empty(result);
    }

    [Fact]
    public void ParsePipeDelimited_EmptyImpact_Skipped()
    {
        var content = "DECISION||Some Title|Rationale|files.cs";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Empty(result);
    }

    [Fact]
    public void ParsePipeDelimited_EmptyTitle_Skipped()
    {
        var content = "DECISION|M||Rationale|files.cs";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Empty(result);
    }

    [Fact]
    public void ParsePipeDelimited_WhitespaceTrimmmed()
    {
        var content = "  DECISION|M|Add required param to CreateUser  |Validation needs|UserService.cs  ";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Single(result);
        Assert.Equal("M", result[0].Impact);
        Assert.Equal("Add required param to CreateUser", result[0].Title);
        Assert.Equal("Validation needs", result[0].Rationale);
        Assert.Equal("UserService.cs", result[0].Files);
    }

    [Fact]
    public void ParsePipeDelimited_MixedWithNumberedSteps()
    {
        // Simulates step-planning output where DECISION blocks precede numbered steps
        var content = """
            DECISION|M|Add pageSize parameter to IProductService.ListAsync|Pagination support|src/Services/IProductService.cs
            DECISION|S|Rename ProductDto.Name to ProductDto.DisplayName|UI consistency|src/Models/ProductDto.cs
            1. Create project structure with folders and config files
            2. Implement product service with repository pattern
            3. Add API controllers and middleware
            4. Wire up DI and add integration tests
            """;

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Add pageSize parameter to IProductService.ListAsync", result[0].Title);
        Assert.Equal("Rename ProductDto.Name to ProductDto.DisplayName", result[1].Title);
    }

    [Fact]
    public void ParsePipeDelimited_AllImpactLevels()
    {
        var content = """
            DECISION|XS|Trivial rename|No real impact|a.cs
            DECISION|S|Small rename|Minor impact|b.cs
            DECISION|M|Medium change|Moderate impact|c.cs
            DECISION|L|Large redesign|Significant impact|d.cs
            DECISION|XL|Architectural change|Major impact|e.cs
            """;

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Equal(5, result.Count);
        Assert.Equal("XS", result[0].Impact);
        Assert.Equal("S", result[1].Impact);
        Assert.Equal("M", result[2].Impact);
        Assert.Equal("L", result[3].Impact);
        Assert.Equal("XL", result[4].Impact);
    }

    [Fact]
    public void ParsePipeDelimited_ExtraPipeFields_IgnoresExtras()
    {
        // More than 5 pipe-delimited fields — only first 5 matter
        var content = "DECISION|M|Title|Rationale|files.cs|extra|data";

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Single(result);
        Assert.Equal("files.cs", result[0].Files);
    }

    [Fact]
    public void ParsePipeDelimited_NonDecisionLinesIgnored()
    {
        var content = """
            ## Implementation Plan
            This is a description of the plan.
            DECISION|M|Change API contract|Requirements changed|api.cs
            - Step 1: Do something
            - Step 2: Do something else
            """;

        var result = DecisionBlockParser.ParsePipeDelimited(content);

        Assert.Single(result);
        Assert.Equal("Change API contract", result[0].Title);
    }

    [Fact]
    public void ParsePipeDelimited_SupportsDeconstruction()
    {
        var content = "DECISION|L|Redesign|New requirements|Service.cs";
        var result = DecisionBlockParser.ParsePipeDelimited(content);

        var (impact, title, rationale, files) = result[0];

        Assert.Equal("L", impact);
        Assert.Equal("Redesign", title);
        Assert.Equal("New requirements", rationale);
        Assert.Equal("Service.cs", files);
    }
}
