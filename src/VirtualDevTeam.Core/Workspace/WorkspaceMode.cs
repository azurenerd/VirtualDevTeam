namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Determines how agent workspaces are created and managed.
/// </summary>
public enum WorkspaceMode
{
    /// <summary>
    /// Current default — full git clone per agent into .agents/ directory.
    /// Each agent gets its own complete copy of the repository.
    /// </summary>
    Clone,

    /// <summary>
    /// Single canonical clone shared by all agents, with lightweight
    /// git worktrees for per-agent isolation. Shares .git/objects
    /// for disk efficiency (~seconds to create vs minutes for clone).
    /// </summary>
    Worktree,

    /// <summary>
    /// Use the operator's existing checkout. Agent branches live in
    /// lightweight worktrees branched off the operator's .git directory.
    /// VDT never modifies the operator's working tree.
    /// </summary>
    InPlace,
}
