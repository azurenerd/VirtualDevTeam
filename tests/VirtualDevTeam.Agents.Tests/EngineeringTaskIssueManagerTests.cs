using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.Agents.Tests;

public class EngineeringTaskIssueManagerTests
{
    [Fact]
    public void ParseTaskId_BracketFormat_ExtractsId()
    {
        Assert.Equal("T1", EngineeringTaskIssueManager.ParseTaskId("[T1] Set up project"));
        Assert.Equal("T-42", EngineeringTaskIssueManager.ParseTaskId("[T-42] Build auth"));
        Assert.Null(EngineeringTaskIssueManager.ParseTaskId("No brackets here"));
    }

    [Fact]
    public void ParseTaskName_BracketFormat_ExtractsName()
    {
        Assert.Equal("Set up project", EngineeringTaskIssueManager.ParseTaskName("[T1] Set up project"));
    }

    [Fact]
    public void ParseTaskName_AgentPrefixAfterBracket_StripsAgent()
    {
        Assert.Equal("Set up project", EngineeringTaskIssueManager.ParseTaskName("[T1] Software Engineer 1: Set up project"));
    }

    [Fact]
    public void ParseAssignedAgent_AgentPrefixAfterBracket_ReturnsAgent()
    {
        Assert.Equal("Software Engineer 1", EngineeringTaskIssueManager.ParseAssignedAgent("[T1] Software Engineer 1: Set up project"));
    }

    [Fact]
    public void ParseAssignedAgent_NoBracket_ReturnsAgent()
    {
        Assert.Equal("Software Engineer 1", EngineeringTaskIssueManager.ParseAssignedAgent("Software Engineer 1: Set up project"));
    }

    [Fact]
    public void ParseAssignedAgent_NoAgent_ReturnsNull()
    {
        Assert.Null(EngineeringTaskIssueManager.ParseAssignedAgent("[T1] Set up project"));
    }

    [Fact]
    public void ParseComplexityFromLabels_ExtractsCorrectly()
    {
        Assert.Equal("High", EngineeringTaskIssueManager.ParseComplexityFromLabels(["complexity:high", "engineering-task"]));
        Assert.Equal("Low", EngineeringTaskIssueManager.ParseComplexityFromLabels(["complexity:low"]));
        Assert.Equal("Medium", EngineeringTaskIssueManager.ParseComplexityFromLabels(["engineering-task"]));
    }

    [Fact]
    public void ParseStatusFromLabels_ReturnsCorrectStatus()
    {
        Assert.Equal("Pending", EngineeringTaskIssueManager.ParseStatusFromLabels(["status:pending"]));
        Assert.Equal("Assigned", EngineeringTaskIssueManager.ParseStatusFromLabels(["status:assigned"]));
        Assert.Equal("InProgress", EngineeringTaskIssueManager.ParseStatusFromLabels(["status:in-progress"]));
        Assert.Equal("Blocked", EngineeringTaskIssueManager.ParseStatusFromLabels(["status:blocked"]));
        Assert.Equal("Pending", EngineeringTaskIssueManager.ParseStatusFromLabels(["engineering-task"]));
    }

    [Fact]
    public void ParseDependencies_ExtractsIssueNumbers()
    {
        var body = "## Metadata\n- **Depends On:** #10, #15, #20";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Equal([10, 15, 20], deps);
    }

    [Fact]
    public void ParseDependencies_BoldColonOutside_ExtractsIssueNumbers()
    {
        // Manually-edited variant: colon is outside the closing ** markers
        var body = "## Metadata\n- **Depends On**: #10, #15";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Equal([10, 15], deps);
    }

    [Fact]
    public void ParseDependencies_ItalicFormat_ExtractsIssueNumbers()
    {
        var body = "## Metadata\n- *Depends On:* #42";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Equal([42], deps);
    }

    [Fact]
    public void ParseDependencies_PlainFormat_ExtractsIssueNumbers()
    {
        var body = "## Metadata\nDepends On: #7, #8";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Equal([7, 8], deps);
    }

