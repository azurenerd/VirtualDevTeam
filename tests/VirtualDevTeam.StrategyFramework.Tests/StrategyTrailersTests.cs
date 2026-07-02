using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class StrategyTrailersTests
{
    [Fact]
    public void BuildBlock_emits_canonical_scalar_format()
    {
        var block = StrategyTrailers.BuildBlock(new[]
        {
            new KeyValuePair<string, string>(StrategyTrailers.StrategyKey, "baseline"),
            new KeyValuePair<string, string>(StrategyTrailers.RunIdKey, "run-1"),
        });
        Assert.Contains("Strategy: baseline", block);
        Assert.Contains("Strategy-Run-Id: run-1", block);
        Assert.EndsWith("\n", block);
    }

    [Theory]
    [InlineData("bad\nvalue")]
    [InlineData("bad\rvalue")]
    public void BuildBlock_rejects_multiline_values(string badValue)
    {
        Assert.Throws<ArgumentException>(() =>
            StrategyTrailers.BuildBlock(new[] { new KeyValuePair<string, string>("Key", badValue) }));
    }

    [Fact]
    public void Append_separates_with_blank_line()
    {
        var body = "Some description.";
        var result = StrategyTrailers.Append(body, new[]
        {
            new KeyValuePair<string, string>(StrategyTrailers.StrategyKey, "mcp-enhanced"),
        });
        Assert.Contains("Some description.", result);
        Assert.Contains("\n\nStrategy: mcp-enhanced", result);
    }

    [Fact]
    public void Append_skips_empty_entries()
    {
        var result = StrategyTrailers.Append("b", new[]
        {
            new KeyValuePair<string, string>("", "v"),
            new KeyValuePair<string, string>("k", ""),
            new KeyValuePair<string, string>(StrategyTrailers.StrategyKey, "baseline"),
        });
        Assert.Contains("Strategy: baseline", result);
        Assert.DoesNotContain(": v", result.Split('\n')[^2]); // last trailer line is Strategy
    }
}

public class JudgeInputSanitizerTests
{
    [Fact]
    public void Truncates_at_max_chars_and_appends_marker()
    {
        var big = new string('x', 200);
        var result = JudgeInputSanitizer.SanitizePatch(big, 50);
        Assert.True(result.Length >= 50);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void Strips_control_chars_except_newline_tab()
    {
        var bel = ((char)7).ToString();
        var input = "line1\nline2\tok" + bel + "bell";
        var result = JudgeInputSanitizer.SanitizePatch(input, 1000);
        Assert.False(result.Contains(bel, StringComparison.Ordinal));
        Assert.Contains("\n", result);
        Assert.Contains("\t", result);
        Assert.Contains("bell", result);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal("", JudgeInputSanitizer.SanitizePatch("", 100));
    }

    [Fact]
    public void Zero_maxChars_disables_truncation()
    {
        var big = new string('x', 100_000);
        var result = JudgeInputSanitizer.SanitizePatch(big, 0);
        Assert.Equal(100_000, result.Length);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void Negative_maxChars_disables_truncation()
    {
        var big = new string('x', 50_000);
        var result = JudgeInputSanitizer.SanitizePatch(big, -1);
        Assert.Equal(50_000, result.Length);
        Assert.DoesNotContain("truncated", result);
    }
}

public class NullLlmJudgeTests
{
    [Fact]
    public async Task Returns_empty_scores_without_error()
    {
        var judge = new NullLlmJudge();
        var result = await judge.ScoreAsync(new JudgeInput
        {
            TaskId = "t",
            TaskTitle = "x",
            TaskDescription = "",
            CandidatePatches = new Dictionary<string, string> { ["a"] = "", ["b"] = "" },
        }, CancellationToken.None);
        Assert.True(result.IsFallback);
        Assert.Null(result.Error);
    }
}
