using VirtualDevTeam.Agents;

namespace VirtualDevTeam.Agents.Tests;

/// <summary>
/// Regression coverage for researcher-md-newline-loss. The Researcher's synthesis-to-ResearchResult
/// parser used to collapse multi-line content (paragraphs, sub-bullets, table rows) onto a single
/// line by joining continuation lines with a space, which produced Research.md entries where bold
/// section headers, prose, and full Markdown tables were all glued onto one giant bullet.
/// </summary>
public class ResearcherAgentParseResultTests
{
    [Fact]
    public void ParseResearchResult_PreservesNewlinesBetweenBulletAndFollowingTable()
    {
        // This mirrors the shape of the LLM output that produced the broken
        // Research.md lines 95-99 in the GridGuardians/Compliance run.
        var synthesis = """
            ## Key Findings
            - Load assets in Phaser via `this.load.atlas(...)`.

            **Critical: Style Consistency Protocol.** AI image generation produces variable results. Mitigate with:

            | Tool | Version | Purpose |
            |---|---|---|
            | Docker | 27+ | Containerisation |
            | Phaser | 3.80 | Game engine |
            """;

        var result = ResearcherAgent.ParseResearchResult(synthesis, detailedAnalysis: "");

        // Exactly one bullet should be captured under Key Findings — the original `-` line.
        // Without the fix, the parser would have continued appending the bold paragraph and
        // every table row onto that same bullet (joined with spaces), yielding 1 item whose
        // text contained the table inline.
        Assert.NotEmpty(result.KeyFindings);
        var firstFinding = result.KeyFindings[0];

        // The first bullet must NOT contain the bold paragraph or any table row glued to it
        // by a space — that was the symptom in the broken Research.md output.
        Assert.DoesNotContain(" **Critical: Style Consistency Protocol.**", firstFinding);
        Assert.DoesNotContain(" | Tool | Version", firstFinding);
        Assert.DoesNotContain(" |---|---|", firstFinding);

        // The table rows, if attached to a bullet at all, must be on their own newline-separated
        // lines (so Markdown still sees row boundaries) — not concatenated with spaces.
        var allFindingsJoined = string.Join("\n---\n", result.KeyFindings);
        Assert.DoesNotContain("|---|---|---| | Docker", allFindingsJoined);
        Assert.DoesNotContain("Purpose | |---", allFindingsJoined);

        // No single captured bullet may exceed ~500 chars purely because of structural collapse.
        // (Fenced code blocks are exempt, but this fixture has none.)
        foreach (var f in result.KeyFindings)
        {
            Assert.True(f.Length < 500,
                $"Captured bullet is suspiciously long ({f.Length} chars), suggesting block-level " +
                $"content was collapsed onto a single line. Content was: {f}");
        }
    }

    [Fact]
    public void ParseResearchResult_PreservesParagraphBreaksInSummary()
    {
        // Two paragraphs separated by a blank line in the summary section should remain two
        // paragraphs (separated by \n\n), not be flattened to one space-joined run-on sentence.
        var synthesis = """
            ## Executive Summary
            First paragraph of the summary describing the overall approach.

            Second paragraph contrasts the alternatives and recommends the chosen path.
            """;

        var result = ResearcherAgent.ParseResearchResult(synthesis, detailedAnalysis: "");

        Assert.Contains("First paragraph", result.Summary);
        Assert.Contains("Second paragraph", result.Summary);

        // Must NOT be joined by a single space (the old buggy behaviour).
        Assert.DoesNotContain("overall approach. Second paragraph", result.Summary);

        // Must contain a paragraph break (a blank line — i.e. two consecutive newlines).
        Assert.Contains("\n\n", result.Summary);
    }
}
