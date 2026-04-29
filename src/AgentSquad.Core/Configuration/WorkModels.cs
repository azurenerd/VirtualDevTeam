namespace AgentSquad.Core.Configuration;

/// <summary>
/// Distinguishes between greenfield project creation and incremental feature development.
/// </summary>
public enum WorkMode
{
    /// <summary>Build an entire project from scratch in a new/empty repo.</summary>
    Project,

    /// <summary>Build a single feature against an existing codebase.</summary>
    Feature
}

/// <summary>
/// Lifecycle status of an active run (project or feature).
/// </summary>
public enum RunStatus
{
    /// <summary>Run has been created but not yet started.</summary>
    NotStarted,

    /// <summary>Run is actively executing with agents spawned.</summary>
    Running,

    /// <summary>Run was paused via stop command; can be resumed.</summary>
    Paused,

    /// <summary>Run completed successfully.</summary>
    Completed,

    /// <summary>Run failed with an unrecoverable error.</summary>
    Failed,

    /// <summary>Run was cancelled by the user.</summary>
    Cancelled
}

/// <summary>
/// Lifecycle status of a feature definition.
/// </summary>
public enum FeatureStatus
{
    /// <summary>Feature has been defined but not queued or started.</summary>
    Draft,

    /// <summary>Feature is queued to run next.</summary>
    Queued,

    /// <summary>Feature is currently being built by agents.</summary>
    Running,

    /// <summary>Feature was successfully built and merged.</summary>
    Completed,

    /// <summary>Feature build failed.</summary>
    Failed,

    /// <summary>Feature was cancelled by the user.</summary>
    Cancelled
}

/// <summary>
/// Represents a single unit of work — either a greenfield project or a feature.
/// All workflow state, gates, issues, and PRs are scoped to a run via <see cref="RunId"/>.
/// Only one run can be active at a time (enforced by <c>RunCoordinator</c>).
/// </summary>
public record ActiveRun
{
    /// <summary>Unique identifier for this run.</summary>
    public required string RunId { get; init; }

    /// <summary>Whether this is a greenfield project or incremental feature.</summary>
    public required WorkMode Mode { get; init; }

    /// <summary>If Feature mode, the ID of the <see cref="FeatureDefinition"/> being built.</summary>
    public string? FeatureId { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Target repository (owner/repo).</summary>
    public required string Repo { get; init; }

    /// <summary>Branch to work from (e.g. "main").</summary>
    public required string BaseBranch { get; init; }

    /// <summary>
    /// Target branch for the work. For features, this is the feature branch name.
    /// For projects, this is typically the default branch.
    /// </summary>
    public string? TargetBranch { get; init; }

    /// <summary>When the run was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the run was started (agents spawned).</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When the run completed, failed, or was cancelled.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Persisted artifact base path (e.g., "AgentDocs/101") so recovery uses the same
    /// scoped folder even if config changes between crash and restart.
    /// </summary>
    public string? ArtifactBasePath { get; init; }

    /// <summary>
    /// Short run-scoped prefix for feature branch names (e.g., first 8 chars of RunId).
    /// Persisted so recovery restores the same branch naming, preventing orphaned branches.
    /// </summary>
    public string? RunScope { get; init; }
}

/// <summary>
/// Defines a feature to be built against an existing codebase.
/// Stored in SQLite and displayed on the Features dashboard page.
/// </summary>
public record FeatureDefinition
{
    /// <summary>Unique identifier (GUID string).</summary>
    public required string Id { get; init; }

    /// <summary>Short title for the feature (e.g., "Add user authentication").</summary>
    public required string Title { get; init; }

    /// <summary>Detailed description of what to build.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional repository override. Defaults to the project's configured repo.
    /// Format: "owner/repo".
    /// </summary>
    public string? TargetRepo { get; init; }

    /// <summary>Branch to fork the feature branch from. Defaults to "main".</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>
    /// Optional tech stack override. When null, inherits from project config.
    /// </summary>
    public string? TechStackOverride { get; init; }

    /// <summary>
    /// Additional context: links to docs, design files, constraints, etc.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>Testable acceptance criteria for the feature.</summary>
    public string? AcceptanceCriteria { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public FeatureStatus Status { get; init; } = FeatureStatus.Draft;

    /// <summary>When the feature was defined.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the feature started building.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When the feature completed or failed.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>The RunId of the run that is/was building this feature.</summary>
    public string? RunId { get; init; }
}
