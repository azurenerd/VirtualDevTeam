using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Tests;

public class CliOutputParserTests
{
    [Fact]
    public void StripAnsiCodes_RemovesColorCodes()
    {
        var input = "\x1B[32mHello\x1B[0m \x1B[1;34mWorld\x1B[0m";
        var result = CliOutputParser.StripAnsiCodes(input);
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void StripAnsiCodes_HandlesEmptyString()
    {
        Assert.Equal(string.Empty, CliOutputParser.StripAnsiCodes(""));
        Assert.Equal(string.Empty, CliOutputParser.StripAnsiCodes(null!));
    }

    [Fact]
    public void StripAnsiCodes_PreservesPlainText()
    {
        var input = "Just plain text with no escape codes";
        Assert.Equal(input, CliOutputParser.StripAnsiCodes(input));
    }

    [Fact]
    public void RemoveCliChrome_StripsBannerLines()
    {
        var input = """
            GitHub Copilot CLI v1.0.18
            Powered by Claude Opus 4.6
            Session ID: abc-123
            Model: claude-opus-4-6

            Here is the actual AI response.
            It has multiple lines.
            """;

        var result = CliOutputParser.RemoveCliChrome(input);

        Assert.Contains("Here is the actual AI response.", result);
        Assert.Contains("It has multiple lines.", result);
        Assert.DoesNotContain("GitHub Copilot", result);
        Assert.DoesNotContain("Powered by", result);
        Assert.DoesNotContain("Session ID:", result);
        Assert.DoesNotContain("Model:", result);
    }

    [Fact]
    public void RemoveCliChrome_StripsPromptMarkers()
    {
        var input = "> user input here\nThe response text\n> another prompt";
        var result = CliOutputParser.RemoveCliChrome(input);

        Assert.Contains("The response text", result);
        Assert.DoesNotContain("> user input here", result);
    }

    [Fact]
    public void RemoveCliChrome_StripsSeparatorLines()
    {
        var input = "Response start\n────────────────\nMore content\n===============\nEnd";
        var result = CliOutputParser.RemoveCliChrome(input);

        Assert.Contains("Response start", result);
        Assert.Contains("More content", result);
        Assert.Contains("End", result);
        Assert.DoesNotContain("────", result);
        Assert.DoesNotContain("====", result);
    }

    [Fact]
    public void ResolveCarriageReturns_KeepsLastOverwrite()
    {
        // Simulates a progress bar: "Loading...\rDone!     "
        var input = "Loading...\rDone!     ";
        var result = CliOutputParser.ResolveCarriageReturns(input);

        Assert.Contains("Done!", result);
        Assert.DoesNotContain("Loading", result);
    }

    [Fact]
    public void ResolveCarriageReturns_NoOpWhenNoCarriageReturns()
    {
        var input = "Normal line 1\nNormal line 2";
        Assert.Equal(input, CliOutputParser.ResolveCarriageReturns(input));
    }

    [Fact]
    public void CollapseBlankLines_CollapsesExcessiveBlanks()
    {
        var input = "Line 1\n\n\n\n\nLine 2";
        var result = CliOutputParser.CollapseBlankLines(input);

        // 2 blank lines preserved = "Line 1\n\n\nLine 2" (3 newlines)
        // But 4+ blank lines should not survive (which would be 5+ newlines)
        var newlineRuns = result.Split("Line 1")[1].Split("Line 2")[0];
        var blankLineCount = newlineRuns.Count(c => c == '\n') - 1; // subtract the line-ending newlines
        Assert.True(blankLineCount <= 2, $"Expected at most 2 blank lines, got {blankLineCount}");
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 2", result);
    }

    [Fact]
    public void Parse_FullPipeline_CleanOutput()
    {
        var rawOutput = """
            GitHub Copilot CLI v1.0
            Powered by Claude
            ────────────────────
            > my prompt

            Here is the clean response.
            It includes code:
            ```csharp
            Console.WriteLine("Hello");
            ```
            Done.
            """;

        var result = CliOutputParser.Parse(rawOutput);

        Assert.Contains("Here is the clean response.", result);
        Assert.Contains("Console.WriteLine", result);
        Assert.Contains("Done.", result);
        Assert.DoesNotContain("GitHub Copilot", result);
        Assert.DoesNotContain("Powered by", result);
        Assert.DoesNotContain("> my prompt", result);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CliOutputParser.Parse(""));
        Assert.Equal(string.Empty, CliOutputParser.Parse(null!));
    }

