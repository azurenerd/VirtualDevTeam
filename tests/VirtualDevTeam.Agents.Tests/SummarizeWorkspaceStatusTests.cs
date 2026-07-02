using VirtualDevTeam.Agents;
using Xunit;

namespace VirtualDevTeam.Agents.Tests;

/// <summary>
/// Tests for <see cref="EngineerAgentBase.SummarizeWorkspaceStatus"/>. This helper drives the
/// workspace-commit fallback used when an LLM emits no FILE: blocks but the agent has written
/// files (e.g. PNGs from a REST call) directly via shell tools. The summary appears in the
/// commit message + a LogActivity line, so it needs to be accurate for both binary-heavy
/// (image-generation) and code-heavy (regular implementation) workspaces.
/// </summary>
public sealed class SummarizeWorkspaceStatusTests
{
    [Fact]
    public void EmptyInput_ReturnsNoChanges()
    {
        Assert.Equal("no changes", EngineerAgentBase.SummarizeWorkspaceStatus(""));
        Assert.Equal("no changes", EngineerAgentBase.SummarizeWorkspaceStatus("   "));
    }

    [Fact]
    public void SingleNewFile_CountsAsNew()
    {
        // Porcelain format: "?? path" = untracked / new file.
        var status = "?? client/public/assets/sprites/cannon-tower/idle.png";
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("1 new", result);
        Assert.Contains("1 PNG", result);
    }

    [Fact]
    public void MultipleNewPngs_ProducesAccurateBinaryNote()
    {
        var status = string.Join("\n", new[]
        {
            "?? client/public/assets/sprites/cannon-tower/idle.png",
            "?? client/public/assets/sprites/archer-tower/idle.png",
            "?? client/public/assets/sprites/goblin/idle.png",
            "?? client/public/assets/sprites/orc/idle.png",
        });
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("4 new", result);
        Assert.Contains("4 PNGs", result);
    }

    [Fact]
    public void MixedCodeAndBinary_ReportsBoth()
    {
        // Realistic mixed-content commit: some new code + a hero sprite.
        var status = string.Join("\n", new[]
        {
            "?? client/src/features/towers/CannonTower.ts",
            " M client/src/scenes/GameScene.ts",
            "?? client/public/assets/sprites/cannon-tower/idle.png",
        });
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("2 new", result);
        Assert.Contains("1 modified", result);
        Assert.Contains("1 PNG", result);
    }

    [Fact]
    public void ModifiedAndDeleted_BothCounted()
    {
        var status = string.Join("\n", new[]
        {
            " M client/src/foo.ts",
            " M client/src/bar.ts",
            " D client/src/baz.ts",
        });
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("2 modified", result);
        Assert.Contains("1 deleted", result);
        Assert.DoesNotContain("PNG", result);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".mp4")]
    [InlineData(".webm")]
    public void OtherMediaExtensions_CountedAsOtherMedia(string ext)
    {
        var status = $"?? client/public/assets/preview{ext}";
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("1 new", result);
        Assert.Contains("other media", result);
    }

    [Fact]
    public void NonMediaFiles_NoBinaryNote()
    {
        var status = "?? client/src/feature.ts";
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("1 new", result);
        Assert.DoesNotContain("PNG", result);
        Assert.DoesNotContain("media", result);
    }

    [Fact]
    public void MalformedLines_AreSkippedGracefully()
    {
        var status = string.Join("\n", new[]
        {
            "??", // too short
            "X",  // too short
            "?? real/file.png", // valid
            "",   // empty (filtered)
        });
        var result = EngineerAgentBase.SummarizeWorkspaceStatus(status);
        Assert.Contains("1 new", result);
        Assert.Contains("1 PNG", result);
    }
}
