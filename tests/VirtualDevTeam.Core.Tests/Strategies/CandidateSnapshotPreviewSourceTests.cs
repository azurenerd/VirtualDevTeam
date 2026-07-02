using System.Text.Json;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Preview;

namespace VirtualDevTeam.Core.Tests.Strategies;

/// <summary>
/// Snapshot-shape tests for the <see cref="CandidateSnapshot.PreviewSource"/> +
/// <see cref="CandidateSnapshot.IncludedAssetPaths"/> fields added for the
/// <c>strategies-ui-asset-gallery</c> feature. The dashboard's <c>/api/strategies</c>
/// endpoints serialize <see cref="CandidateSnapshot"/> directly, so the JSON contract
/// here must stay aligned with what <c>Strategies.razor</c> consumes.
/// </summary>
public class CandidateSnapshotPreviewSourceTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Defaults_PreviewSourceIsPlaywrightScreenshot_AndIncludedAssetPathsIsNull()
    {
        var snap = new CandidateSnapshot
        {
            StrategyId = "baseline",
            State = CandidateState.Evaluated,
        };

        Assert.Equal(CandidatePreviewSource.PlaywrightScreenshot, snap.PreviewSource);
        Assert.Null(snap.IncludedAssetPaths);
    }

    [Fact]
    public void Serialize_ImageAssetsSnapshotWithThreeAssetPaths_EmitsExpectedJsonShape()
    {
        var assets = new[]
        {
            "art/sprites/player.png",
            "art/sprites/enemy.png",
            "art/backgrounds/level-1.png",
        };

        var snap = new CandidateSnapshot
        {
            StrategyId = "agentic-1",
            State = CandidateState.Scored,
            PreviewSource = CandidatePreviewSource.ImageAssets,
            IncludedAssetPaths = assets,
            ScreenshotBase64 = "iVBORw0KGgo=", // placeholder; real value is contact-sheet PNG
        };

        // System.Text.Json default serializes enums as integers — that's the on-the-wire
        // contract the dashboard sees. ImageAssets = 1, Diagrams = 2, NoVisualContent = 3,
        // CaptureUnavailable = 4, CaptureFailed = 5.
        var json = JsonSerializer.Serialize(snap, CamelCase);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // strategyId still serializes as a string; state serializes as the int enum
        Assert.Equal("agentic-1", root.GetProperty("strategyId").GetString());
        Assert.Equal((int)CandidateState.Scored, root.GetProperty("state").GetInt32());

        // PreviewSource serializes as the integer enum value (ImageAssets == 1)
        Assert.Equal((int)CandidatePreviewSource.ImageAssets, root.GetProperty("previewSource").GetInt32());

        // IncludedAssetPaths serializes as a string array preserving insertion order
        var pathsJson = root.GetProperty("includedAssetPaths");
        Assert.Equal(JsonValueKind.Array, pathsJson.ValueKind);
        Assert.Equal(3, pathsJson.GetArrayLength());
        Assert.Equal(assets[0], pathsJson[0].GetString());
        Assert.Equal(assets[1], pathsJson[1].GetString());
        Assert.Equal(assets[2], pathsJson[2].GetString());

        // ScreenshotBase64 still rides through alongside (dashboard renders this as the
        // contact-sheet image)
        Assert.Equal("iVBORw0KGgo=", root.GetProperty("screenshotBase64").GetString());
    }

    [Theory]
    [InlineData(CandidatePreviewSource.CaptureUnavailable, 4)]
    [InlineData(CandidatePreviewSource.CaptureFailed, 5)]
    public void Serialize_NewPreviewSources_RoundTripAsExpectedIntegers(CandidatePreviewSource source, int expectedValue)
    {
        var snap = new CandidateSnapshot
        {
            StrategyId = "baseline",
            State = CandidateState.Evaluated,
            PreviewSource = source,
        };

        var json = JsonSerializer.Serialize(snap, CamelCase);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(expectedValue, root.GetProperty("previewSource").GetInt32());

        var roundTrip = JsonSerializer.Deserialize<CandidateSnapshot>(json, CamelCase);
        Assert.NotNull(roundTrip);
        Assert.Equal(source, roundTrip!.PreviewSource);
    }
}
