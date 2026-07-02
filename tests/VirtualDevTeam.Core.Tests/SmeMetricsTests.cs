using Xunit;
using VirtualDevTeam.Core.Services;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.Tests;

public class SmeMetricsTests
{
    /// <summary>
    /// SmeMetrics - Verify all counters start at zero.
    /// </summary>
    [Fact]
    public void InitialSnapshot_AllZeros()
    {
        var metrics = new SmeMetrics();
        var snapshot = metrics.GetSnapshot();

        Assert.Equal(0, snapshot.SmeAgentsSpawned);
        Assert.Equal(0, snapshot.SmeAgentsRetired);
        Assert.Equal(0, snapshot.McpServerErrors);
        Assert.Equal(0, snapshot.KnowledgeFetchSuccesses);
        Assert.Equal(0, snapshot.KnowledgeFetchFailures);
    }

    /// <summary>
    /// SmeMetrics - IncrementSmeAgentsSpawned increments the counter correctly.
    /// </summary>
    [Fact]
    public void IncrementSmeAgentsSpawned_IncrementsCorrectly()
    {
        var metrics = new SmeMetrics();

        metrics.IncrementSmeAgentsSpawned();
        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(1, snapshot1.SmeAgentsSpawned);

        metrics.IncrementSmeAgentsSpawned();
        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(2, snapshot2.SmeAgentsSpawned);

        metrics.IncrementSmeAgentsSpawned();
        var snapshot3 = metrics.GetSnapshot();
        Assert.Equal(3, snapshot3.SmeAgentsSpawned);
    }

    /// <summary>
    /// SmeMetrics - IncrementSmeAgentsRetired increments the counter correctly.
    /// </summary>
    [Fact]
    public void IncrementSmeAgentsRetired_IncrementsCorrectly()
    {
        var metrics = new SmeMetrics();

        metrics.IncrementSmeAgentsRetired();
        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(1, snapshot1.SmeAgentsRetired);

        metrics.IncrementSmeAgentsRetired();
        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(2, snapshot2.SmeAgentsRetired);
    }

    /// <summary>
    /// SmeMetrics - IncrementMcpServerErrors increments the counter correctly.
    /// </summary>
    [Fact]
    public void IncrementMcpServerErrors_IncrementsCorrectly()
    {
        var metrics = new SmeMetrics();

        metrics.IncrementMcpServerErrors();
        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(1, snapshot1.McpServerErrors);

        metrics.IncrementMcpServerErrors();
        metrics.IncrementMcpServerErrors();
        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(3, snapshot2.McpServerErrors);
    }

    /// <summary>
    /// SmeMetrics - IncrementKnowledgeFetchSuccesses increments the counter correctly.
    /// </summary>
    [Fact]
    public void IncrementKnowledgeFetchSuccesses_IncrementsCorrectly()
    {
        var metrics = new SmeMetrics();

        metrics.IncrementKnowledgeFetchSuccesses();
        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(1, snapshot1.KnowledgeFetchSuccesses);

        metrics.IncrementKnowledgeFetchSuccesses();
        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(2, snapshot2.KnowledgeFetchSuccesses);
    }

    /// <summary>
    /// SmeMetrics - IncrementKnowledgeFetchFailures increments the counter correctly.
    /// </summary>
    [Fact]
    public void IncrementKnowledgeFetchFailures_IncrementsCorrectly()
    {
        var metrics = new SmeMetrics();

        metrics.IncrementKnowledgeFetchFailures();
        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(1, snapshot1.KnowledgeFetchFailures);

        metrics.IncrementKnowledgeFetchFailures();
        metrics.IncrementKnowledgeFetchFailures();
        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(3, snapshot2.KnowledgeFetchFailures);
    }

    /// <summary>
    /// SmeMetrics - GetSnapshot returns correct values after multiple increments on all counters.
    /// </summary>
    [Fact]
    public void GetSnapshot_ReturnsCorrectValues_AfterMultipleIncrements()
    {
        var metrics = new SmeMetrics();

        // Increment various counters different amounts
        for (int i = 0; i < 5; i++)
            metrics.IncrementSmeAgentsSpawned();

        for (int i = 0; i < 3; i++)
            metrics.IncrementSmeAgentsRetired();

        for (int i = 0; i < 7; i++)
            metrics.IncrementMcpServerErrors();

        for (int i = 0; i < 10; i++)
            metrics.IncrementKnowledgeFetchSuccesses();

        for (int i = 0; i < 2; i++)
            metrics.IncrementKnowledgeFetchFailures();

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(5, snapshot.SmeAgentsSpawned);
        Assert.Equal(3, snapshot.SmeAgentsRetired);
        Assert.Equal(7, snapshot.McpServerErrors);
        Assert.Equal(10, snapshot.KnowledgeFetchSuccesses);
        Assert.Equal(2, snapshot.KnowledgeFetchFailures);
    }

