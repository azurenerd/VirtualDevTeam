namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// User-specific develop settings stored in develop-settings.json (gitignored, reset-safe).
/// PATs are NEVER stored here — they use .NET User Secrets.
/// </summary>
public class DevelopSettings
{
    public string Platform { get; set; } = "AzureDevOps"; // "GitHub" or "AzureDevOps"

    /// <summary>
    /// When true, agents work locally using SQLite + local git for PRs/issues during development.
    /// At completion, one clean PR is submitted to the platform (GitHub or ADO) for human review.
    /// The Platform field still determines where the final PR goes and which repo config to use.
    /// </summary>
    public bool UseLocalDevMode { get; set; } = false;
    public GitHubRepoSettings GitHub { get; set; } = new();
    public AdoRepoSettings AzureDevOps { get; set; } = new();
    public string AuthMethod { get; set; } = "GhCli";
    public string Description { get; set; } = "";
    public string TechStack { get; set; } = "";
    public string ExecutiveUsername { get; set; } = "";
    public int? ParentWorkItemId { get; set; }

    /// <summary>
    /// Work item ID used as the source of the project description.
    /// Separate from ParentWorkItemId (backlog linking) — this controls where requirements come from.
    /// </summary>
    public int? SourceWorkItemId { get; set; }
    public bool CreateNewRepo { get; set; } = false;
    public string NewRepoName { get; set; } = "";

    /// <summary>Base folder for agent-generated docs (default: "AgentDocs").</summary>
    public string DocsFolderPath { get; set; } = "AgentDocs";

    /// <summary>When true, PM creates 1 issue with doc links instead of N user stories.</summary>
    public bool SingleIssueMode { get; set; } = false;

    /// <summary>
    /// Pull request delivery mode. Controls work decomposition and sequencing.
    /// "SinglePR" = one monolithic PR; "MultiPRParallel" = multiple PRs in parallel waves;
    /// "MultiPRSerial" = multiple PRs worked serially (one at a time).
    /// </summary>
    public string PrMode { get; set; } = "SinglePR";

    /// <summary>
    /// Backward-compatible property for existing develop-settings.json files.
    /// Reading: maps to PrMode check. Writing: sets PrMode accordingly.
    /// </summary>
    [Obsolete("Use PrMode instead.")]
    public bool SinglePRMode
    {
        get => PrMode == "SinglePR";
        set
        {
            // Only apply legacy mapping if PrMode hasn't been explicitly set to serial
            if (PrMode != "MultiPRSerial")
                PrMode = value ? "SinglePR" : "MultiPRParallel";
        }
    }

    /// <summary>
    /// When true, engineers pause before implementing each PR to surface implementation
    /// assumptions as questions for human review. When false, questions are still generated
    /// and logged as decisions, but the agent proceeds without waiting.
    /// </summary>
    public bool PrePRClarificationGate { get; set; } = true;

    /// <summary>
    /// When true, PRs may merge even when the Test Engineer reports failing UI tests.
    /// Default: true.
    ///
    /// Rationale: many UI test failures are test-selector flakiness (Playwright strict-mode
    /// violations), infrastructure timing, or known-broken test wiring the engineer cannot
    /// fix from review feedback alone. With this at true the PM still posts the failing-
    /// tests warning but does NOT block the merge — the team ships and triages UI tests as
    /// a follow-up. Bypasses are recorded as Decision entries for audit.
    ///
    /// With this at false the PM blocks force-approval until the failures are fixed.
    /// The system also auto-bypasses with an audited Decision entry when it detects an
    /// unrecoverable rework loop (rework attempts produce no committable changes after
    /// max cycles), so the pipeline cannot deadlock indefinitely.
    ///
    /// Operators who want strict UI-quality gating should set this to false in the wizard.
    /// </summary>
    public bool AllowFailingUiTests { get; set; } = true;

    /// <summary>
    /// Optional working branch name. When set, all agent work targets this branch
    /// instead of the default branch. Leave empty to work directly on the default branch.
    /// </summary>
    public string? WorkingBranch { get; set; }

    /// <summary>Workspace mode for this project (Clone/Worktree/InPlace). Defaults to Clone.</summary>
    public string? WorkspaceMode { get; set; }

    /// <summary>Path to operator's existing checkout (InPlace mode).</summary>
    public string? ExistingRepoPath { get; set; }

