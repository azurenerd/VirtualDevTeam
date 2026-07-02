using VirtualDevTeam.Agents;

namespace VirtualDevTeam.Agents.Tests;

public class FileScopeFilterTests
{
    [Fact]
    public void ExtractAllowedFiles_MarkdownFormat_ParsesCreateAndModify()
    {
        var description = """
            ## File Plan
            - ➕ **Create:** `Components/TimelineSection.razor`
            - ✏️ **Modify:** `Pages/Index.razor`
            - 📎 **Reference (do not recreate):** `wwwroot/css/site.css`
            """;

        var allowed = EngineerAgentBase.ExtractAllowedFilesFromDescription(description);

        Assert.Contains("Components/TimelineSection.razor", allowed);
        Assert.Contains("Pages/Index.razor", allowed);
        // Reference files should NOT be in allowed (they shouldn't be modified)
        Assert.DoesNotContain("wwwroot/css/site.css", allowed);
    }

    [Fact]
    public void ExtractAllowedFiles_RawFormat_ParsesCreateAndModify()
    {
        var description = "CREATE:src/Components/Header.razor\nMODIFY:src/Pages/Home.razor\nUSE:wwwroot/css/app.css";

        var allowed = EngineerAgentBase.ExtractAllowedFilesFromDescription(description);

        Assert.Contains("src/Components/Header.razor", allowed);
        Assert.Contains("src/Pages/Home.razor", allowed);
        Assert.DoesNotContain("wwwroot/css/app.css", allowed);
    }

    [Fact]
    public void ExtractAllowedFiles_EmptyDescription_ReturnsEmpty()
    {
        Assert.Empty(EngineerAgentBase.ExtractAllowedFilesFromDescription(null));
        Assert.Empty(EngineerAgentBase.ExtractAllowedFilesFromDescription(""));
        Assert.Empty(EngineerAgentBase.ExtractAllowedFilesFromDescription("No file plan here"));
    }

    [Fact]
    public void ExtractAllowedFiles_CaseInsensitive()
    {
        var description = "create:src/Foo.cs\nmodify:src/Bar.cs";
        var allowed = EngineerAgentBase.ExtractAllowedFilesFromDescription(description);

        Assert.Contains("src/Foo.cs", allowed);
        Assert.Contains("src/Bar.cs", allowed);
    }

    [Fact]
    public void ExtractAllowedFiles_NormalizesBackslashes()
    {
        var description = "- ➕ **Create:** `src\\Components\\Header.razor`";
        var allowed = EngineerAgentBase.ExtractAllowedFilesFromDescription(description);

        // Should be normalized to forward slashes
        Assert.Contains("src/Components/Header.razor", allowed);
    }

    [Fact]
    public void BuildFileScopePromptBlock_WithFilePlan_ContainsFileList()
    {
        var description = "- ➕ **Create:** `ReportingDashboard.Web/data.json`";
        var block = EngineerAgentBase.BuildFileScopePromptBlock(description, null);

        Assert.Contains("FILE SCOPE RULE", block);
        Assert.Contains("ReportingDashboard.Web/data.json", block);
        Assert.Contains("STRICTLY ENFORCED", block);
    }

    [Fact]
    public void BuildFileScopePromptBlock_NoFilePlan_ReturnsEmpty()
    {
        var block = EngineerAgentBase.BuildFileScopePromptBlock("No file plan here", null);
        Assert.Equal("", block);
    }

    [Fact]
    public void BuildFileScopePromptBlock_FallsBackToIssueDescription()
    {
        var issueDesc = "CREATE:src/Models/Data.cs\nMODIFY:src/Services/DataService.cs";
        var block = EngineerAgentBase.BuildFileScopePromptBlock("No plan in PR", issueDesc);

        Assert.Contains("src/Models/Data.cs", block);
        Assert.Contains("src/Services/DataService.cs", block);
    }
}
