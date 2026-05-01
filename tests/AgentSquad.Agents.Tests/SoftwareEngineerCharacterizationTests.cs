using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Agents.Tests;

/// <summary>
/// Characterization tests that lock critical SE agent behaviors before decomposition.
/// These tests verify the existing behavior of dependency gating, task assignment,
/// rework cycle tracking, review claim semantics, and PR-task correlation.
/// </summary>
public class SoftwareEngineerCharacterizationTests
{
    // ── Dependency Gating ──────────────────────────────────────────────

    private static EngineeringTaskIssueManager CreateManagerWithTasks(params EngineeringTask[] tasks)
    {
        var mgr = new EngineeringTaskIssueManager(NullLogger.Instance);
        mgr.SeedCacheForTesting(tasks);
        return mgr;
    }

    [Fact]
    public void AreDependenciesMet_NoDependencies_ReturnsTrue()
    {
        var task = new EngineeringTask { Id = "T1", IssueNumber = 10, DependencyIssueNumbers = [] };
        var mgr = CreateManagerWithTasks(task);

        Assert.True(mgr.AreDependenciesMet(task));
    }

    [Fact]
    public void AreDependenciesMet_AllDepsDone_ReturnsTrue()
    {
        var dep1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Done" };
        var dep2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Status = "closed" };
        var task = new EngineeringTask { Id = "T3", IssueNumber = 12, DependencyIssueNumbers = [10, 11] };
        var mgr = CreateManagerWithTasks(dep1, dep2, task);