    [Fact]
    public void ParseDependencies_ListFormat_ExtractsIssueNumbers()
    {
        // Multi-line list format with issue numbers on separate lines
        var body = "## Metadata\n- **Depends On:**\n- #5\n- #6\n- **Wave:** W1";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Equal([5, 6], deps);
    }

    [Fact]
    public void ParseDependencies_ProseTextWithDependsOn_DoesNotFalsePositive()
    {
        // Prose "depends on" without #N references should return empty, not throw
        var body = "## Description\nThis feature depends on the auth module being ready.\n\n## Metadata\n- **Wave:** W1";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Empty(deps);
    }

    [Fact]
    public void ParseDependencies_NoDeps_ReturnsEmpty()
    {
        var body = "## Metadata\n- **Task ID:** T1\n- **Complexity:** High";
        var deps = EngineeringTaskIssueManager.ParseDependencies(body);
        Assert.Empty(deps);
    }

    [Fact]
    public void ParseParentIssue_ExtractsNumber()
    {
        var body = "## Metadata\n- **Parent Issue:** #52\n- **Complexity:** High";
        Assert.Equal(52, EngineeringTaskIssueManager.ParseParentIssue(body));
    }

    [Fact]
    public void ParseParentIssue_NoParent_ReturnsNull()
    {
        Assert.Null(EngineeringTaskIssueManager.ParseParentIssue("Just some text"));
        Assert.Null(EngineeringTaskIssueManager.ParseParentIssue(null));
    }

    [Fact]
    public void ParseDescription_ExtractsBodyBeforeMetadata()
    {
        var body = "## Set up project\n\nCreate the project structure.\n\n## Metadata\n- **Task ID:** T1";
        var desc = EngineeringTaskIssueManager.ParseDescription(body);
        Assert.Equal("Create the project structure.", desc);
    }

    [Fact]
    public void MapIssueToTask_FullIssue_MapsCorrectly()
    {
        var issue = new AgentIssue
        {
            Number = 100,
            Title = "[T3] Software Engineer 1: Build auth module",
            Body = "## Build auth module\n\nImplement JWT auth.\n\n## Metadata\n- **Task ID:** T3\n- **Complexity:** High\n- **Parent Issue:** #52\n- **Depends On:** #98, #99",
            State = "open",
            Url = "https://github.com/owner/repo/issues/100",
            Labels = ["engineering-task", "complexity:high", "status:assigned"]
        };

        var task = EngineeringTaskIssueManager.MapIssueToTask(issue);

        Assert.Equal("T3", task.Id);
        Assert.Equal("Build auth module", task.Name);
        Assert.Equal("High", task.Complexity);
        Assert.Equal("Assigned", task.Status);
        Assert.Equal("Software Engineer 1", task.AssignedTo);
        Assert.Equal(100, task.IssueNumber);
        Assert.Equal(52, task.ParentIssueNumber);
        Assert.Equal([98, 99], task.DependencyIssueNumbers);
    }

    [Fact]
    public void MapIssueToTask_ClosedIssue_StatusIsDone()
    {
        var issue = new AgentIssue
        {
            Number = 101,
            Title = "[T1] Setup scaffolding",
            Body = "## Setup scaffolding\n\nInit project.\n\n## Metadata\n- **Task ID:** T1",
            State = "closed",
            Url = "https://github.com/owner/repo/issues/101",
            Labels = ["engineering-task", "complexity:low", "status:in-progress"]
        };

        var task = EngineeringTaskIssueManager.MapIssueToTask(issue);
        Assert.Equal("Done", task.Status);
    }

