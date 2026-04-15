using AgentSquad.Core.Agents;
using AgentSquad.Core.Workspace;

namespace AgentSquad.Core.Configuration;

public class AgentSquadConfig
{
    public ProjectConfig Project { get; set; } = new();
    public Dictionary<string, ModelConfig> Models { get; set; } = new();
    public AgentConfigs Agents { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();
    public CopilotCliConfig CopilotCli { get; set; } = new();
    public WorkspaceConfig Workspace { get; set; } = new();
    public HumanInteractionConfig HumanInteraction { get; set; } = new();
    public AgenticLoopConfig AgenticLoop { get; set; } = new();
    public Dictionary<string, McpServerDefinition> McpServers { get; set; } = new();
    public SmeAgentsConfig SmeAgents { get; set; } = new();
}

public class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// The primary tech stack for the project. Agents use this in all prompts
    /// to ensure generated code, architecture, and plans target the correct language/framework.
    /// Examples: "C# .NET 8 with Blazor Server", "TypeScript with Next.js and React", "Python with FastAPI"
    /// </summary>
    public string TechStack { get; set; } = "C# .NET 8 with Blazor Server";

    /// <summary>
    /// GitHub username of the Executive stakeholder (human) for escalation.
    /// The PM agent creates executive-request Issues assigned to this user when
    /// it needs human clarification on requirements.
    /// </summary>
    public string ExecutiveGitHubUsername { get; set; } = "azurenerd";

    /// <summary>
    /// The SHA of the baseline commit that represents the "clean" repo state.
    /// Used by the dashboard cleanup to atomically reset the repo via Git Trees API.
    /// When set, cleanup resolves this commit's tree to determine which files to preserve.
    /// When empty, cleanup falls back to the preserve-files list approach.
    /// </summary>
    public string BaselineCommitSha { get; set; } = "";

    /// <summary>
    /// Custom prompt that guides the Researcher agent on what to investigate.
    /// When empty, a comprehensive default prompt is generated from the project description.
    /// Use this to steer research toward specific areas, technologies, or concerns.
    /// </summary>
    public string ResearchPrompt { get; set; } = "";

    /// <summary>
    /// When true, Researcher/PM/Architect produce minimal 1-paragraph documents
    /// using only the project description and tech stack. Skips multi-turn AI
    /// conversations and context gathering for rapid startup during testing.
    /// </summary>
    public bool QuickDocumentCreation { get; set; } = false;
}

public class ModelConfig
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? Endpoint { get; set; }
    public int MaxTokensPerRequest { get; set; } = 4096;
    public double Temperature { get; set; } = 0.3;
}

