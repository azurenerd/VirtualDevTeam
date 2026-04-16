using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Core.Tests;

public class AgentTaskTrackerTests : IDisposable
{
    private readonly AgentTaskTracker _tracker;
    private readonly List<AgentTaskStep> _changedSteps = new();

    public AgentTaskTrackerTests()
    {
        _tracker = new AgentTaskTracker(NullLogger<AgentTaskTracker>.Instance);
        _tracker.OnStepChanged += step => _changedSteps.Add(step);
    }

    public void Dispose()
    {
        _tracker.OnStepChanged -= step => _changedSteps.Add(step);
    }

    [Fact]
    public void BeginStep_CreatesStepWithCorrectState()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Read context", "Reading PMSpec");

        Assert.NotNull(stepId);
        Assert.Contains("agent-1", stepId);

        var step = _tracker.GetCurrentStep("agent-1");
        Assert.NotNull(step);
        Assert.Equal("Read context", step.Name);
        Assert.Equal("Reading PMSpec", step.Description);
        Assert.Equal(AgentTaskStepStatus.InProgress, step.Status);
        Assert.NotNull(step.StartedAt);
        Assert.Null(step.CompletedAt);
    }

    [Fact]
    public void CompleteStep_SetsCompletedStatus()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.CompleteStep(stepId);

        var steps = _tracker.GetSteps("agent-1");
        Assert.Single(steps);
        Assert.Equal(AgentTaskStepStatus.Completed, steps[0].Status);
        Assert.NotNull(steps[0].CompletedAt);
    }

    [Fact]
    public void FailStep_SetsFailedStatusWithReason()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.FailStep(stepId, "API error");

        var steps = _tracker.GetSteps("agent-1");
        Assert.Single(steps);
        Assert.Equal(AgentTaskStepStatus.Failed, steps[0].Status);
        Assert.Equal("API error", steps[0].Description);
    }

    [Fact]
    public void SetStepWaiting_SetsWaitingOnHumanStatus()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Decision gate");
        _tracker.SetStepWaiting(stepId);

        var steps = _tracker.GetSteps("agent-1");
        Assert.Single(steps);
        Assert.Equal(AgentTaskStepStatus.WaitingOnHuman, steps[0].Status);
    }

    [Fact]
    public void RecordSubStep_AddsToStepSubSteps()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Multi-turn design");

        _tracker.RecordSubStep(stepId, "Turn 1: Key decisions", TimeSpan.FromSeconds(8.1), 0.02m);
        _tracker.RecordSubStep(stepId, "Turn 2: Components", TimeSpan.FromSeconds(9.3), 0.03m);

        var step = _tracker.GetCurrentStep("agent-1");
        Assert.NotNull(step);
        Assert.Equal(2, step.SubSteps.Count);
        Assert.Equal("Turn 1: Key decisions", step.SubSteps[0].Description);
        Assert.Equal(0, step.SubSteps[0].TurnIndex);
        Assert.Equal(1, step.SubSteps[1].TurnIndex);
        Assert.Equal(0.05m, step.EstimatedCost);
    }

    [Fact]
    public void RecordLlmCall_IncrementsCountAndCost()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Generate code");

        _tracker.RecordLlmCall(stepId, 0.05m);
        _tracker.RecordLlmCall(stepId, 0.03m);

        var step = _tracker.GetCurrentStep("agent-1");
        Assert.NotNull(step);
        Assert.Equal(2, step.LlmCallCount);
        Assert.Equal(0.08m, step.EstimatedCost);
    }

    [Fact]
    public void GetProgress_ReturnsCorrectCounts()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.CompleteStep(s1);

        var s2 = _tracker.BeginStep("agent-1", "task-1", "Step 2");
        _tracker.CompleteStep(s2, AgentTaskStepStatus.Skipped);

        _tracker.BeginStep("agent-1", "task-1", "Step 3"); // in-progress

        var (completed, total) = _tracker.GetProgress("agent-1");
        Assert.Equal(2, completed); // completed + skipped
        Assert.Equal(3, total);
    }

    [Fact]
    public void GetActiveSteps_ReturnsOnlyInProgressSteps()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.CompleteStep(s1);

        _tracker.BeginStep("agent-1", "task-1", "Step 2"); // in-progress
        _tracker.BeginStep("agent-2", "task-2", "Step 1"); // in-progress

        var active = _tracker.GetActiveSteps();
        Assert.Equal(2, active.Count);
        Assert.All(active, s => Assert.Equal(AgentTaskStepStatus.InProgress, s.Status));
    }

    [Fact]
    public void GetTaskSteps_FiltersByTaskId()
    {
        _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.BeginStep("agent-1", "task-2", "Step 2");
        _tracker.BeginStep("agent-1", "task-1", "Step 3");

        var task1Steps = _tracker.GetTaskSteps("agent-1", "task-1");
        Assert.Equal(2, task1Steps.Count);
        Assert.All(task1Steps, s => Assert.Equal("task-1", s.TaskId));
    }

    [Fact]
    public void GetSteps_EmptyForUnknownAgent()
    {
        var steps = _tracker.GetSteps("unknown-agent");
        Assert.Empty(steps);
    }

    [Fact]
    public void GetCurrentStep_NullForUnknownAgent()
    {
        var step = _tracker.GetCurrentStep("unknown-agent");
        Assert.Null(step);
    }

    [Fact]
    public void GetProgress_ZeroForUnknownAgent()
    {
        var (completed, total) = _tracker.GetProgress("unknown-agent");
        Assert.Equal(0, completed);
        Assert.Equal(0, total);
    }

    [Fact]
    public void OnStepChanged_FiresForAllStateChanges()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        _tracker.RecordSubStep(stepId, "Sub-step");
        _tracker.RecordLlmCall(stepId, 0.01m);
        _tracker.CompleteStep(stepId);

        Assert.Equal(4, _changedSteps.Count); // begin + substep + llmcall + complete
    }

    [Fact]
    public void StepIndex_IncrementsPerAgent()
    {
        _tracker.BeginStep("agent-1", "task-1", "Step A");
        _tracker.BeginStep("agent-1", "task-1", "Step B");
        _tracker.BeginStep("agent-1", "task-1", "Step C");

        var steps = _tracker.GetSteps("agent-1");
        Assert.Equal(0, steps[0].StepIndex);
        Assert.Equal(1, steps[1].StepIndex);
        Assert.Equal(2, steps[2].StepIndex);
    }

    [Fact]
    public void Elapsed_ComputedCorrectlyForCompletedStep()
    {
        var stepId = _tracker.BeginStep("agent-1", "task-1", "Step 1");
        // Simulate time passing by completing immediately
        _tracker.CompleteStep(stepId);

        var step = _tracker.GetSteps("agent-1")[0];
        Assert.NotNull(step.Elapsed);
        Assert.True(step.Elapsed.Value.TotalMilliseconds >= 0);
    }

    [Fact]
    public void InvalidStepId_OperationsAreNoOps()
    {
        // These should not throw
        _tracker.CompleteStep("nonexistent");
        _tracker.FailStep("nonexistent", "reason");
        _tracker.SetStepWaiting("nonexistent");
        _tracker.RecordSubStep("nonexistent", "sub");
        _tracker.RecordLlmCall("nonexistent", 0.01m);
    }

    [Fact]
    public void ThreadSafety_ConcurrentBeginStep()
    {
        var tasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() =>
                _tracker.BeginStep($"agent-{i % 5}", "task-1", $"Step {i}")))
            .ToArray();

        Task.WaitAll(tasks);

        // All 5 agents should have steps
        for (int i = 0; i < 5; i++)
        {
            var steps = _tracker.GetSteps($"agent-{i}");
            Assert.Equal(10, steps.Count);
        }
    }

    // ── Grouped steps tests ──

    [Fact]
    public void GetGroupedSteps_GroupsByTaskId()
    {
        _tracker.BeginStep("agent-1", "task-a", "Step A1");
        _tracker.BeginStep("agent-1", "task-b", "Step B1");
        _tracker.BeginStep("agent-1", "task-a", "Step A2");

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Equal(2, groups.Count);
        Assert.Equal("task-a", groups[0].TaskId);
        Assert.Equal(2, groups[0].Steps.Count);
        Assert.Equal("task-b", groups[1].TaskId);
        Assert.Single(groups[1].Steps);
    }

    [Fact]
    public void GetGroupedSteps_PreservesInsertionOrder()
    {
        _tracker.BeginStep("agent-1", "first", "S1");
        _tracker.BeginStep("agent-1", "second", "S2");
        _tracker.BeginStep("agent-1", "third", "S3");

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Equal(3, groups.Count);
        Assert.Equal("first", groups[0].TaskId);
        Assert.Equal("second", groups[1].TaskId);
        Assert.Equal("third", groups[2].TaskId);
    }

    [Fact]
    public void GetGroupedSteps_ReturnsEmptyForUnknownAgent()
    {
        var groups = _tracker.GetGroupedSteps("nonexistent");
        Assert.Empty(groups);
    }

    [Fact]
    public void GetGroupedSteps_CalculatesPerTaskProgress()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-a", "Step 1");
        _tracker.CompleteStep(s1);
        _tracker.BeginStep("agent-1", "task-a", "Step 2");

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Single(groups);
        Assert.Equal(1, groups[0].Completed);
        Assert.Equal(2, groups[0].Total);
        Assert.Equal(AgentTaskStepStatus.InProgress, groups[0].Status);
    }

    [Fact]
    public void GetGroupedSteps_CompletedGroupStatus()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-a", "Step 1");
        _tracker.CompleteStep(s1);
        var s2 = _tracker.BeginStep("agent-1", "task-a", "Step 2");
        _tracker.CompleteStep(s2);

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Single(groups);
        Assert.Equal(AgentTaskStepStatus.Completed, groups[0].Status);
        Assert.NotNull(groups[0].CompletedAt);
        Assert.NotNull(groups[0].TotalElapsed);
    }

    [Fact]
    public void GetGroupedSteps_SumsLlmCallsAndCost()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-a", "Step 1");
        _tracker.RecordLlmCall(s1, 0.05m);
        _tracker.CompleteStep(s1);
        var s2 = _tracker.BeginStep("agent-1", "task-a", "Step 2");
        _tracker.RecordLlmCall(s2, 0.10m);
        _tracker.RecordLlmCall(s2, 0.03m);
        _tracker.CompleteStep(s2);

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Equal(3, groups[0].TotalLlmCalls);
        Assert.Equal(0.18m, groups[0].TotalCost);
    }

    [Fact]
    public void GetGroupedSteps_WaitingOnHumanStatus()
    {
        var s1 = _tracker.BeginStep("agent-1", "task-a", "Step 1");
        _tracker.CompleteStep(s1);
        var s2 = _tracker.BeginStep("agent-1", "task-a", "Gate");
        _tracker.SetStepWaiting(s2);

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Equal(AgentTaskStepStatus.WaitingOnHuman, groups[0].Status);
    }

    [Theory]
    [InlineData("pm-kickoff", "Project Kickoff")]
    [InlineData("pm-spec", "PM Specification")]
    [InlineData("pe-planning", "Engineering Planning")]
    [InlineData("pe-orchestration", "Engineer Orchestration")]
    [InlineData("te-loop", "Test Monitoring")]
    [InlineData("te-pr-42", "PR #42 Testing")]
    [InlineData("te-pr-1357", "PR #1357 Testing")]
    [InlineData("issue-99", "Issue #99")]
    [InlineData("research-abc123", "Research")]
    [InlineData("arch-design", "Architecture Design")]
    [InlineData("some-unknown-task", "Some Unknown Task")]
    public void GetTaskDisplayName_MapsCorrectly(string taskId, string expected)
    {
        Assert.Equal(expected, AgentTaskTracker.GetTaskDisplayName(taskId));
    }

    [Fact]
    public void GetGroupedSteps_HasCorrectDisplayNames()
    {
        _tracker.BeginStep("agent-1", "pe-planning", "Read arch");
        _tracker.BeginStep("agent-1", "pe-orchestration", "Assign");

        var groups = _tracker.GetGroupedSteps("agent-1");

        Assert.Equal("Engineering Planning", groups[0].DisplayName);
        Assert.Equal("Engineer Orchestration", groups[1].DisplayName);
    }
}

