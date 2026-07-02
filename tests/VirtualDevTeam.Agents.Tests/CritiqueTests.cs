using VirtualDevTeam.Agents;

namespace VirtualDevTeam.Agents.Tests;

public class CritiqueTests
{
    [Fact]
    public void FormatCritiqueSection_NullCritique_ShowsNoConcerns()
    {
        var result = ProgramManagerAgent.FormatCritiqueSection(null);
        Assert.Contains("🦆 Independent Critique", result);
        Assert.Contains("✅ No significant concerns identified", result);
    }

    [Fact]
    public void FormatCritiqueSection_EmptyString_ShowsNoConcerns()
    {
        var result = ProgramManagerAgent.FormatCritiqueSection("   ");
        Assert.Contains("✅ No significant concerns identified", result);
    }

    [Fact]
    public void FormatCritiqueSection_WithFindings_IncludesFindings()
    {
        var critique = "⚠️ Missing null check on config parameter\n⚠️ No test for empty array case";
        var result = ProgramManagerAgent.FormatCritiqueSection(critique);
        Assert.Contains("🦆 Independent Critique", result);
        Assert.Contains("⚠️ Missing null check", result);
        Assert.Contains("⚠️ No test for empty array", result);
        Assert.DoesNotContain("✅ No significant concerns", result);
    }

    [Fact]
    public void FormatCritiqueSection_TrimsWhitespace()
    {
        var critique = "  \n⚠️ Some concern\n  ";
        var result = ProgramManagerAgent.FormatCritiqueSection(critique);
        Assert.Contains("⚠️ Some concern", result);
        // Should start with the header, not whitespace
        Assert.StartsWith("\n\n### 🦆", result);
    }
}
