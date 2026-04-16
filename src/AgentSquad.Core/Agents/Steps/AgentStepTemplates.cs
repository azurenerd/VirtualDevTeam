namespace AgentSquad.Core.Agents.Steps;

/// <summary>
/// Provides expected step templates per agent role. Used by the dashboard to show
/// greyed-out "upcoming steps" before the agent reaches them, giving users a preview
/// of the workflow the agent will follow.
/// </summary>
public static class AgentStepTemplates
{
    public static IReadOnlyList<string> GetTemplateSteps(AgentRole role) => role switch
    {
        AgentRole.Researcher => ResearcherSteps,
        AgentRole.ProgramManager => ProgramManagerSteps,
        AgentRole.Architect => ArchitectSteps,
        AgentRole.SoftwareEngineer => SoftwareEngineerSteps,
        AgentRole.TestEngineer => TestEngineerSteps,
        _ => GenericSteps,
    };

    private static readonly string[] ResearcherSteps =
    [
        "Create research PR",
        "Multi-turn research",
        "Self-assessment & refinement",
        "Commit Research.md",
        "Signal PM"
    ];

    private static readonly string[] ProgramManagerSteps =
    [
        "Read project context",
        "Generate PM Spec",
        "Self-assessment & refinement",
        "Commit PMSpec.md",
        "Human gate review",
        "Team composition analysis",
        "Signal Architect"
    ];

    private static readonly string[] ArchitectSteps =
    [
        "Read context (PMSpec, Research)",
        "Multi-turn architecture design",
        "Self-assessment & impact classification",
        "Commit Architecture.md",
        "Decision gate",
        "Human gate review",
        "Merge PR"
    ];

    private static readonly string[] SoftwareEngineerSteps =
    [
        "Read architecture",
        "Task decomposition",
        "Self-assessment & impact classification",
        "Decision gate",
        "Create GitHub issues",
        "Implement assigned tasks",
        "Self-review",
        "Mark ready for review"
    ];

    private static readonly string[] TestEngineerSteps =
    [
        "Wait for PRs",
        "Generate test plan",
        "Execute tests",
        "Report results"
    ];

    private static readonly string[] GenericSteps =
    [
        "Initialize",
        "Execute task",
        "Finalize"
    ];
}