    [Fact]
    public void MapIssueToTask_BlockedLabel_StatusIsBlocked()
    {
        var issue = new AgentIssue
        {
            Number = 102,
            Title = "[T2] Blocked work",
            Body = "## Blocked work\n\nCannot complete.\n\n## Metadata\n- **Task ID:** T2",
            State = "open",
            Url = "https://github.com/owner/repo/issues/102",
            Labels = ["engineering-task", "complexity:medium", "status:blocked"]
        };

        var task = EngineeringTaskIssueManager.MapIssueToTask(issue);

        Assert.Equal("Blocked", task.Status);
    }

    [Fact]
    public void FindNextAssignableTask_SkipsBlockedTasks()
    {
        var blocked = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Blocked", Complexity = "High" };
        var pending = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W0", Status = "Pending", Complexity = "High" };
        var mgr = CreateManagerWithTasks(blocked, pending);

        var next = mgr.FindNextAssignableTask("High");

        Assert.NotNull(next);
        Assert.Equal("T2", next.Id);
        Assert.Equal(1, mgr.PendingCount);
        Assert.False(EngineeringTaskIssueManager.IsTaskDone(blocked));
    }

    [Fact]
    public void IsWaveEligible_BlockedEarlierWave_BlocksLaterWaves()
    {
        var blocked = new EngineeringTask { Id = "T1", IssueNumber = 10, Wave = "W0", Status = "Blocked", Complexity = "High" };
        var later = new EngineeringTask { Id = "T2", IssueNumber = 11, Wave = "W1", Status = "Pending", Complexity = "High" };
        var mgr = CreateManagerWithTasks(blocked, later);

        Assert.False(mgr.IsWaveEligible(later));
        Assert.False(mgr.AreAllTasksDone());
    }

    [Fact]
    public async Task MarkBlockedAsync_UpdatesPlatformAndCache()
    {
        var workItems = new FakeWorkItemService();
        var mgr = new EngineeringTaskIssueManager(workItems, NullLogger.Instance);
        mgr.SeedCacheForTesting([
            new EngineeringTask
            {
                Id = "T1",
                Name = "Retrying task",
                IssueNumber = 42,
                Status = "Pending",
                Labels = ["engineering-task", "complexity:high", "status:pending"]
            }
        ]);

        await mgr.MarkBlockedAsync(42, "blocked after retries", CancellationToken.None);

        var task = mgr.FindByIssueNumber(42);
        Assert.NotNull(task);
        Assert.Equal("Blocked", task.Status);
        Assert.Contains("status:blocked", task.Labels);
        Assert.DoesNotContain("status:pending", task.Labels);
        Assert.Equal(0, mgr.PendingCount);
        Assert.Equal(42, workItems.LastUpdateId);
        Assert.Contains("status:blocked", workItems.LastLabels!);
        Assert.DoesNotContain("status:pending", workItems.LastLabels!);
        Assert.Equal((42, "blocked after retries"), workItems.LastComment);
    }

    [Fact]
    public void IsTaskDone_VariousStatuses()
    {
        Assert.True(EngineeringTaskIssueManager.IsTaskDone(new EngineeringTask { Status = "Done" }));
        Assert.True(EngineeringTaskIssueManager.IsTaskDone(new EngineeringTask { Status = "Complete" }));
        Assert.True(EngineeringTaskIssueManager.IsTaskDone(new EngineeringTask { Status = "closed" }));
        Assert.False(EngineeringTaskIssueManager.IsTaskDone(new EngineeringTask { Status = "Pending" }));
        Assert.False(EngineeringTaskIssueManager.IsTaskDone(new EngineeringTask { Status = "InProgress" }));
    }

    [Fact]
    public void BuildIssueBodyWithDeps_ProducesCorrectMarkdown()
    {
        var task = new EngineeringTask
        {
            Id = "T5",
            Name = "Build UI components",
            Description = "Create Blazor components for the dashboard.",
            Complexity = "Medium",
            ParentIssueNumber = 52
        };

        var body = EngineeringTaskIssueManager.BuildIssueBodyWithDeps(task, [98, 99]);

        Assert.Contains("## Build UI components", body);
        Assert.Contains("Create Blazor components", body);
        Assert.Contains("**Task ID:** T5", body);
        Assert.Contains("**Complexity:** Medium", body);
        Assert.Contains("**Wave:** W1", body);
        Assert.Contains("**Parent Issue:** #52", body);
        Assert.Contains("**Depends On:** #98, #99", body);
    }