public class AgentStepTemplatesTests
{
    [Theory]
    [InlineData(AgentRole.Researcher, 5)]
    [InlineData(AgentRole.ProgramManager, 7)]
    [InlineData(AgentRole.Architect, 7)]
    [InlineData(AgentRole.SoftwareEngineer, 8)]
    [InlineData(AgentRole.TestEngineer, 4)]
    [InlineData(AgentRole.Custom, 3)]
    public void GetTemplateSteps_ReturnsCorrectCountPerRole(AgentRole role, int expectedCount)
    {
        var steps = AgentStepTemplates.GetTemplateSteps(role);
        Assert.Equal(expectedCount, steps.Count);
    }

    [Fact]
    public void GetTemplateSteps_AllStepsHaveContent()
    {
        foreach (AgentRole role in Enum.GetValues<AgentRole>())
        {
            var steps = AgentStepTemplates.GetTemplateSteps(role);
            Assert.NotEmpty(steps);
            Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        }
    }

    [Fact]
    public void GetTemplateSteps_NoDuplicatesWithinRole()
    {
        foreach (AgentRole role in Enum.GetValues<AgentRole>())
        {
            var steps = AgentStepTemplates.GetTemplateSteps(role);
            Assert.Equal(steps.Count, steps.Distinct().Count());
        }
    }
}
