using VirtualDevTeam.Core.HealthMonitor;
using Xunit;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for the pure-logic helpers in <see cref="FixRecommendationPlannerService"/>.
/// The two-pass LLM flow itself is integration-tested at runtime — these tests cover
/// the bits that have to keep working even if the LLM response shape drifts a little.
/// </summary>
public class FixRecommendationPlannerServiceTests
{
    [Theory]
    [InlineData("{\"confidence\": 0.85, \"top_risks\": [\"foo\"]}", 0.85)]
    [InlineData("Some prose.\n\n{\"confidence\":0.42,\"top_risks\":[]}", 0.42)]
    [InlineData("```json\n{\"confidence\":0.6}\n```", 0.6)]
    [InlineData("Trailing text {\"confidence\": 1.0, \"top_risks\": []} after", 1.0)]
    [InlineData("Multiple {\"confidence\":0.1} then {\"confidence\":0.9}", 0.9)]
    public void ParseConfidence_ExtractsValueFromValidJson(string input, double expected)
    {
        var result = FixRecommendationPlannerService.ParseConfidence(input);
        Assert.Equal(expected, result, precision: 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("This response has no JSON at all")]
    [InlineData("Has {} but no confidence field")]
    [InlineData("{ malformed json {")]
    public void ParseConfidence_DefaultsToHalfWhenUnparseable(string input)
    {
        var result = FixRecommendationPlannerService.ParseConfidence(input);
        Assert.Equal(0.5, result, precision: 2);
    }

    [Theory]
    [InlineData("{\"confidence\": -0.5}", 0.0)]    // clamp below
    [InlineData("{\"confidence\": 1.5}", 1.0)]     // clamp above
    [InlineData("{\"confidence\": 0.0}", 0.0)]
    [InlineData("{\"confidence\": 1.0}", 1.0)]
    public void ParseConfidence_ClampsToZeroOneRange(string input, double expected)
    {
        var result = FixRecommendationPlannerService.ParseConfidence(input);
        Assert.Equal(expected, result, precision: 2);
    }

    [Fact]
    public void ParseConfidence_AcceptsStringNumber()
    {
        // Some LLMs emit confidence as a quoted string. Tolerate it.
        var result = FixRecommendationPlannerService.ParseConfidence("{\"confidence\":\"0.73\"}");
        Assert.Equal(0.73, result, precision: 2);
    }
}