public class AgentConfigs
{
    public AgentConfig ProgramManager { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig Researcher { get; set; } = new() { ModelTier = "standard", Enabled = true };
    public AgentConfig Architect { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig PrincipalEngineer { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig TestEngineer { get; set; } = new() { ModelTier = "standard", Enabled = true };
    public AgentConfig SeniorEngineerTemplate { get; set; } = new() { ModelTier = "standard" };
    public AgentConfig JuniorEngineerTemplate { get; set; } = new() { ModelTier = "local" };

    /// <summary>
    /// User-defined custom agents beyond the 7 built-in roles.
    /// Each custom agent gets a unique name and the same configuration options as built-in agents.
    /// </summary>
    public List<CustomAgentConfig> CustomAgents { get; set; } = new();
}

public class AgentConfig
{
    public string ModelTier { get; set; } = "standard";
    public bool Enabled { get; set; } = true;
    public int? MaxDailyTokens { get; set; }

    /// <summary>
    /// Custom role description that augments the agent's default system prompt.
    /// When set, this is prepended to every system message to define the agent's persona.
    /// Leave empty to use the default hardcoded role behavior.
    /// </summary>
    public string RoleDescription { get; set; } = "";

    /// <summary>
    /// MCP server names this agent can use for tool calls via the Copilot CLI.
    /// Each name is passed as a <c>--mcp-server</c> flag to the CLI process.
    /// </summary>
    public List<string> McpServers { get; set; } = new();

    /// <summary>
    /// URLs to documentation or knowledge pages that provide additional context for this agent.
    /// Content is fetched and summarized at agent initialization and cached for the session.
    /// </summary>
    public List<string> KnowledgeLinks { get; set; } = new();
}

/// <summary>
/// Configuration for a user-defined custom agent. Extends AgentConfig with a
/// unique name that serves as the agent's identity and display name.
/// </summary>
public class CustomAgentConfig : AgentConfig
{
    /// <summary>
    /// Unique display name for this custom agent (e.g., "Security Reviewer", "API Specialist").
    /// Used as the agent's display name and for identification in the system.
    /// </summary>
    public string Name { get; set; } = "";
}

public class LimitsConfig
{
    /// <summary>
    /// Per-role engineer pool configuration. Controls how many additional engineers
    /// of each type can be spawned. The original PE is always created as a core agent;
    /// the pool config only governs ADDITIONAL engineers.
    /// Default: 2 PEs, 0 SEs, 0 JEs — only additional Principal Engineers are spawned.
    /// </summary>
    public EngineerPoolConfig EngineerPool { get; set; } = new();

    /// <summary>
    /// Legacy property. Now computed from EngineerPool totals.
    /// Setting this directly is ignored if EngineerPool is explicitly configured.
    /// </summary>
    public int MaxAdditionalEngineers
    {
        get => EngineerPool.PrincipalEngineerPool + EngineerPool.SeniorEngineerPool + EngineerPool.JuniorEngineerPool;
        set { } // no-op for backward compat deserialization
    }

    public int MaxDailyTokenBudget { get; set; } = 1_000_000;
    public int GitHubPollIntervalSeconds { get; set; } = 60;
    public int AgentTimeoutMinutes { get; set; } = 60;
    public int MaxConcurrentAgents { get; set; } = 10;

    /// <summary>
    /// Maximum number of clarification round-trips between an engineer and the PM
    /// on a single Issue before the engineer proceeds with best understanding.
    /// </summary>
    public int MaxClarificationRoundTrips { get; set; } = 5;

    /// <summary>
    /// Maximum number of rework cycles (review → change → re-review) per PR before
    /// the reviewer force-approves to prevent infinite loops.
    /// This is the default fallback; prefer the phase-specific limits below.
    /// </summary>
    public int MaxReworkCycles { get; set; } = 3;

    /// <summary>
    /// Maximum Architect ↔ Engineer rework cycles per PR.
    /// Architect reviews first (Phase 1); after this limit, force-approve and proceed to TE testing.
    /// Falls back to MaxReworkCycles if not explicitly set.
    /// </summary>
    public int MaxArchitectReworkCycles { get; set; } = 3;

    /// <summary>
    /// Maximum PM ↔ Engineer rework cycles per PR.
    /// PM reviews last (Phase 3, after TE adds tests); after this limit, force-approve and merge.
    /// Falls back to MaxReworkCycles if not explicitly set.
    /// </summary>
    public int MaxPmReworkCycles { get; set; } = 3;

    /// <summary>
    /// Maximum rework cycles for Test Engineer source-bug feedback, tracked independently
    /// from peer review rework so TE feedback isn't blocked by exhausted peer review cycles.
    /// </summary>
    public int MaxTestReworkCycles { get; set; } = 2;

    /// <summary>
    /// Maximum times the Test Engineer will request source bug fixes from an engineer
    /// for a single PR before giving up and removing the failing tests.
    /// </summary>
    public int MaxSourceBugRounds { get; set; } = 2;

    /// <summary>
    /// If the Principal Engineer estimates all remaining tasks can be completed within
    /// this many minutes, it won't request additional engineers.
    /// </summary>
    public int SelfCompletionThresholdMinutes { get; set; } = 10;

    /// <summary>
    /// Minimum number of parallelizable tasks required before the Principal Engineer
    /// requests a new engineer from the PM.
    /// </summary>
    public int MinParallelizableTasksForNewEngineer { get; set; } = 3;
}

/// <summary>
/// Controls the pool of additional engineers that can be spawned to parallelize work.
/// The original PrincipalEngineer is always created as a core agent (rank 0).
/// Additional engineers are spawned on demand by the PE when parallelizable tasks exist.
/// </summary>
public class EngineerPoolConfig
{
    /// <summary>
    /// Maximum additional Principal Engineers (premium tier, full review + implementation capability).
    /// These act as worker PEs — the original PE (rank 0) remains the leader.
    /// Default: 2. Set to 0 to disable additional PE spawning.
    /// </summary>
    public int PrincipalEngineerPool { get; set; } = 2;

    /// <summary>
    /// Maximum additional Senior Engineers (standard tier, self-review capability).
    /// Default: 0. Set > 0 to allow SE spawning alongside or instead of PEs.
    /// </summary>
    public int SeniorEngineerPool { get; set; } = 0;

    /// <summary>
    /// Maximum additional Junior Engineers (budget/local tier, basic implementation).
    /// Default: 0. Set > 0 to allow JE spawning alongside or instead of PEs.
    /// </summary>
    public int JuniorEngineerPool { get; set; } = 0;
}

public class DashboardConfig
{
    /// <summary>Runner port — hosts the API and embedded dashboard.</summary>
    public int Port { get; set; } = 5050;
    /// <summary>Standalone dashboard port — the separate Dashboard.Host process.</summary>
    public int StandalonePort { get; set; } = 5051;
    public bool EnableSignalR { get; set; } = true;
}

/// <summary>
/// Configuration for the Copilot CLI AI provider.
/// When enabled, agents use the copilot CLI as the default AI backend instead of API keys.
/// </summary>
public class CopilotCliConfig
{
    /// <summary>Whether to use Copilot CLI as the default AI provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path to the copilot executable. Defaults to "copilot" (assumes it's on PATH).</summary>
    public string ExecutablePath { get; set; } = "copilot";

    /// <summary>Maximum number of concurrent copilot processes.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Timeout in seconds for a single AI request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>Alias for RequestTimeoutSeconds (backwards compatibility with appsettings).</summary>
    public int ProcessTimeoutSeconds
    {
        get => RequestTimeoutSeconds;
        set => RequestTimeoutSeconds = value;
    }

    /// <summary>Model to request from Copilot CLI (e.g., "claude-opus-4.6").</summary>
    public string ModelName { get; set; } = "claude-opus-4.6";

    /// <summary>Alias for ModelName (backwards compatibility with appsettings).</summary>
    public string DefaultModel
    {
        get => ModelName;
        set => ModelName = value;
    }

    /// <summary>
    /// Automatically approve all interactive prompts (y/n, selections, etc.).
    /// When true, the interactive watchdog auto-responds to unexpected prompts.
    /// </summary>
    public bool AutoApprovePrompts { get; set; } = true;

    /// <summary>Reasoning effort level: "low", "medium", "high", "xhigh", or null for default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Use --silent flag to suppress stats and chrome in output.</summary>
    public bool SilentMode { get; set; } = true;

    /// <summary>Use --output-format json for structured JSONL output.</summary>
    public bool JsonOutput { get; set; } = false;

    /// <summary>
    /// When true, injects a brevity constraint into every prompt so AI responses
    /// return in ~10 seconds instead of minutes. Uses a faster model (claude-haiku-4.5)
    /// and limits responses to 500 words. Useful for testing E2E flow quickly.
    /// Set to false for production-quality output.
    /// </summary>
    public bool FastMode { get; set; } = false;

    /// <summary>Model to use when FastMode is enabled. Defaults to claude-haiku-4.5.</summary>
    public string FastModeModel { get; set; } = "claude-haiku-4.5";

    /// <summary>
    /// Maximum number of automatic retries for transient errors (auth failures, timeouts).
    /// Retries use exponential backoff (5s, 15s, 30s). Set to 0 to disable retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// When true, multi-turn agents (Researcher, Architect, PM) collapse their
    /// chain-of-thought into a single comprehensive prompt instead of multiple
    /// conversational turns. Faster and cheaper but potentially less thorough.
    /// Independent of FastMode — can use premium models with single-pass for
    /// speed without sacrificing model quality.
    /// </summary>
    public bool SinglePassMode { get; set; } = false;

    /// <summary>Tools to exclude from the CLI's available tools (e.g., "shell", "write").</summary>
    public List<string> ExcludedTools { get; set; } = new();

    /// <summary>Working directory for copilot processes. Null uses the current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Additional arguments to pass to the copilot CLI.</summary>
    public string? AdditionalArgs { get; set; }
}

/// <summary>
/// Configuration for human-agent hybrid interaction touchpoints.
/// Each workflow gate can be set to require human approval or run fully autonomously.
/// When Enabled is false (default), all gates auto-proceed regardless of individual settings.
/// </summary>
public class HumanInteractionConfig
{
    /// <summary>
    /// Master switch for human interaction gates. When false, all gates auto-proceed
    /// and agents operate fully autonomously. Set to true to activate individual gate settings.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Per-gate configuration. Keys are gate IDs (e.g., "ProjectKickoff", "PMSpecification").
    /// Gates not explicitly configured default to RequiresHuman=false.
    /// </summary>
    public Dictionary<string, GateConfig> Gates { get; set; } = new()
    {
        [GateIds.ProjectKickoff] = new(),
        [GateIds.AgentTeamComposition] = new(),
        [GateIds.SmeAgentSpawn] = new(),
        [GateIds.ResearchFindings] = new(),
        [GateIds.ResearchCompleteness] = new(),
        [GateIds.PMSpecification] = new(),
        [GateIds.ArchitectureDesign] = new(),
        [GateIds.EngineeringPlan] = new(),
        [GateIds.TaskAssignment] = new(),
        [GateIds.PRCodeComplete] = new(),
        [GateIds.PRReviewApproval] = new(),
        [GateIds.ReworkExhaustion] = new(),
        [GateIds.SourceBugEscalation] = new(),
        [GateIds.TestResults] = new(),
        [GateIds.TestScreenshots] = new(),
        [GateIds.FinalPRApproval] = new(),
        [GateIds.FinalReview] = new(),
        [GateIds.DeploymentDecision] = new(),
    };

    /// <summary>Check if a specific gate requires human approval (respects Enabled master switch).</summary>
    public bool RequiresHuman(string gateId)
    {
        if (!Enabled) return false;
        return Gates.TryGetValue(gateId, out var gate) && gate.RequiresHuman;
    }

    /// <summary>Apply a preset: sets all gates to the specified RequiresHuman value.</summary>
    public void ApplyPreset(HumanInteractionPreset preset)
    {
        switch (preset)
        {
            case HumanInteractionPreset.FullAuto:
                Enabled = false;
                foreach (var gate in Gates.Values) gate.RequiresHuman = false;
                break;
            case HumanInteractionPreset.Supervised:
                Enabled = true;
                foreach (var kvp in Gates)
                {
                    kvp.Value.RequiresHuman = kvp.Key is GateIds.PMSpecification
                        or GateIds.ArchitectureDesign or GateIds.EngineeringPlan
                        or GateIds.ReworkExhaustion or GateIds.FinalPRApproval
                        or GateIds.FinalReview or GateIds.DeploymentDecision;
                }
                break;
            case HumanInteractionPreset.FullControl:
                Enabled = true;
                foreach (var gate in Gates.Values) gate.RequiresHuman = true;
                break;
        }
    }
}

/// <summary>Configuration for an individual workflow gate.</summary>
public class GateConfig
{
    /// <summary>When true, the workflow pauses at this gate until a human approves.</summary>
    public bool RequiresHuman { get; set; } = false;

    /// <summary>
    /// Minutes to wait for human response before applying FallbackAction.
    /// 0 means wait indefinitely (no timeout).
    /// </summary>
    public int TimeoutMinutes { get; set; } = 0;

    /// <summary>Action when timeout expires: "auto-approve", "block", or "escalate".</summary>
    public string FallbackAction { get; set; } = "auto-approve";
}

/// <summary>Preset autonomy profiles for quick configuration.</summary>
public enum HumanInteractionPreset
{
    /// <summary>All gates auto-proceed. No human involvement.</summary>
    FullAuto,
    /// <summary>Critical gates require human approval; routine gates auto-proceed.</summary>
    Supervised,
    /// <summary>All gates require human approval.</summary>
    FullControl,
}

/// <summary>
/// Well-known gate IDs corresponding to the 17 workflow integration points
/// defined in the VisionDoc. Use these constants instead of magic strings.
/// </summary>
public static class GateIds
{
    // Phase: Initialization
    public const string ProjectKickoff = "ProjectKickoff";
    public const string AgentTeamComposition = "AgentTeamComposition";
    public const string SmeAgentSpawn = "SmeAgentSpawn";

    // Phase: Research
    public const string ResearchFindings = "ResearchFindings";
    public const string ResearchCompleteness = "ResearchCompleteness";

    // Phase: Architecture
    public const string PMSpecification = "PMSpecification";
    public const string ArchitectureDesign = "ArchitectureDesign";

    // Phase: Engineering Planning
    public const string EngineeringPlan = "EngineeringPlan";
    public const string TaskAssignment = "TaskAssignment";

    // Phase: Parallel Development
    public const string PRCodeComplete = "PRCodeComplete";
    public const string PRReviewApproval = "PRReviewApproval";
    public const string ReworkExhaustion = "ReworkExhaustion";
    public const string SourceBugEscalation = "SourceBugEscalation";

    // Phase: Testing
    public const string TestResults = "TestResults";
    public const string TestScreenshots = "TestScreenshots";
    public const string FinalPRApproval = "FinalPRApproval";

    // Phase: Review & Completion
    public const string FinalReview = "FinalReview";
    public const string DeploymentDecision = "DeploymentDecision";

    /// <summary>All gate IDs with display names, grouped by workflow phase.</summary>
    public static readonly IReadOnlyList<(string Phase, string Id, string Name, string Description)> AllGates = new[]
    {
        ("Initialization", ProjectKickoff, "Project Kickoff",
            "Pause before agents begin work. When enabled: you review the project description, goals, and constraints — agents wait for your go-ahead. When auto: agents start immediately after launch."),
        ("Initialization", AgentTeamComposition, "Agent Team",
            "Pause after PM proposes the agent team. When enabled: you review which agents spawn, their model tiers, and role assignments — you can adjust before resources are allocated. When auto: PM's team composition is accepted and agents spawn immediately."),
        ("Initialization", SmeAgentSpawn, "SME Agent Spawn",
            "Pause when PM or PE requests spawning an SME agent during workflow. When enabled: you review the SME definition, MCP servers, and justification before the agent is created. When auto: SME agents spawn immediately when requested."),
        ("Research", ResearchFindings, "Research Findings",
            "Pause after Researcher produces Research.md. When enabled: you review competitive analysis, technology landscape, and key findings before the PM writes the spec. When auto: PM proceeds to write the spec from research as-is."),
        ("Research", ResearchCompleteness, "Research Complete",
            "Pause before closing the Research phase. When enabled: you confirm all research threads are thorough and no gaps remain. When auto: the system advances to Architecture as soon as research signals complete."),
        ("Architecture", PMSpecification, "PM Specification",
            "Pause after PM produces PMSpec.md. When enabled: you review business requirements, user stories, acceptance criteria, and scope — this is the foundation all downstream agents build on. Changes here cascade everywhere. When auto: spec is accepted and Architect begins immediately."),
        ("Architecture", ArchitectureDesign, "Architecture Design",
            "Pause after Architect produces Architecture.md. When enabled: you review system design, component diagrams, technology choices, and data models — the blueprint engineers will implement. When auto: Architecture is accepted and planning begins."),
        ("Engineering", EngineeringPlan, "Engineering Plan",
            "Pause after Principal Engineer produces EngineeringPlan.md. When enabled: you review the task breakdown, dependency ordering, and effort estimates before any code is written. When auto: tasks are created and assigned immediately."),
        ("Engineering", TaskAssignment, "Task Assignment",
            "Pause when PRs are created and engineers are assigned. When enabled: you review each task's PR, branch naming, and engineer assignment. When auto: engineers begin coding as soon as tasks are assigned."),
        ("Development", PRCodeComplete, "PR Code Complete",
            "Pause when an individual PR is marked ready for review. When enabled: you review the code, tests, and documentation before peer review begins. When auto: PR goes directly to agent peer review."),
        ("Development", PRReviewApproval, "PR Review Result",
            "Pause after an agent peer review completes. When enabled: you see the review verdict (approve/request changes) and decide whether to accept it or override. When auto: review result is applied automatically — approvals proceed, change requests trigger rework."),
        ("Development", ReworkExhaustion, "Rework Exhaustion",
            "Pause when an agent has exhausted its max rework cycles on a PR. When enabled: you decide the next step — merge as-is, provide guidance, reassign, or close. When auto: the system escalates or closes the PR based on configured policy."),
        ("Development", SourceBugEscalation, "Source Bug Escalation",
            "Pause when the Test Engineer finds bugs in source code (not test code). When enabled: you review the bug report and decide whether to send it back to the engineer or handle it differently. When auto: bug report is sent to the assigned engineer automatically."),
        ("Testing", TestResults, "Test Results",
            "Pause after test tiers complete (unit, integration, UI). When enabled: you review pass/fail results, coverage, and failure details before the PR proceeds. When auto: test results feed directly into the review/merge pipeline."),
        ("Testing", TestScreenshots, "Test Screenshots",
            "Pause when visual test artifacts (screenshots, renders) are available. When enabled: you visually verify UI correctness from the artifacts. When auto: screenshots are attached to the PR but no human review gate is enforced."),
        ("Testing", FinalPRApproval, "Final PR Approval",
            "Pause before merging a PR — after ALL agent reviews and tests have passed. When enabled: you are the final reviewer and nothing merges without your explicit approval. This is the strongest code-quality gate. When auto: PRs merge automatically once all agent checks pass."),
        ("Completion", FinalReview, "Final Review",
            "Pause when all PRs are merged and all tests pass. When enabled: you do a final holistic review of the entire deliverable before the project is marked complete. When auto: project completes automatically once all work items close."),
        ("Completion", DeploymentDecision, "Deployment",
            "Ship/no-ship decision. When enabled: you make the final call on whether to deploy the completed project. When auto: the system marks the project as complete without a deployment gate."),
    };
}

/// <summary>
/// Configuration for the agentic self-assessment loop.
/// When enabled, document-producing agents (Researcher, PM, Architect, Principal Engineer)
/// assess their own output against quality criteria and refine before publishing.
/// This reduces downstream review cycles by catching gaps early.
/// </summary>
public class AgenticLoopConfig
{
    /// <summary>Master switch. When false, all agents use the classic single-pass generation.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Maximum self-assessment iterations before publishing best effort.
    /// Each iteration = one assessment AI call + one refinement AI call.
    /// </summary>
    public int MaxIterations { get; set; } = 2;

    /// <summary>
    /// Per-role enablement. Allows turning on agentic loop for specific roles
    /// while keeping others in classic mode. Only checked when Enabled=true.
    /// Roles not listed here default to enabled when the master switch is on.
    /// </summary>
    public Dictionary<string, AgenticRoleConfig> Roles { get; set; } = new()
    {
        ["Researcher"] = new() { Enabled = true },
        ["ProgramManager"] = new() { Enabled = true },
        ["Architect"] = new() { Enabled = true },
        ["PrincipalEngineer"] = new() { Enabled = true },
        ["SeniorEngineer"] = new() { Enabled = false },
        ["JuniorEngineer"] = new() { Enabled = false },
        ["TestEngineer"] = new() { Enabled = false },
    };

    /// <summary>
    /// Confidence threshold for skipping refinement on minor gaps.
    /// When enabled, assessments include confidence scores and gap severities.
    /// High-confidence results with only minor gaps skip refinement to save API calls.
    /// Disabled by default — enable when using metered API keys.
    /// </summary>
    public ConfidenceThresholdConfig ConfidenceThreshold { get; set; } = new();

    /// <summary>Check if agentic loop is enabled for a specific agent role.</summary>
    public bool IsEnabledForRole(AgentRole role)
    {
        if (!Enabled) return false;
        var roleName = role.ToString();
        return !Roles.TryGetValue(roleName, out var roleConfig) || roleConfig.Enabled;
    }
}

/// <summary>Per-role agentic loop configuration.</summary>
public class AgenticRoleConfig
{
    /// <summary>When false, this role uses classic single-pass generation even if the master switch is on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Override MaxIterations for this role. Null = use global default.</summary>
    public int? MaxIterations { get; set; }
}

/// <summary>
/// Confidence threshold configuration. When enabled, the assessment AI reports a confidence
/// percentage and gap severities. If confidence ≥ MinConfidence and no critical/major gaps,
/// refinement is skipped to save API calls.
/// </summary>
public class ConfidenceThresholdConfig
{
    /// <summary>When false (default), all failed assessments trigger refinement regardless of severity.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Minimum confidence percentage (0-100) to skip refinement when only minor gaps exist.</summary>
    public int MinConfidence { get; set; } = 80;
}
