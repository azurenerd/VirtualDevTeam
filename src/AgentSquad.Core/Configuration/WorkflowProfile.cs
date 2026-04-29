namespace AgentSquad.Core.Configuration;

/// <summary>
/// Defines per-mode workflow behavior: which agent roles are required,
/// how artifacts are named/placed, and how GitHub issues/PRs are labeled.
/// The <see cref="WorkflowStateMachine"/> and agents read the active profile
/// to adapt their behavior without scattered mode checks.
/// </summary>
public interface IWorkflowProfile
{
    /// <summary>Project or Feature.</summary>
    WorkMode Mode { get; }

    /// <summary>Human-readable label for this work mode (e.g., "Greenfield Project", "Feature: Add Auth").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Agent roles that must be spawned for this workflow.
    /// Feature mode may omit Researcher/Architect if not needed.
    /// </summary>
    IReadOnlyList<string> RequiredAgentRoles { get; }

    /// <summary>
    /// Base path for artifacts (Research.md, PMSpec.md, etc.) relative to repo root.
    /// Empty string for project mode (repo root), ".agentsquad/features/{id}" for feature mode.
    /// </summary>
    string ArtifactBasePath { get; }

    /// <summary>Name of the specification document (e.g., "PMSpec.md" or "FeatureSpec.md").</summary>
    string SpecDocName { get; }

    /// <summary>Name of the research document (e.g., "Research.md" or "FeatureResearch.md").</summary>
    string ResearchDocName { get; }

    /// <summary>Name of the architecture document (e.g., "Architecture.md" or "FeatureDesign.md").</summary>
    string ArchitectureDocName { get; }

    /// <summary>
    /// Label prefix for all GitHub issues/PRs created during this run.
    /// Used for run-scoped filtering. Empty for backward compat in project mode.
    /// </summary>
    string RunLabel { get; }

    /// <summary>
    /// Whether to decompose into multiple engineering tasks.
    /// Project mode: respects SinglePRMode config. Feature mode: typically single task.
    /// </summary>
    bool DecomposeToMultipleTasks { get; }

    /// <summary>
    /// Branch naming pattern for engineering work.
    /// Project: "agent/{name}/{task-slug}". Feature: "feature/{feature-slug}".
    /// </summary>
    string GetBranchName(string agentName, string taskSlug);

    /// <summary>
    /// Full path to an artifact document (combines ArtifactBasePath + docName).
    /// </summary>
    string GetArtifactPath(string docName) =>
        string.IsNullOrEmpty(ArtifactBasePath) ? docName : $"{ArtifactBasePath}/{docName}";
}

/// <summary>
/// Workflow profile for greenfield project creation.
/// Uses run-scoped artifact paths under a configurable docs folder, standard branch naming, and full agent set.
/// </summary>
public class ProjectWorkflowProfile : IWorkflowProfile
{
    private readonly bool _singlePrMode;
    private readonly string _docsFolderPath;
    private readonly string _runScope;

    public ProjectWorkflowProfile(bool singlePrMode = true, string docsFolderPath = "AgentDocs", string? runScope = null)
    {
        _singlePrMode = singlePrMode;
        _docsFolderPath = docsFolderPath;
        _runScope = runScope ?? Guid.NewGuid().ToString("N")[..8];
    }

    public WorkMode Mode => WorkMode.Project;
    public string DisplayName => "Greenfield Project";

    public IReadOnlyList<string> RequiredAgentRoles { get; } = new[]
    {
        "ProgramManager", "Researcher", "Architect", "SoftwareEngineer", "TestEngineer"
    };

    public string ArtifactBasePath =>
        string.IsNullOrEmpty(_docsFolderPath) ? "" : $"{_docsFolderPath}/{_runScope}";
    public string SpecDocName => "PMSpec.md";
    public string ResearchDocName => "Research.md";
    public string ArchitectureDocName => "Architecture.md";
    public string RunLabel => "";
    public bool DecomposeToMultipleTasks => !_singlePrMode;

    public string GetBranchName(string agentName, string taskSlug) =>
        $"agent/{agentName}/{taskSlug}";
}

/// <summary>
/// Workflow profile for incremental feature development against an existing repo.
/// Uses isolated artifact paths, feature branch naming, and minimal agent set.
/// </summary>
public class FeatureWorkflowProfile : IWorkflowProfile
{
    private readonly FeatureDefinition _feature;
    private readonly string _runId;

    public FeatureWorkflowProfile(FeatureDefinition feature, string runId)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        _runId = runId ?? throw new ArgumentNullException(nameof(runId));
    }

    public WorkMode Mode => WorkMode.Feature;
    public string DisplayName => $"Feature: {_feature.Title}";

    public IReadOnlyList<string> RequiredAgentRoles { get; } = new[]
    {
        "ProgramManager", "SoftwareEngineer"
    };

    public string ArtifactBasePath => $".agentsquad/features/{_feature.Id}";
    public string SpecDocName => "FeatureSpec.md";
    public string ResearchDocName => "FeatureResearch.md";
    public string ArchitectureDocName => "FeatureDesign.md";
    public string RunLabel => $"run:{_runId}";
    public bool DecomposeToMultipleTasks => false;

    /// <summary>The feature definition driving this profile.</summary>
    public FeatureDefinition Feature => _feature;

    public string GetBranchName(string agentName, string taskSlug)
    {
        var slug = _feature.Title
            .ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("--", "-")
            .Trim('-');
        // Truncate to keep branch names reasonable
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        return $"feature/{slug}";
    }
}
