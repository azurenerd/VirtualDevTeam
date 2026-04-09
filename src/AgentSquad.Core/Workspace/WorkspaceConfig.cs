namespace AgentSquad.Core.Workspace;

/// <summary>
/// Configuration for agent local workspaces — enables real build and test
/// verification before code is committed to GitHub.
/// When RootPath is null/empty, agents fall back to GitHub API-only mode.
/// </summary>
public class WorkspaceConfig
{
    /// <summary>
    /// Root directory for agent workspaces. Each agent gets a subdirectory.
    /// Example: "C:/AgentSquadWorkspaces" → "C:/AgentSquadWorkspaces/PrincipalEngineer/{repo}"
    /// When null or empty, local workspace is disabled and agents use GitHub API-only mode.
    /// </summary>
    public string? RootPath { get; set; } = @"C:\Agents";

    /// <summary>
    /// Command to build the project. Executed from the repo root directory.
    /// Examples: "dotnet build", "npm run build", "cargo build"
    /// </summary>
    public string BuildCommand { get; set; } = "dotnet build";

    /// <summary>
    /// Command to run tests. Executed from the repo root directory.
    /// Examples: "dotnet test --no-build --verbosity normal", "npm test", "cargo test"
    /// </summary>
    public string TestCommand { get; set; } = "dotnet test --no-build --verbosity normal";

    /// <summary>
    /// Maximum seconds to wait for a build to complete before killing the process.
    /// </summary>
    public int BuildTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum seconds to wait for tests to complete before killing the process.
    /// </summary>
    public int TestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum attempts to fix build errors by feeding them back to the AI.
    /// After this many failures, the agent logs the error and skips the step.
    /// </summary>
    public int MaxBuildRetries { get; set; } = 3;

    /// <summary>
    /// Maximum attempts to fix test failures by feeding them back to the AI.
    /// </summary>
    public int MaxTestRetries { get; set; } = 3;

    /// <summary>
    /// Whether to delete agent workspaces when the project is complete (all issues closed).
    /// </summary>
    public bool CleanupOnProjectComplete { get; set; } = true;

    /// <summary>
    /// Whether local workspace mode is enabled (RootPath is configured).
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>
    /// Optional .gitconfig overrides for cloned repos (e.g., user.name, user.email).
    /// Applied via git config after clone.
    /// </summary>
    public string GitUserName { get; set; } = "AgentSquad";

    /// <summary>
    /// Git email for commits in local workspaces.
    /// </summary>
    public string GitUserEmail { get; set; } = "agentsquad@noreply.github.com";
}