    /// <summary>
    /// SmeMetrics - Concurrent increments are thread-safe and produce correct totals.
    /// Spawn 100 tasks, each incrementing a counter, and verify the final count is correct.
    /// </summary>
    [Fact]
    public void ThreadSafety_ConcurrentIncrements()
    {
        var metrics = new SmeMetrics();
        int taskCount = 100;

        // Test concurrent SmeAgentsSpawned increments
        var spawnTasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() => metrics.IncrementSmeAgentsSpawned()))
            .ToArray();
        Task.WaitAll(spawnTasks);

        var snapshot1 = metrics.GetSnapshot();
        Assert.Equal(taskCount, snapshot1.SmeAgentsSpawned);

        // Test concurrent SmeAgentsRetired increments
        var retiredTasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() => metrics.IncrementSmeAgentsRetired()))
            .ToArray();
        Task.WaitAll(retiredTasks);

        var snapshot2 = metrics.GetSnapshot();
        Assert.Equal(taskCount, snapshot2.SmeAgentsRetired);

        // Test concurrent McpServerErrors increments
        var errorTasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() => metrics.IncrementMcpServerErrors()))
            .ToArray();
        Task.WaitAll(errorTasks);

        var snapshot3 = metrics.GetSnapshot();
        Assert.Equal(taskCount, snapshot3.McpServerErrors);

        // Test concurrent KnowledgeFetchSuccesses increments
        var successTasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() => metrics.IncrementKnowledgeFetchSuccesses()))
            .ToArray();
        Task.WaitAll(successTasks);

        var snapshot4 = metrics.GetSnapshot();
        Assert.Equal(taskCount, snapshot4.KnowledgeFetchSuccesses);

        // Test concurrent KnowledgeFetchFailures increments
        var failureTasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() => metrics.IncrementKnowledgeFetchFailures()))
            .ToArray();
        Task.WaitAll(failureTasks);

        var snapshot5 = metrics.GetSnapshot();
        Assert.Equal(taskCount, snapshot5.KnowledgeFetchFailures);
    }

    /// <summary>
    /// SmeMetrics - Mixed concurrent increments across all counters maintain thread safety.
    /// </summary>
    [Fact]
    public void ThreadSafety_MixedConcurrentIncrements()
    {
        var metrics = new SmeMetrics();
        int taskCount = 50;

        var tasks = new List<Task>();

        // Create tasks that randomly increment different counters
        for (int i = 0; i < taskCount; i++)
        {
            int counterType = i % 5;
            tasks.Add(Task.Run(() =>
            {
                switch (counterType)
                {
                    case 0:
                        metrics.IncrementSmeAgentsSpawned();
                        break;
                    case 1:
                        metrics.IncrementSmeAgentsRetired();
                        break;
                    case 2:
                        metrics.IncrementMcpServerErrors();
                        break;
                    case 3:
                        metrics.IncrementKnowledgeFetchSuccesses();
                        break;
                    case 4:
                        metrics.IncrementKnowledgeFetchFailures();
                        break;
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        var snapshot = metrics.GetSnapshot();

        // Each counter should have been incremented taskCount/5 times (10 times each)
        Assert.Equal(10, snapshot.SmeAgentsSpawned);
        Assert.Equal(10, snapshot.SmeAgentsRetired);
        Assert.Equal(10, snapshot.McpServerErrors);
        Assert.Equal(10, snapshot.KnowledgeFetchSuccesses);
        Assert.Equal(10, snapshot.KnowledgeFetchFailures);
    }
}

public class KnowledgeBudgetTests
{
    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars returns 8000 for "premium" tier.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_Premium_Returns8000()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars("premium");
        Assert.Equal(8000, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars returns 4000 for "standard" tier.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_Standard_Returns4000()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars("standard");
        Assert.Equal(4000, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars returns 2000 for "budget" tier.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_Budget_Returns2000()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars("budget");
        Assert.Equal(2000, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars returns 2000 for "local" tier.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_Local_Returns2000()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars("local");
        Assert.Equal(2000, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars returns 2500 for unknown tier.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_Unknown_Returns2500()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars("unknown");
        Assert.Equal(2500, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars is case-insensitive.
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_CaseInsensitive()
    {
        Assert.Equal(8000, KnowledgeBudget.GetMaxKnowledgeChars("PREMIUM"));
        Assert.Equal(8000, KnowledgeBudget.GetMaxKnowledgeChars("Premium"));
        Assert.Equal(8000, KnowledgeBudget.GetMaxKnowledgeChars("PrEmIuM"));

        Assert.Equal(4000, KnowledgeBudget.GetMaxKnowledgeChars("STANDARD"));
        Assert.Equal(4000, KnowledgeBudget.GetMaxKnowledgeChars("Standard"));

        Assert.Equal(2000, KnowledgeBudget.GetMaxKnowledgeChars("BUDGET"));
        Assert.Equal(2000, KnowledgeBudget.GetMaxKnowledgeChars("Budget"));

        Assert.Equal(2000, KnowledgeBudget.GetMaxKnowledgeChars("LOCAL"));
        Assert.Equal(2000, KnowledgeBudget.GetMaxKnowledgeChars("Local"));
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxKnowledgeChars handles null input (returns default 2500).
    /// </summary>
    [Fact]
    public void GetMaxKnowledgeChars_NullInput_ReturnsDefault()
    {
        int result = KnowledgeBudget.GetMaxKnowledgeChars(null);
        Assert.Equal(2500, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxRoleDescriptionChars returns correct values for all tiers.
    /// </summary>
    [Fact]
    public void GetMaxRoleDescriptionChars_AllTiers()
    {
        Assert.Equal(3000, KnowledgeBudget.GetMaxRoleDescriptionChars("premium"));
        Assert.Equal(2000, KnowledgeBudget.GetMaxRoleDescriptionChars("standard"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxRoleDescriptionChars("budget"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxRoleDescriptionChars("local"));
        Assert.Equal(1500, KnowledgeBudget.GetMaxRoleDescriptionChars("unknown"));
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxRoleDescriptionChars is case-insensitive.
    /// </summary>
    [Fact]
    public void GetMaxRoleDescriptionChars_CaseInsensitive()
    {
        Assert.Equal(3000, KnowledgeBudget.GetMaxRoleDescriptionChars("PREMIUM"));
        Assert.Equal(3000, KnowledgeBudget.GetMaxRoleDescriptionChars("Premium"));
        Assert.Equal(2000, KnowledgeBudget.GetMaxRoleDescriptionChars("STANDARD"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxRoleDescriptionChars("BUDGET"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxRoleDescriptionChars("LOCAL"));
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxRoleDescriptionChars handles null input (returns default 1500).
    /// </summary>
    [Fact]
    public void GetMaxRoleDescriptionChars_NullInput_ReturnsDefault()
    {
        int result = KnowledgeBudget.GetMaxRoleDescriptionChars(null);
        Assert.Equal(1500, result);
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxPerLinkChars returns correct values for all tiers.
    /// </summary>
    [Fact]
    public void GetMaxPerLinkChars_AllTiers()
    {
        Assert.Equal(2000, KnowledgeBudget.GetMaxPerLinkChars("premium"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxPerLinkChars("standard"));
        Assert.Equal(500, KnowledgeBudget.GetMaxPerLinkChars("budget"));
        Assert.Equal(500, KnowledgeBudget.GetMaxPerLinkChars("local"));
        Assert.Equal(800, KnowledgeBudget.GetMaxPerLinkChars("unknown"));
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxPerLinkChars is case-insensitive.
    /// </summary>
    [Fact]
    public void GetMaxPerLinkChars_CaseInsensitive()
    {
        Assert.Equal(2000, KnowledgeBudget.GetMaxPerLinkChars("PREMIUM"));
        Assert.Equal(2000, KnowledgeBudget.GetMaxPerLinkChars("Premium"));
        Assert.Equal(1000, KnowledgeBudget.GetMaxPerLinkChars("STANDARD"));
        Assert.Equal(500, KnowledgeBudget.GetMaxPerLinkChars("BUDGET"));
        Assert.Equal(500, KnowledgeBudget.GetMaxPerLinkChars("LOCAL"));
    }

    /// <summary>
    /// KnowledgeBudget - GetMaxPerLinkChars handles null input (returns default 800).
    /// </summary>
    [Fact]
    public void GetMaxPerLinkChars_NullInput_ReturnsDefault()
    {
        int result = KnowledgeBudget.GetMaxPerLinkChars(null);
        Assert.Equal(800, result);
    }
}
