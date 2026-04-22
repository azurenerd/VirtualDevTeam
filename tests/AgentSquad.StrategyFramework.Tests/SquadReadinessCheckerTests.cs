using AgentSquad.Core.Frameworks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="SquadReadinessChecker"/> — validates the dependency
/// chain checking logic without actually running external tools.
/// </summary>
public class SquadReadinessCheckerTests
{
    [Fact]
    public async Task CheckReadiness_returns_result_with_status()
    {
        // This test validates the checker runs without throwing.
        // The actual status depends on the dev machine's installed tools,
        // but the result shape must always be valid.
        var checker = new SquadReadinessChecker(NullLogger<SquadReadinessChecker>.Instance);

        var result = await checker.CheckReadinessAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Message);
        Assert.NotNull(result.MissingDependencies);
        Assert.True(Enum.IsDefined(result.Status));
    }

    [Fact]
    public async Task CheckReadiness_supports_cancellation()
    {
        var checker = new SquadReadinessChecker(NullLogger<SquadReadinessChecker>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckReadinessAsync(cts.Token));
    }
}