    [Fact]
    public void Parse_AnsiCodesAndChrome_Combined()
    {
        // ANSI bold + Version chrome line + actual content
        var rawOutput = "\x1B[1mSome styled text\x1B[0m\nThe actual content here";

        var result = CliOutputParser.Parse(rawOutput);

        // Should contain the non-chrome content
        Assert.Contains("The actual content here", result);
        // ANSI escape bytes should be stripped
        Assert.False(result.Any(c => c == '\x1B'), "Result should not contain ESC characters");
    }

    [Fact]
    public void ParseJsonOutput_ExtractsAssistantMessage()
    {
        var jsonl = """
            {"type":"session.tools_updated","data":{"model":"claude-opus-4.6"},"id":"1","timestamp":"2026-01-01T00:00:00Z","ephemeral":true}
            {"type":"user.message","data":{"content":"Say hello"},"id":"2","timestamp":"2026-01-01T00:00:01Z"}
            {"type":"assistant.message_delta","data":{"messageId":"m1","deltaContent":"Hello!"},"id":"3","timestamp":"2026-01-01T00:00:02Z","ephemeral":true}
            {"type":"assistant.message","data":{"messageId":"m1","content":"Hello! How can I help you?","toolRequests":[],"outputTokens":10},"id":"4","timestamp":"2026-01-01T00:00:03Z"}
            {"type":"assistant.turn_end","data":{"turnId":"0"},"id":"5","timestamp":"2026-01-01T00:00:03Z"}
            {"type":"result","timestamp":"2026-01-01T00:00:04Z","sessionId":"abc-123","exitCode":0,"usage":{"premiumRequests":1,"totalApiDurationMs":2000,"sessionDurationMs":4000}}
            """;

        var content = CliOutputParser.ParseJsonOutput(jsonl);

        Assert.NotNull(content);
        Assert.Equal("Hello! How can I help you?", content);
    }

    [Fact]
    public void ParseJsonOutput_ReturnsNullForEmptyInput()
    {
        Assert.Null(CliOutputParser.ParseJsonOutput(""));
        Assert.Null(CliOutputParser.ParseJsonOutput(null!));
        Assert.Null(CliOutputParser.ParseJsonOutput("   "));
    }

    [Fact]
    public void ParseJsonOutput_HandlesNoAssistantMessage()
    {
        var jsonl = """
            {"type":"session.tools_updated","data":{},"id":"1","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"result","timestamp":"2026-01-01T00:00:01Z","exitCode":1}
            """;

        // Returns empty string (not null) because JSONL events were found —
        // prevents text-mode fallback from passing raw JSONL as the response.
        Assert.Equal(string.Empty, CliOutputParser.ParseJsonOutput(jsonl));
    }

    [Fact]
    public void ParseJsonOutput_ReturnsNullForNonJsonlInput()
    {
        var plainText = "This is just plain text\nwith no JSON at all\n";

        // No JSONL events detected → null signals legitimate text-mode fallback.
        Assert.Null(CliOutputParser.ParseJsonOutput(plainText));
    }

    [Fact]
    public void ParseJsonOutput_ReturnsEmptyForMcpSessionEventsOnly()
    {
        // Simulates the 61 MB PMSpec.md bug: CLI returned only MCP session events, no assistant content.
        var jsonl = """
            {"type":"session.mcp_server_status_changed","data":{"serverName":"security-context","status":"failed"},"id":"1","timestamp":"2026-05-20T01:46:18.004Z","parentId":"5d16875d","ephemeral":true}
            {"type":"session.mcp_server_status_changed","data":{"serverName":"enghub","status":"connected"},"id":"2","timestamp":"2026-05-20T01:46:18.038Z","parentId":"5d16875d","ephemeral":true}
            {"type":"session.mcp_servers_loaded","data":{"servers":[{"name":"enghub","status":"connected"}]},"id":"3","timestamp":"2026-05-20T01:46:26.142Z","parentId":"5d16875d","ephemeral":true}
            {"type":"result","timestamp":"2026-05-20T01:47:00.000Z","exitCode":0}
            """;

        Assert.Equal(string.Empty, CliOutputParser.ParseJsonOutput(jsonl));
    }

    [Fact]
    public void ParseJsonOutput_SkipsMalformedLines()
    {
        var jsonl = """
            not-valid-json
            {"type":"assistant.message","data":{"content":"Valid response"},"id":"1","timestamp":"2026-01-01T00:00:00Z"}
            also { not } valid
            """;

        var content = CliOutputParser.ParseJsonOutput(jsonl);
        Assert.Equal("Valid response", content);
    }

