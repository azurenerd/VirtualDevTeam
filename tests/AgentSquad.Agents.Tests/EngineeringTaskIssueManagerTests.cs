using AgentSquad.Core.GitHub.Models;

namespace AgentSquad.Agents.Tests;

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
}