    /// <summary>Where agent worktrees are created (Worktree/InPlace modes).</summary>
    public string? WorktreeRoot { get; set; }

    /// <summary>Sparse checkout patterns for worktrees.</summary>
    public List<string>? SparseCheckoutPaths { get; set; }

    /// <summary>Large project service registry configuration.</summary>
    public LargeProjectConfig? LargeProject { get; set; }

    /// <summary>Per-project human gate preferences (overrides appsettings.json at runtime).</summary>
    public GatePreferences? GatePreferences { get; set; }

    /// <summary>Controls which agent roles participate in PR reviews.</summary>
    public AgentReviewerSettings AgentReviewers { get; set; } = new();

    /// <summary>
    /// AI-generated clarifying questions with optional user answers.
    /// Answered Q&A pairs are appended to the project description at runtime.
    /// </summary>
    public List<ClarifyingQA> ClarifyingAnswers { get; set; } = new();

    /// <summary>
    /// Gets clarifying questions that were NOT answered by the user in the wizard.
    /// These represent decisions that agents must make autonomously.
    /// </summary>
    public IReadOnlyList<ClarifyingQA> GetUnansweredQuestions() =>
        ClarifyingAnswers.Where(qa => string.IsNullOrWhiteSpace(qa.Answer)).ToList();

    /// <summary>
    /// Number of "Ask More" iterations completed (0 = only initial generation).
    /// Reset when ClarifyingSourceHash changes (description was modified).
    /// </summary>
    public int ClarifyingIterationCount { get; set; } = 0;

    /// <summary>
    /// SHA256 hash of the project description at the time clarifying questions were generated.
    /// Used to invalidate clarifying state when the description changes.
    /// </summary>
    public string? ClarifyingSourceHash { get; set; }

    /// <summary>
    /// AI-generated scenarios persisted from the wizard Scenario Review step.
    /// Loaded on page init to avoid re-generation on refresh/restart.
    /// </summary>
    public List<PersistedScenario> GeneratedScenarios { get; set; } = new();

    /// <summary>
    /// SHA256 hash of the project description when scenarios were generated.
    /// If the description changes, scenarios should be regenerated.
    /// </summary>
    public string? ScenarioSourceHash { get; set; }

    /// <summary>
    /// Azure OpenAI image-generation settings configured in wizard step 2.
    /// Secrets (API key) are stored separately in dotnet user-secrets, NEVER here.
    /// </summary>
    public DevelopAzureOpenAIImageSettings AzureOpenAIImage { get; set; } = new();

    /// <summary>
    /// When true, the dashboard redirects to /welcome on load.
    /// Set to false after the wizard completes. Useful for testing the welcome flow.
    /// </summary>
    public bool ShowWelcomeWizard { get; set; } = false;

    /// <summary>
    /// FlowMonitor auto-approval timeout in minutes. When > 0, any gate or decision
    /// pending longer than this will be auto-approved by FlowMonitor. 0 = disabled.
    /// </summary>
    public int FlowMonitorAutoApprovalMinutes { get; set; } = 30;

    /// <summary>
    /// CLI wrapper command to prepend to all Copilot CLI invocations (e.g., "agency").
    /// Null = use appsettings.json default. Empty string = explicitly disable wrapper.
    /// Per-user override so appsettings.json default doesn't break users without the wrapper.
    /// </summary>
    public string? WrapperCommand { get; set; }

    /// <summary>
    /// Condensed summary of project description after reading referenced documents.
    /// When set, used for Config.Project.Description instead of the raw description
    /// so agents get clean text without triggering MCP doc reads on every prompt.
    /// Raw description preserved in <see cref="Description"/> for wizard display.
    /// </summary>
    public string? ResolvedProjectDescription { get; set; }

    /// <summary>
    /// AI-generated summary of the existing project's codebase, documentation, and conventions.
    /// Gathered by the wizard's context scan after repo/path validation for existing projects.
    /// Flows into all agent prompts via <c>{{existing_project_context}}</c> template variable.
    /// Empty/null for new (greenfield) projects.
    /// </summary>
    public string? ExistingProjectContext { get; set; }

    /// <summary>
    /// Git commit SHA at the time <see cref="ExistingProjectContext"/> was gathered.
    /// Used to detect staleness — if HEAD has moved, context may need refresh.
    /// </summary>
    public string? ExistingProjectContextCommitSha { get; set; }