    [Fact]
    public void ParseJsonUsage_ExtractsResultStats()
    {
        var jsonl = """
            {"type":"assistant.message","data":{"content":"Hello"},"id":"1","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"result","timestamp":"2026-01-01T00:00:01Z","sessionId":"sess-456","exitCode":0,"usage":{"premiumRequests":3,"totalApiDurationMs":4550,"sessionDurationMs":10111}}
            """;

        var usage = CliOutputParser.ParseJsonUsage(jsonl);

        Assert.NotNull(usage);
        Assert.Equal("sess-456", usage!.SessionId);
        Assert.Equal(0, usage.ExitCode);
        Assert.Equal(3, usage.PremiumRequests);
        Assert.Equal(4550, usage.TotalApiDurationMs);
        Assert.Equal(10111, usage.SessionDurationMs);
    }

    [Fact]
    public void ParseJsonUsage_ReturnsNullWhenNoResultEvent()
    {
        var jsonl = """{"type":"assistant.message","data":{"content":"Hello"},"id":"1","timestamp":"2026-01-01T00:00:00Z"}""";
        Assert.Null(CliOutputParser.ParseJsonUsage(jsonl));
    }

    [Fact]
    public void ParseJsonOutput_TrimsWhitespace()
    {
        var jsonl = """{"type":"assistant.message","data":{"content":"\n\nHello world!\n"},"id":"1","timestamp":"2026-01-01T00:00:00Z"}""";
        var content = CliOutputParser.ParseJsonOutput(jsonl);
        Assert.Equal("Hello world!", content);
    }

    [Fact]
    public void ParseJsonOutput_MultipleAssistantMessages_JoinedWithBlankLineSeparator()
    {
        // Repro for researcher-md-newline-loss: when the CLI emits multiple assistant.message
        // events in one response (e.g. with interleaved tool calls), each carries its own
        // Markdown payload — a paragraph plus a table. Without a separator the parser used to
        // silently overwrite all but the last message, and the user-visible symptom was that
        // Research.md contained collapsed Markdown blocks (tables and paragraphs on one line).
        var jsonl = """
            {"type":"assistant.message","data":{"messageId":"m1","content":"## Section One\n\nIntro paragraph for section one.\n\n| Col | Val |\n|---|---|\n| a | 1 |"},"id":"1","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant.message","data":{"messageId":"m2","content":"## Section Two\n\nIntro paragraph for section two.\n\n| Col | Val |\n|---|---|\n| b | 2 |"},"id":"2","timestamp":"2026-01-01T00:00:01Z"}
            {"type":"assistant.message","data":{"messageId":"m3","content":"## Section Three\n\nIntro paragraph for section three.\n\n| Col | Val |\n|---|---|\n| c | 3 |"},"id":"3","timestamp":"2026-01-01T00:00:02Z"}
            {"type":"result","timestamp":"2026-01-01T00:00:03Z","sessionId":"abc","exitCode":0,"usage":{"premiumRequests":1,"totalApiDurationMs":2000,"sessionDurationMs":4000}}
            """;

        var content = CliOutputParser.ParseJsonOutput(jsonl);

        Assert.NotNull(content);

        // All three payloads must be present (no silent overwrite).
        Assert.Contains("## Section One", content);
        Assert.Contains("## Section Two", content);
        Assert.Contains("## Section Three", content);
        Assert.Contains("| a | 1 |", content);
        Assert.Contains("| b | 2 |", content);
        Assert.Contains("| c | 3 |", content);

        // Each payload's internal newlines (paragraph break before the table) must survive
        // so Markdown renders tables correctly instead of collapsing them inline.
        Assert.Contains("Intro paragraph for section one.\n\n| Col | Val |", content);

        // Consecutive payloads must be separated by a blank line so the boundary is itself
        // a Markdown block break — otherwise '## Section Two' would fuse to the previous row.
        Assert.Contains("| a | 1 |\n\n## Section Two", content);
        Assert.Contains("| b | 2 |\n\n## Section Three", content);
    }

    [Fact]
    public void ParseJsonOutput_SkipsEphemeralAssistantMessages()
    {
        // Ephemeral streaming deltas must not contribute — only finalized non-ephemeral
        // assistant.message events count.
        var jsonl = """
            {"type":"assistant.message","data":{"content":"streaming partial..."},"id":"1","timestamp":"2026-01-01T00:00:00Z","ephemeral":true}
            {"type":"assistant.message","data":{"content":"final answer"},"id":"2","timestamp":"2026-01-01T00:00:01Z"}
            """;

        var content = CliOutputParser.ParseJsonOutput(jsonl);

        Assert.Equal("final answer", content);
    }
}