    // ── Wave Eligibility Tests ─────────────────────────────────────────────

    private static EngineeringTaskIssueManager CreateManagerWithTasks(params EngineeringTask[] tasks)
    {
        var mgr = new EngineeringTaskIssueManager(NullLogger.Instance);
        mgr.SeedCacheForTesting(tasks);
        return mgr;
    }

    [Fact]
    public void IsWaveEligible_W0Task_AlwaysEligible()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Pending" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.True(mgr.IsWaveEligible(t1));
    }

    [Fact]
    public void IsWaveEligible_W1Task_BlockedByPendingW0()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Pending" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.False(mgr.IsWaveEligible(t2));
    }

    [Fact]
    public void IsWaveEligible_W1Task_EligibleWhenW0Done()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Done" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.True(mgr.IsWaveEligible(t2));
    }

    [Fact]
    public void IsWaveEligible_W2Task_BlockedByInProgressW1()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Done" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "InProgress" };
        var t3 = new EngineeringTask { Id = "T3", Wave = "W2", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2, t3);

        Assert.False(mgr.IsWaveEligible(t3));
    }

    [Fact]
    public void IsWaveEligible_TFinal_DoesNotBlockLaterWaves()
    {
        // T-FINAL is in W2 and not done, but should NOT block a W1 task
        // (T-FINAL always depends on everything so it's excluded from blocking)
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Done" };
        var tFinal = new EngineeringTask { Id = "T-FINAL", Wave = "W2", Status = "Pending" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, tFinal, t2);

        // T-FINAL is in W2, T2 is in W1 — T-FINAL doesn't block T2
        Assert.True(mgr.IsWaveEligible(t2));
    }

    [Fact]
    public void IsWaveEligible_NullWave_AlwaysEligible()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = null, Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1);

        Assert.True(mgr.IsWaveEligible(t1));
    }

    [Fact]
    public void IsWaveEligible_MultipleW0Tasks_AllMustBeDone()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Done" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W0", Status = "Pending" };
        var t3 = new EngineeringTask { Id = "T3", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2, t3);

        Assert.False(mgr.IsWaveEligible(t3)); // T2 in W0 not done
    }

    [Fact]
    public void IsWaveEligible_ClosedStatus_CountsAsDone()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "closed" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.True(mgr.IsWaveEligible(t2));
    }

    [Fact]
    public void NextAvailableTaskId_EmptyCache_ReturnsT1()
    {
        var mgr = CreateManagerWithTasks();
        Assert.Equal("T1", mgr.NextAvailableTaskId());
    }

    [Fact]
    public void NextAvailableTaskId_ExistingTasks_ReturnsNextId()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Pending" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t2);

        Assert.Equal("T3", mgr.NextAvailableTaskId());
    }

    [Fact]
    public void NextAvailableTaskId_SkipsSpecialIds()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Pending" };
        var tFinal = new EngineeringTask { Id = "T-FINAL", Wave = "W99", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, tFinal);

        // T-FINAL should not affect numbering (starts with "T-")
        Assert.Equal("T2", mgr.NextAvailableTaskId());
    }

    [Fact]
    public void NextAvailableTaskId_GapInIds_UsesMax()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W0", Status = "Pending" };
        var t5 = new EngineeringTask { Id = "T5", Wave = "W1", Status = "Pending" };
        var mgr = CreateManagerWithTasks(t1, t5);

        // Should use max (5) + 1, not count (2) + 1
        Assert.Equal("T6", mgr.NextAvailableTaskId());
    }

    // ────────────────────────────────────────────────────────────────────────
    // IsEngineeringPrBranch — pins the central engineering-vs-non-engineering
    // PR classifier (2026-05-11 post-run-restart-t1-duplicate fix).
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("feature/something")]
    [InlineData("agent")] // no trailing slash
    public void IsEngineeringPrBranch_NonAgentBranches_ReturnsFalse(string? branch)
    {
        Assert.False(EngineeringTaskIssueManager.IsEngineeringPrBranch(branch));
    }

    [Theory]
    [InlineData("agent/run-123/softwareengineer-1/t1-foundation")]
    [InlineData("agent/run-123/softwareengineer/t-final-integration")]
    [InlineData("agent/run-456/SoftwareEngineer-2/T2_grid_rendering")] // case-insensitive
    public void IsEngineeringPrBranch_SeBranches_ReturnsTrue(string branch)
    {
        Assert.True(EngineeringTaskIssueManager.IsEngineeringPrBranch(branch));
    }

    [Theory]
    // The 2026-05-11 GridGuardians regression case: SME engineer roles.
    [InlineData("agent/run-789/game-developer-1/t1-project-foundation-scaffolding")]
    [InlineData("agent/run-abc/frontend-engineer/t3-grid-rendering")]
    [InlineData("agent/run-def/backend-engineer/t2-api-surface")]
    [InlineData("agent/run-xyz/specialist-engineer-1/t5-pathfinding")]
    [InlineData("agent/run-000/game-engine-engineer/t7-physics")]
    public void IsEngineeringPrBranch_SmeEngineerBranches_ReturnsTrue(string branch)
    {
        Assert.True(EngineeringTaskIssueManager.IsEngineeringPrBranch(branch));
    }

    [Theory]
    // f95607a regression case: auto-merged research/pmspec/architecture must NOT match.
    [InlineData("agent/run-123/architect/architecture")]
    [InlineData("agent/run-123/researcher/research")]
    [InlineData("agent/run-123/programmanager/pmspec")]
    [InlineData("agent/run-456/pm/clarification")]
    [InlineData("agent/run-789/testengineer/tests-for-t1")]
    [InlineData("agent/run-789/test-engineer/tests-for-t1")]
    [InlineData("agent/run-abc/tester/regression")]
    [InlineData("agent/run-def/executive/intake")]
    [InlineData("agent/run-xyz/custom/whatever")]
    public void IsEngineeringPrBranch_NonEngineerBranches_ReturnsFalse(string branch)
    {
        Assert.False(EngineeringTaskIssueManager.IsEngineeringPrBranch(branch));
    }

    [Fact]
    public void IsEngineeringPrBranch_AgentRootWithoutRole_ReturnsTrue()
    {
        // Branches with only two segments still pass — we err on the side of inclusion
        // for engineering. The narrow goal is to exclude the known non-engineer roles.
        Assert.True(EngineeringTaskIssueManager.IsEngineeringPrBranch("agent/some-id"));
        Assert.True(EngineeringTaskIssueManager.IsEngineeringPrBranch("agent/some-id/"));
    }

    private sealed class FakeWorkItemService : IWorkItemService
    {
        public int? LastUpdateId { get; private set; }
        public IReadOnlyList<string>? LastLabels { get; private set; }
        public (int Id, string Comment)? LastComment { get; private set; }

        public Task<PlatformWorkItem> CreateAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default) =>
            Task.FromResult(new PlatformWorkItem { Number = 1, Title = title, Body = body, Labels = labels.ToList() });

        public Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<PlatformWorkItem?>(null);

        public Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task<IReadOnlyList<PlatformWorkItem>> ListAllForProjectAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(string agentName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(string label, string? state = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task UpdateAsync(int id, string? title = null, string? body = null, IReadOnlyList<string>? labels = null, string? state = null, CancellationToken ct = default)
        {
            LastUpdateId = id;
            LastLabels = labels;
            return Task.CompletedTask;
        }

        public Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CloseAsync(int id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task AddCommentAsync(int id, string comment, CancellationToken ct = default)
        {
            LastComment = (id, comment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformComment>>([]);

        public Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);

        public Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