    /// <summary>
    /// Timestamp (UTC) when <see cref="ExistingProjectContext"/> was last gathered.
    /// </summary>
    public DateTime? ExistingProjectContextGatheredAt { get; set; }
}

/// <summary>
/// Image-gen settings as the wizard captures them. Mirror of <see cref="AzureOpenAIImageConfig"/>;
/// kept separate so persistence (develop-settings.json) and runtime (VirtualDevTeamConfig.AzureOpenAIImage)
/// can evolve independently.
/// </summary>
public class DevelopAzureOpenAIImageSettings
{
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-04-01-preview";
    public string PrimaryDeployment { get; set; } = "gpt-image-2";
    public List<string> FallbackDeployments { get; set; } = new()
    {
        "gpt-image-1.5", "gpt-image-1", "gpt-image-1-mini"
    };
    public int MaxAttemptsPerImage { get; set; } = 3;
    public double VerificationConfidenceThreshold { get; set; } = 0.75;
    public bool EnableVerification { get; set; } = true;

    /// <summary>"DefaultAzureCredential" (preferred) or "ApiKey" (emergency fallback).</summary>
    public string AuthMethod { get; set; } = "DefaultAzureCredential";
}

/// <summary>
/// Controls which agent roles participate in PR code reviews.
/// When disabled, that agent skips the review step entirely.
/// </summary>
public class AgentReviewerSettings
{
    /// <summary>When true, the PM reviews PRs for business alignment and acceptance criteria coverage.</summary>
    public bool PmReviews { get; set; } = true;

    /// <summary>When true, the Architect reviews PRs for design compliance, patterns, and architecture alignment.</summary>
    public bool ArchitectReviews { get; set; } = true;

    /// <summary>When true, other Software Engineers review PRs for code quality, readability, and correctness.</summary>
    public bool EngineerReviews { get; set; } = true;

    /// <summary>
    /// When true, the Test Engineer participates: it spawns at runtime, adds tests to PRs,
    /// and the merge pipeline waits for the <c>tests-added</c> label. When false, the TE
    /// is suppressed entirely — PRs flow straight from <c>architect-approved</c> to
    /// <c>pm-approved</c> to merge with no test-coverage gate. Default: true (preserves
    /// the historical inline-test-workflow behavior).
    /// </summary>
    public bool TestEngineerReviews { get; set; } = true;
}

/// <summary>
/// Lightweight scenario DTO persisted to develop-settings.json.
/// Contains only the fields needed to restore the wizard Scenario Review UI.
/// </summary>
public class PersistedScenario
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string JourneyKind { get; set; } = "UiInteraction";
    public string Priority { get; set; } = "Important";
    public string Status { get; set; } = "Proposed";
    public string Actor { get; set; } = "";
    public string Trigger { get; set; } = "";
    public List<string> Steps { get; set; } = new();
    public List<string> ExpectedTerminalState { get; set; } = new();
    public List<string> SubsystemsInvolved { get; set; } = new();
    public bool InteractiveValidationSafe { get; set; } = true;
}

/// <summary>
/// Per-project gate preferences stored in develop-settings.json.
/// When present, overrides the HumanInteractionConfig from appsettings.json.
/// </summary>
public class GatePreferences
{
    /// <summary>Master enable/disable for human gating.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-gate overrides. Key = GateId, Value = true if human review required.</summary>
    public Dictionary<string, bool> Gates { get; set; } = new();
}

public class GitHubRepoSettings
{
    public string Repo { get; set; } = ""; // "owner/repo" format
    public string DefaultBranch { get; set; } = "main";
}

public class AdoRepoSettings
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
}

/// <summary>
/// A clarifying question generated by AI with an optional user-provided answer.
/// </summary>
public record ClarifyingQA
{
    public string Question { get; set; } = "";
    public string? Answer { get; set; }

    /// <summary>
    /// AI-proposed answer for the question. Pre-fills the answer textarea.
    /// The user can accept, edit, or clear it.
    /// </summary>
    public string? ProposedAnswer { get; set; }

    /// <summary>
    /// Which "Ask More" iteration generated this question.
    /// 1 = initial generation, 2 = first "Ask More", etc.
    /// </summary>
    public int Iteration { get; set; } = 1;
}