        Assert.True(mgr.AreDependenciesMet(task));
    }

    [Fact]
    public void AreDependenciesMet_OnePending_ReturnsFalse()
    {
        var dep1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Done" };
        var dep2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Status = "Pending" };
        var task = new EngineeringTask { Id = "T3", IssueNumber = 12, DependencyIssueNumbers = [10, 11] };
        var mgr = CreateManagerWithTasks(dep1, dep2, task);

        Assert.False(mgr.AreDependenciesMet(task));
    }

    [Fact]
    public void AreDependenciesMet_UnknownDepIssue_ReturnsTrue()
    {
        // If a dependency issue doesn't exist in cache, treat as met (defensive)
        var task = new EngineeringTask { Id = "T1", IssueNumber = 10, DependencyIssueNumbers = [999] };
        var mgr = CreateManagerWithTasks(task);

        Assert.True(mgr.AreDependenciesMet(task));
    }

    [Fact]
    public void AreDependenciesMet_InProgressDep_ReturnsFalse()
    {
        var dep = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "InProgress" };
        var task = new EngineeringTask { Id = "T2", IssueNumber = 11, DependencyIssueNumbers = [10] };
        var mgr = CreateManagerWithTasks(dep, task);

        Assert.False(mgr.AreDependenciesMet(task));
    }

    // ── FindNextAssignableTask (combines wave + deps + status) ─────────

    [Fact]
    public void FindNextAssignableTask_SkipsTasksWithUnmetDeps()
    {
        var dep = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Pending", Complexity = "High" };
        var blocked = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Pending", Complexity = "High", DependencyIssueNumbers = [10] };
        var mgr = CreateManagerWithTasks(dep, blocked);

        // T1 is assignable (no deps), T2 is blocked by T1
        var next = mgr.FindNextAssignableTask("High");
        Assert.NotNull(next);
        Assert.Equal("T1", next.Id);
    }

    [Fact]
    public void FindNextAssignableTask_RespectsComplexityPreference()
    {
        var high = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Pending", Complexity = "High" };
        var medium = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Pending", Complexity = "Medium" };
        var mgr = CreateManagerWithTasks(high, medium);

        // Request Medium first — should get T2
        var next = mgr.FindNextAssignableTask("Medium", "High");
        Assert.NotNull(next);
        Assert.Equal("T2", next.Id);
    }

    [Fact]
    public void FindNextAssignableTask_SkipsNonPendingTasks()
    {
        var inProgress = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "InProgress", Complexity = "High" };
        var done = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Done", Complexity = "High" };
        var pending = new EngineeringTask { Id = "T3", IssueNumber = 12, Wave = "W0", Status = "Pending", Complexity = "High" };
        var mgr = CreateManagerWithTasks(inProgress, done, pending);

        var next = mgr.FindNextAssignableTask("High");
        Assert.NotNull(next);
        Assert.Equal("T3", next.Id);
    }

    [Fact]
    public void FindNextAssignableTask_ReturnsNull_WhenAllBlocked()
    {
        var dep = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Pending", Complexity = "High" };
        var blocked = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W1", Status = "Pending", Complexity = "Medium" };
        var mgr = CreateManagerWithTasks(dep, blocked);

        // Request Medium only — T2 is blocked by wave (W0 not done)
        var next = mgr.FindNextAssignableTask("Medium");
        Assert.Null(next);
    }

    [Fact]
    public void FindNextAssignableTask_FallsThroughComplexityLevels()
    {
        var low = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Pending", Complexity = "Low" };
        var mgr = CreateManagerWithTasks(low);

        // Request High, then Medium, then Low — only Low exists
        var next = mgr.FindNextAssignableTask("High", "Medium", "Low");
        Assert.NotNull(next);
        Assert.Equal("T1", next.Id);
    }

    // ── FindAssignedTo (worker recovery) ───────────────────────────────

    [Fact]
    public void FindAssignedTo_ReturnsAssignedTask()
    {
        var task = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Assigned", AssignedTo = "Software Engineer 1" };
        var mgr = CreateManagerWithTasks(task);

        var found = mgr.FindAssignedTo("Software Engineer 1");
        Assert.NotNull(found);
        Assert.Equal("T1", found.Id);
    }

    [Fact]
    public void FindAssignedTo_CaseInsensitive()
    {
        var task = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "InProgress", AssignedTo = "Software Engineer 1" };
        var mgr = CreateManagerWithTasks(task);

        var found = mgr.FindAssignedTo("software engineer 1");
        Assert.NotNull(found);
    }

    [Fact]
    public void FindAssignedTo_SkipsDoneTasks()
    {
        var done = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Done", AssignedTo = "Software Engineer 1" };
        var mgr = CreateManagerWithTasks(done);

        Assert.Null(mgr.FindAssignedTo("Software Engineer 1"));
    }

    [Fact]
    public void FindAssignedTo_SkipsPendingTasks()
    {
        var pending = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Pending", AssignedTo = "Software Engineer 1" };
        var mgr = CreateManagerWithTasks(pending);

        Assert.Null(mgr.FindAssignedTo("Software Engineer 1"));
    }

    // ── File Overlap Detection (parallel safety) ──────────────────────

    [Fact]
    public void DetectFileOverlaps_SharedFilesExcluded()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = ["shared/models.cs", "myapp/a.cs"] },
            new() { Id = "T2", OwnedFiles = ["shared/models.cs", "myapp/b.cs"] }
        };

        // shared/models.cs is declared as shared — should NOT appear as overlap
        var sharedFiles = new HashSet<string> { "shared/models.cs" };
        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, sharedFiles);
        Assert.Empty(overlaps);
    }

    [Fact]
    public void DetectFileOverlaps_MixOfSharedAndConflicting()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = ["shared/models.cs", "myapp/service.cs"] },
            new() { Id = "T2", OwnedFiles = ["shared/models.cs", "myapp/service.cs"] }
        };

        var sharedFiles = new HashSet<string> { "shared/models.cs" };
        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, sharedFiles);
        Assert.Single(overlaps);
        Assert.True(overlaps.ContainsKey("myapp/service.cs"));
    }

    // ── CanRelaxDependency (typed dependency relaxation) ──────────────

    [Fact]
    public void CanRelaxDependency_ApiType_RelaxedWhenDepIsW1()
    {
        var dep = new EngineeringTask { Id = "T2", Wave = "W1" };
        var shared = new HashSet<string>();

        Assert.True(SoftwareEngineerAgent.CanRelaxDependency("api", dep, shared));
    }

    [Fact]
    public void CanRelaxDependency_InterfaceType_RelaxedWhenSharedFilesExist()
    {
        var dep = new EngineeringTask { Id = "T2", Wave = "W0" };
        var shared = new HashSet<string> { "shared/IService.cs" };

        Assert.True(SoftwareEngineerAgent.CanRelaxDependency("interface", dep, shared));
    }

    [Fact]
    public void CanRelaxDependency_SchemaType_RelaxedOnlyForT1()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W0" };
        var shared = new HashSet<string>();

        Assert.True(SoftwareEngineerAgent.CanRelaxDependency("schema", t1, shared));
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("schema", t2, shared));
    }

    [Fact]
    public void CanRelaxDependency_FileType_NeverRelaxed()
    {
        var dep = new EngineeringTask { Id = "T1", Wave = "W1" };
        var shared = new HashSet<string> { "shared/IService.cs" };

        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("files", dep, shared));
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("file", dep, shared));
    }

    [Fact]
    public void CanRelaxDependency_UnknownType_NeverRelaxed()
    {
        var dep = new EngineeringTask { Id = "T1", Wave = "W1" };
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("unknown_type", dep, new HashSet<string>()));
    }

    // ── Review Claim Atomicity (s_activeReviews) ──────────────────────

    [Fact]
    public void ActiveReviews_TryAdd_OnlyFirstClaimerSucceeds()
    {
        // This tests the ConcurrentDictionary semantics used for cross-PE review claims
        var dict = new System.Collections.Concurrent.ConcurrentDictionary<int, (string AgentId, DateTime ClaimedAt)>();

        var firstClaim = dict.TryAdd(42, ("se1", DateTime.UtcNow));
        var secondClaim = dict.TryAdd(42, ("se2", DateTime.UtcNow));

        Assert.True(firstClaim);
        Assert.False(secondClaim);
        Assert.Equal("se1", dict[42].AgentId);
    }

    [Fact]
    public void ActiveReviews_TryRemove_ClearsForNextClaim()
    {
        var dict = new System.Collections.Concurrent.ConcurrentDictionary<int, (string AgentId, DateTime ClaimedAt)>();

        dict.TryAdd(42, ("se1", DateTime.UtcNow));
        dict.TryRemove(42, out _);
        var reClaim = dict.TryAdd(42, ("se2", DateTime.UtcNow));

        Assert.True(reClaim);
        Assert.Equal("se2", dict[42].AgentId);
    }

    // ── PR Title Parsing (agent-to-task correlation) ──────────────────

    [Fact]
    public void ParseAssignedAgent_PrefixWithColon_ExtractsCorrectly()
    {
        // Critical for PR-task matching during recovery
        Assert.Equal("Software Engineer 1",
            EngineeringTaskIssueManager.ParseAssignedAgent("Software Engineer 1: Implement auth module"));
    }

    [Fact]
    public void ParseAssignedAgent_NameWithNumber_DoesNotPartialMatch()
    {
        // "Software Engineer" should NOT match "Software Engineer 1:" prefix
        var agent = EngineeringTaskIssueManager.ParseAssignedAgent("Software Engineer 1: Build API");
        Assert.Equal("Software Engineer 1", agent);
        Assert.NotEqual("Software Engineer", agent);
    }

    [Fact]
    public void ParseAssignedAgent_BracketFormat_ExtractsFromBothPositions()
    {
        // [T1] Software Engineer 1: Task name
        Assert.Equal("Software Engineer 1",
            EngineeringTaskIssueManager.ParseAssignedAgent("[T1] Software Engineer 1: Build auth"));

        // Without brackets
        Assert.Equal("Software Engineer 1",
            EngineeringTaskIssueManager.ParseAssignedAgent("Software Engineer 1: Build auth"));
    }

    // ── Task Inventory Counts ─────────────────────────────────────────

    [Fact]
    public void TaskCounts_ReflectCacheState()
    {
        var t1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "Done" };
        var t2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Status = "Pending" };
        var t3 = new EngineeringTask { Id = "T3", IssueNumber = 12, Status = "InProgress" };
        var mgr = CreateManagerWithTasks(t1, t2, t3);

        Assert.Equal(3, mgr.TotalCount);
        Assert.Equal(1, mgr.DoneCount);
        Assert.Equal(1, mgr.PendingCount);
    }

    [Fact]
    public void TaskCounts_ClosedCountsAsDone()
    {
        var t1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Status = "closed" };
        var t2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Status = "Complete" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.Equal(2, mgr.DoneCount);
        Assert.Equal(0, mgr.PendingCount);
    }

    // ── Wave + Dependency Combined Gating ─────────────────────────────

    [Fact]
    public void FindNextAssignableTask_CombinesWaveAndDependencyGating()
    {
        // T1: W0, no deps → assignable
        // T2: W0, depends on T1 → blocked by deps
        // T3: W1, no deps → blocked by wave (T1 not done)
        var t1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Pending", Complexity = "High" };
        var t2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Pending", Complexity = "High", DependencyIssueNumbers = [10] };
        var t3 = new EngineeringTask { Id = "T3", IssueNumber = 12, Wave = "W1", Status = "Pending", Complexity = "High" };
        var mgr = CreateManagerWithTasks(t1, t2, t3);

        var next = mgr.FindNextAssignableTask("High");
        Assert.NotNull(next);
        Assert.Equal("T1", next.Id); // Only T1 is assignable
    }

    [Fact]
    public void FindNextAssignableTask_UnblocksAfterDepsComplete()
    {
        var t1 = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Done", Complexity = "High" };
        var t2 = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Pending", Complexity = "High", DependencyIssueNumbers = [10] };
        var mgr = CreateManagerWithTasks(t1, t2);

        var next = mgr.FindNextAssignableTask("High");
        Assert.NotNull(next);
        Assert.Equal("T2", next.Id); // T2 unblocked because T1 is Done
    }

    // ── Path Normalization Edge Cases ─────────────────────────────────

    [Fact]
    public void NormalizeFilePath_StripsCSharpNamespaceAnnotation()
    {
        // File plan format: "MyApp/File.cs(MyApp.Services)" — strip namespace annotation
        Assert.Equal("myapp/file.cs", SoftwareEngineerAgent.NormalizeFilePath("MyApp/File.cs(MyApp.Services)"));
    }

    [Fact]
    public void NormalizeFilePath_NormalizesBackslashes()
    {
        Assert.Equal("myapp/file.cs", SoftwareEngineerAgent.NormalizeFilePath("MyApp\\File.cs"));
    }

    [Fact]
    public void NormalizeFilePath_StripsLeadingSlash()
    {
        Assert.Equal("myapp/file.cs", SoftwareEngineerAgent.NormalizeFilePath("/MyApp/File.cs"));
    }

    // ── Integration Task Convention ─────────────────────────────────

    [Fact]
    public void TFinal_ExcludedFromWaveBlocking()
    {
        // T-FINAL is excluded from blocking later waves in IsWaveEligible
        var tFinal = new EngineeringTask { Id = "T-FINAL", Wave = "W99", Status = "Pending" };
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Done" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, tFinal, t2);

        // T-FINAL shouldn't block W1 even though it's in a higher wave and not done
        Assert.True(mgr.IsWaveEligible(t2));
    }

    [Fact]
    public void TFinal_ExcludedFromNextAvailableTaskIdNumbering()
    {
        var t1 = new EngineeringTask { Id = "T1", Status = "Done" };
        var tFinal = new EngineeringTask { Id = "T-FINAL", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, tFinal);

        // T-FINAL uses "T-" prefix, so should not affect numeric ID generation
        Assert.Equal("T2", mgr.NextAvailableTaskId());
    }
}
